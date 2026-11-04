using System.IO;
using BepInEx;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Тайловый сплат-материал рельефа (канон SupCom/Neroxis, юзер-канон
    /// 2026-11-03): МАСКА зон (низкое разрешение на всю карту) + ТАЙЛОВЫЕ
    /// детальные текстуры (период ~метры), смешивает шейдер. Снимает потолок
    /// «м/тексель» навсегда: деталь не зависит от размера карты.
    ///
    /// Шейдер нельзя скомпилировать в рантайме — он приезжает ASSET-БАНДЛОМ
    /// (сборка в Unity Editor 2022.3.x, см. splatshader/ в корне репо; игра =
    /// 2022.3.62, URP 14). Файл: BepInEx/plugins/SupremeNuclear/splat.bundle.
    /// Бандла нет/шейдер не поддержан — тихий фолбэк на гибрид «по карте
    /// высот» (TerrainMaterial.ResolvePlan), мод полностью работоспособен.
    ///
    /// Схема смешивания: hue зон несёт НАШ albedo (макро, 16384²), тайлы —
    /// оттеночная деталь (grayscale-шум ~1-4 м) ПОД маской видов поверхности:
    /// трава/песок/скала/снег. Сэмплятся всё мировыми координатами (как Field
    /// — его меш живёт без UV), floating origin компенсирует _Datum_OriginPosition.
    /// </summary>
    internal static class TerrainSplat
    {
        private static Shader _shader;
        private static bool _tried;
        private static string _failReason;

        public static bool Ready
        {
            get
            {
                if (!_tried)
                    TryLoad();
                return _shader != null;
            }
        }

        private static void TryLoad()
        {
            _tried = true;
            try
            {
                string path = Path.Combine(Paths.PluginPath, "SupremeNuclear", "splat.bundle");
                if (!File.Exists(path))
                {
                    _failReason = "файл не найден: " + path;
                    return;
                }
                AssetBundle bundle = AssetBundle.LoadFromFile(path);
                _shader = bundle.LoadAsset<Shader>("Assets/SplatTerrain.shader");
                if (_shader == null)
                {
                    _failReason = "шейдер SplatTerrain в бандле нет";
                    return;
                }
                if (!_shader.isSupported)
                {
                    _failReason = "шейдер не поддержан (SRP/GPU): " + _shader.name;
                    _shader = null;
                    return;
                }
                Log.Info("MapGen: сплат-шейдер загружен — " + _shader.name +
                         " (бандл " + new FileInfo(path).Length / 1024 + " КБ)");
            }
            catch (System.Exception e)
            {
                _failReason = e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>Материал сплата: albedo-макро + маска видов + 4 тайла +
        /// нормал-деталь (ванильная field_n, если найдена). null — фолбэк.</summary>
        public static Material TryCreateMaterial(Texture2D macroAlbedo, float sizeM,
            Texture detailNormal)
        {
            if (!Ready)
            {
                Log.Info("MapGen: сплат недоступен (" + _failReason + ") — гибрид");
                return null;
            }
            try
            {
                Texture2D mask = MapTextures.SplatMask();
                Texture2D[] tiles = MapTextures.SplatTiles();
                if (mask == null || tiles == null || tiles.Length != 4)
                {
                    Log.Warn("MapGen: маска/тайлы сплата не готовы — гибрид");
                    return null;
                }
                var m = new Material(_shader) { name = "SN-TerrainSplat" };
                m.SetTexture("_MacroMap", macroAlbedo);
                m.SetFloat("_MacroScale", sizeM); // макро/маска — 0..1 на габарит карты
                m.SetTexture("_MaskMap", mask);
                m.SetTexture("_Tile0", tiles[0]);
                m.SetTexture("_Tile1", tiles[1]);
                m.SetTexture("_Tile2", tiles[2]);
                m.SetTexture("_Tile3", tiles[3]);
                m.SetFloat("_TileMetres", 4f);      // период детальной фактуры
                if (detailNormal != null && m.HasProperty("_BumpMap"))
                {
                    m.SetTexture("_BumpMap", detailNormal);
                    m.SetFloat("_BumpMetres", 1f);  // field_n — ванильный 1 м
                }
                Log.Info("MapGen: материал рельефа — СПЛАТ: макро " +
                         (macroAlbedo != null ? macroAlbedo.width + "²" : "?") +
                         ", маска " + mask.width + "², тайлы " + tiles[0].width + "²×4 (" +
                         (detailNormal != null ? "нормаль " + detailNormal.name : "без нормали") + ")");
                return m;
            }
            catch (System.Exception e)
            {
                Log.Warn("MapGen: сборка сплат-материала не удалась (" +
                         e.GetType().Name + ": " + e.Message + ") — гибрид");
                return null;
            }
        }
    }
}
