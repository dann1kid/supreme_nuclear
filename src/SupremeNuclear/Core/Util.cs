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

        /// <summary>Итоговый наклон камеры из пользовательского угла и двух автодоворотов:
        /// к виду сверху на стратезуме (blendTop) и круче вниз у земли (blendClose),
        /// факторы 0..1. Полосы не пересекаются, поэтому порядок вложенного лерпа не важен.
        /// При осмотре на Space оба фактора — 0: угол осмотра применяется напрямую.</summary>
        public static float ComposePitch(float userPitch, float topDownPitch, float closePitch,
            float blendTop, float blendClose) =>
            Mathf.Lerp(Mathf.Lerp(userPitch, topDownPitch, blendTop), closePitch, blendClose);

        /// <summary>Дистанция подхода к цели атаки: НЕ ДАЛЬШЕ текущей (0.9×— чуть
        /// ближе; приказ «назад» уводил корабли от берега, который им и был нужен)
        /// и в пределах 0.8×дальности пригодного по цели вооружения.</summary>
        public static float ApproachDistance(float currentDist, float range) =>
            Mathf.Min(currentDist * 0.9f, range * 0.8f);

        /// <summary>Русская форма существительного по числу: RuPlural(2, "носитель",
        /// "носителя", "носителей") → "носителя". 11–14 всегда «много» (11 носителей).</summary>
        public static string RuPlural(int n, string one, string few, string many)
        {
            int lastTwo = n % 100, last = n % 10;
            if (lastTwo >= 11 && lastTwo <= 14)
                return many;
            if (last == 1)
                return one;
            return last >= 2 && last <= 4 ? few : many;
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
            if (s.Length <= maxChars)
                return s;
            return s.Substring(0, maxChars - 1) + "…";
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
    }
}
