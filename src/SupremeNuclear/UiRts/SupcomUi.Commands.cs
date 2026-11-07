using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


namespace SupremeNuclear
{
    // Часть SupcomUi: контекст фабрик, тиры/вкладки, командная панель, чипы типов.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; канвас собирает SupcomUi.cs (BuildSafe).
    public sealed partial class SupcomUi
    {
        /// <summary>Фабрика-представитель выделения (первая: верфь/депо/ангар/
        /// недострой-фабрика/аэродром): очередь «Шаблон+» и аэродром редактора
        /// лодаутов. Пауза/Цикл идут мимо — группой по _ctxFactories.</summary>
        private object CtxFactory() =>
            _ctxDock != null ? (object)_ctxDock :
            _ctxDepot != null ? (object)_ctxDepot :
            _ctxHangar != null ? (object)_ctxHangar :
            _ctxSite != null ? (object)_ctxSite :
            (object)_ctxAirbase;

        /// <summary>Группа кнопок Пауза/Цикл (v0.108): выделенные фабрики; НО
        /// контекст базы (вышка/ромб/АВИАНОСЕЦ) фабрик в выделении не имеет —
        /// группой становятся ВСЕ ангарные фабрики её Airbase (как мультивыделение
        /// всех ангаров базы). Выделенные ангары приоритетнее (подмножество).</summary>
        private IReadOnlyList<object> PauseCycleTargets()
        {
            if (_ctxFactories.Count > 0)
                return _ctxFactories;
            if (_ctxAirbase != null && _ctxAirbase.hangars != null)
            {
                _ctxBaseFactories.Clear();
                foreach (Hangar hangar in _ctxAirbase.hangars)
                    if (hangar != null)
                        _ctxBaseFactories.Add(hangar);
                if (_ctxBaseFactories.Count > 0)
                    return _ctxBaseFactories;
            }
            return _ctxFactories;
        }

        private readonly List<Unit> _ctxEngineers = new List<Unit>();

        /// <summary>Инженеры выделения — второй адресат кнопки «Пауза» (v0.151,
        /// юзер-канон «на всех юнитах выборки, к которым подходит»): пауза их
        /// налива (стройка/ремонт/сбор/захват), см. PauseService.</summary>
        private List<Unit> SelectedEngineers()
        {
            _ctxEngineers.Clear();
            var controller = CommanderModeController.Instance;
            if (controller == null)
                return _ctxEngineers;
            foreach (Unit unit in controller.Selection.Selected)
                if (unit != null && !unit.disabled && GameApi.IsEngineer(unit))
                    _ctxEngineers.Add(unit);
            return _ctxEngineers;
        }

        /// <summary>Единое состояние кнопки «Пауза» (v0.151): фабрики И инженеры
        /// выделения — все на паузе? (унификация v0.100, распространённая на
        /// юнитов; TogglePauseAll фабрик согласован этой же парой).</summary>
        private bool AllPausedUnified()
        {
            List<Unit> engs = SelectedEngineers();
            return ProductionTasks.AllPaused(PauseCycleTargets()) &&
                   (engs.Count == 0 || PauseService.AllPaused(engs));
        }

        /// <summary>Плитки «переход на тир» в каталоге фабрики (канон SupCom,
        /// v0.136): ПЕРВОЙ в списке СВОЕГО тира; клик ставит апгрейд в очередь.
        /// HQ-модель (канон FAF, юзер 2026-11-03): T1-фабрика БЕЗ ветки видит ДВЕ
        /// плитки — «→T2» (дешёвая, 60%: полный ростер пока жив HQ тира) и
        /// «→T2 ШТАБ» (100%: независимый ростер, держит дешёвые фракции). Ветка
        /// ВЫБРАНА (тир ≥2 или апгрейд в очереди) — плитка ТОЛЬКО её ветки.
        /// Цепочка стакается: цель — от «пендящего» тира.</summary>
        private void UpgradeOrderTile(object factory)
        {
            if (factory == null)
                return;
            int from = ProductionTasks.PendingTierOf(factory);
            if (from >= 3)
                return;
            bool branchChosen = FactoryTierService.TierOf(factory) >= 2 ||
                                ProductionTasks.UpgradeItemOf(factory) != null;
            bool hq = FactoryTierService.BranchIsHq(factory);
            if (!FactoryTierService.NextCost(from, hq, out float mass, out _, out _))
                return;
            if (_prodTier > 0 && _prodTier != from)
                return; // плитка в списке СВОЕГО тира: T1-список → «→T2»
            void TileUp(bool asHq, float price)
            {
                Tile(_strip1Content, new TileSpec
                {
                    Caption = "T" + from + "→T" + (from + 1) + // цифры/стрелка — не переводится
                        (asHq ? " " + Loc.Tr("ШТАБ") : ""),
                    TypeBadge = Loc.Tr("АПГРЕЙД"),
                    Badge = price.ToString("F0"),
                    OnLeft = () => ProductionTasks.EnqueueUpgrade(factory, asHq),
                });
            }
            if (branchChosen)
            {
                // Дешёвая ветка обезглавлена (HQ тира умер) — плитки нет вовсе:
                // продолжение невозможно, пока фракция не построит новый HQ.
                if (hq || FactoryTierService.CheapUpgradeAvailable(factory, from + 1))
                    TileUp(hq, mass);
            }
            else
            {
                // Выбор ветки: дешёвая — ТОЛЬКО при живом HQ целевого тира своего
                // типа фабрики (канон: «дешёвая после наличия HQ нужной фабрики»);
                // нет HQ — одна плитка ШТАБ (полная).
                if (FactoryTierService.CheapUpgradeAvailable(factory, from + 1))
                    TileUp(false, mass);
                if (FactoryTierService.NextCost(from, true, out float hqMass, out _, out _))
                    TileUp(true, hqMass);
            }
        }

