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

        /// <summary>Накопленные цели «Распределения» (R): ПКМ по врагам копит список,
        /// ПКМ по земле — раздача группе. CommanderHud рисует маркеры, пока режим
        /// заряжен; чистится зарядом и выдачей.</summary>
        public readonly List<Unit> FanTargets = new List<Unit>();
        // Скоординированная атака: участники и их ETA (переиспользуемые буферы).
        private static readonly List<Unit> CoordUnits = new List<Unit>();
        private static readonly List<float> CoordEtas = new List<float>();

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

            // Ралли проверяется ПЕРВЫМ (ПКМ, только БЕЗ заряженного режима — у заряда
            // ПКМ = отмена): выделение аэродрома живёт отдельно от списка юнитов
            // (Selected при этом пуст — ранний return его съедал).
            if (OrderModes.Pending == OrderMode.None &&
                RtsInput.RightDown && !RtsInput.PointerOverUi && TrySetRallyForFactorySelection())
                return;

            if (_controller.Selection.Selected.Count == 0)
            {
                OrderModes.Pending = OrderMode.None;
                return;
            }

            // === ЗАРЯЖЕННЫЙ РЕЖИМ (хоткей/панель; SupCom-схема, v0.114): ЛКМ выдаёт
            // приказ (Shift — очередь), режим ОСТАЁТСЯ заряженным — можно накликать
            // серию точек/целей («раскидал»); ПКМ гасит заряд БЕЗ выдачи. Дефолтные
            // контекстные действия (эскорт/посадка/ралли/достройка/конвертация
            // вейпоинта) в заряде не работают — только в дефолте по ПКМ. ===
            if (OrderModes.Pending != OrderMode.None)
            {
                if (RtsInput.RightDown && !RtsInput.PointerOverUi)
                {
                    OrderModes.Pending = OrderMode.None; // ПКМ — отмена заряда
                    FanTargets.Clear();
                    return;
                }
                // RouteEditor.Consumed: клик УШЁЛ в захват вейпоинта (в «Залпе» —
                // точка обстрела: тянем её, а не выдаём новый залп в это место)
                if (RtsInput.LeftDown && !RtsInput.PointerOverUi && !RouteEditor.Consumed)
                {
                    if (OrderModes.Pending == OrderMode.Cancel)
                    {
                        CancelAll(_controller.Selection.Selected);
                        if (!RtsInput.Shift)
                            OrderModes.Pending = OrderMode.None; // стоп — разовый приказ
                        return;
                    }
                    GameApi.TryGetLocalHQ(out FactionHQ chargeHq);
                    Unit chargeEnemy = TryPickEnemyAt(chargeHq);
                    if (OrderModes.Pending == OrderMode.DisperseAttack && chargeEnemy != null)
                    {
                        // «Распределение»: копим цели (дедуп), заряд держим —
                        // раздача ЛКМ по земле.
                        if (!FanTargets.Contains(chargeEnemy))
                            FanTargets.Add(chargeEnemy);
                        CommanderHud.Notice(Loc.F("Распределение: целей — {0}", FanTargets.Count));
                        return;
                    }
                    IssuePending(_controller.Selection.Selected, RtsInput.Shift);
                    // Канон SupCom: обычный клик — ОДИН приказ и выход в дефолт
                    // (ЛКМ — выделение, ПКМ — контекст); Shift удерживает заряд
                    // серией. Многоточечные по природе держат заряд: ПАТРУЛЬ (точки
                    // дописываются в цикл) и ЗАЛП (SupCom-канон флота: серия кликов
                    // дозирует расход по точкам/волнам) — выход ПКМ/Esc.
                    if (!RtsInput.Shift && OrderModes.Pending != OrderMode.Patrol &&
                        OrderModes.Pending != OrderMode.Salvo)
                        OrderModes.Pending = OrderMode.None;
                }
                return;
            }

            // === ДЕФОЛТ (заряда нет): ПКМ — контекстные приказы, Pending==None
            // трактуется как «Движение». ===
            if (RtsInput.RightDown && !RtsInput.PointerOverUi)
            {
                // ПКМ по кресту серии атак — СКООРДИНИРОВАННАЯ АТАКА (канон FAF:
                // все бьют одновременно); раньше пика врага — крест и враг стоят
                // в одной точке, разбор по пик-радиусу маркера приказа.
                if (TryCoordinatedStrike())
                    return;

                // ПКМ прямо по врагу — атака (раньше правки вейпоинтов/недостроев:
                // клик по цели не должен съедаться смежными экраными действиями).
                GameApi.TryGetLocalHQ(out FactionHQ localHq);
                Unit enemy = TryPickEnemyAt(localHq);
                if (enemy != null && IssueAttackOrders(enemy))
                    return;

                // Ралли фабрик: выделение только из фабрик → ПКМ задаёт маршрут вышедшим
                // войскам (Shift — дописать точку), как приказы юнитам.
                if (TrySetRallyForFactorySelection())
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

                // ПКМ по РАНЕНОЙ своей технике/зданию выделенными инженерами — РЕМОНТ
                // (v0.149, канон SupCom: 4-я функция сьюта; полный HP — эскорт ниже).
                if (TryIssueRepairOrders(localHq))
                    return;

                // ПКМ по обломку выделенными инженерами — адресный сбор (v0.149,
                // долг §12-46г): точка очереди у обломка, дальше работает автосбор.
                if (TryIssueSalvageOrder())
                    return;

                // ПКМ по СОЮЗНОМУ мобильному юниту = СОПРОВОЖДЕНИЕ/АССИСТ (v0.97,
                // канон SupCom: ПКМ по своему — assist/escort): боевые следуют за
                // целью и защищают её, инженеры вливаются в её стройки, авиация
                // сопровождает полётом.
                if (TryIssueEscortOrders(localHq))
                    return;

                // ПКМ по ПОЛОТНУ своей ВПП = посадка НА ЭТУ базу (v0.107).
                if (TryIssueLandingOrders(localHq))
                    return;

                IssuePending(_controller.Selection.Selected, RtsInput.Shift);
            }
            // Приказы выдаются ТОЛЬКО по событиям ввода — ничего на каждый кадр.
        }

        /// <summary>Скоординированная атака (канон FAF, ПКМ по кресту цели): все
        /// ВЫДЕЛЕННЫЕ наземные/флот юниты с этой целью (текущая погоня или очередь
        /// серии) бьют ОДНОВРЕМЕННО — время залпа = самое позднее ETA + 0.5 с;
        /// прибывшие раньше держат огонь до него (GroundOrders.CoordStrike +
        /// StanceService-оверлей, снимается сам). true — команда принята.</summary>
        private bool TryCoordinatedStrike()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return false;
            WaypointHit hit = WaypointScanner.Scan(_controller, cam,
                new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y),
                WaypointScanner.PickPx, false);
            if (hit.Kind != WaypointKind.AttackTarget)
                return false;
            var targets = GroundOrders.TargetQueueOf(hit.Owner);
            if (targets == null || hit.Index >= targets.Count || targets[hit.Index] == null)
                return false;
            Unit goal = targets[hit.Index];
            CoordUnits.Clear();
            CoordEtas.Clear();
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || unit.disabled || !(unit is GroundVehicle || unit is Ship))
                    continue;
                bool has = GroundOrders.TargetOf(unit) == goal;
                if (!has)
                {
                    var queue = GroundOrders.TargetQueueOf(unit);
                    if (queue != null)
                        for (int i = 0; i < queue.Count; i++)
                            if (queue[i] == goal)
                            {
                                has = true;
                                break;
                            }
                }
                if (!has)
                    continue;
                Vector3 d = goal.transform.position - unit.transform.position;
                float eta = new Vector2(d.x, d.z).magnitude / CoordSpeedOf(unit);
                CoordUnits.Add(unit);
                CoordEtas.Add(eta);
            }
            if (CoordUnits.Count == 0)
                return false;
            float volleyAt = Time.time + 0.5f;
            for (int i = 0; i < CoordEtas.Count; i++)
                volleyAt = Mathf.Max(volleyAt, Time.time + CoordEtas[i]);
            for (int i = 0; i < CoordUnits.Count; i++)
                GroundOrders.CoordStrike[CoordUnits[i]] = (goal, volleyAt);
            CommanderHud.Notice(Loc.F("Скоординированная атака: юнитов — {0}, одновременный удар через ~{1:0} с",
                CoordUnits.Count, volleyAt - Time.time));
            return true;
        }

        /// <summary>Оценка скорости юнита для ETA координации: текущая (в движении)
        /// либо типовая походная 10 м/с (стоящий разгонится — ETA с запасом вверх).</summary>
        private static float CoordSpeedOf(Unit unit)
        {
            Vector3 v = unit.GetComponent<Rigidbody>() != null
                ? unit.GetComponent<Rigidbody>().velocity
                : Vector3.zero;
            return Mathf.Max(new Vector2(v.x, v.z).magnitude, 10f);
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
            else if (hit.Kind == WaypointKind.RallyPoint)
                CommanderHud.Notice(Loc.Tr("Точка ралли снята"));
            return true;
        }

        /// <summary>Выделение без мобильных юнитов, но с фабриками (депо/ангар/верфь/
        /// аэродром)? → ПКМ задаёт ралли-маршрут ВСЕМ фабрикам выделения (Shift —
        /// дописать точку) — общий маршрут войскам пачки фабрик, как приказы юнитам.</summary>
        private bool TrySetRallyForFactorySelection()
        {
            List<Unit> selected = _controller.Selection.Selected;
            var factories = new List<object>();
            // Ctrl+ПКМ — ралли ДЛЯ ФАБРИК даже при «мобильной» природе: авианосец —
            // и корабль, и аэродром; обычный ПКМ двигает КОРАБЛЬ (приказ), Ctrl+ПКМ
            // задаёт ралли его авиакрылу (депо/верфь/ангар в выделении — тоже).
            // Прочая мобильная техника в выделении сохраняет veto — смешанное
            // боевое выделение живёт обычными приказами. Ctrl+Shift — удаление
            // вейпоинтов (гейт в Tick выше), поэтому ралли по Ctrl только ЗАМЕНЯЕТ
            // маршрут (дописать точку — ПКМ по базе без корабля в выделении).
            bool rallyMode = RtsInput.Ctrl && !RtsInput.Shift;
            foreach (Unit unit in selected)
            {
                if (unit == null)
                    continue;
                if (unit is VehicleDepot depot)
                {
                    factories.Add(depot);
                    continue;
                }
                Hangar hangar = unit is Building b ? b.GetComponent<Hangar>() : null;
                if (hangar != null)
                {
                    factories.Add(hangar);
                    continue;
                }
                if (BuildTasks.IsDock(unit))
                {
                    factories.Add(unit);
                    continue;
                }
                if (rallyMode && unit is Ship ship)
                {
                    // корабль-аэродром (CV/LHA): ралли получает его Airbase —
                    // ангары корпуса наследуют (ProductionTasks.RouteOf)
                    Airbase shipBase = ship.GetAirbase();
                    if (shipBase != null)
                        factories.Add(shipBase);
                    continue;
                }
                if (unit is GroundVehicle || unit is Ship || unit is Aircraft)
                    return false; // есть мобильные — обычные приказы
            }
            Airbase airbase = _controller.Selection.SelectedAirbase;
            if (airbase != null)
                factories.Add(airbase);
            // Недострой-фабрика (Selected при этом пуст, как у аэродрома): ралли
            // получает ПЛОЩАДКА — маршрут переедет на здание по завершении стройки
            // (ProductionTasks.AdoptSite). Чужую площадку ралли не кормим.
            if (factories.Count == 0 && selected.Count == 0 &&
                BuildTasks.IsFactorySite(BuildTasks.SelectedTask) &&
                GameApi.TryGetLocalHQ(out FactionHQ rallyHq) &&
                BuildTasks.SelectedTask.Faction == rallyHq)
                factories.Add(BuildTasks.SelectedTask);
            if (factories.Count == 0)
                return false;
            Vector3 target = RouteEditor.SnapAt(GameApi.MainCam);
            // Один клик = один групповой вейпоинт у ВСЕХ фабрик выделения (как у
            // приказов юнитам): спущенные пачкой наследуют groupId — общий
            // маркер/драг/удаление вместо N копий точки (§12-30).
            int groupId = BuildTasks.NextGroupId();
            foreach (object factory in factories)
            {
                if (RtsInput.Shift)
                    ProductionTasks.EnqueueRally(factory, target, groupId);
                else
                    ProductionTasks.SetRally(factory, target, groupId);
            }
            // Всё — авианосцы (attached-аэродромы): честное имя ралли — авиакрыло
            bool airWing = factories.Count > 0;
            foreach (object factory in factories)
                if (!(factory is Airbase ab && ab.AttachedAirbase))
                {
                    airWing = false;
                    break;
                }
            if (airWing)
                CommanderHud.Notice(Loc.F("Ралли авиакрыла: маршрут из {0} тчк",
                    ProductionTasks.RallyOf(factories[0])?.Count ?? 0));
            else
                CommanderHud.Notice(Loc.F("Ралли: {0} {1}, маршрут из {2} тчк",
                    factories.Count,
                    Loc.Plural(factories.Count, "фабрика", "фабрики", "фабрик"),
                    ProductionTasks.RallyOf(factories[0])?.Count ?? 0));
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
            target = GameApi.OnSurface(target); // клик «в воду» — якорь на ПОВЕРХНОСТИ, не на дне

            switch (OrderModes.Pending)
            {
                case OrderMode.None: // дефолт по ПКМ без заряда = «Движение»
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

                case OrderMode.Nuke:
                    if (hasTarget)
                    {
                        IssueNukeStrike(selected, target, queue);
                        Markers.Add(new Marker(target, Time.unscaledTime));
                    }
                    break;

                case OrderMode.Guard:
                    // ЛКМ по союзному юниту/зданию — охрана (канон SupCom guard);
                    // мимо цели — тихий no-op (заряд остаётся, выход ПКМ/Esc).
                    if (!IssueGuardOrders(localHq))
                        return;
                    break;

                case OrderMode.DisperseAttack:
                    // сюда доходит только клик по земле (цели съедены веткой заряда
                    // в Tick выше): копили ЛКМ по врагам — раздаём, не копили —
                    // деградация в атаку-движение.
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
            target = GameApi.OnSurface(target); // клик миникарты по воде — на поверхность
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
                return;
            }
            if (mode == OrderMode.Nuke)
            {
                IssueNukeStrike(selected, target, RtsInput.Shift);
                Markers.Add(new Marker(target, Time.unscaledTime));
                return;
            }
            IssueForTarget(selected, mode, target, RtsInput.Shift);
            if (mode != OrderMode.Patrol)
                Markers.Add(new Marker(target, Time.unscaledTime));
            // Заряд не сбрасываем (SupCom-схема): выдача не гасит режим — только
            // ПКМ/Esc/пустое выделение.
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
                            // Патруль ДОПИСЫВАЕТ точку в живой цикл (канон SupCom
                            // IssuePatrol — аппенд, не замена): заряд держится, юнит
                            // накликивает маршрут; не в патруле — новый цикл.
                            bool airPatrol = AircraftOrders.TryGetPlan(a, out var airPlan) &&
                                             airPlan.Count > 0 &&
                                             airPlan[airPlan.Count - 1].Mode == AirOrderMode.Patrol;
                            if (airPatrol)
                                AircraftOrders.Enqueue(a, AirOrderMode.Patrol, target, _moveGroupId);
                            else
                                AircraftOrders.Set(a, AirOrderMode.Patrol, target, _moveGroupId);
                            anyAir = true;
                            continue;
                        }
                        if (!(unit is GroundVehicle || unit is Ship))
                            continue;
                        bool engineer = GameApi.IsEngineer(unit);
                        if (engineer && GroundOrders.KindOf(unit) != GroundOrderKind.Patrol)
                            BuildTasks.ClearFor(unit); // патруль инженера = новая очередь (точки дописываются ниже)
                        if (GroundOrders.KindOf(unit) == GroundOrderKind.Patrol)
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
            int air = 0;
            foreach (Unit unit in selected)
            {
                if (!(unit is Aircraft aircraft))
                    continue;
                air++;
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
                : air > 0
                    ? Loc.Tr("Посадка недоступна: нет своего аэродрома с открытой посадкой")
                    : Loc.Tr("В выделении нет бортов"));
        }

        // Наземка/флот смешанного выделения при ПКМ-посадке: им достаётся обычное
        // движение в точку клика (скретч, чистится каждым вызовом).
        private readonly List<Unit> _landingGround = new List<Unit>();

        /// <summary>ПКМ по ПОЛОТНУ своей ВПП (v0.107, дефолтный режим «Движение»,
        /// в выделении есть авиация) — посадка НА УКАЗАННУЮ базу: терминальный RTB
        /// к аэродрому клика (Shift — RTB в хвост очереди, как Shift+G). Стоящий
        /// у ДРУГОЙ базы борт перебазируется (SortieService выкатит его вахтой
        /// «сел не у цели»). База проверяется каждому борту нативным
        /// Airbase.IsSuitable (длина/трос/вертикальные точки) и должна быть
        /// открыта для посадки (тумблеры AirbaseAccess). Наземка/флот смешанного
        /// выделения получают обычное движение в точку клика.</summary>
        private bool TryIssueLandingOrders(FactionHQ localHq)
        {
            bool anyAir = false;
            foreach (Unit unit in _controller.Selection.Selected)
                if (unit is Aircraft)
                {
                    anyAir = true;
                    break;
                }
            if (!anyAir ||
                !GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 ground))
                return false;
            Airbase airbase = GameApi.OwnRunwayAirbaseAt(ground, localHq);
            if (airbase == null)
                return false;

            bool queue = RtsInput.Shift;
            bool landingOpen = AirbaseAccess.IsLandingOpen(airbase);
            int landed = 0, unsuitable = 0;
            _landingGround.Clear();
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || unit.disabled)
                    continue; // Unity fake-null: мёртвый между кадрами не приказывается
                if (!(unit is Aircraft aircraft))
                {
                    _landingGround.Add(unit);
                    continue;
                }
                if (!landingOpen)
                    continue;
                if (!airbase.IsSuitable(SortieService.LandingQuery(aircraft)))
                {
                    unsuitable++;
                    continue;
                }
                if (queue)
                    AircraftOrders.EnqueueRtb(aircraft, airbase);
                else
                    AircraftOrders.ReturnToBase(aircraft, airbase);
                landed++;
            }

            if (landed > 0)
            {
                // Маркер-крест только при «чистой» посадке: наземному хвосту его
                // поставит свой IssuePending (не дублируем перекрестья).
                if (_landingGround.Count == 0)
                    Markers.Add(new Marker(ground, Time.unscaledTime));
                CommanderHud.Notice(queue
                    ? Loc.F("Посадка на ВПП (в очередь): бортов — {0}", landed)
                    : Loc.F("Посадка на ВПП: бортов — {0}", landed));
            }
            if (!landingOpen)
                CommanderHud.Notice(Loc.Tr("Посадка на этой базе закрыта тумблером"));
            if (unsuitable > 0)
                CommanderHud.Notice(Loc.F("ВПП не подходит: {0} {1}", unsuitable,
                    Loc.Plural(unsuitable, "борт", "борта", "бортов")));

            // Наземка/флот не садятся — обычное движение в точку клика.
            if (_landingGround.Count > 0)
                IssuePending(_landingGround, queue);
            return true;
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
                // Ангар/верфь — тоже фабрики: отмена снимает и их ралли.
                Hangar hangar = unit is Building b ? b.GetComponent<Hangar>() : null;
                if (hangar != null)
                {
                    ProductionTasks.ClearRally(hangar);
                    continue;
                }
                if (BuildTasks.IsDock(unit))
                {
                    ProductionTasks.ClearRally(unit);
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

        // Корабли ручного залпа текущего клика (SupCom-канон: волна без похода в атаку).
        private readonly List<Unit> _fleetSalvo = new List<Unit>();

        // Остаток выделения залпа по площади после вырезания кораблей (без аллокаций).
        private readonly List<Unit> _salvoRest = new List<Unit>();

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
            int issued = 0;
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
                    // Очередь сопровождения (§12-36а, зеркально авиации): в линейной
                    // цепочке связь активируется после пройденных точек, в эскортирующей —
                    // по смерти текущей цели. Инженеру легально наравне с боевыми.
                    GroundOrders.EnqueueEscort(unit, friend);
                    issued++;
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
            return true;
        }

        /// <summary>Союзный юнит/здание под курсором для ОХРАНЫ — экранный пик
        /// (свои видны всегда): мобильная техника, флот, авиация И здания (канон
        /// SupCom: охрана радара/завода = защита + авто-ремонт инженером).
        /// Зеркало TryPickFriendlyAt с расширенным набором типов.</summary>
        private Unit TryPickGuardTargetAt(FactionHQ localHq)
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
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft || unit is Building))
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

        /// <summary>Охрана союзного юнита/здания (v0.158, SupCom guard, OrderMode.Y):
        /// наземка/флот — follow-модель эскорта с веером секторов; инженер — охрана
        /// с авто-ремонтом (повреждённая цель временно уходит в Repair); авиация
        /// охраны не имеет (план-модель: escort-сегменты, ПКМ по союзнику) —
        /// пропускается с Notice. Цель сама себе стражем быть не может.
        /// true — клик по цели съеден (цель найдена или выделение только авиация).</summary>
        private bool IssueGuardOrders(FactionHQ localHq)
        {
            Unit friend = TryPickGuardTargetAt(localHq);
            if (friend == null)
                return false;
            Vector3 fp = friend.transform.position;

            _escortFan.Clear();
            int airCount = 0;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || unit == friend)
                    continue; // сам себя не охраняет
                if (unit is Aircraft)
                {
                    airCount++;
                    continue;
                }
                if (unit is GroundVehicle || unit is Ship)
                    _escortFan.Add(unit);
            }
            if (_escortFan.Count == 0)
            {
                CommanderHud.Notice(airCount > 0
                    ? Loc.Tr("Охрана: авиации не назначается — сопровождение (ПКМ по союзнику)")
                    : Loc.Tr("Охрана: цель — всё выделение"));
                return true; // клик съеден: приказов движению по самой цели не выдаём
            }

            // Сортировка по азимутам вокруг цели: соседние сектора веера соседям
            // (как полукруг групповой атаки и веер эскорта).
            _escortFan.Sort((a, b) => Mathf.Atan2(a.transform.position.x - fp.x,
                    a.transform.position.z - fp.z)
                .CompareTo(Mathf.Atan2(b.transform.position.x - fp.x,
                    b.transform.position.z - fp.z)));
            float guardStandoff = Settings.EscortStandoffM + friend.maxRadius + 10f;
            int issued = 0;
            for (int i = 0; i < _escortFan.Count; i++)
            {
                Unit unit = _escortFan[i];
                if (GameApi.IsEngineer(unit))
                    BuildTasks.ClearFor(unit); // новая связь = новая очередь (как патруль)
                Vector3 up = unit.transform.position;
                float bearing = Mathf.Atan2(up.x - fp.x, up.z - fp.z) +
                    GroundOrders.FanBearingOffset(i, _escortFan.Count,
                        Settings.FormationSpacing, guardStandoff);
                GroundOrders.SetGuard(unit, friend, bearing);
                issued++;
            }
            if (issued > 0)
                CommanderHud.Notice(Loc.F("Охрана: {0} → {1}", issued,
                    friend.definition != null ? friend.definition.unitName : "?"));
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

        /// <summary>ПКМ инженерами по РАНЕНОЙ своей технике/зданию = РЕМОНТ (v0.149,
        /// канон SupCom): стоящая связь Repair (как сопровождение — в очередь точек
        /// не кладётся), ведение к цели и налив HP — GroundOrders/RepairService.
        /// Целый юнит не пикнется — его подберёт сопровождение ниже. true — клик
        /// съеден (в выделении были инженеры).</summary>
        private bool TryIssueRepairOrders(FactionHQ localHq)
        {
            Unit target = TryPickRepairTargetAt(localHq);
            if (target == null)
                return false;
            int issued = 0, skippedShift = 0;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || unit == target || !GameApi.IsEngineer(unit))
                    continue;
                if (RtsInput.Shift)
                {
                    // Ремонт — стоящий приказ (как сопровождение): в очередь не кладётся.
                    skippedShift++;
                    continue;
                }
                BuildTasks.ClearFor(unit); // новая связь = новая очередь (канон)
                GroundOrders.SetRepair(unit, target);
                issued++;
            }
            if (issued == 0 && skippedShift == 0)
                return false; // инженеров в выделении нет — пусть клик идёт дальше (эскорт/движение)
            if (issued > 0)
                CommanderHud.Notice(Loc.F("Ремонт: {0} → {1}", issued,
                    target.definition != null ? target.definition.unitName : "?"));
            if (skippedShift > 0)
                CommanderHud.Notice(Loc.Tr("Ремонт не ставится в очередь — выдай без Shift"));
            return true;
        }

        /// <summary>РАНЕНЫЙ свой юнит/здание под курсором — экранный пик для ремонта
        /// (техника/флот/здания; авиацию чинит аэродромный сервис — §3-10).
        /// Расширение TryPickFriendlyAt: здания включены, раненость обязательна.</summary>
        private Unit TryPickRepairTargetAt(FactionHQ localHq)
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
                if (!(unit is GroundVehicle || unit is Ship || unit is Building))
                    continue;
                if (GameApi.HpFraction(unit) >= 0.995f)
                    continue; // целый — не цель ремонта (его подберёт эскорт)
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

        /// <summary>ПКМ инженерами по обломку = адресный сбор (v0.149, §12-46г):
        /// точка очереди у обломка (сальваж — движение, а не стоящая связь) —
        /// доедет и встанет, автосбор SalvageService разбирает сам. Без Shift —
        /// новая очередь, с Shift — дописать точку (групповой вейпоинт).</summary>
        private bool TryIssueSalvageOrder()
        {
            Wreckage wreck = TryPickWreckAt();
            if (wreck == null)
                return false;
            Vector3 wp = wreck.transform.position;
            wp.y = GameApi.GroundY(wp.x, wp.z);
            var point = wp.ToGlobalPosition();
            int groupId = BuildTasks.NextGroupId();
            int issued = 0;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (unit == null || !GameApi.IsEngineer(unit))
                    continue;
                if (!RtsInput.Shift)
                    BuildTasks.ClearFor(unit); // новый приказ = новая очередь (канон)
                BuildTasks.AppendMove(unit, point, groupId);
                issued++;
            }
            if (issued == 0)
                return false; // инженеров нет — не съедаем клик по земле рядом с обломком
            CommanderHud.Notice(Loc.F("Сбор обломка: {0}", issued));
            return true;
        }

        /// <summary>Обломок под курсором — экранный пик по MissionManager.listWrecks
        /// (Wreckage — не Unit, в реестре юнитов его нет; зеркало TryPickEnemyAt).</summary>
        private Wreckage TryPickWreckAt()
        {
            var mm = MissionManager.i;
            Camera cam = GameApi.MainCam;
            if (mm == null || cam == null || mm.listWrecks.Count == 0)
                return null;
            float radiusPx = Settings.EnemyPickRadiusPx;
            Wreckage best = null;
            float bestDist = float.MaxValue;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Wreckage wreck in mm.listWrecks)
            {
                if (wreck == null)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(wreck.transform.position);
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                if (d < radiusPx && d < bestDist)
                {
                    bestDist = d;
                    best = wreck;
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
            anchor = GameApi.OnSurface(anchor); // залп по цели у воды — якорь на поверхности
            // Флот в режиме «Залп» — РУЧНОЙ пуск (SupCom-канон: волна = по одной
            // ракете с корабля, серия кликов дозирует расход), корабли НЕ идут в
            // атаку. Собираем отдельно, пускаем после цикла.
            _fleetSalvo.Clear();
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
                else if (unit is Ship)
                {
                    if (salvo)
                    {
                        _fleetSalvo.Add(unit);
                        any = true;
                    }
                    else
                    {
                        _attackFan.Add(unit); // сектора разносятся вторым проходом
                        any = true;
                    }
                }
                else if (unit is GroundVehicle && !GameApi.IsEngineer(unit))
                {
                    if (salvo && FleetSalvo.UnitHasMissiles(unit))
                    {
                        // РСЗО/ПУ наземки — в общий залп по точке/цели (v0.133:
                        // SupCom-тактики для батарей; пушечная техника — атакой)
                        _fleetSalvo.Add(unit);
                        any = true;
                    }
                    else
                    {
                        _attackFan.Add(unit); // сектора разносятся вторым проходом
                        any = true;
                    }
                }
            }
            if (_fleetSalvo.Count > 0)
            {
                int fired = FleetSalvo.LaunchWave(_fleetSalvo, enemy, enemy.GlobalPosition());
                Markers.Add(new Marker(enemy.transform.position, Time.unscaledTime));
                SalvoNotice(fired);
                _fleetSalvo.Clear();
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

        /// <summary>Notice ручного залпа по факту: ушедшие сразу / волна в очереди
        /// (трубы перезаряжаются — SupCom-перечисление пусков) / нечем вовсе.</summary>
        private static void SalvoNotice(int fired)
        {
            CommanderHud.Notice(fired > 0
                ? Loc.F("Ручной залп: пусков — {0}", fired)
                : FleetSalvo.Queue.Count > 0
                    ? Loc.Tr("Ручной залп: волна в очереди — трубы перезаряжаются")
                    : Loc.Tr("Ручной залп: нет готовых ракет (гейты/боезапас/перезарядка)"));
        }

        /// <summary>Заряд режима «Залп» (V/кнопка — без ограничения типа; ПКМ по
        /// чипу типа на панели — волны флота только этим вооружением).</summary>
        public void ChargeSalvo(WeaponInfo scope = null)
        {
            FleetSalvo.Scope = scope;
            OrderModes.Pending = OrderMode.Salvo;
        }

        /// <summary>Заряд «Ядерного удара» (N / кнопка ЯО): носители получают точки
        /// пуска по кликам (Shift — серия), я-авиация — одноразовое разрешение
        /// и атаку-движение в точку. Alt+N — тумблер авто-ЯО на выделение.</summary>
        public void ChargeNuke()
        {
            OrderModes.Pending = OrderMode.Nuke;
            CommanderHud.Notice(Loc.Tr("ЯО: клик — точка удара (Shift — серия)"));
        }

        /// <summary>Alt+N: автозарядка носителей / автоприменение ЯО авиации.</summary>
        public void ToggleNukeAuto(List<Unit> selected)
        {
            int ships = 0, air = 0;
            foreach (Unit unit in selected)
            {
                if (WarheadService.IsCarrier(unit))
                {
                    WarheadService.Charge c = WarheadService.ChargeOf(unit);
                    if (c == null)
                        continue; // заряд появится тиком
                    c.AutoCharge = !c.AutoCharge;
                    ships++;
                }
                else if (unit is Aircraft && WarheadService.HasNuclearWeapon(unit))
                {
                    WeaponGateService.SetNuclearAuto(unit, !WeaponGateService.NuclearAuto(unit));
                    air++;
                }
            }
            if (ships + air > 0)
                CommanderHud.Notice(Loc.F("ЯО: авто переключено (носителей {0}, бортов {1})", ships, air));
        }

        /// <summary>Выдача «ЯО» в точку: корабли — очередь точек пуска, я-авиация —
        /// разрешение + атака-движение (применит по врагам у точки нативным AI).</summary>
        private void IssueNukeStrike(List<Unit> selected, Vector3 point, bool queue)
        {
            GlobalPosition gp = point.ToGlobalPosition();
            _nukeAir.Clear();
            int ships = 0;
            foreach (Unit unit in selected)
            {
                if (WarheadService.IsCarrier(unit))
                {
                    WarheadService.AddStrike(unit, gp);
                    ships++;
                }
                else if (unit is Aircraft && WarheadService.HasNuclearWeapon(unit))
                    _nukeAir.Add(unit);
            }
            if (_nukeAir.Count > 0)
            {
                WarheadService.OrderAircraftStrike(_nukeAir, point);
                IssueForTarget(_nukeAir, OrderMode.AttackMove, point, queue);
            }
            if (ships + _nukeAir.Count > 0)
                CommanderHud.Notice(Loc.F("ЯО: точка удара принята (носителей {0}, бортов {1})",
                    ships, _nukeAir.Count));
        }

        private readonly List<Unit> _nukeAir = new List<Unit>();

        /// <summary>Заряд «Распределения» (R / кнопка): список целей копится с нуля.</summary>
        public void ChargeFanAttack()
        {
            FanTargets.Clear();
            OrderModes.Pending = OrderMode.DisperseAttack;
        }

        /// <summary>Раздача «распределения»: каждая цель получает ёмкость N/M (+1 первым по
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
                CommanderHud.Notice(Loc.Tr("Распределение: цели потеряны"));
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
                CommanderHud.Notice(Loc.Tr("Распределение: в выделении нет атакующих"));
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
            CommanderHud.Notice(Loc.F("Распределение: {0} → {1} {2}", attackers.Count, FanTargets.Count,
                Loc.Plural(FanTargets.Count, "цель", "цели", "целей")));
            FanTargets.Clear();
        }

        /// <summary>Disperse Move (Alt+Shift+R, FAF-стиль): очередь групповых
        /// вейпоинтов выделенных распределяется — юнит i получает группу точек
        /// i mod G (порядок кликов = возрастание groupId), у каждого остаётся ОДНА
        /// точка — прямой вектор вместо общего маршрута. Наземка/флот и авиация
        /// распределяются независимо.
        /// v0.107: распределяются и ЦЕЛИ атак — пачка с серией ПКМ по куче целей
        /// (каждый юнит несёт всю серию в очереди) получает по ОДНОЙ цели на
        /// юнита (Util.AssignBalanced — ближайшая цель со свободной ёмкостью, как в
        /// режиме «Распределение»); у юнита остаётся максимум одна точка и одна цель
        /// за ней.
        /// v0.108: распределяется и СТРОЙКА — серия площадок очередей выделенных
        /// инженеров делится между ними кучками (Util.AssignSpread: наименее
        /// загруженный берёт ближайшую к концу своего маршрута), каждый строит
        /// свою подвыборку (BuildTasks.DisperseSequences, хост).</summary>
        public void IssueDisperseMove(List<Unit> selected)
        {
            var ground = new List<Unit>();
            var air = new List<Unit>();
            var engineers = new List<Unit>();
            foreach (Unit unit in selected)
            {
                if (unit == null || unit.disabled)
                    continue;
                if (unit is Aircraft)
                    air.Add(unit);
                else if (unit is GroundVehicle || unit is Ship)
                {
                    if (GameApi.IsEngineer(unit))
                        engineers.Add(unit); // стройка раскладывается своей раздачей
                    else
                        ground.Add(unit);
                }
            }

            var groups = new List<int>();
            int points = 0, vectors = 0, targets = 0, targetKinds = 0, sites = 0;

            CollectGroundGroups(ground, groups);
            if (groups.Count > 0)
            {
                vectors += groups.Count;
                for (int i = 0; i < ground.Count; i++)
                    if (GroundOrders.DisperseKeep(ground[i], groups[i % groups.Count]))
                        points++;
            }
            targets += DisperseGroundTargets(ground);
            targetKinds += _targetPool.Count;

            CollectAirGroups(air, groups);
            int[] airAssigned = CollectAirTargets(air);
            targetKinds += _airPool.Count;
            if (groups.Count > 0 || airAssigned != null)
            {
                vectors += groups.Count;
                for (int i = 0; i < air.Count; i++)
                    if (AircraftOrders.DisperseKeep(air[i],
                            groups.Count > 0 ? groups[i % groups.Count] : 0,
                            airAssigned != null && airAssigned[i] >= 0
                                ? _airPool[airAssigned[i]]
                                : null, out bool keptPoint))
                    {
                        if (keptPoint)
                            points++;
                        if (airAssigned != null && airAssigned[i] >= 0)
                            targets++;
                    }
            }
            sites += BuildTasks.DisperseSequences(engineers);

            if (points > 0)
                CommanderHud.Notice(Loc.F("Рассредоточение: {0} {1} на {2} {3}", points,
                    Loc.Plural(points, "юнит", "юнита", "юнитов"), vectors,
                    Loc.Plural(vectors, "вектор", "вектора", "векторов")));
            if (targets > 0)
                CommanderHud.Notice(Loc.F("Рассредоточение атак: {0} {1} на {2} {3}", targets,
                    Loc.Plural(targets, "юнит", "юнита", "юнитов"), targetKinds,
                    Loc.Plural(targetKinds, "цель", "цели", "целей")));
            if (points == 0 && targets == 0 && sites == 0)
                CommanderHud.Notice(Loc.Tr("Нет очереди вейпоинтов"));
        }

        // Пулы живых целей раздачи Disperse: живут между вызовами, чистятся в начале сбора.
        private readonly List<Unit> _targetPool = new List<Unit>();
        private readonly List<Unit> _airPool = new List<Unit>();

        /// <summary>Раздача целей атак наземки/флота при Disperse: пул живых целей
        /// очередей выделения (порядок встреч = порядок серии), каждому охотнику —
        /// БЛИЖАЙШАЯ цель со свободной ёмкостью. Возвращает число юнитов, которым
        /// цель назначена (назначенная цель не из очереди юнита — чужая серия,
        /// юнита не трогаем). Пул остаётся в <see cref="_targetPool"/>.</summary>
        private int DisperseGroundTargets(List<Unit> ground)
        {
            _targetPool.Clear();
            var hunters = new List<Unit>();
            foreach (Unit unit in ground)
            {
                var queue = GroundOrders.TargetQueueOf(unit);
                if (queue == null)
                    continue;
                hunters.Add(unit);
                foreach (Unit target in queue)
                    if (target != null && !target.disabled && !_targetPool.Contains(target))
                        _targetPool.Add(target);
            }
            if (_targetPool.Count == 0)
                return 0;
            var unitPos = new List<Vector3>(hunters.Count);
            var targetPos = new List<Vector3>(_targetPool.Count);
            foreach (Unit unit in hunters)
                unitPos.Add(unit.transform.position);
            foreach (Unit target in _targetPool)
                targetPos.Add(target.transform.position);
            int[] assigned = Util.AssignBalanced(unitPos, targetPos);
            int n = 0;
            for (int i = 0; i < hunters.Count; i++)
                if (GroundOrders.DisperseTargets(hunters[i], _targetPool[assigned[i]]))
                    n++;
            return n;
        }

        /// <summary>Пул живых Attack-целей планов бортов выделения (порядок встреч)
        /// и раздача по бортам (AssignBalanced, ближайшая со свободной ёмкостью).
        /// null — целей нет; иначе индекс пула на каждый борт из <paramref name="air"/>
        /// (-1 — борт без целей). Пул остаётся в <see cref="_airPool"/>.</summary>
        private int[] CollectAirTargets(List<Unit> air)
        {
            _airPool.Clear();
            var hunters = new List<Unit>();
            var hunterIdx = new int[air.Count];
            for (int i = 0; i < air.Count; i++)
            {
                hunterIdx[i] = -1;
                if (!AircraftOrders.TryGetPlan(air[i], out var plan))
                    continue;
                bool hunter = false;
                foreach (AircraftOrders.OrderSegment seg in plan)
                {
                    if (seg.Mode != AirOrderMode.Attack)
                        continue;
                    Unit t = seg.Target;
                    if (t == null || t.disabled)
                        continue;
                    hunter = true;
                    if (!_airPool.Contains(t))
                        _airPool.Add(t);
                }
                if (hunter)
                {
                    hunterIdx[i] = hunters.Count;
                    hunters.Add(air[i]);
                }
            }
            if (_airPool.Count == 0)
                return null;
            var unitPos = new List<Vector3>(hunters.Count);
            var targetPos = new List<Vector3>(_airPool.Count);
            foreach (Unit unit in hunters)
                unitPos.Add(unit.transform.position);
            foreach (Unit target in _airPool)
                targetPos.Add(target.transform.position);
            int[] hunterAssigned = Util.AssignBalanced(unitPos, targetPos);
            var assigned = new int[air.Count];
            for (int i = 0; i < air.Count; i++)
                assigned[i] = hunterIdx[i] >= 0 ? hunterAssigned[hunterIdx[i]] : -1;
            return assigned;
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
            target = GameApi.OnSurface(target); // залп «в воду» — в точку ПОВЕРХНОСТИ
            // Флот и РСЗО/ПУ наземки — РУЧНОЙ пуск по ТОЧКЕ (SupCom-нул, v0.117/v0.133):
            // волна уходит сразу, цель-юнит не нужна (ракета летит в точку, ГСН может
            // захватить цель у неё по пути). Пусковые не двигаются и НЕ участвуют в
            // решётке залпа авиации — дальше разбираем только остальных.
            _fleetSalvo.Clear();
            bool anyLauncher = false;
            foreach (Unit unit in selected)
                if (unit is Ship || (unit is GroundVehicle && !GameApi.IsEngineer(unit) &&
                                     FleetSalvo.UnitHasMissiles(unit)))
                {
                    _fleetSalvo.Add(unit);
                    anyLauncher = true;
                }
            if (anyLauncher)
            {
                // Пуск ПО ТОЧКЕ (SupCom-нул): ракета ведётся в координату
                // (отложенное Missile.SetAimpoint после спавна), цель-юнит не нужна —
                // насыщение ПВО, сплеш, удар наугад, упреждение кликом вперёд.
                // Точка — на ПОВЕРХНОСТИ (клик по воде даёт ДНО — канон флота).
                Vector3 surface = new Vector3(target.x, GameApi.RouteY(target.x, target.z), target.z);
                int fired = FleetSalvo.LaunchWave(_fleetSalvo, null, surface.ToGlobalPosition());
                SalvoNotice(fired);
                _salvoRest.Clear();
                foreach (Unit unit in selected)
                    if (!(unit is Ship || (unit is GroundVehicle && FleetSalvo.UnitHasMissiles(unit))))
                        _salvoRest.Add(unit);
                selected = _salvoRest;
            }
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
        /// DrawHostileIcons), и пик берёт ТОЛЬКО её — живая позиция невидимого врага
        /// не «прощупывается» ПКМ (утечка тумана войны). Гейт — «есть отметка»
        /// (GameApi.IsRadarContact, опознание НЕ нужно — атаковать безымянную
        /// радар-точку можно, канон SupCom): подвижные — живой контакт, здания —
        /// любой. Радиус — max(базовых px, половины видимого корпуса); из
        /// кандидатов ближайший к курсору.</summary>
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
                // Ракеты видны обеим сторонам без разведки — якорь живой позиции;
                // прочие враги — только якорь контакта (туман войны).
                Vector3 anchor;
                if (unit is Missile)
                    anchor = GameApi.UnitAnchorWorld(unit);
                else if (GameApi.IsRadarContact(unit, localHq, out TrackingInfo info))
                    anchor = GameApi.ContactAnchorWorld(unit, info);
                else
                    continue; // отметки нет — пика нет
                Vector3 sp = cam.WorldToScreenPoint(anchor);
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
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
            int cancelledWaves = 0; // волны залпа, снятые движением без Shift

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
                {
                    // Новый приказ движения без Shift = НОВАЯ ОЧЕРЕДЬ (канон SupCom):
                    // снимает ждущие волны залпа у ЛЮБОГО участника — наземная ПУ
                    // с v0.134, корабли уравнены 2026-11-06 (прежде «веду и
                    // обстреливаю параллельно» — поведение точек залпа расходилось
                    // с остальными вейпоинтами). Shift — дописывает движение, волны
                    // живут. Приказ АТАКИ волны не снимает (обстрел совместим с боем).
                    cancelledWaves += FleetSalvo.CancelWavesOf(unit);
                    GroundOrders.Set(unit, pos.ToGlobalPosition(), attackMove, _moveGroupId);
                }
                LastDestinations[unit] = pos.ToGlobalPosition();
            }
            if (cancelledWaves > 0)
                CommanderHud.Notice(Loc.F("Волны обстрела сняты: {0}", cancelledWaves));
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
