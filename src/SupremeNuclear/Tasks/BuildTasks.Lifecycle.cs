using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть BuildTasks: жизненный цикл задач: MoveSite/Cancel/TryResume, патруль/эскорт-ассист.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр задач и Task-API — BuildTasks.cs.
    public static partial class BuildTasks
    {
        /// <summary>Перетащить площадку (вейпоинт стройки): точка задачи переезжает,
        /// строители, у которых старая точка стоит в цепочке, перенаправляются.</summary>
        public static void MoveSite(Task task, Vector3 groundPoint)
        {
            if (task == null)
                return;
            // Снап-роли НЕ переносятся: точка занимается при постановке, перенос
            // уводил здание с точки (доход/буст оставался) — бесконечные ресурсы
            // «переснапнул и увёл» (плейтест 2026-10-31). Хочешь на другой точке —
            // снести и построить заново.
            if (task.Definition == EconomyService.ExtractorDefinition ||
                task.Definition == EconomyService.HydroDefinition)
            {
                CommanderHud.Notice(Loc.Tr("Экстрактор и гидрокарбонку нельзя переместить — построй на новой точке"));
                return;
            }
            Vector3 oldLocal = task.Site.ToLocalPosition();
            task.Site = groundPoint.ToGlobalPosition();
            Version++;
            foreach (Unit b in task.Builders)
            {
                if (b == null || b.disabled)
                    continue;
                if (!GroundOrders.TryGetQueue(b, out var queue, out int idx) || idx >= queue.Count)
                    continue;
                Vector3 q0 = queue[idx].ToLocalPosition();
                float match = Settings.BuildSiteRadiusM;
                if (new Vector2(q0.x - oldLocal.x, q0.z - oldLocal.z).sqrMagnitude < match * match)
                    GroundOrders.Set(b, ApproachPoint(task, b));
            }
        }

        /// <summary>Полное удаление площадки (клик по плитке очереди). Без возврата:
        /// уплаченное — стоимость недостроя (философия игрока).</summary>
        public static void Cancel(Task task)
        {
            if (task == null || !Active.Remove(task))
                return;
            BuildGhosts.Hide(task);
            foreach (Unit b in task.Builders)
            {
                if (b is Ship sea)
                    sea.SetHoldPosition(false); // площадку снесли — строители с якоря
                DetachRoute(b, task);
            }
            Version++;
        }

        /// <summary>Курсор над ПЛОЩАДКОЙ (достройка/снос): ТЕЛО 3D-призрака
        /// (экран-прямоугольник по габаритам), маркер точки сайта (пик-радиус
        /// вейпоинтов) или ЛЮБАЯ точка полосы ВПП (сэмплы вдоль отрезка — у
        /// 4-км полосы пикать стартовый торец бессмысленно).</summary>
        private static bool SiteScreenHit(Task t, Camera cam, Vector2 mouse)
        {
            if (BuildGhosts.ScreenContains(t, cam, mouse))
                return true;
            Vector3 site = t.Site.ToLocalPosition();
            Vector3 sp = cam.WorldToScreenPoint(site);
            if (sp.z > 0f && Vector2.Distance(new Vector2(sp.x, sp.y), mouse) < Settings.WaypointPickPx)
                return true;
            if (!t.IsRunway)
                return false;
            Vector3 b = t.RunwayEnd.ToLocalPosition();
            float len = new Vector2(b.x - site.x, b.z - site.z).magnitude;
            int steps = Mathf.Clamp(Mathf.CeilToInt(len / 200f), 1, 24);
            for (int i = 1; i < steps; i++)
            {
                Vector3 p = Vector3.Lerp(site, b, i / (float)steps);
                Vector3 s = cam.WorldToScreenPoint(p);
                if (s.z > 0f && Vector2.Distance(new Vector2(s.x, s.y), mouse) < Settings.WaypointPickPx)
                    return true;
            }
            return false;
        }

        /// <summary>Достройка недостроя: ПКМ по призраку/маркеру площадки выделенными
        /// инженерами. Пик — по ПРИЗРАКУ (SiteScreenHit), не по земле под ним:
        /// клик по силуэту = адресный приказ стройки, мимо — движение. Без Shift —
        /// НОВАЯ очередь (канон стройки: только эта задача), с Shift — дописать в
        /// конец очереди каждого инженера. Свои площадки (фракция локального HQ).</summary>
        public static bool TryResume(Vector2 mouseScreen, Camera cam, List<Unit> selected, bool queue)
        {
            if (cam == null || Active.Count == 0 || selected == null)
                return false;
            if (!GameApi.TryGetLocalHQ(out FactionHQ localHq))
                return false;
            bool hasEngineer = false;
            foreach (Unit unit in selected)
                if (GameApi.IsEngineer(unit))
                {
                    hasEngineer = true;
                    break;
                }
            if (!hasEngineer)
                return false;
            foreach (Task t in Active)
            {
                if (t.Faction != localHq || !SiteScreenHit(t, cam, mouseScreen))
                    continue;
                int added = 0;
                foreach (Unit unit in selected)
                {
                    if (!GameApi.IsEngineer(unit))
                        continue;
                    if (!queue)
                        ClearFor(unit); // без Shift — новая очередь: прежние задачи/движения снимаются
                    if (t.Builders.Contains(unit))
                        continue; // уже строит эту площадку (с ClearFor — снят и вернём ниже)
                    t.Builders.Add(unit);
                    added++;
                    // Запись в ОБЩУЮ ОЧЕРЕДЬ инженера (как при постановке): порядок
                    // кликов, фокус и отрисовка едины — точка в GroundOrders-цепочке
                    // жила бы параллельно с очередью. Маршрут к площадке выдаст аванцер.
                    if (!HasActiveTaskBefore(unit, t))
                    {
                        if (!Sequences.TryGetValue(unit, out List<SeqEntry> seq))
                            Sequences[unit] = seq = new List<SeqEntry>();
                        AdoptChainTail(unit, seq);
                        seq.Add(new SeqEntry { Task = t });
                    }
                }
                if (added > 0)
                {
                    AssignApproachBearings(t);
                    Version++;
                    CommanderHud.Notice(Loc.F("Достройка: +{0} инженер. ({1} готово)", added, t.Progress01.ToString("P0")));
                    return true;
                }
                return false;
            }
            return false;
        }

        private static bool HasActiveTaskBefore(Unit builder, Task except)
        {
            foreach (Task t in Active)
                if (t != except && t.Builders.Contains(builder))
                    return true;
            return false;
        }

        // == ПАТРУЛЬ-АССИСТ (SupCom, v0.95): инженер в патрульном цикле сам подхватывает
        // близлежащий недострой — патруль снимается на время стройки (снимок цепочки),
        // после готовности/сноса площадки возвращается на сохранённую точку маршрута.
        // Явный приказ игроку (ClearFor) снимает инженера с задачи — патруль НЕ
        // возвращаем (правит игрок). Хост.
        private sealed class PatrolAssist
        {
            public Task Task;
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            public readonly List<int> Groups = new List<int>();
            public int Index;
            public int LoopStart; // начало цикла хвоста (конвертация «от точки», v0.114)
        }

        private static readonly Dictionary<Unit, PatrolAssist> PatrolAssists =
            new Dictionary<Unit, PatrolAssist>();
        private static readonly List<Unit> PatrolScan = new List<Unit>();
        private static float _nextPatrolScan;

        private static void TickPatrolAssist()
        {
            // 1. Живые записи: завершение/снос площадки — вернуть патруль; перебитый
            //    игроком приказ — снять запись без возврата.
            if (PatrolAssists.Count > 0)
            {
                List<Unit> done = null;
                foreach (KeyValuePair<Unit, PatrolAssist> kv in PatrolAssists)
                {
                    Unit u = kv.Key;
                    if (u == null || u.disabled)
                    {
                        (done ??= new List<Unit>()).Add(u);
                        continue;
                    }
                    Task t = kv.Value.Task;
                    if (!Active.Contains(t))
                    {
                        // Площадка готова/снесена. Возврат — только если игрок не дописал
                        // своей очереди поверх (тогда живёт его очередь, не цикл).
                        if (!HasSequence(u))
                        {
                            GroundOrders.RestorePatrol(u, kv.Value.Points, kv.Value.Groups,
                                kv.Value.Index, kv.Value.LoopStart);
                            Sequences.Remove(u); // пустой остаток ассист-записи
                            Version++;
                            Log.Info("Build: патруль-ассист — патруль возвращён: " + u.NetworkunitName);
                        }
                        (done ??= new List<Unit>()).Add(u);
                        continue;
                    }
                    if (!t.Builders.Contains(u))
                        (done ??= new List<Unit>()).Add(u); // перебит игроком — без возврата
                }
                if (done != null)
                    foreach (Unit u in done)
                        PatrolAssists.Remove(u);
            }

            // 2. Новые подхваты — троттл 0.5 с, радиус настройки (0 = выкл).
            float radius = Settings.BuildPatrolAssistRadiusM;
            if (radius <= 0f || Active.Count == 0 ||
                Time.unscaledTime < _nextPatrolScan)
                return;
            _nextPatrolScan = Time.unscaledTime + 0.5f;
            GroundOrders.CollectPatrols(PatrolScan);
            float radiusSq = radius * radius;
            foreach (Unit u in PatrolScan)
            {
                if (!GameApi.IsEngineer(u) || PatrolAssists.ContainsKey(u) ||
                    HasSequence(u) || HasActiveTask(u))
                    continue;
                FactionHQ hq = u.NetworkHQ;
                if (hq == null)
                    continue;
                Vector3 pos = u.transform.position;
                Task best = null;
                float bestSq = radiusSq;
                foreach (Task t in Active)
                {
                    if (t.Faction != hq)
                        continue;
                    float dSq = SiteDistSq(t, pos);
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        best = t;
                    }
                }
                if (best == null)
                    continue;
                var rec = new PatrolAssist { Task = best };
                if (!GroundOrders.TryGetPatrolSnapshot(u, rec.Points, rec.Groups, out int idx,
                        out int loopStart))
                    continue;
                rec.Index = idx;
                rec.LoopStart = loopStart;
                PatrolAssists[u] = rec;
                best.Builders.Add(u);
                AssignApproachBearings(best);
                // Очередь из одной записи: аванцер ведёт к площадке (подход/возврат
                // при отклонении — его же логика), фокус оплаты подхватится сам.
                Sequences[u] = new List<SeqEntry> { new SeqEntry { Task = best } };
                GroundOrders.Set(u, ApproachPoint(best, u)); // заменяет патруль транспортом
                Version++;
                Log.Info($"Build: патруль-ассист — {u.NetworkunitName} → {best.Label} " +
                         $"({Mathf.Sqrt(bestSq):F0} м), патруль из {rec.Points.Count} тчк снят");
            }
        }

        // == ЭСКОРТ-АССИСТ (v0.97, SupCom assist): инженер в сопровождении юнита
        // вливается в стройки СВОЕЙ цели (цель — строитель задачи): сопровождение
        // снимается на время (снимок связи), после готовности/сноса площадки —
        // возврат к цели. Явный приказ игрока (ClearFor) снимает без возврата.
        // Ассистятся только задачи цели — чужие недострои патрульным сканом не
        // трогаем (в отличие от патруль-ассиста). Хост.
        private sealed class EscortAssist
        {
            public Task Task;
            public Unit Target;   // цель сопровождения (для возврата связи)
            public float Bearing; // сектор веера сопровождения
        }

        private static readonly Dictionary<Unit, EscortAssist> EscortAssists =
            new Dictionary<Unit, EscortAssist>();
        private static float _nextEscortScan;

        private static void TickEscortAssist()
        {
            // 1. Живые записи: площадка готова/снесена — вернуть сопровождение;
            //    цель погибла/перебита игроком — снять запись без возврата.
            if (EscortAssists.Count > 0)
            {
                List<Unit> done = null;
                foreach (KeyValuePair<Unit, EscortAssist> kv in EscortAssists)
                {
                    Unit u = kv.Key;
                    if (u == null || u.disabled)
                    {
                        (done ??= new List<Unit>()).Add(u);
                        continue;
                    }
                    EscortAssist rec = kv.Value;
                    bool targetDead = rec.Target == null || rec.Target.disabled;
                    if (!Active.Contains(rec.Task) && !targetDead)
                    {
                        // Площадка готова/снесена. Возврат — только если игрок не дописал
                        // своей очереди поверх (тогда живёт его очередь, не связь).
                        if (!HasSequence(u))
                        {
                            GroundOrders.RestoreEscort(u, rec.Target, rec.Bearing);
                            Version++;
                            Log.Info("Build: эскорт-ассист — сопровождение возвращено: " +
                                     u.NetworkunitName);
                        }
                        (done ??= new List<Unit>()).Add(u);
                        continue;
                    }
                    if (targetDead || !rec.Task.Builders.Contains(u))
                        (done ??= new List<Unit>()).Add(u); // цель погибла/перебит игроком — без возврата
                }
                if (done != null)
                    foreach (Unit u in done)
                        EscortAssists.Remove(u);
            }

            // 2. Новые подхваты — троттл 0.5 с: инженер в сопровождении, его цель
            //    СТРОИТ — влиться в её задачу.
            if (Active.Count == 0 || Time.unscaledTime < _nextEscortScan)
                return;
            _nextEscortScan = Time.unscaledTime + 0.5f;
            foreach (KeyValuePair<Unit, EscortAssist> kv in EscortAssists)
                _escortingNow.Add(kv.Key); // уже занятые ассистом — не трогаем
            GroundOrders.CollectEscorts(EscortScan, EscortTargets);
            for (int i = 0; i < EscortScan.Count; i++)
            {
                Unit u = EscortScan[i];
                if (!GameApi.IsEngineer(u) || _escortingNow.Contains(u) ||
                    HasSequence(u) || HasActiveTask(u))
                    continue;
                Unit target = EscortTargets[i];
                // Задача цели = ГОЛОВА её очереди (первая не-мёртвая площадка —
                // цель может стоять в Builders сразу нескольких задач серии).
                Task task = null;
                IReadOnlyList<SeqEntry> targetSeq = SequenceOf(target);
                if (targetSeq != null)
                    foreach (SeqEntry e in targetSeq)
                    {
                        if (e.IsStale)
                            continue;
                        if (e.Task != null)
                        {
                            task = e.Task;
                            break;
                        }
                    }
                if (task == null)
                    continue; // цель сейчас не строит — просто следуем
                if (!GroundOrders.TryGetEscortSnapshot(u, out _, out float bearing))
                    continue;
                EscortAssists[u] = new EscortAssist { Task = task, Target = target, Bearing = bearing };
                task.Builders.Add(u);
                AssignApproachBearings(task);
                // Очередь из одной записи: аванцер ведёт к площадке (подход/возврат
                // при отклонении — его же логика), фокус оплаты подхватится сам.
                Sequences[u] = new List<SeqEntry> { new SeqEntry { Task = task } };
                GroundOrders.Set(u, ApproachPoint(task, u)); // заменяет сопровождение транспортом
                Version++;
                Log.Info($"Build: эскорт-ассист — {u.NetworkunitName} вливается в стройку " +
                         $"цели ({task.Label})");
            }
            _escortingNow.Clear();
        }

        private static readonly List<Unit> EscortScan = new List<Unit>();
        private static readonly List<Unit> EscortTargets = new List<Unit>();
        private static readonly HashSet<Unit> _escortingNow = new HashSet<Unit>();

        /// <summary>Аванцер единых очередей: пока юнит едет (GroundOrders не исчерпана) —
        /// ничего; голова Move исполнена — снять и выдать следующую; голова Task держит
        /// до достройки (при потере маршрута — вернуть, троттл 3 с). Каждый тик.</summary>
        private static void AdvanceSequences()
        {
            foreach (KeyValuePair<Unit, List<SeqEntry>> kv in Sequences)
            {
                Unit u = kv.Key;
                if (u == null || u.disabled)
                    continue;
                List<SeqEntry> seq = kv.Value;
                if (seq.RemoveAll(e => e.IsStale) > 0)
                    Version++;
                while (true)
                {
                    while (seq.Count > 0 && seq[0].IsStale)
                        seq.RemoveAt(0);
                    if (seq.Count == 0)
                        break;
                    if (GroundOrders.TryGetQueue(u, out var q, out int qi) && qi < q.Count)
                        break; // ещё едет к выданной точке
                    SeqEntry head = seq[0];
                    if (head.Task != null)
                    {
                        Vector3 pos = u.transform.position;
                        // Вне радиуса оплаты → вернуться. Порог = сам радиус
                        // (был ×1.2 — между 1.0 и 1.2 радиуса была мёртвая зона: не платит
                        // и не перенаправляет, инженер «не доезжал»). ВПП меряем
                        // по отрезку полосы (SiteDistSq).
                        bool far = SiteDistSq(head.Task, pos) >
                                   Settings.BuildSiteRadiusM * Settings.BuildSiteRadiusM;
                        if (far && Time.unscaledTime -
                            (ReRouteAt.TryGetValue(u, out float at) ? at : -99f) > 3f)
                        {
                            ReRouteAt[u] = Time.unscaledTime;
                            GroundOrders.Set(u, ApproachPoint(head.Task, u));
                        }
                        head.Issued = true;
                        break; // держит до достройки
                    }
                    if (head.Issued)
                    {
                        seq.RemoveAt(0); // движение исполнено — дальше по очереди
                        continue;
                    }
                    GroundOrders.Set(u, head.Move);
                    head.Issued = true;
                    break;
                }
            }
        }
    }
}
