using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace SupremeNuclear
{
    /// <summary>
    /// 3D-превью юнитов (v0.153): модель <see cref="UnitDefinition.unitPrefab"/> рендерится
    /// скрытой студийной камерой в текстуру-иконку — «вполне различимые картинки юнитов»
    /// (юзер 2026-11-04) вместо похожих плоских спрайтов friendlyIcon/hostileIcon игры.
    /// Источник идеи — механика CommanderUnitPreview из реверса Ground Control (поза
    /// камеры/свет/подмена солнца); КОД НЕ КОПИРОВАЛСЯ (лицензия) — своя реализация.
    ///
    /// Конвейер: очередь 1 рендер/кадр из Tick (CommanderModeController.UpdateSafe):
    /// сбор меш-частей префаба (LOD0) → Graphics.DrawMesh на служебный слой (из
    /// Update — ARCHITECTURE §8.16) → camera.Render с подменой RenderSettings.sun/
    /// ambientProbe на студийный свет (sun/moon LevelInfo на время гасятся) →
    /// ReadPixels в Texture2D → Sprite.Create → кэш по definition.
    ///
    /// Потребители: PreviewOf (плитки панелей — юзер «нравятся» — и фолбэк мира:
    /// null — рисуй старый плоский спрайт, превью подменится само) и Bind
    /// (долгоживущие uGUI Image — подмена по готовности).
    /// МИР/миникарта — стратегические иконки SupCom FA (StrategicIcons, юзер
    /// 2026-11-04 «превью на глобальной камере нечитабельны»); превью для них —
    /// фолбэк при отсутствии иконки в наборе. Слой 16 служебный, свободность
    /// проверяет SelfCheck. Reset — sceneLoaded (ARCHITECTURE §4). Настройка
    /// General.UnitPreviewIcons выключает превью — потребители на плоских спрайтах.
    /// </summary>
    internal static class UnitPreview
    {
        internal const int PreviewLayer = 16;   // служебный слой рендера (SelfCheck следит)
        private const int TexSize = 256;        // плитки 48px и портрет 84px — запас на 4K
        private const float FieldOfView = 26f;  // поза 3/4: камера сверху под 18°, модель -145° по yaw
        private const float CamPitchDeg = 18f;
        private const float CamYawDeg = -145f;

        /// <summary>Меш-часть префаба: sharedMesh + sharedMaterials + матрица от корня.</summary>
        private sealed class Part
        {
            public Mesh Mesh;
            public Material[] Materials;
            public Matrix4x4 Local;
        }

        private static readonly Dictionary<UnitDefinition, Sprite> Cache = new Dictionary<UnitDefinition, Sprite>();
        private static readonly Dictionary<UnitDefinition, List<Part>> Parts = new Dictionary<UnitDefinition, List<Part>>();
        private static readonly List<UnitDefinition> Queue = new List<UnitDefinition>();
        private static readonly HashSet<UnitDefinition> Queued = new HashSet<UnitDefinition>();
        private static readonly HashSet<UnitDefinition> Failed = new HashSet<UnitDefinition>();
        private static readonly Dictionary<Image, UnitDefinition> Binds = new Dictionary<Image, UnitDefinition>();

        private static Camera _camera;
        private static Light _studio;
        private static RenderTexture _target;
        private static SphericalHarmonicsL2 _studioAmbient;
        private static bool _primed;

        /// <summary>Спрайт-превью или null (не готово/выключено/без модели) — вызывающий
        /// рисует старый фолбэк (friendlyIcon/hostileIcon/mapIcon); когда превью доедет,
        /// следующий запрос вернёт его.</summary>
        public static Sprite PreviewOf(UnitDefinition def)
        {
            if (!Settings.UnitPreviewIcons || def == null)
                return null;
            if (Cache.TryGetValue(def, out Sprite s))
                return s != null ? s : null; // s fake-null (выгрузка ассетов) — ниже перерендер
            if (Failed.Contains(def))
                return null;
            Enqueue(def);
            return null;
        }

        /// <summary>Долгоживущая uGUI-иконка (плитка SupcomUi): ставит превью сейчас
        /// или регистрирует подмену по готовности; мёртвые Image чистятся при рендере.</summary>
        public static void Bind(Image img, UnitDefinition def)
        {
            if (!Settings.UnitPreviewIcons || img == null || def == null)
                return;
            Sprite s = PreviewOf(def);
            if (s != null)
            {
                img.sprite = s;
                return;
            }
            Binds[img] = def;
        }

        /// <summary>Тик конвейера (CommanderModeController.UpdateSafe): 1 рендер за кадр;
        /// первый тик сцены прогревает очередь всем каталогом Encyclopedia.</summary>
        public static void Tick()
        {
            if (!_primed)
            {
                var controller = CommanderModeController.Instance;
                if (controller == null || !controller.IsActive)
                    return; // в меню рендерить рано — кэш всё равно умрёт на sceneLoaded
                Prime();
            }
            if (Queue.Count == 0)
                return;
            UnitDefinition def = Queue[0];
            Queue.RemoveAt(0);
            Queued.Remove(def);
            Render(def);
            ApplyBinds(def);
        }

        /// <summary>Сброс сцены (sceneLoaded, ARCHITECTURE §4): сцена держит очередь,
        /// бинды (Image), камеру/свет/RT и кэши — всё под ноль с релизом GPU-памяти.</summary>
        public static void Reset()
        {
            Queue.Clear();
            Queued.Clear();
            Failed.Clear();
            Binds.Clear();
            Parts.Clear();
            foreach (Sprite s in Cache.Values)
            {
                if (s == null)
                    continue;
                Texture2D tex = s.texture;
                Object.Destroy(s);
                if (tex != null)
                    Object.Destroy(tex);
            }
            Cache.Clear();
            if (_target != null)
            {
                _target.Release();
                Object.Destroy(_target);
                _target = null;
            }
            if (_camera != null)
            {
                Object.Destroy(_camera.gameObject);
                _camera = null;
            }
            if (_studio != null)
            {
                Object.Destroy(_studio.gameObject);
                _studio = null;
            }
            _primed = false;
        }

        private static void Prime()
        {
            _primed = true;
            Encyclopedia enc = Encyclopedia.i;
            if (enc == null)
                return;
            EnqueueAll(enc.vehicles);
            EnqueueAll(enc.ships);
            EnqueueAll(enc.buildings);
            EnqueueAll(enc.aircraft); // ракеты не превьюются — рисуются плоскими как были
        }

        private static void EnqueueAll<T>(List<T> defs) where T : UnitDefinition
        {
            if (defs == null)
                return;
            foreach (UnitDefinition d in defs)
                if (d != null && d.unitPrefab != null)
                    Enqueue(d);
        }

        private static void Enqueue(UnitDefinition def)
        {
            if (Queued.Contains(def))
                return;
            Queued.Add(def);
            Queue.Add(def);
        }

        private static void ApplyBinds(UnitDefinition def)
        {
            if (Binds.Count == 0 || !Cache.TryGetValue(def, out Sprite ready) || ready == null)
                return;
            List<Image> dead = null;
            foreach (KeyValuePair<Image, UnitDefinition> kv in Binds)
            {
                Image img = kv.Key;
                if (img == null)
                {
                    (dead ??= new List<Image>()).Add(img); // Unity fake-null — prune (§8.14)
                    continue;
                }
                if (kv.Value == def)
                    img.sprite = ready;
            }
            if (dead != null)
                foreach (Image img in dead)
                    Binds.Remove(img);
        }

        private static void Render(UnitDefinition def)
        {
            List<Part> parts = GetParts(def);
            if (parts.Count == 0)
            {
                Failed.Add(def); // без модели/мешей — не рендерим и не дёргаем очередь по кругу
                return;
            }
            EnsureStage();

            Bounds bounds = GetBounds(parts);
            float radius = Mathf.Max(0.5f, bounds.extents.magnitude);
            float dist = radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad) * 0.92f;
            Transform camT = _camera.transform;
            _camera.nearClipPlane = Mathf.Max(0.05f, dist - radius * 2f);
            _camera.farClipPlane = dist + radius * 2f;
            Matrix4x4 root = Matrix4x4.TRS(camT.position + camT.forward * dist,
                Quaternion.Euler(0f, CamYawDeg, 0f), Vector3.one) * Matrix4x4.Translate(-bounds.center);
            foreach (Part p in parts)
            {
                Matrix4x4 m = root * p.Local;
                int subs = Mathf.Min(p.Materials.Length, p.Mesh.subMeshCount);
                for (int j = 0; j < subs; j++)
                    if (p.Materials[j] != null)
                        Graphics.DrawMesh(p.Mesh, m, p.Materials[j], PreviewLayer, _camera, j,
                            null, ShadowCastingMode.Off, false);
            }

            RenderLit(() => _camera.Render());

            // Пиксели в спрайт: RT → Texture2D → Sprite.Create (uGUI-плитки живут на
            // спрайтах; IMGUI DrawUnitSprite работает с тем же полным rect).
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = _target;
            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.ReadPixels(new Rect(0f, 0f, TexSize, TexSize), 0, 0, false);
            tex.Apply(false, true);
            RenderTexture.active = prevActive;
            Cache[def] = Sprite.Create(tex, new Rect(0f, 0f, TexSize, TexSize),
                new Vector2(0.5f, 0.5f), TexSize, 0, SpriteMeshType.FullRect);
        }

        /// <summary>Рендер со студийным светом: на время рендера RenderSettings.sun/
        /// ambientProbe подменяются, sun/moon LevelInfo гасятся (чтобы иконка не
        /// зависела от времени суток карты); восстановление — в finally (§8.9).</summary>
        private static void RenderLit(System.Action draw)
        {
            EnsureStudio();
            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            Light sun = level != null ? level.sun : null;
            Light moon = level != null ? level.moon : null;
            bool sunOn = sun != null && sun.enabled;
            bool moonOn = moon != null && moon.enabled;
            Light prevSun = RenderSettings.sun;
            SphericalHarmonicsL2 prevAmbient = RenderSettings.ambientProbe;
            try
            {
                if (sunOn)
                    sun.enabled = false;
                if (moonOn)
                    moon.enabled = false;
                _studio.enabled = true;
                RenderSettings.sun = _studio;
                RenderSettings.ambientProbe = _studioAmbient;
                draw();
            }
            finally
            {
                _studio.enabled = false;
                RenderSettings.sun = prevSun;
                RenderSettings.ambientProbe = prevAmbient;
                if (sunOn)
                    sun.enabled = true;
                if (moonOn)
                    moon.enabled = true;
            }
        }

        private static void EnsureStage()
        {
            if (_target == null)
            {
                _target = new RenderTexture(TexSize, TexSize, 24, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 4,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _target.Create();
            }
            if (_camera == null)
            {
                var go = new GameObject("SupremeNuclearUnitPreviewCam") // 3d-go: оффскрин-камера, не UI
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                // Далеко над картой, чтобы не пересекаться с миром; рисуем только свой слой.
                go.transform.SetPositionAndRotation(new Vector3(4700f, 9000f, 0f),
                    Quaternion.Euler(CamPitchDeg, 0f, 0f));
                _camera = go.AddComponent<Camera>();
                _camera.cullingMask = 1 << PreviewLayer;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                _camera.fieldOfView = FieldOfView;
                _camera.allowHDR = false;
                _camera.allowMSAA = true;
                _camera.depth = -50f;
                _camera.enabled = false; // рендерим вручную из тика
            }
            _camera.targetTexture = _target;
        }

        private static void EnsureStudio()
        {
            if (_studio != null)
                return;
            var go = new GameObject("SupremeNuclearUnitPreviewLight") // 3d-go: студийный свет, не UI
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            go.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            _studio = go.AddComponent<Light>();
            _studio.type = LightType.Directional;
            _studio.color = new Color(1f, 0.96f, 0.9f);
            _studio.intensity = 1.6f;
            _studio.shadows = LightShadows.None;
            _studio.cullingMask = 1 << PreviewLayer;
            _studio.enabled = false;
            _studioAmbient = default;
            _studioAmbient.AddAmbientLight(new Color(0.42f, 0.44f, 0.47f));
        }

        /// <summary>Меш-части префаба (LOD0): кэш по definition; нижние LOD, слой 3
        /// (эффекты/декор игры) и безмешевые рендереры пропускаются.</summary>
        private static List<Part> GetParts(UnitDefinition def)
        {
            if (Parts.TryGetValue(def, out List<Part> cached))
                return cached;
            var parts = new List<Part>();
            Parts[def] = parts;
            GameObject prefab = def.unitPrefab;
            if (prefab == null)
                return parts;
            var skip = new HashSet<Renderer>();
            foreach (LODGroup lod in prefab.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] levels = lod.GetLODs();
                for (int i = 1; i < levels.Length; i++)
                    foreach (Renderer r in levels[i].renderers)
                        if (r != null)
                            skip.Add(r);
            }
            Transform root = prefab.transform;
            foreach (Renderer rend in prefab.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = rend is MeshRenderer mr && mr.TryGetComponent(out MeshFilter mf)
                    ? mf.sharedMesh : null;
                if (mesh == null || skip.Contains(rend) || rend.gameObject.layer == 3)
                    continue;
                parts.Add(new Part
                {
                    Mesh = mesh,
                    Materials = rend.sharedMaterials,
                    Local = root.worldToLocalMatrix * rend.transform.localToWorldMatrix
                });
            }
            return parts;
        }

        private static Bounds GetBounds(List<Part> parts)
        {
            Bounds bounds = default;
            bool any = false;
            foreach (Part p in parts)
            {
                Bounds b = p.Mesh.bounds;
                Vector3 ext = b.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + new Vector3(
                        (i & 1) == 0 ? -ext.x : ext.x,
                        (i & 2) == 0 ? -ext.y : ext.y,
                        (i & 4) == 0 ? -ext.z : ext.z);
                    Vector3 world = p.Local.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        bounds = new Bounds(world, Vector3.zero);
                        any = true;
                    }
                    else
                        bounds.Encapsulate(world);
                }
            }
            return bounds;
        }
    }
}
