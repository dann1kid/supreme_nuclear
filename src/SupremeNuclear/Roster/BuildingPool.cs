using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Запись пула строений: роль → клон донора. Пул = БЕЛЫЙ СПИСОК
    /// каталога стройки (юзер-канон 2026-11-03 «пул по тирам, роли явно, без
    /// костылей»): не вошедшее в пул нативное здание в меню НЕ попадает
    /// (на нативных картах/миссиях всё как было). Клон создаётся один раз за
    /// запуск (Encyclopedia переживает сцены, EngineerService-паттерн).
    /// Флаги поведения — на РОЛИ, не на имена/подстроки: heliport — VTOL-площадка
    /// (без рулёжки/сети/сервиса). Остальное поведение — по компонентам префаба
    /// (Hangar/VehicleDepot/WarheadStorage — сериализованы, это тип, не костыль).</summary>
    public sealed class PoolSpec
    {
        public string role = "";     // ключ роли (mes/hyd/gen/depot/hangar/…)
        public string donor = "";    // jsonKey донора из Encyclopedia.buildings
        public string name = "";     // "" = имя донора (после ростера)
        public string code = "";     // "" = role.ToUpper()
        public int tier = 1;         // тир каталога (гейт инженера)
        public float price;          // 0 = нативная (эконом-роли проставит профиль)
        public bool heliport;        // вертолётная площадка: рулёжку не строить
        public bool factory;         // фабрика: пуласт T2/T3-варианты «дешёвая
                                     // ветка напрямую» (юзер-канон 2026-11-04:
                                     // инженер нужного тира + живой HQ типа)
    }

    /// <summary>
    /// Пул строений мода (v0.148): единая фабрика клонов вместо разрозненных
    /// костылей (донорские подмены экономики по подстрокам, детект вертолётности
    /// по имени, SetFactory нативных Factory). Каталог стройки SupcomUi итерирует
    /// ТОЛЬКО пул; EconomyService берёт мекс/гидро/ген из ролей; BuildTasks
    /// читает флаги поведения из спецификации. Конфиг —
    /// BepInEx/plugins/SupremeNuclear/Roster/buildings.json (создаётся образцом,
    /// правки юзера неприкосновенны).
    /// </summary>
    internal static class BuildingPool
    {
        private static bool _ready;
        private static readonly List<PoolSpec> _specs = new List<PoolSpec>();
        private static readonly Dictionary<string, BuildingDefinition> _byRole =
            new Dictionary<string, BuildingDefinition>();
        private static readonly Dictionary<BuildingDefinition, PoolSpec> _byDef =
            new Dictionary<BuildingDefinition, PoolSpec>();
        private static readonly List<BuildingDefinition> _all = new List<BuildingDefinition>();

        public static bool Ready => _ready;
        /// <summary>Все клоны пула в порядке конфигурации (экономика первой).</summary>
        public static IReadOnlyList<BuildingDefinition> All => _all;

        public static BuildingDefinition Of(string role) =>
            role != null && _byRole.TryGetValue(role, out BuildingDefinition def) ? def : null;

        public static PoolSpec SpecOf(BuildingDefinition def) =>
            def != null && _byDef.TryGetValue(def, out PoolSpec spec) ? spec : null;

        public static bool IsPool(BuildingDefinition def) => SpecOf(def) != null;

        /// <summary>Вертолётная площадка (роль пула с heliport=true).</summary>
        public static bool IsHeliport(BuildingDefinition def) =>
            SpecOf(def)?.heliport == true;

        /// <summary>Создать пул (ретраится, пока ростер/энциклопедия не готовы).
        /// Зовётся из CommanderModeController.UpdateSafe ПОСЛЕ UnitRoster —
        /// доноры обязаны получить ростер-правки (имена/цены) до клонирования.</summary>
        public static void EnsureCreated()
        {
            if (_ready)
                return;
            if (!UnitRoster.Applied)
                return;
            Encyclopedia e = Encyclopedia.i;
            if (e?.buildings == null)
                return; // сцена догружается

            LoadSpecs();
            var byKey = new Dictionary<string, BuildingDefinition>();
            foreach (BuildingDefinition b in e.buildings)
                if (b != null && !string.IsNullOrEmpty(b.jsonKey) && !byKey.ContainsKey(b.jsonKey))
                    byKey[b.jsonKey] = b;

            foreach (PoolSpec spec in _specs)
            {
                if (string.IsNullOrEmpty(spec.role) || _byRole.ContainsKey(spec.role))
                    continue;
                if (!byKey.TryGetValue(spec.donor, out BuildingDefinition donor))
                {
                    Log.Warn("BuildingPool: донор '" + spec.donor + "' роли " + spec.role +
                             " не найден — роль пропущена");
                    continue;
                }
                BuildingDefinition clone = Object.Instantiate(donor);
                string display = string.IsNullOrEmpty(spec.name) ? donor.unitName : spec.name;
                clone.name = display;
                clone.unitName = display;
                clone.code = string.IsNullOrEmpty(spec.code)
                    ? spec.role.ToUpperInvariant()
                    : spec.code;
                if (spec.price > 0f)
                    clone.value = spec.price;
                UnitRoster.SetTier(clone, spec.tier); // тир каталога = гейт инженера
                _byRole[spec.role] = clone;
                _byDef[clone] = spec;
                _all.Add(clone);

                // Фабрика — T2/T3-варианты «дешёвая ветка НАПРЯМУЮ» (юзер-канон
                // 2026-11-04): инженер нужного тира строит сразу T2/T3-клон
                // (гейт — живой HQ типа этого тира в Place). Цена = базовая +
                // дешёвые апгрейды до целевого тира (как путь T1+upgrade).
                if (spec.factory && spec.tier == 1)
                {
                    float upTo2 = FactoryTierService.NextCost(1, false, out float m2, out _, out _)
                        ? m2 : 0f;
                    float upTo3 = FactoryTierService.NextCost(2, false, out float m3, out _, out _)
                        ? m3 : 0f;
                    for (int tier = 2; tier <= 3; tier++)
                    {
                        BuildingDefinition variant = Object.Instantiate(clone);
                        string vname = display + " T" + tier;
                        variant.name = vname;
                        variant.unitName = vname;
                        variant.code = (string.IsNullOrEmpty(spec.code)
                            ? spec.role.ToUpperInvariant() : spec.code) + tier;
                        variant.value = clone.value + (tier >= 2 ? upTo2 : 0f) +
                                        (tier >= 3 ? upTo3 : 0f);
                        UnitRoster.SetTier(variant, tier);
                        var vspec = new PoolSpec
                        {
                            role = spec.role, donor = spec.donor, name = vname,
                            code = variant.code, tier = tier, price = variant.value,
                            heliport = spec.heliport, factory = true,
                        };
                        _byDef[variant] = vspec;
                        _all.Add(variant);
                    }
                }
            }

            _ready = _all.Count > 0;
            Log.Info("BuildingPool: " + _all.Count + " ролей из " + _specs.Count +
                     " — каталог стройки = только пул (Roster/buildings.json)");
        }

        /// <summary>Образец пула: доноры МЕЛКИЕ (юзер-канон 2026-11-03: мекс и
        /// гидрокарбонку — «на что-то мелкое», прежде enrichmentPlant/refinery),
        /// экономика первой, оборона — полной линией; крупные/мусорные нативные
        /// (factory_*, controlTower, radar, shelter1, доноры) в пул НЕ входят.
        /// Чистая — тестируется.</summary>
        internal static List<PoolSpec> Defaults() => new List<PoolSpec>
        {
            // Экономика (цены проставит EconomyProfile — как раньше); имена —
            // канон SupCom (Mass Extractor / Hydrocarbon Power Plant / Power
            // Generator), юзер-канон 2026-11-04 «понятно, что производит».
            new PoolSpec { role = "mes", donor = "gabionBunker1", name = "Экстрактор массы", code = "MES" },
            new PoolSpec { role = "hyd", donor = "ammoDump", name = "Гидрокарбонная станция", code = "HYD" },
            new PoolSpec { role = "gen", donor = "fuelTank1", name = "Генератор энергии", code = "GEN" },
            // Фабрики (T1: рост тира — апгрейд на месте, §2.3а); имена — SupCom
            // (Land/Air Factory): по имени виден ВЫПУСК (техника/авиация/вертолёты).
            new PoolSpec { role = "depot", donor = "VehicleDepot1", name = "Наземная фабрика", code = "LAND", factory = true },
            new PoolSpec { role = "hangar", donor = "hangar_med", name = "Авиафабрика", code = "AIR", factory = true },
            new PoolSpec { role = "heliport", donor = "Helipad", name = "Вертолётная фабрика", code = "HELO", heliport = true, factory = true },
            new PoolSpec { role = "revetment", donor = "revetment1", name = "Авиафабрика лёгкая", code = "AIRL", factory = true },
            new PoolSpec { role = "storage", donor = "ammunitionBunker", name = "Хранилище БЧ", code = "WHS", tier = 3 },
            // Оборона (вооружённая + укрытия — вся линия, юзер-выбор 2026-11-03)
            new PoolSpec { role = "mg", donor = "Emplacement1_MG" },
            new PoolSpec { role = "atgm", donor = "Emplacement1_ATGM" },
            new PoolSpec { role = "manpads", donor = "Emplacement1_MANPADS" },
            new PoolSpec { role = "aaa", donor = "Emplacement1_23mm" },
            new PoolSpec { role = "pillbox", donor = "pillbox" },
            new PoolSpec { role = "gabion", donor = "gabionBunker1" },
            new PoolSpec { role = "tower", donor = "guardTower1" },
        };

        private static void LoadSpecs()
        {
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Roster");
            string path = Path.Combine(dir, "buildings.json");
            try
            {
                if (File.Exists(path))
                {
                    var file = JsonConvert.DeserializeObject<PoolFile>(
                        File.ReadAllText(path));
                    if (file?.entries != null && file.entries.Count > 0)
                    {
                        _specs.AddRange(file.entries);
                        return;
                    }
                    Log.Warn("BuildingPool: файл пуст — сеюсь образцом (" + path + ")");
                }
                else
                {
                    Directory.CreateDirectory(dir);
                    var fresh = new PoolFile { entries = Defaults() };
                    File.WriteAllText(path, JsonConvert.SerializeObject(fresh,
                        Formatting.Indented,
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                    Log.Info("BuildingPool: образец создан — " + path);
                }
                _specs.AddRange(Defaults());
            }
            catch (System.Exception ex)
            {
                Log.Warn("BuildingPool: конфиг не прочитан (" + ex.Message + ") — сеюсь образцом");
                _specs.Clear();
                _specs.AddRange(Defaults());
            }
        }

        private sealed class PoolFile
        {
            public string comment = "Пул строений = БЕЛЫЙ СПИСОК каталога стройки: только эти " +
                                    "роли видны у инженера. donor — jsonKey нативного здания; name/code \"\" — " +
                                    "имя донора/роль; tier — гейт инженера; price>0 переопределяет цену " +
                                    "(эконом-роли mes/hyd/gen цену берут из Economy/default.json); heliport — " +
                                    "площадка без рулёжки. Удалил строку = здания нет в меню; донор мелкий — " +
                                    "здание мелкое. Нативные здания НЕ в пуле на наших картах не строятся.";
            public List<PoolSpec> entries = new List<PoolSpec>();
        }
    }
}
