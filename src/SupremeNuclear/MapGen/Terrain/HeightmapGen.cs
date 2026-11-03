using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

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

        /// <summary>Финальный фолбэк выбора точки: НЕ КРАЙ (воду/крутость под
        /// точкой исправит штамп площадки). Край — единственное абсолютное
        /// «нельзя» (краевые горы).</summary>
        public bool StampableOk(float x, float z)
        {
            int i = Clamp((int)(x / Step + W * 0.5f), 0, W - 1);
            int j = Clamp((int)(z / Step + H * 0.5f), 0, H - 1);
            return !Edge[i, j];
        }

        /// <summary>Ровный коридор проезда (юзер-канон 2026-11-02 «участки под
        /// проезд техники — плоские и ровные»): полоса вдоль отрезка a→b —
        /// профиль вдоль линии усредняется окном ~400 м (рельеф сохраняется,
        /// террасные ступени/выбросы сглаживаются), поперёк лента выравнивается
        /// на высоту профиля. Вода не трогается (наземка через море не ездит);
        /// кромка мягкая (2 клетки). Пары вызовов с X-зеркальными отрезками
        /// сохраняют симметрию карты.</summary>
        public void SmoothCorridor(Vector2 a, Vector2 b, float widthM)
        {
            float len = (a - b).magnitude;
            if (len < Step * 4f)
                return;
            int steps = Math.Max(2, (int)(len / Step));
            float invSteps = 1f / steps;

            // Профиль высот вдоль линии (по выборке меша) + скользящее среднее.
            int win = Math.Max(1, (int)(400f / Step));
            var profile = new float[steps + 1];
            for (int s = 0; s <= steps; s++)
            {
                Vector2 p = Vector2.Lerp(a, b, s * invSteps);
                profile[s] = SampleHeight(p.x, p.y);
            }
            var smooth = new float[steps + 1];
            for (int s = 0; s <= steps; s++)
            {
                float sum = 0f;
                int cnt = 0;
                for (int k = Math.Max(0, s - win); k <= Math.Min(steps, s + win); k++)
                {
                    sum += profile[k];
                    cnt++;
                }
                smooth[s] = sum / cnt;
            }

            // Клетки полосы: расстояние до отрезка + параметр t → сглаженный профиль.
            float w = Math.Max(Step * 2f, widthM);
            float rim = 2f * Step;
            var dir = (b - a) / len;
            int i0 = Clamp((int)((Math.Min(a.x, b.x) - w - rim) / Step + W * 0.5f) - 1, 0, W - 1);
            int i1 = Clamp((int)((Math.Max(a.x, b.x) + w + rim) / Step + W * 0.5f) + 1, 0, W - 1);
            int j0 = Clamp((int)((Math.Min(a.y, b.y) - w - rim) / Step + H * 0.5f) - 1, 0, H - 1);
            int j1 = Clamp((int)((Math.Max(a.y, b.y) + w + rim) / Step + H * 0.5f) + 1, 0, H - 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float x = (i + 0.5f - W * 0.5f) * Step;
                    float z = (j + 0.5f - H * 0.5f) * Step;
                    Vector2 ap = new Vector2(x - a.x, z - a.y);
                    float t = Vector2.Dot(ap, dir);
                    float d = Math.Abs(ap.x * -dir.y + ap.y * dir.x); // расстояние до линии
                    if (t < -rim || t > len + rim || d > w + rim)
                        continue;
                    if (Height[i, j] < SeaLevel)
                        continue; // вода не выравниваем
                    float tc = ClampF(t / len * steps, 0f, steps);
                    int k0 = Math.Min(steps, (int)tc);
                    int k1 = Math.Min(steps, k0 + 1);
                    float target = Lerp(smooth[k0], smooth[k1], tc - k0);
                    float weight = 1f - Smoothstep01((d - w * 0.5f) / (w * 0.5f + rim));
                    Height[i, j] = Lerp(Height[i, j], Math.Max(target, SeaLevel + 5f), weight);
                }
        }

        private static float Smoothstep01(float x)
        {
            x = x < 0f ? 0f : x > 1f ? 1f : x;
            return x * x * (3f - 2f * x);
        }

        /// <summary>Пересчёт масок Water/Plain/Steep/Edge + Min/Max по текущим
        /// Height (тем же правилом, что и генератор). Вызывается после штампов
        /// площадок — плоскость и суша внутри штампа становятся свойством масок.</summary>
        public void RebuildMasks(ScenarioTemplate.TerrainSettings t)
        {
            float steepTan = (float)Math.Tan(t.steepDeg * Math.PI / 180.0);
            int edgeCells = (int)(t.edgeM / Step);
            float min = float.MaxValue, max = float.MinValue;
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    float h = Height[i, j];
                    if (h < min) min = h;
                    if (h > max) max = h;
                    Water[i, j] = h < SeaLevel;
                    float dh = Math.Max(
                        Math.Abs(h - Height[Math.Min(W - 1, i + 1), j]),
                        Math.Abs(h - Height[i, Math.Min(H - 1, j + 1)]));
                    Steep[i, j] = !Water[i, j] && dh / Step > steepTan;
                    Plain[i, j] = !Water[i, j] && !Steep[i, j];
                    Edge[i, j] = i < edgeCells || j < edgeCells ||
                                 i >= W - edgeCells || j >= H - edgeCells;
                }
            Min = min;
            Max = max;
        }

        /// <summary>ШТАМП круглой ровной площадки (гарантия спавна, юзер-канон
        /// 2026-10-31 «ровная площадка, нет воды»): клетки радиуса выравниваются
        /// на высоту центра (кламп ≥ море+5 — пруд под точкой становится сушей),
        /// кромка в 2 клетки линейно сходит к исходному рельефу (ступень террасы
        /// 70 м / 400 м кромки = 10° — ниже порога Steep). После штампов маски
        /// пересчитывает RebuildMasks. Отклонение от Neroxis осознанное: он
        /// выбирает точки по маскам без деформации (у него нет террас), нам
        /// плоскость гарантирует только штамп.</summary>
        public void StampPad(float cx, float cz, float radiusM)
        {
            int ci = Clamp((int)(cx / Step + W * 0.5f), 0, W - 1);
            int cj = Clamp((int)(cz / Step + H * 0.5f), 0, H - 1);
            float padH = Math.Max(Height[ci, cj], SeaLevel + 5f);
            float rim = 2f * Step;
            float rMax = Math.Max(1f * Step, radiusM);
            int reach = (int)Math.Ceiling((rMax + rim) / Step);
            for (int dj = -reach; dj <= reach; dj++)
                for (int di = -reach; di <= reach; di++)
                {
                    int i = ci + di, j = cj + dj;
                    if (i < 0 || j < 0 || i >= W || j >= H)
                        continue;
                    float dx = di * Step, dz = dj * Step;
                    float d = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (d <= rMax)
                        Height[i, j] = padH;
                    else if (d <= rMax + rim)
                        Height[i, j] = Lerp(Height[i, j], padH,
                            1f - (d - rMax) / rim);
                }
        }

        /// <summary>Штамп ПРЯМОУГОЛЬНОЙ площадки (базы: полоса вдоль оси — yaw
        /// баз у нас 0°/90°, поворот не нужен; alongX — длинная сторона по X).
        /// Семантика высот/кромки — как у StampPad.</summary>
        public void StampPadRect(float cx, float cz, float halfAlongM, float halfAcrossM, bool alongX)
        {
            int ci = Clamp((int)(cx / Step + W * 0.5f), 0, W - 1);
            int cj = Clamp((int)(cz / Step + H * 0.5f), 0, H - 1);
            float padH = Math.Max(Height[ci, cj], SeaLevel + 5f);
            float rim = 2f * Step;
            float ha = Math.Max(1f * Step, halfAlongM), hc = Math.Max(1f * Step, halfAcrossM);
            float halfLong = alongX ? ha : hc, halfShort = alongX ? hc : ha;
            int reach = (int)Math.Ceiling((halfLong + rim) / Step);
            for (int dj = -reach; dj <= reach; dj++)
                for (int di = -reach; di <= reach; di++)
                {
                    int i = ci + di, j = cj + dj;
                    if (i < 0 || j < 0 || i >= W || j >= H)
                        continue;
                    // «Прямоугольное расстояние» с раздельной кромкой по осям.
                    float along = Math.Abs(di * Step) - halfLong;
                    float across = Math.Abs(dj * Step) - halfShort;
                    float tAlong = along <= 0f ? 0f : Math.Min(1f, along / rim);
                    float tAcross = across <= 0f ? 0f : Math.Min(1f, across / rim);
                    float t = Math.Max(tAlong, tAcross);
                    Height[i, j] = t <= 0f
                        ? padH
                        : Lerp(Height[i, j], padH, 1f - t);
                }
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
        /// рампа растягивается — наземка проходит между плато. Порог — от
        /// террасы и клетки (max): мелкая клетка не должна жевать ступени.</summary>
        internal static void SmoothRamps(float[,] h, int w, float stepM, float terraceStepM)
        {
            float limit = Math.Max(stepM, terraceStepM) * 0.7f; // ~35°
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
    /// value-noise fBm равнин + стилевая морфология воды/суши (Neroxis-канон v3:
    /// land / coast / seas / islands), детерминизм от сида, точечная симметрия.
    /// DESIGN §2.4: ≥50% манёвренных равнин, горы по краям (у land).
    /// </summary>
    public static class HeightmapGen
    {
        /// <summary>Нормализация стиля шаблона (неизвестный → land).</summary>
        public static string StyleOf(string style)
        {
            switch ((style ?? "land").Trim().ToLowerInvariant())
            {
                case "coast": return "coast";
                case "seas": return "seas";
                case "islands": return "islands";
                default: return "land";
            }
        }

        /// <summary>Водные стили: рельеф обязан получить острова под базы
        /// (раскладка баз идёт геометрически, до SiteOk — см. ScenarioWorld).</summary>
        public static bool IsWatery(ScenarioTemplate.TerrainSettings t) =>
            StyleOf(t.style) != "land";

        /// <param name="basePoints">Точки баз (мировые XZ) — на водных стилях
        /// под ними поднимаются острова/мысы (гарантия суши).</param>
        public static Heightmap Generate(ScenarioTemplate.TerrainSettings t, int seed,
            IReadOnlyList<Vector2> basePoints = null)
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
            string style = StyleOf(t.style);
            float water = ClampF(t.waterAmount, 0f, 1f);
            // Симметризация усреднением сжимает дисперсию в √2 — редкопороговые
            // события вымирали (islands: вода 1.00, тест 2026-11-03). Пороги
            // пятен сдвигаем к 0.5 квантильной поправкой (σ/√2 ≈ 0.707).
            string symKind = SymmetryOf(t.symmetry);
            // quadrant усредняет ДВАЖДЫ (оси X и Z) — σ/2; mirror/rotational — σ/√2.
            float symK = symKind == "quadrant" ? 0.5f :
                symKind == "none" ? 1f : 0.707f;
            float ThrSym(float thr) => 0.5f + (thr - 0.5f) * symK;

            // Ручки частот шаблона заданы «на клетку 200 м» (историческая
            // семантика): множитель k сохраняет МИРОВЫЕ волны при любом stepM
            // (v3: клетка по умолчанию 100 м — без k рельеф сжался бы вдвое).
            float k = 200f / t.stepM;

            // Полоса топления кромки в океан (РЕАЛИИ NO, см. ниже в цикле):
            // ~5% стороны, но не шире 2·edgeM и не меньше двух клеток.
            float edgeFall = Math.Max(2f, Math.Min(w * 0.05f, t.edgeM * 2f / map.Step));

            // Ровни — низкочастотный fBm вокруг plainHeight + холмы центра
            // (без них середина карты «мёртвая»: gentleAmp на 40 км не читается);
            // горы — подъём к краям по ЕВКЛИДОВУ радиусу с шумовым возмущением кромки
            // (max-метрика давала ровную КВАДРАТНУЮ раму гор — плейтест 2026-10-29
            // «меш искривляется»), модулированный гребневым шумом. Краевые горы —
            // только у land: у водных стилей барьер карты — океан.
            // Параллельно по строкам (запрос 2026-11-03 «сократить генерацию»):
            // клетки независимы, запись map.Height[i,j] — по индексам.
            Parallel.For(0, w, j =>
            {
                for (int i = 0; i < w; i++) // ПОЛНОЕ поле: симметрия — отдельным
                                             // проходом Symmetrize (усреднение с
                                             // отражением = гладкие оси без швов)
                {
                    float rolling = t.plainHeight +
                                    (Fbm(i * k * t.plainFreq, j * k * t.plainFreq, seed, 4) - 0.5f) * 2f * t.gentleAmp +
                                    (Fbm(i * k * t.hillsFreq + 21.7f, j * k * t.hillsFreq, seed + 999, 4) - 0.5f) * 2f * t.hillsAmp;
                    // Редкие внутренние хребты (DESIGN §2.4 «горы по краям и редкие хребты»):
                    // гребневый шум, вскрытый низкочастотной маской-пятнами.
                    float mask = Smoothstep(t.ridgeMaskStart, t.ridgeMaskEnd,
                        Fbm(i * k * 0.008f + 13.3f, j * k * 0.008f, seed + 555, 3));
                    float ridge = Ridge(i * k * 0.06f + 7.7f, j * k * 0.06f, seed) * mask * t.ridgeHeight;
                    float landH = rolling + ridge;

                    // Радиус (доля half) с изрезанной кромкой — у coast это кромка
                    // континента, у land — рама гор.
                    float dx = i - half + 0.5f, dz = j - half + 0.5f;
                    float r = Math.Min(1f, (float)Math.Sqrt(dx * dx + dz * dz) / half) +
                              (Fbm(i * k * 0.01f + 3.1f, j * k * 0.01f, seed + 333, 3) - 0.5f) * 2f * t.coastWobble;
                    float mtn = Smoothstep(t.mountainStart, 1f, r) * t.mountainHeight;
                    mtn *= 0.65f + 0.7f * Ridge(i * k * 0.03f + 31.7f, j * k * 0.03f, seed);

                    // Мировые координаты клетки — стилевые маски не зависят от шага.
                    float x = (i + 0.5f - half) * map.Step;
                    float z = (j + 0.5f - half) * map.Step;

                    // Расстояние до кромки карты (клеток, по ближайшей стороне).
                    float d = Math.Min(Math.Min(i, w - 1 - i), Math.Min(j, w - 1 - j));

                    float h;
                    switch (style)
                    {
                        case "coast":
                        {
                            // «Окружающая вода»: континент-диск в океане. waterAmount
                            // раздвигает океан (радиус континента вниз). Ядро континента
                            // — монолитная суша (max до +25 м): без этого внутренние
                            // впадины rolling разъедали континент озёрами до ~23% суши
                            // (диагностика тестов v3) — «Dual Gap»-стиль без внутренних морей.
                            float cr = t.coastRadius - 0.35f * water;
                            float land01 = 1f - Smoothstep(cr - t.coastBand, cr + t.coastBand, r);
                            float core = Math.Max(landH, t.seaLevelM + 25f);
                            h = Lerp(t.oceanFloorM, core, land01);
                            break;
                        }
                        case "seas":
                        {
                            // Полуморская: крупные внутренние моря fBm-пятнами,
                            // суша доминирует; краевые горы остаются (море их срезает).
                            float n = Fbm(x / t.seaScaleM + 5.1f, z / t.seaScaleM, seed + 444, 4);
                            float thr = ThrSym(0.68f - 0.30f * water);
                            float sea = Smoothstep(thr, thr + 0.10f, n);
                            h = Lerp(landH + mtn, t.oceanFloorM, sea);
                            break;
                        }
                        case "islands":
                        {
                            // Морская: океан + архипелаг мелких островов fBm-порогом
                            // (острова баз поднимаются отдельно — IslandBump ниже).
                            float n = Fbm(x / t.islandScaleM + 9.7f, z / t.islandScaleM, seed + 888, 4);
                            float thr = ThrSym(0.71f - 0.22f * water);
                            float isle = Smoothstep(thr, thr + 0.12f, n);
                            h = Lerp(t.oceanFloorM, landH, isle);
                            break;
                        }
                        default:
                        {
                            // land: прежняя морфология; waterAmount > 0 добавляет
                            // управляемые озёра (локальные впадины пятнами).
                            h = landH + mtn;
                            if (water > 0.01f)
                            {
                                float n = Fbm(x / (t.seaScaleM * 0.5f) + 3.3f,
                                    z / (t.seaScaleM * 0.5f), seed + 222, 3);
                                float thr = ThrSym(0.86f - 0.36f * water);
                                float lake = Smoothstep(thr, thr + 0.12f, n);
                                h -= lake * (t.plainHeight + t.gentleAmp + t.hillsAmp);
                            }
                            break;
                        }
                    }
                    map.Height[i, j] = h;

                    // РЕАЛИИ NO (юзер 2026-11-02: «карта по сути бесконечная — края
                    // должны заканчиваться без висящих краёв»): вода-плоскость игры
                    // бесконечна (LevelInfo.UpdateWaterPlane следует за камерой), а
                    // стоковые карты NO — участки суши в океане. Топим кромку меша:
                    // последняя полоса (~5% стороны) плавно уходит на дно — торец
                    // меша всегда под водой, горизонт без обрыва. Симметрично
                    // (расстояние до кромки симметрично при любой оси).
                    if (d <= edgeFall)
                        map.Height[i, j] = Lerp(h, t.oceanFloorM,
                            1f - Smoothstep(0f, 1f, d / edgeFall));
                }
            });

            // СИММЕТРИЯ (юзер-канон 2026-11-03 «несколько осей, бесшовно»):
            // усреднение с отражением — паттерн Neroxis: строгая симметрия по
            // построению И гладкая ось (изломов-«швов» нет). ДЛЯ ОСТРОВОВ — MAX:
            // среднее «дно+суша» = мелководье, редкая пороговая суша вымирала
            // (плейтест-диагностика 2026-11-03: islands вода 1.00); max сохраняет
            // острова парами, ось задевает редко. Канонический порядок пары —
            // байт-в-байт точность (float не ассоциативен — урок террас).
            Symmetrize(map.Height, w, t.symmetry, style != "islands");

            // Острова/мысы под базы — ДО террас (кромка террасируется как рельеф).
            // Рельеф зеркален по X (колонки), раскладка — точечная: пишем ЧЕТЫРЬМЯ
            // точками (±x, ±z), держат обе симметрии сразу.
            if (IsWatery(t) && basePoints != null)
                foreach (Vector2 p in basePoints)
                    for (int sx = -1; sx <= 1; sx += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                            IslandBump(map, p.x * sx, p.y * sz, t.baseIslandRadiusM);

            // Террасы SupCom (юзер-канон 2026-10-30: «плоскости и плато»): высоты
            // квантуются в ступени terraceStepM; середина ступени остаётся РАМПОЙ
            // (плавный переход), края — плоские плато. Даёт ровные поля для боя и
            // спавна + высотные плато под «превалирующие бункеры». Применяется ДО
            // масок — Steep/Water посчитаются по террасированным высотам.
            Heightmap.Terrace(map.Height, w, t.terraceStepM);
            // Рампы террас сглаживаются: узкая рампа имеет уклон 1/(2·soft) ~45°+
            // независимо от высоты ступени — наземка ходит между плато на грани.
            // Два прохода усреднения по крутым клеткам разводят уклон (~30°),
            // слегка скругляя кромки плато (как у SupCom). Порог — от террасы И
            // клетки: при stepM 100 м порог «0.7·клетки» жевал бы ступени 70 м.
            Heightmap.SmoothRamps(map.Height, w, t.stepM, t.terraceStepM);

            // Классификация масок по финальным высотам (после зеркалирования).
            map.RebuildMasks(t);
            return map;
        }

        /// <summary>Нормализация режима симметрии шаблона (неизвестный → mirror).</summary>
        public static string SymmetryOf(string symmetry)
        {
            switch ((symmetry ?? "mirror").Trim().ToLowerInvariant())
            {
                case "quadrant": return "quadrant"; // 2 оси: X + Z (четверть отражается 4 раза)
                case "rotational": return "rotational"; // поворот 180° (точечная)
                case "none": return "none"; // без симметрии (полная свобода рельефа)
                default: return "mirror"; // 1 ось X (канон дуэлей)
            }
        }

        /// <summary>Симметризация высот усреднением с отражением. Каждая пара
        /// получает ОДНО значение, посчитанное в каноническом порядке — строгая
        /// симметрия без float-расхождений; усреднение сглаживает ось (нет
        /// зеркальных изломов). quadrant = сначала ось X, затем Z (послойно —
        /// ассоциативность контролируема каноническим порядком каждого прохода).</summary>
        internal static void Symmetrize(float[,] h, int w, string symmetryRaw, bool smooth)
        {
            string symmetry = SymmetryOf(symmetryRaw);
            if (symmetry == "none")
                return;
            // smooth: среднее пары (гладкая ось); иначе max (пороговая суша).
            float Join(float a, float b) => smooth ? (a + b) * 0.5f : Math.Max(a, b);
            if (symmetry == "mirror" || symmetry == "quadrant")
            {
                for (int j = 0; j < w; j++)
                    for (int i = 0; i < w / 2; i++)
                    {
                        int m = w - 1 - i;
                        h[i, j] = h[m, j] = Join(h[i, j], h[m, j]); // first = меньший i
                    }
            }
            if (symmetry == "quadrant")
            {
                for (int j = 0; j < w / 2; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int m = w - 1 - j;
                        h[i, j] = h[i, m] = Join(h[i, j], h[i, m]); // first = меньший j
                    }
            }
            if (symmetry == "rotational")
            {
                // Поворот 180°: пары (i,j)↔(w-1-i, w-1-j); first — меньший j
                // (при равенстве — меньший i): канонический порядок одинаков
                // для обеих клеток пары.
                for (int j = 0; j < w; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int mi = w - 1 - i, mj = w - 1 - j;
                        if (mj < j || (mj == j && mi <= i))
                            continue; // обрабатываем пару один раз, с «первой» клетки
                        h[i, j] = h[mi, mj] = Join(h[i, j], h[mi, mj]);
                    }
            }
        }

        /// <summary>Остров/мыс в точке: пологий купол до высоты равнины — гарантия
        /// суши под базой на водных стилях (штамп площадки потом выровняет центр).
        /// Только поднимает (max) — дно не углубляет. Вызывается до террас.</summary>
        private static void IslandBump(Heightmap map, float cx, float cz, float radiusM)
        {
            int ci = Clamp((int)(cx / map.Step + map.W * 0.5f), 0, map.W - 1);
            int cj = Clamp((int)(cz / map.Step + map.H * 0.5f), 0, map.H - 1);
            float topH = Math.Max(map.Height[ci, cj], map.SeaLevel + 60f);
            float r = Math.Max(map.Step * 4f, radiusM);
            int reach = (int)Math.Ceiling(r / map.Step);
            for (int dj = -reach; dj <= reach; dj++)
                for (int di = -reach; di <= reach; di++)
                {
                    int i = ci + di, j = cj + dj;
                    if (i < 0 || j < 0 || i >= map.W || j >= map.H)
                        continue;
                    float d = (float)Math.Sqrt(di * di + dj * dj) * map.Step;
                    if (d > r)
                        continue;
                    // Широкая вершина (0.45R) и плавный сход к морю.
                    float m = 1f - Smoothstep(r * 0.45f, r, d);
                    float target = Lerp(map.Height[i, j], topH, m);
                    if (target > map.Height[i, j])
                        map.Height[i, j] = target;
                }
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

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        private static float ClampF(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
