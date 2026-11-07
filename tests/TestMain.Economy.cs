using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    // Часть TestMain: каталог и экономика.
    internal static partial class TestMain
    {
        // каталог, прогресс оплаты, потоки экономики, инженеры, профиль (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsCatalogEconomy()
        {
            // ---------- Catalog.ShortLabel: короткая подпись плитки ----------
            Check("Подпись: код приоритетен", Catalog.ShortLabel("F-16C", "Fighting Falcon") == "F-16C");
            Check("Подпись: нет кода — первое слово имени",
                Catalog.ShortLabel("", "Extractor Mk2") == "Extractor");
            Check("Подпись: длинное слово обрезается до 10",
                Catalog.ShortLabel(null, "Бронированный автомобиль").Length == 10);
            Check("Подпись: пусто — ?", Catalog.ShortLabel(null, null) == "?");

            // ---------- Catalog.BadgeOf/TypeNameOf: бейджи типов наземки ----------
            Check("Бейдж: танк", Catalog.BadgeOf(VehicleType.MBT) == "ТНК");
            Check("Бейдж: ПЗРК", Catalog.BadgeOf(VehicleType.IR_SAM) == "ПЗРК");
            Check("Бейдж: ЗРК", Catalog.BadgeOf(VehicleType.R_SAM) == "ЗРК");
            Check("Бейдж: все типы непустые", Enum.GetValues(typeof(VehicleType))
                .Cast<VehicleType>().All(t => !string.IsNullOrEmpty(Catalog.BadgeOf(t))));
            Check("Бейдж: имя типа есть у всех", Enum.GetValues(typeof(VehicleType))
                .Cast<VehicleType>().All(t => !string.IsNullOrEmpty(Catalog.TypeNameOf(t))));

            // ---------- Catalog.AirBadge/AirRoleName: роль авиации ----------
            RoleIdentity Fighter() => new RoleIdentity { antiAir = 1f, antiSurface = 0.1f };
            RoleIdentity Strike() => new RoleIdentity { antiAir = 0.05f, antiSurface = 0.9f };
            RoleIdentity Multi() => new RoleIdentity { antiAir = 0.7f, antiSurface = 0.6f };
            RoleIdentity Heli() => new RoleIdentity { antiAir = 0.1f, antiSurface = 0.1f };
            var asType = new TypeIdentity { surface = 1f, air = 0f };
            var radarType = new TypeIdentity { radar = 1f };
            Check("Роль: истребитель", Catalog.AirBadge(Fighter(), asType) == "И");
            Check("Роль: ударный", Catalog.AirBadge(Strike(), asType) == "У");
            Check("Роль: многоролевой", Catalog.AirBadge(Multi(), asType) == "М");
            Check("Роль: слабый боец — транспорт", Catalog.AirBadge(Heli(), asType) == "ТР");
            Check("Роль: слабый боец-радар — ДРЛО", Catalog.AirBadge(Heli(), radarType) == "РЛС");
            Check("Роль: полное имя", Catalog.AirRoleName(Fighter(), asType) == "истребитель");

            // ---------- BuildTasks.Task.Progress01: доля оплаты ----------
            var task = new BuildTasks.Task { Cost = 250f, PaidMass = 100f };
            Check("Progress01: 100/250 = 0.4", Math.Abs(task.Progress01 - 0.4f) < 0.001f);
            task.PaidMass = 999f;
            Check("Progress01: кламп сверху", Math.Abs(task.Progress01 - 1f) < 0.001f);
            task.PaidMass = -5f;
            Check("Progress01: кламп снизу", Math.Abs(task.Progress01 - 0f) < 0.001f);

            // ---------- EcoFlow.DrainStep: налив двух валют с дефицитом ----------
            var full = EcoFlow.DrainStep(10f, 5f, 100f, 100f);
            Check("DrainStep: хватает всего", full.Mass == 10f && full.Energy == 5f);
            var massStarved = EcoFlow.DrainStep(10f, 5f, 4f, 100f);
            Check("DrainStep: дефицит массы режет обе",
                Math.Abs(massStarved.Mass - 4f) < 1e-4f && Math.Abs(massStarved.Energy - 2f) < 1e-4f);
            var eStarved = EcoFlow.DrainStep(10f, 5f, 100f, 1f);
            Check("DrainStep: дефицит энергии режет обе",
                Math.Abs(eStarved.Mass - 2f) < 1e-4f && Math.Abs(eStarved.Energy - 1f) < 1e-4f);
            var nothing = EcoFlow.DrainStep(10f, 5f, 0f, 0f);
            Check("DrainStep: пустые хранилища — нули", nothing.Mass == 0f && nothing.Energy == 0f);
            var noWant = EcoFlow.DrainStep(0f, 0f, 0f, 0f);
            Check("DrainStep: нет запроса — нули", noWant.Mass == 0f && noWant.Energy == 0f);
            var neg = EcoFlow.DrainStep(10f, 5f, -50f, 100f);
            Check("DrainStep: отрицательный доступ — кламп 0", neg.Mass == 0f && neg.Energy == 0f);

            // ---------- EcoFlow.AddCapped: сток энергии с капом ----------
            Check("AddCapped: обычное пополнение", Math.Abs(EcoFlow.AddCapped(10f, 5f, 100f) - 15f) < 1e-4f);
            Check("AddCapped: перелив до капа", Math.Abs(EcoFlow.AddCapped(90f, 50f, 100f) - 100f) < 1e-4f);
            Check("AddCapped: отрицательный доход игнорируется",
                Math.Abs(EcoFlow.AddCapped(10f, -5f, 100f) - 10f) < 1e-4f);
            Check("AddCapped: нулевой кап", EcoFlow.AddCapped(10f, 5f, 0f) == 0f);

            // ---------- EcoFlow.ServiceCost: наценка за ремонт (DESIGN §3-10) ----------
            Check("ServiceCost: целый борт — база",
                Math.Abs(EcoFlow.ServiceCost(150f, 1f, 500f, 0.25f) - 150f) < 1e-4f);
            Check("ServiceCost: половина урона",
                Math.Abs(EcoFlow.ServiceCost(150f, 0.5f, 500f, 0.25f) - 212.5f) < 1e-3f);
            Check("ServiceCost: hp01 клампится сверху",
                Math.Abs(EcoFlow.ServiceCost(150f, 2f, 500f, 0.25f) - 150f) < 1e-4f);

            // ---------- EcoFlow.RepairCost: масса за ремонт (канон SupCom, v0.149) ----------
            Check("RepairCost: полное восстановление = фактор × цену",
                Math.Abs(EcoFlow.RepairCost(400f, 0f, 1f, 0.5f) - 200f) < 1e-4f);
            Check("RepairCost: половина урона",
                Math.Abs(EcoFlow.RepairCost(400f, 0.5f, 1f, 0.5f) - 100f) < 1e-4f);
            Check("RepairCost: целый — ноль",
                EcoFlow.RepairCost(400f, 1f, 1f, 0.5f) == 0f);
            Check("RepairCost: отрицательная доля — ноль",
                EcoFlow.RepairCost(400f, 0.8f, 0.5f, 0.5f) == 0f);
            Check("RepairCost: кламп to01 сверху",
                Math.Abs(EcoFlow.RepairCost(400f, 0f, 1.5f, 0.5f) - 200f) < 1e-4f);
            Check("RepairCost: ценность ≤ 0 — базис 1",
                Math.Abs(EcoFlow.RepairCost(0f, 0f, 1f, 0.5f) - 0.5f) < 1e-4f);
            Check("RepairCost: нулевой фактор — бесплатно",
                EcoFlow.RepairCost(400f, 0f, 1f, 0f) == 0f);

            // ---------- EngineerService: спеки канона v2 (FAF 5/13/32.5, юзер 2026-11-04) ----------
            var eng2 = new EngineerService.TierSpec();
            var eng3 = new EngineerService.TierSpec { priceRatio = 6f, factor = 6.5f, hpRatio = 5.33f, code = "ИНЖ3" };
            Check("Инженеры v2: T2 отношения канона",
                Math.Abs(eng2.priceRatio - 2.5f) < 1e-4f && Math.Abs(eng2.factor - 2.6f) < 1e-4f &&
                Math.Abs(eng2.hpRatio - 2.67f) < 1e-4f);
            Check("Инженеры v2: T3 отношения канона",
                Math.Abs(eng3.priceRatio - 6f) < 1e-4f && Math.Abs(eng3.factor - 6.5f) < 1e-4f &&
                Math.Abs(eng3.hpRatio - 5.33f) < 1e-4f);
            Check("Инженеры v2: цены от базы 180 = 450/1080",
                Math.Abs(180f * eng2.priceRatio - 450f) < 1e-4f &&
                Math.Abs(180f * eng3.priceRatio - 1080f) < 1e-4f);

            // ---------- EngineerService.LegacyCustomized: детектор конфига v1 ----------
            const string legacyDefault = "{\"tier2\":{\"price\":450.0,\"factor\":2.0,\"code\":\"ИНЖ2\"}," +
                                         "\"tier3\":{\"price\":1100.0,\"factor\":4.0,\"code\":\"ИНЖ3\"}}";
            const string legacyCustom = "{\"tier2\":{\"price\":500.0,\"factor\":3.0,\"code\":\"ИНЖ2\"}," +
                                        "\"tier3\":{\"price\":1100.0,\"factor\":4.0,\"code\":\"ИНЖ3\"}}";
            const string v2Config = "{\"version\":2,\"tier2\":{\"priceRatio\":2.5,\"factor\":2.6}}";
            Check("Инженеры: дефолтный v1 — не кастомный",
                !EngineerService.LegacyCustomized(legacyDefault));
            Check("Инженеры: кастомный v1 — детект",
                EngineerService.LegacyCustomized(legacyCustom));
            Check("Инженеры: v2 и мусор — не кастомные",
                !EngineerService.LegacyCustomized(v2Config) && !EngineerService.LegacyCustomized("{не json"));

            // ---------- EconomyProfile v2: потоки в секунду, апгрейды наливом ----------
            var profile = new EconomyProfile();
            Check("Rate v2: T1/T2/T3 в секунду",
                profile.Rate(1) == 2.5f && profile.Rate(2) == 7f && profile.Rate(3) == 17f);
            Check("Rate v2: кламп тира", profile.Rate(0) == 2.5f && profile.Rate(99) == 17f);
            profile.incomePerSecond = new[] { 10f };
            Check("Rate v2: один тир", profile.Rate(3) == 10f);
            profile.incomePerSecond = null;
            profile.incomePerMinute = new[] { 150f };
            Check("Rate v2: старый формат минут конвертируется", Math.Abs(profile.Rate(1) - 2.5f) < 1e-4f);
            var profile2 = new EconomyProfile();
            Check("Апгрейд v2: цены налива массы",
                profile2.NextUpgradeMass(1) == 800f && profile2.NextUpgradeMass(2) == 2400f);
            Check("Апгрейд v2: максимальный тир — нет цены", profile2.NextUpgradeMass(3) == -1f);
            Check("Апгрейд v2: цены энергии и время",
                profile2.NextUpgradeEnergy(1) == 400f && profile2.NextUpgradeSeconds(1) == 15f);
        }
    }
}
