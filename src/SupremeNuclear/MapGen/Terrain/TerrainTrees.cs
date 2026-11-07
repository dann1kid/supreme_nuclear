using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.Effects;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Деревья на спец-карте (v0.154, канон «детали Neroxis»): модель/материалы/
    /// compute-шейдеры — СТОКОВЫЕ (шаблон ищется в DetailRenderer.treeRenderers
    /// сток-префабов), ПОЗИЦИИ — наши, из масок рельефа (ScatterPlan.Trees:
    /// лесные кластеры на равнинах, вне площадок/коридоров/пляжей/снега,
    /// симметрия карты, детерминизм сида). Стоковые деревья срезаны
    /// (TerrainGrass — они стояли бы по ЧУЖИМ позициям).
    ///
    /// Позиции TreeRenderer ест через TextAsset с сырыми байтами (ScatterLoad:
    /// 12 байт = Vector3). Бинарный TextAsset в рантайме не собрать (конструктор
    /// только string, UTF-8 не байт-прозрачен) — ставим ASCII-заглушку ТОЧНОЙ
    /// длины N*12 (байты ASCII = UTF-8 1:1, count сойдётся), а реальное
    /// содержимое подкладывает патч №14 (TreePositionsPatch): после нативного
    /// CommandSetup (буферы/группы посчитаны по N) — GraphicsBuffer.SetData
    /// реальных позиций. Вписывание в DetailRenderer.treeRenderers — ДО
    /// активации карты (Start строит argBuffer по длинам массивов).
    /// </summary>
    [HarmonyPatch]
    internal static class TerrainTrees
    {
        /// <summary>Ожидающие подмены позиции: ключ — instanceId TreeRenderer.
        /// Транзит «сборка карты → CommandSetup клона»; без потребления записи
        /// остаются до следующей сборки (сотни КБ максимум — терпимо).</summary>
        internal static readonly Dictionary<int, Vector3[]> Pending =
            new Dictionary<int, Vector3[]>();

        private static bool _noTemplate; // сток-шаблон не нашёлся — больше не ищем

        /// <summary>Построить деревья в детайл-стеке карты (root ещё неактивен).
        /// Вызывается из RuntimeMap.EnsureRegistered сразу после AttachStockStack.</summary>
        public static void Build(GameObject root, Heightmap hm, WorldGen world,
            ScenarioTemplate.TerrainSettings t, int seed)
        {
            try
            {
                if (root == null || hm == null || t == null || t.treeDensity <= 0f)
                    return;
                Transform stack = root.transform.Find("SN-DetailStack");
                DetailRenderer dr = stack == null
                    ? null
                    : stack.GetComponentInChildren<DetailRenderer>(true);
                if (dr == null)
                {
                    Log.Info("MapTrees: детайл-стека нет — деревья не ставятся");
                    return;
                }
                TreeRenderer template = FindStockTemplate();
                if (template == null)
                {
                    if (!_noTemplate)
                        Log.Info("MapTrees: стоковый TreeRenderer (меш/материал/compute) не найден — деревья недоступны");
                    _noTemplate = true;
                    return;
                }

                ScatterPlan.Constraints c = ScatterPlan.From(hm, world, t);
                List<ScatterPlan.TreeSpot> spots = ScatterPlan.Trees(c, t, seed);
                if (spots.Count == 0)
                {
                    Log.Info("MapTrees: планировщик не дал ни одной позиции — деревья пропущены");
                    return;
                }

                // Позиции: глобальные XZ + точная высота меша (+2 м — в грунт не втыкаться).
                var positions = new Vector3[spots.Count];
                for (int i = 0; i < spots.Count; i++)
                    positions[i] = new Vector3(spots[i].X, hm.SampleHeight(spots[i].X, spots[i].Z) + 2f,
                        spots[i].Z);

                var go = new GameObject("SN-Trees"); // 3d-go
                go.transform.SetParent(stack, false);
                TreeRenderer tr = go.AddComponent<TreeRenderer>();
                // Конфиг — сплошной клон стока (публичные поля напрямую,
                // приватные serialize — рефлексией): дерево выглядит нативно,
                // воронки его вырубают, lush/высотный тон работают как в игре.
                tr.PositionData = new TextAsset(new string('x', positions.Length * 12))
                {
                    name = "SN-TreePositions", // ASCII: bytes.Length == N*12 (канон ScatterLoad)
                };
                tr.LushMap = template.LushMap;
                tr.AltitudeRange = template.AltitudeRange;
                tr.AltIntensity = template.AltIntensity;
                tr.LushIntensity = template.LushIntensity;
                tr.LargeNoiseScale = template.LargeNoiseScale;
                tr.NoiseSmallScale = template.NoiseSmallScale;
                tr.BlastIntensity = template.BlastIntensity;
                tr.VisibleBufferSizePercent = template.VisibleBufferSizePercent;
                CopyPrivate(template, tr, "mesh", "material", "viewRadius", "meshRadius",
                    "clearTreeRadiusMultiplier", "treeColorCompute", "clearShader");

                // Вписать в очередь DetailRenderer ДО активации (Start читает
                // массивы для размеров argBuffer; мусорные null стока долой).
                var list = new List<TreeRenderer>();
                TreeRenderer[] existing = (TreeRenderer[])AccessTools
                    .Field(typeof(DetailRenderer), "treeRenderers").GetValue(dr);
                if (existing != null)
                    foreach (TreeRenderer e in existing)
                        if (e != null)
                            list.Add(e);
                list.Add(tr);
                AccessTools.Field(typeof(DetailRenderer), "treeRenderers")
                    .SetValue(dr, list.ToArray());

                Pending[tr.GetInstanceID()] = positions;
                Log.Info($"MapTrees: деревьев запланировано {positions.Length} " +
                         $"(шаблон '{template.gameObject.name}', плотность {t.treeDensity:F2})");
            }
            catch (System.Exception e)
            {
                Log.Warn("MapTrees: деревья не построились — " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void CopyPrivate(TreeRenderer from, TreeRenderer to, params string[] fields)
        {
            foreach (string f in fields)
                AccessTools.Field(typeof(TreeRenderer), f)?.SetValue(to,
                    AccessTools.Field(typeof(TreeRenderer), f)?.GetValue(from));
        }

        /// <summary>Стоковый TreeRenderer с полным набором ассетов — из префабов
        /// карт (как TerrainGrass ищет стек). Возвращённый компонент живёт в
        /// сток-префабе — используется только здесь и сейчас (клонирование полей).</summary>
        private static TreeRenderer FindStockTemplate()
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
                    if (dr == null)
                        continue;
                    TreeRenderer[] arr = (TreeRenderer[])AccessTools
                        .Field(typeof(DetailRenderer), "treeRenderers").GetValue(dr);
                    if (arr == null)
                        continue;
                    foreach (TreeRenderer tr in arr)
                    {
                        if (tr == null)
                            continue;
                        Mesh mesh = (Mesh)AccessTools.Field(typeof(TreeRenderer), "mesh")?.GetValue(tr);
                        Material mat = (Material)AccessTools.Field(typeof(TreeRenderer), "material")?.GetValue(tr);
                        ComputeShader color = (ComputeShader)AccessTools.Field(typeof(TreeRenderer), "treeColorCompute")?.GetValue(tr);
                        ComputeShader clear = (ComputeShader)AccessTools.Field(typeof(TreeRenderer), "clearShader")?.GetValue(tr);
                        if (mesh != null && mat != null && color != null && clear != null &&
                            tr.LushMap != null)
                            return tr;
                    }
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Патч №14 (реестр — ARCHITECTURE §6): свои позиции деревьев. Нативный
    /// CommandSetup строит буферы/группы по длине TextAsset (наша ASCII-заглушка
    /// той же длины), содержимое подменяем SetData — композиция невозможна:
    /// буфер создаётся внутри (композиция — крайняя мера по правилу 3).
    /// </summary>
    [HarmonyPatch(typeof(TreeRenderer), nameof(TreeRenderer.CommandSetup))]
    internal static class TreePositionsPatch
    {
        [HarmonyPostfix]
        private static void AfterCommandSetup(TreeRenderer __instance)
        {
            try
            {
                int id = __instance.GetInstanceID();
                if (!TerrainTrees.Pending.TryGetValue(id, out Vector3[] positions))
                    return;
                var buffer = (GraphicsBuffer)AccessTools
                    .Field(typeof(TreeRenderer), "treePositionBuffer").GetValue(__instance);
                if (buffer == null)
                    return;
                buffer.SetData(positions);
                TerrainTrees.Pending.Remove(id);
                Log.Info("MapTrees: буфер позиций подменён — " + positions.Length + " деревьев");
            }
            catch (System.Exception e)
            {
                Log.Warn("MapTrees: подмена буфера не удалась — " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
