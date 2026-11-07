using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть ProductionTasks: ралли-маршруты фабрик, хост-тик очереди, диспетчеризация спущенных.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; общее состояние и сводка API — ProductionTasks.cs.
    public static partial class ProductionTasks
    {
        public static RallyRoute RallyOf(object factory) =>
            Rally.TryGetValue(factory, out RallyRoute route) ? route : null;

        /// <summary>Ралли-маршрут фабрики → патрульный цикл будущих выходов (§12-30,
        /// ПКМ по точке ралли — зеркально конвертации вейпоинтов юнитов): флаг
        /// маршрута, точки/группы не меняются. Уже спущенные цепочки не ретроактивны.
        /// true — маршрут есть и теперь патрульный (повторный клик — идемпотентно).</summary>
        public static bool ConvertRallyToPatrol(object factory)
        {
            if (!Rally.TryGetValue(factory, out RallyRoute route) || route.Count == 0)
                return false;
            if (!route.Patrol)
            {
                route.Patrol = true;
                Version++;
            }
            return true;
        }

        /// <summary>Маршрут ралли фабрики — патрульный цикл? (отрисовка замыкающей
        /// ноги DrawFactoryRally; нет маршрута — false).</summary>
        public static bool RallyIsPatrol(object factory) =>
            Rally.TryGetValue(factory, out RallyRoute route) && route.Patrol;

        /// <summary>Ралли своей фракции? AI-фабрики ставят ралли в тот же реестр —
        /// чужие точки сбора не рисуются и не пикаются (зеркально CollectRallies).</summary>
        public static bool IsOwnRally(object factory, FactionHQ hq)
        {
            switch (factory)
            {
                case BuildTasks.Task site: // недострой-фабрика
                    return site.Faction == hq;
                case Hangar hangar: // ангар ключуется сам, юнит — attachedUnit
                    Unit hu = hangar != null ? hangar.attachedUnit : null;
                    return hu != null && GameApi.IsFriendlyUnit(hu, hq);
                case Airbase airbase:
                    return airbase != null && airbase.CurrentHQ == hq;
                case Building building: // VehicleDepot и док-корпус верфи
                    return building != null && GameApi.IsFriendlyUnit(building, hq);
                default:
                    return false;
            }
        }

        /// <summary>Все фабрики с живыми ралли-маршрутами (отрисовка Shift-режима
        /// видимости: невыделенные фабрики — приглушённо, как цепочки юнитов).
        /// only ≠ null — только своей фракции (AI делит реестр ралли).</summary>
        public static void CollectRallies(List<object> factories, FactionHQ only = null)
        {
            factories.Clear();
            foreach (KeyValuePair<object, RallyRoute> kv in Rally)
                if (kv.Value.Count > 0 && (only == null || IsOwnRally(kv.Key, only)))
                    factories.Add(kv.Key);
        }

        public static void SetRally(object factory, Vector3 point, int groupId)
        {
            if (factory == null)
                return;
            var route = new RallyRoute();
            route.Points.Add(point.ToGlobalPosition());
            route.Groups.Add(groupId);
            Rally[factory] = route;
            Version++;
        }

        public static void EnqueueRally(object factory, Vector3 point, int groupId)
        {
            if (factory == null)
                return;
            if (!Rally.TryGetValue(factory, out RallyRoute route))
                Rally[factory] = route = new RallyRoute();
            route.Points.Add(point.ToGlobalPosition());
            route.Groups.Add(groupId);
            Version++;
        }

        public static void ClearRally(object factory)
        {
            if (factory != null && Rally.Remove(factory))
                Version++;
        }

        // ---------- редактирование точек ралли (драг/удаление, v0.108; §12-30) ----------
        // Сигнатуры зеркальны GroupIdOf/CollectGroup/RemoveByGroupId сервисов приказов:
        // точки ралли — тот же мир групповых вейпоинтов, «фабрика» вместо «юнита».

        /// <summary>Группа клика точки ралли (0 — нет), зеркально GroupIdOf сервисов.</summary>
        public static int RallyGroupIdOf(object factory, int index)
        {
            return Rally.TryGetValue(factory, out RallyRoute route) &&
                   index >= 0 && index < route.Groups.Count
                ? route.Groups[index]
                : 0;
        }

        /// <summary>Члены группы точки ралли: ВСЕ фабрики, чей маршрут несёт этот
        /// groupId (один клик задаёт одну точку каждой фабрике выделения — лента
        /// дедупится в одну, вейпоинт правится там, где рисуется). Пары
        /// (фабрика, индекс) — все точки с groupId (у фабрики их обычно одна).</summary>
        public static void CollectRallyGroup(int groupId, List<object> factories, List<int> indices)
        {
            factories.Clear();
            indices.Clear();
            if (groupId == 0)
                return;
            foreach (KeyValuePair<object, RallyRoute> kv in Rally)
                for (int i = 0; i < kv.Value.Groups.Count; i++)
                    if (kv.Value.Groups[i] == groupId)
                    {
                        factories.Add(kv.Key);
                        indices.Add(i);
                    }
        }

        /// <summary>Переместить точку ралли (драг): y ложится на рельеф, зеркально
        /// GroundOrders.SetQueuePoint. Спущенные по старой точке юниты сохраняют
        /// свои цепочки — правится ИСТОЧНИК, будущие спуски идут в новое место.</summary>
        public static void SetRallyPoint(object factory, int index, Vector3 world)
        {
            if (!Rally.TryGetValue(factory, out RallyRoute route) ||
                index < 0 || index >= route.Points.Count)
                return;
            float y = GameApi.GroundY(world.x, world.z);
            route.Points[index] = new Vector3(world.x, y, world.z).ToGlobalPosition();
            Version++;
        }

        /// <summary>Удалить точку ралли (Ctrl+Shift+ПКМ). Маршрут может опустеть —
        /// пустое ралли перестаёт действовать (CollectRallies пустые не отдаёт).</summary>
        public static void RemoveRallyPoint(object factory, int index)
        {
            if (!Rally.TryGetValue(factory, out RallyRoute route) ||
                index < 0 || index >= route.Points.Count)
                return;
            route.Points.RemoveAt(index);
            route.Groups.RemoveAt(index);
            Version++;
        }

        /// <summary>Удалить ВСЕ точки группы клика из маршрута фабрики (групповой
        /// фан-аут удаления, зеркально RemoveByGroupId сервисов).</summary>
        public static void RemoveRallyByGroupId(object factory, int groupId)
        {
            if (groupId == 0 || !Rally.TryGetValue(factory, out RallyRoute route))
                return;
            bool removed = false;
            for (int i = route.Groups.Count - 1; i >= 0; i--)
                if (route.Groups[i] == groupId)
                {
                    route.Groups.RemoveAt(i);
                    route.Points.RemoveAt(i);
                    removed = true;
                }
            if (removed)
                Version++;
        }

        /// <summary>Хост-тик: потоковая оплата головной позиции каждой фабрики
        /// (ангара/депо — по одному потоку на фабрику, параллельность = число
        /// фабрик), выпуск по готовности (ангар занят циклом дверей — оплаченная
        /// позиция ждёт и повторяет попытку).</summary>
        public static void Tick()
        {
            if (!GameApi.InMission || !IsHost)
                return;
            PumpAirRallyWaits(); // двери авианосца могли выпустить борт после сдачи позиции
            if (Queue.Count == 0)
                return;
            float dt = Time.deltaTime;
            var paidSlots = PaidSlotsMap;
            paidSlots.Clear();

            for (int i = 0; i < Queue.Count; )
            {
                QueueItem item = Queue[i];
                FactionHQ hq = FactionOf(item);
                bool dead = hq == null ||
                            (item.Depot == null && item.Hangar == null &&
                             item.Dock == null && item.Site == null &&
                             item.Base == null) ||
                            (item.Depot != null && item.Depot.disabled) ||
                            (item.Hangar != null && item.Hangar.Disabled) ||
                            (item.Dock != null && item.Dock.disabled) ||
                            (item.Base != null && !AirbaseAlive(item.Base));
                if (dead)
                {
                    Refund(item); // фабрика умерла — возврат
                    Queue.RemoveAt(i);
                    Version++;
                    continue;
                }

                // Недострой (v0.107): фабрики ещё нет — заказы только копятся,
                // ни оплаты, ни выпуска. Площадку снесли/потеряли — позиции
                // умирают (уплаченного нет: оплата не начиналась); успешная
                // достройка переезжает на фабрику в AdoptSite ДО этого тика.
                if (item.Site != null)
                {
                    if (!BuildTasks.IsActive(item.Site))
                    {
                        Refund(item);
                        Queue.RemoveAt(i);
                        Version++;
                        DropSiteState(item.Site);
                    }
                    else
                        i++;
                    continue;
                }

                // Слот гейтит только ОПЛАТУ: выпуск оплаченных позиций не гейтится —
                // ангар мог заняться/занят и после оплаты, оплаченное ждёт.
                // Потоки: ангар — ПЕР-ТИПОВОЙ (v0.74, вертолётный/самолётный пулы
                // независимы); депо и ВЕРФЬ — ОДИН поток на фабрику (SupCom-канон:
                // наливается головная позиция, параллельность = число фабрик;
                // плейтест v0.96: верфь с разными типами кораблей платилась в 2+).
                object factory = item.Factory;
                var slotKey = (factory: factory,
                    def: item.Depot != null || item.Dock != null || item.Base != null
                        ? null : item.UnitDef);
                paidSlots.TryGetValue(slotKey, out int usedSlot);
                // Фабрики РАЗДЕЛЕНЫ (юзер-канон 2026-11-03): каждый ангар — своя
                // фабрика со своим потоком; былой «апгрейд базы занимает все
                // ангары» (baseBusy) упразднён вместе со склейкой через аэродром.
                // Скрипт очереди: юнит тира выше РАЗРЕШЁННОГО ждёт (заказать
                // можно заранее: гейт постановки смотрит на «пендящий» тир).
                bool tierWait = !item.IsUpgrade && item.UnitDef != null &&
                                !FactoryTierService.CanProduce(factory, item.UnitDef);
                // CanProduce = тир И HQ-ростер: дешёвая фабрика, потерявшая HQ
                // тира, НЕ наливает позиции неразрешённых юнитов (деградация
                // канона FAF; инженеры остаются). Позиция ждёт в очереди.
                bool canPay = !tierWait && usedSlot < PaySlots(item);
                if (canPay)
                    paidSlots[slotKey] = usedSlot + 1;

                FactoryState state = StateOf(factory);
                // Диагностика «апгрейд встал и не наливается» (плейтест 2026-11-03:
                // у ВТОРОГО депо позиция 0% вечно — тогда чиним по этим строкам):
                // логируем ТОЛЬКО позиции БЕЗ единого налитого рубля — здоровые
                // апгрейды начинают течь за секунды и лог не засоряют.
                if (item.IsUpgrade && item.PaidMass < 1f &&
                    Time.unscaledTime >= _nextUpgradeDiag)
                {
                    _nextUpgradeDiag = Time.unscaledTime + 5f;
                    Log.Info("UpgradeDiag: " + factory.GetType().Name + "#" + factory.GetHashCode() +
                        " →T" + item.ToTier + " — paid " + item.PaidMass.ToString("F0") + "/" +
                        item.Cost.ToString("F0") + ", canPay=" + canPay + ", paused=" + state.Paused +
                        ", slot=" + usedSlot + ", tierWait=" + tierWait +
                        ", funds=" + (hq != null ? hq.factionFunds.ToString("F0") : "?") +
                        ", energyOn=" + EconomyService.EnergyActive);
                }
                if (canPay && !state.Paused)
                {
                    // Темп: апгрейд — из factory.json; флот тяжелее техники — свой
                    // темп производства (Settings/Naval); прочее — Settings.
                    float seconds = item.IsUpgrade ? item.UpgradeSeconds
                        : item.NavalDef != null
                            ? Settings.NavalProductionSecondsPerItem
                            : Settings.ProductionSecondsPerItem;
                    float rate = item.Cost / Mathf.Max(1f, seconds);
                    float massWant = Mathf.Min(rate * dt, item.Cost - item.PaidMass);
                    // Налив двух валют: дефицит энергии режет массу пропорционально
                    // (EconomyService.Drain → EcoFlow.DrainStep, DESIGN §2.2);
                    // пропорция энергии — от ПОТРЕБНОСТИ позиции (апгрейд — профиль).
                    float energyFactor = item.Cost > 0f
                        ? item.EnergyCost / item.Cost
                        : EconomyService.EnergyCostFactor;
                    EcoFlow.Paid paid = EconomyService.Drain(hq, massWant, massWant * energyFactor);
                    item.PaidMass += paid.Mass;
                    item.PaidEnergy += paid.Energy;
                    // Энергостолл: гейты зелёные (canPay, деньги есть), налив срезан
                    // в ноль — режет общий энергосток фракции (SupCom-семантика).
                    // Флаг — плитке очереди (⚡), Notice с тротлом — игроку.
                    item.EnergyStalled = EconomyService.EnergyActive &&
                        massWant > 0.01f && paid.Mass < massWant * 0.25f;
                    if (item.EnergyStalled && hq != null &&
                        GameApi.TryGetLocalHQ(out FactionHQ localHq) && localHq == hq &&
                        Time.unscaledTime >= _nextStallNotice)
                    {
                        _nextStallNotice = Time.unscaledTime + 10f;
                        CommanderHud.Notice(Loc.Tr("Производство стоит: нет энергии — постройте генераторы"));
                        Log.Info("Production: энергостолл — доход " +
                            EconomyService.EnergyIncomePerSecond(hq).ToString("F1") + "/с");
                    }
                }

                if (item.PaidMass >= item.Cost - 0.01f &&
                    item.PaidEnergy >= item.EnergyCost - 0.01f && !state.Paused)
                {
                    if (item.IsUpgrade)
                    {
                        // Апгрейд оплачен: тир фабрики растёт (Notice — владельцу).
                        FactoryTierService.Complete(factory, item.ToTier, item.UpToHq);
                        Queue.RemoveAt(i);
                        Version++;
                        continue;
                    }
                    bool spawned = false;
                    Ship launched = null;
                    if (item.Depot != null)
                        spawned = item.Depot.TrySpawnVehicle(item.GroundDef);
                    else if (item.Dock != null && item.NavalDef != null)
                        spawned = NavalDock.TryLaunch(item.Dock, item.NavalDef, hq, out launched);
                    else if (item.Hangar != null && item.AirDef != null &&
                             item.Hangar.CanSpawnAircraft(item.AirDef))
                    {
                        // Лодаут позиции (запечён при постановке); к моменту спавна
                        // склад мог измениться — перегружаем нелегальный в «Стандарт».
                        Loadout lo = item.Loadout;
                        float fuel = item.Fuel;
                        if (lo != null &&
                            !LoadoutAllowed(item.AirDef, lo, hq))
                        {
                            lo = null;
                            fuel = 1f;
                        }
                        int livery = item.AirDef.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
                        GameObject prevSpawn = HangarSpawnedOf(item.Hangar);
                        var result = item.Hangar.TrySpawnAircraft(
                            null, item.AirDef, new LiveryKey(livery),
                            lo != null ? lo : new Loadout(), fuel);
                        spawned = result.Allowed;
                        if (spawned)
                            AfterAirSpawn(item.Hangar, prevSpawn, result.DelayedSpawn);
                    }

                    if (spawned)
                    {
                        Queue.RemoveAt(i);
                        Version++;
                        if (item.Depot != null)
                        {
                            EngineerService.OnVehicleSpawned(item.Depot, item.GroundDef); // HP-канон/тинт клонов тиров
                            DispatchByRally(item.Depot, item.GroundDef);
                        }
                        else if (launched != null)
                        {
                            EngineerService.OnShipSpawned(item.NavalDef, launched); // морские клоны LC: HP-канон/тинт/пропсы
                            DispatchShipByRally(item.Dock, launched);
                        }
                        OnQueueAdvanced(factory);
                        continue; // на этом месте уже следующая позиция
                    }
                    // занято (кулдаун депо / цикл дверей ангара / нет воды у верфи) — оплачено, повторим
                }
                i++;
            }
        }

        /// <summary>Ралли спущенного корабля: NavalDock вернул ссылку — сразу
        /// цепочка GroundOrders (искать по реестру, как у депо, не нужно).
        /// Точки несут groupId клика ралли — пачка кораблей одного маршрута
        /// рисуется/редактируется как групповой приказ (§12-30).</summary>
        private static void DispatchShipByRally(Building dock, Ship ship)
        {
            if (ship == null || !Rally.TryGetValue(dock, out RallyRoute route) ||
                route.Count == 0)
                return;
            if (route.Patrol)
            {
                GroundOrders.SetPatrol(ship, route.Points[0], route.Groups[0]);
                for (int i = 1; i < route.Count; i++)
                    GroundOrders.EnqueuePatrol(ship, route.Points[i], route.Groups[i]);
                return;
            }
            GroundOrders.Set(ship, route.Points[0], false, route.Groups[0]);
            for (int i = 1; i < route.Count; i++)
                GroundOrders.Enqueue(ship, route.Points[i], false, route.Groups[i]);
        }

        /// <summary>Захват вышедшего юнита депо (ссылку не отдаёт — ищем свежий
        /// GroundVehicle нужного определения в зоне фабрики, ещё не отправленный).
        /// Точки — с groupId клика ралли (групповой приказ пачки, §12-30).</summary>
        private static void DispatchByRally(VehicleDepot depot, VehicleDefinition def)
        {
            if (!Rally.TryGetValue(depot, out RallyRoute route) || route.Count == 0)
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
            if (route.Patrol)
            {
                GroundOrders.SetPatrol(spawned, route.Points[0], route.Groups[0]);
                for (int i = 1; i < route.Count; i++)
                    GroundOrders.EnqueuePatrol(spawned, route.Points[i], route.Groups[i]);
                return;
            }
            GroundOrders.Set(spawned, route.Points[0], false, route.Groups[0]);
            for (int i = 1; i < route.Count; i++)
                GroundOrders.Enqueue(spawned, route.Points[i], false, route.Groups[i]);
        }

        private static GameObject HangarSpawnedOf(Hangar hangar) =>
            hangar != null && HangarSpawnedField != null
                ? HangarSpawnedField.GetValue(hangar) as GameObject
                : null;

        /// <summary>Маршрут ралли ангара: свой; нет своего — ралли аэродрома
        /// (панель аэропорта задаёт общий маршрут всем своим ангарам).</summary>
        private static RallyRoute RouteOf(Hangar hangar)
        {
            if (Rally.TryGetValue(hangar, out RallyRoute route) && route.Count > 0)
                return route;
            Airbase airbase = hangar.parentAirbase;
            if (airbase != null && Rally.TryGetValue(airbase, out route) && route.Count > 0)
                return route;
            return null;
        }

        /// <summary>Ралли свежего борта ангара. Немедленный спавн — ссылка на борт уже
        /// сменилась в тот же кадр; отложенный (двери авианосца) — ждём смены ссылки
        /// в Tick (Prev запоминается ДО вызова спавна: spawnedObject держит прошый борт).</summary>
        private static void AfterAirSpawn(Hangar hangar, GameObject prev, bool delayed)
        {
            if (hangar == null)
                return;
            if (!delayed)
            {
                GameObject fresh = HangarSpawnedOf(hangar);
                if (fresh != null && !ReferenceEquals(fresh, prev))
                    DispatchAirByRally(fresh.GetComponent<Aircraft>(), hangar);
                return;
            }
            AirRallyWaits[hangar] = new AirRallyWait
            { Prev = prev, Deadline = Time.time + AirRallyWaitSec };
        }

        /// <summary>Отложенные спавны: spawnedObject ангара сменился — борт вышел,
        /// выдаём ему ралли. Мёртвый ангар/таймаут — ожидание снимается тихо.</summary>
        private static void PumpAirRallyWaits()
        {
            if (AirRallyWaits.Count == 0)
                return;
            _settledWaits.Clear();
            foreach (var kv in AirRallyWaits)
            {
                if (kv.Key == null || Time.time > kv.Value.Deadline) // Unity fake-null
                {
                    _settledWaits.Add(kv.Key);
                    continue;
                }
                GameObject fresh = HangarSpawnedOf(kv.Key);
                if (fresh == null || ReferenceEquals(fresh, kv.Value.Prev))
                    continue;
                _settledWaits.Add(kv.Key);
                DispatchAirByRally(fresh.GetComponent<Aircraft>(), kv.Key);
            }
            foreach (Hangar done in _settledWaits)
                AirRallyWaits.Remove(done);
        }

        /// <summary>Ралли борта: план Transit по точкам маршрута (AircraftOrders.Set
        /// сам выкатит стоящий борт — такси и взлёт, дальше маршрут). Точки — с
        /// groupId клика ралли: пачка бортов одного маршрута — групповые
        /// вейпоинты плана, как приказы напрямую (§12-30).</summary>
        private static void DispatchAirByRally(Aircraft aircraft, Hangar hangar)
        {
            RallyRoute route = RouteOf(hangar);
            if (aircraft == null || route == null)
                return;
            // Патрульное ралли (§12-30): Enqueue(Patrol) дописывает точки в ОДИН
            // патрульный сегмент (слияние SameFlightModes) — второй вечный цикл
            // не заводится (долг §12-32 не воспроизводится).
            AirOrderMode mode = route.Patrol ? AirOrderMode.Patrol : AirOrderMode.Transit;
            AircraftOrders.Set(aircraft, mode,
                route.Points[0].ToLocalPosition(), route.Groups[0]);
            for (int i = 1; i < route.Count; i++)
                AircraftOrders.Enqueue(aircraft, mode,
                    route.Points[i].ToLocalPosition(), route.Groups[i]);
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
                else if (def is AircraftDefinition ad && factory is Hangar hangar &&
                         CanBuild(hangar, ad))
                    Queue.Add(new QueueItem
                    {
                        Hangar = hangar,
                        AirDef = ad,
                        Cost = ad.value > 0f ? ad.value : Settings.BuildDefaultCost,
                    });
                else if (def is ShipDefinition sd && factory is Building dock &&
                         BuildTasks.IsDock(dock))
                    Queue.Add(new QueueItem
                    {
                        Dock = dock,
                        NavalDef = sd,
                        Cost = sd.value > 0f ? sd.value : Settings.BuildDefaultCost,
                    });
            }
            if (CountFor(factory) > 0)
                Version++;
        }
    }
}
