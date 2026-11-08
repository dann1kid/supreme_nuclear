using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Task-API v1.6 — очереди производства «фабрик» ЛЮБОГО типа (DESIGN 2.3a):
    /// депо (техника), ангар (авиация) и верфь (флот). Потоковая оплата головной позиции;
    /// нет денег — очередь стоит; пауза/цикл/отмена — на фабрике (кнопки нижней панели,
    /// DESIGN 2.10), при мультивыделении — сразу всем фабрикам выделения. Ралли-маршруты —
    /// у любой фабрики: депо отдаёт маршрут вышедшему юниту, верфь — спущенному кораблю,
    /// ангар — свежему борту по смене приватного Hangar.spawnedObject (SelfCheck; отложенный
    /// спавн авианосца ждёт смены ссылки в Tick), ангар без своего ралли наследует ралли
    /// аэродрома. Хост-авторитет, как BuildTasks.</summary>
    public static partial class ProductionTasks
    {
        public sealed class QueueItem
        {
            public VehicleDepot Depot;          // фабрика «наземная»...
            public Hangar Hangar;               // ...или ангар (индивидуальная авиафабрия)
            public Building Dock;               // ...или верфь (флот, v0.92)
            public BuildTasks.Task Site;        // ...или НЕДОСТРОЙ (v0.107): очередь копится
                                                // до завершения стройки, затем AdoptSite
                                                // переносит позиции на живую фабрику
            public bool IsUpgrade;              // позиция = АПГРЕЙД ТИРА фабрики (не юнит):
                                                // встаёт ГОЛОВОЙ и занимает производство (SupCom)
            public int ToTier;                  // ...целевой тир апгрейда
            public bool UpToHq;                 // ...ШТАБ (полная цена, полный ростер; иначе — дешёвая версия, канон FAF)
            public float UpgradeSeconds;        // ...темп налива апгрейда (factory.json)
            public VehicleDefinition GroundDef;
            public AircraftDefinition AirDef;
            public ShipDefinition NavalDef;
            public Loadout Loadout;             // лодаут авиазаказа (null = «Стандарт») — запечён при постановке
            public float Fuel = 1f;             // уровень топлива заказа (FuelRatio пресета)
            public float Cost;
            public float EnergyNeed;            // явная энергопотребность апгрейда (масса = Cost)
            public float PaidMass;   // налито массы (factionFunds)
            public float PaidEnergy; // налито энергии (сток EconomyService)
            public bool EnergyStalled; // не наливается: энергосток пуст (гейты
                // зелёные, DrainStep режет обе валюты; плейтест 2026-11-03: «вторая
                // фабрика не апается» = общий энергосток фракции)
            /// <summary>Полная энергопотребность позиции: апгрейд — явная цена
            /// профиля, юнит — стоимость × фактор профиля.</summary>
            public float EnergyCost => IsUpgrade && EnergyNeed > 0f
                ? EnergyNeed
                : Cost * EconomyService.EnergyCostFactor;
            public object Factory => Depot != null ? (object)Depot
                : Hangar != null ? (object)Hangar
                : Dock != null ? (object)Dock
                : (object)Site;
            public UnitDefinition UnitDef => (UnitDefinition)GroundDef
                ?? (UnitDefinition)AirDef ?? NavalDef;
            public float Progress01 => Cost > 0f ? Mathf.Clamp01(PaidMass / Cost) : 0f;
        }

        private sealed class FactoryState
        {
            public bool Paused;
            public bool Cycling;
            public readonly List<UnitDefinition> CycleItems = new List<UnitDefinition>();
        }

        private static readonly List<QueueItem> Queue = new List<QueueItem>();

        /// <summary>Переиспользуемый словарь занятых слотов оплаты тика (раньше —
        /// new Dictionary на каждый кадр при непустой очереди).</summary>
        private static readonly Dictionary<(object factory, UnitDefinition def), int> PaidSlotsMap =
            new Dictionary<(object factory, UnitDefinition def), int>();
        /// <summary>Троттл диагностики апгрейд-позиций (5 с); фабрики наливаются
        /// независимо — склейки через аэродром нет с 2026-11-03.</summary>
        private static float _nextUpgradeDiag;
        private static float _nextStallNotice; // троттл Notice энергостолла (10 с)
        private static readonly Dictionary<object, FactoryState> Factories =
            new Dictionary<object, FactoryState>();

        // Ралли-маршруты фабрик: сложные цепочки, как приказы юнитам (DESIGN 2.3a).
        // Каждая точка несёт groupId клика (0 — нет): спущенный юнит получает цепочку
        // ГРУППОВЫМИ вейпоинтами — маркер/лента/драг/удаление общие на пачку
        // выпущенных одним маршрутом, как у приказов юнитам напрямую (§12-30).
        public sealed class RallyRoute
        {
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            /// <summary>Группа приказа каждой точки, параллельно Points.</summary>
            public readonly List<int> Groups = new List<int>();
            /// <summary>Патрульный цикл (§12-30, ПКМ по точке ралли): будущие выходы
            /// фабрики получают ПАТРУЛЬ по точкам маршрута вместо линейной цепочки.
            /// Флаг маршрута: новые точки (обычное ралли) дописываются в тот же
            /// цикл; уже спущенные юниты не ретроактивны (правится ИСТОЧНИК).</summary>
            public bool Patrol;
            public int Count => Points.Count;
        }
        private static readonly Dictionary<object, RallyRoute> Rally =
            new Dictionary<object, RallyRoute>();
        private static readonly HashSet<Unit> Routed = new HashSet<Unit>();

        // Ралли авиации: TrySpawnResult борт не отдаёт — приватный Hangar.spawnedObject
        // перезаписывается игрой в SpawnAircraft. Немедленный спавн: ссылка свежая в тот
        // же кадр; отложенный (двери авианосца) — ждём смены ссылки (запомнив ПРОШЛУЮ).
        private static readonly System.Reflection.FieldInfo HangarSpawnedField =
            HarmonyLib.AccessTools.Field(typeof(Hangar), "spawnedObject");
        private sealed class AirRallyWait
        {
            public GameObject Prev;
            public float Deadline;
        }
        private static readonly Dictionary<Hangar, AirRallyWait> AirRallyWaits =
            new Dictionary<Hangar, AirRallyWait>();
        private const float AirRallyWaitSec = 30f;
        private static readonly List<Hangar> _settledWaits = new List<Hangar>();

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
            AirRallyWaits.Clear();
            _siteAirCatalogs.Clear();
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
            // Потолок — «пендящий» тир (с учётом апгрейдов в очереди): очередь
            // программируется наперёд (SupCom: T1-пачка → →T2 → T2-инженеры...).
            if (UnitRoster.TierOf(def) > PendingTierOf(depot))
            {
                CommanderHud.Notice(Loc.F("Фабрика T{0} не строит T{1} — нужен апгрейд",
                    PendingTierOf(depot), UnitRoster.TierOf(def)));
                return;
            }
            // HQ-модель: полный ростер T2+ — только HQ-фабрике или при живом HQ
            // тира (дешёвые без HQ — инженеры). Заказ-гейт зеркален гейту оплаты.
            if (!FactoryTierService.CanProduce(depot, def))
            {
                CommanderHud.Notice(Loc.F("Не строится без живого ШТАБ T{0} — постройте HQ",
                    UnitRoster.TierOf(def)));
                return;
            }
            Queue.Add(new QueueItem
            {
                Depot = depot,
                GroundDef = def,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            });
            Version++;
        }

        /// <summary>Заказ в КОНКРЕТНЫЙ ангар (индивидуальная фабрия, v0.88):
        /// каталог и слот — только его собственные. Группа ангаров = по одному
        /// в каждый способный (SupCom-паттерн, см. SupcomUi).</summary>
        public static void EnqueueAir(Hangar hangar, AircraftDefinition def)
        {
            if (!EnqueueAirSilent(hangar, def))
                CommanderHud.Notice(Loc.F("Ангар не строит: {0}",
                    Catalog.ShortLabel(def.code, def.unitName)));
        }

        /// <summary>Постановка без уведомлений — групповой заказ (отчёт ведёт вызов).</summary>
        public static bool EnqueueAirSilent(Hangar hangar, AircraftDefinition def)
        {
            if (!IsHost || !CanBuild(hangar, def) ||
                UnitRoster.TierOf(def) > PendingTierOf(hangar))
                return false;
            var item = new QueueItem
            {
                Hangar = hangar,
                AirDef = def,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            };
            BakeLoadout(item, hangar.parentAirbase);
            Queue.Add(item);
            Version++;
            return true;
        }

        /// <summary>Заказ в «подходящий» ангар аэродрома (панель аэропорта и шаблоны):
        /// живой ангар, умеющий строить тип, с самой короткой очередью.</summary>
        public static bool EnqueueAirDistributed(Airbase airbase, AircraftDefinition def)
        {
            if (!IsHost || airbase == null || def == null)
                return false;
            Hangar best = null;
            int bestCount = int.MaxValue;
            foreach (Hangar hangar in airbase.hangars)
            {
                Unit unit = hangar != null ? hangar.attachedUnit : null;
                if (unit == null || unit.disabled || !CanBuild(hangar, def) ||
                    UnitRoster.TierOf(def) > PendingTierOf(hangar))
                    continue;
                int count = CountFor(hangar);
                if (count < bestCount)
                {
                    bestCount = count;
                    best = hangar;
                }
            }
            if (best == null)
            {
                CommanderHud.Notice(Loc.F("Нет работоспособного ангара под: {0}",
                    Catalog.ShortLabel(def.code, def.unitName)));
                return false;
            }
            EnqueueAir(best, def);
            return true;
        }

        /// <summary>Отмена позиции очереди с возвратом уплаченного (семантика SupCom).</summary>
        public static void Cancel(QueueItem item)
        {
            if (!IsHost || item == null || !Queue.Remove(item))
                return;
            Version++;
            Refund(item);
        }

        /// <summary>Возврат уплаченного позицией: масса — в фонд, энергия — в сток.</summary>
        private static void Refund(QueueItem item)
        {
            if (item.PaidMass <= 0f && item.PaidEnergy <= 0f)
                return;
            FactionHQ hq = FactionOf(item);
            if (hq == null)
                return;
            hq.AddFunds(item.PaidMass);
            EconomyService.Refund(hq, item.PaidEnergy);
        }

        /// <summary>Добавить count копий позиции СРАЗУ после якоря — визуально в тот же
        /// стак очереди (ЛКМ по плитке стака). Хост-only.</summary>
        public static void InsertAfter(QueueItem anchor, int count)
        {
            if (!IsHost || anchor == null || count <= 0)
                return;
            int idx = Queue.IndexOf(anchor);
            if (idx < 0)
                return;
            idx++;
            for (int i = 0; i < count; i++)
                Queue.Insert(idx + i, new QueueItem
                {
                    Depot = anchor.Depot,
                    Hangar = anchor.Hangar,
                    Dock = anchor.Dock,
                    Site = anchor.Site,
                    GroundDef = anchor.GroundDef,
                    AirDef = anchor.AirDef,
                    NavalDef = anchor.NavalDef,
                    Loadout = anchor.Loadout,   // стак = та же конфигурация
                    Fuel = anchor.Fuel,
                    Cost = anchor.Cost,
                });
            Version++;
        }

        /// <summary>Снять до count ПОСЛЕДНИХ позиций стака def у фабрики (ПКМ по плитке
        /// очереди): уплаченное за снятые возвращается; головная позиция стака (в работе)
        /// снимается последней, когда счётчик до неё доходит.</summary>
        public static void RemoveTail(object factory, UnitDefinition def, int count)
        {
            if (!IsHost || factory == null || def == null || count <= 0)
                return;
            for (int i = Queue.Count - 1; i >= 0 && count > 0; i--)
            {
                QueueItem item = Queue[i];
                if (item.Factory != factory || item.UnitDef != def)
                    continue;
                Queue.RemoveAt(i);
                Version++;
                Refund(item);
                count--;
            }
        }

        /// <summary>Снять до count ПОСЛЕДНИХ позиций def у АЭРОДРОМА-агрегата
        /// (панель вышки/авианосца): позиции живут пер-ангарно — снимаем с самого
        /// ДЛИННОГО ангара, выравнивая очереди (зеркало «в самый свободный»
        /// у EnqueueAirDistributed).</summary>
        public static void RemoveTailAir(Airbase airbase, UnitDefinition def, int count)
        {
            if (!IsHost || airbase == null || def == null || count <= 0)
                return;
            while (count > 0)
            {
                Hangar best = null;
                int bestCount = 0;
                if (airbase.hangars != null)
                    foreach (Hangar hangar in airbase.hangars)
                    {
                        if (hangar == null)
                            continue;
                        int n = 0;
                        foreach (QueueItem item in Queue)
                            if (item.Factory == (object)hangar && item.UnitDef == def)
                                n++;
                        if (n > bestCount)
                        {
                            bestCount = n;
                            best = hangar;
                        }
                    }
                if (best == null)
                    return;
                int take = Mathf.Min(count, bestCount);
                RemoveTail(best, def, take);
                count -= take;
            }
        }

        private static FactionHQ FactionOf(QueueItem item) =>
            item.Depot != null ? item.Depot.NetworkHQ
                : item.Hangar != null ? item.Hangar?.attachedUnit?.NetworkHQ
                : item.Dock != null ? item.Dock?.NetworkHQ
                : item.Site?.Faction;

        /// <summary>Групповая пауза (мультивыделение фабрик): состояние УНИФИЦИРУЕТСЯ —
        /// все на паузу либо все сняты, а не инверсия каждого (смешанное выделение
        /// не должно переворачиваться в противоположное смешанное).</summary>
        public static void TogglePauseAll(IReadOnlyList<object> factories)
        {
            if (!IsHost || factories == null || factories.Count == 0)
                return;
            foreach (object factory in factories)
                if (factory != null && !IsPaused(factory))
                {
                    SetPausedForAll(factories, true);
                    return;
                }
            SetPausedForAll(factories, false);
        }

        private static void SetPausedForAll(IReadOnlyList<object> factories, bool paused)
        {
            foreach (object factory in factories)
                if (factory != null)
                    StateOf(factory).Paused = paused;
            Version++;
        }

        /// <summary>Все фабрики списка на паузе (тон кнопки; пустой список — false).</summary>
        public static bool AllPaused(IReadOnlyList<object> factories)
        {
            if (factories == null || factories.Count == 0)
                return false;
            foreach (object factory in factories)
                if (factory != null && !IsPaused(factory))
                    return false;
            return true;
        }

        /// <summary>Цикл (зацикленная очередь) фабрики: включение снимает текущую
        /// очередь; когда опустеет — снимок ставится заново.</summary>
        public static void SetCycling(object factory, bool cycling)
        {
            if (!IsHost || factory == null)
                return;
            Version++;
            FactoryState state = StateOf(factory);
            state.Cycling = cycling;
            state.CycleItems.Clear();
            if (!cycling)
                return;
            foreach (QueueItem item in Queue)
                if (item.Factory == factory && item.UnitDef != null)
                    state.CycleItems.Add(item.UnitDef);
        }

        /// <summary>Групповой цикл (мультивыделение): унификация, как у паузы —
        /// включение снимает текущую очередь каждой фабрии, выключение чистит снимки.</summary>
        public static void ToggleCycleAll(IReadOnlyList<object> factories)
        {
            if (!IsHost || factories == null || factories.Count == 0)
                return;
            bool anyOff = false;
            foreach (object factory in factories)
                if (factory != null && !IsCycling(factory))
                {
                    anyOff = true;
                    break;
                }
            foreach (object factory in factories)
                if (factory != null)
                    SetCycling(factory, anyOff);
        }

        /// <summary>Все фабрики списка в цикле (тон кнопки; пустой список — false).</summary>
        public static bool AllCycling(IReadOnlyList<object> factories)
        {
            if (factories == null || factories.Count == 0)
                return false;
            foreach (object factory in factories)
                if (factory != null && !IsCycling(factory))
                    return false;
            return true;
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

        /// <summary>Умеет ли КОНКРЕТНЫЙ ангар строить тип (его собственный список;
        /// у вертолётных и самолётных площадок он разный).</summary>
        public static bool CanBuild(Hangar hangar, AircraftDefinition def) =>
            hangar != null && def != null && hangar.GetAvailableAircraft() != null &&
            System.Array.IndexOf(hangar.GetAvailableAircraft(), def) >= 0;

        /// <summary>Слотов оплаты у индивидуальной фабрии — один (ангара/депо):
        /// параллельность рождается из числа ангаров с очередями (v0.88 — пер-
        /// ангарные очереди вместо аэродромных; решено по плейтесту: группа ангаров
        /// + пакетный заказ, и физика обломков требует рассредоточения).</summary>
        private static int PaySlots(QueueItem item) => 1;

    }
}
