using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


namespace SupremeNuclear
{
    // Часть SupcomUi: обновление панелей: топ-бар, ростер, выделение, осмотр.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; канвас собирает SupcomUi.cs (BuildSafe).
    public sealed partial class SupcomUi
    {
        private static int SelectionHashOf(CommanderModeController controller)
        {
            int h = 17;
            foreach (Unit unit in controller.Selection.Selected)
                h = h * 31 + (unit != null ? unit.GetInstanceID() : 0);
            Airbase ab = controller.Selection.SelectedAirbase;
            if (ab != null)
                h = h * 31 + ab.GetInstanceID();
            return h;
        }

        // ---------- обновление ----------

        private void RefreshTopBar()
        {
            var controller = CommanderModeController.Instance;
            bool active = controller != null && controller.IsActive;
            if (_canvas != null)
                _canvas.enabled = active;
            if (!active)
                return;
            if (!_loggedActiveOnce)
            {
                _loggedActiveOnce = true;
                Log.Info($"SupcomUi: активен — canvas={_canvas != null}, funds={_fundsText != null}, income={_incomeText != null}, " +
                         $"extr={_extractorsText != null}, ctx={_strip1 != null}, order={_canvas.sortingOrder}, детей={_canvas.transform.childCount}");
            }

            if (_fundsText == null || _incomeText == null || _extractorsText == null)
                return; // верхняя панель не собралась — остальное живёт
            if (!GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            _fundsText.text = $"{hq.factionFunds:F0}";

            // Панель потока (DESIGN §2.2, этап C): масса и энергия с доходом/расходом.
            bool running = EconomyService.Running;
            float massInc = running ? EconomyService.MassIncomePerSecond(hq) : 0f;
            float massDr = EconomyService.MassDrainPerSecond(hq);
            _incomeText.text = Loc.F("+{0} −{1}/с · экстракторы {2}/{3}",
                massInc.ToString("F1"), massDr.ToString("F1"),
                EconomyService.OwnedExtractors(hq), EconomyService.TotalExtractors);
            _incomeText.color = massInc - massDr >= 0f ? TextMain : WarnRed;

            if (EconomyService.EnergyActive)
            {
                float eInc = EconomyService.EnergyIncomePerSecond(hq);
                float eDr = EconomyService.EnergyDrainPerSecond(hq);
                _extractorsText.text = Loc.F("энергия {0}/{1} · +{2} −{3}/с",
                    EconomyService.EnergyStore(hq).ToString("F0"),
                    EconomyService.EnergyCapacity(hq).ToString("F0"),
                    eInc.ToString("F1"), eDr.ToString("F1"));
                _extractorsText.color = eInc - eDr >= 0f ? TextDim : WarnRed;
            }
            else
                _extractorsText.text = string.Empty;
            // ЯО (v0.139): запас БЧ фракции — когда есть что показывать.
            int warheads = hq.GetWarheadStockpile();
            if (warheads > 0)
                _extractorsText.text += Loc.F(" · БЧ {0}", warheads);
        }

        /// <summary>Вкладка «Список»: плитки выделения по типам (15 в ряд, 3 ряда),
        /// заголовок «Выбрано» сбоку; клик по плитке — подвыделение типа.</summary>
        /// <summary>Вкладка «Список» (DESIGN 2.10): верхняя лента — выделенные объекты
        /// плитками по типам, группы разделены промежутками (инженеры → наземка → флот →
        /// авиация → заводы → здания); клик по плитке — подвыделение типа.
        /// Нижняя лента — строка заказа (задел под фабрики/инженеров, этап B).</summary>
        private void FillRosterTab(CommanderModeController controller, List<Unit> selected)
        {
            ClearChildren(_strip2Content);

            var groups = new Dictionary<UnitDefinition, List<Unit>>();
            foreach (Unit unit in selected)
                if (unit != null && unit.definition != null)
                {
                    if (!groups.TryGetValue(unit.definition, out List<Unit> list))
                        groups[unit.definition] = list = new List<Unit>();
                    list.Add(unit);
                }

            int Category(Unit u) =>
                GameApi.IsEngineer(u) ? 0 :
                u is GroundVehicle ? 1 :
                u is Ship ? 2 :
                u is Aircraft ? 3 :
                u is VehicleDepot ? 4 : 5;

            var ordered = new List<(UnitDefinition def, List<Unit> units, int cat)>();
            foreach (var kv in groups)
                ordered.Add((kv.Key, kv.Value, Category(kv.Value[0])));
            ordered.Sort((a, b) => a.cat.CompareTo(b.cat));

            int lastCat = -1;
            foreach (var (def, units, cat) in ordered)
            {
                if (lastCat != -1 && cat != lastCat)
                    AddRosterGap(_strip1Content); // визуальный промежуток между категориями
                lastCat = cat;

                UnitDefinition d = def;
                Tile(_strip1Content, new TileSpec
                {
                    Icon = IconOf(def),
                    Caption = Catalog.ShortLabel(def.code, def.unitName),
                    Badge = "×" + units.Count,
                    TypeBadge = TypeBadgeOf(def),
                    TierBadge = "T" + UnitRoster.TierOf(def),
                    OnLeft = () => SelectionService.SelectTypeStatic(controller, d),
                    Def = def,
                });
            }

            _contextTitle.text = Loc.F("Выбрано: {0}", selected.Count);
            _strip2.gameObject.SetActive(false); // строка заказа — с фабриками (этап B)
        }

        /// <summary>Промежуток между категориями выделения (визуальное разделение, DESIGN 2.10).</summary>
        private static void AddRosterGap(Transform content)
        {
            var gap = new GameObject("gap", typeof(RectTransform));
            var rt = (RectTransform)gap.transform;
            rt.SetParent(content, false);
            rt.sizeDelta = new Vector2(10f, 48f);
            gap.AddComponent<LayoutElement>().minWidth = 10f;
        }

        private void RefreshSelectionInfo()
        {
            var controller = CommanderModeController.Instance;
            if (controller == null || !controller.IsActive)
                return;
            var selected = controller.Selection.Selected;

            // По умолчанию — режим выделения (ветки ниже переопределяют строки):
            // заголовок зоны, видимость полосы HP (карточка закупки их прячет) и
            // цвет строки состояния (строка лодаута подсвечивается акцентом).
            if (_infoZoneHdr != null)
                _infoZoneHdr.text = Loc.Tr("ВЫДЕЛЕНИЕ");
            if (_infoHpFill != null && _infoHpFill.parent != null)
                _infoHpFill.parent.gameObject.SetActive(true);
            if (_infoHpTxt != null)
                _infoHpTxt.gameObject.SetActive(true);
            if (_infoState != null)
                _infoState.color = TextDim;
            if (_infoWeaponsHeader != null)
                _infoWeaponsHeader.text = Loc.Tr("Вооружение");
            UpdateDefDesc(null);

            // Наведение на плитку закупки (SupCom): панель показывает карточку
            // ОПРЕДЕЛЕНИЯ — приоритетнее выделения, пока курсор на плитке.
            if (_hoverShow != null)
            {
                RefreshDefInfo(_hoverShow);
                return;
            }

            // Выделенный НЕДОСТРОЙ: как объект в SupCom — имя, прогресс, строители.
            if (selected.Count == 0 && BuildTasks.SelectedTask != null)
            {
                BuildTasks.Task t = BuildTasks.SelectedTask;
                if (t.Definition == null)
                    return;
                _infoTitle.text = Util.FitLine(t.Definition.unitName, FitTitleChars);
                _infoClass.text = Util.FitLine(Loc.F("недострой · {0}/{1} · строителей {2}",
                t.PaidMass.ToString("F0"), t.Cost.ToString("F0"), t.Builders.Count), FitBodyChars);
                UiKit.SetProgress(_infoHpFill, t.Progress01);
                _infoHpTxt.text = t.Progress01.ToString("P0");
                _infoFuelTrack.gameObject.SetActive(false);
                _infoFuelTxt.gameObject.SetActive(false);
                _infoMove.text = t.ZoneBlocked ? Loc.Tr("зона не расчищена") : Loc.Tr("идёт стройка");
                _infoState.text = Loc.Tr("ПКМ инженерами — достройка");
                _infoOrder[0].text = "";
                _infoOrder[1].text = "";
                _infoOrder[2].text = "";
                _infoWeaponsHeader.gameObject.SetActive(false);
                for (int i = 0; i < _infoWeapons.Length; i++)
                    _infoWeapons[i].gameObject.SetActive(false);
                FlowInfoBody(false);
                return;
            }

            // Осмотр чужой цели/ракеты (ЛКМ по видимому контакту): панель свойств
            // работает как разведкарточка — без выделения и приказов.
            if (selected.Count == 0 && controller.Selection.Inspected != null)
            {
                RefreshInspected(controller.Selection.Inspected);
                FlowInfoBody(false);
                return;
            }

            Unit single = selected.Count == 1 ? selected[0] : null;
            bool isSingle = single != null && !single.disabled;
            bool isAir = isSingle && single is Aircraft;
            int weapons = isSingle && single.weaponStations != null
                ? Mathf.Min(single.weaponStations.Count, _infoWeapons.Length)
                : 0;
            if (!isSingle)
                weapons = AggregateGroupWeapons(selected);

            int hash = 17;
            foreach (Unit unit in selected)
                hash = hash * 31 + (unit != null ? unit.GetInstanceID() : 0);
            hash = hash * 31 + ((isSingle ? 1 : 0) + (isAir ? 2 : 0) + weapons * 4);
            if (hash != _infoHash)
            {
                _infoHash = hash;
                _infoFuelTrack.gameObject.SetActive(isAir);
                _infoFuelTxt.gameObject.SetActive(isAir);
                _infoWeaponsHeader.gameObject.SetActive(weapons > 0);
                for (int i = 0; i < _infoWeapons.Length; i++)
                    _infoWeapons[i].gameObject.SetActive(i < weapons);
            }

            if (isSingle)
                RefreshSingleUnit(single);
            else
                RefreshMultiUnit(selected);
            FlowInfoBody(isAir);
        }

        /// <summary>Карточка определения при наведении на плитку закупки (SupCom):
        /// шапка — имя, класс/роль · цена · время; строка ТТХ; строка лодаута
        /// (авиация с пресетами — выбор колесом, недоступный на текущем аэродроме
        /// подписан, заказ уйдёт «Стандартом»); вооружение выбранного лодаута;
        /// описание — в окне «ОПИСАНИЕ» под панелью. Контент определения посчитан
        /// один раз на пару (определение, лодаут) и лежит в кэше.</summary>
        private void RefreshDefInfo(UnitDefinition def)
        {
            var adef = def as AircraftDefinition;
            int loadIdx = adef != null ? ProductionTasks.LoadoutIndexOf(adef) : 0;
            TipContent c = GetTipContent(def, loadIdx);
            if (_infoZoneHdr != null)
                _infoZoneHdr.text = Loc.Tr("ЗАКУПКА");
            _infoTitle.text = c.Title;
            _infoClass.text = Util.FitLine(c.ClassLine, 60);
            _infoHpFill.parent.gameObject.SetActive(false);
            _infoHpTxt.gameObject.SetActive(false);
            _infoFuelTrack.gameObject.SetActive(false);
            _infoFuelTxt.gameObject.SetActive(false);
            // ТТХ — факт-на-строку (движение/строки заказов панели переиспользуются
            // под строки карточки; пустые гасятся поточной раскладкой).
            _infoMove.text = StatRowOf(c, 0);
            _infoOrder[0].text = StatRowOf(c, 1);
            _infoOrder[1].text = StatRowOf(c, 2);
            _infoOrder[2].text = "";
            string loLine = c.LoadoutLine;
            if (!string.IsNullOrEmpty(loLine) && adef != null && loadIdx > 0)
            {
                Loadout sel = ProductionTasks.SelectedLoadout(adef, out _);
                if (sel != null && GameManager.GetLocalHQ(out FactionHQ hq) &&
                    !ProductionTasks.LoadoutAllowed(adef, sel, hq))
                    loLine += Loc.Tr(" · недоступен");
            }
            _infoState.text = Util.FitLine(loLine, 60);
            // Строка лодаута — АКЦЕНТ: что выбирается колесом видно сразу,
            // без чтения серых строк (подсветка выбора, плейтест v0.89).
            if (_infoState != null)
                _infoState.color = string.IsNullOrEmpty(c.LoadoutLine) ? TextDim : Accent;
            for (int i = 0; i < _infoOrder.Length; i++)
                _infoOrder[i].text = "";
            _infoWeaponsHeader.gameObject.SetActive(c.Weapons.Count > 0);
            _infoWeaponsHeader.text = loadIdx > 0 ? Loc.Tr("Вооружение лодаута") : Loc.Tr("Вооружение");
            for (int i = 0; i < _infoWeapons.Length; i++)
            {
                bool show = i < c.Weapons.Count;
                _infoWeapons[i].gameObject.SetActive(show);
                if (show)
                    _infoWeapons[i].text = Util.FitLine(c.Weapons[i], 60);
            }
            UpdateDefDesc(c);
            FlowInfoBody(false);
        }

        /// <summary>Строка ТТХ карточки по номеру (пустая — за пределами фактов).</summary>
        private static string StatRowOf(TipContent c, int i) =>
            i < c.StatRows.Count ? c.StatRows[i] : "";

        /// <summary>Окно «ОПИСАНИЕ» под панелью свойств: c=null/без описания —
        /// скрыть. Высота окна под контент; позиции стека левой колонки
        /// пересчитываются сразу (ApplyCommandLayout).</summary>
        private void UpdateDefDesc(TipContent c)
        {
            if (_descPanel == null)
                return;
            bool show = c != null && c.DescLines > 0;
            if (_descPanel.gameObject.activeSelf != show)
                _descPanel.gameObject.SetActive(show);
            if (!show)
            {
                _descHeight = 0f;
                ApplyCommandLayout();
                return;
            }
            _descText.text = c.Desc;
            _descHeight = 18f + c.DescLines * DescLineH + 6f;
            var rt = (RectTransform)_descText.transform;
            rt.offsetMin = new Vector2(12f, -(18f + c.DescLines * DescLineH));
            ApplyCommandLayout();
        }

        private void RefreshSingleUnit(Unit u)
        {
            string defName = u.definition != null ? u.definition.unitName : "";
            _infoTitle.text = Util.FitLine(
                !string.IsNullOrEmpty(u.unitName) ? u.unitName : defName, FitTitleChars);
            _infoClass.text = Util.FitLine(
                (defName.Length > 0 ? defName + " · " : "") + ClassOf(u), FitBodyChars);

            float hp = GameApi.HpFraction(u);
            UiKit.SetProgress(_infoHpFill, hp);
            _infoHpTxt.text = hp >= 0.995f ? Loc.Tr("HP цел") : Loc.F("HP {0}", hp.ToString("P0"));

            float speedKmh = Mathf.Abs(u.speed) * 3.6f;
            if (u is Aircraft a)
            {
                float fuel = a.GetFuelLevel();
                UiKit.SetProgress(_infoFuelFill, fuel);
                _infoFuelTxt.text = Loc.F("топливо {0}", fuel.ToString("P0"));
                _infoMove.text = Util.FitLine(Loc.F("{0} км/ч · {1} м", speedKmh.ToString("F0"), a.radarAlt.ToString("F0")), FitBodyChars);
                _infoState.text = Util.FitLine(a.pilots != null && a.pilots.Length > 0 && a.pilots[0] != null
                    ? Catalog.RuPilotState(a.pilots[0].GetCurrentState())
                    : Loc.Tr("нет пилота"), FitBodyChars);
            }
            else
            {
                _infoMove.text = Loc.F("{0} км/ч", speedKmh.ToString("F0"));
                _infoState.text = Util.FitLine(
                    u is Ship s && s.TryGetComponent(out ShipAI sai)
                        ? Catalog.RuShipState(sai.state.ToString())
                        : "", FitBodyChars);
            }

            _infoOrder[0].text = Util.FitLine(Loc.F("Приказ: {0}", PlanSummary(u)), FitBodyChars);
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";
            _infoWeaponsHeader.text = Loc.Tr("Вооружение");

            int w = 0;
            if (u.weaponStations != null)
                foreach (WeaponStation ws in u.weaponStations)
                {
                    if (w >= _infoWeapons.Length)
                        break;
                    if (ws == null || ws.WeaponInfo == null)
                        continue;
                    string name = !string.IsNullOrEmpty(ws.WeaponInfo.shortName)
                        ? ws.WeaponInfo.shortName
                        : ws.WeaponInfo.weaponName;
                    // Гейт держит станцию «неготовой» при полном боезапасе —
                    // это не «пусто»; «пусто» — только расстрелянный боезапас.
                    string status = !WeaponGateService.IsAllowed(u, ws) ? Loc.Tr(" · выкл")
                        : ws.Reloading ? Loc.Tr(" · перезарядка")
                        : (ws.Ammo > 0 ? "" : Loc.Tr(" · пусто"));
                    _infoWeapons[w++].text = Util.FitLine(
                        $"{name}: {ws.Ammo}/{ws.FullAmmo}{status}", FitBodyChars);
                }
        }

        /// <summary>Собирает сводку вооружения по выделению в _groupWeapons: станции
        /// живых юнитов складываются по имени оружия (грузовые/десантные станции — мимо),
        /// носители считаются по юнитам. Возвращает число строк под доступные метки
        /// (при переполнении последняя метка — «+ ещё N типов»).</summary>
        private int AggregateGroupWeapons(List<Unit> selected)
        {
            _groupWeapons.Clear();
            foreach (Unit unit in selected)
            {
                if (unit == null || unit.disabled || unit.weaponStations == null)
                    continue;
                _groupWeapons.BeginUnit();
                foreach (WeaponStation ws in unit.weaponStations)
                {
                    if (ws == null || ws.WeaponInfo == null || ws.Cargo ||
                        ws.WeaponInfo.cargo || ws.WeaponInfo.troops)
                        continue;
                    _groupWeapons.AddStation(
                        !string.IsNullOrEmpty(ws.WeaponInfo.shortName)
                            ? ws.WeaponInfo.shortName
                            : ws.WeaponInfo.weaponName,
                        ws.Ammo, ws.FullAmmo,
                        Catalog.WeaponDomains(
                            ws.WeaponInfo.effectiveness.antiAir,
                            ws.WeaponInfo.effectiveness.antiSurface,
                            ws.WeaponInfo.effectiveness.antiMissile,
                            ws.WeaponInfo.nuclear, ws.WeaponInfo.jammer));
                }
            }
            return Mathf.Min(_groupWeapons.Rows.Count, _infoWeapons.Length);
        }

        private void RefreshMultiUnit(List<Unit> selected)
        {
            _infoTitle.text = Loc.F("Выбрано: {0}", selected.Count);
            int air = 0, ground = 0, naval = 0;
            float hpSum = 0f, ammoSum = 0f;
            int ammoN = 0, n = 0;
            foreach (Unit unit in selected)
            {
                if (unit == null)
                    continue;
                n++;
                if (unit is Aircraft) air++;
                else if (unit is Ship) naval++;
                else ground++;
                hpSum += GameApi.HpFraction(unit);
                if (unit.weaponStations != null && unit.weaponStations.Count > 0)
                {
                    ammoSum += unit.GetAmmoLevel();
                    ammoN++;
                }
            }
            _infoClass.text = Loc.F("авиация {0} · наземка {1} · флот {2}", air, ground, naval);
            float hp = n > 0 ? hpSum / n : 1f;
            UiKit.SetProgress(_infoHpFill, hp);
            _infoHpTxt.text = hp >= 0.995f ? Loc.Tr("HP целы") : Loc.F("HP {0}", hp.ToString("P0"));
            // GetAmmoLevel — доли, но у энергостанций Full<Ammo (заряд-единицы) —
            // среднее может быть >1; клэмп, чтобы не показывать «6 666%».
            _infoMove.text = ammoN > 0 ? Loc.F("боезапас {0}", Mathf.Clamp01(ammoSum / ammoN).ToString("P0")) : "";
            _infoState.text = "";
            _infoOrder[0].text = "";
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";

            // Сводка вооружения: чем группа может работать по целям (домен-тег) и
            // сколько зарядов осталось; пустые типы видны сразу — сигнал на перезарядку.
            _infoWeaponsHeader.text = Loc.Tr("Вооружение группы");
            var rows = _groupWeapons.Rows;
            bool overflow = rows.Count > _infoWeapons.Length;
            int shown = overflow ? _infoWeapons.Length - 1 : rows.Count;
            for (int i = 0; i < shown; i++)
                _infoWeapons[i].text = Util.FitLine(Catalog.FormatWeaponRow(rows[i]), FitBodyChars);
            if (overflow)
                _infoWeapons[_infoWeapons.Length - 1].text =
                    Loc.F("+ ещё {0} {1}", rows.Count - shown,
                        Loc.Plural(rows.Count - shown, "тип", "типа", "типов"));
        }

        // ---------- панель осмотра (чужие цели и ракеты, ЛКМ) ----------

        /// <summary>Разведкарточка чужой цели: живой контакт показывает HP/скорость,
        /// протухший — только имя/класс и возраст контакта (данные «из памяти»
        /// не даём — слежка вслепую читерит). Ракета — отдельная карточка.</summary>
        private void RefreshInspected(Unit u)
        {
            if (u is Missile missile)
            {
                RefreshInspectedMissile(missile);
                return;
            }

            GameApi.TryGetLocalHQ(out FactionHQ hq);
            GameApi.IsVisibleToCommander(u, hq, out TrackingInfo info);
            float age = info != null ? Time.timeSinceLevelLoad - info.lastSpottedTime : float.MaxValue;
            bool live = GameApi.IsLiveContact(info); // тот же порог, что у иконок тумана войны

            int hash = 17 * 31 + u.GetInstanceID();
            hash = hash * 31 + (live ? 1 : 0);
            if (hash != _inspHash)
            {
                _inspHash = hash;
                _infoFuelTrack.gameObject.SetActive(false);
                _infoFuelTxt.gameObject.SetActive(false);
                _infoWeaponsHeader.gameObject.SetActive(false);
                for (int i = 0; i < _infoWeapons.Length; i++)
                    _infoWeapons[i].gameObject.SetActive(false);
                // HP чужого показываем только под живым наблюдением.
                _infoHpFill.parent.gameObject.SetActive(live);
                _infoHpTxt.gameObject.SetActive(live);
            }

            string defName = u.definition != null ? u.definition.unitName : "";
            _infoTitle.text = Util.FitLine(
                !string.IsNullOrEmpty(u.unitName) ? u.unitName : defName, FitTitleChars);
            _infoClass.text = Util.FitLine(
                (defName.Length > 0 ? defName + " · " : "") + ClassOf(u) + Loc.Tr(" · чужой контакт"),
                FitBodyChars);

            if (live)
            {
                float hp = GameApi.HpFraction(u);
                UiKit.SetProgress(_infoHpFill, hp);
                _infoHpTxt.text = hp >= 0.995f ? Loc.Tr("HP цел") : Loc.F("HP {0}", hp.ToString("P0"));
                float speedKmh = Mathf.Abs(u.speed) * 3.6f;
                _infoMove.text = Util.FitLine(u is Aircraft
                    ? Loc.F("{0} км/ч · {1} м", speedKmh.ToString("F0"), u.radarAlt.ToString("F0"))
                    : Loc.F("{0} км/ч", speedKmh.ToString("F0")), FitBodyChars);
                _infoState.text = Loc.Tr("под наблюдением");
            }
            else
            {
                _infoMove.text = "";
                _infoState.text = Util.FitLine(Loc.F("контакт {0} с назад", age.ToString("F0")), FitBodyChars);
            }
            _infoOrder[0].text = "";
            _infoOrder[1].text = "";
            _infoOrder[2].text = "";
        }

        /// <summary>Карточка ракеты/бомбы (осмотр ЛКМ): скорость/высота, двигатель,
        /// ГСН и БЧ — данные определения; цель показываем только если она наша
        /// (аналог RWR — чужие цели не подсматриваем).</summary>
        private void RefreshInspectedMissile(Missile m)
        {
            int hash = 17 * 31 + m.GetInstanceID() * 7; // макет ракеты всегда один
            if (hash != _inspHash)
            {
                _inspHash = hash;
                _infoHpFill.parent.gameObject.SetActive(false);
                _infoHpTxt.gameObject.SetActive(false);
                _infoFuelTrack.gameObject.SetActive(false);
                _infoFuelTxt.gameObject.SetActive(false);
                _infoWeaponsHeader.gameObject.SetActive(false);
                for (int i = 0; i < _infoWeapons.Length; i++)
                    _infoWeapons[i].gameObject.SetActive(false);
            }

            GameApi.TryGetLocalHQ(out FactionHQ hq);
            bool ours = m.NetworkHQ != null && m.NetworkHQ == hq;
            bool powered = m.GetThrustDuration() > 0f;

            WeaponInfo wi = m.GetWeaponInfo();
            string name = wi != null && !string.IsNullOrEmpty(wi.shortName)
                ? wi.shortName
                : (wi != null ? wi.weaponName : null);
            if (string.IsNullOrEmpty(name) && m.definition != null)
                name = m.definition.unitName;
            _infoTitle.text = Util.FitLine(
                string.IsNullOrEmpty(name) ? (powered ? Loc.Tr("ракета") : Loc.Tr("бомба")) : name, FitTitleChars);
            _infoClass.text = (powered ? Loc.Tr("ракета") : Loc.Tr("бомба")) + (ours ? Loc.Tr(" · наша") : Loc.Tr(" · чужая"));

            float speedKmh = Mathf.Abs(m.speed) * 3.6f;
            _infoMove.text = Util.FitLine(Loc.F("{0} км/ч · {1} м", speedKmh.ToString("F0"),
                m.transform.position.GlobalY().ToString("F0")), FitBodyChars);
            _infoState.text = powered
                ? (m.EngineOn() ? Loc.Tr("двигатель: работает") : Loc.Tr("двигатель: выгорел"))
                : Loc.Tr("свободное падение");

            // ГСН/БЧ — параметры определения (не секрет); цель — только своя (RWR).
            int w = 0;
            string seeker = m.GetSeekerType();
            if (!string.IsNullOrEmpty(seeker))
                _infoOrder[w++].text = Util.FitLine(Loc.F("ГСН: {0}", seeker), FitBodyChars);
            float yieldKg = m.GetYield();
            if (yieldKg > 0f)
                _infoOrder[w++].text = Util.FitLine(Loc.F("БЧ: {0}", FormatYield(yieldKg)), FitBodyChars);
            if (UnitRegistry.TryGetUnit(m.targetID, out Unit tgt) && tgt != null &&
                tgt.NetworkHQ != null && tgt.NetworkHQ == hq)
                _infoOrder[w++].text = Util.FitLine(Loc.F("ЦЕЛЬ: {0}",
                    tgt.definition != null ? tgt.definition.unitName : tgt.name), FitBodyChars);
            for (; w < _infoOrder.Length; w++)
                _infoOrder[w].text = "";
        }

        /// <summary>Масса БЧ в читаемом виде: до тонны — кг, дальше — килотонны.</summary>
        private static string FormatYield(float kg) =>
            kg >= 1e6f
                ? Loc.F("{0} кт", (kg / 1e6f).ToString("F1"))
                : Loc.F("{0} кг", kg.ToString("F0"));

        private static string ClassOf(Unit u)
        {
            if (u is Aircraft) return Loc.Tr("авиация");
            if (u is Ship) return Loc.Tr("флот");
            if (u is Building) return Loc.Tr("здание");
            if (u.definition is VehicleDefinition vd) return vd.vehicleType.ToString();
            return "";
        }

        /// <summary>Краткое описание активного приказа мода: сегменты плана авиации / цепочка наземки.</summary>
        private static string PlanSummary(Unit u)
        {
            if (u is Aircraft && AircraftOrders.TryGetPlan(u, out IReadOnlyList<AircraftOrders.OrderSegment> plan))
            {
                if (plan.Count == 0)
                    return Loc.Tr("нет");
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < plan.Count && i < 3; i++)
                {
                    if (i > 0)
                        sb.Append(" → ");
                    AircraftOrders.OrderSegment seg = plan[i];
                    sb.Append(seg.Mode switch
                    {
                        AirOrderMode.Patrol => Loc.Tr("Патруль"),
                        AirOrderMode.AttackMove => Loc.Tr("Атака-движ."),
                        AirOrderMode.Attack => Loc.Tr("Атака"),
                        AirOrderMode.Salvo => Loc.Tr("Залп"),
                        AirOrderMode.Escort => Loc.Tr("Сопровождение"),
                        AirOrderMode.Rtb => Loc.Tr("На базу"),
                        _ => Loc.Tr("Транзит")
                    });
                    if (seg.Mode != AirOrderMode.Attack && seg.Points.Count > 1)
                        sb.Append(" ×").Append(seg.Points.Count);
                }
                return sb.ToString();
            }
            if (GroundOrders.KindOf(u) == GroundOrderKind.Attack)
                return Loc.Tr("атака цели");
            if (GroundOrders.KindOf(u) == GroundOrderKind.Escort)
            {
                Unit escortTarget = GroundOrders.EscortTargetOf(u);
                return Loc.F("сопровождение: {0}",
                    escortTarget != null && escortTarget.definition != null
                        ? escortTarget.definition.unitName
                        : "?");
            }
            if (GroundOrders.TryGetQueue(u, out IReadOnlyList<GlobalPosition> queue, out int idx))
            {
                int left = Mathf.Max(0, queue.Count - idx);
                var targets = GroundOrders.TargetQueueOf(u);
                string tail = targets != null ? Loc.F(", атак: {0} в очереди", targets.Count) : "";
                if (GroundOrders.KindOf(u) == GroundOrderKind.Patrol)
                    return Loc.F("патруль, {0} тчк (цикл){1}", left, tail);
                if (GroundOrders.KindOf(u) == GroundOrderKind.AttackMove)
                    return Loc.F("атака-движ., осталось {0} тчк{1}", left, tail);
                return Loc.F("цепочка, осталось {0} тчк{1}", left, tail);
            }
            return Loc.Tr("нет");
        }
    }
}
