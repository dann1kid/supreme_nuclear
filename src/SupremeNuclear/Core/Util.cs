using System;
using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Чистые функции без вызовов движка (математика над структурами Unity) —
    /// тестируются тест-раннером.</summary>
    internal static class Util
    {
        /// <summary>Цель в конусе атаки (горизонтальные направления): угол между вектором
        /// приказа и направлением на цель не больше maxDeg. Вырожденный вектор (юнит на
        /// точке) конус не ограничивает — true.</summary>
        public static bool WithinCone(Vector3 heading, Vector3 toTarget, float maxDeg)
        {
            Vector2 h = new Vector2(heading.x, heading.z);
            Vector2 t = new Vector2(toTarget.x, toTarget.z);
            if (h.sqrMagnitude < 1e-6f || t.sqrMagnitude < 1e-6f)
                return true;
            return Vector2.Angle(h, t) <= maxDeg;
        }

        /// <summary>Кривая наклона стратегической камеры по дистанции (SupCom strategic
        /// zoom): у поверхности — крутой взгляд в землю (горизонта не видно), из космоса —
        /// вид сверху, между — один плавный S-переход. Smoothstep делает кривую пологой
        /// у краёв диапазона («вблизи к поверхности чуть меняет наклон») и круче в середине.
        /// Горизонт в обычной игре не виден никогда — только осмотром на Space.</summary>
        public static float ZoomCurvePitch(float distance, float nearPitch, float topDownPitch,
            float nearDist, float topDist)
        {
            float t = Mathf.InverseLerp(nearDist, topDist, distance);
            float smooth = t * t * (3f - 2f * t);
            return Mathf.Lerp(nearPitch, topDownPitch, smooth);
        }

        /// <summary>Дистанция подхода к цели атаки: НЕ ДАЛЬШЕ текущей (0.9×— чуть
        /// ближе; приказ «назад» уводил корабли от берега, который им и был нужен)
        /// и в пределах 0.8×дальности пригодного по цели вооружения.</summary>
        public static float ApproachDistance(float currentDist, float range) =>
            Mathf.Min(currentDist * 0.9f, range * 0.8f);

        /// <summary>Квадрат XZ-дистанции от точки до отрезка (проекция клампится
        /// на концы). Общая метрика «насколько точка у полосы»: выбор аэродрома
        /// для руления (AnyNearAirbasePatch) и поиск своего аэродрома для ангара.
        /// Чистая — тестируется.</summary>
        public static float DistSqToSegmentXZ(Vector2 from, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float abSq = ab.sqrMagnitude;
            if (abSq < 1e-6f)
                return (a - from).sqrMagnitude;
            float t = Mathf.Clamp01(Vector2.Dot(from - a, ab) / abSq);
            Vector2 proj = a + ab * t;
            return (proj - from).sqrMagnitude;
        }

        /// <summary>Точка внутри прямоугольника-«полосы» вокруг отрезка a–b (XZ):
        /// вдоль оси торцов ± длина/2, поперёк ± width/2, к обоим — запас margin
        /// (кромки полотна). Пик полотна ВПП кликом (выделение аэродрома без
        /// вышки, v0.102). Чистая — тестируется.</summary>
        public static bool InRectXZ(Vector2 p, Vector2 a, Vector2 b, float widthM, float marginM)
        {
            Vector2 axis = b - a;
            float len = axis.magnitude;
            if (len < 1e-3f)
                return (p - a).magnitude <= widthM * 0.5f + marginM;
            Vector2 dir = axis / len;
            Vector2 rel = p - (0.5f * (a + b));
            float along = Vector2.Dot(rel, dir);
            float across = rel.x * -dir.y + rel.y * dir.x;
            return Mathf.Abs(along) <= len * 0.5f + marginM &&
                   Mathf.Abs(across) <= widthM * 0.5f + marginM;
        }

        /// <summary>Точка кубической кривой Безье (t∈[0,1]). Сглаживание
        /// рулёжек: касательные на концах задают контрольные точки (p1 —
        /// направление выхода из ворот ангара, p2 — вход по оси ВПП), кривая
        /// гладкая при любой расстановке — без «колен». Аффинность: любой
        /// линейный функционал (например, боковой отступ от оси полосы) на
        /// кривой ограничен его значениями на контрольных точках — зажатые
        /// контроли гарантируют, что кривая не заходит в полосу. Чистая —
        /// тестируется.</summary>
        public static Vector3 Bezier3(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        /// <summary>КОЛЛЕКТОР рулёжки (v0.100): точка стыка и коннектор ангара к
        /// магистрали ПАРАЛЛЕЛЬНО оси ВПП (чистая, тестируется). Магистраль на
        /// стороне ангара (отступ laneOffsetM от оси); дальность стыка — проекция
        /// ангара на ось, но не ближе junctionMinM к торцу (не в зоне S-кривой).
        /// Коннектор — Безье: касательная старта = направление выхода ангара,
        /// финиша = хорда к стыку (перпендикулярные ворота дают почти прямую).
        /// Ворота «мимо стыка» (спиной/поперёк) — касательная старта подмешивает
        /// хорду (порог 0.25): контроль по чистому fwd давал складку-петлю на
        /// полилинии («линия с изломами», плейтест v0.100.0). Вылет контролов
        /// ≤ 0.45 хорды: у ангара вплотную к магистрали контроль 30 м на
        /// короткой хорде вихлял кривую (v0.100.1). Контроль старта зажата по
        /// боковому отступу ≥ магистрали — по выпуклости Безье коннектор не
        /// подходит к полосе ближе отступа.
        /// farM &lt; 0 — магистрали ещё нет. newFarM — дальность магистрали ПОСЛЕ
        /// подключения: запас growM за стыком, минимум minFarM, назад не
        /// ужимается. Хорда &lt; 8 м — коннектор пуст (прямая start → стык).</summary>
        public static void TaxiCollectorPlan(Vector3 threshold, Vector3 start, Vector3 exitFwd,
            Vector3 axis, Vector3 farDir, float laneOffsetM, float junctionMinM,
            float growM, float minFarM, float farM,
            out Vector3 side, out Vector3 junction, out List<Vector3> connector, out float newFarM)
        {
            Vector3 perp = new Vector3(-axis.z, 0f, axis.x);
            Vector3 from = start - threshold;
            side = Vector3.Dot(perp, from) < 0f ? -perp : perp;
            float dfJ = Mathf.Max(Vector3.Dot(from, farDir), junctionMinM);
            junction = threshold + farDir * dfJ + side * laneOffsetM;
            newFarM = Mathf.Max(Mathf.Max(farM, 0f), Mathf.Max(dfJ + growM, minFarM));
            connector = new List<Vector3>();
            float chord = Vector3.Distance(start, junction);
            if (chord < 8f)
                return;
            float d = Mathf.Min(chord * 0.45f, Mathf.Clamp(chord * 0.35f, 30f, 200f));
            Vector3 chordDir = (junction - start).normalized;
            Vector3 t0 = chordDir; // вырожденный fwd — старт по хорде
            if (exitFwd.sqrMagnitude > 1e-6f)
            {
                float fdot = Vector3.Dot(exitFwd, chordDir);
                t0 = fdot >= 0.25f
                    ? exitFwd
                    : (exitFwd + chordDir * (0.25f - fdot)).normalized;
            }
            Vector3 c1 = start + t0 * d;
            float lateral = Vector3.Dot(c1 - threshold, side);
            if (lateral < laneOffsetM)
                c1 += side * (laneOffsetM - lateral);
            Vector3 c2 = junction - chordDir * d;
            int n = Mathf.Max(3, Mathf.CeilToInt(chord * 1.3f / 15f));
            for (int i = 1; i < n; i++)
                connector.Add(Bezier3(start, c1, c2, junction, i / (float)n));
        }

        /// <summary>S-кривая слияния магистрали с ВПП («смена полосы», v0.98.5):
        /// Безье p1 → торец, касательная farDir на ОБОИХ концах, контроли на
        /// кромках коридора «магистраль ↔ ось» — по аффинности кривая между
        /// ними: ось не пересекает, за торец не заходит. Возвращает ТОЛЬКО
        /// внутренние точки по возрастанию t (p1 и торец на совести звущего).
        /// p1 = торец + side·mergeM − farDir·mergeM.</summary>
        public static List<Vector3> TaxiMergeSamples(Vector3 threshold, Vector3 farDir,
            Vector3 side, float mergeM)
        {
            Vector3 laneAt = threshold + side * mergeM;
            Vector3 p1 = laneAt - farDir * mergeM;
            var pts = new List<Vector3>();
            int n = Mathf.Max(3, Mathf.CeilToInt(Vector3.Distance(p1, threshold) * 1.3f / 15f));
            for (int i = 1; i < n; i++)
                pts.Add(Bezier3(p1, laneAt, threshold - farDir * mergeM, threshold, i / (float)n));
            return pts;
        }

        /// <summary>Точки магистрали по дальности от торца (вдоль farDir, отступ
        /// side·laneOffsetM): шаг ≤ 15 м, ОБА конца включены — расширение
        /// начинается точно со старого конца (склейка узлов в радиусе 10 м).</summary>
        public static List<Vector3> TaxiLaneSamples(Vector3 threshold, Vector3 farDir,
            Vector3 side, float laneOffsetM, float fromM, float toM)
        {
            var pts = new List<Vector3>();
            int n = Mathf.Max(1, Mathf.CeilToInt((toM - fromM) / 15f));
            for (int i = 0; i <= n; i++)
                pts.Add(threshold + farDir * Mathf.Lerp(fromM, toM, i / (float)n) + side * laneOffsetM);
            return pts;
        }

        /// <summary>Радиус кольца остановки строителей вокруг площадки: габарит
        /// здания + запас (вне границы призрака), но НЕ дальше радиуса оплаты
        /// площадки (дальше деньги не капают) и не ближе минимума. Чистая —
        /// тестируется.</summary>
        public static float BuildApproachRingM(float footprintHalfM, float marginM, float minM, float maxM) =>
            Mathf.Clamp(footprintHalfM + marginM, minM, maxM);

        /// <summary>На сколько равных шагов разбить отрезок длиной distanceM при шаге
        /// ~stepM (проверка прямой трассы наземки на воду: последний шаг — сама
        /// цель). Минимум 1, потолок maxSteps — длинные трассы берутся разреженнее.
        /// Вызывается только из события приказа, не из тика. Чистая — тестируется.</summary>
        public static int SegmentSteps(float distanceM, float stepM, int maxSteps) =>
            Mathf.Clamp(Mathf.CeilToInt(distanceM / Mathf.Max(stepM, 1f)), 1, maxSteps);

        /// <summary>Кандидаты кольцевого поиска вокруг точки (спуск корабля с верфи,
        /// подход инженера к платформе): концентрические кольца от innerRadius шагом
        /// stepM по directions направлений, БЛИЖНИЕ КОЛЬЦА РАНЬШЕ (первый годный
        /// кандидат — ближайший). Чистая — тестируется.</summary>
        public static List<Vector2> RingPoints(float innerRadius, float stepM, int directions,
            float maxRadius)
        {
            var points = new List<Vector2>();
            if (innerRadius < 0.1f || stepM < 1f || directions < 4 || maxRadius <= innerRadius)
                return points;
            int rings = Mathf.FloorToInt((maxRadius - innerRadius) / stepM);
            for (int r = 0; r <= rings; r++)
            {
                float radius = innerRadius + r * stepM;
                for (int d = 0; d < directions; d++)
                {
                    float angle = d * Mathf.PI * 2f / directions;
                    points.Add(new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius));
                }
            }
            return points;
        }

        /// <summary>Индекс формы множественного числа (русское правило):
        /// 0 — один, 1 — несколько, 2 — много. 11–14 всегда «много» (11 носителей).</summary>
        internal static int RuPluralIndex(int n)
        {
            int lastTwo = n % 100, last = n % 10;
            if (lastTwo >= 11 && lastTwo <= 14)
                return 2;
            if (last == 1)
                return 0;
            return last >= 2 && last <= 4 ? 1 : 2;
        }

        /// <summary>Русская форма существительного по числу: RuPlural(2, "носитель",
        /// "носителя", "носителей") → "носителя". 11–14 всегда «много» (11 носителей).</summary>
        public static string RuPlural(int n, string one, string few, string many)
        {
            int idx = RuPluralIndex(n);
            return idx == 0 ? one : idx == 1 ? few : many;
        }

        /// <summary>Однострочная подгонка текста под фиксированную ширину: длиннее
        /// maxChars — обрезается с «…» в конце (панель свойств выделения: строки
        /// не переносятся, иначе текст вылезает за подложку). Чистая функция — тестируется.</summary>
        public static string FitLine(string s, int maxChars)
        {
            if (maxChars < 1)
                return "";
            if (string.IsNullOrEmpty(s))
                return "";
            if (s.Length > maxChars)
                return s.Substring(0, maxChars - 1) + "…";
            return s;
        }

        /// <summary>Адаптивный фильтр курсора «one euro» (Vector2): в покое сильно
        /// сглаживает — гасит сенсорный дрожь мыши (±1-2 пикселя при удержании
        /// транслировались в камеру 1:1 в метры мира), при движении частота среза
        /// растёт со скоростью и лаг остаётся малым. Состояние сбрасывается Reset().</summary>
        public sealed class OneEuro
        {
            private readonly float _minCutoff;
            private readonly float _beta;
            private Vector2 _value;
            private Vector2 _speed;
            private bool _has;

            public OneEuro(float minCutoff = 0.5f, float beta = 0.01f)
            {
                _minCutoff = minCutoff;
                _beta = beta;
            }

            /// <summary>Текущее сглаженное значение (до вызова Filter — ноль).</summary>
            public Vector2 Value => _value;

            /// <summary>Прогнать сырую точку; первый вызов проходит без сглаживания.</summary>
            public Vector2 Filter(Vector2 raw, float dt)
            {
                if (dt <= 0f)
                    return _has ? _value : raw;
                if (!_has)
                {
                    _value = raw;
                    _speed = Vector2.zero;
                    _has = true;
                    return raw;
                }

                float Alpha(float cutoff) => 1f - Mathf.Exp(-2f * Mathf.PI * cutoff * dt);

                // Оценка скорости (сглаженная), затем адаптивная частота среза по ней.
                Vector2 rawSpeed = (raw - _value) / dt;
                _speed += (rawSpeed - _speed) * Alpha(1f);
                float cutoff = _minCutoff + _beta * _speed.magnitude;
                _value += (raw - _value) * Alpha(cutoff);
                return _value;
            }

            public void Reset()
            {
                _has = false;
                _speed = Vector2.zero;
            }
        }

        /// <summary>Серии подряд идущих одинаковых элементов (порядок сохраняется,
        /// РАЗНЫЕ серии не сливаются: A A B A → (A,2)(B,1)(A,1)). Так стакаются плитки очередей.</summary>
        public static List<(T key, int count)> Runs<T>(IList<T> items, Func<T, T, bool> same)
        {
            var runs = new List<(T key, int count)>();
            foreach (T item in items)
            {
                if (runs.Count > 0 && same(runs[runs.Count - 1].key, item))
                {
                    var last = runs[runs.Count - 1];
                    runs[runs.Count - 1] = (last.key, last.count + 1);
                }
                else
                    runs.Add((item, 1));
            }
            return runs;
        }

        /// <summary>Потолок повторов одного элемента в списке залпа: нативное
        /// распределение (CombatAI.LookForMissileTargets) повторяет цель по числу
        /// нужных атак; сверх потолка ракеты не поднимаются. Порядок сохраняется,
        /// лишние повторы выкидываются на месте. Возвращает число удалённых.
        /// Чистая функция — тестируется.</summary>
        public static int TrimSalvo<T>(List<T> list, int maxPerEntry)
        {
            if (maxPerEntry <= 0)
            {
                int removedAll = list.Count;
                list.Clear();
                return removedAll;
            }
            var counts = new Dictionary<T, int>();
            int write = 0;
            for (int read = 0; read < list.Count; read++)
            {
                T item = list[read];
                counts.TryGetValue(item, out int count);
                count++;
                if (count <= maxPerEntry)
                {
                    list[write++] = item;
                    counts[item] = count;
                }
                else
                    counts[item] = count;
            }
            int removed = list.Count - write;
            if (removed > 0)
                list.RemoveRange(write, removed);
            return removed;
        }

        /// <summary>Распределение атакующих по целям «веером»: каждый юнит — к
        /// БЛИЖАЙШЕЙ цели со свободной ёмкостью. Атакующих не меньше целей: ёмкость
        /// цели = N/M (+1 первым по порядку клика — ровная раздача без давки на
        /// ближней); атакующих меньше целей: ёмкость 1 — каждый берёт ближайшую
        /// СВОБОДНУЮ цель, дальние клики остаются без атакующих. Жадный глобальный
        /// выбор пары юнит-цель с минимальной дистанцией (XZ, sqrMagnitude); равные
        /// дистанции — меньший индекс цели, затем юнита (детерминированность).
        /// Возвращает индекс цели на каждого юнита; пустые цели → null.
        /// Чистая функция — тестируется.</summary>
        public static int[] AssignBalanced(IReadOnlyList<Vector3> units, IReadOnlyList<Vector3> targets)
        {
            if (targets.Count == 0)
                return null;
            int n = units.Count;
            var result = new int[n];
            if (n == 0)
                return result;
            int m = targets.Count;
            var capacity = new int[m];
            if (n < m)
            {
                for (int t = 0; t < m; t++)
                    capacity[t] = 1;
            }
            else
            {
                int extra = n % m;
                for (int t = 0; t < m; t++)
                    capacity[t] = n / m + (t < extra ? 1 : 0);
            }
            var taken = new bool[n];
            for (int assigned = 0; assigned < n; assigned++)
            {
                int bestU = -1, bestT = -1;
                float bestDist = float.MaxValue;
                for (int u = 0; u < n; u++)
                {
                    if (taken[u])
                        continue;
                    Vector3 up = units[u];
                    for (int t = 0; t < m; t++)
                    {
                        if (capacity[t] <= 0)
                            continue;
                        float dx = up.x - targets[t].x;
                        float dz = up.z - targets[t].z;
                        float d = dx * dx + dz * dz;
                        if (d < bestDist) // строго меньше: равные остаются у меньших индексов
                        {
                            bestDist = d;
                            bestU = u;
                            bestT = t;
                        }
                    }
                }
                taken[bestU] = true;
                capacity[bestT]--;
                result[bestU] = bestT;
            }
            return result;
        }

        /// <summary>Скругление углов цепочки вейпоинтов (лента без «лесенок»): каждая
        /// промежуточная точка B заменяется дугой — квадратичной кривой Безье P→B→Q,
        /// где P/Q — точки касания на смежных ногах на расстоянии t от угла,
        /// t = min(radiusM, 0.45·min(|AB|, |BC|)) — дуга не съедает больше 45% короткой
        /// ноги, соседние дуги не пересекаются. Стыки дуг с ногами G1-гладкие (у кривой
        /// в P/Q касательная совпадает с направлением ноги). Коллинеарные углы проходят
        /// как есть; вырожденные ноги (совпадающие точки) пропускаются; точный разворот
        /// (P≈Q) остаётся острым углом. Скругление считается в XZ, высота дуги — лерп
        /// по кривой. smooth заполняется сглаженной цепочкой, legStart[i] — индекс в
        /// smooth, с которого начинается нога i (последняя идёт до конца);
        /// возвращается число точек smooth.</summary>
        public static int RoundChainCorners(IReadOnlyList<Vector3> pts, float radiusM,
            List<Vector3> smooth, List<int> legStart)
        {
            smooth.Clear();
            legStart.Clear();
            int count = pts.Count;
            if (count == 0)
                return 0;
            smooth.Add(pts[0]);
            for (int k = 0; k + 1 < count; k++)
            {
                Vector3 a = pts[k];
                Vector3 b = pts[k + 1];
                legStart.Add(smooth.Count - 1);
                Vector3 ab = b - a;
                float len1 = new Vector2(ab.x, ab.z).magnitude;
                if (len1 < 1e-3f)
                    continue; // вырожденная нога: точка b уже лежит в smooth
                if (k + 2 < count)
                {
                    Vector3 bc = pts[k + 2] - b;
                    float len2 = new Vector2(bc.x, bc.z).magnitude;
                    if (len2 >= 1e-3f)
                    {
                        var d1 = new Vector2(ab.x / len1, ab.z / len1);
                        var d2 = new Vector2(bc.x / len2, bc.z / len2);
                        float cross = d1.x * d2.y - d1.y * d2.x;
                        float dot = d1.x * d2.x + d1.y * d2.y;
                        bool turn = Math.Abs(cross) > 1e-4f || dot < 0f;
                        float t = Mathf.Min(radiusM, 0.45f * Mathf.Min(len1, len2));
                        if (turn && t > 0.5f)
                        {
                            Vector3 p = Vector3.LerpUnclamped(b, a, t / len1);
                            Vector3 q = Vector3.LerpUnclamped(b, pts[k + 2], t / len2);
                            if ((q - p).sqrMagnitude > 1e-4f)
                            {
                                smooth.Add(p);
                                int arcSteps = Mathf.Clamp(Mathf.CeilToInt(t / 40f), 2, 8);
                                for (int s = 1; s <= arcSteps; s++)
                                {
                                    float u = s / (float)arcSteps;
                                    float w0 = (1f - u) * (1f - u);
                                    float w1 = 2f * u * (1f - u);
                                    float w2 = u * u;
                                    smooth.Add(p * w0 + b * w1 + q * w2);
                                }
                                continue; // угол заменён дугой, сама B в цепь не входит
                            }
                        }
                    }
                }
                smooth.Add(b);
            }
            return smooth.Count;
        }

        /// <summary>Наклон футпринта под рельеф: средние высоты передней/задней и
        /// правой/левой кромок → продольный (pitch) и поперечный (roll) углы, град.
        /// Знаки согласованы с Quaternion.Euler(pitch, yaw, roll): pitch&gt;0 — нос
        /// (+Z) вниз, roll&gt;0 — правый борт (+X) вверх. Оба угла клампятся
        /// maxDeg (0 — наклона нет, всегда плоско); длины клампятся снизу 1 м.</summary>
        public static void FootprintTilt(float hFront, float hBack, float hRight, float hLeft,
            float lengthM, float widthM, float maxDeg, out float pitchDeg, out float rollDeg)
        {
            pitchDeg = 0f;
            rollDeg = 0f;
            if (maxDeg <= 0f)
                return;
            pitchDeg = Mathf.Clamp(
                Mathf.Atan2(hBack - hFront, Mathf.Max(lengthM, 1f)) * Mathf.Rad2Deg,
                -maxDeg, maxDeg);
            rollDeg = Mathf.Clamp(
                Mathf.Atan2(hRight - hLeft, Mathf.Max(widthM, 1f)) * Mathf.Rad2Deg,
                -maxDeg, maxDeg);
        }

        /// <summary>Равномерный Catmull-Rom: интерполяция между p1 и p2 по t∈[0..1],
        /// p0/p3 — соседние точки следа (задают касательные; недостающие — clamp).
        /// Чистая — тестируется.</summary>
        public static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (2f * p1 +
                           (p2 - p0) * t +
                           (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                           (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }

        /// <summary>Точка на сплайне-следе в arcBehind метрах ДУГИ позади живой позиции
        /// цели (tail — виртуальный последний узел; pts — breadcrumbs в XZ, высота
        /// возврата 0 — вызывающий кладёт GroundY). Дуга сегмента ≈ хорда (шаг
        /// сэмплов мал против кривизны). false — след короче запрошенной дуги.
        /// headingRad — курс следа в найденной точке (atan2(x,z), конечная разность
        /// сплайна). Чистая — тестируется.</summary>
        public static bool TrailPointAt(IReadOnlyList<Vector3> pts, Vector3 tail, float arcBehind,
            out Vector3 at, out float headingRad)
        {
            at = Vector3.zero;
            headingRad = 0f;
            int n = pts.Count;
            if (n == 0 || arcBehind < 0f)
                return false;
            float target = 0f;
            for (int i = 0; i < n - 1; i++)
                target += Horiz(pts[i], pts[i + 1]);
            target += Horiz(pts[n - 1], tail);
            target -= arcBehind; // ищем позицию на дуге (от головы следа)
            if (target < -1e-3f)
                return false; // след короче — вызывающий деградирует в прямой билин
            float walked = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = pts[i];
                Vector3 b = i + 1 < n ? pts[i + 1] : tail;
                float seg = Horiz(a, b);
                if (seg < 1e-3f || walked + seg < target)
                {
                    walked += seg;
                    continue;
                }
                float t = Mathf.Clamp01((target - walked) / seg);
                Vector3 p0 = i > 0 ? pts[i - 1] : a;
                // Узлы цепочки: pts[0..n-1] + tail (виртуальный хвост): сосед СПРАВА
                // от b — pts[i+2], а для предпоследнего сегмента — сам tail (иначе
                // касательная сплайна обрывается клампом и якорь съезжает с дуги).
                Vector3 p3 = i + 2 < n ? pts[i + 2] : i + 2 == n ? tail : b;
                at = CatmullRom(p0, a, b, p3, t);
                at.y = 0f;
                Vector3 ahead = CatmullRom(p0, a, b, p3, Mathf.Min(t + 0.25f, 1f));
                Vector2 d = new Vector2(ahead.x - at.x, ahead.z - at.z);
                headingRad = d.sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(d.x, d.y);
                return true;
            }
            return false; // вырожденный след из нулевых сегментов
        }

        private static float Horiz(Vector3 a, Vector3 b) =>
            new Vector2(a.x - b.x, a.z - b.z).magnitude;

        /// <summary>Маршрут уже встречался среди ранее нарисованных? seen — сплющенные
        /// маршруты кадра, lens — их длины (дедуп общего ралли пачки фабрик: одинаковые
        /// маршруты рисуются одной лентой). Чистая функция — тестируется.</summary>
        public static bool RouteSeenBefore(IReadOnlyList<Vector3> seen, IReadOnlyList<int> lens,
            IReadOnlyList<Vector3> route)
        {
            int n = route.Count;
            for (int s = 0, idx = 0; s < lens.Count; s++)
            {
                int len = lens[s];
                if (len == n)
                {
                    bool same = true;
                    for (int i = 0; i < n; i++)
                        if (seen[idx + i] != route[i])
                        {
                            same = false;
                            break;
                        }
                    if (same)
                        return true;
                }
                idx += len;
            }
            return false;
        }
    }
}
