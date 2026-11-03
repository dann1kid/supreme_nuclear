using System;
using System.Threading.Tasks;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// CPU-текстуры спец-карты из heightmap — ЗОННАЯ окраска v3 (пляж/скалы/
    /// мелководье/снег/макро-пятна), разрешение НЕ зависит от сетки высот
    /// (v2 красила 1 тексель на клетку 200 м — «размазанно», плейтест 2026-11-02).
    /// TerrainColorMap ОБЯЗАН оставаться readable: MapSettings.GetTerrainColorAtCoordinate
    /// делает GetPixel по uv = ((x + size/2)/size, (z + size/2)/size) — пиксель
    /// (px, py) красится от мировой точки ((px+0.5)/res·size − size/2, …) без flip.
    /// </summary>
    internal static class MapTextures
    {
        private const int ColorMapRes = 1024; // TerrainColorMap (CPU-подбор цвета юнитов)
        private const int MacroRes = 1024;    // макро-поле зон для albedo (дорогие расчёты)
        private const int AlbedoRes = 12288;  // дефолт albedo (6.7 м/тексель на 82 км;
                                              // вблизи «так себе» при 8192 — плейтест 2026-11-03;
                                              // ручка terrain.albedoRes, потолок 16384)
        private const int DepthRes = 1024;    // OceanDepthmap (мелководная кромка без смазанной полосы)
        private const int MinimapRes = 1024;  // MapImage (миникарта/меню карт)

        /// <summary>Карта цвета террейна для MapSettings (readable!).</summary>
        public static Texture2D TerrainColor(Heightmap hm, ScenarioTemplate.TerrainSettings t) =>
            TexFromPixels(RenderPixels(hm, t, ColorMapRes), ColorMapRes,
                "SN-TerrainColor", readable: true, mips: false);

        /// <summary>Albedo рельефа (фолбэк-материал меша URP Lit): дорогие зоны
        /// (палитра/скалы/пляж/пятна fBm) считаются в макро-буфере 1024² и
        /// билинейно апсемплятся, поверх — ЗЕРНО-шум тремя масштабами (10/130/900 м
        /// при 8192) — без зерна неразмноженная текстура «мылила» на любом
        /// разрешении (плейтесты 2026-11-02 ×3). Разрешение — ручка terrain.albedoRes
        /// (слабые машины — вниз). Основной путь — сток-материал игры
        /// (TerrainMaterial), это только фолбэк.</summary>
        public static Texture2D Albedo(Heightmap hm, ScenarioTemplate.TerrainSettings t)
        {
            int res = Mathf.Clamp(t.albedoRes <= 0 ? AlbedoRes : t.albedoRes, 1024, 16384);
            Color32[] macro = RenderPixels(hm, t, MacroRes);
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 9; // наклонный RTS-ракурс без анизотропы мылит вдоль взгляда
            tex.name = "SN-TerrainAlbedo";
            var px = new Color32[res * res];
            float scale = (float)MacroRes / res;
            float size = hm.W * hm.Step;
            // Пятна «проталин» — в буфер 4096² (параллельно): fBm не умножается
            // на 151M пикселей albedo (главный тормоз генерации, 2026-11-03).
            const int SpotRes = 4096;
            var spots = new float[SpotRes * SpotRes];
            Parallel.For(0, SpotRes, sj =>
            {
                float z = (sj + 0.5f) / SpotRes * size - size * 0.5f;
                for (int si = 0; si < SpotRes; si++)
                {
                    float x = (si + 0.5f) / SpotRes * size - size * 0.5f;
                    spots[sj * SpotRes + si] =
                        0.5f * Fbm(x * 0.04f, z * 0.04f, 991, 2) +
                        0.3f * Fbm(x * 0.016f, z * 0.016f, 992, 2) +
                        0.2f * Fbm(x * 0.0066f, z * 0.0066f, 993, 2);
                }
            });
            // Основной цикл albedo — параллельно по строкам (пиксели независимы).
            // Умножаем ТОЛЬКО RGB (альфа давала прозрачность, плейтест 2026-11-02).
            Parallel.For(0, res, j =>
            {
                for (int i = 0; i < res; i++)
                {
                    Color c = SampleBilinear(macro, MacroRes, (i + 0.5f) * scale - 0.5f,
                        (j + 0.5f) * scale - 0.5f);
                    float x = (i + 0.5f) / res * size - size * 0.5f;
                    float z = (j + 0.5f) / res * size - size * 0.5f;
                    bool water = hm.SampleHeight(x, z) < hm.SeaLevel;
                    float grain;
                    if (water)
                    {
                        grain = 0.955f + 0.09f * Noise2D(i, j);
                    }
                    else
                    {
                        // Суб-текстура «как Neroxis»: пятна — билинейно из буфера
                        // 4096² (периоды ≥25 м не теряют резкости), поверх крап.
                        float spot = SampleBilinearF(spots, SpotRes,
                            (x + size * 0.5f) / size, (z + size * 0.5f) / size);
                        float patch = spot > 0.60f ? 0.86f : spot < 0.40f ? 1.12f : 1f;
                        grain = patch * (0.97f + 0.06f * Noise2D(i, j) +
                                         0.03f * Noise2D(i / 3, j / 3));
                    }
                    c.r *= grain;
                    c.g *= grain;
                    c.b *= grain;
                    c.a = 1f;
                    px[j * res + i] = c;
                }
            });
            tex.SetPixels32(px);
            tex.Apply(true, true); // мипы, пиксели CPU не нужны
            return tex;
        }

        /// <summary>Билинейная выборка float-буфера по нормированным 0..1 координатам.</summary>
        private static float SampleBilinearF(float[] buf, int res, float fx, float fy)
        {
            fx = Mathf.Clamp(fx * res - 0.5f, 0f, res - 1f);
            fy = Mathf.Clamp(fy * res - 0.5f, 0f, res - 1f);
            int x0 = Mathf.Min(res - 1, (int)fx), y0 = Mathf.Min(res - 1, (int)fy);
            int x1 = Mathf.Min(res - 1, x0 + 1), y1 = Mathf.Min(res - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;
            float a = buf[y0 * res + x0] + (buf[y0 * res + x1] - buf[y0 * res + x0]) * tx;
            float b = buf[y1 * res + x0] + (buf[y1 * res + x1] - buf[y1 * res + x0]) * tx;
            return a + (b - a) * ty;
        }

        /// <summary>Кэш макро-пикселей последней карты: TerrainColor, миникарта
        /// и albedo-макро — ОДИН расчёт вместо трёх (запрос 2026-11-03 «сократить
        /// время генерации»).</summary>
        private static Color32[] _macroCache;

        /// <summary>Пиксели зонной окраски (без создания текстуры). Параллельно
        /// по строкам (пиксели независимы — детерминизм сохранён).</summary>
        private static Color32[] RenderPixels(Heightmap hm, ScenarioTemplate.TerrainSettings t, int res)
        {
            if (res == MacroRes && _macroCache != null)
                return _macroCache;
            float[,] distToWater = DistToWaterM(hm);
            ZonePalette pal = ZonePalette.Of(t);
            // Color32 (4 байта/пиксель): Color[] на больших текстурах — сотни МБ.
            var px = new Color32[res * res];
            float size = hm.W * hm.Step;
            Parallel.For(0, res, j =>
            {
                for (int i = 0; i < res; i++)
                {
                    float x = (i + 0.5f) / res * size - size * 0.5f;
                    float z = (j + 0.5f) / res * size - size * 0.5f;
                    px[j * res + i] = ZoneColor(hm, t, pal, distToWater, x, z);
                }
            });
            if (res == MacroRes)
                _macroCache = px;
            return px;
        }

        private static Texture2D TexFromPixels(Color32[] px, int res, string name,
            bool readable, bool mips)
        {
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, mips, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = name;
            tex.SetPixels32(px);
            tex.Apply(mips, !readable);
            return tex;
        }

        /// <summary>Билинейная выборка из пиксельного буфера (координаты — float,
        /// клампятся; за краем — крайние пиксели).</summary>
        private static Color SampleBilinear(Color32[] px, int res, float fx, float fy)
        {
            fx = Mathf.Clamp(fx, 0f, res - 1f);
            fy = Mathf.Clamp(fy, 0f, res - 1f);
            int x0 = Mathf.Min(res - 1, (int)fx), y0 = Mathf.Min(res - 1, (int)fy);
            int x1 = Mathf.Min(res - 1, x0 + 1), y1 = Mathf.Min(res - 1, y0 + 1);
            float tx = fx - x0, ty = fy - y0;
            Color a = Color.Lerp(px[y0 * res + x0], px[y0 * res + x1], tx);
            Color b = Color.Lerp(px[y1 * res + x0], px[y1 * res + x1], tx);
            return Color.Lerp(a, b, ty);
        }

        /// <summary>Цвет мировой точки: зоны (дно/мель/пляж/грунт/скалы/снег) +
        /// макро-пятна «провинций» + затенение по крутизне. Высота — треугольной
        /// выборкой по мешу (SampleHeight): границы воды совпадают с рельефом.</summary>
        private static Color ZoneColor(Heightmap hm, ScenarioTemplate.TerrainSettings t,
            ZonePalette pal, float[,] distToWater, float x, float z)
        {
            float h = hm.SampleHeight(x, z);
            int ci = Clamp((int)(x / hm.Step + hm.W * 0.5f), 0, hm.W - 1);
            int cj = Clamp((int)(z / hm.Step + hm.H * 0.5f), 0, hm.H - 1);

            if (h < hm.SeaLevel)
            {
                // Вода: мель у кромки светлее, глубина — темнее базового.
                float depth = hm.SeaLevel - h;
                Color c = Color.Lerp(pal.Shallow, pal.Water,
                    Mathf.Clamp01(depth / 30f));
                c *= 1f - 0.25f * Mathf.Clamp01((depth - 60f) / 500f);
                return c;
            }

            // Суша: палитра от уровня моря (не от дна карты — дно растягивало
            // палитру, v3), высотные доли как раньше.
            float span = Mathf.Max(1f, hm.Max - hm.SeaLevel);
            Color land = Sample(pal.Palette, (h - hm.SeaLevel) / span);

            // Скалы: крутизна (по клеткам высот — террасы и так ступенчатые).
            float dh = Mathf.Max(
                Mathf.Abs(h - CellHeight(hm, ci, cj, ci + 1, cj)),
                Mathf.Abs(h - CellHeight(hm, ci, cj, ci, cj + 1))) / hm.Step;
            float rock = Smooth01((dh - 0.30f) / 0.35f); // ~17°..37°
            land = Color.Lerp(land, pal.Cliff, rock);

            // Пляж: полоса у кромки воды (дистанция — поле по маске Water).
            float beach = 1f - Smooth01((distToWater[ci, cj] - t.beachWidthM * 0.4f) /
                                        (t.beachWidthM * 0.6f));
            land = Color.Lerp(land, pal.Beach, beach * 0.85f);

            // Снег на вершинах.
            if (t.snowHeightM < hm.Max)
                land = Color.Lerp(land, pal.Snow,
                    Smooth01((h - t.snowHeightM) / 150f) * 0.9f);

            // Макро-пятна «провинций»: вариация яркости/оттенка волной ~7 км —
            // держит дальний план неоднородным (без него издалека «одно зелёное»,
            // плейтест 2026-11-03). Амплитуда ×2 от первой робкой версии.
            float patch = Fbm(x * 0.00014f + 2.2f, z * 0.00014f, 777, 3);
            float hue = Fbm(x * 0.00009f + 8.4f, z * 0.00009f, 778, 3);
            land *= 0.84f + 0.32f * patch;
            land.g *= 0.94f + 0.12f * hue;

            // Читаемость рельефа: затенение по крутизне (склоны темнее).
            land *= Shade(hm, ci, cj);
            return land;
        }

        private static float CellHeight(Heightmap hm, int ci, int cj, int i, int j)
        {
            i = Clamp(i, 0, hm.W - 1);
            j = Clamp(j, 0, hm.H - 1);
            return hm.Height[i, j];
        }

        /// <summary>Дистанция до ближайшей воды (метры, по клеткам) — поле для
        /// пляжной полосы. Двухпроходный chamfer 3-4 (приближение евклида).</summary>
        private static float[,] DistToWaterM(Heightmap hm)
        {
            const float inf = 1e9f;
            var d = new float[hm.W, hm.H];
            for (int j = 0; j < hm.H; j++)
                for (int i = 0; i < hm.W; i++)
                    d[i, j] = hm.Water[i, j] ? 0f : inf;

            for (int j = 0; j < hm.H; j++)
                for (int i = 0; i < hm.W; i++)
                    Relax(d, hm, i, j,
                        Math.Min(i > 0 ? d[i - 1, j] + 1f : inf,
                                 j > 0 ? d[i, j - 1] + 1f : inf),
                        Math.Min(i > 0 && j > 0 ? d[i - 1, j - 1] + 1.4142f : inf,
                                 i < hm.W - 1 && j > 0 ? d[i + 1, j - 1] + 1.4142f : inf));
            for (int j = hm.H - 1; j >= 0; j--)
                for (int i = hm.W - 1; i >= 0; i--)
                    Relax(d, hm, i, j,
                        Math.Min(i < hm.W - 1 ? d[i + 1, j] + 1f : inf,
                                 j < hm.H - 1 ? d[i, j + 1] + 1f : inf),
                        Math.Min(i < hm.W - 1 && j < hm.H - 1 ? d[i + 1, j + 1] + 1.4142f : inf,
                                 i > 0 && j < hm.H - 1 ? d[i - 1, j + 1] + 1.4142f : inf));
            for (int j = 0; j < hm.H; j++)
                for (int i = 0; i < hm.W; i++)
                    if (d[i, j] >= inf)
                        d[i, j] = hm.W * hm.Step; // воды нет вообще — «далеко»
                    else
                        d[i, j] *= hm.Step;
            return d;
        }

        private static void Relax(float[,] d, Heightmap hm, int i, int j, float ortho, float diag)
        {
            float m = Math.Min(ortho, diag);
            if (m < d[i, j])
                d[i, j] = m;
        }

        /// <summary>Множитель цвета клетки: 1 на равнине, темнее на склоне
        /// (градиент высоты к соседям), ±шум для детализации.</summary>
        private static float Shade(Heightmap hm, int i, int j)
        {
            int i1 = Math.Min(hm.W - 1, i + 1), j1 = Math.Min(hm.H - 1, j + 1);
            float dh = Math.Max(
                Math.Abs(hm.Height[i, j] - hm.Height[i1, j]),
                Math.Abs(hm.Height[i, j] - hm.Height[i, j1])) / hm.Step;
            float slope = Mathf.Clamp01(dh * 3f); // 0 на равнине, ~1 у 18°+
            float noise = Noise2D(i * 7, j * 13) - 0.5f;
            return 1f - 0.35f * slope + noise * 0.12f;
        }

        private static float Noise2D(int x, int y)
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }

        /// <summary>Глубины океана (серая шкала 0..1, мель у берега).</summary>
        public static Texture2D OceanDepth(Heightmap hm, float maxDepthM)
        {
            var tex = new Texture2D(DepthRes, DepthRes, TextureFormat.RGBA32, false, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "SN-OceanDepth";
            var px = new Color[DepthRes * DepthRes];
            float maxDepth = Math.Max(1f, maxDepthM);
            float size = hm.W * hm.Step;
            for (int j = 0; j < DepthRes; j++)
                for (int i = 0; i < DepthRes; i++)
                {
                    float x = (i + 0.5f) / DepthRes * size - size * 0.5f;
                    float z = (j + 0.5f) / DepthRes * size - size * 0.5f;
                    float h = hm.SampleHeight(x, z);
                    float d = h < hm.SeaLevel
                        ? Mathf.Clamp01((hm.SeaLevel - h) / maxDepth)
                        : 0f;
                    px[j * DepthRes + i] = new Color(d, d, d, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Одноцветная «базовая» текстура океана (макро-цвет воды).</summary>
        public static Texture2D OceanBase(ScenarioTemplate.TerrainSettings t)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            tex.name = "SN-OceanBase";
            Color c = ParseHex(t.waterColor);
            var px = new Color[16];
            for (int i = 0; i < 16; i++)
                px[i] = c;
            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Спрайт миникарты (MapImage) — те же зонные цвета, квадрат.
        /// Спрайтовая V-ось перевёрнута относительно TerrainColorMap — как у
        /// стоковых карт (DynamicMap грузит именно спрайт).</summary>
        public static Sprite MapSprite(Heightmap hm, ScenarioTemplate.TerrainSettings t)
        {
            Texture2D tex = TexFromPixels(RenderPixels(hm, t, MinimapRes), MinimapRes,
                "SN-MapImage", readable: true, mips: false);
            return Sprite.Create(tex, new Rect(0f, 0f, MinimapRes, MinimapRes),
                new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>Зонная палитра шаблона (парсится один раз на текстуру).</summary>
        private sealed class ZonePalette
        {
            public Color[] Palette;
            public Color Water, Shallow, Beach, Cliff, Snow;

            public static ZonePalette Of(ScenarioTemplate.TerrainSettings t) => new ZonePalette
            {
                Palette = ParsePalette(t.palette),
                Water = ParseHex(t.waterColor),
                Shallow = ParseHex(t.shallowColor),
                Beach = ParseHex(t.beachColor),
                Cliff = ParseHex(t.cliffColor),
                Snow = new Color(0.92f, 0.94f, 0.96f),
            };
        }

        /// <summary>Цвет вершины рельефа — ЗОНАЛЬНАЯ палитра высот (80%): GPU-дамп
        /// закрыл механику Field (2026-11-03): _Colormask — СЕРАЯ оттеночная
        /// модуляция (R≈G≈B≈0.55, A=1), а МАКРО-ЦВЕТ зон несут именно ВЕРШИННЫЕ
        /// ЦВЕТА (_VertexColorize). Осветлённая база (20% белого) не даёт
        /// погаснуть детальной фактуре.</summary>
        public static Color VertexColor(Heightmap hm, ScenarioTemplate.TerrainSettings t, float h)
        {
            Color[] palette = ParsePalette(t.palette);
            float span = Mathf.Max(1f, hm.Max - hm.SeaLevel);
            return Color.Lerp(Color.white, Sample(palette, (h - hm.SeaLevel) / span), 0.8f);
        }

        private static Color Sample(Color[] palette, float f01)
        {
            if (palette == null || palette.Length == 0)
                return Color.green;
            if (palette.Length == 1)
                return palette[0];
            float x = Mathf.Clamp01(f01) * (palette.Length - 1);
            int i = Mathf.Min(palette.Length - 2, (int)x);
            return Color.Lerp(palette[i], palette[i + 1], x - i);
        }

        // ---- value-noise (макро-пятна провинций; копия HeightmapGen-схемы) ----

        private static float Fbm(float x, float y, int seed, int octaves)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += Value(x, y, seed + o * 101) * amp;
                norm += amp;
                amp *= 0.5f;
                x *= 2f;
                y *= 2f;
            }
            return sum / norm;
        }

        private static float Value(float x, float y, int seed)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float tx = Smooth01(x - ix), ty = Smooth01(y - iy);
            float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed);
            float c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
            return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
        }

        private static float Hash(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
        }

        private static float Smooth01(float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return t * t * (3f - 2f * t);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static Color[] ParsePalette(string[] hex)
        {
            if (hex == null || hex.Length == 0)
                return new[] { new Color(0.3f, 0.45f, 0.25f) };
            var colors = new Color[hex.Length];
            for (int i = 0; i < hex.Length; i++)
                colors[i] = ParseHex(hex[i]);
            return colors;
        }

        private static Color ParseHex(string hex)
        {
            if (!ColorUtility.TryParseHtmlString(hex, out Color c))
            {
                Log.Warn("MapGen: не разобрать цвет '" + hex + "' — использую зелёный");
                c = new Color(0.3f, 0.45f, 0.25f);
            }
            return c;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
