using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    internal static class GameApi
    {
        private static MapSettings _mapSettings;

        public static bool InMission =>
            GameManager.gameState == GameState.SinglePlayer ||
            GameManager.gameState == GameState.Multiplayer;

        public static CameraStateManager CamManager => SceneSingleton<CameraStateManager>.i;

        /// <summary>Сброс кэшей при смене сцены.</summary>
        public static void ResetSceneCache()
        {
            _mapSettings = null;
            _hpCache.Clear();
            _airbaseBuildings.Clear();
            _ringBeltCache.Clear();
            _nextAirbaseScan = 0f;
        }

        /// <summary>Заготовленное изображение карты (MapSettings.MapImage); null на картах без него.</summary>
        public static bool TryGetMapImage(out Sprite sprite)
        {
            if (_mapSettings == null)
                _mapSettings = Object.FindObjectOfType<MapSettings>();
            sprite = _mapSettings != null ? _mapSettings.MapImage : null;
            return sprite != null;
        }

        /// <summary>Границы карты (min/max по XZ), центр — глобальный ноль (как в GC).</summary>
        public static bool TryGetMapBounds(out Vector2 min, out Vector2 max)
        {
            if (_mapSettings == null)
                _mapSettings = Object.FindObjectOfType<MapSettings>();
            if (_mapSettings == null)
            {
                min = new Vector2(-60000f, -60000f);
                max = new Vector2(60000f, 60000f);
                return false;
            }
            // Префаб карты может стоять со смещением от глобального нуля —
            // центр прямоугольника берём с его трансформа.
            Vector2 s = _mapSettings.MapSize;
            Vector3 c = _mapSettings.transform.position;
            min = new Vector2(c.x - s.x * 0.5f, c.z - s.y * 0.5f);
            max = new Vector2(c.x + s.x * 0.5f, c.z + s.y * 0.5f);
            return true;
        }

        // Счётчик сдвигов floating origin: если камера «дерётся» с началом координат,
        // счётчик в секунду будет высоким при неподвижной камере.
        private static Vector3 _lastOriginPos;
        private static float _shiftCountWindow;
        private static int _shiftCount;
        public static int OriginShiftsPerSecond { get; private set; }

        public static void TickOriginShiftMonitor()
        {
            Transform origin = Datum.origin;
            if (origin != null)
            {
                if (_lastOriginPos != origin.position && _lastOriginPos != default)
                    _shiftCount++;
                _lastOriginPos = origin.position;
            }
            _shiftCountWindow += Time.unscaledDeltaTime;
            if (_shiftCountWindow >= 1f)
            {
                OriginShiftsPerSecond = _shiftCount;
                _shiftCount = 0;
                _shiftCountWindow = 0f;
            }
        }


        public static Camera MainCam => CamManager != null ? CamManager.mainCamera : null;

        public static bool TryGetLocalHQ(out FactionHQ hq) => GameManager.GetLocalHQ(out hq);

        /// <summary>Луч экранной точки; вызывать только при готовой камере.</summary>
        public static Ray ScreenRay(Vector3 screenPos)
        {
            var cam = MainCam;
            return cam != null
                ? cam.ScreenPointToRay(screenPos)
                : new Ray(Vector3.zero, Vector3.down);
        }

        public static bool TryGetGroundPoint(Ray ray, out Vector3 point)
        {
            // Дальность должна покрывать зум «из космоса» (сотни км высоты).
            const float maxDist = 500000f;
            if (Physics.Raycast(ray, out var hit, maxDist, PhysicsLayers.StaticsMask))
            {
                point = hit.point;
                return true;
            }
            if (ray.direction.y < -0.001f)
            {
                float t = (Datum.LocalSeaY - ray.origin.y) / ray.direction.y;
                if (t > 0f && t < maxDist)
                {
                    point = ray.origin + ray.direction * t;
                    return true;
                }
            }
            point = Vector3.zero;
            return false;
        }

        // Кэш высот на КАДР: отрисовка маршрутов спрашивает GroundY сотнями вызовов
        // за кадр (координаты вейпоинтов повторяются между проходами наземки/авиации
        // и событиями OnGUI), а это физлуч с 9 км — при большом числе вейпоинтов кадр
        // проседал. Рельеф внутри кадра не меняется; ключ — ячейка 0.125 м.
        private static readonly Dictionary<long, float> _groundYCache = new Dictionary<long, float>();
        private static int _groundYFrame = -1;

        public static float GroundY(float x, float z)
        {
            int frame = Time.frameCount;
            if (_groundYFrame != frame)
            {
                _groundYFrame = frame;
                _groundYCache.Clear();
            }
            long key = ((long)Mathf.RoundToInt(x * 8f) << 32) | (uint)Mathf.RoundToInt(z * 8f);
            if (_groundYCache.TryGetValue(key, out float cached))
                return cached;
            var origin = new Vector3(x, 9000f, z);
            float y = Physics.Linecast(origin, new Vector3(x, -9000f, z), out var hit,
                PhysicsLayers.StaticsMask)
                ? hit.point.y
                : Datum.LocalSeaY;
            _groundYCache[key] = y;
            return y;
        }

        private static readonly RaycastHit[] _groundNoUnitHits = new RaycastHit[32];

        /// <summary>Высота земли БЕЗ юнитов: коллайдеры зданий тоже ловят
        /// StaticsMask-луч GroundY (крыша ангара становилась «рельефом» — плита
        /// выезда ложилась на крышу). Берём верхний хит, не принадлежащий юниту.</summary>
        public static float GroundYNoUnits(float x, float z)
        {
            var ray = new Ray(new Vector3(x, 9000f, z), Vector3.down);
            int n = Physics.RaycastNonAlloc(ray, _groundNoUnitHits, 18000f,
                PhysicsLayers.StaticsMask);
            float best = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Collider c = _groundNoUnitHits[i].collider;
                if (c == null || c.GetComponentInParent<Unit>() != null)
                    continue;
                if (_groundNoUnitHits[i].point.y > best)
                    best = _groundNoUnitHits[i].point.y;
            }
            return best > float.MinValue ? best : GroundY(x, z);
        }

        public static float CameraFloorY(Vector3 pos) =>
            Mathf.Max(GroundY(pos.x, pos.z), Datum.LocalSeaY);

        // Кэш HP-долей юнитов (обновление 5 Гц): аггрегат partLookup.hitPoints считаем здесь,
        // чтобы OnGUI и панели читали готовое число без аллокаций.
        private static readonly Dictionary<Unit, float> _hpCache = new Dictionary<Unit, float>();
        private static float _nextHpRefresh;

        /// <summary>Доля HP юнита 0..1 (агрегат по деталям; 1 = не повреждён).</summary>
        public static float HpFraction(Unit unit) =>
            unit != null && _hpCache.TryGetValue(unit, out float v) ? v : 1f;

        // Кэш «пояса» юнита — смещение вверх от позиции юнита до середины высоты его
        // модели (кольцо выделения). Модель не меняется — считаем один раз; храним
        // именно СМЕЩЕНИЕ, а не мировую Y: сдвиг FloatingOrigin не должен протравливать кэш.
        private static readonly Dictionary<Unit, float> _ringBeltCache = new Dictionary<Unit, float>();
        private static readonly List<Unit> _ringBeltPrune = new List<Unit>();

        /// <summary>Смещение вверх от позиции юнита до середины высоты его модели («пояс»).</summary>
        public static float RingBeltOffset(Unit unit)
        {
            if (unit == null)
                return 0f;
            if (_ringBeltCache.TryGetValue(unit, out float dy))
                return dy;
            Bounds b = default;
            bool has = false;
            foreach (Renderer r in unit.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r.GetType().Name == "ParticleSystemRenderer")
                    continue;
                if (!has)
                {
                    b = r.bounds;
                    has = true;
                }
                else
                    b.Encapsulate(r.bounds);
            }
            dy = has ? b.center.y - unit.transform.position.y : 0f;
            _ringBeltCache[unit] = dy;
            return dy;
        }

        public static void TickHpCache()
        {
            if (Time.unscaledTime < _nextHpRefresh)
                return;
            _nextHpRefresh = Time.unscaledTime + 0.2f;
            _hpCache.Clear();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled)
                    continue;
                List<UnitPart> parts = unit.partLookup;
                if (parts == null || parts.Count == 0)
                    continue;
                float sum = 0f;
                int n = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    UnitPart p = parts[i];
                    if (p == null)
                        continue;
                    sum += p.hitPoints;
                    n++;
                }
                _hpCache[unit] = n > 0 ? Mathf.Clamp01(sum / (n * 100f)) : 1f;
            }
            // «Пояс» мёртвых юнитов из кэша убираем (Unity fake-null), иначе кэш растёт всю сессию.
            if (_ringBeltCache.Count > 0)
            {
                _ringBeltPrune.Clear();
                foreach (KeyValuePair<Unit, float> kv in _ringBeltCache)
                    if (kv.Key == null)
                        _ringBeltPrune.Add(kv.Key);
                for (int i = 0; i < _ringBeltPrune.Count; i++)
                    _ringBeltCache.Remove(_ringBeltPrune[i]);
            }
        }

        public static bool IsSelectableFriendly(Unit unit, FactionHQ localHq)
        {
            if (unit == null || localHq == null || unit.NetworkHQ != localHq)
                return false;
            // Building покрывает VehicleDepot (фабрики) и захваченные экстракторы.
            return unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building;
        }

        /// <summary>Свои без ограничения по типу (для иконок: наземка/флот/авиация).</summary>
        public static bool IsFriendlyUnit(Unit unit, FactionHQ localHq) =>
            unit != null && localHq != null && unit.NetworkHQ == localHq;

        /// <summary>Контакт разведки по юниту, если он «видим»: ракеты обеих сторон
        /// трекаются без разведки (как мигающие метки HUD), чужие юниты — только пока
        /// контакт свежее StaleContactSec (тот же гейт, что у вражеских иконок).</summary>
        public static bool TryGetContact(Unit unit, FactionHQ hq, out TrackingInfo info)
        {
            info = null;
            if (unit == null || hq == null)
                return false;
            if (unit is Missile)
                return true; // ракеты/бомбы рисуются обеим сторонам без развед-гейта
            info = hq.GetTrackingData(unit.persistentID);
            return info != null &&
                   Time.timeSinceLevelLoad - info.lastSpottedTime <= Settings.StaleContactSec;
        }

        /// <summary>Можно осмотреть ЛКМ (инфопанель): ракеты обеих сторон — всегда,
        /// чужие юниты/здания — только видимые по разведке. Свои не-Missile не
        /// инспектируются — они выделяются обычным кликом.</summary>
        public static bool IsInspectableForeign(Unit unit, FactionHQ localHq) =>
            unit != null && localHq != null &&
            (unit is Missile || (unit.NetworkHQ != null && unit.NetworkHQ != localHq)) &&
            TryGetContact(unit, localHq, out _);

        /// <summary>Якорь чужой цели там, где рисуется её иконка: живой контакт —
        /// актуальная позиция юнита, протухший — последняя известная разведкой
        /// (GetPosition сам обновляет позицию, пока контакт жив).</summary>
        public static Vector3 ContactAnchorWorld(Unit unit, TrackingInfo info) =>
            info != null
                ? info.GetPosition().ToLocalPosition() + Vector3.up * (unit.maxRadius * 0.7f)
                : UnitAnchorWorld(unit);

        /// <summary>Пригодность точки для постройки: не вода и не склон
        /// (проба высот ±20 м, как у месторождений экономики).</summary>
        public static bool IsSiteBuildable(float x, float z)
        {
            float h0 = GroundY(x, z);
            if (h0 <= Datum.LocalSeaY + 3f)
                return false;
            const float probe = 20f;
            return Mathf.Abs(GroundY(x + probe, z) - h0) <= 25f &&
                   Mathf.Abs(GroundY(x - probe, z) - h0) <= 25f &&
                   Mathf.Abs(GroundY(x, z + probe) - h0) <= 25f &&
                   Mathf.Abs(GroundY(x, z - probe) - h0) <= 25f;
        }

        /// <summary>Пик юнита ПО ТЕЛУ (экранный bbox всех мешей): клик/шифт-клик в любую
        /// часть модели выделяет её — не надо попадать в точку-центр. Выбирается объект
        /// с наименьшей площадью на экране (ближний из перекрывающихся).</summary>
        public static Unit TryPickUnitByBody(Camera cam, Vector2 mouse, FactionHQ localHq)
        {
            if (cam == null)
                return null;
            Unit best = null;
            float bestArea = float.MaxValue;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || !IsSelectableFriendly(unit, localHq))
                    continue;
                Bounds bounds = default;
                bool has = false;
                foreach (Renderer r in unit.GetComponentsInChildren<Renderer>())
                {
                    if (r == null || !r.enabled || r.GetType().Name == "ParticleSystemRenderer")
                        continue;
                    if (!has)
                    {
                        bounds = r.bounds;
                        has = true;
                    }
                    else
                        bounds.Encapsulate(r.bounds);
                }
                if (!has)
                    continue;
                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                Vector3 e = bounds.extents;
                bool front = false;
                for (int ci = 0; ci < 8; ci++)
                {
                    Vector3 corner = bounds.center + new Vector3(
                        (ci & 1) == 0 ? -e.x : e.x,
                        (ci & 2) == 0 ? -e.y : e.y,
                        (ci & 4) == 0 ? -e.z : e.z);
                    Vector3 sp = cam.WorldToScreenPoint(corner);
                    if (sp.z <= 0f)
                        continue;
                    front = true;
                    minX = Mathf.Min(minX, sp.x); maxX = Mathf.Max(maxX, sp.x);
                    minY = Mathf.Min(minY, sp.y); maxY = Mathf.Max(maxY, sp.y);
                }
                if (!front || mouse.x < minX || mouse.x > maxX || mouse.y < minY || mouse.y > maxY)
                    continue;
                float area = (maxX - minX) * (maxY - minY);
                // Гварды: центр за камерой и вырожденные гигантские bbox (юнит «за спиной»
                // проецируется мусором и ловил клики по пустой земле).
                Vector3 csp = cam.WorldToScreenPoint(bounds.center);
                if (csp.z <= 1f || area > Screen.width * Screen.height * 0.45f)
                    continue;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>Инженер v1 — грузовик или игровой аналог инженера по имени
        /// (Jackknife): строит здания (DESIGN §2.3; модовые определения юнитов — позже).
        /// Морской инженер (v0.96): десантный катер LC — единственный нативный
        /// ховер-амфибия (AirCushion), сам ходит к водным площадкам по seaLanes,
        /// без костылей плавучести; строит верфи и будущие морские постройки.</summary>
        public static bool IsEngineer(Unit unit)
        {
            if (unit is Ship && unit.definition is ShipDefinition sea &&
                sea.shipType == ShipType.LC)
                return true;
            if (!(unit is GroundVehicle) || !(unit.definition is VehicleDefinition vd))
                return false;
            if (vd.vehicleType == VehicleType.TRUCK)
                return true;
            string name = vd.unitName ?? vd.jsonKey ?? "";
            return name.ToLowerInvariant().Contains("jack");
        }

        /// <summary>Вертолётное семейство (вертолёты/тильтвинги): свой автопилот
        /// (AutopilotHelo/AutopilotTiltwing) — высота всегда AGL с огибанием
        /// рельефа, скоростные режимы неприменимы (DESIGN §2.11a). Используется
        /// контекстностью командной панели и рулением.</summary>
        public static bool IsHeloFamily(Aircraft a) =>
            a != null && (a.autopilot is AutopilotHelo || a.autopilot is AutopilotTiltwing);
        // Связь аэродром→его здание-юнит: нужна для развед-гейта ромбов/миникарты
        // (сам Airbase не Unit, трекается именно здание). Скан редкий, кэш 2 с.
        private static readonly Dictionary<Airbase, Building> _airbaseBuildings =
            new Dictionary<Airbase, Building>();
        private static float _nextAirbaseScan;

        /// <summary>Аэродром под разведкой (свежий контакт по его зданию)?</summary>
        public static bool IsAirbaseScouted(Airbase airbase, FactionHQ hq)
        {
            if (airbase == null || hq == null)
                return false;
            if (Time.unscaledTime >= _nextAirbaseScan)
            {
                _nextAirbaseScan = Time.unscaledTime + 2f;
                _airbaseBuildings.Clear();
                foreach (Unit unit in UnitRegistry.allUnits)
                    if (unit is Building b && b.Networkairbase != null)
                        _airbaseBuildings[b.Networkairbase] = b;
            }
            if (!_airbaseBuildings.TryGetValue(airbase, out Building building) || building == null)
                return false;
            TrackingInfo info = hq.GetTrackingData(building.persistentID);
            return info != null && Time.timeSinceLevelLoad - info.lastSpottedTime <= Settings.StaleContactSec;
        }


        /// <summary>Видимый размер юнита на экране, px — критерий «различим глазами или нужна иконка».</summary>
        public static float ApparentSizePx(Unit unit, Camera cam)
        {
            if (unit == null || cam == null)
                return 0f;
            float dist = Vector3.Distance(cam.transform.position, unit.transform.position);
            float world = Mathf.Max(unit.maxRadius * 2f, 2f);
            float pxPerRad = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            return world / Mathf.Max(dist, 1f) * pxPerRad;
        }

        /// <summary>Точка привязки иконки — центр корпуса (пивот юнита стоит у земли).</summary>
        public static Vector3 UnitAnchorWorld(Unit unit) =>
            unit.transform.position + Vector3.up * (unit.maxRadius * 0.7f);
    }
}
