using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


namespace SupremeNuclear
{
    // Часть SupcomUi: вкладки: RefreshTabs, очереди, шаблоны, каталог производства/апгрейдов.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; канвас собирает SupcomUi.cs (BuildSafe).
    public sealed partial class SupcomUi
    {
        private void RefreshTabs()
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            var selected = controller.Selection.Selected;
            Airbase airbase = controller.Selection.SelectedAirbase;

            VehicleDepot depot = null;
            Building navalDock = null;
            Unit extractor = null;
            bool hasEngineer = false;
            Airbase carrier = null; // корабль-аэродром (авианосец/LHA): Airbase —
            // компонент корпуса (Ship.GetAirbase), полосы/точки едут вместе с ним
            var hangars = new List<Hangar>();
            // Все фабрики выделения (депо/верфь/ангар) — групповые Пауза/Цикл и хэш
            // очередей; представители выше — ПЕРВЫЕ своего типа (детерминизм).
            _ctxFactories.Clear();
            int selHash = 17;
            int engTier = 1; // ЛУЧШИЙ инженер выделения: тир-потолок каталога строек
            foreach (Unit unit in selected)
            {
                selHash = selHash * 31 + (unit != null ? unit.GetInstanceID() : 0);
                if (BuildTasks.IsDock(unit))
                {
                    if (navalDock == null) // Unity fake-null: мёртвую верфь заменяет живая
                        navalDock = unit as Building; // корпус-депо верфи — ФЛОТ
                    _ctxFactories.Add(unit);
                }
                else if (unit is VehicleDepot d)
                {
                    if (depot == null)
                        depot = d;
                    _ctxFactories.Add(d);
                }
                else if (unit is Building b && b.GetComponent<Hangar>() != null)
                {
                    hangars.Add(b.GetComponent<Hangar>());
                    _ctxFactories.Add(b.GetComponent<Hangar>());
                }
                else if (EconomyService.IsExtractor(unit) && extractor == null)
                    extractor = unit;
                else if (GameApi.IsEngineer(unit))
                {
                    hasEngineer = true;
                    int t = EngineerService.TierOf(unit != null ? unit.definition : null);
                    if (t > engTier)
                        engTier = t;
                }
                else if (carrier == null && unit is Ship)
                {
                    Airbase shipBase = unit.GetAirbase(); // не-авианосцу вернёт null
                    if (shipBase != null)
                        carrier = shipBase;
                }
            }
            // АВИАНОСЕЦ-АЭРОДРОМ — контекст производства (v0.108): панель корабля =
            // панель его Airbase (палубная авиация: каталог юниона корабельных
            // ангаров, очередь-агрегат, резерв РЕЗ, лодауты) — как у аэродрома,
            // пока в выделении нет другой фабрики/инженера (тогда она приоритетнее).
            // Второй путь и раньше открывал ромб аэродрома над кораблём
            // (SelectedAirbase) — но выделение самого корпуса заказывало ничего.
            if (carrier != null && airbase == null && depot == null && navalDock == null &&
                hangars.Count == 0 && !hasEngineer)
                airbase = carrier;

            if (airbase != null)
                selHash = selHash * 31 + airbase.GetInstanceID();

            // Выделенный недострой открывает свою карточку на «Производстве».
            if (BuildTasks.SelectedTask != null && selected.Count == 0 && depot == null &&
                navalDock == null && airbase == null && hangars.Count == 0)
                _activeTab = TabProduction;

            // Недострой-ФАБРИКА (депо/верфь/ангар) в контексте — работает как
            // готовая (v0.107): каталог и очередь (заказы копятся, выпуск — по
            // завершении стройки), групповые Пауза/Цикл; по готовности очередь
            // переезжает на здание (ProductionTasks.AdoptSite).
            BuildTasks.Task siteFactory = BuildTasks.IsFactorySite(BuildTasks.SelectedTask) &&
                selected.Count == 0 && depot == null && navalDock == null &&
                airbase == null && hangars.Count == 0
                    ? BuildTasks.SelectedTask : null;
            _ctxSite = siteFactory;
            if (siteFactory != null)
            {
                selHash = selHash * 31 + siteFactory.Id.GetHashCode();
                _ctxFactories.Add(siteFactory);
            }

            // Тир контекста (§2.3а): инженер → ЛУЧШИЙ тир выделения (потолок
            // каталога строек); фабрики → МИНИМАЛЬНЫЙ «пендящий» тир выделения
            // (с учётом апгрейдов в их очередях — очередь программируется
            // наперёд: за →T2 сразу видны T2-юниты); контекст аэродрома —
            // минимум по его ангарам (фабрики разделены). Нет контекста — 3.
            // ФАКТИЧЕСКИЙ тир — рядом
            // (суффикс панели «T1→T2», пока апгрейд наливается: мгновенный
            // скачок заголовка читался как «прокачалось сразу», плейтест 2026-11-08).
            _ctxTier = 3;
            _ctxTierActual = 3;
            if (hasEngineer)
            {
                _ctxTier = engTier;
                _ctxTierActual = engTier;
            }
            else
            {
                foreach (object f in _ctxFactories)
                {
                    _ctxTier = Mathf.Min(_ctxTier, ProductionTasks.PendingTierOf(f));
                    _ctxTierActual = Mathf.Min(_ctxTierActual, FactoryTierService.TierOf(f));
                }
                // Контекст аэродрома (вышка/авианосец): фабрики РАЗДЕЛЕНЫ — тир
                // контекста = минимум по ЕГО ангарам (аэродром больше не фабрика).
                if (airbase != null && airbase.hangars != null)
                    foreach (Hangar hangar in airbase.hangars)
                        if (hangar != null)
                        {
                            _ctxTier = Mathf.Min(_ctxTier,
                                ProductionTasks.PendingTierOf(hangar));
                            _ctxTierActual = Mathf.Min(_ctxTierActual,
                                FactoryTierService.TierOf(hangar));
                        }
            }
            if (_prodTier > _ctxTier)
                _prodTier = 0; // фильтр-просмотр выше нового контекста — в «Все»

            // Автопереключение вкладки под выделение; ручной клик игрока уважаем,
            // пока выделение не сменилось (_manualTabForSel).
            if (_manualTabForSel != selHash)
            {
                if (depot != null || navalDock != null || airbase != null ||
                    hangars.Count > 0 || hasEngineer || carrier != null)
                    _activeTab = TabProduction;
                else if (extractor != null && EconomyService.Running)
                    _activeTab = TabUpgrade;
                else if (selected.Count > 0 && _activeTab != TabRoster)
                    _activeTab = TabRoster;
            }

