using System.Collections.Generic;
using UnityEngine;


namespace SupremeNuclear
{
    // Часть CommanderHud: ленты и вейпоинты: цепочки наземки, ралли, эскорт-связи.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; OnGUI-диспетчер — CommanderHud.cs.
    public sealed partial class CommanderHud
    {
        // ---------- маршруты ----------

        // ---------- маршруты: плоскости-сегменты между вейпоинтами; статику не кэшируем (дёшево),
        // движущийся «якорь» юнита пересчитывается сам собой каждый кадр ----------

        /// <summary>Палитра лент/маркеров — СТРОГИЙ канон SupCom FA 1:1
        /// (commandgraphparams.lua): семейство движения (Move/CoordinatedMove/Patrol)
        /// — ЦИАН 00ffff, семейство атаки (Attack/AggressiveMove/Tactical) — чистый
        /// КРАСНЫЙ ff0000, инженерное семейство (Guard/Repair/Reclaim) — ЖЁЛТЫЙ
        /// ffff00, транспортное (Ferry/Transport) — ФИОЛЕТОВЫЙ 654bc2. Патруль
        /// наследует циан движения (отличается формой маркера-цикла, бегущими
        /// шевронами и замыкающей ногой); залп = Tactical → красный; эскорт =
        /// Guard → жёлтый; RTB — транспортное семейство.</summary>
        internal static Color ModeColor(AirOrderMode mode) =>
            mode switch
            {
                AirOrderMode.Patrol => new Color(0f, 1f, 1f, 1f),
                AirOrderMode.AttackMove => new Color(1f, 0f, 0f, 1f),
                AirOrderMode.Attack => new Color(1f, 0f, 0f, 1f),
                AirOrderMode.Salvo => new Color(1f, 0f, 0f, 1f),
                AirOrderMode.Escort => new Color(1f, 1f, 0f, 1f),
                AirOrderMode.Rtb => new Color(0.4f, 0.29f, 0.76f, 1f),
                _ => new Color(0f, 1f, 1f, 1f)
            };

        /// <summary>Глобальная точка маршрута на ПОВЕРХНОСТИ (вода — уровень
        /// моря, суша — рельеф): ленты/маркеры флота не проваливаются на дно.</summary>
        private static Vector3 GroundVec(GlobalPosition gp)
        {
            Vector3 l = gp.ToLocalPosition();
            return new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z);
        }

