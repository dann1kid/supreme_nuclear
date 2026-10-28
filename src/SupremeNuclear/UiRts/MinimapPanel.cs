using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SupremeNuclear
{
    /// <summary>Плавающая миникарта в духе SupCom (опциональное окно): фон — готовый
    /// MapSettings.MapImage, иконки юнитов с развед-гейтом как в IMGUI-иконках, рамка
    /// текущего обзора камеры. ЛКМ — прыжок камеры, ПКМ — приказ выделенным.</summary>
    public sealed class MinimapPanel : MonoBehaviour
    {
        private FloatingWindow _window;
        private RectTransform _mapRect;      // фон карты (RawImage со спрайтом или заливка)
        private RawImage _mapRaw;            // фон: uvRect показывает окно обзора (зум)
        private RectTransform _iconsRoot;
        private readonly Image[] _viewEdges = new Image[4];
        // Окно обзора миникарты в нормализованных координатах карты (0..1):
        // _viewSize=1 — вся карта; зум колесом уменьшает окно, _viewMin — его левый-низ.
        private Vector2 _viewMin = Vector2.zero;
        private Vector2 _viewSize = Vector2.one;
        private const float ZoomPerNotch = 1.25f; // шаг зума на щелчок колеса
        private const float IconScaleMax = 4f;    // предел роста иконок при зуме (×)

        /// <summary>Экранный прямоугольник окна миникарты в GUI-координатах (IMGUI:
        /// y сверху вниз, как в OnGUI). CommanderHud гасит под ним мировые точечные
        /// элементы — IMGUI рисуется ПОВЕРХ uGUI, и без маски иконки мира
        /// штампуются поверх миникарты и смешиваются с её блипами.</summary>
        internal static Rect HudMaskRect;
        private static readonly Vector3[] _maskCorners = new Vector3[4];

        // Рельеф: сетка высот ReliefN×ReliefN по GroundY (порциями между обновлениями —
        // рейкасты дорогие, порезка по кадрам), текстура гипсометрической окраски +
        // горизонталей; включается кнопкой «Рельеф» под картой.
        private const int ReliefN = 128;
        private const int ReliefBatch = 512; // точек на тик обновления (10 Гц), ~3 с на карту
        private float[] _reliefHeights;      // [y*N+x], заполняется прогрессом
        private int _reliefSampled;
        private Texture2D _reliefTexture;
        private bool _reliefRequested;       // режим включали — сэмплируем лениво
        private Vector2 _reliefMin, _reliefMax;
        private Text _reliefLabel;
        private readonly Image[] _rangeChipImg = new Image[4];  // чипы колец дальности (АС/АА/разведка/стройка)
        private readonly Text[] _rangeChipLbl = new Text[4];
        private Image _weaponsChipImg;                           // чип «Оружие» (кольца по классам вооружения)
        private Text _weaponsChipLbl;
        private readonly Dictionary<Unit, Image> _blips = new Dictionary<Unit, Image>();
        private readonly List<Unit> _staleBlips = new List<Unit>();
        private readonly Dictionary<Airbase, Image> _airbaseBlips = new Dictionary<Airbase, Image>();
        private readonly List<Airbase> _staleAirbases = new List<Airbase>();
        private float _nextRefresh;
        private float _nextErrorLog = -10f;
        private float _nextBuildAttempt;
        private Canvas _scheduledCanvas; // канвас, ожидающий постройки (пауза на CanvasScaler)
        private Canvas _builtOnCanvas; // канвас, на котором построено окно (смена = пересборка)
        private readonly Vector2[] _viewPts = new Vector2[4];
        private readonly Vector3[] _viewCorners = new Vector3[4];

        private static readonly Color Friendly = new Color(0.45f, 0.9f, 0.5f, 0.95f);
        private static readonly Color Hostile = new Color(1f, 0.35f, 0.3f, 0.95f);
        private static readonly Color Deposit = new Color(1f, 0.85f, 0.2f, 0.95f);
        private static readonly Color MissileFriendly = new Color(0.5f, 0.9f, 0.55f, 0.9f);

        private void Update()
        {
            try
            {
                UpdateSafe();
            }
            catch (System.Exception e)
            {
                // Троттлим: без этого исключение каждый кадр заливает лог (лаг от I/O).
                if (Time.unscaledTime - _nextErrorLog > 5f)
                {
                    _nextErrorLog = Time.unscaledTime;
                    Log.Error("MinimapPanel: сбой Update — " + e.GetType().Name + ": " + e.Message +
                              " | " + e.StackTrace);
                }
            }
        }

        private void UpdateSafe()
        {
            if (!Settings.MinimapEnabled || !GameApi.InMission)
            {
                HudMaskRect = Rect.zero; // окна нет — маска для мировых иконок не нужна
                return;
            }
            Canvas canvas = SupcomUi.Instance != null ? SupcomUi.Instance.Canvas : null;
            if (canvas == null)
                return;
            if (_window == null || _window.Root == null || canvas != _builtOnCanvas)
            {
                // Новый канвас (EnsureUi пересобрал) — строимся не раньше 0.5 с после его
                // ПОЯВЛЕНИЯ: в кадре создания RectTransform канваса ещё в физических
                // пикселях (3840x2160), CanvasScaler применится кадром позже — иначе
                // дефолтная позиция уедет за экран.
                if (_scheduledCanvas != canvas)
                {
                    _scheduledCanvas = canvas;
                    _nextBuildAttempt = Time.unscaledTime + 0.5f;
                }
                if (Time.unscaledTime < _nextBuildAttempt)
                    return;
                _nextBuildAttempt = Time.unscaledTime + 5f; // сбой построения — пауза между попытками
                if (_window != null && _window.Root != null)
                    _window.DestroyAll(); // окно жило на умершем канвасе — сносим вкладку-остаток
                Build(canvas);
                if (_window == null || _window.Root == null)
                    return;
                _builtOnCanvas = canvas;
                _nextBuildAttempt = Time.unscaledTime + 0.5f; // канвас снова пересобрали? — пауза
            }

            // Маска для CommanderHud — каждый кадр (окно двигается драгом плавно).
            UpdateHudMask();

            // Зум колесом при наведении на карту (каждый кадр — дельты колеса
            // одноразовые). Камера над uGUI не зумится (RtsInput.PointerOverUi
            // в CommanderCameraState) — двойного действия нет; защита от чата — Typing.
            float wheel = RtsInput.Wheel;
            if (Mathf.Abs(wheel) >= 0.05f && !RtsInput.Typing && _mapRect != null &&
                RectTransformUtility.RectangleContainsScreenPoint(_mapRect, Input.mousePosition, null))
            {
                ZoomAtScreen(Input.mousePosition, wheel);
            }

            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + 0.1f;
            if (!GameApi.TryGetMapBounds(out Vector2 min, out Vector2 max))
                return;
            // Самовосстановление позиции: канвас мог сжаться CanvasScaler'ом уже ПОСЛЕ
            // постройки окна — держим окно целиком на канвасе (не «уголком» за краем).
            _window.ClampToParent();
            SampleReliefBatch(min, max);
            ApplyViewUv(); // фон мог смениться (рельеф досэмплировался)
            UpdateReliefLabel();
            UpdateBlips(min, max);
            UpdateAirbases(min, max);
            UpdateViewRect(min, max);
        }

        // ---------- построение ----------

        /// <summary>Пересчёт маски: прямоугольник окна в экранных пикселях
        /// (ScreenSpaceOverlay — без камеры, как в кликах карты) с переворотом Y
        /// в GUI-координаты. Закрытое окно (только вкладка) маски не даёт.</summary>
        private void UpdateHudMask()
        {
            if (_window == null || _window.Root == null || !_window.IsVisible)
            {
                HudMaskRect = Rect.zero;
                return;
            }
            _window.Root.GetWorldCorners(_maskCorners);
            float xMin = float.MaxValue, xMax = float.MinValue, yMin = float.MaxValue, yMax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(null, _maskCorners[i]);
                if (sp.x < xMin) xMin = sp.x;
                if (sp.x > xMax) xMax = sp.x;
                if (sp.y < yMin) yMin = sp.y;
                if (sp.y > yMax) yMax = sp.y;
            }
            HudMaskRect = Rect.MinMaxRect(xMin, Screen.height - yMax, xMax, Screen.height - yMin);
        }

        private void Build(Canvas canvas)
        {
            try
            {
                BuildSafe(canvas);
            }
            catch (System.Exception e)
            {
                // Сборка не удалась: сносим недостроенное окно и вкладку ЦЕЛИКОМ —
                // иначе каждая попытка оставляет на канвасе «копию» окна.
                _window?.DestroyAll();
                _window = null;
                _mapRect = null;
                _iconsRoot = null;
                Log.Error("MinimapPanel: сбой построения — " + e.GetType().Name + ": " + e.Message +
                          " | " + e.StackTrace);
            }
        }

        private void BuildSafe(Canvas canvas)
        {
            Font font = SupcomUi.Instance != null ? SupcomUi.Instance.UiFont : null;
            float size = Mathf.Clamp(Settings.MinimapSize, 240f, 900f);
            Rect canvasRect = canvas.GetComponent<RectTransform>().rect;
            Vector2 pos;
            if (Settings.MinimapX >= 0f && Settings.MinimapY >= 0f)
            {
                pos = new Vector2(Settings.MinimapX, Settings.MinimapY);
            }
            else
            {
                pos = new Vector2(Mathf.Max(12f, canvasRect.width - size - 12f),
                    Mathf.Max(12f, canvasRect.height - size - 12f));
            }

            _window = FloatingWindow.Create(canvas.transform, font, Loc.Tr("Карта"), pos, new Vector2(size, size), SaveState);
            // Позиция (сохранённая или дефолт) могла считаться по другому размеру канваса
            // (физические пиксели до CanvasScaler) — сразу держим окно целиком на канвасе.
            _window.ClampToParent();
            // Видимость из конфига: закрытое окно показывает только вкладку восстановления.
            _window.SetVisible(Settings.MinimapVisible);

            // Фон — RawImage: зум показывает под-прямоугольник карты через uvRect
            // (Image такого не умеет). Текстуру/цвет выбирает ApplyViewUv (рельеф/карта/
            // фолбэк). Над нижней полоской кнопок.
            var mapGo = new GameObject("map", typeof(RectTransform), typeof(RawImage));
            _mapRect = (RectTransform)mapGo.transform;
            _mapRect.SetParent(_window.Content, false);
            _mapRect.anchorMin = Vector2.zero;
            _mapRect.anchorMax = Vector2.one;
            _mapRect.offsetMin = new Vector2(0f, 46f); // над двухстрочной полоской кнопок
            _mapRect.offsetMax = new Vector2(0f, 0f);
            var mapImg = mapGo.GetComponent<RawImage>();
            _mapRaw = mapImg;
            mapImg.raycastTarget = true;
            mapImg.uvRect = new Rect(0f, 0f, 1f, 1f);
            var clicks = mapGo.AddComponent<MapClicks>();
            clicks.Owner = this;

            // Полоска режимов под картой, ДВЕ строки: верх — «Рельеф» (фон: высоты
            // + горизонтали) и «Заливка» (стиль колец), низ — четыре чипа колец.
            var barGo = new GameObject("modeBar", typeof(RectTransform));
            var barRt = (RectTransform)barGo.transform;
            barRt.SetParent(_window.Content, false);
            barRt.anchorMin = new Vector2(0f, 0f);
            barRt.anchorMax = new Vector2(1f, 0f);
            barRt.offsetMin = new Vector2(0f, 0f);
            barRt.offsetMax = new Vector2(0f, 44f);

            var reliefGo = new GameObject("reliefBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            var rrt = (RectTransform)reliefGo.transform;
            rrt.SetParent(barRt, false);
            rrt.anchorMin = rrt.anchorMax = new Vector2(0f, 0.5f);
            rrt.pivot = new Vector2(0f, 0.5f);
            rrt.anchoredPosition = new Vector2(2f, 11f); // верхняя строка
            rrt.sizeDelta = new Vector2(120f, 20f);
            reliefGo.GetComponent<Image>().color = UiKit.BtnBg;
            reliefGo.GetComponent<Button>().onClick.AddListener(OnReliefToggle);

            var lblGo = new GameObject("lbl", typeof(RectTransform));
            var lrt = (RectTransform)lblGo.transform;
            lrt.SetParent(rrt, false);
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            var lbl = lblGo.AddComponent<Text>();
            if (font != null)
                lbl.font = font;
            lbl.text = Loc.Tr("Рельеф: …");
            lbl.fontSize = 13;
            lbl.color = UiKit.TextDim;
            lbl.alignment = TextAnchor.MiddleCenter;
            lbl.raycastTarget = false;
            _reliefLabel = lbl;

            // Кольца дальности выделенных юнитов (SupCom): четыре чипа в НИЖНЕЙ
            // строке полоски, каждый на четверть ширины (влезают при любом размере
            // окна); цвет чипа = цвет колец (легенда). Клик — вкл/выкл класс.
            string[] rangeNames = { Loc.Tr("Назем"), Loc.Tr("Воздух"), Loc.Tr("Разведка"), Loc.Tr("Стройка") };
            for (int ri = 0; ri < rangeNames.Length; ri++)
            {
                int kind = ri;
                var chipGo = new GameObject("rangeBtn" + ri, typeof(RectTransform), typeof(Image), typeof(Button));
                var crt = (RectTransform)chipGo.transform;
                crt.SetParent(barRt, false);
                float x0 = ri / (float)rangeNames.Length, x1 = (ri + 1) / (float)rangeNames.Length;
                crt.anchorMin = new Vector2(x0, 0f);   // нижняя строка, четверть ширины
                crt.anchorMax = new Vector2(x1, 0.5f);
                crt.offsetMin = new Vector2(3f, 2f);
                crt.offsetMax = new Vector2(-3f, -2f);
                chipGo.GetComponent<Button>().onClick.AddListener(() => OnRangeToggle(kind));

                var chipLblGo = new GameObject("lbl", typeof(RectTransform));
                var clrt = (RectTransform)chipLblGo.transform;
                clrt.SetParent(crt, false);
                clrt.anchorMin = Vector2.zero;
                clrt.anchorMax = Vector2.one;
                clrt.offsetMin = clrt.offsetMax = Vector2.zero;
                var chipLbl = chipLblGo.AddComponent<Text>();
                if (font != null)
                    chipLbl.font = font;
                chipLbl.text = rangeNames[ri];
                chipLbl.fontSize = 12;
                chipLbl.alignment = TextAnchor.MiddleCenter;
                chipLbl.raycastTarget = false;
                _rangeChipImg[ri] = chipGo.GetComponent<Image>();
                _rangeChipLbl[ri] = chipLbl;
            }

            // «Оружие» (верхняя строка справа): режим колец ПО КЛАССАМ вооружения
            // (пушки/ракеты/бомбы, как кнопки гейтов) вместо доменных колец атаки —
            // видно, чем осталось бить при выключении классов. Кольца рисуются
            // линиями (SupCom), заливки нет.
            var wbtnGo = new GameObject("weaponsBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            var wrt = (RectTransform)wbtnGo.transform;
            wrt.SetParent(barRt, false);
            wrt.anchorMin = wrt.anchorMax = new Vector2(1f, 0.5f);
            wrt.pivot = new Vector2(1f, 0.5f);
            wrt.anchoredPosition = new Vector2(-2f, 11f); // верхняя строка, крайний справа
            wrt.sizeDelta = new Vector2(65f, 20f);
            wbtnGo.GetComponent<Button>().onClick.AddListener(() =>
            {
                Settings.SetRangeWeapons(!Settings.RangeRingWeapons);
                Settings.SaveConfig();
                UpdateRangeChips();
            });
            var wlblGo = new GameObject("lbl", typeof(RectTransform));
            var wlrt = (RectTransform)wlblGo.transform;
            wlrt.SetParent(wrt, false);
            wlrt.anchorMin = Vector2.zero;
            wlrt.anchorMax = Vector2.one;
            wlrt.offsetMin = wlrt.offsetMax = Vector2.zero;
            var wLbl = wlblGo.AddComponent<Text>();
            if (font != null)
                wLbl.font = font;
            wLbl.text = Loc.Tr("Оружие");
            wLbl.fontSize = 12;
            wLbl.alignment = TextAnchor.MiddleCenter;
            wLbl.raycastTarget = false;
            _weaponsChipImg = wbtnGo.GetComponent<Image>();
            _weaponsChipLbl = wLbl;
            UpdateRangeChips();

            // RectMask2D: при зуме иконки/рамка обзора не вылезают за миникарту.
            var iconsGo = new GameObject("icons", typeof(RectTransform), typeof(RectMask2D));
            _iconsRoot = (RectTransform)iconsGo.transform;
            _iconsRoot.SetParent(_mapRect, false);
            _iconsRoot.anchorMin = Vector2.zero;
            _iconsRoot.anchorMax = Vector2.one;
            _iconsRoot.offsetMin = _iconsRoot.offsetMax = Vector2.zero;

            for (int i = 0; i < 4; i++)
            {
                var edgeGo = new GameObject("viewEdge" + i, typeof(RectTransform), typeof(Image));
                var ert = (RectTransform)edgeGo.transform;
                ert.SetParent(_iconsRoot, false);
                ert.sizeDelta = new Vector2(60f, 1.5f);
                var eimg = edgeGo.GetComponent<Image>();
                eimg.color = new Color(0.8f, 1f, 0.85f, 0.8f);
                eimg.raycastTarget = false;
                _viewEdges[i] = eimg;
            }

            _blips.Clear();
            _airbaseBlips.Clear();
            // Новое окно — вся карта (зум между сценами/картами не переносим),
            // рельеф пересчитывается для новой карты.
            _viewMin = Vector2.zero;
            _viewSize = Vector2.one;
            if (_reliefTexture != null)
            {
                Destroy(_reliefTexture);
                _reliefTexture = null;
            }
            _reliefHeights = null;
            _reliefSampled = 0;
            if (Settings.MinimapRelief)
                _reliefRequested = true;
            ApplyViewUv();
            Log.Info("MinimapPanel: построена, окно " + pos.x.ToString("F0") + "x" + pos.y.ToString("F0") +
                     ", размер " + size.ToString("F0") + ", канвас " + canvasRect.width.ToString("F0") + "x" +
                     canvasRect.height.ToString("F0") +
                     ", фон=" + (GameApi.TryGetMapImage(out Sprite s2) && s2 != null ? s2.name : "нет спрайта"));
        }

        private void SaveState(Vector2 pos, Vector2 size, bool visible)
        {
            Settings.MinimapX = pos.x;
            Settings.MinimapY = pos.y;
            Settings.MinimapSize = size.x;
            Settings.MinimapVisible = visible;
            Settings.SaveConfig();
        }

        // ---------- иконки ----------

        private void UpdateBlips(Vector2 min, Vector2 max)
        {
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            float now = Time.timeSinceLevelLoad;
            bool missileBlink = Mathf.Repeat(Time.unscaledTime * 2.5f, 1f) < 0.7f;
            // Иконки растут с зумом миникарты (на всей карте — базовый размер).
            float iconScale = Mathf.Clamp(1f / _viewSize.x, 1f, IconScaleMax);
            // Отрисованные в этом проходе: всё, чего нет в наборе (умер, вышел из разведки,
            // мигает «скрытой» фазой) — сносим, иначе призраки копятся на старых позициях.
            HashSet<Unit> used = new HashSet<Unit>();

            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled)
                    continue;
                bool friendly = unit.NetworkHQ != null && unit.NetworkHQ == hq;
                Color color;
                float sizePx;
                bool diamond = false;
                // Свои/нейтралы — живая позиция; чужие — последняя известная разведке
                // (как в IMGUI-иконках: живая позиция невидимого врага = чит).
                Vector3 blipPos = unit.transform.position;

                if (unit is Missile)
                {
                    if (!friendly && !missileBlink)
                        continue;
                    color = friendly ? MissileFriendly : Hostile;
                    sizePx = 6f;
                }
                else if (EconomyService.IsExtractor(unit))
                {
                    // Ничейные месторождения — фича карты, видны всегда (жёлтый ромб);
                    // чужие/свои экстракторы — свои всегда, чужие только по разведке.
                    FactionHQ owner = unit.NetworkHQ;
                    if (owner == null)
                    {
                        color = Deposit;
                        diamond = true;
                        sizePx = 8f;
                    }
                    else if (owner == hq)
                    {
                        color = Friendly;
                        sizePx = 8f;
                    }
                    else
                    {
                        TrackingInfo info = hq != null ? hq.GetTrackingData(unit.persistentID) : null;
                        if (info == null || now - info.lastSpottedTime > Settings.StaleContactSec)
                            continue;
                        blipPos = info.GetPosition().ToLocalPosition();
                        color = Hostile;
                        sizePx = 8f;
                    }
                }
                else if (friendly)
                {
                    color = Friendly;
                    sizePx = unit is Ship ? 9f : (unit is Aircraft ? 7f : 7f);
                }
                else if (unit is Building)
                {
                    // Чужие здания — только по разведке (как подвижные).
                    TrackingInfo info = hq != null ? hq.GetTrackingData(unit.persistentID) : null;
                    if (info == null || now - info.lastSpottedTime > Settings.StaleContactSec)
                        continue;
                    blipPos = info.GetPosition().ToLocalPosition();
                    color = Hostile;
                    sizePx = 8f;
                }
                else
                {
                    // Чужие подвижные — только по разведке (как в IMGUI-иконках).
                    TrackingInfo info = hq != null ? hq.GetTrackingData(unit.persistentID) : null;
                    if (info == null)
                        continue;
                    float age = now - info.lastSpottedTime;
                    if (age > Settings.StaleContactSec)
                        continue;
                    bool live = age < 4f;
                    blipPos = info.GetPosition().ToLocalPosition();
                    color = live
                        ? Hostile
                        : new Color(0.75f, 0.75f, 0.75f, Mathf.Lerp(0.7f, 0.25f, age / Settings.StaleContactSec));
                    sizePx = unit is Ship ? 9f : 7f;
                }

                if (used.Count >= 600)
                    break; // предохранитель от тысяч точек на тестовых картах

                // Иконка — как у IMGUI-иконок: авиация с mapOrient — ориентированный
                // силуэт mapIcon (миникарта фиксирована «север вверх», поэтому угол —
                // чистый курс, без поворота в кадр камеры; знак минус: uGUI вращает
                // против часовой, IMGUI-вариант — по часовой), прочие — визор-маркеры
                // friendly/hostileIcon. Без спрайта (ракеты, месторождения) — квадрат/ромб.
                Sprite sprite = null;
                float angle = 0f;
                if (!diamond && !(unit is Missile))
                {
                    if (unit is Aircraft && unit.definition != null &&
                        unit.definition.mapOrient && unit.definition.mapIcon != null)
                    {
                        sprite = unit.definition.mapIcon;
                        Vector3 fwd = unit.transform.forward;
                        fwd.y = 0f;
                        if (fwd.sqrMagnitude > 1e-4f)
                        {
                            fwd.Normalize();
                            angle = -Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                        }
                    }
                    else if (unit.definition != null)
                    {
                        sprite = friendly
                            ? (unit.definition.friendlyIcon != null
                                ? unit.definition.friendlyIcon : unit.definition.mapIcon)
                            : (unit.definition.hostileIcon != null
                                ? unit.definition.hostileIcon : unit.definition.friendlyIcon);
                    }
                }
                PlaceBlip(unit, used, blipPos, min, max, color, sizePx * iconScale, diamond, sprite, angle);
            }

            // Убираем блипы, не попавшие в этот проход.
            _staleBlips.Clear();
            foreach (KeyValuePair<Unit, Image> kv in _blips)
                if (!used.Contains(kv.Key))
                    _staleBlips.Add(kv.Key);
            foreach (Unit u in _staleBlips)
            {
                if (_blips.TryGetValue(u, out Image img) && img != null)
                    Destroy(img.gameObject);
                _blips.Remove(u);
            }
        }

        private void PlaceBlip(Unit unit, HashSet<Unit> used, Vector3 world, Vector2 min, Vector2 max,
            Color color, float sizePx, bool diamond, Sprite sprite, float angleDeg)
        {
            Vector2 local = WorldToLocal(world, min, max);
            if (!_blips.TryGetValue(unit, out Image img) || img == null)
            {
                var go = new GameObject("blip", typeof(RectTransform), typeof(Image));
                img = go.GetComponent<Image>();
                img.raycastTarget = false;
                ((RectTransform)go.transform).SetParent(_iconsRoot, false);
                _blips[unit] = img;
            }
            img.gameObject.SetActive(true);
            img.color = color;
            img.sprite = sprite;
            var rt = (RectTransform)img.transform;
            // Пропорции как у IMGUI-иконок: высота sizePx, ширина по аспекту спрайта
            // с потолком (широкие силуэты авиации не растягиваются на пол-миникарты).
            float wpx = sizePx;
            if (sprite != null && sprite.texture != null)
            {
                float aspect = sprite.textureRect.width / Mathf.Max(sprite.textureRect.height, 1f);
                wpx = Mathf.Min(sizePx * aspect, sizePx * 3f);
            }
            // Вне окна обзора (зум) не рисуем (RectMask2D страхует рамку, блипы гасим —
            // лишние Image не двигаем), но в used отмечаем — иначе churn создания/сноса.
            Rect r = _mapRect.rect;
            if (Mathf.Abs(local.x) > r.width * 0.5f + wpx * 0.5f + 8f ||
                Mathf.Abs(local.y) > r.height * 0.5f + sizePx * 0.5f + 8f)
            {
                img.gameObject.SetActive(false);
                used.Add(unit);
                return;
            }
            rt.sizeDelta = new Vector2(wpx, sizePx);
            rt.localPosition = new Vector3(local.x, local.y, 0f);
            rt.localRotation = diamond
                ? Quaternion.Euler(0f, 0f, 45f)
                : Quaternion.Euler(0f, 0f, angleDeg);
            used.Add(unit);
        }

        private void UpdateAirbases(Vector2 min, Vector2 max)
        {
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            if (FactionRegistry.airbaseLookup == null)
                return;
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null)
                    continue;
                FactionHQ owner = airbase.CurrentHQ;
                Color c = owner == hq
                    ? Friendly
                    : (owner != null && GameApi.IsAirbaseScouted(airbase, hq) ? Hostile : Deposit);
                Vector3 pos = airbase.transform.position;
                Vector2 local = WorldToLocal(pos, min, max);
                // Ромб аэродрома тоже растёт с зумом; вне окна обзора — гасим, запись держим.
                float sz = 10f * Mathf.Clamp(1f / _viewSize.x, 1f, IconScaleMax);
                if (Mathf.Abs(local.x) > _mapRect.rect.width * 0.5f + sz * 0.5f + 8f ||
                    Mathf.Abs(local.y) > _mapRect.rect.height * 0.5f + sz * 0.5f + 8f)
                {
                    if (_airbaseBlips.TryGetValue(airbase, out Image hidden) && hidden != null)
                        hidden.gameObject.SetActive(false);
                    continue;
                }
                if (!_airbaseBlips.TryGetValue(airbase, out Image img) || img == null)
                {
                    var go = new GameObject("airbase", typeof(RectTransform), typeof(Image));
                    img = go.GetComponent<Image>();
                    img.raycastTarget = false;
                    ((RectTransform)go.transform).SetParent(_iconsRoot, false);
                    _airbaseBlips[airbase] = img;
                }
                img.gameObject.SetActive(true);
                img.color = c;
                var rt = (RectTransform)img.transform;
                rt.sizeDelta = new Vector2(sz, sz);
                rt.localPosition = new Vector3(local.x, local.y, 0f);
                rt.localRotation = Quaternion.Euler(0f, 0f, 45f);
            }

            _staleAirbases.Clear();
            foreach (KeyValuePair<Airbase, Image> kv in _airbaseBlips)
                if (kv.Key == null)
                    _staleAirbases.Add(kv.Key);
            foreach (Airbase a in _staleAirbases)
            {
                if (_airbaseBlips.TryGetValue(a, out Image img) && img != null)
                    Destroy(img.gameObject);
                _airbaseBlips.Remove(a);
            }
        }

        // ---------- рамка обзора камеры ----------

        private void UpdateViewRect(Vector2 min, Vector2 max)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            _viewCorners[0] = new Vector3(0f, 0f);
            _viewCorners[1] = new Vector3(cam.pixelWidth, 0f);
            _viewCorners[2] = new Vector3(cam.pixelWidth, cam.pixelHeight);
            _viewCorners[3] = new Vector3(0f, cam.pixelHeight);
            for (int i = 0; i < 4; i++)
            {
                Ray ray = cam.ScreenPointToRay(_viewCorners[i]);
                // Взгляд вдаль: верхние лучи уходят выше горизонта и не попадают ни в
                // землю, ни в плоскость моря — ведём их по направлению до карты
                // (RayToMapEdge), а не замираем рамку на прошлой позиции. Все точки
                // клампим в границы карты: рамка обзора не выходит за миникарту
                // (как SupCom обрезает рамку о край карты).
                if (!GameApi.TryGetGroundPoint(ray, out Vector3 hit) && !RayToMapEdge(ray, min, max, out hit))
                {
                    SetViewRectVisible(false);
                    return;
                }
                hit.x = Mathf.Clamp(hit.x, min.x, max.x);
                hit.z = Mathf.Clamp(hit.z, min.y, max.y);
                _viewPts[i] = WorldToLocal(hit, min, max);
            }
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = _viewPts[i];
                Vector2 b = _viewPts[(i + 1) % 4];
                Vector2 d = b - a;
                float len = d.magnitude;
                Image edge = _viewEdges[i];
                if (edge == null)
                    return;
                // Вырожденное ребро (два угла легли в одну точку края) не рисуем.
                edge.gameObject.SetActive(len >= 1f);
                if (len < 1f)
                    continue;
                var rt = (RectTransform)edge.transform;
                rt.localPosition = new Vector3((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, 0f);
                rt.sizeDelta = new Vector2(len, 1.5f);
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            }
        }

        private void SetViewRectVisible(bool visible)
        {
            for (int i = 0; i < 4; i++)
            {
                Image edge = _viewEdges[i];
                if (edge != null)
                    edge.gameObject.SetActive(visible);
            }
        }

        /// <summary>Фолбэк луча выше горизонта (земля и плоскость моря уже проверены
        /// в TryGetGroundPoint): идём по горизонтальной проекции направления от глаза
        /// через две диагонали карты — точка гарантированно досягает границы, вызывающий
        /// код клампит её в границы карты.</summary>
        private static bool RayToMapEdge(Ray ray, Vector2 min, Vector2 max, out Vector3 world)
        {
            Vector2 dir = new Vector2(ray.direction.x, ray.direction.z);
            float dirLen = dir.magnitude;
            if (dirLen < 1e-4f)
            {
                world = Vector3.zero;
                return false;
            }
            Vector2 walk = dir / dirLen * ((max - min).magnitude * 2f);
            Vector2 p = new Vector2(ray.origin.x + walk.x, ray.origin.z + walk.y);
            world = new Vector3(p.x, Datum.LocalSeaY, p.y);
            return true;
        }

        // ---------- координаты и клики ----------

        private Vector2 WorldToLocal(Vector3 world, Vector2 min, Vector2 max)
        {
            Rect r = _mapRect.rect;
            // Карта → нормализованные 0..1 → окно обзора (зум) → локальные миникарты.
            float nx = (Mathf.InverseLerp(min.x, max.x, world.x) - _viewMin.x) / _viewSize.x;
            float ny = (Mathf.InverseLerp(min.y, max.y, world.z) - _viewMin.y) / _viewSize.y;
            return new Vector2((nx - 0.5f) * r.width, (ny - 0.5f) * r.height);
        }

        private Vector3 LocalToWorld(Vector2 local)
        {
            Rect r = _mapRect.rect;
            float nx = r.width > 0f ? local.x / r.width + 0.5f : 0.5f;
            float ny = r.height > 0f ? local.y / r.height + 0.5f : 0.5f;
            GameApi.TryGetMapBounds(out Vector2 min, out Vector2 max);
            Vector3 world = new Vector3(
                Mathf.Lerp(min.x, max.x, _viewMin.x + nx * _viewSize.x),
                0f,
                Mathf.Lerp(min.y, max.y, _viewMin.y + ny * _viewSize.y));
            world.y = GameApi.GroundY(world.x, world.z);
            return world;
        }

        /// <summary>Зум миникарты «в курсор» (как на картах): точка карты под курсором
        /// остаётся под курсором. Ввод уже проверен вызывающим (наведение, чат).</summary>
        private void ZoomAtScreen(Vector2 screenPos, float wheel)
        {
            if (_mapRaw == null || _mapRect == null)
                return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_mapRect, screenPos, null, out Vector2 local))
                return;
            Rect r = _mapRect.rect;
            if (r.width < 1f || r.height < 1f)
                return;
            // Точка под курсором в долях КАРТЫ и её доля внутри ОКНА.
            float ax = local.x / r.width + 0.5f;
            float ay = local.y / r.height + 0.5f;
            float px = _viewMin.x + ax * _viewSize.x;
            float py = _viewMin.y + ay * _viewSize.y;
            float maxZoom = Mathf.Max(1f, Settings.MinimapZoomMax);
            float size = Mathf.Clamp(_viewSize.x / Mathf.Pow(ZoomPerNotch, wheel * 10f), 1f / maxZoom, 1f);
            _viewMin = new Vector2(
                Mathf.Clamp(px - ax * size, 0f, 1f - size),
                Mathf.Clamp(py - ay * size, 0f, 1f - size));
            _viewSize = new Vector2(size, size);
            ApplyViewUv();
        }

        /// <summary>Выбор фона миникарты и его под-прямоугольник: рельеф (своя текстура
        /// на всю карту) → спрайт карты (uvRect в UV текстуры, спрайт может быть её
        /// частью) → фолбэк-заливка. Зум показывается под-прямоугольником через uvRect.</summary>
        private void ApplyViewUv()
        {
            if (_mapRaw == null)
                return;
            if (Settings.MinimapRelief && _reliefTexture != null)
            {
                if (_mapRaw.texture != _reliefTexture)
                    _mapRaw.texture = _reliefTexture;
                _mapRaw.color = Color.white;
                _mapRaw.uvRect = new Rect(_viewMin.x, _viewMin.y, _viewSize.x, _viewSize.y);
                return;
            }
            if (GameApi.TryGetMapImage(out Sprite s) && s != null && s.texture != null)
            {
                if (_mapRaw.texture != s.texture)
                    _mapRaw.texture = s.texture;
                _mapRaw.color = Color.white;
                Rect tr = s.textureRect;
                Texture tex = s.texture;
                _mapRaw.uvRect = new Rect(
                    (tr.x + _viewMin.x * tr.width) / tex.width,
                    (tr.y + _viewMin.y * tr.height) / tex.height,
                    _viewSize.x * tr.width / tex.width,
                    _viewSize.y * tr.height / tex.height);
                return;
            }
            _mapRaw.texture = Texture2D.whiteTexture;
            _mapRaw.color = new Color(0.09f, 0.12f, 0.10f, 1f);
            _mapRaw.uvRect = new Rect(0f, 0f, 1f, 1f);
        }

        // ---------- рельеф (высоты + горизонтали) ----------

        /// <summary>Сэмплирование высот порциями между обновлениями (рейкасты дорогие —
        /// размазываем по кадрам, ~3 с на карту 128×128). Лениво: только если режим
        /// включали. Новая карта (другие границы) — сэмпл заново.</summary>
        private void SampleReliefBatch(Vector2 min, Vector2 max)
        {
            if (!_reliefRequested || _reliefHeights != null && _reliefSampled >= ReliefN * ReliefN)
                return;
            if (_reliefHeights == null || (min - _reliefMin).sqrMagnitude > 1f || (max - _reliefMax).sqrMagnitude > 1f)
            {
                _reliefMin = min;
                _reliefMax = max;
                _reliefHeights = new float[ReliefN * ReliefN];
                _reliefSampled = 0;
            }
            int total = ReliefN * ReliefN;
            int done = 0;
            while (_reliefSampled < total && done < ReliefBatch)
            {
                int x = _reliefSampled % ReliefN;
                int y = _reliefSampled / ReliefN;
                float wx = Mathf.Lerp(_reliefMin.x, _reliefMax.x, x / (float)(ReliefN - 1));
                float wz = Mathf.Lerp(_reliefMin.y, _reliefMax.y, y / (float)(ReliefN - 1));
                _reliefHeights[_reliefSampled] = GameApi.GroundY(wx, wz);
                _reliefSampled++;
                done++;
            }
            if (_reliefSampled >= total)
                BuildReliefTexture();
        }

        /// <summary>Текстура рельефа: море — синяя шкала глубины, суша — гипсометрическая
        /// зелёно-жёлто-буро-белая; горизонтали — тёмная линия на смене высотного пояса
        /// относительно соседа справа/сверху, шаг «красивый» (1/2/5×10^k) от диапазона.
        /// Строки текстуры снизу вверх = юг→север (z растёт вверх миникарты).</summary>
        private void BuildReliefTexture()
        {
            int n = ReliefN;
            float sea = Datum.LocalSeaY;
            float hmax = sea;
            for (int i = 0; i < _reliefHeights.Length; i++)
                if (_reliefHeights[i] > hmax)
                    hmax = _reliefHeights[i];
            float range = Mathf.Max(hmax - sea, 1f);
            float rawStep = range / 10f;
            float pow = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(Mathf.Max(rawStep, 0.1f))));
            float mul = rawStep / pow;
            float step = (mul <= 1f ? 1f : mul <= 2f ? 2f : mul <= 5f ? 5f : 10f) * pow;

            var px = new Color32[n * n];
            var lowLand = new Color(0.32f, 0.50f, 0.24f);
            var midLand = new Color(0.66f, 0.62f, 0.31f);
            var highLand = new Color(0.60f, 0.46f, 0.30f);
            var peak = new Color(0.94f, 0.94f, 0.94f);
            var shallowSea = new Color(0.30f, 0.48f, 0.64f);
            var deepSea = new Color(0.05f, 0.14f, 0.30f);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float h = _reliefHeights[y * n + x];
                    Color c;
                    if (h < sea)
                    {
                        c = Color.Lerp(shallowSea, deepSea, Mathf.Clamp01((sea - h) / 60f));
                    }
                    else
                    {
                        float t = Mathf.Clamp01((h - sea) / range);
                        if (t < 0.34f)
                            c = Color.Lerp(lowLand, midLand, t / 0.34f);
                        else if (t < 0.67f)
                            c = Color.Lerp(midLand, highLand, (t - 0.34f) / 0.33f);
                        else
                            c = Color.Lerp(highLand, peak, (t - 0.67f) / 0.33f);
                    }
                    int band = Mathf.FloorToInt((h - sea) / step);
                    bool contour = x + 1 < n && Mathf.FloorToInt((_reliefHeights[y * n + x + 1] - sea) / step) != band;
                    if (!contour && y + 1 < n && Mathf.FloorToInt((_reliefHeights[(y + 1) * n + x] - sea) / step) != band)
                        contour = true;
                    if (contour)
                        c = new Color(c.r * 0.45f, c.g * 0.45f, c.b * 0.45f, 1f);
                    px[y * n + x] = c;
                }
            }
            if (_reliefTexture != null)
                Destroy(_reliefTexture);
            _reliefTexture = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _reliefTexture.SetPixels32(px);
            _reliefTexture.Apply(false, true);
        }

        private void OnReliefToggle()
        {
            Settings.MinimapRelief = !Settings.MinimapRelief;
            if (Settings.MinimapRelief)
                _reliefRequested = true;
            Settings.SaveConfig();
            UpdateReliefLabel();
            ApplyViewUv();
        }

        /// <summary>Чипы колец дальности: клик — вкл/выкл класс (0 атака-АС / 1 атака-АА /
        /// 2 разведка / 3 стройка), фон чипа в цвете колец (легенда), выключенный — приглушён.</summary>
        private void OnRangeToggle(int kind)
        {
            bool on = kind == 0 ? !Settings.RangeRingAttack
                : kind == 1 ? !Settings.RangeRingAir
                : kind == 2 ? !Settings.RangeRingVision
                : !Settings.RangeRingBuild;
            Settings.SetRangeRing(kind, on);
            Settings.SaveConfig();
            UpdateRangeChips();
        }

        private void UpdateRangeChips()
        {
            bool[] states = { Settings.RangeRingAttack, Settings.RangeRingAir, Settings.RangeRingVision, Settings.RangeRingBuild };
            for (int i = 0; i < states.Length; i++)
            {
                if (_rangeChipImg[i] == null || _rangeChipLbl[i] == null) // Unity fake-null
                    continue;
                Color c = RangeRings.Colors[i];
                // В режиме «Оружие» доменные кольца атаки не рисуются — чипы призрачно
                // приглушаем (вкл/выкл они по-прежнему работают на выход из режима).
                bool ghost = Settings.RangeRingWeapons && i < 2;
                _rangeChipImg[i].color = ghost
                    ? new Color(0.16f, 0.17f, 0.18f, 0.45f)
                    : states[i]
                        ? new Color(c.r, c.g, c.b, 0.55f)
                        : new Color(0.16f, 0.17f, 0.18f, 0.9f);
                _rangeChipLbl[i].color = ghost || !states[i] ? UiKit.TextDim : Color.white;
            }
            if (_weaponsChipImg != null && _weaponsChipLbl != null) // Unity fake-null
            {
                _weaponsChipImg.color = Settings.RangeRingWeapons
                    ? new Color(0.90f, 0.75f, 0.20f, 0.55f)
                    : new Color(0.16f, 0.17f, 0.18f, 0.9f);
                _weaponsChipLbl.color = Settings.RangeRingWeapons ? Color.white : UiKit.TextDim;
            }
        }

        private void UpdateReliefLabel()
        {
            if (_reliefLabel == null)
                return;
            if (!Settings.MinimapRelief)
                _reliefLabel.text = Loc.Tr("Рельеф: выкл");
            else if (_reliefTexture == null)
                _reliefLabel.text = Loc.F("Рельеф: карта {0}%", _reliefSampled * 100 / (ReliefN * ReliefN));
            else
                _reliefLabel.text = Loc.Tr("Рельеф: вкл");
        }

        private void OnMapClick(bool rightButton, Vector2 local)
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;
            Vector3 world = LocalToWorld(local);
            if (rightButton)
            {
                // Ctrl+Shift+ПКМ — только удаление вейпоинтов (мировой экран);
                // миникарта вейпоинты не сканирует — клик гасим, приказов не выдаём.
                if (RtsInput.Ctrl && RtsInput.Shift)
                    return;
                controller.MoveOrders.IssueAtWorld(world);
            }
            else
            {
                controller.CameraState?.CenterOn(world);
            }
        }

        private sealed class MapClicks : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
        {
            public MinimapPanel Owner;
            private Vector2 _downPos;
            private const float ClickPx = 6f; // до этого сдвига — клик, больше — перетаскивание окна

            public void OnPointerDown(PointerEventData e)
            {
                _downPos = e.position;
                // Тянем окно прямо за карту; если отпустили без движения — это клик.
                Owner?._window?.BeginManualDrag(e.position);
            }

            public void OnPointerUp(PointerEventData e)
            {
                Owner?._window?.EndManualDrag();
                if (Owner == null || (e.position - _downPos).sqrMagnitude > ClickPx * ClickPx)
                    return;
                RectTransform rt = (RectTransform)transform;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        rt, e.position, e.pressEventCamera, out Vector2 local))
                    return;
                Owner.OnMapClick(e.button == PointerEventData.InputButton.Right, local);
            }
        }
    }
}
