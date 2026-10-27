using System.Collections.Generic;
using NuclearOption.MissionEditorScripts;
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
            Check("Spawner.SpawnBuilding + unitPrefab (достройка ангаров)", () =>
            {
                if (HarmonyLib.AccessTools.Method(typeof(Spawner), "SpawnBuilding") == null)
                    throw new System.Exception("Spawner.SpawnBuilding не найден");
                if (HarmonyLib.AccessTools.Field(typeof(UnitDefinition), "unitPrefab") == null)
                    throw new System.Exception("UnitDefinition.unitPrefab не найден");
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
