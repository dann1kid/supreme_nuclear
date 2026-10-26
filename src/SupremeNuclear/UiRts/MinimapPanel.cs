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
        private RectTransform _mapRect;      // фон карты (Image со спрайтом или заливка)
        private RectTransform _iconsRoot;
        private readonly Image[] _viewEdges = new Image[4];
        private readonly Dictionary<Unit, Image> _blips = new Dictionary<Unit, Image>();
        private readonly List<Unit> _staleBlips = new List<Unit>();
        private readonly Dictionary<Airbase, Image> _airbaseBlips = new Dictionary<Airbase, Image>();
        private readonly List<Airbase> _staleAirbases = new List<Airbase>();
        private float _nextRefresh;
        private float _nextErrorLog = -10f;
        private float _nextBuildAttempt;
        private Canvas _builtOnCanvas; // канвас, на котором построено окно (смена = пересборка)

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
                return;
            Canvas canvas = SupcomUi.Instance != null ? SupcomUi.Instance.Canvas : null;
            if (canvas == null)
                return;
            if (_window == null || _window.Root == null || canvas != _builtOnCanvas)
            {
                // Новый канвас (EnsureUi пересобрал) — строимся не раньше 0.5 с: в кадре
                // создания RectTransform канваса ещё в физических пикселях (3840x2160),
                // CanvasScaler применится кадром позже — иначе дефолтная позиция уедет за экран.
                if (Time.unscaledTime < _nextBuildAttempt)
                    return;
                _nextBuildAttempt = Time.unscaledTime + 5f;
                if (_window != null && _window.Root != null)
                    _window.DestroyAll(); // окно жило на умершем канвасе — сносим вкладку-остаток
                Build(canvas);
                if (_window == null || _window.Root == null)
                    return;
                _builtOnCanvas = canvas;
                _nextBuildAttempt = Time.unscaledTime + 0.5f; // канвас снова пересобрали? — пауза
            }

            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + 0.1f;
            if (!GameApi.TryGetMapBounds(out Vector2 min, out Vector2 max))
                return;
            KeepWindowOnScreen(canvas);
            UpdateBlips(min, max);
            UpdateAirbases(min, max);
            UpdateViewRect(min, max);
        }

        /// <summary>Окно обязано быть достижимо мышью: канвас мог сжаться CanvasScaler'ом
        /// ПОСЛЕ постройки окна (Build в кадре создания канваса видит физический размер
        /// 3840x2160, потом reference 1920x1080) — возвращаем окно в пределы экрана.</summary>
        private void KeepWindowOnScreen(Canvas canvas)
        {
            if (_window == null || _window.Root == null)
                return;
            Rect r = canvas.GetComponent<RectTransform>().rect;
            if (r.width < 10f || r.height < 10f)
                return;
            Vector2 p = _window.Root.anchoredPosition;
            Vector2 clamped = new Vector2(
                Mathf.Clamp(p.x, 0f, Mathf.Max(0f, r.width - 80f)),
                Mathf.Clamp(p.y, 0f, Mathf.Max(0f, r.height - 80f)));
            if ((clamped - p).sqrMagnitude > 1f)
                _window.Root.anchoredPosition = clamped;
        }

        // ---------- построение ----------

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
                // Сохранённая позиция могла уехать за экран (битые сессии/драг с неверным
                // масштабом) — клампим внутрь, окно обязано быть достижимо мышью.
                pos.x = Mathf.Clamp(pos.x, 0f, Mathf.Max(0f, canvasRect.width - 80f));
                pos.y = Mathf.Clamp(pos.y, 0f, Mathf.Max(0f, canvasRect.height - 80f));
            }
            else
            {
                pos = new Vector2(Mathf.Max(12f, canvasRect.width - size - 12f),
                    Mathf.Max(12f, canvasRect.height - size - 12f));
            }

            _window = FloatingWindow.Create(canvas.transform, font, "Карта", pos, new Vector2(size, size), SaveState);
            // Видимость из конфига: закрытое окно показывает только вкладку восстановления.
            _window.SetVisible(Settings.MinimapVisible);

            var mapGo = new GameObject("map", typeof(RectTransform), typeof(Image));
            _mapRect = (RectTransform)mapGo.transform;
            _mapRect.SetParent(_window.Content, false);
            _mapRect.anchorMin = Vector2.zero;
            _mapRect.anchorMax = Vector2.one;
            _mapRect.offsetMin = _mapRect.offsetMax = Vector2.zero;
            var mapImg = mapGo.GetComponent<Image>();
            mapImg.raycastTarget = true;
            if (GameApi.TryGetMapImage(out Sprite sprite))
            {
                mapImg.sprite = sprite;
                mapImg.color = Color.white;
            }
            else
            {
                mapImg.sprite = null;
                mapImg.color = new Color(0.09f, 0.12f, 0.10f, 1f);
            }
            var clicks = mapGo.AddComponent<MapClicks>();
            clicks.Owner = this;

            var iconsGo = new GameObject("icons", typeof(RectTransform));
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
                    color = live
                        ? Hostile
                        : new Color(0.75f, 0.75f, 0.75f, Mathf.Lerp(0.7f, 0.25f, age / Settings.StaleContactSec));
                    sizePx = unit is Ship ? 9f : 7f;
                }

                if (used.Count >= 600)
                    break; // предохранитель от тысяч точек на тестовых картах
                PlaceBlip(unit, used, unit.transform.position, min, max, color, sizePx, diamond);
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
            Color color, float sizePx, bool diamond)
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
            var rt = (RectTransform)img.transform;
            rt.sizeDelta = new Vector2(sizePx, sizePx);
            rt.localPosition = new Vector3(local.x, local.y, 0f);
            rt.localRotation = diamond ? Quaternion.Euler(0f, 0f, 45f) : Quaternion.identity;
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
                if (!_airbaseBlips.TryGetValue(airbase, out Image img) || img == null)
                {
                    var go = new GameObject("airbase", typeof(RectTransform), typeof(Image));
                    img = go.GetComponent<Image>();
                    img.raycastTarget = false;
                    ((RectTransform)go.transform).SetParent(_iconsRoot, false);
                    _airbaseBlips[airbase] = img;
                }
                img.color = c;
                var rt = (RectTransform)img.transform;
                rt.sizeDelta = new Vector2(10f, 10f);
                Vector2 local = WorldToLocal(pos, min, max);
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
            Vector2[] pts = new Vector2[4];
            Vector3[] corners =
            {
                new Vector3(0f, 0f), new Vector3(cam.pixelWidth, 0f),
                new Vector3(cam.pixelWidth, cam.pixelHeight), new Vector3(0f, cam.pixelHeight)
            };
            for (int i = 0; i < 4; i++)
            {
                Ray ray = cam.ScreenPointToRay(corners[i]);
                if (!GameApi.TryGetGroundPoint(ray, out Vector3 hit))
                    return; // угол не на карту — рамку не рисуем
                pts[i] = WorldToLocal(hit, min, max);
            }
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[(i + 1) % 4];
                Vector2 d = b - a;
                float len = d.magnitude;
                if (len < 1f)
                    return;
                Image edge = _viewEdges[i];
                if (edge == null)
                    return;
                var rt = (RectTransform)edge.transform;
                rt.localPosition = new Vector3((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, 0f);
                rt.sizeDelta = new Vector2(len, 1.5f);
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            }
        }

        // ---------- координаты и клики ----------

        private Vector2 WorldToLocal(Vector3 world, Vector2 min, Vector2 max)
        {
            Rect r = _mapRect.rect;
            float nx = Mathf.InverseLerp(min.x, max.x, world.x);
            float ny = Mathf.InverseLerp(min.y, max.y, world.z);
            return new Vector2((nx - 0.5f) * r.width, (ny - 0.5f) * r.height);
        }

        private Vector3 LocalToWorld(Vector2 local)
        {
            Rect r = _mapRect.rect;
            float nx = r.width > 0f ? local.x / r.width + 0.5f : 0.5f;
            float ny = r.height > 0f ? local.y / r.height + 0.5f : 0.5f;
            GameApi.TryGetMapBounds(out Vector2 min, out Vector2 max);
            Vector3 world = new Vector3(Mathf.Lerp(min.x, max.x, nx), 0f, Mathf.Lerp(min.y, max.y, ny));
            world.y = GameApi.GroundY(world.x, world.z);
            return world;
        }

        private void OnMapClick(bool rightButton, Vector2 local)
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;
            Vector3 world = LocalToWorld(local);
            if (rightButton)
            {
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
