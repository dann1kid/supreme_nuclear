using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Task-API v1.6 — очереди производства «фабрик» ЛЮБОГО типа (DESIGN 2.3a):
    /// депо (техника) и аэродром (авиация). Потоковая оплата головной позиции; нет денег —
    /// очередь стоит; пауза/цикл/отмена — на фабрике (кнопки нижней панели, DESIGN 2.10).
    /// Ралли-маршруты — для депо (вышедший юнит получает маршрут; авиации — после появления
    /// ссылки на борт в TrySpawnResult). Хост-авторитет, как BuildTasks.</summary>
    public static class ProductionTasks
    {
        public sealed class QueueItem
        {
            public VehicleDepot Depot;          // фабрика «наземная»...
            public Airbase Airbase;             // ...или «авиационная» (одно из двух)
            public VehicleDefinition GroundDef;
            public AircraftDefinition AirDef;
            public float Cost;
            public float Paid;
            public object Factory => Depot != null ? (object)Depot : (object)Airbase;
            public UnitDefinition UnitDef => (UnitDefinition)GroundDef ?? AirDef;
            public float Progress01 => Cost > 0f ? Mathf.Clamp01(Paid / Cost) : 0f;
        }

        private sealed class FactoryState
        {
            public bool Paused;
            public bool Cycling;
            public readonly List<UnitDefinition> CycleItems = new List<UnitDefinition>();
        }

        private static readonly List<QueueItem> Queue = new List<QueueItem>();
        private static readonly Dictionary<object, FactoryState> Factories =
            new Dictionary<object, FactoryState>();

        // Ралли-маршруты фабрик: сложные цепочки, как приказы юнитам (DESIGN 2.3a).
        private static readonly Dictionary<object, List<GlobalPosition>> Rally =
            new Dictionary<object, List<GlobalPosition>>();
        private static readonly HashSet<Unit> Routed = new HashSet<Unit>();

        public static int Version { get; private set; }

        public static IReadOnlyList<QueueItem> All => Queue;

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
            Queue.Clear();
            Factories.Clear();
            Rally.Clear();
            Routed.Clear();
        }

        private static FactoryState StateOf(object factory)
        {
            if (!Factories.TryGetValue(factory, out FactoryState state))
                Factories[factory] = state = new FactoryState();
            return state;
        }

        public static int CountFor(object factory)
        {
            int n = 0;
            foreach (QueueItem item in Queue)
                if (item.Factory == factory)
                    n++;
            return n;
        }

        public static bool IsPaused(object factory) =>
            Factories.TryGetValue(factory, out FactoryState s) && s.Paused;

        public static bool IsCycling(object factory) =>
            Factories.TryGetValue(factory, out FactoryState s) && s.Cycling;

        public static void EnqueueGround(VehicleDepot depot, VehicleDefinition def)
        {
            if (!IsHost || depot == null || def == null)
                return;
            Queue.Add(new QueueItem
            {
                Depot = depot,
                GroundDef = def,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            });
            Version++;
        }

        public static void EnqueueAir(Airbase airbase, AircraftDefinition def)
        {
            if (!IsHost || airbase == null || def == null)
                return;
            Queue.Add(new QueueItem
            {
                Airbase = airbase,
                AirDef = def,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            });
            Version++;
        }

        /// <summary>Отмена позиции очереди с возвратом уплаченного (семантика SupCom).</summary>
        public static void Cancel(QueueItem item)
        {
            if (!IsHost || item == null || !Queue.Remove(item))
                return;
            Version++;
            if (item.Paid > 0f)
                FactionOf(item)?.AddFunds(item.Paid);
        }

        private static FactionHQ FactionOf(QueueItem item) =>
            item.Depot != null ? item.Depot.NetworkHQ : item.Airbase?.CurrentHQ;

        public static void TogglePause(object factory)
        {
            if (!IsHost || factory == null)
                return;
            Version++;
            FactoryState state = StateOf(factory);
            state.Paused = !state.Paused;
        }

        /// <summary>Цикл (зацикленная очередь): включение снимает текущую очередь фабрики;
        /// когда опустеет — снимок ставится заново.</summary>
        public static void ToggleCycle(object factory)
        {
            if (!IsHost || factory == null)
                return;
            Version++;
            FactoryState state = StateOf(factory);
            state.Cycling = !state.Cycling;
            state.CycleItems.Clear();
            if (!state.Cycling)
                return;
            foreach (QueueItem item in Queue)
                if (item.Factory == factory && item.UnitDef != null)
                    state.CycleItems.Add(item.UnitDef);
        }

        public static IReadOnlyList<GlobalPosition> RallyOf(object factory) =>
            Rally.TryGetValue(factory, out List<GlobalPosition> route) ? route : null;

        public static void SetRally(object factory, Vector3 point)
        {
            if (factory == null)
                return;
            Rally[factory] = new List<GlobalPosition> { point.ToGlobalPosition() };
            Version++;
        }

        public static void EnqueueRally(object factory, Vector3 point)
        {
            if (factory == null)
                return;
            if (!Rally.TryGetValue(factory, out List<GlobalPosition> route))
                route = Rally[factory] = new List<GlobalPosition>();
            route.Add(point.ToGlobalPosition());
            Version++;
        }

        public static void ClearRally(object factory)
        {
            if (factory != null && Rally.Remove(factory))
                Version++;
        }

        /// <summary>Ключ состояния очереди фабрики для кэша панели.</summary>
        public static int KeyFor(object factory)
        {
            int key = (IsPaused(factory) ? 1 : 0) + (IsCycling(factory) ? 2 : 0);
            foreach (QueueItem item in Queue)
                if (item.Factory == factory)
                    key = key * 31 + (item.UnitDef != null ? item.UnitDef.GetInstanceID() * 7 : 0) +
                         (int)(item.Progress01 * 20f);
            return key;
        }

        /// <summary>Хост-тик: оплата головной позиции каждой фабрики, выпуск по готовности
        /// (депо занят — позиция оплачена и повторит попытку; ангары заняты — ждём).</summary>
        public static void Tick()
        {
            if (!GameApi.InMission || !IsHost || Queue.Count == 0)
                return;
            float dt = Time.deltaTime;
            var paidFactories = new HashSet<object>();

            for (int i = 0; i < Queue.Count; )
            {
                QueueItem item = Queue[i];
                FactionHQ hq = FactionOf(item);
                bool dead = hq == null ||
                            (item.Depot == null && item.Airbase == null) ||
                            (item.Depot != null && item.Depot.disabled);
                if (dead)
                {
                    hq?.AddFunds(item.Paid); // фабрика умерла — возврат
                    Queue.RemoveAt(i);
                    Version++;
                    continue;
                }

                object factory = item.Factory;
                if (paidFactories.Contains(factory))
                {
                    i++;
                    continue;
                }
                paidFactories.Add(factory);

                FactoryState state = StateOf(factory);
                if (!state.Paused)
                {
                    float rate = item.Cost / Mathf.Max(1f, Settings.ProductionSecondsPerItem);
                    float pay = Mathf.Min(rate * dt, item.Cost - item.Paid, hq.factionFunds);
                    if (pay > 0f)
                    {
                        item.Paid += pay;
                        hq.AddFunds(-pay);
                    }
                }

                if (item.Paid >= item.Cost - 0.01f && !state.Paused)
                {
                    bool spawned = false;
                    if (item.Depot != null)
                        spawned = item.Depot.TrySpawnVehicle(item.GroundDef);
                    else if (item.Airbase != null && item.AirDef != null &&
                             item.Airbase.CanSpawnAircraft(item.AirDef))
                    {
                        int livery = item.AirDef.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
                        spawned = item.Airbase.TrySpawnAircraft(
                            null, item.AirDef, new LiveryKey(livery), new Loadout(), 1f).Allowed;
                    }

                    if (spawned)
                    {
                        Queue.RemoveAt(i);
                        Version++;
                        if (item.Depot != null)
                            DispatchByRally(item.Depot, item.GroundDef);
                        OnQueueAdvanced(factory);
                        continue; // на этом месте уже следующая позиция
                    }
                    // занято (кулдаун депо / ангары) — оплачено, повторим следующим тиком
                }
                i++;
            }
        }

        /// <summary>Захват вышедшего юнита депо (ссылку не отдаёт — ищем свежий
        /// GroundVehicle нужного определения в зоне фабрики, ещё не отправленный).</summary>
        private static void DispatchByRally(VehicleDepot depot, VehicleDefinition def)
        {
            if (!Rally.TryGetValue(depot, out List<GlobalPosition> route) || route.Count == 0)
                return;
            Vector3 p = depot.transform.position;
            const float radiusSq = 80f * 80f;
            Unit spawned = null;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || Routed.Contains(unit))
                    continue;
                if (!(unit is GroundVehicle) || unit.definition != def)
                    continue;
                if (unit.NetworkHQ != depot.NetworkHQ)
                    continue;
                Vector3 q = unit.transform.position;
                if (new Vector2(q.x - p.x, q.z - p.z).sqrMagnitude < radiusSq)
                {
                    spawned = unit;
                    break;
                }
            }
            if (spawned == null)
                return;
            Routed.Add(spawned);
            GroundOrders.Set(spawned, route[0]);
            for (int i = 1; i < route.Count; i++)
                GroundOrders.Enqueue(spawned, route[i]);
        }

        private static void OnQueueAdvanced(object factory)
        {
            if (!Factories.TryGetValue(factory, out FactoryState state) ||
                !state.Cycling || CountFor(factory) > 0)
                return;
            foreach (UnitDefinition def in state.CycleItems)
            {
                if (def is VehicleDefinition vd && factory is VehicleDepot depot)
                    Queue.Add(new QueueItem
                    {
                        Depot = depot,
                        GroundDef = vd,
                        Cost = vd.value > 0f ? vd.value : Settings.BuildDefaultCost,
                    });
                else if (def is AircraftDefinition ad && factory is Airbase airbase)
                    Queue.Add(new QueueItem
                    {
                        Airbase = airbase,
                        AirDef = ad,
                        Cost = ad.value > 0f ? ad.value : Settings.BuildDefaultCost,
                    });
            }
            if (CountFor(factory) > 0)
                Version++;
        }
    }
}
