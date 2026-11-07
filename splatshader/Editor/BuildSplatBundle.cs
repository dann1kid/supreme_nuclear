using UnityEditor;
using UnityEngine;

/// <summary>Сборка splat.bundle (меню: Supreme Nuclear → Build splat.bundle;
/// batch: -executeMethod BuildSplatBundle.Build).
/// В бандле: шейдер + 4 фото-страты CC0 Polyhaven (гравий/песок/скалы/снег).
/// Импортёр страт: readable (мод нормализует яркость на загрузке),
/// sRGB=false (множительная семантика серых тайлов — сырые значения).</summary>
public static class BuildSplatBundle
{
    [MenuItem("Supreme Nuclear/Build splat.bundle")]
    public static void Build()
    {
        // ОБЯЗАТЕЛЬНО перед сборкой: в batchmode активный таргет может быть
        // неинициализирован — тогда BuildPipeline считает модуль AssetBundle
        // «disabled in the build» и НЕ ПИШЕТ объект-контейнер в бандл; такой
        // файл LoadFromFile отвергает («not compatible», ловушка 2026-11-05).
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone,
            BuildTarget.StandaloneWindows64);
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
        var assetNames = new System.Collections.Generic.List<string> { "Assets/SplatTerrain.shader" };
        foreach (string jpg in System.IO.Directory.GetFiles("Assets/Textures", "*.jpg"))
        {
            string path = jpg.Replace("\\", "/");
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            if (imp != null)
            {
                imp.isReadable = true;       // мод нормализует пиксели на загрузке
                imp.sRGBTexture = false;     // множитель — сырые значения
                imp.mipmapEnabled = true;
                // БЕЗ СЖАТИЯ: SetPixels32 на DXT-формате бросает
                // ArgumentException («unsupported format», плейтест 2026-11-06
                // — фото откатились на серые тайлы). 1K RGBA32 = 4 МБ/шт.
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.maxTextureSize = 1024;
                imp.SaveAndReimport();
            }
            assetNames.Add(path);
        }
        var build = new AssetBundleBuild
        {
            assetBundleName = "splat.bundle",
            assetNames = assetNames.ToArray(),
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
        System.IO.File.Copy(outDir + "/splat.bundle", "splat.bundle", true);
        Debug.Log("Готово: " + Application.dataPath + "/../splat.bundle (" +
                  new System.IO.FileInfo("splat.bundle").Length / 1024 + " КБ) → положите в " +
                  "BepInEx/plugins/SupremeNuclear/ игры.");
    }
}
