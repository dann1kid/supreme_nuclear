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
        public const string KeyPrefix = "SN-Terrain-";

        private static readonly Dictionary<string, MapSettings> Built = new Dictionary<string, MapSettings>();
        private static readonly Dictionary<string, MapDetails> Details = new Dictionary<string, MapDetails>();

        /// <summary>Высоты/граф проходимости последней спец-карты (переносятся
        /// со сценей для AI-экспансии; null на стоковых картах).</summary>
        internal static Heightmap LastHeightmap;
        internal static PassGraph LastPass;

        public static bool IsOurKey(string mapName) =>
            !string.IsNullOrEmpty(mapName) && mapName.StartsWith(KeyPrefix);

        /// <summary>Ключ карты по шаблону и сиду (стабильный формат: hex-сид).</summary>
        public static string MakeKey(string templateName, int seed) =>
            $"{KeyPrefix}{templateName}-{seed & 0xFFFF:X}";

        /// <summary>Построить карту по ключу (если ещё не построена) и зарегистрировать
        /// в MapLoader. Идемпотентно; вызывается генератором и префиксом EnableMap.</summary>
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

            // Ключ: SN-Terrain-<шаблон>-<seed hex>
            string[] parts = mapName.Split('-');
            if (parts.Length < 4 ||
                !int.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out int seed))
            {
                Log.Error("MapGen: не разобрать ключ спец-карты — " + mapName);
                return false;
            }
            ScenarioTemplate template = ScenarioTemplate.Load(parts[2]);
            if (template?.terrain == null)
            {
                Log.Error("MapGen: шаблон '" + parts[2] + "' без секции terrain — спец-карта невозможна");
                return false;
            }

            Heightmap hm = HeightmapGen.Generate(template.terrain, seed);
            LastHeightmap = hm;
            LastPass = PassGraph.Build(hm.Height, hm.W, hm.H, hm.Step, hm.SeaLevel, hm.Step * 0.75f);
            Texture2D colorTex = MapTextures.TerrainColor(hm, template.terrain);
            Sprite mapImage = MapTextures.MapSprite(hm, template.terrain);

            var root = new GameObject(mapName); // 3d-go
            Object.DontDestroyOnLoad(root);
            root.SetActive(false); // исходник: MapSettingsManager.LoadMap инстанцирует копию

            var networkGo = new GameObject("NetworkMap"); // 3d-go
            networkGo.transform.SetParent(root.transform, false);
            NetworkMap networkMap = networkGo.AddComponent<NetworkMap>();
            networkMap.MapPrefix = 0x53; // 'S' — уникальный префикс мода (0 запрещён)
            // FactionHQ — SCENE-объекты карты: Mirage требует prefabHash/sceneId, ручной
            // ServerObjectManager.Spawn динамического GO падал SendSpawnMessage'ом
            // «Empty prefabHash» при рестарте хоста (плейтест 2026-10-29). NetworkMap
            // спавнит их нативно с id (MapPrefix<<24)|index — как у стоковых карт.
            networkMap.NetworkObjects = BuildFactionHQs(root, hq ?? HqFromMission());

            MapSettings settings = root.AddComponent<MapSettings>();
            settings.NetworkMap = networkMap;
            settings.MapSize = new Vector2(template.terrain.sizeM, template.terrain.sizeM);
            settings.GridSizeX = 4;
            settings.GridSizeY = 4;
            settings.Latitude = 35f;
            settings.RoadNetwork = ScriptableObject.CreateInstance<RoadNetworkSO>(); // пустая сеть — юниты напрямую
            settings.SeaLanes = ScriptableObject.CreateInstance<RoadNetworkSO>();
            settings.OceanBasecolor = MapTextures.OceanBase(template.terrain);
            settings.OceanDepthmap = MapTextures.OceanDepth(hm, template.terrain.mountainHeight);
            settings.TerrainColorMap = colorTex;
            settings.MapImage = mapImage;
            settings.CameraPositionRotation = new PositionRotation
            {
                // Карто-локальная точка (GlobalPosition = мир − origin): карта строится
                // до включения в сцену, вычет чужого origin редактора уносил камеру.
                Position = new GlobalPosition(0f, 2500f, -template.terrain.sizeM * 0.3f),
                Rotation = Quaternion.Euler(55f, 0f, 0f),
            };
            // TerrainHeightMap (трава/эффекты) в стоковых картах — компонент префаба;
            // без него NRE в TerrainHeightMapBlocker.Start на каждом юните миссии
            // (плейтест 2026-10-29). Наш рельеф без игровых PhysicMaterial — список пуст.
            TerrainHeightMap heightMap = root.AddComponent<TerrainHeightMap>();
            heightMap.terrainPhysicMaterials = new PhysicMaterial[0];

            TerrainMesh.Build(root.transform, hm, TerrainMesh.MakeMaterial(colorTex));

            var details = ScriptableObject.CreateInstance<MapDetails>();
            details.PrefabName = mapName;
            details.MapName = "SN " + parts[2] + " " + parts[3];
            details.MapImage = mapImage;

            Built[mapName] = settings;
            Details[mapName] = details;
            RegisterInLoader(mapName);
            Log.Info($"MapGen: спец-карта '{mapName}' построена — {hm.W}×{hm.H} клеток по {hm.Step:F0} м, " +
                     $"высоты {hm.Min:F0}..{hm.Max:F0} м");
            return true;
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

        /// <summary>Собрать HQ-объекты в «префабе» карты (root неактивен — Awake и кэш
        /// компонентов Mirage произойдут на клоне после SetActive нативным LoadMap).</summary>
        private static NetworkIdentity[] BuildFactionHQs(
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
