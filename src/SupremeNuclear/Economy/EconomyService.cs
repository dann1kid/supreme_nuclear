using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Экономика v2 (host-side, DESIGN §2.2): потоковая модель «масса + энергия».
    /// Масса — нативные factionFunds (непрерывный поток вместо тиков v1), энергия —
    /// модовый сток с капом, наполняемый генераторами (строящиеся здания).
    /// Единая точка налива задач — Drain: дефицит любой валюты пропорционально
    /// замедляет обе (EcoFlow.DrainStep). Апгрейд экстрактора — тоже налив.
    /// Разметка месторождений — из JSON-профиля (ручная) либо авторазметка.
    /// </summary>
    public static class EconomyService
    {
        private sealed class Extractor
        {
            public Unit unit;
            public int tier = 1;
            public UpgradeTask upgrade; // активный налив апгрейда; null — нет
        }

        private sealed class GeneratorRec
        {
            public Unit unit;
            public bool coal; // построен на угольной точке — буст дохода/капа
        }

        /// <summary>Вид месторождения.</summary>
        public enum DepositKind { Mass, Coal }

        /// <summary>Точка месторождения (v3, SupCom-канон): маркер-«недра» на карте,
        /// экстрактор/генератор строятся СНАПОМ на точке; маркер скрывается у всех.
        /// Публичный тип — читают призрак стройки, HUD, AI.</summary>
        public sealed class DepositSite
        {
            public GlobalPosition Pos;
            public int Tier = 1;
            public DepositKind Kind;
            public Unit Marker;     // здание-«недра» (нейтральное, не захватывается)
            public bool Occupied;   // на точке стоит постройка
        }

        private sealed class UpgradeTask
        {
            public FactionHQ hq;      // владелец на старте; смена владельца = сгорание задачи
            public float massNeed;
            public float energyNeed;
            public float massPaid;
            public float energyPaid;
            public float seconds;
            public int toTier;
        }

        private sealed class HqStats
        {
            public float energy;       // сток
            public float smMassDrain;  // сглаженный расход массы (для панели потока)
            public float smEnergyDrain;
            public float frMass;       // налитое за текущий кадр
            public float frEnergy;
        }

        private static readonly List<Extractor> Extractors = new List<Extractor>();
        private static readonly List<GeneratorRec> Generators = new List<GeneratorRec>();
        private static readonly List<DepositSite> Sites = new List<DepositSite>();
        private static readonly Dictionary<FactionHQ, HqStats> Stats = new Dictionary<FactionHQ, HqStats>();
        private static EconomyProfile _profile;
        private static float _retryAt;
        private static bool _gaveUp;          // спавнить не из чего — до смены сцены не ретраим
        private static bool _loggedSceneWait; // «ждём сцену» пишем в лог один раз
        private static BuildingDefinition _generatorDefinition;
        private static BuildingDefinition _hydroDefinition;
        private static BuildingDefinition _extractorDefinition;

        public static bool Running { get; private set; }

        /// <summary>Балансный профиль сцены (null до TryStart) — читают смежные сервисы.</summary>
        public static EconomyProfile Profile => _profile;

        /// <summary>Энергия активна: найдено здание-генератор и профиль его не обнуляет.
        /// При false задачи платят только массу (карта без генераторов — игра не ломается).</summary>
        public static bool EnergyActive { get; private set; }

        public static int TotalExtractors => Extractors.Count;
        public static int TotalGenerators => Generators.Count;

        /// <summary>Т1-генератор (Fuel Depot) — строится где угодно, базовая энергия.</summary>
        public static BuildingDefinition GeneratorDefinition => _generatorDefinition;

        /// <summary>ГИДРОКАРБОНКА (Refinery Structure) — только на угольной точке,
        /// буст энергии (юзер-канон 2026-10-30: отдельные здания гидро/т1ген).</summary>
        public static BuildingDefinition HydroDefinition => _hydroDefinition;

        /// <summary>Определение здания-экстрактора (null до TryStart) — гейт снапа на точки массы.</summary>
        public static BuildingDefinition ExtractorDefinition => _extractorDefinition;

        /// <summary>Все точки месторождений сцены (свободные и занятые).</summary>
        public static IReadOnlyList<DepositSite> SiteList => Sites;

        public static bool IsExtractor(Unit unit) =>
            unit is Building && !string.IsNullOrEmpty(unit.NetworkunitName) &&
            unit.NetworkunitName.StartsWith("Extractor");

        public static bool IsGenerator(Unit unit) =>
            unit is Building && !string.IsNullOrEmpty(unit.NetworkunitName) &&
            unit.NetworkunitName.StartsWith("Generator");

        // ---------- Точки месторождений (v3) ----------

        /// <summary>Маркер ли это «недр» (точки массы/угля) — здания миссии
        /// SN-MES/SN-COAL или авторазметки («Mass n»/«Coal n»).</summary>
        public static bool IsSiteMarker(Unit unit) =>
            unit is Building && !string.IsNullOrEmpty(unit.NetworkUniqueName) &&
            (unit.NetworkUniqueName.StartsWith(ScenarioBuild.DepositMarker) ||
             unit.NetworkUniqueName.StartsWith(CoalMarker));

        /// <summary>Маркер угольной точки (MapGen: SN-COAL-…).</summary>
        public const string CoalMarker = "SN-COAL";

        public static bool SiteIsFree(DepositSite site) =>
            site != null && !site.Occupied &&
            (site.Marker == null || (!site.Marker.disabled && site.Marker.gameObject.activeSelf));

        /// <summary>Ближайшая СВОБОДНАЯ точка указанного вида в радиусе (локальные координаты).</summary>
        public static DepositSite FreeSiteNear(Vector3 local, DepositKind kind, float radius)
        {
            DepositSite best = null;
            float bestSq = radius * radius;
            foreach (DepositSite s in Sites)
            {
                if (s.Kind != kind || !SiteIsFree(s))
                    continue;
                float sq = (s.Pos.ToLocalPosition() - local).sqrMagnitude;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>Занять точку построенным зданием: маркер скрывается у всех
        /// (хост; чистое отключение юнита без обломка), точка уходит из свободных.</summary>
        public static void TakeSite(DepositSite site)
        {
            if (site == null || site.Occupied)
                return;
            site.Occupied = true;
            Unit marker = site.Marker;
            if (marker != null)
            {
                // Тихое скрытие: Building.DisableUnit() запускает нативный collapse —
                // ВЗРЫВ маркера с обломками ровно внутри новой постройки (маркер
                // стоит на той же точке!) — постройка гибла от «внутреннего взрыва»
                // (плейтест 2026-10-30). SetActive глушит Update/collapse без эффектов.
                marker.gameObject.SetActive(false);
                site.Marker = null;
            }
        }

        public static int TierOf(Unit unit)
        {
            foreach (Extractor e in Extractors)
                if (e.unit == unit)
                    return e.tier;
            return 1;
        }

        public static int MaxTier => _profile != null ? _profile.TierCount : 1;

        public static float NextUpgradeMass(Unit unit) =>
            _profile != null ? _profile.NextUpgradeMass(TierOf(unit)) : -1f;

        public static float NextUpgradeEnergy(Unit unit) =>
            _profile != null && EnergyActive ? _profile.NextUpgradeEnergy(TierOf(unit)) : 0f;

        // ---------- Апгрейд экстрактора потоковым наливом ----------

        public static bool UpgradeInProgress(Unit unit)
        {
            foreach (Extractor e in Extractors)
                if (e.unit == unit)
                    return e.upgrade != null;
            return false;
        }

        /// <summary>Прогресс активного апгрейда (0..1 по массе); 0 — если апгрейда нет.</summary>
        public static float UpgradeProgress01(Unit unit)
        {
            foreach (Extractor e in Extractors)
                if (e.unit == unit && e.upgrade != null && e.upgrade.massNeed > 0f)
                    return Mathf.Clamp01(e.upgrade.massPaid / e.upgrade.massNeed);
            return 0f;
        }

        /// <summary>Запуск апгрейда наливом (хост). Деньги не нужны сразу — налив сам
        /// упрётся в дефицит; отказ только если тира выше нет или задача уже идёт.</summary>
        public static bool TryStartUpgrade(FactionHQ hq, Unit unit)
        {
            if (hq == null || !Running)
                return false;
            Extractor found = null;
            foreach (Extractor e in Extractors)
                if (e.unit == unit)
                    found = e;
            if (found == null)
                return false;
            if (found.upgrade != null)
                return false;
            float mass = _profile.NextUpgradeMass(found.tier);
            if (mass < 0f)
            {
                CommanderHud.Notice(Loc.Tr("Экстрактор уже максимального тира"));
                return false;
            }
            found.upgrade = new UpgradeTask
            {
                hq = hq,
                toTier = found.tier + 1,
                massNeed = mass,
                energyNeed = NextUpgradeEnergy(unit),
                seconds = _profile.NextUpgradeSeconds(found.tier),
            };
            CommanderHud.Notice(Loc.F("Экстрактор T{0}: апгрейд начат", found.tier));
            Log.Info($"Economy: апгрейд ({unit.NetworkunitName}) T{found.tier}→T{found.upgrade.toTier} наливом " +
                     $"({mass:F0} массы, {found.upgrade.energyNeed:F0} энергии, {found.upgrade.seconds:F0} с)");
            return true;
        }

        /// <summary>Отмена апгрейда с возвратом уплаченного (хост).</summary>
        public static bool CancelUpgrade(Unit unit)
        {
            if (!Running)
                return false;
            foreach (Extractor e in Extractors)
            {
                if (e.unit != unit || e.upgrade == null)
                    continue;
                FactionHQ hq = e.upgrade.hq;
                if (hq != null)
                {
                    hq.AddFunds(e.upgrade.massPaid);
                    Refund(hq, e.upgrade.energyPaid);
                    CommanderHud.Notice(Loc.F("Апгрейд отменён, возвращено {0}",
                        e.upgrade.massPaid.ToString("F0")));
                }
                Log.Info($"Economy: апгрейд ({unit.NetworkunitName}) отменён, возврат {e.upgrade.massPaid:F0}");
                e.upgrade = null;
                return true;
            }
            return false;
        }

        public static int OwnedExtractors(FactionHQ hq)
        {
            int n = 0;
            foreach (Extractor e in Extractors)
                if (e.unit != null && !e.unit.disabled && e.unit.NetworkHQ == hq)
                    n++;
            return n;
        }

        public static int OwnedGenerators(FactionHQ hq)
        {
            int n = 0;
            foreach (GeneratorRec g in Generators)
                if (g.unit != null && !g.unit.disabled && g.unit.NetworkHQ == hq)
                    n++;
            return n;
        }

        /// <summary>Генераторы фракции НА УГОЛЬНЫХ точках (буст, v3) — план AI.</summary>
        public static int OwnedCoalGenerators(FactionHQ hq)
        {
            int n = 0;
            foreach (GeneratorRec g in Generators)
                if (g.coal && g.unit != null && !g.unit.disabled && g.unit.NetworkHQ == hq)
                    n++;
            return n;
        }

        public static float IncomeRate(int tier) => _profile != null ? _profile.Rate(tier) : 0f;

        /// <summary>Энергозатраты задач: доля от масс-налива (0 — энергия неактивна).</summary>
        public static float EnergyCostFactor => EnergyActive && _profile != null
            ? Mathf.Max(0f, _profile.energyCostFactor)
            : 0f;

        /// <summary>Суммарный поток массы своих живых экстракторов (в секунду).</summary>
        public static float MassIncomePerSecond(FactionHQ hq)
        {
            float sum = 0f;
            foreach (Extractor e in Extractors)
                if (e.unit != null && !e.unit.disabled && e.unit.NetworkHQ == hq)
                    sum += _profile != null ? _profile.Rate(e.tier) : 0f;
            return sum;
        }

        // ---------- Энергия ----------

        /// <summary>Сток энергии фракции (0, если энергия неактивна).</summary>
        public static float EnergyStore(FactionHQ hq) =>
            EnergyActive && hq != null && Stats.TryGetValue(hq, out HqStats s) ? s.energy : 0f;

        public static float EnergyCapacity(FactionHQ hq) =>
            EnergyActive && hq != null && _profile != null
                ? _profile.energyBaseCapacity + GeneratorBonus(hq, _profile.generatorCapacityBonus, _profile.coalCapacityBonus)
                : 0f;

        public static float EnergyIncomePerSecond(FactionHQ hq) =>
            EnergyActive && hq != null && _profile != null
                ? _profile.energyBaseIncome + GeneratorBonus(hq, _profile.generatorIncome, _profile.coalIncomePerSecond)
                : 0f;

        /// <summary>Сумма вкладов живых генераторов фракции: обычные — income,
        /// угольные — coalIncome (построены на угольных точках, v3).</summary>
        private static float GeneratorBonus(FactionHQ hq, float income, float coalIncome)
        {
            float sum = 0f;
            foreach (GeneratorRec g in Generators)
                if (g.unit != null && !g.unit.disabled && g.unit.NetworkHQ == hq)
                    sum += g.coal ? coalIncome : income;
            return sum;
        }

        /// <summary>Сглаженный расход массы задачами фракции (для панели потока).</summary>
        public static float MassDrainPerSecond(FactionHQ hq) =>
            hq != null && Stats.TryGetValue(hq, out HqStats s) ? Mathf.Max(0f, s.smMassDrain) : 0f;

        public static float EnergyDrainPerSecond(FactionHQ hq) =>
            hq != null && Stats.TryGetValue(hq, out HqStats s) ? Mathf.Max(0f, s.smEnergyDrain) : 0f;

        /// <summary>
        /// Единая точка налива задач (хост): списывает массу из factionFunds и энергию
        /// из стока, дефицит режет обе валюты пропорционально (EcoFlow.DrainStep).
        /// При выключенной модели (Running=false) или неактивной энергии льёт только массу.
        /// </summary>
        public static EcoFlow.Paid Drain(FactionHQ hq, float massWant, float energyWant)
        {
            if (hq == null || massWant <= 0f && energyWant <= 0f)
                return new EcoFlow.Paid(0f, 0f);

            if (!Running)
            {
                // Мод-экономика не работает — задачи платят только нативные деньги.
                float pay = Mathf.Min(Mathf.Max(0f, massWant), Mathf.Max(0f, hq.factionFunds));
                if (pay > 0f)
                {
                    hq.AddFunds(-pay);
                    TrackDrain(hq, pay, 0f);
                }
                return new EcoFlow.Paid(pay, 0f);
            }

            float energyAvail = EnergyActive ? EnergyOf(hq) : float.MaxValue;
            EcoFlow.Paid paid = EcoFlow.DrainStep(
                Mathf.Max(0f, massWant), Mathf.Max(0f, energyWant),
                Mathf.Max(0f, hq.factionFunds), energyAvail);
            if (paid.Mass > 0f)
                hq.AddFunds(-paid.Mass);
            if (paid.Energy > 0f && EnergyActive)
                Touch(hq).energy -= paid.Energy;
            TrackDrain(hq, paid.Mass, paid.Energy);
            return paid;
        }

        public static void Reset()
        {
            Running = false;
            EnergyActive = false;
            Extractors.Clear();
            Generators.Clear();
            Sites.Clear();
            Stats.Clear();
            _profile = null;
            _generatorDefinition = null;
            _extractorDefinition = null;
            _retryAt = 0f;
            _gaveUp = false;
            _loggedSceneWait = false;
        }

        public static void Tick()
        {
            if (!GameApi.InMission)
            {
                if (Running)
                    Reset();
                return;
            }
            if (!Settings.EconomyEnabled)
                return;

            // Экономика авторитетна только на хосте (в сингле мы и есть хост).
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;

            if (!Running)
            {
                // Миссия ещё не стартовала — юниты сценария (мексовка SN-MES) спавнятся
                // в StartMission; раньше них авторазметка навалила бы свои экстракторы
                // поверх сценарных (плейтест спец-карты 2026-10-29).
                if (!MissionManager.IsRunning)
                    return;
                if (_gaveUp || Time.time < _retryAt)
                    return;
                _retryAt = Time.time + 5f;
                TryStart();
                return;
            }

            float dt = Time.deltaTime;
            PruneDead();
            PayIncome(dt);
            TickUpgrades(dt);
            SmoothDrains(dt);
        }

        private static void TryStart()
        {
            Spawner spawner = NetworkSceneSingleton<Spawner>.i;
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (spawner == null || encyclopedia == null || encyclopedia.buildings == null || encyclopedia.buildings.Count == 0)
            {
                // Сцена может догружаться — ретраим молча, в лог пишем один раз.
                if (!_loggedSceneWait)
                {
                    _loggedSceneWait = true;
                    Log.Info("Economy: ждём сцену (Spawner/Encyclopedia ещё не готовы)…");
                }
                return;
            }

            Log.Info("Economy: здания карты — " + string.Join(", ", JsonKeys(encyclopedia)));

            BuildingDefinition definition = FindExtractorDefinition(encyclopedia);
            if (definition == null)
            {
                // Зданий нет вовсе — на этой сцене экономика невозможна, не долбим лог каждые 5 с.
                _gaveUp = true;
                Log.Error("Economy: в Encyclopedia нет подходящих зданий — экстрактор спавнить не из чего; " +
                          "экономика отключена до смены сцены");
                return;
            }

            _profile = EconomyProfile.Load(Settings.EconomyProfile);
            _extractorDefinition = definition;

            _generatorDefinition = CloneRole(FindByJsonKey(encyclopedia, "fuel", value => value != definition),
                "Генератор T1", "GEN", Mathf.Max(1f, _profile.generatorBuildCost), ref _genOriginal);
            _hydroDefinition = CloneRole(FindByJsonKey(encyclopedia, "refinery", value => value != definition),
                "Гидрокарбонка", "HYD", Mathf.Max(1f, _profile.hydroBuildCost), ref _hydroOriginal);
            _extractorDefinition = CloneRole(definition, "Экстрактор", "MES",
                Mathf.Max(1f, _profile.extractorBuildCost), ref _extractorOriginal);
            EnergyActive = (_generatorDefinition != null || _hydroDefinition != null) &&
                           _profile.generatorIncome > 0f;
            if (!EnergyActive)
                Log.Warn("Economy: здания энергии не найдены (fuel/refinery) — задачи платят только массу");

            // v3: миссия кладёт МАРКЕРЫ-«недра» (SN-MES/SN-COAL) — регистрируем их
            // как точки; экстракторы/генераторы на точках СТРОЯТСЯ (доход — только
            // с построенных). Свои маркеры на стоковых картах не спавним, если
            // миссия уже расставила свои.
            int registered = RegisterDepositSites();
            if (registered > 0)
            {
                SetupScenarioRunways();
                Running = Sites.Count > 0;
                Log.Info($"Economy: сценарные месторождения — {Sites.Count} точек " +
                         $"(генератор '{_generatorDefinition?.jsonKey ?? "нет"}', " +
                         $"энергия {(EnergyActive ? "активна" : "отключена")})");
                return;
            }

            // Стоковая карта: точки из профиля (ручные) либо авторазметка — спавн
            // маркеров «Mass n»/«Coal n» (нейтральные, не захватываются — застраиваются).
            List<EconomyProfile.Deposit> deposits = ResolveDeposits(_profile);
            int spawned = 0;
            foreach (EconomyProfile.Deposit d in deposits)
            {
                try
                {
                    bool coal = d.kind != null && d.kind.ToLowerInvariant() == "coal";
                    Vector3 local = new Vector3(d.x, GameApi.GroundY(d.x, d.z), d.z);
                    string label = (coal ? "Coal " : "Mass ") + (spawned + 1);
                    Vector3 pos = local + (Vector3)definition.spawnOffset;
                    Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                        definition, pos.ToGlobalPosition(), Quaternion.identity, null, label);
                    if (unit == null)
                    {
                        Log.Warn($"Economy: маркер месторождения не создан в ({d.x:F0}, {d.z:F0})");
                        continue;
                    }
                    unit.NetworkunitName = label;
                    unit.NetworkUniqueName = coal ? CoalMarker + "-" + spawned : ScenarioBuild.DepositMarker + "-" + spawned;
                    Sites.Add(new DepositSite
                    {
                        Pos = local.ToGlobalPosition(),
                        Tier = Mathf.Clamp(d.tier, 1, _profile.TierCount),
                        Kind = coal ? DepositKind.Coal : DepositKind.Mass,
                        Marker = unit,
                    });
                    spawned++;
                }
                catch (System.Exception e)
                {
                    Log.Error($"Economy: сбой спавна маркера ({d.x:F0}, {d.z:F0}): {e.Message}");
                }
            }

            Running = Sites.Count > 0;
            Log.Info($"Economy: запущена — точек {Sites.Count} из {deposits.Count} раскладки; роли-клоны: " +
                     $"экстрактор '{_extractorDefinition?.unitName}', гидро '{_hydroDefinition?.unitName}', " +
                     $"ген '{_generatorDefinition?.unitName}'; энергия {(EnergyActive ? "активна" : "отключена")}");
        }

        /// <summary>Регистрация ПОСТРОЕННОГО экстрактора (BuildTasks.SpawnBuilding):
        /// тир наследуется от точки, имя — маркер «Extractor N» для HUD/миникарты.</summary>
        public static void RegisterExtractor(Unit unit, int tier)
        {
            if (unit == null || IsExtractor(unit))
                return;
            int n = 1;
            while (true)
            {
                bool taken = false;
                foreach (Extractor e in Extractors)
                    if (e.unit != null && e.unit.NetworkunitName == $"Extractor {n}")
                    {
                        taken = true;
                        break;
                    }
                if (!taken)
                    break;
                n++;
            }
            unit.NetworkunitName = $"Extractor {n}";
            Extractors.Add(new Extractor { unit = unit, tier = Mathf.Max(1, tier) });
        }

        /// <summary>Регистрация ПОСТРОЕННОГО генератора; coal=true — на угольной точке
        /// (буст дохода/капа, v3).</summary>
        public static void RegisterGenerator(Unit unit, bool coal = false)
        {
            if (unit == null)
                return;
            foreach (GeneratorRec g in Generators)
                if (g.unit == unit)
                {
                    g.coal = coal;
                    return;
                }
            unit.NetworkunitName = $"Generator {Generators.Count + 1}";
            Generators.Add(new GeneratorRec { unit = unit, coal = coal });
        }

        /// <summary>Поиск здания-мины по jsonKey (паттерны экономике известны);
        /// публично для генератора сценариев (миссионная мексовка тем же зданием).</summary>
        public static BuildingDefinition FindExtractorDefinition(Encyclopedia encyclopedia)
        {
            BuildingDefinition fallback = null;
            foreach (BuildingDefinition b in encyclopedia.buildings)
            {
                if (b == null)
                    continue;
                fallback ??= b;
                string key = b.jsonKey ?? string.Empty;
                if (key.ToLowerInvariant().Contains("mine"))
                    return b;
            }
            foreach (BuildingDefinition b in encyclopedia.buildings)
            {
                string key = b != null && b.jsonKey != null ? b.jsonKey.ToLowerInvariant() : string.Empty;
                if (key.Contains("fuel") || key.Contains("terminal") || key.Contains("exchange"))
                    return b;
            }
            Log.Warn("Economy: здание-шахта не найдено, беру первое попавшееся здание: " +
                     (fallback != null ? fallback.jsonKey : "нет"));
            return fallback;
        }

        /// <summary>Оригиналы-доноры ролей — ИЗЫМАЮТСЯ из каталога (заменяясь
        /// клонами с честными именами; юзер-канон 2026-10-30: «скопировать объекты,
        /// чтобы не было путаницы»).</summary>
        public static bool IsReplacedOriginal(BuildingDefinition def) =>
            def != null && (def == _extractorOriginal || def == _hydroOriginal || def == _genOriginal);

        private static BuildingDefinition _extractorOriginal, _hydroOriginal, _genOriginal;

        /// <summary>Клон определения-роли: свой unitName (в каталоге/мире), бортовой
        /// code и цена Т1 — оригинал остаётся нетронутым для нативных карт/миссий.</summary>
        private static BuildingDefinition CloneRole(BuildingDefinition src, string name, string code,
            float value, ref BuildingDefinition original)
        {
            if (src == null)
                return null;
            original = src;
            var clone = Object.Instantiate(src);
            clone.name = name;
            clone.unitName = name;
            clone.code = code;
            clone.value = value;
            return clone;
        }

        /// <summary>Поиск здания по подстроке jsonKey (регистронезависимо),
        /// с предикатом-исключением (ген/гидро не должны совпасть с экстрактором).</summary>
        private static BuildingDefinition FindByJsonKey(
            Encyclopedia encyclopedia, string pattern, System.Func<BuildingDefinition, bool> except = null)
        {
            if (encyclopedia?.buildings == null || string.IsNullOrEmpty(pattern))
                return null;
            string p = pattern.ToLowerInvariant();
            foreach (BuildingDefinition b in encyclopedia.buildings)
            {
                if (b == null) continue;
                if (except != null && !except(b)) continue;
                string key = b.jsonKey ?? string.Empty;
                if (key.ToLowerInvariant().Contains(p))
                    return b;
            }
            return null;
        }

        private static List<string> JsonKeys(Encyclopedia encyclopedia)
        {
            var keys = new List<string>();
            foreach (BuildingDefinition b in encyclopedia.buildings)
                if (b != null && !string.IsNullOrEmpty(b.jsonKey))
                    keys.Add(b.jsonKey);
            return keys;
        }

        private static List<EconomyProfile.Deposit> ResolveDeposits(EconomyProfile profile)
        {
            var result = new List<EconomyProfile.Deposit>();

            if (profile.deposits != null && profile.deposits.Count > 0)
            {
                foreach (EconomyProfile.Deposit d in profile.deposits)
                {
                    if (IsSiteAcceptable(d.x, d.z, result, profile))
                        result.Add(d);
                    else
                        Log.Warn($"Economy: ручная точка ({d.x:F0}, {d.z:F0}) не прошла проверку — пропущена");
                }
                Log.Info($"Economy: ручная разметка — {result.Count}/{profile.deposits.Count} точек принято");
                return result;
            }

            GameApi.TryGetMapBounds(out Vector2 min, out Vector2 max);
            float cx = (min.x + max.x) * 0.5f;
            float cz = (min.y + max.y) * 0.5f;
            float extent = Mathf.Max(max.x - min.x, max.y - min.y);
            var rng = new System.Random((int)(extent * 7f) + profile.depositCount * 131);

            // Кольца вокруг центра (золотой угол — равномерность без кластеров).
            float[] radii = { 0.18f, 0.28f, 0.38f, 0.46f };
            int target = Mathf.Max(1, profile.depositCount);
            foreach (float ring in radii)
            {
                int attempts = target;
                for (int i = 0; i < attempts && result.Count < target; i++)
                {
                    float angle = i * 2.39996f + ring * 13f + (float)rng.NextDouble() * 0.4f;
                    float r = ring * extent * (0.85f + (float)rng.NextDouble() * 0.3f);
                    float x = cx + Mathf.Cos(angle) * r;
                    float z = cz + Mathf.Sin(angle) * r;
                    if (IsSiteAcceptable(x, z, result, profile))
                        result.Add(new EconomyProfile.Deposit { x = x, z = z });
                }
            }

            // Не хватило — добираем у аэродромов (там всегда ровно и это стратегические точки).
            if (result.Count < target && FactionRegistry.airbaseLookup != null)
            {
                foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
                {
                    if (result.Count >= target)
                        break;
                    if (airbase == null)
                        continue;
                    Vector3 p = airbase.transform.position;
                    for (int i = 0; i < 6 && result.Count < target; i++)
                    {
                        float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                        float r = 1600f + (float)rng.NextDouble() * 1400f;
                        float x = p.x + Mathf.Cos(a) * r;
                        float z = p.z + Mathf.Sin(a) * r;
                        if (IsSiteAcceptable(x, z, result, profile))
                            result.Add(new EconomyProfile.Deposit { x = x, z = z });
                    }
                }
            }

            Log.Info($"Economy: авторазметка — {result.Count} месторождений (цель {target})");
            return result;
        }

        private static bool IsSiteAcceptable(float x, float z, List<EconomyProfile.Deposit> placed, EconomyProfile profile)
        {
            if (GameApi.GroundY(x, z) <= Datum.LocalSeaY + 3f)
                return false; // вода

            const float probe = 70f;
            float h0 = GameApi.GroundY(x, z);
            if (Mathf.Abs(GameApi.GroundY(x + probe, z) - h0) > 25f ||
                Mathf.Abs(GameApi.GroundY(x - probe, z) - h0) > 25f ||
                Mathf.Abs(GameApi.GroundY(x, z + probe) - h0) > 25f ||
                Mathf.Abs(GameApi.GroundY(x, z - probe) - h0) > 25f)
                return false; // склон

            float spacingSq = profile.depositSpacing * profile.depositSpacing;
            foreach (EconomyProfile.Deposit p in placed)
                if ((new Vector2(p.x, p.z) - new Vector2(x, z)).sqrMagnitude < spacingSq)
                    return false;
            return true;
        }

        private static void PruneDead()
        {
            for (int i = Extractors.Count - 1; i >= 0; i--)
                if (Extractors[i].unit == null)
                    Extractors.RemoveAt(i); // апгрейд гибнет вместе со зданием — без возврата
            for (int i = Generators.Count - 1; i >= 0; i--)
                if (Generators[i].unit == null)
                    Generators.RemoveAt(i);
            for (int i = Sites.Count - 1; i >= 0; i--)
            {
                // Занятая точка живёт в реестре (занятость — факт), свободная —
                // умирает вместе с маркером (снесли «недра» обстрелом — точка пропала).
                DepositSite s = Sites[i];
                if (!s.Occupied && s.Marker == null)
                    Sites.RemoveAt(i);
            }
            List<FactionHQ> dead = null;
            foreach (FactionHQ hq in Stats.Keys)
                if (hq == null)
                    (dead ??= new List<FactionHQ>()).Add(hq);
            if (dead != null)
                foreach (FactionHQ hq in dead)
                    Stats.Remove(hq);
        }

        /// <summary>Потоки дохода: масса — каждому владельцу экстракторов, энергия — в сток.</summary>
        private static void PayIncome(float dt)
        {
            foreach (Extractor e in Extractors)
            {
                if (e.unit == null || e.unit.disabled)
                    continue;
                FactionHQ hq = e.unit.NetworkHQ;
                if (hq == null)
                    continue; // нейтральный — не добывается
                Touch(hq);
                hq.AddFunds(_profile.Rate(e.tier) * dt);
            }

            if (EnergyActive)
            {
                foreach (FactionHQ hq in Stats.Keys)
                {
                    // Touch по ходу итерации не меняет словарь: HQ уже есть (масса/генераторы),
                    // а фракции без экстракторов получают сток лениво при первом Drain.
                    HqStats s = Stats[hq];
                    s.energy = EcoFlow.AddCapped(s.energy, EnergyIncomePerSecond(hq) * dt, EnergyCapacity(hq));
                }
            }
        }

        private static void TickUpgrades(float dt)
        {
            foreach (Extractor e in Extractors)
            {
                UpgradeTask u = e.upgrade;
                if (u == null || e.unit == null || e.unit.disabled)
                    continue;
                if (u.hq == null || e.unit.NetworkHQ != u.hq)
                {
                    // Экстрактор захвачен/потерян — задача сгорает без возврата (война).
                    Log.Info($"Economy: апгрейд ({e.unit.NetworkunitName}) прерван — сменился владелец");
                    e.upgrade = null;
                    continue;
                }

                float massWant = Mathf.Min(u.massNeed - u.massPaid, u.massNeed / u.seconds * dt);
                float energyWant = Mathf.Min(u.energyNeed - u.energyPaid, u.energyNeed / u.seconds * dt);
                EcoFlow.Paid paid = Drain(u.hq, massWant, energyWant);
                u.massPaid += paid.Mass;
                u.energyPaid += paid.Energy;

                if (u.massPaid >= u.massNeed - 0.01f && u.energyPaid >= u.energyNeed - 0.01f)
                {
                    e.tier = u.toTier;
                    e.upgrade = null;
                    CommanderHud.Notice(Loc.F("Экстрактор → T{0}", e.tier));
                    Log.Info($"Economy: апгрейд завершён ({e.unit.NetworkunitName}) → T{e.tier}");
                }
            }
        }

        private static void SmoothDrains(float dt)
        {
            float k = 1f - Mathf.Exp(-dt / 2f); // окно сглаживания ~2 с
            foreach (HqStats s in Stats.Values)
            {
                s.smMassDrain += (s.frMass / dt - s.smMassDrain) * k;
                s.smEnergyDrain += (s.frEnergy / dt - s.smEnergyDrain) * k;
                s.frMass = 0f;
                s.frEnergy = 0f;
            }
        }

        private static float EnergyOf(FactionHQ hq) => Touch(hq).energy;

        /// <summary>Возврат энергии в сток фракции (отмена задачи, смерть фабрики).</summary>
        public static void Refund(FactionHQ hq, float amount)
        {
            if (!EnergyActive || hq == null || amount <= 0f)
                return;
            HqStats s = Touch(hq);
            s.energy = EcoFlow.AddCapped(s.energy, amount, EnergyCapacity(hq));
        }

        /// <summary>Отладка (Ctrl+M): заполнить сток энергии до капа; вернуть добавленное.</summary>
        public static float DebugFillEnergy(FactionHQ hq)
        {
            if (!EnergyActive || hq == null)
                return 0f;
            HqStats s = Touch(hq);
            float add = Mathf.Max(0f, EnergyCapacity(hq) - s.energy);
            s.energy += add;
            return add;
        }

        private static void TrackDrain(FactionHQ hq, float mass, float energy)
        {
            HqStats s = Touch(hq);
            s.frMass += mass;
            s.frEnergy += energy;
        }

        /// <summary>Ленивая инициализация статистики HQ: стартовый сток + отключение
        /// нативного фонового дохода (regularIncome утекает игрокам через DistributeFunds).</summary>
        private static HqStats Touch(FactionHQ hq)
        {
            if (!Stats.TryGetValue(hq, out HqStats s))
            {
                s = new HqStats { energy = _profile != null ? _profile.energyStart : 0f };
                Stats[hq] = s;
                if (hq.regularIncome != 0f)
                {
                    hq.regularIncome = 0f; // потоком владеет мод
                    Log.Info("Economy: нативный фоновый доход фракции обнулён (мод владеет потоком)");
                }
            }
            return s;
        }

        /// <summary>Миссионная мексовка генератора сценариев (SN-MES…): регистрируем
        /// уже стоящие capturable-мины как экстракторы; тир — из имени (SN-MES-T2-5).</summary>
        private static int RegisterDepositSites()
        {
            int n = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Building building))
                    continue;
                // Юниты миссии несут сохранённое имя в NetworkUniqueName
                // (NetworkunitName — имя определения; плейтест спец-карты 2026-10-29).
                string name = unit.NetworkUniqueName;
                if (string.IsNullOrEmpty(name))
                    continue;
                bool coal = name.StartsWith(CoalMarker);
                if (!coal && !name.StartsWith(ScenarioBuild.DepositMarker))
                    continue;
                int tier = 1;
                if (!coal)
                {
                    string[] parts = name.Split('-');
                    if (parts.Length >= 3 && parts[2].StartsWith("T") &&
                        int.TryParse(parts[2].Substring(1), out int parsed))
                        tier = parsed;
                }
                // Точка НЕ захватывается (захват — инженерный, v3): гасим нативный
                // цикл у миссий, сгенерированных до v3 (capturable=true в сейве).
                if (building.capturable)
                    building.capturable = false;
                // Точка — ВИРТУАЛЬНАЯ (SupCom-канон, юзер 2026-10-30): модель-«недра»
                // прячем (модель завода вводила в заблуждение), носитель остаётся
                // сетевым юнитом ради NetworkUniqueName. Коллайдер гасим тоже —
                // пусть не мешает физике; видимость — HUD-маркер + 3D-кольцо.
                foreach (Renderer r in building.GetComponentsInChildren<Renderer>())
                    r.enabled = false;
                foreach (Collider c in building.GetComponentsInChildren<Collider>())
                    c.enabled = false;
                Sites.Add(new DepositSite
                {
                    Pos = unit.transform.position.ToGlobalPosition(),
                    Tier = Mathf.Clamp(tier, 1, _profile.TierCount),
                    Kind = coal ? DepositKind.Coal : DepositKind.Mass,
                    Marker = unit,
                });
                n++;
            }
            return n;
        }

        /// <summary>Обвязка сгенерированных баз (SN-BASE…): ровное полотно полосы
        /// (TaxiSurface-терраса, как у модовых ВПП) + доворот съездов 1/3+2/3 —
        /// нативный FromSaved создаёт exit-трансформы без поворота, а подбор съезда
        /// матчит их по forward (без доворота «съезд не найден», урок v0.109).</summary>
        private static void SetupScenarioRunways()
        {
            Mission mission = MissionManager.CurrentMission;
            if (mission == null || mission.airbases == null)
                return;
            foreach (SavedAirbase saved in mission.airbases)
            {
                if (saved == null || string.IsNullOrEmpty(saved.UniqueName) ||
                    !saved.UniqueName.StartsWith(ScenarioBuild.BaseMarker) ||
                    saved.runways == null || saved.runways.Count == 0)
                    continue;
                SavedRunway rwy = saved.runways[0];
                if (rwy == null)
                    continue;
                TaxiSurface.Register(rwy.Start, rwy.End, rwy.Width * 0.9f, graded: true);
                RotateScenarioExits(saved);
            }
        }

        private static void RotateScenarioExits(SavedAirbase saved)
        {
            if (FactionRegistry.airbaseLookup == null ||
                !FactionRegistry.airbaseLookup.TryGetValue(saved.UniqueName, out Airbase airbase) ||
                airbase == null || airbase.runways == null || airbase.runways.Length == 0 ||
                airbase.runways[0].exitPoints == null ||
                airbase.runways[0].exitPoints.Length != 4)
                return;
            Vector3 a = saved.runways[0].Start.ToLocalPosition();
            Vector3 b = saved.runways[0].End.ToLocalPosition();
            Vector3 dir = new Vector3(b.x - a.x, 0f, b.z - a.z);
            if (dir.sqrMagnitude < 1f)
                return;
            Quaternion fwd = Quaternion.LookRotation(dir.normalized, Vector3.up);
            Quaternion back = Quaternion.LookRotation(-dir.normalized, Vector3.up);
            airbase.runways[0].exitPoints[0].rotation = fwd;
            airbase.runways[0].exitPoints[1].rotation = fwd;
            airbase.runways[0].exitPoints[2].rotation = back;
            airbase.runways[0].exitPoints[3].rotation = back;
        }
    }
}