        /// <summary>Лента цепочки вейпоинтов: ноги — кубические Эрмит-кривые
        /// (Util.HermiteChain, порт канона Moho UICommandGraph: тангенс узла — митр
        /// смежных ног, длина — min(¼ ноги, 50·толщина) — «текучие» повороты SupCom),
        /// вся цепочка — ОДНА непрерывная полоса экраных трапеций (соседние куски делят
        /// вершины — швов и «лесенок» на стыках ног нет), полуширина адаптивна к
        /// дистанции до камеры, вдоль всей цепочки бежит шеврон-точка.
        /// legCols/legAlphas — по одной записи на ногу цепочки: цвет и яркость
        /// (hover-подсветка) переключаются на границе ног. legWidths — множитель
        /// полуширины на ногу (канон SupCom: групповая магистраль √n юнитов толще
        /// личной ветки; null — все единица).</summary>
        private static void DrawChainRibbon(Camera cam, IReadOnlyList<Vector3> pts,
            List<Color> legCols, List<float> legAlphas, List<float> legWidths = null,
            List<bool> legFlow = null, bool meshGated = true)
        {
            // Меш-режим (порт рендера FA): ленты рисует RouteRibbonMesh мировым
            // мешом с текстурами — IMGUI-ленты не дублируем. meshGated=false —
            // маршруты, которых НЕТ в меше (ралли фабрик), остаются в IMGUI.
            if (meshGated && Settings.RouteUseMesh)
                return;
            Color prevColor = GUI.color;
            Matrix4x4 prevMatrix = GUI.matrix;
            // Близкий зум: гасим только ЗА камерой (раньше 50 м — при приближении
            // лента исчезала прямо под камерой, плейтест v0.114).
            const float minZ = 1.5f;
            float pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            // Полуширина (SupCom-принцип): у камеры — ТОНКАЯ экранная линия
            // (~24px на 1440p, но не тоньше 1.5 м мира), с дистанцией плавно растёт
            // до мирового размера RouteRibbonWidthM (стратегический зум). Прежний
            // экраный потолок 110px прижимал ленту к константе на ЛЮБОМ приближении —
            // рядом с юнитами она была нереально широкой.
            // Толщина: FA-нить — экранный потолок ~12px на 1440p (плейтест v0.114:
            // прежние 24px+ читались жирным слэбом против тонкой линии оригинала);
            // в стратзуме рулит мировая RouteRibbonWidthM.
            float maxHalfAt(float dist) => Mathf.Min(
                Settings.RouteRibbonWidthM * 0.5f,
                Mathf.Max(1.5f, 6f * (Screen.height / 1440f) * dist / pxPerRad));

            // Сглаживание ног — Эрмит-кривые канона Moho (порт command_curve):
            // повороты «текут», как в SupCom; кламп тангенса привязан к МИРОВОЙ
            // ширине ленты (RouteRibbonWidthM × множитель ноги — как width у FA).
            int n = Util.HermiteChain(pts, legWidths, Settings.RouteRibbonWidthM,
                Settings.RouteSampleStepM, RibbonPts, RibbonLegs);
            if (n < 2)
                return;

            // Сечение ленты в точке j: перпендикуляр из соседних точек, полуширина по
            // дистанции и множителю ноги, которой принадлежит точка.
            float WidthMulOf(int legIdx) =>
                legWidths != null && legIdx >= 0 && legIdx < legWidths.Count ? legWidths[legIdx] : 1f;
            Vector3 Section(int j, int legIdx, out Vector3 left, out Vector3 right)
            {
                Vector3 c = RibbonPts[j];
                Vector3 prev = RibbonPts[j > 0 ? j - 1 : 0];
                Vector3 next = RibbonPts[j + 1 < n ? j + 1 : n - 1];
                Vector3 dir = next - prev;
                Vector3 flat = new Vector3(dir.x, 0f, dir.z);
                Vector3 perp = flat.sqrMagnitude < 1e-6f
                    ? Vector3.right
                    : new Vector3(-flat.z, 0f, flat.x).normalized;
                Vector3 sp = cam.WorldToScreenPoint(c);
                float half = maxHalfAt(Mathf.Max(sp.z, 1f)) * WidthMulOf(legIdx);
                left = c - perp * half;
                right = c + perp * half;
                return sp;
            }


            /// <summary>Текстурный квад ленты: ОСЬ X матрицы — ВДОЛЬ пути (u = дистанция
            /// в тайлах, тайл = 5 ширин ленты), ось Y — поперёк (v — профиль полосы);
            /// шевроны и glow живут в альфе текстуры, GUI.color тинтит видом ноги.
            /// Без полупиксельного расширения: края полосы в текстуре прозрачны,
            /// швов между кусками не видно (UV непрерывен по пути).</summary>
            void TexQuad(Vector3 aL, Vector3 aR, Vector3 bL, Vector3 bR, Color col,
                Texture2D tex, float u0, float du)
            {
                Vector3 spL = cam.WorldToScreenPoint(aL);
                Vector3 spR = cam.WorldToScreenPoint(aR);
                Vector3 spCl = cam.WorldToScreenPoint(bL);
                Vector3 spCr = cam.WorldToScreenPoint(bR);
                if (spL.z <= 0f || spR.z <= 0f || spCl.z <= 0f || spCr.z <= 0f)
                    return;
                Vector2 p1 = new Vector2(spL.x, Screen.height - spL.y);
                Vector2 p2 = new Vector2(spR.x, Screen.height - spR.y);
                Vector2 p3 = new Vector2(spCl.x, Screen.height - spCl.y);
                Vector2 along = p3 - p1;   // вдоль пути (u)
                Vector2 across = p2 - p1;  // поперёк ленты (v)
                if (along.sqrMagnitude <= 1e-6f || across.sqrMagnitude <= 1e-6f)
                    return;
                var m = Matrix4x4.identity;
                m.SetColumn(0, new Vector4(along.x, along.y, 0f, 0f));
                m.SetColumn(1, new Vector4(across.x, across.y, 0f, 0f));
                m.SetColumn(3, new Vector4(p1.x, p1.y, 0f, 1f));
                GUI.matrix = m;
                GUI.color = col;
                GUI.DrawTextureWithTexCoords(new Rect(0f, 0f, 1f, 1f), tex,
                    new Rect(u0, 0f, du, 1f), true);
                GUI.matrix = prevMatrix;
            }

            // «Живой» поток канона FA: шевроны ЖИВУТ В ТЕКСТУРЕ ленты (стрелочный
            // тайл = 5 ширин, uv_aspect FA 0.2), анимация = сдвиг UV по пути
            // (orderline_anim_rate 0.25); статичные ноги — мягкая полоса + наконечник.
            int legCount = legCols.Count;
            int legIdx = 0;
            Vector3 prevL = Vector3.zero, prevR = Vector3.zero, prevC = RibbonPts[0];
            bool hasPrev = false;
            float uCarry = 0f; // накопленная UV-фаза пути (в тайлах)

            for (int i = 1; i < n; i++)
            {
                Vector3 a = RibbonPts[i - 1];
                Vector3 b = RibbonPts[i];
                while (legIdx + 1 < legCount && i - 1 >= RibbonLegs[legIdx + 1])
                    legIdx++;
                Color c = legIdx < legCount ? legCols[legIdx] : Color.white;
                float alpha = legIdx < legAlphas.Count ? legAlphas[legIdx] : 1f;
                // Текстурная лента FA: glow-профиль (ядро+ореол) и шевроны — В АЛЬФЕ
                // текстуры, RGB белый; тинт — цвет вида ноги на полной альфе.
                var lineCol = new Color(c.r, c.g, c.b, alpha);
                bool flow = legFlow != null && legIdx < legFlow.Count && legFlow[legIdx];
                Texture2D tex = flow ? _ribbonArrow : _ribbonSoft;
                float shift = flow ? Time.unscaledTime * 0.25f : 0f; // anim_rate FA
                bool legEndsHere = i + 1 >= n ||
                    (legIdx + 1 < legCount && i >= RibbonLegs[legIdx + 1]);

                float xzLen = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (xzLen < 1e-3f)
                    continue;
                int pieces = Mathf.Clamp(Mathf.CeilToInt(xzLen / 200f), 1, 80);
                Vector3 spA = Section(i - 1, legIdx, out Vector3 startL, out Vector3 startR);
                Section(i, legIdx, out Vector3 endL, out Vector3 endR);
                for (int s = 1; s <= pieces; s++)
                {
                    float t = s / (float)pieces;
                    Vector3 curC = Vector3.LerpUnclamped(a, b, t);
                    Vector3 curL = Vector3.LerpUnclamped(startL, endL, t);
                    Vector3 curR = Vector3.LerpUnclamped(startR, endR, t);
                    Vector3 sp = cam.WorldToScreenPoint(curC);
                    bool visible = sp.z > minZ;

                    float pieceLen = Vector3.Distance(prevC, curC);
                    if (hasPrev && visible && pieceLen > 1e-3f)
                    {
                        // Тайл шеврона = 5 × ВИЗУАЛЬНАЯ ширина КУСКА (канон FA: тайл
                        // привязан к рисуемой ширине линии; на близком зуме стрелки
                        // сжимаются до экранных пропорций, а не растягиваются на сотни
                        // метров). Фаза копится по кускам — непрерывна при смене ширины.
                        float wStart = Vector3.Distance(prevL, prevC);
                        float wEnd = Vector3.Distance(curL, curC);
                        float tileM = Mathf.Max(8f, 5f * (wStart + wEnd));
                        if (flow)
                        {
                            float u0 = uCarry;
                            float u1 = u0 + pieceLen / tileM;
                            float ua = u0;
                            for (int k = Mathf.FloorToInt(u0) + 1; ; k++)
                            {
                                float ub = Mathf.Min(k, u1);
                                float fa = (ua - u0) / (u1 - u0);
                                float fb = (ub - u0) / (u1 - u0);
                                TexQuad(
                                    Vector3.LerpUnclamped(prevL, curL, fa),
                                    Vector3.LerpUnclamped(prevR, curR, fa),
                                    Vector3.LerpUnclamped(prevL, curL, fb),
                                    Vector3.LerpUnclamped(prevR, curR, fb),
                                    lineCol, tex, Mathf.Repeat(ua - shift, 1f), ub - ua);
                                ua = ub;
                                if (ub >= u1 - 1e-5f)
                                    break;
                            }
                            uCarry = u1;
                        }
                        else
                        {
                            // Мягкая полоса: вдоль u текстура однородна — UV не важен.
                            TexQuad(prevL, prevR, curL, curR, lineCol, tex, 0f, 1f);
                        }
                    }
                    if (!flow && legEndsHere && s == pieces && visible)
                    {
                        // Наконечник в конце статичной ноги: направление a→b.
                        DrawRibbonChevron(new Vector2(sp.x, Screen.height - sp.y),
                            new Vector2(sp.x - spA.x, spA.y - sp.y),
                            new Color(c.r, c.g, c.b, Mathf.Min(1f, alpha + 0.1f)));
                    }
                    prevC = curC;
                    prevL = curL;
                    prevR = curR;
                    if (visible)
                        hasPrev = true;
                }
            }
            GUI.color = prevColor;
        }

