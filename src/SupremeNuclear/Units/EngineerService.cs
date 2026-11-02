using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Инженеры трёх тиров (канон SupCom, §2.3-1): фабрики НЕ апаются инженерами —
    /// инженеры сами бывают T1/T2/T3 и производятся фабрикой СООТВЕТСТВУЮЩЕГО
    /// тира. T1 — нативный грузовик (TRUCK) и морской катер LC; T2/T3 — клоны
    /// определения грузовика (паттерн EconomyService.CloneRole, но для юнитов):
    /// та же модель, честные имя/код/цена и ФАКТОР строй-мощи (T2 ×2, T3 ×4 из
    /// JSON). Тир клона регистрируется в UnitRoster — гейт фабрики
    /// (FactoryTierService.CanProduce) и каталог депо видят его как обычный тир.
    /// Создаётся ОДИН РАЗ за запуск после ростера (Encyclopedia переживает сцены).
    /// </summary>
    internal static class EngineerService
    {
        public sealed class TierSpec
        {
            public float price = 450f;
            public float factor = 2f;
            public string code = "ИНЖ2";
        }

        private sealed class EngineerFile
        {
            public string comment = "Инженеры T2/T3 (клоны грузовика): price — цена, " +
                                    "factor — множитель мощи стройки, code — бортовой код плитки. " +
                                    "T1 — нативный грузовик/катер, не конфигурируется.";
            public bool enabled = true;
            public TierSpec tier2 = new TierSpec();
            public TierSpec tier3 = new TierSpec { price = 1100f, factor = 4f, code = "ИНЖ3" };
        }

        private static EngineerFile _profile;
        private static bool _ready;
        private static VehicleDefinition _t2, _t3;
        private static readonly Dictionary<UnitDefinition, float> _factors =
            new Dictionary<UnitDefinition, float>();

        public static bool Ready => _ready;

        /// <summary>Клоны для каталога депо (T2, T3; null-элементы отфильтрованы).</summary>
        public static IReadOnlyList<VehicleDefinition> Clones { get; } = new List<VehicleDefinition>(2);

        /// <summary>Создать клоны (ретраится, пока Encyclopedia/ростер не готовы);
        /// зовётся из CommanderModeController.UpdateSafe после UnitRoster.</summary>
        public static void EnsureCreated()
        {
            if (_ready)
                return;
            if (!UnitRoster.Applied)
                return; // ростер мутации донора должны примениться раньше клона
            Encyclopedia e = Encyclopedia.i;
            if (e?.vehicles == null)
                return;
            _profile = _profile ?? Load();
            if (!_profile.enabled)
            {
                _ready = true;
                return;
            }
            VehicleDefinition donor = null;
            foreach (VehicleDefinition def in e.vehicles)
                if (def != null && def.vehicleType == VehicleType.TRUCK)
                {
                    donor = def;
                    break;
                }
            if (donor == null)
            {
                Log.Warn("Engineer: грузовик-донор не найден — тиры инженера отключены");
                _ready = true;
                return;
            }
            _t2 = Clone(donor, Loc.Tr("Инженер T2"), _profile.tier2, 2);
            _t3 = Clone(donor, Loc.Tr("Инженер T3"), _profile.tier3, 3);
            var clones = (List<VehicleDefinition>)Clones;
            if (_t2 != null) clones.Add(_t2);
            if (_t3 != null) clones.Add(_t3);
            _factors[donor] = 1f; // T1 — нативная моща
            _ready = true;
            Log.Info("Engineer: клоны созданы — T2 " + (_t2 != null) + ", T3 " + (_t3 != null) +
                     " (донор " + donor.jsonKey + ", Roster/engineers.json)");
        }

        private static VehicleDefinition Clone(VehicleDefinition donor, string name,
            TierSpec spec, int tier)
        {
            if (spec == null || spec.price <= 0f)
                return null;
            var clone = UnityEngine.Object.Instantiate(donor);
            clone.name = name;
            clone.unitName = name;
            clone.code = string.IsNullOrEmpty(spec.code) ? name : spec.code;
            clone.value = spec.price;
            UnitRoster.SetTier(clone, tier); // гейт фабрики/каталог видит тир клона
            _factors[clone] = Mathf.Max(1f, spec.factor);
            return clone;
        }

        private static EngineerFile Load()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Roster");
                string path = Path.Combine(dir, "engineers.json");
                if (File.Exists(path))
                    return JsonConvert.DeserializeObject<EngineerFile>(File.ReadAllText(path)) ??
                           new EngineerFile();
                var fresh = new EngineerFile();
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(fresh,
                    Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                Log.Info("Engineer: образец создан — " + path);
                return fresh;
            }
            catch (System.Exception ex)
            {
                Log.Warn("Engineer: профиль не прочитан (" + ex.Message + ") — беру умолчания");
                return new EngineerFile();
            }
        }

        /// <summary>Тир инженера по определению: 2/3 — клоны, 1 — нативный инженер
        /// (TRUCK/LC), 0 — не инженер вовсе.</summary>
        public static int TierOf(UnitDefinition def)
        {
            if (def == null)
                return 0;
            if (def == _t2) return 2;
            if (def == _t3) return 3;
            if (def is VehicleDefinition vd && vd.vehicleType == VehicleType.TRUCK)
                return 1;
            if (def is ShipDefinition sea && sea.shipType == ShipType.LC)
                return 1;
            return 0;
        }

        /// <summary>Множитель строй-мощи инженера (BuildTasks: ставка налива ×
        /// фактор; не-инженер — 1, на всякий случай).</summary>
        public static float FactorOf(UnitDefinition def) =>
            def != null && _factors.TryGetValue(def, out float f) ? f : 1f;

        /// <summary>Лучший инженер, доступный фабрике тира factoryTier
        /// (null — только нативный T1).</summary>
        public static VehicleDefinition BestFor(int factoryTier)
        {
            if (factoryTier >= 3 && _t3 != null) return _t3;
            if (factoryTier >= 2 && _t2 != null) return _t2;
            return null;
        }
    }
}
