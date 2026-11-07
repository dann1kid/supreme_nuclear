using System;
using System.Collections.Generic;
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
        // 2048² = 40 м/тексель на 82 км: превью панели/миникарта — макро-вид,
        // юзер смотрит именно сюда («jpeg из 2010» было про 1024² = 80 м)
        private const int MinimapRes = 2048;  // MapImage (миникарта/меню карт)

        /// <summary>Карта цвета террейна для MapSettings (readable!).</summary>
        public static Texture2D TerrainColor(Heightmap hm, ScenarioTemplate.TerrainSettings t) =>
            TexFromPixels(RenderPixels(hm, t, ColorMapRes), ColorMapRes,
                "SN-TerrainColor", readable: true, mips: false);

        /// <summary>Цветовая карта из ГОТОВЫХ пикселей (главный поток) —
        /// асинхронная генерация приносит буфер 1024² с воркера.</summary>
        internal static Texture2D TexFrom(Color32[] px)
        {
            // res — из БУФЕРА (MinimapRes поднят до 2048²; жёсткий ColorMapRes
            // давал SetPixels32 вне границ → MapGenAsync падал, редактор не
            // подменял карту — плейтест 2026-11-06)
            int res = Mathf.RoundToInt(Mathf.Sqrt(px.Length));
            return TexFromPixels(px, res, "SN-TerrainColor", readable: true, mips: false);
        }

        /// <summary>Полный пиксельный срез карты — ЧИСТАЯ математика, воркер-безопасно:
        /// миникарта/цветовая MinimapRes (2048² — макро-вид юзера) + albedo полного
        /// разрешения (res — от AlbedoResOf, отобранного на главном). Вызывается на
        /// воркере MapGenAsync. Имя поля Map1024 — легаси.</summary>
        internal static MapPixelBundle ComputePixels(Heightmap hm, ScenarioTemplate.TerrainSettings t,
            int albedoRes) =>
            new MapPixelBundle
            {
                Map1024 = RenderPixelsUncached(hm, t, MinimapRes, seabed: false),
                Albedo = AlbedoPixels(hm, t, albedoRes),
                AlbedoRes = albedoRes,
            };

        /// <summary>Подсадить готовый буфер 1024² в кэш макро-пикселей (главный
        /// поток): воркер считает его вне кэша, а последующие вызовы
        /// RenderPixels(1024) должны получить тот же буфер без пересчёта.</summary>
        internal static void PrimeMacroCache(Color32[] px1024)
        {
            if (px1024 != null && px1024.Length == MacroRes * MacroRes)
                _macroCache = px1024;
        }

        /// <summary>Albedo рельефа (основной материал — гибрид «по карте высот»):
        /// дорогие зоны (палитра/скалы/пляж/пятна fBm) считаются в макро-буфере
        /// 2048² и билинейно апсемплятся, поверх — ЗЕРНО-шум несколькими
        /// масштабами — без зерна неразмноженная текстура «мылила» на любом
        /// разрешении (плейтесты 2026-11-02 ×3). Разрешение — ручка
        /// terrain.albedoRes (слабые машины — вниз), после сборки — GPU-компрессия
        /// в BC (VRAM ×4 вниз, иначе 16384² RGBA32 не жить).</summary>
        public static Texture2D Albedo(Heightmap hm, ScenarioTemplate.TerrainSettings t)
        {
            int res = AlbedoResOf(t);
            return AlbedoTexture(AlbedoPixels(hm, t, res), res);
        }

        /// <summary>Разрешение albedo (ручка шаблона + потолок GPU SystemInfo —
        /// main-thread-only): отбирается НА ГЛАВНОМ потоке, число уходит воркеру
        /// (асинхронная генерация MapGenAsync).</summary>
        internal static int AlbedoResOf(ScenarioTemplate.TerrainSettings t)
        {
            int res = Mathf.Clamp(t == null || t.albedoRes <= 0 ? AlbedoRes : t.albedoRes, 1024, 16384);
            // Потолок текстур GPU (у части карт 8192): создание сверх него —
            // мусор в сэмплах, «размазанная» текстура.
            return Mathf.Min(res, SystemInfo.maxTextureSize);
        }

        /// <summary>Пиксели albedo — ЧИСТАЯ математика (макро-зоны + пятна + зерно),
        /// Parallel.For сам уходит на пул. Без движковых вызовов — работает на
        /// воркере асинхронной генерации (MapGenAsync); res — от AlbedoResOf.</summary>
        internal static Color32[] AlbedoPixels(Heightmap hm, ScenarioTemplate.TerrainSettings t, int res)
        {
            Color32[] macro = RenderPixelsUncached(hm, t, AlbedoMacroRes, seabed: true);
            var px = new Color32[res * res];
            float scale = (float)AlbedoMacroRes / res;
            float size = hm.W * hm.Step;
            // Пятна-поля — в буфер 8192² (параллельно): fBm не умножается
            // на 268M пикселей albedo (главный тормоз генерации, 2026-11-03).
            // v0.154 анти-камуфляж: частоты КРУПНЕЕ (~83/250/770 м вместо
            // 25/62/150) — пятна читаются «полями/прогалинами», а не кляксами
            // военной формы; отклик непрерывный (см. ниже), порогов нет.
            const int SpotRes = 8192;
            var spots = new float[SpotRes * SpotRes];
            Parallel.For(0, SpotRes, sj =>
            {
                float z = (sj + 0.5f) / SpotRes * size - size * 0.5f;
                for (int si = 0; si < SpotRes; si++)
                {
                    float x = (si + 0.5f) / SpotRes * size - size * 0.5f;
                    spots[sj * SpotRes + si] =
                        0.55f * Fbm(x * 0.012f, z * 0.012f, 991, 2) +
                        0.30f * Fbm(x * 0.004f, z * 0.004f, 992, 2) +
                        0.15f * Fbm(x * 0.0013f, z * 0.0013f, 993, 2);
                }
            });
            // Основной цикл albedo — параллельно по строкам (пиксели независимы).
            // Умножаем ТОЛЬКО RGB (альфа давала прозрачность, плейтест 2026-11-02).
            // v0.154 анти-камуфляж (плейтест «защитные текстуры военной формы»):
            // БЕЛОЕ ЗЕРНО per-texel (Noise2D 5/15 м) убрано — на 5 м/тексель
            // читалось статикой/кляксами; вся вариация теперь из пятен-поля
            // (83..770 м, билинейно = гладко) и крупнее — микродеталь вблизи
            // несут ТАЙЛЫ сплата (4 м) и нормал (1 м), а не альбедо.
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
                        grain = 1f; // дно красит океан-шейдер; зерно дало «статик»
                    }
                    else
                    {
                        float spot = SampleBilinearF(spots, SpotRes,
                            (x + size * 0.5f) / size, (z + size * 0.5f) / size);
                        grain = 1f + 0.14f * (spot - 0.5f) * 2f; // непрерывно, ±14%
                    }
                    c.r *= grain;
                    c.g *= grain;
                    c.b *= grain;
                    c.a = 1f;
                    px[j * res + i] = c;
                }
            });
            return px;
        }

        /// <summary>Текстура albedo из готовых пикселей (ГЛАВНЫЙ поток): SetPixels32
        /// + Apply с мип-цепочкой — единственный неустранимый хич асинхронной
        /// генерации (генерация мипов и заливка GPU — движковые, на воркер не
        /// уносятся); в ступенчатой сборке это отдельная ступень с прогрессом.</summary>
        internal static Texture2D AlbedoTexture(Color32[] px, int res)
        {
            // линейный=false: ZoneColor пишет АВТОРСКИЕ sRGB-цвета (hex-палитра
            // шаблона) — флаг sRGB заставляет GPU декодировать их в линейное
            // пространство перед освещением; с linear:true альбедо вымывалось
            // в белый (плейтест-бисект 2026-11-06, макро-вывод был «пастель»).
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, true, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 16; // наклонный RTS-ракурс без анизотропы мылит вдоль взгляда
            tex.name = "SN-TerrainAlbedo";
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
        private static WorldGen _splatWorld; // коридоры/pads — износ в маске (v0.154)
        private static Texture2D _splatMask;
        private static Texture2D[] _splatTiles;

        /// <summary>Запомнить источник сплата (вызывается при сборке карты ДО
        /// ResolvePlan — маска/тайлы генерятся лениво по первому запросу).</summary>
        public static void RememberSplatSource(Heightmap hm, ScenarioTemplate.TerrainSettings t,
            WorldGen world = null)
        {
            _splatHm = hm;
            _splatT = t;
            _splatWorld = world;
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
            // 4096² = 20 м/тексель (было 2048² = 40 м): границы трава/песок/
            // скала вблизи не мылятся (жалоба «разрешение низкое», 2026-11-06).
            const int res = 4096;
            float[,] distToWater = DistToWaterM(hm);
            float size = hm.W * hm.Step;
            // Износ (v0.154): коридоры — со всеми образами симметрии (маска
            // обязана быть симметричной, как рельеф), кольца баз, сухие проталины.
            List<CorridorSeg> wearSegs = ScatterPlan.SymmetricCorridors(
                _splatWorld?.Corridors, t.symmetry);
            List<PadZone> wearPads = _splatWorld?.Pads;
            float wearAmount = Mathf.Clamp01(t.wearAmount);
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
                    float dith = (Noise2D(i * 3 + 11, j * 3 + 7) - 0.5f) * 10f; // м-дизеринг порогов
                    // v0.154 анти-камуфляж: дизеринг 24 м при текселе 40 м давал
                    // КАЙМУ-КРАПУ на границах зон (пляж/скала) — 10 м = под-тексель.

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

                    // Износ → песок (grass → sand): трассы проезда, кольца баз,
                    // сухие проталины (fBm ~4.8 км). На снегу износа нет.
                    if (wearAmount > 0f)
                    {
                        float wear = 0f;
                        if (wearSegs.Count > 0)
                        {
                            float dMin = float.MaxValue, hw = 0f;
                            foreach (CorridorSeg s in wearSegs)
                            {
                                float d = ScatterPlan.DistToSegXZ(x, z, s);
                                if (d < dMin)
                                {
                                    dMin = d;
                                    hw = s.HalfW;
                                }
                            }
                            // v0.154 анти-камуфляж: кромка износа шире и плавнее
                            // (26 м при текселе 40 м = блочная «сыпь» вдоль дорог).
                            wear = Mathf.Max(wear, Smooth01((hw + 30f - dMin) / 60f));
                        }
                        if (wearPads != null)
                            foreach (PadZone p in wearPads)
                            {
                                float dOut = p.DistanceOutside(x, z);
                                if (dOut < 80f)
                                    wear = Mathf.Max(wear, Smooth01((80f - dOut) / 80f) * 0.7f);
                            }
                        float dry = Fbm(x * 0.00021f, z * 0.00021f, 855, 3);
                        if (dry > 0.62f)
                            wear = Mathf.Max(wear, Smooth01((dry - 0.62f) / 0.12f) * 0.5f);
                        wear = Mathf.Clamp01(wear * wearAmount * (1f - snow));
                        grass *= 1f - wear;
                        sand = Mathf.Clamp01(sand + wear);
                    }

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
                        // v0.154 «низкое разрешение»: тайлы — единственная
                        // деталь вблизи (макро всегда мип-размыт), контраст
                        // поднят ×2.5; иначе глаз читает равнину как «мыло».
                        case 0: // трава: три октавы ворса + кочка-шум
                            g = 0.76f + 0.22f * Fbm(u * 96f, v * 96f, 501, 2) +
                                         0.14f * Fbm(u * 224f, v * 224f, 502, 2) +
                                         0.07f * Fbm(u * 430f, v * 430f, 503, 1);
                            break;
                        case 1: // песок: крап + ДЮННАЯ РЯБЬ (синус с варпом)
                            g = 0.84f + 0.14f * Fbm(u * 140f, v * 140f, 511, 2) +
                                         0.06f * Mathf.Sin((u + v) * 170f +
                                              8f * Fbm(u * 9f, v * 9f, 513, 2)) +
                                         0.04f * Fbm(u * 300f, v * 300f, 514, 1);
                            break;
                        case 2: // скала: ridged fBm — «трещины» (1-|n|), контрастнее.
                                // Домен-варп искривляет трещины низкочастотным
                                // полем — читается как геология, а не кляксы.
                        {
                            float wx = Fbm(u * 3f, v * 3f, 523, 2) * 0.18f;
                            float r1 = 1f - Mathf.Abs(Fbm(u * 14f + wx, v * 14f - wx, 521, 3) * 2f - 1f);
                            float r2 = 1f - Mathf.Abs(Fbm(u * 34f + wx * 2f, v * 34f + wx, 522, 2) * 2f - 1f);
                            g = 0.55f + 0.40f * r1 + 0.22f * r2;
                            break;
                        }
                        default: // снег: крупные пятна + мелкий жёст
                            g = 0.88f + 0.14f * Fbm(u * 40f, v * 40f, 531, 2) +
                                         0.05f * Fbm(u * 160f, v * 160f, 532, 1);
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
        private static Color32[] RenderPixels(Heightmap hm, ScenarioTemplate.TerrainSettings t, int res,
            bool seabed = false)
        {
            if (res == MacroRes && _macroCache != null && !seabed)
                return _macroCache;
            Color32[] px = RenderPixelsUncached(hm, t, res, seabed);
            if (res == MacroRes && !seabed)
                _macroCache = px;
            return px;
        }

        /// <summary>То же без кэша (_macroCache — главный-потоковое состояние;
        /// воркер MapGenAsync зовёт этот вариант). Чистая математика.</summary>
        private static Color32[] RenderPixelsUncached(Heightmap hm, ScenarioTemplate.TerrainSettings t,
            int res, bool seabed)
        {
            float[,] distToWater = DistToWaterM(hm);
            ZonePalette pal = ZonePalette.Of(t);
            // Color32 (4 байта/пиксель): Color[] на больших текстурах — сотни МБ.
            var px = new Color32[res * res];
            float size = hm.W * hm.Step;
            // ПЕК светотени (v0.155, канон Neroxis/SupCom — лайтмап в scmap):
            // AO ложбин + солнечные склоны (тёплые/холодные) пекутся в макро.
            // Поля — по клеткам heightmap, в пиксели — билинейно. Суша only:
            // подводные градиенты вода затемняет сама.
            float[] bakeAo, bakeWarm;
            BakeLightFields(hm, out bakeAo, out bakeWarm);
            int bw = hm.W;
            Parallel.For(0, res, j =>
            {
                for (int i = 0; i < res; i++)
                {
                    float x = (i + 0.5f) / res * size - size * 0.5f;
                    float z = (j + 0.5f) / res * size - size * 0.5f;
                    Color c = ZoneColor(hm, t, pal, distToWater, x, z, seabed);
                    if (c.g >= c.b || c.r > c.b) // суша/пляж (у воды b доминирует)
                    {
                        float u = (i + 0.5f) / res, v = (j + 0.5f) / res;
                        float a = SampleBilinearF(bakeAo, bw, u, v);
                        float warm = SampleBilinearF(bakeWarm, bw, u, v);
                        c *= a;
                        c.r *= 1f + 0.08f * warm; // солнечный склон — теплее
                        c.b *= 1f - 0.06f * warm; // теневой — холоднее
                        c.r = Mathf.Clamp01(c.r);
                        c.g = Mathf.Clamp01(c.g);
                        c.b = Mathf.Clamp01(c.b);
                    }
                    px[j * res + i] = c;
                }
            });
            return px;
        }

        /// <summary>Средний множитель пека последней карты (AO×солнце ≈
        /// 0.55..0.95): фото-страты нормализуются к НЕЙ, а не к константе —
        /// иначе зона смешения 0.5–6 км светилась «фонарём» поверх
        /// затемнённого пеком макро (плейтест 2026-11-06).</summary>
        internal static float LastBakeMean = 1f;

        /// <summary>Поля пека: AO ложбин (низкочастотная высота минус высота) и
        /// солнечного склона (тёплый/холодный, dot нормали с солнцем ЮВ).
        /// Чистая математика — воркер-безопасно.</summary>
        private static void BakeLightFields(Heightmap hm, out float[] ao, out float[] sunWarm)
        {
            int w = hm.W, h = hm.H, n = w * h;
            float step = hm.Step;
            // низкочастотная высота: box blur ±10 клеток, два прохода (строки,
            // столбцы) — O(n)
            int win = Mathf.Max(4, Mathf.RoundToInt(160f / step));
            var low = new float[w, h];
            var tmp = new float[w, h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    low[i, j] = hm.Height[i, j];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    double sum = 0; int cnt = 0;
                    for (int k = Mathf.Max(0, i - win); k <= Mathf.Min(w - 1, i + win); k++)
                    { sum += low[k, j]; cnt++; }
                    tmp[i, j] = (float)(sum / cnt);
                }
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    double sum = 0; int cnt = 0;
                    for (int k = Mathf.Max(0, j - win); k <= Mathf.Min(h - 1, j + win); k++)
                    { sum += tmp[i, k]; cnt++; }
                    low[i, j] = (float)(sum / cnt);
                }
            ao = new float[n];
            sunWarm = new float[n];
            double sumAo = 0;
            float relief = Mathf.Max(40f, (hm.Max - hm.Min) * 0.22f);
            // солнце с юго-востока, ~40° над горизонтом (RTS-канон: читаемость)
            float lx = 0.42f, ly = 0.78f, lz = -0.46f;
            float ll = 1f / Mathf.Sqrt(lx * lx + ly * ly + lz * lz);
            lx *= ll; ly *= ll; lz *= ll;
            for (int j = 0; j < h; j++)
            { // ← потерянная скобка WIP: без неё метод закрылся до LastBakeMean
                for (int i = 0; i < w; i++)
                {
                    float height = hm.Height[i, j];
                    float conc = low[i, j] - height;
                    float aoV = 1f - 0.40f * Mathf.Clamp01(conc / relief);
                    float dx = (HeightAt(hm, i - 1, j) - HeightAt(hm, i + 1, j)) / (2f * step);
                    float dz = (HeightAt(hm, i, j - 1) - HeightAt(hm, i, j + 1)) / (2f * step);
                    // n = normalize(-dx, 1, -dz); dot(n, L)
                    float nl = Mathf.Sqrt(dx * dx + dz * dz + 1f);
                    float dot = ((-dx) * lx + ly + (-dz) * lz) / nl;
                    aoV *= 0.78f + 0.40f * Mathf.Clamp01(dot);
                    aoV = Mathf.Clamp(aoV, 0f, 1f);
                    ao[j * w + i] = aoV; // row-major: SampleBilinearF ждёт [y*w+x]
                    sunWarm[j * w + i] = Mathf.Clamp(dot * 2f - 1f, -1f, 1f);
                    sumAo += aoV;
                }
            }
            LastBakeMean = Mathf.Clamp((float)(sumAo / n), 0.5f, 1f);
            return;
        }

        private static float HeightAt(Heightmap hm, int i, int j)
        {
            i = i < 0 ? 0 : i >= hm.W ? hm.W - 1 : i;
            j = j < 0 ? 0 : j >= hm.H ? hm.H - 1 : j;
            return hm.Height[i, j];
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
        /// выборкой по мешу (SampleHeight): границы воды совпадают с рельефом.
        /// seabed=true — окраска РЕЛЬЕФА (альбедо/вершины): подводная часть —
        /// ДНО (песчаный шельф → тёмная глубина), не цвет поверхности воды;
        /// поверх дна воду красит свой шейдер. Иначе (карта/миникарта) —
        /// подводная часть синяя, как вид сверху (плейтест 2026-11-04:
        /// «на морских картах вся поверхность голубоватая» — дно красилось
        /// водой и накладывалось на воду второй раз).</summary>
        private static Color ZoneColor(Heightmap hm, ScenarioTemplate.TerrainSettings t,
            ZonePalette pal, float[,] distToWater, float x, float z, bool seabed = false)
        {
            float h = hm.SampleHeight(x, z);
            int ci = Clamp((int)(x / hm.Step + hm.W * 0.5f), 0, hm.W - 1);
            int cj = Clamp((int)(z / hm.Step + hm.H * 0.5f), 0, hm.H - 1);

            if (h < hm.SeaLevel)
            {
                float depth = hm.SeaLevel - h;
                if (seabed)
                {
                    // ДНО для альбедо рельефа: песчаный шельф темнеет с глубиной
                    // (сплат-маска под водой тоже песок — согласовано).
                    Color shelf = pal.Beach * 0.92f;
                    Color deep = pal.Beach * 0.38f;
                    return Color.Lerp(shelf, deep, Mathf.Clamp01(depth / 90f));
                }
                // Вода (карта/миникарта, вид сверху): мель светлее, глубина темнее.
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

            // Мокрая кромка (v0.154): тёмная влажная полоса у самой воды —
            // без неё пляж «пластиковый» и одинаковый по всей длине берега.
            if (t.wetBandM > 0f)
            {
                float wet = 1f - Smooth01((distToWater[ci, cj] - t.wetBandM * 0.25f) /
                                          (t.wetBandM * 0.75f));
                land *= 1f - 0.22f * wet;
            }

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
        /// пляжной полосы. Общий chamfer живёт в Heightmap.DistToWaterM — вода
        /// там ровно «ниже уровня моря» (семантика маски Water из RebuildMasks);
        /// им же пользуется берег-жим генератора.</summary>
        private static float[,] DistToWaterM(Heightmap hm) =>
            Heightmap.DistToWaterM(hm.Height, hm.W, hm.Step, hm.SeaLevel);

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
        public static Sprite MapSprite(Heightmap hm, ScenarioTemplate.TerrainSettings t) =>
            MapSpriteFromPixels(RenderPixels(hm, t, MinimapRes));

        /// <summary>Спрайт из ГОТОВЫХ пикселей (главный поток): асинхронная
        /// генерация приносит пиксели с воркера — те же, что пойдут в
        /// TerrainColor/MapImage (единый вызов RenderPixels 1024²).</summary>
        internal static Sprite MapSpriteFromPixels(Color32[] px)
        {
            int res = Mathf.RoundToInt(Mathf.Sqrt(px.Length)); // из буфера (2048²)
            Texture2D tex = TexFromPixels(px, res, "SN-MapImage", readable: true, mips: false);
            return Sprite.Create(tex, new Rect(0f, 0f, res, res),
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
            Color c = ZoneColor(hm, t, _palCache, _distCache, x, z, seabed: true);
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
            // СВОЙ парсер hex (раньше ColorUtility.TryParseHtmlString): ECall
            // движка недоступен на воркере асинхронной генерации и в консольном
            // раннере тестов — парсер должен быть чистой математикой. Семантика
            // прежняя: #RRGGBB[AA], фолбэк — зелёный с предупреждением.
            string h = hex != null ? hex.Trim().TrimStart('#') : null;
            if (h != null && (h.Length == 6 || h.Length == 8) && HexVal(h) >= 0)
            {
                float r = HexVal(h.Substring(0, 2)) / 255f;
                float g = HexVal(h.Substring(2, 2)) / 255f;
                float b = HexVal(h.Substring(4, 2)) / 255f;
                float a = h.Length == 8 ? HexVal(h.Substring(6, 2)) / 255f : 1f;
                return new Color(r, g, b, a);
            }
            Log.Warn("MapGen: не разобрать цвет '" + hex + "' — использую зелёный");
            return new Color(0.3f, 0.45f, 0.25f);
        }

        /// <summary>Значение hex-пары (и -1, если внутри не шестнадцатеричный).</summary>
        private static int HexVal(string pair)
        {
            int v = 0;
            foreach (char c in pair)
            {
                int d = c >= '0' && c <= '9' ? c - '0'
                    : c >= 'a' && c <= 'f' ? c - 'a' + 10
                    : c >= 'A' && c <= 'F' ? c - 'A' + 10
                    : -1;
                if (d < 0)
                    return -1;
                v = v * 16 + d;
            }
            return v;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
