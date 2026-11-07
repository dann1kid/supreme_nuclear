using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть MoveOrderService: атака, залп/ЯО/веер, Disperse, формации, пик врага.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; сводка и вход Tick — MoveOrderService.cs.
    public sealed partial class MoveOrderService
    {
        // Наземные атакующие текущего клика: сортировка по азимуту для полукруга.
        private readonly List<Unit> _attackFan = new List<Unit>();

        // Корабли ручного залпа текущего клика (SupCom-канон: волна без похода в атаку).
        private readonly List<Unit> _fleetSalvo = new List<Unit>();

        // Остаток выделения залпа по площади после вырезания кораблей (без аллокаций).
        private readonly List<Unit> _salvoRest = new List<Unit>();

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
    }
}