        /// <summary>Шеврон направления «∨» в экранных координатах: две тонкие линии-
        /// руки из точки p под ±140° к направлению dir (указывает ПО ходу движения;
        /// бегущие ряды — на ногах атаки-движения/патруля, одиночный — наконечник
        /// статичной ноги). Без аллокаций (локальные GUI.matrix).</summary>
        private static void DrawRibbonChevron(Vector2 p, Vector2 dir, Color c)
        {
            float dl = dir.magnitude;
            if (dl < 1e-3f)
                return;
            float heading = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            Matrix4x4 prev = GUI.matrix;
            Color prevC = GUI.color;
            GUI.color = c;
            const float arm = 9f, thick = 2f;
            for (int s = -1; s <= 1; s += 2)
            {
                var m = Matrix4x4.identity;
                m.SetColumn(3, new Vector4(p.x, p.y, 0f, 1f));
                var rot = Matrix4x4.TRS(Vector3.zero,
                    Quaternion.Euler(0f, 0f, heading + s * 140f), Vector3.one);
                GUI.matrix = m * rot * prev;
                GUI.DrawTexture(new Rect(0f, -thick * 0.5f, arm, thick), _white);
            }
            GUI.matrix = prev;
            GUI.color = prevC;
        }

        /// <summary>Форма маркера вейпоинта — вид приказа без цвета (канон SupCom
        /// «цвет ног + форма маркеров»): квадрат — движение, ромб — атака-движение,
        /// двойная рамка — патрульный цикл.</summary>
        private enum WaypointShape
        {
            Square,
            Diamond,
            Loop
        }

        private static WaypointShape ShapeOf(GroundOrderKind kind) =>
            kind switch
            {
                GroundOrderKind.AttackMove => WaypointShape.Diamond,
                GroundOrderKind.Patrol => WaypointShape.Loop,
                _ => WaypointShape.Square
            };

        private static WaypointShape ShapeOf(AirOrderMode mode) =>
            mode switch
            {
                AirOrderMode.AttackMove => WaypointShape.Diamond,
                AirOrderMode.Patrol => WaypointShape.Loop,
                _ => WaypointShape.Square
            };

        /// <summary>Маркер вейпоинта в точке экрана p: форма по виду приказа, размер
        /// sizeMul (групповой узел √n юнитов — SupCom DrawNode). Ромб — квадрат,
        /// повёрнутый на 45° локальной GUI.matrix (без аллокаций).</summary>
        private static void DrawWaypointMarkerAt(Vector2 p, float half, Color color, float alpha,
            WaypointShape shape)
        {
            Color frame = new Color(color.r, color.g, color.b, Mathf.Min(1f, alpha + 0.15f));
            Color core = new Color(color.r, color.g, color.b, alpha);
            switch (shape)
            {
                case WaypointShape.Diamond:
                {
                    Matrix4x4 prev = GUI.matrix;
                    var m = Matrix4x4.identity;
                    m.SetColumn(3, new Vector4(p.x, p.y, 0f, 1f)); // перенос в центр
                    var rot = Matrix4x4.identity;
                    float c = Mathf.Cos(Mathf.PI * 0.25f), s = Mathf.Sin(Mathf.PI * 0.25f);
                    rot.m00 = c; rot.m01 = -s;
                    rot.m10 = s; rot.m11 = c;
                    GUI.matrix = m * rot * prev;
                    DrawBoxOutline(new Rect(-half, -half, half * 2f, half * 2f), 2.5f, frame);
                    GUI.matrix = prev;
                    DrawRect(new Rect(p.x - 3.5f, p.y - 3.5f, 7f, 7f), core);
                    break;
                }
                case WaypointShape.Loop:
                {
                    var outer = new Rect(p.x - half, p.y - half, half * 2f, half * 2f);
                    DrawBoxOutline(outer, 2.5f, frame);
                    float innerHalf = half * 0.5f;
                    DrawBoxOutline(new Rect(p.x - innerHalf, p.y - innerHalf,
                        innerHalf * 2f, innerHalf * 2f), 2f, core);
                    break;
                }
                default:
                {
                    var r = new Rect(p.x - half, p.y - half, half * 2f, half * 2f);
                    DrawBoxOutline(r, 2.5f, frame);
                    float dot = half * 0.4f;
                    DrawRect(new Rect(p.x - dot, p.y - dot, dot * 2f, dot * 2f), core);
                    break;
                }
            }
        }

