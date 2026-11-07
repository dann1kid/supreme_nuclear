using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    // Часть TestMain: геометрия Util, сглаживания, камера, фильтры.
    internal static partial class TestMain
    {
        // геометрия Util (серии, трассы, Безье, рулёжки, кольца, наклон) (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsUtilGeometry()
        {
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
        }
        // Эрмит-сглаживание лент (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsHermite()
        {
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
        }
        // камера и раздача целей (ZoomCurve, AssignBalanced/Spread) (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsCameraAssign()
        {
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
        }
        // кольца/сетки RangeRings, фильтры OneEuro/CatmullRom (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsRingsFilters()
        {
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
        }
    }
}