            for (int i = 0; i < _tabButtons.Length; i++)
                if (_tabButtons[i].img != null)
                    _tabButtons[i].img.color = i == _activeTab ? BtnActive : BtnBg;
            if (_tierRoot != null)
                _tierRoot.gameObject.SetActive(_activeTab == TabProduction);
            for (int i = 0; i < _tierButtons.Length; i++)
                if (_tierButtons[i].img != null)
                {
                    bool locked = i >= 1 && i > _ctxTier; // выше тира фабрики/инженера
                    _tierButtons[i].img.color = i == _prodTier ? BtnActive
                        : locked ? new Color(0.32f, 0.32f, 0.34f, 0.65f) : BtnBg;
                }

            // Деньги НЕ в ключе пересборки (§12-45а, v0.112): в каталоге «Производства»
            // нет фонд-зависимого контента (цены статичны, потоковая модель льёт что есть),
            // а квант в сотни заставлял ClearChildren+пересоздание всей ленты (плитки с
            // замыканиями) на каждом тике дохода — регулярный GC-чурн.
            // «Апгрейд» пересобирается по кванту прогресса налива (5%).
            int hash = 17 * 31 + _activeTab;
            hash = hash * 31 + selHash;
            if (_activeTab == TabProduction)
            {
                hash = hash * 31 + _prodTier * 7 + (depot != null ? 1 : 0) +
                       (airbase != null ? 2 : 0) + (hasEngineer ? 4 : 0) +
                       (navalDock != null ? 8 : 0) + (carrier != null ? 16 : 0) +
                       (siteFactory != null ? 32 : 0);
                // Тиры: завершение апгрейда фабрики/смена контекста — пересборка
                hash = hash * 31 + FactoryTierService.Version * 7 + _ctxTier;
                if (airbase != null)
                    hash = hash * 31 + AirbaseReserve.Version; // плитки резерва ангара
                // смена лодаута колесом — перестройка каталога (суффикс «·ЛN» на плитке)
                hash = hash * 31 + ProductionTasks.LoadoutsVersion;
                // прогресс недостроя-карточки (заголовок «… · 45%»/«выпуск по готовности»)
                if (siteFactory != null)
                    hash = hash * 31 + (int)(siteFactory.Progress01 * 20f);
            }
            else if (_activeTab == TabUpgrade)
            {
                hash = hash * 31 + (extractor != null ? EconomyService.TierOf(extractor) : 0);
                hash = hash * 31 + (EconomyService.Running ? 1 : 0);
                hash = hash * 31 + (extractor != null && EconomyService.UpgradeInProgress(extractor) ? 1 : 0);
                hash = hash * 31 + (int)(extractor != null
                    ? EconomyService.UpgradeProgress01(extractor) * 20f : 0f);
            }
            // Нижняя строка видима ВСЕГДА (без прыжков высоты): пустая — просто пустая лента.
            // Высота панели — компакт v0.64.0 (12 отступ + 134 контент = 146).
            if (_tabPanel != null)
            {
                var prt = (RectTransform)_tabPanel.transform;
                prt.offsetMax = new Vector2(-12f, 146f);
            }
            if (_strip2Nav != null)
                _strip2Nav.SetActive(true);
            _strip2.gameObject.SetActive(true);
            int queueKey = 17 * 31 + BuildTasks.Version + GroundOrders.Version +
                          AircraftOrders.Version;
            if (hasEngineer)
                foreach (BuildTasks.Task t in BuildTasks.All)
                {
                    if (t.Faction != hq)
                        continue; // чужие стройки не в ключе: прогресс AI не дёргает строку
                    queueKey = queueKey * 31 + (int)(t.Progress01 * 20f) + (t.ZoneBlocked ? 3 : 0);
                }
            // Очереди ВСЕХ фабрик выделения: мультивыделение обновляет строку при
            // изменении очереди любой из них (раньше хэшировалась одна — последняя).
            foreach (object factory in _ctxFactories)
                queueKey = queueKey * 31 + ProductionTasks.KeyFor(factory) + 13;
            // Очередь аэродрома (вышка/ромб/АВИАНОСЦА) — АГРЕГАТ его ангаров:
            // заказы живут пер-ангарно, KeyFor(airbase) их не видел — строка
            // вышки стояла пустой и не обновлялась. Ключуем каждый ангар базы.
            if (airbase != null && airbase.hangars != null)
            {
                foreach (Hangar hangar in airbase.hangars)
                    if (hangar != null)
                        queueKey = queueKey * 31 + ProductionTasks.KeyFor(hangar) + 13;
                // Позиция апгрейда тира базы (налив — на ключе аэродрома, v0.136)
                queueKey = queueKey * 31 + ProductionTasks.KeyFor(airbase) + 7;
            }
            // Выделенные ангары: их строка показывает и апгрейд тира базы
            if (hangars.Count > 0 && hangars[0] != null && hangars[0].parentAirbase != null)
                queueKey = queueKey * 31 + ProductionTasks.KeyFor(hangars[0].parentAirbase) + 7;
            bool queueShown = _activeTab == TabProduction || _activeTab == TabTemplates;
            if (queueShown && queueKey != _queueHash)
            {
                _queueHash = queueKey;
                if (hasEngineer)
                    FillBuildQueue(controller);
                else if (hangars.Count == 1)
                    FillFactoryQueue(hangars[0]);
                else if (hangars.Count > 1)
                    FillHangarsQueue(hangars);
                else if (airbase != null)
                    FillHangarsQueue(airbase.hangars, airbase); // агрегат ангаров базы
                else
                    FillFactoryQueue(navalDock != null ? (object)navalDock
                        : depot != null ? (object)depot
                        : siteFactory != null ? (object)siteFactory : null);
            }
            if (!queueShown && _queueHash != 0)
            {
                _queueHash = 0;
                ClearChildren(_strip2Content);
            }

