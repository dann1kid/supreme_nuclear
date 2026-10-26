using System;
using System.Collections.Generic;

namespace SupremeNuclear
{
    /// <summary>Тир-фильтр каталога (DESIGN 2.10): пул режется по цене на три равные части.
    /// Чистая функция — тестируется тест-раннером.</summary>
    internal static class Catalog
    {
        /// <summary>tier: 0 — без фильтра, 1..3 — терциль по цене (1 — дешёвый).
        /// Список правится на месте (сортировка по цене).</summary>
        public static void ApplyTierFilter<T>(List<T> defs, Func<T, float> valueOf, int tier)
        {
            if (tier <= 0 || defs.Count == 0)
                return;
            defs.Sort((a, b) => valueOf(a).CompareTo(valueOf(b)));
            int n = defs.Count;
            // Границы терцилей с защитой от крошечных каталогов: i1>=1, i1<i2<=n
            // (при n=1: T1=[единственный], T2/T3 пусты — но не падаем).
            int i1 = Math.Max(1, n / 3);
            int i2 = Math.Min(Math.Max(i1 + 1, n * 2 / 3), n);
            List<T> keep = tier == 1
                ? defs.GetRange(0, i1)
                : tier == 2 ? defs.GetRange(i1, i2 - i1)
                : defs.GetRange(i2, n - i2);
            defs.Clear();
            defs.AddRange(keep);
        }
    }
}
