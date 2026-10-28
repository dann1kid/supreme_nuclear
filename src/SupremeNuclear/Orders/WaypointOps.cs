using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Фасад операций над вейпоинтами (консолидация v0.89): поиск «что под
    /// курсором» — только WaypointScanner.Scan, действия над найденным — только здесь,
    /// диспетчеризация по WaypointKind — в одном месте на операцию. Разделение ролей:
    /// RouteEditor — ввод (порог клик/драг, снап), MoveOrderService — диспетчеризация
    /// ПКМ; сюда — семантика вейпоинтов. Члены группы приказа — только через
    /// CollectGroup-примитивы сервисов (сигнатуры зеркальны у всех трёх хранилищ).
    /// Новый вид вейпоинта = WaypointKind в сканере + ветки здесь.</summary>
    internal static class WaypointOps
    {
        // Буферы группового фан-аута: операции идут только по событиям ввода,
        // не из OnGUI — переиспользование аллокаций безопасно.
        private static readonly List<Unit> GroupUnits = new List<Unit>();
        private static readonly List<int> GroupIdx = new List<int>();
        private static readonly List<int> GroupIdx2 = new List<int>();

        /// <summary>Удалить вейпоинт под курсором (Ctrl+Shift+ПКМ; allUnits — Shift,
        /// поиск по всем юнитам). Групповой вейпоинт (groupId≠0) удаляется у ВСЕХ
        /// членов группы приказа (выделять всю группу не нужно), индивидуальный —
        /// только у владельца. Площадка сносится вместе с недостроем, без возврата.</summary>
        public static bool DeleteAt(CommanderModeController controller, Camera cam,
            Vector2 mouse, bool allUnits, out WaypointHit hit)
        {
            hit = WaypointScanner.Scan(controller, cam, mouse, WaypointScanner.PickPx, allUnits);
            switch (hit.Kind)
            {
                case WaypointKind.BuildSite:
                    BuildTasks.Cancel(hit.Task);
                    if (BuildTasks.SelectedTask == hit.Task)
                        BuildTasks.SelectedTask = null;
                    return true;
                case WaypointKind.SequenceMove:
                {
                    int gid = BuildTasks.GroupIdOf(hit.Owner, hit.Index);
                    BuildTasks.RemoveSequenceMove(hit.Owner, hit.Index);
                    if (gid != 0)
                        foreach (Unit unit in BuildTasks.UnitsWithSequences())
                            if (unit != null && unit != hit.Owner)
                                BuildTasks.RemoveByGroupId(unit, gid);
                    return true;
                }
                case WaypointKind.GroundPoint:
                {
                    int gid = GroundOrders.GroupIdOf(hit.Owner, hit.Index);
                    GroundOrders.RemovePoint(hit.Owner, hit.Index);
                    if (gid != 0)
                    {
                        GroundOrders.CollectGroup(gid, GroupUnits, GroupIdx);
                        for (int m = 0; m < GroupUnits.Count; m++)
                            if (GroupUnits[m] != hit.Owner)
                                GroundOrders.RemoveByGroupId(GroupUnits[m], gid);
                    }
                    return true;
                }
                case WaypointKind.AirPoint:
                {
                    int gid = AircraftOrders.GroupIdOf(hit.Owner, hit.Segment, hit.Index);
                    AircraftOrders.RemovePoint(hit.Owner, hit.Segment, hit.Index);
                    if (gid != 0)
                    {
                        AircraftOrders.CollectGroup(gid, GroupUnits, GroupIdx, GroupIdx2);
                        for (int m = 0; m < GroupUnits.Count; m++)
                            if (GroupUnits[m] != hit.Owner)
                                AircraftOrders.RemoveByGroupId(GroupUnits[m], gid);
                    }
                    return true;
                }
                case WaypointKind.EscortLink:
                {
                    // Снять сопровождение цели у ВСЕХ эскортировщиков (наземка + авиация):
                    // связь — свойство цели, а не выделения.
                    int ground = GroundOrders.CancelEscortOf(hit.Owner);
                    int air = AircraftOrders.CancelEscortOf(hit.Owner);
                    if (ground + air > 0)
                        CommanderHud.Notice(Loc.F("Сопровождение снято: {0}", ground + air));
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>ПКМ по вейпоинту — превратить в патруль: сегмент плана борта
        /// становится патрульным циклом, цепочка наземки/флота (≥2 точки) замыкается
        /// в цикл. Поиск — единым сканом (земля-проекция, служебные точки не
        /// отдаются); гард «атака/залп/RTB не конвертятся» — внутри конвертаций,
        /// в одном месте. Конвертация пер-бортовая: у группы приказа сегменты
        /// асинхронны.</summary>
        public static bool ConvertToPatrolAt(CommanderModeController controller, Camera cam,
            Vector2 mouse, bool allUnits, out WaypointHit hit)
        {
            hit = WaypointScanner.Scan(controller, cam, mouse, WaypointScanner.PickPx, allUnits);
            if (hit.Kind == WaypointKind.AirPoint)
                return AircraftOrders.ConvertToPatrol(hit.Owner, hit.Segment);
            if (hit.Kind == WaypointKind.GroundPoint)
                return GroundOrders.ConvertToPatrol(hit.Owner);
            return false;
        }

        /// <summary>Переместить вейпоинт (одиночный драг/запись): dispatch по kind.
        /// Сюда же guard площадки «начатая не двигается» (Paid).</summary>
        public static void Move(WaypointHit hit, Vector3 world)
        {
            switch (hit.Kind)
            {
                case WaypointKind.BuildSite:
                    if (hit.Task != null && hit.Task.Paid <= 0f)
                        BuildTasks.MoveSite(hit.Task, world);
                    break;
                case WaypointKind.SequenceMove:
                    BuildTasks.SetSequenceMove(hit.Owner, hit.Index, world);
                    break;
                case WaypointKind.GroundPoint:
                    GroundOrders.SetQueuePoint(hit.Owner, hit.Index, world);
                    break;
                case WaypointKind.AirPoint:
                    AircraftOrders.SetPoint(hit.Owner, hit.Segment, hit.Index, world);
                    break;
            }
        }
    }
}
