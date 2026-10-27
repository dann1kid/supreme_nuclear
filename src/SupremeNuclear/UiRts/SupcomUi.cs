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
        private static readonly Color WarnYellow = new Color(0.85f, 0.75f, 0.25f, 1f);

        private Canvas _canvas;
        private Font _font;

        /// <summary>Канвас для внешних панелей (миникарта); пересобирается по сценам.</summary>
        public Canvas Canvas => _canvas;
        public Font UiFont => _font;

        // Верхняя панель.
        private Text _fundsText;
        private Text _incomeText;
        private Text _extractorsText;

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
        private int _inspHash = -1;   // кэш макета панели осмотра (юнит live/stale, ракета)
        // Сводка вооружения массового выделения: скретч-буфер, чистится на каждом refresh.
        private readonly WeaponTallyAccumulator _groupWeapons = new WeaponTallyAccumulator();

        // Командная карта.
        private RectTransform _commandPanel;
        private readonly List<(Button btn, Image img, System.Action action, Text label)> _cmdButtons = new();
        private Text _modeHint;
        /// <summary>Заголовок секции приказов (сдвигается вверх, когда подсказка
        /// режима скрыта — без пустой строки).</summary>
        private Text _ordersHdr;
        /// <summary>Сетка команд (верх двигается под подсказку/заголовок).</summary>
        private RectTransform _cmdGrid;
        /// <summary>Число рядов/наличие гейтов последней сборки кнопок — для раскладки.</summary>
        private int _cmdRows;
        private bool _cmdGates;
        /// <summary>Полоса гейтов вооружения (внизу карточки, под сеткой команд):
        /// кнопка на каждый класс, присутствующий в выделении.</summary>
        private RectTransform _gateStrip;
        /// <summary>Заголовок секции гейтов (вкл/выкл вместе с полосой).</summary>
        private Text _gateHdr;
        /// <summary>Порядок классов на полосе гейтов — от частых к редким.</summary>
        private static readonly WeaponClass[] GateOrder =
        {
            WeaponClass.Bomb, WeaponClass.MissileAA, WeaponClass.MissileAS,
            WeaponClass.Gun, WeaponClass.Nuclear,
        };

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

        // Hover-инфоокно плиток (каталог/очереди/ростер): карточка определения юнита
        // у курсора. Плитки регистрируются в Tile, опрос — UpdateTileHover
        // (без uGUI-событий: игровой input-модуль их не доставляет, правило №18).
        private RectTransform _tipRoot;
        private Text _tipTitle;
        private Text _tipClass;
        private Text _tipDesc;
        private UnitDefinition _tipDef;       // что показано (кэш текста)
        private UnitDefinition _hoverDef;     // что под курсором сейчас
        private float _hoverSince;
        private readonly List<(RectTransform tile, RectTransform viewport, UnitDefinition def)> _tipTiles = new();
        private const float TipDelaySec = 0.35f; // задержка появления, с

        /// <summary>Лимиты однострочных полей инфопанели (ширина ~410 px): заголовок —
        /// шрифт 16, строки — 12; длиннее — «…» (Util.FitLine). Перенос запрещён
        /// (Label singleLine), иначе текст вылезает за подложку панели.</summary>
        private const int FitTitleChars = 36;
        private const int FitBodyChars = 50;

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
            // (экономика сверху остаётся всегда). Осмотр чужой цели — тоже контекст.
            var ctxController = CommanderModeController.Instance;
            bool contextVisible = ctxController != null && ctxController.IsActive &&
                (ctxController.Selection.Selected.Count > 0 ||
                 ctxController.Selection.SelectedAirbase != null ||
                 ctxController.Selection.Inspected != null ||
                 BuildTasks.Pending != null ||
                 OrderTemplates.PendingTemplate.template != null ||
                 BuildTasks.SelectedTask != null);
            if (_infoPanel != null && _infoPanel.gameObject.activeSelf != contextVisible)
                _infoPanel.gameObject.SetActive(contextVisible);
            if (_commandPanel != null && _commandPanel.gameObject.activeSelf != contextVisible)
                _commandPanel.gameObject.SetActive(contextVisible);
            if (_tabPanel != null && _tabPanel.gameObject.activeSelf != contextVisible)
                _tabPanel.gameObject.SetActive(contextVisible);

            // Каждый кадр (не по таймеру): тултип бегает за мышью и появляется
            // без задержки опроса; сами панели обновляются реже ниже.
            if (ctxController != null && ctxController.IsActive)
                UpdateTileHover();
            else
                HideTip();

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
            _cmdContextKey = -1; // кнопки пересоздадутся под текущий контекст выделения
            _contextHash = -1;
            _infoHash = -1;
            _inspHash = -1;
            _loggedActiveOnce = false;
            _fundsText = null;
            _incomeText = null;
            _extractorsText = null;
            _modeHint = null;
            _tipRoot = null;
            _tipTitle = null;
            _tipClass = null;
            _tipDesc = null;
            _tipDef = null;
            _hoverDef = null;
            _tipTiles.Clear();

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

        /// <summary>Лейбл на всю ширину родителя. singleLine — запрет переноса
        /// (горизонтальный Overflow): строки инфопанели не переносятся, длинные
        /// обрезаются FitLine, иначе вторая строка наезжает на соседние ряды.</summary>
        private Text Label(Transform parent, string name, string text, int size, Color color,
            TextAnchor align, bool singleLine = false)
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
            if (singleLine)
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
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
            // Адаптив v0.71.2: заголовок зоны «ВЫДЕЛЕНИЕ», шапка (имя/класс/полосы)
            // фиксирована, строки тела ПОТОЧНО раскладываются FlowInfoBody — пустые
            // пропускаются, высота панели под контент (низ двигает LayoutCommandArea).
            var panel = Panel("SelectionInfo", _canvas.transform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(12f, 130f), new Vector2(430f, 232f), PanelBg);
            _infoPanel = panel;

            var hdr = Label(panel, "hdr", "ВЫДЕЛЕНИЕ", 10, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)hdr.transform, 2f, 14f);
            _infoTitle = Label(panel, "title", "", 16, TextMain, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoTitle.transform, 15f, 31f);
            _infoClass = Label(panel, "class", "", 12, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoClass.transform, 32f, 46f);

            _infoHpFill = UiKit.ProgressBar(panel, "hp", new Color(0f, 0f, 0f, 0.35f),
                new Color(0.4f, 1f, 0.5f, 0.9f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -62f), new Vector2(-96f, -48f));
            _infoHpTxt = Label(panel, "hptxt", "", 12, TextDim, TextAnchor.MiddleRight, true);
            var rtH = (RectTransform)_infoHpTxt.transform;
            rtH.anchorMin = rtH.anchorMax = new Vector2(1f, 1f);
            rtH.pivot = new Vector2(1f, 1f);
            rtH.offsetMin = new Vector2(-84f, -64f);
            rtH.offsetMax = new Vector2(-8f, -46f);

            _infoFuelFill = UiKit.ProgressBar(panel, "fuel", new Color(0f, 0f, 0f, 0.35f),
                new Color(0.95f, 0.75f, 0.3f, 0.9f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -78f), new Vector2(-96f, -64f));
            _infoFuelTrack = (RectTransform)_infoFuelFill.parent;
            _infoFuelTxt = Label(panel, "fueltxt", "", 12, TextDim, TextAnchor.MiddleRight, true);
            var rtF = (RectTransform)_infoFuelTxt.transform;
            rtF.anchorMin = rtF.anchorMax = new Vector2(1f, 1f);
            rtF.pivot = new Vector2(1f, 1f);
            rtF.offsetMin = new Vector2(-84f, -80f);
            rtF.offsetMax = new Vector2(-8f, -62f);

            // Тело карточки (скорость/состояние/приказ/вооружение) — позиции раздаёт
            // FlowInfoBody при каждом обновлении; стартовые Row — любые безопасные.
            _infoMove = Label(panel, "move", "", 12, TextMain, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoMove.transform, 82f, 98f);
            _infoState = Label(panel, "state", "", 12, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoState.transform, 98f, 114f);
            for (int i = 0; i < _infoOrder.Length; i++)
            {
                _infoOrder[i] = Label(panel, "order" + i, "", 12, TextMain, TextAnchor.UpperLeft, true);
                Row((RectTransform)_infoOrder[i].transform, 114f + 16f * i, 130f + 16f * i);
            }

            _infoWeaponsHeader = Label(panel, "whdr", "Вооружение", 12, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoWeaponsHeader.transform, 166f, 180f);
            for (int i = 0; i < _infoWeapons.Length; i++)
            {
                _infoWeapons[i] = Label(panel, "w" + i, "", 12, TextMain, TextAnchor.UpperLeft, true);
                Row((RectTransform)_infoWeapons[i].transform, 180f + 16f * i, 196f + 16f * i);
            }
        }

        /// <summary>Поточная раскладка тела инфопанели: АКТИВНЫЕ непустые строки
        /// получают следующую позицию (шаг 16), пустые пропускаются — без дыр.
        /// Высота панели поджимается под контент (низ задаёт LayoutCommandArea).
        /// fuel=true — полоса топлива занимает свою строку шапки.</summary>
        private void FlowInfoBody(bool fuel)
        {
            float y = fuel ? 82f : 66f;
            PlaceInfoRow(ref y, _infoMove);
            PlaceInfoRow(ref y, _infoState);
            for (int i = 0; i < _infoOrder.Length; i++)
                PlaceInfoRow(ref y, _infoOrder[i]);
            PlaceInfoRow(ref y, _infoWeaponsHeader);
            for (int i = 0; i < _infoWeapons.Length; i++)
                PlaceInfoRow(ref y, _infoWeapons[i]);
            if (_infoPanel != null)
            {
                float bottom = ((RectTransform)_infoPanel.transform).offsetMin.y;
                _infoPanel.offsetMax = new Vector2(430f, bottom + y + 4f);
            }
        }

        private static void PlaceInfoRow(ref float y, Text t)
        {
            if (t == null || !t.gameObject.activeSelf || string.IsNullOrEmpty(t.text))
                return;
            Row((RectTransform)t.transform, y, y + 16f);
            y += 16f;
        }

        private void BuildCommandCard()
        {
            // Слева-внизу, под панелью свойств выделения (SupCom-раскладка):
            // верх — подсказка режима, ниже — сетка команд. Компакт v0.64.0: 112 px.
            var panel = Panel("CommandCard", _canvas.transform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(12f, 12f), new Vector2(430f, 124f), PanelBg);
            _commandPanel = panel;

            _modeHint = Label(panel, "hint", "", 12, TextDim, TextAnchor.UpperLeft, true);
            var rtH = (RectTransform)_modeHint.transform;
            rtH.anchorMin = new Vector2(0f, 1f);
            rtH.anchorMax = new Vector2(1f, 1f);
            rtH.offsetMin = new Vector2(10f, -18f);
            rtH.offsetMax = new Vector2(-10f, -2f);

            // Заголовок секции приказов: помечает ЧТО за зона; когда подсказка
            // активного режима скрыта — поднимается к верхнему краю (без дыры).
            _ordersHdr = Label(panel, "ordershdr", "ПРИКАЗЫ", 10, TextDim, TextAnchor.UpperLeft, true);
            var rtO = (RectTransform)_ordersHdr.transform;
            rtO.anchorMin = new Vector2(0f, 1f);
            rtO.anchorMax = new Vector2(1f, 1f);
            rtO.offsetMin = new Vector2(10f, -31f);
            rtO.offsetMax = new Vector2(-8f, -19f);

            // Заголовок секции гейтов — виден только с полосой (управляет активностью
            // BuildCommandButtons).
            _gateHdr = Label(panel, "gatehdr", "РАЗРЕШЁННОЕ ВООРУЖЕНИЕ", 10, TextDim,
                TextAnchor.UpperLeft, true);
            var rtG = (RectTransform)_gateHdr.transform;
            rtG.anchorMin = new Vector2(0f, 0f);
            rtG.anchorMax = new Vector2(1f, 0f);
            rtG.offsetMin = new Vector2(10f, 27f);
            rtG.offsetMax = new Vector2(-8f, 40f);
            _gateHdr.gameObject.SetActive(false);

            // Полоса гейтов вооружения — внизу карточки; сетка команд поднимается над ней.
            _gateStrip = Panel("gates", panel, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(8f, 3f), new Vector2(-8f, 25f), new Color(0f, 0f, 0f, 0f));
            var glayout = _gateStrip.gameObject.AddComponent<HorizontalLayoutGroup>();
            glayout.spacing = 4f;
            glayout.childControlWidth = false;
            glayout.childControlHeight = false;
            glayout.childForceExpandWidth = false;
            glayout.childForceExpandHeight = false;
            glayout.childAlignment = TextAnchor.MiddleLeft;

            var grid = Panel("grid", panel, Vector2.zero, Vector2.one,
                new Vector2(8f, 29f), new Vector2(-8f, -33f), new Color(0f, 0f, 0f, 0f));
            _cmdGrid = grid;
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(98f, 26f);
            layout.spacing = new Vector2(6f, 4f);
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
            // (появляются вместе с очередями фабрик). Компакт v0.64.0: панель 134 px
            // (верх на 146 px от низа), плитки 48, ленты 52, поля 6.
            var panel = Panel("TabPanel", _canvas.transform,
                new Vector2(0.34f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 12f), new Vector2(-12f, 146f), PanelBg);
            _tabPanel = panel;

            var tabs = Panel("tabs", panel, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(4f, 4f), new Vector2(38f, -4f), new Color(0f, 0f, 0f, 0.15f));
            var vlayout = tabs.gameObject.AddComponent<VerticalLayoutGroup>();
            vlayout.spacing = 3f;
            vlayout.padding = new RectOffset(2, 2, 2, 2);
            vlayout.childControlWidth = false;
            vlayout.childControlHeight = false;
            vlayout.childForceExpandWidth = false;
            vlayout.childForceExpandHeight = false;
            vlayout.childAlignment = TextAnchor.UpperCenter;

            string[] glyphs = { "$", "#", "^", "Ш" };
            for (int i = 0; i < _tabButtons.Length; i++)
            {
                int index = i;
                Button b = Button_(tabs, "tab" + i, glyphs[i], () => SwitchTab(index), 14);
                var brt = (RectTransform)b.transform;
                brt.sizeDelta = new Vector2(28f, 26f);
                var le = b.gameObject.AddComponent<LayoutElement>();
                le.minWidth = 28f;
                le.minHeight = 26f;
                le.preferredWidth = 28f;
                le.preferredHeight = 26f;
                _tabButtons[i] = (b, b.GetComponent<Image>());
            }

step = "title";
            _contextTitle = Label(panel, "title", "", 12, TextDim, TextAnchor.MiddleRight);
            var rtT = (RectTransform)_contextTitle.transform;
            rtT.anchorMin = new Vector2(0.5f, 1f);
            rtT.anchorMax = new Vector2(1f, 1f);
            rtT.offsetMin = new Vector2(10f, -20f);
            rtT.offsetMax = new Vector2(-8f, -2f);

            // Полоса пула (тиры каталога) — видна только на вкладке «Производство».
step = "tiers";
            _tierRoot = Panel("tiers", panel, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(172f, -20f), new Vector2(560f, -2f), new Color(0f, 0f, 0f, 0f));
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
                Button b = Button_(_tierRoot, "tier" + i, tiers[i], () => SwitchTier(tier), 11);
                ((RectTransform)b.transform).sizeDelta = new Vector2(34f, 18f);
                b.gameObject.AddComponent<LayoutElement>().minWidth = 34f;
                _tierButtons[i] = (b, b.GetComponent<Image>(), i);
            }

            // Две строки-ленты; по 56 px с каждого края — их кнопки листания.
