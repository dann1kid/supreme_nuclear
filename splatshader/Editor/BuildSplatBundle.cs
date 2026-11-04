using UnityEditor;
using UnityEngine;

/// <summary>Сборка splat.bundle (меню: Supreme Nuclear → Build splat.bundle).
/// Проект-заготовка: Unity 2022.3.x (идеально 2022.3.62 — версия игры),
/// шаблон Universal 3D (URP 14 уже в проекте — инклуды шейдера резолвятся).
/// Готовый бандл кладётся в корень проекта — дальше по README в BepInEx.</summary>
public static class BuildSplatBundle
{
    [MenuItem("Supreme Nuclear/Build splat.bundle")]
    public static void Build()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/SplatTerrain.shader");
        if (shader == null)
        {
            Debug.LogError("SplatTerrain.shader не найден в Assets/ — скопируйте его из репо (splatshader/).");
            return;
        }
        if (!shader.isSupported)
        {
            Debug.LogError("SplatTerrain.shader не скомпилировался — смотрите ошибки импорта в консоли.");
            return;
        }
        var build = new AssetBundleBuild
        {
            assetBundleName = "splat.bundle",
            assetNames = new[] { "Assets/SplatTerrain.shader" },
        };
        const string outDir = "SplatBundleOutput";
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
        string src = outDir + "/splat.bundle";
        string dst = "splat.bundle";
        System.IO.File.Copy(src, dst, true);
        Debug.Log("Готово: " + Application.dataPath + "/../splat.bundle (" +
                  new System.IO.FileInfo(dst).Length / 1024 + " КБ) → положите в " +
                  "BepInEx/plugins/SupremeNuclear/ игры.");
    }
}
