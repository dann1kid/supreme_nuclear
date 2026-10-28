using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>3D-призраки площадок ПО МОДЕЛИ GROUND CONTROL (проверено в этой игре):
    /// меши префаба рисуются Graphics.DrawMesh — БЕЗ GameObject/компонентов/физики.
    /// Материал — встроенный Hidden/Internal-Colored (всегда в сборке, маджента
    /// невозможна) с альфа-блендингом: полупрозрачный цветной силуэт здания.</summary>
    internal static class BuildGhosts
    {
        private sealed class Part
        {
            public Mesh Mesh;
            public int SubmeshCount;
            public Matrix4x4 Local;
        }

        private static readonly Dictionary<BuildingDefinition, List<Part>> Parts =
            new Dictionary<BuildingDefinition, List<Part>>();

        private static Material _ghostMat;
        private static MaterialPropertyBlock _block;

        private static readonly Color BuildTint = new Color(0.35f, 0.7f, 1f, 0.35f);
        private static readonly Color ValidTint = new Color(0.3f, 1f, 0.45f, 0.4f);
        private static readonly Color BlockedTint = new Color(1f, 0.28f, 0.22f, 0.4f);

        private static bool _broken;

        public static bool Enabled => Settings.Build3dGhosts && !_broken;

        /// <summary>Меши и матрицы «твёрдых» частей префаба (как IsSolidPart у GC:
        /// активные, непрозрачные (renderQueue ≤ 2500) — прокси/эффекты отсеиваются).</summary>
        private static List<Part> GetParts(BuildingDefinition def)
        {
            if (Parts.TryGetValue(def, out List<Part> cached))
                return cached;
            var list = new List<Part>();
            try
            {
                GameObject prefab = def.unitPrefab;
                if (prefab != null)
                {
                    foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!(r is MeshRenderer))
                            continue;
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null)
                            continue;
                        Material[] mats = r.sharedMaterials;
                        bool solid = false;
                        if (r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                            foreach (Material m in mats)
                                if (m != null && m.renderQueue <= 2500)
                                {
                                    solid = true;
                                    break;
                                }
                        if (!solid)
                            continue;
                        Transform tr = r.transform;
                        list.Add(new Part
                        {
                            Mesh = mf.sharedMesh,
                            SubmeshCount = Mathf.Min(mats.Length, mf.sharedMesh.subMeshCount),
                            Local = prefab.transform.worldToLocalMatrix * tr.localToWorldMatrix,
                        });
                    }
                }
            }
            catch (System.Exception e)
            {
                Log.Error("BuildGhosts: сбой разбора префаба — " + e.Message);
            }
            Parts[def] = list;
            return list;
        }

        /// <summary>Материал-призрак GC: встроенный Internal-Colored, альфа-блендинг,
        /// без записи глубины (Z-fight невозможен).</summary>
        private static Material GhostMaterial()
        {
            if (_ghostMat != null)
                return _ghostMat;
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null)
            {
                _broken = true;
                Log.Error("BuildGhosts: Hidden/Internal-Colored не найден — фолбэк на маркеры");
                return null;
            }
            _ghostMat = new Material(sh) { renderQueue = 3000 };
            _ghostMat.SetFloat("_SrcBlend", 5f);
            _ghostMat.SetFloat("_DstBlend", 10f);
            _ghostMat.SetFloat("_ZWrite", 0f);
            _ghostMat.SetFloat("_Cull", 2f);
            return _ghostMat;
        }

        private static MaterialPropertyBlock Block(Color tint)
        {
            if (_block == null)
                _block = new MaterialPropertyBlock();
            _block.SetColor("_Color", tint);
            return _block;
        }

        /// <summary>Нарисовать силуэт здания в точке (каждый кадр).</summary>
        public static void Draw(BuildingDefinition def, Vector3 origin, Color tint) =>
            Draw(def, origin, Quaternion.identity, tint);

        /// <summary>То же с поворотом (выход ангара ставится курсором).</summary>
        public static void Draw(BuildingDefinition def, Vector3 origin, Quaternion rot, Color tint)
        {
            if (!Enabled || def == null)
                return;
            Material mat = GhostMaterial();
            if (mat == null)
                return;
            MaterialPropertyBlock block = Block(tint);
            Matrix4x4 root = Matrix4x4.TRS(origin, rot, Vector3.one);
            foreach (Part part in GetParts(def))
            {
                Matrix4x4 m = root * part.Local;
                for (int sub = 0; sub < part.SubmeshCount; sub++)
                    Graphics.DrawMesh(part.Mesh, m, mat, 0, null, sub, block);
            }
        }

        // Выход ангара (spawnTransform префаба): локальные позиция+направление
        // относительно корня — рисуем стрелку, чтобы направление выката было видно.
        private static readonly Dictionary<BuildingDefinition, Matrix4x4> Exits =
            new Dictionary<BuildingDefinition, Matrix4x4>();

        // Выход депо: техника спавнится в приватном spawnTransform и выталкивается
        // вперёд по его forward (VehicleDepot.TrySpawnVehicle/MoveVehicleFromDepot).
        // Публичного геттера, как Hangar.GetSpawnTransform, у депо нет — рефлексия
        // с кэшем FieldInfo (§1). Проба поля — в SelfCheck.
        private static readonly System.Reflection.FieldInfo DepotSpawnField =
            typeof(VehicleDepot).GetField("spawnTransform",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        internal static bool TryGetExit(BuildingDefinition def, out Matrix4x4 local)
        {
            local = Matrix4x4.identity;
            if (def == null || def.unitPrefab == null)
                return false;
            if (Exits.TryGetValue(def, out Matrix4x4 cached))
            {
                local = cached;
                return cached != Matrix4x4.zero;
            }
            Hangar hangar = def.unitPrefab.GetComponent<Hangar>();
            Transform spawn;
            if (hangar != null)
                spawn = hangar.GetSpawnTransform();
            else
            {
                VehicleDepot depot = def.unitPrefab.GetComponent<VehicleDepot>();
                spawn = depot != null && DepotSpawnField != null
                    ? DepotSpawnField.GetValue(depot) as Transform
                    : null;
            }
            Matrix4x4 result;
            if (spawn != null)
            {
                result = def.unitPrefab.transform.worldToLocalMatrix * spawn.localToWorldMatrix;
            }
            else
            {
                // Нет точки спавна (наблюдалось на hardened-ангаре): синтетический
                // выход — центр торца «вперёд» префаба (+Z). Единая точка для
                // стрелки/превью/дороги — игрок видит её и доворачивает площадку.
                result = Matrix4x4.TRS(
                    new Vector3(0f, 0f, def.length * 0.5f + 2f),
                    Quaternion.identity, Vector3.one);
                Log.Info("BuildGhosts: у производящего здания нет точки спавна — выход по габариту: " +
                         def.unitName);
            }
            Exits[def] = result;
            local = result;
            return true;
        }

        private static Mesh _arrowMesh;
        private static Material _arrowMat;
        private static readonly Color ExitTint = new Color(1f, 0.8f, 0.2f, 0.85f);

        /// <summary>Плоская стрелка +Z единичной длины (шафт + остриё).</summary>
        private static Mesh ArrowMesh()
        {
            if (_arrowMesh != null)
                return _arrowMesh;
            var mesh = new Mesh { name = "hangar-exit-arrow" };
            var v = new[]
            {
                // шафт
                new Vector3(-0.08f, 0f, 0f), new Vector3(0.08f, 0f, 0f),
                new Vector3(0.08f, 0f, 0.75f), new Vector3(-0.08f, 0f, 0.75f),
                // остриё
                new Vector3(-0.3f, 0f, 0.75f), new Vector3(0.3f, 0f, 0.75f),
                new Vector3(0f, 0f, 1f),
            };
            mesh.vertices = v;
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 6, 5 };
            mesh.RecalculateBounds();
            _arrowMesh = mesh;
            return mesh;
        }

        /// <summary>Материал стрелки — копия призрачного без кульинга (обе стороны).</summary>
        private static Material ArrowMaterial()
        {
            if (_arrowMat != null)
                return _arrowMat;
            Material ghost = GhostMaterial();
            if (ghost == null)
                return null;
            _arrowMat = new Material(ghost);
            _arrowMat.SetFloat("_Cull", 0f);
            return _arrowMat;
        }

        /// <summary>Стрелка выхода производящего здания: от точки выката вдоль
        /// направления (по поверхности — суша ИЛИ вода: у верфи GroundY даёт дно).</summary>
        private static void DrawExitArrow(BuildingDefinition def, in Matrix4x4 root)
        {
            if (!TryGetExit(def, out Matrix4x4 exitLocal))
                return;
            Material mat = ArrowMaterial();
            if (mat == null)
                return;
            Matrix4x4 m = root * exitLocal;
            Vector3 pos = m.GetColumn(3);
            Vector3 fwd = m.MultiplyVector(Vector3.forward);
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f)
                return;
            float len = Mathf.Clamp(def.length * 0.9f, 20f, 70f);
            Vector3 ground = new Vector3(pos.x,
                Mathf.Max(GameApi.GroundY(pos.x, pos.z), Datum.LocalSeaY) + 0.6f, pos.z);
            Matrix4x4 arrow = Matrix4x4.TRS(ground, Quaternion.LookRotation(fwd.normalized),
                new Vector3(len, 1f, len));
            Graphics.DrawMesh(ArrowMesh(), arrow, mat, 0, null, 0, Block(ExitTint));
        }

        /// <summary>Покадровая подача призраков (зовётся из Update контроллера ДО
        /// рендера; DrawMesh из OnGUI подавался после рендера — призраки мерцали).</summary>
        public static void Render()
        {
            if (!Enabled || !GameApi.InMission)
                return;
            Camera cam = GameApi.MainCam;
            bool drewRunwayGhost = false; // превью из цикла задач — НЕ стирать в конце

            // Призраки всех площадок (поворот площадки + стрелка выхода; депо
            // дополнительно наклонено под рельеф — как встанет при достройке).
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                if (t.Definition == null)
                {
                    // Задача-ВПП: силуэта-префаба нет — превью полосой с порогами.
                    if (t.IsRunway)
                    {
                        TaxiSurface.SetRunwayPreview(t.Site, t.RunwayEnd);
                        drewRunwayGhost = true;
                    }
                    continue;
                }
                Vector3 lp = t.Site.ToLocalPosition();
                // Верфь: призрак на уровне будущей палубы (море) — GroundY под
                // водой даёт дно, силуэт корпуса лежал бы на дне.
                float baseY = t.IsDock ? Datum.LocalSeaY : GameApi.GroundY(lp.x, lp.z);
                Vector3 origin = new Vector3(lp.x, baseY, lp.z) +
                                 (Vector3)t.Definition.spawnOffset;
                Quaternion rot = Quaternion.Euler(t.PitchDeg, t.YawDeg, t.RollDeg);
                Color tint = t.ZoneBlocked
                    ? BlockedTint
                    : (t.Paid > 0f ? ValidTint : BuildTint);
                Matrix4x4 root = Matrix4x4.TRS(origin, rot, Vector3.one);
                Draw(t.Definition, origin, rot, tint);
                if (BuildTasks.IsProducingDefinition(t.Definition))
                    DrawExitArrow(t.Definition, root);
            }

            // Силуэт режима установки. Пока ЛКМ удерживается (жест поворота) —
            // призрак стоит на якоре и крутится выходом к курсору; зелёный/красный
            // по валидности точки (для ангара — и по близости своего аэродрома).
            if (BuildTasks.Pending != null && cam != null && !RtsInput.PointerOverUi &&
                GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 ground))
            {
                BuildingDefinition def = BuildTasks.Pending;
                Vector3 at = BuildTasks.PlacingActive
                    ? BuildTasks.PlacingSite.ToLocalPosition()
                    : ground;
                float yaw = BuildTasks.PlacingActive ? BuildTasks.PendingYawDeg : 0f;
                bool ok = GameApi.IsSiteBuildable(at.x, at.z) &&
                          !BuildTasks.IsZoneBlockedAt(at, null);
                if (ok && BuildTasks.IsHangarDefinition(def))
                {
                    GameApi.TryGetLocalHQ(out FactionHQ hq);
                    ok = BuildTasks.NearestOwnAirbase(at, hq, Settings.HangarAttachRadiusM) != null;
                }
                Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
                // Депо наклоняем живьём тем же расчётом, что зафиксирует Place, —
                // призрак показывает финальную посадку по склону.
                if (BuildTasks.IsProducingDefinition(def) && !BuildTasks.IsHangarDefinition(def))
                {
                    BuildTasks.TerrainTilt(def, at, yaw, out float pitch, out float roll);
                    rot = Quaternion.Euler(pitch, yaw, roll);
                }
                Draw(def, at + (Vector3)def.spawnOffset, rot, ok ? ValidTint : BlockedTint);
                if (BuildTasks.IsProducingDefinition(def))
                {
                    Matrix4x4 root = Matrix4x4.TRS(
                        at + (Vector3)def.spawnOffset, rot, Vector3.one);
                    DrawExitArrow(def, root);
                    // Превью будущей рулёжки (только ангар): светлая полоса от выхода
                    // к точке подключения — видно маршрут до постановки. НЕ гейтится
                    // валидностью площадки: цвет призрака уже несёт ок/не-ок,
                    // а рядом стоящие инженеры (блокеры зоны) не должны прятать путь.
                    if (BuildTasks.IsHangarDefinition(def) &&
                        TryGetExit(def, out Matrix4x4 exitLocal))
                    {
                        GameApi.TryGetLocalHQ(out FactionHQ hq);
                        Matrix4x4 m = root * exitLocal;
                        Vector3 fwd = m.MultiplyVector(Vector3.forward);
                        if (BuildTasks.TryPreviewTaxiRoute(m.GetColumn(3), fwd, hq,
                                out var preview))
                            TaxiSurface.SetPreview(preview);
                        else
                            TaxiSurface.ClearPreview();
                    }
                    else
                        TaxiSurface.ClearPreview();
                }
                else
                    TaxiSurface.ClearPreview();
            }
            // Режим протяжки ВПП: живая полоса «якорь → курсор» с порогами.
            else if (BuildTasks.PendingRunway && cam != null)
            {
                if (BuildTasks.PlacingActive)
                    TaxiSurface.SetRunwayPreview(BuildTasks.PlacingSite,
                        BuildTasks.PlacingRunwayEnd);
                else if (!RtsInput.PointerOverUi &&
                         GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos),
                             out Vector3 rwyAt))
                    TaxiSurface.SetRunwayPreview(
                        rwyAt.ToGlobalPosition(), rwyAt.ToGlobalPosition());
                else
                    TaxiSurface.ClearPreview();
            }
            // Режим установки ВЕРФИ (v0.95): силуэт корпуса на уровне будущей
            // палубы + стрелка выхода; поворот — жестом удержания ЛКМ, как у
            // ангаров/депо. Тинт — по глубине воды под призраком (тот же гейт,
            // что в TryPlaceDock); наклона нет — платформа плоская.
            else if (BuildTasks.PendingDock && cam != null)
            {
                BuildingDefinition hull = BuildTasks.DockDefinition();
                if (hull != null && !RtsInput.PointerOverUi &&
                    GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 dockAt))
                {
                    // Во время жеста валидность меряем по ЗАКРЕПЛЁННОМУ якорю
                    // (клик уже был по глубокой воде), до клика — по курсору.
                    Vector3 anchor = BuildTasks.PlacingActive
                        ? BuildTasks.PlacingSite.ToLocalPosition()
                        : dockAt;
                    bool ok = anchor.y <= Datum.LocalSeaY - Settings.DockMinDepthM;
                    Vector3 at = new Vector3(anchor.x, Datum.LocalSeaY, anchor.z);
                    float yaw = BuildTasks.PlacingActive ? BuildTasks.PendingYawDeg : 0f;
                    Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
                    Vector3 origin = at + (Vector3)hull.spawnOffset;
                    Draw(hull, origin, rot, ok ? ValidTint : BlockedTint);
                    Matrix4x4 root = Matrix4x4.TRS(origin, rot, Vector3.one);
                    DrawExitArrow(hull, root);
                }
                TaxiSurface.ClearPreview();
            }
            else if (BuildTasks.Pending == null && !BuildTasks.PendingRunway &&
                     !BuildTasks.PendingDock && !drewRunwayGhost)
                TaxiSurface.ClearPreview();
        }

        public static Color TintBuild => BuildTint;
        public static Color TintValid => ValidTint;
        public static Color TintBlocked => BlockedTint;

        public static void Reset()
        {
            Parts.Clear();
            Exits.Clear();
        }

        // Совместимость со старыми вызовами: Task-призраки теперь рисуются покадрово
        // из CommanderHud.DrawBuildMarkers; заглушки гасят прежний API.
        public static void Show(BuildTasks.Task t) { }
        public static void Hide(BuildTasks.Task t) { }
        public static void HideAll() { }
        public static void Sync() { }
        /// <summary>Курсор над телом призрака: экран-прямоугольник от РАЗМЕРОВ
        /// определения (length/width/height), приведённых к пикселям — надёжнее
        /// меш-bounds с матрицами префаба (те давали мусорный bbox на весь экран).</summary>
        public static bool ScreenContains(BuildTasks.Task t, Camera cam, Vector2 mouse)
        {
            if (t?.Definition == null || cam == null)
                return false;
            Vector3 lp = t.Site.ToLocalPosition();
            // Верфь — от уровня палубы (море): GroundY под водой даёт дно, пик тела
            // по дну промахивался по экрану на глубину.
            float baseY = t.IsDock ? Datum.LocalSeaY : GameApi.GroundY(lp.x, lp.z);
            Vector3 anchor = new Vector3(lp.x, baseY + t.Definition.height * 0.5f, lp.z);
            Vector3 sp = cam.WorldToScreenPoint(anchor);
            if (sp.z <= 0f)
                return false;
            float dist = Mathf.Max(sp.z, 1f);
            float pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            float worldR = 0.5f * Mathf.Max(t.Definition.length, t.Definition.width) + 2f;
            float rPx = Mathf.Max(worldR / dist * pxPerRad, 14f);
            float vPx = Mathf.Max(t.Definition.height / dist * pxPerRad * 0.5f, rPx * 0.6f);
            return Mathf.Abs(mouse.x - sp.x) <= rPx &&
                   Mathf.Abs(mouse.y - sp.y) <= vPx;
        }
    }
}
