using System.Reflection;
using HarmonyLib;

namespace SupremeNuclear
{
    /// <summary>Постфикс LeaveState посадочных состояний: игра снимает борт
    /// с runway.landingList ТОЛЬКО в штатных ветках (докат до exit-точки,
    /// уход на второй круг) — сам выход из состояния НЕ снимает. Борт,
    /// выдернутый из посадки приказом (RTS: перезадачили идущего на посадку),
    /// ОСТАЁТСЯ в списке посадок навсегда, а IsAvailableForTakeoff при
    /// непустом landingList всегда false — все последующие вылеты с полосы
    /// «бесконечно ждут разрешение» (плейтест v0.98.3: борт на кривой рулёжки).
    /// Снятие идемпотентно (List.Remove), штатные пути не задваиваются.
    /// Композиция невозможна: приватное внутреннее состояние на выходе из
    /// состояния. Реестр — ARCHITECTURE §6.8.</summary>
    internal static class LandingDeregisterPatch
    {
        internal static readonly FieldInfo AircraftField =
            AccessTools.Field(typeof(PilotBaseState), "aircraft");

        internal static readonly FieldInfo UsageFieldFull =
            AccessTools.Field(typeof(AIPilotLandingState), "runwayUsage");

        internal static readonly FieldInfo UsageFieldShort =
            AccessTools.Field(typeof(AIPilotShortLandingState), "runwayUsage");

        internal static bool FieldsOk =>
            AircraftField != null && UsageFieldFull != null && UsageFieldShort != null;

        internal static void Deregister(object state, FieldInfo usageField)
        {
            if (!FieldsOk || state == null)
                return;
            var usage = usageField.GetValue(state) as Airbase.Runway.RunwayUsage?;
            var aircraft = AircraftField.GetValue(state) as Aircraft;
            if (usage == null || aircraft == null || usage.Value.Runway == null)
                return; // не успел зарегистрироваться — снимать нечего
            usage.Value.Runway.DeregisterLanding(aircraft);
        }
    }

    [HarmonyPatch(typeof(AIPilotLandingState), "LeaveState")]
    internal static class LandingDeregisterPatch_Full
    {
        private static void Postfix(AIPilotLandingState __instance) =>
            LandingDeregisterPatch.Deregister(__instance, LandingDeregisterPatch.UsageFieldFull);
    }

    [HarmonyPatch(typeof(AIPilotShortLandingState), "LeaveState")]
    internal static class LandingDeregisterPatch_Short
    {
        private static void Postfix(AIPilotShortLandingState __instance) =>
            LandingDeregisterPatch.Deregister(__instance, LandingDeregisterPatch.UsageFieldShort);
    }
}
