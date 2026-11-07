using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


namespace SupremeNuclear
{
    // Часть SupcomUi: сборка канваса и панелей (верхняя панель, информация, командная карта, вкладки).
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; канвас собирает SupcomUi.cs (BuildSafe).
    public sealed partial class SupcomUi
    {
        private void BuildTopBar()
        {
            var bar = Panel("TopBar", _canvas.transform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(12f, -108f), new Vector2(620f, -56f), PanelBg);

            var f = Label(bar, "funds", "0", 30, Accent, TextAnchor.MiddleLeft);
            _fundsText = f;
            var rtF = (RectTransform)f.transform;
            rtF.anchorMin = new Vector2(0f, 0f);
            rtF.anchorMax = new Vector2(0.45f, 1f);
            rtF.offsetMin = new Vector2(14f, 0f);
            rtF.offsetMax = Vector2.zero;

            _incomeText = Label(bar, "income", Loc.Tr("+0 −0/с"), 15, TextMain, TextAnchor.MiddleLeft);
            var rtI = (RectTransform)_incomeText.transform;
            rtI.anchorMin = new Vector2(0.45f, 0.5f);
            rtI.anchorMax = new Vector2(1f, 1f);
            rtI.offsetMin = new Vector2(6f, 0f);
            rtI.offsetMax = new Vector2(-10f, 0f);

            _extractorsText = Label(bar, "energy", string.Empty, 15, TextDim, TextAnchor.MiddleLeft);
            var rtE = (RectTransform)_extractorsText.transform;
            rtE.anchorMin = new Vector2(0.45f, 0f);
            rtE.anchorMax = new Vector2(1f, 0.5f);
            rtE.offsetMin = new Vector2(6f, 0f);
            rtE.offsetMax = new Vector2(-10f, 0f);
        }

        /// <summary>Строка панели от верхнего края: якоря (0..1, 1), отступы 12/8.</summary>
        private static void Row(RectTransform rt, float fromTop, float toTop)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(12f, -toTop);
            rt.offsetMax = new Vector2(-8f, -fromTop);
        }

        private void BuildSelectionInfo()
        {
            // Адаптив v0.71.2: заголовок зоны «ВЫДЕЛЕНИЕ», шапка (имя/класс/полосы)
            // фиксирована, строки тела ПОТОЧНО раскладываются FlowInfoBody — пустые
            // пропускаются, высота панели под контент (низ двигает LayoutCommandArea).
            var panel = Panel("SelectionInfo", _canvas.transform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(12f, 130f), new Vector2(430f, 232f), PanelBg);
            _infoPanel = panel;

            var hdr = Label(panel, "hdr", Loc.Tr("ВЫДЕЛЕНИЕ"), 10, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)hdr.transform, 2f, 14f);
            _infoZoneHdr = hdr;
            _infoTitle = Label(panel, "title", "", 16, TextMain, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoTitle.transform, 15f, 31f);
            _infoClass = Label(panel, "class", "", 12, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoClass.transform, 32f, 46f);

            _infoHpFill = UiKit.ProgressBar(panel, "hp", new Color(0f, 0f, 0f, 0.35f),
                new Color(0.4f, 1f, 0.5f, 0.9f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -62f), new Vector2(-96f, -48f));
            _infoHpTxt = Label(panel, "hptxt", "", 12, TextDim, TextAnchor.MiddleRight, true);
            var rtH = (RectTransform)_infoHpTxt.transform;
            rtH.anchorMin = rtH.anchorMax = new Vector2(1f, 1f);
            rtH.pivot = new Vector2(1f, 1f);
            rtH.offsetMin = new Vector2(-84f, -64f);
            rtH.offsetMax = new Vector2(-8f, -46f);

            _infoFuelFill = UiKit.ProgressBar(panel, "fuel", new Color(0f, 0f, 0f, 0.35f),
                new Color(0.95f, 0.75f, 0.3f, 0.9f),
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(12f, -78f), new Vector2(-96f, -64f));
            _infoFuelTrack = (RectTransform)_infoFuelFill.parent;
            _infoFuelTxt = Label(panel, "fueltxt", "", 12, TextDim, TextAnchor.MiddleRight, true);
            var rtF = (RectTransform)_infoFuelTxt.transform;
            rtF.anchorMin = rtF.anchorMax = new Vector2(1f, 1f);
            rtF.pivot = new Vector2(1f, 1f);
            rtF.offsetMin = new Vector2(-84f, -80f);
            rtF.offsetMax = new Vector2(-8f, -62f);

