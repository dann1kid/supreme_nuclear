using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


namespace SupremeNuclear
{
    // Часть SupcomUi: hover-карточка определения и окно «ОПИСАНИЕ».
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; канвас собирает SupcomUi.cs (BuildSafe).
    public sealed partial class SupcomUi
    {
        /// <summary>Кэш рассчитанной карточки по определению (определения immutable,
        /// обход префаба — один раз; ключ включает индекс лодаута — колесо пересчитывает
        /// вооружение; словарь instance — переживает сцены честно).</summary>
        private readonly Dictionary<(UnitDefinition def, int loadout), TipContent> _tipContent = new();

        /// <summary>Рассчитанный контент карточки определения (SupCom-инфо):
        /// строки для панели свойств и окна описания — считается один раз.</summary>
        private sealed class TipContent
        {
            public string Title;
            public string ClassLine;   // класс/роль · цена · время
            public string LoadoutLine; // «лодаут: …» — только у авиации с выбором
            public readonly List<string> StatRows = new(); // ТТХ: факт-на-строку, без обрезок
            public readonly List<string> Weapons = new();
            public string Desc;        // подрезанная под DefMaxDescLines
            public int DescLines;
        }

        /// <summary>Лимиты однострочных полей инфопанели (ширина ~410 px): заголовок —
        /// шрифт 16, строки — 12; длиннее — «…» (Util.FitLine). Перенос запрещён
        /// (Label singleLine), иначе текст вылезает за подложку панели.</summary>
        private const int FitTitleChars = 36;
        private const int FitBodyChars = 50;
        // ---------- наведение на плитку закупки (карточка определения, SupCom) ----------

        /// <summary>Опрос плиток под курсором (каждый кадр, без uGUI-событий — игровой
        /// input-модуль Enter/Exit не доставляет): курсор внутри плитки И внутри её
        /// ленты-вьюпорта (замаскированные при прокрутке плитки не считаются).
        /// Снесённые плитки выпадают сами (Unity fake-null). Наведение переключает
        /// панель свойств на карточку закупки МГНОВЕННО (v0.87.0; в v0.60.0–v0.85.0
        /// карточка не показывалась вообще — сброс состояния наведения в окне
        /// задержки перезапускал её таймер каждый кадр). КОЛЕСО над плиткой каталога
        /// авиации циклит пресет лодаута (событие ввода — только в кадр прокрутки;
        /// каталог пересобирается по LoadoutsVersion, карточка — ключом кэша).</summary>
        private void UpdateTileHover()
        {
            UnitDefinition hit = null;
            bool hitWheel = false;
            if (Settings.UiTooltips && !RtsInput.Typing && _canvas != null)
            {
                for (int i = _tipTiles.Count - 1; i >= 0; i--)
                {
                    var entry = _tipTiles[i];
                    if (entry.tile == null)
                    {
                        _tipTiles.RemoveAt(i);
                        continue;
                    }
                    if (hit != null || !entry.tile.gameObject.activeInHierarchy)
                        continue;
                    if (RectTransformUtility.RectangleContainsScreenPoint(entry.tile, Input.mousePosition, null) &&
                        (entry.viewport == null ||
                         RectTransformUtility.RectangleContainsScreenPoint(entry.viewport, Input.mousePosition, null)))
                    {
                        hit = entry.def;
                        hitWheel = entry.wheel;
                    }
                }
            }
            if (hit != _hoverShow)
            {
                // Вход/уход с плитки — панель переклеивается немедленно, не ждём
                // очередного 0.2-секундного рефреша.
                _hoverShow = hit;
                _infoHash = -1;
                _nextRefresh = 0f;
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f && hitWheel && hit is AircraftDefinition ad)
            {
                ProductionTasks.CycleLoadout(ad, wheel > 0f ? 1 : -1);
                // Колесо УПТРЕБЛЕНО UI: камера в этом кадре не зумит (PointerOverUi
                // не всегда ловит плитки — подавляем явно).
                RtsInput.WheelConsumedByUi = true;
                _nextRefresh = 0f;   // карточка/каталог — немедленно (оружие по пресету, бейдж плитки)
            }
            // СКМ по плитке каталога авиации — редактор лодаута «ОСНАЩЕНИЕ»
            // (правки станций/топлива = пресет «Свой» в общем цикле колеса).
            if (Input.GetMouseButtonDown(2) && hitWheel && hit is AircraftDefinition adEdit)
            {
                RtsInput.MiddleConsumedByUi = true;   // камера не начинает драг/тап
                ToggleLoadoutEditor(adEdit);
            }
            // Редактор лодаута перерисовывается по версии выбора: колесо по плитке
            // или правки в другом месте меняют выбор — окно не ждёт 0.2-с цикла.
            if (_loWin != null && _loWin.gameObject.activeSelf && _loDef != null &&
                ProductionTasks.LoadoutsVersion != _loVersion)
                RefreshLoadoutEditor();
        }

