using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Строка ростера: jsonKey юнита + дизайн-правки (DESIGN §2.13,
    /// «стандартизация ростера»). price/name — переопределения (0/пусто = натив),
    /// tier 1..3 — явный тир каталога, hidden — вывести из каталога.</summary>
    public sealed class UnitRosterEntry
    {
        public string jsonKey = "";
        public string name = "";
        public int tier = 1;
        public float price;
        public bool hidden;
    }

    /// <summary>
    /// Ростер юнитов (vehicles/aircraft/ships) И ЗДАНИЙ (buildings, v0.135):
    /// ЯВНЫЕ тиры/цены/имена из BepInEx/plugins/SupremeNuclear/Roster/units.json
    /// вместо терциля по цене. При первом запуске файл создаётся ПОЛНЫМ ДАМПОМ
    /// энциклопедии (авто-тиры — терциль по value в своём каталоге, цены —
    /// нативные; фабричные здания сеются тиром 1 — фабрика ставится T1 и растёт
    /// апгрейдом) — юзер правит дизайн без пересборки; юниты/здания новых версий
    /// игры дописываются (merge-back, правки не теряются). Применяется ОДИН РАЗ
    /// за запуск (Encyclopedia переживает сцены): мутации def.value/unitName
    /// подхватывают все потребители (ProductionTasks/UI/AI читают поле в момент
    /// использования). Тир здания — гейт инженера (Build), тир юнита — гейт
    /// фабрики (CanProduce), см. FactoryTierService/EngineerService.
    /// </summary>
    internal static class UnitRoster
    {
        private static bool _applied;
        private static readonly Dictionary<UnitDefinition, int> _tiers =
            new Dictionary<UnitDefinition, int>();
        private static readonly HashSet<UnitDefinition> _hidden =
            new HashSet<UnitDefinition>();

        public static bool Applied => _applied;

        /// <summary>Явный тир юнита ростера (1..3; нет в таблице — 1).
        /// Клоны-инженеры регистрируют сюда свой тир (EngineerService).</summary>
        public static int TierOf(UnitDefinition def) =>
            def != null && _tiers.TryGetValue(def, out int tier)
                ? Mathf.Clamp(tier, 1, 3)
                : 1;

        /// <summary>Явный тир ЗДАНИЯ ростера (гейт инженера, §2.13): тот же
        /// реестр, отдельная точка входа — читается понятнее на вызове.</summary>
        public static int BuildingTierOf(BuildingDefinition def) => TierOf(def);

        /// <summary>Регистрация тира модового клона (инженеры T2/T3): клонов
        /// нет в units.json, тир знает их создатель.</summary>
        public static void SetTier(UnitDefinition def, int tier)
        {
            if (def != null)
                _tiers[def] = Mathf.Clamp(tier, 1, 3);
        }

        public static bool IsHidden(UnitDefinition def) =>
            def != null && _hidden.Contains(def);

        /// <summary>Применить ростер (ретраится, пока Encyclopedia не готова).
        /// Зовётся из CommanderModeController.UpdateSafe до сервисов.</summary>
        public static void EnsureApplied()
        {
            if (_applied)
                return;
            Encyclopedia e = Encyclopedia.i;
            if (e?.vehicles == null || e.aircraft == null || e.ships == null ||
                e.buildings == null)
                return; // сцена ещё догружается

            List<UnitRosterEntry> entries = LoadWithDump(e, out List<UnitRosterEntry> buildingEntries);
            var byKey = new Dictionary<string, UnitRosterEntry>();
            foreach (UnitRosterEntry en in entries)
                if (!string.IsNullOrEmpty(en.jsonKey) && !byKey.ContainsKey(en.jsonKey))
                    byKey[en.jsonKey] = en;
            foreach (UnitRosterEntry en in buildingEntries)
                if (!string.IsNullOrEmpty(en.jsonKey) && !byKey.ContainsKey(en.jsonKey))
                    byKey[en.jsonKey] = en;

            int applied = 0;
            void Apply(IEnumerable<UnitDefinition> defs)
            {
                foreach (UnitDefinition def in defs)
                {
                    if (def == null)
                        continue;
                    if (byKey.TryGetValue(def.jsonKey, out UnitRosterEntry en))
                    {
                        if (en.price > 0f)
                            def.value = en.price;
                        if (!string.IsNullOrEmpty(en.name))
                            def.unitName = en.name;
                        _tiers[def] = Mathf.Clamp(en.tier, 1, 3);
                        if (en.hidden)
                            _hidden.Add(def);
                        applied++;
                    }
                    else
                    {
                        _tiers[def] = 1; // нет в файле (merge-back не успел) — базовый
                    }
                }
            }
            Apply(e.vehicles.Cast<UnitDefinition>());
            Apply(e.aircraft.Cast<UnitDefinition>());
            Apply(e.ships.Cast<UnitDefinition>());
            Apply(e.buildings.Cast<UnitDefinition>()); // тиры зданий = гейт инженера

            _applied = true;
            Log.Info("Roster: применён — " + applied + " позиций из " + byKey.Count +
                     " (файл Roster/units.json)");
        }

        /// <summary>Загрузить units.json; отсутствующий — создать полным дампом
        /// (авто-тиры = терциль по value в своём каталоге); существующий —
        /// дополнить новыми jsonKey (merge-back: правки юзера неприкосновенны).
        /// Юниты и здания — две секции одного файла.</summary>
        private static List<UnitRosterEntry> LoadWithDump(Encyclopedia e,
            out List<UnitRosterEntry> buildings)
        {
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Roster");
            string path = Path.Combine(dir, "units.json");
            try
            {
                if (File.Exists(path))
                {
                    var file = JsonConvert.DeserializeObject<UnitRosterFile>(
                        File.ReadAllText(path));
                    if (file?.units != null)
                    {
                        if (file.buildings == null)
                            file.buildings = new List<UnitRosterEntry>(); // файл до v0.135
                        MergeBack(path, file, e);
                        buildings = file.buildings;
                        return file.units;
                    }
                }

                var fresh = new UnitRosterFile();
                DumpInto(fresh.units, e.vehicles.Cast<UnitDefinition>().ToList(), null);
                DumpInto(fresh.units, e.aircraft.Cast<UnitDefinition>().ToList(), null);
                DumpInto(fresh.units, e.ships.Cast<UnitDefinition>().ToList(), null);
                DumpInto(fresh.buildings, e.buildings.Cast<UnitDefinition>().ToList(),
                    d => ForceTier1(d) ? 1 : ForceTier3(d) ? 3 : 0);
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(fresh,
                    Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                Log.Info("Roster: образец создан — " + fresh.units.Count + " юнитов, " +
                         fresh.buildings.Count + " зданий — " + path);
                buildings = fresh.buildings;
                return fresh.units;
            }
            catch (System.Exception ex)
            {
                Log.Warn("Roster: файл не прочитан (" + ex.Message + ") — расту на авто-тирах");
                var fresh = new UnitRosterFile();
                DumpInto(fresh.units, e.vehicles.Cast<UnitDefinition>().ToList(), null);
                DumpInto(fresh.units, e.aircraft.Cast<UnitDefinition>().ToList(), null);
                DumpInto(fresh.units, e.ships.Cast<UnitDefinition>().ToList(), null);
                DumpInto(fresh.buildings, e.buildings.Cast<UnitDefinition>().ToList(),
                    d => ForceTier1(d) ? 1 : ForceTier3(d) ? 3 : 0);
                buildings = fresh.buildings;
                return fresh.units;
            }
        }

        private sealed class UnitRosterFile
        {
            public string comment = "Ростер Supreme Nuclear: tier 1..3 (юнит — гейт фабрики, " +
                                    "здание — гейт инженера), price>0 переопределяет цену, name переименовывает, " +
                                    "hidden=true прячет из каталога. Фабричные здания сеются тиром 1: фабрика " +
                                    "ставится T1 и растёт апгрейдом на месте. Новые юниты/здания игры " +
                                    "дописываются сюда автоматически (правки не трогаются).";
            public List<UnitRosterEntry> units = new List<UnitRosterEntry>();
            public List<UnitRosterEntry> buildings = new List<UnitRosterEntry>();
        }

        /// <summary>Фабричное здание (депо/ангар/корпус верфи): сеется тиром 1 —
        /// рост тира фабрии это апгрейд на месте, не постройка (§2.3а).</summary>
        private static bool ForceTier1(UnitDefinition def) =>
            def is BuildingDefinition b &&
            (BuildTasks.IsHangarDefinition(b) || BuildTasks.IsProducingDefinition(b));

        /// <summary>Хранилище БЧ — тир 3 (§2.14-3): ЯО открывается T3-инженером.</summary>
        private static bool ForceTier3(UnitDefinition def) =>
            def is BuildingDefinition b && BuildTasks.IsStorageDefinition(b);

        /// <summary>Дамп каталога: авто-тир = терциль по value (чистая логика
        /// тестируется — Catalog тем же правилом); forceTier > 0 сеет принудительно
        /// (фабричные = 1, хранилище БЧ = 3).</summary>
        private static void DumpInto(List<UnitRosterEntry> into, List<UnitDefinition> defs,
            Func<UnitDefinition, int> forceTier)
        {
            var sorted = new List<UnitDefinition>(defs);
            sorted.RemoveAll(d => d == null);
            sorted.Sort((a, b) => a.value.CompareTo(b.value));
            int n = sorted.Count;
            if (n == 0)
                return;
            TercileCuts(n, out int i1, out int i2);
            for (int i = 0; i < n; i++)
            {
                int forced = forceTier != null ? forceTier(sorted[i]) : 0;
                into.Add(new UnitRosterEntry
                {
                    jsonKey = sorted[i].jsonKey,
                    tier = forced > 0 ? forced
                        : i < i1 ? 1 : i < i2 ? 2 : 3,
                    price = sorted[i].value,
                });
            }
        }

        /// <summary>Границы терцилей с защитой от крошечных каталогов:
        /// i1>=1, i1&lt;i2&lt;=n (при n=1: T1=[единственный]).</summary>
        internal static void TercileCuts(int n, out int i1, out int i2)
        {
            i1 = Math.Max(1, n / 3);
            i2 = Math.Min(Math.Max(i1 + 1, n * 2 / 3), n);
        }

        /// <summary>Новые jsonKey (апдейт игры) дописываются в файл существующего
        /// ростера — без перезаписи пользовательских правок. Новые ЗДАНИЯ сеются
        /// терцилем по цене в своей порции (как дамп), юниты — тиром 1.</summary>
        private static void MergeBack(string path, UnitRosterFile file, Encyclopedia e)
        {
            var known = new HashSet<string>();
            foreach (UnitRosterEntry en in file.units)
                if (!string.IsNullOrEmpty(en.jsonKey))
                    known.Add(en.jsonKey);
            foreach (UnitRosterEntry en in file.buildings)
                if (!string.IsNullOrEmpty(en.jsonKey))
                    known.Add(en.jsonKey);
            int added = 0;
            void Add(IEnumerable<UnitDefinition> defs, List<UnitRosterEntry> into, bool tercile)
            {
                var freshDefs = new List<UnitDefinition>();
                foreach (UnitDefinition def in defs)
                {
                    if (def == null || known.Contains(def.jsonKey))
                        continue;
                    freshDefs.Add(def);
                }
                if (freshDefs.Count == 0)
                    return;
                if (tercile)
                {
                    DumpInto(into, freshDefs,
                        d => ForceTier1(d) ? 1 : ForceTier3(d) ? 3 : 0);
                    foreach (UnitDefinition def in freshDefs)
                        known.Add(def.jsonKey);
                    added += freshDefs.Count;
                }
                else
                    foreach (UnitDefinition def in freshDefs)
                    {
                        into.Add(new UnitRosterEntry
                        {
                            jsonKey = def.jsonKey,
                            price = def.value,
                            tier = 1,
                        });
                        known.Add(def.jsonKey);
                        added++;
                    }
            }
            Add(e.vehicles.Cast<UnitDefinition>(), file.units, false);
            Add(e.aircraft.Cast<UnitDefinition>(), file.units, false);
            Add(e.ships.Cast<UnitDefinition>(), file.units, false);
            Add(e.buildings.Cast<UnitDefinition>(), file.buildings, true);
            if (added > 0)
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(file,
                    Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                Log.Info("Roster: merge-back — дописано " + added + " новых позиций игры");
            }
        }
    }
}
