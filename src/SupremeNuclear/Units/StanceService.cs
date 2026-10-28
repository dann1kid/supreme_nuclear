using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Стойки: «не открывать огонь» (остальные — штатное поведение игры:
    /// ответный огонь и атака по площади у турелей нативные).</summary>
    public static class StanceService
    {
        private static readonly HashSet<Unit> HoldFire = new HashSet<Unit>();

        public static bool IsHoldFire(Unit unit) => unit != null && HoldFire.Contains(unit);

        /// <summary>Есть ли в мире хоть один юнит в стойке — быстрый выход
        /// предохранителя WeaponUseGatePatch без обхода выделения.</summary>
        public static bool AnyHolding => HoldFire.Count > 0;

        public static bool Toggle(Unit unit)
        {
            if (unit == null)
                return false;
            if (!HoldFire.Remove(unit))
            {
                HoldFire.Add(unit);
                return true;
            }
            return false;
        }

        public static int CountHolding(IEnumerable<Unit> units)
        {
            int n = 0;
            foreach (Unit unit in units)
                if (IsHoldFire(unit))
                    n++;
            return n;
        }

        public static void Prune()
        {
            List<Unit> dead = null;
            foreach (Unit unit in HoldFire)
                if (unit == null || unit.disabled)
                    (dead ??= new List<Unit>()).Add(unit);
            if (dead == null)
                return;
            foreach (Unit unit in dead)
                HoldFire.Remove(unit);
        }

        /// <summary>Полный сброс (смена сцены).</summary>
        public static void Reset() => HoldFire.Clear();
    }
}
