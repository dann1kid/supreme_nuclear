using System;
using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Гейты вооружения (DESIGN §2.11): два этажа per-unit правил.
    /// 1) Маска ВЫКЛЮЧЕННЫХ классов (WeaponClass) — выключенный класс не применяется
    ///    НИКОГДА — ни автоогнём, ни в атаке-движении, ни в явной атаке, ни в залпе V.
    /// 2) Правила по ТИПАМ вооружения (v0.106): per-unit разрешённые домены целей
    ///    (TargetDomain) для конкретного WeaponInfo — «AGM-48 только по наземным»,
    ///    платформенные пресеты «пуск только с наземных/воздушных», «выкл».
    /// Стойка hold-fire ортогональна (гасит всё сразу). Асы-люди не затрагиваются.
    /// ГЛАВНАЯ линия принуждения (v0.106) — префиксы WeaponStation.Fire/LaunchMount
    /// (WeaponUseGatePatch, реестр ARCHITECTURE §6): единый предохранитель всех путей
    /// применения — турели, FireControl ПВО/кораблей/пусковых, боевой AI, наш залп,
    /// сетевые Cmd/Rpc-пуски. Дополнительно: №2 Turret.AssessTargetPriority — турель
    /// без разрешённых против кандидата станций не берёт цель (не выдаёт себя
    /// наведением); №3/№5 AssessHQTargets Postfix — перевыбор разрешённой станции
    /// боевого AI; Tick: LastFiredTime = now для выключенных класс-гейтом станций.
    /// Состояние переживает смену выделения, сбрасывается в sceneLoaded (реестр
    /// ARCHITECTURE §4), мёртвые юниты вычищаются Prune.
    /// </summary>
    internal static class WeaponGateService
    {
        /// <summary>Выключенные классы per-unit; отсутствие записи = всё разрешено.</summary>
        private static readonly Dictionary<Unit, WeaponClass> Blocked =
            new Dictionary<Unit, WeaponClass>();

        /// <summary>Правила типов per-unit: WeaponInfo → разрешённые домены целей.
        /// Отсутствие записи/типа = без ограничений; Both не храним (словарь разрежен).</summary>
        private static readonly Dictionary<Unit, Dictionary<WeaponInfo, TargetDomain>> TypeRules =
            new Dictionary<Unit, Dictionary<WeaponInfo, TargetDomain>>();

        private static bool IsHost
        {
            get
            {
                var net = NuclearOption.Networking.NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        public static WeaponClass BlockedOf(Unit unit) =>
            unit != null && Blocked.TryGetValue(unit, out WeaponClass mask) ? mask : WeaponClass.None;

        /// <summary>Станция разрешена юниту: класс станции не выключен.</summary>
        public static bool IsAllowed(Unit unit, WeaponStation station)
        {
            if (station == null)
                return true;
            return (BlockedOf(unit) & Catalog.ClassOf(station.WeaponInfo)) == 0;
        }

        // ---------- правила по типам вооружения (v0.106, DESIGN §2.11) ----------

        /// <summary>Есть ли хоть какое-то ограничение в мире (класс-гейт/тип/стойка) —
        /// быстрый выход префикса Fire/LaunchMount без единого обращения к словарям.</summary>
        public static bool AnyRestrictions =>
            Blocked.Count > 0 || TypeRules.Count > 0 || StanceService.AnyHolding;

        /// <summary>Домен ЦЕЛИ для правил: летящий борт (radarAlt &gt; 1 м — эвристика
        /// самого CombatAI) и ракета в полёте — Air; техника/флот/постройки/
        /// паркованный борт — Surface.</summary>
        public static TargetDomain DomainOf(Unit target)
        {
            if (target is Aircraft)
                return target.radarAlt > 1f ? TargetDomain.Air : TargetDomain.Surface;
            if (target is Missile)
                return TargetDomain.Air;
            return TargetDomain.Surface;
        }

        /// <summary>Тип разрешён юниту против конкретной цели (нет правила — разрешён).</summary>
        public static bool TypeAllows(Unit unit, WeaponStation station, Unit target)
        {
            if (station == null || station.WeaponInfo == null || target == null)
                return true;
            if (!TypeRules.TryGetValue(unit, out Dictionary<WeaponInfo, TargetDomain> rules) ||
                !rules.TryGetValue(station.WeaponInfo, out TargetDomain rule))
                return true;
            return (rule & DomainOf(target)) != 0;
        }

        /// <summary>Есть ли у юнита хоть одно правило типа (ранний выход AI-перевыбора).</summary>
        public static bool HasTypeRules(Unit unit) => unit != null && TypeRules.ContainsKey(unit);

        /// <summary>ЕДИНЫЙ ПРЕДОХРАНИТЕЛЬ (префиксы WeaponStation.Fire/LaunchMount):
        /// true — применение запрещено (hold-fire / класс-гейт / правило типа против
        /// домена этой цели). Цель null (пушки по вектору) — домен не проверяем,
        /// стойка и класс работают. Ас-человек не проверяется здесь — см. патч.
        /// Вызов каждый кадр у пушек: без ограничений в мире — нулевая работа.</summary>
        public static bool BlocksUse(Unit owner, WeaponStation station, Unit target)
        {
            if (station == null || station.WeaponInfo == null)
                return false;
            if (StanceService.IsHoldFire(owner))
                return true;
            if ((BlockedOf(owner) & Catalog.ClassOf(station.WeaponInfo)) != 0)
                return true;
            return !TypeAllows(owner, station, target);
        }

        /// <summary>Может ли турель вступить в бой с кандидатом: хоть одна её станция
        /// проходит класс-гейт и правило типа против КОНКРЕТНОГО кандидата. Выбор
        /// станции внутри AssessTargetPriority остаётся нативным (натив может
        /// выбрать и неразрешённую — выстрел режет предохранитель, компромисс §12).</summary>
        public static bool TurretMayEngage(Turret turret, Unit unit, Unit candidate)
        {
            WeaponClass mask = BlockedOf(unit);
            bool hasTypeRules = TypeRules.TryGetValue(unit, out Dictionary<WeaponInfo, TargetDomain> rules);
            if (mask == WeaponClass.None && !hasTypeRules)
                return true; // у юнита нет ограничений вовсе
            WeaponStation[] stations = turret.GetWeaponStations();
            if (stations == null)
                return true;
            foreach (WeaponStation st in stations)
            {
                if (st == null || st.WeaponInfo == null)
                    continue;
                if ((mask & Catalog.ClassOf(st.WeaponInfo)) != 0)
                    continue;
                if (hasTypeRules && rules.TryGetValue(st.WeaponInfo, out TargetDomain rule) &&
                    candidate != null && (rule & DomainOf(candidate)) == 0)
                    continue;
                return true; // есть станция, которой можно бить этого кандидата
            }
            return false;
        }

        /// <summary>Есть ли у юнита боевая станция этого типа (грузы/десант не в счёт).</summary>
        private static bool UnitHasWeapon(Unit unit, WeaponInfo wi)
        {
            if (unit == null || unit.weaponStations == null)
                return false;
            foreach (WeaponStation st in unit.weaponStations)
                if (st != null && !st.Cargo && st.WeaponInfo == wi &&
                    !wi.cargo && !wi.troops)
                    return true;
            return false;
        }

        /// <summary>Типы вооружения выделения (distinct WeaponInfo), по убыванию
        /// суммарного ПОЛНОГО боезапаса (порядок стабилен в бою и после перезарядок;
        /// при равенстве — по инстансу); max &gt; 0 — кэп для полосы чипов UI.</summary>
        public static List<WeaponInfo> TypesOf(List<Unit> units, int max)
        {
            var totals = new Dictionary<WeaponInfo, int>();
            if (units != null)
                foreach (Unit unit in units)
                {
                    if (unit == null || unit.weaponStations == null)
                        continue;
                    foreach (WeaponStation st in unit.weaponStations)
                    {
                        if (st == null || st.Cargo || st.WeaponInfo == null ||
                            st.WeaponInfo.cargo || st.WeaponInfo.troops)
                            continue;
                        totals.TryGetValue(st.WeaponInfo, out int n);
                        totals[st.WeaponInfo] = n + Math.Max(0, st.FullAmmo);
                    }
                }
            var list = new List<WeaponInfo>(totals.Keys);
            list.Sort((a, b) => totals[b] != totals[a]
                ? totals[b].CompareTo(totals[a])
                : a.GetInstanceID().CompareTo(b.GetInstanceID()));
            if (max > 0 && list.Count > max)
                list.RemoveRange(max, list.Count - max);
            return list;
        }

        /// <summary>Применить состояние чипа к выделению: юнитам, у которых тип ЕСТЬ,
        /// пишется правило по их платформе (авиация/прочее); Both — запись снимается.</summary>
        public static void ApplyTypeState(List<Unit> units, WeaponInfo wi, TypeGateState state)
        {
            if (units == null || wi == null)
                return;
            foreach (Unit unit in units)
            {
                if (unit == null || !UnitHasWeapon(unit, wi))
                    continue;
                TargetDomain rule = Catalog.RuleFor(state, unit is Aircraft);
                if (!TypeRules.TryGetValue(unit, out Dictionary<WeaponInfo, TargetDomain> rules))
                {
                    if (rule == TargetDomain.Both)
                        continue; // снимать нечего — записи и нет
                    rules = new Dictionary<WeaponInfo, TargetDomain>();
                    TypeRules[unit] = rules;
                }
                if (rule == TargetDomain.Both)
                    rules.Remove(wi);
                else
                    rules[wi] = rule;
                if (rules.Count == 0)
                    TypeRules.Remove(unit);
            }
        }

        /// <summary>Агрегат состояния чипа по выделению: null — вперемешку (±).</summary>
        public static TypeGateState? TypeStateOf(List<Unit> units, WeaponInfo wi)
        {
            var perUnit = new List<(bool platformAir, TargetDomain rule)>();
            if (units != null)
                foreach (Unit unit in units)
                {
                    if (unit == null || !UnitHasWeapon(unit, wi))
                        continue;
                    TargetDomain rule = TargetDomain.Both;
                    if (TypeRules.TryGetValue(unit, out Dictionary<WeaponInfo, TargetDomain> rules) &&
                        rules.TryGetValue(wi, out TargetDomain r))
                        rule = r;
                    perUnit.Add((unit is Aircraft, rule));
                }
            return Catalog.AggregateState(perUnit);
        }

        /// <summary>Есть ли у юнита станция этого класса.</summary>
        private static bool UnitHasClass(Unit unit, WeaponClass cls)
        {
            if (unit == null || unit.weaponStations == null)
                return false;
            foreach (WeaponStation st in unit.weaponStations)
                if (st != null && Catalog.ClassOf(st.WeaponInfo) == cls)
                    return true;
            return false;
        }

        /// <summary>Объединение классов станций выделения — какие кнопки-гейты показывать.</summary>
        public static WeaponClass ClassesOf(List<Unit> units)
        {
            var union = WeaponClass.None;
            if (units == null)
                return union;
            foreach (Unit unit in units)
            {
                if (unit == null || unit.weaponStations == null)
                    continue;
                foreach (WeaponStation st in unit.weaponStations)
                    if (st != null)
                        union |= Catalog.ClassOf(st.WeaponInfo);
            }
            return union;
        }

        /// <summary>Тоггл класса по выделению: применяется к юнитам, у которых класс ЕСТЬ;
        /// «у кого-то выключен → включить всем, у всех включен → выключить всем».
        /// Возвращает итоговое единое состояние (true = класс разрешён всем).</summary>
        public static bool ToggleForSelection(List<Unit> units, WeaponClass cls)
        {
            bool anyBlocked = false;
            foreach (Unit unit in units)
                if (UnitHasClass(unit, cls) && (BlockedOf(unit) & cls) != 0)
                {
                    anyBlocked = true;
                    break;
                }
            bool enable = anyBlocked;
            foreach (Unit unit in units)
            {
                if (!UnitHasClass(unit, cls))
                    continue;
                WeaponClass mask = BlockedOf(unit);
                mask = enable ? mask & ~cls : mask | cls;
                if (mask == WeaponClass.None)
                    Blocked.Remove(unit);
                else
                    Blocked[unit] = mask;
            }
            return enable;
        }

        /// <summary>Состояние класса в выделении для подсветки кнопки: 1 — всем разрешён,
        /// 0 — всем выключен, −1 — вперемешку. Считается по юнитам, у которых класс есть.</summary>
        public static int GateStateOf(List<Unit> units, WeaponClass cls)
        {
            int on = 0, off = 0;
            foreach (Unit unit in units)
            {
                if (!UnitHasClass(unit, cls))
                    continue;
                if ((BlockedOf(unit) & cls) != 0)
                    off++;
                else
                    on++;
            }
            return on == 0 && off == 0 ? 1 : on == 0 ? 0 : off == 0 ? 1 : -1;
        }

        /// <summary>Хост: держать выключенные станции «неготовыми» (LastFiredTime = now) —
        /// общий предохранитель всех путей применения оружия. Пустая маска у всех —
        /// нулевая работа (обычный случай).</summary>
        public static void Tick()
        {
            if (Blocked.Count == 0 || !IsHost)
                return;
            float now = Time.timeSinceLevelLoad;
            foreach (KeyValuePair<Unit, WeaponClass> kv in Blocked)
            {
                Unit unit = kv.Key;
                if (unit == null || unit.disabled || unit.weaponStations == null)
                    continue; // мёртвые выпадут в Prune, словарь тут не трогаем
                if (unit is Aircraft ac && ac.Player != null)
                    continue; // борт у человека-аса — не мешаем ручному применению
                WeaponClass mask = kv.Value;
                foreach (WeaponStation st in unit.weaponStations)
                {
                    if (st == null || st.WeaponInfo == null)
                        continue;
                    if ((mask & Catalog.ClassOf(st.WeaponInfo)) != 0)
                        st.LastFiredTime = now;
                }
            }
        }

        /// <summary>Мёртвые/удалённые юниты (Unity fake-null) — из масок и правил типов.</summary>
        public static void Prune()
        {
            if (Blocked.Count == 0 && TypeRules.Count == 0)
                return;
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, WeaponClass> kv in Blocked)
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            foreach (KeyValuePair<Unit, Dictionary<WeaponInfo, TargetDomain>> kv in TypeRules)
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead != null)
                foreach (Unit unit in dead)
                {
                    Blocked.Remove(unit);
                    TypeRules.Remove(unit);
                }
        }

        public static void Reset()
        {
            Blocked.Clear();
            TypeRules.Clear();
        }
    }
}