        /// <summary>ГРУППОВЫЕ плитки апгрейда субтипа (юзер-канон 2026-11-04):
        /// мультивыделение/контекст базы — по ПАРЕ плиток на каждый ТИП фабрик
        /// («Танки», «Авиация», «Вертолёты», «Флот»): клик ставит апгрейд КАЖДОЙ
        /// фабрике группы выбранной веткой (EnqueueUpgrade сам зажмёт ветку к
        /// выбранной ранее). Умный выбор по субгруппе: дешёвая плитка есть только
        /// при живом HQ целевого тира СВОЕГО типа; нет HQ — только ШТАБ. Шаг —
        /// свой следующий у каждой (T1-е →T2, T2-е →T3), подпись/цена — по
        /// минимальному пендящему тиру группы.</summary>
        private void UpgradeOrderTilesForGroup(IReadOnlyList<object> factories, string typeLabel)
        {
            if (factories == null || factories.Count == 0)
                return;
            // Минимальный пендящий тир группы (плитки в списке СВОЕГО тира)
            int from = 3;
            object lead = null;
            foreach (object f in factories)
            {
                int p = ProductionTasks.PendingTierOf(f);
                if (p < from)
                {
                    from = p;
                    lead = f;
                }
            }
            if (from >= 3 || lead == null)
                return;
            if (_prodTier > 0 && _prodTier != from)
                return; // плитка в списке СВОЕГО тира: T1-список → «→T2»
            bool cheapOk = FactoryTierService.CheapUpgradeAvailable(lead, from + 1);
            void TileUp(bool asHq, float price)
            {
                Tile(_strip1Content, new TileSpec
                {
                    Caption = typeLabel + " T" + from + "→T" + (from + 1) +
                        (asHq ? " " + Loc.Tr("ШТАБ") : ""),
                    TypeBadge = Loc.Tr("АПГРЕЙД"),
                    Badge = price.ToString("F0"),
                    OnLeft = () =>
                    {
                        int n = 0;
                        foreach (object f in factories)
                            if (ProductionTasks.PendingTierOf(f) < 3 &&
                                ProductionTasks.EnqueueUpgrade(f, asHq))
                                n++;
                        if (n > 0)
                            CommanderHud.Notice(Loc.F("Апгрейд: {0} ×{1}", typeLabel, n));
                    },
                });
            }
            if (cheapOk && FactoryTierService.NextCost(from, false, out float sup, out _, out _))
                TileUp(false, sup);
            if (FactoryTierService.NextCost(from, true, out float hqMass, out _, out _))
                TileUp(true, hqMass);
        }

        /// <summary>Ангар — вертолётная площадка (флаг роли пула определения).</summary>
        private static bool HangarIsHeli(Hangar hangar) =>
            hangar != null && BuildingPool.SpecOf(
                hangar.attachedUnit != null
                    ? hangar.attachedUnit.definition as BuildingDefinition
                    : null)?.heliport == true;

        /// <summary>Позиции апгрейда в строке очереди (по плитке на звено цепочки,
        /// порядок очереди): у наливаемого — полоса, ПКМ — отмена с возвратом.</summary>
        private void EmitUpgradeQueueTile(object factory)
        {
            object key = FactoryTierService.KeyOfFactory(factory);
            if (key == null)
                return;
            foreach (ProductionTasks.QueueItem up in ProductionTasks.All)
            {
                if (!up.IsUpgrade || up.Factory != key)
                    continue;
                ProductionTasks.QueueItem item = up;
                Tile(_strip2Content, new TileSpec
                {
                    Caption = "T" + (item.ToTier - 1) + "→T" + item.ToTier +
                        (item.EnergyStalled ? " ⚡" : ""),
                    TypeBadge = Loc.Tr("АПГРЕЙД"),
                    Progress = item.Progress01,
                    BarColor = item.EnergyStalled
                        ? new Color(1f, 0.75f, 0.2f, 0.95f) : (Color?)null,
                    OnRight = () => ProductionTasks.Cancel(item),
                });
            }
        }

        private void SwitchTier(int tier)
        {
            // Полоса — фильтр-ПРОСМОТР в пределах тира контекста (каталог и так
            // ограничен тиром фабрики/инженера): выше просто нечего показывать.
            if (tier >= 1 && tier > _ctxTier)
                tier = _ctxTier;
            if (_prodTier == tier)
                return;
            _prodTier = tier;
            _contextHash = -1; // форсируем перестройку каталога под новый пул
        }


