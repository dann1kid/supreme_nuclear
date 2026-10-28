using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    public enum WaypointKind
    {
        None,
        BuildSite,   // площадка задачи стройки
        SequenceMove, // запись-движение общей очереди инженера
        GroundPoint, // точка цепочки наземки/флота
        AirPoint,    // точка плана авиации
        RallyPoint,  // точка ралли-маршрута фабрики (владелец — фабрика, не юнит)
        EscortLink   // связь сопровождения (маркер — на ЦЕЛИ сопровождения)
    }

    /// <summary>ЕДИНЫЙ скан вейпоинтов под курсором (консолидация v0.48):
    /// раньше поиск существовал в двух расходящихся копиях — RouteEditor.TryGrab
    /// (захват для драга) и MoveOrderService.TryDeleteWaypoint (удаление). Теперь
    /// оба зовут этот скан; порядок приоритета один: площадки → движения очередей →
    /// точки ралли фабрик → цепочки наземки → планы авиации → связи сопровождения.
    /// allUnits = режим видимости (Shift).</summary>
    public readonly struct WaypointHit
    {
        public readonly WaypointKind Kind;
        public readonly Unit Owner;            // SequenceMove/GroundPoint/AirPoint
        public readonly BuildTasks.Task Task;  // BuildSite
        public readonly object RallyFactory;   // RallyPoint: фабрика-владелец маршрута
        public readonly int Segment;           // AirPoint: индекс сегмента плана
        public readonly int Index;             // индекс точки в цепочке/очереди/сегменте

        public WaypointHit(WaypointKind kind, Unit owner, BuildTasks.Task task, int segment, int index)
        {
            Kind = kind;
            Owner = owner;
            Task = task;
            RallyFactory = null;
            Segment = segment;
            Index = index;
        }

        public WaypointHit(WaypointKind kind, object rallyFactory, int index)
        {
            Kind = kind;
            Owner = null;
            Task = null;
            RallyFactory = rallyFactory;
            Segment = 0;
            Index = index;
        }
    }

    internal static class WaypointScanner
    {
        /// <summary>ЕДИНЫЙ пик-радиус всех операций над вейпоинтами под курсором
        /// (захват драга, удаление, конвертация в патруль) — «что под курсором»
        /// одно, радиусы не должны расходиться. Настройка: Orders → WaypointPickPx.</summary>
        public static float PickPx => Settings.WaypointPickPx;

        // Центроиды групповых точек (наземка — остаток очереди, авиация — все точки
        // плана; зеркально отрисовке). Групповой маркер рисуется в ЦЕНТРОИДЕ
        // формации — «вейпоинт управляется там, где рисуется» (v0.61.2): раньше
        // удаление/захват ловили только фактические точки бортов, а у авиации
        // решётка формации 240 м — по нарисованному маркеру промахивались (клик
        // проваливался в выдачу нового приказа). Скан — event-time.
        private static readonly Dictionary<int, Vector4> GroupCentroids =
            new Dictionary<int, Vector4>();
        private static readonly List<Unit> CUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<GlobalPosition>> CQueues =
            new List<IReadOnlyList<GlobalPosition>>();
        private static readonly List<int> CIdx = new List<int>();
        private static readonly List<IReadOnlyList<AircraftOrders.OrderSegment>> CPlans =
            new List<IReadOnlyList<AircraftOrders.OrderSegment>>();
        private static readonly List<Unit> CTargets = new List<Unit>();
        private static readonly HashSet<Unit> SeenEscortTargets = new HashSet<Unit>();
        private static readonly List<object> RallyKeys = new List<object>();
        // Владельцы сканируемого вида (выделение + allUnits-надстройка) — буферы
        // переиспользуются: раньше скан копировал Selected трижды и вёл дедуп
        // линейным Contains (§12-45в, v0.112).
        private static readonly List<Unit> Owners = new List<Unit>();
        private static readonly HashSet<Unit> OwnerSeen = new HashSet<Unit>();
        private static readonly List<Unit> EngBuf = new List<Unit>();
        // Наземный сбор — ОТДЕЛЬНО от CUnits: авиа-сбор из AccumulateGroupCentroids
        // тогда живёт до §5/§6 без пересбора (раньше — второй CollectAll за скан).
        private static readonly List<Unit> GUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<GlobalPosition>> GQueues =
            new List<IReadOnlyList<GlobalPosition>>();
        private static readonly List<int> GIdx = new List<int>();

        /// <summary>Начать список владельцев с выделения (null отфильтрованы).</summary>
        private static void OwnersFromSelection(CommanderModeController controller)
        {
            Owners.Clear();
            OwnerSeen.Clear();
            foreach (Unit u in controller.Selection.Selected)
                if (u != null && OwnerSeen.Add(u))
                    Owners.Add(u);
        }

        private static void Accumulate(int gid, float x, float z)
        {
            GroupCentroids[gid] = GroupCentroids.TryGetValue(gid, out Vector4 acc)
                ? acc + new Vector4(x, 0f, z, 1f)
                : new Vector4(x, 0f, z, 1f);
        }

        private static Vector3 CentroidOf(int gid)
        {
            Vector4 acc = GroupCentroids[gid];
            float x = acc.x / acc.w, z = acc.z / acc.w;
            return new Vector3(x, GameApi.GroundY(x, z), z);
        }

        /// <summary>Точка, где РИСУЕТСЯ вейпоинт: групповая (gid≠0) — центроид
        /// формации группы, индивидуальная — сама точка.</summary>
        private static Vector3 PickPoint(int gid, Vector3 own)
        {
            return gid != 0 && GroupCentroids.TryGetValue(gid, out Vector4 _)
                ? CentroidOf(gid)
                : own;
        }

        private static void AccumulateGroupCentroids()
        {
            GroupCentroids.Clear();
            GroundOrders.CollectAll(CUnits, CQueues, CIdx);
            for (int i = 0; i < CUnits.Count; i++)
                for (int p = CIdx[i]; p < CQueues[i].Count; p++)
                {
                    int gid = GroundOrders.GroupIdOf(CUnits[i], p);
                    if (gid == 0)
                        continue;
                    Vector3 l = CQueues[i][p].ToLocalPosition();
                    Accumulate(gid, l.x, l.z);
                }
            AircraftOrders.CollectAll(CUnits, CPlans);
            for (int i = 0; i < CUnits.Count; i++)
                for (int s = 0; s < CPlans[i].Count; s++)
                {
                    AircraftOrders.OrderSegment seg = CPlans[i][s];
                    for (int p = 0; p < seg.PointGroups.Count; p++)
                    {
                        int gid = seg.PointGroups[p];
                        if (gid == 0)
                            continue;
                        Vector3 l = seg.Points[p].ToLocalPosition();
                        Accumulate(gid, l.x, l.z);
                    }
                }
        }

        /// <summary>Ближайший вейпоинт в радиусе px от точки экрана.
        /// Свои — по выделению; allUnits расширяет на всех (режим видимости).</summary>
        public static WaypointHit Scan(CommanderModeController controller, Camera cam,
            Vector2 mouse, float px, bool allUnits)
        {
            AccumulateGroupCentroids();
            // 1. Площадки стройки: СВОЕЙ фракции — всегда (клик по недострою
            // выделяет его карточку независимо от того, кто строит; плейтест
            // v0.96: верфь строила катер — без него в выделении недострой не
            // пиковался и его нельзя было снести), плюс при allUnits — любые.
            // Площадка — и маркер, и ТЕЛО 3D-призрака (тянуть/сносить удобно
            // за всю модель); фильтр один на оба способа попадания.
            GameApi.TryGetLocalHQ(out FactionHQ localHq);
            int ti = 0;
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                bool mine = allUnits || t.Faction == localHq;
                if (!mine)
                    foreach (Unit u in controller.Selection.Selected)
                        if (t.Builders.Contains(u))
                        {
                            mine = true;
                            break;
                        }
                if (mine && (Near(cam, t.Site.ToLocalPosition(), mouse, px) ||
                             BuildGhosts.ScreenContains(t, cam, mouse)))
                    return new WaypointHit(WaypointKind.BuildSite, null, t, 0, ti);
                ti++;
            }

            // 2. Записи-движения общих очередей инженеров.
            OwnersFromSelection(controller);
            if (allUnits)
            {
                BuildTasks.UnitsWithSequences(EngBuf);
                foreach (Unit eng in EngBuf)
                    if (OwnerSeen.Add(eng))
                        Owners.Add(eng);
            }
            foreach (Unit unit in Owners)
            {
                var seq = BuildTasks.SequenceOf(unit);
                if (seq == null)
                    continue;
                for (int i = 0; i < seq.Count; i++)
                {
                    if (seq[i].Task != null)
                        continue; // площадки — своим сканом выше
                    if (Near(cam, seq[i].Move.ToLocalPosition(), mouse, px))
                        return new WaypointHit(WaypointKind.SequenceMove, unit, null, 0, i);
                }
            }

            // 3. Точки ралли-маршрутов фабрик (v0.108, §12-30): зеркально отрисовке —
            // ралли ВЫДЕЛЕННЫХ фабрик, allUnits (Shift) расширяет на все живые.
            // Ралли — ИСТОЧНИК маршрутов спуска, копии точек живут в цепочках
            // вышедших юнитов ниже по приоритету: драг/удаление здесь правит фабрику
            // (будущие спуски), хвосты вышедших правятся их цепочками. Ключи-площадки
            // (ралли недостроенной фабрики) не отдаются: их маркер не рисуется —
            // вейпоинт управляется там, где рисуется.
            RallyKeys.Clear();
            ProductionTasks.CollectRallies(RallyKeys);
            for (int ri = 0; ri < RallyKeys.Count; ri++)
            {
                object f = RallyKeys[ri];
                if (!LiveRallyFactory(f) || (!allUnits && !FactorySelected(controller, f)))
                    continue;
                ProductionTasks.RallyRoute route = ProductionTasks.RallyOf(f);
                for (int p = 0; p < route.Count; p++)
                {
                    // Пик — там, где рисуется маркер: на земле (CommanderHud.GroundVec).
                    Vector3 l = route.Points[p].ToLocalPosition();
                    Vector3 v = new Vector3(l.x, GameApi.GroundY(l.x, l.z), l.z);
                    if (Near(cam, v, mouse, px))
                        return new WaypointHit(WaypointKind.RallyPoint, f, p);
                }
            }

            // 4. Цепочки наземки/флота.
            OwnersFromSelection(controller);
            if (allUnits)
            {
                GroundOrders.CollectAll(GUnits, GQueues, GIdx);
                for (int i = 0; i < GUnits.Count; i++)
                    if (GIdx[i] < GQueues[i].Count && OwnerSeen.Add(GUnits[i]))
                        Owners.Add(GUnits[i]);
            }
            foreach (Unit unit in Owners)
            {
                if (unit == null || !GroundOrders.TryGetQueue(unit, out var queue, out _))
                    continue;
                // Цепочка инженера с живой очередью — служебная выданная голова:
                // она сканируется записью очереди выше (SequenceMove), два скана
                // одной точки драли бы/удаляли разные хранилища (v0.82.2).
                if (BuildTasks.ChainIsIssuedHead(unit))
                    continue;
                int start = GroundOrders.DrawStartIndex(unit); // производная точка
                // подхода атак не сканируется — она не редактируется
                for (int p = start; p < queue.Count; p++)
                {
                    // Групповая точка пикается в ЦЕНТРОИДЕ (там, где маркер).
                    Vector3 l = PickPoint(GroundOrders.GroupIdOf(unit, p), queue[p].ToLocalPosition());
                    if (Near(cam, l, mouse, px))
                        return new WaypointHit(WaypointKind.GroundPoint, unit, null, 0, p);
                }
            }

            // 5. Планы авиации. Авиа-сбор уже лежит в CUnits/CPlans с начала скана
            // (AccumulateGroupCentroids; наземные цепочки выше шли по СВОИМ буферам
            // GUnits — этот сбор не перезаписывался) — пересбор не нужен, раньше
            // здесь был второй CollectAll за один скан.
            OwnersFromSelection(controller);
            if (allUnits)
                for (int i = 0; i < CUnits.Count; i++)
                    if (OwnerSeen.Add(CUnits[i]))
                        Owners.Add(CUnits[i]);
            foreach (Unit unit in Owners)
            {
                if (!(unit is Aircraft) || !AircraftOrders.TryGetPlan(unit, out var plan))
                    continue;
                for (int s = 0; s < plan.Count; s++)
                    for (int p = 0; p < plan[s].Points.Count; p++)
                    {
                        // Служебные точки (взлёт патруля p=0, одиночный стоп-старт) —
                        // не вейпоинты приказа: скан их не отдаёт (нет захвата/удаления).
                        if (AircraftOrders.IsServicePoint(plan[s], p))
                            continue;
                        // Общее правило: вейпоинт управляется там, где рисуется. Авиаточки
                        // хранят AGL-высоту, а маркер лежит НА ЗЕМЛЕ (DrawPlanCurve/GroundVec):
                        // при наклонной камере проекции расходятся на сотни пикселей —
                        // захват и удаление точек (в т.ч. патруля) промахивались.
                        // Групповая точка пикается в ЦЕНТРОИДЕ формации (там, где маркер).
                        Vector3 own = plan[s].Points[p].ToLocalPosition();
                        own.y = GameApi.GroundY(own.x, own.z);
                        if (Near(cam, PickPoint(plan[s].PointGroups[p], own), mouse, px))
                            return new WaypointHit(WaypointKind.AirPoint, unit, null, s, p);
                    }
            }

            // 6. Связи сопровождения: маркер рисуется на ЦЕЛИ — там же и пикается
            // (Ctrl+Shift+ПКМ снимает сопровождение у ВСЕХ её эскортировщиков,
            // наземка и авиация разом). Точки цепочек выше приоритетнее.
            // Авиа-эскорт — из живого сбора CUnits/CPlans (см. §5); наземный сбор
            // ниже перезаписывает CUnits — потому авиация идёт первой.
            SeenEscortTargets.Clear();
            for (int i = 0; i < CUnits.Count; i++)
            {
                if (!(CUnits[i] is Aircraft))
                    continue;
                for (int s = 0; s < CPlans[i].Count; s++)
                {
                    AircraftOrders.OrderSegment seg = CPlans[i][s];
                    if (seg.Mode != AirOrderMode.Escort || seg.Target == null || seg.Target.disabled)
                        continue;
                    if (!SeenEscortTargets.Add(seg.Target))
                        continue;
                    Vector3 tp = seg.Target.transform.position;
                    tp.y = GameApi.GroundY(tp.x, tp.z);
                    if (Near(cam, tp, mouse, px))
                        return new WaypointHit(WaypointKind.EscortLink, seg.Target, null, 0, 0);
                }
            }
            GroundOrders.CollectEscorts(CUnits, CTargets);
            SeenEscortTargets.Clear();
            for (int i = 0; i < CTargets.Count; i++)
            {
                Unit target = CTargets[i];
                if (target == null || target.disabled)
                    continue;
                if (!SeenEscortTargets.Add(target))
                    continue; // несколько эскортов одной цели — один маркер
                Vector3 tp = target.transform.position;
                tp.y = GameApi.GroundY(tp.x, tp.z);
                if (Near(cam, tp, mouse, px))
                    return new WaypointHit(WaypointKind.EscortLink, target, null, 0, 0);
            }

            return new WaypointHit(WaypointKind.None, null, null, 0, 0);
        }

        /// <summary>Ключ ралли — ЖИВАЯ фабрика (маркер такого ралли рисуется)?
        /// Зеркально CommanderHud.RallyAnchor: площадки-ключи (ралли недостроенных)
        /// и мёртвые фабрики не рисуются — сканером не отдаются.</summary>
        private static bool LiveRallyFactory(object factory)
        {
            switch (factory)
            {
                case VehicleDepot depot:
                    return depot != null && !depot.disabled;
                case Hangar hangar:
                    return hangar != null && !hangar.Disabled;
                case Building dock:
                    return BuildTasks.IsDock(dock);
                case Airbase airbase:
                    return airbase != null;
                default:
                    return false;
            }
        }

        /// <summary>Фабрика ралли — в ВЫДЕЛЕНИИ? Ангар выделяется своим зданием
        /// (attachedUnit), аэродром — отдельным SelectedAirbase; зеркало сбору
        /// фабрик выделения в MoveOrderService.TrySetRallyForFactorySelection.</summary>
        private static bool FactorySelected(CommanderModeController controller, object factory)
        {
            switch (factory)
            {
                case VehicleDepot depot:
                    return controller.Selection.Selected.Contains(depot);
                case Hangar hangar:
                    Unit hu = hangar != null && !hangar.Disabled ? hangar.attachedUnit : null;
                    return hu != null && controller.Selection.Selected.Contains(hu);
                case Building dock:
                    return BuildTasks.IsDock(dock) &&
                           controller.Selection.Selected.Contains(dock);
                case Airbase airbase:
                    return controller.Selection.SelectedAirbase == airbase;
                default:
                    return false;
            }
        }

        private static bool Near(Camera cam, Vector3 world, Vector2 mouse, float px)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            return sp.z > 0f && Vector2.Distance(new Vector2(sp.x, sp.y), mouse) < px;
        }
    }
}
