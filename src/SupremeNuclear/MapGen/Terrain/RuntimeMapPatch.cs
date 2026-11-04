using HarmonyLib;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;

namespace SupremeNuclear
{
    /// <summary>
    /// Патч №12 (реестр — ARCHITECTURE §6): спец-карты с собственным террейном.
    /// (1) Prefix MapSettingsManager.EnableMap — вписывает нашу запись в Maps:
    /// менеджер пересоздаётся при каждой загрузке GameWorld-сцены, разовая правка
    /// списков не живёт; точка входа нативная, композиции нет.
    /// (2) Postfix LevelInfo.ApplyMapSettings — сразу после применения карты:
    /// пересборка BattlefieldGrid под наш размер (сцена запекла старый) и спавн
    /// FactionHQ; MissionManager ждёт LoadedMapSettings по Yield-циклу, поэтому
    /// к его пробуждению HQ уже зарегистрированы.
    /// </summary>
    [HarmonyPatch]
    internal static class RuntimeMapPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MapSettingsManager), nameof(MapSettingsManager.EnableMap))]
        private static void BeforeEnableMap(MapSettingsManager __instance, string mapName)
        {
            if (!RuntimeMap.IsOurKey(mapName))
                return;
            if (RuntimeMap.EnsureRegistered(mapName))
                RuntimeMap.RegisterInManager(__instance, mapName);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
        private static void AfterApplyMapSettings(MapSettings mapSettings)
        {
            if (mapSettings == null || !RuntimeMap.IsOurKey(mapSettings.name))
                return;
            // Пространственная решётка юнитов пеклась в Awake сцены со старым размером
            // карты — под наш (юнитов в этот момент ещё нет, пересоздание безопасно).
            // HQ уже заспавнены нативно: EnableMap → Instantiate → NetworkMap.ServerSpawn.
            BattlefieldGrid.GenerateGrid(mapSettings.MapSize.x, 2000f);
            // Материал рельефа: карта СТРОИТСЯ в сцене редактора (TerrainHeightMap там
            // нет — префаб получает URP-фолбэк, плейтест 2026-11-02 «сток-материал не
            // найден»). Здесь мы уже в GameWorld — меняем фолбэк клона на нативный
            // материал террейна (по ссылке → IsTerrain-матч травы/воронок).
            TerrainMaterial.UpgradeClone(mapSettings);
            // Нативная GPU-трава: наш рельеф — в реестр высот СЦЕНОВОГО
            // TerrainHeightMap (чей реестр DetailRenderer и запекает).
            TerrainGrass.Register(mapSettings);
            // Диагностика «юниты проваливаются» (плейтест 2026-10-29): физика должна
            // видеть Statics-коллайдеры клона; рейкаст — в точке базовой зоны.
            var colliders = mapSettings.GetComponentsInChildren<UnityEngine.MeshCollider>();
            UnityEngine.Bounds bounds = default;
            bool any = false;
            foreach (UnityEngine.MeshCollider c in colliders)
            {
                if (c == null) continue;
                if (!any) { bounds = c.bounds; any = true; }
                else bounds.Encapsulate(c.bounds);
            }
            UnityEngine.Vector3 probe = new GlobalPosition(8000f, 0f, 0f)
                .ToLocalPosition() + UnityEngine.Vector3.up * 10000f;
            bool hit = UnityEngine.Physics.Raycast(probe, UnityEngine.Vector3.down,
                out UnityEngine.RaycastHit rh, 20000f, PhysicsLayers.StaticsMask);
            // ВСЁ в ГЛОБАЛЬНЫХ координатах (world − origin.position): мировые
            // числа после первого OriginShift (стартовая камера карты уводит
            // origin на (0,−2496,24576)) отличаются от высот hm на ровно этот
            // сдвиг — «баг высот при перезагрузке» 2026-11-03 был ЛОЖНОЙ
            // ТРЕВОГОЙ именно из-за печати мировых Y рядом с глобальными.
            UnityEngine.Vector3 op = Datum.originPosition;
            Log.Info($"MapGen: карта включена — коллайдеров {colliders.Length}, bounds " +
                     $"{(any ? bounds.size.x.ToString("F0") + "x" + bounds.size.z.ToString("F0") + "м, y(глоб) " + (bounds.min.y - op.y).ToString("F0") + ".." + (bounds.max.y - op.y).ToString("F0") : "нет")}, " +
                     $"проба (8000,0): {(hit ? "земля(глоб) " + (rh.point.y - op.y).ToString("F0") + " м" : "МИМО")}, " +
                     $"origin {op.x.ToString("F0")},{op.y.ToString("F0")},{op.z.ToString("F0")}, " +
                     $"root карты {mapSettings.transform.position.ToString("F0")}");
            PositionHqAtBases();
        }

        /// <summary>Сценские FactionHQ (запечены в GameWorld, например «BoscaliHQ»)
        /// регистрируются РАНЬШЕ наших объектов карты, а RegisterFaction «первый
        /// побеждает» — из реестра достаётся HQ на стартовой точке сцены (плейтест
        /// 2026-10-29: HQ @ центр карты → площадки AI у центра). Ставим живые HQ
        /// на базы их фракций — у каждого пира свой локальный вызов (позиция HQ
        /// по сети не синхронится, дальше статична).</summary>
        private static void PositionHqAtBases()
        {
            Mission mission = MissionManager.CurrentMission;
            if (mission == null || mission.airbases == null)
                return;
            foreach (SavedAirbase ab in mission.airbases)
            {
                if (ab == null || string.IsNullOrEmpty(ab.faction))
                    continue;
                if (!FactionRegistry.factionLookup.TryGetValue(ab.faction, out Faction faction) ||
                    !FactionRegistry.HQLookup.TryGetValue(faction, out FactionHQ hq) || hq == null)
                    continue;
                UnityEngine.Vector3 target = ab.Center.ToLocalPosition();
                if ((hq.transform.position - target).sqrMagnitude > 4f)
                {
                    hq.transform.position = target;
                    Log.Info($"MapGen: HQ '{ab.faction}' поставлен на базу " +
                             $"{target.x.ToString("F0")},{target.z.ToString("F0")}");
                }
            }
        }
    }

    /// <summary>
    /// Патч №13 (реестр — ARCHITECTURE §6): рестарт из редактора на спец-карте.
    /// Нативный MissionManager.OnStartServer ждёт ТОЛЬКО применения карты и стартует
    /// миссию через пару кадров — при «уже загруженной» карте (AlreadyLoaded, сцена
    /// редактора) Player ещё не заспавнен и ServerMissionStartPlayer падает
    /// «SetRank called when server not active» → вечная загрузка (плейтест 2026-10-29).
    /// Сброс CurrentMap заставляет рестарт перезагрузить сцену честным путём.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayFromEditorPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NuclearOption.MissionEditorScripts.MissionEditor),
            nameof(NuclearOption.MissionEditorScripts.MissionEditor.PlayFromEditor))]
        private static void BeforePlayFromEditor()
        {
            Mission mission = MissionManager.CurrentMission;
            if (!RuntimeMap.IsOurKey(mission?.MapKey.Path))
                return;
            foreach (MapLoader loader in UnityEngine.Resources.FindObjectsOfTypeAll<MapLoader>())
                loader.ClearMap();
            Log.Info("MapGen: CurrentMap сброшен — рестарт перезагрузит спец-карту полным путём");
        }
    }
}
