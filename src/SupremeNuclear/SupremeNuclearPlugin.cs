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
            Log.SubscribeUnity(); // счётчик ошибок сессии для F11 (до патчей — видеть и свои провалы)
            Loc.Bootstrap(Settings.BindLanguage(Config)); // до Bind: описания настроек локализуются
            Settings.Bind(Config);

            if (!Settings.ModEnabled)
            {
                Log.Info($"{PluginInfo.Name} {PluginInfo.Version} отключён в конфигурации");
                return;
            }

            SelfCheck.RunStartup();

            // Все Harmony-патчи мода — атрибутными классами по сборке, реестр — ARCHITECTURE §6.
            _harmony = new Harmony(PluginInfo.Guid);
            _harmony.PatchAll();

            SceneManager.sceneLoaded += (_, __) =>
            {
                // Статическое состояние мода переживает сцены: всё, что держит юнитов/точки
                // умершей сцены, сбрасываем здесь (см. ARCHITECTURE §4).
                Log.ResetSessionCounters(); // ошибки/предупреждения сцены не копятся
                GameApi.ResetSceneCache();
                EconomyService.Reset();
                SalvageService.Reset(); // прогресс разбора обломков привязан к сцене
                RepairService.Reset(); // налив ремонта и снятые патрули привязаны к сцене
                EngineerService.ResetScene(); // задекорированные клоны умерли вместе со сценой
                PauseService.Reset(); // паузы юнитов умерли вместе со сценой
                ShipPadService.Reset(); // палубы умерли вместе со сценой
                CaptureService.Reset(); // прогресс инженерного захвата привязан к сцене
                AiCommanderService.Reset(); // планы AI-фракций привязаны к юнитам сцены
                BuildTasks.Reset();
                BuildGhosts.Reset();
                ProductionTasks.Reset();
                FactoryTierService.Reset(); // тиры/апгрейды фабрик умерли вместе со сценой
                AircraftOrders.Reset();
                AircraftTargetPinPatch.ResetClaims(); // реестр занятости целей бортов сцены
                GroundOrders.Reset();
                StanceService.Reset();
                WeaponGateService.Reset(); // гейты вооружения привязаны к юнитам сцены
                FleetSalvo.Reset(); // ограничение залпа типом — новый заряд в новой сцене
                DefenseService.Reset(); // режимы защиты/таймеры сброса привязаны к бортам сцены
                SalvoService.Reset();   // выбор станций залпа — тоже
                AirbaseReserve.Reset(); // резервы ангаров умершей сцены недействительны
                AirbaseAccess.Reset(); // тумблеры посадки/взлёта умершей сцены недействительны
                SortieService.Reset(); // слоты выката привязаны к базам сцены
                VictoryService.Reset(); // «имевшие имущество» фракции — объекты сцены
                FogOfWarService.Reset(); // туман войны: скрытия/ghost-здания/сетка умерли со сценой
                SupplyService.Reset(); // кэш станций снабжения умер вместе со сценой
                WarheadService.Reset(); // заряды БЧ/точки пуска умерли вместе со сценой
                SelectionService.ResetGroups();
                RangeRings.Reset(); // кольца дальности: круги/буферы сцены
                UnitPreview.Reset(); // превью-иконки: очередь/бинды/RT/кэш умершей сцены
                TaxiSurface.Reset(); // плиты дорисованных рулёжек умершей сцены
                RouteRibbonMesh.Destroy(); // меши/материалы от донора умершей сцены
                CommanderAudio.Restore(); // страховка: вернуть доплер источникам, если вышли без Deactivate
                OrderModes.Pending = OrderMode.None;
            };

            gameObject.AddComponent<CommanderModeController>();
            gameObject.AddComponent<CommanderHud>();
            gameObject.AddComponent<SupcomUi>();
            gameObject.AddComponent<MinimapPanel>();
            gameObject.AddComponent<SkirmishPanel>(); // «Скримиш» в главном меню (лобби v1)
            gameObject.AddComponent<MapGenAsync>();   // асинхронная генерация карт (v0.154)
            gameObject.AddComponent<LobbyPanel>();    // в-игре фаза MP-лобби (v0.154)
            SnLobbyService.InitializeScenes();        // MP-лобби: сброс сеанса при возврате в меню
            SnLobbyService.EnsureInitialized();        // MP-лобби: сеть (дожим — MapGenAsync.Update)

            Log.Info($"{PluginInfo.Name} {PluginInfo.Version} загружен — {Settings.ToggleCommanderKey} в миссии включает режим командира");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
    }
}
