using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть GroundOrders: правка точек/целей, сборщики, групповые операции, Disperse.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр цепочек и Tick — GroundOrders.cs.
    public static partial class GroundOrders
    {
        /// <summary>Перенацелить приказ атаки: q-я цель очереди → ближайший враг у
        /// точки бросания (драг креста, канон Moho: драг атак-приказа = ретаргет).
        /// Фан-аут: та же цель заменяется у ВСЕХ юнитов (приказ группы). Врага рядом
        /// нет — no-op. true — цель заменена.</summary>
        public static bool RetargetAt(Unit owner, int q, Vector3 worldPoint)
        {
            if (!Chains.TryGetValue(owner, out Chain ch) ||
                q < 0 || q >= ch.TargetQueue.Count)
                return false;
            Unit old = ch.TargetQueue[q];
            Unit fresh = NearestEnemyAround(owner, worldPoint, 150f);
            if (fresh == null || fresh == old)
                return false;
            Version++;
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                Chain c = kv.Value;
                for (int i = c.TargetQueue.Count - 1; i >= 0; i--)
                {
                    if (c.TargetQueue[i] != old)
                        continue;
                    c.TargetQueue[i] = fresh;
                    if (c.Kind == GroundOrderKind.Attack && c.Target == old)
                    {
                        c.Target = fresh;
                        c.Holding = false;
                        c.LastPoint = default;
                        CoordStrike.Remove(kv.Key);
                        StanceService.SetCoordHold(kv.Key, false);
                        IssuePursuit(kv.Key, c, PursueRange(kv.Key, fresh), force: true);
                    }
                }
            }
            return true;
        }

        /// <summary>Снять приказ атаки по цели q-й записи очереди (Ctrl+Shift+ПКМ по
        /// кресту): запись уходит у ВСЕХ юнитов с той же целью (группа); текущая
        /// погоня за ней прекращается (маршрут продолжится/следующая серия).</summary>
        public static int RemoveQueueTarget(Unit owner, int q)
        {
            if (!Chains.TryGetValue(owner, out Chain ch) ||
                q < 0 || q >= ch.TargetQueue.Count)
                return 0;
            Unit old = ch.TargetQueue[q];
            Version++;
            int removed = 0;
            foreach (KeyValuePair<Unit, Chain> kv in Chains)
            {
                Unit u = kv.Key;
                Chain c = kv.Value;
                for (int i = c.TargetQueue.Count - 1; i >= 0; i--)
                {
                    if (c.TargetQueue[i] != old)
                        continue;
                    c.TargetQueue.RemoveAt(i);
                    c.TargetAfter.RemoveAt(i);
                    removed++;
                }
                if (c.Kind == GroundOrderKind.Attack && c.Target == old)
                {
                    c.Target = null; // мёртвый путь TickPursuit продолжит маршрут/серию
                    c.Holding = false;
                }
                CoordStrike.Remove(u);
                StanceService.SetCoordHold(u, false);
            }
            return removed;
        }

        /// <summary>Ближайший живой враг юнита в радиусе от точки (для ретаргета
        /// драгом): любой боевой класс, свой/чужой — по HQ владельца.</summary>
        private static Unit NearestEnemyAround(Unit owner, Vector3 at, float radiusM)
        {
            FactionHQ hq = owner.NetworkHQ;
            if (hq == null)
                return null;
            Unit best = null;
            float bestSq = radiusM * radiusM;
            foreach (Unit enemy in BattlefieldGrid.GetUnitsInRangeEnumerable(
                         at.ToGlobalPosition(), radiusM))
            {
                if (enemy == null || enemy.disabled || enemy.NetworkHQ == null || enemy.NetworkHQ == hq)
                    continue;
                if (!(enemy is GroundVehicle || enemy is Ship || enemy is Aircraft || enemy is Building))
                    continue;
                Vector3 d = enemy.transform.position - at;
                float sq = d.x * d.x + d.z * d.z;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = enemy;
                }
            }
            return best;
        }

        /// <summary>Ядро удаления точки (общее для одиночного и группового удаления):
        /// снос точки (со своим видом), сдвиг позиций серий целей (TargetAfter),
        /// корректировка курсора — при удалении точки ДО текущей он сдвигается, при
        /// удалении текущей остаётся, указывая на следующую. Kind линейной цепочки
        /// пересинхронизируется на вид новой текущей точки. Чистая — тестируется.</summary>
        public static void RemoveAtCore(Chain ch, int index)
        {
            if (index < 0 || index >= ch.Points.Count)
                return;
            ch.Points.RemoveAt(index);
            ch.PointGroups.RemoveAt(index);
            if (index < ch.PointKinds.Count)
                ch.PointKinds.RemoveAt(index);
            for (int t = 0; t < ch.TargetAfter.Count; t++)
                if (ch.TargetAfter[t] > index)
                    ch.TargetAfter[t]--;
            for (int t = 0; t < ch.EscortAfter.Count; t++)
                if (ch.EscortAfter[t] > index)
                    ch.EscortAfter[t]--;
            if (index < ch.Index)
                ch.Index--;
            // Патрульный хвост не выпал из цикла — начало цикла сдвигается вместе с очередью.
            if (ch.PatrolLoopStart > index)
                ch.PatrolLoopStart--;
            SyncLinearKind(ch);
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
            float y = GameApi.RouteY(groundPoint.x, groundPoint.z); // вода — поверхность
            ch.Points[index] = new Vector3(groundPoint.x, y, groundPoint.z).ToGlobalPosition();
            if (index == ch.Index && ch.Kind != GroundOrderKind.Attack)
                unit.GetComponent<UnitCommand>()?.SetDestination(ch.Points[index], playerCommand: true);
        }

        /// <summary>Сбор цепочек для отрисовки/сканера. only ≠ null — только своей
        /// фракции: AI-командир кладёт цепочки в тот же реестр, чужие ленты/точки
        /// игроку не показываются и не пикаются (хост-сервисы зовут без фильтра).</summary>
        public static void CollectAll(List<Unit> units, List<IReadOnlyList<GlobalPosition>> queues,
            List<int> indices, FactionHQ only = null)
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
                if (only != null && !GameApi.IsFriendlyUnit(kv.Key, only))
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

        /// <summary>Вид точки очереди (Move/AttackMove/Patrol) — отрисовка красит
        /// ногу цветом ВХОДА в точку (канон SupCom: attack-move нога красная).
        /// Точки нет — вид текущей цепочки.</summary>
        public static GroundOrderKind KindOfPoint(Unit unit, int index)
        {
            if (!Chains.TryGetValue(unit, out Chain ch))
                return GroundOrderKind.Move;
            return index >= 0 && index < ch.PointKinds.Count ? ch.PointKinds[index] : ch.Kind;
        }

        /// <summary>Индекс начала патрульного цикла (замыкающая нога рисуется на него;
        /// 0 — цикл всех точек).</summary>
        public static int PatrolLoopStartOf(Unit unit) =>
            Chains.TryGetValue(unit, out Chain ch) && ch.Kind == GroundOrderKind.Patrol
                ? ch.PatrolLoopStart
                : 0;

        /// <summary>Вид линейной цепочки на точке index: у точки свой вид, нет точки —
        /// Move. Чистая — тестируется.</summary>
        public static GroundOrderKind LinearKindAt(Chain ch, int index) =>
            index >= 0 && index < ch.PointKinds.Count ? ch.PointKinds[index] : GroundOrderKind.Move;

        /// <summary>Инвариант per-point вида: Kind линейной цепочки = вид ТЕКУЩЕЙ
        /// точки (патруль/атака/эскорт не трогаем — их Kind несёт сама цепочка).</summary>
        private static void SyncLinearKind(Chain ch)
        {
            if (ch.Kind != GroundOrderKind.Move && ch.Kind != GroundOrderKind.AttackMove)
                return;
            ch.Kind = LinearKindAt(ch, ch.Index);
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
                int from = ch.Kind == GroundOrderKind.Patrol
                    ? Mathf.Min(ch.Index, ch.PatrolLoopStart) // цикл: все точки живы
                    : ch.Index;
                for (int i = from; i < ch.PointGroups.Count; i++)
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
                ch.PointKinds.RemoveRange(ch.Index, keep - ch.Index);
            }
            if (ch.Points.Count > ch.Index + 1)
            {
                int tail = ch.Points.Count - ch.Index - 1;
                ch.Points.RemoveRange(ch.Index + 1, tail);
                ch.PointGroups.RemoveRange(ch.Index + 1, tail);
                ch.PointKinds.RemoveRange(ch.Index + 1, tail);
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
    }
}
