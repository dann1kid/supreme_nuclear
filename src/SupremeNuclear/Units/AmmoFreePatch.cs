using HarmonyLib;

namespace SupremeNuclear
{
    /// <summary>
    /// Патч №13 — бесконечные обычные боеприпасы (§2.14 слой 1, SupCom-канон):
    /// ПОСТФИКС WeaponStation.UpdateLastFired — единственная точка учёта расхода
    /// (сервер: пересчёт AccountAmmo; клиент: Ammo -= rounds). После каждого
    /// применения оружия не-авиация получает бесплатный полный Rearm — мгновенно,
    /// без окон высушивания и без поллинга (юзер подсказал: патчить расход дешевле
    /// реарма по таймеру). Авиацию не трогаем: её сервис за деньги — часть
    /// ценности борта (SortieService). Гейт O(1): профиль выключен — выход.
    /// </summary>
    [HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.UpdateLastFired))]
    internal static class AmmoFreePatch
    {
        private static void Postfix(WeaponStation __instance)
        {
            if (!SupplyService.FreeAmmo || !SupplyService.GroundStation(__instance))
                return;
            int need = __instance.FullAmmo - __instance.Ammo;
            if (need > 0)
                __instance.Rearm(need);
        }
    }
}
