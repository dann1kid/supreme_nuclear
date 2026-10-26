using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Временный IMGUI-оверлей; целевой UI — uGUI на следующих итерациях.</summary>
    public sealed class CommanderHud : MonoBehaviour
    {
        public static CommanderHud Instance { get; private set; }

        private static readonly List<(string text, float expires)> Notices = new List<(string, float)>();
        public static bool DebugOverlay;

        private static Texture2D _white;
        private static GUIStyle _label;
        private static GUIStyle _small;
        private float _fps;

        private void Awake()
        {
            Instance = this;
            DebugOverlay = Settings.ShowDebugOverlay;
        }

        private void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.05f);
            if (RtsInput.KeyDown(KeyCode.F11))
                DebugOverlay = !DebugOverlay;
        }

        public static void Notice(string text, float life = 3.5f) =>
            Notices.Add((text, Time.unscaledTime + life));

        private void OnGUI()
        {
            if (!GameApi.InMission)
                return;

            EnsureStyles();

            var controller = CommanderModeController.Instance;
            bool active = controller != null && controller.IsActive;

            GUI.Label(new Rect(10f, 8f, 1100f, 22f),
                $"SUPREME NUCLEAR v{PluginInfo.Version}  |  Режим командира: {(active ? "<color=lime>ВКЛ</color>" : "ВЫКЛ")}  |  {Settings.ToggleCommanderKey} — переключить",
                _label);

            if (active)
            {
                SelectionService selection = controller.Selection;

                if (selection.IsDragging)
                    DrawBoxOutline(selection.DragRectGui, 1.5f, new Color(0.6f, 1f, 0.6f, 0.9f));

                DrawDepositMarkers();
                DrawStrategicIcons(controller);
                DrawHpBars();
                DrawBuildMarkers();
                DrawSelectionRings(controller);
                DrawSelectionBrackets(controller);
                DrawAircraftRoutes(controller);
                DrawOrderMarkers(controller);
            }

            DrawNotices();
            DrawSelfCheckFailures();
            if (DebugOverlay)
                DrawDebug(controller);
        }

        // ---------- панели ----------





        // ---------- иконки ----------

        /// <summary>ЕДИНАЯ отрисовка спрайта-иконки юнита (консолидация v0.48):
        /// аспект сохранён, тень опциональна. Использовать во всех иконочных циклах
        /// вместо копий uv/aspect/shadow-кода.</summary>
        private static void DrawUnitSprite(Vector2 center, float sizePx, Sprite sprite,
            Color tint, bool withShadow)
        {
            if (sprite == null || sprite.texture == null)
            {
                DrawRect(new Rect(center.x - sizePx * 0.5f, center.y - sizePx * 0.5f, sizePx, sizePx), tint);
                return;
            }
            var t = sprite.texture;
            var uv = new Rect(
                sprite.textureRect.x / t.width, sprite.textureRect.y / t.height,
                sprite.textureRect.width / t.width, sprite.textureRect.height / t.height);
            float aspect = sprite.textureRect.width / Mathf.Max(sprite.textureRect.height, 1f);
            float w = Mathf.Min(sizePx * aspect, sizePx * 2.4f);
            var r = new Rect(center.x - w * 0.5f, center.y - sizePx * 0.5f, w, sizePx);
            Color prev = GUI.color;
            if (withShadow)
            {
                GUI.color = new Color(0f, 0f, 0f, tint.a > 0.7f ? 0.55f : tint.a);
                GUI.DrawTextureWithTexCoords(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), t, uv);
            }
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(r, t, uv);
            GUI.color = prev;
        }

        /// <summary>HP-полоски над повреждёнными своими юнитами (доля HP — из кэша GameApi,
        /// агрегат по деталям; рисуются только у повреждённых, цвет зелёный→жёлтый→красный).</summary>
        private static void DrawHpBars()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            float k = Screen.height / 1440f;
            float w = 26f * k;
            float h = 3f * k;
            int drawn = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (drawn > Settings.IconMaxUnits)
                    break;
                if (unit == null || unit.disabled || !GameApi.IsFriendlyUnit(unit, hq))
                    continue;
                float hp = GameApi.HpFraction(unit);
                if (hp >= 0.995f)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f || sp.x < -30f || sp.x > Screen.width + 30f || sp.y < -30f || sp.y > Screen.height + 30f)
                    continue;
                float guiY = Screen.height - sp.y;
                var bg = new Rect(sp.x - w * 0.5f, guiY - 14f * k, w, h);
                DrawRect(bg, new Color(0f, 0f, 0f, 0.6f));
                Color c = hp > 0.5f
                    ? new Color(0.4f, 1f, 0.5f, 0.95f)
                    : (hp > 0.25f ? new Color(1f, 0.8f, 0.3f, 0.95f) : new Color(1f, 0.35f, 0.3f, 0.95f));
                DrawRect(new Rect(bg.x, bg.y, bg.width * hp, h), c);
                drawn++;
            }
        }

        /// <summary>Маркеры Task-API: площадки стройки (рамка + заливка прогресса) и
        /// призрак устанавливаемого здания под курсором.</summary>
        private static void DrawBuildMarkers()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            float k = Screen.height / 1440f;
            Color c = new Color(0.4f, 0.9f, 1f, 0.95f);

            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                Vector3 site = t.Site.ToLocalPosition();
                site.y = GameApi.GroundY(site.x, site.z);
                Vector3 sp = cam.WorldToScreenPoint(site + Vector3.up * 6f);
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float gy = Screen.height - sp.y;
                float s = 34f * k;
                var r = new Rect(sp.x - s * 0.5f, gy - s * 0.5f, s, s);
                DrawBoxOutline(r, 2f * k, t.ZoneBlocked ? new Color(1f, 0.5f, 0.35f, 0.95f) : c);
                if (t.ZoneBlocked)
                    GUI.Label(new Rect(sp.x - 140f, gy + s * 0.5f + 2f, 280f, 18f * k),
                        "зона не расчищена", _small);
                float p = t.Progress01;
                var fill = new Rect(r.x + 3f * k, r.yMax - 3f * k - (r.height - 6f * k) * p,
                    r.width - 6f * k, (r.height - 6f * k) * p);
                DrawRect(fill, new Color(c.r, c.g, c.b, 0.28f));
                GUI.Label(new Rect(sp.x - 140f, gy - s * 0.5f - 20f * k, 280f, 18f * k),
                    $"{(t.Definition != null ? t.Definition.unitName : "?")} {p:P0}", _small);
            }

            // Призраки шаблона: весь набор у курсора с сохранёнными смещениями;
            // красный = точка не пригодна (вода/склон) или зона занята.
            var tpl = OrderTemplates.PendingTemplate.template;
            if (tpl != null && !RtsInput.PointerOverUi &&
                GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 tplGround))
            {
                foreach ((string key, float dx, float dz) in OrderTemplates.OffsetsOf(tpl))
                {
                    Vector3 site = new Vector3(tplGround.x + dx, 0f, tplGround.z + dz);
                    site.y = GameApi.GroundY(site.x, site.z);
                    bool ok = GameApi.IsSiteBuildable(site.x, site.z) &&
                              !BuildTasks.IsZoneBlockedAt(site, null);
                    Vector3 tsp = cam.WorldToScreenPoint(site + Vector3.up * 6f);
                    if (tsp.z <= 0f)
                        continue;
                    float tgy = Screen.height - tsp.y;
                    Color tc = ok ? new Color(0.4f, 0.9f, 1f, 0.95f) : new Color(1f, 0.45f, 0.3f, 0.95f);
                    float fw2 = 24f * k;
                    DrawBoxOutline(new Rect(tsp.x - fw2 * 0.5f, tgy - fw2 * 0.5f, fw2, fw2), 1.5f * k, tc);
                    DrawCross(new Vector2(tsp.x, tgy), 14f * k, tc);
                }
                Vector3 csp = cam.WorldToScreenPoint(tplGround + Vector3.up * 6f);
                if (csp.z > 0f)
                    GUI.Label(new Rect(csp.x - 220f, Screen.height - csp.y + 16f * k, 440f, 18f * k),
                        tpl.name + " · ЛКМ — построить (Shift — серия) · ПКМ — отмена", _small);
            }

            if (BuildTasks.Pending == null || RtsInput.PointerOverUi)
                return;
            if (!GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 ground))
                return;
            Vector3 gsp = cam.WorldToScreenPoint(ground + Vector3.up * 6f);
            if (gsp.z <= 0f)
                return;
            float ggy = Screen.height - gsp.y;
            // Радиус зоны стройки: честный мир→px (как у колец выделения).
            float pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            float dist = Mathf.Max(Vector3.Distance(cam.transform.position, ground), 1f);
            float r0 = Settings.BuildSiteRadiusM / dist * pxPerRad;
            r0 = Mathf.Clamp(r0, 18f * k, Screen.height * 0.6f);
            bool zoneBlocked = BuildTasks.IsZoneBlockedAt(ground, null);
            Color gc = zoneBlocked ? new Color(1f, 0.45f, 0.3f, 0.95f) : c;
            for (int i = 0; i < 32; i++)
            {
                float a1 = i / 32f * Mathf.PI * 2f;
                Vector2 p1 = new Vector2(gsp.x + Mathf.Cos(a1) * r0, ggy + Mathf.Sin(a1) * r0);
                DrawRect(new Rect(p1.x - 1.5f, p1.y - 1.5f, 3f, 3f), gc);
            }
            // Футпринт здания (упрощённо ~24 м).
            float fw = 24f / dist * pxPerRad;
            fw = Mathf.Clamp(fw, 8f * k, 200f);
            DrawBoxOutline(new Rect(gsp.x - fw * 0.5f, ggy - fw * 0.5f, fw, fw), 1.5f * k, gc);
            DrawCross(new Vector2(gsp.x, ggy), 26f * k, gc);
            GUI.Label(new Rect(gsp.x - 220f, ggy + 16f * k, 440f, 18f * k),
                $"{BuildTasks.Pending.unitName} — {BuildTasks.Pending.value:F0} · ЛКМ — поставить · ПКМ — отмена", _small);
        }

        private static bool IsExtractor(Unit unit) => EconomyService.IsExtractor(unit);

        /// <summary>SupCom-стиль: юниты-точки рисуются 2D-иконками. Свои — визорные маркеры,
        /// авиация — ориентированные по курсу силуэты, свои экстракторы — зелёные.</summary>
        private static void DrawStrategicIcons(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;

            float px = Settings.IconSizePx * (Screen.height / 1440f);
            var selected = new HashSet<Unit>(controller.Selection.Selected);
            int drawn = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (drawn > Settings.IconMaxUnits)
                    break;
                if (!GameApi.IsFriendlyUnit(unit, hq) ||
                    !(unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building))
                    continue;
                if (!Settings.IconsAlwaysOn && GameApi.ApparentSizePx(unit, cam) >= Settings.IconMinPx)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float guiY = Screen.height - sp.y;
                bool isSelected = selected.Contains(unit);
                float size = unit is Ship ? px * 1.4f : (unit is Building ? px * 1.3f : (unit is Aircraft ? px * 1.15f : px));

                // Ориентированные силуэты — только авиация; наземка/флот/здания — маркеры визора.
                if (unit is Aircraft && unit.definition != null && unit.definition.mapOrient && unit.definition.mapIcon != null)
                {
                    float ang = HeadingScreenAngle(unit, cam);
                    Color tint = isSelected ? new Color(0.7f, 1f, 0.7f, 1f) : new Color(0.45f, 0.9f, 0.5f, 1f);
                    DrawOrientedSprite(new Vector2(sp.x + 1.5f, guiY + 1.5f), size, unit.definition.mapIcon, new Color(0f, 0f, 0f, 0.55f), ang);
                    DrawOrientedSprite(new Vector2(sp.x, guiY), size, unit.definition.mapIcon, tint, ang);
                    if (isSelected)
                        DrawBoxOutline(new Rect(sp.x - size * 0.9f, guiY - size * 0.9f, size * 1.8f, size * 1.8f), 1.5f, new Color(0.6f, 1f, 0.6f, 1f));
                    drawn++;
                    continue;
                }

                Sprite sprite = unit.definition != null
                    ? (unit.definition.friendlyIcon != null ? unit.definition.friendlyIcon : unit.definition.mapIcon)
                    : null;
                Color friendTint = isSelected ? new Color(0.7f, 1f, 0.7f, 1f) : new Color(0.45f, 0.9f, 0.5f, 1f);
                DrawUnitSprite(new Vector2(sp.x, guiY), size, sprite, friendTint, true);
                if (isSelected)
                    DrawBoxOutline(new Rect(sp.x - size * 0.7f - 4f, guiY - size * 0.5f - 4f,
                        size * 1.4f + 8f, size + 8f), 1.5f, new Color(0.6f, 1f, 0.6f, 1f));
                drawn++;
            }

            DrawAirbaseDiamonds(cam, hq, px);
            DrawHostileIcons(cam, hq, px);
            DrawMissileWarnings(cam, hq, px);
        }

        /// <summary>Аэродромы — ромбы: свой зелёный; чужой красный ТОЛЬКО под разведкой,
        /// иначе жёлтый «неизвестно» (нейтральные тоже жёлтые — владельца не видно).</summary>
        private static void DrawAirbaseDiamonds(Camera cam, FactionHQ hq, float px)
        {
            if (FactionRegistry.airbaseLookup == null)
                return;
            float d = px * 1.7f;
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null)
                    continue;
                Vector3 pos = airbase.transform.position;
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(pos.x, GameApi.GroundY(pos.x, pos.z) + 20f, pos.z));
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float gx = sp.x;
                float gy = Screen.height - sp.y;
                FactionHQ owner = airbase.CurrentHQ;
                Color c = owner == hq
                    ? new Color(0.45f, 0.95f, 0.5f, 0.95f)
                    : owner != null && GameApi.IsAirbaseScouted(airbase, hq)
                        ? new Color(1f, 0.35f, 0.3f, 0.95f)
                        : new Color(1f, 0.85f, 0.2f, 0.95f);
                // Ромб.
                var pts = new[]
                {
                    new Vector2(gx, gy - d * 0.6f), new Vector2(gx + d * 0.45f, gy),
                    new Vector2(gx, gy + d * 0.6f), new Vector2(gx - d * 0.45f, gy)
                };
                for (int i = 0; i < 4; i++)
                {
                    Vector2 a1 = pts[i];
                    Vector2 a2 = pts[(i + 1) % 4];
                    int dots = Mathf.Clamp((int)(Vector2.Distance(a1, a2) / 8f), 1, 12);
                    for (int k = 0; k <= dots; k++)
                    {
                        Vector2 p = Vector2.Lerp(a1, a2, k / (float)dots);
                        DrawRect(new Rect(p.x - 1.2f, p.y - 1.2f, 2.4f, 2.4f), c);
                    }
                }
                DrawRect(new Rect(gx - 2f, gy - 2f, 4f, 4f), c);
            }
        }

        /// <summary>Чужие юниты и здания — ТОЛЬКО по разведке: без контакта скрыты,
        /// красные под наблюдением, серые тающие по давности (у статики размер иконки
        /// не отключается по apparent-size — это стратегический слой).</summary>
        private static void DrawHostileIcons(Camera cam, FactionHQ hq, float px)
        {
            float now = Time.timeSinceLevelLoad;
            int drawn = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (drawn > Settings.IconMaxUnits)
                    break;
                if (unit.NetworkHQ == null || unit.NetworkHQ == hq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building))
                    continue;
                bool isStatic = unit is Building;
                if (!isStatic && !Settings.IconsAlwaysOn && GameApi.ApparentSizePx(unit, cam) >= Settings.IconMinPx)
                    continue;

                TrackingInfo info = hq.GetTrackingData(unit.persistentID);
                if (info == null)
                    continue;
                float age = now - info.lastSpottedTime;
                if (age > Settings.StaleContactSec)
                    continue;
                bool live = age < 4f;

                Vector3 staleAnchor = info.GetPosition().ToLocalPosition() + Vector3.up * (unit.maxRadius * 0.7f);
                Vector3 sp = cam.WorldToScreenPoint(staleAnchor);
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f || sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float guiY = Screen.height - sp.y;
                float size = isStatic ? px * 1.3f : (unit is Ship ? px * 1.4f : px);
                Color tint = live
                    ? new Color(1f, 0.35f, 0.3f, 1f)
                    : new Color(0.75f, 0.75f, 0.75f, Mathf.Lerp(0.85f, 0.2f, age / Settings.StaleContactSec));

                if (unit is Aircraft && unit.definition != null && unit.definition.mapOrient && unit.definition.mapIcon != null)
                {
                    float ang = HeadingScreenAngle(unit, cam);
                    DrawOrientedSprite(new Vector2(sp.x + 1.5f, guiY + 1.5f), size, unit.definition.mapIcon, new Color(0f, 0f, 0f, 0.5f), ang);
                    DrawOrientedSprite(new Vector2(sp.x, guiY), size, unit.definition.mapIcon, tint, ang);
                    if (!live)
                        DrawCross(new Vector2(sp.x, guiY + size * 0.7f), size * 0.5f, tint * 0.8f);
                    drawn++;
                    continue;
                }

                Sprite sprite = unit.definition != null
                    ? (unit.definition.hostileIcon != null ? unit.definition.hostileIcon : unit.definition.friendlyIcon)
                    : null;
                DrawUnitSprite(new Vector2(sp.x, guiY), size, sprite, tint, true);
                if (!live)
                    DrawCross(new Vector2(sp.x, guiY + size * 0.7f), size * 0.5f, tint * 0.8f);
                drawn++;
            }
        }

        private static void DrawMissileWarnings(Camera cam, FactionHQ hq, float px)
        {
            float phase = Mathf.Repeat(Time.unscaledTime * 2.5f, 1f);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Missile))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f || sp.x < -30f || sp.x > Screen.width + 30f || sp.y < -30f || sp.y > Screen.height + 30f)
                    continue;
                float guiY = Screen.height - sp.y;

                bool friendly = unit.NetworkHQ != null && unit.NetworkHQ == hq;
                if (!friendly && phase > 0.7f)
                    continue;

                Color tint = friendly
                    ? new Color(0.5f, 0.9f, 0.55f, 0.95f)
                    : new Color(1f, 0.25f, 0.2f, 1f);
                float size = px * 0.9f;

                Sprite sprite = unit.definition != null
                    ? (unit.definition.hostileIcon != null ? unit.definition.hostileIcon : unit.definition.friendlyIcon)
                    : null;
                DrawUnitSprite(new Vector2(sp.x, guiY), size, sprite, tint, false);
            }
        }

        private static float HeadingScreenAngle(Unit unit, Camera cam)
        {
            Vector3 f = unit.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-4f)
                return 0f;
            f.Normalize();
            float yaw = cam.transform.eulerAngles.y * Mathf.Deg2Rad;
            float cx = f.x * Mathf.Cos(yaw) - f.z * Mathf.Sin(yaw);
            float cz = f.x * Mathf.Sin(yaw) + f.z * Mathf.Cos(yaw);
            return Mathf.Atan2(cx, cz) * Mathf.Rad2Deg;
        }

        private static void DrawOrientedSprite(Vector2 center, float size, Sprite sprite, Color tint, float angleDeg)
        {
            if (sprite == null || sprite.texture == null)
                return;
            var t = sprite.texture;
            var uv = new Rect(
                sprite.textureRect.x / t.width, sprite.textureRect.y / t.height,
                sprite.textureRect.width / t.width, sprite.textureRect.height / t.height);
            float aspect = sprite.textureRect.width / Mathf.Max(sprite.textureRect.height, 1f);
            float w = Mathf.Min(size * aspect, size * 4f);
            var r = new Rect(center.x - w * 0.5f, center.y - size * 0.5f, w, size);

            Matrix4x4 prevMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angleDeg, r.center);
            Color prevColor = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(r, t, uv);
            GUI.color = prevColor;
            GUI.matrix = prevMatrix;
        }

        // ---------- маркеры и выделение ----------

        /// <summary>Маркеры месторождений: только НЕзахваченные, жёлтые пульсирующие (SupCom-мекс).</summary>
        private static void DrawDepositMarkers()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !EconomyService.Running)
                return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 3f);
            float s = 18f * (Screen.height / 1440f);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Building) || unit.disabled)
                    continue;
                if (unit.NetworkHQ != null)
                    continue;
                string name = unit.NetworkunitName;
                if (string.IsNullOrEmpty(name) || !name.StartsWith("Extractor"))
                    continue;

                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f || sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f)
                    continue;
                float guiY = Screen.height - sp.y;

                Color color = new Color(1f, 0.85f, 0.2f, 1f);
                var outer = new Rect(sp.x - s * 0.5f, guiY - s * 0.5f, s, s);
                DrawBoxOutline(outer, 1.5f, color);
                float innerSize = s * (0.25f + 0.35f * pulse);
                Color fill = color;
                fill.a = 0.35f + 0.5f * pulse;
                DrawRect(new Rect(sp.x - innerSize * 0.5f, guiY - innerSize * 0.5f, innerSize, innerSize), fill);
            }
        }

        private static Texture2D _ring;

        private static Texture2D RingTexture()
        {
            if (_ring != null)
                return _ring;
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = (size - 1) * 0.5f;
            float rOut = c - 2f;
            float rIn = c - 7f;
            for (int yy = 0; yy < size; yy++)
            {
                for (int xx = 0; xx < size; xx++)
                {
                    float d = Mathf.Sqrt((xx - c) * (xx - c) + (yy - c) * (yy - c));
                    float a = Mathf.Clamp01(rOut - d) - Mathf.Clamp01(rIn - d);
                    tex.SetPixel(xx, yy, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            _ring = tex;
            return tex;
        }

        /// <summary>RTS-кольцо выделения, лежащее на земле (перспективный эллипс из 40 точек).</summary>
        private static void DrawSelectionRings(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            var color = new Color(0.55f, 1f, 0.55f, 0.85f);
            float dotPx = Mathf.Max(2f, 2.6f * (Screen.height / 1440f));
            const int segments = 40;
            int drawn = 0;
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                if (++drawn > 80)
                    break;

                Vector3 center = unit.transform.position;
                center.y = GameApi.GroundY(center.x, center.z);
                float worldRadius = unit.maxRadius * 2.6f;
                if (worldRadius < 4f)
                    worldRadius = 8f;

                var screenPts = new Vector2[segments];
                bool visible = true;
                float sumDist = 0f;
                for (int i = 0; i < segments; i++)
                {
                    float a = i / (float)segments * Mathf.PI * 2f;
                    Vector3 wp = center + new Vector3(Mathf.Cos(a) * worldRadius, 0f, Mathf.Sin(a) * worldRadius);
                    Vector3 sp = cam.WorldToScreenPoint(wp);
                    if (sp.z <= 0f)
                    {
                        visible = false;
                        break;
                    }
                    screenPts[i] = new Vector2(sp.x, Screen.height - sp.y);
                    sumDist += Vector2.Distance(screenPts[i], screenPts[0]);
                }
                if (!visible)
                    continue;
                float avgRadius = sumDist / segments;
                if (avgRadius < 4f || avgRadius > Screen.height)
                    continue;

                if (avgRadius < Settings.SelectionRingMinPx)
                {
                    Vector2 c = Vector2.zero;
                    for (int i = 0; i < segments; i++)
                        c += screenPts[i];
                    c /= segments;
                    float rr = Settings.SelectionRingMinPx;
                    Color prev = GUI.color;
                    GUI.color = color;
                    GUI.DrawTexture(new Rect(c.x - rr, c.y - rr, rr * 2f, rr * 2f), RingTexture());
                    GUI.color = prev;
                    continue;
                }

                int ringDots = 0;
                for (int i = 0; i < segments; i++)
                {
                    if (ringDots > 26)
                        break; // потолок точек на кольцо: плотная обводка больших колец дорого стоит в IMGUI
                    Vector2 a1 = screenPts[i];
                    Vector2 a2 = screenPts[(i + 1) % segments];
                    float len = Vector2.Distance(a1, a2);
                    int dots = Mathf.Max(1, Mathf.CeilToInt(len / 9f));
                    ringDots += dots;
                    for (int k = 0; k < dots; k++)
                    {
                        Vector2 p = Vector2.Lerp(a1, a2, k / (float)dots);
                        DrawRect(new Rect(p.x - dotPx * 0.5f, p.y - dotPx * 0.5f, dotPx, dotPx), color);
                    }
                }
            }
        }

        private static void DrawSelectionBrackets(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            int drawn = 0;
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null || drawn > 80)
                    continue;
                if (GameApi.ApparentSizePx(unit, cam) < Settings.IconMinPx)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float guiY = Screen.height - sp.y;
                DrawCornerBrackets(new Rect(sp.x - 10f, guiY - 10f, 20f, 20f), new Color(0.6f, 1f, 0.6f, 0.95f));
                drawn++;
            }
        }

        private void DrawOrderMarkers(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            float now = Time.unscaledTime;
            controller.MoveOrders.PruneMarkers(now);
            foreach (MoveOrderService.Marker marker in controller.MoveOrders.Markers)
            {
                Vector3 sp = cam.WorldToScreenPoint(marker.Position);
                if (sp.z <= 0f)
                    continue;
                float age = Mathf.Clamp01((now - marker.SpawnedAt) / 2.5f);
                float size = 8f + 26f * age;
                float guiY = Screen.height - sp.y;
                var color = new Color(1f, 0.8f, 0.2f, 1f - age);
                DrawCross(new Vector2(sp.x, guiY), size, color);
            }
        }

        // ---------- маршруты ----------

        // ---------- маршруты: плоскости-сегменты между вейпоинтами; статику не кэшируем (дёшево),
        // движущийся «якорь» юнита пересчитывается сам собой каждый кадр ----------

        internal static Color ModeColor(AirOrderMode mode) =>
            mode switch
            {
                AirOrderMode.Patrol => new Color(0.3f, 0.95f, 0.85f, 1f),
                AirOrderMode.AttackMove => new Color(1f, 0.5f, 0.2f, 1f),
                AirOrderMode.Attack => new Color(1f, 0.25f, 0.2f, 1f),
                _ => new Color(1f, 0.8f, 0.25f, 1f)
            };

        /// <summary>Глобальная точка на земле.</summary>
        private static Vector3 GroundVec(GlobalPosition gp)
        {
            Vector3 l = gp.ToLocalPosition();
            return new Vector3(l.x, GameApi.GroundY(l.x, l.z), l.z);
        }

        /// <summary>Сегмент-плоскость от вейпоинта А к вейпоинту Б: края ±w/2 по XZ,
        /// высоты — по своим точкам (без сэмплирования рельефа между ними).
        /// Сегменты независимы: уход юнита из вида не прячет остальные ноги.</summary>
        /// <summary>Нога маршрута: параметрическое разбиение на мелкие куски (~200 м).
        /// Края Л/П интерполируются общим t — соседние куски делят углы ровно (без швов),
        /// мелкие куски корректно ложатся в перспективу, ближние к камере пропускаются.</summary>
        /// <summary>Нога маршрута: параметрические куски ~200 м с АДАПТИВНОЙ мировой шириной —
        /// ближе к камере ширина в метрах плавно уменьшается, чтобы полоса не превышала
        /// экранный потолок; геометрия остаётся честной (без экранного срезания и искажений).</summary>
        private static void DrawLegQuad(Camera cam, Vector3 a, Vector3 b, Color color, float alpha)
        {
            Vector3 flat = new Vector3(b.x - a.x, 0f, b.z - a.z);
            float len = flat.magnitude;
            if (len < 1f)
                return;
            Vector3 dirW = flat.normalized;
            Vector3 perpW = new Vector3(-dirW.z, 0f, dirW.x);

            int pieces = Mathf.Clamp(Mathf.CeilToInt(len / 200f), 1, 80);
            Color prevColor = GUI.color;
            Matrix4x4 prevMatrix = GUI.matrix;
            var ribbon = new Color(color.r, color.g, color.b, alpha * 0.5f);
            const float minZ = 50f;
            float pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            // Мировая ширина, дающая не более 110px на экране:
            float maxHalfAt(float dist) => Mathf.Min(
                Settings.RouteRibbonWidthM * 0.5f,
                55f * (Screen.height / 1440f) * dist / pxPerRad);

            Vector3 prevL = Vector3.zero, prevR = Vector3.zero;
            bool hasPrev = false;
            for (int i = 0; i <= pieces; i++)
            {
                float t = i / (float)pieces;
                Vector3 c = Vector3.LerpUnclamped(a, b, t);
                Vector3 sp = cam.WorldToScreenPoint(c);
                bool visible = sp.z > minZ;

                // Кромки ТЕКУЩЕГО куска (адаптивная ширина по дистанции).
                float half = maxHalfAt(Mathf.Max(sp.z, 1f));
                Vector3 curL = c - perpW * half;
                Vector3 curR = c + perpW * half;

                if (hasPrev && visible)
                {
                    Vector3 spL = cam.WorldToScreenPoint(prevL);
                    Vector3 spR = cam.WorldToScreenPoint(prevR);
                    Vector3 spCl = cam.WorldToScreenPoint(curL);
                    Vector3 spCr = cam.WorldToScreenPoint(curR);
                    if (spL.z > 0f && spR.z > 0f && spCl.z > 0f && spCr.z > 0f)
                    {
                        Vector2 p1 = new Vector2(spL.x, Screen.height - spL.y);
                        Vector2 p2 = new Vector2(spR.x, Screen.height - spR.y);
                        Vector2 p3 = new Vector2(spCl.x, Screen.height - spCl.y);
                        Vector2 p4 = new Vector2(spCr.x, Screen.height - spCr.y);
                        float minX = Mathf.Min(Mathf.Min(p1.x, p2.x), Mathf.Min(p3.x, p4.x));
                        float maxX = Mathf.Max(Mathf.Max(p1.x, p2.x), Mathf.Max(p3.x, p4.x));
                        float minY = Mathf.Min(Mathf.Min(p1.y, p2.y), Mathf.Min(p3.y, p4.y));
                        float maxY = Mathf.Max(Mathf.Max(p1.y, p2.y), Mathf.Max(p3.y, p4.y));
                        if (maxX - minX < Screen.width * 2f && maxY - minY < Screen.height * 2f)
                        {
                            Vector2 xAxis = p2 - p1;
                            Vector2 yAxis = p3 - p1;
                            if (xAxis.sqrMagnitude > 1e-6f && yAxis.sqrMagnitude > 1e-6f)
                            {
                                var m = Matrix4x4.identity;
                                m.SetColumn(0, new Vector4(xAxis.x, xAxis.y, 0f, 0f));
                                m.SetColumn(1, new Vector4(yAxis.x, yAxis.y, 0f, 0f));
                                m.SetColumn(3, new Vector4(p1.x, p1.y, 0f, 1f));
                                GUI.matrix = m;
                                GUI.color = ribbon;
                                GUI.DrawTexture(new Rect(0f, 0f, 1f, 1f), _white);
                                GUI.matrix = prevMatrix;
                            }
                        }
                    }
                }

                prevL = curL;
                prevR = curR;
                hasPrev = visible || (hasPrev && !visible);
            }
            GUI.color = prevColor;

            // Шеврон: бегущая точка вдоль ноги.
            float ct = Mathf.Repeat(Time.unscaledTime * 0.45f, 1f);
            Vector3 sc = cam.WorldToScreenPoint(Vector3.LerpUnclamped(a, b, ct));
            if (sc.z > minZ)
                DrawRect(new Rect(sc.x - 2f, Screen.height - sc.y - 2f, 4f, 4f),
                    new Color(color.r, color.g, color.b, alpha));
        }

        /// <summary>Вейпоинт с цветом записи очереди (движение/стройка).</summary>
        private static void DrawWorldWaypointColored(Camera cam, Vector3 wp, Color color, float alpha)
        {
            Vector3 sp = cam.WorldToScreenPoint(wp);
            if (sp.z <= 0f)
                return;
            Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
            var r = new Rect(p.x - 10f, p.y - 10f, 20f, 20f);
            DrawBoxOutline(r, 2.5f, new Color(color.r, color.g, color.b, Mathf.Min(1f, alpha + 0.15f)));
            DrawRect(new Rect(p.x - 4f, p.y - 4f, 8f, 8f), new Color(color.r, color.g, color.b, alpha));
        }

        private static void DrawWorldWaypoints(Camera cam, IEnumerable<Vector3> waypoints, Color color, float alpha)
        {
            foreach (Vector3 wp in waypoints)
            {
                Vector3 sp = cam.WorldToScreenPoint(wp);
                if (sp.z <= 0f)
                    continue;
                Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
                var r = new Rect(p.x - 10f, p.y - 10f, 20f, 20f);
                DrawBoxOutline(r, 2.5f, new Color(color.r, color.g, color.b, Mathf.Min(1f, alpha + 0.15f)));
                DrawRect(new Rect(p.x - 4f, p.y - 4f, 8f, 8f), new Color(color.r, color.g, color.b, alpha));
            }
        }

        /// <summary>Индекс вейпоинта под курсором (радиус px) или -1.</summary>
        private static int HoverWaypointIndex(Camera cam, List<Vector3> wps, float radiusPx = 22f)
        {
            Vector3 m = Input.mousePosition;
            for (int i = 0; i < wps.Count; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(wps[i]);
                if (sp.z > 0f && Vector2.Distance(new Vector2(sp.x, sp.y), new Vector2(m.x, m.y)) < radiusPx)
                    return i;
            }
            return -1;
        }

        /// <summary>Ноги цепочки с hover-подсветкой: путь от источника до вейпоинта
        /// под курсором — ярче (SupCom-поведение).</summary>
        private static void DrawChainLegs(Camera cam, Vector3 anchor, List<Vector3> wps,
            Color c, float baseAlpha)
        {
            int hover = HoverWaypointIndex(cam, wps);
            Vector3 prev = anchor;
            for (int i = 0; i < wps.Count; i++)
            {
                float a = hover >= 0 && i <= hover ? 1f : baseAlpha;
                DrawLegQuad(cam, prev, wps[i], c, a);
                prev = wps[i];
            }
        }

        // Переиспользуемые буферы отрисовки маршрутов: OnGUI зовётся 2+ раза за кадр,
        // списки на каждый вызов давили GC (долг §12-8 — частично закрыт).
        private static readonly List<Unit> RouteUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<GlobalPosition>> RouteQueues = new List<IReadOnlyList<GlobalPosition>>();
        private static readonly List<int> RouteIdx = new List<int>();
        private static readonly List<Unit> RouteAirUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<AircraftOrders.OrderSegment>> RouteAirPlans =
            new List<IReadOnlyList<AircraftOrders.OrderSegment>>();
        private static readonly List<Vector3> RouteWps = new List<Vector3>();

        private void DrawAircraftRoutes(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            bool shiftHeld = RtsInput.Shift;

            // Наземка/флот: ноги якорь→wp[qi] и wp[i]→wp[i+1].
            List<Unit> gUnits = RouteUnits;
            List<IReadOnlyList<GlobalPosition>> gQueues = RouteQueues;
            List<int> gIdx = RouteIdx;
            GroundOrders.CollectAll(gUnits, gQueues, gIdx);
            // Групповые вейпоинты (SupCom): у выделенной группы точки одного приказа
            // рисуются ОДНИМ маркером-центроидом, а не россыпью по формации.
            var selectedGround = new List<(Unit unit, IReadOnlyList<GlobalPosition> q, int qi)>();
            foreach (Unit unit in controller.Selection.Selected)
                if (GroundOrders.TryGetQueue(unit, out var sq, out int sqi) && sqi < sq.Count)
                    selectedGround.Add((unit, sq, sqi));
            var groupCentroids = new Dictionary<int, Vector3>(); // индекс в цепочке → центр
            if (selectedGround.Count > 1)
            {
                for (int idx = 0; ; idx++)
                {
                    var cluster = new List<Vector3>();
                    bool full = true;
                    foreach (var m in selectedGround)
                        if (m.qi + idx < m.q.Count)
                            cluster.Add(GroundVec(m.q[m.qi + idx]));
                        else
                        {
                            full = false;
                            break;
                        }
                    if (!full || cluster.Count < 2)
                        break;
                    Vector3 c0 = Vector3.zero;
                    foreach (Vector3 v in cluster)
                        c0 += v;
                    c0 /= cluster.Count;
                    float spread = 0f;
                    foreach (Vector3 v in cluster)
                        spread = Mathf.Max(spread, (v - c0).magnitude);
                    if (spread < 250f)
                        groupCentroids[idx] = c0; // точка «одного приказа» группы
                }
            }

            for (int gi = 0; gi < gUnits.Count; gi++)
            {
                Unit unit = gUnits[gi];
                bool own = controller.Selection.Selected.Contains(unit);
                if (!own && !shiftHeld)
                    continue;
                IReadOnlyList<GlobalPosition> queue = gQueues[gi];
                int qi = gIdx[gi];

                float a = own ? 0.85f : 0.3f;
                Color c = ModeColor(AirOrderMode.Transit);
                Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                anchor.y = GameApi.GroundY(anchor.x, anchor.z);

                List<Vector3> wps = RouteWps;
                wps.Clear();
                for (int i = qi; i < queue.Count; i++)
                    wps.Add(GroundVec(queue[i]));

                if (wps.Count > 0)
                    DrawChainLegs(cam, anchor, wps, c, a);
                // Индивидуальные вейпоинты — кроме групповых (их рисует центроид ниже).
                for (int i = 0; i < wps.Count; i++)
                    if (!groupCentroids.ContainsKey(i))
                        DrawWorldWaypointColored(cam, wps[i], c, a);
            }
            foreach (KeyValuePair<int, Vector3> kv in groupCentroids)
            {
                Vector3 sp = cam.WorldToScreenPoint(kv.Value);
                if (sp.z <= 0f)
                    continue;
                Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
                var r = new Rect(p.x - 13f, p.y - 13f, 26f, 26f);
                DrawBoxOutline(r, 3f, new Color(1f, 0.8f, 0.25f, 0.95f));
                DrawRect(new Rect(p.x - 6f, p.y - 6f, 12f, 12f), new Color(1f, 0.8f, 0.25f, 0.9f));
            }

            // ЕДИНАЯ очередь выделенных инженеров по порядку кликов: движения (оранж)
            // и площадки (бирюза) одной цепочкой. Маркер площадки рисуется ОДИН раз
            // на задачу — сколько бы инженеров её ни строило.
            var drawnSites = new HashSet<BuildTasks.Task>();
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (!GameApi.IsEngineer(unit))
                    continue;
                var seq = BuildTasks.SequenceOf(unit);
                if (seq == null || seq.Count == 0)
                    continue;
                Vector3 prev = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                prev.y = GameApi.GroundY(prev.x, prev.z);
                var pts = new List<Vector3>(seq.Count);
                var cols = new List<Color>(seq.Count);
                foreach (var e in seq)
                {
                    Vector3 lp = (e.Task != null ? e.Task.Site : e.Move).ToLocalPosition();
                    pts.Add(new Vector3(lp.x, GameApi.GroundY(lp.x, lp.z), lp.z));
                    cols.Add(e.Task != null ? ModeColor(AirOrderMode.Patrol) : ModeColor(AirOrderMode.Transit));
                }
                for (int i = 0; i + 1 < pts.Count; i++)
                    DrawLegQuad(cam, pts[i], pts[i + 1], cols[i + 1], 0.85f);
                DrawLegQuad(cam, prev, pts[0], cols[0], 0.85f);
                for (int i = 0; i < pts.Count; i++)
                {
                    var te = seq[i];
                    if (te.Task != null)
                    {
                        if (!drawnSites.Add(te.Task))
                            continue; // уже нарисована другим инженером группы
                    }
                    DrawWorldWaypointColored(cam, pts[i], cols[i], 0.85f);
                }
            }

            // Режим видимости (Shift): единые очереди ВСЕХ инженеров приглушённо.
            if (shiftHeld)
            {
                foreach (Unit unit in UnitRegistry.allUnits)
                {
                    if (unit == null || !GameApi.IsEngineer(unit))
                        continue;
                    var seq = BuildTasks.SequenceOf(unit);
                    if (seq == null || seq.Count == 0)
                        continue;
                    Vector3 prev = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                    prev.y = GameApi.GroundY(prev.x, prev.z);
                    Vector3 lastPt = prev;
                    Color lastCol = ModeColor(AirOrderMode.Transit);
                    for (int ei = 0; ei < seq.Count; ei++)
                    {
                        var e = seq[ei];
                        Vector3 lp = (e.Task != null ? e.Task.Site : e.Move).ToLocalPosition();
                        Vector3 pt = new Vector3(lp.x, GameApi.GroundY(lp.x, lp.z), lp.z);
                        Color c = e.Task != null ? ModeColor(AirOrderMode.Patrol) : ModeColor(AirOrderMode.Transit);
                        DrawLegQuad(cam, lastPt, pt, c, 0.3f);
                        DrawWorldWaypointColored(cam, pt, c, 0.3f);
                        lastPt = pt;
                    }
                }
            }

            // Ралли-маршруты выделенных фабрик (депо и аэродром) — как цепочки наземки.
            Airbase selAirbase = controller.Selection.SelectedAirbase;
            if (selAirbase != null)
            {
                var airRoute = ProductionTasks.RallyOf(selAirbase);
                if (airRoute != null && airRoute.Count > 0)
                {
                    Color ac = ModeColor(AirOrderMode.Transit);
                    Vector3 aAnchor = selAirbase.transform.position;
                    aAnchor.y = GameApi.GroundY(aAnchor.x, aAnchor.z);
                    var aWps = new List<Vector3>(airRoute.Count);
                    foreach (GlobalPosition gp in airRoute)
                        aWps.Add(GroundVec(gp));
                    for (int i = 0; i + 1 < aWps.Count; i++)
                        DrawLegQuad(cam, aWps[i], aWps[i + 1], ac, 0.85f);
                    DrawLegQuad(cam, aAnchor, aWps[0], ac, 0.85f);
                    DrawWorldWaypoints(cam, aWps, ac, 0.85f);
                }
            }
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (!(unit is VehicleDepot depot))
                    continue;
                var route = ProductionTasks.RallyOf(depot);
                if (route == null || route.Count == 0)
                    continue;
                Color c = ModeColor(AirOrderMode.Transit);
                Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                anchor.y = GameApi.GroundY(anchor.x, anchor.z);
                var wps = new List<Vector3>(route.Count);
                foreach (GlobalPosition gp in route)
                    wps.Add(GroundVec(gp));
                for (int i = 0; i + 1 < wps.Count; i++)
                    DrawLegQuad(cam, wps[i], wps[i + 1], c, 0.85f);
                DrawLegQuad(cam, anchor, wps[0], c, 0.85f);
                DrawWorldWaypoints(cam, wps, c, 0.85f);
            }

            // Авиация: планы по сегментам.
            List<Unit> airUnits = RouteAirUnits;
            List<IReadOnlyList<AircraftOrders.OrderSegment>> airPlans = RouteAirPlans;
            AircraftOrders.CollectAll(airUnits, airPlans);
            for (int ai = 0; ai < airUnits.Count; ai++)
            {
                Unit unit = airUnits[ai];
                bool own = controller.Selection.Selected.Contains(unit);
                if (!own && !shiftHeld)
                    continue;
                DrawPlanCurve(cam, unit, airPlans[ai], own ? 0.85f : 0.3f);
            }
        }

        private static void DrawPlanCurve(Camera cam, Unit unit, IReadOnlyList<AircraftOrders.OrderSegment> plan, float alpha)
        {
            Vector3 up = unit.transform.position;
            Vector3 anchor = new Vector3(up.x, GameApi.GroundY(up.x, up.z), up.z);

            foreach (AircraftOrders.OrderSegment seg in plan)
            {
                if (seg == null || (seg.Points.Count == 0 && seg.Target == null))
                    continue;
                Color c = ModeColor(seg.Mode);

                if (seg.Mode == AirOrderMode.Attack && seg.Target != null)
                {
                    Vector3 tp = seg.Target.transform.position;
                    Vector3 tgt = new Vector3(tp.x, GameApi.GroundY(tp.x, tp.z), tp.z);
                    DrawLegQuad(cam, anchor, tgt, c, alpha);
                    Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(seg.Target));
                    if (sp.z > 0f)
                    {
                        Vector2 scr = new Vector2(sp.x, Screen.height - sp.y);
                        float pulse = 10f + 8f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f));
                        DrawCross(scr, pulse * 2f, c);
                        DrawCross(scr, pulse, c);
                    }
                    anchor = tgt;
                    continue;
                }

                var wps = RouteWps;
                wps.Clear();
                for (int i = 0; i < seg.Points.Count; i++)
                    wps.Add(GroundVec(seg.Points[i]));

                if (wps.Count > 0)
                {
                    int hover = HoverWaypointIndex(cam, wps);
                    float a0 = hover >= 0 ? Mathf.Max(alpha, 1f) : alpha;
                    DrawLegQuad(cam, anchor, wps[0], c, a0);
                    for (int i = 0; i + 1 < wps.Count; i++)
                        DrawLegQuad(cam, wps[i], wps[i + 1], c, hover >= i ? Mathf.Max(alpha, 1f) : alpha);
                    if (seg.Mode == AirOrderMode.Patrol && wps.Count > 1)
                        DrawLegQuad(cam, wps[wps.Count - 1], wps[0], c, alpha);
                }
                DrawWorldWaypoints(cam, wps, c, alpha);
                if (wps.Count > 0)
                    anchor = wps[wps.Count - 1];
            }
        }

        // ---------- служебное ----------

        private void DrawNotices()
        {
            float now = Time.unscaledTime;
            for (int i = Notices.Count - 1; i >= 0; i--)
            {
                if (Notices[i].Item2 < now)
                    Notices.RemoveAt(i);
            }
            float y = Screen.height - 140f;
            foreach ((string text, float expires) in Notices)
            {
                float alpha = Mathf.Clamp01(expires - now);
                var prev = _small.normal.textColor;
                var c = prev;
                c.a = alpha;
                _small.normal.textColor = c;
                GUI.Label(new Rect(10f, y, 1000f, 20f), text, _small);
                _small.normal.textColor = prev;
                y -= 20f;
            }
        }

        private void DrawSelfCheckFailures()
        {
            if (SelfCheck.Failures.Count == 0)
                return;
            var rect = new Rect(10f, Screen.height * 0.5f, 460f, 24f + 20f * SelfCheck.Failures.Count);
            GUI.Box(rect, GUIContent.none);
            GUI.Label(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, 20f), "SelfCheck ПРОВАЛЫ:", _small);
            for (int i = 0; i < SelfCheck.Failures.Count; i++)
                GUI.Label(new Rect(rect.x + 8f, rect.y + 24f + 20f * i, rect.width - 16f, 20f), SelfCheck.Failures[i], _small);
        }

        private void DrawDebug(CommanderModeController controller)
        {
            var cam = GameApi.CamManager;
            string pos = cam != null ? cam.transform.position.ToString("F0") : "-";
            int selected = controller != null ? controller.Selection.Selected.Count : 0;
            string camState = controller != null && controller.IsActive && controller.CameraState != null
                ? controller.CameraState.DebugLine
                : "камера: режим командира выключен";
            GUI.Label(new Rect(10f, 140f, 1200f, 20f),
                $"fps {_fps:F0} | юнитов {UnitRegistry.allUnits.Count} | выделено {selected} | камера {pos} | сдвигов origin/сек {GameApi.OriginShiftsPerSecond} | {GameManager.gameState}",
                _small);
            GUI.Label(new Rect(10f, 160f, 1200f, 20f), camState, _small);
            GUI.Label(new Rect(10f, 180f, 1200f, 20f), "драг: " + RouteEditor.LastGrabDebug, _small);
            var sel = controller != null ? controller.Selection : null;
            GUI.Label(new Rect(10f, 200f, 1200f, 20f),
                "клик: " + (sel != null ? sel.LastClickDebug : "-"), _small);
        }

        private static void EnsureStyles()
        {
            if (_label != null)
                return;
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        }

        private static void DrawBoxOutline(Rect r, float thickness, Color color)
        {
            DrawRect(new Rect(r.x, r.y, r.width, thickness), color);
            DrawRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), color);
            DrawRect(new Rect(r.x, r.y, thickness, r.height), color);
            DrawRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), color);
        }

        private static void DrawCornerBrackets(Rect r, Color color)
        {
            const float len = 7f;
            const float th = 2f;
            DrawRect(new Rect(r.x, r.y, len, th), color);
            DrawRect(new Rect(r.x, r.y, th, len), color);
            DrawRect(new Rect(r.xMax - len, r.y, len, th), color);
            DrawRect(new Rect(r.xMax - th, r.y, th, len), color);
            DrawRect(new Rect(r.x, r.yMax - th, len, th), color);
            DrawRect(new Rect(r.x, r.yMax - len, th, len), color);
            DrawRect(new Rect(r.xMax - len, r.yMax - th, len, th), color);
            DrawRect(new Rect(r.xMax - th, r.yMax - len, th, len), color);
        }

        private static void DrawCross(Vector2 center, float size, Color color)
        {
            const float th = 2f;
            DrawRect(new Rect(center.x - size * 0.5f, center.y - th * 0.5f, size, th), color);
            DrawRect(new Rect(center.x - th * 0.5f, center.y - size * 0.5f, th, size), color);
        }

        private static void DrawRect(Rect r, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, _white);
            GUI.color = prev;
        }
    }
}
