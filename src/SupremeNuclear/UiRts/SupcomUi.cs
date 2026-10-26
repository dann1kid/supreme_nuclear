using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace SupremeNuclear
{
    /// <summary>uGUI-интерфейс в духе SupCom: верхняя панель экономики (поток),
    /// список выделения (группы по типам, клик — подвыделение), командная карта
    /// (приказы/стойка/профиль высоты), контекстная панель (покупки/апгрейд).</summary>
    public sealed class SupcomUi : MonoBehaviour
    {
        public static SupcomUi Instance { get; private set; }

        private static readonly Color PanelBg = new Color(0.07f, 0.09f, 0.11f, 0.92f);
        private static readonly Color PanelBg2 = new Color(0.10f, 0.13f, 0.16f, 0.92f);
        private static readonly Color Accent = new Color(0.35f, 1f, 0.5f, 1f);
        private static readonly Color BtnBg = new Color(0.16f, 0.20f, 0.24f, 1f);
        private static readonly Color BtnActive = new Color(0.20f, 0.45f, 0.26f, 1f);
        private static readonly Color TextMain = new Color(0.92f, 0.95f, 0.92f, 1f);
        private static readonly Color TextDim = new Color(0.62f, 0.66f, 0.62f, 1f);
        private static readonly Color WarnRed = new Color(1f, 0.42f, 0.36f, 1f);

        private Canvas _canvas;
        private Font _font;

        /// <summary>Канвас для внешних панелей (миникарта); пересобирается по сценам.</summary>
        public Canvas Canvas => _canvas;
        public Font UiFont => _font;

        // Верхняя панель.
        private Text _fundsText;
        private Text _incomeText;
        private Text _extractorsText;

        // Список выделения (вкладка «Список» нижней панели).
        private readonly List<(Text count, Image icon, Button btn)> _rosterRows = new();

        // Панель свойств выделения (левая колонка над ростером).
        private RectTransform _infoPanel;
        private Text _infoTitle;
        private Text _infoClass;
        private RectTransform _infoHpFill;
        private Text _infoHpTxt;
        private RectTransform _infoFuelTrack;
        private RectTransform _infoFuelFill;
        private Text _infoFuelTxt;
        private Text _infoMove;
        private Text _infoState;
        private readonly Text[] _infoOrder = new Text[3];
        private Text _infoWeaponsHeader;
        private readonly Text[] _infoWeapons = new Text[8];
        private int _infoHash = -1;

        // Командная карта.
        private RectTransform _commandPanel;
        private readonly List<(Button btn, Image img, System.Action action)> _cmdButtons = new();
        private Text _modeHint;

        // Нижняя панель-табы: 0 — производство, 1 — список выделения, 2 — апгрейд.
        private const int TabProduction = 0;
        private const int TabRoster = 1;
        private const int TabUpgrade = 2;
        private const int TabTemplates = 3;
        private RectTransform _tabPanel;
        private Text _contextTitle;          // заголовок контента вкладки
        private RectTransform _strip1;       // верхняя строка-лента (спека DESIGN 2.10)
        private RectTransform _strip1Content;
        private RectTransform _strip2;       // нижняя строка-лента: очередь стройки инженера
        private RectTransform _strip2Content;
        private GameObject _strip2Nav;       // стрелки нижней строки (вкл/выкл вместе с ней)
        private int _queueHash = -1;         // ключ очереди (стройки/производства)
        private Button _pauseBtn;            // контекстные кнопки фабрики (DESIGN 2.10)
        private Button _cycleBtn;
        private Button _tplBtn;              // снять очередь фабрики в персистентный шаблон
        private VehicleDepot _ctxDepot;      // фабрика, к которой относятся Пауза/Цикл
        private Airbase _ctxAirbase;         // ...или аэродром
        private RectTransform _tierRoot;     // полоса выбора пула (тиры каталога)
        private readonly (Button btn, Image img, int tier)[] _tierButtons =
            new (Button, Image, int)[4];     // Все / T1 / T2 / T3
        private int _prodTier;               // 0 — все, 1..3 — тир по цене каталога
        private readonly (Button btn, Image img)[] _tabButtons = new (Button, Image)[4];
        private int _activeTab = TabRoster;
        private int _contextHash = -1;
        /// <summary>selHash, для которого игрок выбрал вкладку вручную: пока выделение
        /// не сменилось, автопереключение не отбирает вкладку у игрока.</summary>
        private int _manualTabForSel = -2;
        private float _nextRefresh;
        private bool _loggedActiveOnce;

        private void Awake()
        {
            Instance = this;
            try
            {
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }
            catch { _font = null; }
            if (_font == null)
            {
                try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
            }
            Log.Info("SupcomUi: шрифт — " + (_font != null ? _font.name : "NULL"));

            BuildSafe("canvas", BuildCanvas);
            BuildSafe("topbar", BuildTopBar);
            BuildSafe("info", BuildSelectionInfo);
            BuildSafe("commandcard", BuildCommandCard);
            BuildSafe("tabs", BuildTabPanel);
            Log.Info("SupcomUi: построен, canvas=" + (_canvas != null));
        }

        private void BuildSafe(string step, System.Action build)
        {
            try
            {
                build();
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: сбой этапа " + step + " — " + e);
            }
        }

        private void Update()
        {
            try
            {
                UpdateSafe();
            }
            catch (System.Exception e)
            {
                if (Time.unscaledTime - _lastErrorAt > 5f)
                {
                    _lastErrorAt = Time.unscaledTime;
                    Log.Error("SupcomUi: сбой Update — " + e.GetType().Name + ": " + e.Message + " | " + e.StackTrace);
                }
            }
        }

        private float _lastErrorAt = -10f;

        private void UpdateSafe()
        {
            EnsureUi(); // канвас мог быть уничтожен сменой сцены — пересобираем лениво
            if (_canvas == null)
                return;

            // Нижние панели — КОНТЕКТ выделения: без выделения всё низ экрана свободен
            // (экономика сверху остаётся всегда).
            var ctxController = CommanderModeController.Instance;
            bool contextVisible = ctxController != null && ctxController.IsActive &&
                (ctxController.Selection.Selected.Count > 0 ||
                 ctxController.Selection.SelectedAirbase != null ||
                 BuildTasks.Pending != null ||
                 OrderTemplates.PendingTemplate.template != null ||
                 BuildTasks.SelectedTask != null);
            if (_infoPanel != null && _infoPanel.gameObject.activeSelf != contextVisible)
                _infoPanel.gameObject.SetActive(contextVisible);
            if (_commandPanel != null && _commandPanel.gameObject.activeSelf != contextVisible)
                _commandPanel.gameObject.SetActive(contextVisible);
            if (_tabPanel != null && _tabPanel.gameObject.activeSelf != contextVisible)
                _tabPanel.gameObject.SetActive(contextVisible);

            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + 0.2f;
            RefreshTopBar();
            RefreshSelectionInfo();
            RefreshCommands();
            RefreshTabs();
        }

        // ---------- строительство ----------

        /// <summary>Канвас живёт через смены сцен; если уничтожен (или ещё не создан) — строим всё заново.</summary>
        private void EnsureUi()
        {
            if (_canvas != null)
                return;

            _cmdButtons.Clear();
            _rosterRows.Clear();
            _contextHash = -1;
            _infoHash = -1;
            _loggedActiveOnce = false;
            _fundsText = null;
            _incomeText = null;
            _extractorsText = null;
            _modeHint = null;

            BuildSafe("canvas", BuildCanvas);
            BuildSafe("topbar", BuildTopBar);
            BuildSafe("info", BuildSelectionInfo);
            BuildSafe("commandcard", BuildCommandCard);
            BuildSafe("tabs", BuildTabPanel);
            Log.Info("SupcomUi: (re)built, canvas=" + (_canvas != null));
        }

        private void BuildCanvas()
        {
            if (EventSystem.current == null)
            {
                GameObject es = new GameObject("SN_EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                DontDestroyOnLoad(es);
            }

            var go = new GameObject("SN_Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvas = go.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30000;
            DontDestroyOnLoad(go); // переживает смену сцены
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // UiScale «укрупняет» панели: меньше reference — крупнее контент на том же экране.
            scaler.referenceResolution = new Vector2(1920f, 1080f) / Mathf.Clamp(Settings.UiScale, 0.75f, 2f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        private RectTransform Panel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color bg)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            go.GetComponent<Image>().color = bg;
            return rt;
        }

        private Text Label(Transform parent, string name, string text, int size, Color color, TextAnchor align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            return t;
        }

        private Button Button_(Transform parent, string name, string label, System.Action onClick, int fontSize = 16)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            go.GetComponent<Image>().color = BtnBg;
            var btn = go.GetComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());
            var t = Label(go.transform, "lbl", label, fontSize, TextMain, TextAnchor.MiddleCenter);
            t.raycastTarget = false;
            return btn;
        }

        private Image Icon(Transform parent, Sprite sprite, Vector2 size)
        {
            var go = new GameObject("icon", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.preserveAspect = true;
            img.raycastTarget = false;
            return img;
        }

        private void BuildTopBar()
        {
            var bar = Panel("TopBar", _canvas.transform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -108f), new Vector2(620f, -56f), PanelBg);

            var f = Label(bar, "funds", "0", 30, Accent, TextAnchor.MiddleLeft);
            _fundsText = f;
            var rtF = (RectTransform)f.transform;
            rtF.anchorMin = new Vector2(0f, 0f);
            rtF.anchorMax = new Vector2(0.45f, 1f);
            rtF.offsetMin = new Vector2(14f, 0f);
            rtF.offsetMax = Vector2.zero;

            _incomeText = Label(bar, "income", "+0/мин", 17, TextMain, TextAnchor.MiddleLeft);
            var rtI = (RectTransform)_incomeText.transform;
            rtI.anchorMin = new Vector2(0.45f, 0.5f);
            rtI.anchorMax = new Vector2(1f, 1f);
            rtI.offsetMin = new Vector2(6f, 0f);
            rtI.offsetMax = new Vector2(-10f, 0f);

            _extractorsText = Label(bar, "extractors", "экстракторы 0/0", 15, TextDim, TextAnchor.MiddleLeft);
            var rtE = (RectTransform)_extractorsText.transform;
            rtE.anchorMin = new Vector2(0.45f, 0f);
            rtE.anchorMax = new Vector2(1f, 0.5f);
            rtE.offsetMin = new Vector2(6f, 0f);
            rtE.offsetMax = new Vector2(-10f, 0f);
        }

        /// <summary>Строка панели от верхнего края: якоря (0..1, 1), отступы 12/8.</summary>
        private static void Row(RectTransform rt, float fromTop, float toTop)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(12f, -toTop);
            rt.offsetMax = new Vector2(-8f, -fromTop);
        }

        private void BuildSelectionInfo()
        {
            var panel = Panel("SelectionInfo", _canvas.transform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(12f, 162f), new Vector2(430f, 534f), PanelBg);
            _infoPanel = panel;

            _infoTitle = Label(panel, "title", "", 17, TextMain, TextAnchor.UpperLeft);
            Row((RectTransform)_infoTitle.transform, 6f, 28f);
            _infoClass = Label(panel, "class", "", 13, TextDim, TextAnchor.UpperLeft);
            Row((RectTransform)_infoClass.transform, 30f, 48f);

            _infoHpFill = UiKit.ProgressBar(panel, "hp", new Color(0f, 0f, 0f, 0.35f),
                new Color(0.4f, 1f, 0.5f, 0.9f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -70f), new Vector2(-96f, -54f));
            _infoHpTxt = Label(panel, "hptxt", "", 13, TextDim, TextAnchor.MiddleRight);
            var rtH = (RectTransform)_infoHpTxt.transform;
            rtH.anchorMin = rtH.anchorMax = new Vector2(1f, 1f);
            rtH.pivot = new Vector2(1f, 1f);
            rtH.offsetMin = new Vector2(-84f, -72f);
            rtH.offsetMax = new Vector2(-8f, -52f);

            _infoFuelFill = UiKit.ProgressBar(panel, "fuel", new Color(0f, 0f, 0f, 0.35f),
                new Color(0.95f, 0.75f, 0.3f, 0.9f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -92f), new Vector2(-96f, -76f));
            _infoFuelTrack = (RectTransform)_infoFuelFill.parent;
            _infoFuelTxt = Label(panel, "fueltxt", "", 13, TextDim, TextAnchor.MiddleRight);
            var rtF = (RectTransform)_infoFuelTxt.transform;
            rtF.anchorMin = rtF.anchorMax = new Vector2(1f, 1f);
            rtF.pivot = new Vector2(1f, 1f);
            rtF.offsetMin = new Vector2(-84f, -94f);
            rtF.offsetMax = new Vector2(-8f, -74f);

            _infoMove = Label(panel, "move", "", 13, TextMain, TextAnchor.UpperLeft);
            Row((RectTransform)_infoMove.transform, 100f, 118f);
            _infoState = Label(panel, "state", "", 13, TextDim, TextAnchor.UpperLeft);
            Row((RectTransform)_infoState.transform, 120f, 138f);
            for (int i = 0; i < _infoOrder.Length; i++)
            {
                _infoOrder[i] = Label(panel, "order" + i, "", 13, TextMain, TextAnchor.UpperLeft);
                Row((RectTransform)_infoOrder[i].transform, 140f + 18f * i, 158f + 18f * i);
            }

            _infoWeaponsHeader = Label(panel, "whdr", "Вооружение", 13, TextDim, TextAnchor.UpperLeft);
            Row((RectTransform)_infoWeaponsHeader.transform, 200f, 218f);
            for (int i = 0; i < _infoWeapons.Length; i++)
            {
                _infoWeapons[i] = Label(panel, "w" + i, "", 13, TextMain, TextAnchor.UpperLeft);
                Row((RectTransform)_infoWeapons[i].transform, 218f + 18f * i, 236f + 18f * i);
            }
        }

        private void BuildCommandCard()
        {
            // Слева-внизу, под панелью свойств выделения (SupCom-раскладка):
            // верх — подсказка режима, ниже — сетка команд.
            var panel = Panel("CommandCard", _canvas.transform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(12f, 12f), new Vector2(430f, 150f), PanelBg);
            _commandPanel = panel;

            _modeHint = Label(panel, "hint", "", 13, TextDim, TextAnchor.UpperLeft);
            var rtH = (RectTransform)_modeHint.transform;
            rtH.anchorMin = new Vector2(0f, 1f);
            rtH.anchorMax = new Vector2(1f, 1f);
            rtH.offsetMin = new Vector2(10f, -20f);
            rtH.offsetMax = new Vector2(-10f, -2f);

            var grid = Panel("grid", panel, Vector2.zero, Vector2.one,
                new Vector2(8f, 6f), new Vector2(-8f, -24f), new Color(0f, 0f, 0f, 0f));
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(98f, 30f);
            layout.spacing = new Vector2(6f, 6f);
            layout.padding = new RectOffset(2, 0, 2, 0);
            layout.startCorner = GridLayoutGroup.Corner.UpperLeft;
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = 4;
        }

        private void BuildTabPanel()
        {
            string step = "panel";
            try
            {
            BuildTabPanelSafe();
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: сбой этапа tabs, шаг '" + step + "' — " + e.GetType().Name +
                          ": " + e.Message + " | " + e.StackTrace);
            }
        }

        private void BuildTabPanelSafe()
        {
            string step = "panel";
            try
            {
            // Схема (DESIGN 2.10, уточнение игрока): вкладки-иконки СЛЕВА переключают
            // контент ДВУХ строк-лент; сверху полоса пула (тиры каталога); у каждой строки
            // свои кнопки листания у краёв. Контекст-колонки паузы/цикла/шаблонов — этап B
            // (появляются вместе с очередями фабрик).
            var panel = Panel("TabPanel", _canvas.transform,
                new Vector2(0.34f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 12f), new Vector2(-12f, 118f), PanelBg);
            _tabPanel = panel;

            var tabs = Panel("tabs", panel, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(4f, 4f), new Vector2(40f, -4f), new Color(0f, 0f, 0f, 0.15f));
            var vlayout = tabs.gameObject.AddComponent<VerticalLayoutGroup>();
            vlayout.spacing = 6f;
            vlayout.padding = new RectOffset(2, 2, 4, 4);
            vlayout.childControlWidth = false;
            vlayout.childControlHeight = false;
            vlayout.childForceExpandWidth = false;
            vlayout.childForceExpandHeight = false;
            vlayout.childAlignment = TextAnchor.UpperCenter;

            string[] glyphs = { "$", "#", "^", "Ш" };
            for (int i = 0; i < _tabButtons.Length; i++)
            {
                int index = i;
                Button b = Button_(tabs, "tab" + i, glyphs[i], () => SwitchTab(index), 15);
                var brt = (RectTransform)b.transform;
                brt.sizeDelta = new Vector2(28f, 28f);
                var le = b.gameObject.AddComponent<LayoutElement>();
                le.minWidth = 28f;
                le.minHeight = 28f;
                le.preferredWidth = 28f;
                le.preferredHeight = 28f;
                _tabButtons[i] = (b, b.GetComponent<Image>());
            }

step = "title";
            _contextTitle = Label(panel, "title", "", 13, TextDim, TextAnchor.MiddleRight);
            var rtT = (RectTransform)_contextTitle.transform;
            rtT.anchorMin = new Vector2(0.5f, 1f);
            rtT.anchorMax = new Vector2(1f, 1f);
            rtT.offsetMin = new Vector2(10f, -22f);
            rtT.offsetMax = new Vector2(-8f, -2f);

            // Полоса пула (тиры каталога) — видна только на вкладке «Производство».
step = "tiers";
            _tierRoot = Panel("tiers", panel, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(196f, -24f), new Vector2(600f, -2f), new Color(0f, 0f, 0f, 0f));
            var tlayout = _tierRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
            tlayout.spacing = 4f;
            tlayout.childControlWidth = false;
            tlayout.childControlHeight = false;
            tlayout.childForceExpandWidth = false;
            tlayout.childForceExpandHeight = false;
            tlayout.childAlignment = TextAnchor.MiddleLeft;
            string[] tiers = { "Все", "I", "II", "III" };
            for (int i = 0; i < _tierButtons.Length; i++)
            {
                int tier = i;
                Button b = Button_(_tierRoot, "tier" + i, tiers[i], () => SwitchTier(tier), 12);
                ((RectTransform)b.transform).sizeDelta = new Vector2(38f, 20f);
                b.gameObject.AddComponent<LayoutElement>().minWidth = 38f;
                _tierButtons[i] = (b, b.GetComponent<Image>(), i);
            }

            // Две строки-ленты; по 70 px с каждого края — их кнопки листания.
step = "strip1";
            _strip1 = UiKit.HStrip(panel, "strip1",
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(196f, -92f), new Vector2(-196f, -26f), out _strip1Content);
step = "strip2";
            _strip2 = UiKit.HStrip(panel, "strip2",
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(196f, -170f), new Vector2(-196f, -104f), out _strip2Content);

step = "arrows";
            UiKit.StripArrows(panel, _font, _strip1Content, 66f, true,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(118f, -92f), new Vector2(192f, -26f));
            UiKit.StripArrows(panel, _font, _strip1Content, 66f, false,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-192f, -92f), new Vector2(-118f, -26f));
            _strip2Nav = new GameObject("strip2nav", typeof(RectTransform));
            var navRoot = (RectTransform)_strip2Nav.transform;
            navRoot.SetParent(panel, false);
            navRoot.anchorMin = Vector2.zero;
            navRoot.anchorMax = Vector2.one;
            navRoot.offsetMin = navRoot.offsetMax = Vector2.zero;
            UiKit.StripArrows(navRoot, _font, _strip2Content, 66f, true,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(118f, -170f), new Vector2(192f, -104f));
            UiKit.StripArrows(navRoot, _font, _strip2Content, 66f, false,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-192f, -170f), new Vector2(-118f, -104f));
            _strip2Nav.SetActive(false);

            // Контекстные кнопки фабрики (2.10): Пауза и Цикл — у правого края нижней строки.
            var ctxGo = new GameObject("factoryCtx", typeof(RectTransform));
            var ctxRt = (RectTransform)ctxGo.transform;
            ctxRt.SetParent(panel, false);
            ctxRt.anchorMin = new Vector2(0f, 1f);
            ctxRt.anchorMax = new Vector2(0f, 1f);
            ctxRt.offsetMin = new Vector2(44f, -170f);
            ctxRt.offsetMax = new Vector2(114f, -26f);
            var ctxLayout = ctxGo.AddComponent<VerticalLayoutGroup>();
            ctxLayout.spacing = 4f;
            ctxLayout.childControlWidth = false;
            ctxLayout.childControlHeight = false;
            ctxLayout.childForceExpandWidth = false;
            ctxLayout.childForceExpandHeight = false;
            _pauseBtn = Button_(ctxGo.transform, "pause", "Пауза", () =>
            {
                if (CtxFactory() != null) ProductionTasks.TogglePause(CtxFactory());
            }, 11);
            ((RectTransform)_pauseBtn.transform).sizeDelta = new Vector2(66f, 30f);
            _pauseBtn.gameObject.AddComponent<LayoutElement>().minHeight = 30f;
            _cycleBtn = Button_(ctxGo.transform, "cycle", "Цикл", () =>
            {
                if (CtxFactory() != null) ProductionTasks.ToggleCycle(CtxFactory());
            }, 11);
            ((RectTransform)_cycleBtn.transform).sizeDelta = new Vector2(66f, 30f);
            _cycleBtn.gameObject.AddComponent<LayoutElement>().minHeight = 30f;
            _tplBtn = Button_(ctxGo.transform, "tpl", "Шаблон+", () =>
            {
                var controller = CommanderModeController.Instance;
                // На вкладке «Ш» приоритет — набор выделенных зданий, затем очереди.
                var buildingSet = new List<(string key, float value, Vector3 pos)>();
                if (controller != null)
                    foreach (Unit unit in controller.Selection.Selected)
                        if (unit is Building && unit.definition != null)
                        {
                            Vector3 p = unit.transform.position;
                            buildingSet.Add((unit.definition.jsonKey, unit.definition.value, p));
                        }
                OrderTemplates.Template tpl = null;
                if (buildingSet.Count > 0)
                    tpl = OrderTemplates.CaptureBuildings(buildingSet);
                else if (CtxFactory() != null)
                    tpl = OrderTemplates.Capture(CtxFactory());
                else if (controller != null)
                {
                    // Очередь площадок инженера — с относительными позициями площадок.
                    var set = new List<(string key, float value, Vector3 pos)>();
                    foreach (BuildTasks.Task t in BuildTasks.All)
                        if (t.Definition != null)
                        {
                            Vector3 lp = t.Site.ToLocalPosition();
                            set.Add((t.Definition.jsonKey, t.Definition.value, lp));
                        }
                    tpl = set.Count > 0 ? OrderTemplates.CaptureBuildings(set) : null;
                }
                CommanderHud.Notice(tpl != null
                    ? $"Шаблон сохранён: {tpl.name}"
                    : "Нечего сохранять (набор/очередь пусты)");
            }, 11);
            ((RectTransform)_tplBtn.transform).sizeDelta = new Vector2(66f, 30f);
            _tplBtn.gameObject.AddComponent<LayoutElement>().minHeight = 30f;
            ctxGo.SetActive(false);
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: tabs step '" + step + "' — " + e.GetType().Name + ": " + e.Message);
                throw;
            }
        }

        /// <summary>Фабрика контекстных кнопок: депо или аэродром текущего выделения.</summary>
        private object CtxFactory() => _ctxDepot != null ? (object)_ctxDepot : (object)_ctxAirbase;

        private void SwitchTier(int tier)
        {
            if (_prodTier == tier)
                return;
            _prodTier = tier;
            _contextHash = -1; // форсируем перестройку каталога под новый пул
        }


        private void SwitchTab(int tab)
        {
            if (_activeTab == tab)
                return;
            _activeTab = tab;
            _contextHash = -1; // форсируем перестройку контента под новую вкладку
            // Ручной выбор вкладки действует, пока не сменится выделение.
            var controller = CommanderModeController.Instance;
            if (controller != null)
                _manualTabForSel = SelectionHashOf(controller);
        }

        private static int SelectionHashOf(CommanderModeController controller)
        {
            int h = 17;
            foreach (Unit unit in controller.Selection.Selected)
                h = h * 31 + (unit != null ? unit.GetInstanceID() : 0);
            Airbase ab = controller.Selection.SelectedAirbase;
            if (ab != null)
                h = h * 31 + ab.GetInstanceID();
            return h;
        }

        // ---------- обновление ----------

        private void RefreshTopBar()
        {
            var controller = CommanderModeController.Instance;
            bool active = controller != null && controller.IsActive;
            if (_canvas != null)
                _canvas.enabled = active;
            if (!active)
                return;
            if (!_loggedActiveOnce)
            {
                _loggedActiveOnce = true;
                Log.Info($"SupcomUi: активен — canvas={_canvas != null}, funds={_fundsText != null}, income={_incomeText != null}, " +
                         $"extr={_extractorsText != null}, ctx={_strip1 != null}, order={_canvas.sortingOrder}, детей={_canvas.transform.childCount}");
            }

            if (_fundsText == null || _incomeText == null || _extractorsText == null)
                return; // верхняя панель не собралась — остальное живёт
            if (!GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            _fundsText.text = $"{hq.factionFunds:F0}";
            float rate = EconomyService.Running ? EconomyService.IncomePerMinute(hq) : 0f;
            _incomeText.text = $"+{rate:F0}/мин";
            _incomeText.color = rate > 0f ? TextMain : WarnRed;
            if (EconomyService.Running)
                _extractorsText.text = $"экстракторы {EconomyService.OwnedExtractors(hq)}/{EconomyService.TotalExtractors} · T1-3";
        }

        /// <summary>Вкладка «Список»: плитки выделения по типам (15 в ряд, 3 ряда),
        /// заголовок «Выбрано» сбоку; клик по плитке — подвыделение типа.</summary>
        /// <summary>Вкладка «Список» (DESIGN 2.10): верхняя лента — выделенные объекты
        /// плитками по типам, группы разделены промежутками (инженеры → наземка → флот →
        /// авиация → заводы → здания); клик по плитке — подвыделение типа.
        /// Нижняя лента — строка заказа (задел под фабрики/инженеров, этап B).</summary>
        private void FillRosterTab(CommanderModeController controller, List<Unit> selected)
        {
            foreach (var row in _rosterRows)
                if (row.btn != null)
                    Destroy(row.btn.gameObject);
            _rosterRows.Clear();
            ClearChildren(_strip2Content);

            var groups = new Dictionary<UnitDefinition, List<Unit>>();
            foreach (Unit unit in selected)
                if (unit != null && unit.definition != null)
                {
                    if (!groups.TryGetValue(unit.definition, out List<Unit> list))
                        groups[unit.definition] = list = new List<Unit>();
                    list.Add(unit);
                }

            int Category(Unit u) =>
                GameApi.IsEngineer(u) ? 0 :
                u is GroundVehicle ? 1 :
                u is Ship ? 2 :
                u is Aircraft ? 3 :
                u is VehicleDepot ? 4 : 5;

            var ordered = new List<(UnitDefinition def, List<Unit> units, int cat)>();
            foreach (var kv in groups)
                ordered.Add((kv.Key, kv.Value, Category(kv.Value[0])));
            ordered.Sort((a, b) => a.cat.CompareTo(b.cat));

            int lastCat = -1;
            foreach (var (def, units, cat) in ordered)
            {
                if (lastCat != -1 && cat != lastCat)
                    AddRosterGap(_strip1Content); // визуальный промежуток между категориями
                lastCat = cat;

                var go = new GameObject("tile", typeof(RectTransform), typeof(Image), typeof(Button));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_strip1Content, false);
                rt.sizeDelta = new Vector2(60f, 60f);
                go.GetComponent<Image>().color = PanelBg2;
                var btn = go.GetComponent<Button>();
                UnitDefinition d = def;
                btn.onClick.AddListener(() => SelectionService.SelectTypeStatic(controller, d));

                var icon = Icon(go.transform, def.friendlyIcon, new Vector2(34f, 34f));
                ((RectTransform)icon.transform).anchorMin = ((RectTransform)icon.transform).anchorMax =
                    new Vector2(0.5f, 1f);
                ((RectTransform)icon.transform).anchoredPosition = new Vector2(0f, -20f);

                var count = Label(go.transform, "count", "x" + units.Count, 12, Accent, TextAnchor.LowerCenter);
                var rtC = (RectTransform)count.transform;
                rtC.anchorMin = new Vector2(0f, 0f);
                rtC.anchorMax = new Vector2(1f, 0.3f);
                rtC.offsetMin = rtC.offsetMax = Vector2.zero;
                count.raycastTarget = false;

                _rosterRows.Add((count, icon, btn));
            }

            _contextTitle.text = "Выбрано: " + selected.Count;
            _strip2.gameObject.SetActive(false); // строка заказа — с фабриками (этап B)
        }

        /// <summary>Промежуток между категориями выделения (визуальное разделение, DESIGN 2.10).</summary>
        private static void AddRosterGap(Transform content)
        {
            var gap = new GameObject("gap", typeof(RectTransform));
            var rt = (RectTransform)gap.transform;
            rt.SetParent(content, false);
            rt.sizeDelta = new Vector2(14f, 60f);
            gap.AddComponent<LayoutElement>().minWidth = 14f;
        }

        private void RefreshSelectionInfo()
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;
            var selected = controller.Selection.Selected;

            // Выделенный НЕДОСТРОЙ: как объект в SupCom — имя, прогресс, строители.
            if (selected.Count == 0 && BuildTasks.SelectedTask != null)
            {
                BuildTasks.Task t = BuildTasks.SelectedTask;
                if (t.Definition == null)
                    return;
                _infoTitle.text = t.Definition.unitName;
                _infoClass.text = "недострой · " + t.Paid.ToString("F0") + "/" +
                                  t.Cost.ToString("F0") + " · строителей " + t.Builders.Count;
                UiKit.SetProgress(_infoHpFill, t.Progress01);
                _infoHpTxt.text = t.Progress01.ToString("P0");
                _infoFuelTrack.gameObject.SetActive(false);
                _infoFuelTxt.gameObject.SetActive(false);
                _infoMove.text = t.ZoneBlocked ? "зона не расчищена" : "идёт стройка";
                _infoState.text = "ПКМ инженерами — достройка";
                _infoOrder[0].text = "";
                _infoOrder[1].text = "";
                _infoOrder[2].text = "";
                _infoWeaponsHeader.gameObject.SetActive(false);
                for (int i = 0; i < _infoWeapons.Length; i++)
                    _infoWeapons[i].gameObject.SetActive(false);
                return;
            }

            Unit single = selected.Count == 1 ? selected[0] : null;
            bool isSingle = single != null && !single.disabled;
            bool isAir = isSingle && single is Aircraft;
            int weapons = isSingle && single.weaponStations != null
                ? Mathf.Min(single.weaponStations.Count, _infoWeapons.Length)
                : 0;

            int hash = 17;
            foreach (Unit unit in selected)
                hash = hash * 31 + (unit != null ? unit.GetInstanceID() : 0);
            hash = hash * 31 + ((isSingle ? 1 : 0) + (isAir ? 2 : 0) + weapons * 4);
            if (hash != _infoHash)
            {
                _infoHash = hash;
                _infoFuelTrack.gameObject.SetActive(isAir);
                _infoFuelTxt.gameObject.SetActive(isAir);
                _infoWeaponsHeader.gameObject.SetActive(weapons > 0);
                for (int i = 0; i < _infoWeapons.Length; i++)
                    _infoWeapons[i].gameObject.SetActive(i < weapons);
            }

            if (isSingle)
                RefreshSingleUnit(single);
            else
                RefreshMultiUnit(selected);
        }

        private void RefreshSingleUnit(Unit u)
        {
            string defName = u.definition != null ? u.definition.unitName : "";
            _infoTitle.text = !string.IsNullOrEmpty(u.unitName) ? u.unitName : defName;
            _infoClass.text = (defName.Length > 0 ? defName + " · " : "") + ClassOf(u);

            float hp = GameApi.HpFraction(u);
            UiKit.SetProgress(_infoHpFill, hp);
            _infoHpTxt.text = hp >= 0.995f ? "цел" : $"{hp:P0}";

            float speedKmh = Mathf.Abs(u.speed) * 3.6f;
            if (u is Aircraft a)
            {
                float fuel = a.GetFuelLevel();
                UiKit.SetProgress(_infoFuelFill, fuel);
                _infoFuelTxt.text = $"топливо {fuel:P0}";
                _infoMove.text = $"{speedKmh:F0} км/ч · {a.radarAlt:F0} м";
                _infoState.text = a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null
                    ? a.pilots[0].GetCurrentState()
                    : "нет пилота";
            }
            else
            {
                _infoMove.text = $"{speedKmh:F0} км/ч";
                _infoState.text = u is Ship s && s.TryGetComponent(out ShipAI sai)
                    ? sai.state.ToString()
                    : "";
            }

            _infoOrder[0].text = "Приказ: " + PlanSummary(u);
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";

            int w = 0;
            if (u.weaponStations != null)
                foreach (WeaponStation ws in u.weaponStations)
                {
                    if (w >= _infoWeapons.Length)
                        break;
                    if (ws == null || ws.WeaponInfo == null)
                        continue;
                    string name = !string.IsNullOrEmpty(ws.WeaponInfo.shortName)
                        ? ws.WeaponInfo.shortName
                        : ws.WeaponInfo.weaponName;
                    string status = ws.Reloading ? " · перезарядка" : (ws.Ready() ? "" : " · пусто");
                    _infoWeapons[w++].text = $"{name}: {ws.Ammo}/{ws.FullAmmo}{status}";
                }
        }

        private void RefreshMultiUnit(List<Unit> selected)
        {
            _infoTitle.text = $"Выбрано: {selected.Count}";
            int air = 0, ground = 0, naval = 0;
            float hpSum = 0f, ammoSum = 0f;
            int ammoN = 0, n = 0;
            foreach (Unit unit in selected)
            {
                if (unit == null)
                    continue;
                n++;
                if (unit is Aircraft) air++;
                else if (unit is Ship) naval++;
                else ground++;
                hpSum += GameApi.HpFraction(unit);
                if (unit.weaponStations != null && unit.weaponStations.Count > 0)
                {
                    ammoSum += unit.GetAmmoLevel();
                    ammoN++;
                }
            }
            _infoClass.text = $"авиация {air} · наземка {ground} · флот {naval}";
            float hp = n > 0 ? hpSum / n : 1f;
            UiKit.SetProgress(_infoHpFill, hp);
            _infoHpTxt.text = hp >= 0.995f ? "целы" : $"{hp:P0}";
            _infoMove.text = ammoN > 0 ? $"боезапас {ammoSum / ammoN:P0}" : "";
            _infoState.text = "";
            _infoOrder[0].text = "";
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";
        }

        private static string ClassOf(Unit u)
        {
            if (u is Aircraft) return "авиация";
            if (u is Ship) return "флот";
            if (u is Building) return "здание";
            if (u.definition is VehicleDefinition vd) return vd.vehicleType.ToString();
            return "";
        }

        /// <summary>Краткое описание активного приказа мода: сегменты плана авиации / цепочка наземки.</summary>
        private static string PlanSummary(Unit u)
        {
            if (u is Aircraft && AircraftOrders.TryGetPlan(u, out IReadOnlyList<AircraftOrders.OrderSegment> plan))
            {
                if (plan.Count == 0)
                    return "нет";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < plan.Count && i < 3; i++)
                {
                    if (i > 0)
                        sb.Append(" → ");
                    AircraftOrders.OrderSegment seg = plan[i];
                    sb.Append(seg.Mode switch
                    {
                        AirOrderMode.Patrol => "Патруль",
                        AirOrderMode.AttackMove => "Атака-движ.",
                        AirOrderMode.Attack => "Атака",
                        _ => "Транзит"
                    });
                    if (seg.Mode != AirOrderMode.Attack && seg.Points.Count > 1)
                        sb.Append(" ×").Append(seg.Points.Count);
                }
                return sb.ToString();
            }
            if (GroundOrders.TryGetQueue(u, out IReadOnlyList<GlobalPosition> queue, out int idx))
                return $"цепочка, осталось {Mathf.Max(0, queue.Count - idx)} тчк";
            return "нет";
        }

        private void RefreshCommands()
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;

            // Кнопки строятся один раз; обновляем активность и подпись.
            if (_cmdButtons.Count == 0)
                BuildCommandButtons(controller);

            string pending = OrderModes.Pending != OrderMode.None && OrderModes.Pending != OrderMode.Move
                ? OrderModes.Label(OrderModes.Pending)
                : null;
            if (BuildTasks.Pending != null)
                _modeHint.text = $"{BuildTasks.Pending.unitName}: ЛКМ — площадка (Shift — серия) · ПКМ/Esc — отмена";
            else
                _modeHint.text = pending != null
                    ? $"{pending}: ПКМ — вейпоинт (Shift — серия) · Esc — движение"
                    : "ПКМ — приказ/вейпоинт (Shift — серия) · ЛКМ — выделение/перетаскивание точек";

            // Индивидуальные команды: профили высоты — только когда в выделении есть авиация.
            bool hasAir = false;
            foreach (Unit unit in controller.Selection.Selected)
                if (unit is Aircraft)
                {
                    hasAir = true;
                    break;
                }
            foreach (var (btn, img, _) in _cmdButtons)
            {
                if (btn.name == "cmd_low" || btn.name == "cmd_med" || btn.name == "cmd_high")
                    btn.gameObject.SetActive(hasAir);
            }

            foreach (var (btn, img, _) in _cmdButtons)
                img.color = BtnBg;
            HighlightCommand("cmd_move", OrderModes.Pending == OrderMode.Move || OrderModes.Pending == OrderMode.None);
            HighlightCommand("cmd_amove", OrderModes.Pending == OrderMode.AttackMove);
            HighlightCommand("cmd_patrol", OrderModes.Pending == OrderMode.Patrol);
            HighlightCommand("cmd_attack", OrderModes.Pending == OrderMode.Attack);
        }

        private void HighlightCommand(string name, bool on)
        {
            foreach (var (btn, img, _) in _cmdButtons)
                if (btn.name == name && on)
                    img.color = BtnActive;
        }

        private void BuildCommandButtons(CommanderModeController controller)
        {
            Transform grid = null;
            foreach (Transform child in _canvas.transform)
                if (child.name == "CommandCard")
                    foreach (Transform c2 in child)
                        if (c2.name == "grid")
                            grid = c2;
            if (grid == null)
                return;

            var defs = new (string name, string label, System.Action a)[]
            {
                ("cmd_move", "Движение (M)", () => OrderModes.Pending = OrderMode.Move),
                ("cmd_amove", "Атака-движ (A)", () => OrderModes.Pending = OrderMode.AttackMove),
                ("cmd_patrol", "Патруль (U)", () => OrderModes.Pending = OrderMode.Patrol),
                ("cmd_attack", "Атака (T)", () => OrderModes.Pending = OrderMode.Attack),
                ("cmd_stop", "Стоп (S)", () => controller.MoveOrders.CancelAll(controller.Selection.Selected)),
                ("cmd_hold", "Огонь: удерж (H)", () =>
                {
                    foreach (Unit unit in controller.Selection.Selected)
                        StanceService.Toggle(unit);
                }),
                ("cmd_low", "Низко 150м", () => AircraftOrders.ProfileIndex = 0),
                ("cmd_med", "Средне 800м", () => AircraftOrders.ProfileIndex = 1),
                ("cmd_high", "Высоко 2500м", () => AircraftOrders.ProfileIndex = 2)
            };
            foreach (var (name, label, action) in defs)
            {
                Button b = Button_(grid, name, label, action, 12);
                _cmdButtons.Add((b, b.GetComponent<Image>(), action));
            }
        }

        private void RefreshTabs()
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            var selected = controller.Selection.Selected;
            Airbase airbase = controller.Selection.SelectedAirbase;

            VehicleDepot depot = null;
            Unit extractor = null;
            bool hasEngineer = false;
            int selHash = 17;
            foreach (Unit unit in selected)
            {
                selHash = selHash * 31 + (unit != null ? unit.GetInstanceID() : 0);
                if (unit is VehicleDepot d)
                    depot = d;
                else if (EconomyService.IsExtractor(unit) && extractor == null)
                    extractor = unit;
                else if (!hasEngineer && GameApi.IsEngineer(unit))
                    hasEngineer = true;
            }
            if (airbase != null)
                selHash = selHash * 31 + airbase.GetInstanceID();

            // Выделенный недострой открывает свою карточку на «Производстве».
            if (BuildTasks.SelectedTask != null && selected.Count == 0 && depot == null && airbase == null)
                _activeTab = TabProduction;

            // Автопереключение вкладки под выделение; ручной клик игрока уважаем,
            // пока выделение не сменилось (_manualTabForSel).
            if (_manualTabForSel != selHash)
            {
                if (depot != null || airbase != null || hasEngineer)
                    _activeTab = TabProduction;
                else if (extractor != null && EconomyService.Running)
                    _activeTab = TabUpgrade;
                else if (selected.Count > 0 && _activeTab != TabRoster)
                    _activeTab = TabRoster;
            }

            for (int i = 0; i < _tabButtons.Length; i++)
                if (_tabButtons[i].img != null)
                    _tabButtons[i].img.color = i == _activeTab ? BtnActive : BtnBg;
            if (_tierRoot != null)
                _tierRoot.gameObject.SetActive(_activeTab == TabProduction);
            for (int i = 0; i < _tierButtons.Length; i++)
                if (_tierButtons[i].img != null)
                    _tierButtons[i].img.color = i == _prodTier ? BtnActive : BtnBg;

            // Деньги квантуем до сотен: перестройка только при реальных изменениях.
            int fundsKey = (int)(hq.factionFunds / 100f);
            int hash = 17 * 31 + _activeTab;
            hash = hash * 31 + selHash;
            if (_activeTab == TabProduction)
                hash = hash * 31 + fundsKey + _prodTier * 7 + (depot != null ? 1 : 0) +
                       (airbase != null ? 2 : 0) + (hasEngineer ? 4 : 0) +
                       OrderTemplates.Count * 13;
            else if (_activeTab == TabUpgrade)
            {
                hash = hash * 31 + (extractor != null ? EconomyService.TierOf(extractor) : 0);
                hash = hash * 31 + (EconomyService.Running ? 1 : 0);
                hash = hash * 31 + fundsKey;
            }
            // Нижняя строка видима ВСЕГДА (без прыжков высоты): пустая — просто пустая лента.
            if (_tabPanel != null)
            {
                var prt = (RectTransform)_tabPanel.transform;
                prt.offsetMax = new Vector2(-12f, 196f);
            }
            if (_strip2Nav != null)
                _strip2Nav.SetActive(true);
            _strip2.gameObject.SetActive(true);
            int queueKey = 17 * 31 + BuildTasks.Version + GroundOrders.Version +
                          AircraftOrders.Version;
            if (hasEngineer)
                foreach (BuildTasks.Task t in BuildTasks.All)
                    queueKey = queueKey * 31 + (int)(t.Progress01 * 20f) + (t.ZoneBlocked ? 3 : 0);
            if (depot != null)
                queueKey = queueKey * 31 + ProductionTasks.KeyFor(depot) + 7;
            if (airbase != null)
                queueKey = queueKey * 31 + ProductionTasks.KeyFor(airbase) + 11;
            bool queueShown = _activeTab == TabProduction || _activeTab == TabTemplates;
            if (queueShown && queueKey != _queueHash)
            {
                _queueHash = queueKey;
                if (hasEngineer)
                    FillBuildQueue(controller);
                else
                    FillFactoryQueue(depot != null ? (object)depot : (object)airbase);
            }
            if (!queueShown && _queueHash != 0)
            {
                _queueHash = 0;
                ClearChildren(_strip2Content);
            }

            // Контекстные кнопки: на «Производстве» у фабрики — Пауза/Цикл/Шаблон+,
            // на «Ш» — только Шаблон+ (сохранить набор).
            _ctxDepot = depot;
            _ctxAirbase = airbase;
            Transform ctx = _tabPanel != null ? _tabPanel.Find("factoryCtx") : null;
            if (ctx != null)
            {
                bool ctxOnFactory = _activeTab == TabProduction && CtxFactory() != null;
                ctx.gameObject.SetActive(ctxOnFactory || _activeTab == TabTemplates);
                if (_pauseBtn != null)
                    _pauseBtn.gameObject.SetActive(ctxOnFactory);
                if (_cycleBtn != null)
                    _cycleBtn.gameObject.SetActive(ctxOnFactory);
                Image pi = _pauseBtn != null ? _pauseBtn.GetComponent<Image>() : null;
                if (pi != null)
                    pi.color = CtxFactory() != null && ProductionTasks.IsPaused(CtxFactory()) ? BtnActive : BtnBg;
                Image ci = _cycleBtn != null ? _cycleBtn.GetComponent<Image>() : null;
                if (ci != null)
                    ci.color = CtxFactory() != null && ProductionTasks.IsCycling(CtxFactory()) ? BtnActive : BtnBg;
            }

            if (hash == _contextHash)
                return;
            _contextHash = hash;
            ClearChildren(_strip1Content);
            _strip1Content.anchoredPosition = Vector2.zero;
            _strip2Content.anchoredPosition = Vector2.zero;

            switch (_activeTab)
            {
                case TabProduction:
                    if (BuildTasks.SelectedTask != null && selected.Count == 0 &&
                        depot == null && airbase == null)
                        FillUnfinishedTab(BuildTasks.SelectedTask);
                    else
                        FillProductionTab(hq, depot, airbase, hasEngineer);
                    break;
                case TabTemplates:
                    FillTemplatesTab(controller, depot, airbase, hasEngineer, selected);
                    break;
                case TabUpgrade:
                    FillUpgradeTab(hq, extractor, EconomyService.Running);
                    break;
                default:
                    FillRosterTab(controller, selected);
                    break;
            }
        }

        /// <summary>Вкладка «Производство»: инженер → каталог зданий (стройка где угодно),
        /// иначе каталог депо (по типам) или аэродрома.
        /// Группы — боковыми заголовками: слева колонка заголовка, справа поток позиций.</summary>
        /// <summary>Вкладка «Производство» (DESIGN 2.10): строка заказа — ЛЕНТА позиций.
        /// Инженер в выделении → здания (стройка где угодно), иначе депо/аэродром.
        /// Нижняя лента — задел под очередь фабрики (этап B).</summary>
        /// <summary>Нижняя строка «Производства» (инженер): очередь активных строек —
        /// плитка «иконка здания + прогресс», клик — камера к площадке.</summary>
        private void FillBuildQueue(CommanderModeController controller)
        {
            ClearChildren(_strip2Content);
            // Стак: подряд идущие задания одного здания — одна плитка с числом.
            BuildTasks.Task runDef = null;   // образец повтора
            int runCount = 0;
            bool first = true;
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                if (t.Definition == runDef?.Definition)
                {
                    runCount++;
                    continue;
                }
                if (runDef != null)
                {
                    AddQueueTile(_strip2Content, runDef, first, runCount, controller);
                    first = false;
                }
                runDef = t;
                runCount = 1;
            }
            if (runDef != null)
                AddQueueTile(_strip2Content, runDef, first, runCount, controller);
        }

        /// <summary>Плитка очереди стройки: иконка + бейдж ×N, у головной — прогресс-бар
        /// (зона не расчищена — полоса красная); клик — камера к площадке.</summary>
        private void AddQueueTile(Transform content, BuildTasks.Task t, bool head, int count,
            CommanderModeController controller)
        {
            BuildTasks.Task task = t;
            GameObject go = Tile60(content, t.Definition != null ? t.Definition.friendlyIcon : null,
                () =>
                {
                    Vector3 site = task.Site.ToLocalPosition();
                    controller.CameraState?.CenterOn(site);
                },
                count > 1 ? "×" + count : null,
                head ? t.Progress01 : (float?)null,
                head && t.ZoneBlocked ? new Color(1f, 0.45f, 0.3f, 0.95f) : (Color?)null);
        }

        /// <summary>ЕДИНЫЙ строитель плитки 60x60 (консолидация v0.48): иконка,
        /// опционально бейдж ×N и прогресс-бар; клики ЛКМ/ПКМ через TileClicks.</summary>
        private GameObject Tile60(Transform parent, Sprite icon, System.Action onLeft,
            string badge, float? progress, Color? barColor)
        {
            var go = new GameObject("tile", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(60f, 60f);
            go.GetComponent<Image>().color = PanelBg2;
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 60f;
            le.minHeight = 60f;
            le.preferredWidth = 60f;
            le.preferredHeight = 60f;

            var iconImg = Icon(go.transform, icon, new Vector2(34f, 34f));
            ((RectTransform)iconImg.transform).anchorMin = ((RectTransform)iconImg.transform).anchorMax =
                new Vector2(0.5f, 1f);
            ((RectTransform)iconImg.transform).anchoredPosition = new Vector2(0f, -20f);

            if (!string.IsNullOrEmpty(badge))
            {
                var b = Label(go.transform, "n", badge, 12, Accent, TextAnchor.UpperRight);
                var rtN = (RectTransform)b.transform;
                rtN.anchorMin = new Vector2(0.35f, 0.62f);
                rtN.anchorMax = new Vector2(0.97f, 0.98f);
                rtN.offsetMin = rtN.offsetMax = Vector2.zero;
                b.raycastTarget = false;
            }
            if (progress.HasValue)
                AddTileProgressBar(go.transform, progress.Value, barColor.HasValue && barColor.Value.r > 0.9f);

            var tile = go.AddComponent<TileClicks>();
            tile.Left = onLeft;
            return go;
        }

        /// <summary>Клики плитки: ЛКМ — действие, ПКМ — правое действие (наборы/очереди).</summary>
        private sealed class TileClicks : MonoBehaviour, IPointerDownHandler
        {
            public System.Action Left;
            public System.Action Right;

            public void OnPointerDown(PointerEventData e)
            {
                if (e.button == PointerEventData.InputButton.Left)
                    Left?.Invoke();
                else if (e.button == PointerEventData.InputButton.Right)
                    Right?.Invoke();
            }
        }

        /// <summary>Прогресс-бар в нижней части плитки очереди (заполнение = готовность).</summary>
        private void AddTileProgressBar(Transform tile, float progress, bool blocked)
        {
            var track = new GameObject("pb", typeof(RectTransform), typeof(Image));
            var trt = (RectTransform)track.transform;
            trt.SetParent(tile, false);
            trt.anchorMin = new Vector2(0.06f, 0.03f);
            trt.anchorMax = new Vector2(0.94f, 0.16f);
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            track.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
            track.GetComponent<Image>().raycastTarget = false;

            var fill = new GameObject("f", typeof(RectTransform), typeof(Image));
            var frt = (RectTransform)fill.transform;
            frt.SetParent(trt, false);
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = new Vector2(Mathf.Clamp01(progress), 1f);
            frt.offsetMin = frt.offsetMax = Vector2.zero;
            var fimg = fill.GetComponent<Image>();
            fimg.color = blocked
                ? new Color(1f, 0.45f, 0.3f, 0.95f)
                : new Color(0.35f, 1f, 0.5f, 0.95f);
            fimg.raycastTarget = false;
        }

                /// <summary>Нижняя строка «Производства» (фабрика): очередь заказов — плитка
        /// «иконка + %» у головной позиции; клик по позиции — отмена с возвратом денег.</summary>
        private void FillFactoryQueue(object factory)
        {
            ClearChildren(_strip2Content);
            if (factory == null)
                return;
            // Стак подряд идущих одинаковых позиций (порядок сохраняется).
            var runs = new List<(UnitDefinition def, ProductionTasks.QueueItem head, int count)>();
            foreach (ProductionTasks.QueueItem item in ProductionTasks.All)
            {
                if (item.Factory != factory || item.UnitDef == null)
                    continue;
                if (runs.Count > 0 && runs[runs.Count - 1].def == item.UnitDef)
                {
                    var last = runs[runs.Count - 1];
                    runs[runs.Count - 1] = (last.def, last.head, last.count + 1);
                }
                else
                    runs.Add((item.UnitDef, item, 1));
            }
            bool first = true;
            foreach (var run in runs)
            {
                UnitDefinition def = run.def;
                ProductionTasks.QueueItem head = run.head;
                int count = run.count;
                bool headFirst = first;
                first = false;
                Tile60(_strip2Content, def.friendlyIcon,
                    () => ProductionTasks.Cancel(head),
                    count > 1 ? "×" + count : null,
                    headFirst ? head.Progress01 : (float?)null,
                    null);
            }
        }

        /// <summary>Карточка недостроя (выделен кликом по площадке): прогресс и действия.</summary>
        private void FillUnfinishedTab(BuildTasks.Task t)
        {
            _contextTitle.text = "Недострой: " + (t.Definition != null ? t.Definition.unitName : "?") +
                                 " · " + t.Progress01.ToString("P0");
            BuildTasks.Task task = t;
            AddItemButton(_strip1Content,
                t.Definition != null ? t.Definition.friendlyIcon : null,
                "Снести площадку (без возврата)",
                () =>
                {
                    BuildTasks.Cancel(task);
                    BuildTasks.SelectedTask = null;
                });
        }

        /// <summary>Вкладка «Ш» — шаблоны (DESIGN 2.10): ВЕРХняя строка — сохранённые наборы
        /// (иконка-представитель = самое дорогое в наборе); НИЖняя — текущий заказ/набор.
        /// Клик по шаблону: фабрика → весь набор в конец очереди; инженер → призраки.
        /// «Шаблон+» сохраняет: очередь фабрики / очередь площадок / выделенные здания
        /// (shift+клик набор) с относительными позициями.</summary>
        private void FillTemplatesTab(CommanderModeController controller, VehicleDepot depot,
            Airbase airbase, bool hasEngineer, List<Unit> selected)
        {
            // Низ: приоритет — набор выделенных зданий (режим набора под шаблон).
            var buildingSet = new List<Unit>();
            foreach (Unit unit in selected)
                if (unit is Building)
                    buildingSet.Add(unit);
            bool setMode = buildingSet.Count > 0;
            if (setMode)
            {
                _contextTitle.text = "Шаблон: набор " + buildingSet.Count + " зданий";
                FillBuildingSetRow(buildingSet);
            }
            else if (hasEngineer)
                _contextTitle.text = "Шаблон: очередь стройки инженера";
            else if (depot != null)
                _contextTitle.text = "Шаблон: очередь фабрики";
            else if (airbase != null)
                _contextTitle.text = "Шаблон: очередь аэродрома";
            else
                _contextTitle.text = "Шаблоны";

            // Верх: сохранённые шаблоны.
            foreach (OrderTemplates.Template tpl in OrderTemplates.All)
            {
                OrderTemplates.Template t = tpl;
                Sprite icon = ResolveTemplateIcon(t);
                AddItemButton(_strip1Content, icon, t.name, () =>
                {
                    if (setMode || hasEngineer)
                    {
                        var engineers = new List<Unit>();
                        foreach (Unit unit in controller.Selection.Selected)
                            if (GameApi.IsEngineer(unit))
                                engineers.Add(unit);
                        if (engineers.Count == 0)
                        {
                            CommanderHud.Notice("Выдели инженера для постройки шаблона");
                            return;
                        }
                        OrderTemplates.PendingTemplate = (t, engineers);
                        CommanderHud.Notice(t.name + ": призраки у курсора · ЛКМ — строить");
                    }
                    else if (depot != null || airbase != null)
                    {
                        int n = OrderTemplates.Enqueue(t, depot != null ? (object)depot : (object)airbase);
                        CommanderHud.Notice(t.name + ": в очередь " + n);
                    }
                });
            }
            if (OrderTemplates.Count == 0)
                AddItemButton(_strip1Content, null, "шаблонов нет — собери набор и нажми Шаблон+", null);
        }

        private Sprite ResolveTemplateIcon(OrderTemplates.Template t)
        {
            if (t == null || string.IsNullOrEmpty(t.iconKey) || Encyclopedia.i == null)
                return null;
            if (Encyclopedia.i.buildings != null)
                foreach (BuildingDefinition b in Encyclopedia.i.buildings)
                    if (b != null && b.jsonKey == t.iconKey)
                        return b.friendlyIcon;
            if (Encyclopedia.i.vehicles != null)
                foreach (VehicleDefinition v in Encyclopedia.i.vehicles)
                    if (v != null && v.jsonKey == t.iconKey)
                        return v.friendlyIcon;
            if (Encyclopedia.i.aircraft != null)
                foreach (AircraftDefinition a in Encyclopedia.i.aircraft)
                    if (a != null && a.jsonKey == t.iconKey)
                        return a.friendlyIcon;
            return null;
        }

        /// <summary>Набор выделенных зданий плитками; ПКМ по плитке — убрать из набора.</summary>
        private void FillBuildingSetRow(List<Unit> buildingSet)
        {
            ClearChildren(_strip2Content);
            foreach (Unit unit in buildingSet)
            {
                Unit u = unit;
                Unit su = u;
                Tile60(_strip2Content, u.definition != null ? u.definition.friendlyIcon : null,
                    null, null, null, null)
                    .GetComponent<TileClicks>().Right = () =>
                    {
                        var controller = CommanderModeController.Instance;
                        if (controller != null)
                            controller.Selection.Selected.Remove(su);
                    };
            }
        }

        /// <summary>Вкладка «Производство» (DESIGN 2.10): строка заказа — ЛЕНТА позиций,
        /// полоса тиров сверху фильтрует пул по цене (терцили каталога). Инженер в
        /// выделении → здания (стройка где угодно), иначе депо/аэродром. Нижняя лента —
        /// задел под очередь фабрики (этап B).</summary>
        private void FillProductionTab(FactionHQ hq, VehicleDepot depot, Airbase airbase, bool hasEngineer)
        {
            string tierName = _prodTier == 0 ? "" : " · T" + _prodTier;
            if (hasEngineer && Encyclopedia.i != null && Encyclopedia.i.buildings != null)
            {
                _contextTitle.text = "Стройка (инженер): здание → ЛКМ по земле" + tierName;
                var defs = new List<BuildingDefinition>();
                foreach (BuildingDefinition def in Encyclopedia.i.buildings)
                    if (def != null)
                        defs.Add(def);
                Catalog.ApplyTierFilter(defs, d => d.value, _prodTier);
                foreach (BuildingDefinition def in defs)
                {
                    BuildingDefinition d = def;
                    AddItemButton(_strip1Content, def.friendlyIcon,
                        def.unitName + " — " + def.value.ToString("F0"),
                        () => BuildTasks.Pending = d);
                }
                return; // нижняя строка — очередь стройки (RefreshTabs)
            }

            if (depot != null && PurchaseService.Catalog != null)
            {
                _contextTitle.text = "Фабрика — техника (клик: в очередь)" + tierName;
                var defs = new List<VehicleDefinition>();
                foreach (VehicleDefinition def in PurchaseService.Catalog)
                    if (def != null)
                        defs.Add(def);
                Catalog.ApplyTierFilter(defs, d => d.value, _prodTier);
                foreach (VehicleDefinition def in defs)
                {
                    VehicleDefinition d = def;
                    AddItemButton(_strip1Content, def.friendlyIcon,
                        def.unitName + " — " + def.value.ToString("F0"),
                        () => ProductionTasks.EnqueueGround(depot, d));
                }
                // Персистентные шаблоны: клик — поставить весь набор в очередь.
                foreach (OrderTemplates.Template tpl in OrderTemplates.All)
                {
                    OrderTemplates.Template t = tpl;
                    int units = 0;
                    foreach (OrderTemplates.Item it in t.items)
                        units += it.count;
                    AddItemButton(_strip1Content, null,
                        t.name + " ×" + units,
                        () =>
                        {
                            int n = OrderTemplates.Enqueue(t, depot);
                            CommanderHud.Notice($"{t.name}: в очередь {n}");
                        });
                }
                return; // нижняя строка — очередь фабрики (RefreshTabs)
            }

            if (airbase != null && Encyclopedia.i != null && Encyclopedia.i.aircraft != null)
            {
                _contextTitle.text = "Аэродром — авиация (клик: в очередь)" + tierName;
                var defs = new List<AircraftDefinition>();
                foreach (AircraftDefinition def in Encyclopedia.i.aircraft)
                    if (def != null)
                        defs.Add(def);
                Catalog.ApplyTierFilter(defs, d => d.value, _prodTier);
                foreach (AircraftDefinition def in defs)
                {
                    AircraftDefinition d = def;
                    AddItemButton(_strip1Content, def.friendlyIcon,
                        def.unitName + " — " + def.value.ToString("F0"),
                        () => ProductionTasks.EnqueueAir(airbase, d));
                }
                // Шаблоны применимы и к аэродрому (jsonKey авиации).
                foreach (OrderTemplates.Template tpl in OrderTemplates.All)
                {
                    OrderTemplates.Template t = tpl;
                    int units = 0;
                    foreach (OrderTemplates.Item it in t.items)
                        units += it.count;
                    AddItemButton(_strip1Content, null,
                        t.name + " ×" + units,
                        () =>
                        {
                            int n = OrderTemplates.Enqueue(t, airbase);
                            CommanderHud.Notice($"{t.name}: в очередь {n}");
                        });
                }
                return; // нижняя строка — очередь аэродрома (RefreshTabs)
            }

            _contextTitle.text = "Производство";
            AddItemButton(_strip1Content, null, "выдели депо, аэродром или инженера", null);
            _strip2.gameObject.SetActive(false);
        }

        /// <summary>Фильтр пула по тирам (спека 2.10): каталог сортируется по цене и режется
        /// на три равные части; 0 — без фильтра. Список правится на месте.</summary>


        /// <summary>Вкладка «Апгрейд»: тир экстрактора и апгрейд за деньги фракции.</summary>
        /// <summary>Вкладка «Апгрейд»: тир экстрактора и кнопка улучшения (лента).</summary>
        private void FillUpgradeTab(FactionHQ hq, Unit extractor, bool ecoRunning)
        {
            if (extractor == null || !ecoRunning)
            {
                _contextTitle.text = "Апгрейд";
                AddItemButton(_strip1Content, null, "выдели свой экстрактор", null);
                _strip2.gameObject.SetActive(false);
                return;
            }
            int tier = EconomyService.TierOf(extractor);
            _contextTitle.text = "Экстрактор T" + tier + " · +" +
                                 EconomyService.IncomeRate(tier).ToString("F0") + "/мин";
            float cost = EconomyService.NextUpgradeCost(extractor);
            if (cost >= 0f)
            {
                bool ok = hq.factionFunds >= cost;
                AddItemButton(_strip1Content, null,
                    "Улучшить до T" + (tier + 1) + " — " + cost.ToString("F0"),
                    ok ? () => EconomyService.TryUpgrade(hq, extractor) : null);
            }
            else
                AddItemButton(_strip1Content, null, "максимальный тир", null);
            _strip2.gameObject.SetActive(false);
        }

        /// <summary>Строка нижней панели: слева колонка заголовка (150 px), справа —
        /// контейнер для позиций. Заголовок сбоку экономит вертикальное пространство.</summary>
        /// <summary>Позиция каталога: кнопка фиксированной ширины с иконкой, названием и ценой.</summary>
        private void AddItemButton(Transform parent, Sprite sprite, string label, System.Action onClick)
        {
            if (sprite != null)
            {
                AddIconTile(parent, sprite, onClick);
                return;
            }
            AddTextPill(parent, label, onClick);
        }

        /// <summary>Плитка заказа: ТОЛЬКО иконка (SupCom) — подписи в каталоге не нужны.</summary>
        private void AddIconTile(Transform parent, Sprite sprite, System.Action onClick)
        {
            var go = new GameObject("item", typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(60f, 60f);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 60f;
            le.preferredWidth = 60f;
            le.minHeight = 60f;
            le.preferredHeight = 60f;
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.preserveAspect = true;
            var btn = go.GetComponent<Button>();
            if (onClick != null)
                btn.onClick.AddListener(() => onClick());
        }

        /// <summary>Текстовая пилюля — для шаблонов и подсказок (без иконки).</summary>
        private void AddTextPill(Transform parent, string label, System.Action onClick)
        {
            var go = new GameObject("item", typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(178f, 36f);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 178f;
            le.preferredWidth = 178f;
            le.minHeight = 36f;
            le.preferredHeight = 36f;
            go.GetComponent<Image>().color = onClick != null
                ? UiKit.BtnBg
                : new Color(0.08f, 0.10f, 0.12f, 0.9f);
            var btn = go.GetComponent<Button>();
            if (onClick != null)
                btn.onClick.AddListener(() => onClick());

            var txt = Label(go.transform, "lbl", label, 13,
                onClick != null ? TextMain : TextDim, TextAnchor.MiddleLeft);
            var rtT = (RectTransform)txt.transform;
            rtT.anchorMin = new Vector2(0.04f, 0f);
            rtT.anchorMax = new Vector2(1f, 1f);
            rtT.offsetMin = rtT.offsetMax = Vector2.zero;
            txt.raycastTarget = false;
        }

        private static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                Destroy(t.GetChild(i).gameObject);
        }
    }
}
