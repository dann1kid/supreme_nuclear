using System;

namespace SupremeNuclear
{
    /// <summary>
    /// Сгенерированный рельеф спец-карты: высоты + МАСКИ пригодности (паттерн Neroxis:
    /// раскладка ресурсов работает по маскам, а не рейкастами). Симметрия точечная —
    /// генерим половину по X, зеркалим (как ScenarioLayout.Mirror). y=0 — уровень моря
    /// (Datum.LocalSeaY); вся игра берёт высоты рейкастами по Statics-коллайдерам,
    /// которые строятся из Height в TerrainMesh.
    /// </summary>
    public sealed class Heightmap
    {
        public int W, H;             // клеток по осям
        public float Step;           // метров на клетку
        public float SeaLevel;       // метров; ниже — вода
        public float[,] Height;      // метров над уровнем моря
        public bool[,] Water;        // Height < SeaLevel
        public bool[,] Plain;        // проходимая суша: не вода и не крутизна
        public bool[,] Steep;        // склон/гора круче порога — непроходимо
        public bool[,] Edge;         // краевая зона — ничего не ставим
        public float Min, Max;

        /// <summary>Выборка высоты по локальным координатам мира (x, z) —
        /// ТРЕУГОЛЬНАЯ, ровно по triangulation меша (TerrainMesh: ячейка делится
        /// диагональю b→c на треугольники a-c-b и b-c-d). Билинейная расползалась
        /// с мешем на метры на крутых склонах — юниты миссии спавнились ПОД
        /// поверхностью и улетали сквозь карту («все юниты пропали», плейтест
        /// 2026-10-30: база на холме 334 м). Узловая координата: вершина i стоит
        /// на x = i*Step − W*Step/2, кламп в [0..W−1] диапазоном узлов.</summary>
        public float SampleHeight(float x, float z)
        {
            float fx = ClampF(x / Step + W * 0.5f, 0f, W - 1f);
            float fz = ClampF(z / Step + H * 0.5f, 0f, H - 1f);
            int x0 = Math.Min(W - 1, (int)fx);
            int z0 = Math.Min(H - 1, (int)fz);
            int x1 = Math.Min(W - 1, x0 + 1);
            int z1 = Math.Min(H - 1, z0 + 1);
            float tx = fx - x0, tz = fz - z0;
            float h00 = Height[x0, z0], h10 = Height[x1, z0];
            float h01 = Height[x0, z1], h11 = Height[x1, z1];
            // Диагональ ячейки b→c (b=(x1,z0), c=(x0,z1)): ниже — плоскость a,b,c,
            // выше — плоскость b,c,d (линейная интерполяция по каждой).
            if (tx + tz < 1f)
                return h00 + (h10 - h00) * tx + (h01 - h00) * tz;
            float ux = 1f - tx, uz = 1f - tz;
            return h11 + (h01 - h11) * ux + (h10 - h11) * uz;
        }

        /// <summary>«Плоское пятно» (юзер-канон 2026-10-30: споты — на ровных
        /// площадках): окно ±2 клетки, перепад ≤ 10 м — террасное плато целиком,
        /// без кромок и рамп.</summary>
        public bool FlatOk(float x, float z)
        {
            int ci = Clamp((int)(x / Step + W * 0.5f), 0, W - 1);
            int cj = Clamp((int)(z / Step + H * 0.5f), 0, H - 1);
            if (!SiteOk(x, z))
                return false;
            float h = Height[ci, cj];
            for (int dj = -2; dj <= 2; dj++)
                for (int di = -2; di <= 2; di++)
                {
                    int i = Clamp(ci + di, 0, W - 1);
                    int j = Clamp(cj + dj, 0, H - 1);
                    if (System.Math.Abs(Height[i, j] - h) > 10f)
                        return false;
                }
            return true;
        }

        /// <summary>Пригодность точки под раскладку (базы/месторождения): не вода,
        /// не склон, не край. Клеточные маски сэмплируются по центрам клеток.</summary>
        public bool SiteOk(float x, float z)
        {
            int i = Clamp((int)(x / Step + W * 0.5f), 0, W - 1);
            int j = Clamp((int)(z / Step + H * 0.5f), 0, H - 1);
            return !Water[i, j] && !Steep[i, j] && !Edge[i, j];
        }

