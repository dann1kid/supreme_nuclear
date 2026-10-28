using System.Collections.Generic;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>КОЛЛЕКТОР рулёжной сети СВОЕГО аэродрома (v0.100, закрыл §12-38):
    /// ОДНА магистраль ПАРАЛЛЕЛЬНО оси ВПП на каждой стороне ангаров (отступ
    /// LaneOffsetM), съезд на полосу — ОДНА S-кривая у торца (тангенциальный
    /// въезд, v0.98.5); ангары подключаются КОРОТКИМИ коннекторами к СЕРЕДИНЕ
    /// магистрали — как у нативных аэродромов; подходы больше не сходятся
    /// пучком у торца и не пересекаются. До v0.100 каждый ангар строил
    /// собственный подвод к дорожке слияния. Магистраль создаётся лениво
    /// (первый ангар стороны) и растёт хвостами по мере ангаров дальше по
    /// полосе; якорь — торец, выбранный первым ангаром стороны (нативный
    /// паттерн «одна параллельная рулёжка на всю полосу, въезд с торца»).
    /// Нативные аэродромы НЕ трогаем: у них есть своя такси-сеть, ангар
    /// подключается к её узлу напрямую. Граф: коннектор добавляется последним
    /// и режет магистраль в точке стыка (Road.CheckIntersection → TrySplit,
    /// радиус 10 м), RegenerateNetwork склеивает концы в узел. Геометрия —
    /// чистые Util.Taxi* (тестируется); статический реестр сбрасывается в
    /// sceneLoaded (BuildTasks.Reset). Хост-only: дороги строит хост
    /// (руление — серверная логика).</summary>
    internal static class TaxiCollector
    {
        internal const float LaneOffsetM = 50f;   // отступ магистрали от оси ВПП
        internal const float JunctionMinM = 25f;  // мин. дальность стыка (вне зоны S-кривой)
        internal const float GrowM = 80f;         // запас магистрали за самым дальним ангаром
        internal const float MinFarM = 150f;      // мин. длина магистрали при создании

        /// <summary>План подключения ангара: чистая геометрия, ничего не строит
        /// (превью и стройка зовут одно и то же — превью не врёт).</summary>
        internal class Plan
        {
            public GlobalPosition Threshold;         // торец-якорь (может отличаться от «ближнего»: магистраль уже есть)
            public int Side;                         // +1/−1 относительно оси ВПП
            public GlobalPosition Junction;          // точка стыка на магистрали
            public List<GlobalPosition> Connector = new List<GlobalPosition>(); // между start и стыком
            public GlobalPosition ConnectorStripEnd; // конец ПОЛОТНА коннектора: срез по дальней кромке магистрали
            public bool Creates;                     // магистрали на стороне ещё нет
            public float NewFarM;                    // дальность магистрали после подключения
            public List<GlobalPosition> Body = new List<GlobalPosition>();     // Creates: торец → S → магистраль до конца
            public List<GlobalPosition> Extension = new List<GlobalPosition>(); // рост хвоста (старый конец → новый)
            public List<GlobalPosition> Route = new List<GlobalPosition>();    // маршрут до торца: стык → p1 → S → торец
        }

        private class Collector
        {
            public GlobalPosition Threshold; // якорь-торец
            public float FarM;               // текущая дальность магистрали от торца
        }

        private static readonly Dictionary<Airbase, Dictionary<int, Collector>> Registry =
            new Dictionary<Airbase, Dictionary<int, Collector>>();

        public static void Reset() => Registry.Clear();

        /// <summary>Сторона ангара относительно оси ВПП (+1/−1). Почти осевые
        /// ангары сюда не доходят — подключаются напрямую (BuildTasks.DirectPoints).</summary>
        internal static int SideOf(Vector3 threshold, Vector3 axis, Vector3 start) =>
            Vector3.Dot(new Vector3(-axis.z, 0f, axis.x), start - threshold) < 0f ? -1 : 1;

        /// <summary>Якорь существующей магистрали на стороне ангара (false — нет):
        /// подключаемся К НЕМУ, а не к «ближайшему» торцу — одна магистраль на
        /// сторону, без второй встречной поверх первой.</summary>
        internal static bool TryGetAnchor(Airbase airbase, int side,
            out GlobalPosition threshold, out float farM)
        {
            if (Registry.TryGetValue(airbase, out Dictionary<int, Collector> sides) &&
                sides.TryGetValue(side, out Collector c))
            {
                threshold = c.Threshold;
                farM = c.FarM;
                return true;
            }
            threshold = default;
            farM = -1f;
            return false;
        }

        /// <summary>Геометрия подключения (чистая): коннектор + создание/рост
        /// магистрали + маршрут до торца. existed=false — магистрали нет.</summary>
        internal static Plan PlanFor(GlobalPosition threshold, Vector3 axis, Vector3 farDir,
            GlobalPosition start, Vector3 exitFwd, bool existed, float farM)
        {
            Vector3 t = threshold.ToLocalPosition();
            Vector3 s = start.ToLocalPosition();
            Util.TaxiCollectorPlan(t, s, exitFwd, axis, farDir,
                LaneOffsetM, JunctionMinM, GrowM, MinFarM, existed ? farM : -1f,
                out Vector3 side, out Vector3 junction, out List<Vector3> connector, out float newFarM);
            var plan = new Plan
            {
                Threshold = threshold,
                Side = Vector3.Dot(new Vector3(-axis.z, 0f, axis.x), s - t) < 0f ? -1 : 1,
                Junction = junction.ToGlobalPosition(),
                ConnectorStripEnd = (junction + (junction - s).normalized * 12f).ToGlobalPosition(),
                Creates = !existed,
                NewFarM = newFarM,
            };
            foreach (Vector3 p in connector)
                plan.Connector.Add(p.ToGlobalPosition());

            Vector3 p1 = t + side * LaneOffsetM - farDir * LaneOffsetM;
            List<Vector3> merge = Util.TaxiMergeSamples(t, farDir, side, LaneOffsetM);
            if (plan.Creates)
            {
                plan.Body.Add(threshold);
                for (int i = merge.Count - 1; i >= 0; i--) // S от торца к p1 (обратно по t)
                    plan.Body.Add(merge[i].ToGlobalPosition());
                foreach (Vector3 p in Util.TaxiLaneSamples(t, farDir, side, LaneOffsetM,
                    -LaneOffsetM, newFarM)) // первая точка = p1
                    plan.Body.Add(p.ToGlobalPosition());
            }
            else if (newFarM > farM + 0.5f)
            {
                foreach (Vector3 p in Util.TaxiLaneSamples(t, farDir, side, LaneOffsetM,
                    farM, newFarM)) // первая = текущий конец (склейка узлов)
                    plan.Extension.Add(p.ToGlobalPosition());
            }
            plan.Route.Add(p1.ToGlobalPosition()); // прямой участок магистрали до входа S
            foreach (Vector3 p in merge)
                plan.Route.Add(p.ToGlobalPosition());
            plan.Route.Add(threshold);
            return plan;
        }

        /// <summary>Дороги магистрали из плана + отметка в реестре. Коннектор
        /// добавляет вызывающий (BuildTasks) и ЗАТИМ зовёт CheckIntersection —
        /// магистраль режется в точке стыка; последним — RegenerateNetwork.</summary>
        internal static void Apply(Airbase airbase, Plan plan, RoadNetwork net)
        {
            if (plan.Creates && plan.Body.Count >= 2)
                AddRoad(net, plan.Body);
            if (!plan.Creates && plan.Extension.Count >= 2)
                AddRoad(net, plan.Extension);
            if (!Registry.TryGetValue(airbase, out Dictionary<int, Collector> sides))
            {
                sides = new Dictionary<int, Collector>();
                Registry[airbase] = sides;
            }
            sides[plan.Side] = new Collector { Threshold = plan.Threshold, FarM = plan.NewFarM };
        }

        private static void AddRoad(RoadNetwork net, List<GlobalPosition> points)
        {
            var road = new Road();
            foreach (GlobalPosition p in points)
                road.AddPoint(p);
            road.CalcLength();
            net.roads.Add(road);
        }
    }
}
