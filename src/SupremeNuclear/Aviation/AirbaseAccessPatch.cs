using HarmonyLib;

namespace SupremeNuclear
{
    /// <summary>
    /// Вертолётные вертикальные точки посадки не имеют нативных флагов «посадка
    /// закрыта» (у полос есть — Airbase.Runway.Landing, туда пишет AirbaseAccess).
    /// Два префикса закрывают для вертолётов базу с закрытой посадкой:
    /// IsSuitable — чтобы нативный TryGetNearestAirbase (вертикальный запрос
    /// AIHeloLandingState) сразу вёл борт к другой базе, а не барражировал у
    /// закрытой; TryRequestVerticalLanding — чтобы закрытие сработало и посреди
    /// захода на точку. Блокируется ТОЛЬКО вертикальный запрос (RunwayType=Vertical
    /// не пересекается с битами полос): посадочные запросы самолётов решают сами
    /// нативные флаги полос, запросы Any/Takeoff не трогаем — ими пользуется взлёт.
    /// Оба префикса берут управление только при блокировке (иначе true — оригинал).
    /// Реестр патчей — ARCHITECTURE §6.
    /// </summary>
    [HarmonyPatch(typeof(Airbase))]
    internal static class AirbaseAccessPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Airbase.IsSuitable), new[] { typeof(RunwayQuery) })]
        private static bool IsSuitable_Prefix(Airbase __instance, RunwayQuery query,
            ref bool __result)
        {
            if (query.RunwayType != RunwayQueryType.Vertical ||
                AirbaseAccess.IsLandingOpen(__instance))
                return true; // не вертикальный запрос / посадка открыта — мод не берёт управление
            __result = false;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Airbase.TryRequestVerticalLanding))]
        private static bool TryRequestVerticalLanding_Prefix(Airbase __instance,
            out Airbase.VerticalLandingPoint landingPoint, ref bool __result)
        {
            landingPoint = null;
            if (AirbaseAccess.IsLandingOpen(__instance))
                return true; // посадка открыта — мод не берёт управление
            __result = false;
            return false;
        }

        /// <summary>Целевые методы на месте (проверка SelfCheck, правило №13).</summary>
        public static bool FieldsOk =>
            AccessTools.Method(typeof(Airbase), nameof(Airbase.IsSuitable),
                new[] { typeof(RunwayQuery) }) != null &&
            AccessTools.Method(typeof(Airbase), nameof(Airbase.TryRequestVerticalLanding)) != null;
    }
}
