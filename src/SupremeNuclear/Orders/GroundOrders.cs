using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Цепочки вейпоинтов для наземки/флота: SetDestination выдаёт следующую точку,
    /// когда юнит доехал до текущей (90 м). Все точки — GlobalPosition (origin-proof).</summary>
    public static class GroundOrders
    {
        private const float ArriveM = 90f;

        /// <summary>Растёт при любом изменении цепочек — инвалидирует меш-ленту.</summary>
        public static int Version;

        private static readonly Dictionary<Unit, List<GlobalPosition>> Queues =
            new Dictionary<Unit, List<GlobalPosition>>();

        private static readonly Dictionary<Unit, int> Indices = new Dictionary<Unit, int>();

        public static int Count => Queues.Count;

        public static void Set(Unit unit, GlobalPosition destination)
        {
            Version++;
            Queues[unit] = new List<GlobalPosition> { destination };
            Indices[unit] = 0;
            unit.GetComponent<UnitCommand>()?.SetDestination(destination, playerCommand: true);
        }

        public static void Enqueue(Unit unit, GlobalPosition destination)
        {
            Version++;
            if (!Queues.TryGetValue(unit, out List<GlobalPosition> queue))
            {
                Set(unit, destination);
                return;
            }
            queue.Add(destination);
        }

        public static void Clear(Unit unit)
        {
            Version++;
            Queues.Remove(unit);
            Indices.Remove(unit);
        }

        /// <summary>Полный сброс (смена сцены): цепочки умершей сцены недействительны.</summary>
        public static void Reset()
        {
            Version++;
            Queues.Clear();
            Indices.Clear();
        }

        public static void Tick()
        {
            List<Unit> done = null;
            foreach (KeyValuePair<Unit, List<GlobalPosition>> kv in Queues)
            {
                Unit unit = kv.Key;
                if (unit == null || unit.disabled)
                {
                    (done ??= new List<Unit>()).Add(unit);
                    continue;
                }
                int index = Indices.TryGetValue(unit, out int i) ? i : 0;
                if (index >= kv.Value.Count)
                    continue;

                Vector3 target = kv.Value[index].ToLocalPosition();
                Vector3 pos = unit.transform.position;
                float dist = new Vector2(pos.x - target.x, pos.z - target.z).magnitude;
                if (dist > ArriveM)
                    continue;

                index++;
                Indices[unit] = index;
                if (index < kv.Value.Count)
                    unit.GetComponent<UnitCommand>()?.SetDestination(kv.Value[index], playerCommand: true);
                // цепочка пройдена: очередь ОСТАЁТСЯ (для отрисовки), тик её больше не трогает
            }
            if (done == null)
                return;
            foreach (Unit unit in done)
            {
                Queues.Remove(unit);
                Indices.Remove(unit);
            }
        }

        /// <summary>Удалить точку цепочки (Ctrl+Shift+ПКМ). Если удалена текущая —
        /// юнит сразу получает следующую; хвост позади текущей сдвигается.</summary>
        public static void RemovePoint(Unit unit, int index)
        {
            Version++;
            if (!Queues.TryGetValue(unit, out List<GlobalPosition> queue) ||
                index < 0 || index >= queue.Count)
                return;
            int cur = Indices.TryGetValue(unit, out int i) ? i : 0;
            queue.RemoveAt(index);
            if (index < cur)
                Indices[unit] = cur - 1;
            else if (index == cur)
            {
                Indices[unit] = cur;
                if (cur < queue.Count)
                    unit.GetComponent<UnitCommand>()?.SetDestination(queue[cur], playerCommand: true);
            }
        }

        /// <summary>Перетаскивание точки наземной цепочки.</summary>
        public static void SetQueuePoint(Unit unit, int index, Vector3 groundPoint)
        {
            Version++;
            if (!Queues.TryGetValue(unit, out List<GlobalPosition> queue))
                return;
            if (index < 0 || index >= queue.Count)
                return;
            float y = GameApi.GroundY(groundPoint.x, groundPoint.z);
            queue[index] = new Vector3(groundPoint.x, y, groundPoint.z).ToGlobalPosition();
            int cur = Indices.TryGetValue(unit, out int i) ? i : 0;
            if (index == cur)
                unit.GetComponent<UnitCommand>()?.SetDestination(queue[index], playerCommand: true);
        }

        public static void CollectAll(List<Unit> units, List<IReadOnlyList<GlobalPosition>> queues, List<int> indices)
        {
            units.Clear();
            queues.Clear();
            indices.Clear();
            foreach (KeyValuePair<Unit, List<GlobalPosition>> kv in Queues)
            {
                if (kv.Key == null || kv.Key.disabled || kv.Value.Count == 0)
                    continue;
                units.Add(kv.Key);
                queues.Add(kv.Value);
                indices.Add(Indices.TryGetValue(kv.Key, out int i) ? i : 0);
            }
        }

        public static bool TryGetQueue(Unit unit, out IReadOnlyList<GlobalPosition> points, out int index)
        {
            points = null;
            index = 0;
            if (!Queues.TryGetValue(unit, out List<GlobalPosition> queue) || queue.Count == 0)
                return false;
            points = queue;
            index = Indices.TryGetValue(unit, out int i) ? i : 0;
            return true;
        }

        public static void Prune()
        {
            List<Unit> dead = null;
            foreach (Unit unit in Queues.Keys)
                if (unit == null || unit.disabled)
                    (dead ??= new List<Unit>()).Add(unit);
            if (dead == null)
                return;
            foreach (Unit unit in dead)
                Clear(unit);
        }
    }
}
