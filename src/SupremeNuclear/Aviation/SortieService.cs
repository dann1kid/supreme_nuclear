using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Миссия вылета (DESIGN §2.11): Attack-сегмент плана — это ВЫЛЕТ, а не перманентный
    /// приказ. Следит за бортами с приказом атаки: «сухие» станции или топливо ниже
    /// порога → вставка RTB-сегмента к своему аэродрому; у аэродрома борт передаётся
    /// нативной посадке; на земле — обслуживание (перевооружение/заправка за деньги,
    /// интерим до логистической мини-игры, DESIGN §3-7); после взлёта RTB снимается и
    /// вылет возобновляется, пока цель жива. Хост-авторитет: посадка/сервис/деньги —
    /// только на сервере, клиентские планы игнорируются (руление AI — серверное).
    /// </summary>
    internal static class SortieService
    {
        private const float AirborneAgainM = 150f; // взлетел снова: RTB снимается
        private const float OnGroundAltM = 1.5f;   // стоит на земле
        private const float OnGroundSpeed = 3f;

        private static readonly List<Unit> Units = new List<Unit>();
        private static readonly List<IReadOnlyList<AircraftOrders.OrderSegment>> Plans =
            new List<IReadOnlyList<AircraftOrders.OrderSegment>>();

        private static bool IsHost
        {
            get
            {
                var net = NuclearOption.Networking.NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        /// <summary>Тикает всегда в миссии (не только в режиме командира) — как BuildTasks:
        /// планы живут независимо от того, открыт ли стол командира.</summary>
        public static void Tick()
        {
            if (!IsHost)
                return;
            AirbaseReserve.Prune();
            AircraftOrders.CollectAll(Units, Plans);
            for (int i = 0; i < Units.Count; i++)
            {
                AircraftOrders.OrderSegment seg = Plans[i].Count > 0 ? Plans[i][0] : null;
                if (seg != null)
                    TickSegment(Units[i] as Aircraft, Plans[i], seg);
            }
        }

        private static void TickSegment(Aircraft aircraft, IReadOnlyList<AircraftOrders.OrderSegment> plan,
            AircraftOrders.OrderSegment seg)
        {
            if (aircraft == null)
                return;
            if (seg.Mode == AirOrderMode.Attack)
            {
                // Вылет: цель мертва — сегмент уберёт Step. Жива, но борт «сухой»/низко
                // топлива → RTB к ближайшему своему аэродрому (очередь сохраняется:
                // после обслуживания вылет повторится по этому же сегменту).
                if (seg.Target == null || seg.Target.disabled || CombatReady(aircraft))
                    return;
                InsertRtb(aircraft);
                return;
            }
            if (seg.Mode == AirOrderMode.Salvo)
            {
                // Залп отстрелялся, борт «сухой» → домой на перевооружение; после
                // обслуживания вылет возобновится следующим сегментом (отстрелянный
                // залп Step сразу снимет). Боеготов — продолжает план.
                if (!seg.SalvoFired || CombatReady(aircraft))
                    return;
                InsertRtb(aircraft);
                return;
            }
            if (seg.Mode != AirOrderMode.Rtb)
                return;

            Pilot pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;

            if (!seg.RtbLanded)
            {
                if (aircraft.radarAlt > AirborneAgainM)
                {
                    TryHandOffToLanding(aircraft, seg);
                    return;
                }
                // Терминальный RTB (команда «на базу», план из одного сегмента): как только
                // самолёт сел и нативно освободил полосу (перешёл в такси) — паркуем его
                // ДО взлёта на время сервиса (после — деспавн в резерв); вертолёты
                // переходят в parked нативно по вертикальной посадке.
                if (plan.Count == 1 && pilot != null && pilot.currentState is AIPilotTaxiState)
                {
                    seg.RtbLanded = true;
                    pilot.SwitchState(pilot.parkedState);
                    return;
                }
                if (aircraft.radarAlt < OnGroundAltM && aircraft.speed < OnGroundSpeed)
                    seg.RtbLanded = true; // сел сам (нативный возврат по топливу) или после передачи
                return;
            }

            if (aircraft.radarAlt > AirborneAgainM)
            {
                // Обслужен и взлетел — вылет возобновляется следующим сегментом плана.
                AircraftOrders.DropHeadSegment(aircraft);
                return;
            }
            if (aircraft.radarAlt < OnGroundAltM && !seg.Serviced)
                ServiceOnGround(aircraft, seg);
            // Терминальный RTB (команда «на базу», DESIGN §2.11): обслужен — борт уходит
            // в РЕЗЕРВ ангара (деспавн): не занимает полосы, не тикает в симуляции,
            // выкат — плиткой из панели аэродрома (нативный спавн из ангара).
            if (seg.Serviced && plan.Count == 1)
            {
                AirbaseReserve.Add(seg.Base, aircraft.definition as AircraftDefinition);
                AircraftOrders.Forget(aircraft);
                aircraft.DisableUnit();
                CommanderHud.Notice(Loc.F("{0}: в резерве аэродрома", aircraft.definition.unitName));
                return;
            }
            // Авто-RTB вертолёта: нативно запаркован по посадке — после обслуживания
            // взлёт и продолжение вылета (план: [Rtb, следующий сегмент]).
            if (seg.Serviced && plan.Count > 1 && pilot != null &&
                pilot.currentState is PilotParkedState)
                Launch(aircraft);
        }

        /// <summary>Вставить RTB-сегмент в голову плана (базы нет — остаёмся в бою,
        /// нативный топливный возврат).</summary>
        private static void InsertRtb(Aircraft aircraft)
        {
            Airbase airbase = NearestOwnAirbase(aircraft);
            if (airbase == null)
                return;
            AircraftOrders.InsertHeadSegment(aircraft, AircraftOrders.MakeRtb(aircraft, airbase));
        }

        /// <summary>Новый приказ стоящему на базе борту: выкат из паркинга — самолёты
        /// через такси к полосе, вертолёты/тильтвинги вертикальным взлётом. Летящий/
        /// садящийся/таксиующий борт не трогается (проверка состояния).</summary>
        public static void Launch(Unit unit)
        {
            if (!(unit is Aircraft aircraft) || aircraft.pilots == null || aircraft.pilots.Length == 0)
                return;
            Pilot pilot = aircraft.pilots[0];
            if (!(pilot.currentState is PilotParkedState))
                return;
            if (pilot.pilotType == Pilot.PilotType.Plane)
            {
                if (pilot.AITaxiState == null)
                    pilot.AITaxiState = new AIPilotTaxiState();
                pilot.SwitchState(pilot.AITaxiState);
            }
            else
            {
                if (pilot.AIHeloTakeoffState == null)
                    pilot.AIHeloTakeoffState = new AIHeloTakeoffState();
                pilot.SwitchState(pilot.AIHeloTakeoffState);
            }
        }

        /// <summary>У аэродрома передаём борт нативной посадке (однократно) — если он ещё
        /// в боевом состоянии; уже садящийся/таксующий борт не трогаем. Самолёты —
        /// AILandingState, вертолёты/тильтвинги — AIHeloLandingState (вертикальная).</summary>
        private static void TryHandOffToLanding(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            if (seg.RtbHandedOff || seg.Points.Count == 0)
                return;
            Vector3 ab = seg.Points[0].ToLocalPosition();
            float dist = new Vector2(aircraft.transform.position.x - ab.x, aircraft.transform.position.z - ab.z).magnitude;
            if (dist > Settings.LandingHandoffM)
                return;
            Pilot pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
            if (pilot == null)
                return;
            bool helo = pilot.currentState is AIHeloCombatState;
            if (!(helo || pilot.currentState is AIPilotCombatModes))
                return; // уже садится/таксиует — не трогаем
            PilotBaseState landing = helo ? (PilotBaseState)pilot.AIHeloLandingState : pilot.AILandingState;
            if (landing == null)
                return;
            pilot.SwitchState(landing);
            seg.RtbHandedOff = true;
        }

        /// <summary>Обслуживание на земле у своего аэродрома: время → деньги → полный
        /// боекомплект и баки (интерим до логистики; RpcRearm — сетевой путь игры).</summary>
        private static void ServiceOnGround(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            Vector3 ab = seg.Points.Count > 0 ? seg.Points[0].ToLocalPosition() : aircraft.transform.position;
            float dist = new Vector2(aircraft.transform.position.x - ab.x, aircraft.transform.position.z - ab.z).magnitude;
            if (dist > Settings.AirbaseServiceRadiusM)
                return; // сел не у своего аэродрома (выкатился/авария) — сервис не идёт
            seg.ServiceT += Time.deltaTime;
            if (seg.ServiceT < Settings.AirbaseServiceSeconds)
                return;

            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null)
                return;
            if (hq.factionFunds < Settings.AirbaseRearmCost)
                return; // ждём денег: таймер стоит, попробуем в следующем тике

            var stations = new int[aircraft.weaponStations.Count];
            for (int i = 0; i < stations.Length; i++)
            {
                WeaponStation st = aircraft.weaponStations[i];
                stations[i] = st == null || st.Cargo
                    ? 0
                    : Mathf.Max(st.FullAmmo - st.GetAmmoTotal(), 0);
            }
            hq.AddFunds(-Settings.AirbaseRearmCost);
            aircraft.RpcRearm(new RearmEventArgs { Rearmer = null, Stations = stations });
            aircraft.Refuel(null);
            seg.Serviced = true;
            CommanderHud.Notice(Loc.F("{0}: обслужен, вылет возобновляется", aircraft.definition.unitName));
        }

        /// <summary>Борт боеготов: топливо выше порога и есть станция с боеприпасами
        /// (энергетическое — с зарядом, как нативный ChooseHQTarget).</summary>
        private static bool CombatReady(Aircraft aircraft)
        {
            if (aircraft.GetFuelLevel() < Settings.RtbFuelLevel)
                return false;
            foreach (WeaponStation st in aircraft.weaponStations)
            {
                if (st == null || st.Cargo || st.WeaponInfo == null || st.Ammo <= 0)
                    continue;
                if (st.WeaponInfo.energy && aircraft.GetPowerSupply().GetCharge() < 0.6f)
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>Ближайший СВОЙ аэродром, способный принять борт (учёт полосы/троса);
        /// null — базы нет. Используется и авто-RTB, и командой «на базу».</summary>
        internal static Airbase NearestOwnAirbase(Aircraft aircraft)
        {
            if (aircraft.NetworkHQ == null)
                return null;
            AircraftParameters p = aircraft.definition.aircraftParameters;
            var query = new RunwayQuery
            {
                RunwayType = RunwayQueryType.Landing,
                MinSize = p.verticalLanding ? aircraft.definition.length : p.takeoffDistance,
                LandingSpeed = p.verticalLanding ? 0f : p.takeoffSpeed,
                TailHook = aircraft.weaponManager != null && aircraft.weaponManager.HasTailHook()
            };
            return aircraft.NetworkHQ.GetNearestAirbase(aircraft.transform.position, query);
        }
    }
}
