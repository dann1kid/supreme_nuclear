using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    // Часть TestMain: локализация, вооружение в UI.
    internal static partial class TestMain
    {
        // локализация и подгонка строк (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsLocAndFitLine()
        {
            // ---------- RuPlural: русская плюрализация ----------
            Check("Plural: 1/2/5 носителей",
                Util.RuPlural(1, "носитель", "носителя", "носителей") == "носитель" &&
                Util.RuPlural(2, "носитель", "носителя", "носителей") == "носителя" &&
                Util.RuPlural(5, "носитель", "носителя", "носителей") == "носителей");
            Check("Plural: 11–14 всегда «много», 21 — «один»",
                Util.RuPlural(11, "тип", "типа", "типов") == "типов" &&
                Util.RuPlural(14, "тип", "типа", "типов") == "типов" &&
                Util.RuPlural(21, "тип", "типа", "типов") == "тип" &&
                Util.RuPlural(112, "тип", "типа", "типов") == "типов");

            // ---------- Loc: локализация (gettext: ключ = русская исходная строка) ----------
            int locVersion;
            Loc.Install("ru", "{\"носитель\": \"carrier\"}", null);
            Check("Loc: ru — тождество даже при наличии en-таблицы (фолбэк не включается)",
                Loc.Tr("патруль") == "патруль" && Loc.Tr("носитель") == "носитель" &&
                Loc.Language == "ru");
            Check("Loc: ru — плюрализация исходным русским правилом",
                Loc.Plural(2, "носитель", "носителя", "носителей") == "носителя" &&
                Loc.Plural(11, "тип", "типа", "типов") == "типов");
            Loc.Install("en",
                "{\"движение\": \"Move\", \"носов|носка|носков\": \"pair|pairs\", " +
                "\"готово: {0}\": \"done: {0}\", \"_plural\": \"en\"}", null);
            Check("Loc: en — перевод по таблице, пропуски — исходником",
                Loc.Tr("движение") == "Move" && Loc.Tr("нет перевода") == "нет перевода" &&
                Loc.Language == "en");
            Check("Loc: en — две формы (one/other)",
                Loc.Plural(1, "носов", "носка", "носков") == "pair" &&
                Loc.Plural(3, "носов", "носка", "носков") == "pairs");
            Check("Loc: форматная строка переводится целиком",
                Loc.F("готово: {0}", 5) == "done: 5");
            Loc.Install("fr",
                "{\"движение\": \"mouvement\"}",
                "{\"движение\": \"mouvement\", \"залп\": \"salve\", \"_plural\": \"en\"}");
            Check("Loc: третья локаль — своя таблица, фолбэк en, глубже — исходник",
                Loc.Tr("движение") == "mouvement" && Loc.Tr("залп") == "salve" &&
                Loc.Tr("стоп") == "стоп");
            locVersion = Loc.Version;
            Loc.Install("ru", null, null);
            Check("Loc: сброс в ru — состояние тестов не течёт",
                Loc.Tr("движение") == "движение" && Loc.Language == "ru");
            Check("Loc: Install бампит Version — живая смена языка перестраивает UI (v0.153)",
                Loc.Version == locVersion + 1);

            // ---------- FitLine: однострочная подгонка под ширину панели ----------
            Check("FitLine: короткая строка без изменений",
                Util.FitLine("авиация", 48) == "авиация" &&
                Util.FitLine("авиация", 7) == "авиация");
            Check("FitLine: длинная обрезается с «…» ровно до maxChars",
                Util.FitLine("Приказ: Патруль ×12 → Атака-движ. ×3 → На базу", 20).Length == 20 &&
                Util.FitLine("Приказ: Патруль ×12 → Атака-движ. ×3 → На базу", 20).EndsWith("…"));
            Check("FitLine: пустая/null и maxChars<1",
                Util.FitLine(null, 48) == "" && Util.FitLine("", 48) == "" &&
                Util.FitLine("любой текст", 0) == "");
        }
        // гейты/сводки вооружения, домены, hover-карточки (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsWeaponsInfo()
        {
            // ---------- Гейты вооружения: классификация станции ----------
            Check("Класс: ядерное главнее вида (Я-бомба/Я-ракета → Nuclear)",
                Catalog.ClassOf(true, true, false, false, false, 0f, 0f) == WeaponClass.Nuclear &&
                Catalog.ClassOf(true, false, false, false, true, 0f, 1f) == WeaponClass.Nuclear);
            Check("Класс: бомбы/планирующие → Bomb",
                Catalog.ClassOf(false, true, false, false, false, 0f, 1f) == WeaponClass.Bomb &&
                Catalog.ClassOf(false, false, true, false, false, 0f, 1f) == WeaponClass.Bomb);
            Check("Класс: пушки → Gun (даже ПР/двухдоменные)",
                Catalog.ClassOf(false, false, false, true, false, 1f, 1f) == WeaponClass.Gun &&
                Catalog.ClassOf(false, false, false, true, false, 0f, 0f) == WeaponClass.Gun);
            Check("Класс: ракеты по домену (ВВ/АС/мультидомен/без домена)",
                Catalog.ClassOf(false, false, false, false, true, 0.9f, 0.1f) == WeaponClass.MissileAA &&
                Catalog.ClassOf(false, false, false, false, true, 0.1f, 0.9f) == WeaponClass.MissileAS &&
                Catalog.ClassOf(false, false, false, false, true, 0.5f, 0.5f) == WeaponClass.MissileAS &&
                Catalog.ClassOf(false, false, false, false, true, 0f, 0f) == WeaponClass.MissileAS);
            Check("Класс: лейблы всех классов",
                Catalog.ClassLabel(WeaponClass.Nuclear) == "ЯД" &&
                Catalog.ClassLabel(WeaponClass.Bomb) == "Бомбы" &&
                Catalog.ClassLabel(WeaponClass.Gun) == "Пушки" &&
                Catalog.ClassLabel(WeaponClass.MissileAA) == "Рак.ВВ" &&
                Catalog.ClassLabel(WeaponClass.MissileAS) == "Рак.АС");

            // ---------- Правила ТИПОВ вооружения: цикл/агрегация (v0.106) ----------
            Check("Тип-правило: RuleFor — целевые состояния одинаковы для платформ",
                Catalog.RuleFor(TypeGateState.None, true) == TargetDomain.Both &&
                Catalog.RuleFor(TypeGateState.None, false) == TargetDomain.Both &&
                Catalog.RuleFor(TypeGateState.SurfaceOnly, true) == TargetDomain.Surface &&
                Catalog.RuleFor(TypeGateState.SurfaceOnly, false) == TargetDomain.Surface &&
                Catalog.RuleFor(TypeGateState.AirOnly, true) == TargetDomain.Air &&
                Catalog.RuleFor(TypeGateState.Off, false) == TargetDomain.None);
            Check("Тип-правило: RuleFor — платформенные пресеты гасят чужую платформу",
                Catalog.RuleFor(TypeGateState.GroundLaunch, true) == TargetDomain.None &&
                Catalog.RuleFor(TypeGateState.GroundLaunch, false) == TargetDomain.Both &&
                Catalog.RuleFor(TypeGateState.AirLaunch, true) == TargetDomain.Both &&
                Catalog.RuleFor(TypeGateState.AirLaunch, false) == TargetDomain.None);
            Check("Тип-правило: агрегат — пусто и единые состояния",
                Catalog.AggregateState(new List<(bool, TargetDomain)>()) == TypeGateState.None &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (false, TargetDomain.Surface) }) == TypeGateState.SurfaceOnly &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.Air), (false, TargetDomain.Air) }) == TypeGateState.AirOnly &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.None), (true, TargetDomain.None) }) == TypeGateState.Off);
            Check("Тип-правило: агрегат — платформенные пресеты и вперемешку",
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.None), (false, TargetDomain.Both) }) == TypeGateState.GroundLaunch &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.Both), (false, TargetDomain.None) }) == TypeGateState.AirLaunch &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (true, TargetDomain.Air), (false, TargetDomain.Surface) }) == null &&
                Catalog.AggregateState(new List<(bool, TargetDomain)> { (false, TargetDomain.Both), (false, TargetDomain.Surface) }) == null);
            Check("Тип-правило: цикл 6 состояний в обе стороны, вперемешку = с начала",
                Catalog.Next(TypeGateState.None, true) == TypeGateState.SurfaceOnly &&
                Catalog.Next(TypeGateState.SurfaceOnly, true) == TypeGateState.AirOnly &&
                Catalog.Next(TypeGateState.AirOnly, true) == TypeGateState.GroundLaunch &&
                Catalog.Next(TypeGateState.GroundLaunch, true) == TypeGateState.AirLaunch &&
                Catalog.Next(TypeGateState.AirLaunch, true) == TypeGateState.Off &&
                Catalog.Next(TypeGateState.Off, true) == TypeGateState.None &&
                Catalog.Next(TypeGateState.None, false) == TypeGateState.Off &&
                Catalog.Next(null, true) == TypeGateState.SurfaceOnly);
            Check("Тип-правило: суффиксы чипа (Latin-1)",
                Catalog.TypeGateSuffix(TypeGateState.None) == "" &&
                Catalog.TypeGateSuffix(TypeGateState.SurfaceOnly) == "·Н" &&
                Catalog.TypeGateSuffix(TypeGateState.AirOnly) == "·В" &&
                Catalog.TypeGateSuffix(TypeGateState.GroundLaunch) == "·з" &&
                Catalog.TypeGateSuffix(TypeGateState.AirLaunch) == "·в" &&
                Catalog.TypeGateSuffix(TypeGateState.Off) == "×" &&
                Catalog.TypeGateSuffix(null) == "±");

            // ---------- Переводы состояний AI (инфопанель) ----------
            Check("Состояния пилота: известные переводятся",
                Catalog.RuPilotState("Bombing") == "бомбометание" &&
                Catalog.RuPilotState("Evading Missiles") == "уклонение от ракет" &&
                Catalog.RuPilotState("taking off") == "взлёт" &&
                Catalog.RuPilotState("parked") == "на стоянке");
            Check("Состояния: неизвестное — как есть, пустое — пусто",
                Catalog.RuPilotState("Final Approach Overshoot") == "Final Approach Overshoot" &&
                Catalog.RuPilotState("") == "" && Catalog.RuPilotState(null) == "" &&
                Catalog.RuShipState("attacking") == "атака" &&
                Catalog.RuShipState("docked") == "у причала" &&
                Catalog.RuShipState("weird") == "weird");

            // ---------- Сводка вооружения группы (инфопанель) ----------
            var guns = new WeaponTallyAccumulator();
            guns.BeginUnit(); // борт 1: две станции AIM-120 + бомбы
            guns.AddStation("AIM-120C", 4, 4, WeaponDomain.Air);
            guns.AddStation("AIM-120C", 2, 2, WeaponDomain.Air);
            guns.AddStation("Mk-82", 4, 6, WeaponDomain.Surface);
            guns.BeginUnit(); // борт 2: одна станция AIM-120 (расстреляна) + пушка
            guns.AddStation("AIM-120C", 0, 4, WeaponDomain.Air);
            guns.AddStation("M61", 480, 500, WeaponDomain.Air | WeaponDomain.Surface);
            Check("Сводка: станции одного имени сливаются (заряд/ёмкость суммируются)",
                guns.Rows.Count == 3 &&
                guns.Rows[0].Name == "AIM-120C" && guns.Rows[0].Ammo == 6 && guns.Rows[0].Full == 10);
            Check("Сводка: носители считаются по юнитам, не по станциям",
                guns.Rows[0].Carriers == 2 && guns.Rows[1].Carriers == 1 && guns.Rows[2].Carriers == 1);
            Check("Сводка: порядок первого появления сохраняется",
                guns.Rows[0].Name == "AIM-120C" && guns.Rows[1].Name == "Mk-82" &&
                guns.Rows[2].Name == "M61");
            Check("Сводка: домены станций OR-ятся в маску",
                guns.Rows[2].Domains == (WeaponDomain.Air | WeaponDomain.Surface));

            var acc2 = new WeaponTallyAccumulator();
            acc2.BeginUnit();
            acc2.AddStation("", 4, 4, WeaponDomain.Air);
            acc2.AddStation("Пустышка", 1, 0, WeaponDomain.Air);
            acc2.AddStation("AIM-9", 2, 2, WeaponDomain.Air);
            Check("Сводка: null/пустое имя и нулевая ёмкость игнорируются",
                acc2.Rows.Count == 1 && acc2.Rows[0].Name == "AIM-9");
            acc2.Clear();
            Check("Сводка: Clear сбрасывает всё", acc2.Rows.Count == 0);
            acc2.BeginUnit();
            acc2.AddStation("AIM-9", 1, 2, WeaponDomain.Air);
            Check("Сводка: после Clear счёт носителей с нуля", acc2.Rows[0].Carriers == 1);

            // ---------- Домен-теги оружия ----------
            Check("Теги: АА/АЗ/У по эффективности",
                Catalog.DomainTag(WeaponDomain.Air) == "АА" &&
                Catalog.DomainTag(WeaponDomain.Surface) == "АЗ" &&
                Catalog.DomainTag(WeaponDomain.Air | WeaponDomain.Surface) == "У");
            Check("Теги: противоракетное/РЭБ/ядерный префикс/пусто",
                Catalog.DomainTag(WeaponDomain.Missile) == "ПР" &&
                Catalog.DomainTag(WeaponDomain.Jammer) == "РЕБ" &&
                Catalog.DomainTag(WeaponDomain.Nuclear | WeaponDomain.Surface) == "Я АЗ" &&
                Catalog.DomainTag(WeaponDomain.None) == "");
            Check("Теги: порог эффективности — как у игры (> 0)",
                Catalog.WeaponDomains(0.01f, 0f, 0f, false, false) == WeaponDomain.Air &&
                Catalog.WeaponDomains(0f, 0.5f, 0f, true, false) ==
                    (WeaponDomain.Surface | WeaponDomain.Nuclear) &&
                Catalog.WeaponDomains(0f, 0f, 0f, false, false) == WeaponDomain.None);

            // ---------- Формат строки сводки ----------
            Check("Строка: «АА AIM-120C: 6/10 · 2 носителя»",
                Catalog.FormatWeaponRow(guns.Rows[0]) == "АА  AIM-120C: 6/10 · 2 носителя",
                Catalog.FormatWeaponRow(guns.Rows[0]));
            Check("Строка: без тега — имя сразу, ядерная — префикс",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "B61", Ammo = 1, Full = 2, Carriers = 1,
                    Domains = WeaponDomain.Nuclear | WeaponDomain.Surface
                }) == "Я АЗ B61: 1/2 · 1 носитель",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "B61", Ammo = 1, Full = 2, Carriers = 1,
                    Domains = WeaponDomain.Nuclear | WeaponDomain.Surface
                }));
            Check("Строка: расстрелянный тип помечается «пусто»",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "Mk-82", Ammo = 0, Full = 6, Carriers = 1, Domains = WeaponDomain.Surface
                }).EndsWith("· пусто"));

            // ---------- Hover-карточка определения (форматтеры SupCom-инфо) ----------
            Check("Карточка: класс · цена · время производства",
                Catalog.FormatTipClass("истребитель", 150f, 12f, false) == "истребитель · 150 · ~12 с");
            Check("Карточка: стройка помечена, время инженера",
                Catalog.FormatTipClass("здание", 300f, 20f, true) == "здание · 300 · стройка ~20 с");
            Check("Карточка: без времени — только класс и цена",
                Catalog.FormatTipClass("танк", 45f, 0f, false) == "танк · 45");
            Check("Карточка: ТТХ факт-на-строку (скорость/обзор/броня·экипаж·масса)",
                string.Join("|", Catalog.TipStatRows(8000f, 250f, 2f, 1f, 9500f)) ==
                "скорость 250 м/с|обзор 8 км|броня 2 · экипаж 1 · масса ≈9.5 т",
                string.Join("|", Catalog.TipStatRows(8000f, 250f, 2f, 1f, 9500f)));
            Check("Карточка: обзор до километра — в метрах",
                Catalog.TipStatRows(800f, 0f, 0f, 0f, 0f).Count == 1 &&
                Catalog.TipStatRows(800f, 0f, 0f, 0f, 0f)[0] == "обзор 800 м");
            Check("Карточка: десятые км — инвариантная точка (детерминизм)",
                Catalog.TipStatRows(9500f, 0f, 0f, 0f, 0f)[0] == "обзор 9.5 км",
                Catalog.TipStatRows(9500f, 0f, 0f, 0f, 0f)[0]);
            Check("Карточка: нули — пустой список",
                Catalog.TipStatRows(0f, 0f, 0f, 0f, 0f).Count == 0);
            Check("Карточка: строка оружия — тег + ×боезапас (нагрузка всегда полная)",
                Catalog.FormatTipWeaponRow(new WeaponTally
                {
                    Name = "AIM-120C", Ammo = 4, Full = 4, Carriers = 1, Domains = WeaponDomain.Air
                }) == "АА  AIM-120C ×4",
                Catalog.FormatTipWeaponRow(new WeaponTally
                {
                    Name = "AIM-120C", Ammo = 4, Full = 4, Carriers = 1, Domains = WeaponDomain.Air
                }));
            Check("Карточка: РЭБ-тег у постановщика помех",
                Catalog.FormatTipWeaponRow(new WeaponTally
                {
                    Name = "AN/ALQ-131", Ammo = 1, Full = 1, Carriers = 1, Domains = WeaponDomain.Jammer
                }) == "РЕБ AN/ALQ-131 ×1");
        }
    }
}
