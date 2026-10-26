using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    public enum AirOrderMode
    {
        Transit,
        AttackMove,
        Patrol,
        Attack
    }

    /// <summary>
    /// Планы полёта — очередь сегментов-приказов с ПРОФИЛЕМ ВЫСОТЫ на сегмент:
    /// Low = 150 м AGL с огибанием рельефа (скрытный подлёт), Med = 800 м, High = 2500 м.
    /// Точки хранят AGL-высоту в y; мировая высота считается на лету (живое следование рельефу).
    /// </summary>
    public static class AircraftOrders
    {
        public sealed class FlightProfile
        {
            public readonly float AltitudeAgl;
            public readonly bool FollowTerrain;
            public readonly string Name;

            public FlightProfile(float agl, bool follow, string name)
            {
                AltitudeAgl = agl;
                FollowTerrain = follow;
                Name = name;
            }
        }

        public static readonly FlightProfile[] Profiles =
        {
            new FlightProfile(150f, true, "Низко"),
            new FlightProfile(Settings.AircraftOrderAltitude, false, "Средне"),
            new FlightProfile(2500f, false, "Высоко")
        };

        /// <summary>Профиль для НОВЫХ сегментов (Alt+1/2/3 или кнопки).</summary>
        public static int ProfileIndex = 1;

        /// <summary>Растёт при любом изменении приказов.</summary>
        public static int Version;

        public sealed class OrderSegment
        {
            public AirOrderMode Mode;
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            public int Index;
            public Unit Target;
            public float AltAgl;
            public bool FollowTerrain;
        }

        private static readonly Dictionary<Unit, List<OrderSegment>> Plans =
            new Dictionary<Unit, List<OrderSegment>>();

        public static int Count => Plans.Count;

        private static void AppendPoint(OrderSegment segment, Vector3 groundPoint)
        {
            // y хранит AGL — мировая высота вычисляется при рулении (следование рельефу).
            segment.Points.Add(new Vector3(groundPoint.x, segment.AltAgl, groundPoint.z).ToGlobalPosition());
        }

        private static OrderSegment MakeSegment(AirOrderMode mode, Vector3 groundPoint)
        {
            var seg = new OrderSegment
            {
                Mode = mode,
                AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                FollowTerrain = Profiles[ProfileIndex].FollowTerrain
            };
            AppendPoint(seg, groundPoint);
            return seg;
        }

        public static void Set(Unit aircraft, AirOrderMode mode, Vector3 groundPoint)
        {
            Version++;
            if (mode == AirOrderMode.Patrol)
            {
                SetPatrol(aircraft, groundPoint);
                return;
            }
            Plans[aircraft] = new List<OrderSegment> { MakeSegment(mode, groundPoint) };
        }

        public static void Enqueue(Unit aircraft, AirOrderMode mode, Vector3 groundPoint)
        {
            Version++;
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                plan = Plans[aircraft] = new List<OrderSegment>();

            if (mode == AirOrderMode.Patrol)
            {
                for (int i = plan.Count - 1; i >= 0; i--)
                {
                    if (plan[i].Mode == AirOrderMode.Patrol)
                    {
                        AppendPoint(plan[i], groundPoint);
                        return;
                    }
                }
                var seg = new OrderSegment
                {
                    Mode = AirOrderMode.Patrol,
                    AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                    FollowTerrain = Profiles[ProfileIndex].FollowTerrain
                };
                seg.Points.Add(new Vector3(aircraft.transform.position.x, seg.AltAgl, aircraft.transform.position.z).ToGlobalPosition());
                AppendPoint(seg, groundPoint);
                plan.Add(seg);
                return;
            }

            OrderSegment last = plan.Count > 0 ? plan[plan.Count - 1] : null;
            if (last != null && last.Mode == mode && mode != AirOrderMode.Attack)
            {
                AppendPoint(last, groundPoint);
                return;
            }
            plan.Add(MakeSegment(mode, groundPoint));
        }

        public static void SetPatrol(Unit aircraft, Vector3 groundPoint)
        {
            Version++;
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Patrol,
                AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                FollowTerrain = Profiles[ProfileIndex].FollowTerrain
            };
            seg.Points.Add(new Vector3(aircraft.transform.position.x, seg.AltAgl, aircraft.transform.position.z).ToGlobalPosition());
            AppendPoint(seg, groundPoint);
            Plans[aircraft] = new List<OrderSegment> { seg };
        }

        public static void Attack(Unit aircraft, Unit target)
        {
            Version++;
            Plans[aircraft] = new List<OrderSegment>
            {
                new OrderSegment { Mode = AirOrderMode.Attack, Target = target }
            };
        }

        public static void Cancel(Unit aircraft)
        {
            Version++;
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Transit,
                AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                FollowTerrain = false
            };
            seg.Points.Add(new Vector3(aircraft.transform.position.x, seg.AltAgl, aircraft.transform.position.z).ToGlobalPosition());
            Plans[aircraft] = new List<OrderSegment> { seg };
        }

        /// <summary>Сегмент становится патрульным циклом (ПКМ по вейпоинту).</summary>
        public static void ConvertToPatrol(Unit aircraft, int segmentIndex)
        {
            Version++;
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                return;
            if (segmentIndex < 0 || segmentIndex >= plan.Count)
                return;
            OrderSegment seg = plan[segmentIndex];
            seg.Mode = AirOrderMode.Patrol;
            seg.Target = null;
            if (seg.Points.Count == 1)
                seg.Points.Insert(0, new Vector3(aircraft.transform.position.x, seg.AltAgl, aircraft.transform.position.z).ToGlobalPosition());
            seg.Index = 0;
        }

        /// <summary>Удалить точку плана (Ctrl+Shift+ПКМ). Пустые сегменты уберёт Step.</summary>
        public static void RemovePoint(Unit aircraft, int segmentIndex, int pointIndex)
        {
            Version++;
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan) ||
                segmentIndex < 0 || segmentIndex >= plan.Count)
                return;
            OrderSegment seg = plan[segmentIndex];
            if (pointIndex < 0 || pointIndex >= seg.Points.Count)
                return;
            seg.Points.RemoveAt(pointIndex);
            if (seg.Index > pointIndex)
                seg.Index--;
            if (seg.Mode != AirOrderMode.Attack && seg.Points.Count > 0)
                seg.Index = Mathf.Clamp(seg.Index, 0, seg.Points.Count - 1);
            if (seg.Points.Count == 0 && seg.Target == null)
                plan.RemoveAt(segmentIndex);
        }

        /// <summary>Перетаскивание точки плана (x/z; AGL сохраняется).</summary>
        public static void SetPoint(Unit aircraft, int segmentIndex, int pointIndex, Vector3 groundPoint)
        {
            Version++;
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                return;
            if (segmentIndex < 0 || segmentIndex >= plan.Count)
                return;
            OrderSegment seg = plan[segmentIndex];
            if (pointIndex < 0 || pointIndex >= seg.Points.Count)
                return;
            float agl = seg.Points[pointIndex].ToLocalPosition().y;
            seg.Points[pointIndex] = new Vector3(groundPoint.x, agl, groundPoint.z).ToGlobalPosition();
        }

        public static void Forget(Unit aircraft)
        {
            Version++;
            Plans.Remove(aircraft);
        }

        /// <summary>Полный сброс (смена сцены): планы умершей сцены недействительны.</summary>
        public static void Reset()
        {
            Version++;
            Plans.Clear();
        }

        public static OrderSegment Step(Unit aircraft, Vector3 currentPosition)
        {
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan) || plan.Count == 0)
                return null;
            while (plan.Count > 0)
            {
                OrderSegment seg = plan[0];
                if (seg.Mode == AirOrderMode.Attack)
                {
                    if (seg.Target != null && !seg.Target.disabled)
                        return seg;
                    plan.RemoveAt(0);
                    continue;
                }
                if (seg.Points.Count == 0)
                {
                    plan.RemoveAt(0);
                    continue;
                }
                if (seg.Mode == AirOrderMode.Patrol)
                    return seg;

                while (seg.Index < seg.Points.Count - 1 &&
                       HorizontalDistance(currentPosition, WorldOf(seg, seg.Index)) < 900f)
                {
                    seg.Index++;
                }
                if (seg.Index >= seg.Points.Count - 1 && plan.Count > 1 &&
                    HorizontalDistance(currentPosition, WorldOf(seg, seg.Points.Count - 1)) < 1500f)
                {
                    plan.RemoveAt(0);
                    continue;
                }
                return seg;
            }
            return null;
        }

        /// <summary>Мировая точка вейпоинта: x/z + GroundY + AGL (живое следование рельефу).</summary>
        private static Vector3 WorldOf(OrderSegment seg, int pointIndex)
        {
            Vector3 p = seg.Points[pointIndex].ToLocalPosition();
            return new Vector3(p.x, GameApi.GroundY(p.x, p.z) + p.y, p.z);
        }

        public static bool TryGetDestination(Unit aircraft, Vector3 currentPosition,
            out GlobalPosition destination, out AirOrderMode mode, out bool followTerrain)
        {
            destination = default;
            mode = AirOrderMode.Transit;
            followTerrain = false;
            OrderSegment seg = Step(aircraft, currentPosition);
            if (seg == null)
                return false;
            mode = seg.Mode;
            followTerrain = seg.FollowTerrain;

            if (seg.Mode == AirOrderMode.Patrol)
            {
                int guard = seg.Points.Count;
                while (guard-- > 0 && HorizontalDistance(currentPosition, WorldOf(seg, seg.Index)) < 900f)
                    seg.Index = (seg.Index + 1) % seg.Points.Count;
                Vector3 wp = WorldOf(seg, seg.Index);
                destination = wp.ToGlobalPosition();
                return true;
            }

            Vector3 center = WorldOf(seg, seg.Points.Count - 1);
            Vector2 d = new Vector2(currentPosition.x - center.x, currentPosition.z - center.z);
            if (seg.Index >= seg.Points.Count - 1 && d.magnitude < 2500f)
            {
                const float rot = 25f * Mathf.Deg2Rad;
                Vector2 nd = new Vector2(
                    d.x * Mathf.Cos(rot) - d.y * Mathf.Sin(rot),
                    d.x * Mathf.Sin(rot) + d.y * Mathf.Cos(rot)).normalized;
                Vector3 orbit = new Vector3(center.x + nd.x * 1500f, center.y, center.z + nd.y * 1500f);
                destination = orbit.ToGlobalPosition();
                return true;
            }

            destination = WorldOf(seg, seg.Index).ToGlobalPosition();
            return true;
        }

        public static bool TryGetPlan(Unit aircraft, out IReadOnlyList<OrderSegment> plan)
        {
            plan = null;
            return Plans.TryGetValue(aircraft, out List<OrderSegment> p) && p.Count > 0 && (plan = p) != null;
        }

        public static void CollectAll(List<Unit> units, List<IReadOnlyList<OrderSegment>> plans)
        {
            units.Clear();
            plans.Clear();
            foreach (KeyValuePair<Unit, List<OrderSegment>> kv in Plans)
            {
                if (kv.Key == null || kv.Key.disabled || kv.Value.Count == 0)
                    continue;
                units.Add(kv.Key);
                plans.Add(kv.Value);
            }
        }

        public static bool EnemyNearby(Unit aircraft, FactionHQ localHq, float range = 9000f)
        {
            Vector3 pos = aircraft.transform.position;
            float sq = range * range;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ == localHq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft))
                    continue;
                Vector3 d = unit.transform.position - pos;
                if (d.sqrMagnitude < sq)
                    return true;
            }
            return false;
        }

        public static void Prune()
        {
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, List<OrderSegment>> kv in Plans)
            {
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            }
            if (dead == null)
                return;
            foreach (Unit unit in dead)
                Plans.Remove(unit);
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b) =>
            new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }
}
