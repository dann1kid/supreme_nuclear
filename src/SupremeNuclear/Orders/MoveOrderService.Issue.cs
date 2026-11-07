using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть MoveOrderService: конвейер выдачи приказов (ралли фабрик, Pending/точка, посадка, отмена).
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; сводка и вход Tick — MoveOrderService.cs.
    public sealed partial class MoveOrderService
    {
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
    }
}