            // Контекстные кнопки: на «Производстве» у фабрик — Пауза/Цикл/Шаблон+,
            // на «Ш» — только Шаблон+ (сохранить набор). Пауза/Цикл — ГРУППОВЫЕ:
            // действуют на ВСЕ фабрики выделения (мульти-ангар/депо/верфь, канон
            // SupCom «фабрики управляемы пачкой»); подсветка — «все в состоянии».
            _ctxDepot = depot;
            _ctxDock = navalDock;
            _ctxAirbase = airbase;
            // У построенной ВПП нет вышки — тумблеры посадки/взлёта доступны и с
            // ангара: его parentAirbase (все ангары выделения с ОДНОЙ базы;
            // смешанное выделение — не показываем, база неоднозначна).
            if (_ctxAirbase == null && hangars.Count > 0)
            {
                Airbase parent = hangars[0].parentAirbase;
                bool same = parent != null;
                for (int h = 1; h < hangars.Count && same; h++)
                    same = hangars[h].parentAirbase == parent;
                if (same)
                    _ctxAirbase = parent;
            }
            // Авианосец: у палубы нет ни вышки, ни ангаров-зданий — тумблеры даём
            // с выделения самого корабля (ромб летит вместе с ним — и тот путь есть).
            if (_ctxAirbase == null && carrier != null)
                _ctxAirbase = carrier;
            _ctxHangar = hangars.Count > 0 ? hangars[0] : null;
            Transform ctx = _tabPanel != null ? _tabPanel.Find("factoryCtx") : null;
            if (ctx != null)
            {
                bool ctxOnFactory = _activeTab == TabProduction && _ctxFactories.Count > 0;
                bool ctxOnAirbase = _activeTab == TabProduction && _ctxAirbase != null;
                // Инженеры выделения — тоже контекст «Пауза» (v0.151): кнопка одна,
                // действует на фабрики И инженеров; Цикл — только фабрикам.
                bool ctxOnEngineers = _activeTab == TabProduction && hasEngineer;
                ctx.gameObject.SetActive(ctxOnFactory || ctxOnAirbase ||
                                         ctxOnEngineers || _activeTab == TabTemplates);
                // Пауза/Цикл — и контексту БАЗЫ (вышка/ромб/авианосец): группа —
                // все ангарные фабрики базы (PauseCycleTargets), v0.108.
                if (_pauseBtn != null)
                    _pauseBtn.gameObject.SetActive(ctxOnFactory || ctxOnAirbase || ctxOnEngineers);
                if (_cycleBtn != null)
                    _cycleBtn.gameObject.SetActive(ctxOnFactory || ctxOnAirbase);
                if (_tplBtn != null)
                    _tplBtn.gameObject.SetActive(ctxOnFactory || _activeTab == TabTemplates);
                Image pi = _pauseBtn != null ? _pauseBtn.GetComponent<Image>() : null;
                if (pi != null)
                    pi.color = AllPausedUnified() ? BtnActive : BtnBg;
                Image ci = _cycleBtn != null ? _cycleBtn.GetComponent<Image>() : null;
                if (ci != null)
                    ci.color = ProductionTasks.AllCycling(PauseCycleTargets()) ? BtnActive : BtnBg;
                // Тумблеры аэродрома: видны при выделенном аэродроме, подсветка = закрыто.
                if (_noLandBtn != null)
                {
                    _noLandBtn.gameObject.SetActive(ctxOnAirbase);
                    Image nli = _noLandBtn.GetComponent<Image>();
                    if (nli != null)
                        nli.color = _ctxAirbase != null && !AirbaseAccess.IsLandingOpen(_ctxAirbase)
                            ? BtnActive : BtnBg;
                }
                if (_noTakeoffBtn != null)
                {
                    _noTakeoffBtn.gameObject.SetActive(ctxOnAirbase);
                    Image nti = _noTakeoffBtn.GetComponent<Image>();
                    if (nti != null)
                        nti.color = _ctxAirbase != null && !AirbaseAccess.IsTakeoffOpen(_ctxAirbase)
                            ? BtnActive : BtnBg;
                }
            }

            if (hash == _contextHash)
                return;
            _contextHash = hash;
            ClearChildren(_strip1Content);
            _strip1Content.anchoredPosition = Vector2.zero;
            _strip2Content.anchoredPosition = Vector2.zero;

            switch (_activeTab)
            {
                case TabProduction:
                    if (BuildTasks.SelectedTask != null && selected.Count == 0 &&
                        depot == null && navalDock == null && airbase == null &&
                        hangars.Count == 0)
                        FillUnfinishedTab(BuildTasks.SelectedTask);
                    else
                        FillProductionTab(hq, depot, navalDock, airbase, hasEngineer, hangars);
                    break;
                case TabTemplates:
                    FillTemplatesTab(controller, depot, airbase, hasEngineer, selected);
                    break;
                case TabUpgrade:
                    FillUpgradeTab(hq, extractor, EconomyService.Running);
                    break;
                default:
                    FillRosterTab(controller, selected);
                    break;
            }
        }

        /// <summary>Вкладка «Производство»: инженер → каталог зданий (стройка где угодно),
        /// иначе каталог депо (по типам) или аэродрома.
        /// Группы — боковыми заголовками: слева колонка заголовка, справа поток позиций.</summary>
        /// <summary>Вкладка «Производство» (DESIGN 2.10): строка заказа — ЛЕНТА позиций.
        /// Инженер в выделении → здания (стройка где угодно), иначе депо/аэродром.
        /// Нижняя лента — задел под очередь фабрики (этап B).</summary>
        /// <summary>Нижняя строка «Производства» (инженер): очередь активных строек —
        /// плитка «иконка здания + прогресс», клик — камера к площадке. Только СВОИ
        /// задачи фракции: чужие (AI) площадки в ленте выглядели «чужой очередью
        /// моего инженера» (плейтест 2026-10-30).</summary>
        private void FillBuildQueue(CommanderModeController controller)
        {
            ClearChildren(_strip2Content);
            if (!GameApi.TryGetLocalHQ(out FactionHQ localHq))
                return;
            // Выделенные инженеры → лента показывает ИХ очереди (задачи, где кто-то
            // из выделенных — строитель). Без выделения инженеров — все задачи
            // фракции. Раньше лента была всегда фракционной: чужая/осиротевшая
            // задача висела как «здание, которое он не строит» (юзер, 2026-10-30).
            var selEngineers = new List<Unit>();
            foreach (Unit u in controller.Selection.Selected)
                if (u != null && GameApi.IsEngineer(u))
                    selEngineers.Add(u);
            // Стак: подряд идущие задания одного здания — одна плитка с числом.
            BuildTasks.Task runDef = null;   // образец повтора
            int runCount = 0;
            bool first = true;
            foreach (BuildTasks.Task t in BuildTasks.All)
            {
                if (t.Faction != localHq)
                    continue;
                if (selEngineers.Count > 0 && !HasAnyBuilder(t, selEngineers))
                    continue;
                if (t.Definition == runDef?.Definition)
                {
                    runCount++;
                    continue;
                }
                if (runDef != null)
                {
                    AddQueueTile(_strip2Content, runDef, first, runCount, controller);
                    first = false;
                }
                runDef = t;
                runCount = 1;
            }
            if (runDef != null)
                AddQueueTile(_strip2Content, runDef, first, runCount, controller);
        }

        private static bool HasAnyBuilder(BuildTasks.Task t, List<Unit> units)
        {
            foreach (Unit b in t.Builders)
                foreach (Unit u in units)
                    if (b == u)
                        return true;
            return false;
        }

