using System.Reflection;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Камера «взглядом из космоса» как в SupCom: фокус на земле, запертый в границах карты.
    /// Навигация только мышью: СКМ — «схватить и тащить» карту (скорость 1:1 масштабу сцены),
    /// колесо — зум к курсору в обе стороны (из космоса видно всю карту), Space+ЛКМ — угол.
    /// Клавиатурного панорамирования нет. Патчей нет: UpdateState зовётся из LateUpdate игры.
    /// </summary>
    public sealed class CommanderCameraState : CameraBaseState
    {
        private static readonly FieldInfo ThresholdField = typeof(FloatingOrigin)
            .GetField("threshold", BindingFlags.NonPublic | BindingFlags.Instance);

        private Vector3 _focus;
        private float _yaw;
        private float _pitch;
        private float _distance;

        private Vector3 _targetFocus;
        private float _targetYaw;
        private float _targetPitch;
        private float _targetDistance;

        // Фокус в глобальных координатах: игра сдвигает мировое начало координат
        // (FloatingOrigin, порог 1024м по любой оси) — локальный Vector3 «протухает» каждый кадр
        // на большой высоте/краю карты, что выглядело как бесконечный уезд камеры.
        private GlobalPosition _focusG;

        private float _savedFarClip;
        private CloudLayer _clouds;
        private bool _cloudsVisible = true;
        private Vector2 _mmDownPos;
        private float _mmDownTime;
        private string _edgeNow = "edge -";
        private Behaviour _trackIr;
        private bool _trackIrWasEnabled;

        // Якорное перетаскивание: при зажатой СКМ точка земли под курсором
        // «приклеивается» к курсору; поправка каждый кадр держит её под ним.
        private GlobalPosition _dragAnchorG;
        private bool _dragging;

        private Vector3 _lastFocusForDebug;
        private float _debugFocusSpeed;
        private Vector3 _lastTargetLocal;
        private float _savedOriginThreshold = 1024f;

        public override void EnterState(CameraStateManager cam)
        {
            cam.cameraPivot.SetParent(null);
            cam.transform.SetParent(null, worldPositionStays: true);
            CameraStateManager.cameraMode = CameraMode.free;
            if (cam.cockpitCamRender != null)
                cam.cockpitCamRender.enabled = false;
            FlightHud.EnableCanvas(false);

            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            if (!GameApi.TryGetGroundPoint(ray, out _focus))
                _focus = new Vector3(cam.transform.position.x, GameApi.GroundY(cam.transform.position.x, cam.transform.position.z), cam.transform.position.z);
            Vector3 euler = cam.transform.eulerAngles;
            _yaw = euler.y;
            _pitch = NormalizePitch(euler.x);
            _distance = Mathf.Max(Mathf.Abs((cam.transform.position - _focus).magnitude), 80f);

            _targetFocus = _focus;
            _targetYaw = _yaw;
            _targetPitch = _pitch;
            _targetDistance = _distance;
            _focusG = _focus.ToGlobalPosition();
            _lastTargetLocal = _focus;

            _savedFarClip = cam.mainCamera.farClipPlane;
            _clouds = Object.FindObjectOfType<CloudLayer>();
            // TrackIR живёт в другой сборке — берём по имени; посторонний трекинг
            // может сдвигать камеру после нас на каждом кадре.
            _trackIr = cam.GetComponent("TrackIRComponent") as Behaviour;
            if (_trackIr != null)
            {
                _trackIrWasEnabled = _trackIr.enabled;
                _trackIr.enabled = false;
            }

            cam.cameraVelocity = Vector3.zero;
            cam.allowInputs = false;

            // Дрейф на стратезуме лечим радикально: оседаем одним легальным сдвигом origin
            // у точки фокуса, после чего запрещаем игре сдвигать origin на всё время режима.
            FloatingOrigin fo = FloatingOrigin.Instance;
            if (fo != null && ThresholdField != null)
                _savedOriginThreshold = (float)ThresholdField.GetValue(fo);
            cam.transform.position = _focus + Vector3.up * 500f;
            cam.CheckOriginShift();
            if (fo != null && ThresholdField != null)
                ThresholdField.SetValue(fo, 1e9f);

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.Confined; // курсор не выпадает на соседние мониторы
        }

        public override void LeaveState(CameraStateManager cam)
        {
            cam.allowInputs = true;
            if (cam.mainCamera != null)
                cam.mainCamera.farClipPlane = _savedFarClip;
            if (_trackIr != null)
                _trackIr.enabled = _trackIrWasEnabled;
            SetClouds(visible: true);

            FloatingOrigin fo = FloatingOrigin.Instance;
            if (fo != null && ThresholdField != null)
                ThresholdField.SetValue(fo, _savedOriginThreshold);
            cam.CheckOriginShift();

            Cursor.lockState = CursorLockMode.None;
        }

        /// <summary>Строка диагностики для F11-оверлея.</summary>
        public string DebugLine =>
            $"focus {_focusG} | dist {_distance:F0} | скорость фокуса {_debugFocusSpeed:F1} м/с | {_edgeNow} | мышь {Input.mousePosition.x:F0},{Input.mousePosition.y:F0} | экран {Screen.width}x{Screen.height}";

        public float CurrentDistance => _distance;

        /// <summary>Мгновенно навести фокус на точку (дистанция/угол сохраняются).</summary>
        public void CenterOn(Vector3 worldPoint)
        {
            _targetFocus = new Vector3(worldPoint.x, GameApi.GroundY(worldPoint.x, worldPoint.z), worldPoint.z);
        }

        private void TickDebug(float dt)
        {
            Vector3 focusLocal = _focusG.ToLocalPosition();
            if (dt > 0.0001f)
                _debugFocusSpeed = Mathf.Lerp(_debugFocusSpeed, (focusLocal - _lastFocusForDebug).magnitude / dt, 0.2f);
            _lastFocusForDebug = focusLocal;
        }

        public override void UpdateState(CameraStateManager cam)
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f)
                return;

            if (cam.windNoiseExternal != null)
                cam.windNoiseExternal.volume = 0f;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.Confined;

            // Поглощаем сдвиг floating origin ДЕЛЬТОЙ к текущим целям:
            // внешние записи фокуса (CenterOn и т.п.) между кадрами не затираются.
            Vector3 localized = _focusG.ToLocalPosition();
            Vector3 originShift = localized - _lastTargetLocal;
            if (originShift.sqrMagnitude > 1e-6f)
            {
                _focus += originShift;
                _targetFocus += originShift;
            }

            if (!RtsInput.Typing)
                HandleInput();

            ClampTargets();
            _focusG = _targetFocus.ToGlobalPosition();
            _lastTargetLocal = _targetFocus;

            float posT = 1f - Mathf.Exp(-dt * Settings.MoveSmoothing);
            float rotT = 1f - Mathf.Exp(-dt * Settings.RotSmoothing);
            _focus = Vector3.Lerp(_focus, _targetFocus, posT);
            _yaw = Mathf.LerpAngle(_yaw, _targetYaw, rotT);
            _pitch = Mathf.Lerp(_pitch, _targetPitch, rotT);
            _distance = Mathf.Lerp(_distance, _targetDistance, posT);

            float effPitch = EffectivePitch(_distance, _pitch);
            Quaternion rotation = Quaternion.Euler(effPitch, _yaw, 0f);
            Vector3 position = _focus - rotation * Vector3.forward * _distance;

            float floor = GameApi.CameraFloorY(position);
            position.y = Mathf.Clamp(position.y, floor + Settings.MinHeight, floor + 200000f);

            // Вид из космоса требует длинного far-плана; возвращаем исходный при выходе.
            float needFar = _distance * 2.2f + 5000f;
            if (cam.mainCamera.farClipPlane < needFar)
                cam.mainCamera.farClipPlane = needFar;

            SetClouds(position.y < Settings.CloudHideAltitude);

            cam.transform.SetPositionAndRotation(position, rotation);
            cam.cameraVelocity = Vector3.zero;
            TickDebug(dt);
        }

        public override void FixedUpdateState(CameraStateManager cam)
        {
        }

        private void HandleInput()
        {
            // Панорамирование: СКМ — «схватить и тащить». Точка земли под курсором прижимается
            // к курсору: поправка каждый кадр возвращает её под него. Скорость автоматически
            // равна масштабу сцены (вблизи — метры, из космоса — километры), любой наклон учтён.
            // Короткий клик СКМ без движения — центрировать камеру на точке под курсором.
            if (Input.GetMouseButtonDown(2) && !RtsInput.PointerOverUi)
            {
                _mmDownPos = RtsInput.MousePos;
                _mmDownTime = Time.unscaledTime;
                if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 grab))
                {
                    _dragAnchorG = grab.ToGlobalPosition();
                    _dragging = true;
                }
            }
            if (Input.GetMouseButtonUp(2))
            {
                _dragging = false;
                // Порог «клика без движения» масштабируем под разрешение (на 4К 6px — ничто).
                float tapPx = Mathf.Max(6f, Screen.height / 240f);
                if (!RtsInput.PointerOverUi &&
                    Time.unscaledTime - _mmDownTime < 0.3f &&
                    Vector2.Distance(RtsInput.MousePos, _mmDownPos) < tapPx &&
                    GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 snap))
                {
                    _targetFocus = snap;
                }
            }
            if (_dragging && RtsInput.MiddleHeld && !RtsInput.PointerOverUi)
            {
                if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 now))
                    _targetFocus += _dragAnchorG.ToLocalPosition() - now;
            }
            else if (_dragging && !RtsInput.MiddleHeld)
            {
                _dragging = false;
            }

            // Панорамирование у края экрана: скорость пропорциональна размеру вида.
            // Порог «курсор покинул окно» покрывает мульти-монитор (курсор уходит на соседний экран).
            if (Settings.EdgeScroll && Application.isFocused && !RtsInput.Typing)
            {
                Vector3 m = Input.mousePosition;
                float ex = 0f, ez = 0f;
                if (m.x <= 1f || (m.x >= 1f && m.x < Settings.EdgePixels)) ex = -1f;
                else if (m.x >= Screen.width - 1f || m.x > Screen.width - Settings.EdgePixels) ex = 1f;
                if (m.y <= 1f || (m.y >= 1f && m.y < Settings.EdgePixels)) ez = -1f;
                else if (m.y >= Screen.height - 1f || m.y > Screen.height - Settings.EdgePixels) ez = 1f;
                if (ex != 0f || ez != 0f)
                {
                    float fovRad = GameApi.MainCam.fieldOfView * Mathf.Deg2Rad;
                    float viewHeight = 2f * Mathf.Tan(fovRad * 0.5f) * _targetDistance;
                    Vector3 right = Quaternion.Euler(0f, _targetYaw, 0f) * Vector3.right;
                    Vector3 forward = Quaternion.Euler(0f, _targetYaw, 0f) * Vector3.forward;
                    _targetFocus += (right * ex + forward * ez).normalized * (viewHeight * Settings.EdgeSpeed * Time.unscaledDeltaTime);
                }
                _edgeNow = (ex != 0f || ez != 0f) ? $"edge x={ex} z={ez}" : "edge -";
            }
            else
            {
                _edgeNow = "edge off";
            }

            // Home — центр карты.
            if (Input.GetKeyDown(KeyCode.Home) && GameApi.TryGetMapBounds(out Vector2 bmin, out Vector2 bmax))
                _targetFocus = new Vector3((bmin.x + bmax.x) * 0.5f, _targetFocus.y, (bmin.y + bmax.y) * 0.5f);

            // Зум колесом. Скольжение фокуса к курсору пропорционально РЕАЛЬНОМУ изменению
            // дистанции: если дистанция упёрлась в минимум/максимум — фокус не двигается вовсе.
            // Порог 0.05 отсекает шум оси колеса (микрозначения тянули фокус на больших высотах).
            float wheel = RtsInput.Wheel;
            if (Mathf.Abs(wheel) >= 0.05f && !RtsInput.PointerOverUi)
            {
                const float stepCap = 0.3f;
                float step = Mathf.Clamp(wheel * Settings.ZoomPerNotch, -stepCap, stepCap);
                (float minDist, float maxDist) = DistanceBounds();
                float newDist = Mathf.Clamp(_targetDistance * (1f - step), minDist, maxDist);
                float applied = newDist / Mathf.Max(_targetDistance, 0.001f);
                if (applied < 0.999f && GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 anchor))
                    _targetFocus = anchor + (_targetFocus - anchor) * applied;
                _targetDistance = newDist;
            }

            // Угол при приближении: Space+ЛКМ.
            if (RtsInput.RotateModifierHeld && RtsInput.LeftHeld && !RtsInput.PointerOverUi)
            {
                float dx = Input.GetAxis("Mouse X");
                float dy = Input.GetAxis("Mouse Y");
                _targetYaw += dx * Settings.RotateDegPerPixel;
                _targetPitch = Mathf.Clamp(_targetPitch - dy * Settings.RotateDegPerPixel, Settings.PitchMin, Settings.PitchMax);
            }
        }

        private void ClampTargets()
        {
            float effPitch = EffectivePitch(_targetDistance, _targetPitch);
            float sin = Mathf.Max(Mathf.Sin(effPitch * Mathf.Deg2Rad), 0.2f);
            (float minDist, float maxDist) = DistanceBounds();
            _targetDistance = Mathf.Clamp(_targetDistance, minDist, maxDist);

            if (GameApi.TryGetMapBounds(out Vector2 min, out Vector2 max))
            {
                float cx = (min.x + max.x) * 0.5f;
                float cz = (min.y + max.y) * 0.5f;
                float halfW = (max.x - min.x) * 0.5f;
                float halfH = (max.y - min.y) * 0.5f;

                // Стягивание к центру — только на последних 15% зума (вся карта в кадре).
                float t = Mathf.InverseLerp(maxDist * 0.85f, maxDist, _targetDistance);
                if (t > 0f)
                {
                    float fovRad = (GameApi.MainCam != null ? GameApi.MainCam.fieldOfView : 60f) * Mathf.Deg2Rad;
                    float aspect = (GameApi.MainCam != null ? GameApi.MainCam.aspect : 16f / 9f);
                    float visHalfV = _targetDistance * Mathf.Tan(fovRad * 0.5f) / sin;
                    float visHalfH = visHalfV * aspect * 0.75f;
                    float allowedX = Mathf.Lerp(halfW + Settings.MapMargin, Mathf.Max(0f, halfW - visHalfH), t);
                    float allowedZ = Mathf.Lerp(halfH + Settings.MapMargin, Mathf.Max(0f, halfH - visHalfV), t);
                    _targetFocus.x = Mathf.Clamp(_targetFocus.x, cx - allowedX, cx + allowedX);
                    _targetFocus.z = Mathf.Clamp(_targetFocus.z, cz - allowedZ, cz + allowedZ);
                }

                // Единственное жёсткое правило на всех зумах: САМА КАМЕРА не покидает карту
                // (небольшой запас). Фокус следует за камерой — поэтому центр экрана
                // доходит до края (и даже заглядывает за него) на любом зуме и наклоне.
                Quaternion rot = Quaternion.Euler(effPitch, _targetYaw, 0f);
                Vector3 view = rot * Vector3.forward;
                Vector3 pos = _targetFocus - view * _targetDistance;
                Vector3 clamped = pos;
                clamped.x = Mathf.Clamp(clamped.x, min.x - 300f, max.x + 300f);
                clamped.z = Mathf.Clamp(clamped.z, min.y - 300f, max.y + 300f);
                if (clamped != pos)
                    _targetFocus = clamped + view * _targetDistance;
            }

            _targetFocus.y = GameApi.GroundY(_targetFocus.x, _targetFocus.z);
        }

        /// <summary>Мин/макс дистанции до фокуса при текущем угле (высотные клампы камеры).</summary>
        private (float minDist, float maxDist) DistanceBounds()
        {
            float sin = Mathf.Max(Mathf.Sin(EffectivePitch(_targetDistance, _targetPitch) * Mathf.Deg2Rad), 0.2f);
            float minDist = Mathf.Max(Settings.MinDistance, Settings.MinHeight / sin);
            // ×0.9 достаточно, чтобы карта влезала в кадр; жёсткий потолок ниже 90 км —
            // за пределами ±90 км игра не может сдвигать origin, начинается дрожь мира.
            float maxAlt = Mathf.Min(Mathf.Max(Settings.MaxHeight, MapExtent() * 0.9f), Settings.MaxAltitudeCeiling);
            return (minDist, maxAlt / sin);
        }

        private static float MapExtent()
        {
            return GameApi.TryGetMapBounds(out Vector2 min, out Vector2 max)
                ? Mathf.Max(max.x - min.x, max.y - min.y)
                : 0f;
        }

        /// <summary>Чем дальше — тем ближе к виду сверху (SupCom strategic zoom).</summary>
        private static float EffectivePitch(float distance, float userPitch)
        {
            float blend = Mathf.InverseLerp(Settings.TopDownStartDistance, Settings.TopDownFullDistance, distance);
            return Mathf.Lerp(userPitch, Settings.TopDownPitch, blend);
        }

        private void SetClouds(bool visible)
        {
            if (_cloudsVisible == visible)
                return;
            _cloudsVisible = visible;
            if (_clouds != null)
                _clouds.gameObject.SetActive(visible);
        }

        private static float NormalizePitch(float eulerX)
        {
            if (eulerX > 180f)
                eulerX -= 360f;
            return Mathf.Clamp(eulerX, Settings.PitchMin, Settings.PitchMax);
        }
    }
}
