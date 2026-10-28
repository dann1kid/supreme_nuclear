using System;
using System.Collections.Generic;
using NuclearOption.SavedMission;
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

        // Панель свойств выделения (левая колонка над ростером). При наведении
        // на плитку закупки переключается на карточку определения (SupCom).
        private RectTransform _infoPanel;
        private Text _infoZoneHdr;   // «ВЫДЕЛЕНИЕ» / «ЗАКУПКА» (ховер плитки каталога)
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
        /// <summary>Индексы ракетных станций чипов залпа (параллельно кнопкам st_*,
        /// v0.92: чипы только ракетным станциям — бомбовым/пушечным залп нечем).</summary>
        private readonly List<int> _chipStations = new();
        /// <summary>Число рядов команд и высота секции гейтов последней сборки —
        /// для раскладки (3 — секции нет; 44 — классы; 68 — классы + чипы типов).</summary>
        private int _cmdRows;
        private float _cmdGateHeight;
        /// <summary>Полоса гейтов вооружения (внизу карточки, под сеткой команд):
        /// кнопка на каждый класс, присутствующий в выделении.</summary>
        private RectTransform _gateStrip;
        /// <summary>Ряд чипов ТИПОВ вооружения (v0.106): под полосой классов; чип
        /// циклит правило типа (ЛКМ — вперёд, колесо над чипом — туда-обратно).</summary>
        private RectTransform _gateTypes;
        /// <summary>Чипы типов последней сборки — рефреш подписи/цвета по агрегату
        /// выделения (как класс-гейты, «+/−/±» → суффикс «·Н/·В/·з/·в/×/±»).</summary>
        private readonly List<(Button btn, Image img, Text label, WeaponInfo wi)> _typeChips = new();
        /// <summary>Кэп чипов типов в один ряд (карточка 418 px, чип 64 px; хвост
        /// типов сверх кэпа не показывается — долг §12).</summary>
        private const int TypeChipCap = 6;
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
        private Button _noLandBtn;           // тумблеры аэродрома (DESIGN §2.11b):
        private Button _noTakeoffBtn;        // подсветка = закрыто (как Пауза = пауза)
        private VehicleDepot _ctxDepot;      // ПЕРВЫЕ фабрики своего типа в выделении
        private Building _ctxDock;           // (представители: шаблон «Шаблон+», лодауты)
        private Airbase _ctxAirbase;         // ...или аэродром
        private Hangar _ctxHangar;           // ...или первый ангар выделения
        /// <summary>ВСЕ фабрики выделения (депо + верфи + ангары): групповые
        /// Пауза/Цикл действуют на весь список, а не на представителя.</summary>
        private readonly List<object> _ctxFactories = new List<object>();
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

        // Наведение на плитку закупки (каталог/очередь/ростер): опрос плиток под
        // курсором — UpdateTileHover (без uGUI-событий: игровой input-модуль их
        // не доставляет, правило №18). Панель свойств выделения (лево-низ)
        // переключается на карточку ОПРЕДЕЛЕНИЯ МГНОВЕННО (v0.87.0 по плейтесту:
        // задержка 0.35 с рвала решение; панельная схема не мигает при проводе
        // курсора) — как в SupCom: имя,
        // класс/цена/время и ТТХ в самой панели, описание — в отдельном окне
        // «ОПИСАНИЕ» под ней. Плитки регистрируются в Tile.
        private UnitDefinition _hoverShow;     // что показано панелью (null = выделение)
        private readonly List<(RectTransform tile, RectTransform viewport, UnitDefinition def, bool wheel)> _tipTiles = new();
        private const int DefMaxWeaponRows = 8;  // строки вооружения в панели, дальше «+ ещё N»
        private const int DefMaxDescLines = 10;  // строки описания в окне «ОПИСАНИЕ»
        private const float DescTextWidth = 398f; // внутренняя ширина текста описания
        private const float DescLineH = 15f;     // строка переноса описания 12pt

        /// <summary>Окно «ОПИСАНИЕ» (второе окно карточки закупки, SupCom-раскладка):
        /// между панелью свойств и командной картой; виден только при наведении.</summary>
        private RectTransform _descPanel;
        private Text _descText;
        private float _descHeight;             // высота под контент (ApplyCommandLayout)

        /// <summary>Окно «ОСНАЩЕНИЕ» — редактор лодаута (СКМ по плитке каталога
        /// авиации): пресеты/топливо/станции с циклом, правки = пресет «Свой».
        /// Контент перестраивается целиком (клики/колесо — через LoadoutsVersion).</summary>
        private FloatingWindow _loWin;
        private AircraftDefinition _loDef;
        private int _loVersion = -1;

        /// <summary>Кэш рассчитанной карточки по определению (определения immutable,
        /// обход префаба — один раз; ключ включает индекс лодаута — колесо пересчитывает
        /// вооружение; словарь instance — переживает сцены честно).</summary>
        private readonly Dictionary<(UnitDefinition def, int loadout), TipContent> _tipContent = new();

        /// <summary>Рассчитанный контент карточки определения (SupCom-инфо):
        /// строки для панели свойств и окна описания — считается один раз.</summary>
        private sealed class TipContent
        {
            public string Title;
            public string ClassLine;   // класс/роль · цена · время
            public string LoadoutLine; // «лодаут: …» — только у авиации с выбором
            public readonly List<string> StatRows = new(); // ТТХ: факт-на-строку, без обрезок
            public readonly List<string> Weapons = new();
            public string Desc;        // подрезанная под DefMaxDescLines
            public int DescLines;
        }

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
            BuildSafe("defdesc", BuildDefDesc);
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
            RtsInput.WheelConsumedByUi = false;   // одноразовые флаги кадра (камера)
            RtsInput.MiddleConsumedByUi = false;
            EnsureUi(); // канвас мог быть уничтожен сменой сцены — пересобираем лениво
            if (_canvas == null)
                return;

            // Нижние панели — КОНТЕКТ выделения: без выделения всё низ экрана свободен
            // (экономика сверху остаётся всегда). Осмотр чужой цели/ракеты — контекст
            // ТОЛЬКО карточки свойств (разведкарточка): приказы чужим не выдаются,
            // командная карта и лента каталога при чистом осмотре не открываются.
            var ctxController = CommanderModeController.Instance;
            bool contextVisible = ctxController != null && ctxController.IsActive &&
                (ctxController.Selection.Selected.Count > 0 ||
                 ctxController.Selection.SelectedAirbase != null ||
                 ctxController.Selection.Inspected != null ||
                 BuildTasks.Pending != null ||
                 OrderTemplates.PendingTemplate.template != null ||
                 BuildTasks.SelectedTask != null);
            bool inspectOnly = contextVisible &&
                ctxController.Selection.Selected.Count == 0 &&
                ctxController.Selection.SelectedAirbase == null &&
                ctxController.Selection.Inspected != null &&
                BuildTasks.Pending == null &&
                OrderTemplates.PendingTemplate.template == null &&
                BuildTasks.SelectedTask == null;
            bool commandsVisible = contextVisible && !inspectOnly;
            if (_infoPanel != null && _infoPanel.gameObject.activeSelf != contextVisible)
                _infoPanel.gameObject.SetActive(contextVisible);
            if (_commandPanel != null && _commandPanel.gameObject.activeSelf != commandsVisible)
            {
                _commandPanel.gameObject.SetActive(commandsVisible);
                ApplyCommandLayout(); // стек левой колонки опирается на карточку приказов
            }
            if (_tabPanel != null && _tabPanel.gameObject.activeSelf != commandsVisible)
                _tabPanel.gameObject.SetActive(commandsVisible);

            // Каждый кадр (не по таймеру): наведение на плитку закупки без задержки
            // опроса; сами панели обновляются реже ниже (смена ховера форсит рефреш).
            if (ctxController != null && ctxController.IsActive)
            {
                UpdateTileHover();
                UpdateTypeChipWheel(); // колесо над чипом типа — цикл правила туда-обратно
            }
            else
                ResetHover();

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
            _typeChips.Clear();
            _cmdContextKey = -1; // кнопки пересоздадутся под текущий контекст выделения
            _contextHash = -1;
            _infoHash = -1;
            _inspHash = -1;
            _loggedActiveOnce = false;
            _fundsText = null;
            _incomeText = null;
            _extractorsText = null;
            _modeHint = null;
            _infoZoneHdr = null;
            _descPanel = null;
            _descText = null;
            _hoverShow = null;
            _tipTiles.Clear();

            BuildSafe("canvas", BuildCanvas);
            BuildSafe("topbar", BuildTopBar);
            BuildSafe("info", BuildSelectionInfo);
            BuildSafe("defdesc", BuildDefDesc);
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

            _incomeText = Label(bar, "income", Loc.Tr("+0/мин"), 17, TextMain, TextAnchor.MiddleLeft);
            var rtI = (RectTransform)_incomeText.transform;
            rtI.anchorMin = new Vector2(0.45f, 0.5f);
            rtI.anchorMax = new Vector2(1f, 1f);
            rtI.offsetMin = new Vector2(6f, 0f);
            rtI.offsetMax = new Vector2(-10f, 0f);

            _extractorsText = Label(bar, "extractors", Loc.Tr("экстракторы 0/0"), 15, TextDim, TextAnchor.MiddleLeft);
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

            var hdr = Label(panel, "hdr", Loc.Tr("ВЫДЕЛЕНИЕ"), 10, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)hdr.transform, 2f, 14f);
            _infoZoneHdr = hdr;
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

            _infoWeaponsHeader = Label(panel, "whdr", Loc.Tr("Вооружение"), 12, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoWeaponsHeader.transform, 166f, 180f);
            for (int i = 0; i < _infoWeapons.Length; i++)
            {
                _infoWeapons[i] = Label(panel, "w" + i, "", 12, TextMain, TextAnchor.UpperLeft, true);
                Row((RectTransform)_infoWeapons[i].transform, 180f + 16f * i, 196f + 16f * i);
            }
        }

        /// <summary>Окно «ОПИСАНИЕ» — второе окно карточки закупки (SupCom-раскладка):
        /// многострочный перенос текста определения; виден только при наведении на
        /// плитку закупки, высота под контент, позицию в стеке левой колонки
        /// задаёт ApplyCommandLayout (командная карта → ОПИСАНИЕ → ВЫДЕЛЕНИЕ).</summary>
        private void BuildDefDesc()
        {
            var panel = Panel("DefDesc", _canvas.transform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(12f, 140f), new Vector2(430f, 240f), PanelBg);
            _descPanel = panel;

            var hdr = Label(panel, "hdr", Loc.Tr("ОПИСАНИЕ"), 10, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)hdr.transform, 2f, 16f);

            // Единственный многострочный текст левой колонки: перенос по ширине,
            // по вертикали обрезается (длина уже подрезана расчётом строк).
            _descText = Label(panel, "desc", "", 12, TextDim, TextAnchor.UpperLeft);
            var rt = (RectTransform)_descText.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(12f, -200f);
            rt.offsetMax = new Vector2(-8f, -18f);
            _descText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _descText.verticalOverflow = VerticalWrapMode.Truncate;
            _descText.raycastTarget = false;

            panel.gameObject.SetActive(false);
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
            _ordersHdr = Label(panel, "ordershdr", Loc.Tr("ПРИКАЗЫ"), 10, TextDim, TextAnchor.UpperLeft, true);
            var rtO = (RectTransform)_ordersHdr.transform;
            rtO.anchorMin = new Vector2(0f, 1f);
            rtO.anchorMax = new Vector2(1f, 1f);
            rtO.offsetMin = new Vector2(10f, -31f);
            rtO.offsetMax = new Vector2(-8f, -19f);

            // Заголовок секции гейтов — виден только с полосой (управляет активностью
            // BuildCommandButtons); высота секции меняется (классы / классы+типы),
            // офсеты выставляет BuildCommandButtons.
            _gateHdr = Label(panel, "gatehdr", Loc.Tr("РАЗРЕШЁННОЕ ВООРУЖЕНИЕ"), 10, TextDim,
                TextAnchor.UpperLeft, true);
            var rtG = (RectTransform)_gateHdr.transform;
            rtG.anchorMin = new Vector2(0f, 0f);
            rtG.anchorMax = new Vector2(1f, 0f);
            rtG.offsetMin = new Vector2(10f, 51f);
            rtG.offsetMax = new Vector2(-8f, 64f);
            _gateHdr.gameObject.SetActive(false);

            // Секция гейтов вооружения — внизу карточки, два ряда: ТИПЫ (нижний,
            // v0.106) и КЛАССЫ; сетка команд поднимается над ними.
            _gateTypes = Panel("gatetypes", panel, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(8f, 3f), new Vector2(-8f, 25f), new Color(0f, 0f, 0f, 0f));
            var tlayout = _gateTypes.gameObject.AddComponent<HorizontalLayoutGroup>();
            tlayout.spacing = 4f;
            tlayout.childControlWidth = false;
            tlayout.childControlHeight = false;
            tlayout.childForceExpandWidth = false;
            tlayout.childForceExpandHeight = false;
            tlayout.childAlignment = TextAnchor.MiddleLeft;
            _gateTypes.gameObject.SetActive(false);

            _gateStrip = Panel("gates", panel, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(8f, 27f), new Vector2(-8f, 49f), new Color(0f, 0f, 0f, 0f));
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

            string[] glyphs = { "$", "#", "^", Loc.Tr("Ш") };
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
            string[] tiers = { Loc.Tr("Все"), "I", "II", "III" };
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
            // Кнопки 22 px с интервалом 3: до пяти в колонке (Пауза/Цикл/Шаблон+ +
            // тумблеры аэродрома с ангара) — 122 px, колонка до -144 (низ панели).
            var ctxGo = new GameObject("factoryCtx", typeof(RectTransform));
            var ctxRt = (RectTransform)ctxGo.transform;
            ctxRt.SetParent(panel, false);
            ctxRt.anchorMin = new Vector2(0f, 1f);
            ctxRt.anchorMax = new Vector2(0f, 1f);
            ctxRt.offsetMin = new Vector2(44f, -144f);
            ctxRt.offsetMax = new Vector2(108f, -22f);
            var ctxLayout = ctxGo.AddComponent<VerticalLayoutGroup>();
            ctxLayout.spacing = 3f;
            ctxLayout.childControlWidth = false;
            ctxLayout.childControlHeight = false;
            ctxLayout.childForceExpandWidth = false;
            ctxLayout.childForceExpandHeight = false;
            _pauseBtn = Button_(ctxGo.transform, "pause", Loc.Tr("Пауза"), () =>
            {
                ProductionTasks.TogglePauseAll(_ctxFactories);
            }, 10);
            ((RectTransform)_pauseBtn.transform).sizeDelta = new Vector2(60f, 22f);
            _pauseBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            _cycleBtn = Button_(ctxGo.transform, "cycle", Loc.Tr("Цикл"), () =>
            {
                ProductionTasks.ToggleCycleAll(_ctxFactories);
            }, 10);
            ((RectTransform)_cycleBtn.transform).sizeDelta = new Vector2(60f, 22f);
            _cycleBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            _tplBtn = Button_(ctxGo.transform, "tpl", Loc.Tr("Шаблон+"), () =>
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
                    ? Loc.F("Шаблон сохранён: {0}", tpl.name)
                    : Loc.Tr("Нечего сохранять (набор/очередь пусты)"));
            }, 10);
            ((RectTransform)_tplBtn.transform).sizeDelta = new Vector2(60f, 22f);
            _tplBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            _noLandBtn = Button_(ctxGo.transform, "noLand", Loc.Tr("Без посадки"), () =>
            {
                if (_ctxAirbase != null)
                    AirbaseAccess.SetLanding(_ctxAirbase, !AirbaseAccess.IsLandingOpen(_ctxAirbase));
            }, 9);
            ((RectTransform)_noLandBtn.transform).sizeDelta = new Vector2(62f, 22f);
            _noLandBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            _noTakeoffBtn = Button_(ctxGo.transform, "noTakeoff", Loc.Tr("Без взлёта"), () =>
            {
                if (_ctxAirbase != null)
                    AirbaseAccess.SetTakeoff(_ctxAirbase, !AirbaseAccess.IsTakeoffOpen(_ctxAirbase));
            }, 9);
            ((RectTransform)_noTakeoffBtn.transform).sizeDelta = new Vector2(62f, 22f);
            _noTakeoffBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            ctxGo.SetActive(false);
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: tabs step '" + step + "' — " + e.GetType().Name + ": " + e.Message);
                throw;
            }
        }

        /// <summary>Фабрика-представитель выделения (первая: верфь/депо/ангар/аэродром):
        /// очередь «Шаблон+» и аэродром редактора лодаутов. Пауза/Цикл идут мимо —
        /// группой по _ctxFactories.</summary>
        private object CtxFactory() =>
            _ctxDock != null ? (object)_ctxDock :
            _ctxDepot != null ? (object)_ctxDepot :
            _ctxHangar != null ? (object)_ctxHangar :
            (object)_ctxAirbase;

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
            _incomeText.text = Loc.F("+{0}/мин", rate.ToString("F0"));
            _incomeText.color = rate > 0f ? TextMain : WarnRed;
            if (EconomyService.Running)
                _extractorsText.text = Loc.F("экстракторы {0}/{1} · T1-3", EconomyService.OwnedExtractors(hq), EconomyService.TotalExtractors);
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

            _contextTitle.text = Loc.F("Выбрано: {0}", selected.Count);
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

            // По умолчанию — режим выделения (ветки ниже переопределяют строки):
            // заголовок зоны, видимость полосы HP (карточка закупки их прячет) и
            // цвет строки состояния (строка лодаута подсвечивается акцентом).
            if (_infoZoneHdr != null)
                _infoZoneHdr.text = Loc.Tr("ВЫДЕЛЕНИЕ");
            if (_infoHpFill != null && _infoHpFill.parent != null)
                _infoHpFill.parent.gameObject.SetActive(true);
            if (_infoHpTxt != null)
                _infoHpTxt.gameObject.SetActive(true);
            if (_infoState != null)
                _infoState.color = TextDim;
            if (_infoWeaponsHeader != null)
                _infoWeaponsHeader.text = Loc.Tr("Вооружение");
            UpdateDefDesc(null);

            // Наведение на плитку закупки (SupCom): панель показывает карточку
            // ОПРЕДЕЛЕНИЯ — приоритетнее выделения, пока курсор на плитке.
            if (_hoverShow != null)
            {
                RefreshDefInfo(_hoverShow);
                return;
            }

            // Выделенный НЕДОСТРОЙ: как объект в SupCom — имя, прогресс, строители.
            if (selected.Count == 0 && BuildTasks.SelectedTask != null)
            {
                BuildTasks.Task t = BuildTasks.SelectedTask;
                if (t.Definition == null)
                    return;
                _infoTitle.text = Util.FitLine(t.Definition.unitName, FitTitleChars);
                _infoClass.text = Util.FitLine(Loc.F("недострой · {0}/{1} · строителей {2}",
                t.Paid.ToString("F0"), t.Cost.ToString("F0"), t.Builders.Count), FitBodyChars);
                UiKit.SetProgress(_infoHpFill, t.Progress01);
                _infoHpTxt.text = t.Progress01.ToString("P0");
                _infoFuelTrack.gameObject.SetActive(false);
                _infoFuelTxt.gameObject.SetActive(false);
                _infoMove.text = t.ZoneBlocked ? Loc.Tr("зона не расчищена") : Loc.Tr("идёт стройка");
                _infoState.text = Loc.Tr("ПКМ инженерами — достройка");
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

        /// <summary>Карточка определения при наведении на плитку закупки (SupCom):
        /// шапка — имя, класс/роль · цена · время; строка ТТХ; строка лодаута
        /// (авиация с пресетами — выбор колесом, недоступный на текущем аэродроме
        /// подписан, заказ уйдёт «Стандартом»); вооружение выбранного лодаута;
        /// описание — в окне «ОПИСАНИЕ» под панелью. Контент определения посчитан
        /// один раз на пару (определение, лодаут) и лежит в кэше.</summary>
        private void RefreshDefInfo(UnitDefinition def)
        {
            var adef = def as AircraftDefinition;
            int loadIdx = adef != null ? ProductionTasks.LoadoutIndexOf(adef) : 0;
            TipContent c = GetTipContent(def, loadIdx);
            if (_infoZoneHdr != null)
                _infoZoneHdr.text = Loc.Tr("ЗАКУПКА");
            _infoTitle.text = c.Title;
            _infoClass.text = Util.FitLine(c.ClassLine, 60);
            _infoHpFill.parent.gameObject.SetActive(false);
            _infoHpTxt.gameObject.SetActive(false);
            _infoFuelTrack.gameObject.SetActive(false);
            _infoFuelTxt.gameObject.SetActive(false);
            // ТТХ — факт-на-строку (движение/строки заказов панели переиспользуются
            // под строки карточки; пустые гасятся поточной раскладкой).
            _infoMove.text = StatRowOf(c, 0);
            _infoOrder[0].text = StatRowOf(c, 1);
            _infoOrder[1].text = StatRowOf(c, 2);
            _infoOrder[2].text = "";
            string loLine = c.LoadoutLine;
            if (!string.IsNullOrEmpty(loLine) && adef != null && loadIdx > 0)
            {
                Loadout sel = ProductionTasks.SelectedLoadout(adef, out _);
                if (sel != null && GameManager.GetLocalHQ(out FactionHQ hq) &&
                    !ProductionTasks.LoadoutAllowed(adef, sel, hq))
                    loLine += Loc.Tr(" · недоступен");
            }
            _infoState.text = Util.FitLine(loLine, 60);
            // Строка лодаута — АКЦЕНТ: что выбирается колесом видно сразу,
            // без чтения серых строк (подсветка выбора, плейтест v0.89).
            if (_infoState != null)
                _infoState.color = string.IsNullOrEmpty(c.LoadoutLine) ? TextDim : Accent;
            for (int i = 0; i < _infoOrder.Length; i++)
                _infoOrder[i].text = "";
            _infoWeaponsHeader.gameObject.SetActive(c.Weapons.Count > 0);
            _infoWeaponsHeader.text = loadIdx > 0 ? Loc.Tr("Вооружение лодаута") : Loc.Tr("Вооружение");
            for (int i = 0; i < _infoWeapons.Length; i++)
            {
                bool show = i < c.Weapons.Count;
                _infoWeapons[i].gameObject.SetActive(show);
                if (show)
                    _infoWeapons[i].text = Util.FitLine(c.Weapons[i], 60);
            }
            UpdateDefDesc(c);
            FlowInfoBody(false);
        }

        /// <summary>Строка ТТХ карточки по номеру (пустая — за пределами фактов).</summary>
        private static string StatRowOf(TipContent c, int i) =>
            i < c.StatRows.Count ? c.StatRows[i] : "";

        /// <summary>Окно «ОПИСАНИЕ» под панелью свойств: c=null/без описания —
        /// скрыть. Высота окна под контент; позиции стека левой колонки
        /// пересчитываются сразу (ApplyCommandLayout).</summary>
        private void UpdateDefDesc(TipContent c)
        {
            if (_descPanel == null)
                return;
            bool show = c != null && c.DescLines > 0;
            if (_descPanel.gameObject.activeSelf != show)
                _descPanel.gameObject.SetActive(show);
            if (!show)
            {
                _descHeight = 0f;
                ApplyCommandLayout();
                return;
            }
            _descText.text = c.Desc;
            _descHeight = 18f + c.DescLines * DescLineH + 6f;
            var rt = (RectTransform)_descText.transform;
            rt.offsetMin = new Vector2(12f, -(18f + c.DescLines * DescLineH));
            ApplyCommandLayout();
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
            _infoHpTxt.text = hp >= 0.995f ? Loc.Tr("HP цел") : Loc.F("HP {0}", hp.ToString("P0"));

            float speedKmh = Mathf.Abs(u.speed) * 3.6f;
            if (u is Aircraft a)
            {
                float fuel = a.GetFuelLevel();
                UiKit.SetProgress(_infoFuelFill, fuel);
                _infoFuelTxt.text = Loc.F("топливо {0}", fuel.ToString("P0"));
                _infoMove.text = Util.FitLine(Loc.F("{0} км/ч · {1} м", speedKmh.ToString("F0"), a.radarAlt.ToString("F0")), FitBodyChars);
                _infoState.text = Util.FitLine(a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null
                    ? Catalog.RuPilotState(a.pilots[0].GetCurrentState())
                    : Loc.Tr("нет пилота"), FitBodyChars);
            }
            else
            {
                _infoMove.text = Loc.F("{0} км/ч", speedKmh.ToString("F0"));
                _infoState.text = Util.FitLine(
                    u is Ship s && s.TryGetComponent(out ShipAI sai)
                        ? Catalog.RuShipState(sai.state.ToString())
                        : "", FitBodyChars);
            }

            _infoOrder[0].text = Util.FitLine(Loc.F("Приказ: {0}", PlanSummary(u)), FitBodyChars);
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";
            _infoWeaponsHeader.text = Loc.Tr("Вооружение");

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
                    // Гейт держит станцию «неготовой» при полном боезапасе —
                    // это не «пусто»; «пусто» — только расстрелянный боезапас.
                    string status = !WeaponGateService.IsAllowed(u, ws) ? Loc.Tr(" · выкл")
                        : ws.Reloading ? Loc.Tr(" · перезарядка")
                        : (ws.Ammo > 0 ? "" : Loc.Tr(" · пусто"));
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
            _infoTitle.text = Loc.F("Выбрано: {0}", selected.Count);
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
            _infoClass.text = Loc.F("авиация {0} · наземка {1} · флот {2}", air, ground, naval);
            float hp = n > 0 ? hpSum / n : 1f;
            UiKit.SetProgress(_infoHpFill, hp);
            _infoHpTxt.text = hp >= 0.995f ? Loc.Tr("HP целы") : Loc.F("HP {0}", hp.ToString("P0"));
            // GetAmmoLevel — доли, но у энергостанций Full<Ammo (заряд-единицы) —
            // среднее может быть >1; клэмп, чтобы не показывать «6 666%».
            _infoMove.text = ammoN > 0 ? Loc.F("боезапас {0}", Mathf.Clamp01(ammoSum / ammoN).ToString("P0")) : "";
            _infoState.text = "";
            _infoOrder[0].text = "";
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";

            // Сводка вооружения: чем группа может работать по целям (домен-тег) и
            // сколько зарядов осталось; пустые типы видны сразу — сигнал на перезарядку.
            _infoWeaponsHeader.text = Loc.Tr("Вооружение группы");
            var rows = _groupWeapons.Rows;
            bool overflow = rows.Count > _infoWeapons.Length;
            int shown = overflow ? _infoWeapons.Length - 1 : rows.Count;
            for (int i = 0; i < shown; i++)
                _infoWeapons[i].text = Util.FitLine(Catalog.FormatWeaponRow(rows[i]), FitBodyChars);
            if (overflow)
                _infoWeapons[_infoWeapons.Length - 1].text =
                    Loc.F("+ ещё {0} {1}", rows.Count - shown,
                        Loc.Plural(rows.Count - shown, "тип", "типа", "типов"));
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
                (defName.Length > 0 ? defName + " · " : "") + ClassOf(u) + Loc.Tr(" · чужой контакт"),
                FitBodyChars);

            if (live)
            {
                float hp = GameApi.HpFraction(u);
                UiKit.SetProgress(_infoHpFill, hp);
                _infoHpTxt.text = hp >= 0.995f ? Loc.Tr("HP цел") : Loc.F("HP {0}", hp.ToString("P0"));
                float speedKmh = Mathf.Abs(u.speed) * 3.6f;
                _infoMove.text = Util.FitLine(u is Aircraft
                    ? Loc.F("{0} км/ч · {1} м", speedKmh.ToString("F0"), u.radarAlt.ToString("F0"))
                    : Loc.F("{0} км/ч", speedKmh.ToString("F0")), FitBodyChars);
                _infoState.text = Loc.Tr("под наблюдением");
            }
            else
            {
                _infoMove.text = "";
                _infoState.text = Util.FitLine(Loc.F("контакт {0} с назад", age.ToString("F0")), FitBodyChars);
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
                string.IsNullOrEmpty(name) ? (powered ? Loc.Tr("ракета") : Loc.Tr("бомба")) : name, FitTitleChars);
            _infoClass.text = (powered ? Loc.Tr("ракета") : Loc.Tr("бомба")) + (ours ? Loc.Tr(" · наша") : Loc.Tr(" · чужая"));

            float speedKmh = Mathf.Abs(m.speed) * 3.6f;
            _infoMove.text = Util.FitLine(Loc.F("{0} км/ч · {1} м", speedKmh.ToString("F0"),
                m.transform.position.GlobalY().ToString("F0")), FitBodyChars);
            _infoState.text = powered
                ? (m.EngineOn() ? Loc.Tr("двигатель: работает") : Loc.Tr("двигатель: выгорел"))
                : Loc.Tr("свободное падение");

            // ГСН/БЧ — параметры определения (не секрет); цель — только своя (RWR).
            int w = 0;
            string seeker = m.GetSeekerType();
            if (!string.IsNullOrEmpty(seeker))
                _infoOrder[w++].text = Util.FitLine(Loc.F("ГСН: {0}", seeker), FitBodyChars);
            float yieldKg = m.GetYield();
            if (yieldKg > 0f)
                _infoOrder[w++].text = Util.FitLine(Loc.F("БЧ: {0}", FormatYield(yieldKg)), FitBodyChars);
            if (UnitRegistry.TryGetUnit(m.targetID, out Unit tgt) && tgt != null &&
                tgt.NetworkHQ != null && tgt.NetworkHQ == hq)
                _infoOrder[w++].text = Util.FitLine(Loc.F("ЦЕЛЬ: {0}",
                    tgt.definition != null ? tgt.definition.unitName : tgt.name), FitBodyChars);
            for (; w < _infoOrder.Length; w++)
                _infoOrder[w].text = "";
        }

        /// <summary>Масса БЧ в читаемом виде: до тонны — кг, дальше — килотонны.</summary>
        private static string FormatYield(float kg) =>
            kg >= 1e6f
                ? Loc.F("{0} кт", (kg / 1e6f).ToString("F1"))
                : Loc.F("{0} кг", kg.ToString("F0"));

        private static string ClassOf(Unit u)
        {
            if (u is Aircraft) return Loc.Tr("авиация");
            if (u is Ship) return Loc.Tr("флот");
            if (u is Building) return Loc.Tr("здание");
            if (u.definition is VehicleDefinition vd) return vd.vehicleType.ToString();
            return "";
        }

        /// <summary>Краткое описание активного приказа мода: сегменты плана авиации / цепочка наземки.</summary>
        private static string PlanSummary(Unit u)
        {
            if (u is Aircraft && AircraftOrders.TryGetPlan(u, out IReadOnlyList<AircraftOrders.OrderSegment> plan))
            {
                if (plan.Count == 0)
                    return Loc.Tr("нет");
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < plan.Count && i < 3; i++)
                {
                    if (i > 0)
                        sb.Append(" → ");
                    AircraftOrders.OrderSegment seg = plan[i];
                    sb.Append(seg.Mode switch
                    {
                        AirOrderMode.Patrol => Loc.Tr("Патруль"),
                        AirOrderMode.AttackMove => Loc.Tr("Атака-движ."),
                        AirOrderMode.Attack => Loc.Tr("Атака"),
                        AirOrderMode.Salvo => Loc.Tr("Залп"),
                        AirOrderMode.Escort => Loc.Tr("Сопровождение"),
                        AirOrderMode.Rtb => Loc.Tr("На базу"),
                        _ => Loc.Tr("Транзит")
                    });
                    if (seg.Mode != AirOrderMode.Attack && seg.Points.Count > 1)
                        sb.Append(" ×").Append(seg.Points.Count);
                }
                return sb.ToString();
            }
            if (GroundOrders.KindOf(u) == GroundOrderKind.Attack)
                return Loc.Tr("атака цели");
            if (GroundOrders.KindOf(u) == GroundOrderKind.Escort)
            {
                Unit escortTarget = GroundOrders.EscortTargetOf(u);
                return Loc.F("сопровождение: {0}",
                    escortTarget != null && escortTarget.definition != null
                        ? escortTarget.definition.unitName
                        : "?");
            }
            if (GroundOrders.TryGetQueue(u, out IReadOnlyList<GlobalPosition> queue, out int idx))
            {
                int left = Mathf.Max(0, queue.Count - idx);
                var targets = GroundOrders.TargetQueueOf(u);
                string tail = targets != null ? Loc.F(", атак: {0} в очереди", targets.Count) : "";
                if (GroundOrders.KindOf(u) == GroundOrderKind.Patrol)
                    return Loc.F("патруль, {0} тчк (цикл){1}", left, tail);
                if (GroundOrders.KindOf(u) == GroundOrderKind.AttackMove)
                    return Loc.F("атака-движ., осталось {0} тчк{1}", left, tail);
                return Loc.F("цепочка, осталось {0} тчк{1}", left, tail);
            }
            return Loc.Tr("нет");
        }

        /// <summary>Ключ контекста командной карты: состав выделения по типам войск —
        /// при смене ключа кнопки перестраиваются (контекстные ряды авиации/наземки).</summary>
        private int _cmdContextKey = -1;

        private void RefreshCommands()
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;

            // Контекст по ТИПАМ боевых единиц (v0.92): ряды показываются только
            // тем, к кому применимы. Скорость — самолётам (у вертолётов газ ведёт
            // игровой hover-контур); «Залп» и чипы станций — ракетоносцам
            // (бомбовозу/пулемётному залп нечем); статичные здания без мобильных
            // в выделении — только hold-fire; пустое выделение — базовый набор
            // без авиарядов.
            bool hasAir = false;
            bool hasPlane = false;
            bool hasMissileAir = false;
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
                    if (!GameApi.IsHeloFamily(a))
                        hasPlane = true;
                    if (HasMissileStation(a))
                    {
                        hasMissileAir = true;
                        if (selCount == 1)
                            singleAir = a; // чипы станций — одиночному ракетоносцу
                    }
                }
                else if (unit is GroundVehicle || unit is Ship)
                    hasGround = true;
            }
            if (selCount != 1)
                singleAir = null;
            bool showAir = hasAir;
            bool buildingsOnly = selCount > 0 && !hasAir && !hasGround;
            WeaponClass gateClasses = WeaponGateService.ClassesOf(controller.Selection.Selected);
            // Состав типов чипов входит в ключ пересборки (как gateClasses): смена
            // набора вооружения выделения перестраивает ряд чипов.
            int typeKey = 0;
            foreach (WeaponInfo wi in WeaponGateService.TypesOf(controller.Selection.Selected, TypeChipCap))
                typeKey = typeKey * 31 + wi.GetInstanceID();
            int key = (showAir ? 1 : 0) | (hasGround ? 2 : 0) | (hasPlane ? 4 : 0) |
                      (hasMissileAir ? 8 : 0) | (singleAir != null ? 16 : 0) |
                      (buildingsOnly ? 32 : 0) | ((int)gateClasses << 6) | (typeKey << 12);
            if (key != _cmdContextKey)
            {
                _cmdContextKey = key;
                BuildCommandButtons(controller, showAir, hasPlane, hasMissileAir, singleAir, buildingsOnly);
            }

            // Подсказка — ТОЛЬКО в активном спец-режиме (стройка/залп/веер/приказ):
            // в покое её нет, верх карточки — заголовок «ПРИКАЗЫ» (без пустой строки).
            string pending = OrderModes.Pending != OrderMode.None && OrderModes.Pending != OrderMode.Move
                ? OrderModes.Label(OrderModes.Pending)
                : null;
            if (BuildTasks.Pending != null)
                _modeHint.text = Util.FitLine(
                    Loc.F("{0}: ЛКМ — площадка, удержание — поворот (Shift — серия) · ПКМ/Esc — отмена",
                            BuildTasks.Pending.unitName), 62);
            else if (BuildTasks.PendingRunway)
                _modeHint.text = Util.FitLine(
                    Loc.F("ВПП: ЛКМ — торец, ПРОТЯНУТЬ — второй ({0}–{1} м) · ПКМ/Esc — отмена",
                            Settings.RunwayMinM.ToString("F0"), Settings.RunwayMaxM.ToString("F0")), 62);
            else if (BuildTasks.PendingDock)
                _modeHint.text = Util.FitLine(
                    Loc.F("Верфь: ЛКМ по воде (глубже {0} м) — платформа, удержание — поворот · ПКМ/Esc — отмена",
                            Settings.DockMinDepthM.ToString("F0")), 62);
            else if (pending == Loc.Tr("Залп"))
                _modeHint.text = Util.FitLine(
                    Loc.Tr("Залп: ПКМ по цели/земле — залповый пуск · Shift+ПКМ по целям — доб. в залп"), 62);
            else if (pending == Loc.Tr("Атака веером"))
                _modeHint.text = Util.FitLine(
                    Loc.Tr("Веер: ПКМ по целям — накопить · ПКМ по земле — раздать группу"), 62);
            else if (pending != null)
                _modeHint.text = Util.FitLine(
                    Loc.F("{0}: ПКМ — вейпоинт (Shift — серия) · Esc — движение", pending), 62);
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
                HighlightCommand("cmd_tf0", AircraftOrders.TerrainHug == TerrainHugMode.Profile);
                HighlightCommand("cmd_tf1", AircraftOrders.TerrainHug == TerrainHugMode.Always);
                HighlightCommand("cmd_tf2", AircraftOrders.TerrainHug == TerrainHugMode.Never);
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

            // Чипы ТИПОВ (v0.106): подпись «имя + суффикс состояния», цвет как у
            // классов (зелёный без правил, красный выкл, жёлтый ограничение/вперемешку).
            foreach (var (chipBtn, chipImg, chipLabel, chipWi) in _typeChips)
            {
                if (chipBtn == null)
                    continue; // уничтожен пересборкой — список почистит сборка
                TypeGateState? state = WeaponGateService.TypeStateOf(controller.Selection.Selected, chipWi);
                chipImg.color = state == TypeGateState.None ? BtnActive
                    : state == TypeGateState.Off ? WarnRed : WarnYellow;
                chipLabel.text = TypeChipName(chipWi) + Catalog.TypeGateSuffix(state);
            }

            // Чипы станций: боезапас живой, выбранность — подсветкой. Кнопка st_i
            // соответствует _chipStations[i]-й станции борта (только ракетные).
            if (singleAir != null)
            {
                int chip = 0;
                foreach (var (btn, img, _, label) in _cmdButtons)
                {
                    if (!btn.name.StartsWith("st_"))
                        continue;
                    if (chip >= _chipStations.Count)
                        break;
                    int stIdx = _chipStations[chip];
                    WeaponStation st = singleAir.weaponStations[stIdx];
                    if (st != null && st.WeaponInfo != null)
                    {
                        label.text = StationChipLabel(st);
                        img.color = SalvoService.StationEnabled(singleAir, stIdx) ? BtnActive : BtnBg;
                    }
                    chip++;
                }
            }
        }

        /// <summary>Есть ли у борта ракетная станция (без учёта боезапаса): залп V
        /// и чипы станций — только ракетоносцам; бомбовозу/пулемётному залп
        /// нечем (SalvoService завершит сегмент сразу — долг §12-21).</summary>
        private static bool HasMissileStation(Aircraft a)
        {
            foreach (WeaponStation st in a.weaponStations)
                if (st != null && !st.Cargo && st.WeaponInfo != null && st.WeaponInfo.missile)
                    return true;
            return false;
        }

        /// <summary>Подпись чипа станции: короткое имя вооружения ×боезапас.</summary>
        private static string StationChipLabel(WeaponStation st)
        {
            string name = !string.IsNullOrEmpty(st.WeaponInfo.shortName)
                ? st.WeaponInfo.shortName
                : st.WeaponInfo.weaponName;
            if (string.IsNullOrEmpty(name))
                name = Loc.Tr("ст.");
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

        /// <summary>Контекстная пересборка сетки команд (состав выделения сменился,
        /// v0.92 — по типам боевых единиц): базовые приказы — мобильным (авиация/
        /// наземка/флот), ряды авиации — при авиации, скорость — только при
        /// самолётах, залп — ракетоносцам, чипы станций — одиночному ракетоносцу,
        /// статичным зданиям — только hold-fire. Карточка растёт вверх, панель
        /// свойств выделения поднимается за ней.</summary>
        private void BuildCommandButtons(CommanderModeController controller, bool showAir,
            bool hasPlane, bool hasMissileAir, Aircraft singleAir, bool buildingsOnly)
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
            _chipStations.Clear();
            _typeChips.Clear();

            var defs = new List<(string name, string label, System.Action a)>();
            if (!buildingsOnly)
            {
                // Базовые приказы — всем мобильным (авиация/наземка/флот).
                defs.Add(("cmd_move", Loc.Tr("Движение (M)"), () => OrderModes.Pending = OrderMode.Move));
                defs.Add(("cmd_amove", Loc.Tr("Атака-движ (A)"), () => OrderModes.Pending = OrderMode.AttackMove));
                // Патруль — авиации и наземке/флоту (v0.95: циклы; инженеры в патруле
                // сами подхватывают близлежащие недострои).
                defs.Add(("cmd_patrol", Loc.Tr("Патруль (U)"), () => OrderModes.Pending = OrderMode.Patrol));
                defs.Add(("cmd_attack", Loc.Tr("Атака (T)"), () => OrderModes.Pending = OrderMode.Attack));
                if (showAir && hasMissileAir)
                    defs.Add(("cmd_salvo", Loc.Tr("Залп (V)"), () => OrderModes.Pending = OrderMode.Salvo));
                defs.Add(("cmd_fan", Loc.Tr("Веер (R)"), () => controller.MoveOrders.ChargeFanAttack()));
                defs.Add(("cmd_stop", Loc.Tr("Стоп (S)"), () => controller.MoveOrders.CancelAll(controller.Selection.Selected)));
                defs.Add(("cmd_disperse", Loc.Tr("Распределить (Alt+Shift+R)"), () =>
                    controller.MoveOrders.IssueDisperseMove(controller.Selection.Selected)));
                if (showAir)
                    defs.Add(("cmd_rtb", Loc.Tr("На базу (G)"), () =>
                        controller.MoveOrders.IssueReturnToBase(controller.Selection.Selected)));
            }
            // Hold-fire — всем, у кого есть оружие: подвижным и статичным зданиям
            // (для выделения из одних зданий это единственная команда).
            defs.Add(("cmd_hold", Loc.Tr("Огонь: удерж (H)"), () =>
            {
                foreach (Unit unit in controller.Selection.Selected)
                    StanceService.Toggle(unit);
            }));
            if (showAir)
            {
                defs.Add(("cmd_vlow", Loc.Tr("Свер-хнизко 75м"), () => controller.SetProfileUi(0)));
                defs.Add(("cmd_low", Loc.Tr("Низко 150м"), () => controller.SetProfileUi(1)));
                defs.Add(("cmd_med", Loc.Tr("Средне 800м"), () => controller.SetProfileUi(2)));
                defs.Add(("cmd_high", Loc.Tr("Высоко 2500м"), () => controller.SetProfileUi(3)));
                if (hasPlane) // вертолётам тротл не назначается — газ ведёт игра
                {
                    defs.Add(("cmd_spd0", Loc.Tr("Полный (Shift+1)"), () => controller.SetSpeedUi(0)));
                    defs.Add(("cmd_spd1", Loc.Tr("Крейсер (Shift+2)"), () => controller.SetSpeedUi(1)));
                    defs.Add(("cmd_spd2", Loc.Tr("Эконом (Shift+3)"), () => controller.SetSpeedUi(2)));
                    // «Вдоль рельефа» (v0.94): вертолётам руление огибает всегда —
                    // тоггл показываем только при самолётах.
                    defs.Add(("cmd_tf0", Loc.Tr("Рельеф: авто (Alt+T)"), () => controller.SetTerrainFollowUi(0)));
                    defs.Add(("cmd_tf1", Loc.Tr("Рельеф: огибать"), () => controller.SetTerrainFollowUi(1)));
                    defs.Add(("cmd_tf2", Loc.Tr("Рельеф: напрямую"), () => controller.SetTerrainFollowUi(2)));
                }
                defs.Add(("cmd_flare", Loc.Tr("Ловушки! (X)"), () =>
                {
                    int popped = DefenseService.PopNow(controller.Selection.Selected);
                    CommanderHud.Notice(popped > 0 ? Loc.F("Ловушки: бортов — {0}", popped) : Loc.Tr("Нет бортов с контрмерами"));
                }));
                defs.Add(("cmd_def0", Loc.Tr("Защита: Авто"), () => controller.SetDefenseUi(0)));
                defs.Add(("cmd_def1", Loc.Tr("Защита: Ловушки"), () => controller.SetDefenseUi(1)));
                defs.Add(("cmd_def2", Loc.Tr("Защита: Выкл"), () => controller.SetDefenseUi(2)));
            }

            // Чипы ракетных станций залпа (одиночный ракетоносец): ЛКМ — вкл/выкл
            // станции; индексы — реальные позиции в weaponStations (для обновления
            // подписи/подсветки в RefreshCommands).
            if (singleAir != null)
                for (int idx = 0; idx < singleAir.weaponStations.Count && _chipStations.Count < 12; idx++)
                {
                    WeaponStation st = singleAir.weaponStations[idx];
                    if (st == null || st.Cargo || st.WeaponInfo == null || !st.WeaponInfo.missile)
                        continue;
                    int stationIdx = idx;
                    defs.Add(("st_" + _chipStations.Count, StationChipLabel(st),
                        () => SalvoService.ToggleStation(singleAir, stationIdx)));
                    _chipStations.Add(idx);
                }

            int rows = Mathf.CeilToInt(defs.Count / 4f);
            foreach (var (name, label, action) in defs)
            {
                Button b = Button_(grid, name, label, action, 11);
                _cmdButtons.Add((b, b.GetComponent<Image>(), action, b.GetComponentInChildren<Text>()));
            }

            // Гейты вооружения (DESIGN §2.11), два ряда внизу карточки: КЛАССЫ —
            // тоггл всему выделению (гейт главнее явных приказов); ТИПЫ (v0.106) —
            // чип на каждый тип вооружения выделения (кэп TypeChipCap, по убыванию
            // полного боезапаса), ЛКМ циклит правило, колесо — туда-обратно.
            int gates = 0;
            int types = 0;
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
                if (_gateTypes != null)
                {
                    foreach (WeaponInfo wi in WeaponGateService.TypesOf(
                                 controller.Selection.Selected, TypeChipCap))
                    {
                        WeaponInfo w = wi;
                        Button b = Button_(_gateTypes, "typechip_" + w.GetInstanceID(),
                            TypeChipName(w), () => CycleTypeGate(controller, w, true), 10);
                        ((RectTransform)b.transform).sizeDelta = new Vector2(64f, 22f);
                        b.gameObject.AddComponent<LayoutElement>().minWidth = 64f;
                        _cmdButtons.Add((b, b.GetComponent<Image>(), null, b.GetComponentInChildren<Text>()));
                        _typeChips.Add((b, b.GetComponent<Image>(), b.GetComponentInChildren<Text>(), w));
                        types++;
                    }
                    _gateTypes.gameObject.SetActive(types > 0);
                }
            }
            // Раскладка секции (снизу вверх): типы 3..25, классы 27..49, заголовок
            // 51..64; сетка команд — над секцией; высота панели — в ApplyCommandLayout.
            _gateStrip.gameObject.SetActive(gates > 0);
            if (_gateHdr != null)
                _gateHdr.gameObject.SetActive(gates > 0);
            var gridRt = (RectTransform)grid;
            gridRt.offsetMin = new Vector2(8f, gates > 0 ? (types > 0 ? 67f : 43f) : 3f);
            _cmdRows = rows;
            _cmdGateHeight = gates > 0 ? (types > 0 ? 68f : 44f) : 3f;
            ApplyCommandLayout();
        }

        /// <summary>Раскладка командной карточки по последней сборке кнопок и наличию
        /// подсказки активного режима: без подсказки заголовок «ПРИКАЗЫ» у верхнего
        /// края (никаких пустых строк), с подсказкой — под ней. Панель свойств
        /// выделения поднимается за карточкой (низ = верх карточки + зазор 6);
        /// скрытая карточка (чистый осмотр) места в стеке не занимает.</summary>
        private void ApplyCommandLayout()
        {
            // Стек левой колонки (снизу вверх): командная карта → ОПИСАНИЕ (ховер
            // плитки закупки) → панель свойств. Панель свойств всегда опирается
            // на верх того, что под ней; без командной карты — на низ экрана.
            float above = 12f;
            if (_commandPanel != null && _commandPanel.gameObject.activeSelf)
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
                               Mathf.Max(0, _cmdRows - 1) * 4f + 5f + _cmdGateHeight;
                _commandPanel.offsetMax = new Vector2(430f, 12f + height);
                above = 12f + height + 6f;
            }
            if (_descPanel != null && _descPanel.gameObject.activeSelf)
            {
                _descPanel.offsetMin = new Vector2(12f, above);
                _descPanel.offsetMax = new Vector2(430f, above + _descHeight);
                above += _descHeight + 6f;
            }
            if (_infoPanel != null)
                _infoPanel.offsetMin = new Vector2(12f, above);
        }

        // ---------- чипы ТИПОВ вооружения (v0.106, DESIGN §2.11) ----------

        /// <summary>Подпись чипа типа: shortName до 7 символов (влезает в 64 px
        /// при 10 кегле); пустое имя — «ст.».</summary>
        private static string TypeChipName(WeaponInfo wi)
        {
            string name = !string.IsNullOrEmpty(wi.shortName) ? wi.shortName : wi.weaponName;
            if (string.IsNullOrEmpty(name))
                name = Loc.Tr("ст.");
            return name.Length > 7 ? name.Substring(0, 7) : name;
        }

        /// <summary>Цикл правила типа по выделению (ЛКМ — вперёд, колесо — в обе
        /// стороны): применяется всем юнитам выделения, у которых тип есть; Notice
        /// называет выбранное состояние, карточка рефрешится немедленно.</summary>
        private void CycleTypeGate(CommanderModeController controller, WeaponInfo wi, bool forward)
        {
            TypeGateState state =
                Catalog.Next(WeaponGateService.TypeStateOf(controller.Selection.Selected, wi), forward);
            WeaponGateService.ApplyTypeState(controller.Selection.Selected, wi, state);
            // имя оружия — данные игры (не переводится), клей «: » — не фраза
            CommanderHud.Notice(TypeChipName(wi) + ": " + Catalog.TypeGateLabel(state));
            _nextRefresh = 0f;
        }

        /// <summary>Колесо над чипом типа — цикл правила туда-обратно (как колесо
        /// лодаутов над плиткой каталога): скан прямоугольников чипов каждый кадр
        /// с прокруткой, колесо УПОТРЕБЛЕНО UI — камера в этом кадре не зумит.</summary>
        private void UpdateTypeChipWheel()
        {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel == 0f || RtsInput.Typing || _typeChips.Count == 0)
                return;
            var controller = CommanderModeController.Instance;
            if (controller == null)
                return;
            for (int i = _typeChips.Count - 1; i >= 0; i--)
            {
                var chip = _typeChips[i];
                if (chip.btn == null)
                {
                    _typeChips.RemoveAt(i); // уничтожен пересборкой карточки
                    continue;
                }
                if (!chip.btn.gameObject.activeInHierarchy)
                    continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(
                        (RectTransform)chip.btn.transform, Input.mousePosition, null))
                    continue;
                CycleTypeGate(controller, chip.wi, wheel > 0f);
                RtsInput.WheelConsumedByUi = true;
                return; // один чип за прокрутку
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
            Building navalDock = null;
            Unit extractor = null;
            bool hasEngineer = false;
            Airbase carrier = null; // корабль-аэродром (авианосец/LHA): Airbase —
            // компонент корпуса (Ship.GetAirbase), полосы/точки едут вместе с ним
            var hangars = new List<Hangar>();
            // Все фабрики выделения (депо/верфь/ангар) — групповые Пауза/Цикл и хэш
            // очередей; представители выше — ПЕРВЫЕ своего типа (детерминизм).
            _ctxFactories.Clear();
            int selHash = 17;
            foreach (Unit unit in selected)
            {
                selHash = selHash * 31 + (unit != null ? unit.GetInstanceID() : 0);
                if (BuildTasks.IsDock(unit))
                {
                    if (navalDock == null) // Unity fake-null: мёртвую верфь заменяет живая
                        navalDock = unit as Building; // корпус-депо верфи — ФЛОТ
                    _ctxFactories.Add(unit);
                }
                else if (unit is VehicleDepot d)
                {
                    if (depot == null)
                        depot = d;
                    _ctxFactories.Add(d);
                }
                else if (unit is Building b && b.GetComponent<Hangar>() != null)
                {
                    hangars.Add(b.GetComponent<Hangar>());
                    _ctxFactories.Add(b.GetComponent<Hangar>());
                }
                else if (EconomyService.IsExtractor(unit) && extractor == null)
                    extractor = unit;
                else if (!hasEngineer && GameApi.IsEngineer(unit))
                    hasEngineer = true;
                else if (carrier == null && unit is Ship)
                {
                    Airbase shipBase = unit.GetAirbase(); // не-авианосцу вернёт null
                    if (shipBase != null)
                        carrier = shipBase;
                }
            }
            if (airbase != null)
                selHash = selHash * 31 + airbase.GetInstanceID();

            // Выделенный недострой открывает свою карточку на «Производстве».
            if (BuildTasks.SelectedTask != null && selected.Count == 0 && depot == null &&
                navalDock == null && airbase == null && hangars.Count == 0)
                _activeTab = TabProduction;

            // Автопереключение вкладки под выделение; ручной клик игрока уважаем,
            // пока выделение не сменилось (_manualTabForSel).
            if (_manualTabForSel != selHash)
            {
                if (depot != null || navalDock != null || airbase != null ||
                    hangars.Count > 0 || hasEngineer || carrier != null)
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
                       (airbase != null ? 2 : 0) + (hasEngineer ? 4 : 0) +
                       (navalDock != null ? 8 : 0) + (carrier != null ? 16 : 0);
                if (airbase != null)
                    hash = hash * 31 + AirbaseReserve.Version; // плитки резерва ангара
                // смена лодаута колесом — перестройка каталога (суффикс «·ЛN» на плитке)
                hash = hash * 31 + ProductionTasks.LoadoutsVersion;
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
            // Очереди ВСЕХ фабрик выделения: мультивыделение обновляет строку при
            // изменении очереди любой из них (раньше хэшировалась одна — последняя).
            foreach (object factory in _ctxFactories)
                queueKey = queueKey * 31 + ProductionTasks.KeyFor(factory) + 13;
            bool queueShown = _activeTab == TabProduction || _activeTab == TabTemplates;
            if (queueShown && queueKey != _queueHash)
            {
                _queueHash = queueKey;
                if (hasEngineer)
                    FillBuildQueue(controller);
                else if (hangars.Count == 1)
                    FillFactoryQueue(hangars[0]);
                else if (hangars.Count > 1)
                    FillHangarsQueue(hangars);
                else
                    FillFactoryQueue(navalDock != null ? (object)navalDock
                        : depot != null ? (object)depot : (object)airbase);
            }
            if (!queueShown && _queueHash != 0)
            {
                _queueHash = 0;
                ClearChildren(_strip2Content);
            }

            // Контекстные кнопки: на «Производстве» у фабрик — Пауза/Цикл/Шаблон+,
            // на «Ш» — только Шаблон+ (сохранить набор). Пауза/Цикл — ГРУППОВЫЕ:
            // действуют на ВСЕ фабрики выделения (мульти-ангар/депо/верфь, канон
            // SupCom «фабрики управляемы пачкой»); подсветка — «все в состоянии».
            _ctxDepot = depot;
            _ctxDock = navalDock;
            _ctxAirbase = airbase;
            // У построенной ВПП нет вышки — тумблеры посадки/взлёта доступны и с
            // ангара: его parentAirbase (все ангары выделения с ОДНОЙ базы;
            // смешанное выделение — не показываем, база неоднозначна).
            if (_ctxAirbase == null && hangars.Count > 0)
            {
                Airbase parent = hangars[0].parentAirbase;
                bool same = parent != null;
                for (int h = 1; h < hangars.Count && same; h++)
                    same = hangars[h].parentAirbase == parent;
                if (same)
                    _ctxAirbase = parent;
            }
            // Авианосец: у палубы нет ни вышки, ни ангаров-зданий — тумблеры даём
            // с выделения самого корабля (ромб летит вместе с ним — и тот путь есть).
            if (_ctxAirbase == null && carrier != null)
                _ctxAirbase = carrier;
            _ctxHangar = hangars.Count > 0 ? hangars[0] : null;
            Transform ctx = _tabPanel != null ? _tabPanel.Find("factoryCtx") : null;
            if (ctx != null)
            {
                bool ctxOnFactory = _activeTab == TabProduction && _ctxFactories.Count > 0;
                bool ctxOnAirbase = _activeTab == TabProduction && _ctxAirbase != null;
                ctx.gameObject.SetActive(ctxOnFactory || ctxOnAirbase || _activeTab == TabTemplates);
                if (_pauseBtn != null)
                    _pauseBtn.gameObject.SetActive(ctxOnFactory);
                if (_cycleBtn != null)
                    _cycleBtn.gameObject.SetActive(ctxOnFactory);
                if (_tplBtn != null)
                    _tplBtn.gameObject.SetActive(ctxOnFactory || _activeTab == TabTemplates);
                Image pi = _pauseBtn != null ? _pauseBtn.GetComponent<Image>() : null;
                if (pi != null)
                    pi.color = ProductionTasks.AllPaused(_ctxFactories) ? BtnActive : BtnBg;
                Image ci = _cycleBtn != null ? _cycleBtn.GetComponent<Image>() : null;
                if (ci != null)
                    ci.color = ProductionTasks.AllCycling(_ctxFactories) ? BtnActive : BtnBg;
                // Тумблеры аэродрома: видны при выделенном аэродроме, подсветка = закрыто.
                if (_noLandBtn != null)
                {
                    _noLandBtn.gameObject.SetActive(ctxOnAirbase);
                    Image nli = _noLandBtn.GetComponent<Image>();
                    if (nli != null)
                        nli.color = _ctxAirbase != null && !AirbaseAccess.IsLandingOpen(_ctxAirbase)
                            ? BtnActive : BtnBg;
                }
                if (_noTakeoffBtn != null)
                {
                    _noTakeoffBtn.gameObject.SetActive(ctxOnAirbase);
                    Image nti = _noTakeoffBtn.GetComponent<Image>();
                    if (nti != null)
                        nti.color = _ctxAirbase != null && !AirbaseAccess.IsTakeoffOpen(_ctxAirbase)
                            ? BtnActive : BtnBg;
                }
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
                        depot == null && navalDock == null && airbase == null &&
                        hangars.Count == 0)
                        FillUnfinishedTab(BuildTasks.SelectedTask);
                    else
                        FillProductionTab(hq, depot, navalDock, airbase, hasEngineer, hangars);
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
                Caption = t.IsRunway || t.IsDock
                    ? t.Label // спец-площадки подписаны собой («ВПП»/«Верфь»)
                    : t.Definition != null
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
            public bool WheelLoadout;    // каталог авиации: колесо над плиткой циклит пресет лодаута
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
                _tipTiles.Add((rt, parent.parent as RectTransform, spec.Def, spec.WheelLoadout));
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
        /// (броня — зелёный, ПВО — фиолетовый, артиллерия — оранжевый и т.д.).
        /// Цепочка if, а не switch: паттерны switch — только константы, вызов
        /// Loc.Tr в позиции case не компилируется (CS0426).</summary>
        private static Color BadgeColorOf(string badge)
        {
            if (badge == Loc.Tr("И")) return new Color(0.45f, 0.85f, 1f, 1f);
            if (badge == Loc.Tr("У")) return new Color(1f, 0.68f, 0.3f, 1f);
            if (badge == Loc.Tr("М")) return new Color(1f, 0.9f, 0.35f, 1f);
            if (badge == Loc.Tr("ТР")) return new Color(0.72f, 0.78f, 0.84f, 1f);
            if (badge == Loc.Tr("РЛС")) return new Color(0.62f, 0.9f, 1f, 1f);
            if (badge == Loc.Tr("ТНК") || badge == Loc.Tr("ББМ") || badge == Loc.Tr("ЛБМ"))
                return new Color(0.55f, 0.85f, 0.5f, 1f);
            if (badge == Loc.Tr("АРТ")) return new Color(1f, 0.65f, 0.35f, 1f);
            if (badge == Loc.Tr("ЗУ") || badge == Loc.Tr("ПЗРК") || badge == Loc.Tr("ЗРК"))
                return new Color(0.85f, 0.55f, 1f, 1f);
            return new Color(0.72f, 0.76f, 0.72f, 1f);
        }

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

        /// <summary>Бейдж типа плитки: наземка — из vehicleType, флот — класс
        /// корабля, авиация — роль из roleIdentity; здания — без бейджа.</summary>
        private static string TypeBadgeOf(UnitDefinition def)
        {
            if (def is VehicleDefinition vd)
                return Catalog.BadgeOf(vd.vehicleType);
            if (def is ShipDefinition ship)
                return Catalog.BadgeOf(ship.shipType);
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

                /// <summary>Очередь ГРУППЫ ангаров (агрегат): по типу — сумма позиций,
        /// полоса = максимальный прогресс среди наливаемых (показывает активность),
        /// бейдж ×N — всего позиций. ЛКМ — +1 каждому умеющему (Shift ×5),
        /// ПКМ — −1 с конца одного ангара группы (Shift ×5).</summary>
        private void FillHangarsQueue(List<Hangar> hangars)
        {
            ClearChildren(_strip2Content);
            var order = new List<UnitDefinition>();
            var totals = new Dictionary<UnitDefinition, int>();
            var activeCounts = new Dictionary<UnitDefinition, int>();
            var topProgress = new Dictionary<UnitDefinition, float>();
            foreach (ProductionTasks.QueueItem item in ProductionTasks.All)
            {
                if (item.UnitDef == null)
                    continue;
                bool ours = false;
                foreach (Hangar hangar in hangars)
                    if (item.Factory == (object)hangar)
                    {
                        ours = true;
                        break;
                    }
                if (!ours)
                    continue;
                if (!totals.ContainsKey(item.UnitDef))
                {
                    totals[item.UnitDef] = 0;
                    order.Add(item.UnitDef);
                }
                totals[item.UnitDef]++;
                if (item.Paid > 0f)
                {
                    activeCounts[item.UnitDef] = activeCounts.TryGetValue(item.UnitDef, out int n)
                        ? n + 1 : 1;
                    topProgress[item.UnitDef] = Mathf.Max(
                        topProgress.TryGetValue(item.UnitDef, out float p) ? p : 0f,
                        item.Progress01);
                }
            }
            foreach (UnitDefinition def in order)
            {
                UnitDefinition d = def;
                int act = activeCounts.TryGetValue(def, out int n) ? n : 0;
                Tile(_strip2Content, new TileSpec
                {
                    Icon = IconOf(d),
                    Caption = Catalog.ShortLabel(d.code, d.unitName),
                    TypeBadge = TypeBadgeOf(d),
                    Badge = "×" + totals[def] + (act > 0 ? " ▲" + act : ""),
                    Progress = act > 0 && topProgress.TryGetValue(def, out float p) ? p : (float?)null,
                    OnLeft = () =>
                    {
                        if (!(d is AircraftDefinition ad))
                            return;
                        foreach (Hangar hangar in hangars)
                            if (ProductionTasks.CanBuild(hangar, ad))
                                ProductionTasks.EnqueueAirSilent(hangar, ad);
                    },
                    OnRight = () =>
                    {
                        for (int i = hangars.Count - 1; i >= 0; i--)
                            if (ProductionTasks.CountFor(hangars[i]) > 0)
                            {
                                ProductionTasks.RemoveTail(hangars[i], d, RtsInput.Shift ? 5 : 1);
                                break;
                            }
                    },
                    Def = d,
                });
            }
        }

        /// <summary>Нижняя строка «Производства» (фабрика): очередь заказов — плитки
        /// «иконка + подпись + ×N», у головной позиции — прогресс; клики по стаку:
        /// ЛКМ — добавить 1 (Shift — 5), ПКМ — снять 1 с конца (Shift — 5, с возвратом денег).</summary>
        private void FillFactoryQueue(object factory)
        {
            ClearChildren(_strip2Content);
            if (factory == null)
                return;
            // Стак подряд идущих одинаковых позиций. НАЛИВАЕМЫЕ позиции (Paid > 0)
            // при параллельной оплате видны КАЖДАЯ со своей полосой (SupCom-очередь),
            // ждущий хвост — одной плиткой ×N. Раньше полоса была только у головы
            // первого стака — параллельная сборка была невидима (плейтест v0.87).
            var runDef = default(UnitDefinition);
            var items = new List<ProductionTasks.QueueItem>();
            void EmitRun()
            {
                int waiting = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Paid <= 0f)
                    {
                        waiting = items.Count - i;
                        break;
                    }
                    ProductionTasks.QueueItem active = items[i];
                    Tile(_strip2Content, new TileSpec
                    {
                        Icon = IconOf(runDef),
                        Caption = Catalog.ShortLabel(runDef.code, runDef.unitName),
                        TypeBadge = TypeBadgeOf(runDef),
                        Progress = active.Progress01,
                        OnLeft = () => ProductionTasks.InsertAfter(active, RtsInput.Shift ? 5 : 1),
                        OnRight = () => ProductionTasks.Cancel(active),
                        Def = runDef,
                    });
                }
                if (waiting > 0)
                {
                    UnitDefinition def = runDef;
                    ProductionTasks.QueueItem tail = items[items.Count - 1];
                    Tile(_strip2Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        Badge = waiting > 1 ? "×" + waiting : null,
                        OnLeft = () => ProductionTasks.InsertAfter(tail, RtsInput.Shift ? 5 : 1),
                        OnRight = () => ProductionTasks.RemoveTail(factory, def, RtsInput.Shift ? 5 : 1),
                        Def = def,
                    });
                }
            }
            foreach (ProductionTasks.QueueItem item in ProductionTasks.All)
            {
                if (item.Factory != factory || item.UnitDef == null)
                    continue;
                if (items.Count > 0 && item.UnitDef != runDef)
                {
                    EmitRun();
                    items.Clear();
                }
                if (items.Count == 0)
                    runDef = item.UnitDef;
                items.Add(item);
            }
            if (items.Count > 0)
                EmitRun();
        }

        /// <summary>Карточка недостроя (выделен кликом по площадке): прогресс и действия.</summary>
        private void FillUnfinishedTab(BuildTasks.Task t)
        {
            _contextTitle.text = Loc.F("Недострой: {0} · {1}",
                t.Definition != null ? t.Definition.unitName : "?", t.Progress01.ToString("P0"));
            BuildTasks.Task task = t;
            AddItemButton(_strip1Content,
                t.Definition != null ? t.Definition.friendlyIcon : null,
                Loc.Tr("Снести площадку (без возврата)"),
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
                _contextTitle.text = Loc.F("Шаблон: набор {0} зданий", buildingSet.Count);
                FillBuildingSetRow(buildingSet);
            }
            else if (hasEngineer)
                _contextTitle.text = Loc.Tr("Шаблон: очередь стройки инженера");
            else if (depot != null)
                _contextTitle.text = Loc.Tr("Шаблон: очередь фабрики");
            else if (airbase != null)
                _contextTitle.text = Loc.Tr("Шаблон: очередь аэродрома");
            else
                _contextTitle.text = Loc.Tr("Шаблоны");

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
                            CommanderHud.Notice(Loc.Tr("Выдели инженера для постройки шаблона"));
                            return;
                        }
                        OrderTemplates.PendingTemplate = (t, engineers);
                        CommanderHud.Notice(Loc.F("{0}: призраки у курсора · ЛКМ — строить", t.name));
                    }
                    else if (depot != null || airbase != null)
                    {
                        int n = OrderTemplates.Enqueue(t, depot != null ? (object)depot : (object)airbase);
                        CommanderHud.Notice(Loc.F("{0}: в очередь {1}", t.name, n));
                    }
                });
            }
            if (OrderTemplates.Count == 0)
                AddItemButton(_strip1Content, null, Loc.Tr("шаблонов нет — собери набор и нажми Шаблон+"), null);
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
        private void FillProductionTab(FactionHQ hq, VehicleDepot depot, Building navalDock,
            Airbase airbase, bool hasEngineer, List<Hangar> hangars = null)
        {
            string tierName = _prodTier == 0 ? "" : " · T" + _prodTier;
            // АНГАР(Ы) — индивидуальные авиафабрии (v0.88): каталог — юнион списков
            // выделенных; клик по плитке = +1 КАЖДОМУ выделенному, кто умеет строить
            // (паттерн SupCom «выделил пачку фабрик — скормил заказ всем»).
            if (hangars != null && hangars.Count > 0)
            {
                _contextTitle.text = hangars.Count == 1
                    ? Loc.Tr("Ангар — авиация (клик: в очередь)") + tierName
                    : Loc.F("Ангары ×{0} — клик: +1 каждому умеющему", hangars.Count) + tierName;
                var defs = new List<AircraftDefinition>();
                foreach (Hangar hangar in hangars)
                    foreach (AircraftDefinition a in hangar.GetAvailableAircraft())
                        if (a != null && !defs.Contains(a))
                            defs.Add(a);
                Catalog.ApplyTierFilter(defs, d => d.value, _prodTier);
                foreach (AircraftDefinition def in defs)
                {
                    AircraftDefinition d = def;
                    // Отступление от стандарта — суффикс подписи «·ЛN» (индекс пресета)
                    int li = ProductionTasks.LoadoutIndexOf(d);
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName) + (li > 0 ? Loc.Tr("·Л") + li : ""),
                        TypeBadge = TypeBadgeOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () =>
                        {
                            int n = 0;
                            foreach (Hangar hangar in hangars)
                                if (ProductionTasks.CanBuild(hangar, d) &&
                                    ProductionTasks.EnqueueAirSilent(hangar, d))
                                    n++;
                            if (n == 0)
                                CommanderHud.Notice(Loc.F("Ни один выделенный ангар не строит: {0}",
                                    Catalog.ShortLabel(d.code, d.unitName)));
                        },
                        Def = def,
                        WheelLoadout = true,
                    });
                }
                return; // нижняя строка — очередь ангара(ов) (RefreshTabs)
            }
            // ВЕРФЬ — третья фабрика (v0.92): каталог — весь флот энциклопедии
            // (терциль по цене тем же тир-фильтром: T1 катера, T3 авианосцы).
            if (navalDock != null && Encyclopedia.i != null && Encyclopedia.i.ships != null)
            {
                _contextTitle.text = Loc.Tr("Верфь — флот (клик: в очередь)") + tierName;
                var defs = new List<ShipDefinition>();
                foreach (ShipDefinition def in Encyclopedia.i.ships)
                    if (def != null && def.unitPrefab != null &&
                        def.unitPrefab.GetComponent<Ship>() != null && def.IsAllowed(false))
                        defs.Add(def);
                Catalog.ApplyTierFilter(defs, d => d.value, _prodTier);
                foreach (ShipDefinition def in defs)
                {
                    ShipDefinition d = def;
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueShip(navalDock, d),
                        Def = def,
                    });
                }
                return; // нижняя строка — очередь верфи (RefreshTabs)
            }
            if (hasEngineer && Encyclopedia.i != null && Encyclopedia.i.buildings != null)
            {
                _contextTitle.text = Loc.Tr("Стройка (инженер): здание → ЛКМ по земле") + tierName;
                // ВПП — особый элемент: не здание, по готовности создаёт НОВЫЙ
                // аэродром (своя очередь + грид ангаров) — «ещё одна фабрика».
                Tile(_strip1Content, new TileSpec
                {
                    Caption = Loc.Tr("ВПП"),
                    TypeBadge = Loc.Tr("АЭР"),
                    Badge = Settings.RunwayCost.ToString("F0"),
                    OnLeft = () =>
                    {
                        BuildTasks.Pending = null;
                        BuildTasks.PendingRunway = true;
                    },
                });
                // ВЕРФЬ — вторая спец-площадка: платформа на воде, производит
                // флот (SupCom naval yard; каталог — вся Encyclopedia.ships).
                if (BuildTasks.DockDefinition() != null)
                    Tile(_strip1Content, new TileSpec
                    {
                        Caption = Loc.Tr("ВЕРФЬ"),
                        TypeBadge = Loc.Tr("ФЛОТ"),
                        Badge = Settings.DockCost.ToString("F0"),
                        OnLeft = () =>
                        {
                            BuildTasks.Pending = null;
                            BuildTasks.PendingRunway = false;
                            BuildTasks.PendingDock = true;
                        },
                    });
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
                        TypeBadge = BuildTasks.IsHangarDefinition(d) ? Loc.Tr("АНГ") : null,
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => BuildTasks.Pending = d,
                        Def = def,
                    });
                }
                return; // нижняя строка — очередь стройки (RefreshTabs)
            }

            if (depot != null && PurchaseService.Catalog != null)
            {
                _contextTitle.text = Loc.Tr("Фабрика — техника (клик: в очередь)") + tierName;
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
                _contextTitle.text = Loc.Tr("Аэродром — авиация (клик: в очередь; РЕЗ — выкат)") + tierName;
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
                            TypeBadge = Loc.Tr("РЕЗ"),
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
                    AddItemButton(_strip1Content, null, Loc.Tr("нет работоспособных ангаров"), null);
                    return;
                }
                Catalog.ApplyTierFilter(defs, d => d.value, _prodTier);
                foreach (AircraftDefinition def in defs)
                {
                    AircraftDefinition d = def;
                    int li = ProductionTasks.LoadoutIndexOf(d);
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName) + (li > 0 ? Loc.Tr("·Л") + li : ""),
                        TypeBadge = TypeBadgeOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueAirDistributed(airbase, d),
                        Def = def,
                        WheelLoadout = true,
                    });
                }
                return; // нижняя строка — очередь аэродрома (RefreshTabs)
            }

            _contextTitle.text = Loc.Tr("Производство");
            AddItemButton(_strip1Content, null, Loc.Tr("выдели депо, аэродром или инженера"), null);
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
                _contextTitle.text = Loc.Tr("Апгрейд");
                AddItemButton(_strip1Content, null, Loc.Tr("выдели свой экстрактор"), null);
                _strip2.gameObject.SetActive(false);
                return;
            }
            int tier = EconomyService.TierOf(extractor);
            _contextTitle.text = Loc.F("Экстрактор T{0} · +{1}/мин",
                tier, EconomyService.IncomeRate(tier).ToString("F0"));
            float cost = EconomyService.NextUpgradeCost(extractor);
            if (cost >= 0f)
            {
                bool ok = hq.factionFunds >= cost;
                AddItemButton(_strip1Content, null,
                    Loc.F("Улучшить до T{0} — {1}", tier + 1, cost.ToString("F0")),
                    ok ? () => EconomyService.TryUpgrade(hq, extractor) : null);
            }
            else
                AddItemButton(_strip1Content, null, Loc.Tr("максимальный тир"), null);
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

        // ---------- наведение на плитку закупки (карточка определения, SupCom) ----------

        /// <summary>Опрос плиток под курсором (каждый кадр, без uGUI-событий — игровой
        /// input-модуль Enter/Exit не доставляет): курсор внутри плитки И внутри её
        /// ленты-вьюпорта (замаскированные при прокрутке плитки не считаются).
        /// Снесённые плитки выпадают сами (Unity fake-null). Наведение переключает
        /// панель свойств на карточку закупки МГНОВЕННО (v0.87.0; в v0.60.0–v0.85.0
        /// карточка не показывалась вообще — сброс состояния наведения в окне
        /// задержки перезапускал её таймер каждый кадр). КОЛЕСО над плиткой каталога
        /// авиации циклит пресет лодаута (событие ввода — только в кадр прокрутки;
        /// каталог пересобирается по LoadoutsVersion, карточка — ключом кэша).</summary>
        private void UpdateTileHover()
        {
            UnitDefinition hit = null;
            bool hitWheel = false;
            if (Settings.UiTooltips && !RtsInput.Typing && _canvas != null)
            {
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
                    {
                        hit = entry.def;
                        hitWheel = entry.wheel;
                    }
                }
            }
            if (hit != _hoverShow)
            {
                // Вход/уход с плитки — панель переклеивается немедленно, не ждём
                // очередного 0.2-секундного рефреша.
                _hoverShow = hit;
                _infoHash = -1;
                _nextRefresh = 0f;
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f && hitWheel && hit is AircraftDefinition ad)
            {
                ProductionTasks.CycleLoadout(ad, wheel > 0f ? 1 : -1);
                // Колесо УПТРЕБЛЕНО UI: камера в этом кадре не зумит (PointerOverUi
                // не всегда ловит плитки — подавляем явно).
                RtsInput.WheelConsumedByUi = true;
                _nextRefresh = 0f;   // карточка/каталог — немедленно (оружие по пресету, бейдж плитки)
            }
            // СКМ по плитке каталога авиации — редактор лодаута «ОСНАЩЕНИЕ»
            // (правки станций/топлива = пресет «Свой» в общем цикле колеса).
            if (Input.GetMouseButtonDown(2) && hitWheel && hit is AircraftDefinition adEdit)
            {
                RtsInput.MiddleConsumedByUi = true;   // камера не начинает драг/тап
                ToggleLoadoutEditor(adEdit);
            }
            // Редактор лодаута перерисовывается по версии выбора: колесо по плитке
            // или правки в другом месте меняют выбор — окно не ждёт 0.2-с цикла.
            if (_loWin != null && _loWin.gameObject.activeSelf && _loDef != null &&
                ProductionTasks.LoadoutsVersion != _loVersion)
                RefreshLoadoutEditor();
        }

        /// <summary>Сброс наведения (режим выключен/чат/пересборка канваса):
        /// панель возвращается к выделению на ближайшем рефреше.</summary>
        private void ResetHover()
        {
            if (_hoverShow == null)
                return;
            _hoverShow = null;
            _infoHash = -1;
            _nextRefresh = 0f;
        }

        // ---------- окно «ОСНАЩЕНИЕ» (редактор лодаута, СКМ по плитке каталога) ----------

        /// <summary>Открыть/закрыть редактор лодаута борта (СКМ по плитке каталога
        /// авиации). Живёт в плавающем окне до закрытия; правки станций/топлива
        /// = пресет «Свой», встающий в общий цикл колеса.</summary>
        private void ToggleLoadoutEditor(AircraftDefinition def)
        {
            if (_loWin != null && _loWin.gameObject.activeSelf && _loDef == def)
            {
                _loWin.gameObject.SetActive(false);
                return;
            }
            if (ProductionTasks.HardpointSetsOf(def) == null)
            {
                CommanderHud.Notice(Loc.F("Нет данных о станциях: {0}",
                                    Catalog.ShortLabel(def.code, def.unitName)));
                return;
            }
            _loDef = def;
            _loVersion = -1;
            if (_loWin == null)
            {
                // Над панелью закупки (низ-центр, правее левой колонки); окно
                // перетаскиваемо и самовосстанавливается в границах канваса.
                var crt = (RectTransform)_canvas.transform;
                float x = Mathf.Clamp(crt.rect.width * 0.5f - 60f, 460f, crt.rect.width - 420f);
                _loWin = FloatingWindow.Create(_canvas.transform, _font, Loc.Tr("ОСНАЩЕНИЕ"),
                    new Vector2(x, 168f), new Vector2(404f, 330f), null);
            }
            if (!_loWin.gameObject.activeSelf)
                _loWin.gameObject.SetActive(true);
            RefreshLoadoutEditor();
        }

        /// <summary>Полная перерисовка контента редактора (открытие, клики, колесо —
        /// всё через смену LoadoutsVersion/прямой вызов). Контент —_fact-на-строку,
        /// правки применяются к заказам сразу (сохранять нечего: состояние липкое).</summary>
        private void RefreshLoadoutEditor()
        {
            if (_loWin == null || _loWin.Content == null || _loDef == null)
                return;
            _loVersion = ProductionTasks.LoadoutsVersion;
            AircraftDefinition def = _loDef;
            HardpointSet[] sets = ProductionTasks.HardpointSetsOf(def);
            GameManager.GetLocalHQ(out FactionHQ hq);
            Airbase ab = _ctxAirbase != null ? _ctxAirbase
                : (_ctxHangar != null ? _ctxHangar.parentAirbase : null);

            ClearChildren(_loWin.Content);
            var content = _loWin.Content;

            // Шапка: силуэт + имя/класс + масса пустого борта
            var icon = Icon(content, def.mapIcon != null ? def.mapIcon : def.friendlyIcon,
                new Vector2(84f, 84f));
            var irt = (RectTransform)icon.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0f, 1f);
            irt.pivot = new Vector2(0f, 1f);
            irt.anchoredPosition = new Vector2(10f, -6f);
            icon.preserveAspect = true;

            var name = Label(content, "name", Util.FitLine(def.unitName, 34), 16, TextMain,
                TextAnchor.UpperLeft, true);
            PlaceLo(name.transform, 104f, 4f, 300f, 22f);
            var cls = Label(content, "cls", Loc.F("{0} · цена {1}", ClassOfDefinition(def), def.value.ToString("F0")),
                12, TextDim, TextAnchor.UpperLeft, true);
            PlaceLo(cls.transform, 104f, 26f, 300f, 18f);
            string emptyMass = def.mass > 1000f
                ? Loc.F("масса пустого ≈ {0} т", (def.mass / 1000f).ToString("0.#",
                      System.Globalization.CultureInfo.InvariantCulture))
                : def.mass > 0f ? Loc.F("масса ≈ {0} кг", def.mass.ToString("0")) : "";
            var mass = Label(content, "mass", emptyMass, 12, TextDim, TextAnchor.UpperLeft, true);
            PlaceLo(mass.transform, 104f, 44f, 300f, 18f);
            int sel = ProductionTasks.LoadoutIndexOf(def);
            var selLo = ProductionTasks.SelectedLoadout(def, out float selFuel);
            string fuelTxt = selFuel > 0.995f ? "100%" : Mathf.RoundToInt(selFuel * 100f) + "%";
            var cur = Label(content, "cur", Loc.F("выбрано: {0} · топливо {1}", LoNameOf(def, sel), fuelTxt),
                13, Accent, TextAnchor.UpperLeft, true);
            PlaceLo(cur.transform, 104f, 64f, 300f, 18f);

            // Пресеты-кнопки: Стандарт → авторские → «Свои N»; «+» — новый «Свой»
            // (клон текущего выбора), «−» — удалить выбранный «Свой».
            float y = 96f;
            var phdr = Label(content, "phdr", Loc.Tr("Пресеты (клик — выбрать):"), 11, TextDim,
                TextAnchor.UpperLeft, true);
            PlaceLo(phdr.transform, 10f, y, 300f, 14f);
            y += 16f;
            float x = 10f;
            for (int i = 0; i < ProductionTasks.LoadoutCount(def); i++)
            {
                int idx = i;
                var b = Button_(content, "preset" + i, LoNameOf(def, i), () =>
                {
                    ProductionTasks.SelectLoadoutIndex(def, idx);
                    RefreshLoadoutEditor();
                }, 11);
                PaintLoButton(b, x, y, 84f, 24f, i == sel);
                x += 88f;
                if (x > 290f)
                {
                    x = 10f;
                    y += 28f;
                }
            }
            var plus = Button_(content, "plus", "+", () =>
            {
                ProductionTasks.AddCustom(def);
                _nextRefresh = 0f;
                RefreshLoadoutEditor();
            }, 14);
            PaintLoButton(plus, x, y, 26f, 24f, false);
            x += 30f;
            if (ProductionTasks.SelectedCustom(def) != null)
            {
                var minus = Button_(content, "minus", "−", () =>
                {
                    ProductionTasks.RemoveSelectedCustom(def);
                    _nextRefresh = 0f;
                    RefreshLoadoutEditor();
                }, 14);
                PaintLoButton(minus, x, y, 26f, 24f, false);
            }

            // Топливо (чипы — для «Своего»; у пресета топливо зашито автором)
            y += 30f;
            bool customSel = ProductionTasks.SelectedCustom(def) != null;
            var fl = Label(content, "fuel", Loc.Tr("Топливо:"), 11, TextDim, TextAnchor.UpperLeft, true);
            PlaceLo(fl.transform, 10f, y, 60f, 14f);
            x = 66f;
            foreach (float f in new[] { 0.5f, 0.75f, 1f })
            {
                float val = f;
                var b = Button_(content, "fuel" + f, Mathf.RoundToInt(f * 100f) + "%", () =>
                {
                    if (ProductionTasks.HasCustomLoadout(def))
                        ProductionTasks.SetCustomFuel(def, val);
                    RefreshLoadoutEditor();
                }, 11);
                PaintLoButton(b, x, y - 4f, 46f, 22f, customSel &&
                    Mathf.Abs(selFuel - val) < 0.01f);
                x += 50f;
            }
            if (!customSel)
            {
                var note = Label(content, "fnote", Loc.Tr("у пресета — авторское"), 10, TextDim,
                    TextAnchor.UpperLeft, true);
                PlaceLo(note.transform, x + 4f, y, 150f, 14f);
            }

            // Станции: клик — следующий легальный вариант (правки = «Свой»)
            y += 26f;
            var shdr = Label(content, "shdr", Loc.Tr("Станции (клик — сменить вооружение):"), 11, TextDim,
                TextAnchor.UpperLeft, true);
            PlaceLo(shdr.transform, 10f, y, 380f, 14f);
            y += 16f;
            for (int i = 0; i < sets.Length; i++)
            {
                int setIdx = i;
                HardpointSet set = sets[i];
                int pylons = set != null && set.hardpoints != null ? set.hardpoints.Count : 1;
                string setName = set != null && !string.IsNullOrEmpty(set.name)
                    ? set.name : Loc.F("Ст.{0}", i + 1);
                // Отображение: маунт выбранного лодаута, а при «Стандарте» —
                // заводской из дефолт-лодаута игры (внутренние пушки и т.п.).
                WeaponMount m = selLo != null && i < selLo.weapons.Count ? selLo.weapons[i]
                    : ProductionTasks.DefaultMountOf(def, i);
                string mount = m != null && m.info != null
                    ? (!string.IsNullOrEmpty(m.info.shortName) ? m.info.shortName : m.info.weaponName)
                      + " ×" + Mathf.Max(1, m.ammo) * pylons
                    : Loc.Tr("пусто");
                if (!ProductionTasks.StationConfigurable(def, i, ab, hq))
                {
                    // Настроек нет (пустые options и ничего не висит) — надпись, не кнопка
                    var lbl = Label(content, "st" + i,
                        setName + " ×" + pylons + " · " + Loc.Tr("нет вариантов"),
                        11, TextDim, TextAnchor.MiddleLeft, true);
                    PlaceLo(lbl.transform, 10f, y, 384f, 24f);
                    y += 27f;
                    continue;
                }
                var b = Button_(content, "st" + i, setName + " ×" + pylons + " · " + mount, () =>
                {
                    ProductionTasks.CycleStation(def, setIdx, 1, ab, hq);
                    _nextRefresh = 0f;
                    RefreshLoadoutEditor();
                }, 11);
                PaintLoButton(b, 10f, y, 384f, 24f, false);
                y += 27f;
            }

            // Итог: масса подвесок + легальность на текущем аэродроме
            float susp = 0f;
            if (selLo != null)
                for (int i = 0; i < selLo.weapons.Count; i++)
                {
                    WeaponMount m = selLo.weapons[i];
                    if (m == null || m.info == null)
                        continue;
                    int pylons = sets != null && i < sets.Length && sets[i] != null &&
                                 sets[i].hardpoints != null ? sets[i].hardpoints.Count : 1;
                    susp += Mathf.Max(1, m.ammo) * m.info.massPerRound * pylons;
                }
            bool allowed = ProductionTasks.LoadoutAllowed(def, selLo, hq);
            string massLine = (susp >= 1000f
                ? Loc.F("+≈{0} т подвесок", (susp / 1000f).ToString("0.#",
                      System.Globalization.CultureInfo.InvariantCulture))
                : susp > 0f ? Loc.F("+≈{0} кг подвесок", susp.ToString("0")) : Loc.Tr("без подвесок"))
                + (selLo == null ? Loc.Tr(" (стандарт)") : "")
                + Loc.F(" · на этом аэродроме: {0}", allowed ? Loc.Tr("легально") : Loc.Tr("НЕДОСТУПНО"));
            var it = Label(content, "itogo", massLine, 11, allowed ? TextDim : new Color(1f, 0.5f, 0.4f, 1f),
                TextAnchor.UpperLeft, true);
            PlaceLo(it.transform, 10f, y, 384f, 16f);
            y += 20f;

            _loWin.Root.sizeDelta = new Vector2(404f, Mathf.Max(240f, y + 8f));
            _loWin.ClampToParent();
        }

        /// <summary>Имя варианта по индексу: Стандарт / пресет / «Свой N».</summary>
        private static string LoNameOf(AircraftDefinition def, int idx)
        {
            if (idx <= 0)
                return Loc.Tr("Стандарт");
            var presets = ProductionTasks.UsablePresets(def);
            if (idx - 1 < presets.Count)
            {
                string n = presets[idx - 1].Name;
                return string.IsNullOrEmpty(n) ? "#" + idx : n;
            }
            return ProductionTasks.CustomName + " " + (idx - presets.Count);
        }

        private void PlaceLo(Transform t, float x, float y, float w, float h)
        {
            var rt = (RectTransform)t;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private void PaintLoButton(Button b, float x, float y, float w, float h, bool selected)
        {
            PlaceLo(b.transform, x, y, w, h);
            var img = b.GetComponent<Image>();
            if (img != null)
                img.color = selected ? BtnActive : BtnBg;
        }

        /// <summary>Контент карточки с кэшем (ключ — определение + индекс лодаута:
        /// колесо даёт новую запись, старые не портятся); сбой расчёта не роняет UI —
        /// показывает что насчиталось (например, только имя).</summary>
        private TipContent GetTipContent(UnitDefinition def, int loadIdx)
        {
            var key = (def, loadIdx);
            if (_tipContent.TryGetValue(key, out TipContent c))
                return c;
            c = new TipContent { Title = def.unitName ?? "?" };
            try
            {
                BuildTipContent(def, c, loadIdx);
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: сбой расчёта карточки закупки (" + (def.unitName ?? "?") +
                          ") — " + e.GetType().Name + ": " + e.Message);
            }
            _tipContent[key] = c;
            return c;
        }

        /// <summary>Расчёт строк карточки по определению: темп фабрики/стройки —
        /// из Settings; вооружение — состав ВЫБРАННОГО ЛОДАУТА у авиации с пресетами
        /// (заряд пилона × пилонов сета, hardpointSets сериализованы на префабе),
        /// иначе дефолтная нагрузка префаба (Weapon-компоненты с info/ammo);
        /// скорость авиации — aircraftInfo (фолбэк — параметры).</summary>
        private void BuildTipContent(UnitDefinition def, TipContent c, int loadIdx)
        {
            bool building = def is BuildingDefinition;
            float seconds = building ? Settings.BuildSecondsPerBuilder : Settings.ProductionSecondsPerItem;
            c.Title = Util.FitLine(def.unitName, FitTitleChars);
            c.ClassLine = Catalog.FormatTipClass(ClassOfDefinition(def), def.value, seconds, building);

            var acc = new WeaponTallyAccumulator();
            acc.BeginUnit();
            Loadout lo = null;
            if (def is AircraftDefinition adef)
            {
                int n = ProductionTasks.LoadoutCount(adef);
                if (n > 1)
                {
                    int i = Mathf.Clamp(loadIdx, 0, n - 1);
                    lo = ProductionTasks.SelectedLoadout(adef, out float selFuel);
                    string name = i == 0 ? Loc.Tr("Стандарт") : LoNameOf(adef, i);
                    // Бак — только когда отличен от полного: выбор виден целиком.
                    string tank = selFuel > 0.05f && selFuel < 0.995f
                        ? Loc.F(" · бак {0}%", Mathf.RoundToInt(selFuel * 100f)) : "";
                    c.LoadoutLine = Loc.F("лодаут: {0}{1} · колесо — смена · СКМ — правка", name, tank);
                }
            }
            if (lo != null)
            {
                HardpointSet[] sets = ProductionTasks.HardpointSetsOf(def as AircraftDefinition);
                List<WeaponMount> ws = lo.weapons;
                for (int i = 0; i < ws.Count; i++)
                {
                    WeaponMount mount = ws[i];
                    WeaponInfo wi = mount != null ? mount.info : null;
                    if (wi == null || wi.hideInDisplay)
                        continue;
                    int mult = 1;
                    if (sets != null && i < sets.Length && sets[i] != null && sets[i].hardpoints != null)
                        mult = Mathf.Max(1, sets[i].hardpoints.Count);
                    int total = Mathf.Max(1, mount.ammo) * mult;
                    string name = !string.IsNullOrEmpty(wi.shortName) ? wi.shortName : wi.weaponName;
                    acc.AddStation(name, total, total,
                        Catalog.WeaponDomains(wi.effectiveness.antiAir, wi.effectiveness.antiSurface,
                            wi.effectiveness.antiMissile, wi.nuclear, wi.jammer));
                }
            }
            else if (def.unitPrefab != null)
                foreach (Weapon w in def.unitPrefab.GetComponentsInChildren<Weapon>(true))
                {
                    WeaponInfo wi = w != null ? w.info : null;
                    if (wi == null || wi.hideInDisplay)
                        continue;
                    string name = !string.IsNullOrEmpty(wi.shortName) ? wi.shortName : wi.weaponName;
                    acc.AddStation(name, w.ammo, w.ammo,
                        Catalog.WeaponDomains(wi.effectiveness.antiAir, wi.effectiveness.antiSurface,
                            wi.effectiveness.antiMissile, wi.nuclear, wi.jammer));
                }
            for (int i = 0; i < acc.Rows.Count; i++)
            {
                if (c.Weapons.Count == DefMaxWeaponRows)
                {
                    int rest = acc.Rows.Count - i;
                    c.Weapons.Add(Loc.F("+ ещё {0} {1}", rest, Loc.Plural(rest, "тип", "типа", "типов")));
                    break;
                }
                c.Weapons.Add(Catalog.FormatTipWeaponRow(acc.Rows[i]));
            }

            float speed = 0f;
            if (def is AircraftDefinition ad)
                speed = ad.aircraftInfo != null ? ad.aircraftInfo.maxSpeed
                    : (ad.aircraftParameters != null ? ad.aircraftParameters.maxSpeed : 0f);
            c.StatRows.AddRange(Catalog.TipStatRows(def.visibleRange, speed,
                def.armorTier, def.manpower, def.mass));

            // Описание: перенос по ширине окна «ОПИСАНИЕ»; длиннее максимума строк —
            // подрезаем хвостами по 24 символа с «…» (только на первом расчёте).
            string desc = def.description ?? "";
            if (desc.Length > 0)
            {
                c.Desc = desc;
                c.DescLines = MeasureLines(c.Desc);
                while (c.DescLines > DefMaxDescLines && c.Desc.Length > 16)
                {
                    c.Desc = c.Desc.Substring(0, System.Math.Max(16, c.Desc.Length - 24)).TrimEnd() + "…";
                    c.DescLines = MeasureLines(c.Desc);
                }
                if (c.DescLines > DefMaxDescLines)
                    c.DescLines = DefMaxDescLines;
            }
        }

        /// <summary>Число строк переноса текста по ширине окна «ОПИСАНИЕ»
        /// (шрифт/настройки живого лейбла); пустой текст или нет лейбла — 0.</summary>
        private int MeasureLines(string text)
        {
            if (string.IsNullOrEmpty(text) || _descText == null || _descText.font == null)
                return 0;
            var gen = _descText.cachedTextGenerator;
            gen.Populate(text, _descText.GetGenerationSettings(new Vector2(DescTextWidth, 4096f)));
            return System.Math.Max(1, gen.lineCount);
        }

        /// <summary>Класс определения для карточки: тип наземки/флота/роль авиации.
        /// v0.97: рядом расшифровка бейджа плитки «(ТНК)/(ЭМ)/(И)» — сокращения
        /// каталога читаются без шпаргалки; корабли раньше падали в «здание».</summary>
        private static string ClassOfDefinition(UnitDefinition def)
        {
            if (def is AircraftDefinition)
                return Loc.F("авиация · {0} ({1})",
                    Catalog.AirRoleName(def.roleIdentity, def.typeIdentity),
                    Catalog.AirBadge(def.roleIdentity, def.typeIdentity));
            if (def is ShipDefinition sea)
                return Loc.F("флот · {0} ({1})",
                    Catalog.TypeNameOf(sea.shipType), Catalog.BadgeOf(sea.shipType));
            if (def is VehicleDefinition vd)
                return Loc.F("наземка · {0} ({1})",
                    Catalog.TypeNameOf(vd.vehicleType), Catalog.BadgeOf(vd.vehicleType));
            return Loc.Tr("здание");
        }

        private static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                Destroy(t.GetChild(i).gameObject);
        }
    }
}
