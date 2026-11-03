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
        /// <summary>Лента-текстуры для меш-рендера (RouteRibbonMesh читает напрямую).</summary>
        internal static Texture2D RibbonTexSoft => _ribbonSoft;
        internal static Texture2D RibbonTexArrow => _ribbonArrow;
        /// <summary>Лента-текстура FA (процедурная): полоса с мягкими краями и ярким
        /// ядром (glow-профиль в альфе, RGB белый — GUI.color тинтит по виду ноги).
        /// Стрелочная модуляция для «живых» ног (attack-move/патруль): яркость
        /// полосы несёт бегущий шеврон, анимация = сдвиг UV по пути (anim_rate FA).</summary>
        private static Texture2D _ribbonSoft;
        private static Texture2D _ribbonArrow;

        /// <summary>Профиль полосы (канон orderline FA): v=0..1 поперёк ленты —
        /// яркое ядро ~18% + мягкий ореол до краёв.</summary>
        private static float RibbonBandProfile(float v)
        {
            float x = Mathf.Abs(v - 0.5f) * 2f; // 0 центр — 1 край
            float core = Mathf.Clamp01(1f - x / 0.18f);
            float glow = Mathf.Exp(-(x * x) / (0.55f * 0.55f)) * 0.42f;
            return Mathf.Clamp01(core + glow);
        }

        internal static void EnsureRibbonTextures()
        {
            if (_ribbonSoft != null && _ribbonArrow != null)
                return; // одноразовая генерация (смена вида = пересоздание игры)
            const int w = 16, h = 64;
            _ribbonSoft = new Texture2D(w, h, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < h; y++)
            {
                float a = RibbonBandProfile((y + 0.5f) / h) * 0.85f;
                for (int x = 0; x < w; x++)
                    _ribbonSoft.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            _ribbonSoft.Apply();
            // Стрелочная: один тайл = 5 ширин ленты (uv_aspect FA 0.2); шеврон «>»
            // по ходу (+u) — яркость полосы модулирована 0.45..1.0.
            const int aw = 256;
            _ribbonArrow = new Texture2D(aw, h, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f) / h;
                float band = RibbonBandProfile(v);
                for (int x = 0; x < aw; x++)
                {
                    float u = (x + 0.5f) / aw;
                    // ТРИ шеврона на тайл (период 1/3) без мёртвых зон: паттерн
                    // плотный и ФАЗОНЕЗАВИСИМЫЙ — на любом видимом отрезке 2-3
                    // бегущих шеврона, где бы ни стартовала UV-фаза пути (плейтесты:
                    // одиночный шеврон «у последнего вейпоинта», переезды фазы).
                    float base0 = 0.10f - Mathf.Abs(v - 0.5f) * 0.20f; // вершина в центре линии
                    float du = Mathf.Repeat(u - base0 + 1f / 6f, 1f / 3f) - 1f / 6f;
                    float chev = Mathf.Clamp01(1f - Mathf.Abs(du) / 0.045f);
                    // База стрелочной = яркости обычной полосы (0.85 soft), шеврон —
                    // полновесный яркий штрих (тонкий на выровненной базе был
                    // неразличим, «всё одинаковое»).
                    float a = band * (0.72f + 0.28f * chev);
                    _ribbonArrow.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            _ribbonArrow.Apply();
        }

        /// <summary>Центральная линия шеврона по v (0..1 поперёк ленты): вершина «>»
        /// в центре ленты на u=0.30, крылья уходят назад (u меньше) к краям.</summary>
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
            {
                RangeRings.Tick(controller);
                FleetSalvo.Tick(); // очередь волн: трубы по готовности, перенацеливания
            }
        }

        public static void Notice(string text, float life = 3.5f) =>
            Notices.Add((text, Time.unscaledTime + life));

        // Заголовок меняется только тумблером режима — строится один раз на
        // состояние (Loc.F+конкат на КАЖДОМ OnGUI-событии, включая Layout-проходы,
        // аллоцировали ~6 строк 2-4 раза за кадр бесконечно).
        private static string _headerActive;
        private static string _headerIdle;

        private static string BuildHeader(bool active) =>
            $"SUPREME NUCLEAR v{PluginInfo.Version}  |  " + Loc.F("Режим командира: {0}  |  {1} — переключить",
                active ? "<color=lime>" + Loc.Tr("ВКЛ") + "</color>" : Loc.Tr("ВЫКЛ"),
                Settings.ToggleCommanderKey);

        private void OnGUI()
        {
            if (!GameApi.InMission)
                return;

            EnsureStyles();

            var controller = CommanderModeController.Instance;
            bool active = controller != null && controller.IsActive;

            GUI.Label(new Rect(10f, 8f, 1100f, 22f),
                active ? _headerActive ??= BuildHeader(true) : _headerIdle ??= BuildHeader(false),
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
                    DrawFleetSalvoMarks();
                    DrawNukeAim();
                    DrawDepositMarkers();
                    DrawSalvageMarkers();
                    DrawCaptureMarkers();
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
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            float k = Screen.height / 1440f;
            Color c = new Color(0.4f, 0.9f, 1f, 0.95f);

            // Маркеры очередей только СВОИХ площадок: AI строит через тот же
            // BuildTasks, чужие рамки/прогресс игроку не видны (v0.143.8).
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                if (t.Faction != hq)
                    continue;
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
        private static readonly HashSet<Unit> IconSelected = new HashSet<Unit>();

        private static void DrawStrategicIcons(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;

            float px = Settings.IconSizePx * (Screen.height / 1440f);
            var selected = IconSelected; // без new HashSet на каждый Repaint
            selected.Clear();
            selected.UnionWith(controller.Selection.Selected);
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
            DrawCommanderBoards(cam, hq, px);
            DrawHostileIcons(cam, hq, px);
            DrawMissileWarnings(cam, hq, px);
        }

        /// <summary>Маркер командирского борта (DESIGN §2.1): свой — зелёная
        /// «корона» с подписью КОМАНДИР (+EMCON при молчании); чужой — красный,
        /// только пока есть контакт разведки (как чужой аэродром).</summary>
        private static void DrawCommanderBoards(Camera cam, FactionHQ hq, float px)
        {
            IReadOnlyList<CommanderBoardService.BoardRec> boards = CommanderBoardService.Boards;
            if (boards == null || boards.Count == 0)
                return;
            float d = px * 2.1f;
            for (int i = 0; i < boards.Count; i++)
            {
                CommanderBoardService.BoardRec rec = boards[i];
                Unit board = rec.Board;
                if (board == null || board.disabled || rec.DeadHandled)
                    continue;
                bool own = rec.Hq == hq;
                if (!own && !GameApi.TryGetContact(board, hq, out _))
                    continue; // чужой борт — только по разведке
                Vector3 pos = board.transform.position;
                Vector3 sp = cam.WorldToScreenPoint(pos);
                if (sp.z <= 0f || sp.x < -60f || sp.x > Screen.width + 60f ||
                    sp.y < -60f || sp.y > Screen.height + 60f)
                    continue;
                float gx = sp.x;
                float gy = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(gx, gy)))
                    continue;
                Color c = own
                    ? new Color(0.45f, 0.95f, 0.5f, 0.95f)
                    : new Color(1f, 0.35f, 0.3f, 0.95f);
                // «Корона»: треугольник вверх над точкой + двойная нижняя перекладина.
                var tri = new[]
                {
                    new Vector2(gx, gy - d * 0.7f),
                    new Vector2(gx + d * 0.5f, gy + d * 0.25f),
                    new Vector2(gx - d * 0.5f, gy + d * 0.25f),
                };
                for (int e = 0; e < 3; e++)
                {
                    Vector2 a1 = tri[e];
                    Vector2 a2 = tri[(e + 1) % 3];
                    int dots = Mathf.Clamp((int)(Vector2.Distance(a1, a2) / 7f), 1, 14);
                    for (int k = 0; k <= dots; k++)
                    {
                        Vector2 p = Vector2.Lerp(a1, a2, k / (float)dots);
                        DrawRect(new Rect(p.x - 1.2f, p.y - 1.2f, 2.4f, 2.4f), c);
                    }
                }
                DrawRect(new Rect(gx - d * 0.5f, gy + d * 0.38f, d, 2.4f), c);
                DrawRect(new Rect(gx - 2f, gy - 2f, 4f, 4f), c);
                // Подпись: свой — «КОМАНДИР» (+ «EMCON» приглушённо),
                // чужой — метка цели.
                if (own)
                {
                    ClosedLabel(gx, gy + d * 0.6f, Loc.Tr("КОМАНДИР"));
                    if (rec.Emcon)
                        ClosedLabel(gx, gy + d * 0.6f + 14f, "EMCON"); // аббревиатура, не переводится
                }
                else
                    ClosedLabel(gx, gy + d * 0.6f, Loc.Tr("ГЛАВНАЯ ЦЕЛЬ"));
            }
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

        /// <summary>Маркеры СВОБОДНЫХ месторождений (v3): жёлтый квадрат — масса,
        /// оранжевый ромб — уголь (SupCom: «видишь точку — строй»). Занятые скрыты
        /// у всех; построенные здания видны как юниты.</summary>
        private static void DrawDepositMarkers()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !EconomyService.Running)
                return;

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 3f);
            float s = 18f * (Screen.height / 1440f);
            foreach (EconomyService.DepositSite site in EconomyService.SiteList)
            {
                if (!EconomyService.SiteIsFree(site))
                    continue;
                Vector3 world = site.Pos.ToLocalPosition();
                world.y += 12f;
                Vector3 sp = cam.WorldToScreenPoint(world);
                if (sp.z <= 0f || sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                Color color = site.Kind == EconomyService.DepositKind.Coal
                    ? new Color(1f, 0.55f, 0.2f, 1f)
                    : new Color(1f, 0.85f, 0.2f, 1f);
                var outer = new Rect(sp.x - s * 0.5f, guiY - s * 0.5f, s, s);
                DrawBoxOutline(outer, 1.5f, color);
                float innerSize = s * (0.25f + 0.35f * pulse);
                Color fill = color;
                fill.a = 0.35f + 0.5f * pulse;
                DrawRect(new Rect(sp.x - innerSize * 0.5f, guiY - innerSize * 0.5f, innerSize, innerSize), fill);
            }
        }

        /// <summary>Маркеры салвижа: обломки, которые прямо сейчас разбирают инженеры
        /// (оранжевая рамка, заполнение = прогресс разбора). Только активные сборы —
        /// поля боёв не шумят маркерами.</summary>
        private static void DrawSalvageMarkers()
        {
            Camera cam = GameApi.MainCam;
            var wrecks = SalvageService.MarkerWrecks;
            var progress = SalvageService.MarkerProgress;
            if (cam == null || wrecks.Count == 0)
                return;

            float s = 16f * (Screen.height / 1440f);
            Color color = new Color(1f, 0.55f, 0.15f, 1f);
            for (int i = 0; i < wrecks.Count; i++)
            {
                Wreckage wreck = wrecks[i];
                if (wreck == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(wreck.transform.position);
                if (sp.z <= 0f || sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                var outer = new Rect(sp.x - s * 0.5f, guiY - s * 0.5f, s, s);
                DrawBoxOutline(outer, 1.5f, color);
                float innerSize = s * (0.15f + 0.7f * progress[i]);
                Color fill = color;
                fill.a = 0.7f;
                DrawRect(new Rect(sp.x - innerSize * 0.5f, guiY - innerSize * 0.5f, innerSize, innerSize), fill);
            }
        }

        /// <summary>Маркеры инженерного захвата (v3): оранжевый ромб-рамка на цели,
        /// заполнение = прогресс (как салваж, но обводка двойная — захват).</summary>
        private static void DrawCaptureMarkers()
        {
            Camera cam = GameApi.MainCam;
            var targets = CaptureService.MarkerTargets;
            var progress = CaptureService.MarkerProgress;
            if (cam == null || targets.Count == 0)
                return;

            float s = 18f * (Screen.height / 1440f);
            Color color = new Color(1f, 0.4f, 0.1f, 1f);
            for (int i = 0; i < targets.Count; i++)
            {
                Building b = targets[i];
                if (b == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(b));
                if (sp.z <= 0f || sp.x < -40f || sp.x > Screen.width + 40f || sp.y < -40f || sp.y > Screen.height + 40f)
                    continue;
                float guiY = Screen.height - sp.y;
                if (UnderMinimapWindow(new Vector2(sp.x, guiY)))
                    continue;

                var outer = new Rect(sp.x - s * 0.5f, guiY - s * 0.5f, s, s);
                DrawBoxOutline(outer, 2f, color);
                float innerSize = s * (0.15f + 0.7f * progress[i]);
                Color fill = color;
                fill.a = 0.55f;
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

        /// <summary>Палитра лент/маркеров — СТРОГИЙ канон SupCom FA 1:1
        /// (commandgraphparams.lua): семейство движения (Move/CoordinatedMove/Patrol)
        /// — ЦИАН 00ffff, семейство атаки (Attack/AggressiveMove/Tactical) — чистый
        /// КРАСНЫЙ ff0000, инженерное семейство (Guard/Repair/Reclaim) — ЖЁЛТЫЙ
        /// ffff00, транспортное (Ferry/Transport) — ФИОЛЕТОВЫЙ 654bc2. Патруль
        /// наследует циан движения (отличается формой маркера-цикла, бегущими
        /// шевронами и замыкающей ногой); залп = Tactical → красный; эскорт =
        /// Guard → жёлтый; RTB — транспортное семейство.</summary>
        internal static Color ModeColor(AirOrderMode mode) =>
            mode switch
            {
                AirOrderMode.Patrol => new Color(0f, 1f, 1f, 1f),
                AirOrderMode.AttackMove => new Color(1f, 0f, 0f, 1f),
                AirOrderMode.Attack => new Color(1f, 0f, 0f, 1f),
                AirOrderMode.Salvo => new Color(1f, 0f, 0f, 1f),
                AirOrderMode.Escort => new Color(1f, 1f, 0f, 1f),
                AirOrderMode.Rtb => new Color(0.4f, 0.29f, 0.76f, 1f),
                _ => new Color(0f, 1f, 1f, 1f)
            };

        /// <summary>Глобальная точка маршрута на ПОВЕРХНОСТИ (вода — уровень
        /// моря, суша — рельеф): ленты/маркеры флота не проваливаются на дно.</summary>
        private static Vector3 GroundVec(GlobalPosition gp)
        {
            Vector3 l = gp.ToLocalPosition();
            return new Vector3(l.x, GameApi.RouteY(l.x, l.z), l.z);
        }

        /// <summary>Лента цепочки вейпоинтов: ноги — кубические Эрмит-кривые
        /// (Util.HermiteChain, порт канона Moho UICommandGraph: тангенс узла — митр
        /// смежных ног, длина — min(¼ ноги, 50·толщина) — «текучие» повороты SupCom),
        /// вся цепочка — ОДНА непрерывная полоса экраных трапеций (соседние куски делят
        /// вершины — швов и «лесенок» на стыках ног нет), полуширина адаптивна к
        /// дистанции до камеры, вдоль всей цепочки бежит шеврон-точка.
        /// legCols/legAlphas — по одной записи на ногу цепочки: цвет и яркость
        /// (hover-подсветка) переключаются на границе ног. legWidths — множитель
        /// полуширины на ногу (канон SupCom: групповая магистраль √n юнитов толще
        /// личной ветки; null — все единица).</summary>
        private static void DrawChainRibbon(Camera cam, IReadOnlyList<Vector3> pts,
            List<Color> legCols, List<float> legAlphas, List<float> legWidths = null,
            List<bool> legFlow = null, bool meshGated = true)
        {
            // Меш-режим (порт рендера FA): ленты рисует RouteRibbonMesh мировым
            // мешом с текстурами — IMGUI-ленты не дублируем. meshGated=false —
            // маршруты, которых НЕТ в меше (ралли фабрик), остаются в IMGUI.
            if (meshGated && Settings.RouteUseMesh)
                return;
            Color prevColor = GUI.color;
            Matrix4x4 prevMatrix = GUI.matrix;
            // Близкий зум: гасим только ЗА камерой (раньше 50 м — при приближении
            // лента исчезала прямо под камерой, плейтест v0.114).
            const float minZ = 1.5f;
            float pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            // Полуширина (SupCom-принцип): у камеры — ТОНКАЯ экранная линия
            // (~24px на 1440p, но не тоньше 1.5 м мира), с дистанцией плавно растёт
            // до мирового размера RouteRibbonWidthM (стратегический зум). Прежний
            // экраный потолок 110px прижимал ленту к константе на ЛЮБОМ приближении —
            // рядом с юнитами она была нереально широкой.
            // Толщина: FA-нить — экранный потолок ~12px на 1440p (плейтест v0.114:
            // прежние 24px+ читались жирным слэбом против тонкой линии оригинала);
            // в стратзуме рулит мировая RouteRibbonWidthM.
            float maxHalfAt(float dist) => Mathf.Min(
                Settings.RouteRibbonWidthM * 0.5f,
                Mathf.Max(1.5f, 6f * (Screen.height / 1440f) * dist / pxPerRad));

            // Сглаживание ног — Эрмит-кривые канона Moho (порт command_curve):
            // повороты «текут», как в SupCom; кламп тангенса привязан к МИРОВОЙ
            // ширине ленты (RouteRibbonWidthM × множитель ноги — как width у FA).
            int n = Util.HermiteChain(pts, legWidths, Settings.RouteRibbonWidthM,
                Settings.RouteSampleStepM, RibbonPts, RibbonLegs);
            if (n < 2)
                return;

            // Сечение ленты в точке j: перпендикуляр из соседних точек, полуширина по
            // дистанции и множителю ноги, которой принадлежит точка.
            float WidthMulOf(int legIdx) =>
                legWidths != null && legIdx >= 0 && legIdx < legWidths.Count ? legWidths[legIdx] : 1f;
            Vector3 Section(int j, int legIdx, out Vector3 left, out Vector3 right)
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
                float half = maxHalfAt(Mathf.Max(sp.z, 1f)) * WidthMulOf(legIdx);
                left = c - perp * half;
                right = c + perp * half;
                return sp;
            }


            /// <summary>Текстурный квад ленты: ОСЬ X матрицы — ВДОЛЬ пути (u = дистанция
            /// в тайлах, тайл = 5 ширин ленты), ось Y — поперёк (v — профиль полосы);
            /// шевроны и glow живут в альфе текстуры, GUI.color тинтит видом ноги.
            /// Без полупиксельного расширения: края полосы в текстуре прозрачны,
            /// швов между кусками не видно (UV непрерывен по пути).</summary>
            void TexQuad(Vector3 aL, Vector3 aR, Vector3 bL, Vector3 bR, Color col,
                Texture2D tex, float u0, float du)
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
                Vector2 along = p3 - p1;   // вдоль пути (u)
                Vector2 across = p2 - p1;  // поперёк ленты (v)
                if (along.sqrMagnitude <= 1e-6f || across.sqrMagnitude <= 1e-6f)
                    return;
                var m = Matrix4x4.identity;
                m.SetColumn(0, new Vector4(along.x, along.y, 0f, 0f));
                m.SetColumn(1, new Vector4(across.x, across.y, 0f, 0f));
                m.SetColumn(3, new Vector4(p1.x, p1.y, 0f, 1f));
                GUI.matrix = m;
                GUI.color = col;
                GUI.DrawTextureWithTexCoords(new Rect(0f, 0f, 1f, 1f), tex,
                    new Rect(u0, 0f, du, 1f), true);
                GUI.matrix = prevMatrix;
            }

            // «Живой» поток канона FA: шевроны ЖИВУТ В ТЕКСТУРЕ ленты (стрелочный
            // тайл = 5 ширин, uv_aspect FA 0.2), анимация = сдвиг UV по пути
            // (orderline_anim_rate 0.25); статичные ноги — мягкая полоса + наконечник.
            int legCount = legCols.Count;
            int legIdx = 0;
            Vector3 prevL = Vector3.zero, prevR = Vector3.zero, prevC = RibbonPts[0];
            bool hasPrev = false;
            float uCarry = 0f; // накопленная UV-фаза пути (в тайлах)

            for (int i = 1; i < n; i++)
            {
                Vector3 a = RibbonPts[i - 1];
                Vector3 b = RibbonPts[i];
                while (legIdx + 1 < legCount && i - 1 >= RibbonLegs[legIdx + 1])
                    legIdx++;
                Color c = legIdx < legCount ? legCols[legIdx] : Color.white;
                float alpha = legIdx < legAlphas.Count ? legAlphas[legIdx] : 1f;
                // Текстурная лента FA: glow-профиль (ядро+ореол) и шевроны — В АЛЬФЕ
                // текстуры, RGB белый; тинт — цвет вида ноги на полной альфе.
                var lineCol = new Color(c.r, c.g, c.b, alpha);
                bool flow = legFlow != null && legIdx < legFlow.Count && legFlow[legIdx];
                Texture2D tex = flow ? _ribbonArrow : _ribbonSoft;
                float shift = flow ? Time.unscaledTime * 0.25f : 0f; // anim_rate FA
                bool legEndsHere = i + 1 >= n ||
                    (legIdx + 1 < legCount && i >= RibbonLegs[legIdx + 1]);

                float xzLen = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (xzLen < 1e-3f)
                    continue;
                int pieces = Mathf.Clamp(Mathf.CeilToInt(xzLen / 200f), 1, 80);
                Vector3 spA = Section(i - 1, legIdx, out Vector3 startL, out Vector3 startR);
                Section(i, legIdx, out Vector3 endL, out Vector3 endR);
                for (int s = 1; s <= pieces; s++)
                {
                    float t = s / (float)pieces;
                    Vector3 curC = Vector3.LerpUnclamped(a, b, t);
                    Vector3 curL = Vector3.LerpUnclamped(startL, endL, t);
                    Vector3 curR = Vector3.LerpUnclamped(startR, endR, t);
                    Vector3 sp = cam.WorldToScreenPoint(curC);
                    bool visible = sp.z > minZ;

                    float pieceLen = Vector3.Distance(prevC, curC);
                    if (hasPrev && visible && pieceLen > 1e-3f)
                    {
                        // Тайл шеврона = 5 × ВИЗУАЛЬНАЯ ширина КУСКА (канон FA: тайл
                        // привязан к рисуемой ширине линии; на близком зуме стрелки
                        // сжимаются до экранных пропорций, а не растягиваются на сотни
                        // метров). Фаза копится по кускам — непрерывна при смене ширины.
                        float wStart = Vector3.Distance(prevL, prevC);
                        float wEnd = Vector3.Distance(curL, curC);
                        float tileM = Mathf.Max(8f, 5f * (wStart + wEnd));
                        if (flow)
                        {
                            float u0 = uCarry;
                            float u1 = u0 + pieceLen / tileM;
                            float ua = u0;
                            for (int k = Mathf.FloorToInt(u0) + 1; ; k++)
                            {
                                float ub = Mathf.Min(k, u1);
                                float fa = (ua - u0) / (u1 - u0);
                                float fb = (ub - u0) / (u1 - u0);
                                TexQuad(
                                    Vector3.LerpUnclamped(prevL, curL, fa),
                                    Vector3.LerpUnclamped(prevR, curR, fa),
                                    Vector3.LerpUnclamped(prevL, curL, fb),
                                    Vector3.LerpUnclamped(prevR, curR, fb),
                                    lineCol, tex, Mathf.Repeat(ua - shift, 1f), ub - ua);
                                ua = ub;
                                if (ub >= u1 - 1e-5f)
                                    break;
                            }
                            uCarry = u1;
                        }
                        else
                        {
                            // Мягкая полоса: вдоль u текстура однородна — UV не важен.
                            TexQuad(prevL, prevR, curL, curR, lineCol, tex, 0f, 1f);
                        }
                    }
                    if (!flow && legEndsHere && s == pieces && visible)
                    {
                        // Наконечник в конце статичной ноги: направление a→b.
                        DrawRibbonChevron(new Vector2(sp.x, Screen.height - sp.y),
                            new Vector2(sp.x - spA.x, spA.y - sp.y),
                            new Color(c.r, c.g, c.b, Mathf.Min(1f, alpha + 0.1f)));
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

        /// <summary>Шеврон направления «∨» в экранных координатах: две тонкие линии-
        /// руки из точки p под ±140° к направлению dir (указывает ПО ходу движения;
        /// бегущие ряды — на ногах атаки-движения/патруля, одиночный — наконечник
        /// статичной ноги). Без аллокаций (локальные GUI.matrix).</summary>
        private static void DrawRibbonChevron(Vector2 p, Vector2 dir, Color c)
        {
            float dl = dir.magnitude;
            if (dl < 1e-3f)
                return;
            float heading = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            Matrix4x4 prev = GUI.matrix;
            Color prevC = GUI.color;
            GUI.color = c;
            const float arm = 9f, thick = 2f;
            for (int s = -1; s <= 1; s += 2)
            {
                var m = Matrix4x4.identity;
                m.SetColumn(3, new Vector4(p.x, p.y, 0f, 1f));
                var rot = Matrix4x4.TRS(Vector3.zero,
                    Quaternion.Euler(0f, 0f, heading + s * 140f), Vector3.one);
                GUI.matrix = m * rot * prev;
                GUI.DrawTexture(new Rect(0f, -thick * 0.5f, arm, thick), _white);
            }
            GUI.matrix = prev;
            GUI.color = prevC;
        }

        /// <summary>Форма маркера вейпоинта — вид приказа без цвета (канон SupCom
        /// «цвет ног + форма маркеров»): квадрат — движение, ромб — атака-движение,
        /// двойная рамка — патрульный цикл.</summary>
        private enum WaypointShape
        {
            Square,
            Diamond,
            Loop
        }

        private static WaypointShape ShapeOf(GroundOrderKind kind) =>
            kind switch
            {
                GroundOrderKind.AttackMove => WaypointShape.Diamond,
                GroundOrderKind.Patrol => WaypointShape.Loop,
                _ => WaypointShape.Square
            };

        private static WaypointShape ShapeOf(AirOrderMode mode) =>
            mode switch
            {
                AirOrderMode.AttackMove => WaypointShape.Diamond,
                AirOrderMode.Patrol => WaypointShape.Loop,
                _ => WaypointShape.Square
            };

        /// <summary>Маркер вейпоинта в точке экрана p: форма по виду приказа, размер
        /// sizeMul (групповой узел √n юнитов — SupCom DrawNode). Ромб — квадрат,
        /// повёрнутый на 45° локальной GUI.matrix (без аллокаций).</summary>
        private static void DrawWaypointMarkerAt(Vector2 p, float half, Color color, float alpha,
            WaypointShape shape)
        {
            Color frame = new Color(color.r, color.g, color.b, Mathf.Min(1f, alpha + 0.15f));
            Color core = new Color(color.r, color.g, color.b, alpha);
            switch (shape)
            {
                case WaypointShape.Diamond:
                {
                    Matrix4x4 prev = GUI.matrix;
                    var m = Matrix4x4.identity;
                    m.SetColumn(3, new Vector4(p.x, p.y, 0f, 1f)); // перенос в центр
                    var rot = Matrix4x4.identity;
                    float c = Mathf.Cos(Mathf.PI * 0.25f), s = Mathf.Sin(Mathf.PI * 0.25f);
                    rot.m00 = c; rot.m01 = -s;
                    rot.m10 = s; rot.m11 = c;
                    GUI.matrix = m * rot * prev;
                    DrawBoxOutline(new Rect(-half, -half, half * 2f, half * 2f), 2.5f, frame);
                    GUI.matrix = prev;
                    DrawRect(new Rect(p.x - 3.5f, p.y - 3.5f, 7f, 7f), core);
                    break;
                }
                case WaypointShape.Loop:
                {
                    var outer = new Rect(p.x - half, p.y - half, half * 2f, half * 2f);
                    DrawBoxOutline(outer, 2.5f, frame);
                    float innerHalf = half * 0.5f;
                    DrawBoxOutline(new Rect(p.x - innerHalf, p.y - innerHalf,
                        innerHalf * 2f, innerHalf * 2f), 2f, core);
                    break;
                }
                default:
                {
                    var r = new Rect(p.x - half, p.y - half, half * 2f, half * 2f);
                    DrawBoxOutline(r, 2.5f, frame);
                    float dot = half * 0.4f;
                    DrawRect(new Rect(p.x - dot, p.y - dot, dot * 2f, dot * 2f), core);
                    break;
                }
            }
        }

        /// <summary>Вейпоинт с цветом записи очереди (движение/стройка) и формой вида.</summary>
        private static void DrawWorldWaypointColored(Camera cam, Vector3 wp, Color color, float alpha,
            WaypointShape shape = WaypointShape.Square, float sizeMul = 1f)
        {
            Vector3 sp = cam.WorldToScreenPoint(wp);
            if (sp.z <= 0f)
                return;
            Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
            if (UnderMinimapWindow(p))
                return;
            DrawWaypointMarkerAt(p, 10f * sizeMul, color, alpha, shape);
        }

        private static void DrawWorldWaypoints(Camera cam, IEnumerable<Vector3> waypoints, Color color, float alpha)
        {
            foreach (Vector3 wp in waypoints)
                DrawWorldWaypointColored(cam, wp, color, alpha);
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
            DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, meshGated: false);
            DrawWorldWaypoints(cam, RallyWps, Color.white, a); // маркеры белые — канон FA
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
                case BuildTasks.Task site:
                    // Недострой-фабрика: якорь — сама площадка (v0.107).
                    return new Vector3?(site.Site.ToLocalPosition());
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
        // Per-point вид (v0.114): вид точки-узла (цели — Attack) и её абсолютный
        // индекс в очереди (-1 у целей) — цвет ноги входа, форма маркера, подсказка
        // конвертации хвостом. Толщина ноги: √n юнитов группы приказа (канон SupCom).
        private static readonly List<GroundOrderKind> NodeKinds = new List<GroundOrderKind>();
        private static readonly List<int> NodeAbs = new List<int>();
        private static readonly List<float> ChainWidths = new List<float>();
        // «Живой» поток канона FA: бегущие шевроны только на ногах атаки-движения
        // и патруля (orderline_anim_rate); статичные ноги — наконечник на конце.
        private static readonly List<bool> ChainFlow = new List<bool>();
        private static readonly HashSet<Unit> DrawnTargetExits = new HashSet<Unit>();
        // Очереди инженеров (все с записями — групповое слияние не зависит от выделения).
        private static readonly List<Unit> EngSeqUnits = new List<Unit>();
        private static readonly List<IReadOnlyList<BuildTasks.SeqEntry>> EngSeqs =
            new List<IReadOnlyList<BuildTasks.SeqEntry>>();
        // Скретч точек/цветов очередной инженерной цепочки (последний живой кусок
        // долга §12-8: два new List на инженера на каждый Repaint).
        private static readonly List<Vector3> EngPts = new List<Vector3>();
        private static readonly List<Color> EngCols = new List<Color>();
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
        /// <summary>Групповой вейпоинт (один на приказ, в центроиде): крупнее
        /// индивидуального — размер растёт как √n юнитов приказа (канон SupCom
        /// DrawNode, кламп 1..1.6×), форма/цвет — вид приказа точки.</summary>
        private static void DrawGroupWaypointMarker(Camera cam, Vector3 world, Color color, float alpha,
            WaypointShape shape, float sizeMul = 1f)
        {
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f)
                return;
            Vector2 p = new Vector2(sp.x, Screen.height - sp.y);
            if (UnderMinimapWindow(p))
                return;
            DrawWaypointMarkerAt(p, 13f * sizeMul, color, alpha, shape);
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
            int qi, Color c, float a, int loopStartAbs = -1)
        {
            // Узлы очереди В ПОРЯДКЕ КЛИКОВ: дозревшие/текущие цели — в голове,
            // дальше точки, с целями, вставленными после своей позиции серии.
            // Каждый узел несёт вид своей точки (per-point, v0.114): нога красится
            // цветом ВХОДА в узел (канон SupCom: attack-move нога красная), маркер —
            // формой вида. loopStartAbs ≥ 0 — патрульный цикл, замыкающая нога идёт
            // на начало хвоста (конвертация «от точки»), не на первый узел.
            var targets = GroundOrders.TargetQueueOf(unit);
            ChainNodes.Clear();
            NodeGids.Clear();
            ChainNodeTargets.Clear();
            ChainNodeOwned.Clear();
            NodeKinds.Clear();
            NodeAbs.Clear();
            int loopNode = -1;
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
                    NodeKinds.Add(GroundOrderKind.Attack);
                    NodeAbs.Add(-1);
                }
            for (int i = qi; i < queue.Count; i++)
            {
                // Групповая точка — узел-центроид группы приказа, индивидуальная — своя.
                int gid = GroundOrders.GroupIdOf(unit, i);
                GroundOrderKind kind = GroundOrders.KindOfPoint(unit, i);
                Vector3 wp = GroundVec(queue[i]);
                ChainNodes.Add(gid != 0 && GidPointSums.TryGetValue(gid, out Vector4 acc)
                    ? new Vector3(acc.x / acc.w, acc.y / acc.w, acc.z / acc.w)
                    : wp);
                NodeGids.Add(gid);
                ChainNodeTargets.Add(null);
                ChainNodeOwned.Add(false);
                NodeKinds.Add(kind);
                NodeAbs.Add(i);
                if (i == loopStartAbs)
                    loopNode = ChainNodes.Count - 1;
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
                    NodeKinds.Add(GroundOrderKind.Attack);
                    NodeAbs.Add(-1);
                }
            }
            // Патруль (loopStartAbs = ТЕКУЩАЯ точка): обёртка хвоста цикла
            // [LoopStart..qi) — весь цикл нарисован, голова ленты = куда юнит идёт.
            if (loopStartAbs >= 0)
            {
                int wrapFrom = GroundOrders.PatrolLoopStartOf(unit);
                for (int i = wrapFrom; i < loopStartAbs; i++)
                {
                    int gid = GroundOrders.GroupIdOf(unit, i);
                    ChainNodes.Add(gid != 0 && GidPointSums.TryGetValue(gid, out Vector4 acc)
                        ? new Vector3(acc.x / acc.w, acc.y / acc.w, acc.z / acc.w)
                        : GroundVec(queue[i]));
                    NodeGids.Add(gid);
                    ChainNodeTargets.Add(null);
                    ChainNodeOwned.Add(false);
                    NodeKinds.Add(GroundOrderKind.Patrol);
                    NodeAbs.Add(i);
                }
            }
            if (ChainNodes.Count == 0)
                return;
            // Начало хвоста уже пройдено (Index > LoopStart): цикл замыкаем на первый
            // РИСУЕМЫЙ узел — петля видна на любом участке цикла (меш-лента делает
            // тот же кламп max(LoopStart, DrawStart)).
            if (loopNode < 0 && loopStartAbs >= 0 && ChainNodes.Count >= 2)
                loopNode = 0;

            Color attackC = ModeColor(AirOrderMode.Attack);
            Color attackMoveC = ModeColor(AirOrderMode.AttackMove);
            Color patrolC = ModeColor(AirOrderMode.Patrol);
            // Цвет ноги ВХОДА в точку — вид самой точки (канон SupCom: нога красится
            // цветом приказа, к которому ведёт; attack-move нога красная).
            Color KindColor(GroundOrderKind kind) =>
                kind == GroundOrderKind.AttackMove ? attackMoveC
                : kind == GroundOrderKind.Patrol ? patrolC
                : c;
            // Толщина групповой магистрали: √n юнитов приказа (кламп настройки),
            // личные ветки — единица.
            float GroupWidth(int gid) =>
                gid != 0 && GidPointSums.TryGetValue(gid, out Vector4 acc)
                    ? Mathf.Min(Mathf.Sqrt(Mathf.Max(acc.w, 1f)), Settings.RouteGroupWidthMax)
                    : 1f;
            bool pursuing = GroundOrders.KindOf(unit) == GroundOrderKind.Attack;
            Unit curTarget = GroundOrders.TargetOf(unit);

            Vector3 anchor = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
            anchor.y = GameApi.GroundY(anchor.x, anchor.z);

            int hover = HoverWaypointIndex(cam, ChainNodes);
            ChainPts.Clear();
            ChainCols.Clear();
            ChainAlphas.Clear();
            ChainWidths.Clear();
            ChainFlow.Clear();
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
                float legW = 1f;
                if (nodeTarget != null)
                {
                    // Нога ДО цели — один раз на цель; владелец ноги рисует и крест.
                    drawLeg = DrawnGroundTargets.Add(nodeTarget);
                    ChainNodeOwned[i] = drawLeg;
                    legC = attackC;
                    legA = pursuing && nodeTarget == curTarget ? a : a * 0.6f;
                }
                else
                {
                    legC = KindColor(NodeKinds[i]);
                    legW = GroupWidth(gid);
                    if (prevTarget != null)
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
                }

                if (drawLeg)
                {
                    if (ChainPts.Count == 0)
                        ChainPts.Add(prev);
                    ChainPts.Add(node);
                    ChainCols.Add(legC);
                    // Hover-подсветка ХВОСТА от наведённого узла (v0.114): конвертация
                    // в патруль замыкает хвост — игрок видит, что станет циклом.
                    ChainAlphas.Add(hover >= 0 && i >= hover ? Mathf.Max(legA, 1f) : legA);
                    ChainWidths.Add(legW);
                    ChainFlow.Add(NodeKinds[i] == GroundOrderKind.AttackMove ||
                                  NodeKinds[i] == GroundOrderKind.Patrol);
                }
                else if (ChainPts.Count > 0)
                {
                    // Разрыв: общий сегмент уже нарисован другим членом группы.
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                    ChainWidths.Clear();
                    ChainFlow.Clear();
                }
                prev = node;
                prevGid = gid;
                prevTarget = nodeTarget;
            }
            // Замыкающая нога патруля: последний узел хвоста → его НАЧАЛО (узел
            // loopStartAbs, не первый узел очереди — конвертация «от точки»).
            // Магистральные групповые сегменты дедуплицированы тем же PairKey.
            if (loopNode >= 0 && ChainNodes.Count >= 2)
            {
                int lastGid = NodeGids[NodeGids.Count - 1];
                int loopGid = NodeGids[loopNode];
                Color loopC = KindColor(NodeKinds[loopNode]);
                float loopW = GroupWidth(loopGid);
                bool closeLeg = lastGid != 0 && loopGid != 0
                    ? DrawnGidLegs.Add(PairKey(lastGid, loopGid))
                    : true;
                if (closeLeg)
                {
                    if (ChainPts.Count == 0)
                    {
                        ChainPts.Add(ChainNodes[ChainNodes.Count - 1]);
                        ChainCols.Add(loopC);
                        ChainAlphas.Add(a);
                        ChainWidths.Add(loopW);
                    }
                    ChainPts.Add(ChainNodes[loopNode]);
                    ChainCols.Add(loopC);
                    ChainAlphas.Add(hover >= 0 && loopNode >= hover ? Mathf.Max(a, 1f) : a);
                    ChainWidths.Add(loopW);
                    ChainFlow.Add(true); // цикл патруля — бегущие шевроны
                }
            }
            if (ChainPts.Count >= 2)
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);

            // Маркеры: групповые — один на приказ (в центроиде, крупнее — √n юнитов),
            // индивидуальные — свои; форма = вид точки (квадрат/ромб/цикл), цвет —
            // цвет вида; наведённый — крупнее и ярче + подсказка конвертации хвоста.
            // Цели — крестики: текущая погоня — пульсирующий двойной, ждущие — статичные.
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
                GroundOrderKind kind = NodeKinds[i];
                WaypointShape shape = ShapeOf(kind);
                bool hovered = i == hover;
                // Маркер — БЕЛЫЙ (канон FA: waypoint_color 44/88/ff ffffff), вид
                // приказа несёт ФОРМА маркера и цвет НОГИ.
                if (NodeGids[i] != 0)
                {
                    if (DrawnGidMarkers.Add(NodeGids[i]))
                    {
                        float n = GidPointSums.TryGetValue(NodeGids[i], out Vector4 acc) ? acc.w : 1f;
                        float sizeMul = Mathf.Min(Mathf.Sqrt(Mathf.Max(n, 1f)), 1.6f);
                        DrawGroupWaypointMarker(cam, ChainNodes[i], Color.white,
                            hovered ? 1f : a, shape, hovered ? sizeMul * 1.25f : sizeMul);
                    }
                }
                else
                {
                    DrawWorldWaypointColored(cam, ChainNodes[i], Color.white,
                        hovered ? 1f : a, shape, hovered ? 1.25f : 1f);
                }
                // Подсказка конвертации (выделенным): хвост от этой точки можно
                // замкнуть в патруль — говорим прямо у маркера, как Moho cursor text.
                if (hovered && a > 0.5f && NodeAbs[i] >= 0 &&
                    GroundOrders.CanConvertPatrolAt(unit, NodeAbs[i]))
                {
                    Vector3 sp = cam.WorldToScreenPoint(ChainNodes[i]);
                    if (sp.z > 0f)
                        GUI.Label(new Rect(sp.x + 14f, Screen.height - sp.y - 10f, 320f, 20f),
                            Loc.Tr("ПКМ — патруль отсюда"), _small);
                }
            }
        }

        /// <summary>Точка цели — на поверхности (вода — уровень моря): кресты
        /// по кораблям не проваливаются на дно.</summary>
        private static Vector3 TargetGround(Unit target)
        {
            Vector3 tp = target.transform.position;
            return new Vector3(tp.x, GameApi.RouteY(tp.x, tp.z), tp.z);
        }

        /// <summary>Связи сопровождения наземки/флота (v0.97): лента «эскорт → цель»
        /// и ЗЕЛЁНЫЙ маркер на цели (один на цель; дедуп общий с авиа-эскортом —
        /// DrawPlanCurve). Выделенные ярко, при Shift и остальные приглушённо —
        /// как цепочки.</summary>
        private static void DrawEscortLinks(Camera cam, CommanderModeController controller,
            bool shiftHeld)
        {
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            GroundOrders.CollectEscorts(EscortUnits, EscortTargetsBuf, hq);
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
                float a = own ? 0.87f : 0.2f;
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
                DrawWorldWaypointColored(cam, TargetGround(target), Color.white, a); // канон FA
            }
        }

        private void DrawAircraftRoutes(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            bool shiftHeld = RtsInput.Shift;

            // Наземка/флот: ноги якорь→wp[qi] и wp[i]→wp[i+1]. Свои только:
            // AI-цепочки/планы делят те же реестры, чужие ленты не рисуем.
            List<Unit> gUnits = RouteUnits;
            List<IReadOnlyList<GlobalPosition>> gQueues = RouteQueues;
            List<int> gIdx = RouteIdx;
            GroundOrders.CollectAll(gUnits, gQueues, gIdx, hq);
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
                // Патруль — цикл: центроид групповой точки должен учитывать её у ВСЕХ
                // членов, независимо от того, кто где в цикле (иначе маркер «уезжал»,
                // когда один член проходил точку, и возвращался при замыкании).
                if (GroundOrders.KindOf(unit) == GroundOrderKind.Patrol)
                    qi = Mathf.Min(qi, GroundOrders.PatrolLoopStartOf(unit));
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
                    // ЕДИНОЙ лентой через цель — DrawGroundChain, каждая нога — цвет
                    // вида своей точки. Патруль — циклом с замыкающей ногой на начало
                    // хвоста (PatrolLoopStart, конвертация «от точки»).
                    bool patrol = GroundOrders.KindOf(unit) == GroundOrderKind.Patrol;
                    // Патруль: лента от юнита к ТЕКУЩЕЙ точке, весь цикл — обёрткой
                    // (внутри DrawGroundChain), замыкание на текущую точку.
                    DrawGroundChain(cam, unit, gQueues[gi], GroundOrders.DrawStartIndex(unit),
                        patrol ? ModeColor(AirOrderMode.Patrol) : routeC,
                        own ? 0.87f : 0.2f,
                        patrol ? GroundOrders.DrawStartIndex(unit) : -1);
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
            foreach (Unit unit in BuildTasks.UnitsWithSequences(hq))
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
                float a = own ? 0.87f : 0.2f;
                IReadOnlyList<BuildTasks.SeqEntry> seq = EngSeqs[ei];
                Vector3 prev = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                prev.y = GameApi.GroundY(prev.x, prev.z);
                var pts = EngPts;
                var cols = EngCols;
                pts.Clear();
                cols.Clear();
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
                    DrawWorldWaypointColored(cam, pts[i], Color.white, a); // канон FA
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
                DrawFactoryRally(cam, selAirbase.transform.position, selAirbase, rallyColor, 0.87f);
            // Недострой-фабрика: ралли площадки видно как у готовой (v0.107).
            if (BuildTasks.IsFactorySite(BuildTasks.SelectedTask))
                DrawFactoryRally(cam, BuildTasks.SelectedTask.Site.ToLocalPosition(),
                    BuildTasks.SelectedTask, rallyColor, 0.85f);
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                if (unit is VehicleDepot depot)
                    DrawFactoryRally(cam, unit.transform.position, depot, rallyColor, 0.87f);
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
                ProductionTasks.CollectRallies(RallyAll, hq);
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
            AircraftOrders.CollectAll(airUnits, airPlans, hq);
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
                DrawPlanCurve(cam, unit, airPlans[ai], own ? 0.87f : 0.2f);
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
            ChainWidths.Clear();
            ChainFlow.Clear();
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
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
                        runOpen = false;
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        ChainWidths.Clear();
                        ChainFlow.Clear();
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
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
                        runOpen = false;
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        ChainWidths.Clear();
                        ChainFlow.Clear();
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
                        DrawWorldWaypointColored(cam, etgt, Color.white, alpha); // белый — канон FA
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
                        // Hover подсвечивает ХВОСТ от наведённого узла (единая
                        // семантика с наземкой, v0.114).
                        ChainAlphas.Add(hover >= 0 && i >= hover
                            ? Mathf.Max(alpha, 1f) : alpha);
                        // Магистраль группы — толще: √n бортов приказа (кламп настройки).
                        ChainWidths.Add(gid != 0 && AirGidCentroids.TryGetValue(gid, out Vector4 gc)
                            ? Mathf.Min(Mathf.Sqrt(Mathf.Max(gc.w, 1f)), Settings.RouteGroupWidthMax)
                            : 1f);
                        ChainFlow.Add(seg.Mode == AirOrderMode.AttackMove ||
                                      seg.Mode == AirOrderMode.Patrol);
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
                        ChainWidths.Add(firstGid != 0 && AirGidCentroids.TryGetValue(firstGid, out Vector4 fc)
                            ? Mathf.Min(Mathf.Sqrt(Mathf.Max(fc.w, 1f)), Settings.RouteGroupWidthMax)
                            : 1f);
                        ChainFlow.Add(seg.Mode == AirOrderMode.AttackMove ||
                                      seg.Mode == AirOrderMode.Patrol);
                    }
                }
                // Маркеры: групповая точка (groupId≠0) — ОДНА на приказ группы бортов
                // (в центроиде их формационных точек, крупнее — √n), индивидуальная —
                // своя; форма = вид сегмента (квадрат/ромб/цикл, v0.114).
                // Служебные точки — НЕ вейпоинты приказа: маркер не рисуем (единое
                // правило со сканером — AircraftOrders.IsServicePoint).
                WaypointShape shape = ShapeOf(seg.Mode);
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
                                new Vector3(acc.x / acc.w, acc.y / acc.w, acc.z / acc.w),
                                Color.white, alpha, // маркер белый — вид несёт форма (канон FA)
                                shape,
                                Mathf.Min(Mathf.Sqrt(Mathf.Max(acc.w, 1f)), 1.6f));
                    }
                    else
                        DrawWorldWaypointColored(cam, wps[i], Color.white, alpha, shape);
                }
                if (loop)
                {
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
                    runOpen = false;
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                    ChainWidths.Clear();
                    ChainFlow.Clear();
                }
            }
            if (runOpen)
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
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
            EnsureRibbonTextures();
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            for (int i = 0; i < FanNumbers.Length; i++) // номера целей веера — без аллокаций в OnGUI
                FanNumbers[i] = (i + 1).ToString();
        }

        /// <summary>Номера накопленных целей «Распределения» (заполняются один раз).</summary>
        private static readonly string[] FanNumbers = new string[32];

        /// <summary>ЯО (v0.139): заряженный режим «Ядерный удар» — у курсора КОЛЬЦО
        /// зоны поражения (нативная формула шоквейва от yield ракеты) + очередь
        /// точек пуска носителей пульсирующими крестами (ушедшие снимает сервис).</summary>
        private static void DrawNukeAim()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            Color c = new Color(1f, 0.75f, 0.2f, 0.9f);
            // Точки пуска в очереди: живут до пуска/гибели носителя.
            for (int i = 0; i < WarheadService.Strikes.Count; i++)
            {
                WarheadService.StrikePoint s = WarheadService.Strikes[i];
                Vector3 sp = cam.WorldToScreenPoint(s.Pos.ToLocalPosition());
                if (sp.z <= 0f)
                    continue;
                Vector2 scr = new Vector2(sp.x, Screen.height - sp.y);
                if (UnderMinimapWindow(scr))
                    continue;
                DrawCross(scr, 22f, c);
                ClosedLabel(scr.x - 10f, scr.y + 12f, Loc.Tr("ЯО"));
            }
            if (OrderModes.Pending != OrderMode.Nuke)
                return;
            // Прицел: кольцо поражения в точке земли под курсором.
            Ray ray = GameApi.ScreenRay(Input.mousePosition);
            if (!GameApi.TryGetGroundPoint(ray, out Vector3 ground))
                return;
            float radiusM = WarheadService.BlastRadiusM();
            Vector3 center = new Vector3(ground.x, GameApi.GroundYNoUnits(ground.x, ground.z), ground.z);
            Vector3 centerSp = cam.WorldToScreenPoint(center);
            if (centerSp.z <= 0f)
                return;
            Vector3 edge = cam.WorldToScreenPoint(center + cam.transform.right * radiusM);
            float rPx = Mathf.Abs(edge.x - centerSp.x);
            if (rPx < 4f)
                return;
            Vector2 ctr = new Vector2(centerSp.x, Screen.height - centerSp.y);
            int dots = Mathf.Clamp((int)(2f * Mathf.PI * rPx / 9f), 12, 64);
            for (int i = 0; i < dots; i++)
            {
                float a = i / (float)dots * 2f * Mathf.PI;
                Vector2 p = ctr + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rPx;
                DrawRect(new Rect(p.x - 1.3f, p.y - 1.3f, 2.6f, 2.6f), c);
            }
            ClosedLabel(ctr.x - 16f, ctr.y - rPx - 16f,
                Loc.F("ЯО · R {0} м", radiusM.ToString("F0")));
        }

        /// <summary>Накопленные цели «Распределения» (режим R): пульсирующий фиолетовый
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

        /// <summary>Отметки залпов флота (FleetSalvo, v0.119–0.122): ОЧЕРЕДЬ — крест
        /// волны, ждущей трубы (по цели — СЛЕДУЕТ за ней), с линиями «корабль →
        /// точка»; СТРЕЛЯННЫЕ волны (Marks) — угасающий крест в точке удара
        /// (ленты — лишь MarkRibbonSec после залпа). Видно, кому какой приказ
        /// отдан и что ещё pending (точки GlobalPosition — FloatingOrigin-устойчивы;
        /// мёртвые корабли пропускаются).</summary>
        private const float MarkRibbonSec = 15f;
        // Голова цепи очереди: отстрелянная точка живёт в цепи, пока ракета летит.
        private const float ChainHeadSec = 20f;

        // Цепь очереди пусков на корабль (перезаполняется каждый кадр отрисовки).
        private static readonly Dictionary<Unit, List<Vector3>> SalvoChains = new Dictionary<Unit, List<Vector3>>();

        private static void DrawFleetSalvoMarks()
        {
            // Repaint-гейт обязателен: рисование в Layout-проходе ломает IMGUI-
            // состояние для ВСЕГО после нас (плейтест v0.127: пропали значки юнитов).
            if (Event.current.type != EventType.Repaint)
                return;
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            float now = Time.unscaledTime;
            Color baseC = ModeColor(AirOrderMode.Salvo); // цвет семейства «залп»

            // ОЧЕРЕДЬ ПУСКОВ (канон SupCom): у корабля ОДНА общая очередь запусков —
            // лента-ЦЕПЬ «корабль → точка₁ → точка₂ → …» как маршрут приказов, а не
            // пучок лент к каждой точке. Кресты — вейпоинты очереди.
            var queue = FleetSalvo.Queue;
            SalvoChains.Clear();
            for (int qi = 0; qi < queue.Count; qi++)
            {
                FleetSalvo.QueuedWave wave = queue[qi];
                // точка волны — на ПОВЕРХНОСТИ (RouteY: вода — зеркало, не дно):
                // ровно там же её пикает сканер вейпоинтов — иначе драг не берёт
                Vector3 aim = wave.Aim.ToLocalPosition();
                Vector3 world = wave.Target != null && wave.Target.disabled == false
                    ? GameApi.UnitAnchorWorld(wave.Target)
                    : new Vector3(aim.x, GameApi.RouteY(aim.x, aim.z), aim.z);
                Vector3 wsp = cam.WorldToScreenPoint(world);
                var wscr = new Vector2(wsp.x, Screen.height - wsp.y);
                Color c = baseC * 0.95f;
                if (wsp.z > 0f && !UnderMinimapWindow(wscr))
                {
                    float pulse = 12f + 6f * Mathf.Abs(Mathf.Sin(now * 3f));
                    DrawCross(wscr, pulse, c);
                    DrawCross(wscr, pulse * 0.5f, c);
                }
                // копим точку в цепь каждого ждущего корабля (порядок = порядок кликов)
                if (wsp.z > 0f)
                    for (int si = 0; si < wave.Ships.Count; si++)
                    {
                        Unit ship = wave.Ships[si];
                        if (ship == null || ship.disabled) // Unity fake-null
                            continue;
                        if (!SalvoChains.TryGetValue(ship, out List<Vector3> chain))
                        {
                            chain = new List<Vector3>();
                            SalvoChains[ship] = chain;
                        }
                        chain.Add(world);
                    }
            }
            // цепь каждого корабля: якорь → [последняя ОТСТРЕЛЯННАЯ точка, пока
            // ракета в полёте (ChainHeadSec)] → ждущие точки очереди; альфа гаснет
            // к хвосту (голова — ближайший пуск). КОНТРАКТ: цвета ПО НОГАМ (N-1).
            var marks = FleetSalvo.Marks;
            foreach (KeyValuePair<Unit, List<Vector3>> kv in SalvoChains)
            {
                Unit ship = kv.Key;
                List<Vector3> chain = kv.Value;
                if (ship == null || ship.disabled || chain.Count == 0) // Unity fake-null
                    continue;
                ChainPts.Clear();
                ChainCols.Clear();
                ChainAlphas.Clear();
                ChainPts.Add(GameApi.UnitAnchorWorld(ship));
                // голова: самая свежая отстрелянная точка ЭТОГО корабля (Until −
                // MarkTtlSec = момент отстрела) — первый клик тоже виден в цепи,
                // пока ракета летит (канон SupCom: пуск живёт в очереди)
                float bestFired = float.MinValue;
                int headMark = -1;
                for (int mi = 0; mi < marks.Count; mi++)
                {
                    FleetSalvo.SalvoMark m = marks[mi];
                    float firedAt = m.Until - FleetSalvo.MarkTtlSec;
                    if (now - firedAt > ChainHeadSec || firedAt <= bestFired)
                        continue;
                    for (int si = 0; si < m.Ships.Count; si++)
                        if (m.Ships[si] == ship)
                        {
                            bestFired = firedAt;
                            headMark = mi;
                            break;
                        }
                }
                if (headMark >= 0)
                {
                    Vector3 hp = marks[headMark].Point.ToLocalPosition();
                    ChainPts.Add(new Vector3(hp.x, GameApi.RouteY(hp.x, hp.z), hp.z));
                }
                for (int pi = 0; pi < chain.Count; pi++)
                    ChainPts.Add(chain[pi]);
                int legs = ChainPts.Count - 1;
                for (int l = 0; l < legs; l++)
                {
                    ChainCols.Add(baseC);
                    ChainAlphas.Add(Mathf.Max(0.35f, 0.85f - 0.12f * l));
                }
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, meshGated: false);
            }

            // отстрелянные волны: угасающие отметки (ждущие в очереди не рисуем —
            // их крест уже нарисован выше по живой цели)
            if (marks.Count == 0)
                return;
            for (int mi = marks.Count - 1; mi >= 0; mi--)
            {
                FleetSalvo.SalvoMark m = marks[mi];
                if (now >= m.Until)
                {
                    marks.RemoveAt(mi);
                    continue;
                }
                bool waiting = false;
                for (int qi = 0; qi < queue.Count; qi++)
                    if (queue[qi].MarkId == m.Id)
                    {
                        waiting = true;
                        break;
                    }
                if (waiting)
                    continue;
                // ОТСТРЕЛЯННАЯ волна: тусклый СТАТИЧНЫЙ крест (без пульса) — visibly
                // отличим от живой ждущей (та пульсирует ярко); тащить нечего
                Color c = baseC * (0.35f * Mathf.Clamp01((m.Until - now) / 10f));
                Vector3 mp = m.Point.ToLocalPosition();
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(mp.x, GameApi.RouteY(mp.x, mp.z), mp.z));
                if (sp.z <= 0f)
                    continue;
                var scr = new Vector2(sp.x, Screen.height - sp.y);
                if (!UnderMinimapWindow(scr))
                {
                    float pulse = 12f + 6f * Mathf.Abs(Mathf.Sin(now * 3f));
                    DrawCross(scr, pulse, c);
                    DrawCross(scr, pulse * 0.5f, c);
                }
                // ленты отстрелянной волны — только 15 с после залпа («кто только
                // что бил»); дальше тусклый крест без лент — паутина хвостов
                // читалась как «лента между целями» (плейтест v0.130)
                if (m.Until - now <= MarkRibbonSec)
                {
                    Vector3 markWorld = new Vector3(mp.x, GameApi.RouteY(mp.x, mp.z), mp.z);
                    for (int si = 0; si < m.Ships.Count; si++)
                    {
                        Unit ship = m.Ships[si];
                        if (ship == null || ship.disabled) // Unity fake-null
                            continue;
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        ChainPts.Add(GameApi.UnitAnchorWorld(ship));
                        ChainPts.Add(markWorld);
                        ChainCols.Add(c);
                        ChainAlphas.Add(0.4f * c.a);
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, meshGated: false);
                    }
                }
            }
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