        private void SwitchTab(int tab)
        {
            if (_activeTab == tab)
                return;
            _activeTab = tab;
            _contextHash = -1; // форсируем перестройку контента под новую вкладку
            // Ручной выбор вкладки действует, пока не сменится выделение.
            var controller = CommanderModeController.Instance;
            if (controller != null)
                _manualTabForSel = SelectionHashOf(controller);
        }

        /// <summary>Ключ контекста командной карты: состав выделения по типам войск —
        /// при смене ключа кнопки перестраиваются (контекстные ряды авиации/наземки).</summary>
        private int _cmdContextKey = -1;

        private void RefreshCommands()
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;

            // Контекст по ТИПАМ боевых единиц (v0.92): ряды показываются только
            // тем, к кому применимы. Скорость — самолётам (у вертолётов газ ведёт
            // игровой hover-контур); «Залп» и чипы станций — ракетоносцам
            // (бомбовозу/пулемётному залп нечем); статичные здания без мобильных
            // в выделении — только hold-fire; пустое выделение — базовый набор
            // без авиарядов.
            bool hasAir = false;
            bool hasPlane = false;
            bool hasMissileAir = false;
            bool hasMissileFleet = false; // корабли с ракетами — кнопка «Залп» (ручной пуск)
            bool hasGround = false;
            Aircraft singleAir = null;
            int selCount = 0;
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                selCount++;
                if (unit is Aircraft a)
                {
                    hasAir = true;
                    if (!GameApi.IsHeloFamily(a))
                        hasPlane = true;
                    if (HasMissileStation(a))
                    {
                        hasMissileAir = true;
                        if (selCount == 1)
                            singleAir = a; // чипы станций — одиночному ракетоносцу
                    }
                }
                else if (unit is Ship)
                {
                    hasGround = true;
                    if (FleetSalvo.UnitHasMissiles(unit))
                        hasMissileFleet = true;
                }
                else if (unit is GroundVehicle)
                {
                    hasGround = true;
                    if (FleetSalvo.UnitHasMissiles(unit)) // РСЗО/ПУ — общий залп (v0.133)
                        hasMissileFleet = true;
                }
            }
            if (selCount != 1)
                singleAir = null;
            bool showAir = hasAir;
            bool buildingsOnly = selCount > 0 && !hasAir && !hasGround;
            // Авианосец-аэродром в выделении → кнопка-способность «Авиакрыло»
            // (SupCom-паттерн «строительный модуль — отдельная сущность»);
            // выделен ЕГО аэродром (ромб/кнопка) → обратная «Корабль» (v0.108).
            Airbase carrierBase = null;
            foreach (Unit unit in controller.Selection.Selected)
                if (unit is Ship ship && carrierBase == null)
                {
                    Airbase shipBase = ship.GetAirbase(); // не-авианосцу вернёт null
                    if (shipBase != null)
                        carrierBase = shipBase;
                }
            Airbase backToShip = controller.Selection.Selected.Count == 0 &&
                controller.Selection.SelectedAirbase != null &&
                controller.Selection.SelectedAirbase.AttachedAirbase
                    ? controller.Selection.SelectedAirbase : null;
            WeaponClass gateClasses = WeaponGateService.ClassesOf(controller.Selection.Selected);
            // Состав типов чипов входит в ключ пересборки (как gateClasses): смена
            // набора вооружения выделения перестраивает ряд чипов.
            int typeKey = 0;
            foreach (WeaponInfo wi in WeaponGateService.TypesOf(controller.Selection.Selected, TypeChipCap))
                typeKey = typeKey * 31 + wi.GetInstanceID();
            int key = (showAir ? 1 : 0) | (hasGround ? 2 : 0) | (hasPlane ? 4 : 0) |
                      (hasMissileAir ? 8 : 0) | (singleAir != null ? 16 : 0) |
                      (buildingsOnly ? 32 : 0) | (hasMissileFleet ? 2048 : 0) |
                      ((int)gateClasses << 6) | (typeKey << 12);
            // Кнопки-переключатели: в ключ — ИД базы (смена корабля/базы перестраивает
            // замыкание кнопки, иначе оно держало бы устаревшую ссылку).
            key = key * 31 + (carrierBase != null ? carrierBase.GetInstanceID() : 0);
            key = key * 31 + (backToShip != null ? backToShip.GetInstanceID() : 0);
            if (key != _cmdContextKey)
            {
                _cmdContextKey = key;
                BuildCommandButtons(controller, showAir, hasPlane, hasMissileAir, hasMissileFleet, singleAir, buildingsOnly,
                    carrierBase, backToShip);
            }

