using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SupremeNuclear
{
    /// <summary>
    /// «Скримиш» — вход в ЕДИНОЕ лобби (v3, юзер 2026-11-06: «убери синглплеер,
    /// общее лобби для сингла и MP»). Панель = только параметры создания лобби
    /// (имя/видимость/пароль/слоты) + «Создать лобби»: комната в EMPTY
    /// (SnLobbyService), где стол армий (ИИ/игроки/наблюдатели), карта
    /// (MapSetupBlock с превью) и условие победы. Соло = то же лобби с одной
    /// живой армией (победа «бесконечная»). Кнопку меню НЕ патчим Harmony
    /// (§7-3): опрос 0.5 с + клон missionsButton (панель — свой канвас 30001,
    /// poll-клики: uGUI-события в меню не доходят, плейтест 2026-11-02).
    /// </summary>
    public sealed class SkirmishPanel : MonoBehaviour
    {
        private Canvas _canvas;
        private Font _font;
        private RectTransform _root;
        private int _builtLocVersion = -1;
        private RectTransform _backdrop;
        private InputField _lobbyNameInput, _lobbyPassInput;
        private readonly Button[] _visButtons = new Button[3];
        private readonly Button[] _slotButtons = new Button[4];
        private Button _createLobbyButton;
        private Text _status;
        private int _slots = 8;
        private int _visibility; // 0 public / 1 friends / 2 private
        private bool _starting;

        private Button _menuButton;
        private Button _langButton;
        private int _menuLabelsLocVersion = -1;
        private float _nextScanAt;
        private float _nextErrorLogAt = -10f;
        private static TMP_FontAsset _tmpCyrillicFont;
        private bool _navWasEnabled = true;

        // Клики опросом (канон №18): нажатие без движения <6 px = клик.
        private readonly List<KeyValuePair<RectTransform, Action>> _clickables =
            new List<KeyValuePair<RectTransform, Action>>();
        private Vector2 _pressAt;
        private bool _pressValid;

        private void Awake()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            LobbyEvents.HostResult += OnHostResult;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            LobbyEvents.HostResult -= OnHostResult;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _menuButton = null;
            _langButton = null;
            if (_root != null && _root.gameObject.activeSelf)
                Hide();
        }

        private void EnterLobbyMenu()
        {
            Log.Info("Skirmish: клик — открываю панель лобби (сцена " +
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
                    Log.Error("SkirmishPanel: сбой Update — " + e);
                }
            }
        }

        private void UpdateSafe()
        {
            PollClicks();
            if (Time.unscaledTime < _nextScanAt)
                return;
            _nextScanAt = Time.unscaledTime + 0.5f;
            var menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
            if (menu == null)
                return;
            InjectMenuButton(menu);
            if (_menuLabelsLocVersion != Loc.Version &&
                (_menuButton != null || _langButton != null))
            {
                _menuLabelsLocVersion = Loc.Version;
                RefreshMenuLabels();
            }
        }

        // ---------- кнопка в главном меню ----------

        private void InjectMenuButton(MainMenu menu)
        {
            if (_menuButton != null)
                return;
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
                // Персистентный слушатель сцены срезается ПОДМЕНОЙ события целиком
                // (поле m_OnClick; рефлексивный Clear() не пробивал, плейтест
                // 2026-11-03: клик открывал и лобби, и «выбор миссий»).
                Log.Info("Skirmish: клон кнопки, персистентных слушателей " +
                         clone.onClick.GetPersistentEventCount());
                clone.onClick = new Button.ButtonClickedEvent();
                clone.onClick.AddListener(EnterLobbyMenu);
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
                        tmp.text = "Skirmish";
                }
                _menuButton = clone;
                InjectLanguageButton(source);
            }
            catch (System.Exception e)
            {
                Log.Error("Skirmish: кнопка меню не встала — " + e.Message);
            }
        }

        /// <summary>Свитчер языка ПОД кнопкой «Скримиш» (латиницей — §12-63).</summary>
        private void InjectLanguageButton(Button source)
        {
            if (_langButton != null)
                return;
            try
            {
                var clone = UnityEngine.Object.Instantiate(source, source.transform.parent);
                clone.name = "SN_LanguageButton";
                clone.onClick = new Button.ButtonClickedEvent();
                clone.onClick.AddListener(CycleLanguage);
                var tmp = clone.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.text = LanguageLabel();
                    tmp.enableAutoSizing = true;
                    tmp.fontSizeMin = 12f;
                    tmp.fontSizeMax = Mathf.Max(12f, tmp.fontSize);
                    tmp.ForceMeshUpdate();
                }
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
            }
            catch (System.Exception e)
            {
                Log.Error("Skirmish: свитчер языка не встал — " + e.Message);
            }
        }

        private static string LanguageLabel()
        {
            string v = Settings.Language == "ru" || Settings.Language == "en"
                ? Settings.Language
                : Loc.Language;
            return "Language: " + v;
        }

        private void CycleLanguage()
        {
            string next = Loc.Language == "ru" ? "en" : "ru";
            Settings.SetLanguage(next);
            _menuLabelsLocVersion = Loc.Version;
            RefreshMenuLabels();
        }

        private void RefreshMenuLabels()
        {
            if (_menuButton != null)
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
                    Log.Warn("Skirmish: CreateFontAsset вернул null — кнопки меню латиницей (§12-63)");
            }
            catch (System.Exception e)
            {
                Log.Warn("Skirmish: TMP-шрифт с кириллицей не создан — " + e.Message);
            }
            return _tmpCyrillicFont;
        }

        // ---------- панель (только создание лобби) ----------

        public void Show()
        {
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
            RefreshDynamic();
            _root.gameObject.SetActive(true);
            if (_backdrop != null)
                _backdrop.gameObject.SetActive(true);
            if (EventSystem.current != null)
            {
                _navWasEnabled = EventSystem.current.sendNavigationEvents;
                EventSystem.current.sendNavigationEvents = false;
            }
            Log.Info("Skirmish: панель открыта — EventSystem " +
                     (EventSystem.current != null ? EventSystem.current.GetType().Name : "ОТСУТСТВУЕТ"));
        }

        private void Hide()
        {
            if (_root != null)
                _root.gameObject.SetActive(false);
            if (_backdrop != null)
                _backdrop.gameObject.SetActive(false);
            if (EventSystem.current != null)
                EventSystem.current.sendNavigationEvents = _navWasEnabled;
        }

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
                Log.Error("SkirmishPanel: сборка не удалась — " + e);
                if (_canvas != null && _root == null)
                    Destroy(_canvas.gameObject);
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
            _canvas.sortingOrder = 30001;
            DontDestroyOnLoad(canvasGo);
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f) / Mathf.Clamp(Settings.UiScale, 0.75f, 2f);
            scaler.matchWidthOrHeight = 0.5f;
            BuildPanel();
            _builtLocVersion = Loc.Version;
        }

        private void BuildPanel()
        {
            _clickables.Clear();
            _backdrop = Panel("ModalBackdrop", _canvas.transform,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                new Color(0f, 0f, 0f, 0.55f));
            _root = Panel("Skirmish", _canvas.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-270f, -218f), new Vector2(270f, 218f), SupcomUi.PanelBg);

            var title = Label(_root, "title", Loc.Tr("Скримиш — создать лобби"), 22,
                SupcomUi.Accent, TextAnchor.MiddleLeft);
            var titleRt = (RectTransform)title.transform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.offsetMin = new Vector2(18f, -40f);
            titleRt.offsetMax = new Vector2(-46f, -8f);
            var close = Button_(_root, "close", "×", Hide, 20);
            var closeRt = (RectTransform)close.transform;
            closeRt.anchorMin = new Vector2(1f, 1f);
            closeRt.anchorMax = new Vector2(1f, 1f);
            closeRt.offsetMin = new Vector2(-46f, -38f);
            closeRt.offsetMax = new Vector2(-10f, -6f);

            float y = -56f;
            y = CtlRow(Loc.Tr("Лобби"), y, out RectTransform nameRow);
            _lobbyNameInput = MakeInput(nameRow, "name", Settings.LobbyName,
                InputField.ContentType.Standard, 142f, 396f);
            y = CtlRow(Loc.Tr("Видимость"), y, out RectTransform visRow);
            string[] visLabels = { Loc.Tr("Публичное"), Loc.Tr("Друзьям"), Loc.Tr("Приватное") };
            _visibility = Settings.LobbyVisibility == "public" ? 0
                : Settings.LobbyVisibility == "private" ? 2 : 1;
            for (int i = 0; i < 3; i++)
            {
                int v = i;
                _visButtons[i] = Button_(visRow, "vis_" + i, visLabels[i],
                    () => { _visibility = v; RefreshDynamic(); }, 14);
                var rt = (RectTransform)_visButtons[i].transform;
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(0f, 0.5f);
                rt.offsetMin = new Vector2(142f + i * 126f, -16f);
                rt.offsetMax = new Vector2(142f + i * 126f + 118f, 16f);
            }
            y = CtlRow(Loc.Tr("Пароль"), y, out RectTransform passRow);
            _lobbyPassInput = MakeInput(passRow, "pass", Settings.LobbyPassword,
                InputField.ContentType.Password, 142f, 396f);
            y = CtlRow(Loc.Tr("Слотов"), y, out RectTransform slotRow);
            int[] slotValues = { 4, 8, 12, 16 };
            for (int i = 0; i < 4; i++)
            {
                int v = slotValues[i];
                _slotButtons[i] = Button_(slotRow, "slots_" + v, v.ToString(),
                    () => { _slots = v; RefreshDynamic(); }, 14);
                var rt = (RectTransform)_slotButtons[i].transform;
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(0f, 0.5f);
                rt.offsetMin = new Vector2(142f + i * 94f, -16f);
                rt.offsetMax = new Vector2(142f + i * 94f + 86f, 16f);
            }
            _slots = Settings.LobbySlots;

            // Статус (ход создания/отказ).
            _status = Label(_root, "status", "", 14, SupcomUi.TextDim,
                TextAnchor.MiddleLeft, multiLine: true);
            var statusRt = (RectTransform)_status.transform;
            statusRt.anchorMin = new Vector2(0f, 1f);
            statusRt.anchorMax = new Vector2(1f, 1f);
            statusRt.offsetMin = new Vector2(16f, y - 78f);
            statusRt.offsetMax = new Vector2(-16f, y);

            _createLobbyButton = Button_(_root, "createLobby", Loc.Tr("Создать лобби"),
                OnCreateLobbyClicked, 22);
            var clRt = (RectTransform)_createLobbyButton.transform;
            clRt.anchorMin = new Vector2(0.5f, 1f);
            clRt.anchorMax = new Vector2(0.5f, 1f);
            clRt.offsetMin = new Vector2(-140f, y - 152f);
            clRt.offsetMax = new Vector2(140f, y - 96f);
            var hint = Label(_root, "hint",
                Loc.Tr("Соло = то же лобби: оставь одну живую армию и стартуй. Игроки входят списком серверов игры или Steam-инвайтом; армии, ИИ и условие победы настраиваются в лобби."),
                13, SupcomUi.TextDim, TextAnchor.MiddleLeft, multiLine: true);
            var hintRt = (RectTransform)hint.transform;
            hintRt.anchorMin = new Vector2(0f, 1f);
            hintRt.anchorMax = new Vector2(1f, 1f);
            hintRt.offsetMin = new Vector2(16f, y - 214f);
            hintRt.offsetMax = new Vector2(-16f, y - 156f);

            _root.gameObject.SetActive(false);
            _backdrop.gameObject.SetActive(false);
        }

        private float CtlRow(string text, float y, out RectTransform row)
        {
            row = Panel("row", _root, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, y - 40f), new Vector2(-16f, y), SupcomUi.PanelBg2);
            var lbl = Label(row, "lbl", text, 14, SupcomUi.TextDim, TextAnchor.MiddleLeft);
            var rt = (RectTransform)lbl.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.offsetMin = new Vector2(10f, 0f);
            rt.offsetMax = new Vector2(134f, 0f);
            return y - 46f;
        }

        // ---------- состояние/действия ----------

        private void RefreshDynamic()
        {
            for (int i = 0; i < 3; i++)
            {
                var img = _visButtons[i] != null ? _visButtons[i].GetComponent<Image>() : null;
                if (img != null)
                    img.color = _visibility == i ? SupcomUi.BtnActive : SupcomUi.BtnBg;
            }
            int[] slots = { 4, 8, 12, 16 };
            for (int i = 0; i < 4; i++)
            {
                var img = _slotButtons[i] != null ? _slotButtons[i].GetComponent<Image>() : null;
                if (img != null)
                    img.color = _slots == slots[i] ? SupcomUi.BtnActive : SupcomUi.BtnBg;
            }
            if (_createLobbyButton != null)
                _createLobbyButton.interactable = SnLobbyService.Initialized && !_starting;
        }

        private void OnCreateLobbyClicked()
        {
            if (_starting)
                return;
            if (!SnLobbyService.Initialized)
            {
                SetStatus(Loc.Tr("Сеть ещё не готова — подожди пару секунд и повтори"), SupcomUi.WarnYellow);
                return;
            }
            _starting = true;
            Settings.SetLobby(_lobbyNameInput != null ? _lobbyNameInput.text : "",
                new[] { "public", "friends", "private" }[_visibility],
                _lobbyPassInput != null ? _lobbyPassInput.text : "", _slots);
            SetStatus(Loc.Tr("Создаю лобби…"), SupcomUi.WarnYellow);
            SnLobbyService.CreateLobbyAsync(
                Settings.LobbyName,
                (SnLobbyService.LobbyVisibility)_visibility,
                Settings.LobbyPassword,
                Settings.LobbySlots).Forget();
            RefreshDynamic();
        }

        private void OnHostResult(bool ok, string error)
        {
            _starting = false;
            RefreshDynamic();
            if (ok)
            {
                Hide(); // комнату покажет LobbyPanel
                return;
            }
            if (!string.IsNullOrEmpty(error))
                SetStatus(error, SupcomUi.WarnRed);
        }

        private void SetStatus(string text, Color color)
        {
            if (_status == null)
                return;
            _status.text = text;
            _status.color = color;
        }

        // ---------- uGUI-примитивы (зеркало SupcomUi) ----------

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
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        private Button Button_(Transform parent, string name, string label, Action onClick, int fontSize = 16)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            go.GetComponent<Image>().color = SupcomUi.BtnBg;
            // Клик — ТОЛЬКО опросом (uGUI-события в меню не доставлялись).
            _clickables.Add(new KeyValuePair<RectTransform, Action>(rt, onClick));
            var t = Label(go.transform, "lbl", label, fontSize, SupcomUi.TextMain, TextAnchor.MiddleCenter);
            t.raycastTarget = false;
            return go.GetComponent<Button>();
        }

        private InputField MakeInput(RectTransform row, string name, string initial,
            InputField.ContentType contentType, float left, float right)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            var rt = (RectTransform)go.transform;
            rt.SetParent(row, false);
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(left, -16f);
            rt.offsetMax = new Vector2(right, 16f);
            go.GetComponent<Image>().color = SupcomUi.BtnBg;
            var input = go.GetComponent<InputField>();
            input.contentType = contentType;
            input.caretColor = SupcomUi.Accent;
            var text = MakeInputText("text", SupcomUi.TextMain, 15);
            text.transform.SetParent(go.transform, false);
            input.textComponent = text;
            var placeholder = MakeInputText("ph", SupcomUi.TextDim, 14);
            placeholder.transform.SetParent(go.transform, false);
            input.placeholder = placeholder;
            input.text = initial ?? "";
            _clickables.Add(new KeyValuePair<RectTransform, Action>(rt,
                () => input.ActivateInputField()));
            return input;
        }

        private Text MakeInputText(string name, Color color, int size)
        {
            // Только ctor-Text (инвариант №15).
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
                return;
            DeactivateIfFocused(_lobbyNameInput, up);
            DeactivateIfFocused(_lobbyPassInput, up);
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

        private static void DeactivateIfFocused(InputField input, Vector2 up)
        {
            if (input != null && input.isFocused &&
                !RectTransformUtility.RectangleContainsScreenPoint(
                    (RectTransform)input.transform, up, null))
            {
                input.DeactivateInputField();
            }
        }
    }
}
