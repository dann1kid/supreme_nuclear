using System;
using System.Collections.Generic;
using NuclearOption;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// ЯО v1 (DESIGN §2.14 слой 3, юзер-канон v0.139 «каждый накопитель — шахта»):
    /// заряд живёт НА носителе и гибнет вместе с ним. Накопители: (а) ПРИСТРОЙКА
    /// «Хранилище БЧ» при аэродроме — нативный WarheadStorage, наливает в себя
    /// (авиация питается нативно: списание при подвеске, возврат при посадке,
    /// сгорание при сносе склада, MP-синк — SyncVar); (б) ЮНИТЫ-НОСИТЕЛИ —
    /// тяжёлые корабли (типы из профиля) и наземные ПУ с ядерными станциями:
    /// наливают заряд на себя (кап), пуск тратит заряд, смерть юнита = заряды
    /// исчезают. Пуск с корабля — ПРИКАЗ «ЯО» (кнопка/режим как залпы): точка →
    /// очередь точек (Shift-серия), ракета уходит по мере готовности заряда —
    /// нативный Spawner.SpawnMissile с ядерным дефом (урон — Shockwave по yield).
    /// Автоприменение ЯО по умолчанию ЗАПРЕЩЕНО (WeaponGateService). Host-only.
    /// </summary>
    internal static class WarheadService
    {
        public sealed class Charge
        {
            public int Count;                  // готовые БЧ на юните
            public float PaidMass, PaidEnergy; // налив следующей
            public bool AutoCharge = true;     // тумблер автозарядки (ПКМ по кнопке ЯО)
        }

        public sealed class StrikePoint
        {
            public Unit Owner;
            public GlobalPosition Pos;
            public float Deadline; // точка живёт до пуска/гибели; TTL — страховка от вечных
        }

        private const float StrikeTtlSec = 600f;

        private static readonly Dictionary<Unit, Charge> _charges = new Dictionary<Unit, Charge>();
        private static readonly List<StrikePoint> _strikes = new List<StrikePoint>();
        private static readonly Dictionary<Unit, float> _cooldowns = new Dictionary<Unit, float>();
        private static readonly Dictionary<WarheadStorage, float[]> _storeFill =
            new Dictionary<WarheadStorage, float[]>(); // налив склада: {mass, energy}
        private static BuildingDefinition _storageDef;
        private static MissileDefinition _nukeDef;
        private static float _nukeYield = 12000f; // для кольца поражения (радиус Шоквейва)
        private static float _nukeBlastRadiusM = 650f;
        private static bool _scanned;

        public static int Version { get; private set; }

        // ---------- рантайм-сканы контента ----------

        /// <summary>Здание-хранилище БЧ (нативный WarheadStorage на префабе).</summary>
        public static BuildingDefinition StorageDefinition()
        {
            Scan();
            return _storageDef;
        }

        /// <summary>Ядерная ракета для пуска с корабля (профильный jsonKey, иначе
        /// max blastYield из энциклопедии).</summary>
        public static MissileDefinition NuclearMissile()
        {
            Scan();
            return _nukeDef;
        }

        /// <summary>Радиус поражения ядерной ракеты (для кольца прицела, м).</summary>
        public static float BlastRadiusM()
        {
            Scan();
            return _nukeBlastRadiusM;
        }

        private static void Scan()
        {
            if (_scanned)
                return;
            _scanned = true;
            Encyclopedia e = Encyclopedia.i;
            if (e?.buildings == null)
            {
                _scanned = false; // сцена ещё догружается — ретрай
                return;
            }
            foreach (BuildingDefinition def in e.buildings)
            {
                if (def?.unitPrefab == null)
                    continue;
                if (def.unitPrefab.GetComponentInChildren<WarheadStorage>(true) != null)
                {
                    _storageDef = def;
                    break;
                }
            }
            if (_storageDef == null)
                Log.Warn("ЯО: здание-хранилище БЧ не найдено в энциклопедии — " +
                         "авиация без ЯО (долг §12-57)");
            ScanMissile(e);
            Log.Info("ЯО: скан — склад '" + (_storageDef != null ? _storageDef.jsonKey : "нет") +
                     "', ракета '" + (_nukeDef != null ? _nukeDef.jsonKey : "нет") +
                     "' (yield " + _nukeYield.ToString("F0") + ", R " + _nukeBlastRadiusM.ToString("F0") + " м)");
        }

        private static void ScanMissile(Encyclopedia e)
        {
            if (e.missiles == null)
                return;
            EconomyProfile p = EconomyService.Profile;
            string wantKey = p != null ? (p.nuclearMissileJsonKey ?? "") : "";
            MissileDefinition best = null;
            float bestYield = -1f;
            foreach (MissileDefinition def in e.missiles)
            {
                if (def?.unitPrefab == null)
                    continue;
                float yield = YieldOf(def);
                if (yield <= 0f)
                    continue;
                if (!string.IsNullOrEmpty(wantKey) && def.jsonKey == wantKey)
                {
                    best = def;
                    bestYield = yield;
                    break;
                }
                if (yield > bestYield)
                {
                    bestYield = yield;
                    best = def;
                }
            }
            if (best == null)
            {
                Log.Warn("ЯО: ядерных ракет в энциклопедии нет — пуск с корабля недоступен (§12-57)");
                return;
            }
            _nukeDef = best;
            _nukeYield = bestYield;
            // Шоквейв-радиус натива: (kt*1e6)^(1/3)*13, kt = yield*1e-6.
            float kt = bestYield * 1e-6f;
            _nukeBlastRadiusM = Mathf.Pow(kt * 1e6f, 1f / 3f) * 13f;
        }

        private static readonly System.Reflection.FieldInfo MissileYieldField =
            HarmonyLib.AccessTools.Field(typeof(Missile), "blastYield");

        /// <summary>Ядерный yield ракеты (приватное поле префаба Missile; &gt;200 — ядерная).</summary>
        public static float YieldOf(MissileDefinition def)
        {
            if (def?.unitPrefab == null || MissileYieldField == null)
                return 0f;
            Missile missile = def.unitPrefab.GetComponent<Missile>();
            return missile != null && MissileYieldField.GetValue(missile) is float y ? y : 0f;
        }

        /// <summary>Юнит — носитель БЧ: тяжёлый корабль профиля или не-авиация
        /// с ядерной станцией («ПУ»). Авиация — НЕ носитель (она на складе).</summary>
        public static bool IsCarrier(Unit unit)
        {
            if (unit == null || unit.disabled || unit is Aircraft)
                return false;
            if (unit is Ship ship && ship.definition is ShipDefinition sd)
                return IsNuclearCarrierShip(sd);
            return WarheadsEnabled && HasNuclearWeapon(unit);
        }

        /// <summary>ЯО включено профилем экономики (v0.154): иконки носителей и
        /// кнопки ЯО гаснут вместе с ним — геймплей мёртв, UI не врёт.</summary>
        public static bool WarheadsEnabled =>
            EconomyService.Profile is EconomyProfile p && p.warheadsEnabled;

        /// <summary>Корабль — тип-носитель профиля (def-уровень, v0.154: нужен
        /// StrategicIcons до появления юнита — призраки/иконки по определению).
        /// Строгий матч списка типов — Util.TypeListContains (substring-Contains
        /// ловил бы пересечения коротких кодов типов).</summary>
        public static bool IsNuclearCarrierShip(ShipDefinition sd)
        {
            if (sd == null || !WarheadsEnabled)
                return false;
            return Util.TypeListContains(EconomyService.Profile.nuclearCarrierShipTypes,
                sd.shipType.ToString());
        }

        /// <summary>Есть ли у ОПРЕДЕЛЕНИЯ ядерная станция (кэш по дефу; v0.154
        /// вынесено из HasNuclearWeapon: иконки StrategicIcons считают по дефу —
        /// юнит может не существовать).</summary>
        private static readonly Dictionary<UnitDefinition, bool> _nuclearDef =
            new Dictionary<UnitDefinition, bool>();
        public static bool DefHasNuclearWeapon(UnitDefinition def)
        {
            if (def?.unitPrefab == null)
                return false;
            if (_nuclearDef.TryGetValue(def, out bool nuclear))
                return nuclear;
            nuclear = false;
            foreach (WeaponStation st in def.unitPrefab.GetComponentsInChildren<WeaponStation>(true))
                if (st != null && st.WeaponInfo != null && st.WeaponInfo.nuclear)
                {
                    nuclear = true;
                    break;
                }
            _nuclearDef[def] = nuclear;
            return nuclear;
        }

        /// <summary>Есть ли у юнита ядерная станция (кэш по определению).</summary>
        public static bool HasNuclearWeapon(Unit unit)
        {
            if (unit?.definition == null || unit.weaponStations == null)
                return false;
            return DefHasNuclearWeapon(unit.definition);
        }

        // ---------- заряд юнита-носителя ----------

        public static Charge ChargeOf(Unit unit) =>
            unit != null && _charges.TryGetValue(unit, out Charge c) ? c : null;

        /// <summary>Готовые БЧ на носителе.</summary>
        public static int ChargeCount(Unit unit) => ChargeOf(unit)?.Count ?? 0;

        /// <summary>Тумблер автозарядки на выделение (ПКМ по кнопке «ЯО»).</summary>
        public static void ToggleAutoCharge(List<Unit> units)
        {
            bool anyOff = false;
            foreach (Unit u in units)
            {
                Charge c = ChargeOf(u);
                if (c != null && !c.AutoCharge)
                    anyOff = true;
            }
            foreach (Unit u in units)
            {
                Charge c = ChargeOf(u);
                if (c != null)
                    c.AutoCharge = anyOff; // включаем, если хоть один был выключен
            }
            Version++;
        }

        /// <summary>Списать пуск с носителя (залп станции/автопуск). false — нет заряда.</summary>
        public static bool TryConsumeLaunch(Unit unit)
        {
            Charge c = ChargeOf(unit);
            if (c == null || c.Count <= 0)
                return false;
            c.Count--;
            Version++;
            return true;
        }

        // ---------- приказы «ЯО» ----------

        /// <summary>Точки пуска (лента/сканер читают; тик расходует).</summary>
        public static IReadOnlyList<StrikePoint> Strikes => _strikes;

        /// <summary>Точка пуска носителю (Shift-серия просто добавляет ещё).</summary>
        public static void AddStrike(Unit owner, GlobalPosition pos)
        {
            if (!IsCarrier(owner))
                return;
            _strikes.Add(new StrikePoint { Owner = owner, Pos = pos, Deadline = Time.unscaledTime + StrikeTtlSec });
            Version++;
        }

        /// <summary>Приказ «ядерный удар» авиации: одноразовое разрешение ЯО (до
        /// применения, см. WeaponGateService) + атака-движение в точку — борт
        /// применит ЯО по врагам у точки нативным боевым AI.</summary>
        public static void OrderAircraftStrike(List<Unit> aircraft, Vector3 point)
        {
            int n = 0;
            foreach (Unit unit in aircraft)
            {
                if (!(unit is Aircraft) || !HasNuclearWeapon(unit))
                    continue;
                WeaponGateService.OrderNuclearStrike(unit);
                n++;
            }
            if (n > 0)
                CommanderHud.Notice(Loc.F("Ядерный удар: {0} борт(ов) — применение по точке", n));
        }

        /// <summary>Пуск с носителя в точку (тик зовёт, когда заряд готов).</summary>
        private static bool LaunchFrom(Unit owner, GlobalPosition pos)
        {
            MissileDefinition def = NuclearMissile();
            if (def == null)
                return false;
            var spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null)
                return false;
            Vector3 from = owner.transform.position + Vector3.up * 12f;
            Vector3 aim = pos.ToLocalPosition();
            Vector3 dir = aim - from;
            dir.y = Mathf.Max(dir.y, 0f);
            Vector3 velocity = dir.normalized * 300f + Vector3.up * 120f; // навесной старт
            Missile missile = spawner.SpawnMissile(def, from, Quaternion.LookRotation(velocity), velocity, null, owner);
            if (missile != null)
                FleetSalvo.AimMissileAt(missile, pos); // ГСН → точка удара (knownPos-внедрение)
            Log.Info("ЯО: пуск с '" + owner.NetworkUniqueName + "' → (" +
                     aim.x.ToString("F0") + "," + aim.z.ToString("F0") + "), yield " + _nukeYield.ToString("F0"));
            return true;
        }

        // ---------- тик (host): налив носителей и складов, пуски ----------

        public static void Tick(float dt)
        {
            if (!GameApi.InMission)
            {
                if (_charges.Count > 0 || _strikes.Count > 0 || _storeFill.Count > 0)
                    Reset();
                return;
            }
            var net = NuclearOption.Networking.NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;
            EconomyProfile p = EconomyService.Profile;
            if (p == null || !p.warheadsEnabled)
                return;

            Prune();
            NourishUnits(p, dt);
            NourishStores(p, dt);
            FireStrikes(p);
        }

        private static void Prune()
        {
            bool touched = false;
            foreach (Unit u in new List<Unit>(_charges.Keys))
                if (u == null)
                {
                    _charges.Remove(u); // заряды гибнут вместе с носителем
                    touched = true;
                }
            float now = Time.unscaledTime;
            for (int i = _strikes.Count - 1; i >= 0; i--)
            {
                StrikePoint s = _strikes[i];
                if (s.Owner == null || now >= s.Deadline)
                {
                    _strikes.RemoveAt(i);
                    touched = true;
                }
            }
            foreach (WarheadStorage w in new List<WarheadStorage>(_storeFill.Keys))
                if (w == null || w.Disabled)
                {
                    _storeFill.Remove(w);
                    touched = true;
                }
            if (touched)
                Version++;
        }

        /// <summary>Носители наливают заряд на себя (автозарядка, кап).</summary>
        private static void NourishUnits(EconomyProfile p, float dt)
        {
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!IsCarrier(unit))
                    continue;
                if (!_charges.TryGetValue(unit, out Charge c))
                    _charges[unit] = c = new Charge();
                if (!c.AutoCharge || c.Count >= p.warheadUnitCap)
                    continue;
                FactionHQ hq = unit.NetworkHQ;
                if (hq == null)
                    continue;
                Pour(hq, c, p, dt);
                if (c.PaidMass >= p.warheadMass - 0.5f && c.PaidEnergy >= p.warheadEnergy - 0.5f)
                {
                    c.PaidMass = c.PaidEnergy = 0f;
                    c.Count++;
                    Version++;
                    Log.Info("ЯО: '" + unit.NetworkUniqueName + "' — заряд готов (" + c.Count + ")");
                }
            }
        }

        /// <summary>Склады при аэродромах наливают в себя (каждый параллельно).</summary>
        private static void NourishStores(EconomyProfile p, float dt)
        {
            foreach (var kv in FactionRegistry.HQLookup) // свои и чужие: экономика общая
            {
                FactionHQ hq = kv.Value;
                if (hq == null)
                    continue;
                foreach (Airbase airbase in hq.GetAirbases())
                {
                    if (airbase?.stores == null)
                        continue;
                    foreach (WarheadStorage store in airbase.stores)
                    {
                        if (store == null || store.Disabled)
                            continue;
                        if (store.GetNumber() >= p.warheadStoreCap)
                            continue;
                        if (!_storeFill.TryGetValue(store, out float[] fill))
                            _storeFill[store] = fill = new float[2];
                        Pour(hq, fill, p, dt);
                        if (fill[0] >= p.warheadMass - 0.5f && fill[1] >= p.warheadEnergy - 0.5f)
                        {
                            fill[0] = fill[1] = 0f;
                            store.AddWarhead(1);
                            Version++;
                            Log.Info("ЯО: склад " + (airbase.name ?? "?") + " — БЧ готова (" + store.GetNumber() + ")");
                        }
                    }
                }
            }
        }

        /// <summary>Налив одной БЧ в накопитель (масса/энергия, знакомый Drain).</summary>
        private static void Pour(FactionHQ hq, Charge c, EconomyProfile p, float dt) =>
            PourInto(hq, p, dt, c, null);
        private static void Pour(FactionHQ hq, float[] fill, EconomyProfile p, float dt) =>
            PourInto(hq, p, dt, null, fill);
        private static void PourInto(FactionHQ hq, EconomyProfile p, float dt, Charge c, float[] fill)
        {
            float massWant = Mathf.Min(p.warheadMass / Mathf.Max(0.1f, p.warheadSeconds) * dt,
                p.warheadMass - (c != null ? c.PaidMass : fill[0]));
            float energyWant = Mathf.Min(p.warheadEnergy / Mathf.Max(0.1f, p.warheadSeconds) * dt,
                p.warheadEnergy - (c != null ? c.PaidEnergy : fill[1]));
            EcoFlow.Paid paid = EconomyService.Drain(hq, massWant, energyWant);
            if (c != null)
            {
                c.PaidMass += paid.Mass;
                c.PaidEnergy += paid.Energy;
            }
            else
            {
                fill[0] += paid.Mass;
                fill[1] += paid.Energy;
            }
        }

        /// <summary>Ракеты уходят по готовности заряда (очередь точек, кулдаун).</summary>
        private static void FireStrikes(EconomyProfile p)
        {
            if (_strikes.Count == 0)
                return;
            float now = Time.unscaledTime;
            for (int i = _strikes.Count - 1; i >= 0; i--)
            {
                StrikePoint s = _strikes[i];
                Charge c = ChargeOf(s.Owner);
                if (c == null || c.Count <= 0)
                    continue; // ждёт заряда (или навсегда, если носитель умер — Prune снимет)
                if (_cooldowns.TryGetValue(s.Owner, out float until) && now < until)
                    continue;
                if (!TryConsumeLaunch(s.Owner) || !LaunchFrom(s.Owner, s.Pos))
                    continue;
                _cooldowns[s.Owner] = now + p.nuclearLaunchCooldownSec;
                _strikes.RemoveAt(i);
                Version++;
            }
        }

        /// <summary>Прогресс налива склада 0..1 (карточка «Апгрейда»).</summary>
        public static float StoreFill01(WarheadStorage store)
        {
            EconomyProfile p = EconomyService.Profile;
            if (p == null || p.warheadMass <= 0f || !_storeFill.TryGetValue(store, out float[] fill))
                return 0f;
            return Mathf.Clamp01((fill[0] / p.warheadMass + fill[1] / Mathf.Max(1f, p.warheadEnergy)) * 0.5f);
        }

        public static void Reset()
        {
            _charges.Clear();
            _strikes.Clear();
            _cooldowns.Clear();
            _storeFill.Clear();
            _nuclearDef.Clear(); // дефы перезагружаются сценой — кэш не переживёт (§4)
            Version++;
        }
    }
}
