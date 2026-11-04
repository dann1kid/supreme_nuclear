using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using NuclearOption.Networking;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Инженеры трёх тиров (канон SupCom, §2.3-1): фабрики НЕ апаются инженерами —
    /// инженеры сами бывают T1/T2/T3 и производятся фабрикой СООТВЕТСТВУЮЩЕГО
    /// тира. T1 — нативный грузовик (TRUCK) и морской катер LC. T2/T3 — ДВА
    /// семейства клонов (v0.150 «ховер-класс = клоны LC», юзер-канон 2026-11-04):
    /// наземные — клоны TRUCK (депо; переправа через воду — WaterTick-пружина),
    /// морские — клоны LC (ВЕРФЬ; нативный ховер-амфибия: корабельная физика
    /// вода+суша без костылей). Числа — ОТНОШЕНИЯ к T1 из блюпринтов FAF:
    /// BuildRate 5/13/32.5 → ×2.6/×6.5; HP 150/400/800 → ×2.67/×5.33; масса
    /// 52/130/312 → цена ×2.5/×6 от tier1Price (единая цена T1 у обоих доноров).
    /// Тир клона регистрируется в UnitRoster — гейт фабрики
    /// (FactoryTierService.CanProduce) и каталоги депо/верфи видят его как
    /// обычный тир. Создаётся ОДИН РАЗ за запуск после ростера.
    /// </summary>
    internal static class EngineerService
    {
        /// <summary>Спека тира-клона: отношения к T1 (цены вычисляются, абсолютов нет).</summary>
        public sealed class TierSpec
        {
            public float priceRatio = 2.5f; // цена = tier1Price × отношение (масса 52:130:312)
            public float factor = 2.6f;     // множитель строй-мощи (BuildRate 5:13:32.5)
            public float hpRatio = 2.67f;   // множитель HP клона (HP 150:400:800)
            public string code = "ИНЖ2";    // код плитки наземного клона (депо)
            public string seaCode = "ИНЖ2М"; // код плитки морского клона (верфь, v0.150)
        }

        private sealed class EngineerFile
        {
            public int version = 2; // v1 (абсолютные цены, догма ×2/×4) мигрируется автоматически
            public string comment = "Инженеры (канон SupCom, отношения к T1 из блюпринтов FAF): " +
                                    "tier1Price — цена T1-инженера (грузовик И катер LC; сводит масштабы " +
                                    "с клонами), priceRatio — цена клона = tier1Price × отношение, " +
                                    "factor — множитель мощи стройки, hpRatio — множитель HP клона, " +
                                    "code/seaCode — бортовой код плитки (депо/верфь).";
            public bool enabled = true;
            public float tier1Price = 180f;
            public TierSpec tier2 = new TierSpec();
            public TierSpec tier3 = new TierSpec
            {
                priceRatio = 6f, factor = 6.5f, hpRatio = 5.33f,
                code = "ИНЖ3", seaCode = "ИНЖ3М"
            };
        }

        // v1-схема (для детектора кастомных значений при миграции на v2).
        private sealed class LegacyTierSpec
        {
            public float price = 0f;   // заполняется JSON (0 = ключа «price» не было)
            public float factor = 0f;
        }

        private sealed class LegacyTierFile
        {
            public LegacyTierSpec tier2 = null;
            public LegacyTierSpec tier3 = null;
        }

        private static EngineerFile _profile;
        private static bool _ready;
        private static VehicleDefinition _t2, _t3;
        private static ShipDefinition _seaT2, _seaT3;
        private static readonly Dictionary<UnitDefinition, float> _factors =
            new Dictionary<UnitDefinition, float>();
        private static readonly Dictionary<UnitDefinition, float> _hpRatios =
            new Dictionary<UnitDefinition, float>();

        // Клоны, к которым уже применили HP/тинт (скан реестра может увидеть юнит
        // дважды; флаг на инстансе не повесить — ведём множество, null-и чистятся).
        private static readonly HashSet<Unit> _decorated = new HashSet<Unit>();
        private static MaterialPropertyBlock _tintBlock;

        public static bool Ready => _ready;

        /// <summary>Клоны для каталога депо (T2, T3; null-элементы отфильтрованы).</summary>
        public static IReadOnlyList<VehicleDefinition> Clones { get; } = new List<VehicleDefinition>(2);

        /// <summary>Морские клоны для каталога верфи (T2/T3 на базе LC — ховер-амфибии;
        /// null-элементы отфильтрованы; пусто — донор LC не найден/выключено).</summary>
        public static IReadOnlyList<ShipDefinition> SeaClones { get; } = new List<ShipDefinition>(2);

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
            // Цена T1-инженера сводится с клонами у ОБОИХ доноров (нативные 0.45/90
            // против мод-фондов; прецедент мутации цены — EconomyService.TryStart).
            donor.value = Mathf.Max(1f, _profile.tier1Price);
            _t2 = Clone(donor, Loc.Tr("Инженер T2"), _profile.tier2.code, _profile.tier2, 2);
            _t3 = Clone(donor, Loc.Tr("Инженер T3"), _profile.tier3.code, _profile.tier3, 3);
            var clones = (List<VehicleDefinition>)Clones;
            if (_t2 != null) clones.Add(_t2);
            if (_t3 != null) clones.Add(_t3);
            _factors[donor] = 1f; // T1 — нативная моща

            // МОРСКИЕ клоны (v0.150 «ховер-класс = клоны LC»): донор — катер LC,
            // нативная корабельная физика вода+суша; производит ВЕРФЬ (каталог
            // подмешан в SupcomUi). Донора нет (контент карты) — тихо без моря.
            foreach (ShipDefinition def in e.ships)
                if (def != null && def.shipType == ShipType.LC)
                {
                    _seaDonor = def;
                    break;
                }
            if (_seaDonor != null)
            {
                _seaDonor.value = Mathf.Max(1f, _profile.tier1Price);
                _seaT2 = Clone(_seaDonor, Loc.Tr("Морской инженер T2"), _profile.tier2.seaCode, _profile.tier2, 2);
                _seaT3 = Clone(_seaDonor, Loc.Tr("Морской инженер T3"), _profile.tier3.seaCode, _profile.tier3, 3);
                var sea = (List<ShipDefinition>)SeaClones;
                if (_seaT2 != null) sea.Add(_seaT2);
                if (_seaT3 != null) sea.Add(_seaT3);
                _factors[_seaDonor] = 1f;
            }
            else
                Log.Warn("Engineer: донор LC не найден — морские инженеры T2/T3 отключены");
            _ready = true;
            Log.Info("Engineer: клоны созданы — земля T2 " + (_t2 != null) + "/T3 " + (_t3 != null) +
                     ", море T2 " + (_seaT2 != null) + "/T3 " + (_seaT3 != null) +
                     " (доноры " + donor.jsonKey + " " + donor.length + "×" + donor.width + "×" +
                     donor.height + " м" +
                     (_seaDonor != null ? " / " + _seaDonor.jsonKey + " " + _seaDonor.length + "×" +
                      _seaDonor.width + "×" + _seaDonor.height + " м" : "") +
                     "; габариты — для китбаша, unitcontent/README)");
        }

        private static ShipDefinition _seaDonor;

        // ---------- Амфибии (юзер-канон 2026-11-04 «как в SupCom») ----------
        // Натив убивает наземку при погружении глубже −2 м от уровня моря
        // (GroundVehicleJob_Math1: datum.GlobalY < −2 → underwater → disabled) —
        // Burst-джобу не пропатчить, поэтому инженеры ПОДДЕРЖИВАЮТСЯ НА ПЛАВУ:
        // PD-пружина к ватерлинии −0.4 м + выравнивание + водный ход к точке
        // приказа (GroundOrders) — глубже −2 м не проваливаются = не тонут.
        // Host-only (физика серверна, §7-2); троттл 0.1 с — скан реестра дорог.
        private static float _nextWaterTick;
        private static readonly List<GroundVehicle> WaterBuf = new List<GroundVehicle>();

        public static void WaterTick(float dt)
        {
            if (!GameApi.InMission || !IsHost || Time.unscaledTime < _nextWaterTick)
                return;
            _nextWaterTick = Time.unscaledTime + 0.1f;
            float seaY = Datum.LocalSeaY;
            WaterBuf.Clear();
            foreach (Unit u in UnitRegistry.allUnits)
                if (u is GroundVehicle gv && u != null && !u.disabled && GameApi.IsEngineer(u))
                    WaterBuf.Add(gv);
            foreach (GroundVehicle gv in WaterBuf)
            {
                try
                {
                    Rigidbody rb = gv.rb;
                    if (rb == null)
                        continue;
                    Vector3 p = rb.position;
                    float depth = seaY - p.y;
                    if (depth < 0.25f)
                        continue; // суша/мель — натив сам
                    // Вертикаль: PD к ватерлинии (Acceleration — без массы)
                    float targetY = seaY - 0.4f;
                    float accY = Mathf.Clamp((targetY - p.y) * 8f - rb.velocity.y * 2.5f, -20f, 20f);
                    // Горизонталь: ход к точке приказа (наши приказы — GroundOrders)
                    Vector3 acc = new Vector3(0f, accY, 0f);
                    if (GroundOrders.TryGetQueue(gv, out var queue, out int qi))
                    {
                        for (int k = qi; k < queue.Count; k++)
                        {
                            Vector3 tgt = queue[k].ToLocalPosition();
                            Vector2 d = new Vector2(tgt.x - p.x, tgt.z - p.z);
                            if (d.sqrMagnitude > 25f)
                            {
                                Vector2 dir = d.normalized;
                                acc.x += dir.x * 4f;
                                acc.z += dir.y * 4f;
                                break; // ведём к ПЕРВОЙ дальней точке
                            }
                        }
                    }
                    // Горизонтальный демпфер (водоизмещение): без него возит
                    acc.x -= rb.velocity.x * 0.7f;
                    acc.z -= rb.velocity.z * 0.7f;
                    rb.AddForce(acc, ForceMode.Acceleration);
                    // Выравнивание кверху килем + глушение вращения
                    Vector3 torque = Vector3.Cross(gv.transform.up, Vector3.up) * 6f;
                    torque -= rb.angularVelocity * 1.5f;
                    rb.AddTorque(torque, ForceMode.Acceleration);
                }
                catch (System.Exception e)
                {
                    Log.Warn("Engineer.Water: " + e.Message);
                }
            }
        }

        private static bool IsHost
        {
            get
            {
                var net = NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        private static T Clone<T>(T donor, string name, string code, TierSpec spec, int tier)
            where T : UnitDefinition
        {
            if (spec == null || spec.priceRatio <= 0f || spec.factor <= 0f)
                return null;
            var clone = UnityEngine.Object.Instantiate(donor);
            clone.name = name;
            clone.unitName = name;
            clone.code = string.IsNullOrEmpty(code) ? name : code;
            clone.value = donor.value * spec.priceRatio; // донор уже = tier1Price
            UnitRoster.SetTier(clone, tier); // гейт фабрики/каталог видит тир клона
            _factors[clone] = Mathf.Max(1f, spec.factor);
            _hpRatios[clone] = Mathf.Max(1f, spec.hpRatio);
            return clone;
        }

        // ---------- Спавн клона: канонный HP + тинт тира (host-only) ----------

        /// <summary>Тинты тиров — в тон HUD-чипам (DrawStrategicIcons): T2 жёлтый,
        /// T3 оранжевый. Смягчены: красим рендеры целиком, вырвиглазность не нужна.</summary>
        private static readonly Color Tier2Tint = new Color(1f, 0.9f, 0.55f, 1f);
        private static readonly Color Tier3Tint = new Color(1f, 0.62f, 0.38f, 1f);

        /// <summary>Свежеспавшийся клон депо: HP частей ×hpRatio (канон SupCom),
        /// регистрация масштаба в GameApi.HpFraction, тинт рендеров тира,
        /// китбаш-пропсы (UnitContent). Депо не отдаёт ссылку на юнита — скан
        /// реестра у ворот (как ProductionTasks.DispatchByRally). MP: тинт/HP/пропсы
        /// видит только хост — осознанный долг (класс проблемы §12-46б, SP-first).</summary>
        public static void OnVehicleSpawned(Building depot, VehicleDefinition def)
        {
            if (def != _t2 && def != _t3 || depot == null || !IsHost)
                return;
            Vector3 p = depot.transform.position;
            const float radiusSq = 80f * 80f;
            Unit fresh = null;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || _decorated.Contains(unit))
                    continue;
                if (!(unit is GroundVehicle) || unit.definition != def)
                    continue;
                if (unit.NetworkHQ != depot.NetworkHQ)
                    continue;
                Vector3 q = unit.transform.position;
                if (new Vector2(q.x - p.x, q.z - p.z).sqrMagnitude < radiusSq)
                {
                    fresh = unit;
                    break;
                }
            }
            if (fresh != null)
                Decorate(fresh, def, def == _t3 ? "SN_ENG_T3_LAND" : "SN_ENG_T2_LAND");
        }

        /// <summary>Свежеспущенный морской клон (верфь отдаёт ссылку сразу —
        /// NavalDock.TryLaunch out Ship, скан реестра не нужен): та же
        /// декорация, что у депо-клонов (HP-канон/масштаб/тинт/пропсы).</summary>
        public static void OnShipSpawned(ShipDefinition def, Ship ship)
        {
            if (def != _seaT2 && def != _seaT3 || ship == null || !IsHost)
                return;
            Decorate(ship, def, def == _seaT3 ? "SN_ENG_T3_SEA" : "SN_ENG_T2_SEA");
        }

        private static void Decorate(Unit unit, UnitDefinition def, string propsKey)
        {
            _decorated.Add(unit);
            PruneDecorated();
            float ratio = _hpRatios.TryGetValue(def, out float r) ? r : 1f;
            try
            {
                if (unit.partLookup != null)
                    foreach (UnitPart part in unit.partLookup)
                        if (part != null)
                            part.hitPoints = 100f * ratio;
                GameApi.RegisterHpScale(unit, ratio);
                ApplyTierTint(unit, def == _t3 || def == _seaT3 ? Tier3Tint : Tier2Tint);
                bool props = UnitContent.TryAttachProps(unit, propsKey);
                Log.Info("Engineer: клон " + def.code + " задекорирован (HP ×" +
                         ratio.ToString("F2") + (props ? ", пропсы" : "") + ")");
            }
            catch (System.Exception e)
            {
                Log.Warn("Engineer.Decorate: " + e.Message);
            }
        }

        private static void ApplyTierTint(Unit unit, Color tint)
        {
            _tintBlock ??= new MaterialPropertyBlock();
            foreach (Renderer r in unit.GetComponentsInChildren<Renderer>())
            {
                if (r == null)
                    continue;
                r.GetPropertyBlock(_tintBlock);
                _tintBlock.SetColor("_Color", tint);     // стандартный шейдер
                _tintBlock.SetColor("_BaseColor", tint); // URP
                r.SetPropertyBlock(_tintBlock);
            }
        }

        private static void PruneDecorated()
        {
            if (_decorated.Count == 0)
                return;
            _decorated.RemoveWhere(u => u == null);
        }

        /// <summary>Сброс сцены: юниты умерли (вызывается из sceneLoaded).</summary>
        public static void ResetScene() => _decorated.Clear();

        /// <summary>Масштаб HP юнита-клона (ремонт: максимум части = 100×масштаб).</summary>
        public static float MaxHpScaleOf(Unit unit) =>
            unit != null && unit.definition != null &&
            _hpRatios.TryGetValue(unit.definition, out float r) ? r : 1f;

        private static EngineerFile Load()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Roster");
                string path = Path.Combine(dir, "engineers.json");
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path);
                    var file = JsonConvert.DeserializeObject<EngineerFile>(raw) ?? new EngineerFile();
                    if (file.version < 2)
                    {
                        // v1 (цены абсолютами, догма ×2/×4) — ключи не мапятся на v2,
                        // дефолты v2 (канон FAF) применяются сами; кастом v1 теряется — Warn.
                        if (LegacyCustomized(raw))
                            Log.Warn("Engineer: конфиг v1 с КАСТОМНЫМИ числами заменён каноном v2 " +
                                     "(×2.5/2.6/2.67 и ×6/6.5/5.33) — перепиши Roster/engineers.json под себя");
                        else
                            Log.Info("Engineer: конфиг v1 обновлён до канона v2 (FAF 5/13/32.5)");
                        file.version = 2;
                        File.WriteAllText(path, JsonConvert.SerializeObject(file,
                            Formatting.Indented,
                            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                    }
                    return file;
                }
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

        /// <summary>В конфиге v1 были не-дефолтные числа (v1: 450/×2 и 1100/×4)?
        /// Чистая — тестируется. Ломается JSON — считаем «не кастомили».</summary>
        internal static bool LegacyCustomized(string rawJson)
        {
            try
            {
                var legacy = JsonConvert.DeserializeObject<LegacyTierFile>(rawJson);
                return legacy != null &&
                       (IsCustomTier(legacy.tier2, 450f, 2f) || IsCustomTier(legacy.tier3, 1100f, 4f));
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static bool IsCustomTier(LegacyTierSpec t, float defPrice, float defFactor) =>
            // «price» — ключ именно v1-схемы (v2 пишет priceRatio): нет его — тир
            // не v1-формы, кастома быть не может (в v2 «factor» тоже есть!).
            t != null && t.price > 0f &&
            (Mathf.Abs(t.price - defPrice) > 0.01f || Mathf.Abs(t.factor - defFactor) > 0.01f);

        /// <summary>Тир инженера по определению: 2/3 — клоны (ссылки — РАНЬШЕ типовых
        /// веток: клон наследует тип донора и иначе читается как T1), 1 — нативный
        /// инженер (TRUCK/LC), 0 — не инженер вовсе.</summary>
        public static int TierOf(UnitDefinition def)
        {
            if (def == null)
                return 0;
            if (def == _t2 || def == _seaT2) return 2;
            if (def == _t3 || def == _seaT3) return 3;
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
