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
        EscortLink   // связь сопровождения (маркер — на ЦЕЛИ сопровождения)
    }

    /// <summary>ЕДИНЫЙ скан вейпоинтов под курсором (консолидация v0.48):
    /// раньше поиск существовал в двух расходящихся копиях — RouteEditor.TryGrab
    /// (захват для драга) и MoveOrderService.TryDeleteWaypoint (удаление). Теперь
    /// оба зовут этот скан; порядок приоритета один: площадки → движения очередей →
    /// цепочки наземки → планы авиации. allUnits = режим видимости (Shift).</summary>
    public readonly struct WaypointHit
    {
        public readonly WaypointKind Kind;
        public readonly Unit Owner;            // SequenceMove/GroundPoint/AirPoint
        public readonly BuildTasks.Task Task;  // BuildSite
        public readonly int Segment;           // AirPoint: индекс сегмента плана
        public readonly int Index;             // индекс точки в цепочке/очереди/сегменте

        public WaypointHit(WaypointKind kind, Unit owner, BuildTasks.Task task, int segment, int index)
        {
            Kind = kind;
            Owner = owner;
            Task = task;
            Segment = segment;
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
            var seqOwners = new List<Unit>(controller.Selection.Selected);
            if (allUnits)
                foreach (Unit eng in BuildTasks.UnitsWithSequences())
                    if (!seqOwners.Contains(eng))
                        seqOwners.Add(eng);
            foreach (Unit unit in seqOwners)
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

            // 3. Цепочки наземки/флота.
            var chainOwners = new List<Unit>(controller.Selection.Selected);
            if (allUnits)
            {
                var all = new List<Unit>();
                var qs = new List<IReadOnlyList<GlobalPosition>>();
                var ix = new List<int>();
                GroundOrders.CollectAll(all, qs, ix);
                for (int i = 0; i < all.Count; i++)
                    if (ix[i] < qs[i].Count && !chainOwners.Contains(all[i]))
                        chainOwners.Add(all[i]);
            }
            foreach (Unit unit in chainOwners)
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

            // 4. Планы авиации.
            var airOwners = new List<Unit>(controller.Selection.Selected);
            if (allUnits)
            {
                var allAir = new List<Unit>();
                var allPlans = new List<IReadOnlyList<AircraftOrders.OrderSegment>>();
                AircraftOrders.CollectAll(allAir, allPlans);
                foreach (Unit a in allAir)
                    if (!airOwners.Contains(a))
                        airOwners.Add(a);
            }
            foreach (Unit unit in airOwners)
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

            // 5. Связи сопровождения: маркер рисуется на ЦЕЛИ — там же и пикается
            // (Ctrl+Shift+ПКМ снимает сопровождение у ВСЕХ её эскортировщиков,
            // наземка и авиация разом). Точки цепочек выше приоритетнее.
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
            // Авиа-эскорт без наземных связей: те же цели, отдельное хранилище планов.
            AircraftOrders.CollectAll(CUnits, CPlans);
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

            return new WaypointHit(WaypointKind.None, null, null, 0, 0);
        }

        private static bool Near(Camera cam, Vector3 world, Vector2 mouse, float px)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            return sp.z > 0f && Vector2.Distance(new Vector2(sp.x, sp.y), mouse) < px;
        }
    }
}
