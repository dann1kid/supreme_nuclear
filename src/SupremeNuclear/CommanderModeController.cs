using System.Collections.Generic;
using NuclearOption.Networking;
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
            TaxiSurface.Render(); // полосы дорисованных рулёжек (Update до рендера — №16)

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
                    CommanderHud.Notice(Loc.Tr("Шаблон отменён"));
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
                    CommanderHud.Notice(Loc.Tr("Установка отменена"));
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
                    // ломаются (как обычный приказ). Площадку строят ВСЕ выделенные —
                    // пачкой (SupCom: группа едет вместе, серию строит по очереди).
                    // Shift — дописать в существующие очереди (серия).
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

            // Режим установки ВПП: ЛКМ — первый торец, ПРОТЯНУТЬ — второй (длина/курс
            // курсором, клампится в [RunwayMinM, RunwayMaxM]), отпускание — площадка.
            if (BuildTasks.PendingRunway)
            {
                if (RtsInput.KeyDown(KeyCode.Escape))
                {
                    BuildTasks.PendingRunway = false;
                    BuildTasks.PlacingActive = false;
                }
                else if (RtsInput.RightDown && !RtsInput.PointerOverUi)
                {
                    BuildTasks.PendingRunway = false;
                    BuildTasks.PlacingActive = false;
                    BuildTasks.MarkRmbEaten();
                    CommanderHud.Notice(Loc.Tr("Установка отменена"));
                }
                else if (RtsInput.LeftDown && !RtsInput.PointerOverUi && !RouteEditor.Consumed)
                {
                    if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 rwyStart))
                    {
                        BuildTasks.PlacingActive = true;
                        BuildTasks.PlacingSite = rwyStart.ToGlobalPosition();
                        BuildTasks.PlacingRunwayEnd = BuildTasks.PlacingSite;
                    }
                    BuildTasks.MarkConsumed();
                }
                else if (BuildTasks.PlacingActive && RtsInput.LeftHeld)
                {
                    if (!RtsInput.PointerOverUi &&
                        GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 rwyEnd))
                        BuildTasks.PlacingRunwayEnd = rwyEnd.ToGlobalPosition();
                    BuildTasks.MarkConsumed();
                }
                else if (BuildTasks.PlacingActive && RtsInput.LeftUp)
                {
                    BuildTasks.PlacingActive = false;
                    GameApi.TryGetLocalHQ(out FactionHQ hq);
                    if (!RtsInput.Shift)
                        foreach (Unit unit in Selection.Selected)
                            if (GameApi.IsEngineer(unit))
                                BuildTasks.ClearFor(unit);
                    BuildTasks.TryPlaceRunway(
                        BuildTasks.PlacingSite.ToLocalPosition(),
                        BuildTasks.PlacingRunwayEnd.ToLocalPosition(),
                        Selection.Selected, hq);
                    if (!RtsInput.Shift)
                        BuildTasks.PendingRunway = false;
                    BuildTasks.MarkConsumed();
                }
            }

            // Режим установки ВЕРФИ (v0.92): как здание — клик по воде фиксирует
            // центр, удержание ЛКМ крутит корпус, отпускание ставит площадку
            // (валидация глубины — в TryPlaceDock).
            if (BuildTasks.PendingDock)
            {
                if (RtsInput.KeyDown(KeyCode.Escape))
                {
                    BuildTasks.PendingDock = false;
                    BuildTasks.PlacingActive = false;
                }
                else if (RtsInput.RightDown && !RtsInput.PointerOverUi)
                {
                    BuildTasks.PendingDock = false;
                    BuildTasks.PlacingActive = false;
                    BuildTasks.MarkRmbEaten();
                    CommanderHud.Notice(Loc.Tr("Установка отменена"));
                }
                else if (RtsInput.LeftDown && !RtsInput.PointerOverUi && !RouteEditor.Consumed)
                {
                    if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 dockSite))
                    {
                        BuildTasks.PlacingActive = true;
                        BuildTasks.PlacingSite = dockSite.ToGlobalPosition();
                        BuildTasks.PendingYawDeg = 0f;
                    }
                    BuildTasks.MarkConsumed();
                }
                else if (BuildTasks.PlacingActive && RtsInput.LeftHeld)
                {
                    if (!RtsInput.PointerOverUi &&
                        GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 dg))
                    {
                        Vector3 anchor = BuildTasks.PlacingSite.ToLocalPosition();
                        Vector2 delta = new Vector2(dg.x - anchor.x, dg.z - anchor.z);
                        if (delta.sqrMagnitude > 4f)
                            BuildTasks.PendingYawDeg =
                                Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg;
                    }
                    BuildTasks.MarkConsumed();
                }
                else if (BuildTasks.PlacingActive && RtsInput.LeftUp)
                {
                    BuildTasks.PlacingActive = false;
                    Vector3 dockSite = BuildTasks.PlacingSite.ToLocalPosition();
                    GameApi.TryGetLocalHQ(out FactionHQ hq);
                    if (!RtsInput.Shift)
                        foreach (Unit unit in Selection.Selected)
                            if (GameApi.IsEngineer(unit))
                                BuildTasks.ClearFor(unit);
                    BuildTasks.TryPlaceDock(dockSite, Selection.Selected, hq,
                        BuildTasks.PendingYawDeg);
                    if (!RtsInput.Shift)
                        BuildTasks.PendingDock = false;
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
                else if (RtsInput.KeyDown(KeyCode.T)) CycleTerrainFollow();
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

            // Ctrl+M — отладочные деньги (плейтесты стройки/производства без гринда).
            // Хост-only (инвариант №2), сумма — настройка Debug.Funds.
            if (RtsInput.Ctrl && RtsInput.KeyDown(KeyCode.M) &&
                NetworkManagerNuclearOption.i != null &&
                NetworkManagerNuclearOption.i.Server != null &&
                NetworkManagerNuclearOption.i.Server.Active)
            {
                if (GameApi.TryGetLocalHQ(out FactionHQ debugHq))
                {
                    debugHq.AddFunds(Settings.DebugFunds);
                    CommanderHud.Notice($"+{Settings.DebugFunds:F0} — отладка");
                }
            }

            if (RtsInput.KeyDown(KeyCode.H) && Selection.Selected.Count > 0)
            {
                int on = 0;
                foreach (Unit unit in Selection.Selected)
                    if (StanceService.Toggle(unit))
                        on++;
                CommanderHud.Notice(on > 0
                    ? Loc.F("Огонь удерживают: {0}", on)
                    : Loc.Tr("Огонь разрешён"));
            }

            if (Selection.Selected.Count > 0)
            {
                // Alt+Shift+R — Disperse Move (FAF-стиль): очередь вейпоинтов группы
                // рассредоточивается прямыми векторами. Комбо первым в цепи: R без
                // модификаторов ниже заряжает «Распределение».
                if (alt && shiftHeld && RtsInput.KeyDown(KeyCode.R))
                    MoveOrders.IssueDisperseMove(Selection.Selected);
                else if (RtsInput.KeyDown(KeyCode.M)) OrderModes.Pending = OrderMode.Move;
                else if (RtsInput.KeyDown(KeyCode.A)) OrderModes.Pending = OrderMode.AttackMove;
                else if (RtsInput.KeyDown(KeyCode.U)) OrderModes.Pending = OrderMode.Patrol;
                else if (RtsInput.KeyDown(KeyCode.T)) OrderModes.Pending = OrderMode.Attack;
                else if (RtsInput.KeyDown(KeyCode.V)) OrderModes.Pending = OrderMode.Salvo;
                else if (RtsInput.KeyDown(KeyCode.R))
                    MoveOrders.ChargeFanAttack(); // «Распределение»: копим цели ПКМ-ами
                else if (RtsInput.KeyDown(KeyCode.S))
                    MoveOrders.CancelAll(Selection.Selected); // мгновенная отмена
                if (RtsInput.KeyDown(KeyCode.G))
                    MoveOrders.IssueReturnToBase(Selection.Selected); // борта — домой (Shift — RTB в очередь)
                if (RtsInput.KeyDown(KeyCode.X))
                {
                    int popped = DefenseService.PopNow(Selection.Selected);
                    CommanderHud.Notice(popped > 0
                        ? Loc.F("Ловушки: бортов — {0}", popped)
                        : Loc.Tr("В выделении нет бортов с контрмерами"));
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
            CommanderHud.Notice(Loc.F("Профиль высоты: {0} ({1} м{2}){3}", Loc.Tr(p.Name),
                p.AltitudeAgl.ToString("F0"),
                p.FollowTerrain ? Loc.Tr(", огибание") : "",
                applied > 0 ? Loc.F(" — участок сменили {0}", applied) : ""));
        }

        /// <summary>Скоростной режим (Shift+1..3, кнопки): Полный/Крейсер/Эконом.
        /// Самолёты; вертолётам тротл не назначается (газ ведёт игра).</summary>
        private void SetSpeed(int index)
        {
            var modes = new[] { SpeedMode.Full, SpeedMode.Cruise, SpeedMode.Economy };
            var names = new[] { Loc.Tr("Полный"), Loc.Tr("Крейсер"), Loc.Tr("Эконом") };
            AircraftOrders.SpeedIndex = modes[Mathf.Clamp(index, 0, modes.Length - 1)];
            int applied = 0;
            foreach (Unit unit in Selection.Selected)
                if (unit is Aircraft && AircraftOrders.ApplyModesToCurrent(unit))
                    applied++;
            CommanderHud.Notice(Loc.F("Скорость: {0}{1} {2}", names[Mathf.Clamp(index, 0, names.Length - 1)],
                applied > 0 ? Loc.F(" — участок сменили {0}", applied) : "",
                Loc.Tr("(самолёты)")));
        }

        /// <summary>Тоггл «вдоль рельефа» (Alt+T, кнопки): авто (дефолт профиля
        /// высоты: Сверхнизко/Низко огибают, Средне/Высоко — нет) → огибать →
        /// напрямую. Штамп на новые участки + применение к головному сегменту
        /// выделенных бортов. Вертолётам руление огибает рельеф всегда (v0.93.0) —
        /// тоггл реально действует только на самолёты.</summary>
        private void SetTerrainFollow(TerrainHugMode mode)
        {
            AircraftOrders.TerrainHug = mode;
            int applied = 0;
            foreach (Unit unit in Selection.Selected)
                if (unit is Aircraft && AircraftOrders.ApplyModesToCurrent(unit))
                    applied++;
            string name = mode == TerrainHugMode.Always ? Loc.Tr("Рельеф: огибать")
                : mode == TerrainHugMode.Never ? Loc.Tr("Рельеф: напрямую")
                : Loc.Tr("Рельеф: авто");
            CommanderHud.Notice(name +
                                (applied > 0 ? Loc.F(" — участок сменили {0}", applied) : "") +
                                " " + Loc.Tr("(самолёты)"));
        }

        /// <summary>Alt+T — цикл: авто → огибать → напрямую → авто.</summary>
        private void CycleTerrainFollow()
        {
            TerrainHugMode next = AircraftOrders.TerrainHug == TerrainHugMode.Profile
                ? TerrainHugMode.Always
                : AircraftOrders.TerrainHug == TerrainHugMode.Always
                    ? TerrainHugMode.Never
                    : TerrainHugMode.Profile;
            SetTerrainFollow(next);
        }

        /// <summary>Кнопки панели: 0 — авто, 1 — огибать, 2 — напрямую.</summary>
        public void SetTerrainFollowUi(int index) =>
            SetTerrainFollow((TerrainHugMode)Mathf.Clamp(index, 0, 2));

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
                CommanderHud.Notice(Loc.F("Защита: {0}", DefenseName(first)));
        }

        internal static string DefenseName(DefenseMode mode) =>
            mode switch
            {
                DefenseMode.Auto => Loc.Tr("Авто (уклонение + РЭБ/ловушки)"),
                DefenseMode.Flares => Loc.Tr("Ловушки (маршрут держим)"),
                _ => Loc.Tr("Выкл")
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
                CommanderHud.Notice(Loc.Tr("Защита: в выделении нет бортов"));
                return;
            }
            foreach (Unit unit in Selection.Selected)
                DefenseService.Set(unit, mode);
            CommanderHud.Notice(Loc.F("Защита: {0}", DefenseName(mode)));
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
                        CommanderHud.Notice(Loc.F("Группа {0}: назначено {1}", i, Selection.Selected.Count));
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
                CommanderHud.Notice(Loc.Tr("Уничтожение — только на хосте"));
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
                CommanderHud.Notice(Loc.Tr("Ctrl+K: нечего уничтожать (своё выделение/недострой)"));
            else
            {
                CommanderHud.Notice(Loc.F("Уничтожено: {0}{1}", killed,
                    sites > 0 ? Loc.Tr(" + площадка") : ""));
                Log.Info("Ctrl+K: уничтожено юнитов/зданий " + killed + ", площадок " + sites);
            }
        }

        private void Activate()
        {
            CameraStateManager cam = GameApi.CamManager;
            if (cam == null || cam.mainCamera == null)
            {
                CommanderHud.Notice(Loc.Tr("Supreme Nuclear: камера ещё не готова"));
                return;
            }
            if (cam.currentState == cam.cockpitState || cam.currentState == cam.controlledState ||
                cam.currentState == cam.TVState || cam.currentState == cam.chaseState)
            {
                CommanderHud.Notice(Loc.Tr("Supreme Nuclear: сначала выйди из кабины/съёмки, затем включай режим командира"));
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
