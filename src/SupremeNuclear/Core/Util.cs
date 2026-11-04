using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
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

        /// <summary>Скомпилированный сеттер поля экземпляра (Expression): замена
        /// FieldInfo.SetValue в горячих путях — патчи руления пишут destination
        /// (структура GlobalPosition) на каждом физтике каждого борта, а SetValue
        /// боксит структуру — стабильный GC-поток. Промах AccessTools (null) → null.
        /// Чистая — тестируется.</summary>
        public static Action<object, T> FieldSetter<T>(FieldInfo field)
        {
            if (field == null)
                return null;
            ParameterExpression inst = Expression.Parameter(typeof(object), "inst");
            ParameterExpression value = Expression.Parameter(typeof(T), "value");
            BinaryExpression body = Expression.Assign(
                Expression.Field(Expression.Convert(inst, field.DeclaringType), field), value);
            return Expression.Lambda<Action<object, T>>(body, inst, value).Compile();
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

        /// <summary>Подвод рулёжки ангара к торцу ВПП — «якоря и Безье» (v0.102.2,
        /// канон игрока; коллектор v0.100 отвергнут плейтестом — «ломаная»).
        /// Якорь А: ПРЯМОЙ выезд из ворот (стаб 10–80 м — линия резко не
        /// сворачивает на выходе; не длиннее сближения с полосой, ворота «мимо»
        /// торца — без стаба). Якорь Б: въезд на полосу ПО ОСИ в торец «в
        /// сторону взлёта» (касательная финиша = farDir); у ангара ВДОЛЬ
        /// полосы (за линией торца дальше 40 м) якорь — прямая по оси ЗА
        /// порогом (запас 40–90 м): дуга разворота успевает лечь с боковым
        /// запасом, без резкого V у самой оси. Якоря скрепляет ОДНО плавное
        /// Безье: касательная старта = направление выхода (при воротах «мимо»
        /// цели — подмес хорды, порог 0.25, иначе складка-петля), контроль
        /// старта зажат не ниже кромки полосы (34.5 м — полоса+полоса ленты)
        /// — кривая не пробивает ВПП насквозь. Чистая, тестируется.
        /// Возвращает точки МЕЖДУ start и торцом (не включая их).</summary>
        public static List<Vector3> TaxiApproachPoints(Vector3 threshold, Vector3 start,
            Vector3 exitFwd, Vector3 farDir)
        {
            var pts = new List<Vector3>();
            float d = Vector3.Distance(start, threshold);
            if (d < 8f)
                return pts;
            Vector3 dirT = (threshold - start).normalized;
            Vector3 perp = new Vector3(-farDir.z, 0f, farDir.x);
            float lat = Vector3.Dot(start - threshold, perp);
            float aLat = Mathf.Abs(lat);
            Vector3 side = lat < 0f ? -perp : perp;

            float stub = Mathf.Min(Mathf.Clamp(d * 0.15f, 10f, 80f), Mathf.Max(0f, aLat - 35f));
            if (Vector3.Dot(exitFwd, dirT) < 0.1f)
                stub = 0f; // ворота «мимо» торца — без стаба, сразу кривая
            Vector3 p0 = start + exitFwd * stub;
            if (stub > 0.01f)
                pts.Add(p0);

            float e0 = Vector3.Dot(start - threshold, farDir) > 40f
                ? Mathf.Clamp(d * 0.15f, 40f, 90f)
                : 0f;
            Vector3 p3 = threshold - farDir * e0;
            float dp = Vector3.Distance(p0, p3);
            if (dp < 8f)
            {
                if (e0 > 0.01f && (p3 - start).sqrMagnitude > 0.25f)
                    pts.Add(p3);
                return pts;
            }
            Vector3 dirP3 = (p3 - p0).normalized;
            float fdot = Vector3.Dot(exitFwd, dirP3);
            Vector3 t1 = fdot >= 0.25f
                ? exitFwd
                : (exitFwd + dirP3 * (0.25f - fdot)).normalized;
            Vector3 c1 = p0 + t1 * Mathf.Clamp(dp * 0.35f, 30f, 250f);
            float latFloor = Mathf.Min(aLat, 34.5f);
            if (Vector3.Dot(c1 - threshold, side) < latFloor)
                c1 = threshold + side * latFloor + Vector3.Project(c1 - threshold, farDir);
            Vector3 c2 = p3 - farDir * Mathf.Clamp(dp * 0.22f, 30f, 70f);
            int n = Mathf.Max(3, Mathf.CeilToInt(dp * 1.3f / 15f));
            for (int i = 1; i < n; i++)
                pts.Add(Bezier3(p0, c1, c2, p3, i / (float)n));
            if (e0 > 0.01f)
                pts.Add(p3); // прямой выкат по оси до торца добавит звущий
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

        /// <summary>Распределение пула точек по юнитам КУЧКАМИ (Disperse стройки,
        /// v0.108): точек больше, чем исполнителей — каждому СВОЯ подвыборка вдоль
        /// его пути. Раундами: наименее загруженный юнит (при равенстве — меньший
        /// индекс) берёт БЛИЖАЙШУЮ к концу своего маршрута свободную точку (XZ,
        /// sqrMagnitude; равные дистанции — меньший индекс точки); конец маршрута
        /// сдвигается во взятую точку — кучка собирается вдоль пути, а не вокруг
        /// стартовой позиции. Баланс ⌈M/N⌉/⌊M/N⌋. Возвращает индекс юнита на
        /// каждую точку пула; пустой пул или пустые юниты → null. Чистая
        /// функция — тестируется.</summary>
        public static int[] AssignSpread(IReadOnlyList<Vector3> units, IReadOnlyList<Vector3> points)
        {
            int m = points.Count;
            int n = units.Count;
            if (m == 0 || n == 0)
                return null;
            var result = new int[m];
            var cursor = new Vector3[n];
            for (int i = 0; i < n; i++)
                cursor[i] = units[i];
            var counts = new int[n];
            var taken = new bool[m];
            for (int k = 0; k < m; k++)
            {
                int who = 0;
                for (int i = 1; i < n; i++)
                    if (counts[i] < counts[who])
                        who = i;
                int best = -1;
                float bestSq = float.MaxValue;
                for (int p = 0; p < m; p++)
                {
                    if (taken[p])
                        continue;
                    float dx = cursor[who].x - points[p].x;
                    float dz = cursor[who].z - points[p].z;
                    float d = dx * dx + dz * dz;
                    if (d < bestSq) // строго меньше: равные остаются у меньших индексов
                    {
                        bestSq = d;
                        best = p;
                    }
                }
                taken[best] = true;
                counts[who]++;
                cursor[who] = points[best];
                result[best] = who;
            }
            return result;
        }

        /// <summary>Сглаживание цепочки вейпоинтов кубическими Эрмит-кривыми — порт 1:1
        /// канона Moho UICommandGraph (OpenSupCom command_graph_renderer, MIT; адаптация
        /// 2026-10-30): в каждом узле тангенс — нормализованная сумма ЕДИНИЧНЫХ
        /// направлений смежных ног (митр; концы — направление своей ноги), нога
        /// изгибается Эрмитом с длиной тангенсов m = min(0.25·len, 50·width), где
        /// width — МИРОВАЯ ширина РИСУЕМОЙ ленты (baseWidthM × множитель ноги; у FA
        /// ленты тонкие 1–10 м → кламп 50–500 м, наши ленты 40 м → кламп 2000 м —
        /// формула та же, «кривизна пропорциональна видимой ширине линии»; 50 =
        /// ui_CurveSmoothness FA). Сегментов на ногу — max(ceil(len/maxSegLenM),
        /// 12 на поворотной), кламп 1..20 (20 = ui_CurveSegments FA; прямые ноги —
        /// по шагу длины, поворотные — не меньше двенадцати: широкие дуги требуют
        /// детализации); вейпоинты лежат НА кривой, стыки ног не дублируются.
        /// legWidths[i] — множитель толщины ноги (null = 1). smooth заполняется
        /// точками кривой, legStart[i] — индекс начала ноги i (вырожденная нога —
        /// без точек); возвращается счёт. Буферы переиспользуются; чистая —
        /// тестируется.</summary>
        public static int HermiteChain(IReadOnlyList<Vector3> pts, IReadOnlyList<float> legWidths,
            float baseWidthM, float maxSegLenM, List<Vector3> smooth, List<int> legStart)
        {
            smooth.Clear();
            legStart.Clear();
            int count = pts.Count;
            if (count < 2)
                return 0;
            const float curveSmoothness = 50f; // ui_CurveSmoothness FA
            const int maxSegments = 20;        // ui_CurveSegments FA
            if (maxSegLenM < 1f)
                maxSegLenM = 200f;
            // Тангенсы узлов (митр): сумма единичных направлений смежных ног.
            HermiteTangents.Clear();
            for (int p = 0; p < count; p++)
            {
                Vector3 t = Vector3.zero;
                if (p > 0)
                {
                    Vector3 inDir = pts[p] - pts[p - 1];
                    float dl = inDir.magnitude;
                    if (dl > 1e-6f)
                        t += inDir / dl;
                }
                if (p < count - 1)
                {
                    Vector3 outDir = pts[p + 1] - pts[p];
                    float dl = outDir.magnitude;
                    if (dl > 1e-6f)
                        t += outDir / dl;
                }
                float tl = t.magnitude;
                HermiteTangents.Add(tl > 1e-6f ? t / tl : Vector3.zero);
            }
            for (int i = 0; i + 1 < count; i++)
            {
                legStart.Add(smooth.Count);
                Vector3 a = pts[i];
                Vector3 b = pts[i + 1];
                Vector3 d = b - a;
                float len = d.magnitude;
                if (len < 1e-4f)
                    continue; // вырожденная нога — без точек
                Vector3 dir = d / len;
                // Ширина ноги = мировая ширина ленты × множитель (канон: кламп тангенса
                // привязан к ВИДИМОЙ ширине линии, width = kLineWidth × √units у FA).
                float legMul = legWidths != null && i < legWidths.Count ? Mathf.Max(legWidths[i], 1f) : 1f;
                float width = Mathf.Max(baseWidthM, 1f) * legMul;
                float m = Mathf.Min(0.25f * len, curveSmoothness * width);
                Vector3 sa = HermiteTangents[i].sqrMagnitude < 1e-8f ? dir : HermiteTangents[i];
                Vector3 sb = HermiteTangents[i + 1].sqrMagnitude < 1e-8f ? dir : HermiteTangents[i + 1];
                sa *= m;
                sb *= m;
                // Поворотная нога (тангенс конца отклонился от её направления) получает
                // минимум сегментов независимо от длины — широкая дуга требует
                // детализации, иначе угол выглядит острым (у FA сегментов фиксированно
                // 20 на любую ногу — мы экономим на прямых).
                bool turn = Vector3.Dot(sa, dir) < 0.999f * sa.magnitude ||
                            Vector3.Dot(sb, dir) < 0.999f * sb.magnitude;
                int segments = Mathf.Clamp(
                    Mathf.Max(Mathf.CeilToInt(len / maxSegLenM), turn ? 12 : 1), 1, maxSegments);
                for (int k = 0; k <= segments; k++)
                {
                    if (i > 0 && k == 0)
                        continue; // стык ног: точка уже отдана предыдущей ногой (t=1)
                    float t = k / (float)segments;
                    float t2 = t * t;
                    float t3 = t2 * t;
                    float h00 = 2f * t3 - 3f * t2 + 1f;
                    float h10 = t3 - 2f * t2 + t;
                    float h01 = -2f * t3 + 3f * t2;
                    float h11 = t3 - t2;
                    smooth.Add(a * h00 + sa * h10 + b * h01 + sb * h11);
                }
            }
            return smooth.Count;
        }

        private static readonly List<Vector3> HermiteTangents = new List<Vector3>();

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

        /// <summary>Точки орбиты командирского борта: правильный n-угольник вокруг
        /// базы (xz-плоскость, y = высота AGL сегмента). Чистая — тестируется.</summary>
        public static List<Vector3> CommanderOrbitPoints(Vector2 center, float radiusM, float altM, int count)
        {
            int n = Mathf.Max(3, count);
            var pts = new List<Vector3>(n);
            for (int i = 0; i < n; i++)
            {
                double a = 2.0 * System.Math.PI * i / n;
                pts.Add(new Vector3(
                    center.x + radiusM * (float)System.Math.Cos(a),
                    altM,
                    center.y + radiusM * (float)System.Math.Sin(a)));
            }
            return pts;
        }

        // ---------- туман войны (v0.146): классификация видимости + сетка затемнения ----------

        /// <summary>Видимость чужого юнита локальному командиру (туман войны).</summary>
        public enum FogVisibility { Visible, Hidden }

        /// <summary>Что рисовать по чужому контакту (неопознанные радар-отметки,
        /// v0.147): None — ничего; Anonymous — безымянная точка (радар держит
        /// цель, но оптика её не опознала: без типа/владельца/HP); Identified —
        /// полная иконка/модель.</summary>
        public enum ContactBlip { None, Anonymous, Identified }

        /// <summary>Классификация видимости юнита в тумане войны (канон SupCom —
        /// бинарно, без «серых протухших» контактов): ракеты обеих сторон, свои и
        /// нейтралы видны всегда; чужие ЗДАНИЯ — с момента ОПТИЧЕСКОЙ разведки и
        /// до гибели (статика не двигается: раз опознана — помним, гибель вне
        /// обзора не раскрывает себя, иконку держит ghost-реестр); чужая
        /// ПОДВИЖНАЯ техника — только пока контакт «живой» (сенсоры обновляют
        /// его чаще окна liveWindowSec) И сигнатура опознана оптикой (радар-ONLY
        /// контакт — безымянная отметка, см. ClassifyBlip). Чистая — тестируется.</summary>
        public static FogVisibility ClassifyFog(bool isMissile, bool ownOrNeutral, bool isBuilding,
            bool hasContact, float contactAgeSec, float liveWindowSec, bool identified)
        {
            if (isMissile || ownOrNeutral)
                return FogVisibility.Visible;
            if (!hasContact || !identified)
                return FogVisibility.Hidden;
            if (isBuilding)
                return FogVisibility.Visible;
            return contactAgeSec <= liveWindowSec ? FogVisibility.Visible : FogVisibility.Hidden;
        }

        /// <summary>Классификация КОНТАКТА (что рисуем): ракеты/свои — полные;
        /// чужие здания — контакт есть: опознана → полная, радар-ONLY → безымянная
        /// точка; чужая подвижная техника — живой контакт: опознана → полная
        /// (опознание живёт, пока контакт не потерян — канон SupCom «разведанная
        /// сигнатура не теряется в зоне радаров»), иначе безымянная точка,
        /// протухший контакт — ничего. Чистая — тестируется.</summary>
        public static ContactBlip ClassifyBlip(bool isMissile, bool ownOrNeutral, bool isBuilding,
            bool hasContact, float contactAgeSec, float liveWindowSec, bool identified)
        {
            if (isMissile || ownOrNeutral)
                return ContactBlip.Identified;
            if (!hasContact)
                return ContactBlip.None;
            if (isBuilding)
                return identified ? ContactBlip.Identified : ContactBlip.Anonymous;
            if (contactAgeSec > liveWindowSec)
                return ContactBlip.None;
            return identified ? ContactBlip.Identified : ContactBlip.Anonymous;
        }

        /// <summary>Штамп круга обзора в сетку тумана (cells — nx×nz, row-major по
        /// z: индекс = z*nx + x): круг радиуса r ячеек вокруг (cx, cz), выход за
        /// границы карты молча клампится (сенсор у края). Радиус 0 — одна ячейка.
        /// Чистая — тестируется.</summary>
        public static void FogStampCircle(bool[] cells, int nx, int nz, int cx, int cz, int r)
        {
            if (cells == null || nx <= 0 || nz <= 0 || cells.Length < nx * nz || r < 0)
                return;
            int r2 = r * r;
            for (int dz = -r; dz <= r; dz++)
            {
                int z = cz + dz;
                if (z < 0 || z >= nz)
                    continue;
                int row = z * nx;
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r2)
                        continue;
                    int x = cx + dx;
                    if (x < 0 || x >= nx)
                        continue;
                    cells[row + x] = true;
                }
            }
        }
    }
}
