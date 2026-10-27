using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    public enum GroundOrderKind
    {
        Move,
        /// <summary>Атака-движение: на ходу дерутся нативные турели; заметив цель в
        /// дальности вооружения (по даталинку), юнит ОСТАНАВЛИВАЕТСЯ и стреляет,
        /// после уничтожения продолжает маршрут (DESIGN §2.11).</summary>
        AttackMove,
        /// <summary>Атака цели: подход на дальность вооружения, стоп, нативный огонь;
        /// цель ушла — точка подхода обновляется.</summary>
        Attack
    }

    /// <summary>Цепочки вейпоинтов для наземки/флота: SetDestination выдаёт следующую точку,
    /// когда юнит доехал до текущей (90 м). Все точки — GlobalPosition (origin-proof).
    /// AttackMove — со стоп-энд-файр; Attack — преследование цели без цепочки.
    /// Смешанные Shift-серии исполняются В ПОРЯДКЕ КЛИКОВ (точка → цель → точка),
    /// как планы авиации: каждая цель несёт позицию серии (TargetAfter — сколько
    /// точек стояло в очереди на момент клика), дозревшая атака поднимается
    /// промоушеном, после её смерти маршрут продолжается с места промоушена.
    /// Каждая точка несёт groupId клика (0 — индивидуальный приказ): групповой вейпоинт —
    /// свойство ТОЧКИ, а не выделения: маркер/лента/драг/удаление — на всю группу приказа.</summary>
    public static class GroundOrders
    {
        private const float ArriveM = 90f;
        private const float ScanIntervalSec = 0.5f; // троттлинг обзора на юнита
        private const float PursueReissueM = 60f;   // цель сдвинулась сильнее — новая точка подхода

        /// <summary>Растёт при любом изменении цепочек — инвалидирует меш-ленту.</summary>
        public static int Version;

        public sealed class Chain
        {
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            /// <summary>Группа приказа каждой точки, параллельно Points (0 — индивидуальная).</summary>
            public readonly List<int> PointGroups = new List<int>();
            /// <summary>Очередь целей атаки (Shift+ПКМ по целям): в атакующей цепочке
            /// [0] — текущая (Target). Каждая цель стоит на СВОЕЙ позиции серии:
            /// TargetAfter[i] — сколько точек было в очереди на момент клика цели
            /// (движения, кликнутые ПОСЛЕ цели, исполняются после неё).</summary>
            public readonly List<Unit> TargetQueue = new List<Unit>();
            /// <summary>Позиция серии каждой цели, параллельно TargetQueue.</summary>
            public readonly List<int> TargetAfter = new List<int>();
            public int Index;
            public GroundOrderKind Kind = GroundOrderKind.Move;
            public GroundOrderKind ResumeKind = GroundOrderKind.Move; // вид до промоушена атаки
            public Unit Target;                       // Attack: текущая цель очереди
            public bool Holding;                      // стоят и стреляют (stop-and-fire)
            public float NextScanAt;                  // троттлинг обзора
            public GlobalPosition LastPoint;          // последняя выданная точка подхода
            /// <summary>Азимут подхода групповой атаки (полукруг): мир-фиксированный
            /// угол «цель → точка подхода» — юнит держит СВОЙ сектор, пока цель
            /// двигается. NaN — живой азимут юнит→цель (одиночная атака).</summary>
            public float ApproachBearing = float.NaN;
        }

        private static readonly Dictionary<Unit, Chain> Chains = new Dictionary<Unit, Chain>();

        public static int Count => Chains.Count;

        /// <summary>Вид текущего приказа юнита (нет цепочки — Move).</summary>
        public static GroundOrderKind KindOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) ? ch.Kind : GroundOrderKind.Move;

        /// <summary>Цель атаки юнита (для отрисовки), иначе null.</summary>
        public static Unit TargetOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Attack ? ch.Target : null;

        /// <summary>Юнит без активных приказов: непройденных точек нет И целей нет.
        /// Атака/очередь целей — занят даже при пустой цепочке точек (раньше атаку
        /// «занятой» делала производная точка подхода — её больше нет).</summary>
        public static bool IsIdle(Unit unit) =>
            !Chains.TryGetValue(unit, out Chain ch) ||
            (ch.Index >= ch.Points.Count && ch.TargetQueue.Count == 0);

        /// <summary>Цель дозрела: точки ДО её позиции серии пройдены. Удаления точек
        /// могли сдвинуть позицию — клампим к длине очереди. Чистая — тестируется.</summary>
        public static bool TargetDue(int donePoints, int after, int pointCount) =>
            donePoints >= Mathf.Min(after, pointCount);

        public static void Set(Unit unit, GlobalPosition destination) => Set(unit, destination, false);

        public static void Set(Unit unit, GlobalPosition destination, bool attackMove)
            => Set(unit, destination, attackMove, 0);

        public static void Set(Unit unit, GlobalPosition destination, bool attackMove, int groupId)
        {
            Version++;
            var ch = new Chain { Kind = attackMove ? GroundOrderKind.AttackMove : GroundOrderKind.Move };
            ch.Points.Add(destination);
            ch.PointGroups.Add(groupId);
            Chains[unit] = ch;
            unit.GetComponent<UnitCommand>()?.SetDestination(destination, playerCommand: true);
        }

        /// <summary>Атака цели: преследование с подходом на дальность вооружения.
        /// Перезаписывает цепочку (очередь целей — новая). approachBearing — сектор
        /// полукруга групповой атаки (NaN — живой азимут). Точки подхода в цепочке
        /// НЕТ: погоня рисуется крестом на цели и линией юнит→цель, очередь
        /// реальных точек не затрагивается.</summary>
        public static void SetAttack(Unit unit, Unit target, float approachBearing = float.NaN)
        {
            Version++;
            var ch = new Chain
            {
                Kind = GroundOrderKind.Attack,
                Target = target,
                ApproachBearing = approachBearing
            };
            ch.TargetQueue.Add(target);
            ch.TargetAfter.Add(0);
            Chains[unit] = ch;
            if (target != null)
                IssuePursuit(unit, ch, PursueRange(unit, target), force: true);
        }

        /// <summary>Дальность подхода для погони: пригодное оружие, безоружному — доехать.
        /// Также радиус дуги полукруга групповой атаки (MoveOrderService).</summary>
        public static float PursueRange(Unit unit, Unit target)
        {
            float range = EngageRange(unit, target);
            return range < 1f ? ArriveM : range;
        }

        /// <summary>Смещение азимута i-го атакующего в полукруге группы: симметричный
        /// веер, шаг = дуга arcStepM на радиусе подхода radiusM (левый фланг остаётся
        /// слева — маршруты не пересекаются). Чистая — тестируется.</summary>
        public static float FanBearingOffset(int index, int count, float arcStepM, float radiusM) =>
            (index - (count - 1) * 0.5f) * arcStepM / Mathf.Max(radiusM, 1f);

        /// <summary>Дописать цель в очередь атак (Shift+ПКМ): цель встаёт НА СВОЮ
        /// позицию серии — за точками, уже стоящими в очереди на момент клика
        /// (точки, кликнутые после неё, исполняются после её смерти). В атакующей
        /// цепочке — серия вслед за текущей целью; сектор полукруга берётся только
        /// при создании цепочки (ожидание за движениями подходит живым азимутом).</summary>
        public static void EnqueueAttack(Unit unit, Unit target, float approachBearing = float.NaN)
        {
            Version++;
            if (Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Attack)
            {
                ch.TargetQueue.Add(target);
                ch.TargetAfter.Add(ch.Index); // позиция уже достигнута — серия без задержки
                return;
            }
            if (Chains.TryGetValue(unit, out Chain move))
            {
                move.TargetQueue.Add(target);
                move.TargetAfter.Add(move.Points.Count); // за точками, стоящими на момент клика
                return;
            }
            SetAttack(unit, target, approachBearing);
        }

        /// <summary>Индекс первой РИСУЕМОЙ/РЕДАКТИРУЕМОЙ точки цепочки. Производной
        /// точки подхода у атак нет: во время атаки очередь реальных точек
        /// сохраняется и рисуется с точки продолжения маршрута.</summary>
        public static int DrawStartIndex(Unit unit)
        {
            return Chains.TryGetValue(unit, out Chain ch) ? ch.Index : 0;
        }

        /// <summary>Очередь целей атаки юнита (текущая серия ИЛИ ждущие своей позиции
        /// серии — обе рисуются крестами), иначе null.</summary>
        public static IReadOnlyList<Unit> TargetQueueOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) && ch.TargetQueue.Count > 0
                ? ch.TargetQueue
                : null;

        /// <summary>Позиция серии цели очереди (сколько точек стояло перед ней на
        /// момент клика) — отрисовка смешанной очереди вставляет цель-узел после
        /// этой точки (CommanderHud.DrawGroundChain).</summary>
        public static int TargetAfterOf(Unit unit, int queueIndex) =>
            Chains.TryGetValue(unit, out Chain ch) &&
            queueIndex >= 0 && queueIndex < ch.TargetAfter.Count ? ch.TargetAfter[queueIndex] : 0;

        public static void Enqueue(Unit unit, GlobalPosition destination) => Enqueue(unit, destination, false);

        public static void Enqueue(Unit unit, GlobalPosition destination, bool attackMove)
            => Enqueue(unit, destination, attackMove, 0);

        public static void Enqueue(Unit unit, GlobalPosition destination, bool attackMove, int groupId)
        {
            Version++;
            if (!Chains.TryGetValue(unit, out Chain ch))
            {
                Set(unit, destination, attackMove, groupId);
                return;
            }
            ch.Points.Add(destination);
            ch.PointGroups.Add(groupId);
        }

        public static void Clear(Unit unit)
        {
            Version++;
            Chains.Remove(unit);
        }

        /// <summary>Полный сброс (смена сцены): цепочки умершей сцены недействительны.</summary>
        public static void Reset()
        {
            Version++;
            Chains.Clear();
        }

        public static void Tick()
        {
            float now = Time.time;
            List<Unit> done = null;
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                Unit unit = kv.Key;
                Chain ch = kv.Value;
                if (unit == null || unit.disabled)
                {
                    (done ??= new List<Unit>()).Add(unit);
                    continue;
                }
                if (ch.Kind != GroundOrderKind.Attack && PromoteDue(unit, ch))
                    continue; // дозревшая цель: цепочка стала атакующей — погоня со следующего тика
                if (ch.Kind == GroundOrderKind.Attack)
                {
                    // true = цепочка завершена (цель мертва, очередей нет) — удаление
                    // ОТЛОЖЕНО: Remove внутри foreach по Chains даёт InvalidOperationException
                    // и обрывает тик остальной группы.
                    if (TickPursuit(unit, ch, now))
                        (done ??= new List<Unit>()).Add(unit);
                    continue;
                }
                if (ch.Kind == GroundOrderKind.AttackMove)
                {
                    TickStopAndFire(unit, ch, now);
                    if (ch.Holding)
                        continue; // стоят и стреляют — маршрут не продвигается
                }

                if (ch.Index >= ch.Points.Count)
                    continue; // пройдено: очередь ОСТАЁТСЯ (для отрисовки), целей нет
                Vector3 target = ch.Points[ch.Index].ToLocalPosition();
                Vector3 pos = unit.transform.position;
                float dist = new Vector2(pos.x - target.x, pos.z - target.z).magnitude;
                if (dist > ArriveM)
                    continue;

                ch.Index++;
                if (ch.Index < ch.Points.Count)
                    unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
                // цепочка пройдена: очередь ОСТАЁТСЯ (для отрисовки), тик её больше не трогает
                // (дозревшие цели поднимет PromoteDue на следующем тике)
            }
            if (done == null)
                return;
            foreach (Unit unit in done)
                Chains.Remove(unit);
        }

        /// <summary>Поднять ДОЗРЕВШУЮ атаку: цель исполняется на СВОЕЙ позиции серии
        /// (точка → цель → точка), ДО движений, кликнутых после неё. Мёртвые головы
        /// очереди уходят; true — цепочка стала атакующей (подлёт уже выдан).</summary>
        private static bool PromoteDue(Unit unit, Chain ch)
        {
            bool promoted = false;
            while (ch.TargetQueue.Count > 0)
            {
                if (!TargetDue(ch.Index, ch.TargetAfter[0], ch.Points.Count))
                    break; // позиция серии ещё впереди — едем по маршруту
                Unit target = ch.TargetQueue[0];
                if (target == null || target.disabled)
                {
                    ch.TargetQueue.RemoveAt(0);
                    ch.TargetAfter.RemoveAt(0);
                    Version++;
                    continue;
                }
                if (!promoted)
                {
                    ch.ResumeKind = ch.Kind; // после атаки — прежний вид цепочки (Move/AttackMove)
                    ch.Kind = GroundOrderKind.Attack;
                    ch.Target = target;
                    ch.Holding = false;
                    ch.LastPoint = default;
                    IssuePursuit(unit, ch, PursueRange(unit, target), force: true);
                    Version++;
                    promoted = true;
                }
                break; // остальные цели — на своих позициях, серия поднимется по мере смерти
            }
            return promoted;
        }

        /// <summary>Stop-and-fire атака-движения: цель в дальности ПРИГОДНОГО вооружения
        /// (по даталинку) → стоп и нативный огонь турелей; цель ушла/уничтожена →
        /// продолжаем маршрут. Корабли не останавливаются (дерутся на ходу: нативный
        /// «стоп» кораблю = SetDestination в себя — корабль не умеет стоять и начинает
        /// циркулировать вокруг точки).</summary>
        private static void TickStopAndFire(Unit unit, Chain ch, float now)
        {
            if (unit is Ship)
                return; // флот: огонь на ходу, без остановок
            if (now < ch.NextScanAt)
                return;
            ch.NextScanAt = now + ScanIntervalSec;
            Unit enemy = NearestEngageableEnemy(unit);
            if (enemy != null)
            {
                if (!ch.Holding)
                {
                    ch.Holding = true;
                    StopUnit(unit);
                }
            }
            else if (ch.Holding)
            {
                ch.Holding = false;
                if (ch.Index < ch.Points.Count)
                    unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
            }
        }

        /// <summary>Преследование цели: мертва — следующая ДОЗРЕВШАЯ цель серии, иначе
        /// продолжение маршрута с точки, на которой атака началась (точки, кликнутые
        /// после цели); в дальности вооружения — стоп и нативный огонь; далеко — точка
        /// подхода на границе дальности (обновляется по движению цели).
        /// true = цепочка завершена — вызывающий тик удаляет её ОТЛОЖЕННО (Remove внутри
        /// foreach по Chains бросает InvalidOperationException и обрывает тик группы).</summary>
        private static bool TickPursuit(Unit unit, Chain ch, float now)
        {
            if (ch.Target == null || ch.Target.disabled)
            {
                // Серия целей (Shift+ПКМ): мёртвая голова очереди уходит, следующая
                // берётся ТОЛЬКО если её позиция серии уже достигнута — погоня
                // продолжается без переигровки.
                while (ch.TargetQueue.Count > 0 &&
                       (ch.TargetQueue[0] == null || ch.TargetQueue[0].disabled))
                {
                    ch.TargetQueue.RemoveAt(0);
                    ch.TargetAfter.RemoveAt(0);
                }
                if (ch.TargetQueue.Count > 0 &&
                    TargetDue(ch.Index, ch.TargetAfter[0], ch.Points.Count) &&
                    ch.TargetQueue[0] != ch.Target)
                {
                    ch.Target = ch.TargetQueue[0];
                    ch.Holding = false;
                    ch.LastPoint = default;
                    IssuePursuit(unit, ch, PursueRange(unit, ch.Target), force: true);
                    Version++;
                    return false;
                }
                if (ch.Index < ch.Points.Count)
                {
                    // Атака на своей позиции серии закончилась — маршрут продолжается
                    // (точки, кликнутые ПОСЛЕ цели в Shift-серии).
                    ch.Kind = ch.ResumeKind;
                    ch.Target = null;
                    ch.Holding = false;
                    unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
                    Version++;
                    return false;
                }
                return true; // цель мертва, продолжений нет — цепочка завершена
            }
            if (now < ch.NextScanAt)
                return false;
            ch.NextScanAt = now + ScanIntervalSec;

            Vector3 tp = ch.Target.transform.position;
            Vector3 up = unit.transform.position;
            float dist = new Vector2(up.x - tp.x, up.z - tp.z).magnitude - ch.Target.maxRadius;
            float range = EngageRange(unit, ch.Target);
            if (range < 1f)
                range = ArriveM; // безоружному/без пригодного оружия приказ атаки = доехать до цели

            if (dist <= range * 0.85f)
            {
                // В дальности пригодного вооружения: стоим (точка подхода уже довела
                // сюда, нативная остановка на ней), огонь — нативные турели. Никаких
                // «стоп»-приказов: кораблю они разворачивают циркуляцию.
                ch.Holding = true;
                return false;
            }
            ch.Holding = false;
            IssuePursuit(unit, ch, range, force: false);
            return false;
        }

        /// <summary>Точка подхода: на дистанции min(0.9×текущей, 0.8×дальности ПРИГОДНОГО
        /// вооружения) от цели — НИКОГДА не дальше текущей позиции (приказ «назад»
        /// уводил корабли от берега, который им и был нужен). Азимут — сектор
        /// полукруга групповой атаки (ApproachBearing), одиночной — живой юнит→цель.</summary>
        private static void IssuePursuit(Unit unit, Chain ch, float range, bool force)
        {
            if (ch.Target == null)
                return;
            Vector3 up = unit.transform.position;
            Vector3 tp = ch.Target.transform.position;
            float cur = new Vector2(up.x - tp.x, up.z - tp.z).magnitude;
            float approach = Util.ApproachDistance(cur, range);
            float bearing = ch.ApproachBearing;
            if (float.IsNaN(bearing))
                bearing = Mathf.Atan2(up.x - tp.x, up.z - tp.z);
            Vector3 standoff = new Vector3(tp.x + Mathf.Sin(bearing) * approach, 0f,
                tp.z + Mathf.Cos(bearing) * approach);
            standoff.y = GameApi.GroundY(standoff.x, standoff.z);
            GlobalPosition point = standoff.ToGlobalPosition();
            Vector3 last = ch.LastPoint.ToLocalPosition();
            if (!force && new Vector2(last.x - standoff.x, last.z - standoff.z).magnitude < PursueReissueM)
                return;
            ch.LastPoint = point;
            unit.GetComponent<UnitCommand>()?.SetDestination(point, playerCommand: true);
        }

        private static void StopUnit(Unit unit)
        {
            Vector3 p = unit.transform.position;
            p.y = GameApi.GroundY(p.x, p.z);
            unit.GetComponent<UnitCommand>()?.SetDestination(p.ToGlobalPosition(), playerCommand: true);
        }

        /// <summary>Дальность ПРИГОДНОГО для цели вооружения: максимум дальности станций,
        /// которые могут бить ЭТУ цель (нативный fit-чек CalcOpportunityThreat), а не
        /// любой станции — иначе ПВО-ракеты (землю не бьющие) отгоняли корабль от берега,
        /// который он и должен был обстрелять AGM.</summary>
        private static float EngageRange(Unit unit, Unit target)
        {
            float r = 0f;
            if (target == null || target.definition == null)
                return 0f;
            foreach (WeaponStation st in unit.weaponStations)
            {
                if (st == null || st.Cargo || st.WeaponInfo == null || st.Ammo <= 0)
                    continue;
                if (st.CalcOpportunityThreat(target.definition, unit).opportunity <= 0f)
                    continue; // этим оружием цель не бьётся
                r = Mathf.Max(r, st.WeaponInfo.targetRequirements.maxRange);
            }
            return r;
        }

        /// <summary>Ближайший враг, которого юнит реально может ОБСТРЕЛЯТЬ (fit-оружие в
        /// его дальности) — только видимый по даталинку (турели нативно стреляют лишь
        /// по трекнутым целям). Иначе танк с ПВО-станциями останавливался перед
        /// наземкой, которую бить нечем.</summary>
        private static Unit NearestEngageableEnemy(Unit unit)
        {
            FactionHQ hq = unit.NetworkHQ;
            float scan = unit.GetMaxRange();
            if (hq == null || scan < 1f)
                return null;
            Unit best = null;
            float bestDist = float.MaxValue;
            foreach (Unit enemy in BattlefieldGrid.GetUnitsInRangeEnumerable(unit.GlobalPosition(), scan))
            {
                if (enemy == null || enemy.disabled || enemy.NetworkHQ == null || enemy.NetworkHQ == hq)
                    continue;
                if (!(enemy is GroundVehicle || enemy is Ship || enemy is Aircraft || enemy is Building))
                    continue;
                if (hq.GetTrackingData(enemy.persistentID) == null)
                    continue; // даталинк: невидимого не видим
                Vector3 d = enemy.transform.position - unit.transform.position;
                float dist = new Vector2(d.x, d.z).magnitude - enemy.maxRadius;
                if (dist > EngageRange(unit, enemy))
                    continue; // бить нечем/не достаёт — не останавливаемся
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = enemy;
                }
            }
            return best;
        }

        /// <summary>Удалить точку цепочки (Ctrl+Shift+ПКМ). Если удалена текущая —
        /// юнит сразу получает следующую; хвост позади текущей сдвигается. Позиции
        /// целей серии после удалённой точки сдвигаются вместе с очередью. Точки
        /// атаки (кресты на целях) здесь не живут — только реальные движения.</summary>
        public static void RemovePoint(Unit unit, int index)
        {
            Version++;
            if (!Chains.TryGetValue(unit, out Chain ch) || index < 0 || index >= ch.Points.Count)
                return;
            int cur = ch.Index;
            ch.Points.RemoveAt(index);
            ch.PointGroups.RemoveAt(index);
            for (int t = 0; t < ch.TargetAfter.Count; t++)
                if (ch.TargetAfter[t] > index)
                    ch.TargetAfter[t]--;
            if (index < cur)
                ch.Index = cur - 1;
            else if (index == cur)
            {
                ch.Index = cur;
                if (ch.Kind != GroundOrderKind.Attack && cur < ch.Points.Count)
                    unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[cur], playerCommand: true);
            }
        }

        /// <summary>Перетаскивание точки наземной цепочки (атакуемую цель не двигаем;
        /// во время атаки следующая точка не переназначается — погоня главнее).</summary>
        public static void SetQueuePoint(Unit unit, int index, Vector3 groundPoint)
        {
            Version++;
            if (!Chains.TryGetValue(unit, out Chain ch))
                return;
            if (index < 0 || index >= ch.Points.Count)
                return;
            float y = GameApi.GroundY(groundPoint.x, groundPoint.z);
            ch.Points[index] = new Vector3(groundPoint.x, y, groundPoint.z).ToGlobalPosition();
            if (index == ch.Index && ch.Kind != GroundOrderKind.Attack)
                unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[index], playerCommand: true);
        }

        public static void CollectAll(List<Unit> units, List<IReadOnlyList<GlobalPosition>> queues, List<int> indices)
        {
            units.Clear();
            queues.Clear();
            indices.Clear();
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                // Цепочки без точек, но с целями (атака без хвоста), тоже отдаются:
                // отрисовка атакующих ведёт кресты по gUnits этого списка.
                if (kv.Key == null || kv.Key.disabled ||
                    (kv.Value.Points.Count == 0 && kv.Value.TargetQueue.Count == 0))
                    continue;
                units.Add(kv.Key);
                queues.Add(kv.Value.Points);
                indices.Add(kv.Value.Index);
            }
        }

        public static bool TryGetQueue(Unit unit, out IReadOnlyList<GlobalPosition> points, out int index)
        {
            points = null;
            index = 0;
            if (!Chains.TryGetValue(unit, out Chain ch) || ch.Points.Count == 0)
                return false;
            points = ch.Points;
            index = ch.Index;
            return true;
        }

        /// <summary>Группа приказа точки (0 — индивидуальная).</summary>
        public static int GroupIdOf(Unit unit, int index)
        {
            return Chains.TryGetValue(unit, out Chain ch) &&
                   index >= 0 && index < ch.PointGroups.Count ? ch.PointGroups[index] : 0;
        }

        /// <summary>Члены группового приказа среди ОСТАВШИХСЯ точек всех цепочек:
        /// для каждой цепочки с точкой groupId — юнит и её индекс (индексы у каждого свои).</summary>
        public static void CollectGroup(int groupId, List<Unit> units, List<int> indices)
        {
            units.Clear();
            indices.Clear();
            if (groupId == 0)
                return;
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                Chain ch = kv.Value;
                for (int i = ch.Index; i < ch.PointGroups.Count; i++)
                    if (ch.PointGroups[i] == groupId)
                    {
                        units.Add(kv.Key);
                        indices.Add(i);
                        break;
                    }
            }
        }

        /// <summary>Удалить все точки группы приказа у юнита (групповое удаление —
        /// у всех членов группы, а не только выделенных). Цели серии живут своей
        /// очередью: цепочка без точек, но с целями остаётся (атаки дозреют).</summary>
        public static void RemoveByGroupId(Unit unit, int groupId)
        {
            if (groupId == 0)
                return;
            Version++;
            if (!Chains.TryGetValue(unit, out Chain ch))
                return;
            for (int i = ch.PointGroups.Count - 1; i >= 0; i--)
                if (ch.PointGroups[i] == groupId)
                {
                    ch.Points.RemoveAt(i);
                    ch.PointGroups.RemoveAt(i);
                    for (int t = 0; t < ch.TargetAfter.Count; t++)
                        if (ch.TargetAfter[t] > i)
                            ch.TargetAfter[t]--;
                    if (ch.Index > i)
                        ch.Index--;
                }
            if (ch.Points.Count == 0 && ch.TargetQueue.Count == 0)
            {
                Chains.Remove(unit);
                return;
            }
            if (ch.Index < ch.Points.Count && ch.Kind != GroundOrderKind.Attack)
                unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
        }

        /// <summary>Disperse Move: первая ОСТАВШАЯСЯ точка цепочки с groupId
        /// (индекс ≥ Index — пройденные не считаются; 0 — индивидуальная, не
        /// распределяется). Чистая — тестируется.</summary>
        public static int DispersePick(Chain ch, int groupId)
        {
            if (groupId == 0)
                return -1;
            for (int i = ch.Index; i < ch.PointGroups.Count; i++)
                if (ch.PointGroups[i] == groupId)
                    return i;
            return -1;
        }

        /// <summary>Disperse Move (Alt+Shift+R): остаток очереди юнита заменяется
        /// ОДНОЙ точкой — точкой группы groupId (группы у юнита нет — последней
        /// оставшейся), прямой вектор вместо маршрута. Атака-погоня не участвует.
        /// false — нечего распределять.</summary>
        public static bool DisperseKeep(Unit unit, int groupId)
        {
            if (!Chains.TryGetValue(unit, out Chain ch) || ch.Kind == GroundOrderKind.Attack)
                return false;
            int keep = DispersePick(ch, groupId);
            if (keep < 0)
                keep = ch.Points.Count - 1; // своей группы нет — прямой вектор к последней точке
            if (keep < ch.Index)
                return false; // очередь уже пройдена
            Version++;
            if (keep > ch.Index)
            {
                // срезаем точки между текущей и выжившей (индивидуальные вклинивания)
                ch.Points.RemoveRange(ch.Index, keep - ch.Index);
                ch.PointGroups.RemoveRange(ch.Index, keep - ch.Index);
            }
            if (ch.Points.Count > ch.Index + 1)
            {
                int tail = ch.Points.Count - ch.Index - 1;
                ch.Points.RemoveRange(ch.Index + 1, tail);
                ch.PointGroups.RemoveRange(ch.Index + 1, tail);
            }
            ch.Holding = false; // stop-and-fire атакы-движения снимается на новый вектор
            unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
            return true;
        }

        public static void Prune()
        {
            List<Unit> dead = null;
            foreach (Unit unit in Chains.Keys)
                if (unit == null || unit.disabled)
                    (dead ??= new List<Unit>()).Add(unit);
            if (dead == null)
                return;
            foreach (Unit unit in dead)
                Clear(unit);
        }
    }
}
