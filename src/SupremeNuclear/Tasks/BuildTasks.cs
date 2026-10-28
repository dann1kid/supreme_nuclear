using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
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
            public BuildingDefinition Definition; // null у ВПП-задачи; у верфи — корпус-депо
            public GlobalPosition Site;
            public float YawDeg;    // поворот здания (выход — куда смотрит)
            public float PitchDeg;  // наклон под рельеф (депо; ангар/верфь — плоские: апрон/платформа)
            public float RollDeg;
            public bool IsRunway;   // задача-ВПП: по готовности спавнит аэродром
            public bool IsDock;     // задача-ВЕРФЬ: платформа на воде + корпус-депо (v0.92)
            public GlobalPosition RunwayEnd; // второй торец полосы
            public float Cost;
            public float Paid;
            public readonly List<Unit> Builders = new List<Unit>();
            /// <summary>Стабильные азимуты кольца подхода (веер строителей вокруг
            /// площадки, v0.95): пересчитываются при каждом изменении состава
            /// Builders. Нет азимута — инженер идёт к ближней точке кольца.</summary>
            public readonly Dictionary<Unit, float> ApproachBearings = new Dictionary<Unit, float>();
            public bool ZoneBlocked; // зона не расчищена (кто-то из наземки/флота внутри)
            public float Progress01 => Cost > 0f ? Mathf.Clamp01(Paid / Cost) : 0f;
            public string Label => IsRunway ? Loc.Tr("ВПП") : IsDock ? Loc.Tr("Верфь") : Definition?.unitName;
        }

        private static readonly List<Task> Active = new List<Task>();
        public static int Version { get; private set; }
        public static int Count => Active.Count;

        /// <summary>Режим установки: здание, которое поставит следующий ЛКМ по миру.</summary>
        public static BuildingDefinition Pending;

        /// <summary>Режим установки ВПП: ЛКМ — первый торец, протянуть — второй.</summary>
        public static bool PendingRunway;

        /// <summary>Режим установки ВЕРФИ: ЛКМ по воде — платформа, поворот удержанием.</summary>
        public static bool PendingDock;

        // Реестр построенных верфей (корпус = здание-депо, но производит ФЛОТ):
        // панели и ProductionTasks отличают верфь от обычного депо по нему.
        private static readonly List<Unit> Docks = new List<Unit>();

        /// <summary>Это здание — верфь (платформа на воде, производит корабли)?</summary>
        public static bool IsDock(Unit unit)
        {
            if (unit == null || unit.disabled)
                return false;
            return Docks.Contains(unit);
        }

        private static BuildingDefinition _dockDef;
        private static bool _dockDefResolved;

        /// <summary>Корпус верфи: первое здание-депо из энциклопедии (на префабе
        /// VehicleDepot) — визуально «склад у воды», true-фабрикой кораблей делает
        /// его наш реестр, не нативный депо.</summary>
        public static BuildingDefinition DockDefinition()
        {
            if (_dockDefResolved)
                return _dockDef;
            _dockDefResolved = true;
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia == null || encyclopedia.buildings == null)
                return null;
            foreach (BuildingDefinition def in encyclopedia.buildings)
                if (def != null && def.unitPrefab != null &&
                    def.unitPrefab.GetComponent<VehicleDepot>() != null)
                {
                    _dockDef = def;
                    break;
                }
            return _dockDef;
        }

        /// <summary>Жест поворота площадки/протяжки ВПП: ЛКМ удерживается после клика —
        /// позиция зафиксирована (PlacingSite), выход/второй торец ведём курсором.
        /// Заполняется контроллером, читается призраками.</summary>
        public static bool PlacingActive;
        public static GlobalPosition PlacingSite;
        public static float PendingYawDeg;
        public static GlobalPosition PlacingRunwayEnd;

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
            StallDiagAt.Clear();
            Sequences.Clear();
            Pending = null;
            PendingRunway = false;
            PendingDock = false;
            PlacingActive = false;
            PendingYawDeg = 0f;
            MainlineNodes.Clear();
            Docks.Clear();
            _dockDefResolved = false;
            Consumed = false;
            PatrolAssists.Clear();
            _nextPatrolScan = 0f;
            EscortAssists.Clear();
            _nextEscortScan = 0f;
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

        private static readonly Dictionary<BuildingDefinition, bool> ProducingDefs =
            new Dictionary<BuildingDefinition, bool>();

        /// <summary>Производящее здание (ангар ИЛИ депо-фабрика техники; корпус верфи —
        /// депо): имеет точку выхода — призрак показывает стрелку выката и крутится
        /// выходом к курсору; депо на суше дополнительно наклоняется под рельеф.</summary>
        public static bool IsProducingDefinition(BuildingDefinition def)
        {
            if (def == null)
                return false;
            if (ProducingDefs.TryGetValue(def, out bool producing))
                return producing;
            return ProducingDefs[def] = IsHangarDefinition(def) ||
                                         (def.unitPrefab != null &&
                                          def.unitPrefab.GetComponent<VehicleDepot>() != null);
        }

        /// <summary>Наклон депо под рельеф: сэмплы высот четырёх углов футпринта
        /// (в повороте yaw) → pitch/roll с клампом BuildTerrainTiltMaxDeg. Сэмплы —
        /// GroundYNoUnits: GroundY у соседской стены ловит её крышу и наклоняет
        /// площадку «в здание». Вызывается и для живого призрака установки (кадр),
        /// и при постановке площадки (один раз — значения протухнуть не могут,
        /// рельеф под площадкой не меняется).</summary>
        internal static void TerrainTilt(BuildingDefinition def, Vector3 local, float yawDeg,
            out float pitchDeg, out float rollDeg)
        {
            pitchDeg = 0f;
            rollDeg = 0f;
            if (def == null || Settings.BuildTerrainTiltMaxDeg <= 0f)
                return;
            float rad = yawDeg * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));   // локальный +Z
            Vector3 right = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad)); // локальный +X
            float hl = Mathf.Max(def.length, 4f) * 0.5f;
            float hw = Mathf.Max(def.width, 4f) * 0.5f;
            Vector3 front = local + fwd * hl;
            Vector3 back = local - fwd * hl;
            float hFR = GameApi.GroundYNoUnits(front.x + right.x * hw, front.z + right.z * hw);
            float hFL = GameApi.GroundYNoUnits(front.x - right.x * hw, front.z - right.z * hw);
            float hBR = GameApi.GroundYNoUnits(back.x + right.x * hw, back.z + right.z * hw);
            float hBL = GameApi.GroundYNoUnits(back.x - right.x * hw, back.z - right.z * hw);
            Util.FootprintTilt((hFR + hFL) * 0.5f, (hBR + hBL) * 0.5f,
                (hFR + hBR) * 0.5f, (hFL + hBL) * 0.5f,
                def.length, def.width, Settings.BuildTerrainTiltMaxDeg,
                out pitchDeg, out rollDeg);
        }

        /// <summary>Ближайший СВОЙ аэродром в радиусе от точки. Дистанция — до центра
        /// ИЛИ до отрезков ВПП: у построенной длинной полосы ангары у торца далеко
        /// от «центра» (середина полосы). null — если в радиусе своего нет.
        /// RunwayQuery не используется: пустой запрос игра считает неподходящим.</summary>
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
                Airbase.Runway[] runways = airbase.runways;
                if (runways != null)
                    foreach (Airbase.Runway runway in runways)
                    {
                        if (runway == null)
                            continue;
                        float rSq = DistSqToSegment(from, runway.Start, runway.End);
                        if (rSq >= 0f && rSq < distSq)
                            distSq = rSq;
                    }
                if (distSq < bestSq)
                {
                    bestSq = distSq;
                    best = airbase;
                }
            }
            return best;
        }

        /// <summary>Квадрат XZ-дистанции до отрезка (по Transform торцов); −1 — нет.
        /// Геометрия — Util.DistSqToSegmentXZ (чистая, тестируется).</summary>
        private static float DistSqToSegment(Vector3 from, Transform a, Transform b)
        {
            if (a == null || b == null)
                return -1f;
            return Util.DistSqToSegmentXZ(
                new Vector2(from.x, from.z),
                new Vector2(a.position.x, a.position.z),
                new Vector2(b.position.x, b.position.z));
        }

        // «Родные» узлы такси-сети аэродрома (снимок до наших дорисовок): новые
        // ангары цепляются ТОЛЬКО к ним — иначе каждый следующий цепляется к дороге
        // предыдущего и ангары сцепляются цепочкой (плейтест v0.82).
        private static readonly Dictionary<Airbase, List<GlobalPosition>> MainlineNodes =
            new Dictionary<Airbase, List<GlobalPosition>>();

        private static List<GlobalPosition> MainlineOf(Airbase airbase)
        {
            if (MainlineNodes.TryGetValue(airbase, out List<GlobalPosition> nodes))
                return nodes;
            nodes = new List<GlobalPosition>();
            RoadNetwork net = airbase.GetTaxiNetwork();
            if (net != null && net.nodes != null)
                foreach (Node node in net.nodes)
                    if (node != null)
                        nodes.Add(node.position);
            MainlineNodes[airbase] = nodes;
            return nodes;
        }

        /// <summary>Куда подключать новый ангар: ближний РОДНОЙ узел сети; цель «за
        /// спиной» выхода штрафуется ×3 (не вести полосу через своё же здание —
        /// баг «коллайдер на крыше»). Родных узлов нет — ближний торец ВПП.</summary>
        private static bool TryPickConnectTarget(Airbase airbase, GlobalPosition from,
            Vector3 exitFwd, out GlobalPosition target)
        {
            target = default;
            Vector3 f = from.ToLocalPosition();
            float best = float.MaxValue;
            bool found = false;
            foreach (GlobalPosition nodePos in MainlineOf(airbase))
            {
                Vector3 n = nodePos.ToLocalPosition();
                float dist = TargetScore(f, n, exitFwd);
                if (dist < best)
                {
                    best = dist;
                    target = nodePos;
                    found = true;
                }
            }
            if (found)
                return true;
            Airbase.Runway[] runways = airbase.runways;
            if (runways == null)
                return false;
            foreach (Airbase.Runway runway in runways)
            {
                if (runway == null)
                    continue;
                foreach (Transform end in new[] { runway.Start, runway.End })
                    if (end != null)
                    {
                        float dist = TargetScore(f, end.position, exitFwd);
                        if (dist < best)
                        {
                            best = dist;
                            target = end.position.ToGlobalPosition();
                            found = true;
                        }
                    }
            }
            return found;
        }

        private static float TargetScore(Vector3 from, Vector3 to, Vector3 exitFwd)
        {
            Vector2 d = new Vector2(to.x - from.x, to.z - from.z);
            float dist = d.magnitude;
            if (dist < 1f)
                return float.MaxValue;
            float dot = (d.x * exitFwd.x + d.y * exitFwd.z) / dist;
            return dot < -0.25f ? dist * 3f : dist;
        }

        private static bool _previewWasOk = true;

        /// <summary>Превью будущей рулёжки для призрака установки ангара:
        /// выход → линия до точки подключения (как построит ConnectTaxiway).
        /// null — аэродрома в радиусе нет или подключать некуда. Переходы
        /// логируем — плейтест-диагностика «превью не показывается».</summary>
        public static bool TryPreviewTaxiRoute(Vector3 exitPos, Vector3 exitFwd, FactionHQ hq,
            out List<GlobalPosition> points)
        {
            points = null;
            Airbase airbase = NearestOwnAirbase(exitPos, hq, Settings.HangarAttachRadiusM);
            if (airbase == null)
            {
                if (_previewWasOk)
                    Log.Info("Превью рулёжки: своего аэродрома нет в радиусе");
                _previewWasOk = false;
                return false;
            }
            Vector3 fwd = new Vector3(exitFwd.x, 0f, exitFwd.z);
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            GlobalPosition start = (exitPos + fwd * DoorClearanceM).ToGlobalPosition();
            if (!TryPickConnectTarget(airbase, start, fwd, out GlobalPosition to))
            {
                if (_previewWasOk)
                    Log.Info("Превью рулёжки: у аэродрома нет ни сети, ни ВПП");
                _previewWasOk = false;
                return false;
            }
            _previewWasOk = true;
            points = new List<GlobalPosition> { exitPos.ToGlobalPosition(), start };
            if (TryKneePoint(airbase, to, start, out GlobalPosition previewKnee))
                points.Add(previewKnee);
            points.Add(to);
            return true;
        }

        private const float DoorClearanceM = 10f; // старт дороги от ворот — не по зданию
        private const float TaxiKneeM = 90f;      // колено у торца: вылет поперёк оси ВПП

        /// <summary>Колено рулёжки у торца ВПП: заход на полосу строго
        /// ПЕРПЕНДИКУЛЯРНО её оси (живая аэродромная геометрия), на стороне
        /// ангара. Прямая из ангара упирается в торец наискось — скошенный стык,
        /// огрызки полотна и несовпадающая разметка (плейтест v0.96.1, скрин).
        /// false — цель не у торца ВПП (нативный узел в глубине поля) или прямая
        /// уже почти перпендикулярна — колено не нужно. Дорога и поверхность
        /// строятся по одной полилинии: борт едет по точкам дороги между узлами,
        /// асфальт ложится под колёса там же.</summary>
        private static bool TryKneePoint(Airbase airbase, GlobalPosition to,
            GlobalPosition start, out GlobalPosition knee)
        {
            knee = default;
            Vector3 toLocal = to.ToLocalPosition();
            if (!TryGetRunwayAxisAt(airbase, toLocal, out Vector3 axis))
                return false;
            Vector3 fromStart = start.ToLocalPosition() - toLocal;
            if (fromStart.sqrMagnitude < 1f)
                return false;
            Vector3 perp = new Vector3(-axis.z, 0f, axis.x);
            Vector3 side = Vector3.Dot(perp, fromStart) < 0f ? -perp : perp;
            if (Vector3.Dot(fromStart.normalized, side) > 0.95f)
                return false; // заход уже почти перпендикулярен оси — колено лишнее
            knee = (toLocal + side * TaxiKneeM).ToGlobalPosition();
            return true;
        }

        /// <summary>Ось ВПП, чей торец стоит рядом с точкой (узлы такси-сети
        /// живут у торцов; запас 60 м — узел может быть чуть смещён от торца).
        /// false — рядом торца нет.</summary>
        private static bool TryGetRunwayAxisAt(Airbase airbase, Vector3 point, out Vector3 axis)
        {
            axis = default;
            Airbase.Runway[] runways = airbase.runways;
            if (runways == null)
                return false;
            const float snapM = 60f;
            foreach (Airbase.Runway runway in runways)
            {
                if (runway == null || runway.Start == null || runway.End == null)
                    continue;
                Vector3 s = runway.Start.position;
                Vector3 e = runway.End.position;
                Vector3 d = new Vector3(e.x - s.x, 0f, e.z - s.z);
                if (d.sqrMagnitude < 1f)
                    continue;
                Vector3 ds = new Vector3(point.x - s.x, 0f, point.z - s.z);
                Vector3 de = new Vector3(point.x - e.x, 0f, point.z - e.z);
                if (ds.sqrMagnitude < snapM * snapM || de.sqrMagnitude < snapM * snapM)
                {
                    axis = d.normalized;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Дорисовка рулёжки к новому ангару: дорога «выход ангара → ближний
        /// РОДНОЙ узел такси-сети» (торец ВПП, если сети нет). RegenerateNetwork
        /// склеивает концы дорог в узлы в радиусе 10 м — новый ангар втыкается в общий
        /// граф, и выруливающий борт не ищет вход в сеть через полкарты (или полем
        /// напрямую, как без сети). Поверхность — ГРЕЙДИРОВАННАЯ лента от кромки
        /// апрона (padY — штифт старта: сход с плиты пандусом ≤ MaxGrade, а не
        /// обрывом юбки; конец штифтуется в высоту нашей ВПП через TopAt).
        /// Хост-only; клиент сеть не синкает (руление AI — серверная логика).</summary>
        private static void ConnectTaxiway(Airbase airbase, Building hangarBuilding,
            float padY = float.NaN)
        {
            try
            {
                RoadNetwork net = airbase.GetTaxiNetwork();
                if (net == null)
                    return;
                Hangar hangar = hangarBuilding != null ? hangarBuilding.GetComponent<Hangar>() : null;
                Transform exit = hangar != null ? hangar.GetSpawnTransform() : null;
                Vector3 fwd;
                Vector3 exitPos;
                if (exit != null)
                {
                    exitPos = exit.position;
                    fwd = exit.forward;
                }
                else
                {
                    // Нет spawnTransform (hardened): «ворота» — центр торца вперёд,
                    // как в синтетическом выходе призрака (BuildGhosts.TryGetExit).
                    fwd = hangarBuilding.transform.forward;
                    exitPos = hangarBuilding.transform.position +
                              new Vector3(fwd.x, 0f, fwd.z).normalized *
                              (hangarBuilding.definition.length * 0.5f + 2f);
                    Log.Info("Build: у ангара нет spawnTransform — выход по габариту: " +
                             hangarBuilding.definition.unitName);
                }
                fwd = new Vector3(fwd.x, 0f, fwd.z);
                fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
                GlobalPosition start = (exitPos + fwd * DoorClearanceM).ToGlobalPosition();

                if (!TryPickConnectTarget(airbase, start, fwd, out GlobalPosition to))
                    return;

                // Колено у торца ВПП: перпендикулярный заход (и дорога, и полотно).
                bool knee = TryKneePoint(airbase, to, start, out GlobalPosition kneeAt);

                var road = new Road();
                road.AddPoint(start);
                if (knee)
                    road.AddPoint(kneeAt);
                road.AddPoint(to);
                road.CalcLength();
                net.roads.Add(road);
                net.RegenerateNetwork();
                // Поверхность: граф ведёт борт, а плиты-коллайдеры несут шасси —
                // без них на грунте (пустыня) тяжёлый борт вязнет и его катапультирует.
                // Лента стартует С КРОМКИ АПРОНА (габарит + 6 м, геометрия Pad) —
                // пандус накрывает юбку плиты; в зоне перекрытия с ВПП прижимается
                // к её полотну на каждой станции (стык заподлицо).
                Vector3 padEdge = hangarBuilding.transform.position +
                    new Vector3(fwd.x, 0f, fwd.z).normalized *
                    (hangarBuilding.definition.length * 0.5f + 6f);
                var surface = new List<GlobalPosition> { padEdge.ToGlobalPosition() };
                surface.AddRange(road.points);
                TaxiSurface.Register(surface, graded: true, startY: padY);
                Log.Info("Build: рулёжка дорисована — " + airbase.name +
                         " (дорог " + net.roads.Count + ")");
            }
            catch (System.Exception e)
            {
                Log.Error("Build: не удалось дорисовать рулёжку — " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Поставить площадку и отправить выделенных инженеров (хост).
        /// yawDeg — поворот здания: контроллер наберёт его удержанием ЛКМ.
        /// Площадка назначается ВСЕМ выделенным инженерам (SupCom: пачка едет
        /// вместе и строит серию по очереди; заказ одному/подмножеству — у каждого
        /// своя цепочка, см. §14).</summary>
        public static bool TryPlace(Vector3 groundPoint, List<Unit> selected, FactionHQ hq,
            float yawDeg = 0f)
        {
            if (!IsHost)
            {
                CommanderHud.Notice(Loc.Tr("Стройка пока только на хосте"));
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
                CommanderHud.Notice(Loc.Tr("Нужен инженер (грузовик) в выделении"));
                return false;
            }
            return Place(Pending, groundPoint, builders, hq, notice: true, yawDeg: yawDeg);
        }

        /// <summary>Поставить площадку ВПП (драг «первый торец → второй»): по готовности
        /// спавнит НОВЫЙ аэродром (свой очередь/грид ангаров). Длина клампится в
        /// [RunwayMinM, RunwayMaxM] вдоль направления драга. Хост.</summary>
        public static bool TryPlaceRunway(Vector3 start, Vector3 end, List<Unit> selected, FactionHQ hq)
        {
            if (!IsHost)
            {
                CommanderHud.Notice(Loc.Tr("Стройка пока только на хосте"));
                return false;
            }
            if (hq == null)
                return false;
            var builders = new List<Unit>();
            foreach (Unit unit in selected)
                if (GameApi.IsEngineer(unit))
                    builders.Add(unit);
            if (builders.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Нужен инженер (грузовик) в выделении"));
                return false;
            }
            Vector2 dir = new Vector2(end.x - start.x, end.z - start.z);
            float len = dir.magnitude;
            if (len < 30f)
            {
                CommanderHud.Notice(Loc.F("ВПП: протяните полосу от торца до торца ({0}–{1} м)",
                    Settings.RunwayMinM.ToString("F0"), Settings.RunwayMaxM.ToString("F0")));
                return false;
            }
            dir /= len;
            len = Mathf.Clamp(len, Settings.RunwayMinM, Settings.RunwayMaxM);
            Vector3 finish = new Vector3(start.x + dir.x * len, 0f, start.z + dir.y * len);
            // ВПП — тоже сухопутная стройка: старт с воды = полоса на дне (как у
            // зданий, см. Place).
            if (start.y < Datum.LocalSeaY - 0.5f)
            {
                CommanderHud.Notice("ВПП: стройка — только с суши");
                return false;
            }
            // Денежного гейта НЕТ (семантика SupCom, юзер 2026-10-27): форму задаём
            // с любым балансом; наливает что есть, скорость = приток денег.

            var task = new Task
            {
                Faction = hq,
                Definition = null,
                IsRunway = true,
                Site = start.ToGlobalPosition(),
                RunwayEnd = finish.ToGlobalPosition(),
                Cost = Settings.RunwayCost,
            };
            task.Builders.AddRange(builders);
            Active.Add(task);
            Version++;
            AssignApproachBearings(task);
            foreach (Unit b in builders)
            {
                if (!Sequences.TryGetValue(b, out List<SeqEntry> seq))
                    Sequences[b] = seq = new List<SeqEntry>();
                AdoptChainTail(b, seq);
                seq.Add(new SeqEntry { Task = task });
            }
            CommanderHud.Notice(Loc.F("Стройка: ВПП {0} м — {1} ({2} инженер.)",
                len.ToString("F0"), Settings.RunwayCost.ToString("F0"), builders.Count));
            Log.Info($"Build: ВПП на ({start.x:F0},{start.z:F0})→({finish.x:F0},{finish.z:F0}), " +
                     $"длина {len:F0}, строителей {builders.Count}");
            return true;
        }

        /// <summary>Поставить площадку ВЕРФИ на воде (SupCom naval yard, v0.92):
        /// точка клика — дно под водой (глубина ≥ DockMinDepthM); по готовности —
        /// меш-платформа на уровне моря + корпус-депо (реестр верфей). Строят
        /// инженеры; без плавучести инженер строит с кромки воды в радиусе
        /// площадки (EngineerFloat ведёт его вплавь — см. Units/EngineerFloat).</summary>
        public static bool TryPlaceDock(Vector3 waterPoint, List<Unit> selected, FactionHQ hq,
            float yawDeg = 0f)
        {
            if (!IsHost)
            {
                CommanderHud.Notice(Loc.Tr("Стройка пока только на хосте"));
                return false;
            }
            BuildingDefinition hull = DockDefinition();
            if (hq == null || hull == null)
            {
                CommanderHud.Notice(Loc.Tr("Верфь: в каталоге нет здания-депо для корпуса"));
                return false;
            }
            var builders = new List<Unit>();
            foreach (Unit unit in selected)
                if (GameApi.IsEngineer(unit))
                    builders.Add(unit);
            if (builders.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Нужен инженер (грузовик) в выделении"));
                return false;
            }
            // Точка под водой и достаточно глубоко: клик по воде даёт дно
            // (вода не в StaticsMask); платформа прежней верфи перехватит луч
            // и «водой» не считается — верфь на верфь не поставить.
            if (waterPoint.y > Datum.LocalSeaY - Settings.DockMinDepthM)
            {
                CommanderHud.Notice(Loc.F("Верфь: нужна вода глубже {0} м", Settings.DockMinDepthM.ToString("F0")));
                return false;
            }

            var task = new Task
            {
                Faction = hq,
                Definition = hull, // корпус-депо: призрак/габариты; производит флот через реестр
                IsDock = true,
                Site = new Vector3(waterPoint.x, Datum.LocalSeaY, waterPoint.z).ToGlobalPosition(),
                Cost = Settings.DockCost,
                YawDeg = yawDeg,
            };
            task.Builders.AddRange(builders);
            Active.Add(task);
            Version++;
            AssignApproachBearings(task);
            BuildGhosts.Show(task);
            foreach (Unit b in builders)
            {
                if (!Sequences.TryGetValue(b, out List<SeqEntry> seq))
                    Sequences[b] = seq = new List<SeqEntry>();
                AdoptChainTail(b, seq);
                seq.Add(new SeqEntry { Task = task });
            }
            CommanderHud.Notice(Loc.F("Стройка: Верфь — {0} ({1} инженер.)", Settings.DockCost.ToString("F0"), builders.Count));
            Log.Info($"Build: Верфь на ({waterPoint.x:F0},{waterPoint.z:F0}), глубина " +
                     $"{Datum.LocalSeaY - waterPoint.y:F0} м, строителей {builders.Count}");
            return true;
        }

        /// <summary>Ядро установки площадки (расстановка и шаблоны): задача + маршрут
        /// СВОБОДНЫМ инженерам (фокус-модель); деньги проверяются на месте.
        /// Площадка назначается ВСЕМ переданным инженерам (SupCom-канон, §14:
        /// пачка едет вместе и строит серию по очереди; передал одного/подмножество —
        /// ровно они и строят).</summary>
        public static bool Place(BuildingDefinition def, Vector3 groundPoint,
            List<Unit> builders, FactionHQ hq, bool notice = false, float yawDeg = 0f)
        {
            if (def == null || hq == null || builders == null || builders.Count == 0)
                return false;
            float cost = def.value > 0f ? def.value : Settings.BuildDefaultCost;
            // Денежного гейта НЕТ (семантика SupCom): форма стройки — с любым
            // балансом, наливает сколько есть; темп = приток денег × инженеры.

            // Вода — только верфи: клик по воде даёт точку на ДНЕ (вода не ловится
            // лучом), обычное здание спавнилось бы на дне морском — техника из
            // такого депо рождалась «под землёй» (плейтест v0.94).
            if (groundPoint.y < Datum.LocalSeaY - 0.5f)
            {
                if (notice)
                    CommanderHud.Notice("Стройка — только на суше (верфь — на воде)");
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
                    CommanderHud.Notice(Loc.F("Ангар: своего аэродрома нет в радиусе {0} м",
                            Settings.HangarAttachRadiusM.ToString("F0")));
                return false;
            }

            // Наклон под рельеф — только сухопутное депо: ангар встаёт на плоский
            // апрон (TaxiSurface.Pad), верфь — на платформу у уровня моря (TryPlaceDock).
            float pitch = 0f, roll = 0f;
            if (IsProducingDefinition(def) && !IsHangarDefinition(def))
                TerrainTilt(def, placed, yawDeg, out pitch, out roll);

            var task = new Task
            {
                Faction = hq,
                Definition = def,
                Site = placed.ToGlobalPosition(),
                Cost = cost,
                YawDeg = yawDeg,
                PitchDeg = pitch,
                RollDeg = roll,
            };
            task.Builders.AddRange(builders);
            Active.Add(task);
            Version++;
            AssignApproachBearings(task);
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
                CommanderHud.Notice(Loc.F("Стройка: {0} — {1} ({2} инженер.)", def.unitName, cost.ToString("F0"), builders.Count));
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

        /// <summary>СБРОС всей очереди инженера (прямой приказ без Shift, постановка
        /// площадки/шаблона без Shift — семантика SupCom «новая очередь»): уходит из
        /// всех задач, хвост движений чистится. САМИ площадки остаются (SupCom-канон,
        /// возвращён плейтестом v0.83.2 — недострой живёт до достройки/явного сноса,
        /// инженеры переключаются между заданиями свободно; снос безхозных в v0.80.2
        /// отменён). Достройка — ПКМ по недострою (TryResume).</summary>
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
            Sequences.Remove(builder);
            if (builder is Ship sea)
                sea.SetHoldPosition(false); // морской инженер уходит — с якоря
            if (versionBump)
                Version++;
        }

        /// <summary>Радиус кольца остановки строителей: габарит площадки + запас,
        /// но НЕ дальше радиуса оплаты (дальше — деньги не капают) и не ближе
        /// минимума (мелкие постройки). Чистая — тестируется (Util.BuildApproachRingM).</summary>
        private const float MinApproachRingM = 25f;

        private static float RingM(Task t)
        {
            float cap = Settings.BuildSiteRadiusM * 0.85f;
            if (t.IsRunway)
                return Util.BuildApproachRingM(Settings.RunwayWidthM * 0.5f,
                    Settings.BuildApproachMarginM, MinApproachRingM, cap);
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
            return Util.BuildApproachRingM(half, Settings.BuildApproachMarginM, MinApproachRingM, cap);
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
        private static readonly Dictionary<Unit, float> ReRouteAt = new Dictionary<Unit, float>();

        // Диагностика застоя стройки (лог раз в 5 с, пока площадка не оплачена).
        private static readonly Dictionary<Task, float> StallDiagAt = new Dictionary<Task, float>();

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
                            GroundOrders.RestorePatrol(u, kv.Value.Points, kv.Value.Groups, kv.Value.Index);
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
                if (!GroundOrders.TryGetPatrolSnapshot(u, rec.Points, rec.Groups, out int idx))
                    continue;
                rec.Index = idx;
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

        /// <summary>Хост-тик: потоковая оплата, спавн по завершении. Зовётся из контроллера.</summary>
        public static void Tick()
        {
            AdvanceSequences(); // очереди живут и без хоста? — только хост платит;
            // аванцер оставляем здесь (вызовы Tick только в миссии)
            BuildGhosts.Sync(); // призраки следуют за площадками и умирают с задачами
            if (!GameApi.InMission || !IsHost)
                return;
            TickPatrolAssist(); // до Active.Count-гарда: возврат патрулей и на пустом списке
            TickEscortAssist(); // возврат сопровождений — тоже не зависит от Active.Count
            if (Active.Count == 0)
                return;
            float dt = Time.deltaTime;
            var focus = new Dictionary<Unit, Task>();
            bool focusBuilt = false;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Task t = Active[i];
                for (int b = t.Builders.Count - 1; b >= 0; b--)
                    if (t.Builders[b] == null || t.Builders[b].disabled)
                    {
                        t.ApproachBearings.Remove(t.Builders[b]);
                        t.Builders.RemoveAt(b);
                    }
                focusBuilt = false; // состав меняется по ходу — пересобираем фокус каждый тик

                // Мёртвая задача = нет фракции; ВПП-задача БЕЗ определения (Definition
                // null) — законна! Раньше выметалась здесь же на первом тике — ВПП
                // «не строилась» во всех плейтестах, молча (корень бага v0.87–v0.90).
                if (t.Faction == null || (!t.IsRunway && !t.IsDock && t.Definition == null))
                {
                    foreach (Unit b in t.Builders)
                        if (b is Ship sea)
                            sea.SetHoldPosition(false); // задача умерла — с якоря
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
                    // Морской инженер (катер) НЕ умеет стоять сам — «стоп» корабля это
                    // циркуляция: в радиусе площадки ставим на якорь (нативный
                    // SetHoldPosition), порог «остановился» мягче (лёгкий дрейф/качка).
                    float stillGate = b is Ship ? 8f : 3f;
                    if (Mathf.Abs(b.speed) > stillGate)
                        continue;
                    Vector3 p = b.transform.position;
                    if (SiteDistSq(t, p) < radiusSq)
                    {
                        if (b is Ship sea)
                            sea.SetHoldPosition(true);
                        onSite++;
                    }
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
                        // Первая капля — отметка в лог: плейтест-диагностика «не строится»
                        // (нет записи = инженеры не дошли/не остановились).
                        if (t.Paid <= 0f)
                            Log.Info($"Build: оплата начата — {t.Label}" +
                                     $" ({onSite} инженер. на площадке)");
                        t.Paid += pay;
                        t.Faction.AddFunds(-pay);
                    }
                }
                else if (t.Paid <= 0f && Time.unscaledTime -
                         (StallDiagAt.TryGetValue(t, out float diagAt) ? diagAt : -99f) > 5f)
                {
                    // Площадка стоит без оплаты — раз в 5 с снимок причин в лог:
                    // d=дистанция до площадки, v=скорость, f=фокус совпал, зона/на_площадке.
                    StallDiagAt[t] = Time.unscaledTime;
                    var why = new System.Text.StringBuilder();
                    foreach (Unit b in t.Builders)
                    {
                        if (b == null)
                            continue;
                        Vector3 bp = b.transform.position;
                        focus.TryGetValue(b, out Task bf);
                        why.Append("d=").Append(Mathf.Sqrt(SiteDistSq(t, bp)).ToString("F0"))
                           .Append(" v=").Append(Mathf.Abs(b.speed).ToString("F1"))
                           .Append(" f=").Append(bf == t ? "+" : "-").Append("; ");
                    }
                    Log.Info($"Build: ожидание — {t.Label}" +
                             $" зона={t.ZoneBlocked} на_площадке={onSite} " +
                             $"деньги={t.Faction.factionFunds:F0} | {why}");
                }

                if (t.Paid >= t.Cost - 0.01f)
                {
                    if (t.IsRunway)
                        SpawnRunway(t);
                    else if (t.IsDock)
                        SpawnDock(t);
                    else
                        SpawnBuilding(t);
                    PushBuildersOut(t);
                    BuildGhosts.Hide(t);
                    StallDiagAt.Remove(t);
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
                if (b is Ship sea)
                    sea.SetHoldPosition(false); // с якоря — строителей отводит приказ
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
            // Наклон (Pitch/Roll ≠ 0 — только у депо): здание снапится к рельефу,
            // как стояло бы «по склону»; ангар плоский на своём апроне.
            Quaternion rot = Quaternion.Euler(t.PitchDeg, t.YawDeg, t.RollDeg);
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
                // Апрон строим ПЕРВЫМ и ПЛОСКИМ (МАКС рельефа площадки + юбки —
                // v0.91.3, min зарывал верхний край плиты в бугор): здание ставится
                // на выровненное полотно, борт — на бетон с первого колеса.
                float padY = TaxiSurface.Pad(local, t.YawDeg,
                    t.Definition.width + 12f, t.Definition.length + 12f);
                Vector3 pos = new Vector3(local.x, padY, local.z) +
                              (Vector3)t.Definition.spawnOffset;
                Building hangar = spawner.SpawnBuilding(
                    t.Definition.unitPrefab, pos.ToGlobalPosition(), rot,
                    t.Faction, airbase, t.Definition.unitName, false, null);
                if (hangar == null)
                {
                    Log.Error("Build: ангар не создан — возврат денег");
                    t.Faction.AddFunds(t.Paid);
                    return;
                }
                ConnectTaxiway(airbase, hangar, padY);
                CommanderHud.Notice(Loc.F("Построено: {0} → {1}", t.Definition.unitName, airbase.name));
                Log.Info($"Build: ангар {t.Definition.unitName} пристыкован к {airbase.name} " +
                         $"на ({local.x:F0},{local.z:F0}), поворот {t.YawDeg:F0}°, " +
                         $"полотно y={padY:F1}");
                return;
            }
            Vector3 pos2 = new Vector3(local.x, GameApi.GroundY(local.x, local.z), local.z) +
                           (Vector3)t.Definition.spawnOffset;
            // Фракция обязательна: здание с null-HQ выходит нейтральным — не выделяется,
            // и техника из такого депо спавнится тоже нейтральной (TrySpawnVehicle
            // наследует NetworkHQ депо).
            Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                t.Definition, pos2.ToGlobalPosition(), rot, t.Faction, t.Definition.unitName);
            if (unit == null)
            {
                Log.Error("Build: здание не создано — возврат денег");
                t.Faction.AddFunds(t.Paid);
                return;
            }
            CommanderHud.Notice(Loc.F("Построено: {0}", t.Definition.unitName));
            Log.Info($"Build: готово {t.Definition.unitName} на ({local.x:F0},{local.z:F0}) за {t.Paid:F0}");
        }

        /// <summary>ВПП готова: нативный SpawnCustomAirbase — новый аэродром со своей
        /// очередью/гридом ангаров (паттерн «несколько фабрик» SupCom). Полоса
        /// реверсивная взлёт/посадка; аэродром ЗАХВАТЫВАЕМЫЙ (стратегический объект).
        /// Под полосу кладётся поверхность (тяжёлым катиться по грунту нельзя).</summary>
        private static void SpawnRunway(Task t)
        {
            Mission mission = MissionManager.CurrentMission;
            var net = NetworkManagerNuclearOption.i;
            if (mission == null || net == null || net.Server == null || !net.Server.Active)
            {
                Log.Error("Build: ВПП — миссия/сервер не готовы, возврат денег");
                t.Faction.AddFunds(t.Paid);
                return;
            }
            Vector3 a = t.Site.ToLocalPosition();
            Vector3 b = t.RunwayEnd.ToLocalPosition();
            float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;

            var saved = new SavedAirbase
            {
                SavedInMission = true,
                UniqueName = "SN-RWY-" + t.Id.Substring(0, 6),
                DisplayName = Loc.Tr("ВПП"),
                faction = t.Faction.faction != null ? t.Faction.faction.factionName : "",
            };
            saved.Center = new Vector3((a.x + b.x) * 0.5f, 0f, (a.z + b.z) * 0.5f).ToGlobalPosition();
            saved.SelectionPosition = saved.Center;
            // Радиус аэродрома = его CaptureRange — И критерий игры «AnyNearAirbase»
            // (руление/RTB ищут аэродром чей радиус накрывает борт). Дефолт 1000 м:
            // у длинной ВПП ангары на торце ВНЕ радиуса — борта уезжали рулить к
            // соседнему нативному аэропорту. Полдлины + дальность привязки ангаров
            // = вся территория аэродрома (и честный радиус захвата).
            saved.CaptureRange = len * 0.5f + Settings.HangarAttachRadiusM;
            saved.runways.Add(new SavedRunway
            {
                Name = "SN-" + t.Id.Substring(0, 4),
                Reversable = true,
                Takeoff = true,
                Landing = true,
                Width = Settings.RunwayWidthM,
                Start = a.ToGlobalPosition(),
                End = b.ToGlobalPosition(),
            });
            mission.airbases.Add(saved);
            Airbase airbase = mission.SpawnCustomAirbase(saved, net.ServerObjectManager);
            if (airbase == null)
            {
                Log.Error("Build: ВПП — аэродром не создан, возврат денег");
                mission.airbases.Remove(saved);
                t.Faction.AddFunds(t.Paid);
                return;
            }
            // Поверхность полосы: непрозрачный меш-«тармак», ВЫРОВНЕННЫЙ терассой
            // (плоское сечение, лимит уклона, юбки-насыпи) — разбег по ровному.
            TaxiSurface.Register(saved.runways[0].Start, saved.runways[0].End,
                Settings.RunwayWidthM * 0.9f, graded: true);
            CommanderHud.Notice(Loc.F("Построено: ВПП {0} м — новый аэродром", len.ToString("F0")));
            Log.Info($"Build: ВПП-аэродром {saved.UniqueName} на " +
                     $"({saved.Center.ToLocalPosition().x:F0},{saved.Center.ToLocalPosition().z:F0}), " +
                     $"длина {len:F0} м");
        }

        /// <summary>Верфь готова: меш-платформа на уровне моря (физика+визуал
        /// TaxiSurface) и корпус-депо сверху (SpawnFromUnitDefinitionInEditor,
        /// некапчуруемый, ренейм «Верфь»). Корпус попадает в реестр верфей —
        /// панели показывают ФЛОТ, ProductionTasks спускает корабли (NavalDock).</summary>
        private static void SpawnDock(Task t)
        {
            Spawner spawner = NetworkSceneSingleton<Spawner>.i;
            BuildingDefinition hull = t.Definition;
            if (spawner == null || hull == null)
            {
                Log.Error("Build: Верфь — Spawner/корпус недоступны, возврат денег");
                t.Faction.AddFunds(t.Paid);
                return;
            }
            Vector3 local = t.Site.ToLocalPosition();
            Quaternion rot = Quaternion.Euler(0f, t.YawDeg, 0f);
            float sizeX = hull.width + Settings.DockPlatformMarginM;
            float sizeZ = hull.length + Settings.DockPlatformMarginM;
            float deckY = TaxiSurface.WaterPad(local, t.YawDeg, sizeX, sizeZ);
            Vector3 pos = new Vector3(local.x, deckY, local.z) + (Vector3)hull.spawnOffset;
            Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                hull, pos.ToGlobalPosition(), rot, t.Faction, Loc.Tr("Верфь") + " SN-" + t.Id.Substring(0, 6));
            if (unit == null)
            {
                Log.Error("Build: Верфь — корпус не создан, возврат денег");
                t.Faction.AddFunds(t.Paid);
                return;
            }
            unit.NetworkunitName = Loc.Tr("Верфь");
            Docks.Add(unit);
            CommanderHud.Notice(Loc.Tr("Построено: Верфь — флот в очередь"));
            Log.Info($"Build: Верфь на ({local.x:F0},{local.z:F0}), платформа " +
                     $"{sizeX:F0}×{sizeZ:F0}, палуба y={deckY:F1}");
        }

        /// <summary>Активные задачи (маркеры площадок в HUD).</summary>
        public static IReadOnlyList<Task> All => Active;
    }
}
