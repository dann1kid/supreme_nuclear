using System.Collections.Generic;
using NuclearOption;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// AI-командир v1 (DESIGN §3-3, наземка-only): фракция БЕЗ игроков играет
    /// тем же Task-API, что и игрок (§2.3 «AI не может "не уметь строить"»):
    /// инженер-строитель ставит генераторы→депо (BuildTasks.Place), инженеры-
    /// мексовщики едут к нейтральным мексовкам (захват нативный, доход — общий
    /// EconomyService.PayIncome владельцу), депо клепает танки (ProductionTasks,
    /// деньги фракции), собравшиеся волны идут на базу противника — нативный
    /// SetDestination(playerCommand:false, образец MobileArtilleryAI), бой и
    /// захват включаются нативным CheckObstacles/Capture. Хост-only, троттл tickSec.
    /// Модовые GroundOrders-цепочки сознательно НЕ используются: их тик живёт
    /// в режиме командира игрока, AI должен работать всегда.
    /// </summary>
    public static class AiCommanderService
    {
        private sealed class State
        {
            public bool announced;          // Notice о активации показан
            public float snapshotAt;        // последний снапшот состава в лог
            public float nextEngineerLogAt; // троттл лога восполнения инженеров
            public bool airSlept;           // авиакрыло спит (нет ангаров/каталога)
            public int prevAir = -1;        // телеметрия размера авиакрыла
            public float nextAirStrikeAt;   // таймер авиаудара по экономике
            public int wavesSent;
            public float nextWaveAt;        // Time.time, до которого волна ждёт полного сбора
            public Unit builder;            // первый инженер (зонд диагностики)
            public object rallyDepot;       // депо, которому ралли уже выставлено
            public bool energyStall;        // гистерезис энергосталла (M28-паттерн)
            public float defendAt;          // троттл реакции гарнизона на угрозу
            public GlobalPosition? reclaimPos; // стабильная цель сбора обломков
            // Событийная телеметрия (лог «что изменилось», 2026-10-30): прошлые
            // значения счётчиков — изменение печатается сразу, не снапшотом.
            public int prevEngineers = -1, prevCombat = -1, prevDepots = -1, prevFree = -1;
        }

        private static readonly Dictionary<FactionHQ, State> States = new Dictionary<FactionHQ, State>();
        private static readonly List<FactionHQ> DeadHqs = new List<FactionHQ>();
        private static readonly List<Unit> Engineers = new List<Unit>();
        private static readonly List<Unit> Combat = new List<Unit>();
        private static readonly List<Unit> WavePool = new List<Unit>();
        private static readonly List<VehicleDepot> Depots = new List<VehicleDepot>();
        private static AiProfile _profile;
        private static float _nextTick;

        public static void Reset()
        {
            States.Clear();
            DeadHqs.Clear();
            Engineers.Clear();
            Combat.Clear();
            WavePool.Clear();
            Depots.Clear();
            _profile = null;
            _nextTick = 0f;
        }

        public static void Tick()
        {
            if (!GameApi.InMission)
            {
                if (States.Count > 0)
                    Reset();
                return;
            }

            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;

            // Фаза лобби «Скримиша» (v0.154): миссия не пущена (патч №14 держит
            // StartMission) — юнитов сценария ещё нет, AI ждать «Старта».
            if (!MissionManager.IsRunning)
                return;

            // Распознавание — только когда ХОТЯ БЫ ОДНА фракция занята игроком:
            // factionPlayers заполняется при спавне Player, до этого пуст у ВСЕХ
            // (плейтесты 2026-10-29: IsRunning включается РАНЬШЕ входа игрока — обе
            // стороны объявлялись AI). В SP это момент старта; MP-тайминг — §12-49.
            bool anyPlayer = false;
            foreach (FactionHQ probe in FactionRegistry.HQLookup.Values)
                if (probe != null && probe.factionPlayers != null && probe.factionPlayers.Count > 0)
                {
                    anyPlayer = true;
                    break;
                }
            if (!anyPlayer)
                return;

            if (_profile == null)
                _profile = AiProfile.Load();
            if (!_profile.enabled)
                return;
            if (Time.time < _nextTick)
                return;
            _nextTick = Time.time + Mathf.Max(0.25f, _profile.tickSec);

            // AI-фракция = штаб без игроков (нативного флага AI нет, разведка 2026-10-29);
            // локальный HQ исключаем всегда — подстраховка от гонки списков.
            GameApi.TryGetLocalHQ(out FactionHQ localHq);
            foreach (FactionHQ hq in FactionRegistry.HQLookup.Values)
            {
                if (hq == null || hq == localHq)
                    continue;
                if (hq.factionPlayers != null && hq.factionPlayers.Count > 0)
                    continue;
                // Лобби v3: армия-слот человека НЕ управляется ИИ, даже если
                // командир ещё не заспавнился (HQ без игроков).
                if (!IsAiArmy(hq))
                    continue;
                if (!States.TryGetValue(hq, out State st))
                {
                    st = new State
                    {
                        nextWaveAt = Time.time + _profile.waveIntervalSec,
                        nextAirStrikeAt = Time.time + _profile.airStrikeIntervalSec,
                    };
                    States[hq] = st;
                }
                RunFaction(hq, st);
            }

            foreach (KeyValuePair<FactionHQ, State> kv in States)
                if (kv.Key == null || !FactionRegistry.HQLookup.ContainsValue(kv.Key))
                    DeadHqs.Add(kv.Key);
            foreach (FactionHQ dead in DeadHqs)
                States.Remove(dead);
            DeadHqs.Clear();
        }

        private static void RunFaction(FactionHQ hq, State st)
        {
            // База фракции — АЭРОДРОМ миссии (GlobalPosition, floating-safe):
            // позиция сценского HQ ненадёжна (нативный код возвращает её после
            // телепортов — плейтесты 2026-10-30: HQ-спам в логе).
            GlobalPosition? basePos = BasePosOf(hq);
            if (basePos == null)
                return;

            // Сбор своих: инженеры / боевые / депо — один проход по реестру.
            Engineers.Clear();
            Combat.Clear();
            Depots.Clear();
            AirBuf.Clear();
            int powers = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled)
                    continue;
                if (unit.NetworkHQ != hq)
                    continue;
                if (GameApi.IsEngineer(unit))
                    Engineers.Add(unit);
                else if (unit is Aircraft)
                    AirBuf.Add(unit);
                else if (unit is GroundVehicle)
                    Combat.Add(unit);
                if (unit is VehicleDepot depot && depot != null)
                    Depots.Add(depot);
                if (EconomyService.IsGenerator(unit))
                    powers++;
            }

            // События состава: печатаются В МОМЕНТ изменения (гибель инженеров,
            // появление депо, рост армии — раньше ловились только снапшотом ×15 с).
            if (st.prevEngineers >= 0 && st.prevEngineers != Engineers.Count)
                Log.Info("AI-событие: инженеры " + st.prevEngineers + " → " + Engineers.Count +
                         (Engineers.Count < st.prevEngineers ? " (потери!)" : ""));
            if (st.prevCombat >= 0 && st.prevCombat != Combat.Count)
                Log.Info("AI-событие: боевых " + st.prevCombat + " → " + Combat.Count);
            if (st.prevDepots >= 0 && st.prevDepots != Depots.Count)
                Log.Info("AI-событие: депо " + st.prevDepots + " → " + Depots.Count +
                         (Depots.Count > st.prevDepots ? " (производство открыто)" : ""));
            {
                int free = FreeSitesCount();
                if (st.prevFree >= 0 && st.prevFree != free)
                    Log.Info("AI-событие: свободных точек " + st.prevFree + " → " + free +
                             (free < st.prevFree ? " (кто-то застроил)" : " (освободились/снесли)"));
                st.prevFree = free;
            }
            st.prevEngineers = Engineers.Count;
            st.prevCombat = Combat.Count;
            st.prevDepots = Depots.Count;

            if (!st.announced)
            {
                st.announced = true;
                Vector3 announcePos = basePos.Value.ToLocalPosition();
                CommanderHud.Notice(Loc.F("AI-командир активен: {0}", hq.faction != null
                    ? hq.faction.factionName : hq.name));
                Log.Info("AI: командир активен — " +
                         (hq.faction != null ? hq.faction.factionName : hq.name) +
                         $" (база @ {announcePos.x:F0},{announcePos.z:F0})");
                // СТОП нативному автотаргетингу: юниты миссии БЕЗ приказа сами едут
                // к вражеской базе (она objective) — через озёра, и гибнут по пути
                // (плейтест 2026-10-30: «у оппонента нет инженеров», hp 100%).
                // Приказ «на свою позицию» ставит commandedDestination и глушит его.
                foreach (Unit e in Engineers)
                    e.GetComponent<UnitCommand>()?.SetDestination(
                        e.transform.position.ToGlobalPosition(), false);
            }
            else if (Time.time - st.snapshotAt > 15f)
            {
                // Снапшот состава: по падению счётчиков между строками видно,
                // КОГДА юниты гибнут (диагностика «взорвались на земле», 2026-10-30).
                st.snapshotAt = Time.time;
                string fname = hq.faction != null ? hq.faction.factionName : "?";
                // min-hp инженеров: мгновенные исчезновения (физика/провал) держат
                // hp=100% до последнего кадра, бой — просевший hp (диагностика смертей).
                float minHp = 1f;
                foreach (Unit e in Engineers)
                    minHp = Mathf.Min(minHp, GameApi.HpFraction(e));
                Log.Info($"AI: {fname} — инженеров {Engineers.Count} (min hp {minHp:P0}), боевых {Combat.Count}, " +
                         $"депо {Depots.Count}, точек свободно {FreeSitesCount()}, заявок {st.wavesSent}");
            }

            Vector3 bp = basePos.Value.ToLocalPosition();
            RunEconomy(hq, st, bp, Engineers, powers, Depots.Count > 0);
            RunProduction(hq, st, bp, Depots);
            RunWaves(hq, st, bp, Combat);
            RunAir(hq, st, bp, AirBuf);
        }

        /// <summary>Свободный буфер авиакрыла (переиспользуется тиком).</summary>
        private static readonly List<Unit> AirBuf = new List<Unit>();

        /// <summary>Авиакрыло v1 (2026-10-31): CAP-кольцо над своей базой, перехват
        /// воздушных угроз, периодические авиаудары по экономике противника.
        /// Производство — ProductionTasks.EnqueueAirDistributed (оплата потоково
        /// у владельца ангара, т.е. из кошелька фракции); обслуживание бортов —
        /// нативный путь SortieService (RTB/перевооружение/повтор вылета сами).</summary>
        private static void RunAir(FactionHQ hq, State st, Vector3 basePos, List<Unit> aircraft)
        {
            if (!_profile.airEnabled)
                return;
            Airbase airbase = OwnAirbaseOf(hq);
            AircraftDefinition fighter = airbase != null ? FighterDefinition(airbase) : null;
            if (fighter == null)
            {
                if (!st.airSlept)
                {
                    st.airSlept = true;
                    Log.Warn("AI: авиакрыло спит — " + (airbase == null
                        ? "нет своей базы с ангарями"
                        : "в каталоге ангаров нет допустимых бортов (fighterJsonKey?)"));
                }
                return;
            }
            if (st.airSlept)
            {
                st.airSlept = false;
                Log.Info("AI: авиакрыло — " + fighter.jsonKey);
            }

            // Заказ: держим численность (гейт по массе — как у наземки).
            float cost = fighter.value > 0f ? fighter.value : Settings.BuildDefaultCost;
            if (aircraft.Count < _profile.airCap &&
                hq.factionFunds >= cost * _profile.airReserveFactor)
                ProductionTasks.EnqueueAirDistributed(airbase, fighter);

            // Телеметрия размера крыла.
            if (st.prevAir >= 0 && st.prevAir != aircraft.Count)
                Log.Info("AI-событие: авиакрыло " + st.prevAir + " → " + aircraft.Count);
            st.prevAir = aircraft.Count;

            // CAP: борт без плана — на кольцо над базой (высота — профиль приказов).
            float capAlt = AircraftOrders.EffectiveProfile.AltitudeAgl;
            List<Vector3> ring = Util.CommanderOrbitPoints(
                new Vector2(basePos.x, basePos.z), _profile.capRadiusM, capAlt, _profile.capPoints);
            for (int i = 0; i < aircraft.Count; i++)
            {
                Unit u = aircraft[i];
                if (u == null || u.disabled || AircraftOrders.PlanOf(u) != null)
                    continue;
                AircraftOrders.SetPatrol(u, ring[0]);
                for (int k = 1; k < ring.Count; k++)
                    AircraftOrders.Enqueue(u, AirOrderMode.Patrol, ring[k]);
                Log.Info("AI: борт '" + u.NetworkUniqueName + "' → CAP над базой");
            }

            // Перехват: воздушная угроза у базы — Attack в ГОЛОВУ (Patrol-хвост
            // уцелеет, цель погибла — сегмент снимется и крыло вернётся на кольцо).
            Unit threat = NearestEnemyAircraft(hq, basePos, _profile.interceptRadiusM);
            if (threat != null && SendAttack(aircraft, threat, basePos, 2) > 0)
                Log.Info("AI-событие: перехват '" + threat.NetworkUniqueName + "' у базы");

            // Авиаудар по экономике: не голым крылом (строгий > strikeSize).
            if (Time.time >= st.nextAirStrikeAt && aircraft.Count > _profile.airStrikeSize)
            {
                st.nextAirStrikeAt = Time.time + _profile.airStrikeIntervalSec;
                GlobalPosition? enemyBase = EnemyBasePosOf(hq);
                Unit target = NearestEnemyExtractor(hq, basePos) ??
                    NearestEnemyUnit(hq, enemyBase?.ToLocalPosition() ?? basePos, 3000f);
                if (target != null && SendAttack(aircraft, target,
                        enemyBase?.ToLocalPosition() ?? basePos, _profile.airStrikeSize) > 0)
                    Log.Info("AI-событие: авиаудар — " + _profile.airStrikeSize +
                             " борт(а) по '" + target.NetworkUniqueName + "'");
            }
        }

        /// <summary>Выдать Attack-сегмент в голову планов свободных бортов
        /// (уже атакующие пропускаются); возвращает число отправленных.</summary>
        private static int SendAttack(List<Unit> aircraft, Unit target, Vector3 anchor, int maxCount)
        {
            int sent = 0;
            for (int i = 0; i < aircraft.Count && sent < maxCount; i++)
            {
                Unit u = aircraft[i];
                if (u == null || u.disabled)
                    continue;
                List<AircraftOrders.OrderSegment> plan = AircraftOrders.PlanOf(u);
                if (plan != null && plan.Count > 0 && plan[0].Mode == AirOrderMode.Attack)
                    continue; // уже бьёт
                AircraftOrders.InsertHeadSegment(u, AircraftOrders.MakeAttackSegment(target, anchor));
                sent++;
            }
            return sent;
        }

        /// <summary>Ближайший ЧУЖОЙ самолёт у точки (перехват угроз у базы).</summary>
        /// <summary>Армия-слот ИИ ли этот HQ (лобби v3): план слотов знает
        /// контроллеры; вне SN-лобби (Ctrl+G/сток) — прежняя семантика «HQ без
        /// игроков = ИИ».</summary>
        private static bool IsAiArmy(FactionHQ hq)
        {
            var state = SnLobbyService.State;
            if (state?.armies == null || hq.faction == null)
                return true; // вне лобби — старое поведение
            string name = hq.faction.factionName;
            foreach (LobbyState.Army a in state.armies)
            {
                if (FactionFactory.ArmyName(a.id) != name)
                    continue;
                return a.controller == LobbyState.CtrlAi;
            }
            return true; // фракция не из плана (нативные) — старое поведение
        }

        /// <summary>Союзная ли армия с армией AI (лобби v3: команда-союз).
        /// Вне SN-лобби всегда false — все чужие HQ враждебны.</summary>
        private static bool AlliedWith(FactionHQ a, FactionHQ b)
        {
            if (a == null || b == null || a == b)
                return false;
            return SnLobbyService.AlliedFactions(a.faction?.factionName, b.faction?.factionName);
        }

        private static Unit NearestEnemyAircraft(FactionHQ of, Vector3 pos, float radius)
        {
            Unit best = null;
            float bestSq = radius * radius;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit is Building || !(unit is Aircraft))
                    continue;
                if (unit.NetworkHQ == null || unit.NetworkHQ == of || AlliedWith(of, unit.NetworkHQ))
                    continue;
                float sq = (unit.transform.position - pos).sqrMagnitude;
                if (sq >= bestSq)
                    continue;
                bestSq = sq;
                best = unit;
            }
            return best;
        }

        /// <summary>Свой аэродром фракции (объект — для заказа в ангар).</summary>
        private static Airbase OwnAirbaseOf(FactionHQ hq)
        {
            foreach (Airbase ab in FactionRegistry.airbaseLookup.Values)
                if (ab != null && ab.CurrentHQ == hq)
                    return ab;
            return null;
        }

        private static AircraftDefinition _fighterDef;

        /// <summary>Борт авиакрыла: jsonKey профиля либо САМЫЙ ДЕШЁВЫЙ допустимый
        /// (IsAllowed(false) — ивентовый UFO Spawner молча режет) из каталога
        /// ангаров своей базы. Ролей у самолётов нет — один универсал.</summary>
        private static AircraftDefinition FighterDefinition(Airbase airbase)
        {
            if (_fighterDef != null)
                return _fighterDef;
            if (!string.IsNullOrEmpty(_profile.fighterJsonKey) && Encyclopedia.i?.aircraft != null)
                foreach (AircraftDefinition a in Encyclopedia.i.aircraft)
                    if (a != null && a.jsonKey == _profile.fighterJsonKey)
                        return _fighterDef = a;
            if (airbase?.hangars != null)
                foreach (Hangar hangar in airbase.hangars)
                {
                    if (hangar == null)
                        continue;
                    AircraftDefinition[] catalog;
                    try
                    {
                        catalog = hangar.GetAvailableAircraft(); // натив падает на отцепленных префабах
                    }
                    catch (System.Exception)
                    {
                        continue;
                    }
                    if (catalog == null)
                        continue;
                    AircraftDefinition best = null;
                    foreach (AircraftDefinition a in catalog)
                        if (a != null && a.IsAllowed(false) && (best == null || a.value < best.value))
                            best = a;
                    if (best != null)
                        return _fighterDef = best;
                }
            return null;
        }

        /// <summary>Экономика v3: СВОБОДНЫЕ инженеры (пачкой — канон стройки) ведут
        /// план от точек месторождений: уголь у базы → экстракторы на массе → депо →
        /// генераторы по волнам. Захват чужих построек — пассивный CaptureService
        /// (инженер стоит рядом). Мексовки-«готовые здания» ушли (v3): строим сами.</summary>
        private static void RunEconomy(FactionHQ hq, State st, Vector3 basePos, List<Unit> engineers,
            int powers, bool depotBuilt)
        {
            // Зонд «строитель не едет» (диагностика плейтестов) — по первому инженеру.
            if (engineers.Count > 0)
                st.builder = engineers[0];

            // Свободные инженеры (очередь пуста — SeqEntry снимается по завершении).
            BuildBuf.Clear();
            foreach (Unit e in engineers)
                if (e != null && (BuildTasks.SequenceOf(e)?.Count ?? 0) == 0)
                    BuildBuf.Add(e);
            if (BuildBuf.Count == 0)
                return;

            // Reclaim при масс-сталле (M28-паттерн): денег меньше дешёвого здания
            // и доход не покрывает расход — свободные инженеры идут к ближайшим
            // обломкам (салваж подхватит); стройка подождёт.
            if (ShouldReclaim(hq.factionFunds, EconomyService.MassIncomePerSecond(hq),
                    EconomyService.MassDrainPerSecond(hq)) &&
                SendBuildersToWreckage(st, basePos))
            {
                string fname0 = hq.faction != null ? hq.faction.factionName : "?";
                Log.Info("AI: " + fname0 + " — масс-сталл, инженеры на обломки");
                return;
            }

            int wantPowers = _profile.powersInitial + st.wavesSent * _profile.powersPerWave;
            // Энергосталл с ГИСТЕРЕЗИСОМ (адаптация M28AI maudlin27, CC BY-NC-SA —
            // только идея): вход при стоке < 20% капа, выход при > 50% ИЛИ чистом
            // плюсе дохода — одиночный порог дёргал бы план на каждом колебании.
            if (EconomyService.EnergyActive)
            {
                float cap = EconomyService.EnergyCapacity(hq);
                float ratio = cap > 1f ? EconomyService.EnergyStore(hq) / cap : 1f;
                bool netPositive = EconomyService.EnergyIncomePerSecond(hq) >
                                   EconomyService.EnergyDrainPerSecond(hq);
                if (!st.energyStall && ratio < 0.2f)
                {
                    st.energyStall = true;
                    Log.Info("AI-событие: энергосталл (сток " + (ratio * 100f).ToString("F0") + "%)");
                }
                else if (st.energyStall && (ratio > 0.5f || netPositive && ratio > 0.3f))
                {
                    st.energyStall = false;
                    Log.Info("AI-событие: энергосталл снят (сток " + (ratio * 100f).ToString("F0") + "%)");
                }
            }
            else
                st.energyStall = false;
            // Устойчивость к каталогу карты: если здания вида нет (null) — слот
            // недоступен, план идёт дальше (иначе зависал на слоте навсегда).
            bool hydroBuilt = EconomyService.OwnedCoalGenerators(hq) > 0 ||
                              EconomyService.HydroDefinition == null;
            int extractorsTarget = EconomyService.ExtractorDefinition != null
                ? _profile.extractorsTarget : 0;
            int effPowers = EconomyService.GeneratorDefinition != null ||
                            EconomyService.HydroDefinition != null
                ? powers : wantPowers; // энергозданий нет — слот 3 недоступен
            int slot = PlanSlot(hydroBuilt,
                EconomyService.OwnedExtractors(hq), extractorsTarget,
                depotBuilt, effPowers, wantPowers, st.energyStall);

            BuildingDefinition def;
            EconomyService.DepositSite site = null;
            switch (slot)
            {
                case 0: // гидрокарбонка — на ближайшей ДОСТИЖИМОЙ угольной точке
                    def = EconomyService.HydroDefinition;
                    site = ReachableSiteNear(basePos, EconomyService.DepositKind.Coal, 8000f);
                    break;
                case 1: // экстрактор — на ближайшей ДОСТИЖИМОЙ точке массы
                    def = EconomyService.ExtractorDefinition;
                    site = ReachableSiteNear(basePos, EconomyService.DepositKind.Mass, 12000f);
                    break;
                case 2:
                    def = DepotDefinition();
                    break;
                default:
                    def = EconomyService.GeneratorDefinition;
                    break;
            }
            if (def == null)
                return;

            bool placed;
            if (site != null)
                // Снап-постройка: Place сам привяжет к точке (гейт внутри), зона-чек
                // по маркеру неприменим — «недра» и есть место.
                placed = BuildTasks.Place(def, site.Pos.ToLocalPosition(), BuildBuf, hq);
            else
                placed = TryPlaceNearHq(basePos, def, hq, BuildBuf);
            if (placed)
            {
                string fname = hq.faction != null ? hq.faction.factionName : "?";
                Log.Info("AI: стройка " + def.unitName + " (" + fname + ", слот " + slot + ") — " +
                         BuildBuf.Count + " инженер.");
            }
        }

        /// <summary>Свободный буфер строителей (переиспользуется тиком).</summary>
        private static readonly List<Unit> BuildBuf = new List<Unit>();
        /// <summary>Производство: очередь в депо до maxQueue при запасе массы;
        /// ралли депо — точка сбора между своей базой и вражеской.</summary>
        private static void RunProduction(FactionHQ hq, State st, Vector3 basePos, List<VehicleDepot> depots)
        {
            if (depots.Count == 0)
                return;
            VehicleDepot depot = depots[0];
            if (depot == null)
                return;
            int depotTier = FactoryTierService.TierOf(depot);
            VehicleDefinition tank = TankDefinition(depotTier);
            if (tank == null)
                return;

            GlobalPosition? enemyBase = EnemyBasePosOf(hq);
            if (!ReferenceEquals(st.rallyDepot, depot) && enemyBase != null)
            {
                Vector3 from = basePos;
                Vector3 dir = enemyBase.Value.ToLocalPosition() - from;
                dir.y = 0f;
                Vector3 rally = dir.sqrMagnitude > 1f
                    ? from + dir.normalized * _profile.rallyOffsetM
                    : from + Vector3.forward * _profile.rallyOffsetM;
                rally.y = GameApi.GroundY(rally.x, rally.z);
                ProductionTasks.SetRally(depot, rally, 0);
                st.rallyDepot = depot;
            }

            int queue = ProductionTasks.CountFor(depot);
            // Восполнение инженеров — ПРИОРИТЕТ над танками: погибли все — строить
            // больше некому, экономика замирает до конца матча (§12-49ж закрыт).
            // Заказываем ЛУЧШИЙ тир, который депо производит (T2/T3 с апнутого).
            VehicleDefinition engineer = EngineerDefinition(depotTier);
            if (engineer != null && Engineers.Count < _profile.engineersMin)
            {
                float ecost = engineer.value > 0f ? engineer.value : Settings.BuildDefaultCost;
                if (queue < _profile.maxQueue && hq.factionFunds >= ecost)
                {
                    ProductionTasks.EnqueueGround(depot, engineer);
                    if (Time.time >= st.nextEngineerLogAt)
                    {
                        st.nextEngineerLogAt = Time.time + 30f; // не спамим
                        Log.Info("AI: инженеров " + Engineers.Count + " < " + _profile.engineersMin +
                                 " — заказан новый с депо (T" + depotTier + ")");
                    }
                    return;
                }
            }
            float cost = tank.value > 0f ? tank.value : Settings.BuildDefaultCost;
            if (queue < _profile.maxQueue && hq.factionFunds >= cost * _profile.massReserveFactor)
                ProductionTasks.EnqueueGround(depot, tank);

            // Фаза развития (§2.3а): экономика налажена — апгрейд депо В ОЧЕРЕДЬ
            // (голова, занимает производство — как у игрока, v0.136); следующий
            // тир открывает технику/инженеров повыше (канон SupCom).
            if (FactoryUpgradeWanted(depotTier, ProductionTasks.UpgradeItemOf(depot) != null,
                    hq.factionFunds, NextTierMassOf(depot),
                    Engineers.Count, _profile.engineersMin))
            {
                // HQ-модель (канон FAF): ПЕРВЫЙ апгрейд тира фракции — ШТАБ
                // (иначе дешёвые фабрики без HQ строят только инженеров),
                // последующие — дешёвые версии.
                bool asHq = !FactoryTierService.AnyHq(hq, depotTier + 1);
                ProductionTasks.EnqueueUpgrade(depot, asHq);
                Log.Info("AI-событие: фабрика — апгрейд до T" + (depotTier + 1) +
                         (asHq ? " ШТАБ" : "") + " в очереди");
            }
        }

        /// <summary>Цена массы следующего тира фабрики (0 — максимум).</summary>
        private static float NextTierMassOf(object factory) =>
            FactoryTierService.NextCost(FactoryTierService.TierOf(factory), true,
                out float mass, out _, out _) ? mass : 0f;

        /// <summary>Решение «развитие»: тир фабрики не максимальный, налива нет,
        /// инженеры полным составом, масса ≥ 2× цены тира (запас на стройку).
        /// Чистая — тестируется.</summary>
        internal static bool FactoryUpgradeWanted(int tier, bool upgrading, float funds,
            float nextTierMass, int engineers, int engineersMin) =>
            tier < 3 && !upgrading && engineers >= engineersMin &&
            nextTierMass > 0f && funds >= nextTierMass * 2f;

        /// <summary>Волны: стоящие у сбора боевые сверх гарнизона идут на базу
        /// противника (по достижении нативный CheckObstacles сам ведёт в бой).</summary>
        private static void RunWaves(FactionHQ hq, State st, Vector3 basePos, List<Unit> combat)
        {
            GlobalPosition? enemyBase = EnemyBasePosOf(hq);
            if (enemyBase == null)
                return;
            Vector3 enemyPos = enemyBase.Value.ToLocalPosition();

            WavePool.Clear();
            foreach (Unit unit in combat)
            {
                if (unit == null)
                    continue;
                Vector3 pos = unit.transform.position;
                bool idle = unit.rb != null && unit.rb.velocity.sqrMagnitude < 4f;
                bool nearGather = (pos - basePos).sqrMagnitude <
                                  _profile.gatherRadiusM * _profile.gatherRadiusM;
                if (idle && nearGather)
                    WavePool.Add(unit);
            }

            if (!WaveDue(WavePool.Count, _profile.waveSize, _profile.garrisonSize,
                    Time.time, st.nextWaveAt))
                return;

            // Адаптация Moho PickBestAttackVector/LeastDefended (OpenSupCom, MIT):
            // волна не бьётся в оборону заведомо сильнее себя — при перевесе врага
            // у базы цель переносится на ближайшую вражескую экономику (экстрактор).
            float waveValue = 0f;
            foreach (Unit u in WavePool)
                waveValue += UnitValue(u);
            Vector3 target = enemyPos;
            string targetWhat = "базу";
            float defense = EnemyValueNear(hq, enemyPos, 2200f);
            if (defense > waveValue * 1.5f)
            {
                Unit eco = NearestEnemyExtractor(hq, basePos);
                if (eco != null)
                {
                    target = eco.transform.position;
                    targetWhat = "экономику";
                }
            }

            int sent = 0;
            foreach (Unit unit in WavePool)
            {
                if (WavePool.Count - sent <= _profile.garrisonSize)
                    break; // последние garrisonSize остаются гарнизоном
                unit.GetComponent<UnitCommand>()?.SetDestination(
                    target.ToGlobalPosition(), false);
                sent++;
            }

            if (sent > 0)
            {
                st.wavesSent++;
                st.nextWaveAt = Time.time + _profile.waveIntervalSec;
                Log.Info($"AI: волна {st.wavesSent} — {sent} юнитов (сила {waveValue:F0}) " +
                         $"на {targetWhat} {target.x:F0},{target.z:F0} (оборона врага {defense:F0})");
            }

            // Гарнизон-реакция (адаптация M28AI: защитники базы едут К угрозе,
            // а не стоят на месте): враги в 1800 м от своей базы → стоящие боевые
            // получают приказ на ближайшего, троттл 10 с. Нативный авто-таргетинг
            // включится по прибытии.
            if (Time.time >= st.defendAt && EnemyValueNear(hq, basePos, 1800f) > 0f)
            {
                st.defendAt = Time.time + 10f;
                float threatValue = EnemyValueNear(hq, basePos, 1800f);
                Log.Info("AI-событие: угроза у базы (сила " + threatValue.ToString("F0") +
                         ") — гарнизон выдвигается");
                Unit threat = NearestEnemyUnit(hq, basePos, 1800f);
                if (threat != null)
                    foreach (Unit unit in WavePool)
                        unit.GetComponent<UnitCommand>()?.SetDestination(
                            threat.transform.position.ToGlobalPosition(), false);
            }
        }

        /// <summary>Ближайший к точке живой вражеский юнит (не здание) в радиусе.</summary>
        private static Unit NearestEnemyUnit(FactionHQ of, Vector3 pos, float radius)
        {
            float radiusSq = radius * radius;
            Unit best = null;
            float bestSq = float.MaxValue;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit is Building ||
                    unit.NetworkHQ == null || unit.NetworkHQ == of || AlliedWith(of, unit.NetworkHQ))
                    continue;
                float sq = (unit.transform.position - pos).sqrMagnitude;
                if (sq < radiusSq && sq < bestSq)
                {
                    bestSq = sq;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>Стоимость юнита (value его определения; 100 — неизвестно).</summary>
        private static float UnitValue(Unit unit) =>
            unit != null && unit.definition != null && unit.definition.value > 0f
                ? unit.definition.value
                : 100f;

        /// <summary>Суммарная стоимость живых юнитов-врагов в радиусе от точки —
        /// influence-lite по образцу Moho CInfluenceMap (OpenSupCom: threat —
        /// сумма value в клетке; здесь точечный запрос без сетки).</summary>
        private static float EnemyValueNear(FactionHQ of, Vector3 pos, float radius)
        {
            float radiusSq = radius * radius;
            float sum = 0f;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit is Building ||
                    unit.NetworkHQ == null || unit.NetworkHQ == of || AlliedWith(of, unit.NetworkHQ))
                    continue;
                if ((unit.transform.position - pos).sqrMagnitude > radiusSq)
                    continue;
                sum += UnitValue(unit);
            }
            return sum;
        }

        /// <summary>Ближайший к СВОЕЙ базе экстрактор врага (цель-экономика для
        /// волны при сильной обороне его базы).</summary>
        private static Unit NearestEnemyExtractor(FactionHQ of, Vector3 basePos)
        {
            Unit best = null;
            float bestSq = float.MaxValue;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || !EconomyService.IsExtractor(unit) ||
                    unit.NetworkHQ == null || unit.NetworkHQ == of || AlliedWith(of, unit.NetworkHQ))
                    continue;
                float sq = (unit.transform.position - basePos).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>Кандидаты площадки: веер колец вокруг HQ, первый пригодный —
        /// сушу проверит BuildTasks.Place, занятость юнитами (150 м) — отсюда:
        /// площадка поверх стартовой линии давила спавн (плейтест 2026-10-30).</summary>
        private static bool TryPlaceNearHq(Vector3 center, BuildingDefinition def,
            FactionHQ hq, List<Unit> builders)
        {
            for (int ring = 0; ring < 3; ring++)
            {
                float r = Mathf.Lerp(_profile.buildRadiusMinM, _profile.buildRadiusMaxM, ring / 2f);
                for (int a = 0; a < 8; a++)
                {
                    float ang = a * Mathf.PI * 2f / 8f + ring * 0.3f;
                    Vector3 p = center + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r);
                    p.y = GameApi.GroundY(p.x, p.z);
                    if (BuildTasks.IsZoneBlockedAt(p, builders))
                        continue; // юниты в 150 м — не класть площадку на головы
                    if (BuildTasks.Place(def, p, builders, hq))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Аэродром СВОЕЙ фракции из миссии (GlobalPosition — не протухает
        /// при плавающих сдвигах). null — фракция без базы (не играем). Публично —
        /// орбиту командирского борта строим вокруг той же базы.</summary>
        public static GlobalPosition? BasePosOf(FactionHQ hq)
        {
            string fname = hq.faction != null ? hq.faction.factionName : null;
            Mission mission = MissionManager.CurrentMission;
            if (fname == null || mission?.airbases == null)
                return null;
            foreach (SavedAirbase ab in mission.airbases)
                if (ab != null && ab.faction == fname)
                    return ab.Center;
            return null;
        }

        /// <summary>Аэродром ПРОТИВНИКА: приоритет — база фракции с игроками
        /// (нативная победа AI — захват её аэродрома), фолбэк — любой чужой.</summary>
        private static GlobalPosition? EnemyBasePosOf(FactionHQ of)
        {
            string fname = of.faction != null ? of.faction.factionName : null;
            Mission mission = MissionManager.CurrentMission;
            if (mission?.airbases == null)
                return null;
            GlobalPosition? fallback = null;
            foreach (SavedAirbase ab in mission.airbases)
            {
                if (ab == null || ab.faction == fname)
                    continue;
                FactionRegistry.factionLookup.TryGetValue(ab.faction, out Faction f);
                if (f != null && FactionRegistry.HQLookup.TryGetValue(f, out FactionHQ hq) &&
                    hq.factionPlayers != null && hq.factionPlayers.Count > 0)
                    return ab.Center;
                fallback ??= ab.Center;
            }
            return fallback;
        }

        private static BuildingDefinition _depotDef;

        /// <summary>Депо для стройки: jsonKey профиля либо автоподбор по ключу
        /// «depot» (как генераторы EconomyService).</summary>
        private static BuildingDefinition DepotDefinition()
        {
            if (_depotDef != null)
                return _depotDef;
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia == null || encyclopedia.buildings == null)
                return null;
            if (!string.IsNullOrEmpty(_profile.depotJsonKey))
                foreach (BuildingDefinition b in encyclopedia.buildings)
                    if (b != null && b.jsonKey == _profile.depotJsonKey)
                        return _depotDef = b;
            BuildingDefinition best = null;
            foreach (BuildingDefinition b in encyclopedia.buildings)
            {
                if (b == null)
                    continue;
                string key = b.jsonKey ?? string.Empty;
                if (key.IndexOf("depot", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (best == null || b.value < best.value)
                    best = b;
            }
            return _depotDef = best;
        }

        private static VehicleDefinition _engineerDef;

        /// <summary>Инженер производства: ЛУЧШИЙ тир, который строит фабрика
        /// (клоны T2/T3 — с апнутого депо, §2.3-1), иначе профильный jsonKey или
        /// первый TRUCK. Восполнение: без инженеров экономика AI мертва навсегда
        /// (плейтест 2026-10-31: оба погибли в начале — «мало строил»).</summary>
        public static VehicleDefinition EngineerDefinition(int factoryTier = 3)
        {
            VehicleDefinition clone = EngineerService.BestFor(factoryTier);
            if (clone != null)
                return clone;
            if (_engineerDef != null)
                return _engineerDef;
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia == null || encyclopedia.vehicles == null)
                return null;
            if (!string.IsNullOrEmpty(_profile.engineerJsonKey))
                foreach (VehicleDefinition v in encyclopedia.vehicles)
                    if (v != null && v.jsonKey == _profile.engineerJsonKey)
                        return _engineerDef = v;
            foreach (VehicleDefinition v in encyclopedia.vehicles)
                if (v != null && v.vehicleType == VehicleType.TRUCK)
                    return _engineerDef = v;
            return null;
        }

        /// <summary>Танк производства: ЛУЧШИЙ тир ≤ тира фабрики (после апгрейда
        /// депо AI переводит спам на старшую технику), внутри тира — jsonKey
        /// профиля либо самый дешёвый MBT (фолбэк AFV — зеркально StarterRoster
        /// роли "tank"). Публичен: им же BuildTasks конфигурирует нативные
        /// Factory (плейтест 2026-10-30; там тир не ограничен — maxTier 3).</summary>
        public static VehicleDefinition TankDefinition(int factoryTier = 3)
        {
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia == null || encyclopedia.vehicles == null)
                return null;
            int top = 0; // высший доступный тир кандидатов
            foreach (VehicleDefinition v in encyclopedia.vehicles)
            {
                if (v == null || (v.vehicleType != VehicleType.MBT && v.vehicleType != VehicleType.AFV))
                    continue;
                int tier = UnitRoster.TierOf(v);
                if (tier <= factoryTier && tier > top)
                    top = tier;
            }
            if (!string.IsNullOrEmpty(_profile.tankJsonKey))
                foreach (VehicleDefinition v in encyclopedia.vehicles)
                    if (v != null && v.jsonKey == _profile.tankJsonKey &&
                        UnitRoster.TierOf(v) <= top)
                        return v;
            VehicleDefinition best = null;
            foreach (VehicleDefinition v in encyclopedia.vehicles)
            {
                if (v == null || (v.vehicleType != VehicleType.MBT && v.vehicleType != VehicleType.AFV) ||
                    UnitRoster.TierOf(v) != top)
                    continue;
                if (v.vehicleType == VehicleType.AFV && best != null && best.vehicleType == VehicleType.MBT)
                    continue; // MBT приоритетен, AFV — только если MBT нет
                if (best == null || v.value < best.value)
                    best = v;
            }
            return best;
        }

        // ---------- чистые решения (тестируются раннером) ----------

        /// <summary>План стройки: 0 — генератор, 1 — депо, −1 — ничего.
        /// До депо — powersInitial генераторов, затем депо; после — генераторы
        /// до wantPowers (= powersInitial + волны × powersPerWave).</summary>
        /// <summary>Ближайшая свободная ДОСТИЖИМАЯ точка (экспансия по кластерам:
        /// свой + смежные; прямой аналог M28 land-zone expansions).</summary>
        private static EconomyService.DepositSite ReachableSiteNear(
            Vector3 from, EconomyService.DepositKind kind, float radius)
        {
            EconomyService.DepositSite fallback = null;
            float fallbackSq = float.MaxValue;
            foreach (EconomyService.DepositSite s in EconomyService.SiteList)
            {
                if (s.Kind != kind || !EconomyService.SiteBuildable(s)) // суша-only (§3-9)
                    continue;
                float sq = (s.Pos.ToLocalPosition() - from).sqrMagnitude;
                if (sq > radius * radius)
                    continue;
                if (sq < fallbackSq)
                {
                    fallbackSq = sq;
                    fallback = s;
                }
            }
            if (fallback == null)
                return null;
            // Достижимость: сначала ближайшая; если через обрыв — ищем следующую
            // достижимую (полный перебор мал: точек ≤ 40).
            EconomyService.DepositSite best = null;
            float bestSq = float.MaxValue;
            foreach (EconomyService.DepositSite s in EconomyService.SiteList)
            {
                if (s.Kind != kind || !EconomyService.SiteBuildable(s)) // суша-only (§3-9)
                    continue;
                Vector3 p = s.Pos.ToLocalPosition();
                float sq = (p - from).sqrMagnitude;
                if (sq > radius * radius || sq >= bestSq)
                    continue;
                if (!SiteReachable(from, p))
                    continue;
                best = s;
                bestSq = sq;
            }
            return best ?? fallback; // достижимых нет — берём ближайшую (Shortcut попробует)
        }

        /// <summary>Масс-сталл (M28-канон): денег меньше дешёвого здания,
        /// приток не покрывает текущий расход — стройку выгоднее поменять
        /// на разбор обломков.</summary>
        internal static bool ShouldReclaim(float funds, float incomePerSec, float drainPerSec) =>
            funds < Settings.BuildDefaultCost && incomePerSec <= drainPerSec;

        /// <summary>Направить свободных инженеров к ближайшему обломку (стабильная
        /// цель в State — не дёргаем приказами каждый тик); салваж начнётся,
        /// когда они доедут и остановятся. False — обломков рядом нет.</summary>
        private static bool SendBuildersToWreckage(State st, Vector3 basePos)
        {
            if (st.reclaimPos != null)
            {
                Vector3 rp = st.reclaimPos.Value.ToLocalPosition();
                // цель ещё жива (обломок в радиусе) — инженеры едут/стоят
                WreckBuf.Clear();
                BattlefieldGrid.GetWrecksInRangeNonAlloc(rp.ToGlobalPosition(), 200f, WreckBuf);
                if (WreckBuf.Count > 0)
                    return true;
                st.reclaimPos = null;
            }
            WreckBuf.Clear();
            BattlefieldGrid.GetWrecksInRangeNonAlloc(basePos.ToGlobalPosition(), 4000f, WreckBuf);
            if (WreckBuf.Count == 0)
                return false;
            Wreckage best = null;
            float bestSq = float.MaxValue;
            foreach (Wreckage w in WreckBuf)
            {
                if (w == null) continue;
                float sq = (w.transform.position - basePos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = w; }
            }
            if (best == null)
                return false;
            st.reclaimPos = best.transform.position.ToGlobalPosition();
            foreach (Unit e in BuildBuf)
                e.GetComponent<UnitCommand>()?.SetDestination(st.reclaimPos.Value, false);
            return true;
        }

        private static readonly List<Wreckage> WreckBuf = new List<Wreckage>();

        /// <summary>Достижимость точки экспансии: на спец-карте — свой кластер
        /// или смежные (PassGraph, глубина 2); на стоковой/без графа — всегда.</summary>
        private static bool SiteReachable(Vector3 from, Vector3 to)
        {
            PassGraph pass = RuntimeMap.LastPass;
            Heightmap hm = RuntimeMap.LastHeightmap;
            if (pass == null || hm == null)
                return true;
            int fromCluster = pass.ClusterAt(from.x, from.z, hm.Step);
            int toCluster = pass.ClusterAt(to.x, to.z, hm.Step);
            if (fromCluster < 0 || toCluster < 0)
                return true; // старт вне графа (кромка) — не блокируем
            return pass.Reachable(fromCluster, 2).Contains(toCluster);
        }

        /// <summary>План стройки от точек (v3): 0 — угольный генератор (первым —
        /// бутстрап энергии), 1 — экстрактор на массе (до extractorsTarget), 2 — депо,
        /// 3 — обычный генератор (до wantPowers), −1 — ничего. energyLow (адаптация
        /// Moho GetEconomyStoredRatio): сток < 20% — энергопостройка вне очереди.</summary>
        internal static int PlanSlot(bool coalBuilt, int extractors, int extractorsTarget,
            bool depotBuilt, int powers, int wantPowers, bool energyLow = false)
        {
            if (energyLow)
                return coalBuilt ? 3 : 0;
            if (!coalBuilt)
                return 0;
            if (extractors < Mathf.Max(0, extractorsTarget))
                return 1;
            if (!depotBuilt)
                return 2;
            return powers < wantPowers ? 3 : -1;
        }

        /// <summary>Волна готова: сверх гарнизона есть кому идти, и либо собралось
        /// waveSize, либо истёк интервал.</summary>
        internal static bool WaveDue(int candidates, int waveSize, int garrison, float now, float nextWaveAt)
        {
            if (candidates - garrison <= 0)
                return false;
            return candidates >= waveSize || now >= nextWaveAt;
        }

        /// <summary>Число свободных ЗАСТРОИВАЕМЫХ точек месторождений — суша-only
        /// (для снапшота диагностики; подводные точки не считаются целью).</summary>
        private static int FreeSitesCount()
        {
            int n = 0;
            foreach (EconomyService.DepositSite s in EconomyService.SiteList)
                if (EconomyService.SiteBuildable(s))
                    n++;
            return n;
        }
    }
}
