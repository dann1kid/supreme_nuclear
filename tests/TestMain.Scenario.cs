using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    // Часть TestMain: сценарий, рельеф, стили карт, туман.
    internal static partial class TestMain
    {
        // сценарий/рельеф/трассы/AI (чистая логика MapGen) (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsScenario()
        {
            // ---------- ScenarioLayout: геометрия сценария (DESIGN §2.4) ----------
            var tmpl = new ScenarioTemplate { depositCount = 8, centerRichCount = 2, depositSpacing = 500f };
            UnityEngine.Vector2 vmin = new UnityEngine.Vector2(-50000f, -40000f), vmax = new UnityEngine.Vector2(50000f, 40000f);
            UnityEngine.Vector2 cc = ScenarioLayout.Center(vmin, vmax);
            var basesA = ScenarioLayout.Bases(vmin, vmax, tmpl, new System.Random(42), p => true);
            var basesB = ScenarioLayout.Bases(vmin, vmax, tmpl, new System.Random(42), p => true);
            Check("Scenario: базы найдены на открытой карте", basesA.Count == 1);
            Check("Scenario: детерминизм (сид)", basesA[0].Pos == basesB[0].Pos);
            Check("Scenario: база на длинной оси",
                UnityEngine.Mathf.Abs(basesA[0].Pos.y - cc.y) < 0.01f && basesA[0].Pos.x > cc.x);
            Check("Scenario: расстояние до центра = inset×half",
                Math.Abs(UnityEngine.Vector2.Distance(basesA[0].Pos, cc) - 0.40f * 50000f) < 0.01f);
            Check("Scenario: зеркалирование точки",
                ScenarioLayout.Mirror(new UnityEngine.Vector2(10f, 20f), cc) ==
                new UnityEngine.Vector2(cc.x * 2f - 10f, cc.y * 2f - 20f));
            Check("Scenario: непригодная карта — баз нет",
                ScenarioLayout.Bases(vmin, vmax, tmpl, new System.Random(42), p => false).Count == 0);

            var deps = ScenarioLayout.Deposits(vmin, vmax, tmpl, new System.Random(7), p => true, basesA);
            bool allMirrored = true, spacingOk = true;
            foreach (ScenarioLayout.Deposit d in deps)
            {
                bool hasPair = false;
                foreach (UnityEngine.Vector2 img in ScenarioLayout.SymmetryImages(d.Pos, cc, "mirror"))
                    foreach (ScenarioLayout.Deposit o in deps)
                        if ((o.Pos - img).sqrMagnitude < 0.01f)
                            hasPair = true;
                allMirrored &= hasPair;
            }
            for (int i = 0; i < deps.Count && spacingOk; i++)
                for (int j = i + 1; j < deps.Count; j++)
                    if ((deps[i].Pos - deps[j].Pos).sqrMagnitude < 300f * 300f)
                        spacingOk = false;
            // Пары — ОБРАЗЫ симметрии карты (X-зеркало, не точечное отражение:
            // точечное на X-рельефе рвало симметрию, 2026-11-03).
            Check("Scenario: мексовка симметрична образами X", allMirrored, deps.Count + " точек");
            Check("Scenario: спейсинг соблюдён", spacingOk);
            Check("Scenario: счёт чётный (пары)", deps.Count > 0 && deps.Count % 2 == 0,
                deps.Count + " точек");
            bool hasRich = false;
            foreach (ScenarioLayout.Deposit d in deps)
                if (d.Tier == 2) hasRich = true;
            Check("Scenario: жирный центр есть (T2)", hasRich);
            var depsDet1 = ScenarioLayout.Deposits(vmin, vmax, tmpl, new System.Random(9), p => true, basesA);
            var depsDet2 = ScenarioLayout.Deposits(vmin, vmax, tmpl, new System.Random(9), p => true, basesA);
            bool detOk = depsDet1.Count == depsDet2.Count;
            for (int i = 0; detOk && i < depsDet1.Count; i++)
                detOk = depsDet1[i].Pos == depsDet2[i].Pos;
            Check("Scenario: мексовка детерминирована", detOk);
            // Вода (непригодные зоны): генератор добирает счёт из СУХИХ точек —
            // проверяем, что ни одна не попала в «воду», и что сплошная вода даёт пусто.
            var depsWater = ScenarioLayout.Deposits(vmin, vmax, tmpl, new System.Random(9),
                p => UnityEngine.Mathf.Abs(p.x) > 20000f, basesA);
            bool dryOk = depsWater.Count > 0;
            foreach (ScenarioLayout.Deposit d in depsWater)
                dryOk &= UnityEngine.Mathf.Abs(d.Pos.x) > 20000f;
            Check("Scenario: раскладка уважает воду (все точки сухие)", dryOk,
                depsWater.Count + " точек");
            Check("Scenario: сплошная вода — пустая раскладка без падения",
                ScenarioLayout.Deposits(vmin, vmax, tmpl, new System.Random(9), p => false, basesA).Count == 0);

            // ---------- Спавн-мексы у баз + симметрия всех режимов (2026-11-03) ----------
            {
                var t2 = new ScenarioTemplate { spawnMexCount = 4, spawnMexRadiusM = 850f };
                foreach (string sym in new[] { "mirror", "quadrant", "rotational", "none" })
                {
                    var mexes = ScenarioLayout.SpawnMexes(t2, new System.Random(7),
                        vmin, vmax, basesA, p => true, null, sym);
                    Check("SpawnMexes: кольцо у базы есть (" + sym + ")", mexes.Count >= 4,
                        mexes.Count + " точек");
                    bool orbitOk = true;
                    foreach (ScenarioLayout.Deposit d in mexes)
                    {
                        UnityEngine.Vector2[] imgs = ScenarioLayout.SymmetryImages(d.Pos, cc, sym);
                        if (imgs.Length == 0)
                            continue;
                        bool hasImage = false;
                        foreach (UnityEngine.Vector2 img in imgs)
                            foreach (ScenarioLayout.Deposit e in mexes)
                                if ((e.Pos - img).sqrMagnitude < 0.01f)
                                    hasImage = true;
                        orbitOk &= hasImage;
                    }
                    Check("SpawnMexes: орбиты симметрии полны (" + sym + ")", orbitOk);
                    bool nearBase = mexes.Count == 0;
                    foreach (ScenarioLayout.Deposit d in mexes)
                        foreach (ScenarioLayout.BaseSlot b in basesA)
                            if (UnityEngine.Vector2.Distance(d.Pos, b.Pos) < 2000f)
                                nearBase = true;
                    Check("SpawnMexes: точки у спавнов <2 км (" + sym + ")", nearBase);
                }
            }

            // ---------- HeightmapGen: рельеф спец-карты (Neroxis-паттерн, DESIGN §2.4) ----------
            // stepM 80 — калибровочная сетка Duel-шаблонов: множитель частот
            // k=stepM/32 (мировой инвариант, 2026-11-03) на 80 даёт то же 2.5,
            // что и прежнее 200/stepM — террасный тест в исходном масштабе
            // (при 200 формулы расходятся в 6 раз, плоских клеток 17% вместо 30+).
            // Берег-жим выключен: террасный инвариант «плоских ≥15%» калиброван
            // без пляжной полосы (сам жим — отдельной генерацией ниже).
            var terrainParams = new ScenarioTemplate.TerrainSettings
            { sizeM = 16000f, stepM = 80f, edgeM = 2000f, coastFlatM = 0f };
            var hm = HeightmapGen.Generate(terrainParams, 1234);
            var hm2 = HeightmapGen.Generate(terrainParams, 1234);
            int expectCells = (int)(terrainParams.sizeM / terrainParams.stepM);
            Check("Heightmap: размер чётный и по шагу",
                hm.W == expectCells && hm.H == expectCells && hm.W == hm.H);
            bool symOk = true, hmDetOk = hm.W == hm2.W;
            for (int j = 0; j < hm.H && (symOk || hmDetOk); j++)
                for (int i = 0; i < hm.W; i++)
                {
                    if (hm.Height[i, j] != hm.Height[hm.W - 1 - i, j])
                    {
                        symOk = false;
                    }
                    if (hmDetOk && hm.Height[i, j] != hm2.Height[i, j])
                        hmDetOk = false;
                }
            Check("Heightmap: точная симметрия по X", symOk);
            Check("Heightmap: детерминизм сида", hmDetOk);
            bool maskOk = true;
            int plainCnt = 0, inner = 0;
            for (int j = 0; j < hm.H; j++)
                for (int i = 0; i < hm.W; i++)
                {
                    if (hm.Water[i, j] == (hm.Height[i, j] >= hm.SeaLevel))
                        maskOk = false; // Water ⇔ ниже уровня моря
                    if (hm.Plain[i, j] && (hm.Water[i, j] || hm.Steep[i, j]))
                        maskOk = false; // Plain — не вода и не склон
                    if (!hm.Edge[i, j])
                    {
                        inner++;
                        if (hm.Plain[i, j])
                            plainCnt++;
                    }
                }
            Check("Heightmap: маски согласованы с высотами", maskOk);
            Check("Heightmap: равнины ≥ 50% внутри (DESIGN §2.4)",
                inner > 0 && plainCnt / (float)inner >= 0.5f,
                "plain " + (plainCnt / (float)inner).ToString("F2"));
            bool hmEdgeOk = hm.Edge[0, 0] && !hm.Edge[hm.W / 2, hm.H / 2];
            Check("Heightmap: краевая зона у границы, центр свободен", hmEdgeOk);
            float centerH = hm.SampleHeight(0f, 0f);
            Check("Heightmap: билинейная выборка в диапазоне",
                centerH >= hm.Min - 0.01f && centerH <= hm.Max + 0.01f);
            // Регрессия «плоская карта» (2026-10-29): Clamp01 индекса узла давал всем
            // X высоту первой колонки — меш строился плоским при живом heightmap.
            float half = terrainParams.sizeM * 0.5f;
            float sxMin = float.MaxValue, sxMax = float.MinValue;
            for (float sx = -half * 0.9f; sx <= half * 0.9f; sx += terrainParams.stepM)
            {
                float h = hm.SampleHeight(sx, half * 0.25f);
                if (h < sxMin) sxMin = h;
                if (h > sxMax) sxMax = h;
            }
            Check("Heightmap: выборка меняется по X (не первая колонка)",
                sxMax - sxMin > terrainParams.gentleAmp * 0.5f,
                "разброс " + (sxMax - sxMin).ToString("F0") + " м");
            bool nodeOk = true;
            for (int i = 0; i < hm.W; i += hm.W / 8)
                if (Math.Abs(hm.SampleHeight(i * hm.Step - half, 0f) - hm.Height[i, hm.H / 2]) > 0.01f)
                    nodeOk = false;
            Check("Heightmap: выборка совпадает с узлами решётки", nodeOk);
            // Террасы SupCom (2026-10-30): плоских клеток (|dh| к соседу ≈ 0) стало
            // заметно больше — «плоскости и плато» вместо сплошных склонов. Считаем
            // только сушу ЗА кромкой: край карты утоплен в океан полосой ~5% стороны
            // (реалии NO, 2026-11-02) — переходный спад и подводные склоны не обязаны
            // быть плато. Берег-жим (v0.150.1) — тоже: полоса coastFlatM от уреза —
            // НАМЕРЕННЫЙ пологий спуск к воде, не плато.
            {
                int rim = (int)Math.Max(2f, Math.Min(hm.W * 0.05f,
                    terrainParams.edgeM * 2f / hm.Step)) + 2;
                float[,] flatDist = Heightmap.DistToWaterM(hm.Height, hm.W, hm.Step, hm.SeaLevel);
                float flatBandM = terrainParams.coastFlatM; // полоса жима этого шаблона (0 = берега нет)
                int flat = 0, total = 0;
                for (int j = rim; j < hm.H - rim; j += 3)
                    for (int i = rim; i < hm.W - rim; i += 3)
                    {
                        if (hm.Water[i, j] || flatDist[i, j] < flatBandM)
                            continue;
                        total++;
                        float dh = Math.Abs(hm.Height[i, j] - hm.Height[i + 1, j]) +
                                   Math.Abs(hm.Height[i, j] - hm.Height[i, j + 1]);
                        if (dh < 0.5f)
                            flat++;
                    }
                // Порог 0.15 (было 0.30 при старой семантике k=200/stepM): с
                // мировым инвариантом k=stepM/32 16-км карта даёт ~0.16 плоских
                // в ЛЮБОЙ сетке (80: 0.16, 200: 0.17) — террасы на месте,
                // плотность фич перестала зависеть от шага (2026-11-03).
                Check("Heightmap: террасы — плоских клеток (суша) ≥ 15%",
                    total > 0 && flat / (float)total >= 0.15f,
                    "flat " + (total > 0 ? (flat / (float)total).ToString("F2") : "?"));
            }
            // Террасы: плато ложатся на ±(k+0.5)·step (35/105/175 при step=70),
            // нулевого ПЛОСКОГО уровня нет — «плато-0» топило юнитов мгновенно
            // (плейтест 2026-10-30). Рампа между плато −35/+35 проходит через 0
            // законно — это пологий берег, не плато.
            {
                float[] plateauH = { 15f, 55f, 90f, 130f, 200f };
                float[] expect = { 35f, 35f, 105f, 105f, 175f };
                var probe = new float[5, 5];
                for (int j = 0; j < 5; j++)
                    for (int i = 0; i < 5; i++)
                        probe[j, i] = plateauH[i];
                Heightmap.Terrace(probe, 5, 70f);
                bool plateausOk = true;
                for (int i = 0; i < 5; i++)
                    if (Math.Abs(probe[0, i] - expect[i]) > 0.5f)
                        plateausOk = false;
                Check("Heightmap: плато террас на ±35+70k, уровня моря нет", plateausOk);

            // ---------- Берег-жим (v0.150.1): пологий низкий берег ----------
            // chamfer: колонка воды слева → дистанции по строке (сетка квадратная w×w)
            var shoreGrid = new float[4, 4];
            for (int sj = 0; sj < 4; sj++)
                for (int si = 0; si < 4; si++)
                    shoreGrid[si, sj] = si == 0 ? -10f : 50f;
            float[,] shoreDist = Heightmap.DistToWaterM(shoreGrid, 4, 100f, 0f);
            Check("Берег: chamfer — вода 0, суша по клеткам 100/200/300",
                shoreDist[0, 0] == 0f && Math.Abs(shoreDist[1, 0] - 100f) < 0.01f &&
                Math.Abs(shoreDist[2, 0] - 200f) < 0.01f && Math.Abs(shoreDist[3, 0] - 300f) < 0.01f);
            // диагональ к одинокой воде: ~141.42
            var lonelyGrid = new float[3, 3];
            for (int lj = 0; lj < 3; lj++)
                for (int li = 0; li < 3; li++)
                    lonelyGrid[li, lj] = 50f;
            lonelyGrid[0, 0] = -10f;
            float[,] lonelyDist = Heightmap.DistToWaterM(lonelyGrid, 3, 100f, 0f);
            Check("Берег: chamfer — диагональ ~141",
                Math.Abs(lonelyDist[1, 1] - 141.42f) < 0.5f && lonelyDist[2, 2] > lonelyDist[1, 1]);

            // жим: холм 100 м в 100 м от уреза → рампа 3 м (уклон 0.03)
            Check("Берег: холм у уреза срезается рампой",
                Math.Abs(HeightmapGen.ShoreClamp(100f, 0f, 100f, 900f, 0.03f) - 3f) < 1e-3f);
            Check("Берег: низкая суша не трогается",
                HeightmapGen.ShoreClamp(2f, 0f, 100f, 900f, 0.03f) == 2f);
            Check("Берег: подводная клетка не трогается",
                HeightmapGen.ShoreClamp(-5f, 0f, 0f, 900f, 0.03f) == -5f);
            Check("Берег: за полосой — без изменений",
                HeightmapGen.ShoreClamp(100f, 0f, 900f, 900f, 0.03f) == 100f);
            Check("Берег: выкл (flatM 0) — без изменений",
                HeightmapGen.ShoreClamp(100f, 0f, 100f, 0f, 0.03f) == 100f);
            float shoreMid = HeightmapGen.ShoreClamp(100f, 0f, 650f, 900f, 0.03f);
            float shoreMidCap = 0.03f * 650f;
            Check("Берег: середина полосы — бленд между рампой и исходной",
                shoreMid > shoreMidCap && shoreMid < 100f);

            // Интеграция берег-жима (генерация с берегом): точная симметрия цела
            // (дистанции симметризуются) и в ближней зоне уреза суша не выше рампы.
            var shoreTpl = new ScenarioTemplate.TerrainSettings
            { sizeM = 16000f, stepM = 80f, edgeM = 2000f, style = "seas", waterAmount = 0.6f }; // широкие внутренние моря — вода шире двойной аппарели (узкие озёра land всей кромкой уже 700 м)
            var shoreMap = HeightmapGen.Generate(shoreTpl, 4321);
            bool shoreSymOk = true;
            for (int sj2 = 0; sj2 < shoreMap.H; sj2++)
                for (int si2 = 0; si2 < shoreMap.W; si2++)
                    if (shoreMap.Height[si2, sj2] != shoreMap.Height[shoreMap.W - 1 - si2, sj2])
                        shoreSymOk = false;
            Check("Берег: карта с жимом — точная симметрия цела", shoreSymOk);
            float[,] shoreField = Heightmap.DistToWaterM(
                shoreMap.Height, shoreMap.W, shoreMap.Step, shoreMap.SeaLevel);
            float nearZone = 240f; // 3 клетки: вес бленда здесь ≈ 0 — жёсткая рампа
            float nearCap = shoreMap.SeaLevel + shoreTpl.shoreSlope * nearZone;
            bool nearOk = true;
            for (int sj3 = 0; sj3 < shoreMap.H; sj3 += 2)
                for (int si3 = 0; si3 < shoreMap.W; si3 += 2)
                    if (!shoreMap.Water[si3, sj3] && shoreField[si3, sj3] < nearZone &&
                        shoreMap.Height[si3, sj3] > nearCap + 0.5f)
                        nearOk = false;
            Check("Берег: ближняя зона уреза — суша не выше рампы", nearOk);

            // Глубинный жим (чистая NavDepthClamp)
            Check("Глубина: мель за аппарелью углубляется до navDepth",
                Math.Abs(HeightmapGen.NavDepthClamp(-3f, 0f, 900f, 350f, 12f) - (-12f)) < 1e-3f);
            Check("Глубина: аппарель не трогается",
                HeightmapGen.NavDepthClamp(-3f, 0f, 100f, 350f, 12f) == -3f);
            Check("Глубина: глубокая вода не трогается",
                HeightmapGen.NavDepthClamp(-40f, 0f, 900f, 350f, 12f) == -40f);
            Check("Глубина: суша не трогается",
                HeightmapGen.NavDepthClamp(5f, 0f, 900f, 350f, 12f) == 5f);
            float navBlend = HeightmapGen.NavDepthClamp(-3f, 0f, 525f, 350f, 12f);
            Check("Глубина: вход в полосу — бленд между мелью и navDepth",
                navBlend < -3f && navBlend > -12f);
            Check("Глубина: выкл (0) — без изменений",
                HeightmapGen.NavDepthClamp(-3f, 0f, 900f, 350f, 0f) == -3f);

            // Интеграция глубинного жима: за двойной аппарелью ОТ БЕРЕГА мели нет
            // (дистанция для воды — до суши: DistToWaterM даёт воде ноль!)
            float[,] navField = Heightmap.DistToLandM(
                shoreMap.Height, shoreMap.W, shoreMap.Step, shoreMap.SeaLevel);
            bool navOk = true;
            int navCells = 0, waterTotal = 0;
            for (int sj4 = 0; sj4 < shoreMap.H; sj4 += 2)
                for (int si4 = 0; si4 < shoreMap.W; si4 += 2)
                {
                    if (!shoreMap.Water[si4, sj4])
                        continue;
                    waterTotal++;
                    if (navField[si4, sj4] <= shoreTpl.seaRampM * 2f)
                        continue;
                    navCells++;
                    if (shoreMap.SeaLevel - shoreMap.Height[si4, sj4] < shoreTpl.navDepthM - 0.5f)
                        navOk = false;
                }
            Check("Берег: за аппарелью вода судоходна (≥ navDepthM)", navOk && navCells > 0,
                "клеток " + navCells + " из воды " + waterTotal);

            // ---------- Морские трассы (v0.152.1): решётка рёбер над судоходной водой ----------
            // 3×3 узла (шаг 1000): всё судоходно — 12 рёбер (6 верт + 6 гориз)
            float DeepAll(float x, float z) => 30f;
            var laneAll = SeaLanesGen.BuildEdges(DeepAll, 0f, 0f, 2000f, 2000f, 1000f, 10f);
            Check("SeaLanes: полная решётка 3×3 — 12 рёбер", laneAll.Count == 12,
                "рёбер " + laneAll.Count);
            // Центральный столбец узлов — суша: живы только вертикали по краям (2+2)
            float WithLand(float x, float z) => Math.Abs(x - 1000f) < 300f ? -5f : 30f;
            var laneDetour = SeaLanesGen.BuildEdges(WithLand, 0f, 0f, 2000f, 2000f, 1000f, 10f);
            Check("SeaLanes: суша в центре — 4 ребра по краям, сквозь столбец нет",
                laneDetour.Count == 4 &&
                !laneDetour.Exists(e => Math.Abs(e.A.x - 1000f) < 1f && Math.Abs(e.B.x - 1000f) < 1f),
                "рёбер " + laneDetour.Count);
            // Мель ровно в центре ребра (500,1000) — узлы глубоки, ребро отрезано
            float ShoalMid(float x, float z) => Math.Abs(x - 500f) < 100f && Math.Abs(z - 1000f) < 100f ? 2f : 30f;
            var laneShoal = SeaLanesGen.BuildEdges(ShoalMid, 0f, 0f, 2000f, 2000f, 1000f, 10f);
            Check("SeaLanes: мель в центре ребра — одно ребро отрезано",
                laneShoal.Count == 11, "рёбер " + laneShoal.Count);
            // Открытый океан (промах рейкаста) — судоходен: сетка 2×2 без единого отреза
            Check("SeaLanes: промах (океан) судоходен",
                SeaLanesGen.BuildEdges((x, z) => float.MaxValue, 0f, 0f, 1000f, 1000f, 1000f, 10f).Count == 4);

            // ---------- PassGraph: кластеры террас (Moho path_clusters) ----------
            {
                var h = new float[5, 3];
                for (int j = 0; j < 3; j++) { h[0, j] = 10f; h[1, j] = 10f; h[3, j] = 10f; h[4, j] = 10f; h[2, j] = 100f; }
                PassGraph g = PassGraph.Build(h, 5, 3, 100f, 0f, 100f * 0.75f);
                // хребет-обрыв — самостоятельный кластер; берега не связаны рёбрами
                Check("PassGraph: обрыв делит плато (3 кластера)", g.Count == 3);
                Check("PassGraph: берега без переправы — рёбер нет", g.Edges.Count == 0);
                var h2 = new float[4, 2];
                float[] ramp = { 10f, 25f, 40f, 55f };
                for (int j = 0; j < 2; j++) for (int i = 0; i < 4; i++) h2[i, j] = ramp[i];
                PassGraph g2 = PassGraph.Build(h2, 4, 2, 100f, 0f, 100f * 0.75f);
                Check("PassGraph: рампа связывает плато", g2.Count == 1);
                var h3 = new float[3, 2];
                float[] isle = { 10f, -5f, 10f };
                for (int j = 0; j < 2; j++) for (int i = 0; i < 3; i++) h3[i, j] = isle[i];
                PassGraph g3 = PassGraph.Build(h3, 3, 2, 100f, 0f, 100f * 0.75f);
                Check("PassGraph: острова разделены водой", g3.Count == 2);
                var g4 = PassGraph.Build(h, 5, 3, 100f, 0f, 100f * 0.75f);
                g4.Edges.Add(new[] { 1, 2 });
                Check("PassGraph: Reachable через ребро глубиной 1",
                    g4.Reachable(1, 1).Contains(2) && !g4.Reachable(1, 0).Contains(2));
            }

            // ---------- AiCommander: чистые решения (v3 + M28-паттерны) ----------
            Check("AI: план v3 — уголь первым, затем мексовки, затем депо",
                AiCommanderService.PlanSlot(false, 0, 3, false, 0, 99) == 0 &&
                AiCommanderService.PlanSlot(true, 0, 3, false, 0, 99) == 1 &&
                AiCommanderService.PlanSlot(true, 2, 3, false, 0, 99) == 1 &&
                AiCommanderService.PlanSlot(true, 3, 3, false, 0, 99) == 2);
            Check("AI: план v3 — после депо генераторы до цели волн",
                AiCommanderService.PlanSlot(true, 3, 3, true, 2, 3) == 3 &&
                AiCommanderService.PlanSlot(true, 3, 3, true, 3, 3) == -1);
            Check("AI: энергосталл (Moho StoredRatio) — энергопостройка вне очереди",
                AiCommanderService.PlanSlot(true, 3, 3, true, 3, 3, true) == 3 &&
                AiCommanderService.PlanSlot(false, 3, 3, true, 3, 3, true) == 0 &&
                AiCommanderService.PlanSlot(true, 0, 3, false, 0, 99, true) == 3);
            Check("AI: волна — гарнизон держит базу",
                !AiCommanderService.WaveDue(2, 6, 2, 0f, 100f) &&
                AiCommanderService.WaveDue(6, 6, 2, 0f, 100f) &&
                !AiCommanderService.WaveDue(0, 6, 2, 500f, 100f));
            Check("AI: волна по таймеру — минимум сверх гарнизона и срок вышел",
                AiCommanderService.WaveDue(4, 6, 2, 150f, 100f) &&
                !AiCommanderService.WaveDue(4, 6, 2, 50f, 100f));
            Check("AI: reclaim при масс-сталле (M28)",
                AiCommanderService.ShouldReclaim(100f, 0f, 2f) &&
                !AiCommanderService.ShouldReclaim(100f, 5f, 0f) &&
                !AiCommanderService.ShouldReclaim(1000f, 0f, 5f));
            }
        }
        // стили карт v3, залп-домены, Скримиш-сид, туман войны (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsMapGenFog()
        {
            // ---------- FleetSalvo.FitsDomain: домен станции для ручной волны ----------
            {
                // перевес ВВ — только по воздуху; АС и ничья (правило зеркально ClassOf) — по земле/морю
                Check("Флот-залп: ВВ-ракета бьёт воздушную цель, не наземную",
                    FleetSalvo.FitsDomain(0.93f, 0f, true) && !FleetSalvo.FitsDomain(0.93f, 0f, false));
                Check("Флот-залп: AGM (АС-перевес) бьёт наземную/надводную, не воздушную",
                    FleetSalvo.FitsDomain(0.5f, 0.814f, false) && !FleetSalvo.FitsDomain(0.5f, 0.814f, true));
                Check("Флот-залп: ничья весов — по земле (канон тай-брейка АС)",
                    FleetSalvo.FitsDomain(0.6f, 0.6f, false) && !FleetSalvo.FitsDomain(0.6f, 0.6f, true));

                // Кольцо авиации над центром (чистая функция Util; пользователь —
                // AI-авиация, бывшая орбита командирского борта).
                {
                    var orbit = Util.CommanderOrbitPoints(new UnityEngine.Vector2(1000f, -2000f), 2500f, 1500f, 4);
                    Check("Орбита: 4 точки на окружности радиуса, высота AGL",
                        orbit.Count == 4 &&
                        orbit.TrueForAll(p => UnityEngine.Mathf.Abs(UnityEngine.Mathf.Sqrt(
                            (p.x - 1000f) * (p.x - 1000f) + (p.z + 2000f) * (p.z + 2000f)) - 2500f) < 0.5f) &&
                        orbit.TrueForAll(p => UnityEngine.Mathf.Abs(p.y - 1500f) < 0.01f));
                    Check("Орбита: минимум 3 точки и эквидистанция",
                        Util.CommanderOrbitPoints(UnityEngine.Vector2.zero, 100f, 10f, 0).Count == 3 &&
                        UnityEngine.Mathf.Abs(UnityEngine.Vector3.Distance(orbit[0], orbit[1]) -
                                  UnityEngine.Vector3.Distance(orbit[1], orbit[2])) < 0.5f);
                }

                // MapGen v3: адаптивность под игроков (чистые функции шаблона/раскладки).
                {
                    Check("MapGen: пары игроков — players/2, минимум 1",
                        ScenarioTemplate.PairsOf(2) == 1 && ScenarioTemplate.PairsOf(4) == 2 &&
                        ScenarioTemplate.PairsOf(6) == 3 && ScenarioTemplate.PairsOf(1) == 1 &&
                        ScenarioTemplate.PairsOf(0) == 1);
                    Check("MapGen: размер — явная ручка terrain.sizeM (players масштабирует только раскладку)",
                        ScenarioTemplate.PairsOf(4) == 2);
                    Check("MapGen: смещения K баз симметричны (множество == отражённое)",
                        ScenarioLayout.BaseOffsets(1, 2800f).Length == 1 &&
                        ScenarioLayout.BaseOffsets(2, 2800f)[0] == -ScenarioLayout.BaseOffsets(2, 2800f)[1] &&
                        UnityEngine.Mathf.Abs(ScenarioLayout.BaseOffsets(2, 2800f)[0] + 1400f) < 0.01f &&
                        UnityEngine.Mathf.Abs(ScenarioLayout.BaseOffsets(3, 2800f)[0] + 2800f) < 0.01f &&
                        UnityEngine.Mathf.Abs(ScenarioLayout.BaseOffsets(3, 2800f)[1]) < 0.001f &&
                        UnityEngine.Mathf.Abs(ScenarioLayout.BaseOffsets(4, 2800f)[0] + 4200f) < 0.01f);

                    // Гарантия спавна (ScenarioWorld: выбор по маскам → ШТАМПЫ ровных
                    // площадок → пересчёт масок; регресс «масса 0», плейтест 2026-10-31).
                    {
                        int seed = -679669887; // «плохой» сид юзера
                        var w1 = ScenarioWorld.Generate(new ScenarioTemplate(), seed);
                        bool allFlat = w1.Bases.Count >= 1;
                        foreach (ScenarioLayout.BaseSlot b in w1.Bases)
                            if (!w1.Hm.FlatOk(b.Pos.x, b.Pos.y)) allFlat = false;
                        foreach (ScenarioLayout.Deposit d in w1.Mass)
                            if (!w1.Hm.FlatOk(d.Pos.x, d.Pos.y) || !w1.Hm.SiteOk(d.Pos.x, d.Pos.y)) allFlat = false;
                        foreach (ScenarioLayout.Deposit d in w1.Coal)
                            if (!w1.Hm.FlatOk(d.Pos.x, d.Pos.y) || !w1.Hm.SiteOk(d.Pos.x, d.Pos.y)) allFlat = false;
                        Console.WriteLine("  [диаг] гарантия: баз " + w1.Bases.Count + ", мексов " +
                            w1.Mass.Count + ", угля " + w1.Coal.Count + ", все ровно+суша: " + allFlat);
                        Check("MapGen: гарантия спавна — точки ровные и на суше (регресс «масса 0»)",
                            w1.Bases.Count >= 1 && w1.Mass.Count > 0 && w1.Coal.Count > 0 && allFlat);

                        // Детерминизм: тот же (шаблон, сид) → идентичная раскладка
                        // (пайплайн зовут ScenarioBuild и RuntimeMap независимо).
                        var w2 = ScenarioWorld.Generate(new ScenarioTemplate(), seed);
                        bool same = w1.Bases.Count == w2.Bases.Count && w1.Mass.Count == w2.Mass.Count;
                        if (same)
                            for (int i = 0; i < w1.Mass.Count; i++)
                                if ((w1.Mass[i].Pos - w2.Mass[i].Pos).sqrMagnitude > 0.01f) { same = false; break; }
                        Check("MapGen: детерминизм ScenarioWorld (шаблон+сид → тот же мир)", same);

                        // Штамп осушает воду: клетка-вода (не край) после StampPad —
                        // ровная суша (FlatOk).
                        Heightmap hmS = w1.Hm;
                        float sx = 0f, sz = 0f;
                        bool found = false;
                        for (int j = hmS.W / 4; j < hmS.W * 3 / 4 && !found; j++)
                            for (int i = hmS.W / 4; i < hmS.W * 3 / 4; i++)
                                if (hmS.Water[i, j] && !hmS.Edge[i, j])
                                {
                                    sx = (i - hmS.W * 0.5f) * hmS.Step;
                                    sz = (j - hmS.H * 0.5f) * hmS.Step;
                                    found = true;
                                    break;
                                }
                        if (found)
                        {
                            var tS = new ScenarioTemplate();
                            hmS.StampPad(sx, sz, hmS.Step * 3f);
                            hmS.RebuildMasks(tS.terrain);
                        }
                        Check("MapGen: штамп осушает воду и выравнивает площадку",
                            !found || (hmS.FlatOk(sx, sz) && hmS.SiteOk(sx, sz)),
                            found ? "пруд → ровная суша" : "воды в середине не нашлось");
                    }

                    // ---------- MapGen v3: стили карт (Neroxis land/coast/seas/islands) ----------
                    {
                        // Доля воды по стилям (вода = Height < SeaLevel): пороги
                        // морфологии держат долю в диапазоне — регресс при правке.
                        foreach (string style in new[] { "land", "coast", "seas", "islands" })
                        {
                            var tS = new ScenarioTemplate.TerrainSettings
                            {
                                sizeM = 24000f,
                                stepM = 200f,
                                edgeM = 2000f,
                                style = style,
                                waterAmount = style == "coast" ? 0.30f :
                                    style == "seas" ? 0.55f : style == "islands" ? 0.50f : 0f,
                            };
                            var hS = HeightmapGen.Generate(tS, 4242);
                            int waterCnt = 0;
                            for (int j = 0; j < hS.H; j++)
                                for (int i = 0; i < hS.W; i++)
                                    if (hS.Water[i, j]) waterCnt++;
                            float frac = waterCnt / (float)(hS.W * hS.H);
                            float lo = style == "land" ? 0f : style == "coast" ? 0.30f :
                                style == "seas" ? 0.20f : 0.50f;
                            float hi = style == "land" ? 0.30f : style == "coast" ? 0.68f :
                                style == "seas" ? 0.60f : 0.90f;
                            Console.WriteLine("  [диаг] стиль " + style + ": вода " +
                                frac.ToString("F2") + " (диапазон " + lo.ToString("F2") + ".." + hi.ToString("F2") + ")");
                            Check("MapGen: доля воды в диапазоне стиля " + style,
                                frac >= lo && frac <= hi, "вода " + frac.ToString("F2"));

                            // Точная симметрия X держится и на водных стилях
                            // (бампы баз пишутся четверкой ±x±z — обе симметрии).
                            bool symS = true;
                            for (int j = 0; j < hS.H && symS; j++)
                                for (int i = 0; i < hS.W; i++)
                                    if (hS.Height[i, j] != hS.Height[hS.W - 1 - i, j]) { symS = false; break; }
                            Check("MapGen: симметрия X у стиля " + style, symS);

                            // РЕАЛИИ NO (юзер 2026-11-02 «карта бесконечная — края
                            // без висящих краёв»): кромка меша УТОПЛЕНА в океан у
                            // ВСЕХ стилей (вода-плоскость игры бесконечна).
                            bool rimWater = hS.Water[0, hS.H / 2] && hS.Water[hS.W - 1, hS.H / 2] &&
                                            hS.Water[hS.W / 2, 0] && hS.Water[hS.W / 2, hS.H - 1];
                            Check("MapGen: кромка карты под водой у стиля " + style, rimWater);
                        }

                        // НЕСКОЛЬКО ОСЕЙ СИММЕТРИИ (юзер 2026-11-03): mirror — ось X,
                        // quadrant — оси X и Z, rotational — поворот 180°. Высоты строго.
                        foreach (string sym in new[] { "mirror", "quadrant", "rotational" })
                        {
                            var tSym = new ScenarioTemplate.TerrainSettings
                            {
                                sizeM = 24000f, stepM = 200f, edgeM = 2000f,
                                symmetry = sym, style = "land", waterAmount = 0f,
                            };
                            var hSym = HeightmapGen.Generate(tSym, 2024);
                            bool sx = true;
                            for (int j = 0; j < hSym.H && sx; j++)
                                for (int i = 0; i < hSym.W; i++)
                                    if (hSym.Height[i, j] != hSym.Height[hSym.W - 1 - i, j]) { sx = false; break; }
                            bool sz = true;
                            for (int j = 0; j < hSym.H && sz; j++)
                                for (int i = 0; i < hSym.W; i++)
                                    if (hSym.Height[i, j] != hSym.Height[i, hSym.H - 1 - j]) { sz = false; break; }
                            bool srot = true;
                            for (int j = 0; j < hSym.H && srot; j++)
                                for (int i = 0; i < hSym.W; i++)
                                    if (hSym.Height[i, j] != hSym.Height[hSym.W - 1 - i, hSym.H - 1 - j]) { srot = false; break; }
                            bool expectX = sym == "mirror" || sym == "quadrant";
                            bool expectZ = sym == "quadrant";
                            bool expectRot = sym == "rotational" || sym == "quadrant";
                            Console.WriteLine("  [диаг] симметрия " + sym + ": X " + sx + ", Z " + sz + ", поворот " + srot);
                            Check("MapGen: оси симметрии '" + sym + "'",
                                sx == expectX && sz == expectZ && srot == expectRot);
                        }

                        // Водные стили: базы — геометрически (до SiteOk), суша под
                        // ними — остров/мыс IslandBump; после пайплайна все точки
                        // сухие. Связность баз наземкой — кроме islands (океан).
                        foreach (string style in new[] { "coast", "seas", "islands" })
                        {
                            var tmplW = new ScenarioTemplate
                            {
                                terrain = new ScenarioTemplate.TerrainSettings
                                {
                                    sizeM = 24000f,
                                    stepM = 200f,
                                    edgeM = 2000f,
                                    style = style,
                                    waterAmount = style == "coast" ? 0.30f :
                                        style == "seas" ? 0.55f : 0.50f,
                                    baseIslandRadiusM = 5500f,
                                },
                            };
                            var wW = ScenarioWorld.Generate(tmplW, 777);
                            bool dryW = wW.Bases.Count >= 1 && wW.Mass.Count > 0 && wW.Coal.Count > 0;
                            foreach (ScenarioLayout.BaseSlot b in wW.Bases)
                                if (!wW.Hm.SiteOk(b.Pos.x, b.Pos.y)) dryW = false;
                            foreach (ScenarioLayout.Deposit d in wW.Mass)
                                if (!wW.Hm.SiteOk(d.Pos.x, d.Pos.y)) dryW = false;
                            foreach (ScenarioLayout.Deposit d in wW.Coal)
                                if (!wW.Hm.SiteOk(d.Pos.x, d.Pos.y)) dryW = false;
                            Console.WriteLine("  [диаг] " + style + ": баз " + wW.Bases.Count +
                                ", мексов " + wW.Mass.Count + ", угля " + wW.Coal.Count +
                                ", все сухие: " + dryW);
                            Check("MapGen: " + style + " — базы/точки на суше", dryW,
                                "баз " + wW.Bases.Count + ", мексов " + wW.Mass.Count);

                            if (style != "islands" && wW.Bases.Count >= 2)
                            {
                                PassGraph gW = PassGraph.Build(wW.Hm.Height, wW.Hm.W, wW.Hm.H,
                                    wW.Hm.Step, wW.Hm.SeaLevel, wW.Hm.Step * 0.75f);
                                int cA = gW.ClusterAt(wW.Bases[0].Pos.x, wW.Bases[0].Pos.y, wW.Hm.Step);
                                int cB = gW.ClusterAt(wW.Bases[wW.Bases.Count - 1].Pos.x,
                                    wW.Bases[wW.Bases.Count - 1].Pos.y, wW.Hm.Step);
                                bool linked = cA > 0 && cB > 0 && gW.Reachable(cA, 100000).Contains(cB);
                                Check("MapGen: " + style + " — базы связаны наземно (PassGraph)", linked,
                                    "кластеры " + cA + "→" + cB + ", всего " + gW.Count);
                            }
                        }

                        // Хеш морфологии в ключе карты: правка стиля/шага/воды/
                        // палитры меняет мир — ключ обязан различаться (урок
                        // X8-сида: один ключ не накрывает два мира); одинаковые
                        // настройки дают одинаковый хеш.
                        {
                            var tA = new ScenarioTemplate.TerrainSettings();
                            var tB = new ScenarioTemplate.TerrainSettings();
                            Check("MapGen: MorphHash стабилен",
                                tA.MorphHash() == tB.MorphHash(), tA.MorphHash());
                            bool styleDiff = tA.MorphHash() !=
                                new ScenarioTemplate.TerrainSettings { style = "islands" }.MorphHash();
                            bool stepDiff = tA.MorphHash() !=
                                new ScenarioTemplate.TerrainSettings { stepM = 50f }.MorphHash();
                            bool waterDiff = tA.MorphHash() !=
                                new ScenarioTemplate.TerrainSettings { waterAmount = 0.5f }.MorphHash();
                            bool palDiff = tA.MorphHash() !=
                                new ScenarioTemplate.TerrainSettings { palette = new[] { "#000000", "#ffffff" } }.MorphHash();
                            Check("MapGen: MorphHash различает стиль/шаг/воду/палитру",
                                styleDiff && stepDiff && waterDiff && palDiff);
                        }
                    }

                // Тиры фабрик (§2.3а): решение AI на апгрейд + границы терцилей
                // посева ростера (UnitRoster.TercileCuts).
                {
                    Check("Тиры: AI решает «развитие» при профиците (2× цены, инженеры)",
                        AiCommanderService.FactoryUpgradeWanted(1, false, 3000f, 1500f, 2, 2) &&
                        !AiCommanderService.FactoryUpgradeWanted(1, false, 2999f, 1500f, 2, 2) &&
                        !AiCommanderService.FactoryUpgradeWanted(1, true, 9999f, 1500f, 2, 2) &&
                        !AiCommanderService.FactoryUpgradeWanted(1, false, 9999f, 1500f, 1, 2) &&
                        !AiCommanderService.FactoryUpgradeWanted(3, false, 9999f, 0f, 5, 2));
                    UnitRoster.TercileCuts(9, out int i1a, out int i2a);
                    Check("Терцили: 9 позиций → 3+3+3", i1a == 3 && i2a == 6, i1a + "/" + i2a);
                    UnitRoster.TercileCuts(1, out int i1b, out int i2b);
                    Check("Терцили: одна позиция не теряется (T1)", i1b == 1 && i2b == 1);
                    UnitRoster.TercileCuts(2, out int i1c, out int i2c);
                    Check("Терцили: две позиции → 1+1", i1c == 1 && i2c == 2);
                    UnitRoster.TercileCuts(4, out int i1d, out int i2d);
                    Check("Терцили: четыре позиции → 1+1+2", i1d == 1 && i2d == 2, i1d + "/" + i2d);
                }

                // Ростер юнитов: фильтр по ЯВНЫМ тирам (UnitRoster).
                {
                    var unitsR = new System.Collections.Generic.List<(int Id, int Tier)>
                        { (1, 1), (2, 1), (3, 2), (4, 2), (5, 3), (6, 1), (7, 3) };
                    var allR = new System.Collections.Generic.List<(int Id, int Tier)>(unitsR);
                    Catalog.TierFilterByTier(allR, u => u.Tier, 0, 3);
                    Check("Ростер: «Все» без гейта — всё",
                        allR.Count == 7);
                    var g1 = new System.Collections.Generic.List<(int Id, int Tier)>(unitsR);
                    Catalog.TierFilterByTier(g1, u => u.Tier, 0, 1);
                    Check("Ростер: «Все» при потолке T1 — только T1",
                        g1.Count == 3 && g1.TrueForAll(u => u.Tier == 1));
                    var g2 = new System.Collections.Generic.List<(int Id, int Tier)>(unitsR);
                    Catalog.TierFilterByTier(g2, u => u.Tier, 0, 2);
                    Check("Ростер: «Все» при потолке T2 — T1+T2",
                        g2.Count == 5 && g2.TrueForAll(u => u.Tier <= 2));
                    var sel3 = new System.Collections.Generic.List<(int Id, int Tier)>(unitsR);
                    Catalog.TierFilterByTier(sel3, u => u.Tier, 3, 3);
                    Check("Ростер: выбранный T3 — точное равенство",
                        sel3.Count == 2 && sel3.TrueForAll(u => u.Tier == 3));
                    var clampR = new System.Collections.Generic.List<(int Id, int Tier)>(unitsR);
                    Catalog.TierFilterByTier(clampR, u => u.Tier, 3, 1);
                    Check("Ростер: выбранный T3 выше потолка — кламп в T1",
                        clampR.Count == 3 && clampR.TrueForAll(u => u.Tier == 1));
                }

                // Снабжение v1 (§2.14): гейт залпа «хватает обеих валют».
                {
                    Check("Снабжение: залп разрешён при запасе, запрещён при дефиците любой валюты",
                        SupplyService.SalvoAllowed(100f, 100f, 25f, 20f, true) &&
                        !SupplyService.SalvoAllowed(24f, 100f, 25f, 20f, true) &&
                        !SupplyService.SalvoAllowed(100f, 19f, 25f, 20f, true) &&
                        !SupplyService.SalvoAllowed(0f, 0f, 25f, 20f, false) && // масса гейтится и без энергослоя
                        SupplyService.SalvoAllowed(25f, 0f, 25f, 20f, false) && // энергослой не активен — энергия не гейтится
                        SupplyService.SalvoAllowed(10f, 10f, 0f, 0f, true));   // нулевая цена — всегда да
                }

                // Ключ спец-карты (Скримиш v0.143): формат MapGenKey — игроки в
                // ключе (анти-коллизия миров 1×1/2×2), шаблоны с дефисами, старые ключи v1.
                {
                    string key = MapGenKey.Build("Duel-Land", -1, "AB12CD34", 82, 4);
                    Check("MapGenKey: сборка — дефис в шаблоне, сид X8, суффиксы размер/игроки",
                        key == "SN-Terrain-Duel-Land-FFFFFFFF-AB12CD34-82k-p4");
                    Check("MapGenKey: полный разбор (round-trip)",
                        MapGenKey.TryParse(key, out MapGenKey.Parsed p) &&
                        p.Template == "Duel-Land" && p.Seed == -1 && p.Morph == "AB12CD34" &&
                        p.SizeK == 82 && p.Players == 4);
                    string v3 = MapGenKey.Build("duel", 305419896, "00000000");
                    Check("MapGenKey: без суффиксов — null-поля, формат v3",
                        v3 == "SN-Terrain-duel-12345678-00000000" &&
                        MapGenKey.TryParse(v3, out MapGenKey.Parsed p2) &&
                        p2.Template == "duel" && p2.Seed == 305419896 &&
                        p2.SizeK == null && p2.Players == null);
                    Check("MapGenKey: старый ключ v1 (4-симв. сид последним токеном)",
                        MapGenKey.TryParse("SN-Terrain-duel-ABCD", out MapGenKey.Parsed p3) &&
                        p3.Template == "duel" && p3.Seed == 0xABCD && p3.Morph == null);
                    Check("MapGenKey: чужой префикс/без сида/пустой шаблон — мусор",
                        !MapGenKey.TryParse("Kola", out _) &&
                        !MapGenKey.TryParse("SN-Terrain-duel", out _) &&
                        !MapGenKey.TryParse("SN-Terrain--ABCD", out _));
                    Check("MapGenKey: игроки разводят ключи одной карты",
                        MapGenKey.Build("t", 5, "M", null, 2) != MapGenKey.Build("t", 5, "M", null, 4));
                }

                // Сид панели Скримиша и пары игроков.
                {
                    Check("Скримиш: сид — пусто/пробел/мусор = 0 (случайный)",
                        SkirmishService.ParseSeed("") == 0 &&
                        SkirmishService.ParseSeed("  ") == 0 &&
                        SkirmishService.ParseSeed("abc") == 0);
                    Check("Скримиш: сид — число и отрицательное (с пробелами)",
                        SkirmishService.ParseSeed("12345") == 12345 &&
                        SkirmishService.ParseSeed(" -42 ") == -42);
                    Check("Скримиш: PairsOf — чёт/нечёт/минимум",
                        ScenarioTemplate.PairsOf(2) == 1 && ScenarioTemplate.PairsOf(4) == 2 &&
                        ScenarioTemplate.PairsOf(3) == 1 && ScenarioTemplate.PairsOf(1) == 1 &&
                        ScenarioTemplate.PairsOf(0) == 1);
                }

                // ---------- Туман войны: классификация видимости (Util.ClassifyFog) ----------
                {
                    const float live = 4f;
                    Check("Fog: ракеты/свои/нейтралы видны без разведки",
                        Util.ClassifyFog(true, false, false, false, float.MaxValue, live, false) == Util.FogVisibility.Visible &&
                        Util.ClassifyFog(false, true, false, false, float.MaxValue, live, false) == Util.FogVisibility.Visible &&
                        Util.ClassifyFog(false, true, true, false, float.MaxValue, live, false) == Util.FogVisibility.Visible);
                    Check("Fog: чужой без контакта скрыт",
                        Util.ClassifyFog(false, false, false, false, float.MaxValue, live, true) == Util.FogVisibility.Hidden &&
                        Util.ClassifyFog(false, false, true, false, float.MaxValue, live, true) == Util.FogVisibility.Hidden);
                    Check("Fog: здание опознано — видно с любым (даже протухшим) контактом",
                        Util.ClassifyFog(false, false, true, true, 0.5f, live, true) == Util.FogVisibility.Visible &&
                        Util.ClassifyFog(false, false, true, true, 3600f, live, true) == Util.FogVisibility.Visible);
                    Check("Fog: здание радар-ONLY (не опознано) — полной видимости нет",
                        Util.ClassifyFog(false, false, true, true, 0.5f, live, false) == Util.FogVisibility.Hidden);
                    Check("Fog: подвижный — живой контакт И опознание",
                        Util.ClassifyFog(false, false, false, true, 0.5f, live, true) == Util.FogVisibility.Visible &&
                        Util.ClassifyFog(false, false, false, true, 4f, live, true) == Util.FogVisibility.Visible &&
                        Util.ClassifyFog(false, false, false, true, 4.1f, live, true) == Util.FogVisibility.Hidden &&
                        Util.ClassifyFog(false, false, false, true, 0.5f, live, false) == Util.FogVisibility.Hidden &&
                        Util.ClassifyFog(false, false, false, true, 30f, live, true) == Util.FogVisibility.Hidden);
                }

                // ---------- Туман войны: классификация контакта (Util.ClassifyBlip, v0.147) ----------
                {
                    const float live = 4f;
                    Check("Blip: ракеты/свои — полная отметка без разведки",
                        Util.ClassifyBlip(true, false, false, false, float.MaxValue, live, false) == Util.ContactBlip.Identified &&
                        Util.ClassifyBlip(false, true, false, false, float.MaxValue, live, false) == Util.ContactBlip.Identified);
                    Check("Blip: без контакта — ничего",
                        Util.ClassifyBlip(false, false, false, false, float.MaxValue, live, true) == Util.ContactBlip.None &&
                        Util.ClassifyBlip(false, false, true, false, float.MaxValue, live, true) == Util.ContactBlip.None);
                    Check("Blip: подвижный радар-ONLY живой — безымянная точка",
                        Util.ClassifyBlip(false, false, false, true, 0.5f, live, false) == Util.ContactBlip.Anonymous &&
                        Util.ClassifyBlip(false, false, false, true, 4f, live, false) == Util.ContactBlip.Anonymous);
                    Check("Blip: подвижный опознанный живой — полный; протухший — ничего",
                        Util.ClassifyBlip(false, false, false, true, 0.5f, live, true) == Util.ContactBlip.Identified &&
                        Util.ClassifyBlip(false, false, false, true, 4.1f, live, true) == Util.ContactBlip.None);
                    Check("Blip: здание радар-ONLY — безымянная точка; опознанное — полный (любой возраст)",
                        Util.ClassifyBlip(false, false, true, true, 3600f, live, false) == Util.ContactBlip.Anonymous &&
                        Util.ClassifyBlip(false, false, true, true, 3600f, live, true) == Util.ContactBlip.Identified);
                }

                // ---------- Туман войны: штамп круга обзора (Util.FogStampCircle) ----------
                {
                    var cells = new bool[10 * 12]; // прямоугольная сетка 10×12
                    Util.FogStampCircle(cells, 10, 12, 5, 6, 0);
                    Check("Stamp: радиус 0 — ровно одна ячейка",
                        cells[6 * 10 + 5] && CountCells(cells) == 1);

                    System.Array.Clear(cells, 0, cells.Length);
                    Util.FogStampCircle(cells, 10, 12, 5, 6, 2);
                    int stamped = CountCells(cells);
                    Check("Stamp: круг r=2 покрывает 13 ячеек (крест + ортогонали)",
                        stamped == 13, "покрыто " + stamped);
                    Check("Stamp: концы креста штампуются, диагональные углы — нет",
                        cells[6 * 10 + 7] && cells[6 * 10 + 3] && cells[4 * 10 + 5] && cells[8 * 10 + 5] &&
                        !cells[8 * 10 + 7] && !cells[4 * 10 + 3]);

                    System.Array.Clear(cells, 0, cells.Length);
                    Util.FogStampCircle(cells, 10, 12, 0, 0, 3); // сенсор в углу — кламп
                    Check("Stamp: у края карты — четверть круга (11 ячеек), без выхода",
                        CountCells(cells) == 11 && cells[0] && cells[2] && cells[20] && !cells[3 * 10 + 3],
                        "покрыто " + CountCells(cells));

                    System.Array.Clear(cells, 0, cells.Length);
                    Util.FogStampCircle(cells, 10, 12, 99, 99, 5);
                    Util.FogStampCircle(cells, 10, 12, -5, -5, 5);
                    Util.FogStampCircle(null, 10, 12, 0, 0, 1);
                    Util.FogStampCircle(cells, 0, 12, 0, 0, 1);
                    Check("Stamp: центр полностью вне сетки — тихо, без исключений",
                        CountCells(cells) == 0);
                }
                }
            }
        }
    }
}
