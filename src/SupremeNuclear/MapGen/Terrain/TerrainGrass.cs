using System;
using NuclearOption.Effects;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Нативная GPU-трава на спец-карте (юзер-канон 2026-11-03). ДИАГНОСТИКА
    /// закрыла вопрос: DetailRenderer в СЦЕНАХ НЕТ (0 при живой ванильной
    /// траве) — весь детайл-стек (DetailRenderer + GrassRenderer[] +
    /// TerrainHeightMap + lush-карты) приезжает С ПРЕФАБОМ КАРТЫ. У нашего
    /// префаба его нет — трава невозможна, сколь ни регистрируй. Решение:
    /// клонировать живой стек со сток-префаба в наш root (Instantiate
    /// сохраняет serialize-ссылки на ассеты: шейдеры запекания, lush-текстуры,
    /// внутренние связи); TreeRenderer'ы срезаем (деревья стока стояли бы по
    /// ЧУЖИМ позициям), SceneSingleton.i выставляем на стек-THM явно —
    /// Blocker'ы юнитов начинают регистрироваться правильно.
    /// </summary>
    internal static class TerrainGrass
    {
        /// <summary>В пристроенном стеке есть BlastManager (с mapSettings,
        /// заново нацеленной на НАШ MapSettings) — сплат-материалу можно
        /// включать _BlastStrength: CommandSetup стека выставит глобал
        /// _Global_BlastMap. Сбрасывается в AttachStockStack (транзитная
        /// связка «сборка → ResolvePlan/UpgradeClone», не кэш).</summary>
        public static bool StackBlastReady;
        /// <summary>Найти GO DetailRenderer-стека в сток-префабах карт.</summary>
        private static GameObject FindStockStack()
        {
            foreach (MapSettingsManager manager in
                     UnityEngine.Object.FindObjectsOfType<MapSettingsManager>(true))
            {
                if (manager?.Maps == null)
                    continue;
                foreach (MapSettingsManager.Map entry in manager.Maps)
                {
                    DetailRenderer dr = entry?.Prefab == null
                        ? null
                        : entry.Prefab.GetComponentInChildren<DetailRenderer>(true);
                    if (dr != null)
                        return dr.gameObject;
                }
            }
            return null;
        }

        /// <summary>Пристроить детайл-стек стока в наш префаб (при постройке
        /// карты; root неактивен — Awake отложен до нативного SetActive).
        /// Возвращает true при успехе.</summary>
        public static bool AttachStockStack(GameObject root)
        {
            StackBlastReady = false;
            try
            {
                GameObject stackGo = FindStockStack();
                if (stackGo == null)
                {
                    Log.Info("MapGrass: детайл-стек в сток-префабах не найден — травы не будет");
                    return false;
                }
                GameObject clone = UnityEngine.Object.Instantiate(stackGo, root.transform, false);
                clone.name = "SN-DetailStack";
                // Деревья стока — по чужим позициям: срезаем (наши ставит
                // TerrainTrees с позициями по маскам этого рельефа).
                foreach (TreeRenderer tree in clone.GetComponentsInChildren<TreeRenderer>(true))
                    if (tree != null)
                        UnityEngine.Object.Destroy(tree);
                // BlastManager: mapSettings клонировался СО ССЫЛКОЙ на СТОК-префаб
                // (вне нашей иерархии — remap невозможен), CommandSetup посчитал бы
                // UV воронок по чужому MapSize. Нацеливаем на наш MapSettings —
                // Instantiate клона карты remap'ит ссылку внутрь иерархии сам.
                MapSettings ms = root.GetComponent<MapSettings>();
                int blasts = 0;
                foreach (BlastManager bm in clone.GetComponentsInChildren<BlastManager>(true))
                {
                    if (bm == null)
                        continue;
                    if (ms != null)
                        bm.mapSettings = ms;
                    blasts++;
                }
                StackBlastReady = blasts > 0;
                int grass = clone.GetComponentsInChildren<GrassRenderer>(true).Length;
                Log.Info("MapGrass: детайл-стек стока пристроен (GrassRenderer " + grass +
                         ", деревья срезаны, BlastManager " + blasts + ") из '" + stackGo.name + "'");
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("MapGrass: пристройка детайл-стека не удалась — " +
                         e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>При включении карты: THM СТЕКА объявить SceneSingleton.i
        /// (Blocker'ы юнитов пойдут в него) и зарегистрировать чанк-рендереры
        /// как HeightProvider — окна травы запекутся по нашему рельефу.</summary>
        public static void Register(MapSettings mapSettings)
        {
            if (mapSettings == null)
                return;
            try
            {
                // Стек-THM: child клона, НЕ на корне (там наша NRE-заглушка).
                TerrainHeightMap stackThm = null;
                foreach (TerrainHeightMap thm in mapSettings.GetComponentsInChildren<TerrainHeightMap>(true))
                {
                    if (thm == null || thm.transform == mapSettings.transform)
                        continue;
                    stackThm = thm;
                    break;
                }
                if (stackThm == null)
                {
                    Log.Info("MapGrass: стек-THM на карте нет — трава не заведена");
                    return;
                }

                // i = стек-THM: i-потребители (TerrainHeightMapBlocker юнитов)
                // идут в живой THM стека, а не в заглушку.
                typeof(SceneSingleton<TerrainHeightMap>)
                    .GetField("i", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?.SetValue(null, stackThm);

                PhysicMaterial native = null;
                var names = new System.Text.StringBuilder();
                if (stackThm.terrainPhysicMaterials != null)
                    foreach (PhysicMaterial pm in stackThm.terrainPhysicMaterials)
                    {
                        if (pm == null)
                            continue;
                        names.Append(pm.name).Append(' ');
                        if (native == null)
                            native = pm;
                    }
                Log.Info("MapGrass: стек-THM '" + stackThm.gameObject.name +
                         "', физ-материалы террейна: " + (names.Length > 0 ? names.ToString() : "нет"));

                int fixedColliders = 0, registered = 0;
                foreach (MeshCollider col in mapSettings.GetComponentsInChildren<MeshCollider>(false))
                {
                    if (col == null)
                        continue;
                    if (native != null && col.sharedMaterial != native)
                    {
                        col.sharedMaterial = native;
                        fixedColliders++;
                    }
                    MeshRenderer r = col.GetComponent<MeshRenderer>();
                    if (r == null)
                        continue;
                    stackThm.RegisterObject(new TerrainHeightMap.RenderFilter(r,
                        TerrainHeightMap.SubmeshFilter.All,
                        TerrainHeightMap.EntryType.HeightProvider));
                    registered++;
                }
                Log.Info("MapGrass: рельеф зарегистрирован у стек-THM — " + registered +
                         " чанков (физ-материал заменён у " + fixedColliders + ")");
            }
            catch (Exception e)
            {
                Log.Warn("MapGrass: регистрация рельефа для травы не удалась — " +
                         e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
