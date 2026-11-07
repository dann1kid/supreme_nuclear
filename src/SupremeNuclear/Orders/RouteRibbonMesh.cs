using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Меш-лента маршрутов (как в SupCom), рисуется напрямую через Graphics.DrawMesh
    /// каждый кадр — без GameObject/MeshRenderer, поэтому ей не мешают culling mask и
    /// фильтры рендер-конвейера игры. Пересборка — по Version приказов + таймер 0.5 с.
    /// Подача — в Application.onBeforeRender с компенсацией сдвига floating origin
    /// (SubmitBeforeRender): OriginShift игры идёт в LateUpdate камеры, ПОСЛЕ нашей
    /// подачи из Update — без компенсации кадр сдвига рисовал ленту по старым
    /// координатам, рывок на весь вектор сдвига («вейпоинты лезут в камеру»).</summary>
    public static class RouteRibbonMesh
    {
        private const float RebuildSec = 0.5f;
        private const float HeightBias = 3f;

        private static Mesh[] _meshes;

        /// <summary>Число сабмешей-цветов: 0 ход/RTB, 1 атака-движение, 2 патруль,
        /// 3 атака, 4 залп, 5 сопровождение.</summary>
        private const int SubmeshCount = 6;
        private static Material[] _mats;
        private static int _layer;
        private static float _nextRebuild;
        private static float _nextDonorRetry;
        private static int _lastAirVersion = -1;
        private static int _lastGroundVersion = -1;
        private static bool _ready;

        private sealed class RouteDraw
        {
            public int Submesh;
            /// <summary>Индивидуальное смещение высоты (анти-z-fight: копланарные
            /// ленты членов группы мерцали «подразбитиями», плейтест v0.114).</summary>
            public float Bias;
            /// <summary>Подмеш каждого СЕГМЕНТА (World.Count-1 записей; null — вся
            /// лента фиксированным Submesh). Непрерывная геометрия, цвет — по виду
            /// ноги: канон FA (кривая не рвётся на границе приказов).</summary>
            public List<int> SegSub;
            public readonly List<Vector3> World = new List<Vector3>();
        }

        /// <summary>Смещение высоты текущего юнита (ставится перед сборкой его лент).</summary>
        private static float CurBias;

        // Глобальные буферы меша: вершины/UV общие на все сабмеши, треугольники —
        // по сабмешам (общие вершины на границах цветов: геометрия не рвётся).
        private static readonly List<Vector3> MeshVerts = new List<Vector3>();
        private static readonly List<Vector2> MeshUv = new List<Vector2>();
        private static readonly List<int>[] Tris =
        {
            new List<int>(), new List<int>(), new List<int>(),
            new List<int>(), new List<int>(), new List<int>()
        };
        // Сглаживание цепочки Эрмит-кривыми перед сэмплированием рельефа (канон
        // Moho UICommandGraph); ChainSubs — подмеш каждой ноги непрерывной ленты.
        private static float[] _ptU; // UV-дистанция точек текущей ленты (тайлы)
        private static readonly List<Vector3> ChainPts = new List<Vector3>();
        private static readonly List<Vector3> SmoothChain = new List<Vector3>();
        private static readonly List<int> SmoothLegs = new List<int>();
        private static readonly List<int> ChainSubs = new List<int>();
        // Адаптивная ширина от камеры: переборка при движении/зуме (троттл).
        private static Vector3 _lastCamPos = Vector3.zero;
        private static Vector3 _lastCamFwd = Vector3.zero;
        private static float _nextCamRebuild;

        private static Vector3 _lastOriginPos;

        // Origin на момент последней сборки вершин: кадр OriginShift рисует ленту
        // с компенсирующей трансляцией (SubmitBeforeRender), свежие координаты
        // принесёт ближайший Tick (originMoved). Лечит рывок ленты на весь вектор
        // сдвига при движении камеры (threshold 1024 м, сдвиг до километров по Y).
        private static Vector3 _builtOriginPos;
        private static bool _beforeRenderHooked;
        private static bool _drawWanted;

        public static void Tick()
        {
            if (!Settings.RouteUseMesh || !GameApi.InMission)
                return;
            AnimateFlow(); // бегущие шевроны — каждый кадр (сдвиг UV материала)

            // Сдвиг floating origin = смена системы координат: меш обязан пересобраться.
            Transform origin = Datum.origin;
            bool originMoved = origin != null && origin.position != _lastOriginPos;
            if (origin != null)
                _lastOriginPos = origin.position;

            // Адаптивная ширина зависит от камеры: движение/зум → переборка
            // (троттл 0.15 с — ширина чуть запаздывает за камерой, незаметно).
            bool camMoved = false;
            Camera cam = GameApi.MainCam;
            if (cam != null)
            {
                Vector3 pos = cam.transform.position;
                Vector3 fwd = cam.transform.forward;
                if ((pos - _lastCamPos).sqrMagnitude > 4f ||
                    (fwd - _lastCamFwd).sqrMagnitude > 1e-4f)
                {
                    if (Time.unscaledTime >= _nextCamRebuild)
                    {
                        _nextCamRebuild = Time.unscaledTime + 0.15f;
                        _lastCamPos = pos;
                        _lastCamFwd = fwd;
                        camMoved = true;
                    }
                }
            }

            bool dirty = originMoved || camMoved ||
                         AircraftOrders.Version != _lastAirVersion ||
                         GroundOrders.Version != _lastGroundVersion;
            if (dirty || Time.unscaledTime >= _nextRebuild)
            {
                _nextRebuild = Time.unscaledTime + RebuildSec;
                _lastAirVersion = AircraftOrders.Version;
                _lastGroundVersion = GroundOrders.Version;

                var routes = new List<RouteDraw>();
                if (Settings.RouteDebugQuad)
                    routes.Add(DebugFrame());
                CollectGround(routes);
                CollectAir(routes);
                Rebuild(routes);
            }
        }

        /// <summary>Запрос подачи на кадр (вызывается из Update контроллера после Tick).
        /// Сама подача — SubmitBeforeRender: OriginShift игры бежит в LateUpdate КАМЕРЫ —
        /// ПОСЛЕ нашей подачи из Update, и кадр сдвига рисовал ленту по старым
        /// координатам (рывок на весь вектор сдвига — до километров, включая Y —
        /// высоту камеры: «вейпоинты лезут в камеру», разбор 2026-11-06).</summary>
        public static void Render()
        {
            _drawWanted = true;
            if (!_beforeRenderHooked)
            {
                _beforeRenderHooked = true;
                Application.onBeforeRender += SubmitBeforeRender;
            }
        }

        /// <summary>Подача мешей ПОСЛЕ всех LateUpdate (в т.ч. OriginShift) — прямо
        /// перед рендером; правило №16 соблюдено: это «до рендера», не OnGUI.
        /// Вершина запекалась при origin P0 как «глобал + P0»; после сдвига (P0→P1)
        /// её мир = «глобал + P1» = V + (P1 − P0) — рисуем с точной трансляцией,
        /// ближайший Tick (originMoved) принесёт уже свежие координаты.</summary>
        private static void SubmitBeforeRender()
        {
            if (!_drawWanted)
                return;
            _drawWanted = false;
            if (!_ready || _meshes == null || _mats == null)
                return;
            Vector3 off = Datum.origin != null
                ? Datum.origin.position - _builtOriginPos
                : Vector3.zero;
            var draw = Matrix4x4.Translate(off);
            for (int i = 0; i < SubmeshCount; i++) // все сабмеши: залп (4) раньше не подавался
            {
                if (_meshes[i] == null || _meshes[i].vertexCount == 0 || _mats[i] == null)
                    continue;
                Graphics.DrawMesh(_meshes[i], draw, _mats[i], _layer,
                    null, 0, null, UnityEngine.Rendering.ShadowCastingMode.Off, false);
            }
        }

        private static RouteDraw DebugFrame()
        {
            GameApi.TryGetMapBounds(out Vector2 bmin, out Vector2 bmax);
            Vector3 c = new Vector3((bmin.x + bmax.x) * 0.5f, 0f, (bmin.y + bmax.y) * 0.5f);
            float y = GameApi.RouteY(c.x, c.z) + 800f;
            var frame = new RouteDraw { Submesh = 0 };
            frame.World.Add(new Vector3(c.x - 1000f, y, c.z - 1000f));
            frame.World.Add(new Vector3(c.x + 1000f, y, c.z - 1000f));
            frame.World.Add(new Vector3(c.x + 1000f, y, c.z + 1000f));
            frame.World.Add(new Vector3(c.x - 1000f, y, c.z + 1000f));
            frame.World.Add(new Vector3(c.x - 1000f, y, c.z - 1000f));
            return frame;
        }

        private static readonly List<Unit> MeshEscortUnits = new List<Unit>();
        private static readonly List<Unit> MeshEscortTargets = new List<Unit>();
        // Групповые циклы патруля, уже нарисованные в этой сборке (дедуп — цикл
        // рисует один член группы, §12-51 для патруля).
        private static readonly HashSet<int> PatrolCyclesDrawn = new HashSet<int>();
        // Групповой дедуп ЛИНЕЙНЫХ лент (как IMGUI DrawnGidLegs/DrawnGidEntries):
        // вход в группу и магистраль между групповыми узлами рисует первый член.
        private static readonly HashSet<int> MeshGidEntries = new HashSet<int>();
        private static readonly HashSet<long> MeshGidLegs = new HashSet<long>();

        private static long PairKeyM(int a, int b) => ((long)(uint)a << 32) | (uint)b;

        private static void CollectGround(List<RouteDraw> routes)
        {
            PatrolCyclesDrawn.Clear();
            MeshGidEntries.Clear();
            MeshGidLegs.Clear();
            var units = new List<Unit>();
            var queues = new List<IReadOnlyList<GlobalPosition>>();
            var indices = new List<int>();
            // Только СВОИ цепочки: AI-командир делит реестр приказов, чужие
            // мировые ленты игроку не видны (v0.143.8).
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            GroundOrders.CollectAll(units, queues, indices, hq);
            for (int i = 0; i < units.Count; i++)
            {
                Unit unit = units[i];
                // Цепочка инженера с живой очередью — служебная выданная голова;
                // маршрут инженера рисует IMGUI-проход очередей, ленту не дублируем.
                if (BuildTasks.ChainIsIssuedHead(unit))
                    continue;
                // Своё смещение высоты: групповые магистрали членов группы не лежат
                // копланарно (z-fight-мерцание «подразбитиями»).
                CurBias = (Mathf.Abs(unit.GetInstanceID()) % 16) * 0.12f;
                bool patrol = GroundOrders.KindOf(unit) == GroundOrderKind.Patrol;
                Vector3 up = unit.transform.position;
                int cur = GroundOrders.DrawStartIndex(unit); // текущая точка (голова ленты)
                if (patrol)
                {
                    // Групповой цикл рисуется ОДИН раз (первый встреченный член):
                    // N копий с разными UV-фазами размывали шевроны наложением.
                    // Остальные члены рисуют только свою ногу «якорь → текущая».
                    int cycleGid = cur < queues[i].Count
                        ? GroundOrders.GroupIdOf(unit, Mathf.Min(cur, GroundOrders.PatrolLoopStartOf(unit)))
                        : 0;
                    if (cycleGid != 0 && !PatrolCyclesDrawn.Add(cycleGid))
                    {
                        ChainPts.Clear();
                        ChainPts.Add(new Vector3(up.x, GameApi.RouteY(up.x, up.z), up.z));
                        if (cur < queues[i].Count)
                        {
                            Vector3 l = queues[i][cur].ToLocalPosition();
                            ChainPts.Add(new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z));
                        }
                        if (ChainPts.Count > 1)
                            AddSmoothedRoute(routes, 2, ChainPts);
                        continue;
                    }
                    // (cur/loopStart объявлены выше — блок группового дедупа)
                    int loopStart = GroundOrders.PatrolLoopStartOf(unit);
                    ChainPts.Clear();
                    ChainPts.Add(new Vector3(up.x, GameApi.RouteY(up.x, up.z), up.z));
                    for (int q = cur; q < queues[i].Count; q++)
                    {
                        Vector3 l = queues[i][q].ToLocalPosition();
                        ChainPts.Add(new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z));
                    }
                    for (int q = loopStart; q < cur; q++)
                    {
                        Vector3 l = queues[i][q].ToLocalPosition();
                        ChainPts.Add(new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z));
                    }
                    if (cur < queues[i].Count && ChainPts.Count > 2)
                    {
                        Vector3 l = queues[i][cur].ToLocalPosition();
                        ChainPts.Add(new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z)); // замыкание на текущую
                    }
                    AddSmoothedRoute(routes, 2, ChainPts);
                    continue;
                }
                // Линейная цепочка — непрерывный путь с ГРУППОВЫМ ДЕДУПОМ (как IMGUI
                // DrawnGidLegs/Entries): вход в группу и магистраль между соседними
                // групповыми узлами рисует ПЕРВЫЙ встретивший член, индивидуальные
                // ветки — каждый свой (граница гонки копий — §12-51 закрыт).
                ChainPts.Clear();
                ChainSubs.Clear();
                Vector3 runStart = new Vector3(up.x, GameApi.RouteY(up.x, up.z), up.z);
                int prevGid = 0;
                bool firstLeg = true;
                bool runOpen = false;
                for (int q = GroundOrders.DrawStartIndex(unit); q < queues[i].Count; q++)
                {
                    int gid = GroundOrders.GroupIdOf(unit, q);
                    Vector3 l = queues[i][q].ToLocalPosition();
                    Vector3 wp = new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z);
                    bool drawLeg = firstLeg && gid != 0
                        ? MeshGidEntries.Add(gid)
                        : prevGid != 0 && gid != 0
                            ? MeshGidLegs.Add(PairKeyM(prevGid, gid))
                            : true;
                    if (drawLeg)
                    {
                        if (!runOpen)
                        {
                            ChainPts.Clear();
                            ChainSubs.Clear();
                            ChainPts.Add(runStart);
                            runOpen = true;
                        }
                        ChainPts.Add(wp);
                        ChainSubs.Add(SubmeshOf(GroundOrders.KindOfPoint(unit, q)));
                    }
                    else if (runOpen && ChainPts.Count > 1)
                    {
                        AddSmoothedRoute(routes, ChainPts, ChainSubs, 0);
                        runOpen = false;
                    }
                    runStart = wp; // пропущенная магистраль: следующий прогон от ОБЩЕГО узла
                    prevGid = gid;
                    firstLeg = false;
                }
                if (runOpen && ChainPts.Count > 1)
                    AddSmoothedRoute(routes, ChainPts, ChainSubs, 0);
            }

            // Атак-ноги наземки (погоня за целью): юнит → цель, красный сабмеш —
            // иначе при меш-режиме подходы к целям пропадали (кресты остаются в HUD).
            for (int i = 0; i < units.Count; i++)
            {
                Unit target = GroundOrders.TargetOf(units[i]);
                if (target == null || target.disabled)
                    continue;
                Vector3 up = units[i].transform.position;
                var r = new RouteDraw { Submesh = 3, Bias = CurBias };
                r.World.Add(new Vector3(up.x, GameApi.RouteY(up.x, up.z), up.z));
                Vector3 tp = target.transform.position;
                AppendSamples(r.World, new Vector3(tp.x, GameApi.RouteY(tp.x, tp.z), tp.z));
                if (r.World.Count > 1)
                    routes.Add(r);
            }

            // Инженерные очереди (записи движений/площадок): движение — циан,
            // площадка — жёлтый (инженерное семейство FA); от юнита по записям.
            // Только свои: очереди AI-инженеров в том же реестре (v0.143.9).
            foreach (Unit unit in BuildTasks.UnitsWithSequences(hq))
            {
                var seq = BuildTasks.SequenceOf(unit);
                if (seq == null || seq.Count == 0)
                    continue;
                LastChainPoint = new Vector3(float.NaN, 0f, float.NaN); // хвост — свой, не чужой
                Vector3 up = unit.transform.position;
                int kind = -1;
                ChainPts.Clear();
                ChainPts.Add(new Vector3(up.x, GameApi.RouteY(up.x, up.z), up.z));
                for (int e = 0; e < seq.Count; e++)
                {
                    BuildTasks.SeqEntry entry = seq[e];
                    int entryKind = entry.Task != null ? 5 : 0;
                    Vector3 at = entry.Task != null
                        ? entry.Task.Site.ToLocalPosition()
                        : entry.Move.ToLocalPosition();
                    if (entryKind != kind && ChainPts.Count > 1)
                    {
                        AddSmoothedRoute(routes, kind == 5 ? 5 : 0, ChainPts);
                        ChainPts.Clear();
                        if (HasChainPoint)
                            ChainPts.Add(LastChainPoint);
                    }
                    kind = entryKind;
                    ChainPts.Add(new Vector3(at.x, GameApi.RouteY(at.x, at.z), at.z));
                }
                if (ChainPts.Count > 1)
                    AddSmoothedRoute(routes, kind == 5 ? 5 : 0, ChainPts);
            }

            // Связи сопровождения наземки/флота (v0.97): лента «эскорт → цель».
            GroundOrders.CollectEscorts(MeshEscortUnits, MeshEscortTargets, hq);
            for (int i = 0; i < MeshEscortUnits.Count; i++)
            {
                Unit t = MeshEscortTargets[i];
                if (t == null || t.disabled)
                    continue;
                Vector3 up = MeshEscortUnits[i].transform.position;
                var r = new RouteDraw { Submesh = 5, Bias = CurBias };
                r.World.Add(new Vector3(up.x, GameApi.RouteY(up.x, up.z), up.z));
                Vector3 tp = t.transform.position;
                AppendSamples(r.World, new Vector3(tp.x, GameApi.RouteY(tp.x, tp.z), tp.z));
                if (r.World.Count > 1)
                    routes.Add(r);
            }
        }

        private static void CollectAir(List<RouteDraw> routes)
        {
            var units = new List<Unit>();
            var plans = new List<IReadOnlyList<AircraftOrders.OrderSegment>>();
            // Только СВОИ планы: AI-крылья (CAP/удары) летают по тому же реестру.
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            AircraftOrders.CollectAll(units, plans, hq);
            for (int i = 0; i < units.Count; i++)
            {
                Unit unit = units[i];
                CurBias = (Mathf.Abs(unit.GetInstanceID()) % 16) * 0.12f; // анти-z-fight (см. наземку)
                Vector3 up = unit.transform.position;
                Vector3 anchor = new Vector3(up.x, GameApi.RouteY(up.x, up.z), up.z);
                foreach (AircraftOrders.OrderSegment seg in plans[i])
                {
                    if (seg == null || (seg.Points.Count == 0 && seg.Target == null))
                        continue;
                    int sub = SubmeshOf(seg.Mode);
                    if (seg.Mode == AirOrderMode.Attack && seg.Target != null)
                    {
                        Vector3 tp = seg.Target.transform.position;
                        var r = new RouteDraw { Submesh = sub, Bias = CurBias };
                        r.World.Add(anchor);
                        AppendSamples(r.World, new Vector3(tp.x, GameApi.RouteY(tp.x, tp.z), tp.z));
                        if (r.World.Count > 1)
                            routes.Add(r);
                        anchor = r.World[r.World.Count - 1];
                        continue;
                    }
                    if (seg.Mode == AirOrderMode.Escort && seg.Target != null)
                    {
                        // Эскорт-сегмент (v0.97): зелёная нога «борт → цель».
                        Vector3 tp = seg.Target.transform.position;
                        var r = new RouteDraw { Submesh = sub, Bias = CurBias };
                        r.World.Add(anchor);
                        AppendSamples(r.World, new Vector3(tp.x, GameApi.RouteY(tp.x, tp.z), tp.z));
                        if (r.World.Count > 1)
                            routes.Add(r);
                        anchor = r.World[r.World.Count - 1];
                        continue;
                    }

                    var wps = new List<Vector3>(seg.Points.Count);
                    ChainPts.Clear();
                    ChainPts.Add(anchor);
                    for (int p = 0; p < seg.Points.Count; p++)
                    {
                        Vector3 l = seg.Points[p].ToLocalPosition();
                        Vector3 wp = new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z);
                        ChainPts.Add(wp);
                        wps.Add(wp);
                    }
                    if (seg.Mode == AirOrderMode.Patrol && wps.Count > 1)
                        ChainPts.Add(wps[0]); // замыкающая нога патруля
                    AddSmoothedRoute(routes, sub, ChainPts);
                    if (wps.Count > 0)
                        anchor = wps[wps.Count - 1];
                }
            }
        }

        /// <summary>Цепочка вейпоинтов → лента Эрмит-кривых (канон Moho,
        /// Util.HermiteChain, кламп тангенса от мировой ширины ленты): повороты
        /// «текут» как в SupCom; затем сэмплирование рельефа вдоль пути.</summary>
        private static Vector3 LastChainPoint = new Vector3(float.NaN, 0f, float.NaN);
        private static bool HasChainPoint => !float.IsNaN(LastChainPoint.x);
        // Подмеш каждой выходной точки-сегмента (после Эрмита и сэмплирования
        // рельефа) — непрерывная лента красится по виду ноги без разрывов.
        private static readonly List<int> SmoothSubs = new List<int>();

        private static void AddSmoothedRoute(List<RouteDraw> routes, int submesh, List<Vector3> chain) =>
            AddSmoothedRoute(routes, chain, null, submesh);

        /// <summary>Лента из цепочки: Эрмит → сэмплирование рельефа → RouteDraw.
        /// legSub — подмеш каждой ВЕРХНЕУРОВНЕВОЙ ноги (точки→точки); null — вся
        /// лента одним фиксированным подмешем. Выходные сегменты наследуют подмеш
        /// ноги, в которой лежат (маппинг SmoothLegs), — цвет меняется на границе
        /// ног при НЕПРЕРЫВНОЙ геометрии (общие вершины).</summary>
        private static void AddSmoothedRoute(List<RouteDraw> routes, List<Vector3> chain,
            List<int> legSub, int fixedSub)
        {
            int n = Util.HermiteChain(chain, null, Settings.RouteRibbonWidthM,
                Settings.RouteSampleStepM, SmoothChain, SmoothLegs);
            // Хвост пробега — ЗДЕСЬ, для всех вызывающих (стейт в вызывавшем месте
            // давал ПРОТАХШИЙ хвост чужой цепи → гигантский квад через камеру).
            if (n >= 2)
                LastChainPoint = SmoothChain[n - 1];
            if (n < 2)
                return;
            var route = new RouteDraw { Submesh = fixedSub, Bias = CurBias };
            // Сегменты выходных точек: расширяем вместе с сэмплированием рельефа —
            // сегменты внутри куска (j-1→j) принадлежат ноге SmoothLegs-интервала.
            route.World.Add(SmoothChain[0]);
            route.SegSub = legSub != null ? new List<int>(n) : null;
            int leg = 0;
            for (int j = 1; j < n; j++)
            {
                while (leg + 1 < SmoothLegs.Count && j > SmoothLegs[leg + 1])
                    leg++;
                int before = route.World.Count;
                AppendSamples(route.World, SmoothChain[j]);
                int sub = legSub != null && leg < legSub.Count ? legSub[leg] : fixedSub;
                for (int k = before; k < route.World.Count; k++)
                    route.SegSub.Add(sub);
            }
            routes.Add(route);
        }

        private static void AppendSamples(List<Vector3> world, Vector3 to)
        {
            Vector3 from = world[world.Count - 1];
            float dist = new Vector2(to.x - from.x, to.z - from.z).magnitude;
            int steps = Mathf.Clamp(Mathf.CeilToInt(dist / Settings.RouteSampleStepM), 1, 80);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float x = Mathf.Lerp(from.x, to.x, t);
                float z = Mathf.Lerp(from.z, to.z, t);
                world.Add(new Vector3(x, GameApi.RouteY(x, z), z));
            }
        }

        private static int SubmeshOf(AirOrderMode mode) =>
            mode switch
            {
                AirOrderMode.AttackMove => 1,
                AirOrderMode.Patrol => 2,
                AirOrderMode.Attack => 3,
                AirOrderMode.Salvo => 4, // залп — малиновый
                AirOrderMode.Escort => 5, // сопровождение — зелёный
                AirOrderMode.Rtb => 0, // цвет возврата даёт IMGUI; меш-режим экспериментальный
                _ => 0
            };

        /// <summary>Сабмеш цвета вида ТОЧКИ наземной цепочки (per-point, v0.114):
        /// та же палитра, что у режимов авиации.</summary>
        private static int SubmeshOf(GroundOrderKind kind) =>
            kind switch
            {
                GroundOrderKind.AttackMove => 1,
                GroundOrderKind.Patrol => 2,
                _ => 0
            };

        private static void Rebuild(List<RouteDraw> routes)
        {
            if (!EnsureMaterials())
                return;

            MeshVerts.Clear();
            MeshUv.Clear();
            for (int i = 0; i < SubmeshCount; i++)
                Tris[i].Clear();

            // Адаптивная ширина (как IMGUI-лента): у камеры — экранный потолок
            // ~6px полуширины на 1440p, в стратзуме — мировая RouteRibbonWidthM.
            // Тайл шеврона следует за ФАКТИЧЕСКОЙ шириной (u = дист/ширина×0.2):
            // вблизи шевроны сжимаются вместе с лентой и видны на любой ноге.
            Camera cam = GameApi.MainCam;
            float pxPerRad = 1f;
            Vector3 camPos = Vector3.zero;
            bool adaptive = cam != null && Screen.height > 0;
            if (adaptive)
            {
                camPos = cam.transform.position;
                pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                if (pxPerRad < 1f)
                    adaptive = false;
            }
            float worldHalf = Settings.RouteRibbonWidthM * 0.5f;
            float screenScale = Mathf.Max(Screen.height, 1) / 1440f;

            foreach (RouteDraw route in routes)
            {
                var world = route.World;
                int count = world.Count;
                if (count < 2)
                    continue;
                float bias = HeightBias + route.Bias;
                // Тайл шевронов — экранно-пропорционален (~80px на любом зуме),
                // паттерн плотный (3 шеврона на тайл).
                float TileAt(Vector3 c)
                {
                    if (!adaptive)
                        return Mathf.Max(8f, 5f * worldHalf * 2f);
                    return Mathf.Max(8f, 80f * (camPos - c).magnitude / pxPerRad);
                }
                // Данные точки: позиция, перпендикуляр, полуширина, UV-дистанция.
                void PointAt(int i, out Vector3 c, out Vector3 perp, out float half, out float u)
                {
                    c = world[i];
                    Vector3 next = world[i + 1 < count ? i + 1 : count - 1];
                    Vector3 prevP = world[i > 0 ? i - 1 : 0];
                    Vector3 dir = next - prevP;
                    Vector3 flat = new Vector3(dir.x, 0f, dir.z);
                    perp = flat.sqrMagnitude < 1e-6f
                        ? Vector3.right
                        : new Vector3(-flat.z, 0f, flat.x).normalized;
                    half = worldHalf;
                    if (adaptive)
                        half = Mathf.Min(worldHalf,
                            Mathf.Max(1.5f, 6f * screenScale * (camPos - c).magnitude / pxPerRad));
                    u = 0f;
                    if (i > 0)
                        u = _ptU[i - 1] + Vector3.Distance(world[i - 1], c) / TileAt(c);
                }
                // UV точки — сначала накопить (PointAt ссылается на предыдущую).
                if (_ptU == null || _ptU.Length < count)
                    _ptU = new float[Mathf.NextPowerOfTwo(count)];
                for (int i = 0; i < count; i++)
                {
                    PointAt(i, out _, out _, out _, out float uu);
                    _ptU[i] = uu;
                }
                // Пара вершин в точке (uv.x — внутри [0..1], БЕЗ зависимости от wrap
                // текстуры: сегменты режутся по границам тайлов, каждый квад живёт
                // в одном тайле — clamp-обёртка донора больше не прячет шевроны
                // за первым тайлом, плейтест «шеврон только в хвосте полилинии»).
                int EmitPair(Vector3 c, Vector3 perp, float half, float uFrac)
                {
                    int idx = MeshVerts.Count;
                    MeshVerts.Add(c + perp * half + Vector3.up * bias);
                    MeshVerts.Add(c - perp * half + Vector3.up * bias);
                    MeshUv.Add(new Vector2(uFrac, 0f));
                    MeshUv.Add(new Vector2(uFrac, 1f));
                    return idx;
                }

                for (int i = 1; i < count; i++)
                {
                    int sub = route.Submesh;
                    if (route.SegSub != null && i - 1 < route.SegSub.Count)
                        sub = route.SegSub[i - 1];
                    var tris = Tris[sub];
                    bool flow = sub == 1 || sub == 2; // шевронные сабмеши
                    PointAt(i - 1, out Vector3 a, out Vector3 perpA, out float halfA, out float u0);
                    PointAt(i, out Vector3 b, out Vector3 perpB, out float halfB, out float u1);
                    if (u1 < u0)
                        u1 = u0;
                    float du = u1 - u0;
                    if (!flow || du < 1e-4f)
                    {
                        // Мягкая полоса — UV не важен.
                        int v0 = EmitPair(a, perpA, halfA, 0f);
                        int v1 = EmitPair(b, perpB, halfB, Mathf.Min(du, 1f));
                        tris.Add(v0); tris.Add(v0 + 1); tris.Add(v1);
                        tris.Add(v0 + 1); tris.Add(v1 + 1); tris.Add(v1);
                        continue;
                    }
                    // Шевронная: разрез по целым тайлам — каждый квад внутри [k, k+1].
                    float ua = u0;
                    for (int k = Mathf.FloorToInt(u0) + 1; ; k++)
                    {
                        float ub = Mathf.Min(k, u1);
                        float fa = (ua - u0) / du;
                        float fb = (ub - u0) / du;
                        int v0 = EmitPair(
                            Vector3.LerpUnclamped(a, b, fa),
                            Vector3.SlerpUnclamped(perpA, perpB, fa),
                            Mathf.LerpUnclamped(halfA, halfB, fa), ua - Mathf.Floor(ua));
                        int v1 = EmitPair(
                            Vector3.LerpUnclamped(a, b, fb),
                            Vector3.SlerpUnclamped(perpA, perpB, fb),
                            Mathf.LerpUnclamped(halfA, halfB, fb), ub - Mathf.Floor(ub));
                        tris.Add(v0); tris.Add(v0 + 1); tris.Add(v1);
                        tris.Add(v0 + 1); tris.Add(v1 + 1); tris.Add(v1);
                        ua = ub;
                        if (ub >= u1 - 1e-6f)
                            break;
                    }
                }
            }

            for (int i = 0; i < SubmeshCount; i++)
            {
                if (_meshes[i] == null)
                    _meshes[i] = new Mesh { name = "RouteRibbon" + i };
                _meshes[i].Clear();
                _meshes[i].SetVertices(MeshVerts);
                _meshes[i].SetUVs(0, MeshUv);
                _meshes[i].SetTriangles(Tris[i], 0);
                _meshes[i].RecalculateNormals();
                _meshes[i].RecalculateBounds();
            }
            _builtOriginPos = Datum.origin != null ? Datum.origin.position : Vector3.zero;
            _ready = true;
        }

        private static bool EnsureMaterials()
        {
            if (_mats != null)
                return true;

            Material baseMat = null;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                MeshRenderer r = unit != null ? unit.GetComponentInChildren<MeshRenderer>() : null;
                if (r != null && r.sharedMaterial != null)
                {
                    baseMat = r.sharedMaterial;
                    _layer = r.gameObject.layer;
                    break;
                }
            }
            if (baseMat == null)
            {
                MeshRenderer r = Object.FindObjectOfType<MeshRenderer>();
                if (r != null && r.sharedMaterial != null)
                {
                    baseMat = r.sharedMaterial;
                    _layer = r.gameObject.layer;
                }
            }
            if (baseMat == null)
            {
                if (Time.unscaledTime >= _nextDonorRetry)
                {
                    _nextDonorRetry = Time.unscaledTime + 2f;
                    Log.Warn("RouteRibbonMesh: донор материала не найден, повтор через 2 с");
                }
                return false;
            }
            Log.Info("RouteRibbonMesh: donor " + baseMat.name + " (" + baseMat.shader.name + "), layer " + _layer);

            var colors = new[]
            {
                new Color(0f, 1f, 1f),     // 0 движение — циан FA
                new Color(1f, 0f, 0f),     // 1 атака-движение — красный FA (шевроны, аним.)
                new Color(0f, 1f, 1f),     // 2 патруль — циан FA (шевроны, аним.)
                new Color(1f, 0f, 0f),     // 3 атака — красный FA
                new Color(1f, 0f, 0f),     // 4 залп (Tactical) — красный FA
                new Color(1f, 1f, 0f)      // 5 эскорт/площадки — жёлтый FA
            };
            var white = new Texture2D(1, 1);
            white.SetPixel(0, 0, Color.white);
            white.Apply();

            // Текстуры лент (порт command_graph_renderer FA): шевронная — на
            // «живых» сабмешах (1 атака-движение, 2 патруль), мягкая — на статичных.
            CommanderHud.EnsureRibbonTextures();
            Texture2D soft = CommanderHud.RibbonTexSoft != null ? CommanderHud.RibbonTexSoft : white;
            Texture2D arrow = CommanderHud.RibbonTexArrow != null ? CommanderHud.RibbonTexArrow : white;

            _mats = new Material[SubmeshCount];
            for (int i = 0; i < SubmeshCount; i++)
            {
                bool flow = i == 1 || i == 2;
                Texture2D tex = flow ? arrow : soft;
                _mats[i] = new Material(baseMat);
                if (_mats[i].HasProperty("_BaseMap"))
                    _mats[i].SetTexture("_BaseMap", tex);
                if (_mats[i].HasProperty("_MainTex"))
                    _mats[i].SetTexture("_MainTex", tex);
                _mats[i].DisableKeyword("_NORMALMAP");
                if (_mats[i].HasProperty("_BaseColor"))
                    _mats[i].SetColor("_BaseColor", colors[i]);
                else
                    _mats[i].color = colors[i];
                MakeTransparent(_mats[i]);
            }
            _meshes = new Mesh[SubmeshCount];
            return true;
        }

        /// <summary>URP-хак прозрачности ленты: поверхность → Transparent, альфа-
        /// блендинг, без записи глубины, двусторонне. Неизвестные ключи донора
        /// не трогаем.</summary>
        private static void MakeTransparent(Material m)
        {
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.EnableKeyword("_BLENDMODE_ALPHA");
            m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            m.SetInt("_Cull", 0); // Off — двусторонне
            m.SetInt("_Surface", 1);
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>Анимация «живых» сабмешей (1 атака-движение, 2 патруль):
        /// бегущие шевроны = сдвиг UV текстуры, orderline_anim_rate FA = 0.25/с.</summary>
        private static void AnimateFlow()
        {
            if (_mats == null)
                return;
            float shift = -Mathf.Repeat(Time.unscaledTime * 0.25f, 1f);
            var off = new Vector2(shift, 0f);
            for (int i = 1; i <= 2; i++)
            {
                if (_mats[i].HasProperty("_BaseMap"))
                    _mats[i].SetTextureOffset("_BaseMap", off);
                if (_mats[i].HasProperty("_MainTex"))
                    _mats[i].SetTextureOffset("_MainTex", off);
            }
        }

        public static void Destroy()
        {
            if (_meshes != null)
            {
                foreach (Mesh m in _meshes)
                    if (m != null)
                        Object.Destroy(m);
                _meshes = null;
            }
            if (_mats != null)
            {
                foreach (Material m in _mats)
                    if (m != null)
                        Object.Destroy(m);
                _mats = null;
            }
            _drawWanted = false;
            _ready = false;
            _lastAirVersion = -1;
            _lastGroundVersion = -1;
        }
    }
}
