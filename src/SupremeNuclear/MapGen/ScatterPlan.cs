using System;
using System.Collections.Generic;
using UnityEngine; // Vector2 — структуры раскладки; вызовов движка нет

namespace SupremeNuclear
{
    /// <summary>
    /// Планировщик визуального дрессинга спец-карты (v0.154, чистая логика —
    /// тестируется): ПОЗИЦИИ деревьев, шрамов (предзапечённые воронки в
    /// _Global_BlastMap) и спотов декалей из масок рельефа. Детерминизм = сид
    /// карты. Симметрия — образами точки (канон CorridorPair): кандидаты
    /// выбираются только из «первичной» области образа, остальные достраиваются
    /// отражением; приём каждой точки перепроверяется по маскам (симметрия
    /// рельефа гарантирует оба образа, проверка — страховка от асимметрии).
    /// Координаты — карто-локальные метры (центр 0 = глобальные при постройке).
    /// Шум — копия схемы Value/Fbm (как в MapTextures; единого шума нет —
    /// правки сид-карты шумов ломают сгенерированные миры).
    /// </summary>
    internal static class ScatterPlan
    {
        /// <summary>Точка дерева (XZ; высоту добирает TerrainTrees по мешу).</summary>
        public sealed class TreeSpot
        {
            public float X, Z;
        }

        /// <summary>Круглое пятно дрессинга: шрам (выжженное) или декаль.
        /// Kind — вид декали: 0 грязь берега, 1 износ (коридоры/кольца баз),
        /// 2 проталина равнины. Strength 0..1 — сила проявления.</summary>
        public sealed class Blob
        {
            public float X, Z, Radius, Strength;
            public int Kind;
        }

        /// <summary>Данные+запреты планировщика: маски последнего мира (ссылки,
        /// только чтение) + pads/коридоры раскладки + поля зон.</summary>
        public sealed class Constraints
        {
            public Heightmap Hm;
            public List<PadZone> Pads = new List<PadZone>();
            public List<CorridorSeg> Corridors = new List<CorridorSeg>();
            public float BeachWidthM;
            public float SnowHeightM;
            public float[,] DistToWater; // метры до воды, по клеткам

            public float SampleHeight(float x, float z) => Hm.SampleHeight(x, z);
        }

        /// <summary>Собрать ограничения из мира генерации (pads/коридоры —
        /// WorldGen, высоты/маски — Heightmap). world может быть null (стоковый
        /// путь — запреты только по маскам).</summary>
        public static Constraints From(Heightmap hm, WorldGen world,
            ScenarioTemplate.TerrainSettings t)
        {
            return new Constraints
            {
                Hm = hm,
                Pads = world != null ? world.Pads : new List<PadZone>(),
                Corridors = world != null ? world.Corridors : new List<CorridorSeg>(),
                BeachWidthM = Mathf.Max(1f, t.beachWidthM),
                SnowHeightM = t.snowHeightM,
                DistToWater = Heightmap.DistToWaterM(hm.Height, hm.W, hm.Step, hm.SeaLevel),
            };
        }

        // ---------- Общественные метрики (используются и тестами) ----------

