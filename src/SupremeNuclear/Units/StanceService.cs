using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Стойки: «не открывать огонь» (остальные — штатное поведение игры:
    /// ответный огонь и атака по площади у турелей нативные).</summary>
    public static class StanceService
    {
        private static readonly HashSet<Unit> HoldFire = new HashSet<Unit>();
        // Скоординированная атака: временный запрет огня до времени залпа
        // (поверх стойки; снимается сам — юзерскую HoldFire не трогает).
        private static readonly HashSet<Unit> CoordHold = new HashSet<Unit>();

        public static bool IsHoldFire(Unit unit) =>
            unit != null && (HoldFire.Contains(unit) || CoordHold.Contains(unit));

        /// <summary>Есть ли в мире хоть один юнит в стойке — быстрый выход
        /// предохранителя WeaponUseGatePatch без обхода выделения.</summary>
        public static bool AnyHolding => HoldFire.Count > 0 || CoordHold.Count > 0;

        /// <summary>Временный запрет огня координации (снимается сам по таймеру
        /// или с приказом; в пользовательскую стойку не пишется).</summary>
        internal static void SetCoordHold(Unit unit, bool on)
        {
            if (unit == null)
                return;
            if (on)
                CoordHold.Add(unit);
            else
                CoordHold.Remove(unit);
        }

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
            if (dead != null)
                foreach (Unit unit in dead)
                    HoldFire.Remove(unit);
            dead = null;
            foreach (Unit unit in CoordHold)
                if (unit == null || unit.disabled)
                    (dead ??= new List<Unit>()).Add(unit);
            if (dead != null)
                foreach (Unit unit in dead)
                    CoordHold.Remove(unit);
        }

        /// <summary>Полный сброс (смена сцены).</summary>
        public static void Reset()
        {
            HoldFire.Clear();
            CoordHold.Clear();
        }
    }
}
