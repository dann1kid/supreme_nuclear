using HarmonyLib;

namespace SupremeNuclear
{
    /// <summary>Стойка «не открывать огонь»: турель юнита в этой стойке не выбирает цели
    /// (пропускаем AssessTargetPriority целиком). GC патчил этот же метод.</summary>
    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    internal static class TurretHoldFirePatch
    {
        private static bool Prefix(Turret __instance)
        {
            return !StanceService.IsHoldFire(__instance.GetAttachedUnit());
        }
    }
}
