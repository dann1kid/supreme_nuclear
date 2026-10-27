using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Task-API v1 — веха стройки (DESIGN §2.3, архитектура — ARCHITECTURE §14).
    /// Хост-авторитетные задачи постройки зданий. Записи — data-shaped (цена/позиция/прогресс),
    /// чтобы будущее сериализуемое хранилище/AI/сеть работали с ними без ссылок; runtime-ссылки
    /// (FactionHQ/Unit) живут до этапа сейвов (долг в §12).
    /// Оплата потоковая в духе SupCom: пока инженер(ы) стоят на площадке — деньги утекают
    /// равномерно; кончились деньги или инженеры — задача просто стоит.</summary>
    public static class BuildTasks
    {
        public sealed class Task
        {
            public readonly string Id = Guid.NewGuid().ToString("N");
            public FactionHQ Faction;
            public BuildingDefinition Definition;
            public GlobalPosition Site;
            public float YawDeg;    // поворот здания (выход ангара — куда смотрит)
            public float Cost;
            public float Paid;
            public readonly List<Unit> Builders = new List<Unit>();
            public bool ZoneBlocked; // зона не расчищена (кто-то из наземки/флота внутри)
            public float Progress01 => Cost > 0f ? Mathf.Clamp01(Paid / Cost) : 0f;
        }

        private static readonly List<Task> Active = new List<Task>();
        public static int Version { get; private set; }
        public static int Count => Active.Count;

        /// <summary>Режим установки: здание, которое поставит следующий ЛКМ по миру.</summary>
        public static BuildingDefinition Pending;

        /// <summary>Жест поворота площадки: ЛКМ удерживается после клика — позиция
        /// зафиксирована (PlacingSite), выход крутим курсором (PendingYawDeg).
        /// Заполняется контроллером, читается призраками.</summary>
        public static bool PlacingActive;
        public static GlobalPosition PlacingSite;
        public static float PendingYawDeg;

        /// <summary>Выделенный НЕДОСТРОЙ (клик-без-движения по площадке): контекстные
        /// панели показывают его как объект (SupCom-семантика).</summary>
        public static Task SelectedTask;

        /// <summary>Клик этого кадра съеден установкой здания (выделение пропускает кадр).</summary>
        public static bool Consumed { get; private set; }

        /// <summary>ПКМ этого кадра съеден отменой расстановки: без флага приказы
        /// движения успевали обработать тот же клик (контроллер гасит расстановку
        /// ДО тика приказов).</summary>
        public static bool RmbEaten { get; private set; }

        private static bool IsHost
        {
            get
            {
                var net = NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        public static void Reset()
        {
            Version++;
            Active.Clear();
            BuildGhosts.HideAll();
            PushedAt.Clear();
            ReRouteAt.Clear();
            Sequences.Clear();
            Pending = null;
            PlacingActive = false;
            PendingYawDeg = 0f;
            Consumed = false;
        }

        public static void NewFrame()
        {
            Consumed = false;
            RmbEaten = false;
        }

        public static void MarkConsumed() => Consumed = true;

        public static void MarkRmbEaten() => RmbEaten = true;

        private static readonly Dictionary<BuildingDefinition, bool> HangarDefs =
            new Dictionary<BuildingDefinition, bool>();

        /// <summary>Здание-ангар (на префабе есть компонент Hangar): пристраивается
        /// к своему аэродрому и становится слотом производства мультифабрики.</summary>
        public static bool IsHangarDefinition(BuildingDefinition def)
        {
            if (def == null)
                return false;
            if (HangarDefs.TryGetValue(def, out bool isHangar))
                return isHangar;
            return HangarDefs[def] = def.unitPrefab != null &&
                                      def.unitPrefab.GetComponent<Hangar>() != null;
        }

        /// <summary>Ближайший СВОЙ аэродром в радиусе от точки (по центру аэродрома);
        /// null — если в радиусе своего нет. RunwayQuery не используется: ангара
        /// устраивает любой живой аэродром, а пустой запрос игра считает неподходящим.</summary>
        internal static Airbase NearestOwnAirbase(Vector3 from, FactionHQ hq, float maxRange)
        {
            if (hq == null)
                return null;
            Airbase best = null;
            float bestSq = maxRange * maxRange;
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null || airbase.CurrentHQ != hq || airbase.center == null)
                    continue;
                Vector3 c = airbase.center.position;
                float distSq = new Vector2(c.x - from.x, c.z - from.z).sqrMagnitude;
                if (distSq < bestSq)
                {
                    bestSq = distSq;
                    best = airbase;
                }
            }
            return best;
        }

        /// <summary>Дорисовка рулёжки к новому ангару: дорога «выход ангара → ближний
        /// узел такси-сети», а если сеть пуста — к ближнему торцу ВПП. RegenerateNetwork
        /// склеивает концы дорог в узлы в радиусе 10 м — новый ангар втыкается в общий
        /// граф, и выруливающий борт не ищет вход в сеть через полкарты (или полем
        /// напрямую, как без сети). Хост-only; клиент сеть не синкает (руление AI —
        /// серверная логика).</summary>
        private static void ConnectTaxiway(Airbase airbase, Building hangarBuilding)
        {
            try
            {
                RoadNetwork net = airbase.GetTaxiNetwork();
                if (net == null)
                    return;
                Hangar hangar = hangarBuilding != null ? hangarBuilding.GetComponent<Hangar>() : null;
                Transform exit = hangar != null ? hangar.GetSpawnTransform() : null;
                GlobalPosition from = (exit != null ? exit.position : hangarBuilding.transform.position)
                    .ToGlobalPosition();

                GlobalPosition to;
                if (net.TryGetNearestNode(from, out Node node))
                {
                    to = node.position;
                }
                else
                {
                    // Сети нет вовсе: цепляем к ближнему торцу любой полосы — дальше
                    // нативный выбор взлётной полосы сам поведёт борт по прямой/сети.
                    Transform best = null;
                    float bestSq = float.MaxValue;
                    Airbase.Runway[] runways = airbase.runways;
                    if (runways == null)
                        return;
                    Vector3 f = from.ToLocalPosition();
                    foreach (Airbase.Runway runway in runways)
                    {
                        if (runway == null)
                            continue;
                        foreach (Transform end in new[] { runway.Start, runway.End })
                        {
                            if (end == null)
                                continue;
                            float distSq = new Vector2(end.position.x - f.x, end.position.z - f.z)
                                .sqrMagnitude;
                            if (distSq < bestSq)
                            {
                                bestSq = distSq;
                                best = end;
                            }
                        }
                    }
                    if (best == null)
                        return;
                    to = best.position.ToGlobalPosition();
                }

                var road = new Road();
                road.AddPoint(from);
                road.AddPoint(to);
                road.CalcLength();
                net.roads.Add(road);
                net.RegenerateNetwork();
                // Поверхность: граф ведёт борт, а плиты-коллайдеры несут шасси —
                // без них на грунте (пустыня) тяжёлый борт вязнет и его катапультирует.
                TaxiSurface.Register(road.points);
                Log.Info("Build: рулёжка дорисована — " + airbase.name +
                         " (дорог " + net.roads.Count + ", поверхность " +
                         road.points.Count + " тчк)");
            }
            catch (System.Exception e)
            {
                Log.Error("Build: не удалось дорисовать рулёжку — " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Поставить площадку и отправить выделенных инженеров (хост).
        /// yawDeg — поворот здания: контроллер наберёт его удержанием ЛКМ.</summary>
        public static bool TryPlace(Vector3 groundPoint, List<Unit> selected, FactionHQ hq,
            float yawDeg = 0f)
        {
            if (!IsHost)
            {
                CommanderHud.Notice("Стройка пока только на хосте");
                return false;
            }
            if (Pending == null || hq == null)
                return false;
            var builders = new List<Unit>();
            foreach (Unit unit in selected)
                if (GameApi.IsEngineer(unit))
                    builders.Add(unit);
            if (builders.Count == 0)
            {
                CommanderHud.Notice("Нужен инженер (грузовик) в выделении");
                return false;
            }
            return Place(Pending, groundPoint, builders, hq, notice: true, yawDeg: yawDeg);
        }

        /// <summary>Ядро установки площадки (расстановка и шаблоны): задача + маршрут
        /// СВОБОДНЫМ инженерам (фокус-модель); деньги проверяются на месте.</summary>
        public static bool Place(BuildingDefinition def, Vector3 groundPoint,
            List<Unit> builders, FactionHQ hq, bool notice = false, float yawDeg = 0f)
        {
            if (def == null || hq == null || builders == null || builders.Count == 0)
                return false;
            float cost = def.value > 0f ? def.value : Settings.BuildDefaultCost;
            if (hq.factionFunds <= 1f)
            {
                if (notice)
                    CommanderHud.Notice($"Нет денег на стройку: {def.unitName} — {cost:F0}");
                return false;
            }

            // Анти-стены: площадки ближе минимальной дистанции раздвигаются (снап):
            // новая точка отталкивается от существующих, пока не встанет свободно.
            Vector3 placed = EnforceSpacing(groundPoint);

            // Ангары достраиваются ТОЛЬКО к своему аэродрому в радиусе: без него ангар —
            // сирота вне мультифабрики. Рулёжку от ангара мод дорисовывает сам (по
            // завершении стройки), так что площадка не обязана стоять на карте-
            // бетоне (пустынные аэропорты) — физику грунта игра считает сама.
            if (IsHangarDefinition(def) &&
                NearestOwnAirbase(placed, hq, Settings.HangarAttachRadiusM) == null)
            {
                if (notice)
                    CommanderHud.Notice($"Ангар: своего аэродрома нет в радиусе " +
                                        $"{Settings.HangarAttachRadiusM:F0} м");
                return false;
            }

            var task = new Task
            {
                Faction = hq,
                Definition = def,
                Site = placed.ToGlobalPosition(),
                Cost = cost,
                YawDeg = yawDeg,
            };
            task.Builders.AddRange(builders);
            Active.Add(task);
            Version++;
            BuildGhosts.Show(task);

            // Площадка дописывается В ОБЩУЮ ОЧЕРЕДЬ каждого инженера (порядок кликов).
            foreach (Unit b in builders)
            {
                if (!Sequences.TryGetValue(b, out List<SeqEntry> seq))
                    Sequences[b] = seq = new List<SeqEntry>();
                AdoptChainTail(b, seq);
                seq.Add(new SeqEntry { Task = task });
            }
            if (notice)
            {
                CommanderHud.Notice($"Стройка: {def.unitName} — {cost:F0} ({builders.Count} инженер.)");
                Log.Info($"Build: {def.unitName} на ({groundPoint.x:F0},{groundPoint.z:F0}), " +
                         $"цена {cost:F0}, строителей {builders.Count}");
            }
            return true;
        }

        /// <summary>Хвост обычной цепочки наземки становится началом очереди
        /// инженера (движения, кликнутые раньше площадки, исполняются раньше —
        /// порядок кликов). Работает ТОЛЬКО при пустой очереди: при живой очереди
        /// цепочка — не более выданной головы аванцера, копировать нечего.
        /// Цепочке оставляем ровно ТЕКУЩУЮ точку: держать хвост в двух хранилищах
        /// нельзя — вейпоинты задваиваются (маркеры/ленты цепочки и очереди
        /// рисуются каждая своим проходом), а после исчерпания цепочки аванцер
        /// стал бы выдавать копии заново — юнит проезжал бы маршрут дважды.
        /// Групповость точек (groupId клика) переживает переезд — драг/удаление
        /// продолжают действовать на всю группу приказа.</summary>
        private static void AdoptChainTail(Unit b, List<SeqEntry> seq)
        {
            if (seq.Count != 0 ||
                !GroundOrders.TryGetQueue(b, out var q, out int qi) || qi >= q.Count)
                return;
            for (int i = qi; i < q.Count; i++)
                seq.Add(new SeqEntry
                {
                    Move = q[i],
                    Issued = i == qi,
                    GroupId = GroundOrders.GroupIdOf(b, i)
                });
            GroundOrders.Clear(b);
            GroundOrders.Set(b, q[qi]); // текущая точка остаётся выданной, дальше ведёт аванцер
        }

        /// <summary>СБРОС всей очереди инженера (обычный приказ без Shift — семантика
        /// SupCom «новая очередь»): уходит из всех задач (задача без строителей
        /// отменяется с возвратом), хвост движений чистится.</summary>
        public static void ClearFor(Unit builder)
        {
            if (builder == null)
                return;
            // НЕДОСТРОЙ (философия игрока): сорванная инженером стройка ОСТАЁТСЯ
            // площадкой с уже уплаченным прогрессом — её подхватят другие инженеры
            // (ПКМ по недострою выделенными). Возврата денег нет.
            // Площадки НЕ УДАЛЯЮТСЯ сбросом очереди (философия игрока, v0.51): поставил —
            // стоит, пока не построят или не снесут явно (Ctrl+Shift/кнопка). Даже не
            // оплаченная: T3 может поставить серию и уехать, T1/T2 приедут позже.
            bool versionBump = false;
            foreach (Task t in new List<Task>(Active)) // снапшот: внутри Remove
                if (t.Builders.RemoveAll(b => b == builder) > 0)
                {
                    versionBump = true;
                    DetachRoute(builder, t); // отцепиться маршрутом от площадки
                }
            Sequences.Remove(builder);
            if (versionBump)
                Version++;
        }

        /// <summary>Точка подхода инженера к площадке: кольцо вокруг центра (со стороны,
        /// с которой он подъезжает) — инженеры расходятся по кругу, а не толпятся в
        /// футпринте (SupCom-поведение).</summary>
        public static GlobalPosition ApproachPoint(Task t, Unit builder)
        {
            Vector3 site = t.Site.ToLocalPosition();
            Vector3 pos = builder != null ? builder.transform.position : site + Vector3.forward * 10f;
            Vector2 dir = new Vector2(pos.x - site.x, pos.z - site.z);
            dir = dir.sqrMagnitude < 1f ? Vector2.up : dir.normalized;
            float ring = Settings.BuildSiteRadiusM * 0.65f;
            Vector3 approach = new Vector3(site.x + dir.x * ring, 0f, site.z + dir.y * ring);
            approach.y = GameApi.GroundY(approach.x, approach.z);
            return approach.ToGlobalPosition();
        }

        /// <summary>Снять ТЕКУЩИЙ маршрут юнита к площадке (задача снесена/юнит отказался):
        /// иначе он доезжает до удалённой точки. Аванцер тут же выдаст следующую запись.</summary>
        private static void DetachRoute(Unit b, Task t)
        {
            if (b == null || t == null)
                return;
            if (!GroundOrders.TryGetQueue(b, out var q, out int i) || i >= q.Count)
                return;
            Vector3 site = t.Site.ToLocalPosition();
            Vector3 dest = q[i].ToLocalPosition();
            float match = Settings.BuildSiteRadiusM;
            if (new Vector2(dest.x - site.x, dest.z - site.z).sqrMagnitude < match * match)
                GroundOrders.Clear(b);
        }

        public static bool HasActiveTaskPublic(Unit builder) => HasActiveTask(builder);

        private static bool HasActiveTask(Unit builder)
        {
            foreach (Task t in Active)
                if (t.Builders.Contains(builder))
                    return true;
            return false;
        }

        /// <summary>Цепочка SupCom: закончив стройку, инженер едет к своей следующей
        /// задаче в очереди (площадки работают как вейпоинты).</summary>
        private static readonly Dictionary<Unit, float> PushedAt = new Dictionary<Unit, float>();
        private static readonly Dictionary<Unit, float> ReRouteAt = new Dictionary<Unit, float>();

        // ЕДИНАЯ ОЧЕРЕДЬ инженера ПО ПОРЯДКУ КЛИКОВ (SupCom): записи Move/Task вперемешку.
        // Аванцер выдаёт следующую точку, когда текущая исполнена (движение доехало,
        // стройка достроена). Заменяет хвосты и цепочки прошлых версий.
        public sealed class SeqEntry
        {
            public GlobalPosition Move;   // либо движение…
            public Task Task;             // …либо площадка (одно из двух)
            public bool Issued;           // аванцер уже выдал эту точку в GroundOrders
            public int GroupId;           // групповой вейпоинт (SupCom): один приказ группе
            public bool IsStale => Task != null && !Active.Contains(Task);
        }

        private static int _groupSeq;
        public static int NextGroupId() => ++_groupSeq;

        private static readonly Dictionary<Unit, List<SeqEntry>> Sequences =
            new Dictionary<Unit, List<SeqEntry>>();

        public static IReadOnlyList<SeqEntry> SequenceOf(Unit builder) =>
            Sequences.TryGetValue(builder, out List<SeqEntry> seq) ? seq : null;

        /// <summary>Все инженеры с непустыми очередями (для Shift-режима видимости).</summary>
        public static List<Unit> UnitsWithSequences()
        {
            var list = new List<Unit>();
            foreach (KeyValuePair<Unit, List<SeqEntry>> kv in Sequences)
                if (kv.Key != null && !kv.Key.disabled && kv.Value.Count > 0)
                    list.Add(kv.Key);
            return list;
        }

        /// <summary>Дописать движение в общую очередь инженера (shift+движение).
        /// groupId > 0 — групповой вейпоинт: одна команда нескольким инженерам,
        /// удаление/драг любого из них действует на всю группу.</summary>
        public static void AppendMove(Unit builder, GlobalPosition point, int groupId = 0)
        {
            if (builder == null)
                return;
            if (!Sequences.TryGetValue(builder, out List<SeqEntry> seq))
                Sequences[builder] = seq = new List<SeqEntry>();
            seq.Add(new SeqEntry { Move = point, GroupId = groupId });
            Version++;
        }

        public static int GroupIdOf(Unit builder, int index)
        {
            return Sequences.TryGetValue(builder, out List<SeqEntry> seq) &&
                   index >= 0 && index < seq.Count ? seq[index].GroupId : 0;
        }

        /// <summary>Удалить ПЕРВУЮ запись с данным groupId в очереди инженера
        /// (групповое удаление: каждому члену группы — своя запись).</summary>
        public static void RemoveByGroupId(Unit builder, int groupId)
        {
            if (groupId == 0 || builder == null)
                return;
            if (!Sequences.TryGetValue(builder, out List<SeqEntry> seq))
                return;
            for (int i = 0; i < seq.Count; i++)
                if (seq[i].GroupId == groupId)
                {
                    seq.RemoveAt(i);
                    if (seq.Count == 0)
                        Sequences.Remove(builder);
                    Version++;
                    return;
                }
        }

        public static bool HasSequence(Unit builder) =>
            Sequences.TryGetValue(builder, out List<SeqEntry> seq) && seq.Count > 0;

        /// <summary>Перетащить движение в очереди инженера (драг вейпоинта).</summary>
        public static void SetSequenceMove(Unit builder, int index, Vector3 groundPoint)
        {
            if (!Sequences.TryGetValue(builder, out List<SeqEntry> seq) ||
                index < 0 || index >= seq.Count || seq[index].Task != null)
                return;
            seq[index].Move = groundPoint.ToGlobalPosition();
            Version++;
        }

        /// <summary>Удалить запись движения из очереди инженера (Ctrl+Shift+ПКМ).</summary>
        public static void RemoveSequenceMove(Unit builder, int index)
        {
            if (!Sequences.TryGetValue(builder, out List<SeqEntry> seq) ||
                index < 0 || index >= seq.Count || seq[index].Task != null)
                return;
            seq.RemoveAt(index);
            if (seq.Count == 0)
                Sequences.Remove(builder);
            Version++;
        }

        /// <summary>Инженер отказывается от площадки (удаление вейпоинта очереди):
        /// запись сносится, сам недострой остаётся другим строителям.</summary>
        public static void AbandonTaskEntry(Unit builder, Task task)
        {
            if (!Sequences.TryGetValue(builder, out List<SeqEntry> seq) || task == null)
                return;
            for (int i = 0; i < seq.Count; i++)
                if (seq[i].Task == task)
                {
                    seq.RemoveAt(i);
                    break;
                }
            DetachRoute(builder, task);
            task.Builders.RemoveAll(b => b == builder);
            if (seq.Count == 0)
                Sequences.Remove(builder);
            Version++;
        }

        /// <summary>Первая активная площадка в ОЧЕРЕДИ инженера (фокус по порядку кликов).</summary>
        private static Task FocusTaskOf(Unit builder, List<SeqEntry> seq)
        {
            if (seq != null)
            {
                foreach (SeqEntry e in seq)
                    if (e.Task != null && !e.IsStale)
                        return e.Task;
                return null; // очередь есть, площадок в ней нет — фокуса нет
            }
            foreach (Task t in Active)
                if (t.Builders.Contains(builder))
                    return t;
            return null;
        }

        /// <summary>Мешающий юнит в зоне: наземка/флот (не строитель, не здание; авиация
        /// и ракеты не считаются).</summary>
        public static Unit FindZoneBlocker(Task t)
        {
            Vector3 site = t.Site.ToLocalPosition();
            float radiusSq = (Settings.BuildSiteRadiusM + 10f) * (Settings.BuildSiteRadiusM + 10f);
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled)
                    continue;
                if (!(u is GroundVehicle || u is Ship))
                    continue;
                if (t.Builders.Contains(u))
                    continue;
                Vector3 p = u.transform.position;
                if (new Vector2(p.x - site.x, p.z - site.z).sqrMagnitude < radiusSq)
                    return u;
            }
            return null;
        }

        /// <summary>Проверка зоны для призрака установки (предпросмотр «не расчищено»).</summary>
        public static bool IsZoneBlockedAt(Vector3 site, List<Unit> builders)
        {
            float radiusSq = (Settings.BuildSiteRadiusM + 10f) * (Settings.BuildSiteRadiusM + 10f);
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled || builders != null && builders.Contains(u))
                    continue;
                if (!(u is GroundVehicle || u is Ship))
                    continue;
                Vector3 p = u.transform.position;
                if (new Vector2(p.x - site.x, p.z - site.z).sqrMagnitude < radiusSq)
                    return true;
            }
            return false;
        }

        private static bool IsIdleFriendly(Unit u, FactionHQ hq) =>
            u.NetworkHQ == hq && GroundOrders.IsIdle(u); // атака/очередь целей — не простой:
        // пуш из зоны стройки (GroundOrders.Set) порвал бы приказ

        /// <summary>Выпихнуть пассивного юнита из зоны (раз в 3 с на юнита).</summary>
        private static bool TryPushOutOfZone(Unit u, Task t)
        {
            if (Time.unscaledTime - (PushedAt.TryGetValue(u, out float at) ? at : -99f) < 3f)
                return false;
            PushedAt[u] = Time.unscaledTime;
            Vector3 site = t.Site.ToLocalPosition();
            Vector3 p = u.transform.position;
            Vector2 dir = new Vector2(p.x - site.x, p.z - site.z);
            dir = dir.sqrMagnitude < 1f ? Vector2.up : dir.normalized;
            float r = Settings.BuildSiteRadiusM + 40f;
            Vector3 target = new Vector3(site.x + dir.x * r, 0f, site.z + dir.y * r);
            GroundOrders.Set(u, target.ToGlobalPosition());
            return true;
        }

        /// <summary>Перетащить площадку (вейпоинт стройки): точка задачи переезжает,
        /// строители, у которых старая точка стоит в цепочке, перенаправляются.</summary>
        public static void MoveSite(Task task, Vector3 groundPoint)
        {
            if (task == null)
                return;
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
                DetachRoute(b, task);
            Version++;
        }

        /// <summary>Достройка недостроя: инженеры выделением ПКМ по существующей площадке.</summary>
        public static bool TryResume(Vector2 mouseScreen, Camera cam, List<Unit> selected)
        {
            if (cam == null || Active.Count == 0)
                return false;
            const float grabPx = 16f; // фолбэк, если 3D-призрак не построился
            foreach (Task t in Active)
            {
                // Клик НА МОДЕЛЬ призрака — достройка; мимо модели (движение рядом) — нет.
                bool hit = BuildGhosts.ScreenContains(t, cam, mouseScreen);
                if (!hit)
                {
                    Vector3 sp = cam.WorldToScreenPoint(t.Site.ToLocalPosition());
                    hit = sp.z > 0f && Vector2.Distance(new Vector2(sp.x, sp.y), mouseScreen) < grabPx;
                }
                if (!hit)
                    continue;
                int added = 0;
                foreach (Unit unit in selected)
                {
                    if (!GameApi.IsEngineer(unit) || t.Builders.Contains(unit))
                        continue;
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
                    Version++;
                    CommanderHud.Notice($"Достройка: +{added} инженер. ({t.Progress01:P0} готово)");
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
                        Vector3 site = head.Task.Site.ToLocalPosition();
                        Vector3 pos = u.transform.position;
                        // Вне радиуса оплаты → вернуться к кольцу. Порог = сам радиус
                        // (был ×1.2 — между 1.0 и 1.2 радиуса была мёртвая зона: не платит
                        // и не перенаправляет, инженер «не доезжал»).
                        bool far = new Vector2(pos.x - site.x, pos.z - site.z).sqrMagnitude >
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

        /// <summary>Хост-тик: потоковая оплата, спавн по завершении. Зовётся из контроллера.</summary>
        public static void Tick()
        {
            AdvanceSequences(); // очереди живут и без хоста? — только хост платит;
            // аванцер оставляем здесь (вызовы Tick только в миссии)
            BuildGhosts.Sync(); // призраки следуют за площадками и умирают с задачами
            if (!GameApi.InMission || !IsHost || Active.Count == 0)
                return;
            float dt = Time.deltaTime;
            var focus = new Dictionary<Unit, Task>();
            bool focusBuilt = false;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Task t = Active[i];
                for (int b = t.Builders.Count - 1; b >= 0; b--)
                    if (t.Builders[b] == null || t.Builders[b].disabled)
                        t.Builders.RemoveAt(b);
                focusBuilt = false; // состав меняется по ходу — пересобираем фокус каждый тик

                if (t.Faction == null || t.Definition == null)
                {
                    Active.RemoveAt(i);
                    Version++;
                    continue;
                }

                // ФОКУС инженера (канон SupCom): каждый инженер вливается ТОЛЬКО в свою
                // первую невыполненную задачу — даже стоя в радиусе соседних площадок.
                // Фокус = первая задача (по порядку очереди), где он числится строителем.
                if (!focusBuilt)
                {
                    focusBuilt = true;
                    focus.Clear();
                    // Фокус = первая активная площадка В ОЧЕРЕДИ инженера (порядок кликов);
                    // без очереди — fallback на первую активную задачу.
                    foreach (KeyValuePair<Unit, List<SeqEntry>> kv in Sequences)
                    {
                        Task ft = FocusTaskOf(kv.Key, kv.Value);
                        if (ft != null)
                            focus[kv.Key] = ft;
                    }
                    foreach (Task ft in Active)
                        foreach (Unit fb in ft.Builders)
                            if (fb != null && !focus.ContainsKey(fb))
                                focus[fb] = ft;
                }

                Vector3 site = t.Site.ToLocalPosition();
                float radiusSq = Settings.BuildSiteRadiusM * Settings.BuildSiteRadiusM;
                int onSite = 0;
                foreach (Unit b in t.Builders)
                {
                    if (!focus.TryGetValue(b, out Task bFocus) || bFocus != t)
                        continue; // инженер сфокусирован на другой площадке
                    // SupCom: сначала ОСТАНОВИЛСЯ в радиусе, потом строит — на ходу не капает.
                    if (Mathf.Abs(b.speed) > 3f)
                        continue;
                    Vector3 p = b.transform.position;
                    if (new Vector2(p.x - site.x, p.z - site.z).sqrMagnitude < radiusSq)
                        onSite++;
                }
                // Зона стройки: пока внутри чужая наземка/флот (кроме строителей;
                // авиация не считается) — стройка не начинается. Пассивных своих выпихиваем.
                Unit blocker = FindZoneBlocker(t);
                t.ZoneBlocked = blocker != null;
                if (t.ZoneBlocked && IsIdleFriendly(blocker, t.Faction) &&
                    TryPushOutOfZone(blocker, t))
                    t.ZoneBlocked = false; // выпихнули — следующим тиком можно строить

                if (onSite > 0 && !t.ZoneBlocked)
                {
                    float rate = t.Cost / Mathf.Max(1f, Settings.BuildSecondsPerBuilder) * onSite;
                    float pay = Mathf.Min(rate * dt, t.Cost - t.Paid, t.Faction.factionFunds);
                    if (pay > 0f)
                    {
                        t.Paid += pay;
                        t.Faction.AddFunds(-pay);
                    }
                }

                if (t.Paid >= t.Cost - 0.01f)
                {
                    SpawnBuilding(t);
                    PushBuildersOut(t);
                    BuildGhosts.Hide(t);
                    Active.RemoveAt(i); // аванцер снимет протухшую голову очереди сам
                    Version++;
                }
            }
        }

        /// <summary>Стоящие в футпринте строители выталкиваются за радиус площадки —
        /// здание спавнится не на них (SupCom: строители стоят кольцом).</summary>
        /// <summary>Раздвиг площадок: если новая ближе BuildSiteSpacingM к существующей,
        /// отталкивается наружу (до 6 итераций; без места — как есть).</summary>
        private static Vector3 EnforceSpacing(Vector3 point)
        {
            float min = Settings.BuildSiteSpacingM;
            if (min <= 0f || Active.Count == 0)
                return point;
            for (int iter = 0; iter < 6; iter++)
            {
                Vector3 push = Vector3.zero;
                bool conflict = false;
                foreach (Task t in Active)
                {
                    Vector3 s = t.Site.ToLocalPosition();
                    Vector2 d = new Vector2(point.x - s.x, point.z - s.z);
                    float dist = d.magnitude;
                    if (dist >= min)
                        continue;
                    conflict = true;
                    Vector2 dir = dist < 1f ? Vector2.up : d / dist;
                    push += new Vector3(dir.x, 0f, dir.y) * (min - dist);
                }
                if (!conflict)
                    break;
                point += push;
                point.y = GameApi.GroundY(point.x, point.z);
            }
            return point;
        }

        private static void PushBuildersOut(Task t)
        {
            Vector3 site = t.Site.ToLocalPosition();
            // Внутренний радиус = полу-диагональ футпринта здания (длина/ширина
            // определения) + запас: фиксированных 30 м крупным зданиям не хватало,
            // техника оставалась внутри стен при спавне.
            float half = 30f;
            if (t.Definition != null)
                half = Mathf.Max(30f, 0.5f * Mathf.Sqrt(
                    t.Definition.length * t.Definition.length +
                    t.Definition.width * t.Definition.width) + 6f);
            float inner = half;
            foreach (Unit b in t.Builders)
            {
                if (b == null || b.disabled)
                    continue;
                Vector3 p = b.transform.position;
                Vector2 dir = new Vector2(p.x - site.x, p.z - site.z);
                if (dir.sqrMagnitude >= inner * inner)
                    continue;
                dir = dir.sqrMagnitude < 1f ? Vector2.up : dir.normalized;
                float r = Settings.BuildSiteRadiusM + 40f;
                GroundOrders.Set(b, new Vector3(site.x + dir.x * r, 0f, site.z + dir.y * r)
                    .ToGlobalPosition());
            }
        }

        private static void SpawnBuilding(Task t)
        {
            Spawner spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null)
            {
                Log.Error("Build: Spawner недоступен — здание не создано, деньги возвращены");
                t.Faction.AddFunds(t.Paid);
                return;
            }
            Vector3 local = t.Site.ToLocalPosition();
            Vector3 pos = new Vector3(local.x, GameApi.GroundY(local.x, local.z), local.z) +
                          (Vector3)t.Definition.spawnOffset;
            Quaternion rot = Quaternion.Euler(0f, t.YawDeg, 0f);
            // Ангар — нативная привязка к аэродрому: SpawnBuilding с airbase делает
            // SetAirbase (сетевой синк клиентам) + AddHangar — ангар становится слотом
            // мультифабрики (ProductionTasks.PaySlots увидит его без нашей помощи).
            if (IsHangarDefinition(t.Definition))
            {
                Airbase airbase = NearestOwnAirbase(local, t.Faction, Settings.HangarAttachRadiusM);
                if (airbase == null)
                {
                    Log.Warn("Build: аэродром потерян за время стройки ангара — возврат денег");
                    t.Faction.AddFunds(t.Paid);
                    return;
                }
                Building hangar = spawner.SpawnBuilding(
                    t.Definition.unitPrefab, pos.ToGlobalPosition(), rot,
                    t.Faction, airbase, t.Definition.unitName, false, null);
                if (hangar == null)
                {
                    Log.Error("Build: ангар не создан — возврат денег");
                    t.Faction.AddFunds(t.Paid);
                    return;
                }
                ConnectTaxiway(airbase, hangar);
                CommanderHud.Notice($"Построено: {t.Definition.unitName} → {airbase.name}");
                Log.Info($"Build: ангар {t.Definition.unitName} пристыкован к {airbase.name} " +
                         $"на ({local.x:F0},{local.z:F0}), поворот {t.YawDeg:F0}°");
                return;
            }
            // Фракция обязательна: здание с null-HQ выходит нейтральным — не выделяется,
            // и техника из такого депо спавнится тоже нейтральной (TrySpawnVehicle
            // наследует NetworkHQ депо).
            Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                t.Definition, pos.ToGlobalPosition(), rot, t.Faction, t.Definition.unitName);
            if (unit == null)
            {
                Log.Error("Build: здание не создано — возврат денег");
                t.Faction.AddFunds(t.Paid);
                return;
            }
            CommanderHud.Notice($"Построено: {t.Definition.unitName}");
            Log.Info($"Build: готово {t.Definition.unitName} на ({local.x:F0},{local.z:F0}) за {t.Paid:F0}");
        }

        /// <summary>Активные задачи (маркеры площадок в HUD).</summary>
        public static IReadOnlyList<Task> All => Active;
    }
}
