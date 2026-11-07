using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


namespace SupremeNuclear
{
    // Часть SupcomUi: окно «ОСНАЩЕНИЕ» — редактор лодаута заказа.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; канвас собирает SupcomUi.cs (BuildSafe).
    public sealed partial class SupcomUi
    {
        // ---------- окно «ОСНАЩЕНИЕ» (редактор лодаута, СКМ по плитке каталога) ----------

        /// <summary>Открыть/закрыть редактор лодаута борта (СКМ по плитке каталога
        /// авиации). Живёт в плавающем окне до закрытия; правки станций/топлива
        /// = пресет «Свой», встающий в общий цикл колеса.</summary>
        private void ToggleLoadoutEditor(AircraftDefinition def)
        {
            if (_loWin != null && _loWin.gameObject.activeSelf && _loDef == def)
            {
                _loWin.gameObject.SetActive(false);
                return;
            }
            if (ProductionTasks.HardpointSetsOf(def) == null)
            {
                CommanderHud.Notice(Loc.F("Нет данных о станциях: {0}",
                                    Catalog.ShortLabel(def.code, def.unitName)));
                return;
            }
            _loDef = def;
            _loVersion = -1;
            if (_loWin == null)
            {
                // Над панелью закупки (низ-центр, правее левой колонки); окно
                // перетаскиваемо и самовосстанавливается в границах канваса.
                var crt = (RectTransform)_canvas.transform;
                float x = Mathf.Clamp(crt.rect.width * 0.5f - 60f, 460f, crt.rect.width - 420f);
                _loWin = FloatingWindow.Create(_canvas.transform, _font, Loc.Tr("ОСНАЩЕНИЕ"),
                    new Vector2(x, 168f), new Vector2(404f, 330f), null);
            }
            if (!_loWin.gameObject.activeSelf)
                _loWin.gameObject.SetActive(true);
            RefreshLoadoutEditor();
        }

        /// <summary>Полная перерисовка контента редактора (открытие, клики, колесо —
        /// всё через смену LoadoutsVersion/прямой вызов). Контент —_fact-на-строку,
        /// правки применяются к заказам сразу (сохранять нечего: состояние липкое).</summary>
        private void RefreshLoadoutEditor()
        {
            if (_loWin == null || _loWin.Content == null || _loDef == null)
                return;
            _loVersion = ProductionTasks.LoadoutsVersion;
            AircraftDefinition def = _loDef;
            HardpointSet[] sets = ProductionTasks.HardpointSetsOf(def);
            GameManager.GetLocalHQ(out FactionHQ hq);
            Airbase ab = _ctxAirbase != null ? _ctxAirbase
                : (_ctxHangar != null ? _ctxHangar.parentAirbase : null);

            ClearChildren(_loWin.Content);
            var content = _loWin.Content;

            // Шапка: превью борта + имя/класс + масса пустого борта
            var icon = Icon(content, IconOf(def), new Vector2(84f, 84f));
            UnitPreview.Bind(icon, def); // 84px портрет живёт дольше цикла рефрешей
            var irt = (RectTransform)icon.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0f, 1f);
            irt.pivot = new Vector2(0f, 1f);
            irt.anchoredPosition = new Vector2(10f, -6f);
            icon.preserveAspect = true;

            var name = Label(content, "name", Util.FitLine(def.unitName, 34), 16, TextMain,
                TextAnchor.UpperLeft, true);
            PlaceLo(name.transform, 104f, 4f, 300f, 22f);
            var cls = Label(content, "cls", Loc.F("{0} · цена {1}", ClassOfDefinition(def), def.value.ToString("F0")),
                12, TextDim, TextAnchor.UpperLeft, true);
            PlaceLo(cls.transform, 104f, 26f, 300f, 18f);
            string emptyMass = def.mass > 1000f
                ? Loc.F("масса пустого ≈ {0} т", (def.mass / 1000f).ToString("0.#",
                      System.Globalization.CultureInfo.InvariantCulture))
                : def.mass > 0f ? Loc.F("масса ≈ {0} кг", def.mass.ToString("0")) : "";
            var mass = Label(content, "mass", emptyMass, 12, TextDim, TextAnchor.UpperLeft, true);
            PlaceLo(mass.transform, 104f, 44f, 300f, 18f);
            int sel = ProductionTasks.LoadoutIndexOf(def);
            var selLo = ProductionTasks.SelectedLoadout(def, out float selFuel);
            string fuelTxt = selFuel > 0.995f ? "100%" : Mathf.RoundToInt(selFuel * 100f) + "%";
            var cur = Label(content, "cur", Loc.F("выбрано: {0} · топливо {1}", LoNameOf(def, sel), fuelTxt),
                13, Accent, TextAnchor.UpperLeft, true);
            PlaceLo(cur.transform, 104f, 64f, 300f, 18f);

