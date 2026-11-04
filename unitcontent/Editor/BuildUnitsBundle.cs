using UnityEditor;
using UnityEngine;

/// <summary>Сборка units.bundle (меню: Supreme Nuclear → Build units.bundle).
/// Проект-заготовка: Unity 2022.3.x (идеально 2022.3.62 — версия игры),
/// шаблон Universal 3D — тот же, что для splat.bundle (см. splatshader/README).
/// Ассеты — GameObject-наборы пропсов с именами-ключами SN_* (см. README):
/// корневой GameObject называется ТОЧНО как ключ (SN_ENG_T2_LAND и т.д.),
/// дети — любые меши в локальных координатах донора.</summary>
public static class BuildUnitsBundle
{
    [MenuItem("Supreme Nuclear/Build units.bundle")]
    public static void Build()
    {
        // Ключи-конвенция мода (EngineerService.Decorate): расширяемо — добавь
        // сюда свои SN_* и клади одноимённые GameObject в Assets/unitcontent/.
        string[] keys =
        {
            "Assets/unitcontent/SN_ENG_T2_LAND.prefab",
            "Assets/unitcontent/SN_ENG_T2_SEA.prefab",
            "Assets/unitcontent/SN_ENG_T3_LAND.prefab",
            "Assets/unitcontent/SN_ENG_T3_SEA.prefab",
        };
        var assets = new System.Collections.Generic.List<string>();
        foreach (string key in keys)
            if (AssetDatabase.LoadAssetAtPath<GameObject>(key) != null)
                assets.Add(key);
        if (assets.Count == 0)
        {
            Debug.LogWarning("Ни одного набора пропсов не найдено в Assets/unitcontent/ — " +
                             "бандл собирать нечего (мод продолжит работать на тинте).");
            return;
        }
        var build = new AssetBundleBuild
        {
            assetBundleName = "units.bundle",
            assetNames = assets.ToArray(),
        };
        const string outDir = "UnitsBundleOutput";
        if (!System.IO.Directory.Exists(outDir))
            System.IO.Directory.CreateDirectory(outDir);
        var manifest = BuildPipeline.BuildAssetBundles(outDir, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        if (manifest == null || manifest.GetAllAssetBundles().Length == 0)
        {
            Debug.LogError("Сборка бандла не удалась — смотрите лог консоли.");
            return;
        }
        string src = outDir + "/units.bundle";
        string dst = "units.bundle";
        System.IO.File.Copy(src, dst, true);
        Debug.Log("Готово: " + Application.dataPath + "/../units.bundle (" +
                  new System.IO.FileInfo(dst).Length / 1024 + " КБ, " + assets.Count +
                  " наборов) → положите в BepInEx/plugins/SupremeNuclear/ игры.");
    }
}