            // Подсказка — ТОЛЬКО в активном спец-режиме (стройка/залп/веер/приказ):
            // в покое её нет, верх карточки — заголовок «ПРИКАЗЫ» (без пустой строки).
            string pending = OrderModes.Pending != OrderMode.None && OrderModes.Pending != OrderMode.Move
                ? OrderModes.Label(OrderModes.Pending)
                : null;
            if (BuildTasks.Pending != null)
                _modeHint.text = Util.FitLine(
                    Loc.F("{0}: ЛКМ — площадка, удержание — поворот (Shift — серия) · ПКМ/Esc — отмена",
                            BuildTasks.Pending.unitName), 62);
            else if (BuildTasks.PendingRunway)
                _modeHint.text = Util.FitLine(
                    Loc.F("ВПП: ЛКМ — торец, ПРОТЯНУТЬ — второй ({0}–{1} м) · ПКМ/Esc — отмена",
                            Settings.RunwayMinM.ToString("F0"), Settings.RunwayMaxM.ToString("F0")), 62);
            else if (BuildTasks.PendingDock)
                _modeHint.text = Util.FitLine(
                    Loc.F("Верфь: ЛКМ по воде (глубже {0} м) — платформа, удержание — поворот · ПКМ/Esc — отмена",
                            Settings.DockMinDepthM.ToString("F0")), 62);
            else if (pending == Loc.Tr("Залп"))
                _modeHint.text = Util.FitLine(
                    Loc.Tr("Залп: ЛКМ по цели/земле — залповый пуск · Shift+ЛКМ по целям — доб. в залп · ПКМ — отмена"), 62);
            else if (pending == Loc.Tr("Распределение"))
                _modeHint.text = Util.FitLine(
                    Loc.Tr("Распределение: ЛКМ по целям — накопить · ЛКМ по земле — раздать группу · ПКМ — отмена"), 62);
            else if (pending == Loc.Tr("Охрана"))
                _modeHint.text = Util.FitLine(
                    Loc.Tr("Охрана: ЛКМ по союзному юниту/зданию — охрана (инженер авто-ремонт) · ПКМ/Esc — отмена"), 62);
            else if (pending != null)
                _modeHint.text = Util.FitLine(
                    Loc.F("{0}: ЛКМ — вейпоинт (Shift — серия) · ПКМ/Esc — отмена", pending), 62);
            else
                _modeHint.text = "";
            ApplyCommandLayout();

            foreach (var (btn, img, _, _) in _cmdButtons)
                img.color = BtnBg;
            HighlightCommand("cmd_move", OrderModes.Pending == OrderMode.Move || OrderModes.Pending == OrderMode.None);
            HighlightCommand("cmd_amove", OrderModes.Pending == OrderMode.AttackMove);
            HighlightCommand("cmd_patrol", OrderModes.Pending == OrderMode.Patrol);
            HighlightCommand("cmd_attack", OrderModes.Pending == OrderMode.Attack);
            HighlightCommand("cmd_salvo", OrderModes.Pending == OrderMode.Salvo);
            HighlightCommand("cmd_fan", OrderModes.Pending == OrderMode.DisperseAttack);

            if (showAir)
            {
                HighlightCommand("cmd_vlow", AircraftOrders.ProfileIndex == 0);
                HighlightCommand("cmd_low", AircraftOrders.ProfileIndex == 1);
                HighlightCommand("cmd_med", AircraftOrders.ProfileIndex == 2);
                HighlightCommand("cmd_high", AircraftOrders.ProfileIndex == 3);
                HighlightCommand("cmd_spd0", AircraftOrders.SpeedIndex == SpeedMode.Full);
                HighlightCommand("cmd_spd1", AircraftOrders.SpeedIndex == SpeedMode.Cruise);
                HighlightCommand("cmd_spd2", AircraftOrders.SpeedIndex == SpeedMode.Economy);
                HighlightCommand("cmd_tf0", AircraftOrders.TerrainHug == TerrainHugMode.Profile);
                HighlightCommand("cmd_tf1", AircraftOrders.TerrainHug == TerrainHugMode.Always);
                HighlightCommand("cmd_tf2", AircraftOrders.TerrainHug == TerrainHugMode.Never);
                DefenseMode defense = DefenseMode.Auto;
                foreach (Unit unit in controller.Selection.Selected)
                    if (unit is Aircraft)
                    {
                        defense = DefenseService.ModeOf(unit); // подсветка по первому борту
                        break;
                    }
                HighlightCommand("cmd_def0", defense == DefenseMode.Auto);
                HighlightCommand("cmd_def1", defense == DefenseMode.Flares);
                HighlightCommand("cmd_def2", defense == DefenseMode.Off);
            }
            bool anyHold = false;
            foreach (Unit unit in controller.Selection.Selected)
                if (StanceService.IsHoldFire(unit))
                {
                    anyHold = true;
                    break;
                }
            HighlightCommand("cmd_hold", anyHold);

            // Гейты: цвет кнопки — состояние класса в выделении (зелёный всем разрешён,
            // красный всем выключен, жёлтый вперемешку); подпись «+/−/±» дублирует цвет.
            foreach (WeaponClass cls in GateOrder)
            {
                if ((gateClasses & cls) == 0)
                    continue;
                int state = WeaponGateService.GateStateOf(controller.Selection.Selected, cls);
                TintCommand("gate_" + cls,
                    state > 0 ? BtnActive : state == 0 ? WarnRed : WarnYellow,
                    (state > 0 ? "+ " : state == 0 ? "- " : "± ") + Catalog.ClassLabel(cls));
            }