step = "strip1";
            _strip1 = UiKit.HStrip(panel, "strip1",
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(172f, -74f), new Vector2(-168f, -22f), out _strip1Content);
step = "strip2";
            _strip2 = UiKit.HStrip(panel, "strip2",
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(172f, -131f), new Vector2(-168f, -79f), out _strip2Content);

step = "arrows";
            UiKit.StripArrows(panel, _font, _strip1Content, 54f, true,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(112f, -74f), new Vector2(168f, -22f));
            UiKit.StripArrows(panel, _font, _strip1Content, 54f, false,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-168f, -74f), new Vector2(-112f, -22f));
            _strip2Nav = new GameObject("strip2nav", typeof(RectTransform));
            var navRoot = (RectTransform)_strip2Nav.transform;
            navRoot.SetParent(panel, false);
            navRoot.anchorMin = Vector2.zero;
            navRoot.anchorMax = Vector2.one;
            navRoot.offsetMin = navRoot.offsetMax = Vector2.zero;
            UiKit.StripArrows(navRoot, _font, _strip2Content, 54f, true,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(112f, -131f), new Vector2(168f, -79f));
            UiKit.StripArrows(navRoot, _font, _strip2Content, 54f, false,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-168f, -131f), new Vector2(-112f, -79f));
            _strip2Nav.SetActive(false);

            // Контекстные кнопки фабрики (2.10): Пауза и Цикл — у правого края нижней строки.
            var ctxGo = new GameObject("factoryCtx", typeof(RectTransform));
            var ctxRt = (RectTransform)ctxGo.transform;
            ctxRt.SetParent(panel, false);
            ctxRt.anchorMin = new Vector2(0f, 1f);
            ctxRt.anchorMax = new Vector2(0f, 1f);
            ctxRt.offsetMin = new Vector2(44f, -131f);
            ctxRt.offsetMax = new Vector2(108f, -22f);
            var ctxLayout = ctxGo.AddComponent<VerticalLayoutGroup>();
            ctxLayout.spacing = 4f;
            ctxLayout.childControlWidth = false;
            ctxLayout.childControlHeight = false;
            ctxLayout.childForceExpandWidth = false;
            ctxLayout.childForceExpandHeight = false;
            _pauseBtn = Button_(ctxGo.transform, "pause", "Пауза", () =>
            {
                if (CtxFactory() != null) ProductionTasks.TogglePause(CtxFactory());
            }, 10);
            ((RectTransform)_pauseBtn.transform).sizeDelta = new Vector2(60f, 26f);
            _pauseBtn.gameObject.AddComponent<LayoutElement>().minHeight = 26f;
            _cycleBtn = Button_(ctxGo.transform, "cycle", "Цикл", () =>
            {
                if (CtxFactory() != null) ProductionTasks.ToggleCycle(CtxFactory());
            }, 10);
            ((RectTransform)_cycleBtn.transform).sizeDelta = new Vector2(60f, 26f);
            _cycleBtn.gameObject.AddComponent<LayoutElement>().minHeight = 26f;
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
            }, 10);
            ((RectTransform)_tplBtn.transform).sizeDelta = new Vector2(60f, 26f);
            _tplBtn.gameObject.AddComponent<LayoutElement>().minHeight = 26f;
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

                UnitDefinition d = def;
                Tile(_strip1Content, new TileSpec
                {
                    Icon = IconOf(def),
                    Caption = Catalog.ShortLabel(def.code, def.unitName),
                    Badge = "×" + units.Count,
                    TypeBadge = TypeBadgeOf(def),
                    OnLeft = () => SelectionService.SelectTypeStatic(controller, d),
                    Def = def,
                });
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
            rt.sizeDelta = new Vector2(10f, 48f);
            gap.AddComponent<LayoutElement>().minWidth = 10f;
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
                _infoTitle.text = Util.FitLine(t.Definition.unitName, FitTitleChars);
                _infoClass.text = Util.FitLine("недострой · " + t.Paid.ToString("F0") + "/" +
                                  t.Cost.ToString("F0") + " · строителей " + t.Builders.Count, FitBodyChars);
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
                FlowInfoBody(false);
                return;
            }

            // Осмотр чужой цели/ракеты (ЛКМ по видимому контакту): панель свойств
            // работает как разведкарточка — без выделения и приказов.
            if (selected.Count == 0 && controller.Selection.Inspected != null)
            {
                RefreshInspected(controller.Selection.Inspected);
                FlowInfoBody(false);
                return;
            }

            Unit single = selected.Count == 1 ? selected[0] : null;
            bool isSingle = single != null && !single.disabled;
            bool isAir = isSingle && single is Aircraft;
            int weapons = isSingle && single.weaponStations != null
                ? Mathf.Min(single.weaponStations.Count, _infoWeapons.Length)
                : 0;
            if (!isSingle)
                weapons = AggregateGroupWeapons(selected);

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
            FlowInfoBody(isAir);
        }

        private void RefreshSingleUnit(Unit u)
        {
            string defName = u.definition != null ? u.definition.unitName : "";
            _infoTitle.text = Util.FitLine(
                !string.IsNullOrEmpty(u.unitName) ? u.unitName : defName, FitTitleChars);
            _infoClass.text = Util.FitLine(
                (defName.Length > 0 ? defName + " · " : "") + ClassOf(u), FitBodyChars);

            float hp = GameApi.HpFraction(u);
            UiKit.SetProgress(_infoHpFill, hp);
            _infoHpTxt.text = hp >= 0.995f ? "HP цел" : $"HP {hp:P0}";

            float speedKmh = Mathf.Abs(u.speed) * 3.6f;
            if (u is Aircraft a)
            {
                float fuel = a.GetFuelLevel();
                UiKit.SetProgress(_infoFuelFill, fuel);
                _infoFuelTxt.text = $"топливо {fuel:P0}";
                _infoMove.text = Util.FitLine($"{speedKmh:F0} км/ч · {a.radarAlt:F0} м", FitBodyChars);
                _infoState.text = Util.FitLine(a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null
                    ? Catalog.RuPilotState(a.pilots[0].GetCurrentState())
                    : "нет пилота", FitBodyChars);
            }
            else
            {
                _infoMove.text = $"{speedKmh:F0} км/ч";
                _infoState.text = Util.FitLine(
                    u is Ship s && s.TryGetComponent(out ShipAI sai)
                        ? Catalog.RuShipState(sai.state.ToString())
                        : "", FitBodyChars);
            }

            _infoOrder[0].text = Util.FitLine("Приказ: " + PlanSummary(u), FitBodyChars);
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";
            _infoWeaponsHeader.text = "Вооружение";

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
                    _infoWeapons[w++].text = Util.FitLine(
                        $"{name}: {ws.Ammo}/{ws.FullAmmo}{status}", FitBodyChars);
                }
        }

        /// <summary>Собирает сводку вооружения по выделению в _groupWeapons: станции
        /// живых юнитов складываются по имени оружия (грузовые/десантные станции — мимо),
        /// носители считаются по юнитам. Возвращает число строк под доступные метки
        /// (при переполнении последняя метка — «+ ещё N типов»).</summary>
        private int AggregateGroupWeapons(List<Unit> selected)
        {
            _groupWeapons.Clear();
            foreach (Unit unit in selected)
            {
                if (unit == null || unit.disabled || unit.weaponStations == null)
                    continue;
                _groupWeapons.BeginUnit();
                foreach (WeaponStation ws in unit.weaponStations)
                {
                    if (ws == null || ws.WeaponInfo == null || ws.Cargo ||
                        ws.WeaponInfo.cargo || ws.WeaponInfo.troops)
                        continue;
                    _groupWeapons.AddStation(
                        !string.IsNullOrEmpty(ws.WeaponInfo.shortName)
                            ? ws.WeaponInfo.shortName
                            : ws.WeaponInfo.weaponName,
                        ws.Ammo, ws.FullAmmo,
                        Catalog.WeaponDomains(
                            ws.WeaponInfo.effectiveness.antiAir,
                            ws.WeaponInfo.effectiveness.antiSurface,
                            ws.WeaponInfo.effectiveness.antiMissile,
                            ws.WeaponInfo.nuclear, ws.WeaponInfo.jammer));
                }
            }
            return Mathf.Min(_groupWeapons.Rows.Count, _infoWeapons.Length);
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
            _infoHpTxt.text = hp >= 0.995f ? "HP целы" : $"HP {hp:P0}";
            // GetAmmoLevel — доли, но у энергостанций Full<Ammo (заряд-единицы) —
            // среднее может быть >1; клэмп, чтобы не показывать «6 666%».
            _infoMove.text = ammoN > 0 ? $"боезапас {Mathf.Clamp01(ammoSum / ammoN):P0}" : "";
            _infoState.text = "";
            _infoOrder[0].text = "";
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";

            // Сводка вооружения: чем группа может работать по целям (домен-тег) и
            // сколько зарядов осталось; пустые типы видны сразу — сигнал на перезарядку.
            _infoWeaponsHeader.text = "Вооружение группы";
            var rows = _groupWeapons.Rows;
            bool overflow = rows.Count > _infoWeapons.Length;
            int shown = overflow ? _infoWeapons.Length - 1 : rows.Count;
            for (int i = 0; i < shown; i++)
                _infoWeapons[i].text = Util.FitLine(Catalog.FormatWeaponRow(rows[i]), FitBodyChars);
            if (overflow)
                _infoWeapons[_infoWeapons.Length - 1].text =
                    "+ ещё " + (rows.Count - shown) + " " +
                    Util.RuPlural(rows.Count - shown, "тип", "типа", "типов");
        }

        // ---------- панель осмотра (чужие цели и ракеты, ЛКМ) ----------

        /// <summary>Разведкарточка чужой цели: живой контакт показывает HP/скорость,
        /// протухший — только имя/класс и возраст контакта (данные «из памяти»
        /// не даём — слежка вслепую читерит). Ракета — отдельная карточка.</summary>
        private void RefreshInspected(Unit u)
        {
            if (u is Missile missile)
            {
                RefreshInspectedMissile(missile);
                return;
            }

            GameApi.TryGetLocalHQ(out FactionHQ hq);
            float age = float.MaxValue;
            if (GameApi.TryGetContact(u, hq, out TrackingInfo info) && info != null)
                age = Time.timeSinceLevelLoad - info.lastSpottedTime;
            bool live = age < 4f; // тот же порог «живого» контакта, что у иконок

            int hash = 17 * 31 + u.GetInstanceID();
            hash = hash * 31 + (live ? 1 : 0);
            if (hash != _inspHash)
            {
                _inspHash = hash;
                _infoFuelTrack.gameObject.SetActive(false);
                _infoFuelTxt.gameObject.SetActive(false);
                _infoWeaponsHeader.gameObject.SetActive(false);
                for (int i = 0; i < _infoWeapons.Length; i++)
                    _infoWeapons[i].gameObject.SetActive(false);
                // HP чужого показываем только под живым наблюдением.
                _infoHpFill.parent.gameObject.SetActive(live);
                _infoHpTxt.gameObject.SetActive(live);
            }

            string defName = u.definition != null ? u.definition.unitName : "";
            _infoTitle.text = Util.FitLine(
                !string.IsNullOrEmpty(u.unitName) ? u.unitName : defName, FitTitleChars);
            _infoClass.text = Util.FitLine(
                (defName.Length > 0 ? defName + " · " : "") + ClassOf(u) + " · чужой контакт",
                FitBodyChars);

            if (live)
            {
                float hp = GameApi.HpFraction(u);
                UiKit.SetProgress(_infoHpFill, hp);
                _infoHpTxt.text = hp >= 0.995f ? "HP цел" : $"HP {hp:P0}";
                float speedKmh = Mathf.Abs(u.speed) * 3.6f;
                _infoMove.text = Util.FitLine(u is Aircraft
                    ? $"{speedKmh:F0} км/ч · {u.radarAlt:F0} м"
                    : $"{speedKmh:F0} км/ч", FitBodyChars);
                _infoState.text = "под наблюдением";
            }
            else
            {
                _infoMove.text = "";
                _infoState.text = Util.FitLine($"контакт {age:F0} с назад", FitBodyChars);
            }
            _infoOrder[0].text = "";
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";
        }

        /// <summary>Карточка ракеты/бомбы (осмотр ЛКМ): скорость/высота, двигатель,
        /// ГСН и БЧ — данные определения; цель показываем только если она наша
        /// (аналог RWR — чужие цели не подсматриваем).</summary>
        private void RefreshInspectedMissile(Missile m)
        {
            int hash = 17 * 31 + m.GetInstanceID() * 7; // макет ракеты всегда один
            if (hash != _inspHash)
            {
                _inspHash = hash;
                _infoHpFill.parent.gameObject.SetActive(false);
                _infoHpTxt.gameObject.SetActive(false);
                _infoFuelTrack.gameObject.SetActive(false);
                _infoFuelTxt.gameObject.SetActive(false);
                _infoWeaponsHeader.gameObject.SetActive(false);
                for (int i = 0; i < _infoWeapons.Length; i++)
                    _infoWeapons[i].gameObject.SetActive(false);
            }

            GameApi.TryGetLocalHQ(out FactionHQ hq);
            bool ours = m.NetworkHQ != null && m.NetworkHQ == hq;
            bool powered = m.GetThrustDuration() > 0f;

            WeaponInfo wi = m.GetWeaponInfo();
            string name = wi != null && !string.IsNullOrEmpty(wi.shortName)
                ? wi.shortName
                : (wi != null ? wi.weaponName : null);
            if (string.IsNullOrEmpty(name) && m.definition != null)
                name = m.definition.unitName;
            _infoTitle.text = Util.FitLine(
                string.IsNullOrEmpty(name) ? (powered ? "ракета" : "бомба") : name, FitTitleChars);
            _infoClass.text = (powered ? "ракета" : "бомба") + (ours ? " · наша" : " · чужая");

            float speedKmh = Mathf.Abs(m.speed) * 3.6f;
            _infoMove.text = Util.FitLine($"{speedKmh:F0} км/ч · {m.transform.position.GlobalY():F0} м",
                FitBodyChars);
            _infoState.text = powered
                ? (m.EngineOn() ? "двигатель: работает" : "двигатель: выгорел")
                : "свободное падение";

            // ГСН/БЧ — параметры определения (не секрет); цель — только своя (RWR).
            int w = 0;
            string seeker = m.GetSeekerType();
            if (!string.IsNullOrEmpty(seeker))
                _infoOrder[w++].text = Util.FitLine("ГСН: " + seeker, FitBodyChars);
            float yieldKg = m.GetYield();
            if (yieldKg > 0f)
                _infoOrder[w++].text = Util.FitLine("БЧ: " + FormatYield(yieldKg), FitBodyChars);
            if (UnitRegistry.TryGetUnit(m.targetID, out Unit tgt) && tgt != null &&
                tgt.NetworkHQ != null && tgt.NetworkHQ == hq)
                _infoOrder[w++].text = Util.FitLine("ЦЕЛЬ: " +
                    (tgt.definition != null ? tgt.definition.unitName : tgt.name), FitBodyChars);
            for (; w < _infoOrder.Length; w++)
                _infoOrder[w].text = "";
        }

        /// <summary>Масса БЧ в читаемом виде: до тонны — кг, дальше — килотонны.</summary>
        private static string FormatYield(float kg) =>
            kg >= 1e6f
                ? (kg / 1e6f).ToString("F1") + " кт"
                : kg.ToString("F0") + " кг";

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
                        AirOrderMode.Salvo => "Залп",
                        AirOrderMode.Rtb => "На базу",
                        _ => "Транзит"
                    });
                    if (seg.Mode != AirOrderMode.Attack && seg.Points.Count > 1)
                        sb.Append(" ×").Append(seg.Points.Count);
                }
                return sb.ToString();
            }
            if (GroundOrders.KindOf(u) == GroundOrderKind.Attack)
                return "атака цели";
            if (GroundOrders.TryGetQueue(u, out IReadOnlyList<GlobalPosition> queue, out int idx))
            {
                int left = Mathf.Max(0, queue.Count - idx);
                var targets = GroundOrders.TargetQueueOf(u);
                string tail = targets != null ? $", атак: {targets.Count} в очереди" : "";
                if (GroundOrders.KindOf(u) == GroundOrderKind.AttackMove)
                    return $"атака-движ., осталось {left} тчк{tail}";
                return $"цепочка, осталось {left} тчк{tail}";
            }
            return "нет";
        }

        /// <summary>Ключ контекста командной карты: состав выделения по типам войск —
        /// при смене ключа кнопки перестраиваются (контекстные ряды авиации/наземки).</summary>
        private int _cmdContextKey = -1;

        private void RefreshCommands()
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;

            // Контекст: есть ли авиация/наземка в выделении; одиночный борт со станциями
            // получает ряд чипов выбора вооружения залпа.
            bool hasAir = false;
            bool hasGround = false;
            Aircraft singleAir = null;
            int selCount = 0;
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                selCount++;
                if (unit is Aircraft a)
                {
                    hasAir = true;
                    if (selCount == 1 && a.weaponStations.Count > 0)
                        singleAir = a;
                }
                else if (unit is GroundVehicle || unit is Ship)
                    hasGround = true;
            }
            if (selCount != 1)
                singleAir = null;
            bool showAir = hasAir || selCount == 0; // пустое выделение — полный набор
            WeaponClass gateClasses = WeaponGateService.ClassesOf(controller.Selection.Selected);
            int key = (showAir ? 1 : 0) | (hasGround ? 2 : 0) | (singleAir != null ? 4 : 0) |
                      ((int)gateClasses << 3); // состав классов — тоже контекст кнопок
            if (key != _cmdContextKey)
            {
                _cmdContextKey = key;
                BuildCommandButtons(controller, showAir, singleAir);
            }

            // Подсказка — ТОЛЬКО в активном спец-режиме (стройка/залп/веер/приказ):
            // в покое её нет, верх карточки — заголовок «ПРИКАЗЫ» (без пустой строки).
            string pending = OrderModes.Pending != OrderMode.None && OrderModes.Pending != OrderMode.Move
                ? OrderModes.Label(OrderModes.Pending)
                : null;
            if (BuildTasks.Pending != null)
                _modeHint.text = Util.FitLine(
                    $"{BuildTasks.Pending.unitName}: ЛКМ — площадка, удержание — поворот (Shift — серия) · ПКМ/Esc — отмена", 62);
            else if (pending == "Залп")
                _modeHint.text = Util.FitLine(
                    "Залп: ПКМ по цели/земле — залповый пуск · Shift+ПКМ по целям — доб. в залп", 62);
            else if (pending == "Атака веером")
                _modeHint.text = Util.FitLine(
                    "Веер: ПКМ по целям — накопить · ПКМ по земле — раздать группу", 62);
            else if (pending != null)
                _modeHint.text = Util.FitLine(
                    $"{pending}: ПКМ — вейпоинт (Shift — серия) · Esc — движение", 62);
            else
                _modeHint.text = "";
            ApplyCommandLayout();

            foreach (var (btn, img, _, _) in _cmdButtons)
                img.color = BtnBg;
            HighlightCommand("cmd_move", OrderModes.Pending == OrderMode.Move || OrderModes.Pending == OrderMode.None);
            HighlightCommand("cmd_amove", OrderModes.Pending == OrderMode.AttackMove);
            HighlightCommand("cmd_patrol", OrderModes.Pending == OrderMode.Patrol);
            HighlightCommand("cmd_attack", OrderModes.Pending == OrderMode.Attack);
            HighlightCommand("cmd_salvo", OrderModes.Pending == OrderMode.Salvo);
            HighlightCommand("cmd_fan", OrderModes.Pending == OrderMode.DisperseAttack);

            if (showAir)
            {
                HighlightCommand("cmd_vlow", AircraftOrders.ProfileIndex == 0);
                HighlightCommand("cmd_low", AircraftOrders.ProfileIndex == 1);
                HighlightCommand("cmd_med", AircraftOrders.ProfileIndex == 2);
                HighlightCommand("cmd_high", AircraftOrders.ProfileIndex == 3);
                HighlightCommand("cmd_spd0", AircraftOrders.SpeedIndex == SpeedMode.Full);
                HighlightCommand("cmd_spd1", AircraftOrders.SpeedIndex == SpeedMode.Cruise);
                HighlightCommand("cmd_spd2", AircraftOrders.SpeedIndex == SpeedMode.Economy);
                DefenseMode defense = DefenseMode.Auto;
                foreach (Unit unit in controller.Selection.Selected)
                    if (unit is Aircraft)
                    {
                        defense = DefenseService.ModeOf(unit); // подсветка по первому борту
                        break;
                    }
                HighlightCommand("cmd_def0", defense == DefenseMode.Auto);
                HighlightCommand("cmd_def1", defense == DefenseMode.Flares);
                HighlightCommand("cmd_def2", defense == DefenseMode.Off);
            }
            bool anyHold = false;
            foreach (Unit unit in controller.Selection.Selected)
                if (StanceService.IsHoldFire(unit))
                {
                    anyHold = true;
                    break;
                }
            HighlightCommand("cmd_hold", anyHold);

            // Гейты: цвет кнопки — состояние класса в выделении (зелёный всем разрешён,
            // красный всем выключен, жёлтый вперемешку); подпись «+/−/±» дублирует цвет.
            foreach (WeaponClass cls in GateOrder)
            {
                if ((gateClasses & cls) == 0)
                    continue;
                int state = WeaponGateService.GateStateOf(controller.Selection.Selected, cls);
                TintCommand("gate_" + cls,
                    state > 0 ? BtnActive : state == 0 ? WarnRed : WarnYellow,
                    (state > 0 ? "+ " : state == 0 ? "- " : "± ") + Catalog.ClassLabel(cls));
            }

            // Чипы станций: боезапас живой, выбранность — подсветкой.
            if (singleAir != null)
            {
                int idx = 0;
                foreach (var (btn, img, _, label) in _cmdButtons)
                {
                    if (!btn.name.StartsWith("st_"))
                        continue;
                    if (idx < singleAir.weaponStations.Count)
                    {
                        WeaponStation st = singleAir.weaponStations[idx];
                        if (st != null && st.WeaponInfo != null)
                        {
                            label.text = StationChipLabel(st);
                            img.color = SalvoService.StationEnabled(singleAir, idx) ? BtnActive : BtnBg;
                        }
                    }
                    idx++;
                }
            }
        }

        /// <summary>Подпись чипа станции: короткое имя вооружения ×боезапас.</summary>
        private static string StationChipLabel(WeaponStation st)
        {
            string name = !string.IsNullOrEmpty(st.WeaponInfo.shortName)
                ? st.WeaponInfo.shortName
                : st.WeaponInfo.weaponName;
            if (string.IsNullOrEmpty(name))
                name = "ст.";
            if (name.Length > 8)
                name = name.Substring(0, 8);
            return name + " ×" + st.Ammo;
        }

        private void HighlightCommand(string name, bool on)
        {
            foreach (var (btn, img, _, _) in _cmdButtons)
                if (btn.name == name && on)
                    img.color = BtnActive;
        }

        /// <summary>Покрасить кнопку команды в точный цвет; text != null — обновить
        /// и подпись (гейты: «+ Бомбы» / «− Бомбы» / «± Бомбы» = всем/никому/вперемешку).</summary>
        private void TintCommand(string name, Color color, string text = null)
        {
            foreach (var (btn, img, _, label) in _cmdButtons)
                if (btn.name == name)
                {
                    img.color = color;
                    if (text != null && label != null)
                        label.text = text;
                    return;
                }
        }

        /// <summary>Контекстная пересборка сетки команд (состав выделения сменился):
        /// базовые приказы — всегда, ряды авиации (высота/скорость/защита) — при
        /// авиации в выделении, чипы станций — одиночному борту. Карточка растёт
        /// вверх, панель свойств выделения поднимается за ней.</summary>
        private void BuildCommandButtons(CommanderModeController controller, bool showAir, Aircraft singleAir)
        {
            Transform grid = null;
            foreach (Transform child in _canvas.transform)
                if (child.name == "CommandCard")
                    foreach (Transform c2 in child)
                        if (c2.name == "grid")
                            grid = c2;
            if (grid == null)
                return;

            foreach (var (btn, _, _, _) in _cmdButtons)
                Destroy(btn.gameObject);
            _cmdButtons.Clear();

            var defs = new List<(string name, string label, System.Action a)>();
            if (showAir)
            {
                defs.Add(("cmd_move", "Движение (M)", () => OrderModes.Pending = OrderMode.Move));
                defs.Add(("cmd_amove", "Атака-движ (A)", () => OrderModes.Pending = OrderMode.AttackMove));
                defs.Add(("cmd_patrol", "Патруль (U)", () => OrderModes.Pending = OrderMode.Patrol));
                defs.Add(("cmd_attack", "Атака (T)", () => OrderModes.Pending = OrderMode.Attack));
                defs.Add(("cmd_salvo", "Залп (V)", () => OrderModes.Pending = OrderMode.Salvo));
                defs.Add(("cmd_fan", "Веер (R)", () => controller.MoveOrders.ChargeFanAttack()));
                defs.Add(("cmd_stop", "Стоп (S)", () => controller.MoveOrders.CancelAll(controller.Selection.Selected)));
                defs.Add(("cmd_disperse", "Распределить (Alt+Shift+R)", () =>
                    controller.MoveOrders.IssueDisperseMove(controller.Selection.Selected)));
                defs.Add(("cmd_rtb", "На базу (G)", () =>
                    controller.MoveOrders.IssueReturnToBase(controller.Selection.Selected)));
                defs.Add(("cmd_hold", "Огонь: удерж (H)", () =>
                {
                    foreach (Unit unit in controller.Selection.Selected)
                        StanceService.Toggle(unit);
                }));
                defs.Add(("cmd_vlow", "Свер-хнизко 75м", () => controller.SetProfileUi(0)));
                defs.Add(("cmd_low", "Низко 150м", () => controller.SetProfileUi(1)));
                defs.Add(("cmd_med", "Средне 800м", () => controller.SetProfileUi(2)));
                defs.Add(("cmd_high", "Высоко 2500м", () => controller.SetProfileUi(3)));
                defs.Add(("cmd_spd0", "Полный (Shift+1)", () => controller.SetSpeedUi(0)));
                defs.Add(("cmd_spd1", "Крейсер (Shift+2)", () => controller.SetSpeedUi(1)));
                defs.Add(("cmd_spd2", "Эконом (Shift+3)", () => controller.SetSpeedUi(2)));
                defs.Add(("cmd_flare", "Ловушки! (X)", () =>
                {
                    int popped = DefenseService.PopNow(controller.Selection.Selected);
                    CommanderHud.Notice(popped > 0 ? $"Ловушки: бортов — {popped}" : "Нет бортов с контрмерами");
                }));
                defs.Add(("cmd_def0", "Защита: Авто", () => controller.SetDefenseUi(0)));
                defs.Add(("cmd_def1", "Защита: Ловушки", () => controller.SetDefenseUi(1)));
                defs.Add(("cmd_def2", "Защита: Выкл", () => controller.SetDefenseUi(2)));
            }
            else
            {
                // Наземка/флот: только применимые приказы, без авиарядов.
                defs.Add(("cmd_move", "Движение (M)", () => OrderModes.Pending = OrderMode.Move));
                defs.Add(("cmd_amove", "Атака-движ (A)", () => OrderModes.Pending = OrderMode.AttackMove));
                defs.Add(("cmd_attack", "Атака (T)", () => OrderModes.Pending = OrderMode.Attack));
                defs.Add(("cmd_fan", "Веер (R)", () => controller.MoveOrders.ChargeFanAttack()));
                defs.Add(("cmd_stop", "Стоп (S)", () => controller.MoveOrders.CancelAll(controller.Selection.Selected)));
                defs.Add(("cmd_disperse", "Распределить (Alt+Shift+R)", () =>
                    controller.MoveOrders.IssueDisperseMove(controller.Selection.Selected)));
                defs.Add(("cmd_hold", "Огонь: удерж (H)", () =>
                {
                    foreach (Unit unit in controller.Selection.Selected)
                        StanceService.Toggle(unit);
                }));
            }

            // Чипы станций залпа (одиночный борт): ЛКМ — вкл/выкл станции в залпе.
            int chips = 0;
            if (singleAir != null)
                foreach (WeaponStation st in singleAir.weaponStations)
                {
                    if (st == null || st.Cargo || st.WeaponInfo == null || chips >= 12)
                        continue;
                    int idx = chips;
                    defs.Add(("st_" + idx, StationChipLabel(st), () => SalvoService.ToggleStation(singleAir, idx)));
                    chips++;
                }

            int rows = Mathf.CeilToInt(defs.Count / 4f);
            foreach (var (name, label, action) in defs)
            {
                Button b = Button_(grid, name, label, action, 11);
                _cmdButtons.Add((b, b.GetComponent<Image>(), action, b.GetComponentInChildren<Text>()));
            }

            // Гейты вооружения (DESIGN §2.11): кнопка на каждый класс выделения —
            // внизу карточки, тоггл всему выделению; гейт главнее явных приказов.
            // С полосой низ сетки команд поднимается над ней (26 px + зазор).
            int gates = 0;
            if (_gateStrip != null)
            {
                WeaponClass classes = WeaponGateService.ClassesOf(controller.Selection.Selected);
                foreach (WeaponClass cls in GateOrder)
                {
                    if ((classes & cls) == 0)
                        continue;
                    WeaponClass c = cls;
                    Button b = Button_(_gateStrip, "gate_" + cls, Catalog.ClassLabel(cls),
                        () => WeaponGateService.ToggleForSelection(controller.Selection.Selected, c), 11);
                    ((RectTransform)b.transform).sizeDelta = new Vector2(76f, 22f);
                    b.gameObject.AddComponent<LayoutElement>().minWidth = 76f;
                    _cmdButtons.Add((b, b.GetComponent<Image>(), null, b.GetComponentInChildren<Text>()));
                    gates++;
                }
            }
            var gridRt = (RectTransform)grid;
            gridRt.offsetMin = new Vector2(8f, gates > 0 ? 43f : 3f);
            _gateStrip.gameObject.SetActive(gates > 0);
            if (_gateHdr != null)
                _gateHdr.gameObject.SetActive(gates > 0);
            _cmdRows = rows;
            _cmdGates = gates > 0;
            ApplyCommandLayout();
        }

        /// <summary>Раскладка командной карточки по последней сборке кнопок и наличию
        /// подсказки активного режима: без подсказки заголовок «ПРИКАЗЫ» у верхнего
        /// края (никаких пустых строк), с подсказкой — под ней. Панель свойств
        /// выделения поднимается за карточкой (низ = верх карточки + зазор 6).</summary>
        private void ApplyCommandLayout()
        {
            bool hint = _modeHint != null && !string.IsNullOrEmpty(_modeHint.text);
            if (_ordersHdr != null)
            {
                var rt = (RectTransform)_ordersHdr.transform;
                rt.offsetMin = new Vector2(10f, hint ? -31f : -15f);
                rt.offsetMax = new Vector2(-8f, hint ? -19f : -3f);
            }
            if (_cmdGrid != null)
                _cmdGrid.offsetMax = new Vector2(-8f, hint ? -33f : -17f);
            float height = (hint ? 18f : 2f) + 13f + _cmdRows * 26f +
                           Mathf.Max(0, _cmdRows - 1) * 4f + 5f +
                           (_cmdGates ? 44f : 3f);
            _commandPanel.offsetMax = new Vector2(430f, 12f + height);
            if (_infoPanel != null)
                _infoPanel.offsetMin = new Vector2(12f, 12f + height + 6f);
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
            {
                hash = hash * 31 + fundsKey + _prodTier * 7 + (depot != null ? 1 : 0) +
                       (airbase != null ? 2 : 0) + (hasEngineer ? 4 : 0);
                if (airbase != null)
                    hash = hash * 31 + AirbaseReserve.Version; // плитки резерва ангара
            }
            else if (_activeTab == TabUpgrade)
            {
                hash = hash * 31 + (extractor != null ? EconomyService.TierOf(extractor) : 0);
                hash = hash * 31 + (EconomyService.Running ? 1 : 0);
                hash = hash * 31 + fundsKey;
            }
            // Нижняя строка видима ВСЕГДА (без прыжков высоты): пустая — просто пустая лента.
            // Высота панели — компакт v0.64.0 (12 отступ + 134 контент = 146).
            if (_tabPanel != null)
            {
                var prt = (RectTransform)_tabPanel.transform;
                prt.offsetMax = new Vector2(-12f, 146f);
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

        /// <summary>Плитка очереди стройки: иконка + подпись + бейдж ×N, у головной —
        /// прогресс-бар (зона не расчищена — полоса красная); клик — камера к площадке.</summary>
        private void AddQueueTile(Transform content, BuildTasks.Task t, bool head, int count,
            CommanderModeController controller)
        {
            BuildTasks.Task task = t;
            Tile(content, new TileSpec
            {
                Icon = t.Definition != null ? t.Definition.friendlyIcon : null,
                Caption = t.Definition != null
                    ? Catalog.ShortLabel(t.Definition.code, t.Definition.unitName) : null,
                Badge = count > 1 ? "×" + count : null,
                Progress = head ? t.Progress01 : (float?)null,
                BarColor = head && t.ZoneBlocked ? new Color(1f, 0.45f, 0.3f, 0.95f) : (Color?)null,
                OnLeft = () =>
                {
                    Vector3 site = task.Site.ToLocalPosition();
                    controller.CameraState?.CenterOn(site);
                },
                Def = t.Definition,
            });
        }

        /// <summary>Спека плитки 48×48 (консолидация v0.48): каталог/очереди/ростер —
        /// иконка, короткая подпись (code), бейдж ×N/цена (право-верх), бейдж типа
        /// объекта (лево-верх), прогресс-бар, клики ЛКМ/ПКМ, hover-карточка (Def).</summary>
        private struct TileSpec
        {
            public Sprite Icon;
            public string Caption;      // короткая подпись юнита (низ плитки)
            public string Badge;        // ×N стака или цена каталога (право-верх)
            public float? Progress;     // полоса готовности головной позиции очереди
            public Color? BarColor;
            public string TypeBadge;    // тип объекта: ТНК/ЗРК/АРТ... или И/У/М авиации
            public System.Action OnLeft;
            public System.Action OnRight;
            public UnitDefinition Def;  // не null — плитка регистрируется для hover-карточки
        }

        /// <summary>ЕДИНЫЙ строитель плитки 48×48 (консолидация v0.48): иконка,
        /// опционально бейдж ×N и прогресс-бар; клики ЛКМ/ПКМ через TileClicks.</summary>
        private GameObject Tile(Transform parent, in TileSpec spec)
        {
            var go = new GameObject("tile", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(48f, 48f);
            go.GetComponent<Image>().color = PanelBg2;
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 48f;
            le.minHeight = 48f;
            le.preferredWidth = 48f;
            le.preferredHeight = 48f;

            var iconImg = Icon(go.transform, spec.Icon, new Vector2(30f, 30f));
            ((RectTransform)iconImg.transform).anchorMin = ((RectTransform)iconImg.transform).anchorMax =
                new Vector2(0.5f, 1f);
            ((RectTransform)iconImg.transform).anchoredPosition = new Vector2(0f, -18f);

            if (!string.IsNullOrEmpty(spec.TypeBadge))
                AddTileTypeBadge(go.transform, spec.TypeBadge);
            if (!string.IsNullOrEmpty(spec.Badge))
            {
                var b = Label(go.transform, "n", spec.Badge, 11, Accent, TextAnchor.UpperRight);
                var rtN = (RectTransform)b.transform;
                rtN.anchorMin = new Vector2(0.35f, 0.62f);
                rtN.anchorMax = new Vector2(0.97f, 0.98f);
                rtN.offsetMin = rtN.offsetMax = Vector2.zero;
                b.raycastTarget = false;
            }
            bool hasCaption = !string.IsNullOrEmpty(spec.Caption);
            if (hasCaption)
            {
                var c = Label(go.transform, "cap", spec.Caption, 10, TextMain, TextAnchor.LowerCenter);
                var rtC = (RectTransform)c.transform;
                rtC.anchorMin = new Vector2(0.02f, 0f);
                rtC.anchorMax = new Vector2(0.98f, 0.26f);
                rtC.offsetMin = rtC.offsetMax = Vector2.zero;
                c.raycastTarget = false;
            }
            if (spec.Progress.HasValue)
                AddTileProgressBar(go.transform, spec.Progress.Value,
                    spec.BarColor.HasValue && spec.BarColor.Value.r > 0.9f, hasCaption);

            var tile = go.AddComponent<TileClicks>();
            tile.Left = spec.OnLeft;
            tile.Right = spec.OnRight;
            if (spec.Def != null)
                _tipTiles.Add((rt, parent.parent as RectTransform, spec.Def));
            return go;
        }

        /// <summary>Бейдж типа в левом-верхнем углу плитки: цветная аббревиатура
        /// на полупрозрачной подложке — различает однотипные квадратики каталога.</summary>
        private void AddTileTypeBadge(Transform tile, string badge)
        {
            float w = Mathf.Clamp(8f + 5.5f * badge.Length, 14f, 32f);
            var bg = new GameObject("type", typeof(RectTransform), typeof(Image));
            var brt = (RectTransform)bg.transform;
            brt.SetParent(tile, false);
            brt.anchorMin = brt.anchorMax = new Vector2(0f, 1f);
            brt.pivot = new Vector2(0f, 1f);
            brt.anchoredPosition = new Vector2(2f, -1f);
            brt.sizeDelta = new Vector2(w, 12f);
            var bimg = bg.GetComponent<Image>();
            bimg.color = new Color(0f, 0f, 0f, 0.55f);
            bimg.raycastTarget = false;
            var t = Label(bg.transform, "lbl", badge, 9, BadgeColorOf(badge), TextAnchor.MiddleCenter);
            var rtT = (RectTransform)t.transform;
            rtT.anchorMin = Vector2.zero;
            rtT.anchorMax = Vector2.one;
            rtT.offsetMin = rtT.offsetMax = Vector2.zero;
            t.raycastTarget = false;
        }

        /// <summary>Цвет бейджа типа: класс техники/роли авиации кодируется цветом
        /// (броня — зелёный, ПВО — фиолетовый, артиллерия — оранжевый и т.д.).</summary>
        private static Color BadgeColorOf(string badge) => badge switch
        {
            "И" => new Color(0.45f, 0.85f, 1f, 1f),
            "У" => new Color(1f, 0.68f, 0.3f, 1f),
            "М" => new Color(1f, 0.9f, 0.35f, 1f),
            "ТР" => new Color(0.72f, 0.78f, 0.84f, 1f),
            "РЛС" => new Color(0.62f, 0.9f, 1f, 1f),
            "ТНК" or "ББМ" or "ЛБМ" => new Color(0.55f, 0.85f, 0.5f, 1f),
            "АРТ" => new Color(1f, 0.65f, 0.35f, 1f),
            "ЗУ" or "ПЗРК" or "ЗРК" => new Color(0.85f, 0.55f, 1f, 1f),
            _ => new Color(0.72f, 0.76f, 0.72f, 1f),
        };

        /// <summary>Иконка плитки: авиация — индивидуальный силуэт mapIcon (как на
        /// макрокамере/миникарте), прочее — визорный маркер friendlyIcon.</summary>
        private static Sprite IconOf(UnitDefinition def)
        {
            if (def == null)
                return null;
            if (def is AircraftDefinition && def.mapIcon != null)
                return def.mapIcon;
            return def.friendlyIcon != null ? def.friendlyIcon : def.mapIcon;
        }

        /// <summary>Бейдж типа плитки: наземка — из vehicleType, авиация — роль
        /// из roleIdentity; здания — без бейджа.</summary>
        private static string TypeBadgeOf(UnitDefinition def)
        {
            if (def is VehicleDefinition vd)
                return Catalog.BadgeOf(vd.vehicleType);
            if (def is AircraftDefinition)
                return Catalog.AirBadge(def.roleIdentity, def.typeIdentity);
            return null;
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

        /// <summary>Прогресс-бар в нижней части плитки очереди (заполнение = готовность);
        /// при наличии подписи поднимается над ней.</summary>
        private void AddTileProgressBar(Transform tile, float progress, bool blocked, bool hasCaption)
        {
            var track = new GameObject("pb", typeof(RectTransform), typeof(Image));
            var trt = (RectTransform)track.transform;
            trt.SetParent(tile, false);
            if (hasCaption)
            {
                trt.anchorMin = new Vector2(0.06f, 0.3f);
                trt.anchorMax = new Vector2(0.94f, 0.44f);
            }
            else
            {
                trt.anchorMin = new Vector2(0.06f, 0.03f);
                trt.anchorMax = new Vector2(0.94f, 0.16f);
            }
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

                /// <summary>Нижняя строка «Производства» (фабрика): очередь заказов — плитки
        /// «иконка + подпись + ×N», у головной позиции — прогресс; клики по стаку:
        /// ЛКМ — добавить 1 (Shift — 5), ПКМ — снять 1 с конца (Shift — 5, с возвратом денег).</summary>
        private void FillFactoryQueue(object factory)
        {
            ClearChildren(_strip2Content);
            if (factory == null)
                return;
            // Стак подряд идущих одинаковых позиций (порядок сохраняется).
            var runs = new List<(UnitDefinition def, ProductionTasks.QueueItem head,
                ProductionTasks.QueueItem tail, int count)>();
            foreach (ProductionTasks.QueueItem item in ProductionTasks.All)
            {
                if (item.Factory != factory || item.UnitDef == null)
                    continue;
                if (runs.Count > 0 && runs[runs.Count - 1].def == item.UnitDef)
                {
                    var last = runs[runs.Count - 1];
                    runs[runs.Count - 1] = (last.def, last.head, item, last.count + 1);
                }
                else
                    runs.Add((item.UnitDef, item, item, 1));
            }
            bool first = true;
            foreach (var run in runs)
            {
                UnitDefinition def = run.def;
                ProductionTasks.QueueItem head = run.head;
                ProductionTasks.QueueItem tail = run.tail;
                int count = run.count;
                bool headFirst = first;
                first = false;
                Tile(_strip2Content, new TileSpec
                {
                    Icon = IconOf(def),
                    Caption = Catalog.ShortLabel(def.code, def.unitName),
                    TypeBadge = TypeBadgeOf(def),
                    Badge = count > 1 ? "×" + count : null,
                    Progress = headFirst ? head.Progress01 : (float?)null,
                    OnLeft = () => ProductionTasks.InsertAfter(tail, RtsInput.Shift ? 5 : 1),
                    OnRight = () => ProductionTasks.RemoveTail(factory, def, RtsInput.Shift ? 5 : 1),
                    Def = def,
                });
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
                UnitDefinition def = u.definition;
                Tile(_strip2Content, new TileSpec
                {
                    Icon = def != null ? def.friendlyIcon : null,
                    Caption = def != null ? Catalog.ShortLabel(def.code, def.unitName) : null,
                    OnRight = () =>
                    {
                        var controller = CommanderModeController.Instance;
                        if (controller != null)
                            controller.Selection.Selected.Remove(u);
                    },
                    Def = def,
                });
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
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = def.friendlyIcon != null ? def.friendlyIcon : def.mapIcon,
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = BuildTasks.IsHangarDefinition(d) ? "АНГ" : null,
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => BuildTasks.Pending = d,
                        Def = def,
                    });
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
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueGround(depot, d),
                        Def = def,
                    });
                }
                return; // нижняя строка — очередь фабрики (RefreshTabs)
            }

            if (airbase != null && Encyclopedia.i != null && Encyclopedia.i.aircraft != null)
            {
                _contextTitle.text = "Аэродром — авиация (клик: в очередь; РЕЗ — выкат)" + tierName;
                // Резерв ангаров (вернувшиеся «на базу» борта): выкат бесплатный,
                // плитки ПЕРЕД каталогом, бейдж РЕЗ + счётчик.
                var reserve = AirbaseReserve.Of(airbase);
                if (reserve != null)
                    foreach (AirbaseReserve.Entry entry in reserve)
                    {
                        AircraftDefinition d = entry.Definition;
                        int count = entry.Count;
                        Tile(_strip1Content, new TileSpec
                        {
                            Icon = IconOf(d),
                            Caption = Catalog.ShortLabel(d.code, d.unitName),
                            TypeBadge = "РЕЗ",
                            Badge = "×" + count,
                            OnLeft = () => PurchaseService.DeployReserve(airbase, d),
                            Def = d,
                        });
                    }
                // Каталог = ЮНИОН списков живых ангаров аэродрома (нативный
                // GetAvailableAircraft): заказ типа, который этот аэродром не
                // строит, невозможен в принципе — иначе позиция нальётся деньгами
                // и зависнет без шансов на выпуск (нет ни одного подходящего ангара).
                var defs = new List<AircraftDefinition>();
                foreach (AircraftDefinition a in airbase.GetAvailableAircraft())
                    if (a != null)
                        defs.Add(a);
                if (defs.Count == 0)
                {
                    AddItemButton(_strip1Content, null, "нет работоспособных ангаров", null);
                    return;
                }
                Catalog.ApplyTierFilter(defs, d => d.value, _prodTier);
                foreach (AircraftDefinition def in defs)
                {
                    AircraftDefinition d = def;
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueAir(airbase, d),
                        Def = def,
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

        /// <summary>Позиция каталога с иконкой — плитка Tile с подписью-именем;
        /// без иконки (шаблоны, подсказки) — текстовая пилюля.</summary>
        private void AddItemButton(Transform parent, Sprite sprite, string label, System.Action onClick)
        {
            if (sprite != null)
            {
                Tile(parent, new TileSpec
                {
                    Icon = sprite,
                    Caption = TrimCaption(label),
                    OnLeft = onClick,
                });
                return;
            }
            AddTextPill(parent, label, onClick);
        }

        /// <summary>Подпись плитки-шаблона: имя без переносов, до 10 символов.</summary>
        private static string TrimCaption(string s)
        {
            if (string.IsNullOrEmpty(s))
                return null;
            s = s.Replace('\n', ' ');
            return s.Length <= 10 ? s : s.Substring(0, 9) + "…";
        }

        /// <summary>Текстовая пилюля — для шаблонов и подсказок (без иконки).</summary>
        private void AddTextPill(Transform parent, string label, System.Action onClick)
        {
            var go = new GameObject("item", typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.sizeDelta = new Vector2(178f, 30f);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 178f;
            le.preferredWidth = 178f;
            le.minHeight = 30f;
            le.preferredHeight = 30f;
            go.GetComponent<Image>().color = onClick != null
                ? UiKit.BtnBg
                : new Color(0.08f, 0.10f, 0.12f, 0.9f);
            var btn = go.GetComponent<Button>();
            if (onClick != null)
                btn.onClick.AddListener(() => onClick());

            var txt = Label(go.transform, "lbl", label, 12,
                onClick != null ? TextMain : TextDim, TextAnchor.MiddleLeft);
            var rtT = (RectTransform)txt.transform;
            rtT.anchorMin = new Vector2(0.04f, 0f);
            rtT.anchorMax = new Vector2(1f, 1f);
            rtT.offsetMin = rtT.offsetMax = Vector2.zero;
            txt.raycastTarget = false;
        }

        // ---------- hover-инфоокно плиток ----------

        /// <summary>Опрос плиток под курсором (каждый кадр, без uGUI-событий — игровой
        /// input-модуль Enter/Exit не доставляет): курсор внутри плитки И внутри её
        /// ленты-вьюпорта (замаскированные при прокрутке плитки не считаются).
        /// Снесённые плитки выпадают сами (Unity fake-null).</summary>
        private void UpdateTileHover()
        {
            if (!Settings.UiTooltips || RtsInput.Typing || _canvas == null)
            {
                HideTip();
                return;
            }
            UnitDefinition hit = null;
            for (int i = _tipTiles.Count - 1; i >= 0; i--)
            {
                var entry = _tipTiles[i];
                if (entry.tile == null)
                {
                    _tipTiles.RemoveAt(i);
                    continue;
                }
                if (hit != null || !entry.tile.gameObject.activeInHierarchy)
                    continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(entry.tile, Input.mousePosition, null) &&
                    (entry.viewport == null ||
                     RectTransformUtility.RectangleContainsScreenPoint(entry.viewport, Input.mousePosition, null)))
                    hit = entry.def;
            }
            if (hit != _hoverDef)
            {
                _hoverDef = hit;
                _hoverSince = Time.unscaledTime;
            }
            if (hit == null || Time.unscaledTime - _hoverSince < TipDelaySec)
            {
                HideTip();
                return;
            }
            ShowTip(hit);
        }

        private void HideTip()
        {
            if (_tipRoot != null && _tipRoot.gameObject.activeSelf)
                _tipRoot.gameObject.SetActive(false);
            _hoverDef = null;
        }

        /// <summary>Карточка определения у курсора: имя, класс/роль, цена, описание
        /// из энциклопедии игры (как панель свойств юнита, но по определению).</summary>
        private void ShowTip(UnitDefinition def)
        {
            BuildTipOnce();
            if (_tipRoot == null)
                return;
            if (_tipDef != def)
            {
                _tipDef = def;
                _tipTitle.text = def.unitName;
                _tipClass.text = ClassOfDefinition(def) + " · " + def.value.ToString("F0");
                string desc = def.description;
                if (desc != null && desc.Length > 260)
                    desc = desc.Substring(0, 259) + "…";
                _tipDesc.text = desc ?? "";
                _tipDesc.gameObject.SetActive(!string.IsNullOrEmpty(desc));
            }

            var canvasRt = (RectTransform)_canvas.transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRt, Input.mousePosition, null, out Vector2 p))
                return;
            Vector2 size = _tipRoot.sizeDelta;
            Rect cr = canvasRt.rect;
            // Пивот окна — левый-низ: держим карточку у курсора, у краёв — через курсор.
            float x = p.x + 16f;
            float y = p.y + 16f;
            if (x + size.x > cr.width * 0.5f)
                x = p.x - size.x - 16f;
            if (y + size.y > cr.height * 0.5f)
                y = p.y - size.y - 16f;
            _tipRoot.anchoredPosition = new Vector2(x, y);
            if (!_tipRoot.gameObject.activeSelf)
                _tipRoot.gameObject.SetActive(true);
        }

        private void BuildTipOnce()
        {
            if (_tipRoot != null || _canvas == null)
                return;
            try
            {
                var go = new GameObject("TileTip", typeof(RectTransform), typeof(Image));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_canvas.transform, false);
                rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
                rt.sizeDelta = new Vector2(330f, 122f);
                go.GetComponent<Image>().color = new Color(0.04f, 0.06f, 0.08f, 0.97f);
                _tipRoot = rt;

                _tipTitle = Label(rt, "t", "", 15, TextMain, TextAnchor.UpperLeft);
                var rtT = (RectTransform)_tipTitle.transform;
                rtT.anchorMin = new Vector2(0f, 1f);
                rtT.anchorMax = new Vector2(1f, 1f);
                rtT.pivot = new Vector2(0.5f, 1f);
                rtT.offsetMin = new Vector2(10f, -22f);
                rtT.offsetMax = new Vector2(-10f, -4f);
                _tipTitle.raycastTarget = false;

                _tipClass = Label(rt, "cls", "", 12, Accent, TextAnchor.UpperLeft);
                var rtC = (RectTransform)_tipClass.transform;
                rtC.anchorMin = new Vector2(0f, 1f);
                rtC.anchorMax = new Vector2(1f, 1f);
                rtC.pivot = new Vector2(0.5f, 1f);
                rtC.offsetMin = new Vector2(10f, -38f);
                rtC.offsetMax = new Vector2(-10f, -22f);
                _tipClass.raycastTarget = false;

                _tipDesc = Label(rt, "desc", "", 12, TextDim, TextAnchor.UpperLeft);
                var rtD = (RectTransform)_tipDesc.transform;
                rtD.anchorMin = new Vector2(0f, 1f);
                rtD.anchorMax = new Vector2(1f, 1f);
                rtD.pivot = new Vector2(0.5f, 1f);
                rtD.offsetMin = new Vector2(10f, -116f);
                rtD.offsetMax = new Vector2(-10f, -42f);
                _tipDesc.raycastTarget = false;

                rt.SetAsLastSibling();
                rt.gameObject.SetActive(false);
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: сбой построения hover-карточки — " + e.GetType().Name + ": " + e.Message);
                _tipRoot = null;
            }
        }

        /// <summary>Класс определения для карточки: тип наземки / роль авиации.</summary>
        private static string ClassOfDefinition(UnitDefinition def)
        {
            if (def is AircraftDefinition)
                return "авиация · " + Catalog.AirRoleName(def.roleIdentity, def.typeIdentity);
            if (def is VehicleDefinition vd)
                return Catalog.TypeNameOf(vd.vehicleType);
            return "здание";
        }

        private static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                Destroy(t.GetChild(i).gameObject);
        }
    }
}
