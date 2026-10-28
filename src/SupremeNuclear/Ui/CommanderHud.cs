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
            // Кольца дальности: пересчёт 10 Гц тяжёлой работой ВНЕ OnGUI (там только отрисовка).
            var controller = CommanderModeController.Instance;
            if (GameApi.InMission && controller != null && controller.IsActive)
                RangeRings.Tick(controller);
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
                $"SUPREME NUCLEAR v{PluginInfo.Version}  |  " + Loc.F("Режим командира: {0}  |  {1} — переключить",
                    active ? "<color=lime>" + Loc.Tr("ВКЛ") + "</color>" : Loc.Tr("ВЫКЛ"),
                    Settings.ToggleCommanderKey),
                _label);

            if (active)
            {
                // Тяжёлая отрисовка — ТОЛЬКО на Repaint: OnGUI вызывается 2-4 раза за
                // кадр (Layout + Repaint + события мыши/клавиатуры), а пиксели пишет
                // только Repaint — остальные проходы гнали сотни проекций/физлучей
                // GroundY вхолостую (лагало при большом числе вейпоинтов). GUILayout
                // не используется — пропуск Layout-проходов безопасен.
                if (Event.current.type == EventType.Repaint)
                {
                    SelectionService selection = controller.Selection;

                    if (selection.IsDragging)
                        DrawBoxOutline(selection.DragRectGui, 1.5f, new Color(0.6f, 1f, 0.6f, 0.9f));

                    RangeRings.Draw(); // кольца дальности — самый нижний слой
                    DrawDepositMarkers();
                    DrawStrategicIcons(controller);
                    DrawHpBars();
                    DrawBuildMarkers();
                    DrawSelectionRings(controller);
                    DrawSelectionBrackets(controller);
                    DrawInspectedBrackets(controller);
                    DrawAircraftRoutes(controller);
                    DrawOrderMarkers(controller);
                    DrawFanTargets(controller);
                }
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

        /// <summary>Точечный мировой элемент под окном миникарты? IMGUI рисуется ПОВЕРХ
        /// uGUI-окна — без маски экранные иконки мира штампуются на миникарту и
        /// смешиваются с её блипами (позиция — в GUI-координатах, y сверху).</summary>
        private static bool UnderMinimapWindow(Vector2 guiPos)
        {
            Rect m = MinimapPanel.HudMaskRect;
            return m.width > 1f && m.height > 1f && m.Contains(guiPos);
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
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
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
                        Loc.Tr("зона не расчищена"), _small);
                float p = t.Progress01;
                var fill = new Rect(r.x + 3f * k, r.yMax - 3f * k - (r.height - 6f * k) * p,
                    r.width - 6f * k, (r.height - 6f * k) * p);
                DrawRect(fill, new Color(c.r, c.g, c.b, 0.28f));
                GUI.Label(new Rect(sp.x - 140f, gy - s * 0.5f - 20f * k, 280f, 18f * k),
                    Loc.F("{0} {1}",
                        t.Definition != null ? t.Definition.unitName : t.IsRunway ? Loc.Tr("ВПП") : "?",
                        p.ToString("P0")), _small);
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
                        Loc.F("{0} · ЛКМ — построить (Shift — серия) · ПКМ — отмена", tpl.name), _small);
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
                Loc.F("{0} — {1} · ЛКМ — поставить (удержание — поворот) · ПКМ — отмена",
                    BuildTasks.Pending.unitName, BuildTasks.Pending.value.ToString("F0")), _small);
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
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
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
                if (UnderMinimapWindow(new Vector2(gx, gy)))
                    continue;
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
                // Тумблеры аэродрома (DESIGN §2.11b): своё закрытие видно на ромбе —
                // внизу метки «посадка закрыта»/«взлёт закрыт» (только свои, не разведка).
                if (owner == hq)
                {
                    int line = 0;
                    if (!AirbaseAccess.IsLandingOpen(airbase))
                        ClosedLabel(gx, gy + d * 0.7f + line++ * 14f, Loc.Tr("посадка закрыта"));
                    if (!AirbaseAccess.IsTakeoffOpen(airbase))
                        ClosedLabel(gx, gy + d * 0.7f + line * 14f, Loc.Tr("взлёт закрыт"));
                }
            }
        }

        /// <summary>Цвет метки закрытого режима аэродрома (тумблеры §2.11b).</summary>
        private static readonly Color ClosedTint = new Color(1f, 0.6f, 0.15f, 0.95f);

        /// <summary>Подпись под ромбом аэродрома: стиль возвращаем (общий _small).</summary>
        private static void ClosedLabel(float cx, float y, string text)
        {
            var prevColor = _small.normal.textColor;
            var prevAlign = _small.alignment;
            _small.normal.textColor = ClosedTint;
            _small.alignment = TextAnchor.MiddleCenter;
            GUI.Label(new Rect(cx - 70f, y, 140f, 16f), text, _small);
            _small.normal.textColor = prevColor;
            _small.alignment = prevAlign;
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
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
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
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

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
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

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

        private const int RingSegments = 40;
        private static readonly Vector2[] RingScratch = new Vector2[RingSegments];

        /// <summary>RTS-кольцо выделения (перспективный эллипс из точек):
        /// корабли/наземка/здания — «пояс» на середине высоты модели, у авиации — по земле.
        /// Точки кладутся равномерно по ВСЕЙ окружности — фиксированный бюджет точек
        /// обрезал дугу у крупных юнитов (кольцо корабля рисовалось на треть).</summary>
        private static void DrawSelectionRings(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            var color = new Color(0.55f, 1f, 0.55f, 0.85f);
            float dotPx = Mathf.Max(2f, 2.6f * (Screen.height / 1440f));
            int drawn = 0;
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                if (++drawn > 80)
                    break;

                Vector3 center = unit.transform.position;
                center.y = unit is Aircraft
                    ? GameApi.GroundY(center.x, center.z)
                    : center.y + GameApi.RingBeltOffset(unit);
                float worldRadius = unit.maxRadius * 2.6f;
                if (worldRadius < 4f)
                    worldRadius = 8f;

                // Кольцо не штампуем поверх окна миникарты (гасим по центру).
                Vector3 csp = cam.WorldToScreenPoint(center);
                if (csp.z > 0f && UnderMinimapWindow(new Vector2(csp.x, Screen.height - csp.y)))
                    continue;

                Vector2[] screenPts = RingScratch;
                bool visible = true;
                for (int i = 0; i < RingSegments; i++)
                {
                    float a = i / (float)RingSegments * Mathf.PI * 2f;
                    Vector3 wp = center + new Vector3(Mathf.Cos(a) * worldRadius, 0f, Mathf.Sin(a) * worldRadius);
                    Vector3 sp = cam.WorldToScreenPoint(wp);
                    if (sp.z <= 0f)
                    {
                        visible = false;
                        break;
                    }
                    screenPts[i] = new Vector2(sp.x, Screen.height - sp.y);
                }
                if (!visible)
                    continue;
                float perimeter = 0f;
                for (int i = 0; i < RingSegments; i++)
                    perimeter += Vector2.Distance(screenPts[i], screenPts[(i + 1) % RingSegments]);
                float meanRadius = perimeter / (2f * Mathf.PI);
                if (meanRadius < 4f || meanRadius > Screen.height)
                    continue;

                if (meanRadius < Settings.SelectionRingMinPx)
                {
                    Vector2 c = Vector2.zero;
                    for (int i = 0; i < RingSegments; i++)
                        c += screenPts[i];
                    c /= RingSegments;
                    float rr = Settings.SelectionRingMinPx;
                    Color prev = GUI.color;
                    GUI.color = color;
                    GUI.DrawTexture(new Rect(c.x - rr, c.y - rr, rr * 2f, rr * 2f), RingTexture());
                    GUI.color = prev;
                    continue;
                }

                // Равномерный шаг по всему кругу (шаг растёт с радиусом: плотная обводка
                // больших колец дорого стоит в IMGUI), но дуга всегда полная.
                int dots = Mathf.Clamp(Mathf.CeilToInt(perimeter / 9f), RingSegments, 80);
                for (int k = 0; k < dots; k++)
                {
                    float t = k * RingSegments / (float)dots;
                    int i = (int)t;
                    Vector2 p = Vector2.Lerp(screenPts[i], screenPts[(i + 1) % RingSegments], t - i);
                    DrawRect(new Rect(p.x - dotPx * 0.5f, p.y - dotPx * 0.5f, dotPx, dotPx), color);
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
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
                DrawCornerBrackets(new Rect(sp.x - 10f, guiY - 10f, 20f, 20f), new Color(0.6f, 1f, 0.6f, 0.95f));
                drawn++;
            }
        }

        /// <summary>Скобки осмотра на инспектируемой чужой цели/ракете (красные,
        /// крупнее скобок выделения): якорь совпадает с иконкой контакта — протухший
        /// контакт отмечается там, где он виделся в последний раз.</summary>
        private static void DrawInspectedBrackets(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            Unit unit = controller.Selection.Inspected;
            if (cam == null || unit == null)
                return;
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            TrackingInfo info = unit is Missile ? null : hq?.GetTrackingData(unit.persistentID);
            Vector3 sp = cam.WorldToScreenPoint(GameApi.ContactAnchorWorld(unit, info));
            if (sp.z <= 0f)
                return;
            float guiY = Screen.height - sp.y;
            if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                return;
            DrawCornerBrackets(new Rect(sp.x - 13f, guiY - 13f, 26f, 26f), new Color(1f, 0.4f, 0.35f, 0.95f));
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
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;
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
                AirOrderMode.Salvo => new Color(0.95f, 0.3f, 0.8f, 1f),
                AirOrderMode.Escort => new Color(0.55f, 0.95f, 0.4f, 1f),
                AirOrderMode.Rtb => new Color(0.55f, 0.7f, 1f, 1f),
                _ => new Color(1f, 0.8f, 0.25f, 1f)
            };

        /// <summary>Глобальная точка на земле.</summary>
        private static Vector3 GroundVec(GlobalPosition gp)
        {
            Vector3 l = gp.ToLocalPosition();
            return new Vector3(l.x, GameApi.GroundY(l.x, l.z), l.z);
        }

        /// <summary>Лента цепочки вейпоинтов: углы скруглены (Util.RoundChainCorners),
        /// вся цепочка — ОДНА непрерывная полоса экраных трапеций (соседние куски делят
        /// вершины — швов и «лесенок» на стыках ног нет), полуширина адаптивна к
        /// дистанции до камеры, вдоль всей цепочки бежит шеврон-точка.
        /// legCols/legAlphas — по одной записи на ногу цепочки: цвет и яркость
        /// (hover-подсветка) переключаются на границе ног.</summary>
        private static void DrawChainRibbon(Camera cam, IReadOnlyList<Vector3> pts,
            List<Color> legCols, List<float> legAlphas)
        {
            Color prevColor = GUI.color;
            Matrix4x4 prevMatrix = GUI.matrix;
            const float minZ = 50f;
            float pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            // Полуширина (SupCom-принцип): у камеры — ТОНКАЯ экранная линия
            // (~24px на 1440p, но не тоньше 1.5 м мира), с дистанцией плавно растёт
            // до мирового размера RouteRibbonWidthM (стратегический зум). Прежний
            // экраный потолок 110px прижимал ленту к константе на ЛЮБОМ приближении —
            // рядом с юнитами она была нереально широкой.
            float maxHalfAt(float dist) => Mathf.Min(
                Settings.RouteRibbonWidthM * 0.5f,
                Mathf.Max(1.5f, 12f * (Screen.height / 1440f) * dist / pxPerRad));

            // Радиус скругления — с экраным минимумом: мировые 120 м на стратегическом
            // зуме — пара пикселей, угол без этого выглядит лесенкой. Как и ширина,
            // скругление подстраивается под камеру; настройка — нижний порог.
            float camDist = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float d = (cam.transform.position - pts[i]).magnitude;
                if (d < camDist)
                    camDist = d;
            }
            float radiusEff = Mathf.Max(Settings.RouteCornerRadiusM,
                28f * (Screen.height / 1440f) * camDist / pxPerRad);
            int n = Util.RoundChainCorners(pts, radiusEff, RibbonPts, RibbonLegs);
            if (n < 2)
                return;

            // Сечение ленты в точке j: перпендикуляр из соседних точек, полуширина по дистанции.
            Vector3 Section(int j, out Vector3 left, out Vector3 right)
            {
                Vector3 c = RibbonPts[j];
                Vector3 prev = RibbonPts[j > 0 ? j - 1 : 0];
                Vector3 next = RibbonPts[j + 1 < n ? j + 1 : n - 1];
                Vector3 dir = next - prev;
                Vector3 flat = new Vector3(dir.x, 0f, dir.z);
                Vector3 perp = flat.sqrMagnitude < 1e-6f
                    ? Vector3.right
                    : new Vector3(-flat.z, 0f, flat.x).normalized;
                Vector3 sp = cam.WorldToScreenPoint(c);
                float half = maxHalfAt(Mathf.Max(sp.z, 1f));
                left = c - perp * half;
                right = c + perp * half;
                return sp;
            }

            void Quad(Vector3 aL, Vector3 aR, Vector3 bL, Vector3 bR, Color col)
            {
                Vector3 spL = cam.WorldToScreenPoint(aL);
                Vector3 spR = cam.WorldToScreenPoint(aR);
                Vector3 spCl = cam.WorldToScreenPoint(bL);
                Vector3 spCr = cam.WorldToScreenPoint(bR);
                if (spL.z <= 0f || spR.z <= 0f || spCl.z <= 0f || spCr.z <= 0f)
                    return;
                Vector2 p1 = new Vector2(spL.x, Screen.height - spL.y);
                Vector2 p2 = new Vector2(spR.x, Screen.height - spR.y);
                Vector2 p3 = new Vector2(spCl.x, Screen.height - spCl.y);
                Vector2 p4 = new Vector2(spCr.x, Screen.height - spCr.y);
                // Соседние куски идут через РАЗНЫЕ GUI.matrix: общие вершины расходятся
                // на доли пикселя, и на полупрозрачной ленте проступают светлые щели.
                // Расширяем квад от центроида на полпикселя — щель закрывает лёгкое
                // перекрытие (чуть темнее линия вместо разрыва).
                Vector2 cen = (p1 + p2 + p3 + p4) * 0.25f;
                Vector2 Expand(Vector2 p)
                {
                    Vector2 d = p - cen;
                    return p + d * (0.5f / Mathf.Max(d.magnitude, 0.01f));
                }
                p1 = Expand(p1);
                p2 = Expand(p2);
                p3 = Expand(p3);
                p4 = Expand(p4);
                float minX = Mathf.Min(Mathf.Min(p1.x, p2.x), Mathf.Min(p3.x, p4.x));
                float maxX = Mathf.Max(Mathf.Max(p1.x, p2.x), Mathf.Max(p3.x, p4.x));
                float minY = Mathf.Min(Mathf.Min(p1.y, p2.y), Mathf.Min(p3.y, p4.y));
                float maxY = Mathf.Max(Mathf.Max(p1.y, p2.y), Mathf.Max(p3.y, p4.y));
                if (maxX - minX >= Screen.width * 2f || maxY - minY >= Screen.height * 2f)
                    return;
                Vector2 xAxis = p2 - p1;
                Vector2 yAxis = p3 - p1;
                if (xAxis.sqrMagnitude <= 1e-6f || yAxis.sqrMagnitude <= 1e-6f)
                    return;
                var m = Matrix4x4.identity;
                m.SetColumn(0, new Vector4(xAxis.x, xAxis.y, 0f, 0f));
                m.SetColumn(1, new Vector4(yAxis.x, yAxis.y, 0f, 0f));
                m.SetColumn(3, new Vector4(p1.x, p1.y, 0f, 1f));
                GUI.matrix = m;
                GUI.color = col;
                GUI.DrawTexture(new Rect(0f, 0f, 1f, 1f), _white);
                GUI.matrix = prevMatrix;
            }

            float total = 0f;
            for (int i = 1; i < n; i++)
                total += Vector3.Distance(RibbonPts[i - 1], RibbonPts[i]);
            float chevronAt = Mathf.Repeat(Time.unscaledTime * 0.45f, 1f) * total;
            bool chevronDone = total <= 0f;

            int legCount = legCols.Count;
            int legIdx = 0;
            Vector3 prevL = Vector3.zero, prevR = Vector3.zero, prevC = RibbonPts[0];
            bool hasPrev = false;
            float passed = 0f;

            for (int i = 1; i < n; i++)
            {
                Vector3 a = RibbonPts[i - 1];
                Vector3 b = RibbonPts[i];
                while (legIdx + 1 < legCount && i - 1 >= RibbonLegs[legIdx + 1])
                    legIdx++;
                Color c = legIdx < legCount ? legCols[legIdx] : Color.white;
                float alpha = legIdx < legAlphas.Count ? legAlphas[legIdx] : 1f;
                var ribbon = new Color(c.r, c.g, c.b, alpha * 0.5f);

                float xzLen = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (xzLen < 1e-3f)
                    continue;
                int pieces = Mathf.Clamp(Mathf.CeilToInt(xzLen / 200f), 1, 80);
                Vector3 spA = Section(i - 1, out Vector3 startL, out Vector3 startR);
                Section(i, out Vector3 endL, out Vector3 endR);
                for (int s = 1; s <= pieces; s++)
                {
                    float t = s / (float)pieces;
                    Vector3 curC = Vector3.LerpUnclamped(a, b, t);
                    Vector3 curL = Vector3.LerpUnclamped(startL, endL, t);
                    Vector3 curR = Vector3.LerpUnclamped(startR, endR, t);
                    Vector3 sp = cam.WorldToScreenPoint(curC);
                    bool visible = sp.z > minZ;

                    if (hasPrev && visible)
                        Quad(prevL, prevR, curL, curR, ribbon);

                    // Шеврон: бегущая точка вдоль всей цепочки.
                    passed += Vector3.Distance(prevC, curC);
                    if (!chevronDone && passed >= chevronAt)
                    {
                        chevronDone = true;
                        if (sp.z > minZ)
                            DrawRect(new Rect(sp.x - 2f, Screen.height - sp.y - 2f, 4f, 4f),
                                new Color(c.r, c.g, c.b, alpha));
                    }
                    prevC = curC;
                    prevL = curL;
                    prevR = curR;
                    if (visible)
                        hasPrev = true;
                }
            }
            GUI.color = prevColor;
        }

        /// <summary>Вейпоинт с цветом записи очереди (движение/стройка).</summary>
        private static void DrawWorldWaypointColored(Camera cam, Vector3 wp, Color color, float alpha)
        {
            Vector3 sp = cam.WorldToScreenPoint(wp);
            if (sp.z <= 0f)
                return;
            Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
            if (UnderMinimapWindow(p))
                return;
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
                if (UnderMinimapWindow(p))
                    continue;
                var r = new Rect(p.x - 10f, p.y - 10f, 20f, 20f);
                DrawBoxOutline(r, 2.5f, new Color(color.r, color.g, color.b, Mathf.Min(1f, alpha + 0.15f)));
                DrawRect(new Rect(p.x - 4f, p.y - 4f, 8f, 8f), new Color(color.r, color.g, color.b, alpha));
            }
        }

        /// <summary>Индекс вейпоинта под курсором (единый пик-радиус вейпоинтов) или -1.</summary>
        private static int HoverWaypointIndex(Camera cam, List<Vector3> wps)
        {
            float radiusPx = Settings.WaypointPickPx;
            Vector3 m = RtsInput.MousePos;
            for (int i = 0; i < wps.Count; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(wps[i]);
                if (sp.z > 0f && Vector2.Distance(new Vector2(sp.x, sp.y), new Vector2(m.x, m.y)) < radiusPx)
                    return i;
            }
            return -1;
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
        // Лента цепочки: точки/цвета/яркости ног собираются в переиспользуемые буферы
        // (OnGUI 2+ раза за кадр — без аллокаций), сглаженная полоса — в RibbonPts/RibbonLegs.
        private static readonly List<Vector3> ChainPts = new List<Vector3>();
        private static readonly List<Color> ChainCols = new List<Color>();
        private static readonly List<float> ChainAlphas = new List<float>();
        private static readonly List<Vector3> RibbonPts = new List<Vector3>();
        private static readonly List<int> RibbonLegs = new List<int>();
        // Ралли фабрик: точки текущего маршрута + сплющенные «уже нарисованные в кадре»
        // маршруты с их длинами — дедуп общего ралли пачки фабрик (без аллокаций).
        private static readonly List<Vector3> RallyWps = new List<Vector3>();
        private static readonly List<Vector3> RallySeenPts = new List<Vector3>();
        private static readonly List<int> RallySeenLens = new List<int>();
        // Все фабрики с живыми ралли (Shift-режим видимости невыделенных).
        private static readonly List<object> RallyAll = new List<object>();

        /// <summary>Ралли-маршрут фабрики: лента + вейпоинты от корпуса; маршрут,
        /// уже нарисованный в этом кадре (общее ралли пачки фабрик), не дублируется.</summary>
        private static void DrawFactoryRally(Camera cam, Vector3 anchorPos, object factory, Color c, float a)
        {
            var route = ProductionTasks.RallyOf(factory);
            if (route == null || route.Count == 0)
                return;
            RallyWps.Clear();
            foreach (GlobalPosition gp in route.Points)
                RallyWps.Add(GroundVec(gp));
            if (Util.RouteSeenBefore(RallySeenPts, RallySeenLens, RallyWps))
                return;
            RallySeenPts.AddRange(RallyWps);
            RallySeenLens.Add(RallyWps.Count);
            Vector3 anchor = new Vector3(anchorPos.x, 0f, anchorPos.z);
            anchor.y = GameApi.GroundY(anchor.x, anchor.z);
            ChainPts.Clear();
            ChainCols.Clear();
            ChainAlphas.Clear();
            ChainPts.Add(anchor);
            for (int i = 0; i < RallyWps.Count; i++)
            {
                ChainPts.Add(RallyWps[i]);
                ChainCols.Add(c);
                ChainAlphas.Add(a);
            }
            DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
            DrawWorldWaypoints(cam, RallyWps, c, a);
        }

        /// <summary>Якорь ралли-ленты фабрики (её корпус); мёртвая/чужеродная
        /// запись реестра ралли якоря не даёт — null.</summary>
        private static Vector3? RallyAnchor(object factory)
        {
            switch (factory)
            {
                case VehicleDepot depot:
                    return depot != null && !depot.disabled
                        ? new Vector3?(depot.transform.position) : null;
                case Hangar hangar:
                    if (hangar == null || hangar.Disabled)
                        return null;
                    Unit hu = hangar.attachedUnit;
                    return hu != null ? hu.transform.position : hangar.transform.position;
                case Building dock:
                    return BuildTasks.IsDock(dock)
                        ? (Vector3?)dock.transform.position : null;
                case Airbase airbase:
                    return airbase != null ? new Vector3?(airbase.transform.position) : null;
                default:
                    return null;
            }
        }
        // Групповые вейпоинты по groupId: групповая точка — свойство точки, не
        // выделения. Накопители центроидов (xyz = сумма, w = счётчик), дедуп общих
        // сегментов/маркеров/входов между юнитами (gid из одного счётчика — общие).
        private static readonly Dictionary<int, Vector4> GidPointSums = new Dictionary<int, Vector4>();
        private static readonly Dictionary<int, Vector4> GidAnchorSums = new Dictionary<int, Vector4>();
        private static readonly HashSet<int> SeenGids = new HashSet<int>();
        private static readonly HashSet<long> DrawnGidLegs = new HashSet<long>();
        private static readonly HashSet<int> DrawnGidMarkers = new HashSet<int>();
        private static readonly HashSet<int> DrawnGidEntries = new HashSet<int>();
        private static readonly List<Vector3> ChainNodes = new List<Vector3>();
        private static readonly List<int> NodeGids = new List<int>();
        // Смешанная очередь наземки: цель-узел (null — точка) и «владелец» ноги к цели
        // (рисует и крест — дедуп на цель для всей группы приказа).
        private static readonly List<Unit> ChainNodeTargets = new List<Unit>();
        private static readonly List<bool> ChainNodeOwned = new List<bool>();
        private static readonly HashSet<Unit> DrawnTargetExits = new HashSet<Unit>();
        // Очереди инженеров (все с записями — групповое слияние не зависит от выделения).
        private static readonly List<Unit> EngSeqUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<BuildTasks.SeqEntry>> EngSeqs =
            new List<IReadOnlyList<BuildTasks.SeqEntry>>();
        private static readonly HashSet<BuildTasks.Task> DrawnSites = new HashSet<BuildTasks.Task>();
        private static readonly HashSet<int> DrawnGroupMoves = new HashSet<int>();
        private static readonly HashSet<Unit> DrawnGroundTargets = new HashSet<Unit>();
        // Связи сопровождения (наземка + авиа-эскорт): маркер на ЦЕЛИ — один на цель.
        private static readonly List<Unit> EscortUnits = new List<Unit>();
        private static readonly List<Unit> EscortTargetsBuf = new List<Unit>();
        private static readonly HashSet<Unit> DrawnEscortMarkers = new HashSet<Unit>();
        // Групповые точки планов авиации: центроид группы бортов + дедуп маркеров.
        private static readonly Dictionary<int, Vector4> AirGidCentroids = new Dictionary<int, Vector4>();
        private static readonly HashSet<int> DrawnAirGid = new HashSet<int>();
        // SupCom-магистраль авиации (v0.91, зеркально наземке): узел группы —
        // центроид формационных точек, вход — от центроида якорей членов, сегменты
        // магистрали/входы/ноги атак и RTB — по одному разу на приказ.
        private static readonly Dictionary<int, Vector4> AirGidAnchors = new Dictionary<int, Vector4>();
        private static readonly HashSet<int> SeenAirGids = new HashSet<int>();
        private static readonly HashSet<long> DrawnAirLegs = new HashSet<long>();
        private static readonly HashSet<int> DrawnAirEntries = new HashSet<int>();
        private static readonly HashSet<Unit> DrawnAirTargets = new HashSet<Unit>();
        private static readonly HashSet<Airbase> DrawnRtbBases = new HashSet<Airbase>();

        private static long PairKey(int a, int b) => (long)a << 32 | (uint)b;

        /// <summary>Групповой маркер-центроид (26px) — один на приказ группы.</summary>
        private static void DrawGroupWaypointMarker(Camera cam, Vector3 world)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f)
                return;
            Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
            if (UnderMinimapWindow(p))
                return;
            var r = new Rect(p.x - 13f, p.y - 13f, 26f, 26f);
            DrawBoxOutline(r, 3f, new Color(1f, 0.8f, 0.25f, 0.95f));
            DrawRect(new Rect(p.x - 6f, p.y - 6f, 12f, 12f), new Color(1f, 0.8f, 0.25f, 0.9f));
        }

        /// <summary>Цепочка наземки с групповым слиянием: групповые точки (groupId≠0)
        /// заменяются центроидами группы приказа; сегменты между соседними групповыми
        /// точками дедуплицированы между юнитами (магистраль рисует первый встретивший);
        /// вход в группу — ОДИН от центроида якорей её членов; индивидуальные точки —
        /// личные ветки схождения/расхождения. Цели атаки — узлы очереди НА СВОЕЙ
        /// позиции серии (v0.73.2): лента течёт ЧЕРЕЗ цель (как планы авиации и SupCom),
        /// нога до цели и нога от неё — по одному разу на цель (группа бьёт по ней
        /// вместе); текущая цель погони — яркая, с пульсирующим крестом, ждущие свою
        /// позицию — приглушённые, со статичным крестом. Hover подсвечивает путь до узла.</summary>
        private static void DrawGroundChain(Camera cam, Unit unit, IReadOnlyList<GlobalPosition> queue,
            int qi, Color c, float a, bool loop = false)
        {
            // Узлы очереди В ПОРЯДКЕ КЛИКОВ: дозревшие/текущие цели — в голове,
            // дальше точки, с целями, вставленными после своей позиции серии.
            var targets = GroundOrders.TargetQueueOf(unit);
            ChainNodes.Clear();
            NodeGids.Clear();
            ChainNodeTargets.Clear();
            ChainNodeOwned.Clear();
            if (targets != null)
                for (int q = 0; q < targets.Count; q++)
                {
                    if (GroundOrders.TargetAfterOf(unit, q) > qi)
                        continue;
                    Unit t = targets[q];
                    if (t == null || t.disabled)
                        continue;
                    ChainNodes.Add(TargetGround(t));
                    NodeGids.Add(0);
                    ChainNodeTargets.Add(t);
                    ChainNodeOwned.Add(false);
                }
            for (int i = qi; i < queue.Count; i++)
            {
                // Групповая точка — узел-центроид группы приказа, индивидуальная — своя.
                int gid = GroundOrders.GroupIdOf(unit, i);
                Vector3 wp = GroundVec(queue[i]);
                ChainNodes.Add(gid != 0 && GidPointSums.TryGetValue(gid, out Vector4 acc)
                    ? new Vector3(acc.x / acc.w, acc.y / acc.w, acc.z / acc.w)
                    : wp);
                NodeGids.Add(gid);
                ChainNodeTargets.Add(null);
                ChainNodeOwned.Add(false);
                if (targets == null)
                    continue;
                for (int q = 0; q < targets.Count; q++)
                {
                    if (GroundOrders.TargetAfterOf(unit, q) != i + 1)
                        continue;
                    Unit t = targets[q];
                    if (t == null || t.disabled)
                        continue;
                    ChainNodes.Add(TargetGround(t));
                    NodeGids.Add(0);
                    ChainNodeTargets.Add(t);
                    ChainNodeOwned.Add(false);
                }
            }
            if (ChainNodes.Count == 0)
                return;

            Color attackC = ModeColor(AirOrderMode.Attack);
            bool pursuing = GroundOrders.KindOf(unit) == GroundOrderKind.Attack;
            Unit curTarget = GroundOrders.TargetOf(unit);

            Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
            anchor.y = GameApi.GroundY(anchor.x, anchor.z);

            int hover = HoverWaypointIndex(cam, ChainNodes);
            ChainPts.Clear();
            ChainCols.Clear();
            ChainAlphas.Clear();
            Vector3 prev = anchor;
            int prevGid = 0;
            Unit prevTarget = null;
            for (int i = 0; i < ChainNodes.Count; i++)
            {
                Vector3 node = ChainNodes[i];
                int gid = NodeGids[i];
                Unit nodeTarget = ChainNodeTargets[i];
                bool drawLeg = true;
                Color legC = c;
                float legA = a;
                if (nodeTarget != null)
                {
                    // Нога ДО цели — один раз на цель; владелец ноги рисует и крест.
                    drawLeg = DrawnGroundTargets.Add(nodeTarget);
                    ChainNodeOwned[i] = drawLeg;
                    legC = attackC;
                    legA = pursuing && nodeTarget == curTarget ? a : a * 0.6f;
                }
                else if (prevTarget != null)
                    // Нога ОТ цели — тоже один раз на цель (выход из боевого узла).
                    drawLeg = DrawnTargetExits.Add(prevTarget);
                else if (i == 0 && gid != 0)
                {
                    // Вход в группу — от центроида якорей её членов, один раз на группу.
                    if (GidAnchorSums.TryGetValue(gid, out Vector4 aa))
                    {
                        prev = new Vector3(aa.x / aa.w, 0f, aa.z / aa.w);
                        prev.y = GameApi.GroundY(prev.x, prev.z);
                    }
                    drawLeg = DrawnGidEntries.Add(gid);
                }
                else if (prevGid != 0 && gid != 0)
                    // Сегмент магистрали: рисует первый встретивший юнит группы.
                    drawLeg = DrawnGidLegs.Add(PairKey(prevGid, gid));

                if (drawLeg)
                {
                    if (ChainPts.Count == 0)
                        ChainPts.Add(prev);
                    ChainPts.Add(node);
                    ChainCols.Add(legC);
                    ChainAlphas.Add(hover >= i ? Mathf.Max(legA, 1f) : legA);
                }
                else if (ChainPts.Count > 0)
                {
                    // Разрыв: общий сегмент уже нарисован другим членом группы.
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                }
                prev = node;
                prevGid = gid;
                prevTarget = nodeTarget;
            }
            // Замыкающая нога патруля: последний узел → первый. Магистральные
            // групповые сегменты дедуплицированы тем же PairKey (каждый член
            // группы не рисует её дважды), индивидуальные — своя у каждого.
            if (loop && ChainNodes.Count >= 2)
            {
                int lastGid = NodeGids[NodeGids.Count - 1];
                int firstGid = NodeGids[0];
                bool closeLeg = lastGid != 0 && firstGid != 0
                    ? DrawnGidLegs.Add(PairKey(lastGid, firstGid))
                    : true;
                if (closeLeg)
                {
                    if (ChainPts.Count == 0)
                    {
                        ChainPts.Add(ChainNodes[ChainNodes.Count - 1]);
                        ChainCols.Add(c);
                        ChainAlphas.Add(a);
                    }
                    ChainPts.Add(ChainNodes[0]);
                    ChainCols.Add(c);
                    ChainAlphas.Add(a);
                }
            }
            if (ChainPts.Count >= 2)
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);

            // Маркеры: групповые — один на приказ (в центроиде), индивидуальные — свои;
            // цели — крестики: текущая погоня — пульсирующий двойной, ждущие — статичные.
            for (int i = 0; i < ChainNodes.Count; i++)
            {
                Unit t = ChainNodeTargets[i];
                if (t != null)
                {
                    if (!ChainNodeOwned[i])
                        continue; // крест уже нарисовал владелец ноги к этой цели
                    Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(t));
                    if (sp.z <= 0f)
                        continue;
                    var scr = new Vector2(sp.x, Screen.height - sp.y);
                    if (pursuing && t == curTarget)
                    {
                        float pulse = 10f + 8f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f));
                        DrawCross(scr, pulse * 2f, attackC);
                        DrawCross(scr, pulse, attackC);
                    }
                    else
                        DrawCross(scr, 14f, attackC);
                    continue;
                }
                if (NodeGids[i] != 0)
                {
                    if (DrawnGidMarkers.Add(NodeGids[i]))
                        DrawGroupWaypointMarker(cam, ChainNodes[i]);
                }
                else
                    DrawWorldWaypointColored(cam, ChainNodes[i], c, a);
            }
        }

        /// <summary>Наземная точка цели — на рельефе, под её корпусом.</summary>
        private static Vector3 TargetGround(Unit target)
        {
            Vector3 tp = target.transform.position;
            return new Vector3(tp.x, GameApi.GroundY(tp.x, tp.z), tp.z);
        }

        /// <summary>Связи сопровождения наземки/флота (v0.97): лента «эскорт → цель»
        /// и ЗЕЛЁНЫЙ маркер на цели (один на цель; дедуп общий с авиа-эскортом —
        /// DrawPlanCurve). Выделенные ярко, при Shift и остальные приглушённо —
        /// как цепочки.</summary>
        private static void DrawEscortLinks(Camera cam, CommanderModeController controller,
            bool shiftHeld)
        {
            GroundOrders.CollectEscorts(EscortUnits, EscortTargetsBuf);
            if (EscortUnits.Count == 0)
                return;
            Color c = ModeColor(AirOrderMode.Escort);
            for (int i = 0; i < EscortUnits.Count; i++)
            {
                Unit unit = EscortUnits[i];
                bool own = controller.Selection.Selected.Contains(unit);
                if (!own && !shiftHeld)
                    continue;
                Unit target = EscortTargetsBuf[i];
                if (target == null || target.disabled)
                    continue;
                float a = own ? 0.85f : 0.3f;
                if (!DrawnEscortMarkers.Add(target))
                    continue; // ногу и маркер на цель уже нарисовал первый эскорт
                Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                anchor.y = GameApi.GroundY(anchor.x, anchor.z);
                ChainPts.Clear();
                ChainCols.Clear();
                ChainAlphas.Clear();
                ChainPts.Add(anchor);
                ChainPts.Add(TargetGround(target));
                ChainCols.Add(c);
                ChainAlphas.Add(a);
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                DrawWorldWaypointColored(cam, TargetGround(target), c, a);
            }
        }

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
            Color routeC = ModeColor(AirOrderMode.Transit);

            // Групповые вейпоинты (SupCom): групповая точка — свойство ТОЧКИ (groupId
            // клика), а НЕ выделения. У всех юнитов с этим groupId она рисуется одним
            // маркером-центроидом и общей лентой (дедуп между юнитами) — сколько бы
            // членов группы ни выделено (хоть один, хоть никто при Shift). Индивидуальные
            // точки (groupId=0) — только у своего юнита: личные ветки СХОДЯТСЯ к
            // магистрали группы и РАСХОДЯТСЯ из неё.
            // Накопители центроидов: xyz = сумма, w = счётчик.
            GidPointSums.Clear();
            GidAnchorSums.Clear();
            for (int gi = 0; gi < gUnits.Count; gi++)
            {
                Unit unit = gUnits[gi];
                IReadOnlyList<GlobalPosition> queue = gQueues[gi];
                int qi = gIdx[gi];
                SeenGids.Clear();
                for (int i = qi; i < queue.Count; i++)
                {
                    int gid = GroundOrders.GroupIdOf(unit, i);
                    if (gid == 0)
                        continue;
                    Vector3 w = GroundVec(queue[i]);
                    GidPointSums[gid] = (GidPointSums.TryGetValue(gid, out Vector4 acc)
                        ? acc + new Vector4(w.x, w.y, w.z, 1f)
                        : new Vector4(w.x, w.y, w.z, 1f));
                    if (SeenGids.Add(gid)) // якорь юнита — по одному на группу приказа
                    {
                        Vector3 up = unit.transform.position;
                        GidAnchorSums[gid] = (GidAnchorSums.TryGetValue(gid, out Vector4 aacc)
                            ? aacc + new Vector4(up.x, 0f, up.z, 1f)
                            : new Vector4(up.x, 0f, up.z, 1f));
                    }
                }
            }

            DrawnGidLegs.Clear();
            DrawnGidMarkers.Clear();
            DrawnGidEntries.Clear();
            DrawnGroundTargets.Clear(); // цели-узлы: дедуп крестов и ног для всей группы
            DrawnTargetExits.Clear();
            DrawnEscortMarkers.Clear();
            // Два прохода: сначала выделенные (ярко), затем при Shift остальные —
            // общие ленты/маркеры достаются первому встретившему (дедуп).
            for (int pass = 0; pass < 2; pass++)
                for (int gi = 0; gi < gUnits.Count; gi++)
                {
                    Unit unit = gUnits[gi];
                    bool own = controller.Selection.Selected.Contains(unit);
                    if (own != (pass == 0) || (!own && !shiftHeld))
                        continue;
                    // Инженер с живой очередью: его цепочка — СЛУЖЕБНАЯ выданная голова
                    // (транспорт SetDestination), маршрут рисует проход очередей ниже;
                    // оба прохода рисовали одну и ту же голову дважды (v0.82.2).
                    if (BuildTasks.ChainIsIssuedHead(unit))
                        continue;
                    // Смешанная очередь (точки + цели на позициях серии) рисуется
                    // ЕДИНОЙ лентой через цель — DrawGroundChain. Патруль — цветом
                    // цикла и с замыкающей ногой (последняя точка → первая).
                    bool patrol = GroundOrders.KindOf(unit) == GroundOrderKind.Patrol;
                    DrawGroundChain(cam, unit, gQueues[gi], GroundOrders.DrawStartIndex(unit),
                        patrol ? ModeColor(AirOrderMode.Patrol) : routeC,
                        own ? 0.85f : 0.3f, patrol);
                }

            // Связи сопровождения наземки/флота (v0.97): эскорт-цепочки без точек —
            // CollectAll их не отдаёт; лента «эскорт → цель» + зелёный маркер на цели
            // (один на цель, дедуп общий с авиа-эскортом в DrawPlanCurve).
            DrawEscortLinks(cam, controller, shiftHeld);

            // ЕДИНАЯ очередь инженеров по порядку кликов: движения (оранж) и площадки
            // (бирюза) одной цепочкой. Запись с groupId — ГРУППОВАЯ (групповая точка —
            // свойство записи, не выделения): маркер и сегменты между соседними
            // групповыми записями рисуются ОДИН раз на всю группу приказа — сколько
            // бы инженеров ни было выделено (хоть один); подход к первой групповой —
            // одна лента от центроида инженеров группы. Индивидуальные записи —
            // личные ветки схождения/расхождения.
            EngSeqUnits.Clear();
            EngSeqs.Clear();
            foreach (Unit unit in BuildTasks.UnitsWithSequences())
            {
                var seq = BuildTasks.SequenceOf(unit);
                if (seq != null && seq.Count > 0)
                {
                    EngSeqUnits.Add(unit);
                    EngSeqs.Add(seq);
                }
            }
            // Центроиды якорей инженерных групп (gid из общего счётчика с наземкой —
            // накопитель общий): xyz = сумма, w = счётчик.
            foreach (Unit unit in EngSeqUnits)
            {
                var seq = BuildTasks.SequenceOf(unit);
                SeenGids.Clear();
                for (int i = 0; i < seq.Count; i++)
                {
                    int gid = seq[i].GroupId;
                    if (gid == 0 || seq[i].Task != null || !SeenGids.Add(gid))
                        continue;
                    Vector3 up = unit.transform.position;
                    GidAnchorSums[gid] = (GidAnchorSums.TryGetValue(gid, out Vector4 aacc)
                        ? aacc + new Vector4(up.x, 0f, up.z, 1f)
                        : new Vector4(up.x, 0f, up.z, 1f));
                }
            }

            DrawnSites.Clear();
            DrawnGroupMoves.Clear();
            for (int ei = 0; ei < EngSeqs.Count; ei++)
            {
                Unit unit = EngSeqUnits[ei];
                bool own = controller.Selection.Selected.Contains(unit);
                if (!own && !shiftHeld)
                    continue;
                float a = own ? 0.85f : 0.3f;
                IReadOnlyList<BuildTasks.SeqEntry> seq = EngSeqs[ei];
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
                ChainPts.Clear();
                ChainCols.Clear();
                ChainAlphas.Clear();
                int prevGid = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    bool sharedMove = seq[i].Task == null && seq[i].GroupId != 0;
                    int gid = seq[i].GroupId;
                    bool drawLeg = true;
                    if (i == 0 && sharedMove)
                    {
                        // Подход к первой групповой записи — от центроида якорей группы.
                        if (GidAnchorSums.TryGetValue(gid, out Vector4 aa))
                        {
                            prev = new Vector3(aa.x / aa.w, 0f, aa.z / aa.w);
                            prev.y = GameApi.GroundY(prev.x, prev.z);
                        }
                        drawLeg = DrawnGidEntries.Add(gid);
                    }
                    else if (prevGid != 0 && sharedMove)
                        // Общий сегмент (две соседние групповые записи) — один на группу.
                        drawLeg = DrawnGidLegs.Add(PairKey(prevGid, gid));

                    if (drawLeg)
                    {
                        if (ChainPts.Count == 0)
                            ChainPts.Add(prev);
                        ChainPts.Add(pts[i]);
                        ChainCols.Add(cols[i]);
                        ChainAlphas.Add(a);
                    }
                    else if (ChainPts.Count > 0)
                    {
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                    }
                    prev = pts[i];
                    prevGid = sharedMove ? gid : 0;

                    if (seq[i].Task != null)
                    {
                        if (!DrawnSites.Add(seq[i].Task))
                            continue; // маркер площадки — один на задачу
                    }
                    else if (sharedMove && !DrawnGroupMoves.Add(gid))
                        continue; // маркер группового движения — один на приказ группы
                    DrawWorldWaypointColored(cam, pts[i], cols[i], a);
                }
                if (ChainPts.Count >= 2)
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
            }
            // Shift-режим видимости инженерных очередей уже покрыт циклом выше:
            // невыделенные рисуются приглушённо (a=0.3) тем же групповым слиянием.

            // Ралли-маршруты фабрик (депо/ангар/верфь/аэродром) — как цепочки
            // наземки; одинаковые маршруты пачки фабрик (общее ралли) рисуются ОДНОЙ
            // лентой и одним набором маркеров (дедуп DrawFactoryRally). Shift-режим
            // видимости рисует и НЕвыделенные фабрики приглушённо — как цепочки
            // юнитов (верфь/депо без выделения раньше были невидимы вовсе, §12-30).
            Airbase selAirbase = controller.Selection.SelectedAirbase;
            Color rallyColor = ModeColor(AirOrderMode.Transit);
            RallySeenPts.Clear();
            RallySeenLens.Clear();
            if (selAirbase != null)
                DrawFactoryRally(cam, selAirbase.transform.position, selAirbase, rallyColor, 0.85f);
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                if (unit is VehicleDepot depot)
                    DrawFactoryRally(cam, unit.transform.position, depot, rallyColor, 0.85f);
                else if (unit is Building b)
                {
                    Hangar hangar = b.GetComponent<Hangar>();
                    object factory = hangar != null ? (object)hangar
                        : BuildTasks.IsDock(b) ? (object)b : null;
                    if (factory != null)
                        DrawFactoryRally(cam, unit.transform.position, factory, rallyColor, 0.85f);
                }
            }
            if (shiftHeld)
            {
                ProductionTasks.CollectRallies(RallyAll);
                for (int ri = 0; ri < RallyAll.Count; ri++)
                {
                    Vector3? anchor = RallyAnchor(RallyAll[ri]);
                    if (anchor.HasValue)
                        DrawFactoryRally(cam, anchor.Value, RallyAll[ri], rallyColor, 0.3f);
                }
            }

            // Авиация: планы по сегментам. Групповые точки (groupId≠0) — маркер ОДИН
            // на приказ в центроиде группы бортов (формационная решётка слипается).
            List<Unit> airUnits = RouteAirUnits;
            List<IReadOnlyList<AircraftOrders.OrderSegment>> airPlans = RouteAirPlans;
            AircraftOrders.CollectAll(airUnits, airPlans);
            AirGidCentroids.Clear();
            AirGidAnchors.Clear();
            for (int ai = 0; ai < airUnits.Count; ai++)
                for (int s = 0; s < airPlans[ai].Count; s++)
                {
                    var seg = airPlans[ai][s];
                    for (int p = 0; p < seg.PointGroups.Count; p++)
                    {
                        int gid = seg.PointGroups[p];
                        if (gid == 0)
                            continue;
                        Vector3 w = GroundVec(seg.Points[p]);
                        AirGidCentroids[gid] = (AirGidCentroids.TryGetValue(gid, out Vector4 acc)
                            ? acc + new Vector4(w.x, w.y, w.z, 1f)
                            : new Vector4(w.x, w.y, w.z, 1f));
                    }
                }
            // Якорь члена группы — по одному на группу приказа (вход в магистраль
            // рисуется от центроида якорей, не от каждого борта).
            for (int ai = 0; ai < airUnits.Count; ai++)
            {
                SeenAirGids.Clear();
                for (int s = 0; s < airPlans[ai].Count; s++)
                {
                    var seg = airPlans[ai][s];
                    for (int p = 0; p < seg.PointGroups.Count; p++)
                    {
                        int gid = seg.PointGroups[p];
                        if (gid == 0 || !SeenAirGids.Add(gid))
                            continue;
                        Vector3 up = airUnits[ai].transform.position;
                        AirGidAnchors[gid] = (AirGidAnchors.TryGetValue(gid, out Vector4 aacc)
                            ? aacc + new Vector4(up.x, 0f, up.z, 1f)
                            : new Vector4(up.x, 0f, up.z, 1f));
                    }
                }
            }
            DrawnAirGid.Clear();
            DrawnAirLegs.Clear();
            DrawnAirEntries.Clear();
            DrawnAirTargets.Clear();
            DrawnRtbBases.Clear();
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

            // Прогоны: подряд идущие сегменты одного режима — ОДНА лента, стыки между
            // ними скругляются (shift-очередь приказов не даёт лесенок на смене сегмента);
            // смена режима (цвет) или атака по цели режет ленту. Патруль замыкает прогон.
            ChainPts.Clear();
            ChainCols.Clear();
            ChainAlphas.Clear();
            AirOrderMode runMode = AirOrderMode.Transit;
            bool runOpen = false;
            // Групповое слияние (SupCom-магистраль, зеркально наземке): групповая
            // точка (gid≠0) — узел-ЦЕНТРОИД формационных точек группы; ноги между
            // соседними узлами группы, вход в группу, ноги атак и RTB — ОДИН раз на
            // приказ (сколько бы бортов ни рисовало); индивидуальные точки — личные
            // ветки схождения/расхождения от магистрали. Пачка вертолётов рисует
            // одну магистраль, а не N параллельных цепочек по решётке формации.
            int prevGid = 0;
            bool firstNode = true;

            for (int si = 0; si < plan.Count; si++)
            {
                AircraftOrders.OrderSegment seg = plan[si];
                if (seg == null || (seg.Points.Count == 0 && seg.Target == null))
                    continue;
                Color c = ModeColor(seg.Mode);

                if (seg.Mode == AirOrderMode.Attack && seg.Target != null)
                {
                    if (runOpen)
                    {
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        runOpen = false;
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                    }
                    Vector3 tp = seg.Target.transform.position;
                    Vector3 tgt = new Vector3(tp.x, GameApi.GroundY(tp.x, tp.z), tp.z);
                    // Групповая атака: нога и крест — один раз на цель (пачка бортов
                    // не рисует N линий в одну и ту же цель).
                    if (DrawnAirTargets.Add(seg.Target))
                    {
                        ChainPts.Add(anchor);
                        ChainPts.Add(tgt);
                        ChainCols.Add(c);
                        ChainAlphas.Add(alpha);
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(seg.Target));
                        if (sp.z > 0f)
                        {
                            Vector2 scr = new Vector2(sp.x, Screen.height - sp.y);
                            float pulse = 10f + 8f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f));
                            DrawCross(scr, pulse * 2f, c);
                            DrawCross(scr, pulse, c);
                        }
                    }
                    anchor = tgt;
                    prevGid = 0;
                    firstNode = false;
                    continue;
                }

                if (seg.Mode == AirOrderMode.Escort && seg.Target != null)
                {
                    // Эскорт-сегмент (v0.97): зелёная нога «борт → цель» и маркер на
                    // цели (один на цель — дедуп общий с наземными связями).
                    if (runOpen)
                    {
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        runOpen = false;
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                    }
                    Vector3 etp = seg.Target.transform.position;
                    Vector3 etgt = new Vector3(etp.x, GameApi.GroundY(etp.x, etp.z), etp.z);
                    if (DrawnEscortMarkers.Add(seg.Target))
                    {
                        ChainPts.Add(anchor);
                        ChainPts.Add(etgt);
                        ChainCols.Add(c);
                        ChainAlphas.Add(alpha);
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        DrawWorldWaypointColored(cam, etgt, c, alpha);
                    }
                    anchor = etgt;
                    prevGid = 0;
                    firstNode = false;
                    continue;
                }

                var wps = RouteWps;
                wps.Clear();
                for (int i = 0; i < seg.Points.Count; i++)
                    wps.Add(GroundVec(seg.Points[i]));
                if (wps.Count == 0)
                    continue;

                if (runOpen && seg.Mode != runMode)
                {
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                    runOpen = false;
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                }
                if (!runOpen)
                {
                    runMode = seg.Mode;
                    runOpen = true;
                }

                int hover = HoverWaypointIndex(cam, wps);
                bool loop = seg.Mode == AirOrderMode.Patrol && wps.Count > 1;
                Vector3 firstNodePos = wps[0];
                int firstGid = AircraftOrders.GroupIdOf(unit, si, 0);
                for (int i = 0; i < wps.Count; i++)
                {
                    int gid = AircraftOrders.GroupIdOf(unit, si, i);
                    Vector3 node = gid != 0 && AirGidCentroids.TryGetValue(gid, out Vector4 pc)
                        ? new Vector3(pc.x / pc.w, pc.y / pc.w, pc.z / pc.w)
                        : wps[i];
                    if (i == 0)
                        firstNodePos = node;
                    bool drawLeg;
                    if (firstNode && gid != 0)
                    {
                        // Вход в группу — от центроида якорей её членов, один раз на группу.
                        if (AirGidAnchors.TryGetValue(gid, out Vector4 anc))
                        {
                            anchor = new Vector3(anc.x / anc.w,
                                GameApi.GroundY(anc.x / anc.w, anc.z / anc.w), anc.z / anc.w);
                        }
                        drawLeg = DrawnAirEntries.Add(gid);
                    }
                    else if (prevGid != 0 && gid != 0)
                        drawLeg = DrawnAirLegs.Add(PairKey(prevGid, gid)); // сегмент магистрали
                    else if (seg.Mode == AirOrderMode.Rtb && seg.Base != null)
                        drawLeg = DrawnRtbBases.Add(seg.Base); // домой — одна нога на базу
                    else
                        drawLeg = true; // индивидуальные ветки — свои у каждого борта
                    firstNode = false;
                    if (drawLeg)
                    {
                        if (ChainPts.Count == 0)
                            ChainPts.Add(anchor); // нога от конца предыдущего сегмента прогона
                        ChainPts.Add(node);
                        ChainCols.Add(c);
                        ChainAlphas.Add((i == 0 ? hover >= 0 : hover >= i - 1)
                            ? Mathf.Max(alpha, 1f) : alpha);
                    }
                    else if (ChainPts.Count > 0)
                    {
                        // Разрыв: общий сегмент уже нарисован другим членом группы.
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                    }
                    prevGid = gid;
                    anchor = node;
                }
                if (loop)
                {
                    // Замыкающая нога патруля — тоже сегмент магистрали (дедуп).
                    bool drawClose = firstGid != 0 && prevGid != 0
                        ? DrawnAirLegs.Add(PairKey(prevGid, firstGid))
                        : true;
                    if (drawClose)
                    {
                        if (ChainPts.Count == 0)
                            ChainPts.Add(anchor);
                        ChainPts.Add(firstNodePos);
                        ChainCols.Add(c);
                        ChainAlphas.Add(alpha);
                    }
                }
                // Маркеры: групповая точка (groupId≠0) — ОДНА на приказ группы бортов
                // (в центроиде их формационных точек), индивидуальная — своя.
                // Служебные точки — НЕ вейпоинты приказа: маркер не рисуем (единое
                // правило со сканером — AircraftOrders.IsServicePoint).
                for (int i = 0; i < wps.Count; i++)
                {
                    int gid = AircraftOrders.GroupIdOf(unit, si, i);
                    if (AircraftOrders.IsServicePoint(seg, i))
                        continue;
                    if (gid != 0)
                    {
                        if (!DrawnAirGid.Add(gid))
                            continue;
                        if (AirGidCentroids.TryGetValue(gid, out Vector4 acc))
                            DrawWorldWaypointColored(cam,
                                new Vector3(acc.x / acc.w, acc.y / acc.w, acc.z / acc.w), c, alpha);
                    }
                    else
                        DrawWorldWaypointColored(cam, wps[i], c, alpha);
                }
                if (loop)
                {
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                    runOpen = false;
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                }
            }
            if (runOpen)
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
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
            for (int i = 0; i < FanNumbers.Length; i++) // номера целей веера — без аллокаций в OnGUI
                FanNumbers[i] = (i + 1).ToString();
        }

        /// <summary>Номера накопленных целей «Атаки веером» (заполняются один раз).</summary>
        private static readonly string[] FanNumbers = new string[32];

        /// <summary>Накопленные цели «Атаки веером» (режим R): пульсирующий фиолетовый
        /// крест + порядковый номер (порядок кликов — приоритет ёмкости при раздаче).
        /// Мёртвые за время накопления пропускаются.</summary>
        private static void DrawFanTargets(CommanderModeController controller)
        {
            if (OrderModes.Pending != OrderMode.DisperseAttack)
                return;
            List<Unit> targets = controller.MoveOrders.FanTargets;
            if (targets.Count == 0)
                return;
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            Color c = new Color(0.7f, 0.5f, 1f, 0.95f); // фиолетовый: накопление, не выданная атака
            for (int i = 0; i < targets.Count; i++)
            {
                Unit target = targets[i];
                if (target == null || target.disabled) // Unity fake-null
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(target));
                if (sp.z <= 0f)
                    continue;
                Vector2 scr = new Vector2(sp.x, Screen.height - sp.y);
                if (UnderMinimapWindow(scr))
                    continue;
                float pulse = 10f + 8f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f));
                DrawCross(scr, pulse * 2f, c);
                DrawCross(scr, pulse, c);
                if (i < FanNumbers.Length)
                    GUI.Label(new Rect(scr.x + pulse + 8f, scr.y - 9f, 60f, 18f), FanNumbers[i], _small);
            }
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