            // Пресеты-кнопки: Стандарт → авторские → «Свои N»; «+» — новый «Свой»
            // (клон текущего выбора), «−» — удалить выбранный «Свой».
            float y = 96f;
            var phdr = Label(content, "phdr", Loc.Tr("Пресеты (клик — выбрать):"), 11, TextDim,
                TextAnchor.UpperLeft, true);
            PlaceLo(phdr.transform, 10f, y, 300f, 14f);
            y += 16f;
            float x = 10f;
            for (int i = 0; i < ProductionTasks.LoadoutCount(def); i++)
            {
                int idx = i;
                var b = Button_(content, "preset" + i, LoNameOf(def, i), () =>
                {
                    ProductionTasks.SelectLoadoutIndex(def, idx);
                    RefreshLoadoutEditor();
                }, 11);
                PaintLoButton(b, x, y, 84f, 24f, i == sel);
                x += 88f;
                if (x > 290f)
                {
                    x = 10f;
                    y += 28f;
                }
            }
            var plus = Button_(content, "plus", "+", () =>
            {
                ProductionTasks.AddCustom(def);
                _nextRefresh = 0f;
                RefreshLoadoutEditor();
            }, 14);
            PaintLoButton(plus, x, y, 26f, 24f, false);
            x += 30f;
            if (ProductionTasks.SelectedCustom(def) != null)
            {
                var minus = Button_(content, "minus", "−", () =>
                {
                    ProductionTasks.RemoveSelectedCustom(def);
                    _nextRefresh = 0f;
                    RefreshLoadoutEditor();
                }, 14);
                PaintLoButton(minus, x, y, 26f, 24f, false);
            }

            // Топливо (чипы — для «Своего»; у пресета топливо зашито автором)
            y += 30f;
            bool customSel = ProductionTasks.SelectedCustom(def) != null;
            var fl = Label(content, "fuel", Loc.Tr("Топливо:"), 11, TextDim, TextAnchor.UpperLeft, true);
            PlaceLo(fl.transform, 10f, y, 60f, 14f);
            x = 66f;
            foreach (float f in new[] { 0.5f, 0.75f, 1f })
            {
                float val = f;
                var b = Button_(content, "fuel" + f, Mathf.RoundToInt(f * 100f) + "%", () =>
                {
                    if (ProductionTasks.HasCustomLoadout(def))
                        ProductionTasks.SetCustomFuel(def, val);
                    RefreshLoadoutEditor();
                }, 11);
                PaintLoButton(b, x, y - 4f, 46f, 22f, customSel &&
                    Mathf.Abs(selFuel - val) < 0.01f);
                x += 50f;
            }
            if (!customSel)
            {
                var note = Label(content, "fnote", Loc.Tr("у пресета — авторское"), 10, TextDim,
                    TextAnchor.UpperLeft, true);
                PlaceLo(note.transform, x + 4f, y, 150f, 14f);
            }

