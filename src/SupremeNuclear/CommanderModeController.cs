using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Включение/выключение режима командира и тик сервисов.</summary>
    public sealed class CommanderModeController : MonoBehaviour
    {
        public static CommanderModeController Instance { get; private set; }

        public bool IsActive { get; private set; }
        public SelectionService Selection { get; private set; }
        public MoveOrderService MoveOrders { get; private set; }
        public CommanderCameraState CameraState => _cameraState;

        private CommanderCameraState _cameraState;
        private readonly float[] _groupRecallAt = new float[10];
        private bool _stockMapWasActive; // была ли видна родная карта игры до режима командира

        private void Awake()
        {
            Instance = this;
            Selection = new SelectionService();
            MoveOrders = new MoveOrderService(this);
        }

        private float _lastErrorAt = -10f;

        /// <summary>Yaw (град, вокруг Y), каким выход «from» смотрит на «to».</summary>
        private static float YawBetween(Vector3 from, Vector3 to) =>
            Mathf.Atan2(to.x - from.x, to.z - from.z) * Mathf.Rad2Deg;

        private void Update()
        {
            try
            {
                UpdateSafe();
            }
            catch (System.Exception e)
            {
                // Не самоотключаемся (урок SupcomUi): троттлим лог и продолжаем тикать.
                if (Time.unscaledTime - _lastErrorAt > 5f)
                {
                    _lastErrorAt = Time.unscaledTime;
                    Log.Error("CommanderModeController: сбой Update — " + e.GetType().Name + ": " +
                              e.Message + " | " + e.StackTrace);
                }
            }
        }

        private void UpdateSafe()
        {
            if (!GameApi.InMission)
            {
                if (IsActive)
                    Deactivate(silent: true);
                return;
            }

            GameApi.TickOriginShiftMonitor();
            GameApi.TickHpCache();
            EconomyService.Tick();
            BuildTasks.Tick();
            ProductionTasks.Tick();
            SortieService.Tick(); // миссии вылетов живут и со закрытым столом командира
            SalvoService.Tick();  // залп-приказы — тоже (пуски на хосте)
            WeaponGateService.Tick(); // гейты вооружения — блок Ready() выключенных станций
            RouteRibbonMesh.Tick();
            RouteRibbonMesh.Render();
            BuildGhosts.Render();

            if (RtsInput.KeyDown(Settings.ToggleCommanderKey))
            {
                if (IsActive)
                    Deactivate();
                else
                    Activate();
            }

            if (!IsActive)
                return;

            CameraStateManager cam = GameApi.CamManager;
            if (cam == null || cam.currentState != _cameraState)
            {
                // Игра сама переключила камеру (вход в самолёт, меню и т.п.) — тихо выходим.
                Deactivate(silent: true);
                return;
            }

            CommanderAudio.Tick(); // доплер держим нулём: новые источники (взлёт/ракеты) дочищаются

            RouteEditor.Tick(this);

            // Режим установки здания (Task-API): ЛКМ по миру ставит площадку, ПКМ/Esc — отмена.
            BuildTasks.NewFrame();
            // Режим призраков шаблона (вкладка «Ш»): ЛКМ строит весь набор, ПКМ/Esc — отмена.
            if (OrderTemplates.PendingTemplate.template != null)
            {
                if (RtsInput.KeyDown(KeyCode.Escape))
                {
                    OrderTemplates.PendingTemplate = default;
                }
                else if (RtsInput.RightDown && !RtsInput.PointerOverUi)
                {
                    OrderTemplates.PendingTemplate = default;
                    BuildTasks.MarkRmbEaten();
                    CommanderHud.Notice("Шаблон отменён");
                }
                else if (RtsInput.LeftDown && !RtsInput.PointerOverUi && !RouteEditor.Consumed)
                {
                    if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 tplCenter))
                    {
                        GameApi.TryGetLocalHQ(out FactionHQ tplHq);
                        if (!RtsInput.Shift)
                            foreach (Unit unit in Selection.Selected)
                                if (GameApi.IsEngineer(unit))
                                    BuildTasks.ClearFor(unit);
                        OrderTemplates.PlacePending(tplCenter, tplHq);
                        if (!RtsInput.Shift)
                            OrderTemplates.PendingTemplate = default; // Shift — сетка сериями
                    }
                    BuildTasks.MarkConsumed();
                }
            }
            if (BuildTasks.Pending != null)
            {
                if (RtsInput.KeyDown(KeyCode.Escape))
                {
                    BuildTasks.Pending = null;
                    BuildTasks.PlacingActive = false;
                }
                else if (RtsInput.RightDown && !RtsInput.PointerOverUi)
                {
                    BuildTasks.Pending = null;
                    BuildTasks.PlacingActive = false;
                    BuildTasks.MarkRmbEaten();
                    CommanderHud.Notice("Установка отменена");
                }
                else if (RtsInput.LeftDown && !RtsInput.PointerOverUi && !RouteEditor.Consumed)
                {
                    // Клик фиксирует площадку; пока ЛКМ удерживается — поворот выходом
                    // к курсору, отпускание ставит. Дефолт ангару — выходом к аэродрому.
                    if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 site))
                    {
                        BuildTasks.PlacingActive = true;
                        BuildTasks.PlacingSite = site.ToGlobalPosition();
                        BuildTasks.PendingYawDeg = 0f;
                        if (BuildTasks.IsHangarDefinition(BuildTasks.Pending))
                        {
                            GameApi.TryGetLocalHQ(out FactionHQ abHq);
                            Airbase ab = BuildTasks.NearestOwnAirbase(
                                site, abHq, Settings.HangarAttachRadiusM);
                            if (ab != null && ab.center != null)
                                BuildTasks.PendingYawDeg = YawBetween(site, ab.center.position);
                        }
                    }
                    BuildTasks.MarkConsumed();
                }
                else if (BuildTasks.PlacingActive && RtsInput.LeftHeld)
                {
                    // Держим ЛКМ: крутим выход к курсору (порог 2 м — дрожь у якоря).
                    if (!RtsInput.PointerOverUi &&
                        GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 g))
                    {
                        Vector3 anchor = BuildTasks.PlacingSite.ToLocalPosition();
                        Vector2 delta = new Vector2(g.x - anchor.x, g.z - anchor.z);
                        if (delta.sqrMagnitude > 4f)
                            BuildTasks.PendingYawDeg =
                                Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg;
                    }
                    // Весь жест — выделение-neutral: рамка не стартует на движении мыши.
                    BuildTasks.MarkConsumed();
                }
                else if (BuildTasks.PlacingActive && RtsInput.LeftUp)
                {
                    BuildTasks.PlacingActive = false;
                    Vector3 site = BuildTasks.PlacingSite.ToLocalPosition();
                    GameApi.TryGetLocalHQ(out FactionHQ hq);
                    // Без Shift — НОВАЯ очередь: прежние очереди выделенных инженеров
                    // ломаются (как обычный приказ). Shift — дописать (серия SupCom).
                    if (!RtsInput.Shift)
                        foreach (Unit unit in Selection.Selected)
                            if (GameApi.IsEngineer(unit))
                                BuildTasks.ClearFor(unit);
                    BuildTasks.TryPlace(site, Selection.Selected, hq, BuildTasks.PendingYawDeg);
                    if (!RtsInput.Shift)
                        BuildTasks.Pending = null;
                    BuildTasks.MarkConsumed();
                }
            }

            Selection.Tick();
            MoveOrders.Tick();
            GroundOrders.Tick();
            GroundOrders.Prune();
            AircraftOrders.Prune();
            StanceService.Prune();
            WeaponGateService.Prune();
            DefenseService.Prune();
            SalvoService.Prune();
            TickControlGroups();

            // Профили высоты Alt+1..4 и скорость Shift+1..3: штамп для новых участков
            // + немедленное применение к головному сегменту выделенных бортов.
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (alt)
            {
                if (RtsInput.KeyDown(KeyCode.Alpha1)) SetProfile(0);
                else if (RtsInput.KeyDown(KeyCode.Alpha2)) SetProfile(1);
                else if (RtsInput.KeyDown(KeyCode.Alpha3)) SetProfile(2);
                else if (RtsInput.KeyDown(KeyCode.Alpha4)) SetProfile(3);
                else if (RtsInput.KeyDown(KeyCode.D)) CycleDefense();
            }
            else if (shiftHeld)
            {
                if (RtsInput.KeyDown(KeyCode.Alpha1)) SetSpeed(0);
                else if (RtsInput.KeyDown(KeyCode.Alpha2)) SetSpeed(1);
                else if (RtsInput.KeyDown(KeyCode.Alpha3)) SetSpeed(2);
            }

            // Ctrl+K — УНИЧТОЖИТЬ выделенное (дефолт SupCom): юниты/здания гибнут честно
            // (урон по деталям, обломки), площадка-недострой сносится. Хост.
            if (RtsInput.Ctrl && RtsInput.KeyDown(KeyCode.K))
                SelfDestructSelection();

            if (RtsInput.KeyDown(KeyCode.H) && Selection.Selected.Count > 0)
            {
                int on = 0;
                foreach (Unit unit in Selection.Selected)
                    if (StanceService.Toggle(unit))
                        on++;
                CommanderHud.Notice(on > 0
                    ? $"Огонь удерживают: {on}"
                    : "Огонь разрешён");
            }

            if (Selection.Selected.Count > 0)
            {
                // Alt+Shift+R — Disperse Move (FAF-стиль): очередь вейпоинтов группы
                // распределяется прямыми векторами. Комбо первым в цепи: R без
                // модификаторов ниже заряжает «Атаку веером».
                if (alt && shiftHeld && RtsInput.KeyDown(KeyCode.R))
                    MoveOrders.IssueDisperseMove(Selection.Selected);
                else if (RtsInput.KeyDown(KeyCode.M)) OrderModes.Pending = OrderMode.Move;
                else if (RtsInput.KeyDown(KeyCode.A)) OrderModes.Pending = OrderMode.AttackMove;
                else if (RtsInput.KeyDown(KeyCode.U)) OrderModes.Pending = OrderMode.Patrol;
                else if (RtsInput.KeyDown(KeyCode.T)) OrderModes.Pending = OrderMode.Attack;
                else if (RtsInput.KeyDown(KeyCode.V)) OrderModes.Pending = OrderMode.Salvo;
                else if (RtsInput.KeyDown(KeyCode.R))
                    MoveOrders.ChargeFanAttack(); // «Атака веером»: копим цели ПКМ-ами
                else if (RtsInput.KeyDown(KeyCode.S))
                    MoveOrders.CancelAll(Selection.Selected); // мгновенная отмена
                if (RtsInput.KeyDown(KeyCode.G))
                    MoveOrders.IssueReturnToBase(Selection.Selected); // борта — домой, мгновенно
                if (RtsInput.KeyDown(KeyCode.X))
                {
                    int popped = DefenseService.PopNow(Selection.Selected);
                    CommanderHud.Notice(popped > 0
                        ? $"Ловушки: бортов — {popped}"
                        : "В выделении нет бортов с контрмерами");
                }
                if (RtsInput.KeyDown(KeyCode.Escape))
                    OrderModes.Pending = OrderMode.Move;
            }
        }

        /// <summary>Профиль высоты: штамп для новых участков + применение к текущему
        /// участку выделенных бортов (Alt+1..4, кнопки панели).</summary>
        private void SetProfile(int index)
        {
            AircraftOrders.ProfileIndex = Mathf.Clamp(index, 0, AircraftOrders.Profiles.Length - 1);
            var p = AircraftOrders.Profiles[AircraftOrders.ProfileIndex];
            int applied = 0;
            foreach (Unit unit in Selection.Selected)
                if (unit is Aircraft && AircraftOrders.ApplyModesToCurrent(unit))
                    applied++;
            CommanderHud.Notice($"Профиль высоты: {p.Name} ({p.AltitudeAgl:F0} м{(p.FollowTerrain ? ", огибание" : "")})" +
                                (applied > 0 ? $" — участок сменили {applied}" : ""));
        }

        /// <summary>Скоростной режим (Shift+1..3, кнопки): Полный/Крейсер/Эконом.
        /// Самолёты; вертолётам тротл не назначается (газ ведёт игра).</summary>
        private void SetSpeed(int index)
        {
            var modes = new[] { SpeedMode.Full, SpeedMode.Cruise, SpeedMode.Economy };
            var names = new[] { "Полный", "Крейсер", "Эконом" };
            AircraftOrders.SpeedIndex = modes[Mathf.Clamp(index, 0, modes.Length - 1)];
            int applied = 0;
            foreach (Unit unit in Selection.Selected)
                if (unit is Aircraft && AircraftOrders.ApplyModesToCurrent(unit))
                    applied++;
            CommanderHud.Notice($"Скорость: {names[index]}" +
                                (applied > 0 ? $" — участок сменили {applied}" : "") +
                                " (самолёты)");
        }

        /// <summary>Цикл режима авто-защиты выделенных бортов (Alt+D, кнопки).</summary>
        private void CycleDefense()
        {
            if (Selection.Selected.Count == 0)
                return;
            DefenseMode first = DefenseMode.Auto;
            bool any = false;
            foreach (Unit unit in Selection.Selected)
            {
                if (!(unit is Aircraft))
                    continue;
                DefenseMode mode = DefenseService.Cycle(unit);
                if (!any)
                {
                    first = mode;
                    any = true;
                }
            }
            if (any)
                CommanderHud.Notice("Защита: " + DefenseName(first));
        }

        internal static string DefenseName(DefenseMode mode) =>
            mode switch
            {
                DefenseMode.Auto => "Авто (уклонение + РЭБ/ловушки)",
                DefenseMode.Flares => "Ловушки (маршрут держим)",
                _ => "Выкл"
            };

        /// <summary>Кнопки панели: профиль высоты / скорость / защита по индексу.</summary>
        public void SetProfileUi(int index) => SetProfile(index);
        public void SetSpeedUi(int index) => SetSpeed(index);
        public void SetDefenseUi(int index)
        {
            var modes = new[] { DefenseMode.Auto, DefenseMode.Flares, DefenseMode.Off };
            DefenseMode mode = modes[Mathf.Clamp(index, 0, modes.Length - 1)];
            int n = 0;
            foreach (Unit unit in Selection.Selected)
                if (unit is Aircraft)
                    n++;
            if (n == 0)
            {
                CommanderHud.Notice("Защита: в выделении нет бортов");
                return;
            }
            foreach (Unit unit in Selection.Selected)
                DefenseService.Set(unit, mode);
            CommanderHud.Notice("Защита: " + DefenseName(mode));
        }

        /// <summary>Группы управления: Ctrl+1..9 — назначить, 1..9 — вызвать (без
        /// модификаторов; Alt/Shift+цифра заняты профилями высоты и скоростью),
        /// двойное нажатие — камера на группу.</summary>
        private void TickControlGroups()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            for (int i = 1; i <= 9; i++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha0 + i) || RtsInput.Typing)
                    continue;
                if (alt || shift)
                    continue; // Alt+1..4 — профиль высоты, Shift+1..3 — скорость
                if (ctrl)
                {
                    if (Selection.Selected.Count > 0)
                    {
                        SelectionService.AssignGroup(i, Selection.Selected);
                        CommanderHud.Notice($"Группа {i}: назначено {Selection.Selected.Count}");
                    }
                    continue;
                }
                bool doubleTap = Time.unscaledTime - _groupRecallAt[i] < 0.4f;
                _groupRecallAt[i] = Time.unscaledTime;
                List<Unit> group = Selection.RecallGroup(i);
                if (group == null)
                    continue;
                if (doubleTap && _cameraState != null && group[0] != null)
                    _cameraState.CenterOn(group[0].transform.position);
            }
        }

        /// <summary>Ctrl+K: уничтожить всё выделенное. Свои юниты/здания — через игровой
        /// урон по деталям (обломки, статистика); выделенный недострой — снос площадки.</summary>
        private void SelfDestructSelection()
        {
            var net = NuclearOption.Networking.NetworkManagerNuclearOption.i;
            bool host = net != null && net.Server != null && net.Server.Active;
            if (!host)
            {
                CommanderHud.Notice("Уничтожение — только на хосте");
                return;
            }

            int killed = 0;
            GameApi.TryGetLocalHQ(out FactionHQ hq);
            foreach (Unit unit in Selection.Selected)
            {
                if (unit == null || unit.disabled)
                    continue;
                if (unit.NetworkHQ == null || unit.NetworkHQ != hq)
                    continue; // чужих и нейтральных сносить нельзя
                bool any = false;
                foreach (UnitPart part in unit.partLookup)
                {
                    if (part == null)
                        continue;
                    part.TakeDamage(1e6f, 1e6f, 1f, 1e6f, 1e6f, PersistentID.None);
                    any = true;
                }
                if (any)
                    killed++;
            }

            // Аэродром — не Unit: «уничтожить аэродром» = убить его здания.
            Airbase airbase = Selection.SelectedAirbase;
            if (airbase != null)
            {
                foreach (Unit unit in UnitRegistry.allUnits)
                {
                    if (unit is Building b && b.Networkairbase == airbase && !b.disabled)
                    {
                        foreach (UnitPart part in unit.partLookup)
                            if (part != null)
                                part.TakeDamage(1e6f, 1e6f, 1f, 1e6f, 1e6f, PersistentID.None);
                        killed++;
                    }
                }
                Selection.SelectedAirbase = null;
            }

            // Недострой/призрак — снос площадки (если выделен кликом).
            int sites = 0;
            if (BuildTasks.SelectedTask != null)
            {
                BuildTasks.Cancel(BuildTasks.SelectedTask);
                BuildTasks.SelectedTask = null;
                sites++;
            }

            if (killed == 0 && sites == 0)
                CommanderHud.Notice("Ctrl+K: нечего уничтожать (своё выделение/недострой)");
            else
            {
                CommanderHud.Notice("Уничтожено: " + killed +
                    (sites > 0 ? " + площадка" : ""));
                Log.Info("Ctrl+K: уничтожено юнитов/зданий " + killed + ", площадок " + sites);
            }
        }

        private void Activate()
        {
            CameraStateManager cam = GameApi.CamManager;
            if (cam == null || cam.mainCamera == null)
            {
                CommanderHud.Notice("Supreme Nuclear: камера ещё не готова");
                return;
            }
            if (cam.currentState == cam.cockpitState || cam.currentState == cam.controlledState ||
                cam.currentState == cam.TVState || cam.currentState == cam.chaseState)
            {
                CommanderHud.Notice("Supreme Nuclear: сначала выйди из кабины/съёмки, затем включай режим командира");
                return;
            }

            SelfCheck.RunActivation();

            // Родная карта игры не нужна в режиме командира (дублируется с миникартой мода).
            DynamicMap stockMap = SceneSingleton<DynamicMap>.i;
            if (stockMap != null)
            {
                _stockMapWasActive = stockMap.gameObject.activeSelf;
                DynamicMap.EnableCanvas(false);
            }

            Selection.Reset();
            _cameraState = new CommanderCameraState();
            cam.SwitchState(_cameraState);
            // Доплер гасим последним: выше Activate ещё может тихо сорваться,
            // а Restore в Deactivate при IsActive=false уже не выполнится.
            if (Settings.CommanderNoDoppler)
                CommanderAudio.Suppress();
            IsActive = true;
            Log.Info("Режим командира включён");
        }

        private void Deactivate(bool silent = false)
        {
            IsActive = false;
            CommanderAudio.Restore(); // доплер гасили на входе — вернули на любом выходе
            _cameraState = null;
            Selection.Reset();
            CameraStateManager cam = GameApi.CamManager;
            if (cam != null && cam.freeState != null && cam.currentState is CommanderCameraState)
                cam.SwitchState(cam.freeState);
            DynamicMap stockMap = SceneSingleton<DynamicMap>.i;
            if (stockMap != null)
                DynamicMap.EnableCanvas(_stockMapWasActive);
            if (!silent)
                Log.Info("Режим командира выключен");
        }
    }
}
