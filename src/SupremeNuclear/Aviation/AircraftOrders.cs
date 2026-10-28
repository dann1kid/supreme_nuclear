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
        /// <summary>Сопровождение союзного юнита (v0.97): борт держится у цели
        /// (сектор веера группы; вблизи — орбита), враг в дальности вооружения —
        /// нативный бой (как атака-движение), после боя следование продолжается.
        /// Живёт, пока цель жива.</summary>
        Escort,
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

    /// <summary>Тоггл «вдоль рельефа» (Alt+T, кнопки панели): Profile — дефолт
    /// профиля высоты (Сверхнизко/Низко огибают, Средне/Высоко — нет), Always/
    /// Never — принудительно для любого профиля. Штампуется на сегмент, как
    /// высота/скорость. Вертолётам руление огибает рельеф ВСЕГДА (v0.93.0,
    /// AutopilotHelo) — тоггл влияет только на самолёты.</summary>
    public enum TerrainHugMode
    {
        Profile,
        Always,
        Never
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

        /// <summary>Тоггл «вдоль рельефа» для НОВЫХ сегментов (Alt+T или кнопки);
        /// Profile — как в профиле высоты.</summary>
        public static TerrainHugMode TerrainHug = TerrainHugMode.Profile;

        /// <summary>Итоговое огибание рельефа для штампа сегмента. Чистая — тестируется.</summary>
        public static bool EffectiveFollowTerrain(bool profileFollow, TerrainHugMode mode) =>
            mode == TerrainHugMode.Always || (mode == TerrainHugMode.Profile && profileFollow);

        /// <summary>Профиль высоты для НОВЫХ сегментов с учётом тоггла «вдоль
        /// рельефа»: Always/Never перекрывает дефолт профиля.</summary>
        public static FlightProfile EffectiveProfile => new FlightProfile(
            Profiles[ProfileIndex].AltitudeAgl,
            EffectiveFollowTerrain(Profiles[ProfileIndex].FollowTerrain, TerrainHug),
            Profiles[ProfileIndex].Name);

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
            /// <summary>Служебный стоп-старт отмены (S): точка стоит на позиции борта,
            /// маркер не рисуется и не сканируется. Одиночные ноги движения — НЕ
            /// служебные (v0.90: раньше прятались, маркер пропадал, точку нельзя
            /// было схватить/удалить).</summary>
            public bool ServiceStart;
            /// <summary>RTB: передан нативной посадке (однократно, у аэродрома).</summary>
            public bool RtbHandedOff;
            /// <summary>RTB: сел (на земле у аэродрома — идёт обслуживание).</summary>
            public bool RtbLanded;
            /// <summary>RTB: сколько раз борт перекатывался «сесть у цели» после
            /// посадки не у базы приказа (SortieService, лимит — не циклить
            /// две близкие базы перехватом нативной посадки).</summary>
            public int RtbRelaunches;
            /// <summary>RTB: секунды на земле у своего аэродрома до конца сервиса.</summary>
            public float ServiceT;
            /// <summary>RTB: перевооружение/заправка выполнены.</summary>
            public bool Serviced;
            /// <summary>RTB: аэродром назначения (резерв при терминальном возврате).</summary>
            public Airbase Base;
            /// <summary>Escort: мир-фиксированный сектор веера группы вокруг цели
            /// (радианы; NaN — без смещения, у цели). Своя цель известна всегда —
            /// даталинк не нужен.</summary>
            public float EscortBearing = float.NaN;
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
                AltAgl = EffectiveProfile.AltitudeAgl,
                FollowTerrain = EffectiveProfile.FollowTerrain,
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
                    AltAgl = EffectiveProfile.AltitudeAgl,
                    FollowTerrain = EffectiveProfile.FollowTerrain,
                    Speed = SpeedIndex
                };
                seg.Points.Add(StartPoint(seg, aircraft));
                seg.PointGroups.Add(0);
                AppendPoint(seg, groundPoint, groupId);
                plan.Add(seg);
                return;
            }

            OrderSegment last = plan.Count > 0 ? plan[plan.Count - 1] : null;
            if (CanAppend(last, mode, EffectiveProfile, SpeedIndex))
            {
                AppendPoint(last, groundPoint, groupId);
                return;
            }
            plan.Add(MakeSegment(mode, groundPoint, groupId));
        }

        /// <summary>Профиль/скорость сегмента совпадают с текущими (слияание ног маршрута).</summary>
        private static bool SameFlightModes(OrderSegment seg) =>
            Mathf.Approximately(seg.AltAgl, EffectiveProfile.AltitudeAgl) &&
            seg.FollowTerrain == EffectiveProfile.FollowTerrain &&
            seg.Speed == SpeedIndex;

        public static void SetPatrol(Unit aircraft, Vector3 groundPoint, int groupId = 0)
        {
            Version++;
            SortieService.Launch(aircraft); // стоящий на базе борт — выкат под новый приказ
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Patrol,
                AltAgl = EffectiveProfile.AltitudeAgl,
                FollowTerrain = EffectiveProfile.FollowTerrain,
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

        /// <summary>Приказ сопровождения союзного юнита (v0.97, перезапись плана):
        /// борт держится у цели сектором bearing веера группы, дерётся с врагами
        /// в дальности вооружения, следование живёт, пока цель жива.</summary>
        public static void SetEscort(Unit aircraft, Unit target, float bearing)
        {
            Version++;
            SortieService.Launch(aircraft); // стоящий на базе борт — выкат под новый приказ
            Plans[aircraft] = new List<OrderSegment> { MakeEscortSegment(target, bearing) };
        }

        /// <summary>Shift+ПКМ по союзнику: эскорт-сегмент дописывается в план
        /// (исполнится после текущих сегментов — план-модель это умеет бесплатно).</summary>
        public static void EnqueueEscort(Unit aircraft, Unit target, float bearing)
        {
            Version++;
            SortieService.Launch(aircraft);
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
            {
                SetEscort(aircraft, target, bearing);
                return;
            }
            // Терминальный RTB при дописывании снимается — как у движений.
            if (plan.Count == 1 && plan[0].Mode == AirOrderMode.Rtb)
                plan.RemoveAt(0);
            plan.Add(MakeEscortSegment(target, bearing));
        }

        private static OrderSegment MakeEscortSegment(Unit target, float bearing)
        {
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Escort,
                Target = target,
                EscortBearing = bearing,
                AltAgl = EffectiveProfile.AltitudeAgl,
                FollowTerrain = EffectiveProfile.FollowTerrain,
                Speed = SpeedIndex
            };
            // Points[0] — якорь (позиция цели на момент клика): служебная точка,
            // не рисуется/не сканируется — связь борт→цель рисует CommanderHud.
            seg.Points.Add(new Vector3(target.transform.position.x, seg.AltAgl,
                target.transform.position.z).ToGlobalPosition());
            seg.PointGroups.Add(0);
            return seg;
        }

        /// <summary>Снять эскорт-сегменты всех бортов, сопровождающих цель
        /// (Ctrl+Shift+ПКМ по связи). Возвращает число снятых сегментов.</summary>
        public static int CancelEscortOf(Unit target)
        {
            int removed = 0;
            foreach (KeyValuePair<Unit, List<OrderSegment>> kv in Plans)
            {
                if (kv.Key == null || kv.Key.disabled)
                    continue;
                List<OrderSegment> plan = kv.Value;
                for (int s = plan.Count - 1; s >= 0; s--)
                    if (plan[s].Mode == AirOrderMode.Escort && plan[s].Target == target)
                    {
                        plan.RemoveAt(s);
                        removed++;
                    }
            }
            if (removed > 0)
                Version++;
            return removed;
        }

        /// <summary>Команда «на базу» (G): терминальный RTB — посадка, обслуживание
        /// и паркинг до следующего приказа (SortieService).</summary>
        public static void ReturnToBase(Unit aircraft, Airbase airbase)
        {
            Version++;
            Plans[aircraft] = new List<OrderSegment> { MakeRtb(aircraft, airbase) };
        }

        /// <summary>Shift+«на базу»: RTB дописывается в ХВОСТ плана — борт вернётся
        /// домой после отработки очереди (когда очередь до него дойдёт, RTB станет
        /// единственным сегментом = терминальный: посадка/сервис/резерв; как у
        /// авто-RTB, за ним ничего нет — деспавн в резерв корректен). Плана нет —
        /// как G без Shift; хвост уже RTB — noop (второй возврат не копим).</summary>
        public static void EnqueueRtb(Unit aircraft, Airbase airbase)
        {
            Version++;
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan) || plan.Count == 0)
            {
                Plans[aircraft] = new List<OrderSegment> { MakeRtb(aircraft, airbase) };
                return;
            }
            if (plan[plan.Count - 1].Mode == AirOrderMode.Rtb)
                return;
            plan.Add(MakeRtb(aircraft, airbase));
        }

        private static OrderSegment MakeAttackSegment(Unit target, Vector3 anchorGround)
        {
            var seg = new OrderSegment
            {
                Mode = AirOrderMode.Attack,
                Target = target,
                AltAgl = EffectiveProfile.AltitudeAgl,
                FollowTerrain = EffectiveProfile.FollowTerrain,
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
                AltAgl = EffectiveProfile.AltitudeAgl,
                FollowTerrain = EffectiveProfile.FollowTerrain,
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
                Speed = SpeedIndex,
                ServiceStart = true // стоп-старт на позиции борта — не вейпоинт
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
            bool changed = !Mathf.Approximately(seg.AltAgl, EffectiveProfile.AltitudeAgl) ||
                           seg.FollowTerrain != EffectiveProfile.FollowTerrain ||
                           seg.Speed != SpeedIndex;
            if (!changed)
                return false;
            Version++;
            seg.AltAgl = EffectiveProfile.AltitudeAgl;
            seg.FollowTerrain = EffectiveProfile.FollowTerrain;
            seg.Speed = SpeedIndex;
            // Точки хранят AGL в y — переписываем под новый профиль (x/z не меняются).
            for (int i = 0; i < seg.Points.Count; i++)
            {
                Vector3 l = seg.Points[i].ToLocalPosition();
                seg.Points[i] = new Vector3(l.x, seg.AltAgl, l.z).ToGlobalPosition();
            }
            return true;
        }

        /// <summary>Сегмент становится патрульным циклом (ПКМ по вейпоинту).
        /// false — сегмент не конвертируется (атака/залп/RTB) или плана нет.</summary>
        public static bool ConvertToPatrol(Unit aircraft, int segmentIndex)
        {
            Version++;
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                return false;
            if (segmentIndex < 0 || segmentIndex >= plan.Count)
                return false;
            OrderSegment seg = plan[segmentIndex];
            if (seg.Mode == AirOrderMode.Attack || seg.Mode == AirOrderMode.Salvo ||
                seg.Mode == AirOrderMode.Rtb || seg.Mode == AirOrderMode.Escort)
                return false; // атаку/залп/возврат/сопровождение в патруль не превращаем
            seg.Mode = AirOrderMode.Patrol;
            seg.Target = null;
            if (seg.Points.Count == 1)
            {
                seg.Points.Insert(0, StartPoint(seg, aircraft));
                seg.PointGroups.Insert(0, 0);
            }
            seg.Index = 0;
            return true;
        }

        /// <summary>Удалить точку плана (Ctrl+Shift+ПКМ). Пост-обработка — общая
        /// с групповым удалением (NormalizeAfterRemoval): паритет правил дропа
        /// опустевших сегментов и клампа курсора.</summary>
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
            NormalizeAfterRemoval(plan, segmentIndex);
        }

        /// <summary>Нормализация сегмента после сноса точек (общая для одиночного
        /// и группового удаления): кламп курсора (кроме Attack/Salvo — там Index
        /// ведёт выпущенную цель) и дроп опустевшего сегмента без явной цели
        /// и без залп-целей. Чистая — тестируется (через RemovePointsWithGroup).</summary>
        private static void NormalizeAfterRemoval(List<OrderSegment> plan, int s)
        {
            OrderSegment seg = plan[s];
            if (seg.Points.Count > 0)
            {
                if (seg.Mode != AirOrderMode.Attack && seg.Mode != AirOrderMode.Salvo)
                    seg.Index = Mathf.Clamp(seg.Index, 0, seg.Points.Count - 1);
            }
            else if (seg.Target == null && HasNoSalvoTargets(seg))
                plan.RemoveAt(s);
        }

        /// <summary>Служебная точка плана — НЕ вейпоинт приказа: не рисуется и не
        /// сканируется. Это взлёт патруля (p=0), стоп-старт отмены (ServiceStart)
        /// и якоря атаки/залпа/RTB индивидуальных приказов (gid=0) — приказы этих
        /// видов рисуются крестом/лентой к базе, их точки не редактируются
        /// (якорь атаки у группы всегда gid=0 — маркер дал бы стопку на одной
        /// точке; точка RTB — точка посадки, драг ломал бы передачу нативной
        /// посадке). Групповые точки (gid≠0) и одиночные ноги движения —
        /// ОБЫЧНЫЕ вейпоинты. Единое правило для WaypointScanner и CommanderHud.
        /// Чистая — тестируется.</summary>
        public static bool IsServicePoint(OrderSegment seg, int p)
        {
            if (seg.PointGroups[p] != 0)
                return false;
            if (seg.Mode == AirOrderMode.Patrol)
                return p == 0;
            return seg.ServiceStart ||
                   seg.Mode == AirOrderMode.Attack || seg.Mode == AirOrderMode.Salvo ||
                   seg.Mode == AirOrderMode.Escort || seg.Mode == AirOrderMode.Rtb;
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

        /// <summary>Удалить из плана все точки группы приказа; пост-обработка —
        /// общая с одиночным удалением (NormalizeAfterRemoval): опустевший сегмент
        /// дропается только без явной цели И без залп-целей, курсор клампится
        /// вне Attack/Salvo. Чистая функция — тестируется.</summary>
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
                NormalizeAfterRemoval(plan, s);
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
        /// транспортной точке) и/или один Attack-сегмент назначенной цели
        /// attackTarget (очередь атак борта := одна цель, v0.107); профиль/скорость —
        /// исходных сегментов, якорь атаки — позиция цели, остальной план (включая
        /// очередь атак/RTB) заменяется. keptPoint — оставлен вектор движения.
        /// false — нечего оставить.</summary>
        public static bool DisperseKeep(Unit aircraft, int groupId, Unit attackTarget, out bool keptPoint)
        {
            keptPoint = false;
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                return false;
            // Режим полёта исходной атаки снимаем ДО резки плана — назначенная
            // цель наследует профиль/скорость выбора игрока, а не текущие дефолты.
            OrderSegment attackSrc = null;
            for (int i = 0; i < plan.Count; i++)
                if (plan[i].Mode == AirOrderMode.Attack)
                {
                    attackSrc = plan[i];
                    break;
                }

            var keep = new List<OrderSegment>();
            if (groupId != 0)
            {
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
                }
                if (s >= 0)
                {
                    OrderSegment src = plan[s];
                    var point = new OrderSegment
                    {
                        Mode = src.Mode,
                        AltAgl = src.AltAgl,
                        FollowTerrain = src.FollowTerrain,
                        Speed = src.Speed
                    };
                    point.Points.Add(src.Points[p]);
                    point.PointGroups.Add(src.PointGroups[p]);
                    keep.Add(point);
                    keptPoint = true;
                }
            }
            if (attackTarget != null && !attackTarget.disabled)
            {
                var seg = new OrderSegment
                {
                    Mode = AirOrderMode.Attack,
                    Target = attackTarget,
                    AltAgl = attackSrc != null ? attackSrc.AltAgl : EffectiveProfile.AltitudeAgl,
                    FollowTerrain = attackSrc != null ? attackSrc.FollowTerrain : EffectiveProfile.FollowTerrain,
                    Speed = attackSrc != null ? attackSrc.Speed : SpeedIndex
                };
                Vector3 tp = attackTarget.transform.position;
                seg.Points.Add(new Vector3(tp.x, seg.AltAgl, tp.z).ToGlobalPosition());
                seg.PointGroups.Add(0);
                keep.Add(seg);
            }
            if (keep.Count == 0)
                return false;
            Version++;
            Plans[aircraft] = keep;
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

        /// <summary>Снести головной сегмент плана (SortieService: обслуженный борт
        /// взлетел — вылет продолжается следующим сегментом). Планы мутруются
        /// только через API сервиса, не кастом списка напрямую.</summary>
        public static void DropHeadSegment(Unit aircraft)
        {
            Version++;
            if (Plans.TryGetValue(aircraft, out List<OrderSegment> plan) && plan.Count > 0)
                plan.RemoveAt(0);
        }

        /// <summary>Вставить сегмент в голову плана (SortieService: авто-RTB).</summary>
        public static void InsertHeadSegment(Unit aircraft, OrderSegment segment)
        {
            if (aircraft == null || segment == null)
                return;
            Version++;
            if (!Plans.TryGetValue(aircraft, out List<OrderSegment> plan))
                Plans[aircraft] = plan = new List<OrderSegment>();
            plan.Insert(0, segment);
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
            EnemyScans.Clear();
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
                if (seg.Mode == AirOrderMode.Escort)
                {
                    // Сопровождение живёт, пока цель жива; дальше — следующий сегмент.
                    if (seg.Target != null && !seg.Target.disabled)
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

                float reach = ReachRadius(aircraft);
                while (seg.Index < seg.Points.Count - 1 &&
                       HorizontalDistance(currentPosition, WorldOf(seg, seg.Index)) < reach)
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

        /// <summary>Скретч-буфер дистанций патрульного цикла (руление, каждый
        /// Fixed-тик; растёт только при росте цикла).</summary>
        private static float[] PatrolDists = new float[8];

        /// <summary>Радиус зачёта вейпоинта — упреждение на разворот ~4 с полёта:
        /// реактивному на 250 м/с нужно ~900 м (кламп-потолок, прежнее поведение),
        /// вертолёту на 60 м/с — 240 м. Общим 900 м вертолёт на малом цикле
        /// зависал: пузырь накрывал весь цикл, и он зачитывался целиком каждый
        /// кадр (см. NextPatrolIndex). Чистая — тестируется.</summary>
        public static float WaypointReachRadius(float speedMps) =>
            Mathf.Clamp(speedMps * 4f, 120f, 900f);

        /// <summary>Радиус зачёта для борта (speed — м/с, Unit.speed).</summary>
        private static float ReachRadius(Unit unit) =>
            unit is Aircraft a ? WaypointReachRadius(a.speed) : 900f;

        /// <summary>Курсор патрульного цикла: зачитываем подряд точки в радиусе
        /// зачёта (dists[0..count) — горизонтальные дистанции до точек), переход
        /// по кругу. Вышли по дистанции — обычное продвижение. Радиус накрыл ВЕСЬ
        /// цикл (полный проход вернулся бы к той же точке — зависание) — следующая
        /// цель САМАЯ ДАЛЬНЯЯ точка: борт перелетает россыпь насквозь, а не кружит
        /// одну. Чистая — тестируется.</summary>
        public static int NextPatrolIndex(float[] dists, int count, int index, float reach)
        {
            if (count <= 0 || index < 0 || index >= count)
                return index;
            int guard = count;
            while (guard-- > 0 && dists[index] < reach)
                index = (index + 1) % count;
            if (guard >= 0)
                return index; // остановились на точке вне радиуса
            int far = index;
            for (int i = 0; i < count; i++)
                if (dists[i] > dists[far])
                    far = i;
            return far;
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
                int n = seg.Points.Count;
                float reach = ReachRadius(aircraft);
                if (PatrolDists.Length < n)
                    PatrolDists = new float[Mathf.NextPowerOfTwo(n)];
                for (int i = 0; i < n; i++)
                    PatrolDists[i] = HorizontalDistance(currentPosition, WorldOf(seg, i));
                seg.Index = NextPatrolIndex(PatrolDists, n, seg.Index, reach);
                destination = WorldOf(seg, seg.Index).ToGlobalPosition();
                return true;
            }

            // Центр сегмента: атака/залп — известная позиция цели (даталинк) или якорь
            // клика, эскорт — живая позиция СОЮЗНОЙ цели (известна всегда) со смещением
            // сектора веера, остальное — последняя точка маршрута.
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
            else if (seg.Mode == AirOrderMode.Escort)
            {
                if (seg.Target == null || seg.Target.disabled)
                    return false;
                Vector3 tp = seg.Target.transform.position;
                float bearing = seg.EscortBearing;
                if (float.IsNaN(bearing))
                    bearing = 0f;
                float off = Settings.EscortAirDistM;
                center = new Vector3(tp.x + Mathf.Sin(bearing) * off,
                    GameApi.GroundY(tp.x, tp.z) + seg.AltAgl, tp.z + Mathf.Cos(bearing) * off);
            }
            else
            {
                if (seg.Points.Count == 0)
                    return false;
                center = WorldOf(seg, seg.Points.Count - 1);
            }

            Vector2 d = new Vector2(currentPosition.x - center.x, currentPosition.z - center.z);
            bool atLastPoint = seg.Mode == AirOrderMode.Attack || seg.Mode == AirOrderMode.Salvo ||
                               seg.Mode == AirOrderMode.Escort ||
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

            if (seg.Mode == AirOrderMode.Attack || seg.Mode == AirOrderMode.Salvo ||
                seg.Mode == AirOrderMode.Escort)
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

        /// <summary>Скан «рядом враг» для рулевых патчей (звали его 50 Гц на каждого
        /// борта в attack-move/эскорте — полный обход реестра, рос квадратично,
        /// §12-7/§12-18): теперь по нативной решётке BattlefieldGrid (как сканы
        /// наземки) с троттлом ScanIntervalSec — решению «передать бой нативному AI»
        /// хватает полусекундной свежести. Кэш переживает до конца окна и «врага
        /// больше нет» (борта продолжают штатный тик до перевыгреба).</summary>
        private const float ScanIntervalSec = 0.5f;

        private struct EnemyScan
        {
            public float At;
            public float Range;
            public bool Found;
        }

        private static readonly Dictionary<Unit, EnemyScan> EnemyScans =
            new Dictionary<Unit, EnemyScan>();

        public static bool EnemyNearby(Unit aircraft, FactionHQ localHq, float range = 9000f)
        {
            if (EnemyScans.TryGetValue(aircraft, out EnemyScan s) &&
                s.Range == range && Time.unscaledTime - s.At < ScanIntervalSec)
                return s.Found;
            bool found = false;
            Vector3 pos = aircraft.transform.position;
            float sq = range * range;
            foreach (Unit unit in BattlefieldGrid.GetUnitsInRangeEnumerable(
                         aircraft.GlobalPosition(), range))
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ == localHq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building))
                    continue;
                Vector3 d = unit.transform.position - pos;
                if (d.sqrMagnitude < sq)
                {
                    found = true;
                    break;
                }
            }
            EnemyScans[aircraft] = new EnemyScan { At = Time.unscaledTime, Range = range, Found = found };
            return found;
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
            {
                Plans.Remove(unit);
                EnemyScans.Remove(unit);
            }
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b) =>
            new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }
}
