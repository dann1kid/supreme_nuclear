using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Правый клик = движение выделенных. SetDestination сам решает хост/клиент.</summary>
    public sealed partial class MoveOrderService
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

        private int _moveGroupId;

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
