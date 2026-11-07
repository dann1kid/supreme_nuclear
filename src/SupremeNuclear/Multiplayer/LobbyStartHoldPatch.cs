using HarmonyLib;

namespace SupremeNuclear
{
    /// <summary>
    /// Патч №15 (реестр — ARCHITECTURE §6): окно применения плана лобби. В лобби
    /// v2 (комната в EMPTY) стороны — план хоста; фракции применяются патчем №16
    /// при спавне персонажей. Нативный MissionManager.StartMission зовётся сам,
    /// как только хост загрузил карту (OnStartServer → ждёт MapSettings →
    /// StartMission) — холд держит его, пока SnLobbyService.Launch не применит
    /// preventJoin/план (частично уже при спавне) и не снимет флаг; юниты
    /// сценария спавнятся OnSceneLoaded ВНУТРИ StartMission — до снятия их нет.
    /// Префикс молчит вне SN-лобби: одиночная игра, нативный MP, рестарты.
    /// </summary>
    [HarmonyPatch]
    internal static class LobbyStartHoldPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.StartMission))]
        private static bool HoldStartMission()
        {
            if (!SnLobbyService.HoldStart)
                return true; // вне SN-лобби — оригинал (инвариант §7-3)
            Log.Info("SnLobby: StartMission удержан — идёт фаза лобби (старт — кнопкой хоста)");
            return false;
        }
    }
}
