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
        private const int RtbRelaunchLimit = 2;    // повторных выката «сесть не у цели»
        private const float LaunchSlotTimeoutS = 180f; // застрявший выкат не запирает базу навсегда
        private const float LaunchCommittedAltM = 10f; // отрыв колёс — слот выката освобождается

        private static readonly List<Unit> Units = new List<Unit>();
        private static readonly List<IReadOnlyList<AircraftOrders.OrderSegment>> Plans =
            new List<IReadOnlyList<AircraftOrders.OrderSegment>>();

        /// <summary>Очередь выката (v0.110): пачка одновременно выкатившихся бортов
        /// сходится к торцу с разных сторон — нативной дисциплины очереди на рулёжке
        /// нет, передние ждут клиренса с зажатыми тормозами, задние свежевылитые
        /// выталкивают их на полосу (плейтест v0.109). Дозируем выкат: не более
        /// Settings.AirbaseLaunchSlots бортов с аэродрома в такси/разбеге сразу,
        /// ждущие стоят в паркинге — вахта запускает их по освобождению слота.</summary>
        private sealed class LaunchRecord
        {
            public Aircraft Plane;
            public float Since;
        }

        private static readonly Dictionary<Airbase, List<LaunchRecord>> LaunchSlots =
            new Dictionary<Airbase, List<LaunchRecord>>();

        /// <summary>Нативная очередь взлёта полосы — приватна, для янитора голов.</summary>
        private static readonly System.Reflection.FieldInfo TakeoffQueueField =
            HarmonyLib.AccessTools.Field(typeof(Airbase.Runway), "takeoffQueue");

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
            AirbaseAccess.Prune();
            PruneLaunchSlots();
            RunwayQueueJanitor();
            AircraftOrders.CollectAll(Units, Plans);
            for (int i = 0; i < Units.Count; i++)
            {
                AircraftOrders.OrderSegment seg = Plans[i].Count > 0 ? Plans[i][0] : null;
                if (seg == null)
                    continue;
                Aircraft aircraft = Units[i] as Aircraft;
                TickSegment(aircraft, Plans[i], seg);
                // Взлёт-вахта: борт стоит с живым НЕ-RTB планом (приказ пришёл при
                // закрытом взлёте — Launch отказал) и базе открыли взлёт — запускаем.
                // Троттл WatchIntervalSec (§12-45г): проверка «паркинг + взлёт открыт»
                // (нативный AnyNearAirbase) не нуждается в покадровой частоте.
                if (seg.Mode != AirOrderMode.Rtb && aircraft != null &&
                    Time.unscaledTime >= (WatchAt.TryGetValue(aircraft, out float watchAt) ? watchAt : 0f))
                {
                    WatchAt[aircraft] = Time.unscaledTime + WatchIntervalSec;
                    LaunchParkedWhenOpen(aircraft);
                }
            }
        }

        // Троттл неторопливых вахт (v0.112): янитор очередей взлёта (рефлексия
        // takeoffQueue на каждую полосу) и взлёт-вахта парковок — 0.5 с хватает.
        private const float WatchIntervalSec = 0.5f;
        private static float _nextJanitorAt;
        private static readonly Dictionary<Unit, float> WatchAt = new Dictionary<Unit, float>();

        /// <summary>Слоты выката привязаны к базам сцены — сцена умерла, сброс
        /// (зовётся из SupremeNuclearPlugin.sceneLoaded, ARCHITECTURE §4).</summary>
        public static void Reset()
        {
            LaunchSlots.Clear();
            WatchAt.Clear();
        }

        /// <summary>Взлёт открыт на базе, где стоит борт (нет базы рядом — открыт:
        /// аварийная посадка в чистом поле не должна запирать борт).</summary>
        private static bool TakeoffHereOpen(Aircraft aircraft)
        {
            if (aircraft.NetworkHQ == null)
                return true;
            return !aircraft.NetworkHQ.AnyNearAirbase(aircraft.transform.position, out Airbase home) ||
                   AirbaseAccess.IsTakeoffOpen(home);
        }

        /// <summary>Вахта закрытого взлёта: план жив, борт в паркинге и взлёт открыт —
        /// выкат (Notice не спамим: Launch при открытом взлёте молча переключает).
        /// Топлива мало — не трогаем: нативно севший по баку борт иначе зациклит
        /// взлёт/посадку (патруль без RTB-сегмента сервис не проходит).</summary>
        private static void LaunchParkedWhenOpen(Aircraft aircraft)
        {
            Pilot pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
            if (pilot == null || !(pilot.currentState is PilotParkedState) ||
                aircraft.GetFuelLevel() < Settings.RtbFuelLevel || !TakeoffHereOpen(aircraft))
                return;
            Launch(aircraft);
        }

        /// <summary>Свой аэродром под бортом (радиус AnyNearAirbase) — владелец слота
        /// выката; null — базы нет (стоянка в поле), выкат не гейтится.</summary>
        private static Airbase HomeAirbase(Aircraft aircraft)
        {
            return aircraft.NetworkHQ != null &&
                   aircraft.NetworkHQ.AnyNearAirbase(aircraft.transform.position, out Airbase home)
                ? home
                : null;
        }

        /// <summary>Борт занимает слот выката: рулит к полосе (такси-на-взлёт) или
        /// в начале разбега до отрыва колёс. Resupply-такси после посадки и полёт
        /// слот не занимают — следующий выкат стартует ещё в наборе высоты.</summary>
        private static bool LaunchInProgress(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.disabled)
                return false;
            Pilot pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
            if (pilot == null)
                return false;
            if (pilot.currentState is AIPilotTaxiState)
                return !pilot.flightInfo.HasTakenOff;
            return pilot.currentState is AIPilotTakeoffState && aircraft.radarAlt < LaunchCommittedAltM;
        }

        /// <summary>Слоты выката: записи мёртвых/улетевших бортов вычистить,
        /// зависший дольше таймаута выкат — отпустить (база не запирается).</summary>
        private static void PruneLaunchSlots()
        {
            List<Airbase> dead = null;
            foreach (KeyValuePair<Airbase, List<LaunchRecord>> kv in LaunchSlots)
            {
                if (kv.Key == null || kv.Value == null)
                {
                    (dead ??= new List<Airbase>()).Add(kv.Key);
                    continue;
                }
                kv.Value.RemoveAll(r => r == null || r.Plane == null || r.Plane.disabled ||
                                         !LaunchInProgress(r.Plane) ||
                                         Time.time - r.Since > LaunchSlotTimeoutS);
                if (kv.Value.Count == 0)
                    (dead ??= new List<Airbase>()).Add(kv.Key);
            }
            if (dead == null)
                return;
            foreach (Airbase airbase in dead)
                LaunchSlots.Remove(airbase);
        }

        /// <summary>Занять слот выката аэродрома (false — занято, борт ждёт).</summary>
        private static bool TryOccupyLaunchSlot(Airbase airbase, Aircraft plane)
        {
            if (!LaunchSlots.TryGetValue(airbase, out List<LaunchRecord> list))
                list = LaunchSlots[airbase] = new List<LaunchRecord>();
            if (list.Count >= Settings.AirbaseLaunchSlots)
                return false;
            list.Add(new LaunchRecord { Plane = plane, Since = Time.time });
            return true;
        }

        /// <summary>Янитор очереди взлёта полосы: у AIPilotTaxiState НЕТ выхода из
        /// очереди при смене состояния — голова, покинувшая выкат без взлёта
        /// (запаркована выталкиванием/повреждением, ушла в посадку, дубликат от
        /// неудачного само-снятия — DequeueTakeoff снимает ТОЛЬКО голову), держит
        /// ожидание клиренса всей полосы до своего следующего взлёта. Живую голову,
        /// которая не выкатывается, снимаем публичным DequeueTakeoff.</summary>
        private static void RunwayQueueJanitor()
        {
            if (TakeoffQueueField == null || FactionRegistry.airbaseLookup == null)
                return;
            if (Time.unscaledTime < _nextJanitorAt)
                return;
            _nextJanitorAt = Time.unscaledTime + WatchIntervalSec;
            PruneWatchAt();
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null || airbase.disabled || airbase.runways == null)
                    continue;
                foreach (Airbase.Runway runway in airbase.runways)
                {
                    if (runway == null ||
                        !(TakeoffQueueField.GetValue(runway) is Queue<Aircraft> queue) ||
                        queue.Count == 0)
                        continue;
                    Aircraft head = queue.Peek();
                    if (head == null || head.disabled || LaunchInProgress(head))
                        continue; // мёртвую голову чистит сам IsAvailableForTakeoff
                    runway.DequeueTakeoff(head);
                }
            }
        }

        /// <summary>Мёртвые записи взлёт-вахты вычистить (Unity fake-null).</summary>
        private static void PruneWatchAt()
        {
            if (WatchAt.Count == 0)
                return;
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, float> kv in WatchAt)
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead == null)
                return;
            foreach (Unit u in dead)
                WatchAt.Remove(u);
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
                // самолёт сел и нативно освободил полосу (перешёл в такси ПОСЛЕ нашей
                // передачи посадке) — паркуем его ДО взлёта на время сервиса (после —
                // деспавн в резерв); вертолёты переходят в parked нативно по вертикальной
                // посадке. RtbHandedOff отличает «таксиует после посадки» от «таксиует
                // на ВЗЛЁТ» (релэйнч вахты «сел не у цели»): выкат не паркуем обратно.
                if (plan.Count == 1 && seg.RtbHandedOff &&
                    pilot != null && pilot.currentState is AIPilotTaxiState &&
                    (seg.Base == null ||
                     !seg.Base.AircraftIsOnRunway(aircraft, landingRunwaysOnly: true, out _)))
                {
                    // На полотне не паркуем: нативный выкат к exit/сервисной точке
                    // сам уводит борт с полосы (съезды/сервисные точки ВПП v0.109);
                    // стоящий на осевой борт таранят идущие следом на посадку.
                    seg.RtbLanded = true;
                    pilot.SwitchState(pilot.parkedState);
                    return;
                }
                if (aircraft.radarAlt < OnGroundAltM && aircraft.speed < OnGroundSpeed &&
                    pilot != null && pilot.currentState is PilotParkedState)
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
            {
                ServiceOnGround(aircraft, seg);
                // Вахта «сел не у цели»: борт стоит в паркинге ДАЛЕКО от базы
                // приказа (нативная посадка перехватила у соседней полосы до
                // выдачи, уход на второй круг кончился не там, свежий выкат по
                // новому приказу) — повторный выкат к цели, пока хватает топлива
                // и лимит попыток. Исчерпан — план снимается: борт остаётся на
                // стоянке нативно, а не висит вечно «в пути».
                if (!seg.Serviced && pilot != null && pilot.currentState is PilotParkedState &&
                    FarFromRtbBase(aircraft, seg))
                {
                    if (seg.RtbRelaunches < RtbRelaunchLimit &&
                        aircraft.GetFuelLevel() >= Settings.RtbFuelLevel && TakeoffHereOpen(aircraft))
                    {
                        seg.RtbLanded = false;
                        seg.RtbHandedOff = false;
                        seg.RtbRelaunches++;
                        Launch(aircraft);
                    }
                    else if (seg.RtbRelaunches >= RtbRelaunchLimit)
                    {
                        AircraftOrders.Forget(aircraft);
                        CommanderHud.Notice(Loc.F("{0}: сел не у базы приказа — остаётся на стоянке",
                            aircraft.definition.unitName));
                    }
                }
            }
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
            // Взлёт закрыт (AirbaseAccess) — ждём открытия, не дёргая Launch
            // (он отказывает с Notice — был бы спам каждый тик).
            if (seg.Serviced && plan.Count > 1 && pilot != null &&
                pilot.currentState is PilotParkedState && TakeoffHereOpen(aircraft))
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
        /// садящийся/таксиующий борт не трогается (проверка состояния). Самолёту на
        /// базе с закрытым взлётом (AirbaseAccess) выкат запрещён — план ждёт,
        /// взлёт-вахта в Tick запустит борт, когда базу откроют; вертолёты ВПП не
        /// занимают и не ограничиваются.</summary>
        public static void Launch(Unit unit)
        {
            if (!(unit is Aircraft aircraft) || aircraft.pilots == null || aircraft.pilots.Length == 0)
                return;
            Pilot pilot = aircraft.pilots[0];
            if (!(pilot.currentState is PilotParkedState))
                return;
            if (pilot.pilotType == Pilot.PilotType.Plane && !TakeoffHereOpen(aircraft))
            {
                CommanderHud.Notice(Loc.F("Взлёт закрыт: {0} ждёт открытия полосы",
                    aircraft.definition.unitName));
                return;
            }
            if (pilot.pilotType == Pilot.PilotType.Plane)
            {
                // Очередь выката: слот аэродрома занят — борт ждёт в паркинге,
                // вахта LaunchParkedWhenOpen/RTB запустит его по освобождении.
                Airbase home = HomeAirbase(aircraft);
                if (home != null && !TryOccupyLaunchSlot(home, aircraft))
                    return;
                // Повторный выкат уже летавшего борта: AIPilotTaxiState различает
                // «рулю к полосе» и «рулю к обслуживанию» по flightInfo.HasTakenOff,
                // а игра этот флаг НИКОГДА не сбрасывает (ленивый «пилот ушёл»).
                // Без сброса обслуженный борт уходит в ветку обслуживания, тут же
                // паркуется обратно и циклится паркинг↔такси вечно — не выкатившись
                // (плейтест v0.108: «вечное ожидание разрешения на взлёт», борта
                // копятся на полосе и запирают выкат). Сброс = честное «экипаж
                // занял борт заново»; следующий заход на посадку флаг вернёт.
                pilot.flightInfo.HasTakenOff = false;
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
        /// AILandingState, вертолёты/тильтвинги — AIHeloLandingState (вертикальная).
        /// ГЕЙТ (v0.107): нативная посадка ведёт к БЛИЖАЙШЕМУ подходящему аэродрому
        /// ОТ ПОЗИЦИИ БОРТА и не знает про базу приказа — передаём только когда
        /// (а) посадка на базе открыта (нативный RequestLanding на закрытой полосе
        /// вернёт null — посадочный АИ катапультирует пилота!) и (б) база приказа —
        /// ближайшая своя по дистанции до центров (метрика GetNearestAirbase):
        /// иначе у границы HandoffM борта перехватит соседняя база (две базы ближе
        /// 2×HandoffM — цикл «взлёт/посадка не у цели»). Пока гейт закрыт, борт
        /// барражирует точку базы — у центра она всегда становится ближайшей.</summary>
        private static void TryHandOffToLanding(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            if (seg.RtbHandedOff || seg.Points.Count == 0)
                return;
            Vector3 ab = RtbAnchor(aircraft, seg);
            float dist = new Vector2(aircraft.transform.position.x - ab.x, aircraft.transform.position.z - ab.z).magnitude;
            if (dist > Settings.LandingHandoffM)
                return;
            if (seg.Base != null && !AirbaseAccess.IsLandingOpen(seg.Base))
                return;
            Airbase nearest = seg.Base != null ? NearestAirbaseByCenters(aircraft, false) : null;
            if (nearest != null && nearest != seg.Base)
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

        /// <summary>Живой якорь базы RTB: авианосец-аэродром ЕДЕТ — точка плана
        /// протухает уже через минуты (GlobalPosition защищает от сдвига origin,
        /// не от движения самой базы). Базы нет (мёртвая) — точка плана.</summary>
        private static Vector3 RtbAnchor(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            if (seg.Base != null)
                return seg.Base.center.position;
            return seg.Points.Count > 0 ? seg.Points[0].ToLocalPosition() : aircraft.transform.position;
        }

        /// <summary>Борт стоит дальше радиуса обслуживания от базы своего RTB.</summary>
        private static bool FarFromRtbBase(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            Vector3 ab = RtbAnchor(aircraft, seg);
            Vector3 p = aircraft.transform.position;
            return new Vector2(p.x - ab.x, p.z - ab.z).magnitude > Settings.AirbaseServiceRadiusM;
        }

        /// <summary>Ближайший свой аэродром по дистанции до ЦЕНТРОВ (метрика
        /// нативного GetNearestAirbase). requireSuitable — фильтровать
        /// пригодностью борта (`LandingQuery`) и открытостью посадки
        /// (тумблеры AirbaseAccess): выбор базы для RTB; без фильтра — чистая
        /// ближайшая для гейта передачи (нативная посадка сортирует базы сама
        /// со своими плавающими от массы/топлива запросами — честная дистанция
        /// надёжнее, у центра базы цель обгоняет любую соседнюю). Реестра нет —
        /// null (не блокируем посадку).</summary>
        private static Airbase NearestAirbaseByCenters(Aircraft aircraft, bool requireSuitable)
        {
            if (aircraft.NetworkHQ == null || FactionRegistry.airbaseLookup == null)
                return null;
            RunwayQuery query = requireSuitable ? LandingQuery(aircraft) : default(RunwayQuery);
            Vector3 p = aircraft.transform.position;
            Airbase best = null;
            float bestD = float.MaxValue;
            foreach (Airbase ab in FactionRegistry.airbaseLookup.Values)
            {
                if (ab == null || ab.disabled || ab.CurrentHQ != aircraft.NetworkHQ)
                    continue;
                if (requireSuitable && (!ab.IsSuitable(query) || !AirbaseAccess.IsLandingOpen(ab)))
                    continue;
                Vector3 c = ab.center.position;
                float d = (p.x - c.x) * (p.x - c.x) + (p.z - c.z) * (p.z - c.z);
                if (d < bestD)
                {
                    bestD = d;
                    best = ab;
                }
            }
            return best;
        }

        /// <summary>Обслуживание на земле у своего аэродрома: время → деньги → полный
        /// боекомплект и баки (интерим до логистики; RpcRearm — сетевой путь игры).</summary>
        private static void ServiceOnGround(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            Vector3 ab = RtbAnchor(aircraft, seg);
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

        /// <summary>Ближайший СВОЙ аэродром, способный ПРИНЯТЬ борт и открытый для
        /// посадки (тумблеры AirbaseAccess); null — базы нет. Используется авто-RTB
        /// и командой «на базу». Закрытые пропускаем осознанно: нативный
        /// RequestLanding на закрытой полосе вернёт null, а посадочный АИ в этом
        /// случае катапультирует пилота.</summary>
        internal static Airbase NearestOwnAirbase(Aircraft aircraft) =>
            NearestAirbaseByCenters(aircraft, requireSuitable: true);

        /// <summary>Запрос посадочной пригодности борта — зеркало нативного выбора
        /// базы посадкой: вертолётное семейство спрашивает вертикальные точки
        /// (AIHeloLandingState: Vertical + maxRadius), самолёты — полосу
        /// (AIPilotLandingState: Landing + разбег/посадочная скорость/трос,
        /// VTOL-бортам хватает длины корпуса). Единый для «на базу», ПКМ-посадки
        /// на указанную ВПП и проверки базы приказа.</summary>
        internal static RunwayQuery LandingQuery(Aircraft aircraft)
        {
            if (GameApi.IsHeloFamily(aircraft))
                return new RunwayQuery { RunwayType = RunwayQueryType.Vertical, MinSize = aircraft.maxRadius };
            AircraftParameters p = aircraft.definition.aircraftParameters;
            return new RunwayQuery
            {
                RunwayType = RunwayQueryType.Landing,
                MinSize = p.verticalLanding ? aircraft.definition.length : p.takeoffDistance,
                LandingSpeed = p.verticalLanding ? 0f : p.takeoffSpeed,
                TailHook = aircraft.weaponManager != null && aircraft.weaponManager.HasTailHook()
            };
        }
    }
}
