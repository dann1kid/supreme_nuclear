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
        private const int MacroRes = 1024;    // макро-поле зон: миникарта/TerrainColor
        private const int AlbedoMacroRes = 4096; // макро-зоны albedo: 20 м/пиксель —
        // границы зон (пляж/скалы) у 1024 (80 м) мылили вблизи (плейтест 2026-11-03);
        // генерация дорожает (ZoneColor × 16.7M) — юзер-канон «тяжесть пайплайна ОК»
        private const int AlbedoRes = 16384;  // дефолт albedo (5 м/тексель на 82 км;
        // «полосатость» 2026-11-03 была багом индексации макро-буфера, НЕ GPU —
        // после фикса 16384² RGBA32 жив; ручка terrain.albedoRes для слабых машин)
        private const int DepthRes = 1024;    // OceanDepthmap (мелководная кромка без смазанной полосы)
        private const int MinimapRes = 1024;  // MapImage (миникарта/меню карт)

        /// <summary>Карта цвета террейна для MapSettings (readable!).</summary>
        public static Texture2D TerrainColor(Heightmap hm, ScenarioTemplate.TerrainSettings t) =>
            TexFromPixels(RenderPixels(hm, t, ColorMapRes), ColorMapRes,
                "SN-TerrainColor", readable: true, mips: false);

        /// <summary>Albedo рельефа (основной материал — гибрид «по карте высот»):
        /// дорогие зоны (палитра/скалы/пляж/пятна fBm) считаются в макро-буфере
        /// 2048² и билинейно апсемплятся, поверх — ЗЕРНО-шум несколькими
        /// масштабами — без зерна неразмноженная текстура «мылила» на любом
        /// разрешении (плейтесты 2026-11-02 ×3). Разрешение — ручка
        /// terrain.albedoRes (слабые машины — вниз), после сборки — GPU-компрессия
        /// в BC (VRAM ×4 вниз, иначе 16384² RGBA32 не жить).</summary>
        public static Texture2D Albedo(Heightmap hm, ScenarioTemplate.TerrainSettings t)
        {
            int res = Mathf.Clamp(t.albedoRes <= 0 ? AlbedoRes : t.albedoRes, 1024, 16384);
            // Потолок текстур GPU (у части карт 8192): создание сверх него —
            // мусор в сэмплах, «размазанная» текстура.
            res = Mathf.Min(res, SystemInfo.maxTextureSize);
            Color32[] macro = RenderPixels(hm, t, AlbedoMacroRes);
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 16; // наклонный RTS-ракурс без анизотропы мылит вдоль взгляда
            tex.name = "SN-TerrainAlbedo";
            var px = new Color32[res * res];
            float scale = (float)AlbedoMacroRes / res;
            float size = hm.W * hm.Step;
            // Пятна «проталин» — в буфер 8192² (параллельно): fBm не умножается
            // на 268M пикселей albedo (главный тормоз генерации, 2026-11-03);
            // 8192 = 10 м/пятно-пиксель — резче вблизи (юзер-канон «можно и лучше»).
            const int SpotRes = 8192;
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
                    Color c = SampleBilinear(macro, AlbedoMacroRes, (i + 0.5f) * scale - 0.5f,
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
                        // 4096² (периоды ≥25 м не теряют резкости), поверх крап
                        // (амплитуда поднята против «размазанности» вблизи,
                        // плейтест 2026-11-03).
                        float spot = SampleBilinearF(spots, SpotRes,
                            (x + size * 0.5f) / size, (z + size * 0.5f) / size);
                        float patch = spot > 0.60f ? 0.86f : spot < 0.40f ? 1.12f : 1f;
                        grain = patch * (0.955f + 0.085f * Noise2D(i, j) +
                                         0.04f * Noise2D(i / 3, j / 3));
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
            // БЕЗ рантайм-компрессии: GPU Compress→DXT5 на 16384² давал битые
            // блоки — «полосы в разные стороны + синие зоны» (плейтест
            // 2026-11-03, компрессия по логу «успешна»). RGBA32 16384² ≈ 1 ГБ
            // VRAM — приемлемо для десктопа; слабым машинам — terrain.albedoRes.
            Log.Info("MapGen: albedo " + res + "² " + tex.format + " (без компрессии)");
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

        // ---------- Сплат-канон (SupCom/Neroxis): маска видов + тайлы ----------

        private static Heightmap _splatHm;
        private static ScenarioTemplate.TerrainSettings _splatT;
        private static Texture2D _splatMask;
        private static Texture2D[] _splatTiles;

        /// <summary>Запомнить источник сплата (вызывается при сборке карты ДО
        /// ResolvePlan — маска/тайлы генерятся лениво по первому запросу).</summary>
        public static void RememberSplatSource(Heightmap hm, ScenarioTemplate.TerrainSettings t)
        {
            _splatHm = hm;
            _splatT = t;
            _splatMask = null; // карта новая — стереть
        }

        /// <summary>Маска видов поверхности (R=трава G=песок B=скала A=снег) на
        /// всю карту, 2048² (весам хватает 40 м/пиксель — переходы плавные).
        /// Правила — те же поля, что у ZoneColor (высота/крутизна/у кромки),
        /// с дизерингом порогов — естественные границы слоёв.</summary>
        public static Texture2D SplatMask()
        {
            if (_splatMask != null)
                return _splatMask;
            Heightmap hm = _splatHm;
            ScenarioTemplate.TerrainSettings t = _splatT;
            if (hm == null || t == null)
                return null;
            const int res = 2048;
            float[,] distToWater = DistToWaterM(hm);
            float size = hm.W * hm.Step;
            var px = new Color32[res * res];
            System.Threading.Tasks.Parallel.For(0, res, j =>
            {
                float z = (j + 0.5f) / res * size - size * 0.5f;
                for (int i = 0; i < res; i++)
                {
                    float x = (i + 0.5f) / res * size - size * 0.5f;
                    float h = hm.SampleHeight(x, z);
                    int ci = Clamp((int)(x / hm.Step + hm.W * 0.5f), 0, hm.W - 1);
                    int cj = Clamp((int)(z / hm.Step + hm.H * 0.5f), 0, hm.H - 1);
                    float dith = (Noise2D(i * 3 + 11, j * 3 + 7) - 0.5f) * 24f; // м-дизеринг порогов

                    // Скала — крутизна (как rock в ZoneColor).
                    float dh = Mathf.Max(
                        Mathf.Abs(h - CellHeight(hm, ci, cj, ci + 1, cj)),
                        Mathf.Abs(h - CellHeight(hm, ci, cj, ci, cj + 1))) / hm.Step;
                    float rock = Smooth01((dh - 0.30f) / 0.35f);

                    // Снег — вершины.
                    float snow = t.snowHeightM < hm.Max
                        ? Smooth01((h - t.snowHeightM + dith) / 150f) * 0.95f
                        : 0f;

                    // Песок — пляжная полоса + подводные склоны (дно).
                    float beach = 1f - Smooth01((distToWater[ci, cj] - t.beachWidthM * 0.4f +
                                                  dith) / (t.beachWidthM * 0.6f));
                    float under = h < hm.SeaLevel
                        ? Mathf.Clamp01((hm.SeaLevel - h) / 8f)
                        : 0f;
                    float sand = Mathf.Max(beach * 0.9f, under);

                    float grass = Mathf.Clamp01(1f - Mathf.Max(rock, Mathf.Max(snow, sand)));

                    // Нормировка суммы весов (шейдер нормирует сам — порядок
                    // в RGBA фиксируем и здесь).
                    float sum = grass + sand + rock + snow + 1e-4f;
                    px[j * res + i] = new Color32(
                        (byte)(grass / sum * 255f), (byte)(sand / sum * 255f),
                        (byte)(rock / sum * 255f), (byte)(snow / sum * 255f));
                }
            });
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true)
            {
                name = "SN-SplatMask",
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4,
            };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            _splatMask = tex;
            Log.Info("MapGen: сплат-маска " + res + "² готова");
            return tex;
        }

        /// <summary>Четыре процедурных ТАЙЛА детальной фактуры (grayscale,
        /// 2048², период задаёт шейдер _TileMetres): ворс травы / крап песка /
        /// трещиноватая скала (ridged fBm) / мягкий снег. Repeat + мипы.</summary>
        public static Texture2D[] SplatTiles()
        {
            if (_splatTiles != null)
                return _splatTiles;
            const int res = 2048; // ×4 плотность фактуры (юзер-канон 2026-11-03)
            _splatTiles = new Texture2D[4];
            for (int kind = 0; kind < 4; kind++)
                _splatTiles[kind] = MakeTile(kind, res);
            Log.Info("MapGen: сплат-тайлы 4×" + res + "² готовы");
            return _splatTiles;
        }

        private static Texture2D MakeTile(int kind, int res)
        {
            var px = new Color32[res * res];
            System.Threading.Tasks.Parallel.For(0, res, j =>
            {
                for (int i = 0; i < res; i++)
                {
                    float u = i / (float)res, v = j / (float)res;
                    float g;
                    switch (kind)
                    {
                        case 0: // трава: два мелких октавных ворса
                            g = 0.86f + 0.10f * Fbm(u * 48f, v * 48f, 501, 2) +
                                         0.06f * Fbm(u * 110f, v * 110f, 502, 2);
                            break;
                        case 1: // песок: ровный мелкий крап
                            g = 0.90f + 0.08f * Fbm(u * 90f, v * 90f, 511, 2) +
                                         0.03f * Noise2D(i * 5, j * 5);
                            break;
                        case 2: // скала: ridged fBm — «трещины» (1-|n|), контрастнее
                        {
                            float r1 = 1f - Mathf.Abs(Fbm(u * 14f, v * 14f, 521, 3) * 2f - 1f);
                            float r2 = 1f - Mathf.Abs(Fbm(u * 34f, v * 34f, 522, 2) * 2f - 1f);
                            g = 0.68f + 0.28f * r1 + 0.16f * r2;
                            break;
                        }
                        default: // снег: мягкие крупные пятна
                            g = 0.92f + 0.07f * Fbm(u * 22f, v * 22f, 531, 2);
                            break;
                    }
                    g = Mathf.Clamp01(g);
                    byte b = (byte)(g * 255f);
                    px[j * res + i] = new Color32(b, b, b, 255);
                }
            });
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, true)
            {
                name = "SN-SplatTile" + kind,
                wrapMode = TextureWrapMode.Repeat, // тайл!
                anisoLevel = 9,
            };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

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

        /// <summary>Цвет вершины рельефа — ПОЛНАЯ зона (высота + тип поверхности):
        /// палитра высот + скалы по крутизне + пляж у кромки воды + снег на
        /// вершинах + дно/мель подводных склонов + пятна «провинций» + затенение
        /// склонов — та же ZoneColor, что красит карту/миникарту. GPU-дамп закрыл
        /// механику Field (2026-11-03): _Colormask — СЕРАЯ оттеночная модуляция
        /// (R≈G≈B≈0.55), МАКРО-ЦВЕТ зон несут именно ВЕРШИННЫЕ ЦВЕТА
        /// (_VertexColorize); одна палитра высот на террасных плато давала
        /// «однообразную текстуру на всю карту» (плейтест 2026-11-03). База 20%
        /// белого — детальная фактура field_b не гаснет.</summary>
        public static Color VertexColorZone(Heightmap hm, ScenarioTemplate.TerrainSettings t,
            float x, float z)
        {
            if (_palHm != hm)
            {
                _palCache = ZonePalette.Of(t);
                _palHm = hm;
            }
            if (_distHm != hm)
            {
                _distCache = DistToWaterM(hm);
                _distHm = hm;
            }
            Color c = ZoneColor(hm, t, _palCache, _distCache, x, z);
            return Color.Lerp(Color.white, c, 0.8f);
        }

        // Кэши полей зон для покраски ВЕРШИН (миллион вызовов на карту):
        // палитра/поле дистанции до воды пересобираются только при смене карты.
        private static ZonePalette _palCache;
        private static Heightmap _palHm;
        private static float[,] _distCache;
        private static Heightmap _distHm;

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
