using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Временный IMGUI-оверлей; целевой UI — uGUI на следующих итерациях.</summary>
    public sealed partial class CommanderHud : MonoBehaviour
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
        // Смена языка «на лету» (v0.153) бампит Loc.Version — кэш сбрасывается.
        private static string _headerActive;
        private static string _headerIdle;
        private static int _headerLocVersion = -1;

        private static string BuildHeader(bool active) =>
            $"SUPREME NUCLEAR v{PluginInfo.Version}  |  " + Loc.F("Режим командира: {0}  |  {1} — переключить",
                active ? "<color=lime>" + Loc.Tr("ВКЛ") + "</color>" : Loc.Tr("ВЫКЛ"),
                Settings.ToggleCommanderKey);

        private void OnGUI()
        {
            if (!GameApi.InMission)
                return;

            EnsureStyles();

            if (_headerLocVersion != Loc.Version) // живая смена языка — сброс кэша заголовка
            {
                _headerLocVersion = Loc.Version;
                _headerActive = null;
                _headerIdle = null;
            }

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
                    DrawRepairMarkers();
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
            // Соло-плейтест на ИИ (v0.167.1): состояние ИИ-командиров (обновление
            // раз в 15 с вместе с лог-снапшотом) + счётчик ошибок сцены — вердикт
            // плейтеста «на глаз» заменяется показаниями (PLAYTEST.md).
            float y = 220f;
            if (AiCommanderService.DebugLines.Count > 0)
            {
                string age = Time.unscaledTime - AiCommanderService.DebugLinesAt > 20f
                    ? " (обновление раз в 15 с…)"
                    : "";
                foreach (var kv in AiCommanderService.DebugLines)
                {
                    GUI.Label(new Rect(10f, y, 1200f, 20f), Util.FitLine(kv.Value, 118), _small);
                    y += 20f;
                }
                GUI.Label(new Rect(10f, y, 1200f, 20f), "ИИ-снапшот" + age, _small);
                y += 20f;
            }
            GUI.Label(new Rect(10f, y, 1200f, 20f),
                $"log: ошибок {Log.SessionErrors}, warn {Log.SessionWarnings} за сцену", _small);
            y += 20f;
            for (int i = 0; i < Log.LastErrors.Count && i < 2; i++)
            {
                GUI.Label(new Rect(10f, y, 1200f, 20f), Util.FitLine("  ↳ " + Log.LastErrors[i], 118), _small);
                y += 20f;
            }
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