        /// <summary>Сброс наведения (режим выключен/чат/пересборка канваса):
        /// панель возвращается к выделению на ближайшем рефреше.</summary>
        private void ResetHover()
        {
            if (_hoverShow == null)
                return;
            _hoverShow = null;
            _infoHash = -1;
            _nextRefresh = 0f;
        }

        /// <summary>Контент карточки с кэшем (ключ — определение + индекс лодаута:
        /// колесо даёт новую запись, старые не портятся); сбой расчёта не роняет UI —
        /// показывает что насчиталось (например, только имя).</summary>
        private TipContent GetTipContent(UnitDefinition def, int loadIdx)
        {
            var key = (def, loadIdx);
            if (_tipContent.TryGetValue(key, out TipContent c))
                return c;
            c = new TipContent { Title = def.unitName ?? "?" };
            try
            {
                BuildTipContent(def, c, loadIdx);
            }
            catch (System.Exception e)
            {
                Log.Error("SupcomUi: сбой расчёта карточки закупки (" + (def.unitName ?? "?") +
                          ") — " + e.GetType().Name + ": " + e.Message);
            }
            _tipContent[key] = c;
            return c;
        }

        /// <summary>Расчёт строк карточки по определению: темп фабрики/стройки —
        /// из Settings; вооружение — состав ВЫБРАННОГО ЛОДАУТА у авиации с пресетами
        /// (заряд пилона × пилонов сета, hardpointSets сериализованы на префабе),
        /// иначе дефолтная нагрузка префаба (Weapon-компоненты с info/ammo);
        /// скорость авиации — aircraftInfo (фолбэк — параметры).</summary>
        private void BuildTipContent(UnitDefinition def, TipContent c, int loadIdx)
        {
            bool building = def is BuildingDefinition;
            float seconds = building ? Settings.BuildSecondsPerBuilder : Settings.ProductionSecondsPerItem;
            c.Title = Util.FitLine(def.unitName, FitTitleChars);
            c.ClassLine = Catalog.FormatTipClass(ClassOfDefinition(def), def.value, seconds, building);

            var acc = new WeaponTallyAccumulator();
            acc.BeginUnit();
            Loadout lo = null;
            if (def is AircraftDefinition adef)
            {
                int n = ProductionTasks.LoadoutCount(adef);
                if (n > 1)
                {
                    int i = Mathf.Clamp(loadIdx, 0, n - 1);
                    lo = ProductionTasks.SelectedLoadout(adef, out float selFuel);
                    string name = i == 0 ? Loc.Tr("Стандарт") : LoNameOf(adef, i);
                    // Бак — только когда отличен от полного: выбор виден целиком.
                    string tank = selFuel > 0.05f && selFuel < 0.995f
                        ? Loc.F(" · бак {0}%", Mathf.RoundToInt(selFuel * 100f)) : "";
                    c.LoadoutLine = Loc.F("лодаут: {0}{1} · колесо — смена · СКМ — правка", name, tank);
                }
            }
            if (lo != null)
            {
                HardpointSet[] sets = ProductionTasks.HardpointSetsOf(def as AircraftDefinition);
                List<WeaponMount> ws = lo.weapons;
                for (int i = 0; i < ws.Count; i++)
                {
                    WeaponMount mount = ws[i];
                    WeaponInfo wi = mount != null ? mount.info : null;
                    if (wi == null || wi.hideInDisplay)
                        continue;
                    int mult = 1;
                    if (sets != null && i < sets.Length && sets[i] != null && sets[i].hardpoints != null)
                        mult = Mathf.Max(1, sets[i].hardpoints.Count);
                    int total = Mathf.Max(1, mount.ammo) * mult;
                    string name = !string.IsNullOrEmpty(wi.shortName) ? wi.shortName : wi.weaponName;
                    acc.AddStation(name, total, total,
                        Catalog.WeaponDomains(wi.effectiveness.antiAir, wi.effectiveness.antiSurface,
                            wi.effectiveness.antiMissile, wi.nuclear, wi.jammer));
                }
            }
            else if (def.unitPrefab != null)
                foreach (Weapon w in def.unitPrefab.GetComponentsInChildren<Weapon>(true))
                {
                    WeaponInfo wi = w != null ? w.info : null;
                    if (wi == null || wi.hideInDisplay)
                        continue;
                    string name = !string.IsNullOrEmpty(wi.shortName) ? wi.shortName : wi.weaponName;
                    acc.AddStation(name, w.ammo, w.ammo,
                        Catalog.WeaponDomains(wi.effectiveness.antiAir, wi.effectiveness.antiSurface,
                            wi.effectiveness.antiMissile, wi.nuclear, wi.jammer));
                }
            for (int i = 0; i < acc.Rows.Count; i++)
            {
                if (c.Weapons.Count == DefMaxWeaponRows)
                {
                    int rest = acc.Rows.Count - i;
                    c.Weapons.Add(Loc.F("+ ещё {0} {1}", rest, Loc.Plural(rest, "тип", "типа", "типов")));
                    break;
                }
                c.Weapons.Add(Catalog.FormatTipWeaponRow(acc.Rows[i]));
            }

            float speed = 0f;
            if (def is AircraftDefinition ad)
                speed = ad.aircraftInfo != null ? ad.aircraftInfo.maxSpeed
                    : (ad.aircraftParameters != null ? ad.aircraftParameters.maxSpeed : 0f);
            c.StatRows.AddRange(Catalog.TipStatRows(def.visibleRange, speed,
                def.armorTier, def.manpower, def.mass));

            // Описание: перенос по ширине окна «ОПИСАНИЕ»; длиннее максимума строк —
            // подрезаем хвостами по 24 символа с «…» (только на первом расчёте).
            string desc = def.description ?? "";
            if (desc.Length > 0)
            {
                c.Desc = desc;
                c.DescLines = MeasureLines(c.Desc);
                while (c.DescLines > DefMaxDescLines && c.Desc.Length > 16)
                {
                    c.Desc = c.Desc.Substring(0, System.Math.Max(16, c.Desc.Length - 24)).TrimEnd() + "…";
                    c.DescLines = MeasureLines(c.Desc);
                }
                if (c.DescLines > DefMaxDescLines)
                    c.DescLines = DefMaxDescLines;
            }
        }

