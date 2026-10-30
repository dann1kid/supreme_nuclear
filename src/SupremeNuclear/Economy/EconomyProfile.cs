using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Внешний балансный профиль экономики v2: все числа здесь, не в коде.
    /// Файл: BepInEx/plugins/SupremeNuclear/Economy/&lt;Profile&gt;.json.
    /// deposits (опционально) — ручная разметка месторождений на карту; без неё — авторазметка.
    /// Потоки заданы в секунду: масса экстракторов, энергия генераторов; апгрейд — наливом.
    /// Профиль v1 читается без потерь: incomePerMinute конвертируется, новые поля берут дефолты.
    /// Формат сознательно совпадает с будущим выходом генератора карт.
    /// </summary>
    public sealed class EconomyProfile
    {
        public sealed class Deposit
        {
            public float x;
            public float z;
            public int tier = 1;
            public string kind = "mass"; // mass | coal (ручная разметка стоковых карт)
        }

        public List<Deposit> deposits;
        public int depositCount = 12;
        public float depositSpacing = 4000f;

        // Поток массы экстракторов по тирам (в секунду).
        public float[] incomePerSecond = { 2.5f, 7f, 17f };
        // Устаревшее поле v1 (доход в минуту): используется, только если incomePerSecond пуст.
        public float[] incomePerMinute;

        // Энергия (DESIGN §2.2): сток с капом, доход и капу дают генераторы.
        // Старт/кап — интерим «командирского вливания» SupCom (ACU ~10/с и большой запас
        // на первый завод + пачку T1-генов): базовых 1500 хватает на бутстрап (плейтест
        // 2026-10-29), дальше кормят генераторы.
        public float energyStart = 1500f;
        public float energyBaseIncome = 1.5f;     // фоновая генерация (интерим командирского борта)
        public float energyBaseCapacity = 2000f;
        public float generatorIncome = 6f;          // +энергии/с с одного генератора
        public float generatorCapacityBonus = 250f; // +к стоку за генератор
        public string[] generatorJsonKeys = { "power", "generator", "plant" };
        public float energyCostFactor = 0.5f;       // энергозатраты задачи = масс-налив × фактор

        // Месторождения v3 (SupCom-канон, юзер 2026-10-30): точка = маркер-«недра»;
        // экстрактор строится ТОЛЬКО на точке массы, генератор где угодно, но на
        // угольной точке — буст. Захват — инженерный, энергозатратный.
        public float coalIncomePerSecond = 12f;   // энергия/с угольного генератора
        public float extractorBuildCost = 300f;   // цены клонов-построек (Т1-терциль каталога)
        public float hydroBuildCost = 400f;
        public float generatorBuildCost = 150f;
        public float coalCapacityBonus = 600f;    // +кап стока за угольный генератор
        public float siteSnapRadiusM = 150f;      // радиус снапа призрака к точке
        public float captureRadiusM = 140f;       // инженер в этом радиусе захватывает
        public float captureSeconds = 10f;        // база: один инженер
        public float captureEnergyPerSecond = 3f; // энергии/с на инженера при захвате

        // Апгрейд экстрактора — потоковый налив: переход T→T+1 стоит [T] массы/энергии.
        public float[] tierUpgradeMass = { 0f, 800f, 2400f };
        public float[] tierUpgradeEnergy = { 0f, 400f, 1200f };
        public float[] tierUpgradeSeconds = { 0f, 15f, 25f };

        // Салваж обломков (простейший формат DESIGN §3-5): ценность обломка фикс —
        // у нативного Wreckage нет связи с ценой погибшего юнита.
        public float salvageValue = 80f;
        public float salvageSeconds = 8f;   // база: один инженер; несколько — пропорционально быстрее
        public float salvageRadiusM = 120f;

        public static EconomyProfile Load(string name)
        {
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Economy");
            string path = Path.Combine(dir, name + ".json");
            try
            {
                if (File.Exists(path))
                {
                    var profile = JsonConvert.DeserializeObject<EconomyProfile>(File.ReadAllText(path));
                    if (profile != null)
                    {
                        Log.Info("Economy: профиль загружен — " + path);
                        return profile;
                    }
                }

                var fallback = new EconomyProfile();
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(fallback, Formatting.Indented));
                Log.Info("Economy: профиль не найден, создан образец с дефолтами — " + path);
                return fallback;
            }
            catch (System.Exception e)
            {
                Log.Error("Economy: ошибка профиля " + path + ": " + e.Message + " — использую дефолты");
                return new EconomyProfile();
            }
        }

        /// <summary>Число тиров экономики (по массиву дохода).</summary>
        public int TierCount => incomePerSecond != null && incomePerSecond.Length > 0
            ? incomePerSecond.Length
            : (incomePerMinute != null && incomePerMinute.Length > 0 ? incomePerMinute.Length : 1);

        /// <summary>Доход экстрактора тира (масса в секунду).</summary>
        public float Rate(int tier)
        {
            if (incomePerSecond != null && incomePerSecond.Length > 0)
                return incomePerSecond[Mathf.Clamp(tier, 1, incomePerSecond.Length) - 1];
            if (incomePerMinute != null && incomePerMinute.Length > 0)
                return incomePerMinute[Mathf.Clamp(tier, 1, incomePerMinute.Length) - 1] / 60f;
            return 2.5f;
        }

        /// <summary>Цена апгрейда T→T+1 (валюта из массива); -1 — тира выше нет.</summary>
        public float NextUpgradeMass(int tier) => NextTierValue(tierUpgradeMass, tier);

        public float NextUpgradeEnergy(int tier) => NextTierValue(tierUpgradeEnergy, tier);

        /// <summary>Длительность налива апгрейда T→T+1 (секунды); 0.1 при отсутствии данных.</summary>
        public float NextUpgradeSeconds(int tier) =>
            tier >= 1 && tier < TierCount ? Mathf.Max(0.1f, At(tierUpgradeSeconds, tier, 15f)) : 0.1f;

        private float NextTierValue(float[] arr, int tier) =>
            arr != null && tier >= 1 && tier < arr.Length && tier < TierCount ? arr[tier] : -1f;

        private static float At(float[] arr, int idx, float fallback) =>
            arr != null && idx >= 0 && idx < arr.Length ? arr[idx] : fallback;
    }
}
