using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SupremeNuclear
{
    /// <summary>Строительные блоки uGUI в стиле SupcomUi: плавающее окно (drag/resize/закрытие
    /// + вкладка восстановления), скролл-список, прогресс-бар. Общие цвета — как в SupcomUi.</summary>
    internal static class UiKit
    {
        public static readonly Color PanelBg = new Color(0.07f, 0.09f, 0.11f, 0.95f);
        public static readonly Color PanelBg2 = new Color(0.10f, 0.13f, 0.16f, 0.95f);
        public static readonly Color Accent = new Color(0.35f, 1f, 0.5f, 1f);
        public static readonly Color BtnBg = new Color(0.16f, 0.20f, 0.24f, 1f);
        public static readonly Color TextMain = new Color(0.92f, 0.95f, 0.92f, 1f);
        public static readonly Color TextDim = new Color(0.62f, 0.66f, 0.62f, 1f);

        /// <summary>Полоса-заливка; возвращается rect заливки — её anchorMax.x задаёт уровень.</summary>
        public static RectTransform ProgressBar(Transform parent, string name, Color track, Color fill,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)root.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            root.GetComponent<Image>().color = track;

            var fillGo = new GameObject("fill", typeof(RectTransform), typeof(Image));
            var frt = (RectTransform)fillGo.transform;
            frt.SetParent(rt, false);
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = Vector2.one;
            frt.offsetMin = frt.offsetMax = Vector2.zero;
            var fimg = fillGo.GetComponent<Image>();
            fimg.color = fill;
            fimg.raycastTarget = false;
            SetProgress(frt, 1f);
            return frt;
        }

        public static void SetProgress(RectTransform fill, float t)
        {
            if (fill == null)
                return;
            t = Mathf.Clamp01(t);
            fill.anchorMax = new Vector2(t, 1f);
        }

        /// <summary>Горизонтальная лента-строка (SupCom): viewport с маской + контент
        /// с HorizontalLayoutGroup. Прокрутка — смещением content (кнопками листания).</summary>
        public static RectTransform HStrip(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax,
            out RectTransform content)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rrt = (RectTransform)root.transform;
            rrt.SetParent(parent, false);
            rrt.anchorMin = anchorMin;
            rrt.anchorMax = anchorMax;
            rrt.offsetMin = offsetMin;
            rrt.offsetMax = offsetMax;
            root.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            root.AddComponent<RectMask2D>();

            var contentGo = new GameObject("content", typeof(RectTransform));
            content = (RectTransform)contentGo.transform;
            content.SetParent(rrt, false);
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 0.5f);
            content.anchoredPosition = Vector2.zero;
            var layout = contentGo.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.padding = new RectOffset(4, 4, 2, 2); // вертикаль 2: плитка 48 + 2×2 = лента 52
            // Ширина контента — по детям: без фиттера rect.width остаётся 0,
            // маска/листание/раскладка ломаются.
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rrt;
        }

        /// <summary>Кнопки листания ленты (спека §2.10): влево/вправо по одной позиции
        /// и до конца — ▶▶ ◀ ▶ ◀◀. Возвращают ленту в границы.</summary>
        /// <summary>Кнопки листания ОДНОЙ строки у её края (спека DESIGN 2.10):
        /// leftSide=true → [◀◀ ◀] у левого края строки, иначе [▶ ▶▶] у правого.</summary>
        public static GameObject StripArrows(Transform panel, Font font, RectTransform content,
            float stepPx, bool leftSide, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            RectTransform viewport = (RectTransform)content.parent;

            void Shift(float dx)
            {
                float w = content.rect.width;
                float vw = viewport.rect.width;
                float maxX = Mathf.Max(0f, w - vw);
                float x = Mathf.Clamp(content.anchoredPosition.x - dx, -maxX, 0f);
                content.anchoredPosition = new Vector2(x, content.anchoredPosition.y);
            }

            var nav = new GameObject(leftSide ? "navL" : "navR", typeof(RectTransform));
            var nrt = (RectTransform)nav.transform;
            nrt.SetParent(panel, false);
            nrt.anchorMin = anchorMin;
            nrt.anchorMax = anchorMax;
            nrt.offsetMin = offsetMin;
            nrt.offsetMax = offsetMax;
            var h = nav.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 4f;
            h.childControlWidth = false;
            h.childControlHeight = false;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            h.childAlignment = TextAnchor.MiddleCenter;

            (string glyph, float dx)[] navs = leftSide
                ? new[] { ("◀◀", -100000f), ("◀", -stepPx) }
                : new[] { ("▶", stepPx), ("▶▶", 100000f) };
            foreach (var (glyph, dx) in navs)
            {
                var go = new GameObject("nav" + glyph, typeof(RectTransform), typeof(Image), typeof(Button));
                var rt = (RectTransform)go.transform;
                rt.SetParent(nrt, false);
                rt.sizeDelta = new Vector2(26f, 40f);
                go.GetComponent<Image>().color = BtnBg;
                var btn = go.GetComponent<Button>();
                float d = dx;
                btn.onClick.AddListener(() => Shift(d));
                var lbl = new GameObject("g", typeof(RectTransform));
                var lrt = (RectTransform)lbl.transform;
                lrt.SetParent(rt, false);
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.offsetMin = lrt.offsetMax = Vector2.zero;
                var t = lbl.AddComponent<Text>();
                if (font != null) t.font = font;
                t.text = glyph;
                t.fontSize = 12;
                t.color = TextMain;
                t.alignment = TextAnchor.MiddleCenter;
                t.raycastTarget = false;
            }
            return nav;
        }

        /// <summary>Скролл-список: viewport + ScrollRect + контент (VerticalLayout + ContentSizeFitter)
        /// + правый скроллбар. Строки добавляет владелец в возвращённый content.</summary>
        public static RectTransform ScrollList(Transform parent, string name,
            out RectTransform content, out ScrollRect scroll)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rrt = (RectTransform)root.transform;
            rrt.SetParent(parent, false);
            rrt.anchorMin = Vector2.zero;
            rrt.anchorMax = Vector2.one;
            rrt.offsetMin = rrt.offsetMax = Vector2.zero;
            root.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            root.AddComponent<RectMask2D>();

            var viewport = new GameObject("viewport", typeof(RectTransform), typeof(Image));
            var vrt = (RectTransform)viewport.transform;
            vrt.SetParent(rrt, false);
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.pivot = new Vector2(0f, 1f);
            vrt.offsetMin = new Vector2(0f, 0f);
            vrt.offsetMax = new Vector2(-18f, 0f);
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            viewport.GetComponent<Image>().raycastTarget = true; // ловит колесо над списком

            var contentGo = new GameObject("content", typeof(RectTransform));
            content = (RectTransform)contentGo.transform;
            content.SetParent(vrt, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0f, 0f);
            var layout = contentGo.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(4, 4, 4, 4);
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var barGo = new GameObject("scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            var brt = (RectTransform)barGo.transform;
            brt.SetParent(rrt, false);
            brt.anchorMin = new Vector2(1f, 0f);
            brt.anchorMax = new Vector2(1f, 1f);
            brt.pivot = new Vector2(1f, 0.5f);
            brt.sizeDelta = new Vector2(14f, 0f);
            brt.anchoredPosition = new Vector2(0f, 0f);
            barGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            var sliding = new GameObject("sliding", typeof(RectTransform));
            var srt = (RectTransform)sliding.transform;
            srt.SetParent(brt, false);
            srt.anchorMin = Vector2.zero;
            srt.anchorMax = Vector2.one;
            srt.offsetMin = srt.offsetMax = new Vector2(2f, 2f);

            var handle = new GameObject("handle", typeof(RectTransform), typeof(Image));
            var hrt = (RectTransform)handle.transform;
            hrt.SetParent(srt, false);
            hrt.anchorMin = Vector2.zero;
            hrt.anchorMax = Vector2.one;
            hrt.offsetMin = hrt.offsetMax = Vector2.zero;
            handle.GetComponent<Image>().color = BtnBg;

            var bar = barGo.GetComponent<Scrollbar>();
            bar.targetGraphic = handle.GetComponent<Image>();
            bar.handleRect = hrt;
            bar.direction = Scrollbar.Direction.BottomToTop;

            scroll = root.AddComponent<ScrollRect>();
            scroll.viewport = vrt;
            scroll.content = content;
            scroll.vertical = true;
            scroll.horizontal = false;
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            scroll.scrollSensitivity = 30f;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            return rrt;
        }
    }

    /// <summary>Плавающее окно в духе SupCom: заголовок — перетащить, угол — ресайз,
    /// «×» — закрыть; закрытое окно возвращается вкладкой у левого края экрана.
    /// Позиция/размер/видимость отдаются владельцу через saveState для сохранения в конфиг.</summary>
    internal sealed class FloatingWindow : MonoBehaviour
    {
        public RectTransform Root { get; private set; }
        public RectTransform Content { get; private set; }
        public bool IsVisible => Root != null && Root.gameObject.activeSelf;

        private Action<Vector2, Vector2, bool> _saveState;
        private Button _restoreTab;
        private Font _font;
        /// <summary>Канвас-хозяин окна (перевод мыши в координаты канваса).</summary>
        internal Canvas HostCanvas;

        // РУЧНОЙ драг/ресайз: uGUI-события драга (IBeginDragHandler) НЕ ДОСТАВЛЯЮТСЯ
        // игровым input-модулем (клики работают, OnDrag — нет). Поэтому тащим сами:
        // IPointerDown/Up захватывают, Update переносит. Работает на любом модуле.
        private bool _dragging;
        private Vector2 _dragOffset;
        private bool _resizing;
        private Vector2 _resizeStartMouse;
        private Vector2 _resizeStartSize;
        private float _resizeTop; // Y верхнего края на старте: при ресайзе верх/лево неподвижны

        public void BeginManualDrag(Vector2 screenPos)
        {
            if (Root == null)
                return;
            var parent = (RectTransform)Root.parent;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenPos, null, out Vector2 local))
            {
                _dragOffset = local - Root.anchoredPosition;
                _dragging = true;
            }
        }

        public void EndManualDrag()
        {
            if (_dragging)
            {
                _dragging = false;
                Save();
            }
        }

        public void BeginManualResize(Vector2 screenPos)
        {
            if (Root == null)
                return;
            _resizing = true;
            _resizeStartMouse = screenPos;
            _resizeStartSize = Root.sizeDelta;
            _resizeTop = Root.anchoredPosition.y + Root.sizeDelta.y;
        }

        public void EndManualResize()
        {
            if (_resizing)
            {
                _resizing = false;
                Save();
            }
        }

        private void Update()
        {
            if (Root == null)
                return;
            if (_dragging)
            {
                var parent = (RectTransform)Root.parent;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        parent, Input.mousePosition, null, out Vector2 local))
                {
                    Root.anchoredPosition = local - _dragOffset;
                    ClampToParent(); // мягкий кламп: окно не уезжает за экран в процессе драга
                }
            }
            else if (_resizing)
            {
                var parent = (RectTransform)Root.parent;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        parent, Input.mousePosition, null, out Vector2 local) &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        parent, _resizeStartMouse, null, out Vector2 start))
                {
                    // Ручка — НИЖНИЙ-правый угол: правый и нижний края идут за мышью,
                    // левый и верхний неподвижны. Пивот окна — левый-нижний, поэтому
                    // рост вниз компенсируем позицией (верхний край держим на месте) —
                    // иначе вертикаль инвертирована: тянешь ручку вниз, окно сжимается.
                    Vector2 size = new Vector2(
                        Mathf.Clamp(_resizeStartSize.x + (local.x - start.x), 240f, 1200f),
                        Mathf.Clamp(_resizeStartSize.y - (local.y - start.y), 240f, 1200f));
                    Root.sizeDelta = size;
                    Root.anchoredPosition = new Vector2(Root.anchoredPosition.x, _resizeTop - size.y);
                    ClampToParent(); // после ресайза окно обязано остаться на канвасе
                }
            }
        }

        /// <summary>Держим окно ЦЕЛИКОМ в пределах родительского канваса (поле 8 px);
        /// если окно крупнее канваса — видимыми остаются ≥80 px по X и верхние ≥40 px по Y
        /// (заголовок-драг и уголок-ресайз достижимы мышью). Канвас мог сжаться
        /// CanvasScaler'ом ПОСЛЕ постройки окна (Build в кадре создания канваса видит
        /// физические пиксели) — вызывается и из драга/ресайза, и периодически владельцем
        /// окна как самовосстановление.</summary>
        internal void ClampToParent()
        {
            if (Root == null)
                return;
            var parent = (RectTransform)Root.parent;
            if (parent == null)
                return;
            Rect r = parent.rect;
            if (r.width < 10f || r.height < 10f)
                return;
            float w = Root.rect.width;
            float h = Root.rect.height;
            float maxX = Mathf.Max(8f, w <= r.width - 48f ? r.width - w - 8f : r.width - 80f);
            float maxY = Mathf.Max(8f, h <= r.height - 48f ? r.height - h - 8f : r.height - 40f);
            Root.anchoredPosition = new Vector2(
                Mathf.Clamp(Root.anchoredPosition.x, 8f, maxX),
                Mathf.Clamp(Root.anchoredPosition.y, 8f, maxY));
        }

        public static FloatingWindow Create(Transform canvas, Font font, string title,
            Vector2 position, Vector2 size, Action<Vector2, Vector2, bool> saveState)
        {
            string step = "root";
            try
            {
                var go = new GameObject("Window_" + title, typeof(RectTransform), typeof(Image));
                var rt = (RectTransform)go.transform;
                rt.SetParent(canvas, false);
                rt.anchorMin = rt.anchorMax = Vector2.zero; // позиция от левого нижнего угла экрана
                rt.pivot = new Vector2(0f, 0f);
                rt.anchoredPosition = position;
                rt.sizeDelta = size;
                go.GetComponent<Image>().color = UiKit.PanelBg;

                step = "component";
                var win = go.AddComponent<FloatingWindow>();
                win._saveState = saveState;
                win._font = font;
                win.Root = rt;
            Canvas host = canvas != null ? canvas.GetComponent<Canvas>() : null;
            win.HostCanvas = host;

                step = "title";
                var titleGo = new GameObject("title", typeof(RectTransform), typeof(Image));
                var trt = (RectTransform)titleGo.transform;
                trt.SetParent(rt, false);
                trt.anchorMin = new Vector2(0f, 1f);
                trt.anchorMax = new Vector2(1f, 1f);
                trt.pivot = new Vector2(0.5f, 1f);
                trt.sizeDelta = new Vector2(0f, 26f);
                titleGo.GetComponent<Image>().color = new Color(0.12f, 0.16f, 0.20f, 1f);
                var drag = titleGo.AddComponent<TitleDrag>();
                drag.Window = win;

                var lbl = new GameObject("lbl", typeof(RectTransform));
                var lrt = (RectTransform)lbl.transform;
                lrt.SetParent(trt, false);
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = new Vector2(1f, 1f);
                lrt.offsetMin = new Vector2(10f, 0f);
                lrt.offsetMax = new Vector2(-30f, 0f);
                var txt = lbl.AddComponent<Text>();
                if (font != null) txt.font = font;
                txt.text = title;
                txt.fontSize = 14;
                txt.color = UiKit.TextDim;
                txt.alignment = TextAnchor.MiddleLeft;
                txt.raycastTarget = false;

                step = "close";
                var closeGo = new GameObject("close", typeof(RectTransform), typeof(Image), typeof(Button));
                var crt = (RectTransform)closeGo.transform;
                crt.SetParent(trt, false);
                crt.anchorMin = crt.anchorMax = new Vector2(1f, 0.5f);
                crt.pivot = new Vector2(1f, 0.5f);
                crt.anchoredPosition = new Vector2(-3f, 0f);
                crt.sizeDelta = new Vector2(22f, 20f);
                closeGo.GetComponent<Image>().color = new Color(0.45f, 0.16f, 0.14f, 1f);
                var closeBtn = closeGo.GetComponent<Button>();
                closeBtn.onClick.AddListener(win.Close);
                var closeTxt = new GameObject("x", typeof(RectTransform));
                var xrt = (RectTransform)closeTxt.transform;
                xrt.SetParent(crt, false);
                xrt.anchorMin = Vector2.zero;
                xrt.anchorMax = Vector2.one;
                xrt.offsetMin = xrt.offsetMax = Vector2.zero;
                var x = closeTxt.AddComponent<Text>();
                if (font != null) x.font = font;
                x.text = "×";
                x.fontSize = 16;
                x.color = UiKit.TextMain;
                x.alignment = TextAnchor.MiddleCenter;
                x.raycastTarget = false;

                step = "content";
                var contentGo = new GameObject("content", typeof(RectTransform), typeof(Image));
                var crt2 = (RectTransform)contentGo.transform;
                crt2.SetParent(rt, false);
                crt2.anchorMin = Vector2.zero;
                crt2.anchorMax = new Vector2(1f, 1f);
                crt2.offsetMin = new Vector2(3f, 3f);
                crt2.offsetMax = new Vector2(-3f, -29f);
                contentGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
                win.Content = crt2;

                step = "resize";
                var handleGo = new GameObject("resize", typeof(RectTransform), typeof(Image));
                var hrt = (RectTransform)handleGo.transform;
                hrt.SetParent(rt, false);
                hrt.anchorMin = hrt.anchorMax = new Vector2(1f, 0f);
                hrt.pivot = new Vector2(1f, 0f);
                hrt.sizeDelta = new Vector2(18f, 18f);
                handleGo.GetComponent<Image>().color = new Color(0.2f, 0.25f, 0.3f, 0.9f);
                var resize = handleGo.AddComponent<ResizeHandle>();
                resize.Window = win;

                step = "restore-tab";
                win.BuildRestoreTab(canvas, title);
                // Вкладка по умолчанию скрыта (окно открыто); Update на неактивном окне не тикает,
                // поэтому видимостью вкладки управляют Close/Show, а не Update.
                if (win._restoreTab != null)
                    win._restoreTab.gameObject.SetActive(false);
                return win;
            }
            catch (System.Exception e)
            {
                // Шаг называет место сбоя без IL-гаданий (Mono стек не всегда точен).
                Log.Error("FloatingWindow.Create: сбой на шаге '" + step + "' — " +
                          e.GetType().Name + ": " + e.Message + " | " + e.StackTrace);
                throw;
            }
        }

        private void BuildRestoreTab(Transform canvas, string title)
        {
            var tabGo = new GameObject("Tab_" + title, typeof(RectTransform), typeof(Image), typeof(Button));
            var trt = (RectTransform)tabGo.transform;
            trt.SetParent(canvas, false);
            trt.anchorMin = trt.anchorMax = new Vector2(0f, 0.5f);
            trt.pivot = new Vector2(0f, 0.5f);
            trt.anchoredPosition = new Vector2(2f, 120f);
            trt.sizeDelta = new Vector2(74f, 24f);
            tabGo.GetComponent<Image>().color = UiKit.BtnBg;
            _restoreTab = tabGo.GetComponent<Button>();
            _restoreTab.onClick.AddListener(Show);

            var lbl = new GameObject("lbl", typeof(RectTransform));
            var lrt = (RectTransform)lbl.transform;
            lrt.SetParent(trt, false);
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = lrt.offsetMax = Vector2.zero;
            var txt = lbl.AddComponent<Text>();
            if (_font != null) txt.font = _font;
            txt.text = "+ " + title;
            txt.fontSize = 13;
            txt.color = UiKit.TextDim;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;
        }

        public void Close()
        {
            if (Root == null)
                return;
            Root.gameObject.SetActive(false);
            if (_restoreTab != null)
                _restoreTab.gameObject.SetActive(true);
            _saveState?.Invoke(Root.anchoredPosition, Root.sizeDelta, false);
        }

        public void Show()
        {
            if (Root == null)
                return;
            Root.gameObject.SetActive(true);
            if (_restoreTab != null)
                _restoreTab.gameObject.SetActive(false);
            _saveState?.Invoke(Root.anchoredPosition, Root.sizeDelta, true);
        }

        /// <summary>Синхронно задать видимость окна и вкладки восстановления (без записи конфига) —
        /// для восстановления сохранённого состояния при построении.</summary>
        public void SetVisible(bool visible)
        {
            if (Root == null)
                return;
            Root.gameObject.SetActive(visible);
            if (_restoreTab != null)
                _restoreTab.gameObject.SetActive(!visible);
        }

        /// <summary>Снести окно и вкладку целиком (сборка не удалась / владелец умирает).</summary>
        public void DestroyAll()
        {
            if (_restoreTab != null)
                Destroy(_restoreTab.gameObject);
            if (Root != null)
                Destroy(Root.gameObject);
        }

        internal void Save() =>
            _saveState?.Invoke(Root.anchoredPosition, Root.sizeDelta, IsVisible);

        /// <summary>Захват заголовка: pointer-down начинает ручной драг окна
        /// (uGUI-драг не доставляется игровым input-модулем — см. FloatingWindow).</summary>
        private sealed class TitleDrag : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
        {
            public FloatingWindow Window;

            public void OnPointerDown(PointerEventData e) => Window?.BeginManualDrag(e.position);

            public void OnPointerUp(PointerEventData e) => Window?.EndManualDrag();
        }

        /// <summary>Захват угла: pointer-down начинает ручной ресайз окна.</summary>
        private sealed class ResizeHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
        {
            public FloatingWindow Window;

            public void OnPointerDown(PointerEventData e) => Window?.BeginManualResize(e.position);

            public void OnPointerUp(PointerEventData e) => Window?.EndManualResize();
        }
    }
}
