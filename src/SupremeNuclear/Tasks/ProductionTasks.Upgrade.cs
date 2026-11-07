using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть ProductionTasks: апгрейд тира фабрики, заказ корабля на верфь, недострой-фабрика.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; общее состояние и сводка API — ProductionTasks.cs.
    public static partial class ProductionTasks
    {
        // ---------- апгрейд тира фабрики как позиция очереди (v0.136, канон SupCom) ----------

        /// <summary>Заказать апгрейд тира — В ХВОСТ очереди, ПОРЯДОК КЛИКОВ
        /// (юзер-канон v0.138, скрипт SupCom: «T1-пачка → →T2 → T2-инженеры →
        /// →T3 → T3-пачка» программируется сразу). Юниты, заказанные ДО апгрейда,
        /// наливаются раньше него (у депо/верфи поток один и он линейный); юниты
        /// ПОСЛЕ — честно ждут своей очереди (тир-гейт оплаты в Tick). Цепочка
        /// стакается: цель — от «пендящего» тира (PendingTierOf).</summary>
        public static bool EnqueueUpgrade(object factory, bool hq = false)
        {
            if (!IsHost)
                return false;
            object key = FactoryTierService.KeyOfFactory(factory);
            if (key == null || key is UnityEngine.Object u && u == null)
                return false;
            if (key is Airbase ab && !AirbaseAlive(ab))
                return false;
            int from = PendingTierOf(key);
            // Ветка выбрана (тир ≥2 или апгрейд уже стоит) — следующие переходы
            // ТОЛЬКО её ветки (юзер-канон 2026-11-03): ШТАБ→ШТАБ, дешёвая→дешёвая.
            if (FactoryTierService.TierOf(key) >= 2 || UpgradeItemOf(key) != null)
                hq = FactoryTierService.BranchIsHq(key);
            // Дешёвая ветка требует живой HQ целевого тира СВОЕГО типа фабрики
            // (канон: «дешёвая — после наличия HQ нужной фабрики»); оборона
            // заказ-слоя поверх UI-гейта плитки.
            if (!hq && !FactoryTierService.CheapUpgradeAvailable(key, from + 1))
            {
                Log.Info("FactoryTier: дешёвый апгрейд отклонён — нет живого HQ T" +
                         (from + 1) + " нужного типа");
                return false;
            }
            if (from >= 3 ||
                !FactoryTierService.NextCost(from, hq, out float mass, out float energy, out float seconds))
                return false;
            Queue.Add(new QueueItem
            {
                IsUpgrade = true,
                ToTier = from + 1,
                UpToHq = hq,
                Cost = mass,
                EnergyNeed = energy,
                UpgradeSeconds = seconds,
                Depot = key as VehicleDepot,
                Hangar = key as Hangar,
                Dock = key as Building,
                Site = key as BuildTasks.Task,
            });
            Version++;
            Log.Info("FactoryTier: апгрейд до T" + (from + 1) + (hq ? " ШТАБ" : "") +
                     " в очередь фабрики (" + (key is Airbase ? "аэродром" : "фабрика") + ")");
            return true;
        }

        /// <summary>Стоящая в очереди позиция апгрейда этой фабрики (ангар
        /// резолвится в свой аэродром); null — апгрейда нет.</summary>
        public static QueueItem UpgradeItemOf(object factory)
        {
            object key = FactoryTierService.KeyOfFactory(factory);
            if (key == null)
                return null;
            foreach (QueueItem item in Queue)
                if (item.IsUpgrade && item.Factory == key)
                    return item;
            return null;
        }

        /// <summary>Ветка стоящего апгрейда — ШТАБ? (цепочка в очереди несёт
        /// ветку первого же апгрейда фабрики; пусто — false). UI показывает
        /// следующие плитки ТОЛЬКО этой ветки (юзер-канон 2026-11-03).</summary>
        public static bool PendingBranchHq(object factory)
        {
            object key = FactoryTierService.KeyOfFactory(factory);
            if (key == null)
                return false;
            foreach (QueueItem item in Queue)
                if (item.IsUpgrade && item.Factory == key)
                    return item.UpToHq;
            return false;
        }

        /// <summary>Эффективный тир фабрики с учётом цепочки в очереди:
        /// max(текущего, целей стоящих апгрейдов) — плитка каталога и AI
        /// предлагают СЛЕДУЮЩИЙ переход от него (T1→T2 в очереди → «T2→T3»).</summary>
        public static int PendingTierOf(object factory)
        {
            object key = FactoryTierService.KeyOfFactory(factory);
            int tier = FactoryTierService.TierOf(key);
            if (key == null)
                return tier;
            foreach (QueueItem item in Queue)
                if (item.IsUpgrade && item.Factory == key && item.ToTier > tier)
                    tier = item.ToTier;
            return tier;
        }

        /// <summary>Заказ корабля на ВЕРФЬ (v0.92): каталог — весь флот
        /// Encyclopedia.ships; время — NavalProductionSecondsPerItem (корабли
        /// тяжелее техники). Спуск — NavalDock (кольца воды у платформы);
        /// занятая вода не сжигает позицию — ждёт и повторяет.</summary>
        public static void EnqueueShip(Building dock, ShipDefinition def)
        {
            if (!IsHost || dock == null || def == null)
                return;
            if (UnitRoster.TierOf(def) > PendingTierOf(dock))
            {
                CommanderHud.Notice(Loc.F("Фабрика T{0} не строит T{1} — нужен апгрейд",
                    PendingTierOf(dock), UnitRoster.TierOf(def)));
                return;
            }
            Queue.Add(new QueueItem
            {
                Dock = dock,
                NavalDef = def,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            });
            Version++;
        }

        // Недострой-фабрика (v0.107): площадка производящего здания принимает
        // заказы как готовая (SupCom: «достраивается — уже работает»), но ни
        // оплаты, ни выпуска, пока стройка не завершена. Род позиции — по роду
        // площадки: верфь — флот, ангар — авиация (лодаут запекаем по ближайшему
        // своему аэродрому), депо — техника.

        // Каталог ангара-НЕДОСТРОЯ: нативный GetAvailableAircraft читает
        // attachedUnit — на префабе его нет; список availableAircraft сериализован
        // инспектором (проверка поля — SelfCheck), у построенного ангара он тот же.
        private static readonly System.Reflection.FieldInfo HangarAvailableField =
            HarmonyLib.AccessTools.Field(typeof(Hangar), "availableAircraft");
        private static readonly Dictionary<BuildingDefinition, AircraftDefinition[]> _siteAirCatalogs =
            new Dictionary<BuildingDefinition, AircraftDefinition[]>();

        /// <summary>Что будет строить ангар этого определения (данные префаба);
        /// нет данных о префабе/поле — пустой каталог.</summary>
        public static AircraftDefinition[] SiteAirCatalog(BuildingDefinition def)
        {
            if (def == null || def.unitPrefab == null || HangarAvailableField == null)
                return System.Array.Empty<AircraftDefinition>();
            if (_siteAirCatalogs.TryGetValue(def, out AircraftDefinition[] cached))
                return cached;
            Hangar prefabHangar = def.unitPrefab.GetComponent<Hangar>();
            AircraftDefinition[] list = prefabHangar != null
                ? HangarAvailableField.GetValue(prefabHangar) as AircraftDefinition[]
                : null;
            _siteAirCatalogs[def] = list ?? System.Array.Empty<AircraftDefinition>();
            return _siteAirCatalogs[def];
        }

        /// <summary>Заказ в очередь НЕДОСТРОЯ (своей площадки): тип позиции — по
        /// роду площадки. Оплата/выпуск стоят до завершения стройки; по готовности
        /// позиции переезжают на живую фабрику (AdoptSite). false — не наш род
        /// позиции/площадки или чужая площадка.</summary>
        public static bool EnqueueSite(BuildTasks.Task site, UnitDefinition def)
        {
            if (!IsHost || def == null || !BuildTasks.IsActive(site))
                return false;
            // Тир площадки — «пендящий» (апгрейд в её очереди тоже считается).
            if (UnitRoster.TierOf(def) > PendingTierOf(site))
                return false;
            // Только СВОЯ площадка: чужой недострой заказами не кормим (выделить
            // можно любой призрак, оплата бы шла деньгами владельца).
            if (!GameApi.TryGetLocalHQ(out FactionHQ local) || site.Faction != local)
                return false;
            var item = new QueueItem
            {
                Site = site,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            };
            if (site.IsDock && def is ShipDefinition naval)
                item.NavalDef = naval;
            else if (BuildTasks.IsHangarDefinition(site.Definition) && def is AircraftDefinition air)
            {
                item.AirDef = air;
                BakeLoadout(item, BuildTasks.NearestOwnAirbase(
                    site.Site.ToLocalPosition(), site.Faction, Settings.HangarAttachRadiusM));
            }
            else if (BuildTasks.IsProducingDefinition(site.Definition) &&
                     !BuildTasks.IsHangarDefinition(site.Definition) &&
                     def is VehicleDefinition ground)
                item.GroundDef = ground;
            else
                return false;
            Queue.Add(item);
            Version++;
            return true;
        }

        /// <summary>Площадка достроена: очередь недостроя, его Пауза/Цикл и ралли
        /// переезжают на живую фабрику — она сразу начинает наливать головную
        /// позицию. Спавн не удался (factory == null) или тип не фабрика —
        /// накопленное умирает тихо: уплаченного за позиции нет, оплата не
        /// начиналась. Род фабрики — по роду ПЛОЩАДКИ: корпус верфи — тот же
        /// компонент VehicleDepot, что и депо, каст по объекту их бы перепутал.</summary>
        public static void AdoptSite(BuildTasks.Task site, object factory)
        {
            if (site == null)
                return;
            VehicleDepot depot = null;
            Hangar hangar = null;
            Building dock = null;
            if (site.IsDock)
                dock = factory as Building;
            else if (BuildTasks.IsHangarDefinition(site.Definition))
                hangar = factory as Hangar;
            else
                depot = factory as VehicleDepot;
            object key = depot != null ? (object)depot
                : hangar != null ? (object)hangar
                : dock != null ? (object)dock : null;
            bool touched = false;
            for (int i = Queue.Count - 1; i >= 0; i--)
            {
                QueueItem item = Queue[i];
                if (item.Site != site)
                    continue;
                item.Site = null;
                touched = true;
                if (item.IsUpgrade)
                {
                    // Апгрейд площадки переезжает ЦЕЛИКОМ (тип позиции не важен);
                    // ангар-площадка апгрейдит СВОЙ ангар — фабрики разделены,
                    // склейки с аэродромом больше нет (юзер-канон 2026-11-03).
                    item.Depot = depot;
                    item.Hangar = hangar;
                    item.Dock = dock;
                    item.Base = null;
                    continue;
                }
                if (depot != null && item.GroundDef != null)
                    item.Depot = depot;
                else if (hangar != null && item.AirDef != null && CanBuild(hangar, item.AirDef))
                    item.Hangar = hangar;   // реальный ангар строит тип — позиция жива
                else if (dock != null && item.NavalDef != null)
                    item.Dock = dock;
                else
                {
                    Queue.RemoveAt(i);      // тип позиции фабрике не подходит
                }
            }
            if (Factories.TryGetValue(site, out FactoryState state))
            {
                Factories.Remove(site);
                if (key != null && !Factories.ContainsKey(key))
                    Factories[key] = state; // Пауза/Цикл продолжают действовать
                touched = true;
            }
            if (Rally.TryGetValue(site, out RallyRoute route))
            {
                Rally.Remove(site);
                if (key != null && route.Count > 0 && !Rally.ContainsKey(key))
                    Rally[key] = route;     // ралли достанется выпущенным войскам
                touched = true;
            }
            FactoryTierService.Adopt(site, factory); // тир/апгрейд площадки → фабрике
            if (touched)
                Version++;
        }

        /// <summary>Состояние мёртвой площадки (снос/фракция погибла/спавн не
        /// удался): ралли и Пауза/Цикл умирают вместе с ней (очередь чистит Tick).</summary>
        private static void DropSiteState(BuildTasks.Task site)
        {
            bool touched = Rally.Remove(site);
            if (Factories.Remove(site))
                touched = true;
            FactoryTierService.Drop(site); // тир/апгрейд площадки умирает с ней
            if (touched)
                Version++;
        }
    }
}