            // Станции: клик — следующий легальный вариант (правки = «Свой»)
            y += 26f;
            var shdr = Label(content, "shdr", Loc.Tr("Станции (клик — сменить вооружение):"), 11, TextDim,
                TextAnchor.UpperLeft, true);
            PlaceLo(shdr.transform, 10f, y, 380f, 14f);
            y += 16f;
            for (int i = 0; i < sets.Length; i++)
            {
                int setIdx = i;
                HardpointSet set = sets[i];
                int pylons = set != null && set.hardpoints != null ? set.hardpoints.Count : 1;
                string setName = set != null && !string.IsNullOrEmpty(set.name)
                    ? set.name : Loc.F("Ст.{0}", i + 1);
                // Отображение: маунт выбранного лодаута, а при «Стандарте» —
                // заводской из дефолт-лодаута игры (внутренние пушки и т.п.).
                WeaponMount m = selLo != null && i < selLo.weapons.Count ? selLo.weapons[i]
                    : ProductionTasks.DefaultMountOf(def, i);
                string mount = m != null && m.info != null
                    ? (!string.IsNullOrEmpty(m.info.shortName) ? m.info.shortName : m.info.weaponName)
                      + " ×" + Mathf.Max(1, m.ammo) * pylons
                    : Loc.Tr("пусто");
                if (!ProductionTasks.StationConfigurable(def, i, ab, hq))
                {
                    // Настроек нет (пустые options и ничего не висит) — надпись, не кнопка
                    var lbl = Label(content, "st" + i,
                        setName + " ×" + pylons + " · " + Loc.Tr("нет вариантов"),
                        11, TextDim, TextAnchor.MiddleLeft, true);
                    PlaceLo(lbl.transform, 10f, y, 384f, 24f);
                    y += 27f;
                    continue;
                }
                var b = Button_(content, "st" + i, setName + " ×" + pylons + " · " + mount, () =>
                {
                    ProductionTasks.CycleStation(def, setIdx, 1, ab, hq);
                    _nextRefresh = 0f;
                    RefreshLoadoutEditor();
                }, 11);
                PaintLoButton(b, 10f, y, 384f, 24f, false);
                y += 27f;
            }

            // Итог: масса подвесок + легальность на текущем аэродроме
            float susp = 0f;
            if (selLo != null)
                for (int i = 0; i < selLo.weapons.Count; i++)
                {
                    WeaponMount m = selLo.weapons[i];
                    if (m == null || m.info == null)
                        continue;
                    int pylons = sets != null && i < sets.Length && sets[i] != null &&
                                 sets[i].hardpoints != null ? sets[i].hardpoints.Count : 1;
                    susp += Mathf.Max(1, m.ammo) * m.info.massPerRound * pylons;
                }
            bool allowed = ProductionTasks.LoadoutAllowed(def, selLo, hq);
            string massLine = (susp >= 1000f
                ? Loc.F("+≈{0} т подвесок", (susp / 1000f).ToString("0.#",
                      System.Globalization.CultureInfo.InvariantCulture))
                : susp > 0f ? Loc.F("+≈{0} кг подвесок", susp.ToString("0")) : Loc.Tr("без подвесок"))
                + (selLo == null ? Loc.Tr(" (стандарт)") : "")
                + Loc.F(" · на этом аэродроме: {0}", allowed ? Loc.Tr("легально") : Loc.Tr("НЕДОСТУПНО"));
            var it = Label(content, "itogo", massLine, 11, allowed ? TextDim : new Color(1f, 0.5f, 0.4f, 1f),
                TextAnchor.UpperLeft, true);
            PlaceLo(it.transform, 10f, y, 384f, 16f);
            y += 20f;

            _loWin.Root.sizeDelta = new Vector2(404f, Mathf.Max(240f, y + 8f));
            _loWin.ClampToParent();
        }

        /// <summary>Имя варианта по индексу: Стандарт / пресет / «Свой N».</summary>
        private static string LoNameOf(AircraftDefinition def, int idx)
        {
            if (idx <= 0)
                return Loc.Tr("Стандарт");
            var presets = ProductionTasks.UsablePresets(def);
            if (idx - 1 < presets.Count)
            {
                string n = presets[idx - 1].Name;
                return string.IsNullOrEmpty(n) ? "#" + idx : n;
            }
            return ProductionTasks.CustomName + " " + (idx - presets.Count);
        }

        private void PlaceLo(Transform t, float x, float y, float w, float h)
        {
            var rt = (RectTransform)t;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private void PaintLoButton(Button b, float x, float y, float w, float h, bool selected)
        {
            PlaceLo(b.transform, x, y, w, h);
            var img = b.GetComponent<Image>();
            if (img != null)
                img.color = selected ? BtnActive : BtnBg;
        }
    }
}
