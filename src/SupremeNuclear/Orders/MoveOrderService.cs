using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Правый клик = движение выделенных. SetDestination сам решает хост/клиент.</summary>
    public sealed class MoveOrderService
    {
        public readonly struct Marker
        {
            public readonly Vector3 Position;
            public readonly float SpawnedAt;

            public Marker(Vector3 position, float spawnedAt)
            {
                Position = position;
                SpawnedAt = spawnedAt;
            }
        }

        private readonly CommanderModeController _controller;
        public readonly List<Marker> Markers = new List<Marker>();
        public readonly Dictionary<Unit, GlobalPosition> LastDestinations = new Dictionary<Unit, GlobalPosition>();

        /// <summary>Накопленные цели «Атаки веером» (R): ПКМ по врагам копит список,
        /// ПКМ по земле — раздача группе. CommanderHud рисует маркеры, пока режим
        /// заряжен; чистится зарядом и выдачей.</summary>
        public readonly List<Unit> FanTargets = new List<Unit>();

        public MoveOrderService(CommanderModeController controller) => _controller = controller;

        public void Tick()
        {
            // Идёт расстановка постройки/шаблона: ПКМ означает ОТМЕНУ (контроллер),
            // в приказы движение он превращаться не должен.
            if (BuildTasks.Pending != null || OrderTemplates.PendingTemplate.template != null ||
                BuildTasks.RmbEaten)
                return;

            // Ctrl+Shift+ПКМ = ТОЛЬКО удаление вейпоинта (цепочки/планы/площадки/
            // хвосты). Клик комбинацией гасится ВСЕГДА: промах по вейпоинту не
            // должен проваливаться дальше по конвейеру — раньше мимо вейпоинта
            // эта комбинация выставляла НОВЫЙ приказ (ралли/движение/атака).
            if (RtsInput.Ctrl && RtsInput.Shift && RtsInput.RightDown && !RtsInput.PointerOverUi)
            {
                TryDeleteWaypoint();
                return;
            }

            // Ралли проверяется ПЕРВЫМ: выделение аэродрома живёт отдельно от списка
            // юнитов (Selected при этом пуст — ранний return его съедал).
            if (RtsInput.RightDown && !RtsInput.PointerOverUi && TrySetRallyForDepotSelection())
                return;

            if (_controller.Selection.Selected.Count == 0)
            {
                OrderModes.Pending = OrderMode.None;
                return;
            }

            // ПКМ ставит вейпоинт в текущем режиме; Shift удерживает режим (серия),
            // без Shift после точки режим возвращается к дефолтному «Движение».
            if (RtsInput.RightDown && !RtsInput.PointerOverUi)
            {
                if (OrderModes.Pending == OrderMode.Cancel)
                {
                    CancelAll(_controller.Selection.Selected);
                    OrderModes.Pending = OrderMode.Move;
                    return;
                }
                if (OrderModes.Pending == OrderMode.None)
                    OrderModes.Pending = OrderMode.Move; // первый после старта клик = движение

                // ПКМ прямо по врагу — атака (раньше правки вейпоинтов/недостроев:
                // клик по цели не должен съедаться смежными экраными действиями).
                GameApi.TryGetLocalHQ(out FactionHQ localHq);
                Unit enemy = TryPickEnemyAt(localHq);
                if (OrderModes.Pending == OrderMode.DisperseAttack && enemy != null)
                {
                    // «Атака веером»: копим цели (дедуп), режим держим заряженным —
                    // раздача по ПКМ по земле.
                    if (!FanTargets.Contains(enemy))
                        FanTargets.Add(enemy);
                    CommanderHud.Notice(Loc.F("Веер: целей — {0}", FanTargets.Count));
                    return;
                }
                if (enemy != null && IssueAttackOrders(enemy))
                {
                    if (!RtsInput.Shift)
                        OrderModes.Pending = OrderMode.Move;
                    return;
                }

                // Ралли фабрик: выделение только из депо → ПКМ задаёт маршрут вышедшим
                // войскам (Shift — дописать точку), как приказы юнитам.
                if (TrySetRallyForDepotSelection())
                    return;

                // ПКМ на существующем вейпоинте авиаплана — превратить сегмент в патруль.
                if (TryConvertWaypointToPatrol())
                    return;

                // ПКМ по ПРИЗРАКУ/маркеру недостроя — достройка выделенными инженерами
                // (адресное действие по силуэту, не по земле под ним; без Shift —
                // новая очередь, с Shift — дописать).
                if (BuildTasks.TryResume(
                        new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y),
                        GameApi.MainCam, _controller.Selection.Selected, RtsInput.Shift))
                    return;

                // ПКМ по СОЮЗНОМУ мобильному юниту = СОПРОВОЖДЕНИЕ/АССИСТ (v0.97,
                // канон SupCom: ПКМ по своему — assist/escort): боевые следуют за
                // целью и защищают её, инженеры вливаются в её стройки, авиация
                // сопровождает полётом. Только в дефолтном режиме «Движение» —
                // заряженные спец-режимы сохраняют свою семантику.
                if (OrderModes.Pending == OrderMode.Move && TryIssueEscortOrders(localHq))
                    return;

                IssuePending(_controller.Selection.Selected, RtsInput.Shift);
                if (!RtsInput.Shift)
                    OrderModes.Pending = OrderMode.Move;
            }
            // Приказы выдаются ТОЛЬКО по ПКМ — ничего на каждый кадр.
        }

        /// <summary>Удаление вейпоинта под курсором (Ctrl+Shift+ПКМ): цепочки наземки,
        /// планы авиации, площадки стройки (недострой сносится без возврата), хвосты
        /// движений инженеров. Shift расширяет поиск на ВСЕ юниты (режим видимости).
        /// Семантика (скан, групповой фан-аут по groupId) — WaypointOps.DeleteAt.</summary>
        private bool TryDeleteWaypoint()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return false;
            if (!WaypointOps.DeleteAt(_controller, cam,
                    new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y), RtsInput.Shift, out WaypointHit hit))
                return false;
            if (hit.Kind == WaypointKind.BuildSite)
                CommanderHud.Notice(Loc.Tr("Площадка снесена"));
            return true;
        }

        /// <summary>Выделение без мобильных юнитов, но с фабриками (депо/аэродром)?
        /// → ПКМ задаёт ралли-маршрут фабрикам (Shift — дописать точку).</summary>
        private bool TrySetRallyForDepotSelection()
        {
            List<Unit> selected = _controller.Selection.Selected;
            var factories = new List<object>();
            foreach (Unit unit in selected)
            {
                if (unit == null)
                    continue;
                if (unit is VehicleDepot d)
                    factories.Add(d);
                else if (unit is GroundVehicle || unit is Ship || unit is Aircraft)
                    return false; // есть мобильные — обычные приказы
            }
            Airbase airbase = _controller.Selection.SelectedAirbase;
            if (airbase != null)
                factories.Add(airbase);
            if (factories.Count == 0)
                return false;
            Vector3 target = RouteEditor.SnapAt(GameApi.MainCam);
            foreach (object factory in factories)
            {
                if (RtsInput.Shift)
                    ProductionTasks.EnqueueRally(factory, target);
                else
                    ProductionTasks.SetRally(factory, target);
            }
            int points = ProductionTasks.RallyOf(factories[0])?.Count ?? 0;
            CommanderHud.Notice(Loc.F("Ралли фабрики: маршрут из {0} тчк", points));
            return true;
        }

        private int _moveGroupId;

        private void IssuePending(List<Unit> selected, bool queue)
        {
            // Один клик = один групповой вейпоинт — и в Shift-серии, и одиночный:
            // устаревший groupId с прошлой серии связал бы несвязанные точки
            // при групповом драге/удалении.
            _moveGroupId = BuildTasks.NextGroupId();

            GameApi.TryGetLocalHQ(out FactionHQ localHq);
            Vector3 target;
            bool hasTarget = GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 ground);
            if (hasTarget)
                target = RouteEditor.SnapAt(GameApi.MainCam); // снап постановки к юнитам/зданиям, иначе рельеф
            else
                target = ground;

            switch (OrderModes.Pending)
            {
                case OrderMode.Move:
                    if (!hasTarget)
                        return;
                    IssueForTarget(selected, OrderMode.Move, target, queue);
                    Markers.Add(new Marker(target, Time.unscaledTime));
                    break;

                case OrderMode.AttackMove:
                    if (!hasTarget)
                        return;
                    IssueForTarget(selected, OrderMode.AttackMove, target, queue);
                    Markers.Add(new Marker(target, Time.unscaledTime));
                    break;

                case OrderMode.Patrol:
                    if (!hasTarget)
                        return;
                    IssueForTarget(selected, OrderMode.Patrol, target, queue);
                    break;

                case OrderMode.Attack:
                    Unit enemy = TryPickEnemyAt(localHq);
                    if (enemy != null)
                    {
                        IssueAttackOrders(enemy);
                    }
                    else if (hasTarget)
                    {
                        IssueForTarget(selected, OrderMode.AttackMove, target, queue);
                        Markers.Add(new Marker(target, Time.unscaledTime));
                    }
                    break;

                case OrderMode.Salvo:
                    Unit salvoEnemy = TryPickEnemyAt(localHq);
                    if (salvoEnemy != null)
                    {
                        IssueAttackOrders(salvoEnemy); // режим «Залп» по цели
                    }
                    else if (hasTarget)
                    {
                        IssueSalvoArea(selected, target, queue); // залп по площади у якоря
                        Markers.Add(new Marker(target, Time.unscaledTime));
                    }
                    break;

                case OrderMode.DisperseAttack:
                    // сюда доходит только ПКМ по земле (цели съедены веткой Tick выше)
                    if (FanTargets.Count > 0)
                        IssueFanAttack(selected);
                    else if (hasTarget)
                    {
                        // целей не накопили — деградация в атаку-движение, как у «Атаки»
                        IssueForTarget(selected, OrderMode.AttackMove, target, queue);
                        Markers.Add(new Marker(target, Time.unscaledTime));
                    }
                    break;
            }
        }

        /// <summary>Выдача приказа в известную мировую точку без пиков/снапа (миникарта).</summary>
        public void IssueAtWorld(Vector3 target)
        {
            List<Unit> selected = _controller.Selection.Selected;
            if (selected.Count == 0)
                return;
            _moveGroupId = BuildTasks.NextGroupId(); // клик миникарты = свой групповой вейпоинт
            OrderMode mode = OrderModes.Pending;
            if (mode == OrderMode.None)
                mode = OrderMode.Move;
            if (mode == OrderMode.Attack)
                mode = OrderMode.AttackMove; // цели с миникарты не пикингуются — ведём атакой-движением
            if (mode == OrderMode.Salvo)
            {
                // Залп по площади в точку миникарты: авиация пускает по разведанным
                // у якоря, наземка — attack-move (stop-and-fire).
                IssueSalvoArea(selected, target, RtsInput.Shift);
                Markers.Add(new Marker(target, Time.unscaledTime));
                if (!RtsInput.Shift)
                    OrderModes.Pending = OrderMode.Move;
                return;
            }
            IssueForTarget(selected, mode, target, RtsInput.Shift);
            if (mode != OrderMode.Patrol)
                Markers.Add(new Marker(target, Time.unscaledTime));
            if (!RtsInput.Shift)
                OrderModes.Pending = OrderMode.Move;
        }

        /// <summary>Общий путь выдачи: режим + точка + очередь (ПКМ по миру и миникарта).</summary>
        private void IssueForTarget(List<Unit> selected, OrderMode mode, Vector3 target, bool queue)
        {
            switch (mode)
            {
                case OrderMode.Move:
                case OrderMode.AttackMove:
                    // Авиацию выдаёт IssueFormationOrders — ОДИН раз, с формационной
                    // решёткой, режимом клика и groupId (прежняя выдача здесь давала
                    // вторую точку того же клика и Transit вместо AttackMove).
                    // Наземке attack-move идёт с тем же режимом (stop-and-fire).
                    IssueFormationOrders(selected, target, queue,
                        mode == OrderMode.AttackMove ? AirOrderMode.AttackMove : AirOrderMode.Transit,
                        mode == OrderMode.AttackMove);
                    break;

                case OrderMode.Patrol:
                    bool anyAir = false;
                    bool anyGround = false;
                    foreach (Unit unit in selected)
                    {
                        if (unit is Aircraft a)
                        {
                            if (queue) AircraftOrders.Enqueue(a, AirOrderMode.Patrol, target, _moveGroupId);
                            else AircraftOrders.Set(a, AirOrderMode.Patrol, target, _moveGroupId);
                            anyAir = true;
                            continue;
                        }
                        if (!(unit is GroundVehicle || unit is Ship))
                            continue;
                        bool engineer = GameApi.IsEngineer(unit);
                        if (engineer && !(queue &&
                                          GroundOrders.KindOf(unit) == GroundOrderKind.Patrol))
                            BuildTasks.ClearFor(unit); // патруль = новая очередь (Shift дописывает только в цикл)
                        if (queue && GroundOrders.KindOf(unit) == GroundOrderKind.Patrol)
                            GroundOrders.EnqueuePatrol(unit, target.ToGlobalPosition(), _moveGroupId);
                        else
                            GroundOrders.SetPatrol(unit, target.ToGlobalPosition(), _moveGroupId);
                        anyGround = true;
                    }
                    if (!anyAir && !anyGround)
                        CommanderHud.Notice(Loc.Tr("Патруль: в выделении нет мобильных"));
                    break;
            }
        }

        /// <summary>Команда «на базу» (G, DESIGN §2.11): выделенные борта (пачкой или
        /// поодиночке, самолёты и вертолёты) — терминальный RTB к ближайшему подходящему
        /// своему аэродрому: посадка, обслуживание и паркинг до следующего приказа.
        /// Shift — RTB дописывается в очередь (EnqueueRtb): борт вернётся домой
        /// после отработки текущего плана.</summary>
        public void IssueReturnToBase(List<Unit> selected)
        {
            bool queue = RtsInput.Shift;
            int n = 0;
            foreach (Unit unit in selected)
            {
                if (!(unit is Aircraft aircraft))
                    continue;
                Airbase airbase = SortieService.NearestOwnAirbase(aircraft);
                if (airbase == null)
                    continue;
                if (queue)
                    AircraftOrders.EnqueueRtb(aircraft, airbase);
                else
                    AircraftOrders.ReturnToBase(aircraft, airbase);
                n++;
            }
            CommanderHud.Notice(n > 0
                ? queue
                    ? Loc.F("На базу (в очередь): бортов — {0}", n)
                    : Loc.F("На базу: бортов — {0}", n)
                : Loc.Tr("В выделении нет бортов или нет своего аэродрома"));
        }

        /// <summary>Мгновенная отмена всех приказов выделенных.</summary>
        public void CancelAll(List<Unit> selected)
        {
            foreach (Unit unit in selected)
            {
                if (unit == null)
                    continue; // уничтоженный между кадрами — не трогаем (MissingReference)
                if (unit is Aircraft a)
                {
                    AircraftOrders.Cancel(a);
                    continue;
                }
                if (unit is VehicleDepot depot)
                {
                    ProductionTasks.ClearRally(depot);
                    continue;
                }
                if (unit == null)
                    continue;
                if (GameApi.IsEngineer(unit))
                    BuildTasks.ClearFor(unit);
                UnitCommand command = unit.GetComponent<UnitCommand>();
                if (command != null)
                {
                    Vector3 p = unit.transform.position;
                    p.y = GameApi.GroundY(p.x, p.z);
                    command.SetDestination(p.ToGlobalPosition(), playerCommand: true);
                }
                GroundOrders.Clear(unit);
                LastDestinations.Remove(unit);
            }
            if (_controller.Selection.SelectedAirbase != null)
                ProductionTasks.ClearRally(_controller.Selection.SelectedAirbase);
            CommanderHud.Notice(Loc.Tr("Приказы отменены"));
        }

        /// <summary>ПКМ по вейпоинту: сегмент плана борта становится патрульным
        /// циклом, цепочка наземки/флота (≥2 точки) замыкается в цикл. Поиск и
        /// гард — WaypointOps.ConvertToPatrolAt (единый скан: земля-проекция,
        /// служебные точки не отдаются); конвертация пер-бортовая.</summary>
        private bool TryConvertWaypointToPatrol()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return false;
            if (!WaypointOps.ConvertToPatrolAt(_controller, cam,
                    new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y), RtsInput.Shift,
                    out WaypointHit hit))
                return false;
            CommanderHud.Notice(hit.Kind == WaypointKind.GroundPoint
                ? Loc.Tr("Маршрут замкнут в патруль")
                : Loc.Tr("Сегмент превращён в патруль"));
            return true;
        }

        // Наземные атакующие текущего клика: сортировка по азимуту для полукруга.
        private readonly List<Unit> _attackFan = new List<Unit>();

        // Сопровождающие текущего клика (веер секторов вокруг цели) — наземка и авиация.
        private readonly List<Unit> _escortFan = new List<Unit>();

        /// <summary>Сопровождение/ассист союзного юнита (v0.97): боевые наземка/флот —
        /// следование своим сектором веера + защита stop-and-fire; инженеры — следование
        /// и вливание в стройки ЦЕЛИ (эскорт-ассист BuildTasks, без Shift — новая связь);
        /// авиация — эскорт-сегмент плана (Shift дописывает в план, наземке Shift не
        /// кладёт сопровождение в очередь — цепочка одна, Notice). Цель сама себе
        /// сопроводителем быть не может.</summary>
        private bool TryIssueEscortOrders(FactionHQ localHq)
        {
            Unit friend = TryPickFriendlyAt(localHq);
            if (friend == null)
                return false;
            Vector3 fp = friend.transform.position;

            _escortFan.Clear();
            int airCount = 0, groundCount = 0, engCount = 0;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || unit == friend)
                    continue; // сам себя не сопровождает
                if (unit is Aircraft)
                {
                    _escortFan.Add(unit);
                    airCount++;
                    continue;
                }
                if (unit is GroundVehicle || unit is Ship)
                {
                    _escortFan.Add(unit);
                    if (GameApi.IsEngineer(unit))
                        engCount++;
                    else
                        groundCount++;
                }
            }
            if (_escortFan.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Сопровождение: цель — всё выделение"));
                return true; // клик съеден: приказов движению по самому себе не выдаём
            }

            // Сортировка по азимутам вокруг цели: соседние по кругу получают соседние
            // сектора веера — эскорты распределяются вокруг цели, а не в кучу (как
            // полукруг групповой атаки).
            _escortFan.Sort((a, b) => Mathf.Atan2(a.transform.position.x - fp.x,
                    a.transform.position.z - fp.z)
                .CompareTo(Mathf.Atan2(b.transform.position.x - fp.x,
                    b.transform.position.z - fp.z)));
            float groundStandoff = Settings.EscortStandoffM + friend.maxRadius + 10f;
            float airStep = Settings.FormationSpacing * 4f;
            int issued = 0, engNotQueued = 0;
            for (int i = 0; i < _escortFan.Count; i++)
            {
                Unit unit = _escortFan[i];
                if (unit is Aircraft aircraft)
                {
                    float bearing = i * airStep / Settings.EscortAirDistM;
                    if (RtsInput.Shift)
                        AircraftOrders.EnqueueEscort(aircraft, friend, bearing);
                    else
                        AircraftOrders.SetEscort(aircraft, friend, bearing);
                    issued++;
                    continue;
                }
                if (RtsInput.Shift)
                {
                    // Наземная цепочка одна: сопровождение — стоящий приказ, в очередь
                    // точек не кладётся (долг §12-36). Инженеру Shift ломать очередь
                    // молча тоже нельзя — пропускаем с объяснением.
                    if (GameApi.IsEngineer(unit))
                        engNotQueued++;
                    continue;
                }
                if (GameApi.IsEngineer(unit))
                    BuildTasks.ClearFor(unit); // новая связь = новая очередь (как патруль)
                Vector3 up = unit.transform.position;
                float bearingFull = Mathf.Atan2(up.x - fp.x, up.z - fp.z) +
                    GroundOrders.FanBearingOffset(i, _escortFan.Count,
                        Settings.FormationSpacing, groundStandoff);
                GroundOrders.SetEscort(unit, friend, bearingFull);
                issued++;
            }
            if (issued > 0)
                CommanderHud.Notice(Loc.F("Сопровождение: {0} → {1}", issued,
                    friend.definition != null ? friend.definition.unitName : "?"));
            if (engNotQueued > 0)
                CommanderHud.Notice(Loc.Tr("Сопровождение не ставится в очередь — выдай без Shift"));
            return true;
        }

        /// <summary>Союзный мобильный юнит под курсором — экранный пик (свои видны
        /// всегда, даталинк не нужен). Зеркало TryPickEnemyAt: движущаяся техника,
        /// флот и авиация (здания статичны — сопровождать нечего).</summary>
        private Unit TryPickFriendlyAt(FactionHQ localHq)
        {
            float radiusPx = Settings.EnemyPickRadiusPx;
            Camera cam = GameApi.MainCam;
            if (cam == null || localHq == null)
                return null;
            Unit best = null;
            float bestDist = float.MaxValue;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ != localHq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                float limit = Mathf.Max(radiusPx, GameApi.ApparentSizePx(unit, cam) * 0.5f);
                if (d < limit && d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>Атака/залп выделенных по указанной цели (DESIGN §2.11): авиация —
        /// приказ с пиннингом цели (Shift — дописать в план серией), наземка/флот —
        /// преследование с подходом на дальность вооружения ПОЛУКРУГОМ: каждый
        /// атакующий держит свой азимут-сектор вокруг цели (веер по порядку
        /// азимутов — маршруты не пересекаются, левый фланг остаётся слева), шаг
        /// дуги — FormationSpacing на радиусе подхода юнита. В режиме «Залп»
        /// авиация получает залп-приказ (Shift — дописать цель в ЗАЛП, не в
        /// очередь). Якорь — точка клика: к ней борт летит, пока даталинк не
        /// держит цель.</summary>
        private bool IssueAttackOrders(Unit enemy)
        {
            bool any = false;
            bool salvo = OrderModes.Pending == OrderMode.Salvo;
            bool hasGround = GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 anchor);
            if (!hasGround)
                anchor = enemy.transform.position;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit is Aircraft aircraft)
                {
                    if (salvo)
                    {
                        if (RtsInput.Shift)
                            AircraftOrders.EnqueueSalvoTarget(aircraft, enemy, anchor);
                        else
                            AircraftOrders.Salvo(aircraft, enemy, anchor);
                    }
                    else if (RtsInput.Shift)
                        AircraftOrders.EnqueueAttack(aircraft, enemy, anchor);
                    else
                        AircraftOrders.Attack(aircraft, enemy, anchor);
                    any = true;
                }
                else if ((unit is GroundVehicle || unit is Ship) && !GameApi.IsEngineer(unit))
                {
                    _attackFan.Add(unit); // сектора разносятся вторым проходом
                    any = true;
                }
            }
            if (_attackFan.Count > 0)
            {
                // Сортировка по азимуту вокруг цели: соседние по кругу получают
                // соседние сектора — подход разворачивается фронтом/полукольцом,
                // а не гуськом по одной линии.
                Vector3 ep = enemy.transform.position;
                _attackFan.Sort((a, b) => Mathf.Atan2(a.transform.position.x - ep.x,
                        a.transform.position.z - ep.z)
                    .CompareTo(Mathf.Atan2(b.transform.position.x - ep.x,
                        b.transform.position.z - ep.z)));
                for (int i = 0; i < _attackFan.Count; i++)
                {
                    Unit unit = _attackFan[i];
                    Vector3 up = unit.transform.position;
                    float bearing = Mathf.Atan2(up.x - ep.x, up.z - ep.z) +
                        GroundOrders.FanBearingOffset(i, _attackFan.Count,
                            Settings.FormationSpacing, GroundOrders.PursueRange(unit, enemy));
                    // Shift — дописать цель в ОЧЕРЕДЬ атак (серия), без Shift — новая
                    // атака (перезапись). Инженеры не воюют. Вейпоинт атаки — крест
                    // НА ЦЕЛИ (один на цель), точки подхода не рисуются.
                    if (RtsInput.Shift)
                        GroundOrders.EnqueueAttack(unit, enemy, bearing);
                    else
                        GroundOrders.SetAttack(unit, enemy, bearing);
                }
                _attackFan.Clear();
            }
            if (any)
            {
                CommanderHud.Notice(salvo
                    ? Loc.F("Залп: {0}", enemy.definition.unitName)
                    : Loc.F("Атака: {0}", enemy.definition.unitName));
                // Даталинк-гейт (DESIGN §2.11): цель вне трекинга фракции не пиннится —
                // огонь откроется только после захвата сенсорами по пути. Говорим об этом
                // сразу, иначе «летит и молчит» читается как игнор приказа.
                if (GameApi.TryGetLocalHQ(out FactionHQ hq) &&
                    !hq.TryGetKnownPosition(enemy, out _))
                    CommanderHud.Notice(Loc.Tr("Цель вне разведки: ведём на позицию, огонь — после захвата сенсорами"));
            }
            return any;
        }

        /// <summary>Заряд «Атаки веером» (R / кнопка): список целей копится с нуля.</summary>
        public void ChargeFanAttack()
        {
            FanTargets.Clear();
            OrderModes.Pending = OrderMode.DisperseAttack;
        }

        /// <summary>Раздача «веера»: каждая цель получает ёмкость N/M (+1 первым по
        /// порядку клика), каждый атакующий — БЛИЖАЙШУЮ цель со свободной ёмкостью
        /// (Util.AssignBalanced). Авиация — атака-вылет, наземка/флот — преследование;
        /// инженеры не воюют. Якорь борта — позиция его цели.</summary>
        private void IssueFanAttack(List<Unit> selected)
        {
            for (int i = FanTargets.Count - 1; i >= 0; i--)
                if (FanTargets[i] == null || FanTargets[i].disabled) // Unity fake-null
                    FanTargets.RemoveAt(i);
            if (FanTargets.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Веер: цели потеряны"));
                return;
            }

            var attackers = new List<Unit>();
            foreach (Unit unit in selected)
                if (unit != null && !unit.disabled &&
                    (unit is Aircraft ||
                     ((unit is GroundVehicle || unit is Ship) && !GameApi.IsEngineer(unit))))
                    attackers.Add(unit);
            if (attackers.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Веер: в выделении нет атакующих"));
                FanTargets.Clear();
                return;
            }

            var unitPos = new List<Vector3>(attackers.Count);
            var targetPos = new List<Vector3>(FanTargets.Count);
            foreach (Unit unit in attackers)
                unitPos.Add(unit.transform.position);
            foreach (Unit target in FanTargets)
                targetPos.Add(target.transform.position);
            int[] assigned = Util.AssignBalanced(unitPos, targetPos);

            for (int i = 0; i < attackers.Count; i++)
            {
                Unit target = FanTargets[assigned[i]];
                if (attackers[i] is Aircraft aircraft)
                    AircraftOrders.Attack(aircraft, target, target.transform.position);
                else
                    GroundOrders.SetAttack(attackers[i], target);
            }
            CommanderHud.Notice(Loc.F("Веер: {0} → {1} {2}", attackers.Count, FanTargets.Count,
                Loc.Plural(FanTargets.Count, "цель", "цели", "целей")));
            FanTargets.Clear();
        }

        /// <summary>Disperse Move (Alt+Shift+R, FAF-стиль): очередь групповых
        /// вейпоинтов выделенных распределяется — юнит i получает группу точек
        /// i mod G (порядок кликов = возрастание groupId), у каждого остаётся ОДНА
        /// точка — прямой вектор вместо общего маршрута. Наземка/флот и авиация
        /// распределяются независимо; инженеры (общая очередь стройки) не участвуют.</summary>
        public void IssueDisperseMove(List<Unit> selected)
        {
            var ground = new List<Unit>();
            var air = new List<Unit>();
            foreach (Unit unit in selected)
            {
                if (unit == null || unit.disabled)
                    continue;
                if (unit is Aircraft)
                    air.Add(unit);
                else if ((unit is GroundVehicle || unit is Ship) && !GameApi.IsEngineer(unit))
                    ground.Add(unit);
            }

            var groups = new List<int>();
            int dispersed = 0, vectors = 0;

            CollectGroundGroups(ground, groups);
            if (groups.Count > 0)
            {
                vectors += groups.Count;
                for (int i = 0; i < ground.Count; i++)
                    if (GroundOrders.DisperseKeep(ground[i], groups[i % groups.Count]))
                        dispersed++;
            }

            CollectAirGroups(air, groups);
            if (groups.Count > 0)
            {
                vectors += groups.Count;
                for (int i = 0; i < air.Count; i++)
                    if (AircraftOrders.DisperseKeep(air[i], groups[i % groups.Count]))
                        dispersed++;
            }

            CommanderHud.Notice(dispersed > 0
                ? Loc.F("Распределение: {0} {1} на {2} {3}", dispersed,
                      Loc.Plural(dispersed, "юнит", "юнита", "юнитов"), vectors,
                      Loc.Plural(vectors, "вектор", "вектора", "векторов"))
                : Loc.Tr("Нет очереди вейпоинтов"));
        }

        /// <summary>Групповые клики в ОСТАВШИХСЯ очередях наземки (groupId по возрастанию
        /// = порядок кликов); 0 (индивидуальные) не участвуют.</summary>
        private static void CollectGroundGroups(List<Unit> units, List<int> groups)
        {
            groups.Clear();
            foreach (Unit unit in units)
            {
                if (!GroundOrders.TryGetQueue(unit, out var points, out int index))
                    continue;
                for (int i = index; i < points.Count; i++)
                {
                    int g = GroundOrders.GroupIdOf(unit, i);
                    if (g != 0 && !groups.Contains(g))
                        groups.Add(g);
                }
            }
            groups.Sort();
        }

        /// <summary>Групповые клики в транспортных сегментах планов авиации
        /// (в головном — только от Index); groupId по возрастанию = порядок кликов.</summary>
        private static void CollectAirGroups(List<Unit> units, List<int> groups)
        {
            groups.Clear();
            foreach (Unit unit in units)
            {
                if (!AircraftOrders.TryGetPlan(unit, out var plan))
                    continue;
                for (int s = 0; s < plan.Count; s++)
                {
                    AircraftOrders.OrderSegment seg = plan[s];
                    if (seg.Mode != AirOrderMode.Transit && seg.Mode != AirOrderMode.AttackMove)
                        continue;
                    int from = s == 0 ? seg.Index : 0;
                    for (int p = from; p < seg.PointGroups.Count; p++)
                    {
                        int g = seg.PointGroups[p];
                        if (g != 0 && !groups.Contains(g))
                            groups.Add(g);
                    }
                }
            }
            groups.Sort();
        }

        /// <summary>Залп по площади (режим «Залп», ПКМ по земле): авиация — якорь
        /// залпа (у якоря борт пускает всё выбранное по разведанным целям, формационная
        /// решётка как у движения), наземка/флот — attack-move (stop-and-fire). У борта
        /// без ракетных станций (бомбовоз) залп деградирует в атаку-движение.</summary>
        private void IssueSalvoArea(List<Unit> selected, Vector3 target, bool queue)
        {
            int cols = Mathf.CeilToInt(Mathf.Sqrt(selected.Count));
            float groundY = GameApi.GroundY(target.x, target.z);
            int aircraft = 0, ground = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                Unit unit = selected[i];
                if (unit == null)
                    continue;
                if (unit is Aircraft a)
                {
                    aircraft++;
                    if (!HasMissileStation(a))
                    {
                        // Бомбовозу/невооружённому залп нечем — ведём атакой-движением.
                        if (queue)
                            AircraftOrders.Enqueue(a, AirOrderMode.AttackMove, target, _moveGroupId);
                        else
                            AircraftOrders.Set(a, AirOrderMode.AttackMove, target, _moveGroupId);
                        continue;
                    }
                    float airSpacing = Settings.FormationSpacing * 8f;
                    int row = i / cols;
                    int col = i % cols - cols / 2;
                    Vector3 airTarget = target + new Vector3(col * airSpacing, 0f, -row * airSpacing);
                    AircraftOrders.SalvoArea(a, airTarget, _moveGroupId, replace: !queue);
                    continue;
                }

                UnitCommand command = unit.GetComponent<UnitCommand>();
                if (command == null)
                    continue;
                ground++;
                int grow = i / cols;
                int gcol = i % cols - cols / 2;
                Vector3 pos = target + new Vector3(gcol * Settings.FormationSpacing, 0f, -grow * Settings.FormationSpacing);
                pos.y = groundY;
                if (queue)
                    GroundOrders.Enqueue(unit, pos.ToGlobalPosition(), true, _moveGroupId);
                else
                    GroundOrders.Set(unit, pos.ToGlobalPosition(), true, _moveGroupId);
                LastDestinations[unit] = pos.ToGlobalPosition();
            }
            if (aircraft > 0 || ground > 0)
                CommanderHud.Notice(Loc.F("Залп по площади: бортов {0}, наземки {1}", aircraft, ground));
        }

        /// <summary>Есть ли у борта ракетная станция с боеприпасами (залп чем пускать).</summary>
        private static bool HasMissileStation(Aircraft aircraft)
        {
            foreach (WeaponStation st in aircraft.weaponStations)
                if (st != null && !st.Cargo && st.WeaponInfo != null && st.WeaponInfo.missile)
                    return true;
            return false;
        }

        /// <summary>Враг под курсором — экранный пик. КРИТИЧНО: вражеская иконка на
        /// стратегическом слое рисуется по позиции КОНТАКТА РАЗВЕДКИ (CommanderHud.
        /// DrawHostileIcons), которая у движущейся цели отстаёт от фактической на
        /// десятки метров — пик обязан принимать ОБЕ точки, иначе клик по видимой
        /// красной иконке промахивается и превращается в приказ движения. Радиус —
        /// max(базовых px, половины видимого корпуса); из кандидатов ближайший к курсору.</summary>
        private Unit TryPickEnemyAt(FactionHQ localHq, float radiusPx = 0f)
        {
            if (radiusPx <= 0f)
                radiusPx = Settings.EnemyPickRadiusPx;
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return null;
            Unit best = null;
            float bestDist = float.MaxValue;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ == localHq)
                    continue;
                if (!IsAttackable(unit))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                // Позиция контакта — там, где игрок ВИДИТ цель (иконка); якорь тот же,
                // что у отрисовки: трекинг-точка + 0.7×радиуса вверх.
                TrackingInfo info = localHq.GetTrackingData(unit.persistentID);
                if (info != null)
                {
                    Vector3 contact = info.GetPosition().ToLocalPosition() +
                                      Vector3.up * (unit.maxRadius * 0.7f);
                    Vector3 cp = cam.WorldToScreenPoint(contact);
                    if (cp.z > 0f)
                    {
                        float dc = Vector2.Distance(new Vector2(cp.x, cp.y), mouse);
                        if (dc < d)
                            d = dc;
                    }
                }
                // Ракеты — мелкие и быстрые (перехват): радиус притягивания шире,
                // чтобы навестись на летящую цель было реально.
                float limit = unit is Missile ? radiusPx * 2.5f
                    : Mathf.Max(radiusPx, GameApi.ApparentSizePx(unit, cam) * 0.5f);
                if (d < limit && d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            return best;
        }

        /// <summary>Пикингуемая цель: техника, авиация, флот, здания и РАКЕТЫ
        /// (перехват) вражеской фракции (обломки/нейтральные — нет).</summary>
        private static bool IsAttackable(Unit unit) =>
            unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building ||
            unit is Missile;

        private void IssueFormationOrders(List<Unit> units, Vector3 target, bool queue,
            AirOrderMode airMode = AirOrderMode.Transit, bool attackMove = false)
        {
            int n = units.Count;
            int cols = Mathf.CeilToInt(Mathf.Sqrt(n));
            float groundY = GameApi.GroundY(target.x, target.z);

            for (int i = 0; i < n; i++)
            {
                Unit unit = units[i];
                if (unit == null)
                    continue;

                if (unit is Aircraft)
                {
                    // Авиация: широкая решётка вокруг точки, высоту и руление берёт на себя AutoAim.
                    float airSpacing = Settings.FormationSpacing * 8f;
                    int row = i / cols;
                    int col = i % cols - cols / 2;
                    Vector3 airTarget = target + new Vector3(col * airSpacing, 0f, -row * airSpacing);
                    if (queue)
                        AircraftOrders.Enqueue(unit, airMode, airTarget, _moveGroupId);
                    else
                        AircraftOrders.Set(unit, airMode, airTarget, _moveGroupId);
                    continue;
                }

                // Инженеры: единая очередь SupCom.
                if (GameApi.IsEngineer(unit))
                {
                    if (queue)
                    {
                        // Shift: движение дописывается В ОБЩУЮ ОЧЕРЕДЬ инженера
                        // (порядок кликов), если очередь уже есть; иначе — обычная цепочка.
                        // Всем инженерам клика — один groupId: вейпоинт ГРУППОВОЙ.
                        if (BuildTasks.HasActiveTaskPublic(unit) || BuildTasks.HasSequence(unit))
                            BuildTasks.AppendMove(unit, target.ToGlobalPosition(), _moveGroupId);
                        else
                            GroundOrders.Enqueue(unit, target.ToGlobalPosition(), false, _moveGroupId);
                        LastDestinations[unit] = target.ToGlobalPosition();
                        continue;
                    }
                    // Без Shift: НОВАЯ очередь — вся предыдущая (площадки + движения) ломается.
                    BuildTasks.ClearFor(unit);
                }

                UnitCommand command = unit.GetComponent<UnitCommand>();
                if (command == null)
                    continue;
                int grow = i / cols;
                int gcol = i % cols - cols / 2;
                Vector3 pos = target + new Vector3(gcol * Settings.FormationSpacing, 0f, -grow * Settings.FormationSpacing);
                pos.y = groundY;
                if (queue)
                    GroundOrders.Enqueue(unit, pos.ToGlobalPosition(), attackMove, _moveGroupId);
                else
                    GroundOrders.Set(unit, pos.ToGlobalPosition(), attackMove, _moveGroupId);
                LastDestinations[unit] = pos.ToGlobalPosition();
            }
        }

        public void PruneMarkers(float now, float lifetime = 2.5f)
        {
            for (int i = Markers.Count - 1; i >= 0; i--)
            {
                if (now - Markers[i].SpawnedAt > lifetime)
                    Markers.RemoveAt(i);
            }
            List<Unit> dead = null;
            foreach (var kv in LastDestinations)
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead != null)
                foreach (Unit d in dead)
                    LastDestinations.Remove(d);
        }
    }
}
