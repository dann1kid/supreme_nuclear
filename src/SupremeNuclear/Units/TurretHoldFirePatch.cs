using HarmonyLib;

namespace SupremeNuclear
{
    /// <summary>Стойка «не открывать огонь»: турель юнита в этой стойке не выбирает цели
    /// (пропускаем AssessTargetPriority целиком). GC патчил этот же метод.
    /// Сюда же — гейт вооружения (DESIGN §2.11): турель, чья станция в выключенном
    /// классе юнита, тоже не выбирает цель (не целится — не выдаёт себя наведением);
    /// перевод огня на другую станцию турели остаётся нативным. Guard-стиль: без
    /// признака — возвращаем true, оригинал отрабатывает (инвариант §7.3).</summary>
    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    internal static class TurretHoldFirePatch
    {
        private static bool Prefix(Turret __instance)
        {
            Unit unit = __instance.GetAttachedUnit();
            if (StanceService.IsHoldFire(unit))
                return false;
            WeaponClass mask = WeaponGateService.BlockedOf(unit);
            if (mask != WeaponClass.None &&
                (mask & Catalog.ClassOf(__instance.GetWeaponStation()?.WeaponInfo)) != 0)
                return false;
            return true;
        }
    }
}
