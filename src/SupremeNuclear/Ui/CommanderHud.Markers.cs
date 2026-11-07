using System.Collections.Generic;
using UnityEngine;


namespace SupremeNuclear
{
    // Часть CommanderHud: ресурсные/выделительные маркеры, ЯО-прицел, веер, метки залпов.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; OnGUI-диспетчер — CommanderHud.cs.
    public sealed partial class CommanderHud
    {
        // ---------- маркеры и выделение ----------

        /// <summary>Маркеры СВОБОДНЫХ месторождений (v3): жёлтый квадрат — масса,
        /// оранжевый ромб — уголь (SupCom: «видишь точку — строй»). Занятые скрыты
        /// у всех; построенные здания видны как юниты.</summary>
        private static void DrawDepositMarkers()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !EconomyService.Running)
                return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 3f);
            float s = 18f * (Screen.height / 1440f);
            foreach (EconomyService.DepositSite site in EconomyService.SiteList)
            {
                if (!EconomyService.SiteIsFree(site))
                    continue;
                Vector3 world = site.Pos.ToLocalPosition();
                world.y += 12f;
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0f || sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                Color color = site.Kind == EconomyService.DepositKind.Coal
                    ? new Color(1f, 0.55f, 0.2f, 1f)
                    : new Color(1f, 0.85f, 0.2f, 1f);
                var outer = new Rect(sp.x - s * 0.5f, guiY - s * 0.5f, s, s);
                DrawBoxOutline(outer, 1.5f, color);
                float innerSize = s * (0.25f + 0.35f * pulse);
                Color fill = color;
                fill.a = 0.35f + 0.5f * pulse;
                DrawRect(new Rect(sp.x - innerSize * 0.5f, guiY - innerSize * 0.5f, innerSize, innerSize), fill);
            }
        }

        /// <summary>Маркеры салвижа: обломки, которые прямо сейчас разбирают инженеры
        /// (оранжевая рамка, заполнение = прогресс разбора). Только активные сборы —
        /// поля боёв не шумят маркерами.</summary>
        private static void DrawSalvageMarkers()
        {
            Camera cam = GameApi.MainCam;
            var wrecks = SalvageService.MarkerWrecks;
            var progress = SalvageService.MarkerProgress;
            if (cam == null || wrecks.Count == 0)
                return;

            float s = 16f * (Screen.height / 1440f);
            Color color = new Color(1f, 0.55f, 0.15f, 1f);
            for (int i = 0; i < wrecks.Count; i++)
            {
                Wreckage wreck = wrecks[i];
                if (wreck == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(wreck.transform.position);
                if (sp.z <= 0f || sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                var outer = new Rect(sp.x - s * 0.5f, guiY - s * 0.5f, s, s);
                DrawBoxOutline(outer, 1.5f, color);
                float innerSize = s * (0.15f + 0.7f * progress[i]);
                Color fill = color;
                fill.a = 0.7f;
                DrawRect(new Rect(sp.x - innerSize * 0.5f, guiY - innerSize * 0.5f, innerSize, innerSize), fill);
            }
        }

        /// <summary>Маркеры ремонта (v0.149, канон SupCom — жёлтое семейство Repair,
        /// как ModeColor): цели, которые прямо сейчас чинят инженеры; заполнение =
        /// восстановленная доля от HP на начало работы.</summary>
        private static void DrawRepairMarkers()
        {
            Camera cam = GameApi.MainCam;
            var targets = RepairService.MarkerTargets;
            var from01 = RepairService.MarkerFrom01;
            var hp01 = RepairService.MarkerHp01;
            if (cam == null || targets.Count == 0)
                return;

            float s = 18f * (Screen.height / 1440f);
            Color color = new Color(1f, 1f, 0f, 1f);
            for (int i = 0; i < targets.Count; i++)
            {
                Unit target = targets[i];
                if (target == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(target));
                if (sp.z <= 0f || sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                var outer = new Rect(sp.x - s * 0.5f, guiY - s * 0.5f, s, s);
                DrawBoxOutline(outer, 2f, color);
                float span = Mathf.Max(0.05f, 1f - from01[i]);
                float progress = Mathf.Clamp01((hp01[i] - from01[i]) / span);
                float innerSize = s * (0.15f + 0.7f * progress);
                Color fill = color;
                fill.a = 0.55f;
                DrawRect(new Rect(sp.x - innerSize * 0.5f, guiY - innerSize * 0.5f, innerSize, innerSize), fill);
            }
        }

        /// <summary>Маркеры инженерного захвата (v3): оранжевый ромб-рамка на цели,
        /// заполнение = прогресс (как салваж, но обводка двойная — захват).</summary>
        private static void DrawCaptureMarkers()
        {
            Camera cam = GameApi.MainCam;
            var targets = CaptureService.MarkerTargets;
            var progress = CaptureService.MarkerProgress;
            if (cam == null || targets.Count == 0)
                return;

            float s = 18f * (Screen.height / 1440f);
            Color color = new Color(1f, 0.4f, 0.1f, 1f);
            for (int i = 0; i < targets.Count; i++)
            {
                Building b = targets[i];
                if (b == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(b));
                if (sp.z <= 0f || sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                var outer = new Rect(sp.x - s * 0.5f, guiY - s * 0.5f, s, s);
                DrawBoxOutline(outer, 2f, color);
                float innerSize = s * (0.15f + 0.7f * progress[i]);
                Color fill = color;
                fill.a = 0.55f;
                DrawRect(new Rect(sp.x - innerSize * 0.5f, guiY - innerSize * 0.5f, innerSize, innerSize), fill);
            }
        }

        private static Texture2D _ring;

        private static Texture2D RingTexture()
        {
            if (_ring != null)
                return _ring;
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = (size - 1) * 0.5f;
            float rOut = c - 2f;
            float rIn = c - 7f;
            for (int yy = 0; yy < size; yy++)
            {
                for (int xx = 0; xx < size; xx++)
                {
                    float d = Mathf.Sqrt((xx - c) * (xx - c) + (yy - c) * (yy - c));
                    float a = Mathf.Clamp01(rOut - d) - Mathf.Clamp01(rIn - d);
                    tex.SetPixel(xx, yy, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            _ring = tex;
            return tex;
        }

        private const int RingSegments = 40;
        private static readonly Vector2[] RingScratch = new Vector2[RingSegments];

        /// <summary>RTS-кольцо выделения (перспективный эллипс из точек):
        /// корабли/наземка/здания — «пояс» на середине высоты модели, у авиации — по земле.
        /// Точки кладутся равномерно по ВСЕЙ окружности — фиксированный бюджет точек
        /// обрезал дугу у крупных юнитов (кольцо корабля рисовалось на треть).</summary>
        private static void DrawSelectionRings(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            var color = new Color(0.55f, 1f, 0.55f, 0.85f);
            float dotPx = Mathf.Max(2f, 2.6f * (Screen.height / 1440f));
            int drawn = 0;
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                if (++drawn > 80)
                    break;

                Vector3 center = unit.transform.position;
                center.y = unit is Aircraft
                    ? GameApi.GroundY(center.x, center.z)
                    : center.y + GameApi.RingBeltOffset(unit);
                float worldRadius = unit.maxRadius * 2.6f;
                if (worldRadius < 4f)
                    worldRadius = 8f;

                // Кольцо не штампуем поверх окна миникарты (гасим по центру).
                Vector3 csp = cam.WorldToScreenPoint(center);
                if (csp.z > 0f && UnderMinimapWindow(new Vector2(csp.x, Screen.height - csp.y)))
                    continue;

                Vector2[] screenPts = RingScratch;
                bool visible = true;
                for (int i = 0; i < RingSegments; i++)
                {
                    float a = i / (float)RingSegments * Mathf.PI * 2f;
                    Vector3 wp = center + new Vector3(Mathf.Cos(a) * worldRadius, 0f, Mathf.Sin(a) * worldRadius);
                    Vector3 sp = cam.WorldToScreenPoint(wp);
                    if (sp.z <= 0f)
                    {
                        visible = false;
                        break;
                    }
                    screenPts[i] = new Vector2(sp.x, Screen.height - sp.y);
                }
                if (!visible)
                    continue;
                float perimeter = 0f;
                for (int i = 0; i < RingSegments; i++)
                    perimeter += Vector2.Distance(screenPts[i], screenPts[(i + 1) % RingSegments]);
                float meanRadius = perimeter / (2f * Mathf.PI);
                if (meanRadius < 4f || meanRadius > Screen.height)
                    continue;

                if (meanRadius < Settings.SelectionRingMinPx)
                {
                    Vector2 c = Vector2.zero;
                    for (int i = 0; i < RingSegments; i++)
                        c += screenPts[i];
                    c /= RingSegments;
                    float rr = Settings.SelectionRingMinPx;
                    Color prev = GUI.color;
                    GUI.color = color;
                    GUI.DrawTexture(new Rect(c.x - rr, c.y - rr, rr * 2f, rr * 2f), RingTexture());
                    GUI.color = prev;
                    continue;
                }

                // Равномерный шаг по всему кругу (шаг растёт с радиусом: плотная обводка
                // больших колец дорого стоит в IMGUI), но дуга всегда полная.
                int dots = Mathf.Clamp(Mathf.CeilToInt(perimeter / 9f), RingSegments, 80);
                for (int k = 0; k < dots; k++)
                {
                    float t = k * RingSegments / (float)dots;
                    int i = (int)t;
                    Vector2 p = Vector2.Lerp(screenPts[i], screenPts[(i + 1) % RingSegments], t - i);
                    DrawRect(new Rect(p.x - dotPx * 0.5f, p.y - dotPx * 0.5f, dotPx, dotPx), color);
                }
            }
        }

        private static void DrawSelectionBrackets(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            int drawn = 0;
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null || drawn > 80)
                    continue;
                if (GameApi.ApparentSizePx(unit, cam) < Settings.IconMinPx)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
                DrawCornerBrackets(new Rect(sp.x - 10f, guiY - 10f, 20f, 20f), new Color(0.6f, 1f, 0.6f, 0.95f));
                drawn++;
            }
        }

        /// <summary>Скобки осмотра на инспектируемой чужой цели/ракете (красные,
        /// крупнее скобок выделения): якорь совпадает с иконкой контакта — протухший
        /// контакт отмечается там, где он виделся в последний раз.</summary>
        private static void DrawInspectedBrackets(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            Unit unit = controller.Selection.Inspected;
            if (cam == null || unit == null)
                return;
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            TrackingInfo info = unit is Missile ? null : hq?.GetTrackingData(unit.persistentID);
            Vector3 sp = cam.WorldToScreenPoint(GameApi.ContactAnchorWorld(unit, info));
            if (sp.z <= 0f)
                return;
            float guiY = Screen.height - sp.y;
            if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                return;
            DrawCornerBrackets(new Rect(sp.x - 13f, guiY - 13f, 26f, 26f), new Color(1f, 0.4f, 0.35f, 0.95f));
        }

        private void DrawOrderMarkers(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            float now = Time.unscaledTime;
            controller.MoveOrders.PruneMarkers(now);
            foreach (MoveOrderService.Marker marker in controller.MoveOrders.Markers)
            {
                Vector3 sp = cam.WorldToScreenPoint(marker.Position);
                if (sp.z <= 0f)
                    continue;
                float age = Mathf.Clamp01((now - marker.SpawnedAt) / 2.5f);
                float size = 8f + 26f * age;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
                var color = new Color(1f, 0.8f, 0.2f, 1f - age);
                DrawCross(new Vector2(sp.x, guiY), size, color);
            }
        }

        private static readonly string[] FanNumbers = new string[32];

        /// <summary>ЯО (v0.139): заряженный режим «Ядерный удар» — у курсора КОЛЬЦО
        /// зоны поражения (нативная формула шоквейва от yield ракеты) + очередь
        /// точек пуска носителей пульсирующими крестами (ушедшие снимает сервис).</summary>
        private static void DrawNukeAim()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            Color c = new Color(1f, 0.75f, 0.2f, 0.9f);
            // Точки пуска в очереди: живут до пуска/гибели носителя.
            for (int i = 0; i < WarheadService.Strikes.Count; i++)
            {
                WarheadService.StrikePoint s = WarheadService.Strikes[i];
                Vector3 sp = cam.WorldToScreenPoint(s.Pos.ToLocalPosition());
                if (sp.z <= 0f)
                    continue;
                Vector2 scr = new Vector2(sp.x, Screen.height - sp.y);
                if (UnderMinimapWindow(scr))
                    continue;
                DrawCross(scr, 22f, c);
                ClosedLabel(scr.x - 10f, scr.y + 12f, Loc.Tr("ЯО"));
            }
            if (OrderModes.Pending != OrderMode.Nuke)
                return;
            // Прицел: кольцо поражения в точке земли под курсором.
            Ray ray = GameApi.ScreenRay(Input.mousePosition);
            if (!GameApi.TryGetGroundPoint(ray, out Vector3 ground))
                return;
            float radiusM = WarheadService.BlastRadiusM();
            Vector3 center = new Vector3(ground.x, GameApi.GroundYNoUnits(ground.x, ground.z), ground.z);
            Vector3 centerSp = cam.WorldToScreenPoint(center);
            if (centerSp.z <= 0f)
                return;
            Vector3 edge = cam.WorldToScreenPoint(center + cam.transform.right * radiusM);
            float rPx = Mathf.Abs(edge.x - centerSp.x);
            if (rPx < 4f)
                return;
            Vector2 ctr = new Vector2(centerSp.x, Screen.height - centerSp.y);
            int dots = Mathf.Clamp((int)(2f * Mathf.PI * rPx / 9f), 12, 64);
            for (int i = 0; i < dots; i++)
            {
                float a = i / (float)dots * 2f * Mathf.PI;
                Vector2 p = ctr + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rPx;
                DrawRect(new Rect(p.x - 1.3f, p.y - 1.3f, 2.6f, 2.6f), c);
            }
            ClosedLabel(ctr.x - 16f, ctr.y - rPx - 16f,
                Loc.F("ЯО · R {0} м", radiusM.ToString("F0")));
        }

        /// <summary>Накопленные цели «Распределения» (режим R): пульсирующий фиолетовый
        /// крест + порядковый номер (порядок кликов — приоритет ёмкости при раздаче).
        /// Мёртвые за время накопления пропускаются.</summary>
        private static void DrawFanTargets(CommanderModeController controller)
        {
            if (OrderModes.Pending != OrderMode.DisperseAttack)
                return;
            List<Unit> targets = controller.MoveOrders.FanTargets;
            if (targets.Count == 0)
                return;
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            Color c = new Color(0.7f, 0.5f, 1f, 0.95f); // фиолетовый: накопление, не выданная атака
            for (int i = 0; i < targets.Count; i++)
            {
                Unit target = targets[i];
                if (target == null || target.disabled) // Unity fake-null
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(target));
                if (sp.z <= 0f)
                    continue;
                Vector2 scr = new Vector2(sp.x, Screen.height - sp.y);
                if (UnderMinimapWindow(scr))
                    continue;
                float pulse = 10f + 8f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f));
                DrawCross(scr, pulse * 2f, c);
                DrawCross(scr, pulse, c);
                if (i < FanNumbers.Length)
                    GUI.Label(new Rect(scr.x + pulse + 8f, scr.y - 9f, 60f, 18f), FanNumbers[i], _small);
            }
        }

        /// <summary>Отметки залпов флота (FleetSalvo, v0.119–0.122): ОЧЕРЕДЬ — крест
        /// волны, ждущей трубы (по цели — СЛЕДУЕТ за ней), с линиями «корабль →
        /// точка»; СТРЕЛЯННЫЕ волны (Marks) — угасающий крест в точке удара
        /// (ленты — лишь MarkRibbonSec после залпа). Видно, кому какой приказ
        /// отдан и что ещё pending (точки GlobalPosition — FloatingOrigin-устойчивы;
        /// мёртвые корабли пропускаются).</summary>
        private const float MarkRibbonSec = 15f;
        // Голова цепи очереди: отстрелянная точка живёт в цепи, пока ракета летит.
        private const float ChainHeadSec = 20f;

        // Цепь очереди пусков на корабль (перезаполняется каждый кадр отрисовки).
        private static readonly Dictionary<Unit, List<Vector3>> SalvoChains = new Dictionary<Unit, List<Vector3>>();

        private static void DrawFleetSalvoMarks()
        {
            // Repaint-гейт обязателен: рисование в Layout-проходе ломает IMGUI-
            // состояние для ВСЕГО после нас (плейтест v0.127: пропали значки юнитов).
            if (Event.current.type != EventType.Repaint)
                return;
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            float now = Time.unscaledTime;
            Color baseC = ModeColor(AirOrderMode.Salvo); // цвет семейства «залп»

            // ОЧЕРЕДЬ ПУСКОВ (канон SupCom): у корабля ОДНА общая очередь запусков —
            // лента-ЦЕПЬ «корабль → точка₁ → точка₂ → …» как маршрут приказов, а не
            // пучок лент к каждой точке. Кресты — вейпоинты очереди.
            var queue = FleetSalvo.Queue;
            SalvoChains.Clear();
            for (int qi = 0; qi < queue.Count; qi++)
            {
                FleetSalvo.QueuedWave wave = queue[qi];
                // точка волны — на ПОВЕРХНОСТИ (RouteY: вода — зеркало, не дно):
                // ровно там же её пикает сканер вейпоинтов — иначе драг не берёт
                Vector3 aim = wave.Aim.ToLocalPosition();
                Vector3 world = wave.Target != null && wave.Target.disabled == false
                    ? GameApi.UnitAnchorWorld(wave.Target)
                    : new Vector3(aim.x, GameApi.RouteY(aim.x, aim.z), aim.z);
                Vector3 wsp = cam.WorldToScreenPoint(world);
                var wscr = new Vector2(wsp.x, Screen.height - wsp.y);
                Color c = baseC * 0.95f;
                if (wsp.z > 0f && !UnderMinimapWindow(wscr))
                {
                    // Маркер-узел ТОЧЕЧНОЙ волны — канон остальных вейпоинтов:
                    // редактируемое = белый маркер-ФОРМА (ромб — семейство огневых
                    // приказов), крест прицеливания поверх; волны по цели маркера
                    // не имеют (крест следует за юнитом, не таскаются). Hover —
                    // тот же пик-радиус, что у сканера: подсветка там, где возьмёт
                    // драг (правило «управляется там, где рисуется»).
                    bool hovered = false;
                    if (wave.Target == null)
                    {
                        hovered = Vector2.Distance(new Vector2(wsp.x, wsp.y),
                            new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y)) < WaypointScanner.PickPx;
                        DrawWorldWaypointColored(cam, world, Color.white,
                            hovered ? 1f : 0.9f, WaypointShape.Diamond, hovered ? 1.25f : 1f);
                    }
                    float pulse = 12f + 6f * Mathf.Abs(Mathf.Sin(now * 3f));
                    DrawCross(wscr, pulse, c);
                    DrawCross(wscr, pulse * 0.5f, c);
                    if (hovered)
                        GUI.Label(new Rect(wscr.x + 14f, wscr.y - 10f, 400f, 20f),
                            Loc.Tr("Залп: ЛКМ-драг — точка удара · Ctrl+Shift+ПКМ — снять волну"), _small);
                }
                // копим точку в цепь каждого ждущего корабля (порядок = порядок кликов)
                if (wsp.z > 0f)
                    for (int si = 0; si < wave.Ships.Count; si++)
                    {
                        Unit ship = wave.Ships[si];
                        if (ship == null || ship.disabled) // Unity fake-null
                            continue;
                        if (!SalvoChains.TryGetValue(ship, out List<Vector3> chain))
                        {
                            chain = new List<Vector3>();
                            SalvoChains[ship] = chain;
                        }
                        chain.Add(world);
                    }
            }
            // цепь каждого корабля: якорь → [последняя ОТСТРЕЛЯННАЯ точка, пока
            // ракета в полёте (ChainHeadSec)] → ждущие точки очереди; альфа гаснет
            // к хвосту (голова — ближайший пуск). КОНТРАКТ: цвета ПО НОГАМ (N-1).
            var marks = FleetSalvo.Marks;
            foreach (KeyValuePair<Unit, List<Vector3>> kv in SalvoChains)
            {
                Unit ship = kv.Key;
                List<Vector3> chain = kv.Value;
                if (ship == null || ship.disabled || chain.Count == 0) // Unity fake-null
                    continue;
                ChainPts.Clear();
                ChainCols.Clear();
                ChainAlphas.Clear();
                ChainPts.Add(GameApi.UnitAnchorWorld(ship));
                // голова: самая свежая отстрелянная точка ЭТОГО корабля (Until −
                // MarkTtlSec = момент отстрела) — первый клик тоже виден в цепи,
                // пока ракета летит (канон SupCom: пуск живёт в очереди)
                float bestFired = float.MinValue;
                int headMark = -1;
                for (int mi = 0; mi < marks.Count; mi++)
                {
                    FleetSalvo.SalvoMark m = marks[mi];
                    float firedAt = m.Until - FleetSalvo.MarkTtlSec;
                    if (now - firedAt > ChainHeadSec || firedAt <= bestFired)
                        continue;
                    for (int si = 0; si < m.Ships.Count; si++)
                        if (m.Ships[si] == ship)
                        {
                            bestFired = firedAt;
                            headMark = mi;
                            break;
                        }
                }
                if (headMark >= 0)
                {
                    Vector3 hp = marks[headMark].Point.ToLocalPosition();
                    ChainPts.Add(new Vector3(hp.x, GameApi.RouteY(hp.x, hp.z), hp.z));
                }
                for (int pi = 0; pi < chain.Count; pi++)
                    ChainPts.Add(chain[pi]);
                int legs = ChainPts.Count - 1;
                for (int l = 0; l < legs; l++)
                {
                    ChainCols.Add(baseC);
                    ChainAlphas.Add(Mathf.Max(0.35f, 0.85f - 0.12f * l));
                }
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, meshGated: false);
            }

            // отстрелянные волны: угасающие отметки (ждущие в очереди не рисуем —
            // их крест уже нарисован выше по живой цели)
            if (marks.Count == 0)
                return;
            for (int mi = marks.Count - 1; mi >= 0; mi--)
            {
                FleetSalvo.SalvoMark m = marks[mi];
                if (now >= m.Until)
                {
                    marks.RemoveAt(mi);
                    continue;
                }
                bool waiting = false;
                for (int qi = 0; qi < queue.Count; qi++)
                    if (queue[qi].MarkId == m.Id)
                    {
                        waiting = true;
                        break;
                    }
                if (waiting)
                    continue;
                // ОТСТРЕЛЯННАЯ волна: тусклый СТАТИЧНЫЙ крест (без пульса) — visibly
                // отличим от живой ждущей (та пульсирует ярко); тащить нечего
                Color c = baseC * (0.35f * Mathf.Clamp01((m.Until - now) / 10f));
                Vector3 mp = m.Point.ToLocalPosition();
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(mp.x, GameApi.RouteY(mp.x, mp.z), mp.z));
                if (sp.z <= 0f)
                    continue;
                var scr = new Vector2(sp.x, Screen.height - sp.y);
                if (!UnderMinimapWindow(scr))
                {
                    float pulse = 12f + 6f * Mathf.Abs(Mathf.Sin(now * 3f));
                    DrawCross(scr, pulse, c);
                    DrawCross(scr, pulse * 0.5f, c);
                }
                // ленты отстрелянной волны — только 15 с после залпа («кто только
                // что бил»); дальше тусклый крест без лент — паутина хвостов
                // читалась как «лента между целями» (плейтест v0.130)
                if (m.Until - now <= MarkRibbonSec)
                {
                    Vector3 markWorld = new Vector3(mp.x, GameApi.RouteY(mp.x, mp.z), mp.z);
                    for (int si = 0; si < m.Ships.Count; si++)
                    {
                        Unit ship = m.Ships[si];
                        if (ship == null || ship.disabled) // Unity fake-null
                            continue;
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        ChainPts.Add(GameApi.UnitAnchorWorld(ship));
                        ChainPts.Add(markWorld);
                        ChainCols.Add(c);
                        ChainAlphas.Add(0.4f * c.a);
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, meshGated: false);
                    }
                }
            }
        }
    }
}
