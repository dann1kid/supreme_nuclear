using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Экономика v1 (host-side): месторождения на текущей карте + нейтральные
    /// захватываемые экстракторы + тиковый доход в factionFunds.
    /// Разметка — из JSON-профиля (ручная) либо авторазметка по ровным площадкам.
    /// Тиры заложены в данные (доход T1/T2/T3), апгрейд-UI — следующая итерация.
    /// </summary>
    public static class EconomyService
    {
        private sealed class Extractor
        {
            public Unit unit;
            public int tier = 1;
        }

        private static readonly List<Extractor> Extractors = new List<Extractor>();
        private static EconomyProfile _profile;
        private static float _nextIncome;
        private static float _retryAt;
        private static bool _gaveUp;          // спавнить не из чего — до смены сцены не ретраим
        private static bool _loggedSceneWait; // «ждём сцену» пишем в лог один раз

        public static bool Running { get; private set; }
        public static int TotalExtractors => Extractors.Count;

        public static bool IsExtractor(Unit unit) =>
            unit is Building && !string.IsNullOrEmpty(unit.NetworkunitName) &&
            unit.NetworkunitName.StartsWith("Extractor");

        public static int TierOf(Unit unit)
        {
            foreach (Extractor e in Extractors)
                if (e.unit == unit)
                    return e.tier;
            return 1;
        }

        public static int MaxTier => _profile != null && _profile.incomePerMinute != null
            ? _profile.incomePerMinute.Length
            : 1;

        public static float NextUpgradeCost(Unit unit)
        {
            if (_profile?.tierUpgradeCost == null)
                return -1f;
            int tier = TierOf(unit);
            if (tier >= MaxTier || tier >= _profile.tierUpgradeCost.Length)
                return -1f;
            return _profile.tierUpgradeCost[tier];
        }

        /// <summary>Апгрейд тира за деньги фракции (хост).</summary>
        public static bool TryUpgrade(FactionHQ hq, Unit unit)
        {
            if (hq == null || !Running)
                return false;
            Extractor found = null;
            foreach (Extractor e in Extractors)
                if (e.unit == unit)
                    found = e;
            if (found == null)
                return false;
            float cost = NextUpgradeCost(unit);
            if (cost < 0f)
            {
                CommanderHud.Notice(Loc.Tr("Экстрактор уже максимального тира"));
                return false;
            }
            if (hq.factionFunds < cost)
            {
                CommanderHud.Notice(Loc.F("Не хватает денег: апгрейд — {0}", cost.ToString("F0")));
                return false;
            }
            hq.AddFunds(-cost);
            found.tier++;
            CommanderHud.Notice(Loc.F("Экстрактор → T{0} за {1}", found.tier, cost.ToString("F0")));
            Log.Info($"Economy: upgrade ({unit.NetworkunitName}) → T{found.tier} за {cost:F0}");
            return true;
        }

        public static int OwnedExtractors(FactionHQ hq)
        {
            int n = 0;
            foreach (Extractor e in Extractors)
                if (e.unit != null && !e.unit.disabled && e.unit.NetworkHQ == hq)
                    n++;
            return n;
        }

        public static float IncomeRate(int tier) => _profile != null ? _profile.Rate(tier) : 0f;

        public static float IncomePerMinute(FactionHQ hq)
        {
            float sum = 0f;
            foreach (Extractor e in Extractors)
                if (e.unit != null && !e.unit.disabled && e.unit.NetworkHQ == hq)
                    sum += _profile != null ? _profile.Rate(e.tier) : 0f;
            return sum;
        }

        public static void Reset()
        {
            Running = false;
            Extractors.Clear();
            _profile = null;
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
                if (_gaveUp || Time.time < _retryAt)
                    return;
                _retryAt = Time.time + 5f;
                TryStart();
                return;
            }

            if (Time.time >= _nextIncome)
            {
                _nextIncome = Time.time + Mathf.Max(1f, _profile.tickSeconds);
                PayIncome();
            }
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

            List<Vector2> deposits = ResolveDeposits(_profile);
            int spawned = 0;
            foreach (Vector2 d in deposits)
            {
                try
                {
                    Vector3 local = new Vector3(d.x, GameApi.GroundY(d.x, d.y), d.y);
                    Vector3 pos = local + (Vector3)definition.spawnOffset;
                    string label = $"Extractor {spawned + 1}";
                    Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                        definition, pos.ToGlobalPosition(), Quaternion.identity, null, label);
                    if (unit == null)
                    {
                        Log.Warn($"Economy: экстрактор не создан в ({d.x:F0}, {d.y:F0})");
                        continue;
                    }
                    unit.NetworkunitName = label; // маркеры ищут экстракторы по этому имени
                    if (unit is Building building)
                    {
                        building.capturable = true;
                        // Нативный цикл захвата стартует на инициализации только если флаг уже
                        // стоял; мы спавним со стока-false — запускаем цикл вручную.
                        StartNativeCapture(building);
                        Log.Info($"Economy: {label} на ({d.x:F0}, {d.y:F0}), радиус захвата ≈ {unit.maxRadius * 2f:F0} м");
                    }
                    Extractors.Add(new Extractor { unit = unit });
                    spawned++;
                }
                catch (System.Exception e)
                {
                    Log.Error($"Economy: сбой спавна экстрактора ({d.x:F0}, {d.y:F0}): {e.Message}");
                }
            }

            Running = Extractors.Count > 0;
            Log.Info($"Economy: запущена — экстракторов {Extractors.Count} из {deposits.Count} точек, здание '{definition.jsonKey}', тик {_profile.tickSeconds:F0} с");
        }

        private static void StartNativeCapture(Building building)
        {
            try
            {
                MethodInfo method = typeof(Building).GetMethod("CheckForCapture",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null)
                {
                    Log.Error("Economy: Building.CheckForCapture не найден — захват недоступен");
                    return;
                }
                method.Invoke(building, null); // async-метод стартует при вызове; результат не ждём
            }
            catch (System.Exception e)
            {
                Log.Error("Economy: не удалось запустить захват — " + e.Message);
            }
        }

        private static BuildingDefinition FindExtractorDefinition(Encyclopedia encyclopedia)
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

        private static List<Vector2> ResolveDeposits(EconomyProfile profile)
        {
            var result = new List<Vector2>();

            if (profile.deposits != null && profile.deposits.Count > 0)
            {
                foreach (EconomyProfile.Deposit d in profile.deposits)
                {
                    if (IsSiteAcceptable(d.x, d.z, result, profile))
                        result.Add(new Vector2(d.x, d.z));
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
                        result.Add(new Vector2(x, z));
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
                            result.Add(new Vector2(x, z));
                    }
                }
            }

            Log.Info($"Economy: авторазметка — {result.Count} месторождений (цель {target})");
            return result;
        }

        private static bool IsSiteAcceptable(float x, float z, List<Vector2> placed, EconomyProfile profile)
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
            foreach (Vector2 p in placed)
                if ((p - new Vector2(x, z)).sqrMagnitude < spacingSq)
                    return false;
            return true;
        }

        private static void PayIncome()
        {
            for (int i = Extractors.Count - 1; i >= 0; i--)
            {
                Extractor e = Extractors[i];
                if (e.unit == null)
                {
                    Extractors.RemoveAt(i);
                    continue;
                }
                if (e.unit.disabled)
                    continue;
                FactionHQ hq = e.unit.NetworkHQ;
                if (hq == null)
                    continue; // нейтральный — не добывается
                hq.AddFunds(_profile.Rate(e.tier) * _profile.tickSeconds / 60f);
            }
        }
    }
}
