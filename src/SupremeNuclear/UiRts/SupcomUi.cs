using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace SupremeNuclear
{
    /// <summary>uGUI-интерфейс в духе SupCom: верхняя панель экономики (поток),
    /// список выделения (группы по типам, клик — подвыделение), командная карта
    /// (приказы/стойка/профиль высоты), контекстная панель (покупки/апгрейд).</summary>
    public sealed partial class SupcomUi : MonoBehaviour
    {
        public static SupcomUi Instance { get; private set; }

        // internal: палитрой пользуется SkirmishPanel (меню) — единой темой на весь мод.
        internal static readonly Color PanelBg = new Color(0.07f, 0.09f, 0.11f, 0.92f);
        internal static readonly Color PanelBg2 = new Color(0.10f, 0.13f, 0.16f, 0.92f);
        internal static readonly Color Accent = new Color(0.35f, 1f, 0.5f, 1f);
        internal static readonly Color BtnBg = new Color(0.16f, 0.20f, 0.24f, 1f);
        internal static readonly Color BtnActive = new Color(0.20f, 0.45f, 0.26f, 1f);
        internal static readonly Color TextMain = new Color(0.92f, 0.95f, 0.92f, 1f);
        internal static readonly Color TextDim = new Color(0.62f, 0.66f, 0.62f, 1f);
        internal static readonly Color WarnRed = new Color(1f, 0.42f, 0.36f, 1f);
        internal static readonly Color WarnYellow = new Color(0.85f, 0.75f, 0.25f, 1f);

        private Canvas _canvas;
        /// <summary>Локаль, под которую построен канвас: смена General.Language «на лету»
        /// (SettingChanged → Loc.Bootstrap) бампит Loc.Version — канвас целиком
        /// пересобирается (однажды встроенные надписи живых рефрешей не имеют),
        /// MinimapPanel подхватывает новый канвас сама (canvas != _builtOnCanvas).</summary>
        private int _canvasLocVersion = -1;
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
        private BuildTasks.Task _ctxSite;    // ...или выделенный недострой-фабрика (v0.107)
        /// <summary>ВСЕ фабрики выделения (депо + верфи + ангары): групповые
        /// Пауза/Цикл действуют на весь список, а не на представителя.</summary>
        private readonly List<object> _ctxFactories = new List<object>();
        /// <summary>Буфер группы Пауза/Цикл контекста БАЗЫ (вышка/ромб/авианосец):
        /// все ангарные фабрики её Airbase (v0.108) — как мультивыделение ангаров.</summary>
        private readonly List<object> _ctxBaseFactories = new List<object>();
        private RectTransform _tierRoot;     // полоса выбора пула (тиры каталога)
        private readonly (Button btn, Image img, int tier)[] _tierButtons =
            new (Button, Image, int)[4];     // Все / T1 / T2 / T3
        private int _prodTier;               // 0 — все, 1..3 — фильтр-просмотр каталога
        private int _ctxTier = 3;            // тир контекста: фабрика/аэродром или ЛУЧШИЙ инженер выделения
        private int _ctxTierActual = 3;      // ФАКТИЧЕСКИЙ тир без апгрейдов очереди — суффикс «T1→T2», пока наливается (плейтест 2026-11-08 «мгновенно прокачивается»)
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
            _canvasLocVersion = Loc.Version; // построено под текущую локаль
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
            if (_canvas != null && _canvasLocVersion != Loc.Version)
            {
                // Смена языка «на лету» (v0.153): сносим канвас — EnsureUi ниже
                // пересоберёт его со всеми надписями в новой локали (§7: смена
                // через Version-счётчик, не таймер/флаг).
                _canvasLocVersion = Loc.Version;
                Destroy(_canvas.gameObject);
                _canvas = null;
            }
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
                UpdateTypeChipSalvo(); // ПКМ по ракетному чипу — заряд «Залпа» этим типом
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
            _queueHash = -1; // сцена/локаль могла не измениться — иначе строка очереди осталась бы пустой
            _loVersion = -1;
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
            _canvasLocVersion = Loc.Version; // построено под текущую локаль
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

        private static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                Destroy(t.GetChild(i).gameObject);
        }
    }
}