        /// <summary>Мягкое террасирование (SupCom-канон: «плоскости и плато»):
        /// плато по краям ступени, рампа — в середине. frac в [0, 0.5−s) → уровень
        /// ниже; (0.5+s, 1] → выше; между — сглаженный подъём (без излома
        /// коллайдера). Самодостаточен — вызывается генератором до масок.</summary>
        internal static void Terrace(float[,] height, int w, float stepM)
        {
            if (stepM <= 1f)
                return;
            const float soft = 0.11f; // половина ширины рампы в долях ступени (уже — больше плато)
            for (int j = 0; j < w; j++)
                for (int i = 0; i < w; i++)
                {
                    float h = height[i, j];
                    // Сдвиг сетки на полступени: плато на ±(k+0.5)*step, НУЛЕВОЙ
                    // уровень недостижим — без сдвига низины прижимались к 0 =
                    // уровню моря и юниты на «плато-0» мгновенно тонули
                    // (плейтест 2026-10-30: «инженеры потерялись», hp 100%).
                    float q = h / stepM - 0.5f;
                    float fq = (float)Math.Floor(q);
                    float frac = q - fq;
                    float t = (frac - (0.5f - soft)) / (2f * soft);
                    t = t < 0f ? 0f : (t > 1f ? 1f : t);
                    float s = t * t * (3f - 2f * t);
                    height[i, j] = (fq + s) * stepM + 0.5f * stepM;
                }
        }

        /// <summary>Развод крутых кромок: клетки с перепадом к соседу круче
        /// ~35° усредняются с соседями (2 прохода). Кромки плато скругляются,
        /// рампа растягивается — наземка проходит между плато.</summary>
        internal static void SmoothRamps(float[,] h, int w, float stepM)
        {
            float limit = stepM * 0.7f; // ~35° на клетку
            // Через буфер ИЗ ИСХОДНЫХ значений: in-place усреднение слева-направо
            // ломало зеркальную симметрию карты (тест симметрии, 2026-10-30).
            var src = new float[w, w];
            for (int pass = 0; pass < 2; pass++)
            {
                System.Array.Copy(h, src, h.Length);
                for (int j = 1; j < w - 1; j++)
                    for (int i = 1; i < w - 1; i++)
                    {
                        float c = src[i, j];
                        // Канонический порядок слагаемых: обход окрестности у
                        // зеркальной пары идёт в зеркальном порядке, а float-
                        // сложение не ассоциативно — младшие биты расходились и
                        // ломали точную симметрию карты (тест, 2026-10-30).
                        _nb[0] = src[i - 1, j - 1]; _nb[1] = src[i, j - 1]; _nb[2] = src[i + 1, j - 1];
                        _nb[3] = src[i - 1, j]; _nb[4] = src[i + 1, j];
                        _nb[5] = src[i - 1, j + 1]; _nb[6] = src[i, j + 1]; _nb[7] = src[i + 1, j + 1];
                        System.Array.Sort(_nb);
                        float maxDh = 0f;
                        for (int k = 0; k < 8; k++)
                        {
                            float d = System.Math.Abs(_nb[k] - c);
                            if (d > maxDh) maxDh = d;
                        }
                        float sum = c;
                        for (int k = 0; k < 8; k++)
                            sum += _nb[k];
                        if (maxDh > limit)
                            h[i, j] = (c + sum / 9f) * 0.5f; // полшага к среднему
                    }
            }
        }