        /// <summary>Расстояние точки до полосы коридора (XZ, метры).</summary>
        public static float DistToSegXZ(float px, float pz, CorridorSeg s)
        {
            float abx = s.B.x - s.A.x, abz = s.B.y - s.A.y;
            float lenSq = abx * abx + abz * abz;
            if (lenSq < 1e-6f)
                return Mathf.Sqrt((px - s.A.x) * (px - s.A.x) + (pz - s.A.y) * (pz - s.A.y));
            float t = Mathf.Clamp(((px - s.A.x) * abx + (pz - s.A.y) * abz) / lenSq, 0f, 1f);
            float dx = px - (s.A.x + abx * t), dz = pz - (s.A.y + abz * t);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static bool InAnyPad(List<PadZone> pads, float x, float z, float margin)
        {
            if (pads == null)
                return false;
            foreach (PadZone p in pads)
                if (p.Contains(x, z, margin))
                    return true;
            return false;
        }

        public static bool InAnyCorridor(List<CorridorSeg> segs, float x, float z, float margin)
        {
            if (segs == null)
                return false;
            foreach (CorridorSeg s in segs)
                if (DistToSegXZ(x, z, s) <= s.HalfW + margin)
                    return true;
            return false;
        }

        // ---------- Симметрия (образы точки, канон CorridorPair) ----------

        /// <summary>Точка в «первичной» области образа симметрии (кандидаты
        /// выбираются только отсюда — без дублей при отражении).</summary>
        private static bool Primary(float x, float z, string sym)
        {
            switch (sym)
            {
                case "quadrant": return x >= 0f && z >= 0f;
                case "rotational": return z >= 0f;
                case "none": return true;
                default: return x >= 0f; // mirror — канон дуэлей
            }
        }

        /// <summary>Все образы точки при симметрии карты (первичный — первым).</summary>
        private static List<Vector2> Images(Vector2 p, string sym)
        {
            var list = new List<Vector2>(4) { p };
            switch (sym)
            {
                case "quadrant":
                    list.Add(new Vector2(p.x, -p.y));
                    list.Add(new Vector2(-p.x, p.y));
                    list.Add(new Vector2(-p.x, -p.y));
                    break;
                case "rotational":
                    list.Add(new Vector2(-p.x, -p.y));
                    break;
                case "none":
                    break;
                default: // mirror
                    list.Add(new Vector2(-p.x, p.y));
                    break;
            }
            return list;
        }

        /// <summary>Все зеркальные образы первичных коридоров (для износа
        /// сплат-маски — она обязана быть симметричной, как рельеф). Концы
        /// сегмента отражаются ОДНИМ образом — прочие комбинации дали бы
        /// отрезки, которых нет на рельефе.</summary>
        public static List<CorridorSeg> SymmetricCorridors(List<CorridorSeg> primary, string sym)
        {
            var all = new List<CorridorSeg>();
            if (primary == null)
                return all;
            foreach (CorridorSeg s in primary)
            {
                List<Vector2> ia = Images(s.A, sym);
                List<Vector2> ib = Images(s.B, sym);
                for (int k = 0; k < ia.Count; k++)
                    all.Add(new CorridorSeg { A = ia[k], B = ib[k], HalfW = s.HalfW });
            }
            return all;
        }

        // ---------- Деревья ----------

        /// <summary>Позиции деревьев: кластеры fBm на равнинах (маски Plain),
        /// вне пляжа/снега/площадок/коридоров. Возвращает XZ; шаг сетки 40 м,
        /// cap 60000 (сдвиговым прореживанием).</summary>
        public static List<TreeSpot> Trees(Constraints c, ScenarioTemplate.TerrainSettings t, int seed)
        {
            var result = new List<TreeSpot>();
            if (c?.Hm == null || t == null || t.treeDensity <= 0f)
                return result;
            Heightmap hm = c.Hm;
            float size = hm.W * hm.Step;
            float half = size * 0.5f;
            float clusterThr = 0.80f - 0.30f * Mathf.Clamp01(t.treeDensity);
            float keepProb = 0.06f + 0.22f * Mathf.Clamp01(t.treeDensity);
            const float grid = 40f;
            var rng = new System.Random(seed);
            string sym = t.symmetry;
            bool snowLine = t.snowHeightM < hm.Max;

            int n = Mathf.CeilToInt(size / grid);
            for (int j = 0; j < n; j++)
            {
                float z = (j + 0.5f) * grid - half;
                for (int i = 0; i < n; i++)
                {
                    float x = (i + 0.5f) * grid - half;
                    if (!Primary(x, z, sym))
                        continue;
                    if (rng.NextDouble() >= keepProb)
                        continue;
                    float xj = x + ((float)rng.NextDouble() - 0.5f) * grid * 0.9f;
                    float zj = z + ((float)rng.NextDouble() - 0.5f) * grid * 0.9f;
                    if (!Primary(xj, zj, sym) || !AcceptTree(c, xj, zj, clusterThr, snowLine))
                        continue;
                    foreach (Vector2 im in Images(new Vector2(xj, zj), sym))
                        if (AcceptTree(c, im.x, im.y, clusterThr, snowLine))
                            result.Add(new TreeSpot { X = im.x, Z = im.y });
                }
            }
            return Cap(result, 60000);
        }

        /// <summary>Пригодность точки под дерево: суша-равнина, вне пляжа,
        /// ниже снега, вне площадок/коридоров, в лесном кластере (fBm). Шум
        /// семплится от (|x|, |z|) — иначе асимметричный шум принимал
        /// первичную точку и отвергал её зеркальный образ (тест пар 2026-11-05).
        /// Рельеф и запреты симметричны — остальное согласовано само.</summary>
        private static bool AcceptTree(Constraints c, float x, float z, float clusterThr, bool snowLine)
        {
            Heightmap hm = c.Hm;
            if (!hm.SiteOk(x, z)) // не вода, не склон, не край
                return false;
            int ci = Clamp((int)(x / hm.Step + hm.W * 0.5f), 0, hm.W - 1);
            int cj = Clamp((int)(z / hm.Step + hm.H * 0.5f), 0, hm.H - 1);
            if (c.DistToWater[ci, cj] < c.BeachWidthM)
                return false;
            float h = hm.Height[ci, cj];
            if (h < hm.SeaLevel + 3f)
                return false;
            if (snowLine && h > c.SnowHeightM - 80f)
                return false;
            if (InAnyPad(c.Pads, x, z, 120f))
                return false;
            if (InAnyCorridor(c.Corridors, x, z, 60f))
                return false;
            float nx = Math.Abs(x), nz = Math.Abs(z);
            if (Fbm(nx * 0.00045f, nz * 0.00045f, 911, 3) <= clusterThr)
                return false;
            if (Fbm(nx * 0.0025f, nz * 0.0025f, 917, 2) <= 0.42f) // прогалы внутри рощи
                return false;
            return true;
        }

        // ---------- Шрамы (предзапечённые выжженные пятна) ----------

        /// <summary>Выжженные шрамы: вдоль коридоров, у центра, случайные на
        /// равнинах. Радиусы ≥ 60 м (порог DrawBlast — 40 м после ×2 множителя).</summary>
        public static List<Blob> Scars(Constraints c, ScenarioTemplate.TerrainSettings t, int seed)
        {
            var result = new List<Blob>();
            if (c?.Hm == null || t == null || t.battleScars <= 0f)
                return result;
            Heightmap hm = c.Hm;
            float size = hm.W * hm.Step;
            var rng = new System.Random(seed);
            string sym = t.symmetry;

            // Коридоры: точки вдоль отрезков (сражения идут по трассам).
            if (c.Corridors != null)
                foreach (CorridorSeg s in c.Corridors)
                {
                    float len = Vector2.Distance(s.A, s.B);
                    if (len < 900f)
                        continue;
                    int steps = Mathf.Clamp((int)(len / 1400f), 1, 6);
                    for (int k = 0; k < steps; k++)
                    {
                        float f = (k + 0.5f) / steps;
                        Vector2 p = Vector2.Lerp(s.A, s.B, f);
                        p += new Vector2(((float)rng.NextDouble() - 0.5f) * 500f,
                                         ((float)rng.NextDouble() - 0.5f) * 500f);
                        EmitLandBlob(result, c, p.x, p.y, 70f + (float)rng.NextDouble() * 130f,
                            1f, 0, sym, 24, 3f);
                    }
                }

            // Центр карты: главная арена.
            for (int k = 0; k < 2; k++)
            {
                float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                float dist = 700f + (float)rng.NextDouble() * 800f;
                Vector2 p = new Vector2(Mathf.Cos(ang) * dist, Mathf.Sin(ang) * dist);
                EmitLandBlob(result, c, p.x, p.y, 120f + (float)rng.NextDouble() * 100f,
                    1f, 0, sym, 24, 3f);
            }

            // Случайные равнины: масштаб по ручке battleScars.
            int extra = Mathf.RoundToInt(10f * Mathf.Clamp01(t.battleScars));
            for (int k = 0; k < extra; k++)
            {
                float x = ((float)rng.NextDouble() - 0.5f) * size * 0.8f;
                float z = ((float)rng.NextDouble() - 0.5f) * size * 0.8f;
                EmitLandBlob(result, c, x, z, 60f + (float)rng.NextDouble() * 100f,
                    1f, 0, sym, 24, 3f);
            }
            return result;
        }

        // ---------- Декали ----------

        /// <summary>Споты декалей: грязь у кромки воды (Kind 0), износ вдоль
        /// коридоров и кольца баз (Kind 1), проталины равнин (Kind 2).</summary>
        public static List<Blob> Decals(Constraints c, ScenarioTemplate.TerrainSettings t, int seed)
        {
            var result = new List<Blob>();
            if (c?.Hm == null || t == null || t.decalAmount <= 0f)
                return result;
            Heightmap hm = c.Hm;
            float size = hm.W * hm.Step;
            float half = size * 0.5f;
            var rng = new System.Random(seed);
            string sym = t.symmetry;
            float amount = Mathf.Clamp01(t.decalAmount);

            // Кромка воды: мокрая грязь (только над водой, у самой кромки).
            const int shoreGrid = 170;
            int sn = Mathf.CeilToInt(size / shoreGrid);
            for (int j = 0; j < sn; j++)
            {
                float z = (j + 0.5f) * shoreGrid - half;
                for (int i = 0; i < sn; i++)
                {
                    float x = (i + 0.5f) * shoreGrid - half;
                    if (!Primary(x, z, sym) || rng.NextDouble() >= 0.5f * amount)
                        continue;
                    float xj = x + ((float)rng.NextDouble() - 0.5f) * shoreGrid * 0.9f;
                    float zj = z + ((float)rng.NextDouble() - 0.5f) * shoreGrid * 0.9f;
                    int ci = Clamp((int)(xj / hm.Step + hm.W * 0.5f), 0, hm.W - 1);
                    int cj = Clamp((int)(zj / hm.Step + hm.H * 0.5f), 0, hm.H - 1);
                    if (c.DistToWater[ci, cj] < 15f || c.DistToWater[ci, cj] > 135f)
                        continue;
                    if (!hm.SiteOk(xj, zj))
                        continue;
                    EmitLandBlob(result, c, xj, zj, 12f + (float)rng.NextDouble() * 22f,
                        0.55f, 0, sym, 160, 0f);
                }
            }

            // Коридоры: потёртости по обочинам (рядом с полосой, не на ней).
            if (c.Corridors != null)
                foreach (CorridorSeg s in c.Corridors)
                {
                    float len = Vector2.Distance(s.A, s.B);
                    if (len < 600f)
                        continue;
                    int steps = Mathf.Clamp((int)(len / 380f), 1, 14);
                    Vector2 dir = (s.B - s.A) / len;
                    Vector2 perp = new Vector2(-dir.y, dir.x);
                    for (int k = 0; k < steps; k++)
                    {
                        float f = (k + 0.5f) / steps;
                        float side = k % 2 == 0 ? 1f : -1f;
                        float off = s.HalfW + 12f + (float)rng.NextDouble() * 26f;
                        Vector2 p = s.A + dir * (len * f) + perp * (off * side);
                        p += new Vector2(((float)rng.NextDouble() - 0.5f) * 60f,
                                         ((float)rng.NextDouble() - 0.5f) * 60f);
                        EmitLandBlob(result, c, p.x, p.y, 10f + (float)rng.NextDouble() * 12f,
                            0.85f, 1, sym, 200, 0f);
                    }
                }

            // Кольца баз: износ у выездов (прямоугольные площадки — базы).
            if (c.Pads != null)
                foreach (PadZone pad in c.Pads)
                {
                    if (pad.Circle || !Primary(pad.Center.x, pad.Center.y, sym))
                        continue;
                    for (int k = 0; k < 4; k++)
                    {
                        float ang = k * Mathf.PI * 0.5f + (float)rng.NextDouble() * 0.8f;
                        float reach = Mathf.Max(pad.HalfA, pad.HalfB) + 20f;
                        Vector2 p = pad.Center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * reach;
                        EmitLandBlob(result, c, p.x, p.y, 14f + (float)rng.NextDouble() * 10f,
                            0.8f, 1, sym, 64, 0f);
                    }
                }

            // Проталины равнин: крупные светлые пятна, редкие.
            const int plainGrid = 700;
            int pn = Mathf.CeilToInt(size / plainGrid);
            for (int j = 0; j < pn; j++)
            {
                float z = (j + 0.5f) * plainGrid - half;
                for (int i = 0; i < pn; i++)
                {
                    float x = (i + 0.5f) * plainGrid - half;
                    if (!Primary(x, z, sym) || rng.NextDouble() >= 0.35f * amount)
                        continue;
                    float xj = x + ((float)rng.NextDouble() - 0.5f) * plainGrid * 0.8f;
                    float zj = z + ((float)rng.NextDouble() - 0.5f) * plainGrid * 0.8f;
                    if (!hm.SiteOk(xj, zj) || InAnyPad(c.Pads, xj, zj, 60f))
                        continue;
                    EmitLandBlob(result, c, xj, zj, 45f + (float)rng.NextDouble() * 45f,
                        0.3f, 2, sym, 80, 0f);
                }
            }
            return Cap(result, 400);
        }

        /// <summary>Пятно по образам симметрии с проверкой суши каждого образа
        /// (minAbove — метры над уровнем моря: шрамы ≥ 3, декали — не в воде).</summary>
        private static void EmitLandBlob(List<Blob> into, Constraints c, float x, float z,
            float radius, float strength, int kind, string sym, int cap, float minAbove)
        {
            if (into.Count >= cap)
                return;
            float minH = c.Hm.SeaLevel + minAbove;
            if (c.SampleHeight(x, z) < minH)
                return;
            foreach (Vector2 im in Images(new Vector2(x, z), sym))
            {
                if (c.SampleHeight(im.x, im.y) < minH)
                    continue;
                if (into.Count >= cap)
                    return;
                into.Add(new Blob { X = im.x, Z = im.y, Radius = radius, Strength = strength, Kind = kind });
            }
        }

        /// <summary>Прореживание сдвигом до cap (сохраняет покрытие площади).</summary>
        private static List<T> Cap<T>(List<T> list, int cap)
        {
            if (list.Count <= cap)
                return list;
            int stride = Mathf.CeilToInt(list.Count / (float)cap);
            var slim = new List<T>(cap);
            for (int i = 0; i < list.Count; i += stride)
                slim.Add(list[i]);
            return slim;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // ---- value-noise (копия схемы MapTextures/HeightmapGen) ----

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
    }
}
