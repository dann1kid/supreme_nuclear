using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть BuildTasks: геометрия аэродрома: террейн-наклон, рулёжки, сервис-точки, стыковка.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр задач и Task-API — BuildTasks.cs.
    public static partial class BuildTasks
    {
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
            points.AddRange(ApproachPoints(airbase, to, start, fwd));
            points.Add(to);
            return true;
        }

        /// <summary>Точки подвода рулёжки МЕЖДУ start и целью (как построит
        /// ConnectTaxiway): у своей ВПП — «якоря и Безье» (Util.TaxiApproachPoints:
        /// прямой выезд из ворот → одно плавное Безье → выкат по оси в торец
        /// «в сторону взлёта», v0.102.2 — канон игрока, коллектор v0.100
        /// отвергнут); у нативного аэродрома — напрямую к ближнему узлу его
        /// собственной сети.</summary>
        private static List<GlobalPosition> ApproachPoints(Airbase airbase, GlobalPosition to,
            GlobalPosition start, Vector3 fwd)
        {
            Vector3 toLocal = to.ToLocalPosition();
            if (MainlineOf(airbase).Count == 0 &&
                TryGetRunwayAxisAt(airbase, toLocal, out Vector3 axis, out Vector3 farDir))
            {
                List<Vector3> pts = Util.TaxiApproachPoints(
                    toLocal, start.ToLocalPosition(), fwd, farDir);
                var result = new List<GlobalPosition>(pts.Count);
                foreach (Vector3 p in pts)
                    result.Add(p.ToGlobalPosition());
                return result;
            }
            return new List<GlobalPosition>(); // нативный узел — прямая дорога
        }

        private const float DoorClearanceM = 10f; // старт дороги от ворот — не по зданию

        private static readonly System.Reflection.FieldInfo ServicePointsField =
            HarmonyLib.AccessTools.Field(typeof(Airbase), "servicePoints");

        /// <summary>Дополнить массив сервис-точек аэродрома (рефлексия): игра
        /// строит его ТОЛЬКО из SavedAirbase при спайне — у построенной нами ВПП
        /// он пуст, севшие борта не находят куда рулить (TryGetNearestServicePoint
        /// пуст) и парком стоят на полосе. Точка = старт рулёжки ангара: на
        /// апроне и в дорожной сети (обратный рулёж по нашим же дорогам).
        /// ДОЛГ: точка на оси дороги — припаркованный борт частично перекрывает
        /// выезд следующего (§12).</summary>
        private static void AddServicePoint(Airbase airbase, GlobalPosition at)
        {
            try
            {
                if (airbase == null || airbase.transform == null || ServicePointsField == null)
                    return;
                var old = ServicePointsField.GetValue(airbase) as Transform[];
                int count = old?.Length ?? 0;
                Vector3 atLocal = at.ToLocalPosition();
                for (int i = 0; i < count; i++)
                    if (old[i] != null &&
                        (old[i].position - atLocal).sqrMagnitude < 4f)
                        return; // точка уже стоит (переподключение к тому же месту)
                var go = new GameObject("SN-ServicePoint"); // 3d-go: мировая точка, не UI
                go.transform.parent = airbase.transform;
                go.transform.position = atLocal;
                var updated = new Transform[count + 1];
                for (int i = 0; i < count; i++)
                    updated[i] = old[i];
                updated[count] = go.transform;
                ServicePointsField.SetValue(airbase, updated);
            }
            catch (System.Exception e)
            {
                Log.Error("Build: сервис-точка не добавлена — " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Ось ВПП и направление ВГЛУБЬ полосы от торца, стоящего
        /// рядом с точкой (узлы такси-сети живут у торцов; запас 60 м — узел
        /// может быть чуть смещён от торца). false — рядом торца нет.</summary>
        private static bool TryGetRunwayAxisAt(Airbase airbase, Vector3 point,
            out Vector3 axis, out Vector3 farDir)
        {
            axis = default;
            farDir = default;
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
                float dsSq = ds.sqrMagnitude, deSq = de.sqrMagnitude;
                if (dsSq < snapM * snapM || deSq < snapM * snapM)
                {
                    axis = d.normalized;
                    farDir = dsSq <= deSq ? axis : -axis; // от ближнего торца вглубь
                    return true;
                }
            }
            return false;
        }

        /// <summary>Подключить новый ангар к аэродрому: одна дорога
        /// «выход ангара → подвод (ApproachPoints) → узел-торец/нативный узел»;
        /// RegenerateNetwork склеивает концы в узлы в радиусе 10 м — все ангары
        /// сходятся в узле торца, борт сразу в общем графе. Подвод у своей ВПП —
        /// «якоря и Безье» v0.102.2 (канон игрока: прямой выезд из ворот →
        /// одно плавное Безье → выкат по оси в торец в сторону взлёта; коллектор
        /// v0.100 отвергнут плейтестом — «ломаная»). Поверхность — та же
        /// полилиния грейдированной лентой от кромки апрона (padY — штифт
        /// старта: сход с плиты пандусом ≤ MaxGrade, не обрывом юбки);
        /// перекрытия прижаты заподлицо (TaxiSurface.TopAt). Хост-only; клиент
        /// сеть не синкает (руление AI — серверная логика).</summary>
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
                List<GlobalPosition> approach = ApproachPoints(airbase, to, start, fwd);

                // Дорога и полотно — одна полилиния: борт едет по точкам дороги
                // между узлами, асфальт ложится под колёса там же. Лента
                // стартует С КРОМКИ АПРОНА (габарит + 6 м, геометрия Pad) —
                // пандус накрывает юбку плиты; граф ведёт борта, а плиты-
                // коллайдеры несут шасси — без них на грунте (пустыня) тяжёлый
                // борт вязнет и его катапультирует.
                Vector3 padEdge = hangarBuilding.transform.position +
                    new Vector3(fwd.x, 0f, fwd.z).normalized *
                    (hangarBuilding.definition.length * 0.5f + 6f);
                var road = new Road();
                road.AddPoint(start);
                foreach (GlobalPosition p in approach)
                    road.AddPoint(p);
                road.AddPoint(to);
                road.CalcLength();
                net.roads.Add(road);
                net.RegenerateNetwork();
                // Сервис-точка у выхода ангара: севший борт после пробега рулит
                // по сети ОБРАТНО к апронам (TryGetNearestServicePoint), а не
                // паркуется на полосе вечным препятствием.
                AddServicePoint(airbase, start);
                var surface = new List<GlobalPosition> { padEdge.ToGlobalPosition(), start };
                surface.AddRange(approach);
                surface.Add(to);
                TaxiSurface.Register(surface, graded: true, startY: padY);
                Log.Info("Build: рулёжка дорисована — " + airbase.name +
                         " (дорог " + net.roads.Count + ")");
            }
            catch (System.Exception e)
            {
                Log.Error("Build: не удалось дорисовать рулёжку — " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
