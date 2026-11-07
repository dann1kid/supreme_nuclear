using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SupremeNuclear
{
    /// <summary>
    /// Переиспользуемый uGUI-блок «выбор карты» (v0.156.2 — ВЕРТИКАЛЬНАЯ
    /// компоновка под узкую колонку ~326 px лобби): превью сверху по центру,
    /// ниже ряды «метка сверху + контролы на всю ширину» (стили 2×2 / сид /
    /// стороны / библиотека / Сгенерировать+прогресс). Один блок живёт и в
    /// лобби (правая колонка), и мог бы в панели Скримиша. Содержимое строится
    /// примитивами ХОСТА (IHost — панель отдаёт свои Label/Button_/MakeInput и
    /// регистрирует poll-клики сама); генерация — MapGenAsync (воркер + ступени
    /// с превью). Гостям лобби блок неинтерактивен (карту задаёт хост, локальная
    /// генерация/превью едут сами по снапшотам состояния).
    /// </summary>
    internal sealed class MapSetupBlock : IDisposable
    {
        /// <summary>Примитивы и poll-клики отдаёт панель-владелец (свой канвас,
        /// свой список _clickables — паттерн SkirmishPanel).</summary>
        internal interface IHost
        {
            RectTransform Panel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
                Vector2 offsetMin, Vector2 offsetMax, Color bg);
            Text Label(Transform parent, string name, string text, int size, Color color,
                TextAnchor align, bool multiLine = false);
            Button Button_(Transform parent, string name, string label, Action onClick, int fontSize = 16);
            InputField MakeInput(RectTransform row, string name, string initial,
                InputField.ContentType contentType, float left, float right);
            Text MakeInputText(string name, Color color, int size);
        }

        private readonly IHost _host;
        private readonly RectTransform _container;  // весь блок (видимость одной кнопкой)
        private InputField _seedInput;
        private Button _generateButton;
        private RectTransform _progressFill;
        private Text _genStatus;
        private Image _previewImage;
        private Text _libraryLabel;
        private readonly List<Button> _styleButtons = new List<Button>();
        private readonly List<string> _styleTemplates = new List<string>();
        private readonly List<string> _styleNames = new List<string>();
        private readonly Button[] _playerButtons = new Button[4];
        private readonly List<Button> _libraryButtons = new List<Button>();
        private bool _interactive = true;
        private bool _disposed;

        /// <summary>Выбор сменился (панель обновит свои гейты).</summary>
        internal event Action SelectionChanged;

        /// <param name="top">Верхний край блока относительно parent. Вертикальный
        /// стек: превью → стили → сид → стороны → библиотека → генерация.</param>
        internal MapSetupBlock(IHost host, RectTransform parent, float top)
        {
            _host = host;
            _container = host.Panel("MapSetup", parent, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, top - 700f), new Vector2(-8f, top), new Color(0f, 0f, 0f, 0f));
            float y = -6f; // курсор от верха вниз; top задаёт отступ (под «Победой»)

            // Превью: квадрат 130×130 по центру + статус под ним.
            var pvBg = host.Panel("previewBg", _container, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(-65f, y - 130f), new Vector2(65f, y), SupcomUi.PanelBg2);
            var pvGo = new GameObject("preview", typeof(RectTransform), typeof(Image));
            var pvRt = (RectTransform)pvGo.transform;
            pvRt.SetParent(pvBg, false);
            pvRt.anchorMin = new Vector2(0.5f, 0.5f);
            pvRt.anchorMax = new Vector2(0.5f, 0.5f);
            pvRt.offsetMin = new Vector2(-58f, -58f);
            pvRt.offsetMax = new Vector2(58f, 58f);
            _previewImage = pvGo.GetComponent<Image>();
            _previewImage.raycastTarget = false;
            _previewImage.preserveAspect = true;
            _genStatus = host.Label(pvBg, "genStatus", "", 13, SupcomUi.TextDim, TextAnchor.MiddleCenter);
            var gsRt = (RectTransform)_genStatus.transform;
            gsRt.anchorMin = new Vector2(0f, 0f);
            gsRt.anchorMax = Vector2.one;
            gsRt.offsetMin = new Vector2(4f, 2f);
            gsRt.offsetMax = new Vector2(-4f, 18f);
            y -= 152f; // превью 130 + 22 зазор (распределение, юзер 2026-11-06)

            // Карта: метка + 4 стиля сеткой 2×2.
            y = Header(_container, y, Loc.Tr("Карта"));
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
            for (int i = 0; i < 4; i++)
            {
                string style = SkirmishService.StyleOrder[i];
                var btn = _host.Button_(_container, "style_" + style,
                    labels.TryGetValue(style, out string label) ? label : style,
                    () => SelectStyle(style), 14);
                var rt = (RectTransform)btn.transform;
                int col = i % 2, row = i / 2;
                rt.anchorMin = new Vector2(col * 0.5f, 1f);
                rt.anchorMax = new Vector2((col + 1) * 0.5f, 1f);
                rt.offsetMin = new Vector2(col == 0 ? 2f : 6f, y - 36f - row * 40f);
                rt.offsetMax = new Vector2(col == 0 ? -6f : -2f, y - 4f - row * 40f);
                _styleButtons.Add(btn);
                _styleNames.Add(style);
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
            y -= 84f;

            // Сид: метка + input (0..0.62) + «Случайный» (0.64..1).
            y = Header(_container, y, Loc.Tr("Сид"));
            var seedRow = host.Panel("seedRow", _container, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(2f, y - 34f), new Vector2(-2f, y), new Color(0f, 0f, 0f, 0f));
            _seedInput = host.MakeInput(seedRow, "seed",
                Settings.MapGenSeed != 0 ? Settings.MapGenSeed.ToString() : "",
                InputField.ContentType.IntegerNumber, 2f, -2f);
            ((RectTransform)_seedInput.transform).anchorMin = new Vector2(0f, 0f);
            ((RectTransform)_seedInput.transform).anchorMax = new Vector2(0.6f, 1f);
            ((RectTransform)_seedInput.transform).offsetMin = new Vector2(2f, 1f);
            ((RectTransform)_seedInput.transform).offsetMax = new Vector2(0f, -1f);
            var placeholder = host.MakeInputText("ph", SupcomUi.TextDim, 15);
            placeholder.text = Loc.Tr("случайный");
            placeholder.transform.SetParent(_seedInput.transform, false);
            _seedInput.placeholder = placeholder;
            var dice = host.Button_(seedRow, "dice", Loc.Tr("Случайный"), () =>
            {
                if (_seedInput != null)
                    _seedInput.text = "";
            }, 14);
            var dRt = (RectTransform)dice.transform;
            dRt.anchorMin = new Vector2(0.63f, 0f);
            dRt.anchorMax = new Vector2(1f, 1f);
            dRt.offsetMin = new Vector2(4f, 1f);
            dRt.offsetMax = new Vector2(-2f, -1f);
            y -= 40f;

            // Стороны: метка + 4 кнопки в ряд (по 25% ширины).
            y = Header(_container, y, Loc.Tr("Стороны"));
            string[] sideLabels = { "1×1", "2×2", "3×3", "4×4" }; // числа — вне локализации
            int[] sideValues = { 2, 4, 6, 8 };
            for (int i = 0; i < 4; i++)
            {
                int players = sideValues[i];
                var btn = _host.Button_(_container, "players_" + players, sideLabels[i], () =>
                {
                    _players = players;
                    RefreshDynamic();
                    SelectionChanged?.Invoke();
                }, 14);
                var rt = (RectTransform)btn.transform;
                rt.anchorMin = new Vector2(i * 0.25f, 1f);
                rt.anchorMax = new Vector2((i + 1) * 0.25f, 1f);
                rt.offsetMin = new Vector2(i == 0 ? 2f : 4f, y - 32f);
                rt.offsetMax = new Vector2(i == 3 ? -2f : -4f, y);
                _playerButtons[i] = btn;
            }
            y -= 38f;

            // Библиотека: метка + ◀ [имя] ▶ + «Взять» (вторая строка).
            y = Header(_container, y, Loc.Tr("Библиотека"));
            var libRow = host.Panel("libRow", _container, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(2f, y - 32f), new Vector2(-2f, y), new Color(0f, 0f, 0f, 0f));
            var prev = host.Button_(libRow, "lib_prev", "◀", () => CycleSaved(-1), 13);
            var pRt = (RectTransform)prev.transform;
            pRt.anchorMin = new Vector2(0f, 0f);
            pRt.anchorMax = new Vector2(0.14f, 1f);
            pRt.offsetMin = new Vector2(2f, 1f);
            pRt.offsetMax = new Vector2(0f, -1f);
            _libraryLabel = host.Label(libRow, "lib_name", Loc.Tr("(нет карт)"), 12,
                SupcomUi.TextDim, TextAnchor.MiddleCenter);
            var lRt = (RectTransform)_libraryLabel.transform;
            lRt.anchorMin = new Vector2(0.15f, 0f);
            lRt.anchorMax = new Vector2(0.71f, 1f);
            lRt.offsetMin = new Vector2(2f, 1f);
            lRt.offsetMax = new Vector2(-2f, -1f);
            var next = host.Button_(libRow, "lib_next", "▶", () => CycleSaved(1), 13);
            var nRt = (RectTransform)next.transform;
            nRt.anchorMin = new Vector2(0.72f, 0f);
            nRt.anchorMax = new Vector2(0.86f, 1f);
            nRt.offsetMin = new Vector2(0f, 1f);
            nRt.offsetMax = new Vector2(0f, -1f);
            _libraryButtons.Add(prev);
            _libraryButtons.Add(next);
            var take = host.Button_(libRow, "lib_take", Loc.Tr("Взять"), TakeSaved, 13);
            var tRt = (RectTransform)take.transform;
            tRt.anchorMin = new Vector2(0.87f, 0f);
            tRt.anchorMax = new Vector2(1f, 1f);
            tRt.offsetMin = new Vector2(0f, 1f);
            tRt.offsetMax = new Vector2(-2f, -1f);
            _libraryButtons.Add(take);
            y -= 38f;

            // Сгенерировать: кнопка на всю ширину + полоса прогресса под ней.
            _generateButton = host.Button_(_container, "generate", Loc.Tr("Сгенерировать"),
                OnGenerateClicked, 15);
            var genRt = (RectTransform)_generateButton.transform;
            genRt.anchorMin = new Vector2(0f, 1f);
            genRt.anchorMax = new Vector2(1f, 1f);
            genRt.offsetMin = new Vector2(2f, y - 34f);
            genRt.offsetMax = new Vector2(-2f, y);
            var barBg = host.Panel("bar", _container, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(2f, y - 48f), new Vector2(-2f, y - 36f), SupcomUi.PanelBg);
            var fillGo = new GameObject("fill", typeof(RectTransform), typeof(Image));
            _progressFill = (RectTransform)fillGo.transform;
            _progressFill.SetParent(barBg, false);
            _progressFill.anchorMin = Vector2.zero;
            _progressFill.anchorMax = new Vector2(0f, 1f); // ширина = прогресс
            _progressFill.offsetMin = Vector2.zero;
            _progressFill.offsetMax = Vector2.zero;
            fillGo.GetComponent<Image>().color = SupcomUi.Accent;

            MapGenAsync.Changed += OnGenChanged;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            MapGenAsync.Changed -= OnGenChanged;
        }

        /// <summary>Метка-заголовок ряда (18 px + 12 зазор) — над контролами,
        /// на всю ширину. Зазор распределяет секции колонки (юзер 2026-11-06).</summary>
        private float Header(RectTransform parent, float y, string text)
        {
            var lbl = _host.Label(parent, "hdr_" + text, text, 13, SupcomUi.TextDim, TextAnchor.MiddleLeft);
            var rt = (RectTransform)lbl.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(4f, y - 18f);
            rt.offsetMax = new Vector2(-4f, y);
            return y - 30f;
        }

        // ---------- библиотека сохранённых карт ----------

        private List<SkirmishService.SavedMap> _savedMaps = new List<SkirmishService.SavedMap>();
        private int _savedIndex = -1;
        private float _nextScanAt;
        private string _statusOverride; // «шаблон сейва не найден» и пр. (до следующего действия)

        private void CycleSaved(int dir)
        {
            ScanSaved();
            if (_savedMaps.Count == 0)
                return;
            _savedIndex = (_savedIndex + dir + _savedMaps.Count) % _savedMaps.Count;
            RefreshLibrary();
        }

        private void ScanSaved()
        {
            if (Time.unscaledTime < _nextScanAt)
                return;
            _nextScanAt = Time.unscaledTime + 2f;
            _savedMaps = SkirmishService.ListSavedMaps();
            if (_savedIndex >= _savedMaps.Count)
                _savedIndex = _savedMaps.Count - 1;
        }

        private void RefreshLibrary()
        {
            ScanSaved();
            if (_libraryLabel == null)
                return;
            if (_savedMaps.Count == 0)
            {
                _libraryLabel.text = Loc.Tr("(нет карт)");
                return;
            }
            if (_savedIndex < 0)
                _savedIndex = 0;
            SkirmishService.SavedMap m = _savedMaps[_savedIndex];
            _libraryLabel.text = m.Name.Replace("SN-Terrain-", "") +
                (m.Players > 0 ? Loc.F(" ·{0}", m.Players) : "");
        }

        /// <summary>«Взять»: подставить тройку выбранной карты (шаблон → стиль,
        /// сид → поле, пул → стороны) и сразу генерировать — Built-кэш даст
        /// мгновенное превью; холодная карта посчитается фоном.</summary>
        private void TakeSaved()
        {
            ScanSaved();
            if (_savedMaps.Count == 0 || _savedIndex < 0 || _savedIndex >= _savedMaps.Count)
                return;
            SkirmishService.SavedMap m = _savedMaps[_savedIndex];
            _statusOverride = null;
            // Шаблон сейва должен быть среди пресетов стилей (кнопка стиля выбирает его).
            bool found = false;
            var styles = SkirmishService.ListStyles();
            foreach (var kv in styles)
                if (kv.Value == m.Template)
                {
                    SelectStyle(kv.Key);
                    found = true;
                    break;
                }
            if (!found || _template != m.Template)
            {
                _statusOverride = Loc.F("Шаблон сейва '{0}' не найден в MapGen/", m.Template);
                RefreshDynamic();
                return;
            }
            if (_seedInput != null)
                _seedInput.text = m.Seed.ToString();
            _players = Mathf.Max(2, m.Players); // пул сейва (легаси -1 → минимум)
            RefreshDynamic();
            SelectionChanged?.Invoke();
            RequestGeneration();
        }

        // ---------- выбор ----------

        private string _template;
        private int _players = 2;

        internal string Template => _template;
        internal int Players => _players;
        internal bool HasTemplate => _template != null;

        /// <summary>Сид из поля: пусто/мусор = 0 (случайный).</summary>
        internal int Seed => SkirmishService.ParseSeed(_seedInput != null ? _seedInput.text : "");

        private void SelectStyle(string style)
        {
            int idx = _styleNames.IndexOf(style);
            if (idx < 0 || _styleTemplates[idx] == null)
                return;
            _template = _styleTemplates[idx];
            RefreshDynamic();
            SelectionChanged?.Invoke();
        }

        /// <summary>Параметры выбора — в конфиг (общий источник с Ctrl+G).</summary>
        internal void PersistSettings()
        {
            Settings.SetMapGen(_template, Seed);
            Settings.SetMapGenPlayers(_players);
        }

        internal bool MatchesDone() => MapGenAsync.Matches(_template, Seed, _players);

        internal void RequestGeneration()
        {
            PersistSettings();
            _statusOverride = null;
            MapGenAsync.Request(_template, Seed, _players);
        }

        private void OnGenerateClicked() => RequestGeneration();

        // ---------- отображение ----------

        /// <summary>Видимость (панель прячет блок на чужой вкладке).</summary>
        internal void SetVisible(bool on)
        {
            if (_container != null)
                _container.gameObject.SetActive(on);
        }

        /// <summary>Клик мимо поля сида — снять фокус (панель зовёт из PollClicks).</summary>
        internal void DeactivateInputIfFocused(Vector2 up)
        {
            if (_seedInput != null && _seedInput.isFocused &&
                !RectTransformUtility.RectangleContainsScreenPoint(
                    (RectTransform)_seedInput.transform, up, null))
            {
                _seedInput.DeactivateInputField();
            }
        }

        /// <summary>Интерактивность (гостю лобби — только просмотр).</summary>
        internal void SetInteractive(bool on)
        {
            _interactive = on;
            RefreshDynamic();
        }

        /// <summary>Подсветки выбора + гейты кнопок.</summary>
        internal void RefreshDynamic()
        {
            bool genBusy = MapGenAsync.Current == MapGenAsync.Phase.Worker ||
                           MapGenAsync.Current == MapGenAsync.Phase.Building;
            for (int i = 0; i < _styleButtons.Count; i++)
            {
                var img = _styleButtons[i].GetComponent<Image>();
                if (img != null)
                    img.color = _styleTemplates[i] == _template && _template != null
                        ? SupcomUi.BtnActive : SupcomUi.BtnBg;
                _styleButtons[i].interactable = _interactive && _styleTemplates[i] != null;
            }
            int[] values = { 2, 4, 6, 8 };
            for (int i = 0; i < _playerButtons.Length; i++)
            {
                var img = _playerButtons[i] != null ? _playerButtons[i].GetComponent<Image>() : null;
                if (img != null)
                    img.color = values[i] == _players ? SupcomUi.BtnActive : SupcomUi.BtnBg;
                if (_playerButtons[i] != null)
                    _playerButtons[i].interactable = _interactive;
            }
            if (_generateButton != null)
                _generateButton.interactable = _interactive && HasTemplate && !genBusy;
            if (_seedInput != null)
                _seedInput.enabled = _interactive;
            foreach (Button b in _libraryButtons)
                if (b != null)
                    b.interactable = _interactive;
            if (_previewImage != null)
            {
                _previewImage.sprite = MapGenAsync.Preview;
                _previewImage.enabled = MapGenAsync.Preview != null;
            }
        }

        /// <summary>Прогресс/превью/статус генерации (по MapGenAsync) + библиотека.</summary>
        internal void RefreshGenUi()
        {
            RefreshLibrary();
            if (_progressFill == null)
                return;
            float t = Mathf.Clamp01(MapGenAsync.Progress);
            _progressFill.anchorMax = new Vector2(t, 1f);
            _progressFill.offsetMax = new Vector2(0f, 0f);
            if (_statusOverride != null && _genStatus != null &&
                (MapGenAsync.Current == MapGenAsync.Phase.Idle || MapGenAsync.Current == MapGenAsync.Phase.Done))
            {
                _genStatus.text = _statusOverride;
                return;
            }
            if (_genStatus != null)
            {
                switch (MapGenAsync.Current)
                {
                    case MapGenAsync.Phase.Worker:
                        _genStatus.text = Loc.Tr("Считаю мир…");
                        break;
                    case MapGenAsync.Phase.Building:
                        _genStatus.text = Loc.F("Сборка {0}%", Mathf.RoundToInt(t * 100f));
                        break;
                    case MapGenAsync.Phase.Done:
                        _genStatus.text = Loc.Tr("Готово");
                        break;
                    case MapGenAsync.Phase.Failed:
                        _genStatus.text = Loc.Tr("Сбой");
                        break;
                    default:
                        _genStatus.text = "";
                        break;
                }
            }
        }

        private void OnGenChanged()
        {
            RefreshGenUi();
            RefreshDynamic();
        }

        /// <summary>Свежий скан пресетов (панель зовёт на каждом Show).</summary>
        internal void RescanStyles()
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
                    _styleButtons[i].interactable = found != null && _interactive;
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
    }
}
