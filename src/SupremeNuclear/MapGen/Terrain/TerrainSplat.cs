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
        private static AssetBundle _bundle;   // живёт весь процесс — ассеты фото-страт
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
                _bundle = AssetBundle.LoadFromFile(path);
                AssetBundle bundle = _bundle;
                if (bundle == null)
                {
                    // Нативная причина в Player.log («not compatible with … runtime»
                    // = ревизия редактора ≠ ревизии игры, см. splatshader/README).
                    _failReason = "LoadFromFile = null — бандл не принят рантаймом "
                                  + Application.unityVersion + " (собран другой ревизией?)";
                    return;
                }
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

        /// <summary>Фото-страты CC0 Polyhaven в бандле (grass/sand/rock/snow,
        /// 2K jpg): загружаются вместо процедурных серых тайлов, нормализуются
        /// к средней яркости 0.86 — шейдер умножает macro × tile как раньше.
        /// Отсутствие файла — фолбэк на процедурные тайлы (MapTextures).</summary>
        private static readonly string[] PhotoAssets =
        {
            "Assets/Textures/grass_ground_diff_2k.jpg",
            "Assets/Textures/coast_sand_03_diff_2k.jpg",
            "Assets/Textures/aerial_rocks_02_diff_2k.jpg",
            "Assets/Textures/snow_02_diff_2k.jpg",
        };

        private static Texture2D[] _photoTiles;
        private static bool _photoTried;

        /// <summary>Загрузить фото-страты из бандла; null — нет (фолбэк).
        /// Нормализация: средняя яркость → 0.86, света ≤ 1.0 (множительная
        /// семантика серых тайлов; значения «сырые» — импортёр ставит
        /// sRGB=false, см. BuildSplatBundle).</summary>
        private static Texture2D[] PhotoTiles()
        {
            if (_photoTried)
                return _photoTiles;
            _photoTried = true;
            try
            {
                if (_bundle == null)
                {
                    Log.Info("MapGen: фото-страты недоступны (бандл не загружен) — процедурные тайлы");
                    return null;
                }
                var bundle = _bundle;
                const float target = 0.86f; // канон серых тайлов — единая
                // формула macro×tile на всех дистанциях (blend удалён v0.155.1)
                var tiles = new Texture2D[4];
                for (int k = 0; k < 4; k++)
                {
                    var t = bundle.LoadAsset<Texture2D>(PhotoAssets[k]);
                    if (t == null)
                    {
                        Log.Info("MapGen: фото-страта " + PhotoAssets[k] + " нет в бандле — процедурные тайлы");
                        return null;
                    }
                    tiles[k] = NormalizeMultiplier(t, target);
                }
                _photoTiles = tiles;
                Log.Info("MapGen: фото-страты Polyhaven (CC0) нормализованы — 4×2K");
                return tiles;
            }
            catch (System.Exception e)
            {
                Log.Warn("MapGen: фото-страты не загрузились (" + e.GetType().Name + ": " +
                         e.Message + ") — процедурные тайлы");
                return null;
            }
        }

        private static Texture2D NormalizeMultiplier(Texture2D src, float target)
        {
            var px = src.GetPixels32();
            double sum = 0;
            int max = 0;
            foreach (var c in px)
            {
                int l = (c.r + c.g + c.b) / 3;
                sum += l;
                if (l > max)
                    max = l;
            }
            float mean = (float)(sum / px.Length / 255.0);
            float scale = 0.86f / Mathf.Max(0.05f, mean);
            float scaleMax = 250f / Mathf.Max(1, max); // света ≤ ~0.98
            if (scale > scaleMax)
                scale = scaleMax;
            // Контраст ×1.9: фото мягче процедурного шума (жалоба «детальность
            // потеряна», 2026-11-06); разброс растёт, средняя 0.86 сохраняется.
            const float Contrast = 1.6f; // 1.9 клиповало светлые фото (снег) в плоское белое
            for (int i = 0; i < px.Length; i++)
            {
                float r = 0.86f + (px[i].r * scale - 0.86f) * Contrast;
                float g = 0.86f + (px[i].g * scale - 0.86f) * Contrast;
                float b = 0.86f + (px[i].b * scale - 0.86f) * Contrast;
                px[i].r = (byte)Mathf.Clamp(r, 40f, 255f);
                px[i].g = (byte)Mathf.Clamp(g, 40f, 255f);
                px[i].b = (byte)Mathf.Clamp(b, 40f, 255f);
            }
            src.SetPixels32(px);
            src.Apply(true, true); // мипы пересобрать, CPU-пиксели больше не нужны
            return src;
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
                Texture2D[] tiles = PhotoTiles() ?? MapTextures.SplatTiles();
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
                m.SetFloat("_TileMetres", 3f);      // период детальной фактуры (v0.155: 4→3 м, мельче)
                if (m.HasProperty("_Tile2Metres"))
                    m.SetFloat("_Tile2Metres", 48f); // вторая октава (v0.154, крупные поля)
                if (m.HasProperty("_Tile3Metres"))
                    m.SetFloat("_Tile3Metres", 12f); // третья октава (средняя, «низкое разрешение»)
                if (m.HasProperty("_BumpScale"))
                    m.SetFloat("_BumpScale", 1.6f);  // рельеф нормали заметнее у земли
                // Воронки/выжженное (_Global_BlastMap): включаем только когда
                // детайл-стек с BlastManager пристроен — глобал ставит его
                // CommandSetup; без него сэмпл глобала был бы пуст (тёмный пол).
                if (m.HasProperty("_BlastStrength") && TerrainGrass.StackBlastReady)
                    m.SetFloat("_BlastStrength", 1f);
                // Приём теней — эксперимент за настройкой (каст остаётся off).
                if (Settings.TerrainShadows)
                    m.EnableKeyword("_SN_RECEIVE_SHADOWS");
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
