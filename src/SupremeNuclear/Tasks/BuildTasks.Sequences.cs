using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть BuildTasks: очереди инженеров, кольцо подхода, блокировка зоны, Disperse.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр задач и Task-API — BuildTasks.cs.
    public static partial class BuildTasks
    {
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

        /// <summary>СБРОС всей очереди инженера (прямой приказ без Shift, постановка
        /// площадки/шаблона без Shift — семантика SupCom «новая очередь»): уходит из
        /// всех задач, хвост движений чистится. САМИ площадки остаются, ПОКА стоят
        /// хоть в одной очереди (юзер-канон 2026-11-04: площадка-СИРОТА — нет
        /// строителей И нет записей ни в одной очереди — отменяется в тике
        /// CancelOrphans; прежний «вечный недострой» v0.83.2 давил призраками
        /// после ПКМ-переключения инженеров). Достройка — ПКМ по недострою
        /// (TryResume) — действует, пока сирота не снята тиком.
        /// ФИКС 2026-11-04 «здание встало в очередь мувпоинтом без Shift»:
        /// доктрина «хвост движений чистится» не выполнялась буквально —
        /// живая цепочка GroundOrders (ПКМ-приказ) пережила сброс: авансер
        /// AdoptChainTail до неё дотягивался и она шла ПЕРЕД новой площадкой
        /// (выглядело как «площадка дописана в очередь»). Теперь: цепочка
        /// чистится + стоп-приказ «на свою позицию» гасит нативный
        /// commandedDestination (паттерн активации AI).</summary>
        public static void ClearFor(Unit builder)
        {
            if (builder == null)
                return;
            bool versionBump = false;
            foreach (Task t in new List<Task>(Active)) // снапшот: внутри Remove
                if (t.Builders.RemoveAll(b => b == builder) > 0)
                {
                    versionBump = true;
                    DetachRoute(builder, t); // отцепиться маршрутом от площадки
                }
            GroundOrders.Clear(builder); // хвост движений — ЧИСТИТЬ (доктрина)
            Sequences.Remove(builder);
            builder.GetComponent<UnitCommand>()?.SetDestination(
                builder.transform.position.ToGlobalPosition(), false); // стоп нативу
            if (builder is Ship sea)
                sea.SetHoldPosition(false); // морской инженер уходит — с якоря
            if (versionBump)
                Version++;
        }

        /// <summary>Радиус кольца остановки строителей: габарит площадки + запас,
        /// но НЕ дальше радиуса оплаты (дальше — деньги не капают) и не ближе
        /// минимума (мелкие постройки). Чистая — тестируется (Util.BuildApproachRingM).</summary>
        private const float MinApproachRingM = 25f;

        /// <summary>Полудиагональ футпринта площадки (габарит будущего здания;
        /// верфь — платформа, ВПП — полуширина полосы).</summary>
        private static float FootprintHalfM(Task t)
        {
            if (t.IsRunway)
                return Settings.RunwayWidthM * 0.5f;
            float half = 30f;
            if (t.Definition != null)
            {
                float w = t.Definition.width, l = t.Definition.length;
                if (t.IsDock)
                {
                    // платформа верфи шире корпуса на запас по габариту
                    w += Settings.DockPlatformMarginM;
                    l += Settings.DockPlatformMarginM;
                }
                half = 0.5f * Mathf.Sqrt(w * w + l * l);
            }
            return half;
        }

        private static float RingM(Task t)
        {
            float cap = Settings.BuildSiteRadiusM * 0.85f;
            float ring = Util.BuildApproachRingM(FootprintHalfM(t),
                Settings.BuildApproachMarginM, MinApproachRingM, cap);
            // Дисциплина площадки (v0.152, плейтест «толкаются у площадки»):
            // дуга кольца на строителя ≥ шаг формации — много инженеров не
            // слипаются в одну точку веера. Потолок 0.9·радиуса оплаты:
            // кольцо дальше радиуса = «стоит на кольце и не строит».
            float perBuilder = Mathf.Min(
                t.Builders.Count * Settings.FormationSpacing / (2f * Mathf.PI),
                Settings.BuildSiteRadiusM * 0.9f);
            return Mathf.Max(ring, perBuilder);
        }

        /// <summary>Веер строителей вокруг площадки (SupCom «окружают призрак»):
        /// сортировка по азимутам, каждому — СВОЙ сектор с шагом FormationSpacing
        /// по дуге кольца (маршруты не пересекаются, толпы со стороны подъезда нет).
        /// Азимут стабильный: перевыдачи подхода (аванцер/драг площадки) не стягивают
        /// инженеров в одну точку. Пересчёт — при каждом изменении состава строителей.</summary>
        private static readonly List<Unit> FanBuilders = new List<Unit>();

        public static void AssignApproachBearings(Task t)
        {
            t.ApproachBearings.Clear();
            if (t.IsRunway)
                return; // полоса: инженеры расходятся проекциями вдоль отрезка (ApproachPoint)
            FanBuilders.Clear();
            foreach (Unit b in t.Builders)
                if (b != null && !b.disabled)
                    FanBuilders.Add(b);
            if (FanBuilders.Count == 0)
                return;
            Vector3 site = t.Site.ToLocalPosition();
            FanBuilders.Sort((a, b) => AzimuthOf(a, site).CompareTo(AzimuthOf(b, site)));
            float ring = RingM(t);
            for (int i = 0; i < FanBuilders.Count; i++)
                t.ApproachBearings[FanBuilders[i]] = AzimuthOf(FanBuilders[i], site) +
                    GroundOrders.FanBearingOffset(i, FanBuilders.Count, Settings.FormationSpacing, ring);
        }

        private static float AzimuthOf(Unit u, Vector3 site) =>
            Mathf.Atan2(u.transform.position.x - site.x, u.transform.position.z - site.z);

        /// <summary>Точка подхода инженера к площадке: КОЛЬЦО ВНЕ ГАБАРИТА здания
        /// (полу-диагональ футпринта + запас) — инженеры окружают призрак и
        /// останавливаются у границы, а не въезжают в футпринт (v0.95). Азимут —
        /// стабильный веер строителей; одиночке/без азимута — живой азимут подъезда.
        /// ВПП: ближайшая точка ОТРЕЗКА полосы + выход наружу перпендикуляром
        /// (строить можно вдоль всей полосы, длина до 4 км).</summary>
        public static GlobalPosition ApproachPoint(Task t, Unit builder)
        {
            Vector3 site = t.Site.ToLocalPosition();
            float ring = RingM(t);
            float bearing = float.NaN;
            if (builder != null && t.ApproachBearings.TryGetValue(builder, out float assigned))
                bearing = assigned;
            if (t.IsRunway)
            {
                Vector3 pos = builder != null ? builder.transform.position : site;
                Vector3 b = t.RunwayEnd.ToLocalPosition();
                Vector2 ab = new Vector2(b.x - site.x, b.z - site.z);
                float abSq = ab.sqrMagnitude;
                float u = abSq < 1f
                    ? 0f
                    : Mathf.Clamp01(Vector2.Dot(new Vector2(pos.x - site.x, pos.z - site.z), ab) / abSq);
                Vector2 proj = new Vector2(site.x, site.z) + ab * u;
                Vector2 perp = new Vector2(-ab.y, ab.x);
                if (perp.sqrMagnitude < 1e-6f)
                    perp = Vector2.up;
                perp.Normalize();
                // наружу от полосы — в сторону инженера (по обе стороны строить можно)
                float side = new Vector2(pos.x - proj.x, pos.z - proj.y).sqrMagnitude >= 1f &&
                             Vector2.Dot(new Vector2(pos.x - proj.x, pos.z - proj.y), perp) < 0f ? -1f : 1f;
                Vector3 approach = new Vector3(proj.x + perp.x * ring * side, 0f, proj.y + perp.y * ring * side);
                approach.y = GameApi.GroundY(approach.x, approach.z);
                return approach.ToGlobalPosition();
            }
            Vector3 pos0 = builder != null ? builder.transform.position : site + Vector3.forward * 10f;
            if (float.IsNaN(bearing))
                bearing = Mathf.Atan2(pos0.x - site.x, pos0.z - site.z);
            Vector3 approach2 = new Vector3(site.x + Mathf.Sin(bearing) * ring, 0f,
                site.z + Mathf.Cos(bearing) * ring);
            // Верфь: площадка на воде — подход по ПОВЕРХНОСТИ (GroundY над водой
            // даёт дно; инженер-ховер держится на LocalSeaY).
            approach2.y = t.IsDock ? Datum.LocalSeaY : GameApi.GroundY(approach2.x, approach2.z);
            return approach2.ToGlobalPosition();
        }

        /// <summary>Квадрат дистанции до площадки: ВПП — до ОТРЕЗКА полосы,
        /// прочее — до точки сайта (оплата/«далеко» считают по одной мере).</summary>
        private static float SiteDistSq(Task t, Vector3 p)
        {
            Vector3 a = t.Site.ToLocalPosition();
            if (!t.IsRunway)
                return new Vector2(p.x - a.x, p.z - a.z).sqrMagnitude;
            Vector3 b = t.RunwayEnd.ToLocalPosition();
            Vector2 ab = new Vector2(b.x - a.x, b.z - a.z);
            float abSq = ab.sqrMagnitude;
            float u = abSq < 1f
                ? 0f
                : Mathf.Clamp01(Vector2.Dot(new Vector2(p.x - a.x, p.z - a.z), ab) / abSq);
            Vector2 proj = new Vector2(a.x, a.z) + ab * u;
            return (proj - new Vector2(p.x, p.z)).sqrMagnitude;
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

        /// <summary>Троттл скана занятости зоны стройки (на задачу).</summary>
        private const float ZoneScanIntervalSec = 0.5f;

        /// <summary>Переиспользуемый фокус-словарь тика (раньше — new Dictionary
        /// на каждый кадр; пересборка — на месте, логика не менялась).</summary>
        private static readonly Dictionary<Unit, Task> FocusMap = new Dictionary<Unit, Task>();
        private static readonly Dictionary<Unit, float> ReRouteAt = new Dictionary<Unit, float>();

        // Диагностика застоя стройки (лог раз в 5 с, пока площадка не оплачена).
        private static readonly Dictionary<Task, float> StallDiagAt = new Dictionary<Task, float>();

        /// <summary>Все инженеры с непустыми очередями (для Shift-режима видимости).
        /// only ≠ null — только своей фракции: AI-инженеры делят тот же реестр,
        /// чужие очереди не рисуются и не пикаются (v0.143.9).</summary>
        public static List<Unit> UnitsWithSequences(FactionHQ only = null)
        {
            var list = new List<Unit>();
            UnitsWithSequences(list, only);
            return list;
        }

        /// <summary>Буфер-версия (событийные сканы без аллокаций): список очищается.</summary>
        public static void UnitsWithSequences(List<Unit> list, FactionHQ only = null)
        {
            list.Clear();
            foreach (KeyValuePair<Unit, List<SeqEntry>> kv in Sequences)
                if (kv.Key != null && !kv.Key.disabled && kv.Value.Count > 0 &&
                    (only == null || GameApi.IsFriendlyUnit(kv.Key, only)))
                    list.Add(kv.Key);
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

        /// <summary>Члены группового приказа среди записей-движений очередей:
        /// для каждого инженера — индекс ПЕРВОЙ записи с groupId (индексы у
        /// каждого свои). Сигнатура зеркальна GroundOrders/AircraftOrders.CollectGroup.</summary>
        public static void CollectGroup(int groupId, List<Unit> builders, List<int> indices)
        {
            builders.Clear();
            indices.Clear();
            if (groupId == 0)
                return;
            foreach (KeyValuePair<Unit, List<SeqEntry>> kv in Sequences)
            {
                if (kv.Key == null || kv.Key.disabled)
                    continue;
                for (int i = 0; i < kv.Value.Count; i++)
                    if (kv.Value[i].GroupId == groupId && kv.Value[i].Task == null)
                    {
                        builders.Add(kv.Key);
                        indices.Add(i);
                        break;
                    }
            }
        }

        /// <summary>Удалить ПЕРВУЮ запись с данным groupId в очереди инженера
        /// (групповое удаление: каждому члену группы — своя запись). Выданная
        /// голова чистится и в GroundOrders-цепочке (как RemoveSequenceMove).</summary>
        public static void RemoveByGroupId(Unit builder, int groupId)
        {
            if (groupId == 0 || builder == null)
                return;
            if (!Sequences.TryGetValue(builder, out List<SeqEntry> seq))
                return;
            for (int i = 0; i < seq.Count; i++)
                if (seq[i].GroupId == groupId)
                {
                    bool wasIssued = seq[i].Issued;
                    seq.RemoveAt(i);
                    if (wasIssued)
                        GroundOrders.Clear(builder);
                    if (seq.Count == 0)
                        Sequences.Remove(builder);
                    Version++;
                    return;
                }
        }

        public static bool HasSequence(Unit builder) =>
            Sequences.TryGetValue(builder, out List<SeqEntry> seq) && seq.Count > 0;

        /// <summary>Инженер занят стройкой: очередь кликов непуста ИЛИ стоит в
        /// строителях живой задачи (запрос извне — фильтр занятости патруль-подхвата
        /// ремонта RepairService; зеркало внутреннего фильтра TickPatrolAssist).</summary>
        public static bool IsBuilderBusy(Unit builder) =>
            HasSequence(builder) || HasActiveTask(builder);

        // == DISPERSE СТРОЙКИ (v0.108): скретчи раздачи (хоткей, не OnGUI).
        private static readonly List<Unit> DisperseBuilders = new List<Unit>();
        private static readonly List<Task> DispersePool = new List<Task>();
        private static readonly List<Vector3> DisperseUnitPos = new List<Vector3>();
        private static readonly List<Vector3> DisperseTaskPos = new List<Vector3>();
        private static readonly List<Task> DisperseMine = new List<Task>();

        /// <summary>Disperse стройки (Alt+Shift+R, запрос 2026-10-28): серия площадок
        /// из очередей выделенных инженеров распределяется между ними КУЧКАМИ
        /// (Util.AssignSpread: наименее загруженный берёт ближайшую к концу своего
        /// маршрута) — каждый строит СВОЮ подвыборку, а не всю серию гурьбой.
        /// Пул = Task-записи очередей выделенных (порядок серии, дедуп); строители
        /// вне выделения остаются при своих задачах; Move-записи очередей остаются
        /// владельцам (порядок сохраняется). Инженеру без очереди, получившему
        /// площадки, заводится очередь в порядке серии. Сменившаяся голова —
        /// выданный маршрут снимается (аванцер тут же выдаст новый подход);
        /// затронутым задачам перевыдаётся веер строителей. Возвращает число
        /// распределённых площадок.</summary>
        public static int DisperseSequences(List<Unit> selected)
        {
            DisperseBuilders.Clear();
            foreach (Unit b in selected)
                if (b != null && !b.disabled && GameApi.IsEngineer(b))
                    DisperseBuilders.Add(b);
            if (DisperseBuilders.Count == 0)
                return 0;

            DispersePool.Clear();
            foreach (Unit b in DisperseBuilders)
            {
                if (!Sequences.TryGetValue(b, out List<SeqEntry> seq))
                    continue;
                foreach (SeqEntry e in seq)
                    if (e.Task != null && !e.IsStale && !DispersePool.Contains(e.Task))
                        DispersePool.Add(e.Task);
            }
            if (DispersePool.Count == 0)
                return 0;

            DisperseUnitPos.Clear();
            DisperseTaskPos.Clear();
            foreach (Unit b in DisperseBuilders)
                DisperseUnitPos.Add(b.transform.position);
            foreach (Task t in DispersePool)
                DisperseTaskPos.Add(t.Site.ToLocalPosition());
            int[] owner = Util.AssignSpread(DisperseUnitPos, DisperseTaskPos);
            if (owner == null)
                return 0;

            // Снять выделенных со ВСЕХ площадок пула и раздать по кучкам: в
            // Builders каждой задачи — только выигравший её выделенный инженер
            // (плюс не тронутые строители вне выделения).
            foreach (Task t in DispersePool)
                t.Builders.RemoveAll(b => DisperseBuilders.Contains(b));
            var hasWork = new bool[DisperseBuilders.Count];
            for (int k = 0; k < DispersePool.Count; k++)
            {
                DispersePool[k].Builders.Add(DisperseBuilders[owner[k]]);
                hasWork[owner[k]] = true;
            }

            int withWork = 0;
            foreach (bool has in hasWork)
                if (has)
                    withWork++;

            // Очереди: Move-записи остаются владельцу, Task — только доставшиеся.
            for (int i = 0; i < DisperseBuilders.Count; i++)
            {
                Unit b = DisperseBuilders[i];
                DisperseMine.Clear();
                for (int k = 0; k < DispersePool.Count; k++)
                    if (owner[k] == i)
                        DisperseMine.Add(DispersePool[k]);

                if (!Sequences.TryGetValue(b, out List<SeqEntry> seq))
                {
                    // Очереди не было (инженер привлечён раздачей): завести
                    // записями доставшихся площадок в порядке серии.
                    if (DisperseMine.Count == 0)
                        continue;
                    seq = new List<SeqEntry>();
                    foreach (Task t in DisperseMine)
                        seq.Add(new SeqEntry { Task = t });
                    Sequences[b] = seq;
                    GroundOrders.Clear(b); // свободный приказ ведёт аванцер
                    if (b is Ship sea)
                        sea.SetHoldPosition(false);
                    continue;
                }

                int write = 0;
                for (int read = 0; read < seq.Count; read++)
                {
                    SeqEntry e = seq[read];
                    if (e.Task == null || DisperseMine.Contains(e.Task))
                        seq[write++] = e;
                }
                if (write == seq.Count)
                    continue; // очередь не изменилась — голова и маршрут живы
                seq.RemoveRange(write, seq.Count - write);
                if (seq.Count == 0)
                {
                    Sequences.Remove(b);
                    GroundOrders.Clear(b);
                    if (b is Ship sea)
                        sea.SetHoldPosition(false); // без площадок — с якоря
                    continue;
                }
                if (!seq[0].Issued)
                {
                    // Голова сменилась: выданный маршрут мог вести к площадке,
                    // ушедшей другому — снять цепочку, аванцер выдаст новую голову.
                    GroundOrders.Clear(b);
                    if (b is Ship sea)
                        sea.SetHoldPosition(false);
                }
            }

            foreach (Task t in DispersePool)
                AssignApproachBearings(t); // состав строителей изменился — веер заново
            Version++;
            CommanderHud.Notice(Loc.F("Рассредоточение стройки: {0} {1} — {2} {3}",
                withWork, Loc.Plural(withWork, "инженер", "инженера", "инженеров"),
                DispersePool.Count, Loc.Plural(DispersePool.Count, "площадка", "площадки", "площадок")));
            Log.Info($"Build: disperse — {withWork} инженерам роздано {DispersePool.Count} площадок");
            return DispersePool.Count;
        }

        /// <summary>Цепочка юнита в GroundOrders — СЛУЖЕБНАЯ выданная голова
        /// (транспорт SetDestination): у инженера с живой очередью её не рисуем
        /// и не сканируем — маршрут целиком живёт в очереди. Единое правило
        /// для WaypointScanner / RouteRibbonMesh / CommanderHud (v0.82.2/v0.83.1).</summary>
        public static bool ChainIsIssuedHead(Unit unit) =>
            GameApi.IsEngineer(unit) && HasSequence(unit);

        /// <summary>Перетащить движение в очереди инженера (драг вейпоинта).
        /// Выданная голова (Issued) уже живёт точкой в GroundOrders-цепочке —
        /// перевыдать её и туда, иначе инженер продолжает ехать в старое место,
        /// а маркер уехал (регресс v0.80.1: головы маршрутов переехали в очередь,
        /// а перевыдача осталась только у SetQueuePoint цепочек).</summary>
        public static void SetSequenceMove(Unit builder, int index, Vector3 groundPoint)
        {
            if (!Sequences.TryGetValue(builder, out List<SeqEntry> seq) ||
                index < 0 || index >= seq.Count || seq[index].Task != null)
                return;
            seq[index].Move = groundPoint.ToGlobalPosition();
            if (seq[index].Issued)
                GroundOrders.Set(builder, seq[index].Move);
            Version++;
        }

        /// <summary>Удалить запись движения из очереди инженера (Ctrl+Shift+ПКМ).
        /// Выданная голова (Issued) жила и точкой в GroundOrders-цепочке — цепочку
        /// снести тоже, иначе юнит доедет до удалённой точки (аванцер тут же
        /// выдаст следующую запись).</summary>
        public static void RemoveSequenceMove(Unit builder, int index)
        {
            if (!Sequences.TryGetValue(builder, out List<SeqEntry> seq) ||
                index < 0 || index >= seq.Count || seq[index].Task != null)
                return;
            bool wasIssued = seq[index].Issued;
            seq.RemoveAt(index);
            if (wasIssued)
                GroundOrders.Clear(builder);
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

        /// <summary>Мешающий юнит в зоне: наземка/флот, НЕ инженер (инженеры у площадок —
        /// норма жизни: два инженера у соседних ВПП запирали зоны друг другу, плейтест
        /// v0.89); ВПП меряется по отрезку полосы. Авиация и ракеты не считаются.</summary>
        public static Unit FindZoneBlocker(Task t)
        {
            float radiusSq = (Settings.BuildSiteRadiusM + 10f) * (Settings.BuildSiteRadiusM + 10f);
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled)
                    continue;
                if (!(u is GroundVehicle || u is Ship))
                    continue;
                if (GameApi.IsEngineer(u))
                    continue;
                if (t.Builders.Contains(u))
                    continue;
                if (SiteDistSq(t, u.transform.position) < radiusSq)
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
                if (u is Building && EconomyService.IsSiteMarker(u))
                {
                    // Точка месторождения (v3): «недра» занимают место — прочие постройки
                    // не кладутся на точку (многозначность запрещена каноном).
                    Vector3 mp = u.transform.position;
                    if (new Vector2(mp.x - site.x, mp.z - site.z).sqrMagnitude < radiusSq)
                        return true;
                    continue;
                }
                if (u is Building b && !EconomyService.IsSiteMarker(b))
                {
                    // ГОТОВОЕ здание — блокер «не расчищено» на дистанции
                    // СПЕЙСИНГА (не 150 м зоны: ангарам положено стоять у своей
                    // базы/друг друга): ставить поверх — стек (юзер 2026-11-04).
                    Vector3 bp = u.transform.position;
                    float sp = Settings.BuildSiteSpacingM;
                    if (new Vector2(bp.x - site.x, bp.z - site.z).sqrMagnitude < sp * sp)
                        return true;
                    continue;
                }
                if (!(u is GroundVehicle || u is Ship))
                    continue;
                if (GameApi.IsEngineer(u))
                    continue; // инженеры не блокируют зоны (см. FindZoneBlocker)
                Vector3 p = u.transform.position;
                if (new Vector2(p.x - site.x, p.z - site.z).sqrMagnitude < radiusSq)
                    return true;
            }
            return false;
        }

        private static bool IsIdleFriendly(Unit u, FactionHQ hq) =>
            u.NetworkHQ == hq && GroundOrders.IsIdle(u); // атака/очередь целей — не простой:
        // пуш из зоны стройки (GroundOrders.Set) порвал бы приказ

        /// <summary>Первый живой МОБИЛЬНЫЙ юнит внутри ФУТПРИНТА площадки (любой
        /// фракции, включая инженеров-строителей — их место на кольце; авиация
        /// летает над, здания/маркеры исключены). null — футпринт чист.</summary>
        private static Unit FootprintOccupant(Task t)
        {
            float half = FootprintHalfM(t) + 3f;
            float rSq = half * half;
            Vector3 probe = default;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled || !(u is GroundVehicle || u is Ship))
                    continue;
                Vector3 p = u.transform.position;
                if (FootprintDistSqLocal(t, p, ref probe) < rSq)
                    return u;
            }
            return null;
        }

        /// <summary>Квадрат дистанции до ФУТПРИНТА + БЛИЖАЙШАЯ точка футпринта
        /// (не-ВПП — центр; ВПП — проекция на ось полосы: барьер толкает
        /// перпендикуляром от полосы). Буфер переиспользуется.</summary>
        private static float FootprintDistSqLocal(Task t, Vector3 p, ref Vector3 nearest)
        {
            Vector3 a = t.Site.ToLocalPosition();
            if (!t.IsRunway)
            {
                nearest = a;
                return new Vector2(p.x - a.x, p.z - a.z).sqrMagnitude;
            }
            Vector3 b = t.RunwayEnd.ToLocalPosition();
            Vector2 ab = new Vector2(b.x - a.x, b.z - a.z);
            float abSq = ab.sqrMagnitude;
            float u = abSq < 1f
                ? 0f
                : Mathf.Clamp01(Vector2.Dot(new Vector2(p.x - a.x, p.z - a.z), ab) / abSq);
            Vector2 proj = new Vector2(a.x, a.z) + ab * u;
            nearest = new Vector3(proj.x, a.y, proj.y);
            return (proj - new Vector2(p.x, p.z)).sqrMagnitude;
        }

        // == ОДНОПОЛОСНЫЙ БАРЬЕР ФУТПРИНТА (v0.152, плейтест «нет невидимого
        // хитбокса: въезжают внутрь и строятся поверх себя»). Канон SupCom:
        // сквозь площадку не ездят — но ВЫЕХАТЬ можно всегда. Хост-физика
        // (паттерн EngineerService.WaterTick): мобильному юниту ВНУТРИ футпринта
        // активной площадки гасится скорость К центру + мягкий толчок наружу;
        // стоящего внутри — выдавливает (инженеры «выстраиваются на кольце» до
        // первой капли). Троттл 0.1 с, try/catch по юниту.

        private static float _nextBarrierTick;
        private static readonly List<Task> BarrierTasks = new List<Task>();

        private static void TickFootprintBarrier()
        {
            if (Time.unscaledTime < _nextBarrierTick)
                return;
            _nextBarrierTick = Time.unscaledTime + 0.1f;
            BarrierTasks.Clear();
            foreach (Task t in Active)
                if (t != null && t.Faction != null)
                    BarrierTasks.Add(t);
            if (BarrierTasks.Count == 0)
                return;
            foreach (Unit u in UnitRegistry.allUnits)
            {
                if (u == null || u.disabled || !(u is GroundVehicle || u is Ship))
                    continue;
                try
                {
                    Rigidbody rb = u.GetComponent<Rigidbody>();
                    if (rb == null)
                        continue;
                    Vector3 p = rb.position;
                    Vector3 probe = default;
                    foreach (Task t in BarrierTasks)
                    {
                        float half = FootprintHalfM(t) + u.maxRadius + 4f;
                        if (FootprintDistSqLocal(t, p, ref probe) > half * half)
                            continue;
                        // Радиаль наружу от ближайшей точки футпринта (ВПП — от
                        // проекции на ось: толкает перпендикуляром от полосы).
                        Vector2 out2 = new Vector2(p.x - probe.x, p.z - probe.z);
                        if (out2.sqrMagnitude < 0.01f)
                            out2 = Vector2.up;
                        out2.Normalize();
                        Vector3 outDir = new Vector3(out2.x, 0f, out2.y);
                        Vector3 v = rb.velocity;
                        float inward = -(v.x * outDir.x + v.z * outDir.z); // >0 едет внутрь
                        if (inward > 0.3f)
                        {
                            // ОДНОПОЛОСНОСТЬ: въезд гасим (радиальную внутрь — в ноль),
                            // выезд свободен; плюс мягкий толчок наружу.
                            Vector3 acc = outDir * (6f + Mathf.Min(inward, 8f));
                            acc.x += v.x * -1.5f; // лёгкое общее торможение на въезде
                            acc.z += v.z * -1.5f;
                            rb.AddForce(acc, ForceMode.Acceleration);
                        }
                        else if (new Vector2(v.x, v.z).sqrMagnitude < 0.25f)
                        {
                            // Стоит внутри — выдавливаем (не рвём приказы: чистая физика).
                            rb.AddForce(outDir * 3f, ForceMode.Acceleration);
                        }
                        break; // одна площадка хватит (ближайшую не ищем — редкий случай)
                    }
                }
                catch (System.Exception e)
                {
                    Log.Warn("Build.Barrier: " + e.Message);
                }
            }
        }

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
    }
}
