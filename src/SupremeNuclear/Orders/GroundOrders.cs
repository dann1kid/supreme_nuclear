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
        Attack,
        /// <summary>Патруль (v0.95): цикл — точки проходятся по кругу, последняя
        /// замыкается на первую. Ноги — как атака-движение (stop-and-fire; флот
        /// дерётся на ходу). Инженерам патруль даёт авто-достройку близлежащих
        /// недостроев (BuildTasks, SupCom).</summary>
        Patrol,
        /// <summary>Сопровождение союзного юнита (v0.97, SupCom escort/assist):
        /// следование за целью СВОИМ сектором (веер группы), рядом с целью —
        /// защита как атака-движение (stop-and-fire, флот дерётся на ходу).
        /// Инженеры в сопровождении ассистят стройкам цели (BuildTasks).</summary>
        Escort
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
        private const float EscortReissueM = 15f;   // якорь сопровождения ушел дальше — перевыдача (скользит по следу, частые мелкие шаги)
        private const float EscortSettleSec = 2f;   // цель стоит дольше — охрана собирается кольцом вокруг (а не стоит на следе)

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
            /// <summary>Escort: цель сопровождения (живёт в цепочке отдельно от
            /// Target атаки — смешанная очередь атак не должна её терять).</summary>
            public Unit EscortTarget;
        }

        private static readonly Dictionary<Unit, Chain> Chains = new Dictionary<Unit, Chain>();

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

        /// <summary>Юнит без активных приказов: непройденных точек нет И целей нет.
        /// Атака/очередь целей — занят даже при пустой цепочке точек (раньше атаку
        /// «занятой» делала производная точка подхода — её больше нет).
        /// Сопровождение — стоящий приказ: занят всегда (выталкивание из зоны
        /// стройки не должно рвать связь).</summary>
        public static bool IsIdle(Unit unit) =>
            !Chains.TryGetValue(unit, out Chain ch) ||
            (ch.Kind != GroundOrderKind.Escort &&
             ch.Index >= ch.Points.Count && ch.TargetQueue.Count == 0);

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

        /// <summary>Патруль наземки/флота: цикл точек (последняя замыкается на первую).
        /// Перезаписывает текущий приказ (как любой новый приказ без Shift).</summary>
        public static void SetPatrol(Unit unit, GlobalPosition destination, int groupId = 0)
        {
            Version++;
            var ch = new Chain { Kind = GroundOrderKind.Patrol };
            ch.Points.Add(destination);
            ch.PointGroups.Add(groupId);
            Chains[unit] = ch;
            unit.GetComponent<UnitCommand>()?.SetDestination(destination, playerCommand: true);
        }

        /// <summary>Дописать точку в патрульный цикл (Shift); юнит не в патруле —
        /// патруль начинается с этой точки (смешать цикл с линейной очередью нельзя).</summary>
        public static void EnqueuePatrol(Unit unit, GlobalPosition destination, int groupId = 0)
        {
            if (Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Patrol)
            {
                Version++;
                ch.Points.Add(destination);
                ch.PointGroups.Add(groupId);
                return;
            }
            SetPatrol(unit, destination, groupId);
        }

        /// <summary>ПКМ по вейпоинту цепочки: маршрут замыкается в патрульный цикл.
        /// Атака (погоня) не конвертится; меньше двух точек замыкать нечего.</summary>
        public static bool ConvertToPatrol(Unit unit)
        {
            if (!Chains.TryGetValue(unit, out Chain ch) ||
                ch.Kind == GroundOrderKind.Attack || ch.Kind == GroundOrderKind.Patrol ||
                ch.Points.Count < 2)
                return false;
            Version++;
            ch.Kind = GroundOrderKind.Patrol;
            return true;
        }

        /// <summary>Снимок патрульной цепочки (авто-достройка снимает патруль на время
        /// стройки и возвращает после). true — юнит в живом патрульном цикле.</summary>
        public static bool TryGetPatrolSnapshot(Unit unit, List<GlobalPosition> points,
            List<int> groups, out int index)
        {
            points.Clear();
            groups.Clear();
            index = 0;
            if (!Chains.TryGetValue(unit, out Chain ch) ||
                ch.Kind != GroundOrderKind.Patrol || ch.Points.Count == 0)
                return false;
            points.AddRange(ch.Points);
            groups.AddRange(ch.PointGroups);
            index = ch.Index;
            return true;
        }

        /// <summary>Вернуть снятый патруль (восстановление после авто-достройки):
        /// цикл ставится на сохранённую точку маршрута.</summary>
        public static void RestorePatrol(Unit unit, IReadOnlyList<GlobalPosition> points,
            IReadOnlyList<int> groups, int index)
        {
            if (points == null || points.Count == 0)
                return;
            Version++;
            var ch = new Chain
            {
                Kind = GroundOrderKind.Patrol,
                Index = Mathf.Clamp(index, 0, points.Count - 1)
            };
            ch.Points.AddRange(points);
            if (groups != null)
                ch.PointGroups.AddRange(groups);
            Chains[unit] = ch;
            unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
        }

        /// <summary>Юниты в живом патрульном цикле (скан авто-достройки).</summary>
        public static void CollectPatrols(List<Unit> units)
        {
            units.Clear();
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
                if (kv.Key != null && !kv.Key.disabled &&
                    kv.Value.Kind == GroundOrderKind.Patrol && kv.Value.Points.Count > 0)
                    units.Add(kv.Key);
        }

        // == СОПРОВОЖДЕНИЕ (v0.97, SupCom escort/assist): вейпоинт «привязан к
        // союзному юниту» — следование за целью своим сектором (веер группы,
        // ApproachBearing), рядом с целью защита stop-and-fire (флот дерётся на
        // ходу), инженеры ассистят стройкам цели (BuildTasks.TickEscortAssist).

        /// <summary>Приказ сопровождения (перезапись текущего, как любой приказ
        /// без Shift). bearing — сектор веера группы (NaN — живой азимут).</summary>
        public static void SetEscort(Unit unit, Unit target, float bearing = float.NaN)
        {
            Version++;
            var ch = new Chain
            {
                Kind = GroundOrderKind.Escort,
                EscortTarget = target,
                ApproachBearing = bearing
            };
            Chains[unit] = ch;
            IssueFollow(unit, ch, force: true);
        }

        /// <summary>Снапшот сопровождения (эскорт-ассист снимает связь на время
        /// стройки и возвращает после).</summary>
        public static bool TryGetEscortSnapshot(Unit unit, out Unit target, out float bearing)
        {
            target = null;
            bearing = float.NaN;
            if (!Chains.TryGetValue(unit, out Chain ch) ||
                ch.Kind != GroundOrderKind.Escort || ch.EscortTarget == null)
                return false;
            target = ch.EscortTarget;
            bearing = ch.ApproachBearing;
            return true;
        }

        /// <summary>Вернуть снятое сопровождение (после эскорт-ассиста).</summary>
        public static void RestoreEscort(Unit unit, Unit target, float bearing)
        {
            if (target == null)
                return;
            SetEscort(unit, target, bearing);
        }

        /// <summary>Живые связи сопровождения: пары «эскорт → цель» (отрисовка,
        /// скан вейпоинта-связи, отмена по цели).</summary>
        public static void CollectEscorts(List<Unit> units, List<Unit> targets)
        {
            units.Clear();
            targets.Clear();
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                if (kv.Key == null || kv.Key.disabled ||
                    kv.Value.Kind != GroundOrderKind.Escort || kv.Value.EscortTarget == null)
                    continue;
                units.Add(kv.Key);
                targets.Add(kv.Value.EscortTarget);
            }
        }

        /// <summary>Снять сопровождение ВСЕХ юнитов, следующих за целью
        /// (Ctrl+Shift+ПКМ по связи). Возвращает число снятых связей.</summary>
        public static int CancelEscortOf(Unit target)
        {
            List<Unit> drop = null;
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
                if (kv.Value.Kind == GroundOrderKind.Escort && kv.Value.EscortTarget == target)
                    (drop ??= new List<Unit>()).Add(kv.Key);
            if (drop == null)
                return 0;
            foreach (Unit unit in drop)
                Clear(unit);
            return drop.Count;
        }

        /// <summary>Точка следования: якорь скользит ПО СЛЕДУ ЦЕЛИ (Catmull-Rom по
        /// breadcrumbs) — колонна идёт по траектории цели плавно, без рывков между
        /// перевыдачами; деградации в кольцо вокруг текущей позиции — следа нет,
        /// цель стоит (охрана собирается вокруг), эскорт далеко (догоняет напрямую).
        /// Перевыдача — когда якорь уехал дальше EscortReissueM.</summary>
        private static void IssueFollow(Unit unit, Chain ch, bool force)
        {
            if (ch.EscortTarget == null)
                return;
            Unit target = ch.EscortTarget;
            Vector3 tp = target.transform.position;
            float standoff = Settings.EscortStandoffM + target.maxRadius + unit.maxRadius;
            float bearing = ch.ApproachBearing;
            if (float.IsNaN(bearing))
                bearing = Mathf.Atan2(unit.transform.position.x - tp.x, unit.transform.position.z - tp.z);
            Vector3 follow = FollowAnchor(unit, target, tp, standoff, bearing);
            follow.y = GameApi.GroundY(follow.x, follow.z);
            GlobalPosition point = follow.ToGlobalPosition();
            Vector3 last = ch.LastPoint.ToLocalPosition();
            if (!force && new Vector2(last.x - follow.x, last.z - follow.z).magnitude < EscortReissueM)
                return;
            ch.LastPoint = point;
            unit.GetComponent<UnitCommand>()?.SetDestination(point, playerCommand: true);
        }

        /// <summary>Якорь следования эскорта. Базовый режим — ПЛАВНО ПО СЛЕДУ: точка
        /// на сплайне-следе (Util.TrailPointAt) на скорректированной дуге позади
        /// цели + боковое смещение сектора веера (перпендикуляр курсу следа):
        /// сектор «в голову колонны» — ближе к цели, «в хвост» — дальше, фланги —
        /// сбоку от траектории. Деградация в кольцо вокруг текущей позиции цели
        /// (прежнее поведение): следа нет/короче дуги, цель стоит дольше
        /// EscortSettleSec, эскорт далеко (догоняет прямым вектором).</summary>
        private static Vector3 FollowAnchor(Unit unit, Unit target, Vector3 tp,
            float standoff, float bearing)
        {
            Vector3 ring = new Vector3(tp.x + Mathf.Sin(bearing) * standoff, 0f,
                tp.z + Mathf.Cos(bearing) * standoff);
            if (!EscortTrails.TryGetValue(target, out EscortTrail trail) || trail.Points.Count == 0)
                return ring;
            if (Time.time - trail.LastMoveAt > EscortSettleSec)
                return ring; // цель стоит — охрана собирается кольцом, а не толпится на следе
            Vector3 up = unit.transform.position;
            float dist = new Vector2(up.x - tp.x, up.z - tp.z).magnitude;
            if (dist > standoff * 2.5f + ArriveM)
                return ring; // догоняет — прямой вектор быстрее хвоста маршрута
            TrailBuf.Clear();
            for (int i = 0; i < trail.Points.Count; i++)
            {
                Vector3 l = trail.Points[i].ToLocalPosition();
                TrailBuf.Add(new Vector3(l.x, 0f, l.z));
            }
            Vector3 tail = new Vector3(tp.x, 0f, tp.z);
            if (!Util.TrailPointAt(TrailBuf, tail, standoff, out Vector3 at, out float heading))
                return ring; // след короче дистанции станции
            // Сектор веера в системе координат следа: дельта bearing−курс →
            // продольная позиция (0.25..1×standoff позади) и боковое смещение.
            float delta = Mathf.Repeat(bearing - heading + Mathf.PI, Mathf.PI * 2f) - Mathf.PI;
            float arc = standoff * Mathf.Clamp((1f - Mathf.Cos(delta)) * 0.5f, 0.25f, 1f);
            float side = standoff * 0.8f * Mathf.Sin(delta);
            if (!Util.TrailPointAt(TrailBuf, tail, arc, out at, out _))
                return ring;
            Vector3 anchor = new Vector3(at.x + Mathf.Cos(heading) * side, 0f,
                at.z - Mathf.Sin(heading) * side);
            return anchor;
        }

        // == СЛЕД ЦЕЛИ СОПРОВОЖДЕНИЯ (v0.98): breadcrumbs траектории цели (шаг
        // EscortTrailSampleM, длина EscortTrailLengthM, GlobalPosition — origin-proof),
        // по ним ведётся Catmull-Rom-якорь эскортов (FollowAnchor). Реестр живёт,
        // пока цель сопровождают (чистка по LastTouchAt в Prune).

        private sealed class EscortTrail
        {
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            public GlobalPosition LastSample;   // позиция последнего сэмпла (для порога шага)
            public float LastMoveAt = -999f;    // Time.time последнего продвижения цели
            public float LastTouchAt;           // последний опрос живым эскортом (чистка)
        }

        private static readonly Dictionary<Unit, EscortTrail> EscortTrails =
            new Dictionary<Unit, EscortTrail>();
        private static readonly List<Vector3> TrailBuf = new List<Vector3>();

        /// <summary>Сэмплировать след цели (идемпотентно: несколько эскортов одной
        /// цели пишут один след). Первый опрос регистрирует цель без точки — якорь
        /// до первого сдвига цели берётся кольцом.</summary>
        private static void SampleEscortTrail(Unit target)
        {
            if (!EscortTrails.TryGetValue(target, out EscortTrail trail))
            {
                trail = new EscortTrail
                {
                    LastSample = target.transform.position.ToGlobalPosition(),
                    LastTouchAt = Time.time
                };
                EscortTrails[target] = trail;
                return;
            }
            trail.LastTouchAt = Time.time;
            Vector3 tp = target.transform.position;
            Vector3 last = trail.LastSample.ToLocalPosition();
            float step = Settings.EscortTrailSampleM;
            if (new Vector2(tp.x - last.x, tp.z - last.z).sqrMagnitude < step * step)
                return;
            trail.Points.Add(new Vector3(tp.x, 0f, tp.z).ToGlobalPosition());
            trail.LastSample = tp.ToGlobalPosition();
            trail.LastMoveAt = Time.time;
            // Длина следа ограничена: старая голова срезается (хвост актуальнее).
            float budget = Settings.EscortTrailLengthM;
            float acc = 0f;
            int keepFrom = 0;
            for (int i = trail.Points.Count - 1; i > 0; i--)
            {
                Vector3 a = trail.Points[i].ToLocalPosition();
                Vector3 b = trail.Points[i - 1].ToLocalPosition();
                acc += new Vector2(a.x - b.x, a.z - b.z).magnitude;
                if (acc > budget)
                {
                    keepFrom = i;
                    break;
                }
            }
            if (keepFrom > 0)
                trail.Points.RemoveRange(0, keepFrom);
        }

        /// <summary>Чистка следов: цель погибла/пропала или её давно никто не
        /// сопровождает (LastTouchAt).</summary>
        private static void PruneEscortTrails()
        {
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, EscortTrail> kv in EscortTrails)
                if (kv.Key == null || kv.Key.disabled || Time.time - kv.Value.LastTouchAt > 15f)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead == null)
                return;
            foreach (Unit key in dead)
                EscortTrails.Remove(key);
        }

        /// <summary>Тик сопровождения: цель жива — следовать/стоять-и-стрелять.
        /// Стоп-энд-файр — только ДОГНАВ цель (не отстаём из-за боя на полкарты);
        /// корабли не останавливаются (дерутся на ходу). true = цель погибла —
        /// цепочка завершена (удаление отложено вызывающим тиком).</summary>
        private static bool TickEscort(Unit unit, Chain ch, float now)
        {
            if (ch.EscortTarget == null || ch.EscortTarget.disabled)
                return true;
            if (now < ch.NextScanAt)
                return false;
            ch.NextScanAt = now + ScanIntervalSec;
            SampleEscortTrail(ch.EscortTarget); // идемпотентно: одна цель — один след
            Vector3 tp = ch.EscortTarget.transform.position;
            Vector3 up = unit.transform.position;
            float dist = new Vector2(up.x - tp.x, up.z - tp.z).magnitude - ch.EscortTarget.maxRadius;
            bool near = dist <= Settings.EscortStandoffM * 2.5f; // догнал: защищаем, но не отстаём
            Unit enemy = near && !(unit is Ship) ? NearestEngageableEnemy(unit) : null;
            if (enemy != null)
            {
                if (!ch.Holding)
                {
                    ch.Holding = true;
                    StopUnit(unit);
                }
                return false; // стоят и стреляют; флот прошёл мимо — дерётся на ходу
            }
            ch.Holding = false;
            IssueFollow(unit, ch, force: false);
            return false;
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
            EscortTrails.Clear();
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
                if (ch.Kind == GroundOrderKind.Escort)
                {
                    if (TickEscort(unit, ch, now))
                        (done ??= new List<Unit>()).Add(unit);
                    continue;
                }
                if (ch.Kind == GroundOrderKind.AttackMove || ch.Kind == GroundOrderKind.Patrol)
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
                if (ch.Kind == GroundOrderKind.Patrol && ch.Index >= ch.Points.Count)
                    ch.Index = 0; // цикл: последняя точка замыкается на первую
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
                // Атака из сопровождения (EnqueueAttack поверх эскорта): цель серии
                // погибла — сопровождение ЖИВОЙ цели продолжается.
                if (ch.ResumeKind == GroundOrderKind.Escort &&
                    ch.EscortTarget != null && !ch.EscortTarget.disabled)
                {
                    ch.Kind = GroundOrderKind.Escort;
                    ch.Target = null;
                    ch.Holding = false;
                    IssueFollow(unit, ch, force: true);
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

        /// <summary>Ядро удаления точки (общее для одиночного и группового удаления):
        /// снос точки, сдвиг позиций серий целей (TargetAfter), корректировка
        /// курсора — при удалении точки ДО текущей он сдвигается, при удалении
        /// текущей остаётся, указывая на следующую. Чистая — тестируется.</summary>
        public static void RemoveAtCore(Chain ch, int index)
        {
            if (index < 0 || index >= ch.Points.Count)
                return;
            ch.Points.RemoveAt(index);
            ch.PointGroups.RemoveAt(index);
            for (int t = 0; t < ch.TargetAfter.Count; t++)
                if (ch.TargetAfter[t] > index)
                    ch.TargetAfter[t]--;
            if (index < ch.Index)
                ch.Index--;
        }

        /// <summary>Удалить точку цепочки (Ctrl+Shift+ПКМ). Единые с групповым
        /// удалением правила: опустевшая цепочка без целей сносится сразу,
        /// выжившая — перевыдаёт текущую точку (идемпотентно, нативный дедуп).
        /// Позиции целей серии после удалённой точки сдвигаются вместе с
        /// очередью. Точки атаки (кресты на целях) здесь не живут.</summary>
        public static void RemovePoint(Unit unit, int index)
        {
            Version++;
            if (!Chains.TryGetValue(unit, out Chain ch))
                return;
            RemoveAtCore(ch, index);
            if (ch.Points.Count == 0 && ch.TargetQueue.Count == 0)
            {
                Chains.Remove(unit);
                return;
            }
            if (ch.Index < ch.Points.Count && ch.Kind != GroundOrderKind.Attack)
                unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[ch.Index], playerCommand: true);
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
                if (kv.Key == null || kv.Key.disabled)
                    continue;
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
                    RemoveAtCore(ch, i);
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
            if (!Chains.TryGetValue(unit, out Chain ch) ||
                ch.Kind == GroundOrderKind.Attack || ch.Kind == GroundOrderKind.Patrol ||
                ch.Kind == GroundOrderKind.Escort)
                return false; // погоня, цикл патруля и сопровождение не раскладываются в прямой вектор
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

        /// <summary>Disperse целей (Alt+Shift+R): очередь целей юнита := ОДНА назначенная
        /// цель. Атакующая цепочка переключает погоню немедленно (по образцу
        /// TickPursuit: Holding/LastPoint сброс, перевыдача подхода force); ждущая
        /// цель смешанной очереди встаёт за всеми оставшимися точками. false —
        /// назначенная цель не из очереди юнита (чужая серия — её не портим).</summary>
        public static bool DisperseTargets(Unit unit, Unit target)
        {
            if (target == null || target.disabled || !Chains.TryGetValue(unit, out Chain ch) ||
                !ch.TargetQueue.Contains(target))
                return false;
            Version++;
            ch.TargetQueue.Clear();
            ch.TargetAfter.Clear();
            ch.TargetQueue.Add(target);
            if (ch.Kind == GroundOrderKind.Attack)
            {
                ch.TargetAfter.Add(ch.Index); // позиция уже достигнута — цель без задержки
                if (ch.Target != target)
                {
                    ch.Target = target;
                    ch.Holding = false;
                    ch.LastPoint = default;
                    IssuePursuit(unit, ch, PursueRange(unit, target), force: true);
                }
            }
            else
                ch.TargetAfter.Add(ch.Points.Count); // за всеми оставшимися точками
            return true;
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
            PruneEscortTrails();
        }
    }
}