        private static readonly float[] _nb = new float[8];

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static float ClampF(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    /// <summary>
    /// Чистый генератор рельефа (без Unity-вызовов, тестируется раннером):
    /// value-noise fBm равнин + краевые горные маски (DESIGN §2.4: ≥50% манёвренных
    /// равнин, горы по краям), детерминизм от сида, точечная симметрия.
    /// </summary>
    public static class HeightmapGen
    {
        public static Heightmap Generate(ScenarioTemplate.TerrainSettings t, int seed)
        {
            int size = Math.Max(64, (int)(t.sizeM / t.stepM));
            int w = size & ~1; // чётное — точное зеркалирование
            var map = new Heightmap
            {
                W = w,
                H = w,
                Step = t.sizeM / w,
                SeaLevel = t.seaLevelM,
                Height = new float[w, w],
                Water = new bool[w, w],
                Plain = new bool[w, w],
                Steep = new bool[w, w],
                Edge = new bool[w, w],
            };

            float half = w * 0.5f;
            float steepTan = (float)Math.Tan(t.steepDeg * Math.PI / 180.0);
            int edgeCells = (int)(t.edgeM / map.Step);

            // Ровни — низкочастотный fBm вокруг plainHeight + холмы центра
            // (без них середина карты «мёртвая»: gentleAmp на 40 км не читается);
            // горы — подъём к краям по ЕВКЛИДОВУ радиусу с шумовым возмущением кромки
            // (max-метрика давала ровную КВАДРАТНУЮ раму гор — плейтест 2026-10-29
            // «меш искривляется»), модулированный гребневым шумом.
            for (int j = 0; j < w; j++)
            {
                for (int i = 0; i <= w / 2; i++) // половина + центральная колонка
                {
                    float rolling = t.plainHeight +
                                    (Fbm(i * t.plainFreq, j * t.plainFreq, seed, 4) - 0.5f) * 2f * t.gentleAmp +
                                    (Fbm(i * t.hillsFreq + 21.7f, j * t.hillsFreq, seed + 999, 4) - 0.5f) * 2f * t.hillsAmp;
                    // Редкие внутренние хребты (DESIGN §2.4 «горы по краям и редкие хребты»):
                    // гребневый шум, вскрытый низкочастотной маской-пятнами.
                    float mask = Smoothstep(t.ridgeMaskStart, t.ridgeMaskEnd,
                        Fbm(i * 0.008f + 13.3f, j * 0.008f, seed + 555, 3));
                    float ridge = Ridge(i * 0.06f + 7.7f, j * 0.06f, seed) * mask * t.ridgeHeight;
                    float dx = i - half + 0.5f, dz = j - half + 0.5f;
                    float r = Math.Min(1f, (float)Math.Sqrt(dx * dx + dz * dz) / half) +
                              (Fbm(i * 0.01f + 3.1f, j * 0.01f, seed + 333, 3) - 0.5f) * 2f * t.coastWobble;
                    float mtn = Smoothstep(t.mountainStart, 1f, r) * t.mountainHeight;
                    mtn *= 0.65f + 0.7f * Ridge(i * 0.03f + 31.7f, j * 0.03f, seed);
                    float h = rolling + ridge + mtn;
                    map.Height[i, j] = h;
                    map.Height[w - 1 - i, j] = h; // точечная симметрия по X
                }
            }

            // Террасы SupCom (юзер-канон 2026-10-30: «плоскости и плато»): высоты
            // квантуются в ступени terraceStepM; середина ступени остаётся РАМПОЙ
            // (плавный переход), края — плоские плато. Даёт ровные поля для боя и
            // спавна + высотные плато под «превалирующие бункеры». Применяется ДО
            // масок — Steep/Water посчитаются по террасированным высотам.
            Heightmap.Terrace(map.Height, w, t.terraceStepM);
            // Рампы террас сглаживаются: узкая рампа имеет уклон 1/(2·soft) ~45°+
            // независимо от высоты ступени — наземка ходит между плато на грани.
            // Два прохода усреднения по крутым клеткам разводят уклон (~30°),
            // слегка скругляя кромки плато (как у SupCom).
            Heightmap.SmoothRamps(map.Height, w, t.stepM);

            // Классификация масок по финальным высотам (после зеркалирования).
            float min = float.MaxValue, max = float.MinValue;
            for (int j = 0; j < w; j++)
                for (int i = 0; i < w; i++)
                {
                    float h = map.Height[i, j];
                    if (h < min) min = h;
                    if (h > max) max = h;
                    map.Water[i, j] = h < map.SeaLevel;
                    float dh = Math.Max(
                        Math.Abs(h - map.Height[Math.Min(w - 1, i + 1), j]),
                        Math.Abs(h - map.Height[i, Math.Min(w - 1, j + 1)]));
                    map.Steep[i, j] = !map.Water[i, j] && dh / map.Step > steepTan;
                    map.Plain[i, j] = !map.Water[i, j] && !map.Steep[i, j];
                    map.Edge[i, j] = i < edgeCells || j < edgeCells ||
                                     i >= w - edgeCells || j >= w - edgeCells;
                }
            map.Min = min;
            map.Max = max;
            return map;
        }

        /// <summary>Value-noise 2D: детерминированный хэш решётки + бикубическое сглаживание.</summary>
        private static float Value(float x, float y, int seed)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float tx = Smooth01(x - ix), ty = Smooth01(y - iy);
            float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed);
            float c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
            return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
        }

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

        /// <summary>Гребневый шум 0..1 (1 — хребет): 1 − |2·шум − 1|.</summary>
        private static float Ridge(float x, float y, int seed)
        {
            float n = Fbm(x, y, seed + 777, 3);
            return 1f - Math.Abs(2f * n - 1f);
        }

        private static float Hash(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
        }

        private static float Smooth01(float t) => t * t * (3f - 2f * t);

        private static float Smoothstep(float a, float b, float x)
        {
            float t = (x - a) / (b - a);
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            return Smooth01(t);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
