using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SupremeNuclear
{
    /// <summary>
    /// «Скримиш» — быстрый старт боя из главного меню (юзер-решение 2026-11-02,
    /// канон «широкие мазки»: карта/сид/игроки/AI — остальное в JSON-шаблоне).
    /// Кнопку меню НЕ патчим Harmony (§7-3, композиция): опросом раз в 0.5 с
    /// находим MainMenu и клонируем его missionsButton — паттерн нативного
    /// MissionSelectGroupButtons.CreateTabButtons; кнопка — ребёнок сцены,
    /// умирает вместе с ней. Панель — свой канвас поверх меню (sortingOrder
    /// выше SupcomUi), стиль/палитра — SupcomUi. Старт — SkirmishService:
    /// генерация → сейв в User-группу → нативный StartHost мимо редактора;
    /// режим командира поднимется сам (CommanderModeController.PendingAutoActivateMission).
    /// </summary>
    public sealed class SkirmishPanel : MonoBehaviour
    {
        private Canvas _canvas;
        private Font _font;
        private RectTransform _root;
        /// <summary>Локаль, под которую собрана панель: смена языка «на лету» (Loc.Version) —
        /// пересборка на Show (панель DontDestroyOnLoad, надписи встроены один раз).</summary>
        private int _builtLocVersion = -1;
        private RectTransform _backdrop; // модальный фон: глотает клики под панелью
        private readonly List<Button> _styleButtons = new List<Button>();
        private readonly List<string> _styleTemplates = new List<string>();
        private readonly List<string> _styleNames = new List<string>();
        private InputField _seedInput;
        private readonly Button[] _playerButtons = new Button[2];
        private Button _aiButton;
        private Button _startButton;
        private Text _status;

        private string _template;   // выбранный шаблон-пресет стиля
        private int _players = 2;   // панель переключает 1×1/2×2 (в ключ карты — ScenarioBuild)
        private bool _starting;     // «В БОЙ» уже нажат: генерация синхронна, повтор не нужен
        private bool _startNextFrame;
        private Button _menuButton; // клон в меню (живёт в сцене меню, чистится Unity-нуллом)
        private Button _langButton; // свитчер языка под кнопкой Скримиш (тоже ребёнок сцены)
        private int _menuLabelsLocVersion = -1; // локаль, под которую подписаны кнопки меню
        private float _nextScanAt;
        private float _nextErrorLogAt = -10f;
        private static TMP_FontAsset _tmpCyrillicFont; // динамический TMP-шрифт с кириллицей
        private bool _navWasEnabled = true; // sendNavigationEvents до открытия панели (парность №9)

        // Клики опросом (канон №18: uGUI-события в этой игре ненадёжны — в меню
        // клики по кнопкам панели не доходили, плейтест 2026-11-02): нажатие без
        // движения <6 px = клик, hit-test по прямоугольнику (как зум миникарты).
        private readonly List<KeyValuePair<RectTransform, Action>> _clickables =
            new List<KeyValuePair<RectTransform, Action>>();
        private Vector2 _pressAt;
        private bool _pressValid;

        private void Awake()
        {
            // Точный признак ухода из меню (вместо ошибавшегося опроса): любая
            // загрузка сцены — панель меню больше не нужна, кнопку унесла сцена.
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _menuButton = null; // кнопка — ребёнок старой сцены, мертва
            _langButton = null;
            if (_root != null && _root.gameObject.activeSelf)
                Hide();
        }

        /// <summary>Клик «Скримиш»: панель открывается ПОВЕРХ меню — модальный
        /// фон глотает клики вокруг, персистентный нативный слушатель уже мёртв
        /// (подмена onClick), Esc панель не закрывает. Сцену EMPTY (мёртвая
        /// константа игры, в build settings её может не быть) — не используем,
        /// плейтест 2026-11-03.</summary>
        private void EnterLobby()
        {
            Log.Info("Skirmish: клик — открываю лобби (сцена " +
                     SceneManager.GetActiveScene().name + ")");
            Show();
        }

        private void Update()
        {
            try
            {
                UpdateSafe();
            }
            catch (System.Exception e)
            {
                if (Time.unscaledTime - _nextErrorLogAt > 5f)
                {
                    _nextErrorLogAt = Time.unscaledTime;
                    Log.Error("SkirmishPanel: сбой Update — " + e.GetType().Name + ": " +
                              e.Message + " | " + e.StackTrace);
                }
            }
        }

        private void UpdateSafe()
        {
            // ЗАКРЫТИЕ ПО Esc УБРАНО: фантомные HID-клавиатуры (ВР/педали юзера)
            // шлют Escape непрерывно — панель закрывалась «мгновенно» сама.
            // Закрытие — только «×» (poll-клик мышью) или старт.
            PollClicks();

            // Генерация — кадром ПОСЛЕ статуса «Генерация…», иначе экран не успеет
            // её показать (сборка мира синхронная, кадр встанет — осознанно, §12).
            if (_startNextFrame)
            {
                _startNextFrame = false;
                DoStart();
                return;
            }

            // Скан сцены меню — не каждый кадр (поиск по сцену, перф §12-45).
            // Скрытие панели тут БОЛЬШЕ НЕТ: опрос FindObjectOfType ошибочно
            // возвращал null ~1 с на живом меню — панель сама закрывалась
            // («селектор закрывается сразу», плейтест 2026-11-02). Закрытие —
            // только ×/Esc/старт; реальный уход из меню ловит sceneLoaded (Awake).
            if (Time.unscaledTime < _nextScanAt)
                return;
            _nextScanAt = Time.unscaledTime + 0.5f;
            var menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
            if (menu == null)
                return; // меню нет (не загрузилось/ушли) — просто не инъектируем
            InjectMenuButton(menu);
            // Смена языка «на лету» (свитчер или менеджер конфига): подписи кнопок
            // меню встраиваются при инъекции и живых рефрешей не имеют — перечитываем
            // по Loc.Version (§7), не чаще скана меню (0.5 с).
            if (_menuLabelsLocVersion != Loc.Version &&
                (_menuButton != null || _langButton != null))
            {
                _menuLabelsLocVersion = Loc.Version;
                RefreshMenuLabels();
            }
        }

        // ---------- кнопка в главном меню ----------

        /// <summary>Клон нативной missionsButton («МИССИИ») рядом с ней же:
        /// лейаут родителя раскладывает сам, TMP-текст кнопки — «Скримиш».
        /// Idempotent: повторный вход в меню строит кнопку заново.</summary>
        private void InjectMenuButton(MainMenu menu)
        {
            if (_menuButton != null)
                return; // уже стоит (или старый экземпляр уничтожен сценой → null)
            try
            {
                var field = AccessTools.Field(typeof(MainMenu), "missionsButton");
                var source = field?.GetValue(menu) as Button;
                if (source == null)
                {
                    Log.Warn("Skirmish: не нашёл кнопку меню (missionsButton) — вход из меню недоступен");
                    return;
                }
                var clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
                clone.name = "SN_SkirmishButton";
                // Нативный обработчик пришит в СЦЕНЕ (персистентный слушатель
                // SelectMissions): Instantiate его КОПИРУЕТ, RemoveAllListeners не
                // снимает, рефлексивный Clear() внутреннего PersistentCallGroup на
                // этой сборке НЕ СРАБОТАЛ (плейтест 2026-11-03: клик открывал и
                // лобби, и «выбор миссий» — два LoadScene, побеждал последний).
                // НЕПРОБИВАЕМОЕ решение: подменяем САМО событие — новый пустой
                // ButtonClickedEvent в поле m_OnClick; persistent-вызовы остаются
                // в выброшенном объекте. До/после — лог для контроля.
                Log.Info("Skirmish: клон кнопки, персистентных слушателей " +
                         clone.onClick.GetPersistentEventCount());
                // В этой сборке Unity у Button.onClick ЕСТЬ публичный сеттер
                // (UnityEngine.UI: set { m_OnClick = value; }) — подмена события
                // напрямую, без рефлексии.
                clone.onClick = new Button.ButtonClickedEvent();
                clone.onClick.AddListener(EnterLobby);
                Log.Info("Skirmish: слушатели заменены, персистентных осталось " +
                         clone.onClick.GetPersistentEventCount());
                // Шрифт меню (Brass Mono Regular) НЕ содержит кириллицы — русский
                // текст превращался в тофу □□□ (плейтест 2026-11-02: Player.log
                // «not found in the [Brass Mono Regular] font asset»). Подменяем
                // на ДИНАМИЧЕСКИЙ TMP-шрифт из LegacyRuntime.ttf (кириллица есть);
                // не создался — читаемый латинский фолбэк.
                var tmp = clone.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                {
                    var font = CyrillicTmpFont();
                    if (font != null)
                    {
                        tmp.font = font;
                        tmp.text = Loc.Tr("Скримиш");
                    }
                    else
                    {
                        tmp.text = "Skirmish";
                    }
                }
                _menuButton = clone;
                Log.Info("Skirmish: кнопка меню добавлена");
                InjectLanguageButton(source);
            }
            catch (System.Exception e)
            {
                Log.Error("Skirmish: кнопка меню не встала — " + e.Message);
            }
        }

        /// <summary>Свитчер языка ПОД кнопкой «Скримиш» (юзер-решение 2026-11-04: у игры
        /// своего переключения языка нет, лезть в конфиг игрок не обязан). Второй клон
        /// missionsButton. Клик — цикл ru ↔ en с применением на лету (Settings.SetLanguage →
        /// Loc.Bootstrap → пересборка UI по Loc.Version). Подпись латиницей без
        /// Loc.Tr — TMP-шрифт меню кириллицы не имеет (§12-63), а назначение кнопки
        /// понятно в любом языке.</summary>
        private void InjectLanguageButton(Button source)
        {
            if (_langButton != null)
                return; // уже стоит (умерший экземпляр чистит Unity-нуллом)
            try
            {
                var clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
                clone.name = "SN_LanguageButton";
                clone.onClick = new Button.ButtonClickedEvent(); // срез персистентных слушателей (как у Скримиша)
                clone.onClick.AddListener(CycleLanguage);
                var tmp = clone.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.text = LanguageLabel(); // латиница: шрифт меню не трогаем (см. LanguageLabel)
                    // Подпись длиннее «МИССИИ» — автоподгон, иначе текст вылезает
                    // за края кнопки (артефакты плейтеста 2026-11-04).
                    tmp.enableAutoSizing = true;
                    tmp.fontSizeMin = 12f;
                    tmp.fontSizeMax = Mathf.Max(12f, tmp.fontSize);
                    tmp.ForceMeshUpdate(); // сразу чистая сетка — без хвостов старого текста
                }
                // Страховка наложения: без лейаут-группы у родителя клон ложится
                // ровно на Скримиш (второй артефакт того же плейтеста).
                var parentRt = source.transform.parent as RectTransform;
                bool laidOut = parentRt != null &&
                    (parentRt.GetComponent<VerticalLayoutGroup>() != null ||
                     parentRt.GetComponent<HorizontalLayoutGroup>() != null ||
                     parentRt.GetComponent<GridLayoutGroup>() != null);
                if (!laidOut)
                {
                    var rt = (RectTransform)clone.transform;
                    var menuRt = (RectTransform)source.transform;
                    rt.anchoredPosition = menuRt.anchoredPosition +
                        new Vector2(0f, -menuRt.rect.height - 8f);
                }
                _langButton = clone;
                _menuLabelsLocVersion = Loc.Version;
                Log.Info("Skirmish: свитчер языка добавлен (" + Settings.Language +
                         ", лейаут родителя: " + (laidOut ? "есть" : "НЕТ — позиция вручную") + ")");
            }
            catch (System.Exception e)
            {
                Log.Error("Skirmish: свитчер языка не встал — " + e.Message);
            }
        }

        /// <summary>Подпись свитчера — ТОЛЬКО латиница («Language: ru»): TMP-шрифт меню
        /// Brass Mono кириллицы не имеет, а динамический ассет с ней не создаётся
        /// (CreateFontAsset вернул null молча, §12-63) — двуязычная подпись давала
        /// тофу □□□□ (плейтест 2026-11-04). auto/файл локали показываем разрешённым
        /// значением — по нему цикл и кликает.</summary>
        private static string LanguageLabel()
        {
            string v = Settings.Language == "ru" || Settings.Language == "en"
                ? Settings.Language
                : Loc.Language;
            return "Language: " + v;
        }

        /// <summary>Клик свитчера: ru ↔ en, живое применение, подпись сразу.
        /// Цикл по ЭФФЕКТИВНОМУ языку (Loc.Language), не по настройке: у англоязычного
        /// игрока настройка auto уже разрешена в en — клик обязан дать видимую смену.</summary>
        private void CycleLanguage()
        {
            string next = Loc.Language == "ru" ? "en" : "ru";
            Settings.SetLanguage(next);
            _menuLabelsLocVersion = Loc.Version;
            RefreshMenuLabels();
            Log.Info("Skirmish: язык — " + Settings.Language + " (таблицы: " + Loc.Language + ")");
        }

        /// <summary>Перечитать подписи обеих кнопок меню (инъекция ставит их один раз;
        /// живая смена языка — через этот путь). ForceMeshUpdate — без хвоста
        /// прежнего текста в тот же кадр.</summary>
        private void RefreshMenuLabels()
        {
            if (_menuButton != null) // Unity fake-null
            {
                var tmp = _menuButton.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                {
                    var font = CyrillicTmpFont();
                    if (font != null)
                    {
                        tmp.font = font;
                        tmp.text = Loc.Tr("Скримиш");
                    }
                    else
                        tmp.text = "Skirmish";
                    tmp.ForceMeshUpdate();
                }
            }
            if (_langButton != null)
            {
                var tmp = _langButton.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.text = LanguageLabel();
                    tmp.ForceMeshUpdate();
                }
            }
        }

        /// <summary>Динамический TMP_FontAsset из LegacyRuntime.ttf (кэш): у шрифта
        /// меню нет кириллицы, динамический ассет рендерит любые глифы исходного Font.
        /// CreateFontAsset может вернуть null БЕЗ исключения (плейтест 2026-11-04:
        /// ни одного предупреждения в логе, «Скримиш» латиницей) — тогда кнопки
        /// меню молча уходят в латинские фолбэки (§12-63).</summary>
        private static TMP_FontAsset CyrillicTmpFont()
        {
            if (_tmpCyrillicFont != null)
                return _tmpCyrillicFont;
            try
            {
                Font source = SupcomUi.Instance != null && SupcomUi.Instance.UiFont != null
                    ? SupcomUi.Instance.UiFont
                    : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                _tmpCyrillicFont = TMP_FontAsset.CreateFontAsset(source);
                if (_tmpCyrillicFont == null)
                    Log.Warn("Skirmish: CreateFontAsset вернул null без исключения — " +
                             "кнопки меню латиницей, кириллицы в шрифте меню нет (§12-63)");
            }
            catch (System.Exception e)
            {
                Log.Warn("Skirmish: TMP-шрифт с кириллицей не создан — кнопка будет латиницей (" +
                         e.Message + ")");
            }
            return _tmpCyrillicFont;
        }

        // ---------- панель ----------

        public void Show()
        {
            // Смена языка «на лету» (v0.153): панель DontDestroyOnLoad — надписи
            // встроены один раз, при смене локали пересобираем с нуля.
            if (_canvas != null && _builtLocVersion != Loc.Version)
            {
                Destroy(_canvas.gameObject);
                _canvas = null;
                _root = null;
                _backdrop = null;
            }
            EnsureBuilt();
            if (_root == null)
                return;
            RescanStyles(); // пресеты могли добавить/убрать с прошлого открытия
            RefreshDynamic();
            _root.gameObject.SetActive(true);
            if (_backdrop != null)
                _backdrop.gameObject.SetActive(true);
            // МОДАЛЬНОСТЬ по вводу (плейтест 2026-11-02: джойстик/педали юзера
            // непрерывно шлют навигацию — «Submit» жал НАТИВНЫЕ кнопки меню,
            // сцена менялась и панель закрывалась). Пока панель открыта — гасим
            // navigation-события: геймпад/клавиатура не «жмут» меню; мышь живёт
            // (наши кнопки и так poll-клики). Парность: восстановление в Hide.
            if (EventSystem.current != null)
            {
                _navWasEnabled = EventSystem.current.sendNavigationEvents;
                EventSystem.current.sendNavigationEvents = false;
            }
            // Диагностика кликов (плейтест 2026-11-02: клики в панели не доходили):
            // если это снова случится — видно, был ли EventSystem/raycast жив.
            Log.Info("Skirmish: панель открыта — EventSystem " +
                     (EventSystem.current != null ? EventSystem.current.GetType().Name : "ОТСУТСТВУЕТ") +
                     ", шаблон " + (_template ?? "нет") + ", игроков " + _players);
        }

        private void Hide()
        {
            if (_root != null)
                _root.gameObject.SetActive(false);
            if (_backdrop != null)
                _backdrop.gameObject.SetActive(false);
            // Парность №9: что погасили на входе (Show) — вернули на всех выходах.
            if (EventSystem.current != null)
                EventSystem.current.sendNavigationEvents = _navWasEnabled;
        }

        /// <summary>Канвас и панель строятся один раз (DontDestroyOnLoad, сцены
        /// их не уносят); сбой построения — лог, следующие Show повторит попытку.</summary>
        private void EnsureBuilt()
        {
            if (_root != null)
                return;
            try
            {
                BuildSafe();
            }
            catch (System.Exception e)
            {
                Log.Error("SkirmishPanel: сборка не удалась — " + e.GetType().Name + ": " + e.Message);
                if (_canvas != null && _root == null)
                    Destroy(_canvas.gameObject); // недострой не должен висеть поверх меню
                _canvas = null;
            }
        }

        private void BuildSafe()
        {
            _font = SupcomUi.Instance != null ? SupcomUi.Instance.UiFont : null;
            if (_font == null)
            {
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch { }
            }

            if (EventSystem.current == null)
            {
                var es = new GameObject("SN_SkirmishEventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                DontDestroyOnLoad(es);
            }

            var canvasGo = new GameObject("SN_SkirmishCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30001; // выше SupcomUi — панель поверх всего в меню
            DontDestroyOnLoad(canvasGo);
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f) / Mathf.Clamp(Settings.UiScale, 0.75f, 2f);
            scaler.matchWidthOrHeight = 0.5f;

            BuildPanel();
            _builtLocVersion = Loc.Version; // панель построена под текущую локаль
        }

        private void BuildPanel()
        {
            _clickables.Clear(); // пересборка (смена локали) — старые прямоугольники умерли
            // МОДАЛЬНЫЙ ФОН: полупрозрачная полноэкранная заглушка ПОД панелью
            // (создана раньше — ниже по порядку отрисовки). Image.raycastTarget
            // глотает ВСЕ клики, пока панель открыта — фантомные устройства
            // юзера (ВР/педали эмулируют мышь/клавиатуру, плейтест 2026-11-02)
            // физически не могут нажать нативные кнопки меню ПОД ней — сцена не
            // меняется, панель не закрывается. Никаких перехватов ввода.
            _backdrop = Panel("ModalBackdrop", _canvas.transform,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                new Color(0f, 0f, 0f, 0.55f));

            // Все дети — top-anchored, курсор y идёт вниз от верхнего края;
            // высота панели 450 при глубине контента 426 (запас 24).
            _root = Panel("Skirmish", _canvas.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-270f, -225f), new Vector2(270f, 225f), SupcomUi.PanelBg);

            // Заголовок + «×».
            var title = Label(_root, "title", Loc.Tr("Скримиш"), 26, SupcomUi.Accent, TextAnchor.MiddleLeft);
            var titleRt = (RectTransform)title.transform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(0.55f, 1f);
            titleRt.offsetMin = new Vector2(18f, -40f);
            titleRt.offsetMax = new Vector2(0f, -8f);
            var close = Button_(_root, "close", "×", Hide, 20);
            var closeRt = (RectTransform)close.transform;
            closeRt.anchorMin = new Vector2(1f, 1f);
            closeRt.anchorMax = new Vector2(1f, 1f);
            closeRt.offsetMin = new Vector2(-46f, -38f);
            closeRt.offsetMax = new Vector2(-10f, -6f);

            float y = -46f;
            // Карта: 4 кнопки-стиля в ряд.
            y = LabelRow(Loc.Tr("Карта"), y, out RectTransform mapRow);
            BuildStyleButtons(mapRow);

            // Сид: поле + «Случайный» (пусто = случайный — подсказка внизу панели).
            y = LabelRow(Loc.Tr("Сид"), y, out RectTransform seedRow);
            BuildSeedRow(seedRow);

            // Игроки: 1×1 / 2×2 + тумблер AI-командира в той же строке.
            y = LabelRow(Loc.Tr("Игроки"), y, out RectTransform playersRow);
            BuildPlayersRow(playersRow);

            // Статус (ошибки/«Генерация…»).
            _status = Label(_root, "status", "", 15, SupcomUi.TextDim, TextAnchor.MiddleLeft, multiLine: true);
            var statusRt = (RectTransform)_status.transform;
            statusRt.anchorMin = new Vector2(0f, 1f);
            statusRt.anchorMax = new Vector2(1f, 1f);
            statusRt.offsetMin = new Vector2(16f, y - 44f);
            statusRt.offsetMax = new Vector2(-16f, y);

            // «В БОЙ» + подсказка (сид/MP).
            _startButton = Button_(_root, "start", Loc.Tr("В БОЙ"), OnStartClicked, 24);
            var startRt = (RectTransform)_startButton.transform;
            startRt.anchorMin = new Vector2(0.5f, 1f);
            startRt.anchorMax = new Vector2(0.5f, 1f);
            startRt.offsetMin = new Vector2(-110f, y - 104f);
            startRt.offsetMax = new Vector2(110f, y - 52f);
            var back = Button_(_root, "back", Loc.Tr("Закрыть"), Hide, 15);
            var backRt = (RectTransform)back.transform;
            backRt.anchorMin = new Vector2(0f, 1f);
            backRt.anchorMax = new Vector2(0f, 1f);
            backRt.offsetMin = new Vector2(16f, y - 104f);
            backRt.offsetMax = new Vector2(130f, y - 52f);
            var hint = Label(_root, "hint",
                Loc.Tr("Сид пуст — случайная карта каждый бой. Мультиплеер: создайте лобби в разделе «Мультиплеер» — сгенерированные миссии появятся в списке (у всех игроков должен стоять мод)"),
                13, SupcomUi.TextDim, TextAnchor.MiddleLeft, multiLine: true);
            var hintRt = (RectTransform)hint.transform;
            hintRt.anchorMin = new Vector2(0f, 1f);
            hintRt.anchorMax = new Vector2(1f, 1f);
            hintRt.offsetMin = new Vector2(16f, y - 162f);
            hintRt.offsetMax = new Vector2(-16f, y - 110f);

            _root.gameObject.SetActive(false);
            _backdrop.gameObject.SetActive(false);
        }

        /// <summary>Заголовок секции + фон-строка контролов под ним; возвращает
        /// новый курсор y и фон строки (высота 40, кнопки 32 по центру). Текст
        /// приходит УЖЕ переведённый — литерал Loc.Tr виден чекеру локалей.</summary>
        private float LabelRow(string text, float y, out RectTransform row)
        {
            var lbl = Label(_root, "row", text, 15, SupcomUi.TextDim, TextAnchor.MiddleLeft);
            var rt = (RectTransform)lbl.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0.3f, 1f);
            rt.offsetMin = new Vector2(16f, y - 22f);
            rt.offsetMax = new Vector2(0f, y);
            y -= 26f;
            row = Panel("rowBg", _root, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, y - 40f), new Vector2(-16f, y), SupcomUi.PanelBg2);
            return y - 46f;
        }

        private void BuildStyleButtons(RectTransform row)
        {
            _styleButtons.Clear();
            _styleTemplates.Clear();
            _styleNames.Clear();
            var styles = SkirmishService.ListStyles();
            var labels = new Dictionary<string, string>
            {
                { "land", Loc.Tr("Равнина") },
                { "coast", Loc.Tr("Побережье") },
                { "seas", Loc.Tr("Моря") },
                { "islands", Loc.Tr("Острова") },
            };
            const float w = 118f, gap = 8f, h = 32f; // 4×118+3×8+8=504 — во внутренней ширине 508
            for (int i = 0; i < 4; i++)
            {
                string style = SkirmishService.StyleOrder[i];
                var btn = Button_(row, "style_" + style,
                    labels.TryGetValue(style, out string label) ? label : style, () => SelectStyle(style), 15);
                var rt = (RectTransform)btn.transform;
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(0f, 0.5f);
                rt.offsetMin = new Vector2(8f + i * (w + gap), -h * 0.5f);
                rt.offsetMax = new Vector2(8f + i * (w + gap) + w, h * 0.5f);
                _styleButtons.Add(btn);
                _styleNames.Add(style);
                // Пресет стиля есть? (первый файл его стиля из MapGen/*.json)
                string found = null;
                foreach (var kv in styles)
                    if (kv.Key == style)
                    {
                        found = kv.Value;
                        break;
                    }
                _styleTemplates.Add(found);
                btn.interactable = found != null;
            }
            // Дефолт: шаблон из конфига, если он среди пресетов; иначе первый доступный.
            _template = null;
            foreach (var kv in styles)
                if (kv.Value == Settings.MapGenTemplate)
                {
                    _template = kv.Value;
                    break;
                }
            if (_template == null)
                foreach (string t in _styleTemplates)
                    if (t != null)
                    {
                        _template = t;
                        break;
                    }
        }

        /// <summary>Свежий скан пресетов в уже построенные кнопки (при каждом Show):
        /// файлы MapGen/*.json могли появиться/исчезнуть с прошлого открытия.</summary>
        private void RescanStyles()
        {
            var styles = SkirmishService.ListStyles();
            for (int i = 0; i < _styleNames.Count; i++)
            {
                string found = null;
                foreach (var kv in styles)
                    if (kv.Key == _styleNames[i])
                    {
                        found = kv.Value;
                        break;
                    }
                _styleTemplates[i] = found;
                if (_styleButtons[i] != null)
                    _styleButtons[i].interactable = found != null;
            }
            bool currentOk = false;
            foreach (string t in _styleTemplates)
                if (t != null && t == _template)
                {
                    currentOk = true;
                    break;
                }
            if (currentOk)
                return;
            _template = null;
            foreach (string t in _styleTemplates)
                if (t != null)
                {
                    _template = t;
                    break;
                }
        }

        private void BuildSeedRow(RectTransform row)
        {
            // Поле сида: uGUI InputField (правило №15 — Text только в ctor).
            var inputGo = new GameObject("seed", typeof(RectTransform), typeof(Image), typeof(InputField));
            var inputRt = (RectTransform)inputGo.transform;
            inputRt.SetParent(row, false);
            inputRt.anchorMin = new Vector2(0f, 0.5f);
            inputRt.anchorMax = new Vector2(0f, 0.5f);
            inputRt.offsetMin = new Vector2(8f, -16f);
            inputRt.offsetMax = new Vector2(208f, 16f);
            inputGo.GetComponent<Image>().color = SupcomUi.BtnBg;
            var input = inputGo.GetComponent<InputField>();
            input.contentType = InputField.ContentType.IntegerNumber; // сид бывает отрицательным
            input.caretColor = SupcomUi.Accent;
            var text = MakeInputText("text", SupcomUi.TextMain, 16);
            text.transform.SetParent(inputGo.transform, false);
            input.textComponent = text;
            var placeholder = MakeInputText("ph", SupcomUi.TextDim, 16);
            placeholder.text = Loc.Tr("случайный");
            placeholder.transform.SetParent(inputGo.transform, false);
            input.placeholder = placeholder;
            input.text = Settings.MapGenSeed != 0 ? Settings.MapGenSeed.ToString() : "";
            _seedInput = input;
            // Поле сида — тоже опросный клик (uGUI-фокус в меню не доставлялся):
            // активируем ввод вручную, Enter/клик-мимо деактивирует.
            _clickables.Add(new KeyValuePair<RectTransform, Action>(inputRt,
                () => input.ActivateInputField()));

            var dice = Button_(row, "dice", Loc.Tr("Случайный"), OnDiceClicked, 15);
            var diceRt = (RectTransform)dice.transform;
            diceRt.anchorMin = new Vector2(0f, 0.5f);
            diceRt.anchorMax = new Vector2(0f, 0.5f);
            diceRt.offsetMin = new Vector2(216f, -16f);
            diceRt.offsetMax = new Vector2(216f + 130f, 16f);
        }

        private Text MakeInputText(string name, Color color, int size)
        {
            // Только ctor-Text (инвариант №15), родителя выставляет вызывающий.
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6f, 2f);
            rt.offsetMax = new Vector2(-6f, -2f);
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.color = color;
            t.alignment = TextAnchor.MiddleLeft;
            return t;
        }

        private void BuildPlayersRow(RectTransform row)
        {
            string[] labels = { Loc.Tr("1 на 1"), Loc.Tr("2 на 2") };
            int[] values = { 2, 4 };
            for (int i = 0; i < 2; i++)
            {
                int players = values[i];
                var btn = Button_(row, "players_" + players, labels[i], () =>
                {
                    _players = players;
                    RefreshDynamic();
                }, 15);
                var rt = (RectTransform)btn.transform;
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(0f, 0.5f);
                rt.offsetMin = new Vector2(8f + i * 136f, -16f);
                rt.offsetMax = new Vector2(8f + i * 136f + 128f, 16f);
                _playerButtons[i] = btn;
            }

            _aiButton = Button_(row, "ai", "", OnAiClicked, 15);
            var aiRt = (RectTransform)_aiButton.transform;
            aiRt.anchorMin = new Vector2(0f, 0.5f);
            aiRt.anchorMax = new Vector2(0f, 0.5f);
            aiRt.offsetMin = new Vector2(296f, -16f);
            aiRt.offsetMax = new Vector2(296f + 180f, 16f);
        }

        // ---------- состояние/действия ----------

        /// <summary>Подсветки, зависящие от выбора: стиль/игроки/AI/статус.</summary>
        private void RefreshDynamic()
        {
            for (int i = 0; i < _styleButtons.Count; i++)
            {
                var img = _styleButtons[i].GetComponent<Image>();
                if (img != null)
                    img.color = _styleTemplates[i] == _template && _template != null
                        ? SupcomUi.BtnActive : SupcomUi.BtnBg;
            }
            for (int i = 0; i < _playerButtons.Length; i++)
            {
                var img = _playerButtons[i] != null ? _playerButtons[i].GetComponent<Image>() : null;
                int[] values = { 2, 4 };
                if (img != null)
                    img.color = values[i] == _players ? SupcomUi.BtnActive : SupcomUi.BtnBg;
            }
            var aiImg = _aiButton != null ? _aiButton.GetComponent<Image>() : null;
            bool ai = AiProfile.Load().enabled;
            if (aiImg != null)
                aiImg.color = ai ? SupcomUi.BtnActive : SupcomUi.BtnBg;
            if (_aiButton != null)
            {
                var lbl = _aiButton.GetComponentInChildren<Text>();
                if (lbl != null)
                    lbl.text = Loc.F("AI-командир: {0}", ai ? Loc.Tr("вкл") : Loc.Tr("выкл"));
            }
            if (_startButton != null)
                _startButton.interactable = _template != null && !_starting;
            if (_template == null)
                SetStatus(Loc.Tr("Нет пресетов карт — положи JSON в BepInEx/plugins/SupremeNuclear/MapGen/"), SupcomUi.WarnRed);
            else
                SetStatus("", SupcomUi.TextDim);
        }

        private void SelectStyle(string style)
        {
            int idx = _styleNames.IndexOf(style);
            if (idx < 0 || _styleTemplates[idx] == null)
                return;
            _template = _styleTemplates[idx];
            RefreshDynamic();
        }

        private void OnDiceClicked()
        {
            if (_seedInput != null)
                _seedInput.text = ""; // пусто = случайный каждый бой
        }

        private void OnAiClicked()
        {
            var profile = AiProfile.Load();
            profile.enabled = !profile.enabled;
            AiProfile.Save(profile);
            Log.Info("Skirmish: AI-командир → " + (profile.enabled ? "вкл" : "выкл"));
            RefreshDynamic();
        }

        private void OnStartClicked()
        {
            if (_starting || _template == null)
                return;
            _starting = true;
            // Шаблон и сид — в конфиг (общий источник с Ctrl+G: один сид = одна раскладка).
            string seedText = _seedInput != null ? _seedInput.text : "";
            Settings.SetMapGen(_template, SkirmishService.ParseSeed(seedText));
            Log.Info("Skirmish: «В БОЙ» — шаблон " + _template + ", сид-текст '" +
                     seedText.Trim() + "', игроков " + _players);
            SetStatus(Loc.Tr("Генерация карты…"), SupcomUi.WarnYellow);
            RefreshDynamic();
            _startNextFrame = true; // кадр на отрисовку статуса, генерация — следующим
        }

        private void DoStart()
        {
            try
            {
                Log.Info("Skirmish: генерация пошла (шаблон " + _template + ", сид " +
                         Settings.MapGenSeed + ", игроков " + _players + ")");
                int seed = Settings.MapGenSeed;
                if (SkirmishService.Start(_template, seed, _players, out string error))
                {
                    Hide(); // сцена миссии сменит меню; панель больше не нужна
                    return;
                }
                SetStatus(error, SupcomUi.WarnRed);
            }
            catch (System.Exception e)
            {
                Log.Error("SkirmishPanel: старт сорвался — " + e);
                SetStatus(Loc.Tr("Старт не удался — подробности в логе"), SupcomUi.WarnRed);
            }
            finally
            {
                _starting = false;
                RefreshDynamic();
            }
        }

        private void SetStatus(string text, Color color)
        {
            if (_status == null)
                return;
            _status.text = text;
            _status.color = color;
        }

        // ---------- uGUI-примитивы (зеркало SupcomUi, панель живёт на своём канвасе) ----------

        private RectTransform Panel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax, Color bg)
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

        private Text Label(Transform parent, string name, string text, int size, Color color,
            TextAnchor align, bool multiLine = false)
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
            if (!multiLine)
                t.horizontalOverflow = HorizontalWrapMode.Overflow; // однострочные не переносятся
            return t;
        }

        private Button Button_(Transform parent, string name, string label, Action onClick, int fontSize = 16)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            go.GetComponent<Image>().color = SupcomUi.BtnBg;
            // Клик — ТОЛЬКО опросом (PollClicks): onClick-события uGUI в сцене меню
            // не доставлялись (плейтест 2026-11-02) — кнопка чисто визуальная.
            _clickables.Add(new KeyValuePair<RectTransform, Action>(rt, onClick));
            var t = Label(go.transform, "lbl", label, fontSize, SupcomUi.TextMain, TextAnchor.MiddleCenter);
            t.raycastTarget = false;
            return go.GetComponent<Button>();
        }

        /// <summary>Опросный клик: pointer-down → pointer-up без движения (<6 px)
        /// по прямоугольнику кнопки (ScreenSpaceOverlay — камера null, как в
        /// кликах миникарты). Гейта Typing НЕТ: набор сида не должен глушить
        /// клик по «В БОЙ» (чата в меню нет). Мёртвые ссылки чистятся на лету.</summary>
        private void PollClicks()
        {
            if (_root == null || !_root.gameObject.activeSelf)
                return;
            if (Input.GetMouseButtonDown(0))
            {
                _pressAt = Input.mousePosition;
                _pressValid = true;
            }
            if (!Input.GetMouseButtonUp(0) || !_pressValid)
                return;
            _pressValid = false;
            Vector2 up = Input.mousePosition;
            if ((up - _pressAt).sqrMagnitude > 36f)
                return; // сдвиг >6 px — это драг, не клик
            // Клик мимо поля сида снимает фокус ввода (если он был).
            if (_seedInput != null && _seedInput.isFocused &&
                !RectTransformUtility.RectangleContainsScreenPoint(
                    (RectTransform)_seedInput.transform, up, null))
            {
                _seedInput.DeactivateInputField();
            }
            for (int i = 0; i < _clickables.Count; i++)
            {
                var kv = _clickables[i];
                if (kv.Key == null)
                    continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(kv.Key, up, null))
                {
                    kv.Value?.Invoke();
                    return;
                }
            }
        }
    }
}