        /// <summary>Плитка очереди стройки: иконка + подпись + бейдж ×N, у головной —
        /// прогресс-бар (зона не расчищена — полоса красная); клик — камера к площадке.</summary>
        private void AddQueueTile(Transform content, BuildTasks.Task t, bool head, int count,
            CommanderModeController controller)
        {
            BuildTasks.Task task = t;
            Tile(content, new TileSpec
            {
                Icon = IconOf(t.Definition),
                Caption = t.IsRunway || t.IsDock
                    ? t.Label // спец-площадки подписаны собой («ВПП»/«Верфь»)
                    : t.Definition != null
                        ? Catalog.ShortLabel(t.Definition.code, t.Definition.unitName) : null,
                Badge = count > 1 ? "×" + count : null,
                Progress = head ? t.Progress01 : (float?)null,
                BarColor = head && t.ZoneBlocked ? new Color(1f, 0.45f, 0.3f, 0.95f) : (Color?)null,
                OnLeft = () =>
                {
                    Vector3 site = task.Site.ToLocalPosition();
                    controller.CameraState?.CenterOn(site);
                },
                Def = t.Definition,
            });
        }

        /// <summary>Очередь ГРУППЫ ангаров (агрегат): по типу — сумма позиций,
        /// полоса = максимальный прогресс среди наливаемых (показывает активность),
        /// бейдж ×N — всего позиций. ЛКМ — +1 каждому умеющему (Shift ×5),
        /// ПКМ — −1 с конца одного ангара группы (Shift ×5).
        /// distributed != null — режим ПАНЕЛИ АЭРОДРОМА (вышка/авианосец): клики
        /// как у плитки каталога базы — ЛКМ «в самый свободный» ангар
        /// (EnqueueAirDistributed, Shift ×5), ПКМ — снять с самого длинного.</summary>
        private void FillHangarsQueue(List<Hangar> hangars, Airbase distributed = null)
        {
            ClearChildren(_strip2Content);
            // Апгрейд тира базы (если заказан) — головой агрегата: полоса налива базы.
            EmitUpgradeQueueTile(distributed != null ? (object)distributed : hangars.Count > 0 ? hangars[0] : null);
            var order = new List<UnitDefinition>();
            var totals = new Dictionary<UnitDefinition, int>();
            var activeCounts = new Dictionary<UnitDefinition, int>();
            var topProgress = new Dictionary<UnitDefinition, float>();
            var stalled = new Dictionary<UnitDefinition, bool>();
            foreach (ProductionTasks.QueueItem item in ProductionTasks.All)
            {
                if (item.UnitDef == null)
                    continue;
                bool ours = false;
                foreach (Hangar hangar in hangars)
                    if (item.Factory == (object)hangar)
                    {
                        ours = true;
                        break;
                    }
                if (!ours)
                    continue;
                if (!totals.ContainsKey(item.UnitDef))
                {
                    totals[item.UnitDef] = 0;
                    order.Add(item.UnitDef);
                }
                totals[item.UnitDef]++;
                if (item.EnergyStalled)
                    stalled[item.UnitDef] = true;
                if (item.PaidMass > 0f)
                {
                    activeCounts[item.UnitDef] = activeCounts.TryGetValue(item.UnitDef, out int n)
                        ? n + 1 : 1;
                    topProgress[item.UnitDef] = Mathf.Max(
                        topProgress.TryGetValue(item.UnitDef, out float p) ? p : 0f,
                        item.Progress01);
                }
            }
            foreach (UnitDefinition def in order)
            {
                UnitDefinition d = def;
                int act = activeCounts.TryGetValue(def, out int n) ? n : 0;
                Tile(_strip2Content, new TileSpec
                {
                    Icon = IconOf(d),
                    Caption = Catalog.ShortLabel(d.code, d.unitName) +
                        (stalled.TryGetValue(def, out bool st) && st ? " ⚡" : ""),
                    TypeBadge = TypeBadgeOf(d),
                    Badge = "×" + totals[def] + (act > 0 ? " ▲" + act : ""),
                    Progress = act > 0 && topProgress.TryGetValue(def, out float p) ? p : (float?)null,
                    OnLeft = () =>
                    {
                        if (!(d is AircraftDefinition ad))
                            return;
                        if (distributed != null)
                        {
                            // панель аэродрома: как плитка каталога — «в самый свободный»
                            for (int k = 0; k < (RtsInput.Shift ? 5 : 1); k++)
                                if (!ProductionTasks.EnqueueAirDistributed(distributed, ad))
                                    break; // Notice уже показан
                        }
                        else
                            foreach (Hangar hangar in hangars)
                                if (ProductionTasks.CanBuild(hangar, ad))
                                    ProductionTasks.EnqueueAirSilent(hangar, ad);
                    },
                    OnRight = () =>
                    {
                        if (distributed != null)
                        {
                            // панель аэродрома: снимаем с самого ДЛИННОГО ангара —
                            // зеркало «в самый свободный» при постановке
                            ProductionTasks.RemoveTailAir(distributed, d, RtsInput.Shift ? 5 : 1);
                            return;
                        }
                        for (int i = hangars.Count - 1; i >= 0; i--)
                            if (ProductionTasks.CountFor(hangars[i]) > 0)
                            {
                                ProductionTasks.RemoveTail(hangars[i], d, RtsInput.Shift ? 5 : 1);
                                break;
                            }
                    },
                    Def = d,
                });
            }
        }

