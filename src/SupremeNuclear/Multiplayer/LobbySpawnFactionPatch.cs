using HarmonyLib;
using Mirage;
using NuclearOption.Networking;

namespace SupremeNuclear
{
    /// <summary>
    /// Патч №16 (реестр — ARCHITECTURE §6): фракция по плану лобби при спавне
    /// персонажа. В лобби v2 (комната в EMPTY) у игроков персонажей НЕТ — они
    /// спавнятся только при загрузке карты на СТАРТе, а FactionHQ живут в
    /// префабе карты, так что назначить сторону «в комнате» нативно нельзя.
    /// План сторон — данные хоста; на спавне каждого персонажа хост серверно
    /// применяет SetFaction по плану (side A — хост-командир, асы — по своим
    /// сторонам). Гостям, вошедшим по ходу боя без плана, остаётся нативное
    /// меню фракций. Постфикс без guard-возврата (мод ничего не подменяет).
    /// </summary>
    [HarmonyPatch]
    internal static class LobbySpawnFactionPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(NetworkManagerNuclearOption), "SpawnCharacter")]
        private static void AfterSpawnCharacter(INetworkPlayer networkPlayer)
        {
            try
            {
                SnLobbyService.ApplyFactionFor(networkPlayer);
            }
            catch (System.Exception e)
            {
                Log.Warn("SnLobby: план фракций не применён — " + e.Message);
            }
        }
    }
}
