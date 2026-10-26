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
        AirPoint     // точка плана авиации
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
        /// <summary>Ближайший вейпоинт в радиусе px от точки экрана.
        /// Свои — по выделению; allUnits расширяет на всех (режим видимости).</summary>
        public static WaypointHit Scan(CommanderModeController controller, Camera cam,
            Vector2 mouse, float px, bool allUnits)
        {
            // 1. Площадки стройки: свои инженеры в выделении (или все при allUnits).
            int ti = 0;
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                bool mine = allUnits;
                if (!mine)
                    foreach (Unit u in controller.Selection.Selected)
                        if (t.Builders.Contains(u))
                        {
                            mine = true;
                            break;
                        }
                if (mine && Near(cam, t.Site.ToLocalPosition(), mouse, px))
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
                if (unit == null || !GroundOrders.TryGetQueue(unit, out var queue, out int qi))
                    continue;
                for (int p = qi; p < queue.Count; p++)
                    if (Near(cam, queue[p].ToLocalPosition(), mouse, px))
                        return new WaypointHit(WaypointKind.GroundPoint, unit, null, 0, p);
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
                        if (Near(cam, plan[s].Points[p].ToLocalPosition(), mouse, px))
                            return new WaypointHit(WaypointKind.AirPoint, unit, null, s, p);
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
