using HarmonyLib;

namespace SupremeNuclear
{
    /// <summary>Стойка «не открывать огонь»: турель юнита в этой стойке не выбирает цели
    /// (пропускаем AssessTargetPriority целиком). GC патчил этот же метод.
    /// Сюда же — гейты вооружения (DESIGN §2.11): оценка кандидата скипается, если НИ
    /// одна станция турели не проходит правила против этого кандидата (класс-гейт или
    /// правило типа по домену цели — TurretMayEngage). Проверка по КАНДИДАТУ, а не по
    /// текущей станции: перевод огня/выбор станции происходит внутри этого же метода
    /// (currentWeaponStation = ...), и скип по текущей станции замораживал выбор —
    /// смешанная турель (пушка+ракеты) с выключенным ракетным классом навсегда теряла
    /// и разрешённую пушку (v0.106). Натив может выбрать в этом проходе неразрешённую
    /// станцию — выстрел режет предохранитель WeaponUseGatePatch (компромисс §12:
    /// наведение без пуска). Guard-стиль: без признака — true, оригинал (§7.3).</summary>
    [HarmonyPatch(typeof(Turret), "AssessTargetPriority")]
    internal static class TurretHoldFirePatch
    {
        private static bool Prefix(Turret __instance, Unit targetCandidate)
        {
            Unit unit = __instance.GetAttachedUnit();
            if (StanceService.IsHoldFire(unit))
                return false;
            if (!WeaponGateService.TurretMayEngage(__instance, unit, targetCandidate))
                return false;
            return true;
        }
    }
}
