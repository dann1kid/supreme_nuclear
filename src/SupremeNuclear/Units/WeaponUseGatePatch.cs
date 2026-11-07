using HarmonyLib;
using NuclearOption.Networking;

namespace SupremeNuclear
{
    /// <summary>
    /// ЕДИНЫЙ ПРЕДОХРАНИТЕЛЬ применения оружия (v0.106, DESIGN §2.11, реестр
    /// ARCHITECTURE §6): префиксы WeaponStation.Fire и WeaponStation.LaunchMount —
    /// вместе это ВСЕ пути применения: турели (автозахват), FireControl (ПВО-батареи
    /// у радара, корабли, стратегические пусковые — сами зовут Fire напрямую, мимо
    /// турели и Ready()), боевой AI авиации (пуски ракет/бомб идут через LaunchMount),
    /// наш залп V (WeaponManager.Fire), сетевые Cmd/RpcLaunchMissile. Блок — до
    /// оригинала: weaponIndex/боезапас/LastFiredTime не двигаются, следов не остаётся.
    /// Охрана: host-only (клиенту приказы недоступны — правило №2); ас-человек не
    /// затрагивается (проверка как у нативного SafetyIsOn: Player/CombatHUD-борт);
    /// без единого ограничения в мире — O(1) выход (пушки зовут Fire каждый кадр).
    /// RemoteFireAuto/Single (визуальные пути клиентов) не патчатся — истину
    /// определяет хост. Guard-стиль: без условий мода — true, оригинал (§7.3).
    /// </summary>
    [HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.Fire))]
    internal static class WeaponFireGatePatch
    {
        private static bool Prefix(Unit owner, Unit target, WeaponStation __instance) =>
            WeaponUseGatePatch.Allows(owner, __instance, target);
    }

    [HarmonyPatch(typeof(WeaponStation), nameof(WeaponStation.LaunchMount))]
    internal static class WeaponLaunchGatePatch
    {
        // LaunchMount(Unit owner, Unit target, GlobalPosition aimpoint) — нам важны
        // только первые два; aimpoint префикс не объявляет и Harmony не инжектит.
        private static bool Prefix(Unit owner, Unit target, WeaponStation __instance) =>
            WeaponUseGatePatch.Allows(owner, __instance, target);
    }

    /// <summary>Общее ядро двух префиксов.</summary>
    internal static class WeaponUseGatePatch
    {
        internal static bool Allows(Unit owner, WeaponStation station, Unit target)
        {
            if (owner == null || station == null || station.WeaponInfo == null)
                return true;
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return true; // не хост — не наше решение
            // ЯО-предохранитель (§2.14-3): ДО быстрого выхода — флаг оружия O(1);
            // ас-человек (ручной пуск) не гейтится, как всегда.
            if (station.WeaponInfo.nuclear && !IsHumanAce(owner) &&
                !WeaponGateService.NuclearDecision(owner))
                return false;
            if (IsHumanAce(owner))
                return true; // ручное применение живого пилота не трогаем
            // СОЮЗНЫЕ АРМИИ (лобби v3): выстрел по союзной команде резан ВООБЩЕ
            // всегда (не зависит от WeaponGateService.AnyRestrictions) — цель-
            // юнит с HQ союзной фракции. Ас-человек выше уже вышел (ручной пуск
            // по своим остаётся на его совести, как френдли-файр SupCom off).
            if (target != null && target.NetworkHQ != null && owner.NetworkHQ != null &&
                target.NetworkHQ != owner.NetworkHQ &&
                SnLobbyService.AlliedFactions(owner.NetworkHQ.faction?.factionName,
                    target.NetworkHQ.faction?.factionName))
                return false;
            if (!WeaponGateService.AnyRestrictions)
                return true; // в мире нет ни стойки, ни гейтов, ни правил
            return !WeaponGateService.BlocksUse(owner, station, target);
        }

        /// <summary>Борт за живым игроком — та же проверка, что у нативного
        /// SafetyIsOn (WeaponStation): Player или борт текущего CombatHUD.</summary>
        private static bool IsHumanAce(Unit owner) =>
            owner is Aircraft aircraft &&
            (aircraft.Player != null ||
             (SceneSingleton<CombatHUD>.i != null && SceneSingleton<CombatHUD>.i.aircraft == aircraft));
    }
}