        /// <summary>Вейпоинт с цветом записи очереди (движение/стройка) и формой вида.</summary>
        private static void DrawWorldWaypointColored(Camera cam, Vector3 wp, Color color, float alpha,
            WaypointShape shape = WaypointShape.Square, float sizeMul = 1f)
        {
            Vector3 sp = cam.WorldToScreenPoint(wp);
            if (sp.z <= 0f)
                return;
            Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
            if (UnderMinimapWindow(p))
                return;
            DrawWaypointMarkerAt(p, 10f * sizeMul, color, alpha, shape);
        }

        private static void DrawWorldWaypoints(Camera cam, IEnumerable<Vector3> waypoints, Color color, float alpha)
        {
            foreach (Vector3 wp in waypoints)
                DrawWorldWaypointColored(cam, wp, color, alpha);
        }

        /// <summary>Индекс вейпоинта под курсором (единый пик-радиус вейпоинтов) или -1.</summary>
        private static int HoverWaypointIndex(Camera cam, List<Vector3> wps)
        {
            float radiusPx = Settings.WaypointPickPx;
            Vector3 m = RtsInput.MousePos;
            for (int i = 0; i < wps.Count; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(wps[i]);
                if (sp.z > 0f && Vector2.Distance(new Vector2(sp.x, sp.y), new Vector2(m.x, m.y)) < radiusPx)
                    return i;
            }
            return -1;
        }

        // Переиспользуемые буферы отрисовки маршрутов: OnGUI зовётся 2+ раза за кадр,
        // списки на каждый вызов давили GC (долг §12-8 — частично закрыт).
        private static readonly List<Unit> RouteUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<GlobalPosition>> RouteQueues = new List<IReadOnlyList<GlobalPosition>>();
        private static readonly List<int> RouteIdx = new List<int>();
        private static readonly List<Unit> RouteAirUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<AircraftOrders.OrderSegment>> RouteAirPlans =
            new List<IReadOnlyList<AircraftOrders.OrderSegment>>();
        private static readonly List<Vector3> RouteWps = new List<Vector3>();
        // Лента цепочки: точки/цвета/яркости ног собираются в переиспользуемые буферы
        // (OnGUI 2+ раза за кадр — без аллокаций), сглаженная полоса — в RibbonPts/RibbonLegs.
        private static readonly List<Vector3> ChainPts = new List<Vector3>();
        private static readonly List<Color> ChainCols = new List<Color>();
        private static readonly List<float> ChainAlphas = new List<float>();
        private static readonly List<Vector3> RibbonPts = new List<Vector3>();
        private static readonly List<int> RibbonLegs = new List<int>();
        // Ралли фабрик: точки текущего маршрута + сплющенные «уже нарисованные в кадре»
        // маршруты с их длинами — дедуп общего ралли пачки фабрик (без аллокаций).
        private static readonly List<Vector3> RallyWps = new List<Vector3>();
        private static readonly List<Vector3> RallySeenPts = new List<Vector3>();
        private static readonly List<int> RallySeenLens = new List<int>();
        // Все фабрики с живыми ралли (Shift-режим видимости невыделенных).
        private static readonly List<object> RallyAll = new List<object>();

