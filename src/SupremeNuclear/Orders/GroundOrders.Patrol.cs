using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть GroundOrders: патруль наземки/флота (циклы, конвертация, снимки, боевой тик).
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр цепочек и Tick — GroundOrders.cs.
    public static partial class GroundOrders
    {
        /// <summary>Патруль наземки/флота: цикл точек (последняя замыкается на первую).
        /// Перезаписывает текущий приказ (как любой новый приказ без Shift).</summary>
        public static void SetPatrol(Unit unit, GlobalPosition destination, int groupId = 0)
        {
            Version++;
            var ch = new Chain { Kind = GroundOrderKind.Patrol };
            ch.Points.Add(destination);
            ch.PointGroups.Add(groupId);
            ch.PointKinds.Add(GroundOrderKind.Patrol);
            Chains[unit] = ch;
            unit.GetComponent<UnitCommand>()?.SetDestination(destination, playerCommand: true);
        }

        /// <summary>Дописать точку в патрульный цикл (Shift); юнит не в патруле —
        /// патруль начинается с этой точки (смешать цикл с линейной очередью нельзя).</summary>
        public static void EnqueuePatrol(Unit unit, GlobalPosition destination, int groupId = 0)
        {
            if (Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Patrol)
            {
                Version++;
                ch.Points.Add(destination);
                ch.PointGroups.Add(groupId);
                ch.PointKinds.Add(GroundOrderKind.Patrol);
                return;
            }
            SetPatrol(unit, destination, groupId);
        }

        /// <summary>Можно ли замкнуть патрульный цикл с точки fromIndex: линейная
        /// цепочка (движение/атака-движение), точка не последняя (циклу нужно ≥2
        /// точек), в очереди нет ждущих целей серии И ждущих эскорт-связей
        /// (как Moho: конвертация только чисто-движительной очереди — вечный цикл
        /// никогда не дойдёт до стоящей за ним связи). Чистая — тестируется.</summary>
        public static bool CanClosePatrol(GroundOrderKind kind, int pointCount, int fromIndex,
            int waitingTargets, int queuedEscorts) =>
            (kind == GroundOrderKind.Move || kind == GroundOrderKind.AttackMove) &&
            fromIndex >= 0 && fromIndex < pointCount - 1 && waitingTargets == 0 &&
            queuedEscorts == 0;

        /// <summary>ПКМ по вейпоинту цепочки: ХВОСТ от точки fromIndex замыкается в
        /// патрульный цикл (канон SupCom/Moho RestartMoveCommandAsPatrol: конвертация
        /// «от пикнутой точки до конца», не вся цепочка). Точки до fromIndex
        /// проходятся линейно один раз; дальше цикл [fromIndex..end). Групповой
        /// вейпоинт конвертируется у всех членов группы приказа — каждый от СВОЕЙ
        /// точки этой группы (фан-аут в WaypointOps).</summary>
        public static bool ConvertToPatrol(Unit unit, int fromIndex)
        {
            if (!Chains.TryGetValue(unit, out Chain ch) ||
                !CanClosePatrol(ch.Kind, ch.Points.Count, fromIndex,
                    ch.TargetQueue.Count, ch.EscortQueue.Count))
                return false;
            Version++;
            ch.Kind = GroundOrderKind.Patrol;
            ch.PatrolLoopStart = fromIndex;
            // Точки хвоста СТАНОВЯТСЯ патрульными и по виду (KindOfPoint): иначе
            // IMGUI-фолбэк красил цикл цветами движения/атаки и менял формы маркеров.
            for (int i = fromIndex; i < ch.PointKinds.Count; i++)
                ch.PointKinds[i] = GroundOrderKind.Patrol;
            // Патруль дерётся как атака-движение: снять возможный стоп прошлой ноги.
            ch.Holding = false;
            if (ch.Index < ch.Points.Count)
                unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
            return true;
        }

        /// <summary>Можно ли замкнуть патруль с точки index цепочки юнита (подсказка
        /// hover в HUD: рисуем «ПКМ — патруль отсюда», только если действие реально).</summary>
        public static bool CanConvertPatrolAt(Unit unit, int index)
        {
            return Chains.TryGetValue(unit, out Chain ch) &&
                   index >= ch.Index && index < ch.Points.Count &&
                   CanClosePatrol(ch.Kind, ch.Points.Count, index,
                       ch.TargetQueue.Count, ch.EscortQueue.Count);
        }

        /// <summary>Снимок патрульной цепочки (авто-достройка снимает патруль на время
        /// стройки и возвращает после). true — юнит в живом патрульном цикле.</summary>
        public static bool TryGetPatrolSnapshot(Unit unit, List<GlobalPosition> points,
            List<int> groups, out int index, out int loopStart)
        {
            points.Clear();
            groups.Clear();
            index = 0;
            loopStart = 0;
            if (!Chains.TryGetValue(unit, out Chain ch) ||
                ch.Kind != GroundOrderKind.Patrol || ch.Points.Count == 0)
                return false;
            points.AddRange(ch.Points);
            groups.AddRange(ch.PointGroups);
            index = ch.Index;
            loopStart = ch.PatrolLoopStart;
            return true;
        }

        /// <summary>Вернуть снятый патруль (восстановление после авто-достройки):
        /// цикл ставится на сохранённую точку маршрута.</summary>
        public static void RestorePatrol(Unit unit, IReadOnlyList<GlobalPosition> points,
            IReadOnlyList<int> groups, int index, int loopStart = 0)
        {
            if (points == null || points.Count == 0)
                return;
            Version++;
            var ch = new Chain
            {
                Kind = GroundOrderKind.Patrol,
                Index = Mathf.Clamp(index, 0, points.Count - 1),
                PatrolLoopStart = Mathf.Clamp(loopStart, 0, points.Count - 1)
            };
            ch.Points.AddRange(points);
            if (groups != null)
                ch.PointGroups.AddRange(groups);
            for (int i = 0; i < points.Count; i++)
                ch.PointKinds.Add(GroundOrderKind.Patrol);
            Chains[unit] = ch;
            unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
        }

        /// <summary>Юниты в живом патрульном цикле (скан авто-достройки).</summary>
        public static void CollectPatrols(List<Unit> units)
        {
            units.Clear();
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
                if (kv.Key != null && !kv.Key.disabled &&
                    kv.Value.Kind == GroundOrderKind.Patrol && kv.Value.Points.Count > 0)
                    units.Add(kv.Key);
        }
        /// <summary>Бой патруля: скан (троттл 0.5 с); враг найден и погони нет —
        /// ОТРЫВ от маршрута: якорь поводка = текущая позиция, промоушен в атаку
        /// с ResumeKind=Patrol (по смерти цели/поводку TickPursuit вернёт цикл).
        /// true — погоня поднята (тик маршрута не продолжается).</summary>
        private static bool TickPatrolCombat(Unit unit, Chain ch, float now)
        {
            if (now < ch.NextScanAt)
                return false;
            ch.NextScanAt = now + ScanIntervalSec;
            Unit enemy = NearestEngageableEnemy(unit);
            if (enemy == null)
                return false;
            Version++;
            ch.ResumeKind = GroundOrderKind.Patrol;
            ch.PatrolLeashPoint = unit.transform.position.ToGlobalPosition();
            ch.PatrolLeashSet = true;
            ch.Kind = GroundOrderKind.Attack;
            ch.Target = enemy;
            ch.Holding = false;
            ch.LastPoint = default;
            IssuePursuit(unit, ch, PursueRange(unit, enemy), force: true);
            return true;
        }
    }
}
