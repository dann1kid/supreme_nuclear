using System.Collections.Generic;
using NuclearOption.MissionEditorScripts;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Objectives;
using NuclearOption.SavedMission.Outcomes;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Проверки предположений об API игры (паттерн Ground Control):
    /// стартовые — без сцены, активационные — при входе в режим командира.
    /// </summary>
    internal static class SelfCheck
    {
        public static readonly List<string> Failures = new List<string>();

        // Буфер пробы тумана войны (без аллокаций на вызов).
        private static readonly List<Renderer> FogProbe = new List<Renderer>();

        public static void RunStartup()
        {
            Failures.Clear();
            Check("PhysicsLayers.StaticsMask", () => { var _ = PhysicsLayers.StaticsMask; });
            Check("Datum.LocalSeaY", () => { var _ = Datum.LocalSeaY; });
            Check("InputFieldChecker.InsideInputField", () => { var _ = InputFieldChecker.InsideInputField; });
            Check("GlobalPositionExtensions", () =>
            {
                var v = new Vector3(1234.5f, 67.8f, -910.1f);
                var round = v.ToGlobalPosition().ToLocalPosition();
                if ((round - v).magnitude > 0.01f) throw new System.Exception("roundtrip mismatch: " + round);
            });
            Check("AIPilotCombatModes.AssessHQTarget (пиннинг цели)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(AIPilotCombatModes), "AssessHQTargets") == null)
                    throw new System.Exception("метод AssessHQTargets не найден — патч пиннинга мёртв");
                if (!AircraftTargetPinPatch.FieldsOk)
                    throw new System.Exception("не найдены reflection-поля currentTarget/currentWeaponInfo/...");
            });
            Check("FactionHQ.trackingDatabase (распределение целей авиации)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(FactionHQ), "trackingDatabase") == null)
                    throw new System.Exception("поле trackingDatabase не найдено — распределение целей мертво");
                if (HarmonyLib.AccessTools.Method(typeof(TrackingInfo), "TryGetUnit") == null)
                    throw new System.Exception("TrackingInfo.TryGetUnit не найден — перебор целей трекинга мёртв");
                if (HarmonyLib.AccessTools.Method(typeof(FactionHQ), "IsTargetPositionAccurate") == null)
                    throw new System.Exception("FactionHQ.IsTargetPositionAccurate не найден — скоринг целей мёртв");
            });
            Check("AIHeloCombatState (вертолётные патчи)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(AIHeloCombatState), "AssessHQTargets") == null ||
                    HarmonyLib.AccessTools.Method(typeof(AIHeloCombatState), "FixedUpdateState") == null)
                    throw new System.Exception("методы боевого AI вертолёта не найдены");
                if (!HeloTargetPinPatch.FieldsOk)
                    throw new System.Exception("пиннинг: нет полей currentTarget/currentWeaponInfo");
                if (!HeloSteeringPatch.FieldsOk)
                    throw new System.Exception("руление: нет полей destination/aircraft/timeWithoutTarget");
            });
            Check("Airbase.hangars + Hangar.attachedUnit + GetAvailableAircraft", () =>
            {
                if (HarmonyLib.AccessTools.Property(typeof(Airbase), "hangars") == null)
                    throw new System.Exception("Airbase.hangars не найден");
                if (HarmonyLib.AccessTools.Field(typeof(Hangar), "attachedUnit") == null)
                    throw new System.Exception("Hangar.attachedUnit не найден");
                if (HarmonyLib.AccessTools.Method(typeof(Airbase), "GetAvailableAircraft") == null ||
                    HarmonyLib.AccessTools.Method(typeof(Hangar), "GetAvailableAircraft") == null)
                    throw new System.Exception("GetAvailableAircraft не найден");
                if (HarmonyLib.AccessTools.Property(typeof(Airbase), "AttachedAirbase") == null)
                    throw new System.Exception("Airbase.AttachedAirbase не найден — заголовок панели авианосца мёртв");
            });
            Check("Hangar.availableAircraft (каталог недостроя ангара)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(Hangar), "availableAircraft") == null)
                    throw new System.Exception(
                        "Hangar.availableAircraft не найден — каталог очереди недостроя ангара пуст");
            });
            Check("MissionManager.listWrecks (адресный сальвиж)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(MissionManager), nameof(MissionManager.listWrecks)) == null)
                    throw new System.Exception("listWrecks не найден — ПКМ-сбор обломков мёртв");
            });
            Check("UnitPart.hitPoints (модовый ремонт)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(UnitPart), nameof(UnitPart.hitPoints)) == null)
                    throw new System.Exception("UnitPart.hitPoints не найден — налив HP ремонта мёртв");
                if (HarmonyLib.AccessTools.Method(typeof(Building), nameof(Building.Repair)) == null)
                    throw new System.Exception("Building.Repair не найден — нативные «леса» недоступны (долг §12)");
            });
            Check("FactionHQ.AnyNearAirbase (руление к ближней полосе)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(FactionHQ), nameof(FactionHQ.AnyNearAirbase)) == null)
                    throw new System.Exception("метод AnyNearAirbase не найден — патч мёртв");
                if (HarmonyLib.AccessTools.Field(typeof(FactionHQ), "airbasesUnsorted") == null)
                    throw new System.Exception("поле airbasesUnsorted не найдено — патч не увидит аэродромы");
            });
            Check("AIPilotLandingState.LeaveState (снятие с landingList)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(AIPilotLandingState), "LeaveState") == null ||
                    HarmonyLib.AccessTools.Method(typeof(AIPilotShortLandingState), "LeaveState") == null)
                    throw new System.Exception("LeaveState посадок не найден — патч мёртв");
                if (!LandingDeregisterPatch.FieldsOk)
                    throw new System.Exception("нет полей runwayUsage/aircraft — патч не снимет с landingList");
            });
            Check("WeaponStation.Fire/LaunchMount (предохранитель правил оружия)", () =>
            {
                var fire = HarmonyLib.AccessTools.Method(typeof(WeaponStation), nameof(WeaponStation.Fire));
                var launch = HarmonyLib.AccessTools.Method(typeof(WeaponStation), nameof(WeaponStation.LaunchMount));
                if (fire == null || launch == null)
                    throw new System.Exception("методы применения оружия не найдены — предохранитель мёртв");
                foreach (System.Reflection.MethodInfo m in new[] { fire, launch })
                {
                    var ps = m.GetParameters();
                    if (ps.Length < 2 || ps[0].Name != "owner" || ps[1].Name != "target")
                        throw new System.Exception(m.Name + ": параметры owner/target переименованы — префикс не свяжется");
                }
                if (HarmonyLib.AccessTools.Method(typeof(Turret), nameof(Turret.GetWeaponStations)) == null)
                    throw new System.Exception("Turret.GetWeaponStations не найден — проверка турели по кандидату мертва");
                if (HarmonyLib.AccessTools.Property(typeof(Aircraft), nameof(Aircraft.Player)) == null)
                    throw new System.Exception("Aircraft.Player не найден — исключение аса-человека не сработает");
            });
            Check("Airbase.servicePoints (сервис-точки севших бортов)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(Airbase), "servicePoints") == null)
                    throw new System.Exception("поле servicePoints не найдено — севшие борты не припаркуются");
            });
            Check("Выкат ВПП: exit/сервис-точки + повторный взлёт (v0.109)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(SavedRunway), nameof(SavedRunway.exitPoints)) == null)
                    throw new System.Exception("SavedRunway.exitPoints не найден — съезды с ВПП не задать");
                if (HarmonyLib.AccessTools.Field(typeof(SavedAirbase), nameof(SavedAirbase.ServicePoints)) == null)
                    throw new System.Exception("SavedAirbase.ServicePoints не найден — парковка севших не задать");
                if (HarmonyLib.AccessTools.Field(typeof(Airbase.Runway), nameof(Airbase.Runway.exitPoints)) == null)
                    throw new System.Exception("Airbase.Runway.exitPoints не найден — доворот съездов мёртв");
                if (HarmonyLib.AccessTools.Method(typeof(Airbase), nameof(Airbase.AircraftIsOnRunway)) == null)
                    throw new System.Exception("Airbase.AircraftIsOnRunway не найден — парковка вне полосы не проверится");
                var fi = HarmonyLib.AccessTools.Field(typeof(Pilot), nameof(Pilot.flightInfo));
                if (fi == null || HarmonyLib.AccessTools.Field(typeof(Pilot.FlightInfo),
                        nameof(Pilot.FlightInfo.HasTakenOff)) == null)
                    throw new System.Exception("Pilot.flightInfo/HasTakenOff не найдены — повторный выкат сломан");
                if (HarmonyLib.AccessTools.Field(typeof(Airbase.Runway), "takeoffQueue") == null)
                    throw new System.Exception("Airbase.Runway.takeoffQueue не найден — янитор очереди взлёта мёртв");
            });
            Check("Airbase.Runway.Landing/Takeoff + вертикальные точки (тумблеры аэродрома)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(Airbase), "runways") == null ||
                    HarmonyLib.AccessTools.Field(typeof(Airbase.Runway), nameof(Airbase.Runway.Landing)) == null ||
                    HarmonyLib.AccessTools.Field(typeof(Airbase.Runway), nameof(Airbase.Runway.Takeoff)) == null)
                    throw new System.Exception("флаги полос не найдены — тумблеры аэродрома мертвы");
                if (HarmonyLib.AccessTools.Method(typeof(Airbase.Runway), nameof(Airbase.Runway.GetWidth)) == null)
                    throw new System.Exception("Runway.GetWidth не найден — пик полотна ВПП кликом мёртв");
                if (HarmonyLib.AccessTools.Method(typeof(Unit), nameof(Unit.GetAirbase)) == null)
                    throw new System.Exception("Unit.GetAirbase не найден — тумблеры авианосца мертвы");
                if (!AirbaseAccessPatch.FieldsOk)
                    throw new System.Exception("IsSuitable/TryRequestVerticalLanding не найдены — вертолёты не увидят закрытую посадку");
            });
            Check("Spawner.SpawnBuilding + unitPrefab (достройка ангаров)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(Spawner), "SpawnBuilding") == null)
                    throw new System.Exception("Spawner.SpawnBuilding не найден");
                if (HarmonyLib.AccessTools.Field(typeof(UnitDefinition), "unitPrefab") == null)
                    throw new System.Exception("UnitDefinition.unitPrefab не найден");
            });
            Check("UnitPreview: unitPrefab + слой 16 (3D-превью иконок)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(UnitDefinition), "unitPrefab") == null)
                    throw new System.Exception("UnitDefinition.unitPrefab не найден — превью-иконки не соберутся");
                // Слой рендера превью предполагается СВОБОДНЫМ: камера рисует на нём
                // только свои меши; занятый игрой слой подхватит чужую геометрию.
                string layerName = LayerMask.LayerToName(UnitPreview.PreviewLayer);
                if (!string.IsNullOrEmpty(layerName))
                    Log.Warn($"SelfCheck: слой {UnitPreview.PreviewLayer} занят игрой ('{layerName}') — превью-иконки могут рисоваться с мусором");
            });
            Check("VehicleDepot.spawnTransform (стрелка выхода депо)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(VehicleDepot), "spawnTransform") == null)
                    throw new System.Exception("VehicleDepot.spawnTransform не найден — выход депо берётся по габариту");
            });
            Check("Loc: вшитая en-таблица (фолбэк установки без папки Locales/)", () =>
            {
                using (var s = System.Reflection.Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("SupremeNuclear.locales.en.json"))
                    if (s == null)
                        throw new System.Exception(
                            "EmbeddedResource en.json не найден в манифесте DLL — csproj потерял встроенный ресурс");
            });
            Check("Тиры фабрик/инженеров (VehicleDepot.TrySpawnVehicle, Hangar.parentAirbase)", () =>
            {
                // Клоны инженеров T2/T3 спавнятся нативным депо как обычные
                // определения — если сигнатура уплыла, апнутые депо «не строят».
                if (HarmonyLib.AccessTools.Method(typeof(VehicleDepot), "TrySpawnVehicle") == null)
                    throw new System.Exception("VehicleDepot.TrySpawnVehicle не найден — клоны инженеров не спавнятся");
                if (HarmonyLib.AccessTools.Field(typeof(Hangar), nameof(Hangar.parentAirbase)) == null)
                    throw new System.Exception("Hangar.parentAirbase не найден — тир авиации не найдёт аэродром");
                // Апдейт игры 30.10: hangars стал СВОЙСТВОМ (поле → property) —
                // проба смотрит оба; прямой код компилируется против живой сборки.
                if (HarmonyLib.AccessTools.Field(typeof(Airbase), "hangars") == null &&
                    HarmonyLib.AccessTools.Property(typeof(Airbase), "hangars") == null)
                    throw new System.Exception("Airbase.hangars не найден — живость/владелец тира аэродрома мертвы");
                if (HarmonyLib.AccessTools.Field(typeof(Encyclopedia), "buildings") == null)
                    throw new System.Exception("Encyclopedia.buildings не найден — ростер зданий пуст");
            });
            Check("ЯО (WarheadStorage.AddWarhead, Spawner.SpawnMissile, MissionSettings)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(WarheadStorage), nameof(WarheadStorage.AddWarhead)) == null)
                    throw new System.Exception("WarheadStorage.AddWarhead не найден — склад не наполняется");
                if (HarmonyLib.AccessTools.Method(typeof(WarheadStorage), nameof(WarheadStorage.GetNumber)) == null)
                    throw new System.Exception("WarheadStorage.GetNumber не найден — кап склада мёртв");
                // Апдейт игры 30.10: у SpawnMissile ДВЕ перегрузки (MissileDefinition/
                // GameObject) — поиск по одному имени кидает AmbiguousMatch; мод
                // вызывает MissileDefinition-вариант (WarheadService).
                if (HarmonyLib.AccessTools.Method(typeof(Spawner), nameof(Spawner.SpawnMissile),
                        new[] { typeof(MissileDefinition), typeof(Vector3), typeof(Quaternion),
                                typeof(Vector3), typeof(Unit), typeof(Unit) }) == null)
                    throw new System.Exception("Spawner.SpawnMissile не найден — ядерный пуск с корабля мёртв");
                if (HarmonyLib.AccessTools.Method(typeof(FactionHQ), nameof(FactionHQ.GetWarheadStockpile)) == null)
                    throw new System.Exception("FactionHQ.GetWarheadStockpile не найден — топбар БЧ мёртв");
                foreach (string f in new[] { "nuclearEscalationThreshold", "strategicEscalationThreshold",
                                             "minRankTacticalWarhead", "minRankStrategicWarhead" })
                    if (HarmonyLib.AccessTools.Field(typeof(NuclearOption.SavedMission.MissionSettings), f) == null)
                        throw new System.Exception("MissionSettings." + f + " не найден — обнуление эскалации SN-карт мертво");
            });
            Check("SpawnCustomAirbase (постройка ВПП-аэродрома)", () =>
            {
                if (HarmonyLib.AccessTools.Property(typeof(MissionManager), "CurrentMission") == null)
                    throw new System.Exception("MissionManager.CurrentMission не найден");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.SavedMission.Mission),
                        "SpawnCustomAirbase") == null)
                    throw new System.Exception("Mission.SpawnCustomAirbase не найден");
                if (HarmonyLib.AccessTools.Field(typeof(NuclearOption.SavedMission.SavedRunway),
                        "Start") == null ||
                    HarmonyLib.AccessTools.Field(typeof(NuclearOption.SavedMission.SavedRunway),
                        "Width") == null ||
                    HarmonyLib.AccessTools.Field(typeof(NuclearOption.SavedMission.SavedAirbase),
                        "runways") == null)
                    throw new System.Exception("SavedRunway/SavedAirbase поля не найдены");
                if (HarmonyLib.AccessTools.Property(typeof(NetworkManagerNuclearOption),
                        "ServerObjectManager") == null &&
                    HarmonyLib.AccessTools.Field(typeof(NetworkManagerNuclearOption),
                        "ServerObjectManager") == null)
                    throw new System.Exception("ServerObjectManager не найден");
            });
            Check("Верфь/флот (Spawner.SpawnShip, ShipDefinition, seaLanes)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(Spawner), "SpawnShip") == null)
                    throw new System.Exception("Spawner.SpawnShip не найден — верфь не спускает корабли");
                if (HarmonyLib.AccessTools.Field(typeof(ShipDefinition), "shipType") == null)
                    throw new System.Exception("ShipDefinition.shipType не найден");
                if (HarmonyLib.AccessTools.Field(typeof(Encyclopedia), "ships") == null)
                    throw new System.Exception("Encyclopedia.ships не найден — каталог флота пуст");
                if (HarmonyLib.AccessTools.Property(typeof(LevelInfo), "seaLanes") == null ||
                    HarmonyLib.AccessTools.Method(typeof(RoadPathfinding.RoadNetwork), "Exists") == null)
                    throw new System.Exception("LevelInfo.seaLanes/RoadNetwork.Exists не найдены");
                if (HarmonyLib.AccessTools.Method(typeof(Ship), "SetHoldPosition") == null)
                    throw new System.Exception("Ship.SetHoldPosition не найден — якорь морского инженера мёртв");
                if (!System.Enum.IsDefined(typeof(ShipType), ShipType.LC))
                    throw new System.Exception("ShipType.LC не найден — фильтр морского инженера мёртв");
            });
            Check("Контрмеры/ракетное предупреждение (авто-защита)", () =>
            {
                foreach (string name in new[] { "DeployCountermeasure", "ChooseCountermeasure", "PopFlares" })
                    if (HarmonyLib.AccessTools.Method(typeof(CountermeasureManager), name) == null)
                        throw new System.Exception("CountermeasureManager." + name + " не найден");
                foreach (string name in new[] { "IsWarning", "TryGetNearestIncoming" })
                    if (HarmonyLib.AccessTools.Method(typeof(MissileWarning), name) == null)
                        throw new System.Exception("MissileWarning." + name + " не найден");
                if (HarmonyLib.AccessTools.Method(typeof(Aircraft), "GetMissileWarningSystem") == null)
                    throw new System.Exception("Aircraft.GetMissileWarningSystem не найден");
                if (HarmonyLib.AccessTools.Field(typeof(Aircraft), "countermeasureManager") == null)
                    throw new System.Exception("Aircraft.countermeasureManager не найден");
            });
            Check("Залп: WeaponManager/CombatAI (нативный SalvoFire)", () =>
            {
                foreach (string name in new[]
                         {
                             "Fire", "GetTargetList", "TargetListChanged", "SetActiveStation", "SalvoFire"
                         })
                    if (HarmonyLib.AccessTools.Method(typeof(WeaponManager), name,
                            new[] { typeof(List<Unit>), typeof(float) }) == null &&
                        HarmonyLib.AccessTools.Method(typeof(WeaponManager), name) == null)
                        throw new System.Exception("WeaponManager." + name + " не найден");
                if (HarmonyLib.AccessTools.Method(typeof(CombatAI), "LookForMissileTargets") == null)
                    throw new System.Exception("CombatAI.LookForMissileTargets не найден");
                foreach (string name in new[] { "LaunchMount", "Ready", "SafetyIsOn" })
                    if (HarmonyLib.AccessTools.Method(typeof(WeaponStation), name) == null)
                        throw new System.Exception("WeaponStation." + name + " не найден");
                if (HarmonyLib.AccessTools.Method(typeof(WeaponInfo), "CalcAttacksNeeded") == null)
                    throw new System.Exception("WeaponInfo.CalcAttacksNeeded не найден");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponStation), "Number") == null)
                    throw new System.Exception("WeaponStation.Number не найден (SetActiveStation)");
            });
            Check("Режимы скорости: тротл руления", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(ControlInputs), "throttle") == null)
                    throw new System.Exception("ControlInputs.throttle не найден");
                if (HarmonyLib.AccessTools.Field(typeof(AircraftParameters), "cruiseThrottle") == null)
                    throw new System.Exception("AircraftParameters.cruiseThrottle не найден");
            });
            Check("Прямой маршрут наземки (PathfindingAgent)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(PathfindingAgent), "Pathfind") == null)
                    throw new System.Exception("PathfindingAgent.Pathfind не найден — патч маршрута мёртв");
                if (HarmonyLib.AccessTools.Method(typeof(PathfindingAgent), "Shortcut") == null)
                    throw new System.Exception("PathfindingAgent.Shortcut не найден");
                // Две перегрузки (GlobalPosition/Vector3) — ищем ТУ, что зовёт мод
                // (GroundPathfindPatch.CrossesWater); поиск по одному имени кидал
                // AmbiguousMatchException после игрового апдейта.
                if (HarmonyLib.AccessTools.Method(typeof(PathfindingAgent), "RaycastTerrain",
                        new[] { typeof(Vector3), typeof(RaycastHit).MakeByRefType() }) == null)
                    throw new System.Exception("PathfindingAgent.RaycastTerrain не найден (проверка воды)");
                if (!GroundPathfindPatch.FieldsOk)
                    throw new System.Exception("PathfindingAgent.unit не найден (фильтр наземки)");
            });
            // Сводка вооружения группы в инфопанели: домен-теги читают effectiveness
            // (RoleIdentity игры), фильтр груза — Cargo/cargo/troops станции.
            Check("Сводка вооружения группы (effectiveness/груз)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(WeaponInfo), "effectiveness") == null)
                    throw new System.Exception("WeaponInfo.effectiveness не найден (домен-теги)");
                foreach (string name in new[] { "cargo", "troops", "nuclear", "jammer" })
                    if (HarmonyLib.AccessTools.Field(typeof(WeaponInfo), name) == null)
                        throw new System.Exception("WeaponInfo." + name + " не найден");
                foreach (string name in new[] { "Cargo", "FullAmmo", "Ammo" })
                    if (HarmonyLib.AccessTools.Field(typeof(WeaponStation), name) == null)
                        throw new System.Exception("WeaponStation." + name + " не найден");
            });
            Check("Гейты вооружения (классификация/блок Ready)", () =>
            {
                foreach (string name in new[] { "bomb", "glideBomb", "gun", "missile" })
                    if (HarmonyLib.AccessTools.Field(typeof(WeaponInfo), name) == null)
                        throw new System.Exception("WeaponInfo." + name + " не найден (классы гейтов)");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponStation), "LastFiredTime") == null)
                    throw new System.Exception("WeaponStation.LastFiredTime не найден (блок Ready)");
                if (HarmonyLib.AccessTools.Method(typeof(Turret), "GetWeaponStation") == null)
                    throw new System.Exception("Turret.GetWeaponStation не найден (гейт турели)");
            });
            Check("Генератор сценариев (Mission/objectives/SaveLoad)", () =>
            {
                var probe = new NuclearOption.SavedMission.Mission("SN-probe", skipAfterLoad: true);
                if (probe.objectives == null || probe.outcomes == null || probe.objectives.Count == 0)
                    throw new System.Exception("Mission-конструктор не создал objectives/outcomes");
                var obj = NuclearOption.SavedMission.SavedObjective.CreateSavedObjective(
                    ObjectiveType.CaptureAirbase, "SN-probe-cap");
                if (!(obj is CaptureAirbaseSavedObjective cap) || cap.targetAirbases == null)
                    throw new System.Exception("CaptureAirbaseSavedObjective не создан");
                var outcome = NuclearOption.SavedMission.SavedOutcome.CreateSaved(
                    OutcomeType.EndGame, "SN-probe-end");
                if (!(outcome is EndGameSavedOutcome end) ||
                    !System.Enum.IsDefined(typeof(EndType), end.endType))
                    throw new System.Exception("EndGameSavedOutcome не создан");
                if (string.IsNullOrEmpty(MissionTag.SinglePlayer.Tag))
                    throw new System.Exception("MissionTag.SinglePlayer пуст");
            });
            Check("Спец-карты (MapSettings/MapLoader/BattlefieldGrid)", () =>
            {
                foreach (string field in new[] { "NetworkMap", "MapSize", "TerrainColorMap",
                     "OceanBasecolor", "OceanDepthmap", "MapImage", "CameraPositionRotation",
                     "RoadNetwork", "SeaLanes" })
                    if (HarmonyLib.AccessTools.Field(typeof(MapSettings), field) == null)
                        throw new System.Exception("MapSettings." + field + " не найден (спец-карты)");
                foreach (string field in new[] { "Maps", "DefaultMap" })
                    if (HarmonyLib.AccessTools.Field(typeof(NuclearOption.SceneLoading.MapLoader), field) == null)
                        throw new System.Exception("MapLoader." + field + " не найден (регистрация спец-карт)");
                if (HarmonyLib.AccessTools.Method(typeof(BattlefieldGrid), "GenerateGrid") == null)
                    throw new System.Exception("BattlefieldGrid.GenerateGrid не найден (размер спец-карты)");
                if (HarmonyLib.AccessTools.Field(typeof(FactionRegistry), "HQLookup") == null)
                    throw new System.Exception("FactionRegistry.HQLookup не найден (спавн HQ)");
                // AI-командир (v1): фракция без игроков = HQ с пустым factionPlayers.
                if (HarmonyLib.AccessTools.Field(typeof(FactionHQ), "factionPlayers") == null)
                    throw new System.Exception("FactionHQ.factionPlayers не найден (распознавание AI-фракций)");
                // Месторождения v3: скрытие маркера-«недр» застройкой (сетевой despawn
                // без обломка) и смена владельца постройки при инженерном захвате.
                if (HarmonyLib.AccessTools.Method(typeof(Unit), nameof(Unit.DisableUnit)) == null)
                    throw new System.Exception("Unit.DisableUnit не найден (скрытие маркера точки)");
                if (HarmonyLib.AccessTools.Field(typeof(Unit), "NetworkHQ") == null &&
                    HarmonyLib.AccessTools.Property(typeof(Unit), "NetworkHQ") == null)
                    throw new System.Exception("Unit.NetworkHQ не найден (инженерный захват)");
                // Текстуры v3: сток-материал террейна (нативный вид рельефа спец-карты,
                // матч IsTerrain по ссылке — трава/воронки).
                if (HarmonyLib.AccessTools.Field(typeof(NuclearOption.Effects.TerrainHeightMap),
                        "terrainMaterials") == null)
                    throw new System.Exception("TerrainHeightMap.terrainMaterials не найден (нативный материал рельефа)");
            });
            Check("Скримиш (кнопка меню + прямой старт миссии)", () =>
            {
                // Кнопка «Скримиш» — клон нативной missionsButton (рефлексия).
                if (HarmonyLib.AccessTools.Field(typeof(MainMenu), "missionsButton") == null)
                    throw new System.Exception("MainMenu.missionsButton не найден (кнопка Скримиша)");
                // Загрузка сейва и запуск: MissionKey(name, группа) + TryLoad —
                // нативный путь MissionsPicker; группа — СВОЙСТВО MissionGroup.User.
                if (HarmonyLib.AccessTools.Property(typeof(NuclearOption.SavedMission.MissionGroup), "User") == null)
                    throw new System.Exception("MissionGroup.User не найден (загрузка сейва Скримиша)");
                var keyCtor = HarmonyLib.AccessTools.Constructor(
                    typeof(NuclearOption.SavedMission.MissionKey),
                    new[] { typeof(string), typeof(NuclearOption.SavedMission.MissionGroup) });
                if (keyCtor == null)
                    throw new System.Exception("MissionKey(name, group) не найден");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.SavedMission.MissionKey), "TryLoad") == null)
                    throw new System.Exception("MissionKey.TryLoad не найден");
                if (HarmonyLib.AccessTools.Method(typeof(MissionManager), "SetMission") == null)
                    throw new System.Exception("MissionManager.SetMission не найден (нативный старт)");
                var host = new NuclearOption.Networking.HostOptions(
                    NuclearOption.Networking.SocketType.Offline, GameState.SinglePlayer,
                    new NuclearOption.SceneLoading.MapKey());
                if (host.GameState != GameState.SinglePlayer)
                    throw new System.Exception("HostOptions(Offline, SinglePlayer, map) не собрался");
                if (NuclearOption.SavedMission.MissionTag.Multiplayer == null)
                    throw new System.Exception("MissionTag.Multiplayer пуст (MP-пикер нативного лобби)");
                // Персистентный слушатель сцены на клоне кнопки гасится ПОДМЕНОЙ
                // события целиком (поле m_OnClick новым ButtonClickedEvent) —
                // рефлексивный Clear() PersistentCallGroup на этой сборке НЕ работает.
                if (HarmonyLib.AccessTools.Field(typeof(UnityEngine.UI.Button), "m_OnClick") == null)
                    throw new System.Exception("Button.m_OnClick не найден (кнопка Скримиша)");
            });
            // MapGen-дрессинг (v0.154): приватные поля, на которых стоят деревья
            // (патч №14 подменяет буфер позиций) и воронки (DetailRenderer.Start
            // читает массивы и blastManager) — правка игры молча убьёт их иначе.
            Check("MapGen: TreeRenderer/DetailRenderer поля (деревья, шрамы)", () =>
            {
                foreach (string f in new[] { "mesh", "material", "viewRadius", "meshRadius",
                             "clearTreeRadiusMultiplier", "treeColorCompute", "clearShader",
                             "treePositionBuffer" })
                    if (HarmonyLib.AccessTools.Field(typeof(NuclearOption.Effects.TreeRenderer), f) == null)
                        throw new System.Exception("TreeRenderer." + f + " не найден — деревья/патч №14 мертвы");
                if (HarmonyLib.AccessTools.Field(typeof(NuclearOption.Effects.DetailRenderer), "treeRenderers") == null ||
                    HarmonyLib.AccessTools.Field(typeof(NuclearOption.Effects.DetailRenderer), "blastManager") == null)
                    throw new System.Exception("DetailRenderer.treeRenderers/blastManager не найдены — деревья/воронки мертвы");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.Effects.BlastManager), "AddBlast") == null)
                    throw new System.Exception("BlastManager.AddBlast не найден — шрамы мертвы");
            });
            // MP-лобби Ур.2 (v0.154): реплика нативного CreateLobbyModal.HostLobby
            // + фаза лобби (патч №14) + занятие сторон (нативный SetFaction).
            Check("MP-лобби: SteamLobby/StartHostAsync/SetFaction/preventJoin (Ур.2)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.Networking.Lobbies.SteamLobby),
                        "HostLobby") == null)
                    throw new System.Exception("SteamLobby.HostLobby не найден (хостинг лобби)");
                if (HarmonyLib.AccessTools.Field(typeof(NuclearOption.Networking.Lobbies.SteamLobby),
                        "instance") == null)
                    throw new System.Exception("SteamLobby.instance не найден (менеджер лобби)");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.Networking.NetworkManagerNuclearOption),
                        "SetModdedServer") == null)
                    throw new System.Exception("SetModdedServer не найден (тег браузера)");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.Networking.NetworkManagerNuclearOption),
                        "StartHostAsync") == null)
                    throw new System.Exception("StartHostAsync не найден (MP-хост)");
                var mpHost = new NuclearOption.Networking.HostOptions(
                    NuclearOption.Networking.SocketType.Steam, GameState.Multiplayer,
                    new NuclearOption.SceneLoading.MapKey())
                {
                    MaxConnections = 7,
                    Password = "probe",
                };
                if (mpHost.MaxConnections != 7 || mpHost.Password != "probe")
                    throw new System.Exception("HostOptions MaxConnections/Password не пишутся (Steam, Multiplayer)");
                if (HarmonyLib.AccessTools.Method(typeof(MissionManager), "StartMission") == null)
                    throw new System.Exception("MissionManager.StartMission не найден (патч №14 — фаза лобби)");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.Networking.Player), "SetFaction") == null)
                    throw new System.Exception("Player.SetFaction не найден (занятие стороны)");
                if (HarmonyLib.AccessTools.Property(typeof(FactionHQ), "NetworkpreventJoin") == null)
                    throw new System.Exception("FactionHQ.NetworkpreventJoin не найден (закрытие стороны)");
                if (HarmonyLib.AccessTools.Method(typeof(FactionRegistry), "GetAllHQs") == null)
                    throw new System.Exception("FactionRegistry.GetAllHQs не найден (ростер сторон)");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.Networking.Lobbies.HostedLobbyInstance),
                        "SetData") == null)
                    throw new System.Exception("HostedLobbyInstance.SetData не найден (sn_lobby-данные)");
                // GetPvpTypeLobbyString перегружен с обновления игры 2026-11-07
                // ((Mission)/(MissionPvpType)): AccessTools.Method по имени без
                // параметров бросает AmbiguousMatchException («Ambiguous match in
                // Harmony patch for …») — проверяем обе перегрузки явными типами
                // (GetMethod с параметрами не бросает).
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.SavedMission.MissionTag),
                        "GetPvpTypeLobbyString", new[] { typeof(NuclearOption.SavedMission.Mission) }) == null &&
                    HarmonyLib.AccessTools.Method(typeof(NuclearOption.SavedMission.MissionTag),
                        "GetPvpTypeLobbyString", new[] { typeof(NuclearOption.Networking.Lobbies.MissionPvpType) }) == null)
                    throw new System.Exception("MissionTag.GetPvpTypeLobbyString не найден (данные лобби)");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.Networking.Lobbies.SteamLobby),
                        "CheckRelayLocationTask") == null)
                    throw new System.Exception("CheckRelayLocationTask не найден (пинг хоста в браузере)");
                // Лобби v2 (комната в EMPTY): Mirage-сообщение мода (Writer/Reader —
                // публичные статики, регистрируемые вивером игре; мы повторяем в рантайме).
                if (HarmonyLib.AccessTools.Property(typeof(Mirage.Serialization.Writer<SnLobbyMessage>), "Write") == null)
                    throw new System.Exception("Mirage Writer<T>.Write недоступен (кастомное сообщение лобби)");
                if (HarmonyLib.AccessTools.Property(typeof(Mirage.Serialization.Reader<SnLobbyMessage>), "Read") == null)
                    throw new System.Exception("Mirage Reader<T>.Read недоступен (кастомное сообщение лобби)");
                if (HarmonyLib.AccessTools.Method(typeof(Mirage.Serialization.MessagePacker), "RegisterMessage") == null)
                    throw new System.Exception("MessagePacker.RegisterMessage недоступен (id сообщения лобби)");
                // Рецепт LoadNext (DedicatedServerManager): комната без карты → карта на СТАРТ.
                if ((int)GameState.ServerWaiting != 2 && (int)GameState.ServerWaiting < 0)
                    throw new System.Exception("GameState.ServerWaiting отсутствует");
                var sysHost = new NuclearOption.Networking.HostOptions(
                    NuclearOption.Networking.SocketType.Steam, GameState.ServerWaiting,
                    NuclearOption.SceneLoading.MapLoader.Empty);
                if (sysHost.GameState != GameState.ServerWaiting)
                    throw new System.Exception("HostOptions(ServerWaiting, SystemScene) не собрался");
                if (HarmonyLib.AccessTools.Method(typeof(NuclearOption.SceneLoading.MapLoader), "LoadEmpty") == null)
                    throw new System.Exception("MapLoader.LoadEmpty не найден (сцена EMPTY создаётся рантаймом)");
                foreach (string m in new[] { "SendLoadWaitingMessage", "SendServerLoadingMessage",
                             "SendLoadMapMessageAll", "Host_SceneLoaded" })
                    if (HarmonyLib.AccessTools.Method(typeof(NetworkManagerNuclearOption), m) == null)
                        throw new System.Exception("NetworkManagerNuclearOption." + m + " не найден (рецепт LoadNext)");
                if (HarmonyLib.AccessTools.Method(typeof(NetworkManagerNuclearOption), "ServerLoadMapScene") == null)
                    throw new System.Exception("ServerLoadMapScene не найден (загрузка карты на СТАРТ)");
                if (HarmonyLib.AccessTools.Property(typeof(NetworkManagerNuclearOption), "ServerObjectManager") == null &&
                    HarmonyLib.AccessTools.Field(typeof(NetworkManagerNuclearOption), "ServerObjectManager") == null)
                    throw new System.Exception("ServerObjectManager недоступен (снос EMPTY-персонажа хоста)");
                if (HarmonyLib.AccessTools.Method(typeof(NetworkManagerNuclearOption), "SpawnCharacter") == null)
                    throw new System.Exception("SpawnCharacter не найден (патч №16 — фракция по плану)");
                // Server/Client — УНАСЛЕДОВАННЫЕ члены Mirage-базы (поля, не
                // свойства): AccessTools по производному типу их НЕ находит
                // (FlattenHierarchy тянет только статику) — поиск Property/Field
                // по цепочке баз (плейтест 2026-11-07: Property-only не нашёл).
                System.Reflection.MemberInfo InstanceMember(System.Type t, string name)
                {
                    for (var b = t; b != null; b = b.BaseType)
                    {
                        var p = HarmonyLib.AccessTools.Property(b, name);
                        if (p != null)
                            return p;
                        var f = HarmonyLib.AccessTools.Field(b, name);
                        if (f != null)
                            return f;
                    }
                    return null;
                }
                if (InstanceMember(typeof(NetworkManagerNuclearOption), "Server") == null ||
                    InstanceMember(typeof(NetworkManagerNuclearOption), "Client") == null)
                    throw new System.Exception("Server/Client недоступны (подписки лобби)");
                // Лобби v3: рантайм-фракции N армий + командная победа.
                if (HarmonyLib.AccessTools.Property(typeof(Faction), "factionName") == null &&
                    HarmonyLib.AccessTools.Field(typeof(Faction), "factionName") == null)
                    throw new System.Exception("Faction.factionName недоступен (рантайм-фракции армий)");
                if (HarmonyLib.AccessTools.Property(typeof(Faction), "color") == null &&
                    HarmonyLib.AccessTools.Field(typeof(Faction), "color") == null)
                    throw new System.Exception("Faction.color недоступен (цвет-чип армии)");
                if (HarmonyLib.AccessTools.Method(typeof(FactionHQ), "DeclareEndGame") == null)
                    throw new System.Exception("FactionHQ.DeclareEndGame не найден (командная победа/уничтожение)");
                if (HarmonyLib.AccessTools.Property(typeof(FactionHQ), "factionUnits") == null)
                    throw new System.Exception("FactionHQ.factionUnits недоступен (проверка уничтожения)");
                if (HarmonyLib.AccessTools.Property(typeof(Unit), "NetworkHQ") == null)
                    throw new System.Exception("Unit.NetworkHQ недоступен (союзный гейт оружия)");
                if (HarmonyLib.AccessTools.Property(typeof(NetworkManagerNuclearOption), "SetModdedServer") != null &&
                    HarmonyLib.AccessTools.Method(typeof(NetworkManagerNuclearOption), "SetModdedServer") == null)
                    throw new System.Exception("SetModdedServer пропал (инстанс-метод)");
            });
            // Сплат-бандл: раннее eager-подхват (правило 13 — проверка предположения
            // «бандл читается рантаймом ЭТОЙ ревизии»): диагноз в лог сразу, в меню,
            // а не молча в момент первой карты. Неготовность — НЕ провал (фолбэк).
            Check("MapGen: сплат-бандл (eager, диагностика)", () =>
            {
                bool ready = TerrainSplat.Ready;
                Log.Info("SelfCheck: сплат " + (ready ? "ГОТОВ" : "недоступен — гибрид"));
            });
            Log.Info("SelfCheck(startup): " + (Failures.Count == 0 ? "все проверки пройдены" : "ПРОВАЛОВ: " + Failures.Count));
        }

        public static void RunActivation()
        {
            // Стоковая дека́ль дрессинга карт (v0.154): GameAssets к миссии
            // загружен — DecalSpawner работает по тому же полю.
            Check("GameAssets.scorchMarkDecal (декали карт)", () =>
            {
                var assets = GameAssets.i;
                if (assets == null || assets.scorchMarkDecal == null)
                    throw new System.Exception("prefab выжженной декали отсутствует");
                if (assets.scorchMarkDecal
                        .GetComponent<UnityEngine.Rendering.Universal.DecalProjector>() == null)
                    throw new System.Exception("на scorchMarkDecal нет DecalProjector — декали карт мертвы");
            });
            Check("CameraStateManager в сцене", () =>
            {
                if (GameApi.CamManager == null) throw new System.Exception("singleton отсутствует");
            });
            Check("CameraStateManager.mainCamera", () =>
            {
                if (GameApi.MainCam == null) throw new System.Exception("камера отсутствует");
            });
            Check("CameraStateManager.freeState", () =>
            {
                if (GameApi.CamManager.freeState == null) throw new System.Exception("freeState == null");
            });
            Check("GameManager.GetLocalHQ", () =>
            {
                if (!GameManager.GetLocalHQ(out _)) throw new System.Exception("локальная фракция не найдена");
            });
            // Туман войны гасит отрисовку через Renderer/Light enabled — проверяем,
            // что юниты действительно рендерятся компонентами на детях (правило №13).
            Check("Renderer на детях юнита (туман войны)", () =>
            {
                int withRenderers = 0;
                foreach (Unit unit in UnitRegistry.allUnits)
                {
                    if (unit == null)
                        continue;
                    unit.GetComponentsInChildren(true, FogProbe);
                    if (FogProbe.Count > 0)
                        withRenderers++;
                    FogProbe.Clear();
                }
                if (UnitRegistry.allUnits.Count > 0 && withRenderers == 0)
                    throw new System.Exception("ни у одного юнита нет Renderer — скрытие моделей туманом мертво");
            });
            // Гашение Доплера в режиме командира (CommanderAudio) работает пер-источниковым
            // dopplerLevel: глобальный AudioSettings.dopplerFactor из этой версии Unity
            // вырезан. Проба на живом источнике: обнуление должно читаться обратно.
            Check("AudioSource.dopplerLevel (гашение Доплера)", () =>
            {
                AudioSource[] sources = Object.FindObjectsOfType<AudioSource>();
                if (sources.Length == 0)
                    throw new System.Exception("в сцене нет источников звука");
                AudioSource probe = sources[0];
                float saved = probe.dopplerLevel;
                probe.dopplerLevel = 0f;
                bool ok = probe.dopplerLevel == 0f;
                probe.dopplerLevel = saved;
                if (!ok) throw new System.Exception("движок не принял 0");
            });
            // Карточка осмотра ракет/бомб (ЛКМ по видимой цели): методы Missile,
            // без которых панель молча деградирует или кидает NRE.
            Check("Missile API (карточка осмотра)", () =>
            {
                foreach (string name in new[]
                {
                    "GetWeaponInfo", "EngineOn", "GetSeekerType", "GetYield",
                    "GetThrustDuration", "GetRemainingBurnTime"
                })
                    if (HarmonyLib.AccessTools.Method(typeof(Missile), name) == null)
                        throw new System.Exception("Missile." + name + " не найден");
                if (HarmonyLib.AccessTools.Property(typeof(Missile), "targetID") == null)
                    throw new System.Exception("Missile.targetID не найден");
                if (HarmonyLib.AccessTools.Method(typeof(GlobalPositionExtensions), "GlobalY") == null)
                    throw new System.Exception("Vector3.GlobalY не найден");
            });
            // Контекстность командной панели и руление по типам бортов (v0.92):
            // автопилот назначается каждому Aircraft в Awake, и его ТИП задаёт
            // семейство (AutopilotHelo/Tiltwing — вертолётное: высота всегда AGL,
            // скоростные режимы неприменимы). Проба на живых бортах.
            Check("Aircraft.autopilot (типы бортов, контекст панели)", () =>
            {
                Aircraft[] live = Object.FindObjectsOfType<Aircraft>();
                int helos = 0, planes = 0, missing = 0;
                foreach (Aircraft a in live)
                {
                    if (a == null || a.disabled)
                        continue;
                    if (a.autopilot == null)
                    {
                        missing++;
                        continue;
                    }
                    if (GameApi.IsHeloFamily(a))
                        helos++;
                    else
                        planes++;
                }
                if (missing > 0)
                    throw new System.Exception("живых бортов без автопилота: " + missing);
                Log.Info("  [INFO]  бортов в сцене: самолётов " + planes +
                         ", вертолётного семейства " + helos);
            });
            // Разведка этапа «достройка ангаров»: есть ли здания-ангары в каталоге
            // (префаб с компонентом Hangar). Это не гейт — просто фиксируем факт в логе.
            Check("Encyclopedia.buildings: здания-ангары (разведка)", () =>
            {
                Encyclopedia encyclopedia = Encyclopedia.i;
                if (encyclopedia == null || encyclopedia.buildings == null)
                    throw new System.Exception("Encyclopedia недоступна");
                int hangarDefs = 0;
                var names = new System.Text.StringBuilder();
                foreach (BuildingDefinition def in encyclopedia.buildings)
                    if (def != null && def.unitPrefab != null &&
                        def.unitPrefab.GetComponent<Hangar>() != null)
                    {
                        hangarDefs++;
                        names.Append(def.unitName).Append(" | ");
                    }
                Log.Info("  [INFO]  зданий-ангаров в Encyclopedia: " + hangarDefs +
                         " из " + encyclopedia.buildings.Count + " — " + names);
            });
            // Разведка флота (верфь/авианосцы, не гейт): какие ShipDefinition есть
            // в контенте и что несут их префабы — Airbase+Runway(Arrestor/SkiJump)
            // +Hangar = палубный авианосец, UnitStorage = десантный док; полный
            // список зданий (из чего строить верфь) и наличие морского графа
            // (без seaLanes верфь не сможет ни спавнить, ни водить корабли).
            Check("Флот: корабли/здания/seaLanes (разведка)", () =>
            {
                Encyclopedia encyclopedia = Encyclopedia.i;
                if (encyclopedia == null)
                    throw new System.Exception("Encyclopedia недоступна");
                int ships = 0;
                var shipInfo = new System.Text.StringBuilder();
                if (encyclopedia.ships != null)
                    foreach (ShipDefinition def in encyclopedia.ships)
                    {
                        if (def == null || def.unitPrefab == null)
                            continue;
                        ships++;
                        shipInfo.Append(def.unitName).Append(" (")
                                .Append(def.shipType).Append(", $").Append(def.value);
                        Airbase deck = def.unitPrefab.GetComponent<Airbase>();
                        if (deck != null)
                        {
                            shipInfo.Append(", ПАЛУБА");
                            if (deck.runways != null)
                                foreach (Airbase.Runway r in deck.runways)
                                    shipInfo.Append(r.Arrestor ? " финишер" : "")
                                            .Append(r.SkiJump ? " трамплин" : "");
                            shipInfo.Append(" анг:")
                                    .Append(def.unitPrefab.GetComponentsInChildren<Hangar>(true).Length)
                                    .Append(" втол:")
                                    .Append(deck.verticalLandingPoints != null
                                        ? deck.verticalLandingPoints.Length : 0);
                        }
                        if (def.unitPrefab.GetComponent<UnitStorage>() != null)
                            shipInfo.Append(", ДОК");
                        shipInfo.Append(") | ");
                    }
                Log.Info("  [INFO]  кораблей в Encyclopedia: " + ships + " — " + shipInfo);
                var buildingInfo = new System.Text.StringBuilder();
                int withFactory = 0;
                if (encyclopedia.buildings != null)
                    foreach (BuildingDefinition def in encyclopedia.buildings)
                    {
                        if (def == null || def.unitPrefab == null)
                            continue;
                        buildingInfo.Append(def.unitName);
                        if (def.unitPrefab.GetComponent<Factory>() != null)
                        {
                            buildingInfo.Append("(Factory)");
                            withFactory++;
                        }
                        if (def.unitPrefab.GetComponent<VehicleDepot>() != null)
                            buildingInfo.Append("(Depot)");
                        buildingInfo.Append(" | ");
                    }
                Log.Info("  [INFO]  здания (" + withFactory + " с Factory): " + buildingInfo);
                LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
                RoadPathfinding.RoadNetwork seaLanes = level != null ? level.seaLanes : null;
                Log.Info("  [INFO]  seaLanes: " +
                         (seaLanes != null && seaLanes.Exists()
                             ? "есть, дорог " + (seaLanes.roads != null ? seaLanes.roads.Count : 0)
                             : "НЕТ — верфь на этой карте невозможна"));
            });
            // Кольца дальности (RangeRings): поверхность дальностей/сенсоров.
            Check("RangeRings API (дальности/сенсоры)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(Unit), "weaponStations") == null)
                    throw new System.Exception("Unit.weaponStations не найден");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponStation), "WeaponInfo") == null)
                    throw new System.Exception("WeaponStation.WeaponInfo не найден");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponInfo), "targetRequirements") == null)
                    throw new System.Exception("WeaponInfo.targetRequirements не найден");
                if (HarmonyLib.AccessTools.Method(typeof(TargetDetector), "GetVisualRange") == null)
                    throw new System.Exception("TargetDetector.GetVisualRange не найден");
                if (HarmonyLib.AccessTools.Method(typeof(Radar), "GetRadarRange") == null)
                    throw new System.Exception("Radar.GetRadarRange не найден");
            });
            // Hover-карточка определения (SupCom-инфо у плитки покупки): вооружение
            // читается с префаба определения (Weapon.info/ammo сериализованы вместе
            // с дефолтной нагрузкой), скорость авиации — aircraftInfo. Провал —
            // карточка молча теряет строки ТТХ/вооружения.
            Check("Hover-карточка API (оружие/ТТХ определения)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(Weapon), "info") == null)
                    throw new System.Exception("Weapon.info не найден");
                if (HarmonyLib.AccessTools.Field(typeof(Weapon), "ammo") == null)
                    throw new System.Exception("Weapon.ammo не найден");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponInfo), "hideInDisplay") == null)
                    throw new System.Exception("WeaponInfo.hideInDisplay не найден");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponInfo), "shortName") == null)
                    throw new System.Exception("WeaponInfo.shortName не найден");
                if (HarmonyLib.AccessTools.Field(typeof(AircraftDefinition), "aircraftInfo") == null)
                    throw new System.Exception("AircraftDefinition.aircraftInfo не найден");
                if (HarmonyLib.AccessTools.Field(typeof(UnitDefinition), "visibleRange") == null)
                    throw new System.Exception("UnitDefinition.visibleRange не найден");
                // Живая проба: хоть у одного боевого определения энциклопедии префаб
                // несёт Weapon с заполненным info (иначе карточка без вооружения).
                Encyclopedia encyclopedia = Encyclopedia.i;
                if (encyclopedia == null)
                    throw new System.Exception("Encyclopedia недоступна");
                int armed = 0;
                bool HasArmedWeapon(GameObject prefab)
                {
                    foreach (Weapon w in prefab.GetComponentsInChildren<Weapon>(true))
                        if (w != null && w.info != null)
                            return true;
                    return false;
                }
                if (encyclopedia.vehicles != null)
                    foreach (VehicleDefinition def in encyclopedia.vehicles)
                        if (def != null && def.unitPrefab != null && HasArmedWeapon(def.unitPrefab))
                            armed++;
                if (encyclopedia.aircraft != null)
                    foreach (AircraftDefinition def in encyclopedia.aircraft)
                        if (def != null && def.unitPrefab != null && HasArmedWeapon(def.unitPrefab))
                            armed++;
                if (armed == 0)
                    throw new System.Exception("ни у одного определения нет Weapon.info на префабе");
                Log.Info("  [INFO]  определений с оружием на префабе: " + armed);
            });
            // Лодауты заказов (колесо по каталогу): пресеты StandardLoadouts,
            // состав Loadout.weapons, хардпоинты WeaponManager с префаба и
            // проверка доступности WeaponChecker. Провал — заказы всегда «Стандарт».
            Check("Лодауты API (пресеты/хардпоинты/WeaponChecker)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(AircraftParameters), "StandardLoadouts") == null)
                    throw new System.Exception("AircraftParameters.StandardLoadouts не найден");
                foreach (string f in new[] { "Name", "loadout", "FuelRatio", "disabled" })
                    if (HarmonyLib.AccessTools.Field(typeof(StandardLoadout), f) == null)
                        throw new System.Exception("StandardLoadout." + f + " не найден");
                if (HarmonyLib.AccessTools.Field(typeof(Loadout), "weapons") == null)
                    throw new System.Exception("Loadout.weapons не найден");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponMount), "ammo") == null)
                    throw new System.Exception("WeaponMount.ammo не найден");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponManager), "hardpointSets") == null)
                    throw new System.Exception("WeaponManager.hardpointSets не найден");
                if (HarmonyLib.AccessTools.Field(typeof(HardpointSet), "hardpoints") == null)
                    throw new System.Exception("HardpointSet.hardpoints не найден");
                if (HarmonyLib.AccessTools.Method(typeof(WeaponChecker), "GetAvailableWeaponsNonAlloc") == null)
                    throw new System.Exception("WeaponChecker.GetAvailableWeaponsNonAlloc не найден");
                if (HarmonyLib.AccessTools.Field(typeof(Hangar), "parentAirbase") == null)
                    throw new System.Exception("Hangar.parentAirbase не найден");
                if (HarmonyLib.AccessTools.Field(typeof(Hangar), "spawnedObject") == null)
                    throw new System.Exception("Hangar.spawnedObject не найден — ралли ангара не выдастся борту");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponInfo), "weaponIcon") == null)
                    throw new System.Exception("WeaponInfo.weaponIcon не найден");
                if (HarmonyLib.AccessTools.Field(typeof(WeaponMount), "info") == null)
                    throw new System.Exception("WeaponMount.info не найден");
                if (HarmonyLib.AccessTools.Field(typeof(Hardpoint), "HardpointIndex") == null)
                    throw new System.Exception("Hardpoint.HardpointIndex не найден");
                // Живая проба: у скольких бортов есть именованные пресеты (и
                // читаются ли хардпоинты с префаба — множитель боезапаса).
                Encyclopedia encyclopedia2 = Encyclopedia.i;
                if (encyclopedia2 == null || encyclopedia2.aircraft == null)
                    throw new System.Exception("Encyclopedia недоступна");
                int withPresets = 0, withHardpoints = 0;
                foreach (AircraftDefinition def in encyclopedia2.aircraft)
                {
                    if (def == null)
                        continue;
                    StandardLoadout[] arr = def.aircraftParameters != null
                        ? def.aircraftParameters.StandardLoadouts
                        : null;
                    int usable = 0;
                    if (arr != null)
                        foreach (StandardLoadout sl in arr)
                            if (sl != null && !sl.disabled && sl.loadout != null)
                                usable++;
                    if (usable > 0)
                        withPresets++;
                    WeaponManager wm = def.unitPrefab != null
                        ? def.unitPrefab.GetComponentInChildren<WeaponManager>(true) : null;
                    if (wm != null && wm.hardpointSets != null && wm.hardpointSets.Length > 0)
                        withHardpoints++;
                }
                Log.Info("  [INFO]  бортов с пресетами лодаутов: " + withPresets +
                         ", с хардпоинтами на префабе: " + withHardpoints +
                         " из " + encyclopedia2.aircraft.Count);
            });
            Check("Победа annihilation: счёт имущества фракции и конец игры", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(FactionHQ), "factionUnits") == null)
                    throw new System.Exception("FactionHQ.factionUnits не найден — счёт имущества фракции мёртв");
                if (HarmonyLib.AccessTools.Method(typeof(FactionHQ), "RegisterFactionUnit") == null ||
                    HarmonyLib.AccessTools.Method(typeof(FactionHQ), "RemoveFactionUnit") == null)
                    throw new System.Exception("FactionHQ.Register/RemoveFactionUnit не найдены — счёт не ведётся нативом");
                if (HarmonyLib.AccessTools.Property(typeof(GameManager), "gameResolution") == null)
                    throw new System.Exception("GameManager.gameResolution не найден — гонка каналов победы не гасится");
                if (HarmonyLib.AccessTools.Method(typeof(FactionHQ), "DeclareEndGame") == null)
                    throw new System.Exception("FactionHQ.DeclareEndGame не найден — поражение по annihilation мертво");
            });
            Log.Info("SelfCheck(activation): " + (Failures.Count == 0 ? "ОК" : "ПРОВАЛОВ: " + Failures.Count));
        }

        private static void Check(string name, System.Action probe)
        {
            try
            {
                probe();
                Log.Info("  [OK]   " + name);
            }
            catch (System.Exception e)
            {
                Log.Error("  [FAIL] " + name + ": " + e.Message);
                if (!Failures.Contains(name))
                    Failures.Add(name);
            }
        }
    }
}
