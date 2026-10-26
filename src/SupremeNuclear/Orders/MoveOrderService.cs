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

        public MoveOrderService(CommanderModeController controller) => _controller = controller;

        public void Tick()
        {
            // Идёт расстановка постройки/шаблона: ПКМ означает ОТМЕНУ (контроллер),
            // в приказы движение он превращаться не должен.
            if (BuildTasks.Pending != null || OrderTemplates.PendingTemplate.template != null ||
                BuildTasks.RmbEaten)
                return;

            // Ctrl+Shift+ПКМ = УДАЛИТЬ вейпоинт (цепочки/планы/площадки/хвосты).
            if (RtsInput.Ctrl && RtsInput.Shift && RtsInput.RightDown && !RtsInput.PointerOverUi &&
                TryDeleteWaypoint())
                return;

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

                // Ралли фабрик: выделение только из депо → ПКМ задаёт маршрут вышедшим
                // войскам (Shift — дописать точку), как приказы юнитам.
                if (TrySetRallyForDepotSelection())
                    return;

                // ПКМ на существующем вейпоинте авиаплана — превратить сегмент в патруль.
                if (TryConvertWaypointToPatrol())
                    return;

                // ПКМ по недострою (площадке существующей задачи) — достройка выделенными
                // инженерами (shift не обязателен, это адресное действие).
                if (BuildTasks.TryResume(
                        new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y),
                        GameApi.MainCam, _controller.Selection.Selected))
                    return;

                // ПКМ прямо по врагу (10px) — атака авиации.
                GameApi.TryGetLocalHQ(out FactionHQ localHq);
                Unit enemy = TryPickEnemyAt(localHq, 10f);
                if (enemy != null && IssueAttackOrders(enemy))
                {
                    if (!RtsInput.Shift)
                        OrderModes.Pending = OrderMode.Move;
                    return;
                }

                IssuePending(_controller.Selection.Selected, RtsInput.Shift);
                if (!RtsInput.Shift)
                    OrderModes.Pending = OrderMode.Move;
            }
            // Приказы выдаются ТОЛЬКО по ПКМ — ничего на каждый кадр.
        }

        /// <summary>Удаление вейпоинта под курсором (Ctrl+Shift+ПКМ): цепочки наземки,
        /// планы авиации, площадки стройки (недострой сносится без возврата), хвосты
        /// движений инженеров. Shift расширяет поиск на ВСЕ юниты (режим видимости).</summary>
        private bool TryDeleteWaypoint()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return false;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);

            // Тело 3D-призрака под курсором — снос его площадки.
            foreach (BuildTasks.Task t in BuildTasks.All)
                if (BuildGhosts.ScreenContains(t, cam, mouse))
                {
                    BuildTasks.Cancel(t);
                    if (BuildTasks.SelectedTask == t)
                        BuildTasks.SelectedTask = null;
                    CommanderHud.Notice("Площадка снесена");
                    return true;
                }

            // ЕДИНЫЙ скан (консолидация v0.48) — тот же, что у захвата вейпоинтов.
            WaypointHit hit = WaypointScanner.Scan(_controller, cam, mouse, 16f, RtsInput.Shift);
            switch (hit.Kind)
            {
                case WaypointKind.BuildSite:
                    BuildTasks.Cancel(hit.Task); // снос недостроя, без возврата
                    if (BuildTasks.SelectedTask == hit.Task)
                        BuildTasks.SelectedTask = null;
                    CommanderHud.Notice("Площадка снесена");
                    return true;
                case WaypointKind.SequenceMove:
                {
                    // Групповой вейпоинт (SupCom): удаляется у всей группы инженеров.
                    int gid = BuildTasks.GroupIdOf(hit.Owner, hit.Index);
                    BuildTasks.RemoveSequenceMove(hit.Owner, hit.Index);
                    if (gid != 0)
                        foreach (Unit unit in _controller.Selection.Selected)
                            if (unit != null && unit != hit.Owner)
                                BuildTasks.RemoveByGroupId(unit, gid);
                    return true;
                }
                case WaypointKind.GroundPoint:
                    // Групповой вейпоинт (SupCom): удаляется у всей выделенной формации.
                    GroundOrders.RemovePoint(hit.Owner, hit.Index);
                    foreach (Unit unit in _controller.Selection.Selected)
                        if (unit != null && unit != hit.Owner &&
                            GroundOrders.TryGetQueue(unit, out var q, out _) && hit.Index < q.Count)
                            GroundOrders.RemovePoint(unit, hit.Index);
                    return true;
                case WaypointKind.AirPoint:
                    AircraftOrders.RemovePoint(hit.Owner, hit.Segment, hit.Index);
                    foreach (Unit unit in _controller.Selection.Selected)
                        if (unit is Aircraft && unit != hit.Owner &&
                            AircraftOrders.TryGetPlan(unit, out var plan) &&
                            hit.Segment < plan.Count && hit.Index < plan[hit.Segment].Points.Count)
                            AircraftOrders.RemovePoint(unit, hit.Segment, hit.Index);
                    return true;
                default:
                    return false;
            }
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
            CommanderHud.Notice($"Ралли фабрики: маршрут из {points} тчк");
            return true;
        }

        private int _moveGroupId;

        private void IssuePending(List<Unit> selected, bool queue)
        {
            if (queue)
                _moveGroupId = BuildTasks.NextGroupId(); // один клик = один групповой вейпоинт

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
            }
        }

        /// <summary>Выдача приказа в известную мировую точку без пиков/снапа (миникарта).</summary>
        public void IssueAtWorld(Vector3 target)
        {
            List<Unit> selected = _controller.Selection.Selected;
            if (selected.Count == 0)
                return;
            OrderMode mode = OrderModes.Pending;
            if (mode == OrderMode.None)
                mode = OrderMode.Move;
            if (mode == OrderMode.Attack)
                mode = OrderMode.AttackMove; // цели с миникарты не пикингуются — ведём атакой-движением
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
                    AirOrderMode air = mode == OrderMode.AttackMove
                        ? AirOrderMode.AttackMove
                        : AirOrderMode.Transit;
                    foreach (Unit unit in selected)
                        if (unit is Aircraft a)
                        {
                            if (queue) AircraftOrders.Enqueue(a, air, target);
                            else AircraftOrders.Set(a, air, target);
                        }
                    IssueFormationOrders(selected, target, queue);
                    break;

                case OrderMode.Patrol:
                    bool anyAir = false;
                    foreach (Unit unit in selected)
                        if (unit is Aircraft a)
                        {
                            if (queue) AircraftOrders.Enqueue(a, AirOrderMode.Patrol, target);
                            else AircraftOrders.Set(a, AirOrderMode.Patrol, target);
                            anyAir = true;
                        }
                    if (!anyAir)
                        CommanderHud.Notice("Патруль пока только для авиации");
                    break;
            }
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
            CommanderHud.Notice("Приказы отменены");
        }

        /// <summary>ПКМ по вейпоинту плана выделенного борта: сегмент становится патрульным циклом.</summary>
        private bool TryConvertWaypointToPatrol()
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return false;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (!(unit is Aircraft) || !AircraftOrders.TryGetPlan(unit, out var plan))
                    continue;
                for (int s = 0; s < plan.Count; s++)
                {
                    var seg = plan[s];
                    for (int p = 0; p < seg.Points.Count; p++)
                    {
                        Vector3 sp = cam.WorldToScreenPoint(seg.Points[p].ToLocalPosition());
                        if (sp.z <= 0f)
                            continue;
                        if (Vector2.Distance(new Vector2(sp.x, sp.y), mouse) > 14f)
                            continue;
                        AircraftOrders.ConvertToPatrol(unit, s);
                        CommanderHud.Notice("Сегмент превращён в патруль");
                        return true;
                    }
                }
            }
            return false;
        }

        private bool IssueAttackOrders(Unit enemy)
        {
            bool any = false;
            foreach (Unit unit in _controller.Selection.Selected)
            {
                if (!(unit is Aircraft))
                    continue;
                AircraftOrders.Attack(unit, enemy);
                any = true;
            }
            if (any)
                CommanderHud.Notice($"Атака: {enemy.definition.unitName}");
            return any;
        }

        private Unit TryPickEnemyAt(FactionHQ localHq, float radiusPx = 16f)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return null;
            RaycastHit[] hits = Physics.RaycastAll(GameApi.ScreenRay(RtsInput.MousePos), 60000f);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit hit in hits)
            {
                UnitPart part = hit.collider != null ? hit.collider.GetComponentInParent<UnitPart>() : null;
                Unit unit = part != null ? part.parentUnit
                    : (hit.collider != null ? hit.collider.GetComponentInParent<Unit>() : null);
                if (unit != null && unit.NetworkHQ != null && unit.NetworkHQ != localHq &&
                    (unit is GroundVehicle || unit is Ship || unit is Aircraft))
                    return unit;
            }
            Unit best = null;
            float bestDist = radiusPx;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit.NetworkHQ == null || unit.NetworkHQ == localHq)
                    continue;
                if (!(unit is GroundVehicle || unit is Ship || unit is Aircraft))
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            return best;
        }

        private void IssueFormationOrders(List<Unit> units, Vector3 target, bool queue)
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
                        AircraftOrders.Enqueue(unit, AirOrderMode.Transit, airTarget);
                    else
                        AircraftOrders.Set(unit, AirOrderMode.Transit, airTarget);
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
                            GroundOrders.Enqueue(unit, target.ToGlobalPosition());
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
                    GroundOrders.Enqueue(unit, pos.ToGlobalPosition());
                else
                    GroundOrders.Set(unit, pos.ToGlobalPosition());
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
