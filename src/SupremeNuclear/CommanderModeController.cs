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
                }
                else if (RtsInput.RightDown && !RtsInput.PointerOverUi)
                {
                    BuildTasks.Pending = null;
                    BuildTasks.MarkRmbEaten();
                    CommanderHud.Notice("Установка отменена");
                }
                else if (RtsInput.LeftDown && !RtsInput.PointerOverUi && !RouteEditor.Consumed)
                {
                    if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 site))
                    {
                        GameApi.TryGetLocalHQ(out FactionHQ hq);
                        // Без Shift — НОВАЯ очередь: прежние очереди выделенных инженеров
                        // ломаются (как обычный приказ). Shift — дописать (серия SupCom).
                        if (!RtsInput.Shift)
                            foreach (Unit unit in Selection.Selected)
                                if (GameApi.IsEngineer(unit))
                                    BuildTasks.ClearFor(unit);
                        BuildTasks.TryPlace(site, Selection.Selected, hq);
                        if (!RtsInput.Shift)
                            BuildTasks.Pending = null;
                    }
                    BuildTasks.MarkConsumed();
                }
            }

            Selection.Tick();
            MoveOrders.Tick();
            GroundOrders.Tick();
            GroundOrders.Prune();
            AircraftOrders.Prune();
            StanceService.Prune();
            TickControlGroups();
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            {
                if (RtsInput.KeyDown(KeyCode.Alpha1)) SetProfile(0);
                else if (RtsInput.KeyDown(KeyCode.Alpha2)) SetProfile(1);
                else if (RtsInput.KeyDown(KeyCode.Alpha3)) SetProfile(2);
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
                if (RtsInput.KeyDown(KeyCode.M)) OrderModes.Pending = OrderMode.Move;
                else if (RtsInput.KeyDown(KeyCode.A)) OrderModes.Pending = OrderMode.AttackMove;
                else if (RtsInput.KeyDown(KeyCode.U)) OrderModes.Pending = OrderMode.Patrol;
                else if (RtsInput.KeyDown(KeyCode.T)) OrderModes.Pending = OrderMode.Attack;
                else if (RtsInput.KeyDown(KeyCode.S))
                    MoveOrders.CancelAll(Selection.Selected); // мгновенная отмена
                if (RtsInput.KeyDown(KeyCode.Escape))
                    OrderModes.Pending = OrderMode.Move;
            }
        }

        /// <summary>Группы управления: Ctrl+1..9 — назначить, 1..9 — вызвать,
        /// двойное нажатие — камера на группу.</summary>
        private static void SetProfile(int index)
        {
            AircraftOrders.ProfileIndex = Mathf.Clamp(index, 0, AircraftOrders.Profiles.Length - 1);
            var p = AircraftOrders.Profiles[AircraftOrders.ProfileIndex];
            CommanderHud.Notice($"Профиль высоты: {p.Name} ({p.AltitudeAgl:F0} м{(p.FollowTerrain ? ", огибание" : "")})");
        }

        private void TickControlGroups()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            for (int i = 1; i <= 9; i++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha0 + i) || RtsInput.Typing)
                    continue;
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
            IsActive = true;
            Log.Info("Режим командира включён");
        }

        private void Deactivate(bool silent = false)
        {
            IsActive = false;
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
