using System.Collections.Generic;
using Mirage;
using NuclearOption.Effects;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Сборка спец-карты (собственный террейн, рантайм): по ключу
    /// «SN-Terrain-шаблон-сид» строится НЕАКТИВНЫЙ GameObject-«префаб» с MapSettings
    /// + NetworkMap + меш-чанками (DontDestroyOnLoad — исходник переживает смены сцен,
    /// MapSettingsManager.LoadMap инстанцирует его копию). Регистрация: MapLoader.Maps
    /// (SO в памяти) и MapSettingsManager.Maps (пересоздаётся со сценой — вписывает
    /// префикс патча RuntimeMapPatch). Детерминизм: одинаковый шаблон+сид = одинаковый
    /// мир (в MP достаточно одинакового мода). FactionHQ — объекты карты, их спавнит
    /// мод после ApplyMapSettings (MissionManager резолвит фракции до StartMission).
    /// </summary>
    internal static class RuntimeMap
    {
        public const string KeyPrefix = MapGenKey.Prefix;

        private static readonly Dictionary<string, MapSettings> Built = new Dictionary<string, MapSettings>();
        private static readonly Dictionary<string, MapDetails> Details = new Dictionary<string, MapDetails>();

        /// <summary>Высоты/граф проходимости последней спец-карты (переносятся
        /// со сценей для AI-экспансии; null на стоковых картах).</summary>
        internal static Heightmap LastHeightmap;
        internal static PassGraph LastPass;

        /// <summary>Раскладка последней спец-карты (ScenarioWorld: базы/масса/уголь,
        /// согласованная со штампованным рельефом).</summary>
        internal static WorldGen LastWorld;

        /// <summary>Ограничения дрессинга + шаблон/сид последней карты (v0.154):
        /// патч №12 на включении карты кладёт шрамы в BlastManager — само
        /// включение про seed не знает, а Built-мир мог строиться раньше.</summary>
        internal static ScatterPlan.Constraints LastScatter;
        internal static ScenarioTemplate.TerrainSettings LastTerrain;
        internal static int LastSeed;

        public static bool IsOurKey(string mapName) =>
            !string.IsNullOrEmpty(mapName) && mapName.StartsWith(KeyPrefix);

        /// <summary>Ключ карты по шаблону и сиду (формат и разбор — MapGenKey,
        /// чистая логика с тестами). Сид — ПОЛНЫЕ 32 бита (X8): усечение до 16 бит
        /// строило при загрузке мир от другого сида — раскладка миссии (базы/точки)
        /// висела «в небе» и тонула (плейтест 2026-11-01). Хеш морфологии (v3):
        /// правка JSON-шаблона меняет мир — один и тот же ключ не должен накрывать
        /// два разных мира (коллизия в реестре Built, тот же урок). Игроки (v0.143,
        /// Скримиш): players масштабирует базы, но НЕ входит в MorphHash — суффикс
        /// «-pN» в ключе разводит миры 1×1/2×2 и даёт загрузчику точное значение.
        /// Старые ключи (4-симв. сид, без хеша) парсятся как раньше.</summary>
        public static string MakeKey(string templateName, int seed,
            ScenarioTemplate.TerrainSettings terrain = null, int? players = null, int? sizeK = null) =>
            MapGenKey.Build(templateName, seed, terrain?.MorphHash(), sizeK, players);

        /// <summary>Построить карту по ключу (если ещё не построена) и зарегистрировать
        /// в MapLoader. Идемпотентно; вызывается генератором и префиксом EnableMap.
        /// СИНХРОННЫЙ драйвер: сливает все ступени MapBuildJob за один вызов
        /// (поведение прежнее — кадр стоит); асинхронный — MapGenAsync через тот же
        /// job с бюджетом времени на кадр.</summary>
        public static bool EnsureRegistered(string mapName,
            List<KeyValuePair<string, Vector3>> hq = null)
        {
            if (!IsOurKey(mapName))
                return false;
            if (Built.ContainsKey(mapName))
            {
                RegisterInLoader(mapName);
                return true;
            }
            var job = new MapBuildJob(mapName, hq, null, null);
            foreach (float _ in job.Steps())
            {
            }
            if (job.Success)
                RegisterInLoader(mapName);
            return job.Success;
        }

        /// <summary>Карта уже построена (кэш Built)? — без пересборки.</summary>
        public static bool IsBuilt(string mapName) =>
            IsOurKey(mapName) && Built.ContainsKey(mapName);

        /// <summary>Опубликовать «последний мир» (статики для AI/дрессинга/лофанов)
        /// — общая ступень обоих драйверов сборки.</summary>
        internal static void PublishLast(WorldGen world, int seed, ScenarioTemplate.TerrainSettings t)
        {
            Heightmap hm = world.Hm;
            LastHeightmap = hm;
            LastWorld = world;
            LastPass = PassGraph.Build(hm.Height, hm.W, hm.H, hm.Step, hm.SeaLevel, hm.Step * 0.75f);
            LastScatter = ScatterPlan.From(hm, world, t);
            LastTerrain = t;
            LastSeed = seed;
        }

        /// <summary>Вписать MapDetails в MapLoader.Maps (SO в памяти живёт всегда).</summary>
        private static void RegisterInLoader(string mapName)
        {
            if (!Details.TryGetValue(mapName, out MapDetails details))
                return;
            foreach (MapLoader loader in Resources.FindObjectsOfTypeAll<MapLoader>())
            {
                if (System.Array.Exists(loader.Maps, d => d != null && d.PrefabName == mapName))
                    continue;
                var list = new List<MapDetails>(loader.Maps ?? new MapDetails[0]) { details };
                loader.Maps = list.ToArray();
            }
        }

        /// <summary>Вписать запись в MapSettingsManager.Maps (менеджер пересоздаётся
        /// при каждой загрузке GameWorld-сцены — зовёт префикс патча).</summary>
        public static bool RegisterInManager(MapSettingsManager manager, string mapName)
        {
            if (!Built.TryGetValue(mapName, out MapSettings settings) ||
                !Details.TryGetValue(mapName, out MapDetails details))
                return false;
            if (manager.Maps != null)
                foreach (MapSettingsManager.Map m in manager.Maps)
                    if (m?.Details != null && m.Details.PrefabName == mapName)
                        return true;
            var list = new List<MapSettingsManager.Map>(manager.Maps ?? new MapSettingsManager.Map[0])
            {
                new MapSettingsManager.Map { Details = details, Prefab = settings },
            };
            manager.Maps = list.ToArray();
            return true;
        }

        /// <summary>Регистрация собранной карты + вытеснение старых (кэш Built
        /// живёт сессию; рост без границ съедал память — cap 4, вытесненный корень
        /// уничтожается, при обращении карта пересоберётся честным путём).</summary>
        private const int BuiltCap = 4;
        private static readonly List<string> BuiltOrder = new List<string>();

        internal static void RegisterBuilt(string mapName, MapSettings settings, MapDetails details)
        {
            Built[mapName] = settings;
            Details[mapName] = details;
            // РЕГИСТРАЦИЯ В ЛОАДЕРЕ ОБЯЗАТЕЛЬНО (плейтест 2026-11-06 «после
            // генерации остаётся старая карта»): редактор грузит карту через
            // MapLoader.TryLoad НАПРЯМУЮ (без EnableMap) — без записи в
            // MapLoader.Maps он «Could not find GameWorldPrefab» и остаётся
            // на стоке. Стартовая ветка Скримиша не падала лишь потому, что
            // EnableMap-префикс регистрирует позже.
            RegisterInLoader(mapName);
            BuiltOrder.Remove(mapName);
            BuiltOrder.Add(mapName);
            while (BuiltOrder.Count > BuiltCap)
            {
                string old = BuiltOrder[0];
                BuiltOrder.RemoveAt(0);
                if (old == mapName || !Built.TryGetValue(old, out MapSettings oldSettings))
                    continue;
                Built.Remove(old);
                Details.Remove(old);
                if (oldSettings != null)
                {
                    Object.Destroy(oldSettings.gameObject); // неактивный исходник-префаб
                    Log.Info("MapGen: вытеснена из кэша карта — " + old);
                }
            }
        }

        /// <summary>Собрать HQ-объекты в «префабе» карты (root неактивен — Awake и кэш
        /// компонентов Mirage произойдут на клоне после SetActive нативным LoadMap).</summary>
        internal static NetworkIdentity[] BuildHqs(
            GameObject root, List<KeyValuePair<string, Vector3>> hqs)
        {
            var identities = new List<NetworkIdentity>();
            if (hqs == null)
                return identities.ToArray();
            foreach (KeyValuePair<string, Vector3> kv in hqs)
            {
                Faction faction = FindFaction(kv.Key);
                if (faction == null)
                {
                    Log.Warn("MapGen: фракция '" + kv.Key + "' не найдена в ростре — HQ пропущен");
                    continue;
                }
                var go = new GameObject("SN-HQ-" + kv.Key); // 3d-go
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = kv.Value; // root парентится к Datum.origin
                go.AddComponent<NetworkIdentity>();
                FactionHQ f = go.AddComponent<FactionHQ>();
                f.faction = faction;
                identities.Add(go.GetComponent<NetworkIdentity>());
            }
            if (identities.Count > 0)
                Log.Info("MapGen: HQ в составе карты — " + identities.Count + " шт.");
            return identities.ToArray();
        }

        /// <summary>Фракции/позиции штабов из миссии (запуск сохранёнки из меню).</summary>
        private static List<KeyValuePair<string, Vector3>> HqFromMission()
        {
            Mission mission = MissionManager.CurrentMission;
            if (mission == null)
                return null;
            var result = new List<KeyValuePair<string, Vector3>>();
            foreach (MissionFaction mf in mission.factions)
            {
                if (mf == null || string.IsNullOrEmpty(mf.factionName))
                    continue;
                Vector3 pos = new Vector3(0f, 0f, 0f);
                foreach (SavedAirbase ab in mission.airbases)
                    if (ab != null && ab.faction == mf.factionName)
                    {
                        pos = ab.Center.ToLocalPosition();
                        break;
                    }
                result.Add(new KeyValuePair<string, Vector3>(mf.factionName, pos));
            }
            return result;
        }

        private static Faction FindFaction(string factionName)
        {
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia == null || encyclopedia.factions == null)
                return null;
            foreach (Faction f in encyclopedia.factions)
                if (f != null && f.factionName == factionName)
                    return f;
            return null;
        }

    }
}
