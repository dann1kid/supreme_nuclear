using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{

    /// <summary>Цепочки вейпоинтов для наземки/флота: SetDestination выдаёт следующую точку,
    /// когда юнит доехал до текущей (90 м). Все точки — GlobalPosition (origin-proof).
    /// AttackMove — со стоп-энд-файр; Attack — преследование цели без цепочки.
    /// Вид приказа несёт КАЖДАЯ ТОЧКА (PointKinds, канон SupCom/Moho v0.114):
    /// смешанная Shift-серия «движение + атака-движение» в одной ленте легальна,
    /// Kind линейной цепочки = вид текущей точки; конвертация в патруль замыкает
    /// ХВОСТ от пикнутой точки (PatrolLoopStart).
    /// Смешанные Shift-серии с целями исполняются В ПОРЯДКЕ КЛИКОВ (точка → цель → точка),
    /// как планы авиации: каждая цель несёт позицию серии (TargetAfter — сколько
    /// точек стояло в очереди на момент клика), дозревшая атака поднимается
    /// промоушеном, после её смерти маршрут продолжается с места промоушена.
    /// Каждая точка несёт groupId клика (0 — индивидуальный приказ): групповой вейпоинт —
    /// свойство ТОЧКИ, а не выделения: маркер/лента/драг/удаление — на всю группу приказа.</summary>
    public static partial class GroundOrders
    {
        private const float ArriveM = 90f;
        private const float ScanIntervalSec = 0.5f; // троттлинг обзора на юнита
        private const float PursueReissueM = 60f;   // цель сдвинулась сильнее — новая точка подхода
        private const float EscortReissueM = 15f;   // якорь сопровождения ушел дальше — перевыдача (скользит по следу, частые мелкие шаги)
        private const float EscortSettleSec = 2f;   // цель стоит дольше — охрана собирается кольцом вокруг (а не стоит на следе)

        /// <summary>Растёт при любом изменении цепочек — инвалидирует меш-ленту.</summary>
        public static int Version;

        public sealed class Chain
        {
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            /// <summary>Группа приказа каждой точки, параллельно Points (0 — индивидуальная).</summary>
            public readonly List<int> PointGroups = new List<int>();
            /// <summary>Вид КАЖДОЙ точки (Move/AttackMove/Patrol), параллельно Points —
            /// как у Moho: каждая точка очереди несёт свой тип приказа, смешанные
            /// Shift-серии «движение + атака-движение» в одной ленте легальны
            /// (канон SupCom, подтверждён исходниками OpenSupCom 2026-10-30).
            /// Инвариант: Kind линейной цепочки = вид ТЕКУЩЕЙ точки (синхронизируется
            /// при продвижении/удалениях через SyncLinearKind).</summary>
            public readonly List<GroundOrderKind> PointKinds = new List<GroundOrderKind>();
            /// <summary>Патруль: индекс точки, с которой начинается цикл (конвертация
            /// «хвостом от точки» замыкает в цикл точки [LoopStart..end); 0 — цикл
            /// всех точек, как у патруля, выданного целиком).</summary>
            public int PatrolLoopStart;
            /// <summary>Поводок патруля (Moho GuardReturnRadius): точка, откуда
            /// патруль ОТОРВАЛСЯ на погоню; войдя в контакт, юнит отказывается от
            /// погони дальше PatrolLeashM от неё и возвращается на маршрут.</summary>
            public GlobalPosition PatrolLeashPoint;
            public bool PatrolLeashSet;
            /// <summary>Поводок «взведён»: юнит вошёл в дальность оружия — с этого
            /// момента погоня дальше PatrolLeashM от якоря бросается (Moho: leash
            /// armed once engaged).</summary>
            public bool PatrolLeashArmed;
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
            /// <summary>Escort: цель сопровождения (живёт в цепочке отдельно от
            /// Target атаки — смешанная очередь атак не должна её терять).</summary>
            public Unit EscortTarget;
            /// <summary>Guard: цель охраны. Дублируется в EscortTarget (follow-модель
            /// эскорта — след/веер/кольцо — работает без копий); Collections и
            /// удаление связей различают их по Kind.</summary>
            public Unit GuardTarget;
            /// <summary>Guard-инженер временно уходит в Repair: после завершения
            /// налива (RepairService → RepairFinished) охрана живой цели
            /// восстанавливается. Прямой Cancel игроком охрану не воскресает.</summary>
            public bool GuardResume;
            /// <summary>Repair: цель ремонта (отдельно от EscortTarget по той же
            /// причине — вид приказа живёт в цепочке, а не в точках).</summary>
            public Unit RepairTarget;
            /// <summary>Очередь эскортов (Shift+ПКМ по союзникам, §12-36а): в
            /// эскортирующей цепочке следующая связь активируется по СМЕРТИ
            /// текущей цели (позиции серии бессмысленны — точек нет); в линейной —
            /// на СВОЕЙ позиции серии (EscortAfter[i], как TargetAfter у целей).</summary>
            public readonly List<Unit> EscortQueue = new List<Unit>();
            /// <summary>Позиция серии каждой ждущей эскорт-связи, параллельно EscortQueue.</summary>
            public readonly List<int> EscortAfter = new List<int>();
        }

        private static readonly Dictionary<Unit, Chain> Chains = new Dictionary<Unit, Chain>();

        // Скоординированная атака (канон FAF: «в обратном порядке скорости» — все
        // бьют одновременно): юнит в погоне за CoordTarget держит огонь/позицию
        // до CoordUntil. Явный приказ/смена цели/смерть цели снимают удержание.
        internal static readonly Dictionary<Unit, (Unit target, float until)> CoordStrike =
            new Dictionary<Unit, (Unit, float)>();

        public static int Count => Chains.Count;

        /// <summary>Вид текущего приказа юнита (нет цепочки — Move).</summary>
        public static GroundOrderKind KindOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) ? ch.Kind : GroundOrderKind.Move;

        /// <summary>Цель атаки юнита (для отрисовки), иначе null.</summary>
        public static Unit TargetOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Attack ? ch.Target : null;

        /// <summary>Цель сопровождения юнита (для отрисовки/ассиста), иначе null.</summary>
        public static Unit EscortTargetOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Escort ? ch.EscortTarget : null;

        /// <summary>Цель ремонта юнита (налив/отрисовка/завершение), иначе null.</summary>
        public static Unit RepairTargetOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Repair ? ch.RepairTarget : null;

        /// <summary>Юнит без активных приказов: непройденных точек нет И целей нет
        /// И ждущих эскорт-связей нет. Атака/очередь целей — занят даже при пустой
        /// цепочке точек (раньше атаку «занятой» делала производная точка подхода —
        /// её больше нет). Сопровождение (живое или ждущее в очереди), охрана и
        /// ремонт — стоящие приказы: занят всегда (выталкивание из зоны стройки
        /// не должно рвать связь).</summary>
        public static bool IsIdle(Unit unit) =>
            !Chains.TryGetValue(unit, out Chain ch) ||
            (ch.Kind != GroundOrderKind.Escort && ch.Kind != GroundOrderKind.Guard &&
             ch.Kind != GroundOrderKind.Repair &&
             ch.Index >= ch.Points.Count && ch.TargetQueue.Count == 0 &&
             ch.EscortQueue.Count == 0);

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
            GroundOrderKind kind = attackMove ? GroundOrderKind.AttackMove : GroundOrderKind.Move;
            var ch = new Chain { Kind = kind };
            ch.Points.Add(destination);
            ch.PointGroups.Add(groupId);
            ch.PointKinds.Add(kind);
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
            // Вид несёт каждая точка (канон SupCom): флаг attackMove больше НЕ
            // теряется — смешанная серия «движение + атака-движение» рисуется
            // ногами своего цвета и исполняется stop-and-fire только на своих ногах.
            ch.Points.Add(destination);
            ch.PointGroups.Add(groupId);
            ch.PointKinds.Add(ch.Kind == GroundOrderKind.Patrol
                ? GroundOrderKind.Patrol
                : attackMove ? GroundOrderKind.AttackMove : GroundOrderKind.Move);
        }

        public static void Clear(Unit unit)
        {
            Version++;
            Chains.Remove(unit);
            CoordStrike.Remove(unit);
            StanceService.SetCoordHold(unit, false);
        }

        /// <summary>Сброс поводков погонь (сцена/явный стоп) — якоря протухают
        /// вместе с координатами сцены.</summary>
        public static void ResetLeashes()
        {
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
                kv.Value.PatrolLeashSet = false;
        }

        /// <summary>Полный сброс (смена сцены): цепочки умершей сцены недействительны.</summary>
        public static void Reset()
        {
            Version++;
            Chains.Clear();
            EscortTrails.Clear();
            EngageRanges.Clear();
            CoordStrike.Clear();
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
                if (ch.Kind != GroundOrderKind.Attack &&
                    ch.Kind != GroundOrderKind.Escort && ch.Kind != GroundOrderKind.Repair &&
                    ch.Kind != GroundOrderKind.Guard && PromoteEscortDue(unit, ch))
                    continue; // дозревший эскорт: цепочка стала сопровождением (§12-36а)
                if (ch.Kind == GroundOrderKind.Attack)
                {
                    // true = цепочка завершена (цель мертва, очередей нет) — удаление
                    // ОТЛОЖЕНО: Remove внутри foreach по Chains даёт InvalidOperationException
                    // и обрывает тик остальной группы.
                    if (TickPursuit(unit, ch, now))
                        (done ??= new List<Unit>()).Add(unit);
                    continue;
                }
                if (ch.Kind == GroundOrderKind.Escort)
                {
                    if (TickEscort(unit, ch, now))
                        (done ??= new List<Unit>()).Add(unit);
                    continue;
                }
                if (ch.Kind == GroundOrderKind.Guard)
                {
                    if (TickGuard(unit, ch, now))
                        (done ??= new List<Unit>()).Add(unit);
                    continue;
                }
                if (ch.Kind == GroundOrderKind.Repair)
                {
                    if (TickRepair(unit, ch, now))
                        (done ??= new List<Unit>()).Add(unit);
                    continue;
                }
                if (ch.Kind == GroundOrderKind.AttackMove)
                {
                    TickStopAndFire(unit, ch, now);
                    if (ch.Holding)
                        continue; // стоят и стреляют — маршрут не продвигается
                }
                else if (ch.Kind == GroundOrderKind.Patrol)
                {
                    // ПОВОДОК патруля (канон Moho CUnitPatrolTask): заметив врага,
                    // патруль ОТРЫВАЕТСЯ на погоню от точки отрыва; войдя в контакт,
                    // дальше PatrolLeashM от якоря не гонится — возвращается на
                    // маршрут (движение/атака-движение остались стоп-энд-файр).
                    if (TickPatrolCombat(unit, ch, now))
                        continue; // погоня поднята — её ведёт TickPursuit
                }

                if (ch.Index >= ch.Points.Count)
                    continue; // пройдено: очередь ОСТАЁТСЯ (для отрисовки), целей нет
                Vector3 target = ch.Points[ch.Index].ToLocalPosition();
                Vector3 pos = unit.transform.position;
                float dist = new Vector2(pos.x - target.x, pos.z - target.z).magnitude;
                if (dist > ArriveM)
                    continue;

                ch.Index++;
                if (ch.Kind == GroundOrderKind.Patrol && ch.Index >= ch.Points.Count)
                    // цикл: последняя точка хвоста замыкается на его начало; кламп —
                    // удалениями точек начало могло выпасть за границу (пустой хвост)
                    ch.Index = Mathf.Clamp(ch.PatrolLoopStart, 0, ch.Points.Count - 1);
                SyncLinearKind(ch); // per-point вид: на новой точке — свои правила (stop-and-fire)
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

        public static void Prune()
        {
            List<Unit> dead = null;
            foreach (Unit unit in Chains.Keys)
                if (unit == null || unit.disabled)
                    (dead ??= new List<Unit>()).Add(unit);
            if (dead != null)
                foreach (Unit unit in dead)
                    Clear(unit);
            // Кэш дальностей оружия мёртвых юнитов (Unity fake-null)
            List<Unit> deadEngage = null;
            foreach (Unit unit in EngageRanges.Keys)
                if (unit == null || unit.disabled)
                    (deadEngage ??= new List<Unit>()).Add(unit);
            if (deadEngage != null)
                foreach (Unit unit in deadEngage)
                    EngageRanges.Remove(unit);
            PruneEscortTrails();
        }
    }
}
