using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SupremeNuclear
{
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public sealed class SupremeNuclearPlugin : BaseUnityPlugin
    {
        private Harmony _harmony;

        private void Awake()
        {
            Log.Init(Logger);
            Loc.Bootstrap(Settings.BindLanguage(Config)); // до Bind: описания настроек локализуются
            Settings.Bind(Config);

            if (!Settings.ModEnabled)
            {
                Log.Info($"{PluginInfo.Name} {PluginInfo.Version} отключён в конфигурации");
                return;
            }

            SelfCheck.RunStartup();

            // Единственный патч мода — руление авиацией с приказом (AIPilotCombatModes.FixedUpdateState).
            _harmony = new Harmony(PluginInfo.Guid);
            _harmony.PatchAll();

            SceneManager.sceneLoaded += (_, __) =>
            {
                // Статическое состояние мода переживает сцены: всё, что держит юнитов/точки
                // умершей сцены, сбрасываем здесь (см. ARCHITECTURE §4).
                GameApi.ResetSceneCache();
                EconomyService.Reset();
                BuildTasks.Reset();
                BuildGhosts.Reset();
                ProductionTasks.Reset();
                AircraftOrders.Reset();
                GroundOrders.Reset();
                StanceService.Reset();
                WeaponGateService.Reset(); // гейты вооружения привязаны к юнитам сцены
                DefenseService.Reset(); // режимы защиты/таймеры сброса привязаны к бортам сцены
                SalvoService.Reset();   // выбор станций залпа — тоже
                AirbaseReserve.Reset(); // резервы ангаров умершей сцены недействительны
                SelectionService.ResetGroups();
                RangeRings.Reset(); // кольца дальности: круги/буферы сцены
                TaxiSurface.Reset(); // плиты дорисованных рулёжек умершей сцены
                RouteRibbonMesh.Destroy(); // меши/материалы от донора умершей сцены
                CommanderAudio.Restore(); // страховка: вернуть доплер источникам, если вышли без Deactivate
                OrderModes.Pending = OrderMode.None;
            };

            gameObject.AddComponent<CommanderModeController>();
            gameObject.AddComponent<CommanderHud>();
            gameObject.AddComponent<SupcomUi>();
            gameObject.AddComponent<MinimapPanel>();
            gameObject.AddComponent<EngineerFloat>(); // плавучесть инженеров (FixedUpdate, host)

            Log.Info($"{PluginInfo.Name} {PluginInfo.Version} загружен — {Settings.ToggleCommanderKey} в миссии включает режим командира");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
    }
}