        /// <summary>Нижняя строка «Производства» (фабрика): очередь заказов — плитки
        /// «иконка + подпись + ×N», у головной позиции — прогресс; клики по стаку:
        /// ЛКМ — добавить 1 (Shift — 5), ПКМ — снять 1 с конца (Shift — 5, с возвратом денег).</summary>
        private void FillFactoryQueue(object factory)
        {
            ClearChildren(_strip2Content);
            if (factory == null)
                return;
            // Зонд «общая очередь апгрейда» (плейтест 2026-11-03): что реально
            // смэтчилось фабрике при одиночном выделении — по этим строкам виден
            // чужой ключ, если фильтр где-то пропускает.
            {
                int mine = 0, upg = 0;
                foreach (ProductionTasks.QueueItem q in ProductionTasks.All)
                    if (q.Factory == factory)
                    {
                        mine++;
                        if (q.IsUpgrade) upg++;
                    }
                Log.Info("QueueUI: " + factory.GetType().Name + "#" + factory.GetHashCode() +
                         " — позиций " + mine + " (апгрейдов " + upg + ")");
            }
            // Стак подряд идущих одинаковых позиций. НАЛИВАЕМЫЕ позиции (Paid > 0)
            // при параллельной оплате видны КАЖДАЯ со своей полосой (SupCom-очередь),
            // ждущий хвост — одной плиткой ×N. Раньше полоса была только у головы
            // первого стака — параллельная сборка была невидима (плейтест v0.87).
            var runDef = default(UnitDefinition);
            var items = new List<ProductionTasks.QueueItem>();
            void EmitRun()
            {
                int waiting = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].PaidMass <= 0f)
                    {
                        waiting = items.Count - i;
                        break;
                    }
                    ProductionTasks.QueueItem active = items[i];
                    Tile(_strip2Content, new TileSpec
                    {
                        Icon = IconOf(runDef),
                        Caption = Catalog.ShortLabel(runDef.code, runDef.unitName) +
                            (active.EnergyStalled ? " ⚡" : ""),
                        TypeBadge = TypeBadgeOf(runDef),
                        TierBadge = "T" + UnitRoster.TierOf(runDef),
                        Progress = active.Progress01,
                        BarColor = active.EnergyStalled
                            ? new Color(1f, 0.75f, 0.2f, 0.95f) : (Color?)null,
                        OnLeft = () => ProductionTasks.InsertAfter(active, RtsInput.Shift ? 5 : 1),
                        OnRight = () => ProductionTasks.Cancel(active),
                        Def = runDef,
                    });
                }
                if (waiting > 0)
                {
                    UnitDefinition def = runDef;
                    ProductionTasks.QueueItem tail = items[items.Count - 1];
                    Tile(_strip2Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        TierBadge = "T" + UnitRoster.TierOf(def),
                        Badge = waiting > 1 ? "×" + waiting : null,
                        OnLeft = () => ProductionTasks.InsertAfter(tail, RtsInput.Shift ? 5 : 1),
                        OnRight = () => ProductionTasks.RemoveTail(factory, def, RtsInput.Shift ? 5 : 1),
                        Def = def,
                    });
                }
            }
            foreach (ProductionTasks.QueueItem item in ProductionTasks.All)
            {
                if (item.Factory != factory)
                    continue;
                if (item.IsUpgrade)
                {
                    // Плитка апгрейда — НА СВОЁМ МЕСТЕ очереди (скрипт SupCom:
                    // юниты → апгрейд → юниты постарше), не отдельной головой.
                    EmitRun();
                    items.Clear();
                    ProductionTasks.QueueItem up = item;
                    Tile(_strip2Content, new TileSpec
                    {
                        Caption = "T" + (up.ToTier - 1) + "→T" + up.ToTier +
                            (up.EnergyStalled ? " ⚡" : ""),
                        TypeBadge = Loc.Tr("АПГРЕЙД"),
                        Progress = up.Progress01,
                        BarColor = up.EnergyStalled
                            ? new Color(1f, 0.75f, 0.2f, 0.95f) : (Color?)null,
                        OnRight = () => ProductionTasks.Cancel(up),
                    });
                    continue;
                }
                if (item.UnitDef == null)
                    continue;
                if (items.Count > 0 && item.UnitDef != runDef)
                {
                    EmitRun();
                    items.Clear();
                }
                if (items.Count == 0)
                    runDef = item.UnitDef;
                items.Add(item);
            }
            if (items.Count > 0)
                EmitRun();
        }

        /// <summary>Карточка недостроя (выделен кликом по площадке): прогресс и
        /// действия; недострой-ФАБРИКА (депо/верфь/ангар, v0.107) — ПЛЮС каталог
        /// своего рода и очередь как у готовой: заказы копятся, оплата и выпуск
        /// начнутся по завершении стройки (нижняя строка — RefreshTabs).</summary>
        private void FillUnfinishedTab(BuildTasks.Task t)
        {
            bool factorySite = BuildTasks.IsFactorySite(t);
            _contextTitle.text = factorySite
                ? Loc.F("Недострой: {0} · {1} · выпуск по готовности",
                    t.Label, t.Progress01.ToString("P0"))
                : Loc.F("Недострой: {0} · {1}",
                    t.Definition != null ? t.Definition.unitName : "?", t.Progress01.ToString("P0"));
            BuildTasks.Task task = t;
            AddItemButton(_strip1Content,
                IconOf(t.Definition),
                Loc.Tr("Снести площадку (без возврата)"),
                () =>
                {
                    BuildTasks.Cancel(task);
                    BuildTasks.SelectedTask = null;
                });
            if (!factorySite)
                return;
            UpgradeOrderTile(task); // апгрейд можно заказать и площадке — нальётся по готовности

            // Каталог = каталог ГОТОВОЙ фабрики этого рода (каталог верфи — весь
            // флот, ангара — его собственный список с префаба, депо — PurchaseService);
            // потолок — «пендящий» тир ПЛОЩАДКИ (апгрейд в её очереди считается;
            // по готовности Adopt перенесёт и его, и очередь).
            int siteTier = ProductionTasks.PendingTierOf(task);
            if (task.IsDock && Encyclopedia.i != null && Encyclopedia.i.ships != null)
            {
                var defs = new List<ShipDefinition>();
                foreach (ShipDefinition def in Encyclopedia.i.ships)
                    if (def != null && def.unitPrefab != null &&
                        def.unitPrefab.GetComponent<Ship>() != null && def.IsAllowed(false) &&
                        !UnitRoster.IsHidden(def))
                        defs.Add(def);
                foreach (ShipDefinition sea in EngineerService.SeaClones) // морские инженеры T2/T3
                    if (sea != null && sea.unitPrefab != null &&
                        sea.unitPrefab.GetComponent<Ship>() != null && sea.IsAllowed(false) &&
                        !UnitRoster.IsHidden(sea))
                        defs.Add(sea);
                Catalog.TierFilterByTier(defs, UnitRoster.TierOf, _prodTier, siteTier);
                foreach (ShipDefinition def in defs)
                {
                    ShipDefinition d = def;
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        TierBadge = "T" + UnitRoster.TierOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueSite(task, d),
                        Def = def,
                    });
                }
                return;
            }
            if (BuildTasks.IsHangarDefinition(task.Definition))
            {
                var defs = new List<AircraftDefinition>();
                foreach (AircraftDefinition a in ProductionTasks.SiteAirCatalog(task.Definition))
                    if (a != null && !UnitRoster.IsHidden(a))
                        defs.Add(a);
                Catalog.TierFilterByTier(defs, UnitRoster.TierOf, _prodTier, siteTier);
                foreach (AircraftDefinition def in defs)
                {
                    AircraftDefinition d = def;
                    int li = ProductionTasks.LoadoutIndexOf(d);
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName) +
                                  (li > 0 ? Loc.Tr("·Л") + li : ""),
                        TypeBadge = TypeBadgeOf(def),
                        TierBadge = "T" + UnitRoster.TierOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueSite(task, d),
                        Def = def,
                        WheelLoadout = true,
                    });
                }
                if (defs.Count == 0)
                    AddItemButton(_strip1Content, null, Loc.Tr("нет работоспособных ангаров"), null);
                return;
            }
            if (PurchaseService.Catalog != null)
            {
                var defs = new List<VehicleDefinition>();
                foreach (VehicleDefinition def in PurchaseService.Catalog)
                    if (def != null && def.IsAllowed(false) && !UnitRoster.IsHidden(def))
                        defs.Add(def);
                Catalog.TierFilterByTier(defs, UnitRoster.TierOf, _prodTier, siteTier);
                foreach (VehicleDefinition def in defs)
                {
                    VehicleDefinition d = def;
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        TierBadge = "T" + UnitRoster.TierOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueSite(task, d),
                        Def = def,
                    });
                }
            }
        }

        /// <summary>Вкладка «Ш» — шаблоны (DESIGN 2.10): ВЕРХняя строка — сохранённые наборы
        /// (иконка-представитель = самое дорогое в наборе); НИЖняя — текущий заказ/набор.
        /// Клик по шаблону: фабрика → весь набор в конец очереди; инженер → призраки.
        /// «Шаблон+» сохраняет: очередь фабрики / очередь площадок / выделенные здания
        /// (shift+клик набор) с относительными позициями.</summary>
        private void FillTemplatesTab(CommanderModeController controller, VehicleDepot depot,
            Airbase airbase, bool hasEngineer, List<Unit> selected)
        {
            // Низ: приоритет — набор выделенных зданий (режим набора под шаблон).
            var buildingSet = new List<Unit>();
            foreach (Unit unit in selected)
                if (unit is Building)
                    buildingSet.Add(unit);
            bool setMode = buildingSet.Count > 0;
            if (setMode)
            {
                _contextTitle.text = Loc.F("Шаблон: набор {0} зданий", buildingSet.Count);
                FillBuildingSetRow(buildingSet);
            }
            else if (hasEngineer)
                _contextTitle.text = Loc.Tr("Шаблон: очередь стройки инженера");
            else if (depot != null)
                _contextTitle.text = Loc.Tr("Шаблон: очередь фабрики");
            else if (airbase != null)
                _contextTitle.text = Loc.Tr("Шаблон: очередь аэродрома");
            else if (BuildTasks.IsFactorySite(BuildTasks.SelectedTask))
                _contextTitle.text = Loc.Tr("Шаблон: очередь фабрики");
            else
                _contextTitle.text = Loc.Tr("Шаблоны");

            // Верх: сохранённые шаблоны.
            foreach (OrderTemplates.Template tpl in OrderTemplates.All)
            {
                OrderTemplates.Template t = tpl;
                Sprite icon = ResolveTemplateIcon(t);
                AddItemButton(_strip1Content, icon, t.name, () =>
                {
                    if (setMode || hasEngineer)
                    {
                        var engineers = new List<Unit>();
                        foreach (Unit unit in controller.Selection.Selected)
                            if (GameApi.IsEngineer(unit))
                                engineers.Add(unit);
                        if (engineers.Count == 0)
                        {
                            CommanderHud.Notice(Loc.Tr("Выдели инженера для постройки шаблона"));
                            return;
                        }
                        OrderTemplates.PendingTemplate = (t, engineers);
                        CommanderHud.Notice(Loc.F("{0}: призраки у курсора · ЛКМ — строить", t.name));
                    }
                    else if (depot != null || airbase != null)
                    {
                        int n = OrderTemplates.Enqueue(t, depot != null ? (object)depot : (object)airbase);
                        CommanderHud.Notice(Loc.F("{0}: в очередь {1}", t.name, n));
                    }
                    else if (BuildTasks.IsFactorySite(BuildTasks.SelectedTask))
                    {
                        // Недострой-фабрика: набор копится как у готовой (выпуск —
                        // по завершении стройки).
                        BuildTasks.Task site = BuildTasks.SelectedTask;
                        int n = OrderTemplates.Enqueue(t, site);
                        CommanderHud.Notice(Loc.F("{0}: в очередь {1}", t.name, n));
                    }
                });
            }
            if (OrderTemplates.Count == 0)
                AddItemButton(_strip1Content, null, Loc.Tr("шаблонов нет — собери набор и нажми Шаблон+"), null);
        }

        private Sprite ResolveTemplateIcon(OrderTemplates.Template t)
        {
            if (t == null || string.IsNullOrEmpty(t.iconKey) || Encyclopedia.i == null)
                return null;
            if (Encyclopedia.i.buildings != null)
                foreach (BuildingDefinition b in Encyclopedia.i.buildings)
                    if (b != null && b.jsonKey == t.iconKey)
                        return IconOf(b);
            if (Encyclopedia.i.vehicles != null)
                foreach (VehicleDefinition v in Encyclopedia.i.vehicles)
                    if (v != null && v.jsonKey == t.iconKey)
                        return IconOf(v);
            if (Encyclopedia.i.aircraft != null)
                foreach (AircraftDefinition a in Encyclopedia.i.aircraft)
                    if (a != null && a.jsonKey == t.iconKey)
                        return IconOf(a);
            return null;
        }

        /// <summary>Набор выделенных зданий плитками; ПКМ по плитке — убрать из набора.</summary>
        private void FillBuildingSetRow(List<Unit> buildingSet)
        {
            ClearChildren(_strip2Content);
            foreach (Unit unit in buildingSet)
            {
                Unit u = unit;
                UnitDefinition def = u.definition;
                Tile(_strip2Content, new TileSpec
                {
                    Icon = IconOf(def),
                    Caption = def != null ? Catalog.ShortLabel(def.code, def.unitName) : null,
                    OnRight = () =>
                    {
                        var controller = CommanderModeController.Instance;
                        if (controller != null)
                            controller.Selection.Selected.Remove(u);
                    },
                    Def = def,
                });
            }
        }

        /// <summary>Вкладка «Производство» (DESIGN 2.10): строка заказа — ЛЕНТА позиций,
        /// полоса тиров сверху фильтрует пул по цене (терцили каталога). Инженер в
        /// выделении → здания (стройка где угодно), иначе депо/аэродром. Нижняя лента —
        /// задел под очередь фабрики (этап B).</summary>
        // Буферы субгрупп апгрейда ангаров (без аллокаций на перестройку каталога)
        private readonly List<object> _upgradePlanes = new List<object>();
        private readonly List<object> _upgradeHelos = new List<object>();
        private readonly List<object> _upgradeDocks = new List<object>();

        private void FillProductionTab(FactionHQ hq, VehicleDepot depot, Building navalDock,
            Airbase airbase, bool hasEngineer, List<Hangar> hangars = null)
        {
            // Суффикс заголовка — ФАКТИЧЕСКИЙ тир контекста; стоит апгрейд в
            // очереди — «T1→T2» (налив видно в очереди, заголовок не врёт).
            string tierName = _ctxTierActual < _ctxTier
                ? " · T" + _ctxTierActual + "→T" + _ctxTier
                : " · T" + _ctxTier;
            // АНГАР(Ы) — индивидуальные авиафабрии (v0.88): каталог — юнион списков
            // выделенных; клик по плитке = +1 КАЖДОМУ выделенному, кто умеет строить
            // (паттерн SupCom «выделил пачку фабрик — скормил заказ всем»).
            if (hangars != null && hangars.Count > 0)
            {
                _contextTitle.text = hangars.Count == 1
                    ? Loc.Tr("Ангар — авиация (клик: в очередь)") + tierName
                    : Loc.F("Ангары ×{0} — клик: +1 каждому умеющему", hangars.Count) + tierName;
                // Групповые плитки апгрейда ПО СУБТИПАМ (юзер-канон 2026-11-04):
                // самолётные и вертолётные — свои пары HQ/клон; одиночный ангар —
                // тоже через группу из одного (та же умная логика веток).
                _upgradePlanes.Clear();
                _upgradeHelos.Clear();
                foreach (Hangar hangar in hangars)
                {
                    if (hangar == null)
                        continue;
                    if (HangarIsHeli(hangar))
                        _upgradeHelos.Add(hangar);
                    else
                        _upgradePlanes.Add(hangar);
                }
                UpgradeOrderTilesForGroup(_upgradePlanes, Loc.Tr("Авиация"));
                UpgradeOrderTilesForGroup(_upgradeHelos, Loc.Tr("Вертолёты"));
                var defs = new List<AircraftDefinition>();
                foreach (Hangar hangar in hangars)
                    foreach (AircraftDefinition a in hangar.GetAvailableAircraft())
                        if (a != null && !defs.Contains(a) && !UnitRoster.IsHidden(a))
                            defs.Add(a);
                Catalog.TierFilterByTier(defs, UnitRoster.TierOf, _prodTier, _ctxTier);
                foreach (AircraftDefinition def in defs)
                {
                    AircraftDefinition d = def;
                    // Отступление от стандарта — суффикс подписи «·ЛN» (индекс пресета)
                    int li = ProductionTasks.LoadoutIndexOf(d);
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName) + (li > 0 ? Loc.Tr("·Л") + li : ""),
                        TypeBadge = TypeBadgeOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () =>
                        {
                            int n = 0;
                            foreach (Hangar hangar in hangars)
                                if (ProductionTasks.CanBuild(hangar, d) &&
                                    ProductionTasks.EnqueueAirSilent(hangar, d))
                                    n++;
                            if (n == 0)
                                CommanderHud.Notice(Loc.F("Ни один выделенный ангар не строит: {0}",
                                    Catalog.ShortLabel(d.code, d.unitName)));
                        },
                        Def = def,
                        WheelLoadout = true,
                    });
                }
                return; // нижняя строка — очередь ангара(ов) (RefreshTabs)
            }
            // ВЕРФЬ — третья фабрика (v0.92): каталог — весь флот энциклопедии
            // (терциль по цене тем же тир-фильтром: T1 катера, T3 авианосцы).
            if (navalDock != null && Encyclopedia.i != null && Encyclopedia.i.ships != null)
            {
                _contextTitle.text = Loc.Tr("Верфь — флот (клик: в очередь)") + tierName;
                _upgradeDocks.Clear();
                _upgradeDocks.Add(navalDock);
                UpgradeOrderTilesForGroup(_upgradeDocks, Loc.Tr("Флот"));
                var defs = new List<ShipDefinition>();
                foreach (ShipDefinition def in Encyclopedia.i.ships)
                    if (def != null && def.unitPrefab != null &&
                        def.unitPrefab.GetComponent<Ship>() != null && def.IsAllowed(false) &&
                        !UnitRoster.IsHidden(def))
                        defs.Add(def);
                foreach (ShipDefinition sea in EngineerService.SeaClones) // морские инженеры T2/T3 (клоны LC)
                    if (sea != null && sea.unitPrefab != null &&
                        sea.unitPrefab.GetComponent<Ship>() != null && sea.IsAllowed(false) &&
                        !UnitRoster.IsHidden(sea))
                        defs.Add(sea);
                Catalog.TierFilterByTier(defs, UnitRoster.TierOf, _prodTier, _ctxTier);
                foreach (ShipDefinition def in defs)
                {
                    ShipDefinition d = def;
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        TierBadge = "T" + UnitRoster.TierOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueShip(navalDock, d),
                        Def = def,
                    });
                }
                return; // нижняя строка — очередь верфи (RefreshTabs)
            }
            if (hasEngineer && BuildingPool.Ready)
            {
                _contextTitle.text = Loc.Tr("Стройка (инженер): здание → ЛКМ по земле") + tierName;
                // ВПП — особый элемент: не здание, по готовности создаёт НОВЫЙ
                // аэродром (своя очередь + грид ангаров) — «ещё одна фабрика».
                Tile(_strip1Content, new TileSpec
                {
                    Caption = Loc.Tr("ВПП"),
                    TypeBadge = Loc.Tr("АЭР"),
                    Badge = Settings.RunwayCost.ToString("F0"),
                    OnLeft = () =>
                    {
                        BuildTasks.Pending = null;
                        BuildTasks.PendingRunway = true;
                    },
                });
                // ВЕРФЬ — вторая спец-площадка: платформа на воде, производит
                // флот (SupCom naval yard; каталог — вся Encyclopedia.ships).
                if (BuildTasks.DockDefinition() != null)
                    Tile(_strip1Content, new TileSpec
                    {
                        Caption = Loc.Tr("ВЕРФЬ"),
                        TypeBadge = Loc.Tr("ФЛОТ"),
                        Badge = Settings.DockCost.ToString("F0"),
                        OnLeft = () =>
                        {
                            BuildTasks.Pending = null;
                            BuildTasks.PendingRunway = false;
                            BuildTasks.PendingDock = true;
                        },
                    });
                // Каталог стройки = ТОЛЬКО ПУЛ (белый список, v0.148): мусор и
                // крупные нативные здания в меню не попадают вовсе; тиры/цены/
                // имена/доноры — Roster/buildings.json (правки без пересборки).
                var defs = new List<BuildingDefinition>(BuildingPool.All);
                Catalog.TierFilterByTier(defs, UnitRoster.BuildingTierOf, _prodTier, _ctxTier);
                foreach (BuildingDefinition def in defs)
                {
                    BuildingDefinition d = def;
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = BuildTasks.IsHangarDefinition(d) ? Loc.Tr("АНГ") : null,
                        TierBadge = "T" + UnitRoster.BuildingTierOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => BuildTasks.Pending = d,
                        Def = def,
                    });
                }
                return; // нижняя строка — очередь стройки (RefreshTabs)
            }

            if (depot != null && PurchaseService.Catalog != null)
            {
                _contextTitle.text = Loc.Tr("Фабрика — техника (клик: в очередь)") + tierName;
                // Группа — все выделенные депо (мультивыделение: плитка «Танки»
                // ставит апгрейд каждому своей веткой; юзер-канон 2026-11-04).
                _upgradeDocks.Clear();
                _upgradeDocks.Add(depot);
                foreach (object f in _ctxFactories)
                    if (f is VehicleDepot d && d != depot && !_upgradeDocks.Contains(d))
                        _upgradeDocks.Add(d);
                UpgradeOrderTilesForGroup(_upgradeDocks, Loc.Tr("Танки"));
                var defs = new List<VehicleDefinition>();
                foreach (VehicleDefinition def in PurchaseService.Catalog)
                    if (def != null && def.IsAllowed(false) && !UnitRoster.IsHidden(def))
                        defs.Add(def);
                // Инженеры T2/T3 — производятся депо СООТВЕТСТВУЮЩЕГО тира
                // (клоны грузовика, тир зарегистрирован в ростере — фильтр общий).
                foreach (VehicleDefinition clone in EngineerService.Clones)
                    if (clone != null && !defs.Contains(clone))
                        defs.Add(clone);
                Catalog.TierFilterByTier(defs, UnitRoster.TierOf, _prodTier, _ctxTier);
                foreach (VehicleDefinition def in defs)
                {
                    VehicleDefinition d = def;
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName),
                        TypeBadge = TypeBadgeOf(def),
                        TierBadge = "T" + UnitRoster.TierOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueGround(depot, d),
                        Def = def,
                    });
                }
                return; // нижняя строка — очередь фабрики (RefreshTabs)
            }

            if (airbase != null && Encyclopedia.i != null && Encyclopedia.i.aircraft != null)
            {
                // Авианосец/LHA — attached-аэродром: тот же каталог (юнион
                // корабельных ангаров), но заголовок честно называет корабль.
                _contextTitle.text = (airbase.AttachedAirbase
                    ? Loc.Tr("Авианосец — авиация (клик: в очередь; РЕЗ — выкат)")
                    : Loc.Tr("Аэродром — авиация (клик: в очередь; РЕЗ — выкат)")) + tierName;
                // Плиток апгрейда в контексте ВЫШКИ НЕТ (юзер-канон 2026-11-04):
                // апгрейд — только при выделении конкретных ангаров/групп.
                // Резерв ангаров (вернувшиеся «на базу» борта): выкат бесплатный,
                // плитки ПЕРЕД каталогом, бейдж РЕЗ + счётчик.
                var reserve = AirbaseReserve.Of(airbase);
                if (reserve != null)
                    foreach (AirbaseReserve.Entry entry in reserve)
                    {
                        AircraftDefinition d = entry.Definition;
                        int count = entry.Count;
                        Tile(_strip1Content, new TileSpec
                        {
                            Icon = IconOf(d),
                            Caption = Catalog.ShortLabel(d.code, d.unitName),
                            TypeBadge = Loc.Tr("РЕЗ"),
                            Badge = "×" + count,
                            OnLeft = () => PurchaseService.DeployReserve(airbase, d),
                            Def = d,
                        });
                    }
                // Каталог = ЮНИОН списков живых ангаров аэродрома (нативный
                // GetAvailableAircraft): заказ типа, который этот аэродром не
                // строит, невозможен в принципе — иначе позиция нальётся деньгами
                // и зависнет без шансов на выпуск (нет ни одного подходящего ангара).
                var defs = new List<AircraftDefinition>();
                foreach (AircraftDefinition a in airbase.GetAvailableAircraft())
                    if (a != null)
                        defs.Add(a);
                if (defs.Count == 0)
                {
                    AddItemButton(_strip1Content, null, Loc.Tr("нет работоспособных ангаров"), null);
                    return;
                }
                Catalog.TierFilterByTier(defs, UnitRoster.TierOf, _prodTier, _ctxTier);
                foreach (AircraftDefinition def in defs)
                {
                    AircraftDefinition d = def;
                    int li = ProductionTasks.LoadoutIndexOf(d);
                    Tile(_strip1Content, new TileSpec
                    {
                        Icon = IconOf(def),
                        Caption = Catalog.ShortLabel(def.code, def.unitName) + (li > 0 ? Loc.Tr("·Л") + li : ""),
                        TypeBadge = TypeBadgeOf(def),
                        TierBadge = "T" + UnitRoster.TierOf(def),
                        Badge = def.value > 0f ? def.value.ToString("F0") : null,
                        OnLeft = () => ProductionTasks.EnqueueAirDistributed(airbase, d),
                        Def = def,
                        WheelLoadout = true,
                    });
                }
                return; // нижняя строка — очередь аэродрома (RefreshTabs)
            }

            _contextTitle.text = Loc.Tr("Производство");
            AddItemButton(_strip1Content, null, Loc.Tr("выдели депо, аэродром или инженера"), null);
            _strip2.gameObject.SetActive(false);
        }

        /// <summary>Фильтр пула по тирам (спека 2.10): каталог сортируется по цене и режется
        /// на три равные части; 0 — без фильтра. Список правится на месте.</summary>


        /// <summary>Вкладка «Апгрейд»: тир экстрактора и апгрейд потоковым наливом
        /// (масса + энергия); идущий налив — прогресс и отмена с возвратом.</summary>
        private void FillUpgradeTab(FactionHQ hq, Unit extractor, bool ecoRunning)
        {
            if (extractor == null || !ecoRunning)
            {
                _contextTitle.text = Loc.Tr("Апгрейд");
                AddItemButton(_strip1Content, null, Loc.Tr("выдели свой экстрактор"), null);
                _strip2.gameObject.SetActive(false);
                return;
            }
            int tier = EconomyService.TierOf(extractor);
            _contextTitle.text = Loc.F("Экстрактор T{0} · +{1}/с",
                tier, EconomyService.IncomeRate(tier).ToString("F1"));

            if (EconomyService.UpgradeInProgress(extractor))
            {
                AddItemButton(_strip1Content, null,
                    Loc.F("апгрейд идёт · {0}%",
                        (EconomyService.UpgradeProgress01(extractor) * 100f).ToString("F0")),
                    null);
                AddItemButton(_strip1Content, null,
                    Loc.Tr("отменить апгрейд"),
                    () => EconomyService.CancelUpgrade(extractor));
            }
            else
            {
                float mass = EconomyService.NextUpgradeMass(extractor);
                if (mass >= 0f)
                {
                    string price = EconomyService.EnergyActive
                        ? Loc.F("{0} + {1} эн.", mass.ToString("F0"),
                            EconomyService.NextUpgradeEnergy(extractor).ToString("F0"))
                        : mass.ToString("F0");
                    AddItemButton(_strip1Content, null,
                        Loc.F("Улучшить до T{0} — {1}", tier + 1, price),
                        () => EconomyService.TryStartUpgrade(hq, extractor));
                }
                else
                    AddItemButton(_strip1Content, null, Loc.Tr("максимальный тир"), null);
            }
            _strip2.gameObject.SetActive(false);
        }
    }
}