        /// <summary>Число строк переноса текста по ширине окна «ОПИСАНИЕ»
        /// (шрифт/настройки живого лейбла); пустой текст или нет лейбла — 0.</summary>
        private int MeasureLines(string text)
        {
            if (string.IsNullOrEmpty(text) || _descText == null || _descText.font == null)
                return 0;
            var gen = _descText.cachedTextGenerator;
            gen.Populate(text, _descText.GetGenerationSettings(new Vector2(DescTextWidth, 4096f)));
            return System.Math.Max(1, gen.lineCount);
        }

        /// <summary>Класс определения для карточки: тип наземки/флота/роль авиации.
        /// v0.97: рядом расшифровка бейджа плитки «(ТНК)/(ЭМ)/(И)» — сокращения
        /// каталога читаются без шпаргалки; корабли раньше падали в «здание».</summary>
        private static string ClassOfDefinition(UnitDefinition def)
        {
            if (def is AircraftDefinition)
                return Loc.F("авиация · {0} ({1})",
                    Catalog.AirRoleName(def.roleIdentity, def.typeIdentity),
                    Catalog.AirBadge(def.roleIdentity, def.typeIdentity));
            if (def is ShipDefinition sea)
                return Loc.F("флот · {0} ({1})",
                    Catalog.TypeNameOf(sea.shipType), Catalog.BadgeOf(sea.shipType));
            if (def is VehicleDefinition vd)
                return Loc.F("наземка · {0} ({1})",
                    Catalog.TypeNameOf(vd.vehicleType), Catalog.BadgeOf(vd.vehicleType));
            return Loc.Tr("здание");
        }
    }
}