            // Тело карточки (скорость/состояние/приказ/вооружение) — позиции раздаёт
            // FlowInfoBody при каждом обновлении; стартовые Row — любые безопасные.
            _infoMove = Label(panel, "move", "", 12, TextMain, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoMove.transform, 82f, 98f);
            _infoState = Label(panel, "state", "", 12, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoState.transform, 98f, 114f);
            for (int i = 0; i < _infoOrder.Length; i++)
            {
                _infoOrder[i] = Label(panel, "order" + i, "", 12, TextMain, TextAnchor.UpperLeft, true);
                Row((RectTransform)_infoOrder[i].transform, 114f + 16f * i, 130f + 16f * i);
            }

            _infoWeaponsHeader = Label(panel, "whdr", Loc.Tr("Вооружение"), 12, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)_infoWeaponsHeader.transform, 166f, 180f);
            for (int i = 0; i < _infoWeapons.Length; i++)
            {
                _infoWeapons[i] = Label(panel, "w" + i, "", 12, TextMain, TextAnchor.UpperLeft, true);
                Row((RectTransform)_infoWeapons[i].transform, 180f + 16f * i, 196f + 16f * i);
            }
        }

        /// <summary>Окно «ОПИСАНИЕ» — второе окно карточки закупки (SupCom-раскладка):
        /// многострочный перенос текста определения; виден только при наведении на
        /// плитку закупки, высота под контент, позицию в стеке левой колонки
        /// задаёт ApplyCommandLayout (командная карта → ОПИСАНИЕ → ВЫДЕЛЕНИЕ).</summary>
        private void BuildDefDesc()
        {
            var panel = Panel("DefDesc", _canvas.transform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(12f, 140f), new Vector2(430f, 240f), PanelBg);
            _descPanel = panel;

            var hdr = Label(panel, "hdr", Loc.Tr("ОПИСАНИЕ"), 10, TextDim, TextAnchor.UpperLeft, true);
            Row((RectTransform)hdr.transform, 2f, 16f);

            // Единственный многострочный текст левой колонки: перенос по ширине,
            // по вертикали обрезается (длина уже подрезана расчётом строк).
            _descText = Label(panel, "desc", "", 12, TextDim, TextAnchor.UpperLeft);
            var rt = (RectTransform)_descText.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(12f, -200f);
            rt.offsetMax = new Vector2(-8f, -18f);
            _descText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _descText.verticalOverflow = VerticalWrapMode.Truncate;
            _descText.raycastTarget = false;

            panel.gameObject.SetActive(false);
        }

        /// <summary>Поточная раскладка тела инфопанели: АКТИВНЫЕ непустые строки
        /// получают следующую позицию (шаг 16), пустые пропускаются — без дыр.
        /// Высота панели поджимается под контент (низ задаёт LayoutCommandArea).
        /// fuel=true — полоса топлива занимает свою строку шапки.</summary>
        private void FlowInfoBody(bool fuel)
        {
            float y = fuel ? 82f : 66f;
            PlaceInfoRow(ref y, _infoMove);
            PlaceInfoRow(ref y, _infoState);
            for (int i = 0; i < _infoOrder.Length; i++)
                PlaceInfoRow(ref y, _infoOrder[i]);
            PlaceInfoRow(ref y, _infoWeaponsHeader);
            for (int i = 0; i < _infoWeapons.Length; i++)
                PlaceInfoRow(ref y, _infoWeapons[i]);
            if (_infoPanel != null)
            {
                float bottom = ((RectTransform)_infoPanel.transform).offsetMin.y;
                _infoPanel.offsetMax = new Vector2(430f, bottom + y + 4f);
            }
        }

        private static void PlaceInfoRow(ref float y, Text t)
        {
            if (t == null || !t.gameObject.activeSelf || string.IsNullOrEmpty(t.text))
                return;
            Row((RectTransform)t.transform, y, y + 16f);
            y += 16f;
        }

        private void BuildCommandCard()
        {
            // Слева-внизу, под панелью свойств выделения (SupCom-раскладка):
            // верх — подсказка режима, ниже — сетка команд. Компакт v0.64.0: 112 px.
            var panel = Panel("CommandCard", _canvas.transform,
                new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(12f, 12f), new Vector2(430f, 124f), PanelBg);
            _commandPanel = panel;

            _modeHint = Label(panel, "hint", "", 12, TextDim, TextAnchor.UpperLeft, true);
            var rtH = (RectTransform)_modeHint.transform;
            rtH.anchorMin = new Vector2(0f, 1f);
            rtH.anchorMax = new Vector2(1f, 1f);
            rtH.offsetMin = new Vector2(10f, -18f);
            rtH.offsetMax = new Vector2(-10f, -2f);

            // Заголовок секции приказов: помечает ЧТО за зона; когда подсказка
            // активного режима скрыта — поднимается к верхнему краю (без дыры).
            _ordersHdr = Label(panel, "ordershdr", Loc.Tr("ПРИКАЗЫ"), 10, TextDim, TextAnchor.UpperLeft, true);
            var rtO = (RectTransform)_ordersHdr.transform;
            rtO.anchorMin = new Vector2(0f, 1f);
            rtO.anchorMax = new Vector2(1f, 1f);
            rtO.offsetMin = new Vector2(10f, -31f);
            rtO.offsetMax = new Vector2(-8f, -19f);

            // Заголовок секции гейтов — виден только с полосой (управляет активностью
            // BuildCommandButtons); высота секции меняется (классы / классы+типы),
            // офсеты выставляет BuildCommandButtons.
            _gateHdr = Label(panel, "gatehdr", Loc.Tr("РАЗРЕШЁННОЕ ВООРУЖЕНИЕ · ПКМ по ракетному чипу — залп этим"), 10, TextDim,
                TextAnchor.UpperLeft, true);
            var rtG = (RectTransform)_gateHdr.transform;
            rtG.anchorMin = new Vector2(0f, 0f);
            rtG.anchorMax = new Vector2(1f, 0f);
            rtG.offsetMin = new Vector2(10f, 51f);
            rtG.offsetMax = new Vector2(-8f, 64f);
            _gateHdr.gameObject.SetActive(false);

            // Секция гейтов вооружения — внизу карточки, два ряда: ТИПЫ (нижний,
            // v0.106) и КЛАССЫ; сетка команд поднимается над ними.
            _gateTypes = Panel("gatetypes", panel, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(8f, 3f), new Vector2(-8f, 25f), new Color(0f, 0f, 0f, 0f));
            var tlayout = _gateTypes.gameObject.AddComponent<HorizontalLayoutGroup>();
            tlayout.spacing = 4f;
            tlayout.childControlWidth = false;
            tlayout.childControlHeight = false;
            tlayout.childForceExpandWidth = false;
            tlayout.childForceExpandHeight = false;
            tlayout.childAlignment = TextAnchor.MiddleLeft;
            _gateTypes.gameObject.SetActive(false);

            _gateStrip = Panel("gates", panel, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(8f, 27f), new Vector2(-8f, 49f), new Color(0f, 0f, 0f, 0f));
            var glayout = _gateStrip.gameObject.AddComponent<HorizontalLayoutGroup>();
            glayout.spacing = 4f;
            glayout.childControlWidth = false;
            glayout.childControlHeight = false;
            glayout.childForceExpandWidth = false;
            glayout.childForceExpandHeight = false;
            glayout.childAlignment = TextAnchor.MiddleLeft;

            var grid = Panel("grid", panel, Vector2.zero, Vector2.one,
                new Vector2(8f, 29f), new Vector2(-8f, -33f), new Color(0f, 0f, 0f, 0f));
            _cmdGrid = grid;
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(98f, 26f);
            layout.spacing = new Vector2(6f, 4f);
            layout.padding = new RectOffset(2, 0, 2, 0);
            layout.startCorner = GridLayoutGroup.Corner.UpperLeft;
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = 4;
        }

        private void BuildTabPanel()
        {
            string step = "panel";
            try
            {
            BuildTabPanelSafe();
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: сбой этапа tabs, шаг '" + step + "' — " + e.GetType().Name +
                          ": " + e.Message + " | " + e.StackTrace);
            }
        }

        private void BuildTabPanelSafe()
        {
            string step = "panel";
            try
            {
            // Схема (DESIGN 2.10, уточнение игрока): вкладки-иконки СЛЕВА переключают
            // контент ДВУХ строк-лент; сверху полоса пула (тиры каталога); у каждой строки
            // свои кнопки листания у краёв. Контекст-колонки паузы/цикла/шаблонов — этап B
            // (появляются вместе с очередями фабрик). Компакт v0.64.0: панель 134 px
            // (верх на 146 px от низа), плитки 48, ленты 52, поля 6.
            var panel = Panel("TabPanel", _canvas.transform,
                new Vector2(0.34f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 12f), new Vector2(-12f, 146f), PanelBg);
            _tabPanel = panel;

            var tabs = Panel("tabs", panel, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(4f, 4f), new Vector2(38f, -4f), new Color(0f, 0f, 0f, 0.15f));
            var vlayout = tabs.gameObject.AddComponent<VerticalLayoutGroup>();
            vlayout.spacing = 3f;
            vlayout.padding = new RectOffset(2, 2, 2, 2);
            vlayout.childControlWidth = false;
            vlayout.childControlHeight = false;
            vlayout.childForceExpandWidth = false;
            vlayout.childForceExpandHeight = false;
            vlayout.childAlignment = TextAnchor.UpperCenter;

            string[] glyphs = { "$", "#", "^", Loc.Tr("Ш") };
            for (int i = 0; i < _tabButtons.Length; i++)
            {
                int index = i;
                Button b = Button_(tabs, "tab" + i, glyphs[i], () => SwitchTab(index), 14);
                var brt = (RectTransform)b.transform;
                brt.sizeDelta = new Vector2(28f, 26f);
                var le = b.gameObject.AddComponent<LayoutElement>();
                le.minWidth = 28f;
                le.minHeight = 26f;
                le.preferredWidth = 28f;
                le.preferredHeight = 26f;
                _tabButtons[i] = (b, b.GetComponent<Image>());
            }

step = "title";
            _contextTitle = Label(panel, "title", "", 12, TextDim, TextAnchor.MiddleRight);
            var rtT = (RectTransform)_contextTitle.transform;
            rtT.anchorMin = new Vector2(0.5f, 1f);
            rtT.anchorMax = new Vector2(1f, 1f);
            rtT.offsetMin = new Vector2(10f, -20f);
            rtT.offsetMax = new Vector2(-8f, -2f);

            // Полоса пула (тиры каталога) — видна только на вкладке «Производство».
step = "tiers";
            _tierRoot = Panel("tiers", panel, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(172f, -20f), new Vector2(560f, -2f), new Color(0f, 0f, 0f, 0f));
            var tlayout = _tierRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
            tlayout.spacing = 4f;
            tlayout.childControlWidth = false;
            tlayout.childControlHeight = false;
            tlayout.childForceExpandWidth = false;
            tlayout.childForceExpandHeight = false;
            tlayout.childAlignment = TextAnchor.MiddleLeft;
            string[] tiers = { Loc.Tr("Все"), "I", "II", "III" };
            for (int i = 0; i < _tierButtons.Length; i++)
            {
                int tier = i;
                Button b = Button_(_tierRoot, "tier" + i, tiers[i], () => SwitchTier(tier), 11);
                ((RectTransform)b.transform).sizeDelta = new Vector2(34f, 18f);
                b.gameObject.AddComponent<LayoutElement>().minWidth = 34f;
                _tierButtons[i] = (b, b.GetComponent<Image>(), i);
            }
            // Две строки-ленты; по 56 px с каждого края — их кнопки листания.
step = "strip1";
            _strip1 = UiKit.HStrip(panel, "strip1",
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(172f, -74f), new Vector2(-168f, -22f), out _strip1Content);
step = "strip2";
            _strip2 = UiKit.HStrip(panel, "strip2",
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(172f, -131f), new Vector2(-168f, -79f), out _strip2Content);

step = "arrows";
            UiKit.StripArrows(panel, _font, _strip1Content, 54f, true,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(112f, -74f), new Vector2(168f, -22f));
            UiKit.StripArrows(panel, _font, _strip1Content, 54f, false,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-168f, -74f), new Vector2(-112f, -22f));
            _strip2Nav = new GameObject("strip2nav", typeof(RectTransform));
            var navRoot = (RectTransform)_strip2Nav.transform;
            navRoot.SetParent(panel, false);
            navRoot.anchorMin = Vector2.zero;
            navRoot.anchorMax = Vector2.one;
            navRoot.offsetMin = navRoot.offsetMax = Vector2.zero;
            UiKit.StripArrows(navRoot, _font, _strip2Content, 54f, true,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(112f, -131f), new Vector2(168f, -79f));
            UiKit.StripArrows(navRoot, _font, _strip2Content, 54f, false,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-168f, -131f), new Vector2(-112f, -79f));
            _strip2Nav.SetActive(false);

            // Контекстные кнопки фабрики (2.10): Пауза и Цикл — у правого края нижней строки.
            // Кнопки 22 px с интервалом 3: до пяти в колонке (Пауза/Цикл/Шаблон+ +
            // тумблеры аэродрома с ангара) — 122 px, колонка до -144 (низ панели).
            var ctxGo = new GameObject("factoryCtx", typeof(RectTransform));
            var ctxRt = (RectTransform)ctxGo.transform;
            ctxRt.SetParent(panel, false);
            ctxRt.anchorMin = new Vector2(0f, 1f);
            ctxRt.anchorMax = new Vector2(0f, 1f);
            ctxRt.offsetMin = new Vector2(44f, -144f);
            ctxRt.offsetMax = new Vector2(108f, -22f);
            var ctxLayout = ctxGo.AddComponent<VerticalLayoutGroup>();
            ctxLayout.spacing = 3f;
            ctxLayout.childControlWidth = false;
            ctxLayout.childControlHeight = false;
            ctxLayout.childForceExpandWidth = false;
            ctxLayout.childForceExpandHeight = false;
            _pauseBtn = Button_(ctxGo.transform, "pause", Loc.Tr("Пауза"), () =>
            {
                // Один тумблер на ВСЁ подходящее выделение (v0.151): фабрики
                // (очереди) + инженеры (налив). Все на паузе ↔ все сняты —
                // унификация состояния v0.100, общая для обоих семейств.
                bool all = AllPausedUnified();
                ProductionTasks.TogglePauseAll(PauseCycleTargets());
                PauseService.SetPaused(SelectedEngineers(), !all);
            }, 10);
            ((RectTransform)_pauseBtn.transform).sizeDelta = new Vector2(60f, 22f);
            _pauseBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            _cycleBtn = Button_(ctxGo.transform, "cycle", Loc.Tr("Цикл"), () =>
            {
                ProductionTasks.ToggleCycleAll(PauseCycleTargets());
            }, 10);
            ((RectTransform)_cycleBtn.transform).sizeDelta = new Vector2(60f, 22f);
            _cycleBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            _tplBtn = Button_(ctxGo.transform, "tpl", Loc.Tr("Шаблон+"), () =>
            {
                var controller = CommanderModeController.Instance;
                // На вкладке «Ш» приоритет — набор выделенных зданий, затем очереди.
                var buildingSet = new List<(string key, float value, Vector3 pos)>();
                if (controller != null)
                    foreach (Unit unit in controller.Selection.Selected)
                        if (unit is Building && unit.definition != null)
                        {
                            Vector3 p = unit.transform.position;
                            buildingSet.Add((unit.definition.jsonKey, unit.definition.value, p));
                        }
                OrderTemplates.Template tpl = null;
                if (buildingSet.Count > 0)
                    tpl = OrderTemplates.CaptureBuildings(buildingSet);
                else if (CtxFactory() != null)
                    tpl = OrderTemplates.Capture(CtxFactory());
                else if (controller != null)
                {
                    // Очередь площадок инженера — с относительными позициями площадок.
                    // Только своей фракции: AI строит тем же BuildTasks (v0.143.8).
                    GameApi.TryGetLocalHQ(out FactionHQ tplHq);
                    var set = new List<(string key, float value, Vector3 pos)>();
                    foreach (BuildTasks.Task t in BuildTasks.All)
                        if (t.Definition != null && t.Faction == tplHq)
                        {
                            Vector3 lp = t.Site.ToLocalPosition();
                            set.Add((t.Definition.jsonKey, t.Definition.value, lp));
                        }
                    tpl = set.Count > 0 ? OrderTemplates.CaptureBuildings(set) : null;
                }
                CommanderHud.Notice(tpl != null
                    ? Loc.F("Шаблон сохранён: {0}", tpl.name)
                    : Loc.Tr("Нечего сохранять (набор/очередь пусты)"));
            }, 10);
            ((RectTransform)_tplBtn.transform).sizeDelta = new Vector2(60f, 22f);
            _tplBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            _noLandBtn = Button_(ctxGo.transform, "noLand", Loc.Tr("Без посадки"), () =>
            {
                if (_ctxAirbase != null)
                    AirbaseAccess.SetLanding(_ctxAirbase, !AirbaseAccess.IsLandingOpen(_ctxAirbase));
            }, 9);
            ((RectTransform)_noLandBtn.transform).sizeDelta = new Vector2(62f, 22f);
            _noLandBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            _noTakeoffBtn = Button_(ctxGo.transform, "noTakeoff", Loc.Tr("Без взлёта"), () =>
            {
                if (_ctxAirbase != null)
                    AirbaseAccess.SetTakeoff(_ctxAirbase, !AirbaseAccess.IsTakeoffOpen(_ctxAirbase));
            }, 9);
            ((RectTransform)_noTakeoffBtn.transform).sizeDelta = new Vector2(62f, 22f);
            _noTakeoffBtn.gameObject.AddComponent<LayoutElement>().minHeight = 22f;
            ctxGo.SetActive(false);
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: tabs step '" + step + "' — " + e.GetType().Name + ": " + e.Message);
                throw;
            }
        }
    }
}
