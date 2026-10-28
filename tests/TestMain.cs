using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    /// <summary>Консольный тест-раннер чистой логики мода: без фреймворков,
    /// ненулевой код выхода при провале. Запускается из tools/build.py.</summary>
    internal static class TestMain
    {
        private static int _failed;

        private static void Check(string name, bool ok, string details = null)
        {
            Console.WriteLine((ok ? "  [OK]   " : "  [FAIL] ") + name + (details != null ? " — " + details : ""));
            if (!ok)
                _failed++;
        }

        private static int Main()
        {
            Console.WriteLine("SupremeNuclear.Tests");

            // ---------- Util.Runs: серии подряд идущих ----------
            var runs = Util.Runs(new[] { "SAM", "SAM", "JCK", "SAM", "SAM", "SAM" }, (a, b) => a == b);
            Check("Runs: 3 серии", runs.Count == 3, string.Join(",", runs.Select(r => r.key + "x" + r.count)));
            Check("Runs: счётчики 2/1/3",
                runs[0].count == 2 && runs[1].count == 1 && runs[2].count == 3);
            Check("Runs: порядок сохранён",
                runs[0].key == "SAM" && runs[1].key == "JCK" && runs[2].key == "SAM");
            Check("Runs: пустой список", Util.Runs(new string[0], (a, b) => a == b).Count == 0);

            // ---------- Util.SegmentSteps: разбивка прямой трассы наземки (проверка на воду) ----------
            Check("SegmentSteps: нулевая/короткая дистанция — 1 шаг (сама цель)",
                Util.SegmentSteps(0f, 150f, 24) == 1 && Util.SegmentSteps(5f, 150f, 24) == 1 &&
                Util.SegmentSteps(150f, 150f, 24) == 1);
            Check("SegmentSteps: шаг ~150 м",
                Util.SegmentSteps(151f, 150f, 24) == 2 && Util.SegmentSteps(3600f, 150f, 24) == 24);
            Check("SegmentSteps: потолок сэмплов на длинной трассе",
                Util.SegmentSteps(12000f, 150f, 24) == 24);
            Check("SegmentSteps: нулевой шаг не делит на ноль",
                Util.SegmentSteps(100f, 0f, 24) == 24);

            // ---------- Util.DistSqToSegmentXZ: метрика «точка у полосы» (выбор аэродрома) ----------
            Check("DistSqToSegmentXZ: точка на отрезке — ноль, перпендикуляр — квадрат",
                Util.DistSqToSegmentXZ(new UnityEngine.Vector2(0.5f, 0f), UnityEngine.Vector2.zero, UnityEngine.Vector2.right) < 1e-6f &&
                UnityEngine.Mathf.Abs(Util.DistSqToSegmentXZ(new UnityEngine.Vector2(0.5f, 3f), UnityEngine.Vector2.zero, UnityEngine.Vector2.right) - 9f) < 1e-4f);
            Check("DistSqToSegmentXZ: за концами — до ближайшего конца",
                UnityEngine.Mathf.Abs(Util.DistSqToSegmentXZ(new UnityEngine.Vector2(-3f, 4f), UnityEngine.Vector2.zero, UnityEngine.Vector2.right) - 25f) < 1e-4f &&
                UnityEngine.Mathf.Abs(Util.DistSqToSegmentXZ(new UnityEngine.Vector2(5f, 0f), UnityEngine.Vector2.zero, UnityEngine.Vector2.right) - 16f) < 1e-4f);
            Check("DistSqToSegmentXZ: вырожденный отрезок — точка",
                UnityEngine.Mathf.Abs(Util.DistSqToSegmentXZ(new UnityEngine.Vector2(3f, 4f), UnityEngine.Vector2.zero, UnityEngine.Vector2.zero) - 25f) < 1e-4f);

            // ---------- Util.InRectXZ: прямоугольник-полоса (пик полотна ВПП кликом) ----------
            Check("InRectXZ: внутри вдоль и поперёк, снаружи за торцом и сбоку",
                Util.InRectXZ(new UnityEngine.Vector2(50f, 10f), UnityEngine.Vector2.zero, new UnityEngine.Vector2(100f, 0f), 40f, 5f) &&
                !Util.InRectXZ(new UnityEngine.Vector2(120f, 0f), UnityEngine.Vector2.zero, new UnityEngine.Vector2(100f, 0f), 40f, 5f) &&
                !Util.InRectXZ(new UnityEngine.Vector2(50f, 40f), UnityEngine.Vector2.zero, new UnityEngine.Vector2(100f, 0f), 40f, 5f));
            Check("InRectXZ: наклонённая полоса — система координат отрезка",
                Util.InRectXZ(new UnityEngine.Vector2(103f, 103f), new UnityEngine.Vector2(100f, 100f), new UnityEngine.Vector2(200f, 200f), 30f, 2f) &&
                !Util.InRectXZ(new UnityEngine.Vector2(115f, 85f), new UnityEngine.Vector2(100f, 100f), new UnityEngine.Vector2(200f, 200f), 30f, 2f));
            Check("InRectXZ: запас margin расширяет вдоль и поперёк",
                Util.InRectXZ(new UnityEngine.Vector2(104f, 0f), UnityEngine.Vector2.zero, new UnityEngine.Vector2(100f, 0f), 40f, 5f) &&
                Util.InRectXZ(new UnityEngine.Vector2(50f, 26f), UnityEngine.Vector2.zero, new UnityEngine.Vector2(100f, 0f), 40f, 7f));
            Check("InRectXZ: вырожденный отрезок — круг радиусом полوعины+запас",
                Util.InRectXZ(new UnityEngine.Vector2(24f, 0f), UnityEngine.Vector2.zero, UnityEngine.Vector2.zero, 40f, 5f) &&
                !Util.InRectXZ(new UnityEngine.Vector2(26f, 0f), UnityEngine.Vector2.zero, UnityEngine.Vector2.zero, 40f, 5f));

            // ---------- Util.Bezier3: сглаживание рулёжек (подвод к дорожке слияния) ----------
            var bp0 = new UnityEngine.Vector3(0f, 0f, 0f);
            var bp1 = new UnityEngine.Vector3(30f, 0f, 0f);
            var bp2 = new UnityEngine.Vector3(30f, 0f, 100f);
            var bp3 = new UnityEngine.Vector3(0f, 0f, 100f);
            Check("Bezier3: концы кривой = опорные точки",
                (Util.Bezier3(bp0, bp1, bp2, bp3, 0f) - bp0).sqrMagnitude < 1e-6f &&
                (Util.Bezier3(bp0, bp1, bp2, bp3, 1f) - bp3).sqrMagnitude < 1e-6f);
            Check("Bezier3: касательная на концах = контрольные направления",
                UnityEngine.Vector3.Dot((Util.Bezier3(bp0, bp1, bp2, bp3, 0.001f) - bp0).normalized,
                    (bp1 - bp0).normalized) > 0.999f &&
                UnityEngine.Vector3.Dot((bp3 - Util.Bezier3(bp0, bp1, bp2, bp3, 0.999f)).normalized,
                    (bp3 - bp2).normalized) > 0.999f);
            // Аффинность: линейный функционал (здесь z) на кривой не выходит
            // за [min, max] по контрольным точкам — зажатые контроли не пускают
            // кривую в полосу.
            float zMin = UnityEngine.Mathf.Min(UnityEngine.Mathf.Min(bp0.z, bp1.z),
                UnityEngine.Mathf.Min(bp2.z, bp3.z));
            float zMax = UnityEngine.Mathf.Max(UnityEngine.Mathf.Max(bp0.z, bp1.z),
                UnityEngine.Mathf.Max(bp2.z, bp3.z));
            bool zBounded = true;
            for (int i = 0; i <= 20; i++)
            {
                float z = Util.Bezier3(bp0, bp1, bp2, bp3, i / 20f).z;
                zBounded &= z >= zMin - 1e-4f && z <= zMax + 1e-4f;
            }
            Check("Bezier3: аффинная ограниченность — кривая в границах контролов", zBounded);

            // ---------- Util.Taxi*: геометрия КОЛЛЕКТОРА рулёжки (v0.100) ----------
            // Порог (0,0,0), ось и farDir = +X, сторона ангара +Z: магистраль z=+50.
            var thr = new UnityEngine.Vector3(0f, 0f, 0f);
            var axisX = new UnityEngine.Vector3(1f, 0f, 0f);
            var toSide = new UnityEngine.Vector3(0f, 0f, -1f); // ворота ангара к полосе
            const float OFF = 50f, JMIN = 25f, GROW = 80f, MINF = 150f;

            Util.TaxiCollectorPlan(thr, new UnityEngine.Vector3(240f, 0f, 110f), toSide,
                axisX, axisX, OFF, JMIN, GROW, MINF, -1f,
                out UnityEngine.Vector3 side1, out UnityEngine.Vector3 j1,
                out System.Collections.Generic.List<UnityEngine.Vector3> c1, out float f1);
            Check("Taxi: сторона магистрали — к ангару (+Z)",
                side1.z > 0.99f);
            Check("Taxi: стык под ангаром на магистрали (df=240, z=50)",
                UnityEngine.Mathf.Abs(j1.x - 240f) < 0.01f && UnityEngine.Mathf.Abs(j1.z - 50f) < 0.01f);
            Check("Taxi: создание — магистраль до max(240+80,150)=320",
                UnityEngine.Mathf.Abs(f1 - 320f) < 0.01f);
            float maxOff1 = 0f;
            foreach (UnityEngine.Vector3 p in c1)
            {
                maxOff1 = UnityEngine.Mathf.Max(maxOff1, UnityEngine.Mathf.Sqrt(
                    Util.DistSqToSegmentXZ(new UnityEngine.Vector2(p.x, p.z),
                        new UnityEngine.Vector2(240f, 110f), new UnityEngine.Vector2(j1.x, j1.z))));
            }
            Check("Taxi: перпендикулярные ворота — коннектор почти прямая (<2 м от хорды)",
                maxOff1 < 2f, "макс " + maxOff1.ToString("F2") + " м");
            bool latOk1 = true;
            foreach (UnityEngine.Vector3 p in c1)
                latOk1 &= p.z >= OFF - 0.5f;
            Check("Taxi: коннектор не подходит к полосе ближе магистрали (z ≥ 50)", latOk1);

            Util.TaxiCollectorPlan(thr, new UnityEngine.Vector3(-150f, 0f, 120f), toSide,
                axisX, axisX, OFF, JMIN, GROW, MINF, -1f,
                out _, out UnityEngine.Vector3 j2, out _, out float f2);
            Check("Taxi: ангар за порогом — стык клампится на JMIN=25",
                UnityEngine.Mathf.Abs(j2.x - JMIN) < 0.01f);
            Check("Taxi: дальность при создании ≥ MinFar",
                UnityEngine.Mathf.Abs(f2 - MINF) < 0.01f);

            Util.TaxiCollectorPlan(thr, new UnityEngine.Vector3(400f, 0f, 110f), toSide,
                axisX, axisX, OFF, JMIN, GROW, MINF, 320f,
                out _, out UnityEngine.Vector3 j3, out _, out float f3);
            Check("Taxi: дальний ангар растит магистраль (400+80=480)",
                UnityEngine.Mathf.Abs(j3.x - 400f) < 0.01f && UnityEngine.Mathf.Abs(f3 - 480f) < 0.01f);
            Util.TaxiCollectorPlan(thr, new UnityEngine.Vector3(150f, 0f, 110f), toSide,
                axisX, axisX, OFF, JMIN, GROW, MINF, 480f,
                out _, out _, out _, out float f4);
            Check("Taxi: ближний ангар внутри — магистраль не ужимается (480)",
                UnityEngine.Mathf.Abs(f4 - 480f) < 0.01f);

            // Непересечение коннекторов грида: каждый живёт в своём коридоре df
            // (±40 м вокруг ангара) — при шаге грида 160 м коридоры не пересекаются.
            bool corridorsOk = true;
            for (float hx = 80f; hx <= 560f; hx += 160f)
            {
                Util.TaxiCollectorPlan(thr, new UnityEngine.Vector3(hx, 0f, 110f), toSide,
                    axisX, axisX, OFF, JMIN, GROW, MINF, -1f,
                    out _, out _, out System.Collections.Generic.List<UnityEngine.Vector3> cc, out _);
                float lo = hx, hi = UnityEngine.Mathf.Max(hx, JMIN);
                foreach (UnityEngine.Vector3 p in cc)
                {
                    lo = UnityEngine.Mathf.Min(lo, p.x);
                    hi = UnityEngine.Mathf.Max(hi, p.x);
                }
                corridorsOk &= hi - lo < 80f;
            }
            Check("Taxi: коннекторы грида в непересекающихся коридорах (<80 м при шаге 160)",
                corridorsOk);

            // Без складок при воротах «мимо стыка» (спиной/поперёк/диагональю):
            // каждый шаг полилинии обязан продвигаться к стыку (проекция на
            // хорду не убывает) — контроль по чистому fwd давал петлю
            // («линия с изломами», плейтест v0.100.0).
            bool noFolds = true;
            UnityEngine.Vector3[][] foldCases =
            {
                new[] { new UnityEngine.Vector3(240f, 0f, 110f), new UnityEngine.Vector3(0f, 0f, 1f) },        // спиной к магистрали
                new[] { new UnityEngine.Vector3(240f, 0f, 110f), new UnityEngine.Vector3(0.7071f, 0f, 0.7071f) }, // диагональю от стыка
                new[] { new UnityEngine.Vector3(240f, 0f, 60f), new UnityEngine.Vector3(0.7071f, 0f, 0.7071f) },   // вплотную к магистрали
                new[] { new UnityEngine.Vector3(240f, 0f, 110f), new UnityEngine.Vector3(1f, 0f, 0f) },        // вдоль полосы
            };
            foreach (UnityEngine.Vector3[] fc in foldCases)
            {
                Util.TaxiCollectorPlan(thr, fc[0], fc[1], axisX, axisX, OFF, JMIN, GROW, MINF, -1f,
                    out _, out UnityEngine.Vector3 fj, out System.Collections.Generic.List<UnityEngine.Vector3> fcPts, out _);
                UnityEngine.Vector2 fchord = new UnityEngine.Vector2(fj.x - fc[0].x, fj.z - fc[0].z).normalized;
                var whole = new System.Collections.Generic.List<UnityEngine.Vector2> { new UnityEngine.Vector2(fc[0].x, fc[0].z) };
                foreach (UnityEngine.Vector3 p in fcPts)
                    whole.Add(new UnityEngine.Vector2(p.x, p.z));
                whole.Add(new UnityEngine.Vector2(fj.x, fj.z));
                for (int fi = 1; fi < whole.Count; fi++)
                    noFolds &= UnityEngine.Vector2.Dot(whole[fi] - whole[fi - 1], fchord) >= -0.05f;
            }
            Check("Taxi: ворота «мимо стыка» — коннектор без складок (монотонен к стыку)", noFolds);

            var merge = Util.TaxiMergeSamples(thr, axisX, new UnityEngine.Vector3(0f, 0f, 1f), OFF);
            bool mergeOk = merge.Count >= 2; // только внутренние точки: p1 и торец добавляет звущий
            foreach (UnityEngine.Vector3 p in merge)
                mergeOk &= p.x >= -50.1f && p.z >= -0.1f && p.z <= 50.1f;
            Check("TaxiMerge: сэмплы только внутри коридора p1→торец (x≥−50, 0≤z≤50)",
                mergeOk, merge.Count + " точек");
            UnityEngine.Vector3 lastDir = thr - merge[merge.Count - 1];
            Check("TaxiMerge: вход в торец вдоль оси (хорда последнего сэмпла ~farDir)",
                UnityEngine.Vector3.Dot(lastDir.normalized, axisX) > 0.97f);

            var lane = Util.TaxiLaneSamples(thr, axisX, new UnityEngine.Vector3(0f, 0f, 1f), OFF, -50f, 320f);
            bool laneOk = UnityEngine.Mathf.Abs(lane[0].x + 50f) < 0.01f &&
                          UnityEngine.Mathf.Abs(lane[lane.Count - 1].x - 320f) < 0.01f;
            for (int li = 0; li < lane.Count; li++)
            {
                laneOk &= UnityEngine.Mathf.Abs(lane[li].z - 50f) < 0.01f;
                if (li > 0)
                    laneOk &= UnityEngine.Vector3.Distance(lane[li - 1], lane[li]) <= 15.01f;
            }
            Check("TaxiLane: оба конца включены, шаг ≤15 м, на магистрали z=50", laneOk,
                lane.Count + " точек");

            // ---------- Util.RingPoints: кольцевой поиск воды для спуска корабля ----------
            var ring = Util.RingPoints(60f, 30f, 16, 400f);
            Check("RingPoints: размер = кольца × направления, ближние раньше",
                ring.Count == 12 * 16 && ring[0].magnitude >= 59.9f && ring[0].magnitude <= 60.1f);
            Check("RingPoints: радиусы не убывают по ходу списка",
                ring.TrueForAll(p => p.magnitude <= 401f) &&
                ring[ring.Count - 1].magnitude > ring[0].magnitude);
            Check("RingPoints: вырожденные параметры — пустой список без исключений",
                Util.RingPoints(0f, 30f, 16, 400f).Count == 0 &&
                Util.RingPoints(60f, 30f, 3, 400f).Count == 0 &&
                Util.RingPoints(60f, 30f, 16, 50f).Count == 0);

            // ---------- Util.BuildApproachRingM: кольцо остановки строителей (v0.95) ----------
            Check("Кольцо подхода: габарит + запас",
                Util.BuildApproachRingM(50f, 15f, 25f, 119f) > 64.9f &&
                Util.BuildApproachRingM(50f, 15f, 25f, 119f) < 65.1f);
            Check("Кольцо подхода: мелкая постройка — минимум",
                Util.BuildApproachRingM(5f, 15f, 25f, 119f) == 25f);
            Check("Кольцо подхода: крупное здание — потолок радиуса оплаты",
                Util.BuildApproachRingM(300f, 15f, 25f, 119f) == 119f);

            // ---------- Util.FootprintTilt: наклон депо под рельеф ----------
            Util.FootprintTilt(10f, 10f, 10f, 10f, 40f, 24f, 15f, out float tPitch, out float tRoll);
            Check("FootprintTilt: ровная площадка — нулевые углы",
                tPitch == 0f && tRoll == 0f);
            // Склон вверх к носу (перед выше, 10 м на 40 м длины ≈ 14°): нос должен
            // ПОДНЯТЬСЯ — pitch отрицательный (знак Quaternion.Euler: pitch>0 — нос вниз).
            Util.FootprintTilt(15f, 5f, 10f, 10f, 40f, 24f, 15f, out tPitch, out tRoll);
            Check("FootprintTilt: перед выше — нос вверх (pitch<0), крен нулевой",
                tPitch < -13.9f && tPitch > -14.1f && tRoll == 0f);
            // Правый борт выше (6 м на 24 м ширины ≈ 14°) — правый борт вверх: roll>0.
            Util.FootprintTilt(10f, 10f, 13f, 7f, 40f, 24f, 15f, out tPitch, out tRoll);
            Check("FootprintTilt: правый борт выше — roll>0",
                tRoll > 13.9f && tRoll < 14.1f && tPitch == 0f);
            // Обрыв (100 м на 40 м, борта 50/0) — кламп на максимум, не в экстремальный угол.
            Util.FootprintTilt(100f, 0f, 50f, 0f, 40f, 24f, 15f, out tPitch, out tRoll);
            Check("FootprintTilt: кламп максимального наклона",
                tPitch == -15f && tRoll == 15f);
            // maxDeg=0 — наклон выключен (всегда плоско).
            Util.FootprintTilt(100f, 0f, 50f, 0f, 40f, 24f, 0f, out tPitch, out tRoll);
            Check("FootprintTilt: maxDeg=0 — плоско",
                tPitch == 0f && tRoll == 0f);

            // ---------- AircraftOrders: группы приказа у точек плана ----------
            var airSeg = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Patrol };
            airSeg.Points.Add(new UnityEngine.Vector3(0f, 800f, 0f).ToGlobalPosition());
            airSeg.PointGroups.Add(0); // стартовая точка — индивидуальная
            airSeg.Points.Add(new UnityEngine.Vector3(100f, 800f, 0f).ToGlobalPosition());
            airSeg.PointGroups.Add(7);
            airSeg.Points.Add(new UnityEngine.Vector3(200f, 800f, 0f).ToGlobalPosition());
            airSeg.PointGroups.Add(8);
            var airPlan = new List<AircraftOrders.OrderSegment> { airSeg };
            Check("Авиагруппы: точка находится по groupId (индексы у каждого свои)",
                AircraftOrders.TryGetPointByGroupId(airPlan, 7, out int gs, out int gp) &&
                gs == 0 && gp == 1);
            Check("Авиагруппы: чужой groupId не найден",
                !AircraftOrders.TryGetPointByGroupId(airPlan, 99, out _, out _));
            Check("Авиагруппы: удаление по groupId убирает только её точки",
                AircraftOrders.RemovePointsWithGroup(airPlan, 7) == 1 &&
                airSeg.Points.Count == 2 && airSeg.PointGroups.Count == 2 &&
                airSeg.PointGroups[1] == 8);
            var airPlan2 = new List<AircraftOrders.OrderSegment>
            {
                new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit }
            };
            airPlan2[0].Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            airPlan2[0].PointGroups.Add(5);
            Check("Авиагруппы: опустевший сегмент удаляется",
                AircraftOrders.RemovePointsWithGroup(airPlan2, 5) == 1 && airPlan2.Count == 0);

            // Паритет группового удаления с одиночным (NormalizeAfterRemoval общий):
            var airSalvo = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Salvo };
            airSalvo.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            airSalvo.PointGroups.Add(5);
            airSalvo.SalvoTargets = new List<Unit> { null }; // залп-цели есть
            var airSalvoPlan = new List<AircraftOrders.OrderSegment> { airSalvo };
            Check("Авиагруппы: опустевший сегмент с залп-целями переживает удаление (паритет)",
                AircraftOrders.RemovePointsWithGroup(airSalvoPlan, 5) == 1 && airSalvoPlan.Count == 1);
            var airAtk = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Attack, Index = 2 };
            var airTr = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit, Index = 2 };
            foreach (AircraftOrders.OrderSegment s in new[] { airAtk, airTr })
                foreach (int g in new[] { 5, 0, 5 })
                {
                    s.Points.Add(new UnityEngine.Vector3(g, 0f, 0f).ToGlobalPosition());
                    s.PointGroups.Add(g);
                }
            var airAtkPlan = new List<AircraftOrders.OrderSegment> { airAtk };
            var airTrPlan = new List<AircraftOrders.OrderSegment> { airTr };
            AircraftOrders.RemovePointsWithGroup(airAtkPlan, 5);
            AircraftOrders.RemovePointsWithGroup(airTrPlan, 5);
            Check("Авиагруппы: курсор Attack не клампится (паритет), транзит — клампится",
                airAtk.Index == 1 && airAtk.Points.Count == 1 &&
                airTr.Index == 0 && airTr.Points.Count == 1);

            // ---------- AircraftOrders.IsServicePoint: служебные точки (сканер и маркеры) ----------
            var svcPatrol = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Patrol };
            svcPatrol.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcPatrol.Points.Add(new UnityEngine.Vector3(100f, 0f, 0f).ToGlobalPosition());
            svcPatrol.PointGroups.Add(0); // стартовая — служебная
            svcPatrol.PointGroups.Add(7); // точка приказа — обычный вейпоинт
            Check("Служебные точки: взлёт патруля — служебная, точка приказа — нет",
                AircraftOrders.IsServicePoint(svcPatrol, 0) &&
                !AircraftOrders.IsServicePoint(svcPatrol, 1));
            var svcStop = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit, ServiceStart = true };
            svcStop.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcStop.PointGroups.Add(0);
            var svcMove = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit };
            svcMove.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcMove.PointGroups.Add(0);
            var svcMoveG = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit };
            svcMoveG.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcMoveG.PointGroups.Add(3);
            Check("Служебные точки: стоп-старт отмены (флаг) — служебная; одиночная нога движения — обычный вейпоинт (v0.90)",
                AircraftOrders.IsServicePoint(svcStop, 0) &&
                !AircraftOrders.IsServicePoint(svcMove, 0) &&
                !AircraftOrders.IsServicePoint(svcMoveG, 0));
            var svcAtk = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Attack };
            svcAtk.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcAtk.PointGroups.Add(0);
            var svcRtb = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Rtb };
            svcRtb.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcRtb.PointGroups.Add(0);
            Check("Служебные точки: якоря атаки/RTB — служебные (крест/лента, не редактируются)",
                AircraftOrders.IsServicePoint(svcAtk, 0) && AircraftOrders.IsServicePoint(svcRtb, 0));

            // ---------- AircraftOrders: радиус зачёта вейпоинта и курсор патруля ----------
            Check("Радиус зачёта: реактивному 250 м/с — потолок 900 м (прежнее поведение)",
                AircraftOrders.WaypointReachRadius(250f) == 900f &&
                AircraftOrders.WaypointReachRadius(600f) == 900f);
            Check("Радиус зачёта: вертолёту 60 м/с — 240 м; висящему — пол 120 м",
                AircraftOrders.WaypointReachRadius(60f) == 240f &&
                AircraftOrders.WaypointReachRadius(0f) == 120f);
            var pd = new float[] { 100f, 700f, 3000f, 5000f };
            Check("Курсор патруля: текущая в радиусе, следующая вне — шаг вперёд",
                AircraftOrders.NextPatrolIndex(pd, 4, 0, 240f) == 1);
            Check("Курсор патруля: текущая вне радиуса — курсор стоит",
                AircraftOrders.NextPatrolIndex(pd, 4, 1, 240f) == 1);
            var pdClose = new float[] { 100f, 200f, 3000f, 5000f };
            Check("Курсор патруля: близкая пара зачитывается за один проход",
                AircraftOrders.NextPatrolIndex(pdClose, 4, 0, 240f) == 2);
            var pdWrap = new float[] { 100f, 5000f, 200f };
            Check("Курсор патруля: переход по кругу (последняя → нулевая → дальше)",
                AircraftOrders.NextPatrolIndex(pdWrap, 3, 2, 240f) == 1);
            var pdHang = new float[] { 100f, 150f, 120f, 140f };
            int hangNext = AircraftOrders.NextPatrolIndex(pdHang, 4, 2, 240f);
            Check("Курсор патруля: весь цикл в радиусе — цель самая дальняя точка, не зависание на стартовой",
                hangNext == 1 && hangNext != 2,
                "next=" + hangNext);
            Check("Курсор патруля: цикл из одной точки и вырожденные параметры",
                AircraftOrders.NextPatrolIndex(new float[] { 100f }, 1, 0, 240f) == 0 &&
                AircraftOrders.NextPatrolIndex(pd, 0, 3, 240f) == 3 &&
                AircraftOrders.NextPatrolIndex(pd, 4, -1, 240f) == -1);

            // ---------- Util.RoundChainCorners: скругление углов лент ----------
            var rcSmooth = new List<UnityEngine.Vector3>();
            var rcLegs = new List<int>();
            var rcCorner = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 100f),
            };
            int rcn = Util.RoundChainCorners(rcCorner, 20f, rcSmooth, rcLegs);
            bool RcHas(float x, float z) => rcSmooth.Any(v =>
                Math.Abs(v.x - x) < 0.01f && Math.Abs(v.z - z) < 0.01f);
            Check("Скругление: концы цепочки на месте",
                rcSmooth[0] == rcCorner[0] && rcSmooth[rcn - 1] == rcCorner[2], "n=" + rcn);
            Check("Скругление: точки касания на смежных ногах",
                RcHas(80f, 0f) && RcHas(100f, 20f),
                string.Join(" ", rcSmooth.Select(v => v.x + ";" + v.z)));
            Check("Скругление: угол заменён дугой, самой вершины нет", !RcHas(100f, 0f));
            Check("Скругление: середина дуги = контрольная точка Безье", RcHas(95f, 5f));
            Check("Скругление: legStart ведёт ногу от конца дуги",
                rcLegs.Count == 2 && rcLegs[0] == 0 && rcLegs[1] == 3,
                string.Join(",", rcLegs));

            var rcLine = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(50f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
            };
            int rcLn = Util.RoundChainCorners(rcLine, 20f, rcSmooth, rcLegs);
            Check("Скругление: коллинеарная цепочка проходит как есть",
                rcLn == 3 && rcSmooth[1] == rcLine[1] && rcLegs.Count == 2 && rcLegs[1] == 1);

            var rcClamp = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(10f, 0f, 0f),
                new UnityEngine.Vector3(10f, 0f, 1000f),
            };
            int rcCn = Util.RoundChainCorners(rcClamp, 100f, rcSmooth, rcLegs);
            bool RcClampHas(float x, float z) => rcSmooth.Take(rcCn).Any(v =>
                Math.Abs(v.x - x) < 0.01f && Math.Abs(v.z - z) < 0.01f);
            Check("Скругление: радиус зажат 45% короткой ноги (t=4.5)",
                RcClampHas(5.5f, 0f) && RcClampHas(10f, 4.5f),
                string.Join(" ", rcSmooth.Take(rcCn).Select(v => v.x + ";" + v.z)));

            var rcZero = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 100f),
            };
            int rcZn = Util.RoundChainCorners(rcZero, 0f, rcSmooth, rcLegs);
            Check("Скругление: нулевой радиус — цепочка без изменений", rcZn == 3);

            var rcUturn = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(0f, 0f, 0f),
            };
            int rcUn = Util.RoundChainCorners(rcUturn, 20f, rcSmooth, rcLegs);
            Check("Скругление: точный разворот — острый угол, без артефактов",
                rcUn == 3 && rcSmooth[1] == rcUturn[1]);

            var rcDup = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 100f),
            };
            int rcDn = Util.RoundChainCorners(rcDup, 20f, rcSmooth, rcLegs);
            Check("Скругление: вырожденная нога пропущена, конец на месте",
                rcDn > 0 && rcSmooth[rcDn - 1] == rcDup[3] && rcLegs.Count == 3 && rcLegs[0] == 0 && rcLegs[1] == 0,
                "n=" + rcDn + " legs=" + string.Join(",", rcLegs));

            var rcTwo = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
            };
            int rcTn = Util.RoundChainCorners(rcTwo, 20f, rcSmooth, rcLegs);
            Check("Скругление: одна нога — без изменений",
                rcTn == 2 && rcSmooth[0] == rcTwo[0] && rcSmooth[1] == rcTwo[1] && rcLegs.Count == 1 && rcLegs[0] == 0);

            // ---------- Catalog.ApplyTierFilter: терцили по цене ----------
            var defs = Enumerable.Range(1, 9).Select(i => (Id: i, Price: (float)i * 10)).ToList();
            var t1 = defs.ToList(); Catalog.ApplyTierFilter(t1, d => d.Price, 1);
            var t2 = defs.ToList(); Catalog.ApplyTierFilter(t2, d => d.Price, 2);
            var t3 = defs.ToList(); Catalog.ApplyTierFilter(t3, d => d.Price, 3);
            var t0 = defs.ToList(); Catalog.ApplyTierFilter(t0, d => d.Price, 0);
            Check("Тир1 = 3 дешёвых", t1.Count == 3 && t1.All(d => d.Price <= 30), t1.Count.ToString());
            Check("Тир2 = 3 средних", t2.Count == 3 && t2.All(d => d.Price > 30 && d.Price <= 60));
            Check("Тир3 = 3 дорогих", t3.Count == 3 && t3.All(d => d.Price > 60));
            Check("Тир0 = без фильтра", t0.Count == 9);
            var one = new List<(int Id, float Price)> { (1, 5f) };
            Catalog.ApplyTierFilter(one, d => d.Price, 1);
            Check("Тир: один элемент не теряется (T1)", one.Count == 1);
            var one3 = new List<(int Id, float Price)> { (1, 5f) };
            Catalog.ApplyTierFilter(one3, d => d.Price, 3);
            Check("Тир: крошечный каталог не падает (T3 пуст)", one3.Count == 0);

            // ---------- Catalog.ShortLabel: короткая подпись плитки ----------
            Check("Подпись: код приоритетен", Catalog.ShortLabel("F-16C", "Fighting Falcon") == "F-16C");
            Check("Подпись: нет кода — первое слово имени",
                Catalog.ShortLabel("", "Extractor Mk2") == "Extractor");
            Check("Подпись: длинное слово обрезается до 10",
                Catalog.ShortLabel(null, "Бронированный автомобиль").Length == 10);
            Check("Подпись: пусто — ?", Catalog.ShortLabel(null, null) == "?");

            // ---------- Catalog.BadgeOf/TypeNameOf: бейджи типов наземки ----------
            Check("Бейдж: танк", Catalog.BadgeOf(VehicleType.MBT) == "ТНК");
            Check("Бейдж: ПЗРК", Catalog.BadgeOf(VehicleType.IR_SAM) == "ПЗРК");
            Check("Бейдж: ЗРК", Catalog.BadgeOf(VehicleType.R_SAM) == "ЗРК");
            Check("Бейдж: все типы непустые", Enum.GetValues(typeof(VehicleType))
                .Cast<VehicleType>().All(t => !string.IsNullOrEmpty(Catalog.BadgeOf(t))));
            Check("Бейдж: имя типа есть у всех", Enum.GetValues(typeof(VehicleType))
                .Cast<VehicleType>().All(t => !string.IsNullOrEmpty(Catalog.TypeNameOf(t))));

            // ---------- Catalog.AirBadge/AirRoleName: роль авиации ----------
            RoleIdentity Fighter() => new RoleIdentity { antiAir = 1f, antiSurface = 0.1f };
            RoleIdentity Strike() => new RoleIdentity { antiAir = 0.05f, antiSurface = 0.9f };
            RoleIdentity Multi() => new RoleIdentity { antiAir = 0.7f, antiSurface = 0.6f };
            RoleIdentity Heli() => new RoleIdentity { antiAir = 0.1f, antiSurface = 0.1f };
            var asType = new TypeIdentity { surface = 1f, air = 0f };
            var radarType = new TypeIdentity { radar = 1f };
            Check("Роль: истребитель", Catalog.AirBadge(Fighter(), asType) == "И");
            Check("Роль: ударный", Catalog.AirBadge(Strike(), asType) == "У");
            Check("Роль: многоролевой", Catalog.AirBadge(Multi(), asType) == "М");
            Check("Роль: слабый боец — транспорт", Catalog.AirBadge(Heli(), asType) == "ТР");
            Check("Роль: слабый боец-радар — ДРЛО", Catalog.AirBadge(Heli(), radarType) == "РЛС");
            Check("Роль: полное имя", Catalog.AirRoleName(Fighter(), asType) == "истребитель");

            // ---------- BuildTasks.Task.Progress01: доля оплаты ----------
            var task = new BuildTasks.Task { Cost = 250f, Paid = 100f };
            Check("Progress01: 100/250 = 0.4", Math.Abs(task.Progress01 - 0.4f) < 0.001f);
            task.Paid = 999f;
            Check("Progress01: кламп сверху", Math.Abs(task.Progress01 - 1f) < 0.001f);
            task.Paid = -5f;
            Check("Progress01: кламп снизу", Math.Abs(task.Progress01 - 0f) < 0.001f);

            // ---------- EconomyProfile.Rate: тиры и клампы ----------
            var profile = new EconomyProfile();
            Check("Rate: T1/T2/T3", profile.Rate(1) == 150f && profile.Rate(2) == 400f && profile.Rate(3) == 1000f);
            Check("Rate: кламп тира", profile.Rate(0) == 150f && profile.Rate(99) == 1000f);
            profile.incomePerMinute = new[] { 10f };
            Check("Rate: один тир", profile.Rate(3) == 10f);

            // ---------- OrderTemplates.ComputeOffsets: относительные позиции ----------
            var offs = OrderTemplates.ComputeOffsets(new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(100f, 0f, 200f),
                new UnityEngine.Vector3(130f, 0f, 200f),
                new UnityEngine.Vector3(100f, 0f, 260f),
            });
            Check("Offsets: первый — ноль", offs[0].x == 0f && offs[0].y == 0f);
            Check("Offsets: сосед +30x", offs[1].x == 30f && offs[1].y == 0f);
            Check("Offsets: сосед +60z", offs[2].x == 0f && offs[2].y == 60f);

            // ---------- OrderTemplates: JSON roundtrip ----------
            var template = new OrderTemplates.Template
            {
                name = "Шаблон 1",
                items = new List<OrderTemplates.Item>
                {
                    new OrderTemplates.Item { key = "sam", count = 9 },
                    new OrderTemplates.Item { key = "jack", count = 2 },
                }
            };
            var round = JsonConvert.DeserializeObject<OrderTemplates.Template>(
                JsonConvert.SerializeObject(template));
            Check("Шаблон JSON: имя", round.name == "Шаблон 1");
            Check("Шаблон JSON: позиции", round.items.Count == 2 && round.items[0].key == "sam" &&
                round.items[0].count == 9 && round.items[1].count == 2);

            // ---------- Util.WithinCone: конус атаки-движения ----------
            UnityEngine.Vector3 coneNorth = new UnityEngine.Vector3(0f, 0f, 1f);
            Check("Конус: цель по курсу",
                Util.WithinCone(coneNorth, new UnityEngine.Vector3(0f, 0f, 500f), 75f));
            Check("Конус: цель в пределах порога (30° < 75°)",
                Util.WithinCone(coneNorth, new UnityEngine.Vector3(500f, 0f, 866f), 75f));
            Check("Конус: цель за порогом (100° > 75°) — вето",
                !Util.WithinCone(coneNorth, new UnityEngine.Vector3(500f, 0f, -88f), 75f));
            Check("Конус: цель позади (180°) — вето",
                !Util.WithinCone(coneNorth, new UnityEngine.Vector3(0f, 0f, -500f), 75f));
            Check("Конус: вырожденный вектор курса не ветоит",
                Util.WithinCone(UnityEngine.Vector3.zero, coneNorth, 75f));
            Check("Конус: игнорирует высоту (вертикаль не считается углом)",
                Util.WithinCone(coneNorth, new UnityEngine.Vector3(0f, 5000f, 1f), 75f));

            // ---------- Util.ApproachDistance: дистанция подхода без «приказа назад» ----------
            Check("Подход: дальше дальности — 0.8×дальности",
                Math.Abs(Util.ApproachDistance(10000f, 5000f) - 4000f) < 0.01f);
            Check("Подход: УЖЕ в зоне — не отходит (0.9×текущей, ближе к цели)",
                Math.Abs(Util.ApproachDistance(3000f, 10000f) - 2700f) < 0.01f);
            Check("Подход: ровно на границе — слегка ближе",
                Math.Abs(Util.ApproachDistance(4000f, 5000f) - 3600f) < 0.01f);
            Check("Подход: нулевая дистанция — стоим",
                Util.ApproachDistance(0f, 5000f) == 0f);

            // ---------- AircraftOrders.ThrottleOf: скоростные режимы ----------
            Check("Тротл: Полный = 1.0", Math.Abs(AircraftOrders.ThrottleOf(0.85f, SpeedMode.Full) - 1f) < 1e-4f);
            Check("Тротл: Крейсер = бортовой cruiseThrottle",
                Math.Abs(AircraftOrders.ThrottleOf(0.85f, SpeedMode.Cruise) - 0.85f) < 1e-4f);
            Check("Тротл: Крейсер без данных борта — фолбэк 0.9",
                Math.Abs(AircraftOrders.ThrottleOf(0f, SpeedMode.Cruise) - 0.9f) < 1e-4f);
            Check("Тротл: Эконом = настройка",
                Math.Abs(AircraftOrders.ThrottleOf(0.85f, SpeedMode.Economy) - Settings.AircraftEconomyThrottle) < 1e-4f);

            // ---------- AircraftOrders.EffectiveFollowTerrain: тоггл «вдоль рельефа» ----------
            Check("Рельеф: авто = дефолт профиля",
                AircraftOrders.EffectiveFollowTerrain(true, TerrainHugMode.Profile) &&
                !AircraftOrders.EffectiveFollowTerrain(false, TerrainHugMode.Profile));
            Check("Рельеф: принудительно огибать — true даже у «Средне/Высоко»",
                AircraftOrders.EffectiveFollowTerrain(false, TerrainHugMode.Always));
            Check("Рельеф: принудительно напрямую — false даже у «Сверхнизко/Низко»",
                !AircraftOrders.EffectiveFollowTerrain(true, TerrainHugMode.Never));

            // ---------- Util.TrimSalvo: потолок ракет на цель в залпе ----------
            var salvo = new List<string> { "A", "A", "B", "A", "C", "C" };
            int trimmed = Util.TrimSalvo(salvo, 2);
            Check("Залп: потолок 2/цель — A×2 B×1 C×2, лишняя A снята",
                trimmed == 1 && salvo.SequenceEqual(new[] { "A", "A", "B", "C", "C" }),
                string.Join(",", salvo));
            var salvo1 = new List<string> { "A", "B", "A", "B" };
            Check("Залп: потолок 1/цель — по одной на цель",
                Util.TrimSalvo(salvo1, 1) == 2 && salvo1.SequenceEqual(new[] { "A", "B" }));
            var salvoAll = new List<string> { "A", "B" };
            Check("Залп: потолок не режет лишнего",
                Util.TrimSalvo(salvoAll, 4) == 0 && salvoAll.Count == 2);
            var salvoZero = new List<string> { "A", "A" };
            Check("Залп: потолок 0 — пустой залп", Util.TrimSalvo(salvoZero, 0) == 2 && salvoZero.Count == 0);

            // ---------- AircraftOrders.CanAppend: резка ног маршрута ----------
            var flightProfile = new AircraftOrders.FlightProfile(800f, false, "Средне");
            var legSame = new AircraftOrders.OrderSegment
            {
                Mode = AirOrderMode.Transit,
                AltAgl = 800f,
                FollowTerrain = false,
                Speed = SpeedMode.Full
            };
            Check("Ноги: тот же режим/профиль/скорость — точка дописывается",
                AircraftOrders.CanAppend(legSame, AirOrderMode.Transit, flightProfile, SpeedMode.Full));
            Check("Ноги: другая высота — новая нога",
                !AircraftOrders.CanAppend(legSame, AirOrderMode.Transit,
                    new AircraftOrders.FlightProfile(150f, true, "Низко"), SpeedMode.Full));
            Check("Ноги: другая скорость — новая нога",
                !AircraftOrders.CanAppend(legSame, AirOrderMode.Transit, flightProfile, SpeedMode.Cruise));
            Check("Ноги: атака никогда не сливается",
                !AircraftOrders.CanAppend(new AircraftOrders.OrderSegment
                    {
                        Mode = AirOrderMode.Attack, AltAgl = 800f, FollowTerrain = false, Speed = SpeedMode.Full
                    },
                    AirOrderMode.Attack, flightProfile, SpeedMode.Full));
            Check("Ноги: залп никогда не сливается",
                !AircraftOrders.CanAppend(legSame, AirOrderMode.Salvo, flightProfile, SpeedMode.Full));
            Check("Ноги: другой режим приказа — новая нога",
                !AircraftOrders.CanAppend(legSame, AirOrderMode.AttackMove, flightProfile, SpeedMode.Full));

            // ---------- RuPlural: русская плюрализация ----------
            Check("Plural: 1/2/5 носителей",
                Util.RuPlural(1, "носитель", "носителя", "носителей") == "носитель" &&
                Util.RuPlural(2, "носитель", "носителя", "носителей") == "носителя" &&
                Util.RuPlural(5, "носитель", "носителя", "носителей") == "носителей");
            Check("Plural: 11–14 всегда «много», 21 — «один»",
                Util.RuPlural(11, "тип", "типа", "типов") == "типов" &&
                Util.RuPlural(14, "тип", "типа", "типов") == "типов" &&
                Util.RuPlural(21, "тип", "типа", "типов") == "тип" &&
                Util.RuPlural(112, "тип", "типа", "типов") == "типов");

            // ---------- Loc: локализация (gettext: ключ = русская исходная строка) ----------
            Loc.Install("ru", "{\"носитель\": \"carrier\"}", null);
            Check("Loc: ru — тождество даже при наличии en-таблицы (фолбэк не включается)",
                Loc.Tr("патруль") == "патруль" && Loc.Tr("носитель") == "носитель" &&
                Loc.Language == "ru");
            Check("Loc: ru — плюрализация исходным русским правилом",
                Loc.Plural(2, "носитель", "носителя", "носителей") == "носителя" &&
                Loc.Plural(11, "тип", "типа", "типов") == "типов");
            Loc.Install("en",
                "{\"движение\": \"Move\", \"носов|носка|носков\": \"pair|pairs\", " +
                "\"готово: {0}\": \"done: {0}\", \"_plural\": \"en\"}", null);
            Check("Loc: en — перевод по таблице, пропуски — исходником",
                Loc.Tr("движение") == "Move" && Loc.Tr("нет перевода") == "нет перевода" &&
                Loc.Language == "en");
            Check("Loc: en — две формы (one/other)",
                Loc.Plural(1, "носов", "носка", "носков") == "pair" &&
                Loc.Plural(3, "носов", "носка", "носков") == "pairs");
            Check("Loc: форматная строка переводится целиком",
                Loc.F("готово: {0}", 5) == "done: 5");
            Loc.Install("fr",
                "{\"движение\": \"mouvement\"}",
                "{\"движение\": \"mouvement\", \"залп\": \"salve\", \"_plural\": \"en\"}");
            Check("Loc: третья локаль — своя таблица, фолбэк en, глубже — исходник",
                Loc.Tr("движение") == "mouvement" && Loc.Tr("залп") == "salve" &&
                Loc.Tr("стоп") == "стоп");
            Loc.Install("ru", null, null);
            Check("Loc: сброс в ru — состояние тестов не течёт",
                Loc.Tr("движение") == "движение" && Loc.Language == "ru");

            // ---------- FitLine: однострочная подгонка под ширину панели ----------
            Check("FitLine: короткая строка без изменений",
                Util.FitLine("авиация", 48) == "авиация" &&
                Util.FitLine("авиация", 7) == "авиация");
            Check("FitLine: длинная обрезается с «…» ровно до maxChars",
                Util.FitLine("Приказ: Патруль ×12 → Атака-движ. ×3 → На базу", 20).Length == 20 &&
                Util.FitLine("Приказ: Патруль ×12 → Атака-движ. ×3 → На базу", 20).EndsWith("…"));
            Check("FitLine: пустая/null и maxChars<1",
                Util.FitLine(null, 48) == "" && Util.FitLine("", 48) == "" &&
                Util.FitLine("любой текст", 0) == "");

            // ---------- Гейты вооружения: классификация станции ----------
            Check("Класс: ядерное главнее вида (Я-бомба/Я-ракета → Nuclear)",
                Catalog.ClassOf(true, true, false, false, false, 0f, 0f) == WeaponClass.Nuclear &&
                Catalog.ClassOf(true, false, false, false, true, 0f, 1f) == WeaponClass.Nuclear);
            Check("Класс: бомбы/планирующие → Bomb",
                Catalog.ClassOf(false, true, false, false, false, 0f, 1f) == WeaponClass.Bomb &&
                Catalog.ClassOf(false, false, true, false, false, 0f, 1f) == WeaponClass.Bomb);
            Check("Класс: пушки → Gun (даже ПР/двухдоменные)",
                Catalog.ClassOf(false, false, false, true, false, 1f, 1f) == WeaponClass.Gun &&
                Catalog.ClassOf(false, false, false, true, false, 0f, 0f) == WeaponClass.Gun);
            Check("Класс: ракеты по домену (ВВ/АС/мультидомен/без домена)",
                Catalog.ClassOf(false, false, false, false, true, 0.9f, 0.1f) == WeaponClass.MissileAA &&
                Catalog.ClassOf(false, false, false, false, true, 0.1f, 0.9f) == WeaponClass.MissileAS &&
                Catalog.ClassOf(false, false, false, false, true, 0.5f, 0.5f) == WeaponClass.MissileAS &&
                Catalog.ClassOf(false, false, false, false, true, 0f, 0f) == WeaponClass.MissileAS);
            Check("Класс: лейблы всех классов",
                Catalog.ClassLabel(WeaponClass.Nuclear) == "ЯД" &&
                Catalog.ClassLabel(WeaponClass.Bomb) == "Бомбы" &&
                Catalog.ClassLabel(WeaponClass.Gun) == "Пушки" &&
                Catalog.ClassLabel(WeaponClass.MissileAA) == "Рак.ВВ" &&
                Catalog.ClassLabel(WeaponClass.MissileAS) == "Рак.АС");

            // ---------- Правила ТИПОВ вооружения: цикл/агрегация (v0.106) ----------
            Check("Тип-правило: RuleFor — целевые состояния одинаковы для платформ",
                Catalog.RuleFor(TypeGateState.None, true) == TargetDomain.Both &&
                Catalog.RuleFor(TypeGateState.None, false) == TargetDomain.Both &&
                Catalog.RuleFor(TypeGateState.SurfaceOnly, true) == TargetDomain.Surface &&
                Catalog.RuleFor(TypeGateState.SurfaceOnly, false) == TargetDomain.Surface &&
                Catalog.RuleFor(TypeGateState.AirOnly, true) == TargetDomain.Air &&
                Catalog.RuleFor(TypeGateState.Off, false) == TargetDomain.None);
            Check("Тип-правило: RuleFor — платформенные пресеты гасят чужую платформу",
                Catalog.RuleFor(TypeGateState.GroundLaunch, true) == TargetDomain.None &&
                Catalog.RuleFor(TypeGateState.GroundLaunch, false) == TargetDomain.Both &&
                Catalog.RuleFor(TypeGateState.AirLaunch, true) == TargetDomain.Both &&
                Catalog.RuleFor(TypeGateState.AirLaunch, false) == TargetDomain.None);
            Check("Тип-правило: агрегат — пусто и единые состояния",
                Catalog.AggregateState(new List<(bool, TargetDomain)>()) == TypeGateState.None &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (false, TargetDomain.Surface) }) == TypeGateState.SurfaceOnly &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.Air), (false, TargetDomain.Air) }) == TypeGateState.AirOnly &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.None), (true, TargetDomain.None) }) == TypeGateState.Off);
            Check("Тип-правило: агрегат — платформенные пресеты и вперемешку",
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.None), (false, TargetDomain.Both) }) == TypeGateState.GroundLaunch &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.Both), (false, TargetDomain.None) }) == TypeGateState.AirLaunch &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.Air), (false, TargetDomain.Surface) }) == null &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (false, TargetDomain.Both), (false, TargetDomain.Surface) }) == null);
            Check("Тип-правило: цикл 6 состояний в обе стороны, вперемешку = с начала",
                Catalog.Next(TypeGateState.None, true) == TypeGateState.SurfaceOnly &&
                Catalog.Next(TypeGateState.SurfaceOnly, true) == TypeGateState.AirOnly &&
                Catalog.Next(TypeGateState.AirOnly, true) == TypeGateState.GroundLaunch &&
                Catalog.Next(TypeGateState.GroundLaunch, true) == TypeGateState.AirLaunch &&
                Catalog.Next(TypeGateState.AirLaunch, true) == TypeGateState.Off &&
                Catalog.Next(TypeGateState.Off, true) == TypeGateState.None &&
                Catalog.Next(TypeGateState.None, false) == TypeGateState.Off &&
                Catalog.Next(null, true) == TypeGateState.SurfaceOnly);
            Check("Тип-правило: суффиксы чипа (Latin-1)",
                Catalog.TypeGateSuffix(TypeGateState.None) == "" &&
                Catalog.TypeGateSuffix(TypeGateState.SurfaceOnly) == "·Н" &&
                Catalog.TypeGateSuffix(TypeGateState.AirOnly) == "·В" &&
                Catalog.TypeGateSuffix(TypeGateState.GroundLaunch) == "·з" &&
                Catalog.TypeGateSuffix(TypeGateState.AirLaunch) == "·в" &&
                Catalog.TypeGateSuffix(TypeGateState.Off) == "×" &&
                Catalog.TypeGateSuffix(null) == "±");

            // ---------- Переводы состояний AI (инфопанель) ----------
            Check("Состояния пилота: известные переводятся",
                Catalog.RuPilotState("Bombing") == "бомбометание" &&
                Catalog.RuPilotState("Evading Missiles") == "уклонение от ракет" &&
                Catalog.RuPilotState("taking off") == "взлёт" &&
                Catalog.RuPilotState("parked") == "на стоянке");
            Check("Состояния: неизвестное — как есть, пустое — пусто",
                Catalog.RuPilotState("Final Approach Overshoot") == "Final Approach Overshoot" &&
                Catalog.RuPilotState("") == "" && Catalog.RuPilotState(null) == "" &&
                Catalog.RuShipState("attacking") == "атака" &&
                Catalog.RuShipState("docked") == "у причала" &&
                Catalog.RuShipState("weird") == "weird");

            // ---------- Сводка вооружения группы (инфопанель) ----------
            var guns = new WeaponTallyAccumulator();
            guns.BeginUnit(); // борт 1: две станции AIM-120 + бомбы
            guns.AddStation("AIM-120C", 4, 4, WeaponDomain.Air);
            guns.AddStation("AIM-120C", 2, 2, WeaponDomain.Air);
            guns.AddStation("Mk-82", 4, 6, WeaponDomain.Surface);
            guns.BeginUnit(); // борт 2: одна станция AIM-120 (расстреляна) + пушка
            guns.AddStation("AIM-120C", 0, 4, WeaponDomain.Air);
            guns.AddStation("M61", 480, 500, WeaponDomain.Air | WeaponDomain.Surface);
            Check("Сводка: станции одного имени сливаются (заряд/ёмкость суммируются)",
                guns.Rows.Count == 3 &&
                guns.Rows[0].Name == "AIM-120C" && guns.Rows[0].Ammo == 6 && guns.Rows[0].Full == 10);
            Check("Сводка: носители считаются по юнитам, не по станциям",
                guns.Rows[0].Carriers == 2 && guns.Rows[1].Carriers == 1 && guns.Rows[2].Carriers == 1);
            Check("Сводка: порядок первого появления сохраняется",
                guns.Rows[0].Name == "AIM-120C" && guns.Rows[1].Name == "Mk-82" &&
                guns.Rows[2].Name == "M61");
            Check("Сводка: домены станций OR-ятся в маску",
                guns.Rows[2].Domains == (WeaponDomain.Air | WeaponDomain.Surface));

            var acc2 = new WeaponTallyAccumulator();
            acc2.BeginUnit();
            acc2.AddStation("", 4, 4, WeaponDomain.Air);
            acc2.AddStation("Пустышка", 1, 0, WeaponDomain.Air);
            acc2.AddStation("AIM-9", 2, 2, WeaponDomain.Air);
            Check("Сводка: null/пустое имя и нулевая ёмкость игнорируются",
                acc2.Rows.Count == 1 && acc2.Rows[0].Name == "AIM-9");
            acc2.Clear();
            Check("Сводка: Clear сбрасывает всё", acc2.Rows.Count == 0);
            acc2.BeginUnit();
            acc2.AddStation("AIM-9", 1, 2, WeaponDomain.Air);
            Check("Сводка: после Clear счёт носителей с нуля", acc2.Rows[0].Carriers == 1);

            // ---------- Домен-теги оружия ----------
            Check("Теги: АА/АЗ/У по эффективности",
                Catalog.DomainTag(WeaponDomain.Air) == "АА" &&
                Catalog.DomainTag(WeaponDomain.Surface) == "АЗ" &&
                Catalog.DomainTag(WeaponDomain.Air | WeaponDomain.Surface) == "У");
            Check("Теги: противоракетное/РЭБ/ядерный префикс/пусто",
                Catalog.DomainTag(WeaponDomain.Missile) == "ПР" &&
                Catalog.DomainTag(WeaponDomain.Jammer) == "РЕБ" &&
                Catalog.DomainTag(WeaponDomain.Nuclear | WeaponDomain.Surface) == "Я АЗ" &&
                Catalog.DomainTag(WeaponDomain.None) == "");
            Check("Теги: порог эффективности — как у игры (> 0)",
                Catalog.WeaponDomains(0.01f, 0f, 0f, false, false) == WeaponDomain.Air &&
                Catalog.WeaponDomains(0f, 0.5f, 0f, true, false) ==
                    (WeaponDomain.Surface | WeaponDomain.Nuclear) &&
                Catalog.WeaponDomains(0f, 0f, 0f, false, false) == WeaponDomain.None);

            // ---------- Формат строки сводки ----------
            Check("Строка: «АА AIM-120C: 6/10 · 2 носителя»",
                Catalog.FormatWeaponRow(guns.Rows[0]) == "АА  AIM-120C: 6/10 · 2 носителя",
                Catalog.FormatWeaponRow(guns.Rows[0]));
            Check("Строка: без тега — имя сразу, ядерная — префикс",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "B61", Ammo = 1, Full = 2, Carriers = 1,
                    Domains = WeaponDomain.Nuclear | WeaponDomain.Surface
                }) == "Я АЗ B61: 1/2 · 1 носитель",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "B61", Ammo = 1, Full = 2, Carriers = 1,
                    Domains = WeaponDomain.Nuclear | WeaponDomain.Surface
                }));
            Check("Строка: расстрелянный тип помечается «пусто»",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "Mk-82", Ammo = 0, Full = 6, Carriers = 1, Domains = WeaponDomain.Surface
                }).EndsWith("· пусто"));

            // ---------- Hover-карточка определения (форматтеры SupCom-инфо) ----------
            Check("Карточка: класс · цена · время производства",
                Catalog.FormatTipClass("истребитель", 150f, 12f, false) == "истребитель · 150 · ~12 с");
            Check("Карточка: стройка помечена, время инженера",
                Catalog.FormatTipClass("здание", 300f, 20f, true) == "здание · 300 · стройка ~20 с");
            Check("Карточка: без времени — только класс и цена",
                Catalog.FormatTipClass("танк", 45f, 0f, false) == "танк · 45");
            Check("Карточка: ТТХ факт-на-строку (скорость/обзор/броня·экипаж·масса)",
                string.Join("|", Catalog.TipStatRows(8000f, 250f, 2f, 1f, 9500f)) ==
                "скорость 250 м/с|обзор 8 км|броня 2 · экипаж 1 · масса ≈9.5 т",
                string.Join("|", Catalog.TipStatRows(8000f, 250f, 2f, 1f, 9500f)));
            Check("Карточка: обзор до километра — в метрах",
                Catalog.TipStatRows(800f, 0f, 0f, 0f, 0f).Count == 1 &&
                Catalog.TipStatRows(800f, 0f, 0f, 0f, 0f)[0] == "обзор 800 м");
            Check("Карточка: десятые км — инвариантная точка (детерминизм)",
                Catalog.TipStatRows(9500f, 0f, 0f, 0f, 0f)[0] == "обзор 9.5 км",
                Catalog.TipStatRows(9500f, 0f, 0f, 0f, 0f)[0]);
            Check("Карточка: нули — пустой список",
                Catalog.TipStatRows(0f, 0f, 0f, 0f, 0f).Count == 0);
            Check("Карточка: строка оружия — тег + ×боезапас (нагрузка всегда полная)",
                Catalog.FormatTipWeaponRow(new WeaponTally
                {
                    Name = "AIM-120C", Ammo = 4, Full = 4, Carriers = 1, Domains = WeaponDomain.Air
                }) == "АА  AIM-120C ×4",
                Catalog.FormatTipWeaponRow(new WeaponTally
                {
                    Name = "AIM-120C", Ammo = 4, Full = 4, Carriers = 1, Domains = WeaponDomain.Air
                }));
            Check("Карточка: РЭБ-тег у постановщика помех",
                Catalog.FormatTipWeaponRow(new WeaponTally
                {
                    Name = "AN/ALQ-131", Ammo = 1, Full = 1, Carriers = 1, Domains = WeaponDomain.Jammer
                }) == "РЕБ AN/ALQ-131 ×1");

            // ---------- Util.ZoomCurvePitch: кривая наклона стратегической камеры ----------
            Check("Кривая: у поверхности — низ кривой (круто в землю)",
                Util.ZoomCurvePitch(100f, 60f, 87f, 350f, 6000f) == 60f &&
                Util.ZoomCurvePitch(350f, 60f, 87f, 350f, 6000f) == 60f);
            Check("Кривая: из космоса — полный вид сверху",
                Util.ZoomCurvePitch(6000f, 60f, 87f, 350f, 6000f) == 87f &&
                Util.ZoomCurvePitch(50000f, 60f, 87f, 350f, 6000f) == 87f);
            Check("Кривая: монотонность по дистанции",
                Util.ZoomCurvePitch(500f, 60f, 87f, 350f, 6000f) < Util.ZoomCurvePitch(1500f, 60f, 87f, 350f, 6000f) &&
                Util.ZoomCurvePitch(1500f, 60f, 87f, 350f, 6000f) < Util.ZoomCurvePitch(4000f, 60f, 87f, 350f, 6000f));
            Check("Кривая: пологий низ — у поверхности наклон «чуть меняет» (< 2° на двойной дистанции)",
                Util.ZoomCurvePitch(700f, 60f, 87f, 350f, 6000f) - 60f < 2f);
            Check("Кривая: S-форма — первая четверть положе линейной (пологий разгон от земли)",
                Util.ZoomCurvePitch(1762.5f, 60f, 87f, 350f, 6000f) < 66.75f);

            // ---------- Util.AssignBalanced: раздача «веера» по целям ----------
            var fanUnits = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(10f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f), new UnityEngine.Vector3(110f, 0f, 0f)
            };
            var fanTargets = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(5f, 0f, 0f), new UnityEngine.Vector3(105f, 0f, 0f)
            };
            int[] fan = Util.AssignBalanced(fanUnits, fanTargets);
            Check("Веер: 4/2 — по двое к ближним (ёмкости 2+2)",
                fan != null && fan.SequenceEqual(new[] { 0, 0, 1, 1 }),
                fan != null ? string.Join(",", fan) : "null");

            var fan5u = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 1f, 0f), new UnityEngine.Vector3(0f, 2f, 0f),
                new UnityEngine.Vector3(0f, 3f, 0f), new UnityEngine.Vector3(100f, 1f, 0f),
                new UnityEngine.Vector3(100f, 2f, 0f)
            };
            var fan2t = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(100f, 0f, 0f)
            };
            int[] fan5 = Util.AssignBalanced(fan5u, fan2t);
            Check("Веер: 5/2 — ёмкости 3+2, лишний к первой цели (порядок клика)",
                fan5.SequenceEqual(new[] { 0, 0, 0, 1, 1 }),
                string.Join(",", fan5));

            int[] fan1 = Util.AssignBalanced(
                new List<UnityEngine.Vector3> { new UnityEngine.Vector3(90f, 0f, 0f) },
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(100f, 0f, 0f)
                });
            Check("Веер: единственный атакующий — к ближней цели", fan1[0] == 1);

            int[] fanTie = Util.AssignBalanced(
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(0f, 0f, 0f)
                },
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(10f, 0f, 0f), new UnityEngine.Vector3(-10f, 0f, 0f)
                });
            Check("Веер: эквидистанта — детерминизм (меньшие индексы вперёд)",
                fanTie.SequenceEqual(new[] { 0, 1 }), string.Join(",", fanTie));
            Check("Веер: без целей — null", Util.AssignBalanced(fanUnits, new List<UnityEngine.Vector3>()) == null);

            // ---------- GroundOrders.DispersePick: точка группы в остатке очереди ----------
            var fanChain = new GroundOrders.Chain();
            foreach (int g in new[] { 7, 9, 7, 0 })
            {
                fanChain.Points.Add(new UnityEngine.Vector3(g, 0f, 0f).ToGlobalPosition());
                fanChain.PointGroups.Add(g);
            }
            fanChain.Index = 1; // первая точка пройдена
            Check("Наземка: группа находится в остатке (первая от Index)",
                GroundOrders.DispersePick(fanChain, 7) == 2 &&
                GroundOrders.DispersePick(fanChain, 9) == 1);
            Check("Наземка: индивидуальные точки (0) не распределяются",
                GroundOrders.DispersePick(fanChain, 0) == -1);
            Check("Наземка: чужой groupId — нет",
                GroundOrders.DispersePick(fanChain, 99) == -1);
            fanChain.Index = 4;
            Check("Наземка: пройденная очередь — пикер пуст",
                GroundOrders.DispersePick(fanChain, 7) == -1);

            // ---------- GroundOrders.TargetDue: цель дозревает на СВОЕЙ позиции серии ----------
            Check("Смешанная очередь: цель за точками на момент клика — ждёт их",
                !GroundOrders.TargetDue(1, 2, 3) && GroundOrders.TargetDue(2, 2, 3));
            Check("Смешанная очередь: точки, кликнутые ПОСЛЕ цели, её не задерживают",
                GroundOrders.TargetDue(1, 1, 5));
            Check("Смешанная очередь: цель первой в серии (точек до неё нет)",
                GroundOrders.TargetDue(0, 0, 2));
            Check("Смешанная очередь: удаления точек клампят позицию к длине очереди",
                GroundOrders.TargetDue(2, 9, 2));

            // ---------- GroundOrders.RemoveAtCore: ядро удаления точки ----------
            // (общее для одиночного Ctrl+Shift+ПКМ и группового удаления по groupId)
            var rmChain = new GroundOrders.Chain();
            foreach (int g in new[] { 1, 7, 2, 7 })
            {
                rmChain.Points.Add(new UnityEngine.Vector3(g * 10f, 0f, 0f).ToGlobalPosition());
                rmChain.PointGroups.Add(g);
            }
            rmChain.TargetAfter.Add(1); // позиция серии первой цели
            rmChain.TargetAfter.Add(3); // и второй
            rmChain.Index = 2;
            GroundOrders.RemoveAtCore(rmChain, 0); // удаление ДО текущей
            Check("Ядро удаления: точка до текущей — курсор и позиции целей сдвигаются",
                rmChain.Points.Count == 3 && rmChain.Index == 1 &&
                rmChain.TargetAfter[0] == 0 && rmChain.TargetAfter[1] == 2);
            GroundOrders.RemoveAtCore(rmChain, 1); // удаление самой текущей
            Check("Ядро удаления: текущая точка — курсор остаётся (указывает на следующую)",
                rmChain.Points.Count == 2 && rmChain.Index == 1 && rmChain.PointGroups[1] == 7);
            GroundOrders.RemoveAtCore(rmChain, 9);
            Check("Ядро удаления: невалидный индекс — no-op",
                rmChain.Points.Count == 2 && rmChain.Index == 1);

            // ---------- GroundOrders.FanBearingOffset: секторы полукруга атаки ----------
            Check("Полукруг: симметричный веер, центр без смещения",
                UnityEngine.Mathf.Abs(GroundOrders.FanBearingOffset(0, 3, 30f, 480f) +
                                      GroundOrders.FanBearingOffset(2, 3, 30f, 480f)) < 1e-5f &&
                GroundOrders.FanBearingOffset(1, 3, 30f, 480f) == 0f);
            Check("Полукруг: шаг дуги = arcStep/радиус",
                UnityEngine.Mathf.Abs(GroundOrders.FanBearingOffset(1, 2, 30f, 480f) - 15f / 480f) < 1e-5f);
            Check("Полукруг: одиночка — без смещения",
                GroundOrders.FanBearingOffset(0, 1, 30f, 480f) == 0f);
            Check("Полукруг: нулевой радиус не делит на ноль",
                !float.IsNaN(GroundOrders.FanBearingOffset(1, 3, 30f, 0f)) &&
                !float.IsInfinity(GroundOrders.FanBearingOffset(1, 3, 30f, 0f)));

            // ---------- AircraftOrders.TryDispersePoint: транспортные точки плана ----------
            var dpHead = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit, Index = 1 };
            dpHead.PointGroups.Add(5);
            dpHead.PointGroups.Add(6);
            var dpAtk = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Attack };
            var dpTail = new AircraftOrders.OrderSegment { Mode = AirOrderMode.AttackMove };
            dpTail.PointGroups.Add(7);
            var dpPlan = new List<AircraftOrders.OrderSegment> { dpHead, dpAtk, dpTail };
            Check("Авиация: группа в хвостовом транспортном сегменте находится",
                AircraftOrders.TryDispersePoint(dpPlan, 7, out int ds, out int dp) && ds == 2 && dp == 0);
            Check("Авиация: пройденная точка головы пропускается (Index=1)",
                AircraftOrders.TryDispersePoint(dpPlan, 5, out _, out _) == false &&
                AircraftOrders.TryDispersePoint(dpPlan, 6, out _, out int dp6) && dp6 == 1);
            Check("Авиация: атака/залп/патруль/RTB в распределении не участвуют",
                !AircraftOrders.TryDispersePoint(new List<AircraftOrders.OrderSegment>
                {
                    new AircraftOrders.OrderSegment { Mode = AirOrderMode.Patrol }
                }, 5, out _, out _) &&
                !AircraftOrders.TryDispersePoint(dpPlan, 99, out _, out _));

            // ---------- RangeRings.ClipOutRect: вырезание окна миникарты из отрезка ----------
            {
                var rect = new UnityEngine.Rect(10f, 10f, 20f, 20f); // x 10..30, y 10..30
                int n = RangeRings.ClipOutRect(0f, 15f, 40f, 15f, rect,
                    out float ax0, out float ay0, out float ax1, out float ay1,
                    out float bx0, out float by0, out float bx1, out float by1);
                Check("Клип: сквозной отрезок — два наружных куска по краям окна",
                    n == 2 && ax0 == 0f && ax1 == 10f && bx0 == 30f && bx1 == 40f &&
                    ay0 == 15f && by1 == 15f,
                    "n=" + n + " A=" + ax0 + "," + ay0 + "-" + ax1 + "," + ay1 + " B=" + bx0 + "," + by0 + "-" + bx1 + "," + by1);
                n = RangeRings.ClipOutRect(15f, 15f, 15f, 15f, rect,
                    out ax0, out ay0, out ax1, out ay1, out bx0, out by0, out bx1, out by1);
                Check("Клип: целиком внутри окна — рисовать нечего", n == 0, "n=" + n);
                n = RangeRings.ClipOutRect(0f, 40f, 40f, 40f, rect,
                    out ax0, out ay0, out ax1, out ay1, out bx0, out by0, out bx1, out by1);
                Check("Клип: мимо окна — один кусок, отрезок не тронут",
                    n == 1 && ax0 == 0f && ay0 == 40f && ax1 == 40f && ay1 == 40f,
                    "n=" + n + " A=" + ax0 + "," + ay0 + "-" + ax1 + "," + ay1);
                n = RangeRings.ClipOutRect(15f, 15f, 40f, 15f, rect,
                    out ax0, out ay0, out ax1, out ay1, out bx0, out by0, out bx1, out by1);
                Check("Клип: начинается внутри, выходит — только хвост",
                    n == 1 && ax0 == 30f && ay0 == 15f && ax1 == 40f && ay1 == 15f,
                    "n=" + n + " A=" + ax0 + "," + ay0 + "-" + ax1 + "," + ay1);
                n = RangeRings.ClipOutRect(0f, 15f, 20f, 15f, rect,
                    out ax0, out ay0, out ax1, out ay1, out bx0, out by0, out bx1, out by1);
                Check("Клип: заканчивается внутри — только голова",
                    n == 1 && ax0 == 0f && ax1 == 10f && ay1 == 15f,
                    "n=" + n + " A=" + ax0 + "," + ay0 + "-" + ax1 + "," + ay1);
            }

            // ---------- RangeRings: сетка высот рельефа (ключ ячейки, билинейная) ----------
            {
                long a = RangeRings.CellKey(25f, 25f);   // ячейка (0,0): 0..40 по обеим осям
                long b = RangeRings.CellKey(39f, 1f);    // та же ячейка
                long c = RangeRings.CellKey(41f, 25f);   // соседняя по x
                long d = RangeRings.CellKey(25f, -5f);   // соседняя по z (отрицательная)
                long e = RangeRings.CellKey(-5f, -5f);   // отрицательная по обеим
                Check("Высоты: ключ ячейки одинаков внутри, различен между (вкл. отрицательные)",
                    a == b && a != c && a != d && c != d && d != e && e != a, "");
                Check("Высоты: билинейная — углы дают свои высоты, центр — среднее",
                    UnityEngine.Mathf.Abs(RangeRings.Bilinear(0f, 0f, 0f, 0f, 0.7f, 0.3f)) < 1e-6f &&
                    UnityEngine.Mathf.Abs(RangeRings.Bilinear(0f, 40f, 0f, 40f, 1f, 0.5f) - 40f) < 1e-6f &&
                    UnityEngine.Mathf.Abs(RangeRings.Bilinear(0f, 0f, 20f, 40f, 0.5f, 0.5f) - 15f) < 1e-6f,
                    "");
            }

            // ---------- Util.OneEuro: адаптивный фильтр курсора пининга ----------
            var euro = new Util.OneEuro();
            float edt = 1f / 60f;
            Check("OneEuro: первый вызов проходит без сглаживания",
                euro.Filter(new UnityEngine.Vector2(5f, 7f), edt) == new UnityEngine.Vector2(5f, 7f));

            // Дрожь сенсора в покое: курсор мечется 0↔1 px — выход почти неподвижен
            // (меряем установившийся размах после прогрева, не выход на середину).
            var euroNoise = new Util.OneEuro();
            for (int i = 0; i < 240; i++)
                euroNoise.Filter(new UnityEngine.Vector2(i % 2, 0f), edt);
            float outMin = 1f, outMax = 0f;
            for (int i = 0; i < 60; i++)
            {
                float x = euroNoise.Filter(new UnityEngine.Vector2(i % 2, 0f), edt).x;
                outMin = UnityEngine.Mathf.Min(outMin, x);
                outMax = UnityEngine.Mathf.Max(outMax, x);
            }
            Check("OneEuro: дрожь ±1 px в покое гасится (размах выхода < 0.3 px)",
                outMax - outMin < 0.3f, $"размах {(outMax - outMin):F2}");

            // Равномерное движение 600 px/s: фильтр ведёт курсор с малым лагом.
            var euro2 = new Util.OneEuro();
            float lag = 0f;
            for (int i = 1; i <= 90; i++)
            {
                float target = 600f * edt * i;
                lag = UnityEngine.Mathf.Max(lag, target - euro2.Filter(new UnityEngine.Vector2(target, 0f), edt).x);
            }
            Check("OneEuro: равномерное движение — установившийся лаг < 30 px", lag < 30f, $"лаг {lag:F1}");

            // Скачок 0→200 px: сходится за ~0.2 с.
            var euro3 = new Util.OneEuro();
            euro3.Filter(UnityEngine.Vector2.zero, edt);
            for (int i = 0; i < 11; i++)
                euro3.Filter(new UnityEngine.Vector2(200f, 0f), edt);
            Check("OneEuro: скачок 200 px — за 12 кадров больше 150", euro3.Value.x > 150f, $"x {euro3.Value.x:F0}");

            // dt <= 0: значение не меняется (старое, не NaN).
            Check("OneEuro: dt=0 держит прошлое значение",
                euro3.Filter(new UnityEngine.Vector2(999f, 0f), 0f).x == euro3.Value.x);

            // ---------- Util.CatmullRom / TrailPointAt: сплайн следа сопровождения ----------
            var pA = new UnityEngine.Vector3(0f, 0f, 0f);
            var pB = new UnityEngine.Vector3(10f, 0f, 0f);
            var pC = new UnityEngine.Vector3(20f, 0f, 0f);
            var pD = new UnityEngine.Vector3(30f, 0f, 0f);
            Check("CatmullRom: t=0 — p1, t=1 — p2 (с клампом p3)",
                (Util.CatmullRom(pA, pB, pC, pC, 0f) - pB).magnitude < 1e-4f &&
                (Util.CatmullRom(pA, pB, pC, pC, 1f) - pC).magnitude < 1e-4f);
            var mid = Util.CatmullRom(pA, pB, pC, pD, 0.5f);
            Check("CatmullRom: равномерные коллинеарные точки — середина на прямой (x=15)",
                UnityEngine.Mathf.Abs(mid.x - 15f) < 1e-3f && UnityEngine.Mathf.Abs(mid.z) < 1e-3f);

            // Прямой след вдоль +X (heading = atan2(1,0) = π/2): точки через 25 м,
            // tail — живая позиция цели ещё на 25 дальше.
            var trail = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(25f, 0f, 0f),
                new UnityEngine.Vector3(50f, 0f, 0f),
                new UnityEngine.Vector3(75f, 0f, 0f)
            };
            var tailPos = new UnityEngine.Vector3(100f, 0f, 0f);
            bool okTrail = Util.TrailPointAt(trail, tailPos, 30f, out var at30, out float head30);
            Check("TrailPointAt: прямой след — точка в 30 м позади tail (x≈70)",
                okTrail && UnityEngine.Mathf.Abs(at30.x - 70f) < 1e-3f && UnityEngine.Mathf.Abs(at30.z) < 1e-3f,
                $"x {at30.x:F2}");
            Check("TrailPointAt: курс следа вдоль +X — heading = π/2",
                UnityEngine.Mathf.Abs(UnityEngine.Mathf.DeltaAngle(
                    head30 * UnityEngine.Mathf.Rad2Deg, 90f)) < 0.5f,
                $"{head30:F3}");
            Check("TrailPointAt: arcBehind=0 — сама цель (tail)",
                Util.TrailPointAt(trail, tailPos, 0f, out var at0, out _) &&
                (at0 - tailPos).magnitude < 0.1f);
            Check("TrailPointAt: дуга больше следа — false (деградация в кольцо)",
                !Util.TrailPointAt(trail, tailPos, 200f, out _, out _));
            Check("TrailPointAt: пустой след — false",
                !Util.TrailPointAt(new List<UnityEngine.Vector3>(), tailPos, 10f, out _, out _));
            // Поворот следа: 75 м вдоль +X, дальше цель ушла на +Z — якорь ДО
            // поворота остаётся на прямой части (x≈35 при 40 м дуги), якорь ЗА
            // поворотом сходит на вертикальную ногу (сплайн срезает угол изнутри).
            var turn = new UnityEngine.Vector3(75f, 0f, 0f);
            Check("TrailPointAt: до поворота — точка на прямой части следа",
                Util.TrailPointAt(trail, turn, 40f, out var atTurn, out _) &&
                UnityEngine.Mathf.Abs(atTurn.x - 35f) < 1e-3f && UnityEngine.Mathf.Abs(atTurn.z) < 1e-3f);
            var tailTurn = new UnityEngine.Vector3(75f, 0f, 40f);
            Check("TrailPointAt: за поворотом — сходит на новую ногу (z растёт, x у угла)",
                Util.TrailPointAt(trail, tailTurn, 10f, out var atAfter, out _) &&
                atAfter.z > 10f && atAfter.z < 40f && UnityEngine.Mathf.Abs(atAfter.x - 75f) < 1f,
                $"({atAfter.x:F1}, {atAfter.z:F1})");

            // ---------- Util.RouteSeenBefore: дедуп общего ралли пачки фабрик ----------
            var rA1 = new List<UnityEngine.Vector3> { new(10f, 0f, 10f), new(200f, 0f, 40f) };
            var rA2 = new List<UnityEngine.Vector3> { new(10f, 0f, 10f), new(200f, 0f, 40f) };
            var rB = new List<UnityEngine.Vector3> { new(10f, 0f, 10f), new(300f, 0f, 40f) };
            var rAshort = new List<UnityEngine.Vector3> { new(10f, 0f, 10f) };
            var seenFlat = new List<UnityEngine.Vector3>();
            var seenLens = new List<int>();
            Check("RouteSeenBefore: пустая история — не дубликат",
                !Util.RouteSeenBefore(seenFlat, seenLens, rA1));
            seenFlat.AddRange(rA1);
            seenLens.Add(rA1.Count);
            Check("RouteSeenBefore: тот же маршрут — дубликат",
                Util.RouteSeenBefore(seenFlat, seenLens, rA2));
            Check("RouteSeenBefore: другая точка — не дубликат",
                !Util.RouteSeenBefore(seenFlat, seenLens, rB));
            Check("RouteSeenBefore: префикс (другая длина) — не дубликат",
                !Util.RouteSeenBefore(seenFlat, seenLens, rAshort));
            seenFlat.AddRange(rB);
            seenLens.Add(rB.Count);
            Check("RouteSeenBefore: второй в истории ищется за первым",
                Util.RouteSeenBefore(seenFlat, seenLens, rB) &&
                Util.RouteSeenBefore(seenFlat, seenLens, rA1) &&
                !Util.RouteSeenBefore(seenFlat, seenLens, rAshort));

            Console.WriteLine(_failed == 0 ? "ВСЕ ТЕСТЫ ПРОЙДЕНЫ" : "ПРОВАЛОВ: " + _failed);
            return _failed == 0 ? 0 : 1;
        }
    }
}