            // Чипы ТИПОВ (v0.106): подпись «имя + суффикс состояния», цвет как у
            // классов (зелёный без правил, красный выкл, жёлтый ограничение/вперемешку).
            foreach (var (chipBtn, chipImg, chipLabel, chipWi) in _typeChips)
            {
                if (chipBtn == null)
                    continue; // уничтожен пересборкой — список почистит сборка
                TypeGateState? state = WeaponGateService.TypeStateOf(controller.Selection.Selected, chipWi);
                chipImg.color = state == TypeGateState.None ? BtnActive
                    : state == TypeGateState.Off ? WarnRed : WarnYellow;
                chipLabel.text = TypeChipName(chipWi) + Catalog.TypeGateSuffix(state);
            }

            // Чипы станций: боезапас живой, выбранность — подсветкой. Кнопка st_i
            // соответствует _chipStations[i]-й станции борта (только ракетные).
            if (singleAir != null)
            {
                int chip = 0;
                foreach (var (btn, img, _, label) in _cmdButtons)
                {
                    if (!btn.name.StartsWith("st_"))
                        continue;
                    if (chip >= _chipStations.Count)
                        break;
                    int stIdx = _chipStations[chip];
                    WeaponStation st = singleAir.weaponStations[stIdx];
                    if (st != null && st.WeaponInfo != null)
                    {
                        label.text = StationChipLabel(st);
                        img.color = SalvoService.StationEnabled(singleAir, stIdx) ? BtnActive : BtnBg;
                    }
                    chip++;
                }
            }
        }

        /// <summary>Есть ли у борта ракетная станция (без учёта боезапаса): залп V
        /// и чипы станций — только ракетоносцам; бомбовозу/пулемётному залп
        /// нечем (SalvoService завершит сегмент сразу — долг §12-21).</summary>
        private static bool HasMissileStation(Aircraft a)
        {
            foreach (WeaponStation st in a.weaponStations)
                if (st != null && !st.Cargo && st.WeaponInfo != null && st.WeaponInfo.missile)
                    return true;
            return false;
        }

        /// <summary>Подпись чипа станции: короткое имя вооружения ×боезапас.</summary>
        private static string StationChipLabel(WeaponStation st)
        {
            string name = !string.IsNullOrEmpty(st.WeaponInfo.shortName)
                ? st.WeaponInfo.shortName
                : st.WeaponInfo.weaponName;
            if (string.IsNullOrEmpty(name))
                name = Loc.Tr("ст.");
            if (name.Length > 8)
                name = name.Substring(0, 8);
            return name + " ×" + st.Ammo;
        }

        private void HighlightCommand(string name, bool on)
        {
            foreach (var (btn, img, _, _) in _cmdButtons)
                if (btn.name == name && on)
                    img.color = BtnActive;
        }

        /// <summary>Покрасить кнопку команды в точный цвет; text != null — обновить
        /// и подпись (гейты: «+ Бомбы» / «− Бомбы» / «± Бомбы» = всем/никому/вперемешку).</summary>
        private void TintCommand(string name, Color color, string text = null)
        {
            foreach (var (btn, img, _, label) in _cmdButtons)
                if (btn.name == name)
                {
                    img.color = color;
                    if (text != null && label != null)
                        label.text = text;
                    return;
                }
        }

        /// <summary>Контекстная пересборка сетки команд (состав выделения сменился,
        /// v0.92 — по типам боевых единиц): базовые приказы — мобильным (авиация/
        /// наземка/флот), ряды авиации — при авиации, скорость — только при
        /// самолётах, залп — ракетоносцам, чипы станций — одиночному ракетоносцу,
        /// статичным зданиям — только hold-fire. Карточка растёт вверх, панель
        /// свойств выделения поднимается за ней.</summary>
        private void BuildCommandButtons(CommanderModeController controller, bool showAir,
            bool hasPlane, bool hasMissileAir, bool hasMissileFleet, Aircraft singleAir, bool buildingsOnly,
            Airbase carrierBase = null, Airbase backToShip = null)
        {
            Transform grid = null;
            foreach (Transform child in _canvas.transform)
                if (child.name == "CommandCard")
                    foreach (Transform c2 in child)
                        if (c2.name == "grid")
                            grid = c2;
            if (grid == null)
                return;

            foreach (var (btn, _, _, _) in _cmdButtons)
                Destroy(btn.gameObject);
            _cmdButtons.Clear();
            _chipStations.Clear();
            _typeChips.Clear();

            var defs = new List<(string name, string label, System.Action a)>();
            if (!buildingsOnly)
            {
                // Базовые приказы — всем мобильным (авиация/наземка/флот).
                defs.Add(("cmd_move", Loc.Tr("Движение (M)"), () => OrderModes.Pending = OrderMode.Move));
                defs.Add(("cmd_amove", Loc.Tr("Атака-движ (A)"), () => OrderModes.Pending = OrderMode.AttackMove));
                // Патруль — авиации и наземке/флоту (v0.95: циклы; инженеры в патруле
                // сами подхватывают близлежащие недострои).
                defs.Add(("cmd_patrol", Loc.Tr("Патруль (U)"), () => OrderModes.Pending = OrderMode.Patrol));
                defs.Add(("cmd_attack", Loc.Tr("Атака (T)"), () => OrderModes.Pending = OrderMode.Attack));
                // Охрана (v0.158, канон SupCom guard): ЛКМ по союзному юниту/зданию;
                // авиации не назначается (у бортов escort-сегменты плана).
                defs.Add(("cmd_guard", Loc.Tr("Охрана (Y)"), () => OrderModes.Pending = OrderMode.Guard));
                if ((showAir && hasMissileAir) || hasMissileFleet)
                    defs.Add(("cmd_salvo", Loc.Tr("Залп (V)"), () => controller.MoveOrders.ChargeSalvo()));
                // ЯО (v0.139): носители БЧ (корабли/ПУ) и я-авиация — прицел с кольцом
                // поражения; Alt+N — авто-ЯО/автозарядка выделения.
                bool nukeCapable = false;
                foreach (Unit unit in controller.Selection.Selected)
                    if (WarheadService.IsCarrier(unit) ||
                        unit is Aircraft && WarheadService.HasNuclearWeapon(unit))
                    {
                        nukeCapable = true;
                        break;
                    }
                if (nukeCapable)
                    defs.Add(("cmd_nuke", Loc.Tr("ЯО (N)"), () => controller.MoveOrders.ChargeNuke()));
                defs.Add(("cmd_fan", Loc.Tr("Распределение (R)"), () => controller.MoveOrders.ChargeFanAttack()));
                defs.Add(("cmd_stop", Loc.Tr("Стоп (S)"), () => controller.MoveOrders.CancelAll(controller.Selection.Selected)));
                defs.Add(("cmd_disperse", Loc.Tr("Рассредоточить (Alt+Shift+R)"), () =>
                    controller.MoveOrders.IssueDisperseMove(controller.Selection.Selected)));
                if (showAir)
                    defs.Add(("cmd_rtb", Loc.Tr("На базу (G)"), () =>
                        controller.MoveOrders.IssueReturnToBase(controller.Selection.Selected)));
            }
            // Hold-fire — всем, у кого есть оружие: подвижным и статичным зданиям
            // (для выделения из одних зданий это единственная команда).
            defs.Add(("cmd_hold", Loc.Tr("Огонь: удерж (H)"), () =>
            {
                foreach (Unit unit in controller.Selection.Selected)
                    StanceService.Toggle(unit);
            }));
            // СПОСОБНОСТЬ авианосца (SupCom: «модуль» отдельной кнопкой в приказах):
            // переключиться на АВИАКРЫЛО — выделение заменяется аэродромом корабля;
            // там каталог/очередь/резерв/тумблеры, ПКМ = ралли авиакрыла С ПОЛНЫМ
            // Shift (дописать точку), Ctrl+ПКМ с корабля — тот же ралли на бегу.
            if (carrierBase != null)
                defs.Add(("cmd_airwing", Loc.Tr("Авиакрыло"), () =>
                {
                    SelectionService sel = controller.Selection;
                    sel.Selected.Clear();
                    sel.Inspected = null;
                    sel.SelectedAirbase = carrierBase;
                }));
            // Обратное переключение — с аэродрома авианосца к КОРАБЛЮ.
            if (backToShip != null)
                defs.Add(("cmd_ship", Loc.Tr("Корабль"), () =>
                {
                    if (backToShip.TryGetAttachedUnit(out Unit ship) &&
                        ship != null && !ship.disabled)
                    {
                        SelectionService sel = controller.Selection;
                        sel.SelectedAirbase = null;
                        sel.Inspected = null;
                        sel.Selected.Clear();
                        sel.Selected.Add(ship);
                    }
                }));
            if (showAir)
            {
                defs.Add(("cmd_vlow", Loc.Tr("Свер-хнизко 75м"), () => controller.SetProfileUi(0)));
                defs.Add(("cmd_low", Loc.Tr("Низко 150м"), () => controller.SetProfileUi(1)));
                defs.Add(("cmd_med", Loc.Tr("Средне 800м"), () => controller.SetProfileUi(2)));
                defs.Add(("cmd_high", Loc.Tr("Высоко 2500м"), () => controller.SetProfileUi(3)));
                if (hasPlane) // вертолётам тротл не назначается — газ ведёт игра
                {
                    defs.Add(("cmd_spd0", Loc.Tr("Полный (Shift+1)"), () => controller.SetSpeedUi(0)));
                    defs.Add(("cmd_spd1", Loc.Tr("Крейсер (Shift+2)"), () => controller.SetSpeedUi(1)));
                    defs.Add(("cmd_spd2", Loc.Tr("Эконом (Shift+3)"), () => controller.SetSpeedUi(2)));
                    // «Вдоль рельефа» (v0.94): вертолётам руление огибает всегда —
                    // тоггл показываем только при самолётах.
                    defs.Add(("cmd_tf0", Loc.Tr("Рельеф: авто (Alt+T)"), () => controller.SetTerrainFollowUi(0)));
                    defs.Add(("cmd_tf1", Loc.Tr("Рельеф: огибать"), () => controller.SetTerrainFollowUi(1)));
                    defs.Add(("cmd_tf2", Loc.Tr("Рельеф: напрямую"), () => controller.SetTerrainFollowUi(2)));
                }
                defs.Add(("cmd_flare", Loc.Tr("Ловушки! (X)"), () =>
                {
                    int popped = DefenseService.PopNow(controller.Selection.Selected);
                    CommanderHud.Notice(popped > 0 ? Loc.F("Ловушки: бортов — {0}", popped) : Loc.Tr("Нет бортов с контрмерами"));
                }));
                defs.Add(("cmd_def0", Loc.Tr("Защита: Авто"), () => controller.SetDefenseUi(0)));
                defs.Add(("cmd_def1", Loc.Tr("Защита: Ловушки"), () => controller.SetDefenseUi(1)));
                defs.Add(("cmd_def2", Loc.Tr("Защита: Выкл"), () => controller.SetDefenseUi(2)));
            }

            // Чипы ракетных станций залпа (одиночный ракетоносец): ЛКМ — вкл/выкл
            // станции; индексы — реальные позиции в weaponStations (для обновления
            // подписи/подсветки в RefreshCommands).
            if (singleAir != null)
                for (int idx = 0; idx < singleAir.weaponStations.Count && _chipStations.Count < 12; idx++)
                {
                    WeaponStation st = singleAir.weaponStations[idx];
                    if (st == null || st.Cargo || st.WeaponInfo == null || !st.WeaponInfo.missile)
                        continue;
                    int stationIdx = idx;
                    defs.Add(("st_" + _chipStations.Count, StationChipLabel(st),
                        () => SalvoService.ToggleStation(singleAir, stationIdx)));
                    _chipStations.Add(idx);
                }

            int rows = Mathf.CeilToInt(defs.Count / 4f);
            foreach (var (name, label, action) in defs)
            {
                Button b = Button_(grid, name, label, action, 11);
                _cmdButtons.Add((b, b.GetComponent<Image>(), action, b.GetComponentInChildren<Text>()));
            }

            // Гейты вооружения (DESIGN §2.11), два ряда внизу карточки: КЛАССЫ —
            // тоггл всему выделению (гейт главнее явных приказов); ТИПЫ (v0.106) —
            // чип на каждый тип вооружения выделения (кэп TypeChipCap, по убыванию
            // полного боезапаса), ЛКМ циклит правило, колесо — туда-обратно.
            int gates = 0;
            int types = 0;
            if (_gateStrip != null)
            {
                WeaponClass classes = WeaponGateService.ClassesOf(controller.Selection.Selected);
                foreach (WeaponClass cls in GateOrder)
                {
                    if ((classes & cls) == 0)
                        continue;
                    WeaponClass c = cls;
                    Button b = Button_(_gateStrip, "gate_" + cls, Catalog.ClassLabel(cls),
                        () => WeaponGateService.ToggleForSelection(controller.Selection.Selected, c), 11);
                    ((RectTransform)b.transform).sizeDelta = new Vector2(76f, 22f);
                    b.gameObject.AddComponent<LayoutElement>().minWidth = 76f;
                    _cmdButtons.Add((b, b.GetComponent<Image>(), null, b.GetComponentInChildren<Text>()));
                    gates++;
                }
                if (_gateTypes != null)
                {
                    foreach (WeaponInfo wi in WeaponGateService.TypesOf(
                                 controller.Selection.Selected, TypeChipCap))
                    {
                        WeaponInfo w = wi;
                        Button b = Button_(_gateTypes, "typechip_" + w.GetInstanceID(),
                            TypeChipName(w), () => CycleTypeGate(controller, w, true), 10);
                        ((RectTransform)b.transform).sizeDelta = new Vector2(64f, 22f);
                        b.gameObject.AddComponent<LayoutElement>().minWidth = 64f;
                        _cmdButtons.Add((b, b.GetComponent<Image>(), null, b.GetComponentInChildren<Text>()));
                        _typeChips.Add((b, b.GetComponent<Image>(), b.GetComponentInChildren<Text>(), w));
                        types++;
                    }
                    _gateTypes.gameObject.SetActive(types > 0);
                }
            }
            // Раскладка секции (снизу вверх): типы 3..25, классы 27..49, заголовок
            // 51..64; сетка команд — над секцией; высота панели — в ApplyCommandLayout.
            _gateStrip.gameObject.SetActive(gates > 0);
            if (_gateHdr != null)
                _gateHdr.gameObject.SetActive(gates > 0);
            var gridRt = (RectTransform)grid;
            gridRt.offsetMin = new Vector2(8f, gates > 0 ? (types > 0 ? 67f : 43f) : 3f);
            _cmdRows = rows;
            _cmdGateHeight = gates > 0 ? (types > 0 ? 68f : 44f) : 3f;
            ApplyCommandLayout();
        }

        /// <summary>Раскладка командной карточки по последней сборке кнопок и наличию
        /// подсказки активного режима: без подсказки заголовок «ПРИКАЗЫ» у верхнего
        /// края (никаких пустых строк), с подсказкой — под ней. Панель свойств
        /// выделения поднимается за карточкой (низ = верх карточки + зазор 6);
        /// скрытая карточка (чистый осмотр) места в стеке не занимает.</summary>
        private void ApplyCommandLayout()
        {
            // Стек левой колонки (снизу вверх): командная карта → ОПИСАНИЕ (ховер
            // плитки закупки) → панель свойств. Панель свойств всегда опирается
            // на верх того, что под ней; без командной карты — на низ экрана.
            float above = 12f;
            if (_commandPanel != null && _commandPanel.gameObject.activeSelf)
            {
                bool hint = _modeHint != null && !string.IsNullOrEmpty(_modeHint.text);
                if (_ordersHdr != null)
                {
                    var rt = (RectTransform)_ordersHdr.transform;
                    rt.offsetMin = new Vector2(10f, hint ? -31f : -15f);
                    rt.offsetMax = new Vector2(-8f, hint ? -19f : -3f);
                }
                if (_cmdGrid != null)
                    _cmdGrid.offsetMax = new Vector2(-8f, hint ? -33f : -17f);
                float height = (hint ? 18f : 2f) + 13f + _cmdRows * 26f +
                               Mathf.Max(0, _cmdRows - 1) * 4f + 5f + _cmdGateHeight;
                _commandPanel.offsetMax = new Vector2(430f, 12f + height);
                above = 12f + height + 6f;
            }
            if (_descPanel != null && _descPanel.gameObject.activeSelf)
            {
                _descPanel.offsetMin = new Vector2(12f, above);
                _descPanel.offsetMax = new Vector2(430f, above + _descHeight);
                above += _descHeight + 6f;
            }
            if (_infoPanel != null)
                _infoPanel.offsetMin = new Vector2(12f, above);
        }

        // ---------- чипы ТИПОВ вооружения (v0.106, DESIGN §2.11) ----------

        /// <summary>Подпись чипа типа: shortName до 7 символов (влезает в 64 px
        /// при 10 кегле); пустое имя — «ст.».</summary>
        private static string TypeChipName(WeaponInfo wi)
        {
            string name = !string.IsNullOrEmpty(wi.shortName) ? wi.shortName : wi.weaponName;
            if (string.IsNullOrEmpty(name))
                name = Loc.Tr("ст.");
            return name.Length > 7 ? name.Substring(0, 7) : name;
        }

        /// <summary>Цикл правила типа по выделению (ЛКМ — вперёд, колесо — в обе
        /// стороны): применяется всем юнитам выделения, у которых тип есть; Notice
        /// называет выбранное состояние, карточка рефрешится немедленно.</summary>
        private void CycleTypeGate(CommanderModeController controller, WeaponInfo wi, bool forward)
        {
            TypeGateState state =
                Catalog.Next(WeaponGateService.TypeStateOf(controller.Selection.Selected, wi), forward);
            WeaponGateService.ApplyTypeState(controller.Selection.Selected, wi, state);
            // имя оружия — данные игры (не переводится), клей «: » — не фраза
            CommanderHud.Notice(TypeChipName(wi) + ": " + Catalog.TypeGateLabel(state));
            _nextRefresh = 0f;
        }

        /// <summary>Колесо над чипом типа — цикл правила туда-обратно (как колесо
        /// лодаутов над плиткой каталога): скан прямоугольников чипов каждый кадр
        /// с прокруткой, колесо УПОТРЕБЛЕНО UI — камера в этом кадре не зумит.</summary>
        private void UpdateTypeChipWheel()
        {
            float wheel = Input.mouseScrollDelta.y;
            if (wheel == 0f || RtsInput.Typing || _typeChips.Count == 0)
                return;
            var controller = CommanderModeController.Instance;
            if (controller == null)
                return;
            for (int i = _typeChips.Count - 1; i >= 0; i--)
            {
                var chip = _typeChips[i];
                if (chip.btn == null)
                {
                    _typeChips.RemoveAt(i); // уничтожен пересборкой карточки
                    continue;
                }
                if (!chip.btn.gameObject.activeInHierarchy)
                    continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(
                        (RectTransform)chip.btn.transform, Input.mousePosition, null))
                    continue;
                CycleTypeGate(controller, chip.wi, wheel > 0f);
                RtsInput.WheelConsumedByUi = true;
                return; // один чип за прокрутку
            }
        }

        /// <summary>ПКМ по чипу ТИПА вооружения — зарядить режим «Залп» (v0.118):
        /// у флота волны пойдут ТОЛЬКО этим типом (ПКМ по AGM-48 — пуск AGM-48,
        /// ПВО прибережём), у авиации — обычный залповый заряд. Опрос в Update:
        /// игровой input-модуль не доставляет ПКМ в uGUI (правило 18); мир в этом
        /// кадре ПКМ не съест — курсор над UI (PointerOverUi).</summary>
        private void UpdateTypeChipSalvo()
        {
            if (!RtsInput.RightDown || RtsInput.Typing || _typeChips.Count == 0)
                return;
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;
            for (int i = _typeChips.Count - 1; i >= 0; i--)
            {
                var chip = _typeChips[i];
                if (chip.btn == null)
                {
                    _typeChips.RemoveAt(i); // уничтожен пересборкой карточки
                    continue;
                }
                if (!chip.btn.gameObject.activeInHierarchy)
                    continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(
                        (RectTransform)chip.btn.transform, Input.mousePosition, null))
                    continue;
                if (chip.wi == null || !chip.wi.missile)
                    return; // заряд — только с ракетного чипа
                controller.MoveOrders.ChargeSalvo(chip.wi);
                CommanderHud.Notice(Loc.F("Залп заряжен: {0}", TypeChipName(chip.wi)));
                return;
            }
        }
    }
}
