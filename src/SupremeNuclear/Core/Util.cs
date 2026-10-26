using System;
using System.Collections.Generic;

namespace SupremeNuclear
{
    /// <summary>Чистые функции без Unity-зависимостей — тестируются тест-раннером.</summary>
    internal static class Util
    {
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
    }
}
