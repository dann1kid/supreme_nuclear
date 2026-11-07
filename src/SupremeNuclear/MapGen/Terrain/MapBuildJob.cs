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
    /// Ступенчатая сборка спец-карты (v0.154, лобби «Скримиша»): ЕДИНЫЙ пайплайн
    /// старого EnsureRegistered, нарезанный на ступени-итератор. Драйвера два:
    /// синхронный (EnsureRegistered — слив всех ступеней, поведение прежнее)
    /// и бюджетный (MapGenAsync — ступени размазываются по кадрам с лимитом
    /// времени, прогресс — для панели лобби). Асинхронный путь приносит с
    /// воркера готовый мир + пиксели (MapPixelBundle — чистая математика),
    /// синхронный считает их здесь же, как раньше. Порядок операций не менялся:
    /// детерминизм мира и вид карты совпадают с прежними сборками.
    /// </summary>
    internal sealed class MapBuildJob
    {
        private readonly string _mapName;
        private readonly List<KeyValuePair<string, Vector3>> _hq; // null → HqFromMission
        private readonly WorldGen _world;         // prebuilt (воркер) или null (считать самому)
        private readonly MapPixelBundle _bundle;  // prebuilt (воркер) или null

        private ScenarioTemplate _template;
        private int _seed;
        private Heightmap _hm;
        private WorldGen _worldUsed;
        private Texture2D _colorTex, _albedo, _oceanBase, _oceanDepth;
        private Sprite _mapImage;
        private GameObject _root;
        private TerrainMaterial.Plan _plan;
        private int _chunkCursor, _chunkCount;
        private int _chunksPerCall;

        public bool Success;
        public string Error;

        public MapBuildJob(string mapName, List<KeyValuePair<string, Vector3>> hq,
            WorldGen prebuiltWorld, MapPixelBundle prebuiltPixels)
        {
            _mapName = mapName;
            _hq = hq;
            _world = prebuiltWorld;
            _bundle = prebuiltPixels;
        }

        /// <summary>Ступени сборки, 0..1 после каждой; исключений не бросает —
        /// срыв кладётся в Error/Success. Драйвер ограничивает время кадра,
        /// всегда продвигая хотя бы одну ступень.</summary>
        public IEnumerable<float> Steps()
        {
            // ---- 1. Ключ → шаблон → мир (0.00..0.10) ----
            ScenarioTemplate.TerrainSettings t;
            if (_world != null)
            {
                _worldUsed = _world;
                _hm = _world.Hm;
                _template = null; // заполняется ниже из шаблона задачи
                t = null;
                // Шаблон воркер уже загрузил — здесь он нужен для настроек;
                // имя шаблона берём из ключа (параллельно проверяем разбор).
                if (!MapGenKey.TryParse(_mapName, out MapGenKey.Parsed parsed))
                {
                    Error = "не разобрать ключ спец-карты — " + _mapName;
                    Log.Error("MapGen: " + Error);
                    yield break;
                }
                _template = ScenarioTemplate.Load(parsed.Template);
                if (_template?.terrain == null)
                {
                    Error = "шаблон '" + parsed.Template + "' без секции terrain";
                    Log.Error("MapGen: " + Error);
                    yield break;
                }
                // Согласование с ключом (воркер получал тот же players, но источник
                // истины — ключ: файл шаблона мог уйти вперёд/назад).
                if (parsed.Players != null && parsed.Players.Value != _template.players)
                    _template.players = parsed.Players.Value;
                _seed = parsed.Seed;
            }
            else
            {
                if (!MapGenKey.TryParse(_mapName, out MapGenKey.Parsed parsed))
                {
                    Error = "не разобрать ключ спец-карты — " + _mapName;
                    Log.Error("MapGen: " + Error);
                    yield break;
                }
                _template = ScenarioTemplate.Load(parsed.Template);
                if (_template?.terrain == null)
                {
                    Error = "шаблон '" + parsed.Template + "' без секции terrain";
                    Log.Error("MapGen: " + Error);
                    yield break;
                }
                if (parsed.Players != null && parsed.Players.Value != _template.players)
                    _template.players = parsed.Players.Value;
                _seed = parsed.Seed;
                // ЕДИНЫЙ детерминированный пайплайн (как и при генерации в
                // ScenarioBuild): выбор точек по маскам → ШТАМПЫ ровных площадок
                // → пересчёт масок — иначе раскладка миссии разошлась бы с мешом.
                _worldUsed = ScenarioWorld.Generate(_template, _seed);
                _hm = _worldUsed.Hm;
            }
            t = _template.terrain;
            RuntimeMap.PublishLast(_worldUsed, _seed, t);
            yield return 0.10f;

            // ---- 2. Пиксели: цветовая карта, albedo, миникарта (0.10..0.27) ----
            MapPixelBundle bundle = _bundle ?? MapTextures.ComputePixels(
                _hm, t, MapTextures.AlbedoResOf(t));
            // Превью/карту/цветовую красим из ОДНОГО буфера 1024² (как раньше:
            // RenderPixels кэшировал макро — здесь буфер приходит готовым).
            MapTextures.PrimeMacroCache(bundle.Map1024);
            _colorTex = MapTextures.TexFrom(bundle.Map1024);
            yield return 0.16f;
            _albedo = MapTextures.AlbedoTexture(bundle.Albedo, bundle.AlbedoRes);
            yield return 0.27f;

            // ---- 3. Миникарта + океан (0.27..0.34) ----
            _mapImage = MapTextures.MapSpriteFromPixels(bundle.Map1024);
            _oceanBase = MapTextures.OceanBase(t);
            _oceanDepth = MapTextures.OceanDepth(_hm, Mathf.Max(300f, -t.oceanFloorM));
            yield return 0.34f;

            // ---- 4. Корень: NetworkMap/HQ/MapSettings/детайл-стек (0.34..0.42) ----
            BuildRoot();
            yield return 0.42f;

            // ---- 5. Дрессинг: деревья + декали (0.42..0.50) ----
            TerrainTrees.Build(_root, _hm, _worldUsed, t, _seed);
            TerrainDressing.BuildDecals(_root, _hm, _worldUsed, t, _seed);
            yield return 0.50f;

            // ---- 6. Материал/сплат (0.50..0.53) ----
            TerrainMaterial.RememberAlbedo(_albedo);
            MapTextures.RememberSplatSource(_hm, t, _worldUsed);
            _plan = TerrainMaterial.ResolvePlan(_albedo, _albedo, t.sizeM, 1f);
            yield return 0.53f;

            // ---- 7. Меш-чанки (0.53..0.98) — по одному за ступень ----
            _chunkCount = TerrainMesh.ChunkCount(_hm);
            _chunksPerCall = Mathf.Max(1, _chunkCount);
            while (_chunkCursor < _chunksPerCall)
            {
                int cj = _chunkCursor / ChunksPerSideOf();
                int ci = _chunkCursor % ChunksPerSideOf();
                TerrainMesh.BuildChunkAt(_root.transform, _hm, _plan.Mat, _plan.UvTileM,
                    _plan.UseVertexColor
                        ? (x, z, h) => MapTextures.VertexColorZone(_hm, t, x, z)
                        : (System.Func<float, float, float, Color>)null,
                    ci, cj);
                _chunkCursor++;
                yield return 0.53f + 0.45f * _chunkCursor / _chunksPerCall;
            }

            // ---- 8. Регистрация (0.98..1.00) ----
            var details = ScriptableObject.CreateInstance<MapDetails>();
            details.PrefabName = _mapName;
            details.MapName = "SN " + MapNameOf(_mapName) + " " + _seed.ToString("X8");
            details.MapImage = _mapImage;
            RuntimeMap.RegisterBuilt(_mapName, _root.GetComponent<MapSettings>(), details);
            Log.Info($"MapGen: спец-карта '{_mapName}' построена — {_hm.W}×{_hm.H} клеток по {_hm.Step:F0} м, " +
                     $"высоты {_hm.Min:F0}..{_hm.Max:F0} м");
            Success = true;
            yield return 1f;
        }

        private int ChunksPerSideOf() => TerrainMesh.ChunksPerSide;

        private static string MapNameOf(string key)
        {
            // Для details.MapName — имя шаблона (парсинг уже случился в ступени 1;
            // повторный разбор дешёв и локален).
            return MapGenKey.TryParse(key, out MapGenKey.Parsed p) ? p.Template : key;
        }

        /// <summary>Корень карты: NetworkMap + HQ + MapSettings + заглушка высот
        /// + детайл-стек стока (бывшее тело EnsureRegistered, без изменений).</summary>
        private void BuildRoot()
        {
            ScenarioTemplate.TerrainSettings t = _template.terrain;
            _root = new GameObject(_mapName); // 3d-go
            Object.DontDestroyOnLoad(_root);
            _root.SetActive(false); // исходник: MapSettingsManager.LoadMap инстанцирует копию

            var networkGo = new GameObject("NetworkMap"); // 3d-go
            networkGo.transform.SetParent(_root.transform, false);
            NetworkMap networkMap = networkGo.AddComponent<NetworkMap>();
            networkMap.MapPrefix = 0x53; // 'S' — уникальный префикс мода (0 запрещён)
            // FactionHQ — SCENE-объекты карты: Mirage требует prefabHash/sceneId, ручной
            // ServerObjectManager.Spawn динамического GO падал SendSpawnMessage'ом
            // «Empty prefabHash» при рестарте хоста (плейтест 2026-10-29). NetworkMap
            // спавнит их нативно с id (MapPrefix<<24)|index — как у стоковых карт.
            networkMap.NetworkObjects = RuntimeMap.BuildHqs(_root, _hq);

            MapSettings settings = _root.AddComponent<MapSettings>();
            settings.NetworkMap = networkMap;
            settings.MapSize = new Vector2(t.sizeM, t.sizeM);
            settings.GridSizeX = 4;
            settings.GridSizeY = 4;
            settings.Latitude = 35f;
            settings.RoadNetwork = ScriptableObject.CreateInstance<RoadNetworkSO>(); // пустая сеть — юниты напрямую
            settings.SeaLanes = ScriptableObject.CreateInstance<RoadNetworkSO>();
            settings.OceanBasecolor = _oceanBase;
            settings.OceanDepthmap = _oceanDepth;
            settings.TerrainColorMap = _colorTex;
            settings.MapImage = _mapImage;
            settings.CameraPositionRotation = new PositionRotation
            {
                // Карто-локальная точка (GlobalPosition = мир − origin): карта строится
                // до включения в сцену, вычет чужого origin редактора уносил камеру.
                Position = new GlobalPosition(0f, 2500f, -t.sizeM * 0.3f),
                Rotation = Quaternion.Euler(55f, 0f, 0f),
            };
            // TerrainHeightMap-заглушка (NRE-фикс TerrainHeightMapBlocker в
            // редакторе) + ДЕТАЙЛ-СТЕК СТОКА: DetailRenderer/GrassRenderer/THM
            // приезжают с ПРЕФАБОМ карты (диагностика 2026-11-03: в сценах их 0
            // при живой ванильной траве) — клонируем живой стек, трава получает
            // конвейер; деревья стока срезаны (чужие позиции).
            TerrainHeightMap heightMap = _root.AddComponent<TerrainHeightMap>();
            heightMap.terrainPhysicMaterials = new PhysicMaterial[0];
            TerrainGrass.AttachStockStack(_root);
        }
    }

    /// <summary>Пиксельный срез генерации — ЧИСТАЯ математика без Unity-объектов:
    /// считается на воркере MapGenAsync (или синхронно в MapBuildJob). Буферы
    /// отдаются главному потоку, который делает из них текстуры.</summary>
    internal sealed class MapPixelBundle
    {
        /// <summary>Зонная окраска 1024² (seabed=false): превью панели + TerrainColor + MapImage.</summary>
        public Color32[] Map1024;
        /// <summary>Пиксели albedo (полное разрешение AlbedoRes).</summary>
        public Color32[] Albedo;
        public int AlbedoRes;
    }
}
