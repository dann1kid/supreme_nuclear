using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    public enum AirOrderMode
    {
        Transit,
        AttackMove,
        Patrol,
        Attack,
        /// <summary>Залп (DESIGN §2.11): подлёт к цели/якорю и залповый пуск выбранных
        /// ракетных станций по нескольким целям (SalvoService, распределение — нативное
        /// CombatAI.LookForMissileTargets). Сегмент живёт до отстрела или смерти целей.</summary>
        Salvo,
        /// <summary>Возврат на базу: вставляет SortieService при «сухих» станциях или
        /// низком топливе; живёт до конца обслуживания и взлёта (снимает SortieService),
        /// затем вылет возобновляется по следующему сегменту.</summary>
        Rtb
    }

    /// <summary>Скоростной режим участка маршрута (штампуется на сегмент, как профиль
    /// высоты). Полный = 1.0; Крейсер = бортовой cruiseThrottle; Эконом — настройка.
    /// Применяется только самолётами: у вертолётов газ считает hover-контур игры.</summary>
    public enum SpeedMode
    {
        Full,
        Cruise,
        Economy
    }

    /// <summary>
    /// Планы полёта — очередь сегментов-приказов с ПРОФИЛЕМ ВЫСОТЫ И СКОРОСТЬЮ на
    /// сегмент: VeryLow = 75 м AGL с огибанием рельефа, Low = 150 м (тоже огибание),
    /// Med = 800 м, High = 2500 м. Режим на момент клика штампуется на участок;
    /// смена режима посреди Shift-серии режет новую ногу маршрута. Точки хранят
    /// AGL-высоту в y; мировая высота считается на лету (живое следование рельефу).
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
            new FlightProfile(Settings.AircraftAltVeryLowAgl, true, "Сверхнизко"),
            new FlightProfile(Settings.AircraftAltLowAgl, true, "Низко"),
            new FlightProfile(Settings.AircraftOrderAltitude, false, "Средне"),
            new FlightProfile(Settings.AircraftAltHighAgl, false, "Высоко")
        };

        /// <summary>Профиль для НОВЫХ сегментов (Alt+1..4 или кнопки); «Средне».</summary>
        public static int ProfileIndex = 2;

        /// <summary>Скоростной режим для НОВЫХ сегментов (Shift+1..3 или кнопки);
        /// «Полный» — прежнее поведение руления.</summary>
        public static SpeedMode SpeedIndex = SpeedMode.Full;

        /// <summary>Тротл скоростного режима (чистая функция — тестируется).</summary>
        public static float ThrottleOf(float cruiseThrottle, SpeedMode mode)
        {
            switch (mode)
            {
                case SpeedMode.Cruise:
                    return cruiseThrottle > 0.05f ? cruiseThrottle : 0.9f;
                case SpeedMode.Economy:
                    return Settings.AircraftEconomyThrottle;
                default:
                    return 1f;
            }
        }

        /// <summary>Тротл скоростного режима для конкретного борта (крейсер — свой
        /// у каждого типа). Вертолётам не применяется (газ ведёт hover-контур игры).</summary>
        public static float ThrottleOf(Aircraft aircraft, SpeedMode mode)
        {
            AircraftParameters p = aircraft != null && aircraft.definition != null
                ? aircraft.definition.aircraftParameters
                : null;
            return ThrottleOf(p != null ? p.cruiseThrottle : 0f, mode);
        }

        /// <summary>Растёт при любом изменении приказов.</summary>
        public static int Version;

        public sealed class OrderSegment
        {
            public AirOrderMode Mode;
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            /// <summary>Группа приказа для КАЖДОЙ точки (параллельно Points): один клик
            /// группе бортов = один groupId — групповой драг/удаление вейпоинта идут
            /// по нему, а не по индексам (планы бортов асинхронны). 0 — индивидуальная
            /// (стартовая точка, стартовая точка патруля).</summary>
            public readonly List<int> PointGroups = new List<int>();
            public int Index;
            public Unit Target;
            public float AltAgl;
            public bool FollowTerrain;
            /// <summary>Скоростной режим сегмента (штамп на участок; вертолётами игнорируется).</summary>
            public SpeedMode Speed = SpeedMode.Full;
            /// <summary>Залп: явный список целей (Shift+ПКМ по целям — «разом по нескольким»);
            /// Target — первичная цель, SalvoTargets — включая её.</summary>
            public List<Unit> SalvoTargets;
            /// <summary>Залп отстрелян — сегмент завершён (снимет Step).</summary>
            public bool SalvoFired;
            /// <summary>Залп: индексы станций, уже пустивших ракеты в этом залпе.</summary>
            public List<int> SalvoStationsFired;
            /// <summary>RTB: передан нативной посадке (однократно, у аэродрома).</summary>
            public bool RtbHandedOff;
            /// <summary>RTB: сел (на земле у аэродрома — идёт обслуживание).</summary>
            public bool RtbLanded;
            /// <summary>RTB: секунды на земле у своего аэродрома до конца сервиса.</summary>
            public float ServiceT;
            /// <summary>RTB: перевооружение/заправка выполнены.</summary>
            public bool Serviced;
            /// <summary>RTB: аэродром назначения (резерв при терминальном возврате).</summary>
            public Airbase Base;
        }

        private static readonly Dictionary<Unit, List<OrderSegment>> Plans =
            new Dictionary<Unit, List<OrderSegment>>();

        public static int Count => Plans.Count;

        /// <summary>Стартовая точка сегмента (позиция борта, AGL сегмента) — группа 0:
        /// она принадлежит борту, а не приказу группы.</summary>
        private static GlobalPosition StartPoint(OrderSegment segment, Unit aircraft) =>
            new Vector3(aircraft.transform.position.x, segment.AltAgl, aircraft.transform.position.z)
                .ToGlobalPosition();

        private static void AppendPoint(OrderSegment segment, Vector3 groundPoint, int groupId = 0)
        {
            // y хранит AGL — мировая высота вычисляется при рулении (следование рельефу).
            segment.Points.Add(new Vector3(groundPoint.x, segment.AltAgl, groundPoint.z).ToGlobalPosition());
            segment.PointGroups.Add(groupId);
        }

        private static OrderSegment MakeSegment(AirOrderMode mode, Vector3 groundPoint, int groupId = 0)
        {
            var seg = new OrderSegment
            {
                Mode = mode,
                AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                FollowTerrain = Profiles[ProfileIndex].FollowTerrain,
                Speed = SpeedIndex
            };
            AppendPoint(seg, groundPoint, groupId);
            return seg;
        }

        /// <summary>Можно ли дописать точку в конец последнего сегмента (та же нога
        /// маршрута): режим приказа совпадает, не атака/залп, профиль высоты и скорость
        /// те же. Чистая функция — тестируется.</summary>
        public static bool CanAppend(OrderSegment last, AirOrderMode mode,
            FlightProfile profile, SpeedMode speed) =>
            last != null && last.Mode == mode &&
            mode != AirOrderMode.Attack && mode != AirOrderMode.Salvo &&
            Mathf.Approximately(last.AltAgl, profile.AltitudeAgl) &&
            last.FollowTerrain == profile.FollowTerrain &&
            last.Speed == speed;

        public static void Set(Unit aircraft, AirOrderMode mode, Vector3 groundPoint, int groupId = 0)
        {
            Version++;
            SortieService.Launch(aircraft); // стоящий на базе борт — выкат под новый приказ
            if (mode == AirOrderMode.Patrol)
            {
                SetPatrol(aircraft, groundPoint, groupId);
                return;
            }
            Plans[aircraft] = new List<OrderSegment> { MakeSegment(mode, groundPoint, groupId) };
        }

        public static void Enqueue(Unit aircraft, AirOrderMode mode, Vector3 groundPoint, int groupId = 0)
        {
            Version++;
            SortieService.Launch(aircraft); // стоящий на базе борт — выкат под новый приказ
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                plan = Plans[aircraft] = new List<OrderSegment>();

            // Терминальный RTB (команда «на базу», план из одного сегмента) при постановке
            // нового приказа в очередь снимается — борт выполняет приказ сразу, без
            // круга «долететь домой и обратно». Авто-RTB вылета ([Rtb, Attack...]) не трогаем.
            if (plan.Count == 1 && plan[0].Mode == AirOrderMode.Rtb)
                plan.RemoveAt(0);

            if (mode == AirOrderMode.Patrol)
            {
                for (int i = plan.Count - 1; i >= 0; i--)
                {
                    if (plan[i].Mode == AirOrderMode.Patrol && SameFlightModes(plan[i]))
                    {
                        AppendPoint(plan[i], groundPoint, groupId);
                        return;
                    }
                }
                var seg = new OrderSegment
                {
                    Mode = AirOrderMode.Patrol,
                    AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                    FollowTerrain = Profiles[ProfileIndex].FollowTerrain,
                    Speed = SpeedIndex
                };
                seg.Points.Add(StartPoint(seg, aircraft));
                seg.PointGroups.Add(0);
                AppendPoint(seg, groundPoint, groupId);
                plan.Add(seg);
                return;
            }

            OrderSegment last = plan.Count > 0 ? plan[plan.Count - 1] : null;
            if (CanAppend(last, mode, Profiles[ProfileIndex], SpeedIndex))
            {
                AppendPoint(last, groundPoint, groupId);
                return;
            }
            plan.Add(MakeSegment(mode, groundPoint, groupId));
        }

        /// <summary>Профиль/скорость сегмента совпадают с текущими (слияание ног маршрута).</summary>
        private static bool SameFlightModes(OrderSegment seg) =>
            Mathf.Approximately(seg.AltAgl, Profiles[ProfileIndex].AltitudeAgl) &&
            seg.FollowTerrain == Profiles[ProfileIndex].FollowTerrain &&
            seg.Speed == SpeedIndex;

        public static void SetPatrol(Unit aircraft, Vector3 groundPoint, int groupId = 0)
        {
            Version++;
            SortieService.Launch(aircraft); // стоящий на базе борт — выкат под новый приказ
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Patrol,
                AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                FollowTerrain = Profiles[ProfileIndex].FollowTerrain,
                Speed = SpeedIndex
            };
            seg.Points.Add(StartPoint(seg, aircraft));
            seg.PointGroups.Add(0);
            AppendPoint(seg, groundPoint, groupId);
            Plans[aircraft] = new List<OrderSegment> { seg };
        }

        /// <summary>Приказ атаки (DESIGN §2.11): цель пиннится (AircraftTargetPinPatch),
        /// якорь — точка клика, к ней борт летит, пока даталинк не держит цель.</summary>
        public static void Attack(Unit aircraft, Unit target, Vector3 anchorGround)
        {
            Version++;
            SortieService.Launch(aircraft); // стоящий на базе борт — выкат под новый приказ
            Plans[aircraft] = new List<OrderSegment> { MakeAttackSegment(target, anchorGround) };
        }

        /// <summary>Shift-клик: атака дописывается в план (серия целей по порядку).</summary>
        public static void EnqueueAttack(Unit aircraft, Unit target, Vector3 anchorGround)
        {
            Version++;
            SortieService.Launch(aircraft); // стоящий на базе борт — выкат под новый приказ
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
            {
                Attack(aircraft, target, anchorGround);
                return;
            }
            plan.Add(MakeAttackSegment(target, anchorGround));
        }

        /// <summary>Команда «на базу» (G): терминальный RTB — посадка, обслуживание
        /// и паркинг до следующего приказа (SortieService).</summary>
        public static void ReturnToBase(Unit aircraft, Airbase airbase)
        {
            Version++;
            Plans[aircraft] = new List<OrderSegment> { MakeRtb(aircraft, airbase) };
        }

        private static OrderSegment MakeAttackSegment(Unit target, Vector3 anchorGround)
        {
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Attack,
                Target = target,
                AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                FollowTerrain = Profiles[ProfileIndex].FollowTerrain,
                Speed = SpeedIndex
            };
            // Points[0] — якорь подлёта (AGR-высота профиля сегмента)
            seg.Points.Add(new Vector3(anchorGround.x, seg.AltAgl, anchorGround.z).ToGlobalPosition());
            seg.PointGroups.Add(0);
            return seg;
        }

        /// <summary>Залп-сегмент: явная цель (или null — залп по площади у якоря),
        /// якорь — точка клика. Отстрел — SalvoService (хост).</summary>
        private static OrderSegment MakeSalvoSegment(Unit target, Vector3 anchorGround)
        {
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Salvo,
                Target = target,
                AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                FollowTerrain = Profiles[ProfileIndex].FollowTerrain,
                Speed = SpeedIndex,
                SalvoTargets = target != null ? new List<Unit> { target } : new List<Unit>(),
                SalvoStationsFired = new List<int>()
            };
            seg.Points.Add(new Vector3(anchorGround.x, seg.AltAgl, anchorGround.z).ToGlobalPosition());
            seg.PointGroups.Add(0);
            return seg;
        }

        /// <summary>Приказ залпа по цели (DESIGN §2.11): подлёт и залповый пуск выбранных
        /// станций; распределение по соседним целям — нативное.</summary>
        public static void Salvo(Unit aircraft, Unit target, Vector3 anchorGround)
        {
            Version++;
            SortieService.Launch(aircraft);
            Plans[aircraft] = new List<OrderSegment> { MakeSalvoSegment(target, anchorGround) };
        }

        /// <summary>Залп по площади: ПКМ по земле в режиме «Залп» — при подлёте к якорю
        /// борт выпускает всё выбранное по целям в даталинке рядом с якорем.
        /// Без Shift — новый план (replace), Shift — дописать в очередь.</summary>
        public static void SalvoArea(Unit aircraft, Vector3 anchorGround, int groupId = 0, bool replace = true)
        {
            Version++;
            SortieService.Launch(aircraft);
            var seg = MakeSalvoSegment(null, anchorGround);
            seg.PointGroups[0] = groupId; // якорь залпа — групповой вейпоинт приказа
            if (replace || !Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                Plans[aircraft] = new List<OrderSegment> { seg };
            else
                plan.Add(seg);
        }

        /// <summary>Shift+ПКМ по цели в режиме «Залп»: дописать цель в ЗАЛП (одновременный
        /// пуск по нескольким), а не в очередь. Нет живого залп-сегмента в голове — новый.</summary>
        public static void EnqueueSalvoTarget(Unit aircraft, Unit target, Vector3 anchorGround)
        {
            Version++;
            SortieService.Launch(aircraft);
            if (Plans.TryGetValue(aircraft, out List<OrderSegment> plan) && plan.Count > 0)
            {
                OrderSegment head = plan[0];
                if (head.Mode == AirOrderMode.Salvo && !head.SalvoFired &&
                    head.SalvoTargets != null && !head.SalvoTargets.Contains(target))
                {
                    head.SalvoTargets.Add(target);
                    return;
                }
            }
            Salvo(aircraft, target, anchorGround);
        }

        /// <summary>RTB-сегмент к своему аэродрому (создаёт SortieService). Домой — крейсером.</summary>
        public static OrderSegment MakeRtb(Unit aircraft, Airbase airbase)
        {
            Version++;
            Vector3 c = airbase.center.position;
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Rtb,
                AltAgl = 600f,
                FollowTerrain = false,
                Speed = SpeedMode.Cruise,
                Base = airbase
            };
            seg.Points.Add(new Vector3(c.x, seg.AltAgl, c.z).ToGlobalPosition());
            seg.PointGroups.Add(0);
            return seg;
        }

        public static void Cancel(Unit aircraft)
        {
            Version++;
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Transit,
                AltAgl = Profiles[ProfileIndex].AltitudeAgl,
                FollowTerrain = false,
                Speed = SpeedIndex
            };
            seg.Points.Add(StartPoint(seg, aircraft));
            seg.PointGroups.Add(0);
            Plans[aircraft] = new List<OrderSegment> { seg };
        }

        /// <summary>Смена профиля высоты/скорости «на лету»: применяется к ГОЛОВНОМУ
        /// сегменту борта немедленно (и остаётся штампом для новых). RTB не трогаем —
        /// домой летим на его собственной высоте/крейсере.</summary>
        public static bool ApplyModesToCurrent(Unit aircraft)
        {
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan) || plan.Count == 0)
                return false;
            OrderSegment seg = plan[0];
            if (seg.Mode == AirOrderMode.Rtb)
                return false;
            bool changed = !Mathf.Approximately(seg.AltAgl, Profiles[ProfileIndex].AltitudeAgl) ||
                           seg.FollowTerrain != Profiles[ProfileIndex].FollowTerrain ||
                           seg.Speed != SpeedIndex;
            if (!changed)
                return false;
            Version++;
            seg.AltAgl = Profiles[ProfileIndex].AltitudeAgl;
            seg.FollowTerrain = Profiles[ProfileIndex].FollowTerrain;
            seg.Speed = SpeedIndex;
            // Точки хранят AGL в y — переписываем под новый профиль (x/z не меняются).
            for (int i = 0; i < seg.Points.Count; i++)
            {
                Vector3 l = seg.Points[i].ToLocalPosition();
                seg.Points[i] = new Vector3(l.x, seg.AltAgl, l.z).ToGlobalPosition();
            }
            return true;
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
            if (seg.Mode == AirOrderMode.Attack || seg.Mode == AirOrderMode.Salvo ||
                seg.Mode == AirOrderMode.Rtb)
                return; // атаку/залп/возврат на базу в патруль не превращаем
            seg.Mode = AirOrderMode.Patrol;
            seg.Target = null;
            if (seg.Points.Count == 1)
            {
                seg.Points.Insert(0, StartPoint(seg, aircraft));
                seg.PointGroups.Insert(0, 0);
            }
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
            seg.PointGroups.RemoveAt(pointIndex);
            if (seg.Index > pointIndex)
                seg.Index--;
            if (seg.Mode != AirOrderMode.Attack && seg.Mode != AirOrderMode.Salvo && seg.Points.Count > 0)
                seg.Index = Mathf.Clamp(seg.Index, 0, seg.Points.Count - 1);
            if (seg.Points.Count == 0 && seg.Target == null && HasNoSalvoTargets(seg))
                plan.RemoveAt(segmentIndex);
        }

        /// <summary>У залп-сегмента нет явных целей (залп по площади).</summary>
        private static bool HasNoSalvoTargets(OrderSegment seg) =>
            seg.SalvoTargets == null || seg.SalvoTargets.Count == 0;

        /// <summary>Группа приказа точки (0 — индивидуальная).</summary>
        public static int GroupIdOf(Unit aircraft, int segmentIndex, int pointIndex)
        {
            return Plans.TryGetValue(aircraft, out List<OrderSegment> plan) &&
                   segmentIndex >= 0 && segmentIndex < plan.Count &&
                   pointIndex >= 0 && pointIndex < plan[segmentIndex].PointGroups.Count
                ? plan[segmentIndex].PointGroups[pointIndex]
                : 0;
        }

        /// <summary>Найти точку группы приказа в плане (у каждого борта свои индексы) —
        /// чистая функция для группового драга/удаления.</summary>
        public static bool TryGetPointByGroupId(IReadOnlyList<OrderSegment> plan, int groupId,
            out int segmentIndex, out int pointIndex)
        {
            for (int s = 0; s < plan.Count; s++)
            {
                OrderSegment seg = plan[s];
                for (int p = 0; p < seg.PointGroups.Count; p++)
                    if (seg.PointGroups[p] == groupId)
                    {
                        segmentIndex = s;
                        pointIndex = p;
                        return true;
                    }
            }
            segmentIndex = -1;
            pointIndex = -1;
            return false;
        }

        /// <summary>Удалить из плана все точки группы приказа; опустевшие сегменты —
        /// убрать (чистая функция — тестируется).</summary>
        public static int RemovePointsWithGroup(List<OrderSegment> plan, int groupId)
        {
            int removed = 0;
            for (int s = plan.Count - 1; s >= 0; s--)
            {
                OrderSegment seg = plan[s];
                for (int p = seg.PointGroups.Count - 1; p >= 0; p--)
                    if (seg.PointGroups[p] == groupId)
                    {
                        seg.Points.RemoveAt(p);
                        seg.PointGroups.RemoveAt(p);
                        if (seg.Index > p)
                            seg.Index--;
                        removed++;
                    }
                if (seg.Points.Count == 0 && seg.Target == null)
                    plan.RemoveAt(s);
                else if (seg.Points.Count > 0)
                    seg.Index = Mathf.Clamp(seg.Index, 0, seg.Points.Count - 1);
            }
            return removed;
        }

        /// <summary>Удалить точку группового приказа у борта (групповое удаление —
        /// по groupId у всей выделенной группы, зеркально инженерам/наземке).</summary>
        public static void RemoveByGroupId(Unit aircraft, int groupId)
        {
            if (groupId == 0)
                return;
            Version++;
            if (Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                RemovePointsWithGroup(plan, groupId);
        }

        /// <summary>Disperse Move: первая оставшаяся точка группы в ТРАНСПОРТНЫХ
        /// сегментах плана (Transit/AttackMove; в головном — от Index, атака/залп/
        /// патруль/RTB не участвуют). Чистая — тестируется.</summary>
        public static bool TryDispersePoint(IReadOnlyList<OrderSegment> plan, int groupId,
            out int segmentIndex, out int pointIndex)
        {
            for (int s = 0; s < plan.Count; s++)
            {
                OrderSegment seg = plan[s];
                if (seg.Mode != AirOrderMode.Transit && seg.Mode != AirOrderMode.AttackMove)
                    continue;
                int from = s == 0 ? seg.Index : 0; // головной сегмент частично пройден
                for (int p = from; p < seg.PointGroups.Count; p++)
                    if (seg.PointGroups[p] == groupId)
                    {
                        segmentIndex = s;
                        pointIndex = p;
                        return true;
                    }
            }
            segmentIndex = -1;
            pointIndex = -1;
            return false;
        }

        /// <summary>Disperse Move (Alt+Shift+R): план борта := один сегмент прямого
        /// вектора к точке группы groupId (группы нет — к последней оставшейся
        /// транспортной точке); профиль/скорость — исходного сегмента, остальной
        /// план (включая очередь атак/RTB) заменяется. false — транспортных точек нет.</summary>
        public static bool DisperseKeep(Unit aircraft, int groupId)
        {
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                return false;
            if (!TryDispersePoint(plan, groupId, out int s, out int p))
            {
                s = -1;
                for (int i = plan.Count - 1; i >= 0 && s < 0; i--)
                {
                    OrderSegment seg = plan[i];
                    if (seg.Mode != AirOrderMode.Transit && seg.Mode != AirOrderMode.AttackMove)
                        continue;
                    int from = i == 0 ? seg.Index : 0;
                    if (seg.Points.Count > from)
                    {
                        s = i;
                        p = seg.Points.Count - 1;
                    }
                }
                if (s < 0)
                    return false;
            }
            OrderSegment src = plan[s];
            var keep = new OrderSegment
            {
                Mode = src.Mode,
                AltAgl = src.AltAgl,
                FollowTerrain = src.FollowTerrain,
                Speed = src.Speed
            };
            keep.Points.Add(src.Points[p]);
            keep.PointGroups.Add(src.PointGroups[p]);
            Version++;
            Plans[aircraft] = new List<OrderSegment> { keep };
            return true;
        }

        /// <summary>Борта, у которых среди точек планов есть groupId (групповые операции
        /// идут по ВСЕМ членам группы приказа, а не только по выделенным).</summary>
        public static void CollectGroup(int groupId, List<Unit> units, List<int> segments, List<int> points)
        {
            units.Clear();
            segments.Clear();
            points.Clear();
            if (groupId == 0)
                return;
            foreach (KeyValuePair<Unit, List<OrderSegment>> kv in Plans)
            {
                if (kv.Key == null || kv.Key.disabled)
                    continue;
                if (TryGetPointByGroupId(kv.Value, groupId, out int s, out int p))
                {
                    units.Add(kv.Key);
                    segments.Add(s);
                    points.Add(p);
                }
            }
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
                if (seg.Mode == AirOrderMode.Salvo)
                {
                    // Залп по площади (без явных целей) живёт до отстрела; залп по целям —
                    // пока жива хотя бы одна (SalvoService подменяет цели по даталинку).
                    if (!seg.SalvoFired && (HasNoSalvoTargets(seg) || SalvoPrimary(seg) != null))
                        return seg;
                    plan.RemoveAt(0);
                    continue;
                }
                if (seg.Mode == AirOrderMode.Rtb)
                    return seg; // снимает SortieService после обслуживания и взлёта
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
            out GlobalPosition destination, out AirOrderMode mode, out bool followTerrain,
            out Unit attackTarget, out float altitudeAgl, out SpeedMode speed)
        {
            destination = default;
            mode = AirOrderMode.Transit;
            followTerrain = false;
            attackTarget = null;
            altitudeAgl = 0f;
            speed = SpeedMode.Full;
            OrderSegment seg = Step(aircraft, currentPosition);
            if (seg == null)
                return false;
            mode = seg.Mode;
            followTerrain = seg.FollowTerrain;
            attackTarget = seg.Mode == AirOrderMode.Salvo ? SalvoPrimary(seg) : seg.Target;
            altitudeAgl = seg.AltAgl;
            speed = seg.Speed;

            if (seg.Mode == AirOrderMode.Patrol)
            {
                int guard = seg.Points.Count;
                while (guard-- > 0 && HorizontalDistance(currentPosition, WorldOf(seg, seg.Index)) < 900f)
                    seg.Index = (seg.Index + 1) % seg.Points.Count;
                Vector3 wp = WorldOf(seg, seg.Index);
                destination = wp.ToGlobalPosition();
                return true;
            }

            // Центр сегмента: атака/залп — известная позиция цели (даталинк) или якорь
            // клика, остальное — последняя точка маршрута.
            Vector3 center;
            if (seg.Mode == AirOrderMode.Attack)
            {
                if (!TryGetAttackApproach(aircraft, seg, seg.Target, out center))
                    return false;
            }
            else if (seg.Mode == AirOrderMode.Salvo)
            {
                if (!TryGetAttackApproach(aircraft, seg, SalvoPrimary(seg), out center))
                    return false;
            }
            else
            {
                if (seg.Points.Count == 0)
                    return false;
                center = WorldOf(seg, seg.Points.Count - 1);
            }

            Vector2 d = new Vector2(currentPosition.x - center.x, currentPosition.z - center.z);
            bool atLastPoint = seg.Mode == AirOrderMode.Attack || seg.Mode == AirOrderMode.Salvo ||
                               seg.Index >= seg.Points.Count - 1;
            if (atLastPoint && d.magnitude < 2500f)
            {
                const float rot = 25f * Mathf.Deg2Rad;
                Vector2 nd = new Vector2(
                    d.x * Mathf.Cos(rot) - d.y * Mathf.Sin(rot),
                    d.x * Mathf.Sin(rot) + d.y * Mathf.Cos(rot)).normalized;
                Vector3 orbit = new Vector3(center.x + nd.x * 1500f, center.y, center.z + nd.y * 1500f);
                destination = orbit.ToGlobalPosition();
                return true;
            }

            if (seg.Mode == AirOrderMode.Attack || seg.Mode == AirOrderMode.Salvo)
            {
                destination = center.ToGlobalPosition();
                return true;
            }
            destination = WorldOf(seg, seg.Index).ToGlobalPosition();
            return true;
        }

        /// <summary>Первая живая явная цель залпа (SalvoTargets; порядок кликов игрока).</summary>
        public static Unit SalvoPrimary(OrderSegment seg)
        {
            if (seg.SalvoTargets != null)
                foreach (Unit t in seg.SalvoTargets)
                    if (t != null && !t.disabled)
                        return t;
            return seg.Target != null && !seg.Target.disabled ? seg.Target : null;
        }

        /// <summary>Дальность зоны боя: максимум дальности станций с боеприпасами.</summary>
        public static float EngageRadius(Unit aircraft)
        {
            float r = 0f;
            foreach (WeaponStation st in aircraft.weaponStations)
                if (st != null && !st.Cargo && st.WeaponInfo != null && st.Ammo > 0)
                    r = Mathf.Max(r, st.WeaponInfo.targetRequirements.maxRange);
            return r;
        }

        /// <summary>Цель в зоне боя: даталинк держит её и она в пределах дальности
        /// вооружения (× фактор) — бой можно отдавать нативному AI (цель запиннена).</summary>
        public static bool AttackEngaged(Unit aircraft, Unit target)
        {
            if (target == null || target.disabled || aircraft.NetworkHQ == null)
                return false;
            if (!aircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition known))
                return false;
            Vector3 k = known.ToLocalPosition();
            float d = new Vector2(aircraft.transform.position.x - k.x, aircraft.transform.position.z - k.z).magnitude;
            return d < EngageRadius(aircraft) * Settings.AttackEngageFactor;
        }

        /// <summary>Точка подлёта атаки/залпа: известная позиция цели (даталинк), иначе
        /// якорь клика. Высота — AGL профиля сегмента (цель на земле → летим над рельефом).</summary>
        public static bool TryGetAttackApproach(Unit aircraft, OrderSegment seg, Unit target, out Vector3 world)
        {
            if (target != null && !target.disabled && aircraft.NetworkHQ != null &&
                aircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition known))
            {
                Vector3 k = known.ToLocalPosition();
                world = new Vector3(k.x, GameApi.GroundY(k.x, k.z) + seg.AltAgl, k.z);
                return true;
            }
            if (seg.Points.Count > 0)
            {
                Vector3 p = seg.Points[seg.Points.Count - 1].ToLocalPosition();
                world = new Vector3(p.x, GameApi.GroundY(p.x, p.z) + p.y, p.z);
                return true;
            }
            world = default;
            return false;
        }

        /// <summary>Горизонтальное направление текущего участка плана (для конуса
        /// атаки-движения). Вырожденное — Vector3.zero (конус не ветоит).</summary>
        public static Vector3 SegmentHeading(OrderSegment seg, Vector3 currentPosition)
        {
            if (seg == null || seg.Points.Count == 0)
                return Vector3.zero;
            Vector3 wp = WorldOf(seg, Mathf.Clamp(seg.Index, 0, seg.Points.Count - 1));
            Vector2 d = new Vector2(wp.x - currentPosition.x, wp.z - currentPosition.z);
            return d.sqrMagnitude < 1f ? Vector3.zero : new Vector3(d.x, 0f, d.y).normalized;
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
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building))
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
