using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть GroundOrders: сопровождение, охрана, ремонт, след цели (follow-модели).
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр цепочек и Tick — GroundOrders.cs.
    public static partial class GroundOrders
    {
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

        /// <summary>Дописать сопровождение в очередь (Shift+ПКМ по союзнику,
        /// §12-36а — зеркально авиации): в линейной цепочке связь активируется на
        /// СВОЕЙ позиции серии (после точек, кликнутых раньше); в эскортирующей —
        /// по СМЕРТИ текущей цели (точек в цепочке нет, позиции серии бессмысленны);
        /// цепочки нет — сразу SetEscort. Инженеру легально наравне с боевыми.</summary>
        public static void EnqueueEscort(Unit unit, Unit target, float bearing = float.NaN)
        {
            Version++;
            if (!Chains.TryGetValue(unit, out Chain ch) ||
                (ch.Kind != GroundOrderKind.Escort &&
                 ch.Points.Count == 0 && ch.TargetQueue.Count == 0))
            {
                SetEscort(unit, target, bearing);
                return;
            }
            ch.EscortQueue.Add(target);
            // Эскорт — стоящий приказ: в эскортирующей цепочке ждать нечего,
            // в атакующей — серия уже достигнута (как EnqueueAttack по ch.Index).
            ch.EscortAfter.Add(ch.Kind == GroundOrderKind.Attack ? ch.Index : ch.Points.Count);
        }

        /// <summary>Смена эскортируемой цели по очереди (текущая погибла/снята).
        /// Мёртвые головы уходят; true — взяли следующую живую связь.</summary>
        private static bool PromoteNextEscort(Unit unit, Chain ch)
        {
            while (ch.EscortQueue.Count > 0)
            {
                Unit next = ch.EscortQueue[0];
                ch.EscortQueue.RemoveAt(0);
                ch.EscortAfter.RemoveAt(0);
                Version++;
                if (next == null || next.disabled)
                    continue;
                ch.EscortTarget = next;
                ch.ApproachBearing = float.NaN; // живой азимут: веер клика не сохранить
                ch.Holding = false;
                ch.NextScanAt = 0f; // первый follow-скан немедленно
                IssueFollow(unit, ch, force: true);
                return true;
            }
            return false;
        }

        /// <summary>Поднять ДОЗРЕВШИЙ эскорт из очереди: линейная цепочка дошла до
        /// его позиции серии (зеркало PromoteDue у целей). Мёртвые головы уходят;
        /// true — цепочка стала сопровождением.</summary>
        private static bool PromoteEscortDue(Unit unit, Chain ch)
        {
            while (ch.EscortQueue.Count > 0)
            {
                if (!TargetDue(ch.Index, ch.EscortAfter[0], ch.Points.Count))
                    break; // позиция серии ещё впереди — едем по маршруту
                Unit target = ch.EscortQueue[0];
                ch.EscortQueue.RemoveAt(0);
                ch.EscortAfter.RemoveAt(0);
                Version++;
                if (target == null || target.disabled)
                    continue;
                ch.ResumeKind = ch.Kind;
                ch.Kind = GroundOrderKind.Escort;
                ch.EscortTarget = target;
                ch.ApproachBearing = float.NaN;
                ch.Holding = false;
                ch.NextScanAt = 0f;
                IssueFollow(unit, ch, force: true);
                return true;
            }
            return false;
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

        /// <summary>Живые и ждущие связи сопровождения: пары «эскорт → цель»
        /// (отрисовка, скан вейпоинта-связи, отмена по цели) — очередь §12-36а
        /// полноправна: ждущая связь рисуется и пикается так же, как живая.
        /// Юнит с несколькими связями (живая + ждущие) отдаётся парами. only —
        /// фильтр своей фракции (как CollectAll: AI делит реестр, чужие связи
        /// не рисуются/не пикаются).</summary>
        public static void CollectEscorts(List<Unit> units, List<Unit> targets, FactionHQ only = null)
        {
            units.Clear();
            targets.Clear();
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                if (kv.Key == null || kv.Key.disabled)
                    continue;
                if (only != null && !GameApi.IsFriendlyUnit(kv.Key, only))
                    continue;
                Chain c = kv.Value;
                if (c.Kind == GroundOrderKind.Escort && c.EscortTarget != null)
                {
                    units.Add(kv.Key);
                    targets.Add(c.EscortTarget);
                }
                for (int i = 0; i < c.EscortQueue.Count; i++)
                    if (c.EscortQueue[i] != null)
                    {
                        units.Add(kv.Key);
                        targets.Add(c.EscortQueue[i]);
                    }
            }
        }

        /// <summary>Снять сопровождение цели у ВСЕХ юнитов (Ctrl+Shift+ПКМ по
        /// связи): живая связь — приказ снимается целиком (стоящий эскорт);
        /// ждущие записи очереди — удаляются, остальная цепочка (точки/цели)
        /// живёт. Возвращает число снятых связей (живых + ждущих).</summary>
        public static int CancelEscortOf(Unit target)
        {
            int removed = 0;
            List<Unit> drop = null;
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                Chain c = kv.Value;
                if (c.Kind == GroundOrderKind.Escort && c.EscortTarget == target)
                {
                    (drop ??= new List<Unit>()).Add(kv.Key);
                    removed++;
                    continue;
                }
                for (int i = c.EscortQueue.Count - 1; i >= 0; i--)
                    if (c.EscortQueue[i] == target)
                    {
                        c.EscortQueue.RemoveAt(i);
                        c.EscortAfter.RemoveAt(i);
                        removed++;
                    }
            }
            if (drop != null)
                foreach (Unit unit in drop)
                    Clear(unit);
            return removed;
        }

        // == ОХРАНА (v0.158, SupCom guard): приказ «охранять союзника» — follow-модель
        // сопровождения (EscortTarget дублирует GuardTarget: след/веер/кольцо работают
        // без копий) + канонные отличия от эскорта: инженер АВТОМАТОМ чинит охраняемого
        // (временный Repair с GuardResume, возврат через RepairFinished), боевое
        // прикрытие ПЕРЕХВАТЫВАЕТ угрозы у цели — враг рядом с ОХРАНЯЕМЫМ поднимает
        // погоню с поводком ОТ ЦЕЛИ (после боя/поводка страж возвращается, как патруль).
        // Guard ≠ Escort зафиксировано в DESIGN §2.11.

        /// <summary>Приказ охраны (перезапись текущего, как любой приказ без Shift).
        /// bearing — сектор веера группы (NaN — живой азимут).</summary>
        public static void SetGuard(Unit unit, Unit target, float bearing = float.NaN)
        {
            Version++;
            var ch = new Chain
            {
                Kind = GroundOrderKind.Guard,
                GuardTarget = target,
                EscortTarget = target, // follow-модель эскорта читает EscortTarget
                ApproachBearing = bearing
            };
            Chains[unit] = ch;
            IssueFollow(unit, ch, force: true);
        }

        /// <summary>Цель охраны юнита (для отрисовки/ассиста), иначе null.</summary>
        public static Unit GuardTargetOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Guard
                ? ch.GuardTarget
                : null;

        /// <summary>Живые связи охраны: пары «страж → цель» (отрисовка, скан
        /// вейпоинта-связи, отмена по цели). only — фильтр своей фракции.</summary>
        public static void CollectGuards(List<Unit> units, List<Unit> targets, FactionHQ only = null)
        {
            units.Clear();
            targets.Clear();
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                if (kv.Key == null || kv.Key.disabled ||
                    kv.Value.Kind != GroundOrderKind.Guard || kv.Value.GuardTarget == null)
                    continue;
                if (only != null && !GameApi.IsFriendlyUnit(kv.Key, only))
                    continue;
                units.Add(kv.Key);
                targets.Add(kv.Value.GuardTarget);
            }
        }

        /// <summary>Снять охрану цели у ВСЕХ стражей (Ctrl+Shift+ПКМ по связи).
        /// Возвращает число снятых связей.</summary>
        public static int CancelGuardOf(Unit target)
        {
            List<Unit> drop = null;
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
                if (kv.Value.Kind == GroundOrderKind.Guard && kv.Value.GuardTarget == target)
                    (drop ??= new List<Unit>()).Add(kv.Key);
            if (drop == null)
                return 0;
            foreach (Unit unit in drop)
                Clear(unit);
            return drop.Count;
        }

        /// <summary>Ближайшая угроза охраняемому: враг в радиусе GuardScanM от ЦЕЛИ
        /// (не от стража — перехват у охраняемого), видимый даталинку (погоня только
        /// за трекнутым — как у турелей). null — угроз нет.</summary>
        private static Unit NearestGuardThreat(Unit unit, Unit target)
        {
            FactionHQ hq = unit.NetworkHQ;
            if (hq == null)
                return null;
            Unit best = null;
            float bestSq = float.MaxValue;
            foreach (Unit enemy in BattlefieldGrid.GetUnitsInRangeEnumerable(
                         target.GlobalPosition(), Settings.GuardScanM))
            {
                if (enemy == null || enemy.disabled || enemy.NetworkHQ == null ||
                    enemy.NetworkHQ == hq)
                    continue;
                if (!(enemy is GroundVehicle || enemy is Ship || enemy is Aircraft || enemy is Building))
                    continue;
                if (hq.GetTrackingData(enemy.persistentID) == null)
                    continue; // даталинк: невидимого не перехватываем
                Vector3 d = enemy.transform.position - target.transform.position;
                float sq = d.x * d.x + d.z * d.z;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = enemy;
                }
            }
            return best;
        }

        /// <summary>Тик охраны: инженер при повреждённой цели уходит во временный
        /// ремонт (GuardResume — RepairService наливает, RepairFinished вернёт
        /// охрану); боевое прикрытие перехватывает угрозы у цели (погоня с поводком
        /// ОТ ЦЕЛИ, зеркально поводку патруля); никто рядом — follow-модель эскорта
        /// (след/веер/кольцо, stop-and-fire рядом с целью). Авиация охраны не имеет
        /// (у бортов escort-сегменты плана — DESIGN §2.11). true = цель охраны
        /// погибла — цепочка завершена.</summary>
        private static bool TickGuard(Unit unit, Chain ch, float now)
        {
            if (ch.GuardTarget == null || ch.GuardTarget.disabled)
                return true;
            if (GameApi.IsEngineer(unit) && GameApi.HpFraction(ch.GuardTarget) < 1f)
            {
                Version++;
                ch.GuardResume = true;
                ch.Kind = GroundOrderKind.Repair;
                ch.RepairTarget = ch.GuardTarget;
                IssueRepairFollow(unit, ch, force: true);
                return false;
            }
            if (now < ch.NextScanAt)
                return false;
            ch.NextScanAt = now + ScanIntervalSec;
            Unit threat = NearestGuardThreat(unit, ch.GuardTarget);
            if (threat != null)
            {
                Version++;
                ch.ResumeKind = GroundOrderKind.Guard;
                ch.PatrolLeashPoint = ch.GuardTarget.transform.position.ToGlobalPosition();
                ch.PatrolLeashSet = true;
                ch.PatrolLeashArmed = false;
                ch.Kind = GroundOrderKind.Attack;
                ch.Target = threat;
                ch.Holding = false;
                ch.LastPoint = default;
                IssuePursuit(unit, ch, PursueRange(unit, threat), force: true);
                return false;
            }
            return TickEscort(unit, ch, now); // бездействие: следование + stop-and-fire
        }

        /// <summary>Завершение налива (RepairService: HP полно — связь снята).
        /// Инженер с GuardResume возвращается к охране живой цели; остальным —
        /// обычная очистка. Вызывается сервисом завершения ВМЕСТО Clear: прямой
        /// Cancel игроком идёт через Clear — охрана не воскресает.</summary>
        public static void RepairFinished(Unit unit)
        {
            if (!Chains.TryGetValue(unit, out Chain ch))
                return;
            if (ch.GuardResume && ch.GuardTarget != null && !ch.GuardTarget.disabled)
            {
                Version++;
                ch.GuardResume = false;
                ch.RepairTarget = null;
                ch.Kind = GroundOrderKind.Guard;
                ch.Holding = false;
                ch.NextScanAt = 0f;
                IssueFollow(unit, ch, force: true);
                return;
            }
            Clear(unit);
        }

        // == РЕМОНТ (v0.149, канон SupCom — 4-я функция инженерного сьюта):
        // стоящий приказ «инженер → раненая техника/здание»: следование к цели,
        // в радиусе налива (EconomyProfile.repairRadiusM) — останов; HP льёт
        // RepairService (host). Зеркален сопровождению, без веера/следа.

        /// <summary>Приказ ремонта (перезапись текущего, как любой приказ без
        /// Shift; вызывающий обязан сам снять очередь стройки — BuildTasks.ClearFor).</summary>
        public static void SetRepair(Unit unit, Unit target)
        {
            Version++;
            var ch = new Chain
            {
                Kind = GroundOrderKind.Repair,
                RepairTarget = target
            };
            Chains[unit] = ch;
            IssueRepairFollow(unit, ch, force: true);
        }

        /// <summary>Снапшот ремонта (патруль-подхват снимает связь и возвращает
        /// после восстановления).</summary>
        public static bool TryGetRepairSnapshot(Unit unit, out Unit target)
        {
            target = null;
            if (!Chains.TryGetValue(unit, out Chain ch) ||
                ch.Kind != GroundOrderKind.Repair || ch.RepairTarget == null)
                return false;
            target = ch.RepairTarget;
            return true;
        }

        /// <summary>Живые связи ремонта: пары «инженер → цель» (налив RepairService,
        /// отрисовка). only — фильтр своей фракции (AI-инженеры делят тот же реестр).</summary>
        public static void CollectRepairs(List<Unit> units, List<Unit> targets, FactionHQ only = null)
        {
            units.Clear();
            targets.Clear();
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                if (kv.Key == null || kv.Key.disabled ||
                    kv.Value.Kind != GroundOrderKind.Repair || kv.Value.RepairTarget == null)
                    continue;
                if (only != null && !GameApi.IsFriendlyUnit(kv.Key, only))
                    continue;
                units.Add(kv.Key);
                targets.Add(kv.Value.RepairTarget);
            }
        }

        /// <summary>Снять ремонт цели у ВСЕХ инженеров (Ctrl+Shift+ПКМ по связи,
        /// §12-58б — зеркально эскорту). Возвращает число снятых связей.</summary>
        public static int CancelRepairOf(Unit target)
        {
            List<Unit> drop = null;
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
                if (kv.Value.Kind == GroundOrderKind.Repair && kv.Value.RepairTarget == target)
                    (drop ??= new List<Unit>()).Add(kv.Key);
            if (drop == null)
                return 0;
            foreach (Unit unit in drop)
                Clear(unit);
            return drop.Count;
        }

        /// <summary>Радиус налива ремонта из профиля экономики (фолбэк 60 м —
        /// профиль ещё не заведён, ремонт без EconomyService недоступен в UI).</summary>
        internal static float RepairRadiusM
        {
            get
            {
                EconomyProfile p = EconomyService.Profile;
                return p != null ? Mathf.Max(10f, p.repairRadiusM) : 60f;
            }
        }

        /// <summary>Точка подхода ремонта: на луче цель→инженер у ВНУТРЕННЕЙ кромки
        /// радиуса налива (не вплотную к габариту — у больших зданий иначе «не доезжает»).</summary>
        private static void IssueRepairFollow(Unit unit, Chain ch, bool force)
        {
            if (ch.RepairTarget == null)
                return;
            Unit target = ch.RepairTarget;
            Vector3 tp = target.transform.position;
            Vector3 up = unit.transform.position;
            float stand = target.maxRadius + RepairRadiusM * 0.7f;
            Vector2 d2 = new Vector2(up.x - tp.x, up.z - tp.z);
            if (d2.sqrMagnitude < 1f)
                d2 = new Vector2(1f, 0f);
            d2.Normalize();
            Vector3 follow = new Vector3(tp.x + d2.x * stand, 0f, tp.z + d2.y * stand);
            follow.y = GameApi.GroundY(follow.x, follow.z);
            GlobalPosition point = follow.ToGlobalPosition();
            Vector3 last = ch.LastPoint.ToLocalPosition();
            if (!force && new Vector2(last.x - follow.x, last.z - follow.z).magnitude < EscortReissueM)
                return;
            ch.LastPoint = point;
            unit.GetComponent<UnitCommand>()?.SetDestination(point, playerCommand: true);
        }

        /// <summary>Тик ремонта: цель жива — следовать; в радиусе — стоять (наливает
        /// RepairService, здесь только ведение). Полное здоровье НЕ завершает цепь —
        /// завершение ведёт RepairService (он возвращает патруль/шлёт Notice);
        /// исключение — цель погибла. true = цепочка завершена.</summary>
        private static bool TickRepair(Unit unit, Chain ch, float now)
        {
            if (ch.RepairTarget == null || ch.RepairTarget.disabled)
                return true;
            if (now < ch.NextScanAt)
                return false;
            ch.NextScanAt = now + ScanIntervalSec;
            Unit target = ch.RepairTarget;
            Vector3 tp = target.transform.position;
            Vector3 up = unit.transform.position;
            float dist = new Vector2(up.x - tp.x, up.z - tp.z).magnitude - target.maxRadius;
            if (dist <= RepairRadiusM)
                return false; // на месте: стоим, RepairService наливает
            IssueRepairFollow(unit, ch, force: false);
            return false;
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
                return !PromoteNextEscort(unit, ch); // очередь связей — следующая; иначе завершено
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
    }
}