        /// <summary>Ралли-маршрут фабрики: лента + вейпоинты от корпуса; маршрут,
        /// уже нарисованный в этом кадре (общее ралли пачки фабрик), не дублируется.</summary>
        private static void DrawFactoryRally(Camera cam, Vector3 anchorPos, object factory, Color c, float a)
        {
            var route = ProductionTasks.RallyOf(factory);
            if (route == null || route.Count == 0)
                return;
            RallyWps.Clear();
            foreach (GlobalPosition gp in route.Points)
                RallyWps.Add(GroundVec(gp));
            if (Util.RouteSeenBefore(RallySeenPts, RallySeenLens, RallyWps))
                return;
            RallySeenPts.AddRange(RallyWps);
            RallySeenLens.Add(RallyWps.Count);
            Vector3 anchor = new Vector3(anchorPos.x, 0f, anchorPos.z);
            anchor.y = GameApi.GroundY(anchor.x, anchor.z);
            ChainPts.Clear();
            ChainCols.Clear();
            ChainAlphas.Clear();
            ChainPts.Add(anchor);
            for (int i = 0; i < RallyWps.Count; i++)
            {
                ChainPts.Add(RallyWps[i]);
                ChainCols.Add(c);
                ChainAlphas.Add(a);
            }
            // Патрульное ралли (§12-30): замыкающая нога цикла (последняя → первая),
            // как у ленты патруля — будущие выходы идут по кругу, не по линии.
            if (ProductionTasks.RallyIsPatrol(factory) && RallyWps.Count > 1)
            {
                ChainPts.Add(RallyWps[0]);
                ChainCols.Add(c);
                ChainAlphas.Add(a * 0.6f);
            }
            DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, meshGated: false);
            DrawWorldWaypoints(cam, RallyWps, Color.white, a); // маркеры белые — канон FA
        }

        /// <summary>Якорь ралли-ленты фабрики (её корпус); мёртвая/чужеродная
        /// запись реестра ралли якоря не даёт — null.</summary>
        private static Vector3? RallyAnchor(object factory)
        {
            switch (factory)
            {
                case VehicleDepot depot:
                    return depot != null && !depot.disabled
                        ? new Vector3?(depot.transform.position) : null;
                case Hangar hangar:
                    if (hangar == null || hangar.Disabled)
                        return null;
                    Unit hu = hangar.attachedUnit;
                    return hu != null ? hu.transform.position : hangar.transform.position;
                case Building dock:
                    return BuildTasks.IsDock(dock)
                        ? (Vector3?)dock.transform.position : null;
                case Airbase airbase:
                    return airbase != null ? new Vector3?(airbase.transform.position) : null;
                case BuildTasks.Task site:
                    // Недострой-фабрика: якорь — сама площадка (v0.107).
                    return new Vector3?(site.Site.ToLocalPosition());
                default:
                    return null;
            }
        }
        // Групповые вейпоинты по groupId: групповая точка — свойство точки, не
        // выделения. Накопители центроидов (xyz = сумма, w = счётчик), дедуп общих
        // сегментов/маркеров/входов между юнитами (gid из одного счётчика — общие).
        private static readonly Dictionary<int, Vector4> GidPointSums = new Dictionary<int, Vector4>();
        private static readonly Dictionary<int, Vector4> GidAnchorSums = new Dictionary<int, Vector4>();
        private static readonly HashSet<int> SeenGids = new HashSet<int>();
        private static readonly HashSet<long> DrawnGidLegs = new HashSet<long>();
        private static readonly HashSet<int> DrawnGidMarkers = new HashSet<int>();
        private static readonly HashSet<int> DrawnGidEntries = new HashSet<int>();
        private static readonly List<Vector3> ChainNodes = new List<Vector3>();
        private static readonly List<int> NodeGids = new List<int>();
        // Смешанная очередь наземки: цель-узел (null — точка) и «владелец» ноги к цели
        // (рисует и крест — дедуп на цель для всей группы приказа).
        private static readonly List<Unit> ChainNodeTargets = new List<Unit>();
        private static readonly List<bool> ChainNodeOwned = new List<bool>();
        // Per-point вид (v0.114): вид точки-узла (цели — Attack) и её абсолютный
        // индекс в очереди (-1 у целей) — цвет ноги входа, форма маркера, подсказка
        // конвертации хвостом. Толщина ноги: √n юнитов группы приказа (канон SupCom).
        private static readonly List<GroundOrderKind> NodeKinds = new List<GroundOrderKind>();
        private static readonly List<int> NodeAbs = new List<int>();
        private static readonly List<float> ChainWidths = new List<float>();
        // «Живой» поток канона FA: бегущие шевроны только на ногах атаки-движения
        // и патруля (orderline_anim_rate); статичные ноги — наконечник на конце.
        private static readonly List<bool> ChainFlow = new List<bool>();
        private static readonly HashSet<Unit> DrawnTargetExits = new HashSet<Unit>();
        // Очереди инженеров (все с записями — групповое слияние не зависит от выделения).
        private static readonly List<Unit> EngSeqUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<BuildTasks.SeqEntry>> EngSeqs =
            new List<IReadOnlyList<BuildTasks.SeqEntry>>();
        // Скретч точек/цветов очередной инженерной цепочки (последний живой кусок
        // долга §12-8: два new List на инженера на каждый Repaint).
        private static readonly List<Vector3> EngPts = new List<Vector3>();
        private static readonly List<Color> EngCols = new List<Color>();
        private static readonly HashSet<BuildTasks.Task> DrawnSites = new HashSet<BuildTasks.Task>();
        private static readonly HashSet<int> DrawnGroupMoves = new HashSet<int>();
        private static readonly HashSet<Unit> DrawnGroundTargets = new HashSet<Unit>();
        // Связи сопровождения (наземка + авиа-эскорт): маркер на ЦЕЛИ — один на цель.
        private static readonly List<Unit> EscortUnits = new List<Unit>();
        private static readonly List<Unit> EscortTargetsBuf = new List<Unit>();
        private static readonly HashSet<Unit> DrawnEscortMarkers = new HashSet<Unit>();
        private static readonly HashSet<Unit> DrawnRepairMarkers = new HashSet<Unit>();
        private static readonly HashSet<Unit> DrawnGuardMarkers = new HashSet<Unit>();
        /// <summary>Охрана (Guard ≠ Escort, §2.11): янтарная — семейство жёлтых
        /// сервисных связей, но отличима от эскорта/ремонта (канон FA — жёлтые).</summary>
        private static readonly Color GuardLinkColor = new Color(1f, 0.62f, 0.15f, 1f);
        // Групповые точки планов авиации: центроид группы бортов + дедуп маркеров.
        private static readonly Dictionary<int, Vector4> AirGidCentroids = new Dictionary<int, Vector4>();
        private static readonly HashSet<int> DrawnAirGid = new HashSet<int>();
        // SupCom-магистраль авиации (v0.91, зеркально наземке): узел группы —
        // центроид формационных точек, вход — от центроида якорей членов, сегменты
        // магистрали/входы/ноги атак и RTB — по одному разу на приказ.
        private static readonly Dictionary<int, Vector4> AirGidAnchors = new Dictionary<int, Vector4>();
        private static readonly HashSet<int> SeenAirGids = new HashSet<int>();
        private static readonly HashSet<long> DrawnAirLegs = new HashSet<long>();
        private static readonly HashSet<int> DrawnAirEntries = new HashSet<int>();
        private static readonly HashSet<Unit> DrawnAirTargets = new HashSet<Unit>();
        private static readonly HashSet<Airbase> DrawnRtbBases = new HashSet<Airbase>();

        private static long PairKey(int a, int b) => (long)a << 32 | (uint)b;

        /// <summary>Групповой маркер-центроид (26px) — один на приказ группы.</summary>
        /// <summary>Групповой вейпоинт (один на приказ, в центроиде): крупнее
        /// индивидуального — размер растёт как √n юнитов приказа (канон SupCom
        /// DrawNode, кламп 1..1.6×), форма/цвет — вид приказа точки.</summary>
        private static void DrawGroupWaypointMarker(Camera cam, Vector3 world, Color color, float alpha,
            WaypointShape shape, float sizeMul = 1f)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f)
                return;
            Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
            if (UnderMinimapWindow(p))
                return;
            DrawWaypointMarkerAt(p, 13f * sizeMul, color, alpha, shape);
        }

        /// <summary>Цепочка наземки с групповым слиянием: групповые точки (groupId≠0)
        /// заменяются центроидами группы приказа; сегменты между соседними групповыми
        /// точками дедуплицированы между юнитами (магистраль рисует первый встретивший);
        /// вход в группу — ОДИН от центроида якорей её членов; индивидуальные точки —
        /// личные ветки схождения/расхождения. Цели атаки — узлы очереди НА СВОЕЙ
        /// позиции серии (v0.73.2): лента течёт ЧЕРЕЗ цель (как планы авиации и SupCom),
        /// нога до цели и нога от неё — по одному разу на цель (группа бьёт по ней
        /// вместе); текущая цель погони — яркая, с пульсирующим крестом, ждущие свою
        /// позицию — приглушённые, со статичным крестом. Hover подсвечивает путь до узла.</summary>
        private static void DrawGroundChain(Camera cam, Unit unit, IReadOnlyList<GlobalPosition> queue,
            int qi, Color c, float a, int loopStartAbs = -1)
        {
            // Узлы очереди В ПОРЯДКЕ КЛИКОВ: дозревшие/текущие цели — в голове,
            // дальше точки, с целями, вставленными после своей позиции серии.
            // Каждый узел несёт вид своей точки (per-point, v0.114): нога красится
            // цветом ВХОДА в узел (канон SupCom: attack-move нога красная), маркер —
            // формой вида. loopStartAbs ≥ 0 — патрульный цикл, замыкающая нога идёт
            // на начало хвоста (конвертация «от точки»), не на первый узел.
            var targets = GroundOrders.TargetQueueOf(unit);
            ChainNodes.Clear();
            NodeGids.Clear();
            ChainNodeTargets.Clear();
            ChainNodeOwned.Clear();
            NodeKinds.Clear();
            NodeAbs.Clear();
            int loopNode = -1;
            if (targets != null)
                for (int q = 0; q < targets.Count; q++)
                {
                    if (GroundOrders.TargetAfterOf(unit, q) > qi)
                        continue;
                    Unit t = targets[q];
                    if (t == null || t.disabled)
                        continue;
                    ChainNodes.Add(TargetGround(t));
                    NodeGids.Add(0);
                    ChainNodeTargets.Add(t);
                    ChainNodeOwned.Add(false);
                    NodeKinds.Add(GroundOrderKind.Attack);
                    NodeAbs.Add(-1);
                }
            for (int i = qi; i < queue.Count; i++)
            {
                // Групповая точка — узел-центроид группы приказа, индивидуальная — своя.
                int gid = GroundOrders.GroupIdOf(unit, i);
                GroundOrderKind kind = GroundOrders.KindOfPoint(unit, i);
                Vector3 wp = GroundVec(queue[i]);
                ChainNodes.Add(gid != 0 && GidPointSums.TryGetValue(gid, out Vector4 acc)
                    ? new Vector3(acc.x / acc.w, acc.y / acc.w, acc.z / acc.w)
                    : wp);
                NodeGids.Add(gid);
                ChainNodeTargets.Add(null);
                ChainNodeOwned.Add(false);
                NodeKinds.Add(kind);
                NodeAbs.Add(i);
                if (i == loopStartAbs)
                    loopNode = ChainNodes.Count - 1;
                if (targets == null)
                    continue;
                for (int q = 0; q < targets.Count; q++)
                {
                    if (GroundOrders.TargetAfterOf(unit, q) != i + 1)
                        continue;
                    Unit t = targets[q];
                    if (t == null || t.disabled)
                        continue;
                    ChainNodes.Add(TargetGround(t));
                    NodeGids.Add(0);
                    ChainNodeTargets.Add(t);
                    ChainNodeOwned.Add(false);
                    NodeKinds.Add(GroundOrderKind.Attack);
                    NodeAbs.Add(-1);
                }
            }
            // Патруль (loopStartAbs = ТЕКУЩАЯ точка): обёртка хвоста цикла
            // [LoopStart..qi) — весь цикл нарисован, голова ленты = куда юнит идёт.
            if (loopStartAbs >= 0)
            {
                int wrapFrom = GroundOrders.PatrolLoopStartOf(unit);
                for (int i = wrapFrom; i < loopStartAbs; i++)
                {
                    int gid = GroundOrders.GroupIdOf(unit, i);
                    ChainNodes.Add(gid != 0 && GidPointSums.TryGetValue(gid, out Vector4 acc)
                        ? new Vector3(acc.x / acc.w, acc.y / acc.w, acc.z / acc.w)
                        : GroundVec(queue[i]));
                    NodeGids.Add(gid);
                    ChainNodeTargets.Add(null);
                    ChainNodeOwned.Add(false);
                    NodeKinds.Add(GroundOrderKind.Patrol);
                    NodeAbs.Add(i);
                }
            }
            if (ChainNodes.Count == 0)
                return;
            // Начало хвоста уже пройдено (Index > LoopStart): цикл замыкаем на первый
            // РИСУЕМЫЙ узел — петля видна на любом участке цикла (меш-лента делает
            // тот же кламп max(LoopStart, DrawStart)).
            if (loopNode < 0 && loopStartAbs >= 0 && ChainNodes.Count >= 2)
                loopNode = 0;

            Color attackC = ModeColor(AirOrderMode.Attack);
            Color attackMoveC = ModeColor(AirOrderMode.AttackMove);
            Color patrolC = ModeColor(AirOrderMode.Patrol);
            // Цвет ноги ВХОДА в точку — вид самой точки (канон SupCom: нога красится
            // цветом приказа, к которому ведёт; attack-move нога красная).
            Color KindColor(GroundOrderKind kind) =>
                kind == GroundOrderKind.AttackMove ? attackMoveC
                : kind == GroundOrderKind.Patrol ? patrolC
                : c;
            // Толщина групповой магистрали: √n юнитов приказа (кламп настройки),
            // личные ветки — единица.
            float GroupWidth(int gid) =>
                gid != 0 && GidPointSums.TryGetValue(gid, out Vector4 acc)
                    ? Mathf.Min(Mathf.Sqrt(Mathf.Max(acc.w, 1f)), Settings.RouteGroupWidthMax)
                    : 1f;
            bool pursuing = GroundOrders.KindOf(unit) == GroundOrderKind.Attack;
            Unit curTarget = GroundOrders.TargetOf(unit);

            Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
            anchor.y = GameApi.GroundY(anchor.x, anchor.z);

            int hover = HoverWaypointIndex(cam, ChainNodes);
            ChainPts.Clear();
            ChainCols.Clear();
            ChainAlphas.Clear();
            ChainWidths.Clear();
            ChainFlow.Clear();
            Vector3 prev = anchor;
            int prevGid = 0;
            Unit prevTarget = null;
            for (int i = 0; i < ChainNodes.Count; i++)
            {
                Vector3 node = ChainNodes[i];
                int gid = NodeGids[i];
                Unit nodeTarget = ChainNodeTargets[i];
                bool drawLeg = true;
                Color legC = c;
                float legA = a;
                float legW = 1f;
                if (nodeTarget != null)
                {
                    // Нога ДО цели — один раз на цель; владелец ноги рисует и крест.
                    drawLeg = DrawnGroundTargets.Add(nodeTarget);
                    ChainNodeOwned[i] = drawLeg;
                    legC = attackC;
                    legA = pursuing && nodeTarget == curTarget ? a : a * 0.6f;
                }
                else
                {
                    legC = KindColor(NodeKinds[i]);
                    legW = GroupWidth(gid);
                    if (prevTarget != null)
                        // Нога ОТ цели — тоже один раз на цель (выход из боевого узла).
                        drawLeg = DrawnTargetExits.Add(prevTarget);
                    else if (i == 0 && gid != 0)
                    {
                        // Вход в группу — от центроида якорей её членов, один раз на группу.
                        if (GidAnchorSums.TryGetValue(gid, out Vector4 aa))
                        {
                            prev = new Vector3(aa.x / aa.w, 0f, aa.z / aa.w);
                            prev.y = GameApi.GroundY(prev.x, prev.z);
                        }
                        drawLeg = DrawnGidEntries.Add(gid);
                    }
                    else if (prevGid != 0 && gid != 0)
                        // Сегмент магистрали: рисует первый встретивший юнит группы.
                        drawLeg = DrawnGidLegs.Add(PairKey(prevGid, gid));
                }

                if (drawLeg)
                {
                    if (ChainPts.Count == 0)
                        ChainPts.Add(prev);
                    ChainPts.Add(node);
                    ChainCols.Add(legC);
                    // Hover-подсветка ХВОСТА от наведённого узла (v0.114): конвертация
                    // в патруль замыкает хвост — игрок видит, что станет циклом.
                    ChainAlphas.Add(hover >= 0 && i >= hover ? Mathf.Max(legA, 1f) : legA);
                    ChainWidths.Add(legW);
                    ChainFlow.Add(NodeKinds[i] == GroundOrderKind.AttackMove ||
                                  NodeKinds[i] == GroundOrderKind.Patrol);
                }
                else if (ChainPts.Count > 0)
                {
                    // Разрыв: общий сегмент уже нарисован другим членом группы.
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                    ChainWidths.Clear();
                    ChainFlow.Clear();
                }
                prev = node;
                prevGid = gid;
                prevTarget = nodeTarget;
            }
            // Замыкающая нога патруля: последний узел хвоста → его НАЧАЛО (узел
            // loopStartAbs, не первый узел очереди — конвертация «от точки»).
            // Магистральные групповые сегменты дедуплицированы тем же PairKey.
            if (loopNode >= 0 && ChainNodes.Count >= 2)
            {
                int lastGid = NodeGids[NodeGids.Count - 1];
                int loopGid = NodeGids[loopNode];
                Color loopC = KindColor(NodeKinds[loopNode]);
                float loopW = GroupWidth(loopGid);
                bool closeLeg = lastGid != 0 && loopGid != 0
                    ? DrawnGidLegs.Add(PairKey(lastGid, loopGid))
                    : true;
                if (closeLeg)
                {
                    if (ChainPts.Count == 0)
                    {
                        ChainPts.Add(ChainNodes[ChainNodes.Count - 1]);
                        ChainCols.Add(loopC);
                        ChainAlphas.Add(a);
                        ChainWidths.Add(loopW);
                    }
                    ChainPts.Add(ChainNodes[loopNode]);
                    ChainCols.Add(loopC);
                    ChainAlphas.Add(hover >= 0 && loopNode >= hover ? Mathf.Max(a, 1f) : a);
                    ChainWidths.Add(loopW);
                    ChainFlow.Add(true); // цикл патруля — бегущие шевроны
                }
            }
            if (ChainPts.Count >= 2)
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);

            // Маркеры: групповые — один на приказ (в центроиде, крупнее — √n юнитов),
            // индивидуальные — свои; форма = вид точки (квадрат/ромб/цикл), цвет —
            // цвет вида; наведённый — крупнее и ярче + подсказка конвертации хвоста.
            // Цели — крестики: текущая погоня — пульсирующий двойной, ждущие — статичные.
            for (int i = 0; i < ChainNodes.Count; i++)
            {
                Unit t = ChainNodeTargets[i];
                if (t != null)
                {
                    if (!ChainNodeOwned[i])
                        continue; // крест уже нарисовал владелец ноги к этой цели
                    Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(t));
                    if (sp.z <= 0f)
                        continue;
                    var scr = new Vector2(sp.x, Screen.height - sp.y);
                    if (pursuing && t == curTarget)
                    {
                        float pulse = 10f + 8f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f));
                        DrawCross(scr, pulse * 2f, attackC);
                        DrawCross(scr, pulse, attackC);
                    }
                    else
                        DrawCross(scr, 14f, attackC);
                    continue;
                }
                GroundOrderKind kind = NodeKinds[i];
                WaypointShape shape = ShapeOf(kind);
                bool hovered = i == hover;
                // Маркер — БЕЛЫЙ (канон FA: waypoint_color 44/88/ff ffffff), вид
                // приказа несёт ФОРМА маркера и цвет НОГИ.
                if (NodeGids[i] != 0)
                {
                    if (DrawnGidMarkers.Add(NodeGids[i]))
                    {
                        float n = GidPointSums.TryGetValue(NodeGids[i], out Vector4 acc) ? acc.w : 1f;
                        float sizeMul = Mathf.Min(Mathf.Sqrt(Mathf.Max(n, 1f)), 1.6f);
                        DrawGroupWaypointMarker(cam, ChainNodes[i], Color.white,
                            hovered ? 1f : a, shape, hovered ? sizeMul * 1.25f : sizeMul);
                    }
                }
                else
                {
                    DrawWorldWaypointColored(cam, ChainNodes[i], Color.white,
                        hovered ? 1f : a, shape, hovered ? 1.25f : 1f);
                }
                // Подсказка конвертации (выделенным): хвост от этой точки можно
                // замкнуть в патруль — говорим прямо у маркера, как Moho cursor text.
                if (hovered && a > 0.5f && NodeAbs[i] >= 0 &&
                    GroundOrders.CanConvertPatrolAt(unit, NodeAbs[i]))
                {
                    Vector3 sp = cam.WorldToScreenPoint(ChainNodes[i]);
                    if (sp.z > 0f)
                        GUI.Label(new Rect(sp.x + 14f, Screen.height - sp.y - 10f, 320f, 20f),
                            Loc.Tr("ПКМ — патруль отсюда"), _small);
                }
            }
        }

        /// <summary>Точка цели — на поверхности (вода — уровень моря): кресты
        /// по кораблям не проваливаются на дно.</summary>
        private static Vector3 TargetGround(Unit target)
        {
            Vector3 tp = target.transform.position;
            return new Vector3(tp.x, GameApi.RouteY(tp.x, tp.z), tp.z);
        }

        /// <summary>Связи сопровождения наземки/флота (v0.97): лента «эскорт → цель»
        /// и ЗЕЛЁНЫЙ маркер на цели (один на цель; дедуп общий с авиа-эскортом —
        /// DrawPlanCurve). Выделенные ярко, при Shift и остальные приглушённо —
        /// как цепочки.</summary>
        private static void DrawEscortLinks(Camera cam, CommanderModeController controller,
            bool shiftHeld)
        {
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            GroundOrders.CollectEscorts(EscortUnits, EscortTargetsBuf, hq);
            if (EscortUnits.Count == 0)
                return;
            Color c = ModeColor(AirOrderMode.Escort);
            for (int i = 0; i < EscortUnits.Count; i++)
            {
                Unit unit = EscortUnits[i];
                bool own = controller.Selection.Selected.Contains(unit);
                if (!own && !shiftHeld)
                    continue;
                Unit target = EscortTargetsBuf[i];
                if (target == null || target.disabled)
                    continue;
                float a = own ? 0.87f : 0.2f;
                if (!DrawnEscortMarkers.Add(target))
                    continue; // ногу и маркер на цель уже нарисовал первый эскорт
                Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                anchor.y = GameApi.GroundY(anchor.x, anchor.z);
                ChainPts.Clear();
                ChainCols.Clear();
                ChainAlphas.Clear();
                ChainPts.Add(anchor);
                ChainPts.Add(TargetGround(target));
                ChainCols.Add(c);
                ChainAlphas.Add(a);
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                DrawWorldWaypointColored(cam, TargetGround(target), Color.white, a); // канон FA
            }

            // Связи ремонта (v0.149): жёлтая лента «инженер → цель», одна на цель
            // (маркер на цели рисует DrawRepairMarkers — белую точку не дублируем).
            GroundOrders.CollectRepairs(EscortUnits, EscortTargetsBuf, hq);
            if (EscortUnits.Count > 0)
            {
                Color rc = ModeColor(AirOrderMode.Escort); // семейство Repair = жёлтый (канон FA)
                for (int i = 0; i < EscortUnits.Count; i++)
                {
                    Unit unit = EscortUnits[i];
                    bool own = controller.Selection.Selected.Contains(unit);
                    if (!own && !shiftHeld)
                        continue;
                    Unit target = EscortTargetsBuf[i];
                    if (target == null || target.disabled)
                        continue;
                    if (!DrawnRepairMarkers.Add(target))
                        continue; // ногу на цель уже нарисовал первый ремонтник
                    float a = own ? 0.87f : 0.2f;
                    Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                    anchor.y = GameApi.GroundY(anchor.x, anchor.z);
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                    ChainPts.Add(anchor);
                    ChainPts.Add(TargetGround(target));
                    ChainCols.Add(rc);
                    ChainAlphas.Add(a);
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                }
            }

            // Связи охраны (v0.158): янтарная лента «страж → цель» — семейство
            // жёлтых (сервисные связи), но отличима от эскорта/ремонта. Одна на
            // цель (первый страж рисует ногу и маркер).
            GroundOrders.CollectGuards(EscortUnits, EscortTargetsBuf, hq);
            if (EscortUnits.Count == 0)
                return;
            Color gc = GuardLinkColor;
            for (int i = 0; i < EscortUnits.Count; i++)
            {
                Unit unit = EscortUnits[i];
                bool own = controller.Selection.Selected.Contains(unit);
                if (!own && !shiftHeld)
                    continue;
                Unit target = EscortTargetsBuf[i];
                if (target == null || target.disabled)
                    continue;
                if (!DrawnGuardMarkers.Add(target))
                    continue; // ногу на цель уже нарисовал первый страж
                float a = own ? 0.87f : 0.2f;
                Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                anchor.y = GameApi.GroundY(anchor.x, anchor.z);
                ChainPts.Clear();
                ChainCols.Clear();
                ChainAlphas.Clear();
                ChainPts.Add(anchor);
                ChainPts.Add(TargetGround(target));
                ChainCols.Add(gc);
                ChainAlphas.Add(a);
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                DrawWorldWaypointColored(cam, TargetGround(target), Color.white, a); // канон FA
            }
        }
    }
}
