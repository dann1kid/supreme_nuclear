using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using NuclearOption.Networking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SupremeNuclear
{
    /// <summary>
    /// Лобби v3 «как в SupCom» (комната в EMPTY, единое для сингла и MP):
    /// СТОЛ АРМИЙ-слотов (контроллер закрыто/открыто/ИИ/игрок, команда-союз ◀▶,
    /// позиция старта; клики хоста циклят), ПУЛ НАБЛЮДАТЕЛЕЙ под столом (гости
    /// без слота — кандидаты в слоты), блок карты (MapSetupBlock, хост) и
    /// УСЛОВИЕ ПОБЕДЫ (захват всех баз врага / уничтожение / бесконечная;
    /// соло — одна живая армия — всегда бесконечная). Гость: «Взять слот»
    /// (клик по открытому слоту) / «Готов». «×» гостя прячет панель, хоста —
    /// роспуск. Канвас/клики — паттерн SkirmishPanel (poll); фракции армий
    /// ставит патч №16 на спавне; асы-лётчики — нативно после старта.
    /// </summary>
    internal sealed class LobbyPanel : MonoBehaviour, MapSetupBlock.IHost
    {
        private Canvas _canvas;
        private Font _font;
        private RectTransform _root;
        private RectTransform _backdrop;
        private readonly List<KeyValuePair<RectTransform, System.Action>> _clickables =
            new List<KeyValuePair<RectTransform, System.Action>>();
        private Vector2 _pressAt;
        private bool _pressValid;

        private Text _title;
        private Button _victoryButton;
        private readonly List<RectTransform> _armyRows = new List<RectTransform>();
        private readonly List<object> _armyTexts = new List<object>(); // placeholder: подписи в кнопках
        private readonly List<Button[]> _armyButtons = new List<Button[]>(); // [контроллер, команда, позиция]
        private Text _observers;
        private Button _startButton, _disbandButton, _readyButton;
        private Text _hint;
        private MapSetupBlock _mapBlock;
        private RectTransform _ctxMenu;       // контекстное меню слота (FAF-стиль)
        private Vector2 _ctxPos;              // позиция меню в координатах канваса
        private Vector2 _ctxSize;
        private int _ctxArmyId = -1;
        private readonly List<KeyValuePair<RectTransform, Action>> _menuClickables =
            new List<KeyValuePair<RectTransform, Action>>();

        private bool _hostMode;
        private bool _peek;
        private float _nextRefreshAt;
        private string _lastHash = "";
        private bool _navWasEnabled = true;
        private float _nextErrLogAt = -10f;

        // Чат комнаты (§12-64е): лог + строка ввода; обновление по ChatVersion.
        private Text _chatLog;
        private InputField _chatInput;
        private int _seenChatVersion = -1;

        // Drag-рассадка (§12-64а): нажатие на ник сидящего человека (хост) —
        // кандидат драга; > 6 px — призрак следует за курсором, бросок на строку
        // стола пересаживает. Клик (< 6 px) — прежний путь (контекст-меню).
        private int _dragArmyId = -1;
        private Text _dragGhost;

        private void Update()
        {
            try
            {
                UpdateSafe();
            }
            catch (System.Exception e)
            {
                if (Time.unscaledTime - _nextErrLogAt > 5f)
                {
                    _nextErrLogAt = Time.unscaledTime;
                    Log.Warn("LobbyPanel: сбой Update — " + e); // полный стек, троттл 5 с
                }
            }
        }

        private void UpdateSafe()
        {
            // Хост крутит «Стороны» в блоке карты — слоты перестраиваются СРАЗУ,
            // до генерации (юзер 2026-11-06: пустые слоты должны быть доступны).
            if (SnLobbyService.Hosting && _mapBlock != null &&
                _mapBlock.Players != (SnLobbyService.State?.pool ?? 2))
                SnLobbyService.SetPool(_mapBlock.Players);
            bool want = SnLobbyService.InWaitingRoom;
            if (want && SnLobbyService.Hosting)
                _peek = false; // хост панель не прячет
            if (want && !EnsureBuilt())
                return; // панель не собралась — ретрай в следующем кадре
            if (_root == null)
                return;
            if (want != _root.gameObject.activeSelf)
            {
                if (want)
                    Show();
                else
                    Hide();
            }
            if (!want)
                return;
            if (Time.unscaledTime >= _nextRefreshAt)
            {
                _nextRefreshAt = Time.unscaledTime + 0.4f;
                if (SnLobbyService.Hosting)
                    SnLobbyService.EnsureHostSeated(); // «?» вместо ника хоста
                RefreshTable();
                RefreshChat();
                _mapBlock?.RefreshDynamic();
                _mapBlock?.RefreshGenUi();
                SyncHostMap();
            }
            if (_root.gameObject.activeSelf)
                PollClicks();
        }

        // ---------- сборка (лениво, ретраи) ----------

        private bool EnsureBuilt()
        {
            if (_root != null)
                return true;
            try
            {
                var go = new GameObject("SN_LobbyCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                DontDestroyOnLoad(go);
                _canvas = go.GetComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = 30002;
                var scaler = go.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f) / Mathf.Clamp(Settings.UiScale, 0.75f, 2f);
                scaler.matchWidthOrHeight = 0.5f;
                Build();
                _root.gameObject.SetActive(false);
                _backdrop.gameObject.SetActive(false);
                return true;
            }
            catch (System.Exception e)
            {
                if (Time.unscaledTime - _nextErrLogAt > 5f)
                {
                    _nextErrLogAt = Time.unscaledTime;
                    Log.Error("LobbyPanel: сборка не удалась — " + e);
                }
                if (_canvas != null)
                {
                    Destroy(_canvas.gameObject); // недострой не висит поверх игры
                    _canvas = null;
                }
                return false;
            }
        }

        private void Build()
        {
            _font = SupcomUi.Instance != null ? SupcomUi.Instance.UiFont : null;
            if (_font == null)
            {
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch { }
            }
            _clickables.Clear();
            _backdrop = Panel("ModalBackdrop", _canvas.transform,
                Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero,
                new Color(0f, 0f, 0f, 0.55f));
            _root = Panel("SnLobby", _canvas.transform,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(-370f, -352f), new Vector2(370f, 352f), SupcomUi.PanelBg);

            _title = Label(_root, "title", "", 21, SupcomUi.Accent, TextAnchor.MiddleLeft);
            var titleRt = (RectTransform)_title.transform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = new Vector2(1f, 1f);
            titleRt.offsetMin = new Vector2(18f, -38f);
            titleRt.offsetMax = new Vector2(-46f, -8f);
            var close = Button_(_root, "close", "×", OnCloseClicked, 20);
            var closeRt = (RectTransform)close.transform;
            closeRt.anchorMin = new Vector2(1f, 1f);
            closeRt.anchorMax = new Vector2(1f, 1f);
            closeRt.offsetMin = new Vector2(-46f, -36f);
            closeRt.offsetMax = new Vector2(-10f, -4f);

            // Заголовок стола + условие победы (кнопка-цикл, хост).
            var head = Label(_root, "head", Loc.Tr("Слоты (позиции карты)"), 14,
                SupcomUi.TextDim, TextAnchor.MiddleLeft);
            var headRt = (RectTransform)head.transform;
            headRt.anchorMin = new Vector2(0f, 1f);
            headRt.anchorMax = new Vector2(0.55f, 1f);
            headRt.offsetMin = new Vector2(18f, -66f);
            headRt.offsetMax = new Vector2(-16f, -42f);
            _victoryButton = Button_(_root, "victory", "", () => SnLobbyService.CycleVictory(), 13);
            var vRt = (RectTransform)_victoryButton.transform;
            vRt.anchorMin = new Vector2(1f, 1f);
            vRt.anchorMax = new Vector2(1f, 1f);
            vRt.offsetMin = new Vector2(-270f, -66f);
            vRt.offsetMax = new Vector2(-16f, -42f);

            // ДВЕ КОЛОНКИ (юзер 2026-11-06: «карту сместить вправо»):
            // слева — стол слотов + наблюдатели; справа — блок карты.
            // ВАЖНО (плейтест 2026-11-06): колонки на ФИКСИРОВАННЫХ анкорах.
            // Растягивающие (0..1) + offsetMax.x=364 трактовали 364 от ПРАВОГО
            // края рута — левая колонка уезжала за панель и налезала на карту.
            var left = Panel("colLeft", _root, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, -650f), new Vector2(376f, -40f), new Color(0f, 0f, 0f, 0f));

            // Стол СЛОТОВ — слева, компактные строки (до 8 слотов по пулу карты).
            float y = -74f;
            for (int i = 0; i < LobbyState.MaxArmies; i++)
            {
                BuildArmyRow(left, i, y);
                y -= 28f;
            }

            // Пул наблюдателей (кандидаты в слоты) — под столом слева.
            var obsLbl = Label(left, "obsLbl", Loc.Tr("Наблюдатели (без слота)"), 13,
                SupcomUi.TextDim, TextAnchor.MiddleLeft);
            var olRt = (RectTransform)obsLbl.transform;
            olRt.anchorMin = new Vector2(0f, 1f);
            olRt.anchorMax = new Vector2(1f, 1f);
            olRt.offsetMin = new Vector2(2f, y - 16f);
            olRt.offsetMax = new Vector2(-2f, y);
            _observers = Label(left, "observers", "", 13, SupcomUi.TextDim,
                TextAnchor.UpperLeft, multiLine: true);
            var oRt = (RectTransform)_observers.transform;
            oRt.anchorMin = new Vector2(0f, 1f);
            oRt.anchorMax = new Vector2(1f, 1f);
            oRt.offsetMin = new Vector2(2f, y - 64f);
            oRt.offsetMax = new Vector2(-2f, y - 18f);

            // Справа: блок карты (стиль/сид/стороны/библиотека/генерация/превью).
            var right = Panel("colRight", _root, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(388f, -650f), new Vector2(712f, -40f), new Color(0f, 0f, 0f, 0f));
            _mapBlock = new MapSetupBlock(this, right, -30f); // ниже кнопки «Победа»

            // Чат комнаты (§12-64е): лог + строка ввода — НАД нижней полосой
            // кнопок (СТАРТ/Распустить занимают нижние ~70 ед. панели; наслоение
            // плейтест 2026-11-07). Журнал локальный, приходит ретрансляцией
            // хоста (req=chat мимо состояния).
            var chatLog = Label(left, "chatLog", "", 12, SupcomUi.TextMain,
                TextAnchor.LowerLeft, multiLine: true);
            var clRt = (RectTransform)chatLog.transform;
            clRt.anchorMin = new Vector2(0f, 1f);
            clRt.anchorMax = new Vector2(1f, 1f);
            clRt.offsetMin = new Vector2(2f, -546f);
            clRt.offsetMax = new Vector2(-2f, -356f);
            _chatLog = chatLog;
            var chatRow = Panel("chatRow", left, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(2f, -588f), new Vector2(-2f, -552f), new Color(0f, 0f, 0f, 0f));
            var chatGo = new GameObject("chatInput", typeof(RectTransform), typeof(Image), typeof(InputField));
            var ciRt = (RectTransform)chatGo.transform;
            ciRt.SetParent(chatRow, false);
            ciRt.anchorMin = new Vector2(0f, 0f);
            ciRt.anchorMax = new Vector2(1f, 1f);
            ciRt.offsetMin = new Vector2(0f, 0f);
            ciRt.offsetMax = new Vector2(-44f, 0f);
            chatGo.GetComponent<Image>().color = SupcomUi.BtnBg;
            _chatInput = chatGo.GetComponent<InputField>();
            _chatInput.contentType = InputField.ContentType.Standard;
            _chatInput.caretColor = SupcomUi.Accent;
            Text chatText = MakeInputText("text", SupcomUi.TextMain, 14);
            chatText.transform.SetParent(chatGo.transform, false);
            _chatInput.textComponent = chatText;
            var chatPh = MakeInputText("ph", SupcomUi.TextDim, 13);
            chatPh.transform.SetParent(chatGo.transform, false);
            chatPh.fontStyle = FontStyle.Italic;
            chatPh.text = Loc.Tr("сообщение (Enter — отправить)");
            _chatInput.placeholder = chatPh;
            _clickables.Add(new KeyValuePair<RectTransform, System.Action>(ciRt,
                () => _chatInput.ActivateInputField()));
            _chatInput.onSubmit.AddListener(_ => SendChatFromInput());
            Button chatSend = Button_(chatRow, "chatSend", "▶", SendChatFromInput, 15);
            // Button_ не ставит якоря (нулевой размер в центре строки —
            // некликабельно) — правая кромка строки ввода, как у всех кнопок.
            var csRt = (RectTransform)chatSend.transform;
            csRt.anchorMin = new Vector2(1f, 0f);
            csRt.anchorMax = new Vector2(1f, 1f);
            csRt.offsetMin = new Vector2(-40f, 0f);
            csRt.offsetMax = new Vector2(-2f, 0f);

            // Нижний ряд: СТАРТ/Распустить (хост), Готов/Покинуть (гость).
            _startButton = Button_(_root, "start", Loc.Tr("СТАРТ"), OnStartClicked, 22);
            var sRt = (RectTransform)_startButton.transform;
            sRt.anchorMin = new Vector2(0f, 0f);
            sRt.anchorMax = new Vector2(0f, 0f);
            sRt.offsetMin = new Vector2(16f, 14f);
            sRt.offsetMax = new Vector2(166f, 60f);
            _disbandButton = Button_(_root, "disband", Loc.Tr("Распустить"), () => SnLobbyService.Leave(), 15);
            var dRt = (RectTransform)_disbandButton.transform;
            dRt.anchorMin = new Vector2(1f, 0f);
            dRt.anchorMax = new Vector2(1f, 0f);
            dRt.offsetMin = new Vector2(-166f, 14f);
            dRt.offsetMax = new Vector2(-16f, 60f);
            _readyButton = Button_(_root, "ready", Loc.Tr("Готов"), () => SnLobbyService.ToggleReady(), 17);
            var rRt = (RectTransform)_readyButton.transform;
            rRt.anchorMin = new Vector2(0f, 0f);
            rRt.anchorMax = new Vector2(0f, 0f);
            rRt.offsetMin = new Vector2(16f, 14f);
            rRt.offsetMax = new Vector2(146f, 60f);
            var leave = Button_(_root, "leave", Loc.Tr("Покинуть"), () => SnLobbyService.Leave(), 15);
            var lRt = (RectTransform)leave.transform;
            lRt.anchorMin = new Vector2(1f, 0f);
            lRt.anchorMax = new Vector2(1f, 0f);
            lRt.offsetMin = new Vector2(-156f, 14f);
            lRt.offsetMax = new Vector2(-16f, 60f);
            _hint = Label(_root, "hint", "", 12, SupcomUi.TextDim, TextAnchor.MiddleLeft, multiLine: true);
            var hRt = (RectTransform)_hint.transform;
            hRt.anchorMin = new Vector2(0.5f, 0f);
            hRt.anchorMax = new Vector2(1f, 0f);
            hRt.offsetMin = new Vector2(0f, 18f);
            hRt.offsetMax = new Vector2(-16f, 58f);
        }

        /// <summary>Строка слота (столбцы как у FAF, юзер 2026-11-06):
        /// 0 № позиции | 1 Ник (контекст-меню хоста / занять гостем) |
        /// 2 Команда | 3 Цвет | 4 Игрок + «Готов» (синк перед стартом).</summary>
        private void BuildArmyRow(RectTransform parent, int idx, float y)
        {
            var row = Panel("army" + idx, parent, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, y - 26f), new Vector2(-2f, y), SupcomUi.PanelBg2);
            _armyRows.Add(row);
            var buttons = new Button[5];
            float[] lefts = { 2f, 28f, 150f, 208f, 236f };
            float[] rights = { 26f, 148f, 206f, 234f, 346f };
            for (int c = 0; c < 5; c++)
            {
                int armyId = idx;
                int col = c;
                RectTransform cellRt = null; // замыкание: ссылка появится после создания
                var btn = Button_(row, "c" + c, "", () => OnArmyCell(armyId, col, cellRt), 13);
                cellRt = (RectTransform)btn.transform;
                var rt = (RectTransform)btn.transform;
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(0f, 0.5f);
                rt.offsetMin = new Vector2(lefts[c], -12f);
                rt.offsetMax = new Vector2(rights[c], 12f);
                buttons[c] = btn;
            }
            _armyTexts.Add(null);
            _armyButtons.Add(buttons);
        }

        private void OnArmyCell(int armyId, int col, RectTransform cellRt)
        {
            if (SnLobbyService.Hosting)
            {
                switch (col)
                {
                    case 1:
                    case 4:
                        // Ник и столбец состояния открывают FAF-дропдаун слота;
                        // исключение — СВОЙ сидящий слот в col 4: клик = готовность
                        // (быстрый путь, как клик по кнопке «Готов» у гостя).
                        LobbyState.Army seat = SnLobbyService.State?.armies.Find(x => x.id == armyId);
                        if (col == 4 && seat != null && seat.controller == LobbyState.CtrlHuman)
                            SnLobbyService.ToggleSlotReady(armyId);
                        else
                            ShowContextMenu(armyId, cellRt);
                        break;
                    case 2: SnLobbyService.CycleTeam(armyId); break;
                    case 3: SnLobbyService.CycleColor(armyId); break;
                }
            }
            else
            {
                LobbyState.Army a = SnLobbyService.State?.armies.Find(x => x.id == armyId);
                if (a == null)
                    return;
                if (a.controller == LobbyState.CtrlOpen && (col == 1 || col == 4))
                    SnLobbyService.RequestTakeSlot(armyId); // занять слот
                if (col == 4 && a.controller == LobbyState.CtrlHuman &&
                    a.humanId == SnLobbyService.LocalPlayerIndex())
                    SnLobbyService.ToggleReady(); // свой «Готов» кликом по столбцу
            }
        }

        // ---------- показ/скрытие ----------

        private void Show()
        {
            _lastHash = "";
            _root.gameObject.SetActive(true);
            _backdrop.gameObject.SetActive(true);
            RefreshTable();
            if (EventSystem.current != null)
            {
                _navWasEnabled = EventSystem.current.sendNavigationEvents;
                EventSystem.current.sendNavigationEvents = false;
            }
            Log.Info("SnLobby: панель лобби открыта (" + (_hostMode ? "хост" : "гость") + ")");
        }

        private void Hide()
        {
            CloseContextMenu();
            EndDrag();
            _root.gameObject.SetActive(false);
            _backdrop.gameObject.SetActive(false);
            if (EventSystem.current != null)
                EventSystem.current.sendNavigationEvents = _navWasEnabled;
        }

        private void OnCloseClicked()
        {
            if (SnLobbyService.Hosting)
                SnLobbyService.Leave();
            else
                _peek = true;
        }

        /// <summary>Готовая генерация хоста — в состояние лобби (синк гостям:
        /// карта+армии → те строят тот же мир). Один раз на Done-параметры.</summary>
        private string _syncedMapKey;

        private void SyncHostMap()
        {
            if (!SnLobbyService.Hosting || SnLobbyService.State == null ||
                MapGenAsync.Current != MapGenAsync.Phase.Done ||
                string.IsNullOrEmpty(MapGenAsync.SaveName))
                return;
            string signature = MapGenAsync.TemplateName + "#" + MapGenAsync.Seed + "#" +
                               MapGenAsync.Players + "#" + MapGenAsync.SaveName;
            if (_syncedMapKey == signature)
                return;
            _syncedMapKey = signature;
            SnLobbyService.SetMap(MapGenAsync.TemplateName, MapGenAsync.Seed,
                MapGenAsync.Players, MapGenAsync.SaveName);
        }

        // ---------- контекстное меню слота (FAF-стиль) ----------

        /// <summary>Дропдаун состояния слота (FAF-канон, плейтест 2026-11-07
        /// «посмотри как сделано тут»): Закрыт / Открыт / Занять (я) / ИИ-профили
        /// (Стандарт-Лёгкий-Жёсткий-Экономист-Агрессор) / назначить наблюдателя.
        /// Хост-only; открывается кликом по слоту, закрывается выбором/кликом мимо.</summary>
        private void ShowContextMenu(int armyId, RectTransform cellRt)
        {
            if (!SnLobbyService.Hosting || SnLobbyService.State == null ||
                SnLobbyService.State.status != LobbyState.StatusWaiting)
                return;
            CloseContextMenu();
            _ctxArmyId = armyId;

            var observers = LobbyState.Observers(SnLobbyService.State);
            LobbyState.Army menuArmy = SnLobbyService.State.armies.Find(x => x.id == armyId);
            bool canOccupy = menuArmy != null &&
                (menuArmy.controller == LobbyState.CtrlOpen || menuArmy.controller == LobbyState.CtrlAi);
            int itemCount = 2 + (canOccupy ? 1 : 0) + AiProfile.Types.Length +
                (observers.Count > 0 ? 1 + observers.Count : 0);
            float w = 240f, h = itemCount * 30f + 10f;
            _ctxMenu = Panel("ctxMenu", _canvas.transform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                Vector2.zero, new Vector2(w, h), SupcomUi.PanelBg);
            var rt = (RectTransform)_ctxMenu.transform;
            rt.pivot = new Vector2(0f, 1f); // якорь — верхний левый угол меню
            // Позиция — ПОД ЯЧЕЙКОЙ (плейтест 2026-11-06: экранные пиксели при
            // CanvasScaler scale≠1 уводили меню куда попало; world==screen для
            // overlay-канваса, делим на scale → юниты канваса).
            var corners = new Vector3[4];
            cellRt.GetWorldCorners(corners);
            float scale = Mathf.Max(0.01f, _canvas.scaleFactor);
            float cellLeft = corners[0].x / scale;      // низ-лево
            float cellTop = corners[1].y / scale;       // верх-лево
            float refW = Screen.width / scale, refH = Screen.height / scale;
            float mx = Mathf.Clamp(cellLeft, 10f, refW - w - 10f);
            float my = Mathf.Clamp(cellTop - 4f, h + 10f, refH - h - 10f); // верх меню (y от низа)
            _ctxPos = new Vector2(mx, my);
            _ctxSize = new Vector2(w, h);
            rt.anchoredPosition = _ctxPos;

            float iy = 5f;
            MenuItem(Loc.Tr("Закрыт"), () => { SnLobbyService.SetArmyController(armyId, LobbyState.CtrlClosed); CloseContextMenu(); }, iy);
            iy += 30f;
            MenuItem(Loc.Tr("Открыт"), () => { SnLobbyService.SetArmyController(armyId, LobbyState.CtrlOpen); CloseContextMenu(); }, iy);
            iy += 30f;
            if (canOccupy)
            {
                MenuItem(Loc.Tr("Занять (я)"), () => { SnLobbyService.SeatHostAt(armyId); CloseContextMenu(); }, iy);
                iy += 30f;
            }
            // ИИ-профили (FAF-канон: «AI: Easy/Normal/…») — выбор ставит слот
            // в ИИ сразу с пресетом; aiType уходит гостям с Broadcast (§12-49).
            foreach (string type in AiProfile.Types)
            {
                string profile = type; // замыкание на текущий тип
                MenuItem(Loc.F("ИИ: {0}", AiProfile.LabelOf(type)),
                    () => { SnLobbyService.SetAiProfile(armyId, profile); CloseContextMenu(); }, iy);
                iy += 30f;
            }
            if (observers.Count > 0)
            {
                var hdr = Label(_ctxMenu, "ctxHdr", Loc.Tr("Назначить наблюдателя:"), 12,
                    SupcomUi.TextDim, TextAnchor.MiddleLeft);
                var hRt = (RectTransform)hdr.transform;
                hRt.anchorMin = new Vector2(0f, 1f);
                hRt.anchorMax = new Vector2(1f, 1f);
                hRt.offsetMin = new Vector2(8f, -iy - 18f);
                hRt.offsetMax = new Vector2(-8f, -iy);
                iy += 22f;
                foreach (LobbyState.Member m in observers)
                {
                    LobbyState.Member obs = m;
                    MenuItem(m.name, () =>
                    {
                        SnLobbyService.AssignHuman(armyId, obs.id);
                        CloseContextMenu();
                    }, iy);
                    iy += 30f;
                }
            }
        }

        /// <summary>Пункт меню с ЯВНОЙ позицией (плейтест 2026-11-06: пункты без
        /// offsets растягивались на всё меню и ложились друг на друга — в углу
        /// торчал один «ИИ-командир»). Регистрируется в _menuClickables.</summary>
        private void MenuItem(string label, Action onClick, float iy)
        {
            var go = new GameObject("mi_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(_ctxMenu.transform, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(6f, -iy - 28f);
            rt.offsetMax = new Vector2(-6f, -iy);
            go.GetComponent<Image>().color = SupcomUi.BtnBg;
            _menuClickables.Add(new KeyValuePair<RectTransform, Action>(rt, onClick));
            var t = Label(go.transform, "lbl", label, 13, SupcomUi.TextMain, TextAnchor.MiddleCenter);
            t.raycastTarget = false;
        }

        private void CloseContextMenu()
        {
            if (_ctxMenu != null)
            {
                Destroy(_ctxMenu.gameObject);
                _ctxMenu = null;
            }
            _ctxArmyId = -1;
            _menuClickables.Clear();
        }

        private void OnStartClicked()
        {
            string reason = SnLobbyService.LaunchBlockReason();
            if (reason == null)
            {
                SnLobbyService.Launch().Forget();
                return;
            }
            SetHint(reason, SupcomUi.WarnYellow);
        }

        // ---------- таблица ----------

        private void RefreshTable()
        {
            LobbyState state = SnLobbyService.State;
            if (state == null)
                return;
            _hostMode = SnLobbyService.Hosting;
            var sb = new System.Text.StringBuilder();
            foreach (LobbyState.Member m in LobbyState.Observers(state))
                sb.Append("• ").Append(m.name).Append(m.ready ? " ✓" : "   ");
            string victory = LobbyState.EffectiveVictory(state);
            var hash = new System.Text.StringBuilder(sb + "|" + _hostMode + "|" + victory +
                (state.map != null ? state.map.seed + ":" + state.map.players : "-"));
            foreach (LobbyState.Army a in state.armies)
                hash.Append("|").Append(a.controller).Append(a.humanId).Append(".").Append(a.team).Append(".").Append(a.pos);
            foreach (LobbyState.Member m in state.members)
                hash.Append(";").Append(m.id).Append(m.ready ? "r" : "");
            if (hash.ToString() == _lastHash)
                return;
            _lastHash = hash.ToString();
            CloseContextMenu(); // стол перестроен — меню могло устареть
            if (_peek)
                _peek = false; // состав/план сменились — вернуть панель гостю

            _title.text = Loc.Tr("Лобби — ") + (state.map != null
                ? "SN " + state.map.template + " " + state.map.seed.ToString("X8")
                : Loc.Tr("карта не выбрана"));
            _victoryButton.GetComponentInChildren<Text>().text = VictoryLabel(victory);
            _victoryButton.gameObject.SetActive(_hostMode);
            _observers.text = sb.Length > 0 ? sb.ToString() : Loc.Tr("(пока никого — ждём игроков)");

            int pool = LobbyState.PoolSize(state); // слоты видны ДО генерации (выбор сторон)
            for (int i = 0; i < LobbyState.MaxArmies; i++)
            {
                bool inPool = i < pool && i < state.armies.Count;
                LobbyState.Army a = inPool ? state.armies[i] : null;
                bool live = a != null && a.controller != LobbyState.CtrlClosed;
                _armyRows[i].gameObject.SetActive(inPool);
                if (!inPool)
                    continue;
                string who = a.controller == LobbyState.CtrlHuman ? HumanName(state, a.humanId)
                    : a.controller == LobbyState.CtrlAi
                        ? Loc.F("ИИ · {0}", AiProfile.LabelOf(
                            string.IsNullOrEmpty(a.aiType) ? "commander" : a.aiType))
                    : "";
                // Столбцы: 0 № позиции | 1 Ник (меню) | 2 Команда | 3 Цвет |
                // 4 Игрок + «Готов» (синк перед стартом).
                bool seatedReady = a.controller == LobbyState.CtrlHuman && SeatReady(state, a.humanId);
                var b = _armyButtons[i];
                SetCell(b[0], (a.pos + 1).ToString(), false, SupcomUi.TextMain);
                SetCell(b[1], a.controller == LobbyState.CtrlHuman ? who
                    : a.controller == LobbyState.CtrlAi ? Loc.Tr("ИИ")
                    : a.controller == LobbyState.CtrlClosed ? Loc.Tr("Закрыт")
                    : Loc.Tr("Открыт"), _hostMode || a.controller == LobbyState.CtrlOpen,
                    a.controller == LobbyState.CtrlOpen && !_hostMode ? SupcomUi.Accent : SupcomUi.TextMain);
                SetCell(b[2], "Кмд " + a.team, _hostMode, SupcomUi.TextMain);
                SetCell(b[3], "■", _hostMode, FactionFactory.Palette[((a.colorIdx % 4) + 4) % 4]);
                string readyCell = a.controller == LobbyState.CtrlHuman
                    ? (seatedReady ? Loc.Tr("✓ готов") : Loc.Tr("не готов"))
                    : a.controller == LobbyState.CtrlAi ? Loc.Tr("—")
                    : Loc.Tr("Занять");
                SetCell(b[4], readyCell, _hostMode || a.controller == LobbyState.CtrlOpen,
                    seatedReady ? SupcomUi.BtnActive
                    : a.controller == LobbyState.CtrlOpen && !_hostMode ? SupcomUi.Accent : SupcomUi.TextDim);
            }

            bool waiting = state.status == LobbyState.StatusWaiting;
            _startButton.gameObject.SetActive(_hostMode);
            _disbandButton.gameObject.SetActive(_hostMode);
            _readyButton.gameObject.SetActive(!_hostMode);
            _hint.gameObject.SetActive(!_hostMode || !SnLobbyService.ReadyToLaunch());
            _startButton.interactable = _hostMode && waiting && SnLobbyService.ReadyToLaunch();
            _mapBlock?.SetInteractive(_hostMode && waiting);
            if (_hostMode)
            {
                // Единый гейт СТАРТА (карта/пул/валидность/«Готов» гостей):
                // причина — в подсказку, зелёный СТАРТ сам проверяет то же.
                string reason = SnLobbyService.LaunchBlockReason();
                SetHint(reason ?? Loc.Tr("Все готовы — можно стартовать"),
                    reason != null ? SupcomUi.TextDim : SupcomUi.WarnYellow);
            }
            else
            {
                var readyImg = _readyButton.GetComponent<Image>();
                LobbyState.Member me = null;
                int myId = SnLobbyService.LocalPlayerIndex();
                foreach (LobbyState.Member m in state.members)
                    if (m.id == myId)
                        me = m;
                if (readyImg != null)
                    readyImg.color = me != null && me.ready ? SupcomUi.BtnActive : SupcomUi.BtnBg;
                SetHint(Loc.Tr("Возьми открытый слот (клик по «Открыт — взять») и жми «Готов». «×» прячет панель. Асы-лётчики присоединяются после старта."), SupcomUi.TextDim);
            }
        }

        /// <summary>Подпись+гейт ячейки строки армии (кнопка-селектор).</summary>
        private static void SetCell(Button btn, string text, bool interactable, Color color)
        {
            if (btn == null)
                return;
            btn.interactable = interactable;
            var lbl = btn.GetComponentInChildren<Text>();
            if (lbl != null)
            {
                lbl.text = text;
                lbl.color = color;
            }
        }

        /// <summary>Готов ли человек, сидящий в слоте (синк ReadyIds через Broadcast).</summary>
        private static bool SeatReady(LobbyState state, int humanId)
        {
            foreach (LobbyState.Member m in state.members)
                if (m.id == humanId)
                    return m.ready;
            return false;
        }

        private static string HumanName(LobbyState state, int humanId)
        {
            foreach (LobbyState.Member m in state.members)
                if (m.id == humanId)
                    return m.name;
            return humanId == state.hostId ? Loc.Tr("хост") : "?";
        }

        private static string VictoryLabel(string victory) => victory == LobbyState.VictoryCapture
            ? Loc.Tr("Победа: захват всех баз врага")
            : victory == LobbyState.VictoryAnnihilate ? Loc.Tr("Победа: уничтожение")
            : Loc.Tr("Победа: бесконечная");

        /// <summary>Отправка чат-строки: поле не пустое → SendChat и очистка
        /// (фокус снимается — Enter в пустом поле просто мигнёт).</summary>
        private void SendChatFromInput()
        {
            if (_chatInput == null)
                return;
            string text = _chatInput.text;
            _chatInput.text = "";
            if (_chatInput.isFocused)
                _chatInput.DeactivateInputField();
            if (!string.IsNullOrWhiteSpace(text))
                SnLobbyService.SendChat(text);
        }

        /// <summary>Лог чата: новая строка (ChatVersion) — перечитать хвост
        /// журнала (канвас-лейбл не тянет тысячи строк — последние 24).</summary>
        private void RefreshChat()
        {
            if (_chatLog == null || _seenChatVersion == SnLobbyService.ChatVersion)
                return;
            _seenChatVersion = SnLobbyService.ChatVersion;
            var lines = SnLobbyService.ChatLines;
            int from = Mathf.Max(0, lines.Count - 24);
            var sb = new System.Text.StringBuilder();
            for (int i = from; i < lines.Count; i++)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(lines[i]);
            }
            _chatLog.text = sb.ToString();
        }

        // ---------- drag-рассадка (§12-64а, правило 18: IPointer-события в этой
        // игре не доставляются — перетаскивание из IPointerDown/Up-аналогов опроса
        // PollClicks: нажатие > 6 px = драг, иначе клик) ----------

        /// <summary>Строка под курсором (хит-тест панелей строк) или −1.</summary>
        private int HitArmyRow(Vector2 screenPoint)
        {
            for (int i = 0; i < _armyRows.Count; i++)
                if (_armyRows[i] != null && _armyRows[i].gameObject.activeSelf &&
                    RectTransformUtility.RectangleContainsScreenPoint(_armyRows[i], screenPoint, null))
                    return i;
            return -1;
        }

        /// <summary>Ник-ячейка (столбец 1) сидящего человека под курсором —
        /// кандидат драга; хост-only. −1 — мимо.</summary>
        private int HitHumanNickCell(Vector2 screenPoint)
        {
            if (!SnLobbyService.Hosting || SnLobbyService.State == null)
                return -1;
            for (int i = 0; i < _armyButtons.Count && i < SnLobbyService.State.armies.Count; i++)
            {
                LobbyState.Army a = SnLobbyService.State.armies[i];
                if (a == null || a.controller != LobbyState.CtrlHuman ||
                    _armyRows[i] == null || !_armyRows[i].gameObject.activeSelf)
                    continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(
                        (RectTransform)_armyButtons[i][1].transform, screenPoint, null))
                    return i;
            }
            return -1;
        }

        /// <summary>Призрак драга (ник под курсором; ленивая сборка).</summary>
        private void DragGhost(string name)
        {
            if (_dragGhost == null)
            {
                var go = new GameObject("dragGhost", typeof(RectTransform), typeof(Text));
                var rt = (RectTransform)go.transform;
                rt.SetParent(_canvas.transform, false);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(240f, 26f);
                _dragGhost = go.GetComponent<Text>();
                _dragGhost.font = _font;
                _dragGhost.fontSize = 14;
                _dragGhost.color = SupcomUi.Accent;
                _dragGhost.alignment = TextAnchor.MiddleLeft;
                _dragGhost.raycastTarget = false;
                _dragGhost.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
            _dragGhost.gameObject.SetActive(true);
            _dragGhost.text = name;
            // ScreenSpaceOverlay: мировые координаты канваса = экранные пиксели,
            // призрак ставится прямо под курсор (смещение — не закрывать ячейку).
            _dragGhost.transform.position = new Vector3(
                Input.mousePosition.x + 14f, Input.mousePosition.y - 14f, 0f);
        }

        private void EndDrag()
        {
            _dragArmyId = -1;
            if (_dragGhost != null)
                _dragGhost.gameObject.SetActive(false);
        }

        private void SetHint(string text, Color color)
        {
            if (_hint == null)
                return;
            _hint.text = text;
            _hint.color = color;
        }

        // ---------- uGUI-примитивы (IHost) ----------

        public RectTransform Panel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
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

        public Text Label(Transform parent, string name, string text, int size, Color color,
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

        public Button Button_(Transform parent, string name, string label, System.Action onClick, int fontSize = 16)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            go.GetComponent<Image>().color = SupcomUi.BtnBg;
            _clickables.Add(new KeyValuePair<RectTransform, System.Action>(rt, onClick));
            var t = Label(go.transform, "lbl", label, fontSize, SupcomUi.TextMain, TextAnchor.MiddleCenter);
            t.raycastTarget = false;
            return go.GetComponent<Button>();
        }

        public InputField MakeInput(RectTransform row, string name, string initial,
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
            input.text = initial ?? "";
            _clickables.Add(new KeyValuePair<RectTransform, System.Action>(rt,
                () => input.ActivateInputField()));
            return input;
        }

        public Text MakeInputText(string name, Color color, int size)
        {
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
                // Кандидат драга: нажатие на ник сидящего человека (хост). Меню
                // слота лежит ПОВЕРХ строк — нажатие по пункту физически попадает
                // на ник-ячейку под ним: при открытом меню драга не берём (иначе
                // пункт глотался призраком драга при сдвиге > 6 px).
                _dragArmyId = _ctxMenu != null ? -1 : HitHumanNickCell(Input.mousePosition);
            }
            // Драг в процессе: призрак следует за курсором; пока тянем (> 6 px) —
            // клики не диспетчерятся, отпускание будет броском.
            if (_dragArmyId >= 0 && Input.GetMouseButton(0))
            {
                LobbyState.Army a = SnLobbyService.State?.armies.Find(x => x.id == _dragArmyId);
                DragGhost(a != null && a.controller == LobbyState.CtrlHuman
                    ? HumanName(SnLobbyService.State, a.humanId) : "?");
                if (((Vector2)Input.mousePosition - _pressAt).sqrMagnitude > 36f)
                    return;
            }
            if (!Input.GetMouseButtonUp(0) || !_pressValid)
                return;
            _pressValid = false;
            Vector2 up = Input.mousePosition;
            if ((up - _pressAt).sqrMagnitude > 36f)
            {
                // Бросок драга: строка под курсором — пересадка (хост).
                if (_dragArmyId >= 0)
                {
                    int from = _dragArmyId;
                    EndDrag();
                    int target = HitArmyRow(up);
                    if (target >= 0 && target != from)
                        SnLobbyService.MovePlayer(from, target);
                }
                return;
            }
            EndDrag(); // клик (< 6 px) — драг не состоялся
            // Контекстное меню — приоритет: клик по пункту исполняет, клик мимо
            // закрывает меню и НЕ проваливается в строки стола. Хит-тест в
            // координатах канваса (те же, что у позиции меню — плейтест
            // 2026-11-06: экранные пиксели при CanvasScaler не совпадали).
            if (_ctxMenu != null)
            {
                // Хит-тест пунктов — в тех же центр-относительных координатах,
                // что и позиция меню: экран/scale − центр экрана. Прежние
                // варианты давали другую систему координат, чем у меню.
                float scale = Mathf.Max(0.01f, _canvas.scaleFactor);
                Vector2 upLocal = new Vector2(up.x / scale - Screen.width / (2f * scale),
                    up.y / scale - Screen.height / (2f * scale));
                bool inside = upLocal.x >= _ctxPos.x && upLocal.x <= _ctxPos.x + _ctxSize.x &&
                              upLocal.y >= _ctxPos.y - _ctxSize.y && upLocal.y <= _ctxPos.y;
                if (!inside)
                {
                    CloseContextMenu();
                    return;
                }
                for (int i = 0; i < _menuClickables.Count; i++)
                {
                    var kv = _menuClickables[i];
                    if (kv.Key == null)
                        continue;
                    var rt = (RectTransform)kv.Key.transform;
                    // Пункты растянуты по x (anchor 0..1): лево = меню.лево +
                    // offsetMin.x, право = меню.право + offsetMax.x (он отрицательный).
                    // Прежний r = _ctxPos.x + offsetMax.x давал диапазон
                    // [лево, лево-12] — вывернутый и пустой, клики «не работали».
                    float l = _ctxPos.x + rt.offsetMin.x;
                    float r = _ctxPos.x + _ctxSize.x + rt.offsetMax.x;
                    float bottom = _ctxPos.y + rt.offsetMin.y;
                    float top = _ctxPos.y + rt.offsetMax.y;
                    if (upLocal.x >= l && upLocal.x <= r && upLocal.y >= bottom && upLocal.y <= top)
                    {
                        kv.Value?.Invoke();
                        return;
                    }
                }
                CloseContextMenu(); // клик по меню мимо пунктов — закрыть
                return;
            }
            _mapBlock?.DeactivateInputIfFocused(up);
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
