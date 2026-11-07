using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть GroundOrders: промоушены очередей, погоня, стоп-энд-файр, кэш дальности оружия.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр цепочек и Tick — GroundOrders.cs.
    public static partial class GroundOrders
    {
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
            // Скоординированная атака: до времени залпа — запрет огня (позиция у
            // дальности уже держит подход); время вышло/цель сменилась — снять.
            if (CoordStrike.TryGetValue(unit, out var strike))
            {
                if (strike.target == null || strike.target.disabled || ch.Target != strike.target ||
                    Time.time >= strike.until)
                {
                    CoordStrike.Remove(unit);
                    StanceService.SetCoordHold(unit, false);
                }
                else
                    StanceService.SetCoordHold(unit, true);
            }
            // Поводок патруля/охраны: вошли в контакт (engaged) и ушли от якоря
            // отрыва дальше PatrolLeashM — погоню бросаем (цель «умерла» для нас).
            // У охраны якорь — позиция ОХРАНЯЕМОГО на момент перехвата: страж не
            // убегает за врагом от подопечного (канон guard).
            if ((ch.ResumeKind == GroundOrderKind.Patrol ||
                 ch.ResumeKind == GroundOrderKind.Guard) &&
                ch.PatrolLeashSet && ch.PatrolLeashArmed)
            {
                Vector3 leash = ch.PatrolLeashPoint.ToLocalPosition();
                Vector3 upL = unit.transform.position;
                float dl = new Vector2(upL.x - leash.x, upL.z - leash.z).magnitude;
                if (dl > Settings.PatrolLeashM)
                    ch.Target = null;
            }
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
                    // (точки, кликнутые ПОСЛЕ цели в Shift-серии) с видом ТОЧКИ,
                    // на которую вернулись (per-point: нога могла стать атакой-движением).
                    // Погоня патруля возвращается в ЦИКЛ (Kind=Patrol), поводок снят.
                    ch.Kind = ch.ResumeKind == GroundOrderKind.Escort
                        ? ch.ResumeKind
                        : ch.ResumeKind == GroundOrderKind.Patrol
                            ? GroundOrderKind.Patrol
                            : LinearKindAt(ch, ch.Index);
                    ch.PatrolLeashSet = false;
                    ch.PatrolLeashArmed = false;
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
                // Перехват охраны завершён (угроза убита/поводок): страж возвращается
                // к охраняемому (зеркало возврата патруля в цикл).
                if (ch.ResumeKind == GroundOrderKind.Guard &&
                    ch.GuardTarget != null && !ch.GuardTarget.disabled)
                {
                    ch.Kind = GroundOrderKind.Guard;
                    ch.Target = null;
                    ch.Holding = false;
                    ch.PatrolLeashSet = false;
                    ch.PatrolLeashArmed = false;
                    ch.NextScanAt = 0f;
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
                ch.PatrolLeashArmed = ch.ResumeKind == GroundOrderKind.Patrol ||
                                      ch.ResumeKind == GroundOrderKind.Guard; // поводок взведён
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
        /// который он и должен был обстрелять AGM.
        /// Кэш по (юнит → ТИП цели) с чексуммой «у каких станций есть боезапас»
        /// (§12-45ж, v0.113): раньше fit-чек гонялся на КАЖДОГО кандидата в скане
        /// 0.5 с, а зависит он только от пары (станции юнита, тип цели) —
        /// расстрел/перевооружение меняет чексумму и чистит кэш.</summary>
        private sealed class EngageRangeCache
        {
            public int Checksum = int.MinValue;
            public readonly Dictionary<UnitDefinition, float> Ranges =
                new Dictionary<UnitDefinition, float>();
        }

        private static readonly Dictionary<Unit, EngageRangeCache> EngageRanges =
            new Dictionary<Unit, EngageRangeCache>();

        private static int StationsChecksum(Unit unit)
        {
            int sum = 17;
            foreach (WeaponStation st in unit.weaponStations)
                sum = sum * 31 + (st == null || st.Ammo <= 0 ? 0 : 1);
            return sum;
        }

        private static float EngageRange(Unit unit, Unit target)
        {
            if (target == null || target.definition == null)
                return 0f;
            if (!EngageRanges.TryGetValue(unit, out EngageRangeCache cache))
                EngageRanges[unit] = cache = new EngageRangeCache();
            int checksum = StationsChecksum(unit);
            if (checksum != cache.Checksum)
            {
                cache.Checksum = checksum;
                cache.Ranges.Clear();
            }
            else if (cache.Ranges.TryGetValue(target.definition, out float cached))
                return cached;
            float r = 0f;
            foreach (WeaponStation st in unit.weaponStations)
            {
                if (st == null || st.Cargo || st.WeaponInfo == null || st.Ammo <= 0)
                    continue;
                if (st.CalcOpportunityThreat(target.definition, unit).opportunity <= 0f)
                    continue; // этим оружием цель не бьётся
                r = Mathf.Max(r, st.WeaponInfo.targetRequirements.maxRange);
            }
            cache.Ranges[target.definition] = r;
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
    }
}
