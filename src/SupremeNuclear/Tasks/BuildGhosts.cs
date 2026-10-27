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
            Transform spawn = hangar != null ? hangar.GetSpawnTransform() : null;
            Matrix4x4 result = Matrix4x4.zero;
            if (spawn != null)
                result = def.unitPrefab.transform.worldToLocalMatrix * spawn.localToWorldMatrix;
            Exits[def] = result;
            local = result;
            return result != Matrix4x4.zero;
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

        /// <summary>Стрелка выхода ангара: от точки выката вдоль направления (по земле).</summary>
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
            Vector3 ground = new Vector3(pos.x, GameApi.GroundY(pos.x, pos.z) + 0.6f, pos.z);
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

            // Призраки всех площадок (поворот площадки + стрелка выхода ангара).
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                if (t.Definition == null)
                    continue;
                Vector3 lp = t.Site.ToLocalPosition();
                Vector3 origin = new Vector3(lp.x, GameApi.GroundY(lp.x, lp.z), lp.z) +
                                 (Vector3)t.Definition.spawnOffset;
                Quaternion rot = Quaternion.Euler(0f, t.YawDeg, 0f);
                Color tint = t.ZoneBlocked
                    ? BlockedTint
                    : (t.Paid > 0f ? ValidTint : BuildTint);
                Matrix4x4 root = Matrix4x4.TRS(origin, rot, Vector3.one);
                Draw(t.Definition, origin, rot, tint);
                if (BuildTasks.IsHangarDefinition(t.Definition))
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
                Draw(def, at + (Vector3)def.spawnOffset, rot, ok ? ValidTint : BlockedTint);
                if (BuildTasks.IsHangarDefinition(def))
                    DrawExitArrow(def, Matrix4x4.TRS(
                        at + (Vector3)def.spawnOffset, rot, Vector3.one));
            }
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
            Vector3 anchor = new Vector3(lp.x, GameApi.GroundY(lp.x, lp.z) + t.Definition.height * 0.5f, lp.z);
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
