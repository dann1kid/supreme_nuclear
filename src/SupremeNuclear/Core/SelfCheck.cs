using System.Collections.Generic;
using NuclearOption.MissionEditorScripts;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
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
            });
            Check("FactionHQ.AnyNearAirbase (руление к ближней полосе)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(FactionHQ), nameof(FactionHQ.AnyNearAirbase)) == null)
                    throw new System.Exception("метод AnyNearAirbase не найден — патч мёртв");
                if (HarmonyLib.AccessTools.Field(typeof(FactionHQ), "airbasesUnsorted") == null)
                    throw new System.Exception("поле airbasesUnsorted не найдено — патч не увидит аэродромы");
            });
            Check("Spawner.SpawnBuilding + unitPrefab (достройка ангаров)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(Spawner), "SpawnBuilding") == null)
                    throw new System.Exception("Spawner.SpawnBuilding не найден");
                if (HarmonyLib.AccessTools.Field(typeof(UnitDefinition), "unitPrefab") == null)
                    throw new System.Exception("UnitDefinition.unitPrefab не найден");
            });
            Check("VehicleDepot.spawnTransform (стрелка выхода депо)", () =>
            {
                if (HarmonyLib.AccessTools.Field(typeof(VehicleDepot), "spawnTransform") == null)
                    throw new System.Exception("VehicleDepot.spawnTransform не найден — выход депо берётся по габариту");
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
                if (HarmonyLib.AccessTools.Property(typeof(Unit), "rb") == null)
                    throw new System.Exception("Unit.rb не найден — плавучесть инженера мертва");
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
                if (HarmonyLib.AccessTools.Method(typeof(PathfindingAgent), "RaycastTerrain") == null)
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
            Log.Info("SelfCheck(startup): " + (Failures.Count == 0 ? "все проверки пройдены" : "ПРОВАЛОВ: " + Failures.Count));
        }

        public static void RunActivation()
        {
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
