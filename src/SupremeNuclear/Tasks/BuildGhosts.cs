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
        public static void Draw(BuildingDefinition def, Vector3 origin, Color tint)
        {
            if (!Enabled || def == null)
                return;
            Material mat = GhostMaterial();
            if (mat == null)
                return;
            MaterialPropertyBlock block = Block(tint);
            Matrix4x4 root = Matrix4x4.TRS(origin, Quaternion.identity, Vector3.one);
            foreach (Part part in GetParts(def))
            {
                Matrix4x4 m = root * part.Local;
                for (int sub = 0; sub < part.SubmeshCount; sub++)
                    Graphics.DrawMesh(part.Mesh, m, mat, 0, null, sub, block);
            }
        }

        /// <summary>Покадровая подача призраков (зовётся из Update контроллера ДО
        /// рендера; DrawMesh из OnGUI подавался после рендера — призраки мерцали).</summary>
        public static void Render()
        {
            if (!Enabled || !GameApi.InMission)
                return;
            Camera cam = GameApi.MainCam;

            // Призраки всех площадок.
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                if (t.Definition == null)
                    continue;
                Vector3 lp = t.Site.ToLocalPosition();
                Vector3 origin = new Vector3(lp.x, GameApi.GroundY(lp.x, lp.z), lp.z) +
                                 (Vector3)t.Definition.spawnOffset;
                Color tint = t.ZoneBlocked
                    ? BlockedTint
                    : (t.Paid > 0f ? ValidTint : BuildTint);
                Draw(t.Definition, origin, tint);
            }

            // Силуэт режима установки: зелёный/красный по валидности точки.
            if (BuildTasks.Pending != null && cam != null && !RtsInput.PointerOverUi &&
                GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 ground))
            {
                bool ok = GameApi.IsSiteBuildable(ground.x, ground.z) &&
                          !BuildTasks.IsZoneBlockedAt(ground, null);
                Draw(BuildTasks.Pending, ground + (Vector3)BuildTasks.Pending.spawnOffset,
                    ok ? ValidTint : BlockedTint);
            }
        }

        public static Color TintBuild => BuildTint;
        public static Color TintValid => ValidTint;
        public static Color TintBlocked => BlockedTint;

        public static void Reset()
        {
            Parts.Clear();
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
