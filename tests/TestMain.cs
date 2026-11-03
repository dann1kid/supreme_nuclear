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

            // ---------- Util.FieldSetter: скомпилированный сеттер поля ----------
            var holder = new FieldHolder();
            System.Reflection.FieldInfo strField = typeof(FieldHolder).GetField("_text",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            System.Reflection.FieldInfo vecField = typeof(FieldHolder).GetField("_vec",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Action<object, string> setStr = Util.FieldSetter<string>(strField);
            Action<object, UnityEngine.Vector3> setVec = Util.FieldSetter<UnityEngine.Vector3>(vecField);
            setStr(holder, "abc");
            setVec(holder, new UnityEngine.Vector3(1f, 2f, 3f));
            Check("FieldSetter: ссылочное и структурное поля",
                holder.Text == "abc" && holder.Vec == new UnityEngine.Vector3(1f, 2f, 3f));
            Check("FieldSetter: null поля → null", Util.FieldSetter<string>(null) == null);

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
            // Порог (0,0,0), ось взлёта farDir = +X: ангар сбоку (+Z), ворота к полосе.
            var thr = new UnityEngine.Vector3(0f, 0f, 0f);
            var axisX = new UnityEngine.Vector3(1f, 0f, 0f);
            var toSide = new UnityEngine.Vector3(0f, 0f, -1f); // ворота ангара к полосе

            // ---------- Util.TaxiApproachPoints: «якоря и Безье» (v0.102.2) ----------
            System.Func<UnityEngine.Vector3, UnityEngine.Vector3, System.Collections.Generic.List<UnityEngine.Vector3>> approach =
                (s, f) => Util.TaxiApproachPoints(thr, s, f, axisX);

            // Прямой выезд из ворот: первая точка — по направлению ворот
            var a1 = approach(new UnityEngine.Vector3(240f, 0f, 110f), toSide);
            UnityEngine.Vector3 stubDir = a1[0] - new UnityEngine.Vector3(240f, 0f, 110f);
            Check("TaxiApproach: прямой выезд из ворот (первая точка вдоль fwd, ≥10 м)",
                UnityEngine.Vector3.Dot(stubDir.normalized, toSide) > 0.99f &&
                stubDir.magnitude >= 10f, "стаб " + stubDir.magnitude.ToString("F1") + " м");

            // Выкат по оси: хвост полилинии (последняя точка + торец) вдоль оси взлёта
            var poly1 = new System.Collections.Generic.List<UnityEngine.Vector3> { new UnityEngine.Vector3(240f, 0f, 110f) };
            poly1.AddRange(a1); poly1.Add(thr);
            UnityEngine.Vector3 tailDir = poly1[poly1.Count - 1] - poly1[poly1.Count - 2];
            Check("TaxiApproach: выкат в торец вдоль оси взлёта (последняя нога ~farDir)",
                UnityEngine.Vector3.Dot(tailDir.normalized, axisX) > 0.95f &&
                UnityEngine.Mathf.Abs(poly1[poly1.Count - 2].z) < 1.5f);

            // Ангар ВДОЛЬ полосы: якорь выката ЗА порогом (прямая по оси до торца)
            Check("TaxiApproach: ангар вдоль полосы — якорь за порогом на оси",
                poly1[poly1.Count - 2].x < -30f && poly1[poly1.Count - 2].x > -95f,
                "x " + poly1[poly1.Count - 2].x.ToString("F0"));

            // Гладкость на значимых сегментах (≥15 м): поворот ≤30° по всему
            // набору позиций/ориентаций — «просто плавная линия», без изломов
            // (коллектор v0.100 и складки v0.100.0 отвергнуты плейтестами).
            UnityEngine.Vector3[][] smoothCases =
            {
                new[] { new UnityEngine.Vector3(240f, 0f, 110f), toSide },
                new[] { new UnityEngine.Vector3(560f, 0f, 110f), toSide },
                new[] { new UnityEngine.Vector3(-150f, 0f, 120f), toSide },
                new[] { new UnityEngine.Vector3(240f, 0f, 200f), toSide },
                new[] { new UnityEngine.Vector3(80f, 0f, 110f), toSide },
                new[] { new UnityEngine.Vector3(240f, 0f, 110f), new UnityEngine.Vector3(1f, 0f, 0f) },      // вдоль полосы
                new[] { new UnityEngine.Vector3(240f, 0f, 110f), new UnityEngine.Vector3(0f, 0f, 1f) },      // спиной
                new[] { new UnityEngine.Vector3(240f, 0f, 110f), new UnityEngine.Vector3(0.7071f, 0f, 0.7071f) }, // диагональю
                new[] { new UnityEngine.Vector3(-160f, 0f, 0f), new UnityEngine.Vector3(1f, 0f, 0f) },       // на оси за порогом
            };
            float worstTurn = 0f;
            foreach (UnityEngine.Vector3[] sc in smoothCases)
            {
                var pts = approach(sc[0], sc[1]);
                var poly = new System.Collections.Generic.List<UnityEngine.Vector3> { sc[0] };
                foreach (UnityEngine.Vector3 p in pts)
                    if (UnityEngine.Vector3.Distance(p, poly[poly.Count - 1]) > 0.5f)
                        poly.Add(p);
                if ((thr - poly[poly.Count - 1]).sqrMagnitude > 0.25f)
                    poly.Add(thr);
                for (int i = 1; i < poly.Count - 1; i++)
                {
                    float l1 = UnityEngine.Vector3.Distance(poly[i], poly[i - 1]);
                    float l2 = UnityEngine.Vector3.Distance(poly[i + 1], poly[i]);
                    if (l1 < 15f || l2 < 15f)
                        continue; // микровихляния тонут в 24-метровой полосе
                    float segTurn = UnityEngine.Vector3.Angle(poly[i] - poly[i - 1], poly[i + 1] - poly[i]);
                    worstTurn = UnityEngine.Mathf.Max(worstTurn, segTurn);
                }
            }
            Check("TaxiApproach: плавность — поворот ≤30° на сегментах ≥15 м",
                worstTurn <= 30f, "макс " + worstTurn.ToString("F0") + "°");

            // Контроль старта не пробивает полосу: линия не опускается сильно
            // ниже кромки (22.5) ДО зоны порога (x > 40)
            bool edgeOk = true;
            foreach (UnityEngine.Vector3 p in a1)
                if (p.x > 40f && p.z < 12f)
                    edgeOk = false;
            Check("TaxiApproach: вне зоны порога не проваливается к оси (z ≥ 12 при x > 40)", edgeOk);

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

            // ---------- Util.HermiteChain: Эрмит-сглаживание лент (канон Moho) ----------
            var rcSmooth = new List<UnityEngine.Vector3>();
            var rcLegs = new List<int>();
            var rcCorner = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 100f),
            };
            int rcn = Util.HermiteChain(rcCorner, null, 1f, 200f, rcSmooth, rcLegs);
            bool RcHas(float x, float z, float eps = 0.01f) => rcSmooth.Any(v =>
                Math.Abs(v.x - x) < eps && Math.Abs(v.z - z) < eps);
            Check("Эрмит: концы цепочки и вейпоинты лежат НА кривой",
                rcSmooth[0] == rcCorner[0] && rcSmooth[rcn - 1] == rcCorner[2] && RcHas(100f, 0f),
                "n=" + rcn);
            Check("Эрмит: legStart — по ноге на точку, монотонно",
                rcLegs.Count == 2 && rcLegs[0] == 0 && rcLegs[1] > 0,
                string.Join(",", rcLegs));
            // Поворот 90° (ноги по 100 м, m=25): кривая идёт ЧЕРЕЗ вейпоинт касательно
            // (митр 45°) с ограниченным перелётом НАРУЖУ — свойство Эрмита с митр-
            // тангенсами (как Catmull-Rom), канон FA: sag ≈ 0.105·m·0.707 ≈ 1.9 м.
            float rcSag = 0f;
            foreach (var v in rcSmooth)
            {
                float m1 = v.x <= 100f ? Math.Max(-v.z, 0f) : 0f; // провал под ногу 1 (на её пролёте)
                float m2 = v.z >= 0f && v.z <= 100f ? Math.Max(v.x - 100f, 0f) : 0f; // вылет за ногу 2
                rcSag = Math.Max(rcSag, Math.Max(m1, m2));
            }
            Check("Эрмит: поворот 90° — касательный проход, перелёт наружу ограничен",
                rcSag > 0.5f && rcSag < 4f,
                "sag=" + Math.Round(rcSag, 2) + " " +
                string.Join(" ", rcSmooth.Select(v => Math.Round(v.x) + ";" + Math.Round(v.z))));

            var rcLine = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(50f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
            };
            int rcLn = Util.HermiteChain(rcLine, null, 1f, 200f, rcSmooth, rcLegs);
            Check("Эрмит: коллинеарная цепочка — прямая (митр-тангенс вдоль ноги)",
                rcSmooth.All(v => Math.Abs(v.z) < 0.5f) && rcLegs.Count == 2,
                string.Join(" ", rcSmooth.Select(v => Math.Round(v.z, 2))));

            var rcLong = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(1000f, 0f, 0f),
            };
            int rcSegN = Util.HermiteChain(rcLong, null, 1f, 200f, rcSmooth, rcLegs);
            Check("Эрмит: сегменты по длине ноги (1000 м / 200 м = 6 точек)",
                rcSegN == 6 && rcLegs.Count == 1 && rcLegs[0] == 0, "n=" + rcSegN);
            var rcHuge = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(10000f, 0f, 0f),
            };
            int rcCapN = Util.HermiteChain(rcHuge, null, 1f, 200f, rcSmooth, rcLegs);
            Check("Эрмит: кламп 20 сегментов на ногу (ui_CurveSegments FA)",
                rcCapN == 21, "n=" + rcCapN);

            var rcDup = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 100f),
            };
            int rcDn = Util.HermiteChain(rcDup, null, 1f, 200f, rcSmooth, rcLegs);
            Check("Эрмит: вырожденная нога пропущена (legStart без точек), конец на месте",
                rcDn > 0 && rcSmooth[rcDn - 1] == rcDup[3] && rcLegs.Count == 3 &&
                rcLegs[0] == 0 && rcLegs[1] == 0,
                "n=" + rcDn + " legs=" + string.Join(",", rcLegs));

            // Кламп тангенса толщиной ноги (шире лента — шире заход в поворот):
            // на длинных сторонах m = min(¼, 50·w) растёт с толщиной — кривая
            // отклоняется от ломаной сильнее (выпуклый заход), максимальное
            // отклонение от полилинии больше.
            var rcWide = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(1000f, 0f, 0f),
                new UnityEngine.Vector3(1000f, 0f, 1000f),
            };
            var rcW1 = new List<float> { 1f, 1f };
            var rcW3 = new List<float> { 3f, 3f };
            int rw1 = Util.HermiteChain(rcWide, rcW1, 1f, 100f, rcSmooth, rcLegs);
            float dev1 = 0f;
            for (int i = 0; i < rw1; i++)
            {
                var v = rcSmooth[i];
                // расстояние до полилинии (0,0)-(1000,0)-(1000,1000)
                float dLeg1 = v.x <= 1000f ? UnityEngine.Mathf.Abs(v.z)
                    : UnityEngine.Mathf.Sqrt((v.x - 1000f) * (v.x - 1000f) + v.z * v.z);
                float dx = UnityEngine.Mathf.Max(0f, v.x - 1000f);
                float dz = UnityEngine.Mathf.Max(0f, -v.z);
                float dLeg2 = UnityEngine.Mathf.Sqrt(dx * dx + dz * dz);
                dev1 = UnityEngine.Mathf.Max(dev1, UnityEngine.Mathf.Min(dLeg1, dLeg2));
            }
            int rw3 = Util.HermiteChain(rcWide, rcW3, 1f, 100f, rcSmooth, rcLegs);
            float dev3 = 0f;
            for (int i = 0; i < rw3; i++)
            {
                var v = rcSmooth[i];
                float dLeg1 = v.x <= 1000f ? UnityEngine.Mathf.Abs(v.z)
                    : UnityEngine.Mathf.Sqrt((v.x - 1000f) * (v.x - 1000f) + v.z * v.z);
                float dx = UnityEngine.Mathf.Max(0f, v.x - 1000f);
                float dz = UnityEngine.Mathf.Max(0f, -v.z);
                float dLeg2 = UnityEngine.Mathf.Sqrt(dx * dx + dz * dz);
                dev3 = UnityEngine.Mathf.Max(dev3, UnityEngine.Mathf.Min(dLeg1, dLeg2));
            }
            Check("Эрмит: толщина ноги клампит тангенс — широкая лента заходит шире",
                dev3 > dev1 + 0.5f,
                "dev1=" + Math.Round(dev1, 2) + " dev3=" + Math.Round(dev3, 2));
            // Наши ленты (RouteRibbonWidthM=40): кламп 50×40=2000 м не душит
            // кривизну — тангенсы на ¼ ноги, поворот 90° длинных сторон выходит
            // ШИРОКОЙ дугой (усадка клампом до 50 м была багом «прямых линий»).
            var rcN1 = new List<float> { 1f, 1f };
            int rwn = Util.HermiteChain(rcWide, rcN1, 40f, 100f, rcSmooth, rcLegs);
            float devn = 0f;
            for (int i = 0; i < rwn; i++)
            {
                var v = rcSmooth[i];
                float dLeg1 = v.x <= 1000f ? UnityEngine.Mathf.Abs(v.z)
                    : UnityEngine.Mathf.Sqrt((v.x - 1000f) * (v.x - 1000f) + v.z * v.z);
                float dx = UnityEngine.Mathf.Max(0f, v.x - 1000f);
                float dz = UnityEngine.Mathf.Max(0f, -v.z);
                float dLeg2 = UnityEngine.Mathf.Sqrt(dx * dx + dz * dz);
                devn = UnityEngine.Mathf.Max(devn, UnityEngine.Mathf.Min(dLeg1, dLeg2));
            }
            Check("Эрмит: широкая лента (40 м) — тангенс на четверть ноги, дуга широкая",
                devn > 20f && devn > dev1 * 2f,
                "devn=" + Math.Round(devn, 1) + " dev1=" + Math.Round(dev1, 2));

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
            var task = new BuildTasks.Task { Cost = 250f, PaidMass = 100f };
            Check("Progress01: 100/250 = 0.4", Math.Abs(task.Progress01 - 0.4f) < 0.001f);
            task.PaidMass = 999f;
            Check("Progress01: кламп сверху", Math.Abs(task.Progress01 - 1f) < 0.001f);
            task.PaidMass = -5f;
            Check("Progress01: кламп снизу", Math.Abs(task.Progress01 - 0f) < 0.001f);

            // ---------- EcoFlow.DrainStep: налив двух валют с дефицитом ----------
            var full = EcoFlow.DrainStep(10f, 5f, 100f, 100f);
            Check("DrainStep: хватает всего", full.Mass == 10f && full.Energy == 5f);
            var massStarved = EcoFlow.DrainStep(10f, 5f, 4f, 100f);
            Check("DrainStep: дефицит массы режет обе",
                Math.Abs(massStarved.Mass - 4f) < 1e-4f && Math.Abs(massStarved.Energy - 2f) < 1e-4f);
            var eStarved = EcoFlow.DrainStep(10f, 5f, 100f, 1f);
            Check("DrainStep: дефицит энергии режет обе",
                Math.Abs(eStarved.Mass - 2f) < 1e-4f && Math.Abs(eStarved.Energy - 1f) < 1e-4f);
            var nothing = EcoFlow.DrainStep(10f, 5f, 0f, 0f);
            Check("DrainStep: пустые хранилища — нули", nothing.Mass == 0f && nothing.Energy == 0f);
            var noWant = EcoFlow.DrainStep(0f, 0f, 0f, 0f);
            Check("DrainStep: нет запроса — нули", noWant.Mass == 0f && noWant.Energy == 0f);
            var neg = EcoFlow.DrainStep(10f, 5f, -50f, 100f);
            Check("DrainStep: отрицательный доступ — кламп 0", neg.Mass == 0f && neg.Energy == 0f);

            // ---------- EcoFlow.AddCapped: сток энергии с капом ----------
            Check("AddCapped: обычное пополнение", Math.Abs(EcoFlow.AddCapped(10f, 5f, 100f) - 15f) < 1e-4f);
            Check("AddCapped: перелив до капа", Math.Abs(EcoFlow.AddCapped(90f, 50f, 100f) - 100f) < 1e-4f);
            Check("AddCapped: отрицательный доход игнорируется",
                Math.Abs(EcoFlow.AddCapped(10f, -5f, 100f) - 10f) < 1e-4f);
            Check("AddCapped: нулевой кап", EcoFlow.AddCapped(10f, 5f, 0f) == 0f);

            // ---------- EcoFlow.ServiceCost: наценка за ремонт (DESIGN §3-10) ----------
            Check("ServiceCost: целый борт — база",
                Math.Abs(EcoFlow.ServiceCost(150f, 1f, 500f, 0.25f) - 150f) < 1e-4f);
            Check("ServiceCost: половина урона",
                Math.Abs(EcoFlow.ServiceCost(150f, 0.5f, 500f, 0.25f) - 212.5f) < 1e-3f);
            Check("ServiceCost: hp01 клампится сверху",
                Math.Abs(EcoFlow.ServiceCost(150f, 2f, 500f, 0.25f) - 150f) < 1e-4f);

            // ---------- EconomyProfile v2: потоки в секунду, апгрейды наливом ----------
            var profile = new EconomyProfile();
            Check("Rate v2: T1/T2/T3 в секунду",
                profile.Rate(1) == 2.5f && profile.Rate(2) == 7f && profile.Rate(3) == 17f);
            Check("Rate v2: кламп тира", profile.Rate(0) == 2.5f && profile.Rate(99) == 17f);
            profile.incomePerSecond = new[] { 10f };
            Check("Rate v2: один тир", profile.Rate(3) == 10f);
            profile.incomePerSecond = null;
            profile.incomePerMinute = new[] { 150f };
            Check("Rate v2: старый формат минут конвертируется", Math.Abs(profile.Rate(1) - 2.5f) < 1e-4f);
            var profile2 = new EconomyProfile();
            Check("Апгрейд v2: цены налива массы",
                profile2.NextUpgradeMass(1) == 800f && profile2.NextUpgradeMass(2) == 2400f);
            Check("Апгрейд v2: максимальный тир — нет цены", profile2.NextUpgradeMass(3) == -1f);
            Check("Апгрейд v2: цены энергии и время",
                profile2.NextUpgradeEnergy(1) == 400f && profile2.NextUpgradeSeconds(1) == 15f);

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
                foreach (ScenarioLayout.Deposit o in deps)
                    if ((o.Pos - ScenarioLayout.Mirror(d.Pos, cc)).sqrMagnitude < 0.01f)
                        hasPair = true;
                allMirrored &= hasPair;
            }
            for (int i = 0; i < deps.Count && spacingOk; i++)
                for (int j = i + 1; j < deps.Count; j++)
                    if ((deps[i].Pos - deps[j].Pos).sqrMagnitude < 500f * 500f)
                        spacingOk = false;
            Check("Scenario: мексовка зеркальна парами", allMirrored, deps.Count + " точек");
            Check("Scenario: спейсинг соблюдён", spacingOk);
            Check("Scenario: счёт = пары + жирный центр", deps.Count == 20, deps.Count + " точек");
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

            // ---------- HeightmapGen: рельеф спец-карты (Neroxis-паттерн, DESIGN §2.4) ----------
            var terrainParams = new ScenarioTemplate.TerrainSettings { sizeM = 16000f, stepM = 200f, edgeM = 2000f };
            var hm = HeightmapGen.Generate(terrainParams, 1234);
            var hm2 = HeightmapGen.Generate(terrainParams, 1234);
            Check("Heightmap: размер чётный и по шагу", hm.W == 80 && hm.H == 80 && hm.W == hm.H);
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
            // быть плато.
            {
                int rim = (int)Math.Max(2f, Math.Min(hm.W * 0.05f,
                    terrainParams.edgeM * 2f / hm.Step)) + 2;
                int flat = 0, total = 0;
                for (int j = rim; j < hm.H - rim; j += 3)
                    for (int i = rim; i < hm.W - rim; i += 3)
                    {
                        if (hm.Water[i, j])
                            continue;
                        total++;
                        float dh = Math.Abs(hm.Height[i, j] - hm.Height[i + 1, j]) +
                                   Math.Abs(hm.Height[i, j] - hm.Height[i, j + 1]);
                        if (dh < 0.5f)
                            flat++;
                    }
                Check("Heightmap: террасы — плоских клеток (суша) ≥ 30%",
                    total > 0 && flat / (float)total >= 0.30f,
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

            // ---------- Util.AssignSpread: раздача пула КУЧКАМИ (Disperse стройки) ----------
            var spreadUnits = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(100f, 0f, 0f)
            };
            var spreadPoints = new List<UnityEngine.Vector3>
            {
                // два кластера: левый 3 точки, правый 2
                new UnityEngine.Vector3(10f, 0f, 0f), new UnityEngine.Vector3(20f, 0f, 0f),
                new UnityEngine.Vector3(30f, 0f, 0f),
                new UnityEngine.Vector3(90f, 0f, 0f), new UnityEngine.Vector3(110f, 0f, 0f)
            };
            int[] spread = Util.AssignSpread(spreadUnits, spreadPoints);
            Check("Кучки: кластеры делятся по сторонам (левый — первому, правый — второму)",
                spread != null && spread.SequenceEqual(new[] { 0, 0, 0, 1, 1 }),
                spread != null ? string.Join(",", spread) : "null");

            // Баланс: 5 точек / 2 юнита — раздача 3+2, а не 4+1
            int[] spreadCounts = new int[2];
            foreach (int o in spread)
                spreadCounts[o]++;
            Check("Кучки: баланс ⌈M/N⌉/⌊M/N⌋ (3+2, не 4+1)",
                spreadCounts[0] == 3 && spreadCounts[1] == 2,
                spreadCounts[0] + "+" + spreadCounts[1]);

            // Один исполнитель — всё ему
            int[] spread1 = Util.AssignSpread(
                new List<UnityEngine.Vector3> { new UnityEngine.Vector3(0f, 0f, 0f) },
                spreadPoints);
            Check("Кучки: единственный юнит забирает все точки",
                spread1 != null && spread1.All(o => o == 0),
                spread1 != null ? string.Join(",", spread1) : "null");

            // Баланс при M>N: 4 точки / 3 юнита — 2+1+1, вторая точка первому
            // достаётся БЛИЖАЙШАЯ к уже взятой (курсор маршрута движется)
            int[] spread4 = Util.AssignSpread(
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(100f, 0f, 0f),
                    new UnityEngine.Vector3(200f, 0f, 0f)
                },
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(5f, 0f, 0f), new UnityEngine.Vector3(95f, 0f, 0f),
                    new UnityEngine.Vector3(195f, 0f, 0f), new UnityEngine.Vector3(50f, 0f, 0f)
                });
            Check("Кучки: 4/3 — раздача 2+1+1, продолжение рядом с взятой",
                spread4 != null && spread4.SequenceEqual(new[] { 0, 1, 2, 0 }),
                spread4 != null ? string.Join(",", spread4) : "null");

            // Детерминизм: эквидистантная точка — меньшему индексу юнита
            int[] spreadTie = Util.AssignSpread(
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(-10f, 0f, 0f), new UnityEngine.Vector3(10f, 0f, 0f)
                },
                new List<UnityEngine.Vector3> { new UnityEngine.Vector3(0f, 0f, 0f) });
            Check("Кучки: эквидистанта — детерминизм (меньший индекс юнита)",
                spreadTie != null && spreadTie.SequenceEqual(new[] { 0 }),
                spreadTie != null ? string.Join(",", spreadTie) : "null");
            Check("Кучки: пустой пул — null",
                Util.AssignSpread(spreadUnits, new List<UnityEngine.Vector3>()) == null);
            Check("Кучки: без юнитов — null",
                Util.AssignSpread(new List<UnityEngine.Vector3>(), spreadPoints) == null);

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

            // ---------- Per-point вид (v0.114): PointKinds параллельны Points ----------
            var ppChain = new GroundOrders.Chain { Index = 0 };
            foreach (GroundOrderKind k in new[] { GroundOrderKind.Move,
                    GroundOrderKind.AttackMove, GroundOrderKind.AttackMove, GroundOrderKind.Move })
            {
                ppChain.Points.Add(new UnityEngine.Vector3(ppChain.Points.Count * 10f, 0f, 0f)
                    .ToGlobalPosition());
                ppChain.PointGroups.Add(0);
                ppChain.PointKinds.Add(k);
            }
            Check("Per-point: вид точки читается по индексу, за границей — Move",
                GroundOrders.LinearKindAt(ppChain, 1) == GroundOrderKind.AttackMove &&
                GroundOrders.LinearKindAt(ppChain, 99) == GroundOrderKind.Move);
            ppChain.Kind = GroundOrderKind.Move;
            GroundOrders.RemoveAtCore(ppChain, 0); // удалили текущую Move — курсор на AttackMove
            Check("Per-point: удаление текущей синхронизирует Kind с новой текущей точкой",
                ppChain.Index == 0 && ppChain.PointKinds.Count == 3 &&
                ppChain.Kind == GroundOrderKind.AttackMove);
            GroundOrders.RemoveAtCore(ppChain, 0); // снова AttackMove текущая
            Check("Per-point: Kind следует виду точки при продвижении очереди удалениями",
                ppChain.Kind == GroundOrderKind.AttackMove && ppChain.Points.Count == 2);
            // Патрульный хвост: начало цикла сдвигается вместе с очередью
            var plChain = new GroundOrders.Chain { Kind = GroundOrderKind.Patrol, PatrolLoopStart = 2 };
            for (int pl = 0; pl < 4; pl++)
            {
                plChain.Points.Add(new UnityEngine.Vector3(pl * 10f, 0f, 0f).ToGlobalPosition());
                plChain.PointGroups.Add(0);
                plChain.PointKinds.Add(GroundOrderKind.Patrol);
            }
            GroundOrders.RemoveAtCore(plChain, 0);
            Check("PatrolLoopStart: удаление точки до начала цикла сдвигает начало",
                plChain.PatrolLoopStart == 1 && plChain.Points.Count == 3);

            // ---------- GroundOrders.CanClosePatrol: гейты конвертации хвостом ----------
            Check("Конвертация хвостом: линейная цепочка, точка не последняя — можно",
                GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, 2, 0));
            Check("Конвертация хвостом: атака-движение тоже линейная — можно",
                GroundOrders.CanClosePatrol(GroundOrderKind.AttackMove, 4, 0, 0));
            Check("Конвертация хвостом: последняя точка — циклу нужно ≥2 точек",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, 3, 0));
            Check("Конвертация хвостом: ждущие цели серии запрещают (канон Moho)",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, 1, 2));
            Check("Конвертация хвостом: атака/патруль/эскорт не конвертятся",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Attack, 4, 1, 0) &&
                !GroundOrders.CanClosePatrol(GroundOrderKind.Patrol, 4, 1, 0) &&
                !GroundOrders.CanClosePatrol(GroundOrderKind.Escort, 4, 1, 0));
            Check("Конвертация хвостом: отрицательный индекс — no-op",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, -1, 0));

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

            // ---------- FleetSalvo.FitsDomain: домен станции для ручной волны ----------
            {
                // перевес ВВ — только по воздуху; АС и ничья (правило зеркально ClassOf) — по земле/морю
                Check("Флот-залп: ВВ-ракета бьёт воздушную цель, не наземную",
                    FleetSalvo.FitsDomain(0.93f, 0f, true) && !FleetSalvo.FitsDomain(0.93f, 0f, false));
                Check("Флот-залп: AGM (АС-перевес) бьёт наземную/надводную, не воздушную",
                    FleetSalvo.FitsDomain(0.5f, 0.814f, false) && !FleetSalvo.FitsDomain(0.5f, 0.814f, true));
                Check("Флот-залп: ничья весов — по земле (канон тай-брейка АС)",
                    FleetSalvo.FitsDomain(0.6f, 0.6f, false) && !FleetSalvo.FitsDomain(0.6f, 0.6f, true));

                // Командирский борт: орбита/ремонт/стоянка (чистые функции Util).
                {
                    var orbit = Util.CommanderOrbitPoints(new UnityEngine.Vector2(1000f, -2000f), 2500f, 1500f, 4);
                    Check("Борт: орбита — 4 точки на окружности радиуса, высота AGL",
                        orbit.Count == 4 &&
                        orbit.TrueForAll(p => UnityEngine.Mathf.Abs(UnityEngine.Mathf.Sqrt(
                            (p.x - 1000f) * (p.x - 1000f) + (p.z + 2000f) * (p.z + 2000f)) - 2500f) < 0.5f) &&
                        orbit.TrueForAll(p => UnityEngine.Mathf.Abs(p.y - 1500f) < 0.01f));
                    Check("Борт: орбита — минимум 3 точки и эквидистанция",
                        Util.CommanderOrbitPoints(UnityEngine.Vector2.zero, 100f, 10f, 0).Count == 3 &&
                        UnityEngine.Mathf.Abs(UnityEngine.Vector3.Distance(orbit[0], orbit[1]) -
                                  UnityEngine.Vector3.Distance(orbit[1], orbit[2])) < 0.5f);
                    Check("Борт: ремонт детали — растёт и зажат потолком 100",
                        UnityEngine.Mathf.Abs(Util.RepairedHitPoints(80f, 3f, 0.5f) - 81.5f) < 0.001f &&
                        Util.RepairedHitPoints(99.9f, 3f, 5f) == 100f);
                    Check("Борт: ремонт усиленной детали — потолок 800 (живучесть ×8)",
                        Util.RepairedHitPoints(799f, 3f, 5f, 800f) == 800f &&
                        UnityEngine.Mathf.Abs(Util.RepairedHitPoints(100f, 3f, 1f, 800f) - 103f) < 0.001f);
                    Check("Борт: стоянка — тихая скорость + близость к базе",
                        Util.BoardParked(new UnityEngine.Vector2(10f, 20f), UnityEngine.Vector2.zero, 400f, 0.5f) &&
                        !Util.BoardParked(new UnityEngine.Vector2(10f, 20f), UnityEngine.Vector2.zero, 400f, 4f) &&
                        !Util.BoardParked(new UnityEngine.Vector2(500f, 0f), UnityEngine.Vector2.zero, 400f, 0.5f));
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
                }
            }

            Console.WriteLine(_failed == 0 ? "ВСЕ ТЕСТЫ ПРОЙДЕНЫ" : "ПРОВАЛОВ: " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>Носитель приватных полей для проверки Util.FieldSetter.</summary>
        private sealed class FieldHolder
        {
            private string _text;
            private UnityEngine.Vector3 _vec;

            public string Text => _text;
            public UnityEngine.Vector3 Vec => _vec;
        }
    }
}
