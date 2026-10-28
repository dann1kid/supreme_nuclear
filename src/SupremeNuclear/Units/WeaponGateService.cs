using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Гейты вооружения (DESIGN §2.11): per-unit маска ВЫКЛЮЧЕННЫХ классов оружия.
    /// Выключенный класс не применяется НИКОГДА — ни автоогнём, ни в атаке-движении,
    /// ни в явной атаке (ПКМ по цели), ни в залпе V (решение игрока: гейт главнее).
    /// Стойка hold-fire ортогональна (гасит всё сразу). Асы-люди не затрагиваются:
    /// ручной пуск (CmdLaunchMissile → LaunchMount) идёт мимо Ready(), а борт с живым
    /// пилотом-игроком Tick пропускает.
    /// Применение — трёхуровневое, реестр Harmony-патчей не растёт (ARCHITECTURE §6):
    /// 1) №2 Turret.AssessTargetPriority — турель выключенного класса не берёт цель;
    /// 2) №3/№5 AssessHQTargets Postfix — перевыбор разрешённой станции / срыв боя;
    /// 3) Tick: LastFiredTime = now для выключенных станций — жёсткий блок Ready()
    ///    (FireGuns, турели и прочие пути мимо наших патчей). После включения класса
    ///    станция готова через свой fireInterval — следов не остаётся.
    /// Классы — Catalog.ClassOf; состояние переживает смену выделения, сбрасывается
    /// в sceneLoaded (реестр ARCHITECTURE §4), мёртвые юниты вычищаются Prune.
    /// </summary>
    internal static class WeaponGateService
    {
        /// <summary>Выключенные классы per-unit; отсутствие записи = всё разрешено.</summary>
        private static readonly Dictionary<Unit, WeaponClass> Blocked =
            new Dictionary<Unit, WeaponClass>();

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

        /// <summary>Мёртвые/удалённые юниты (Unity fake-null) — из масок.</summary>
        public static void Prune()
        {
            if (Blocked.Count == 0)
                return;
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, WeaponClass> kv in Blocked)
                if (kv.Key == null || kv.Key.disabled)
                {
                    if (dead == null)
                        dead = new List<Unit>();
                    dead.Add(kv.Key);
                }
            if (dead != null)
                foreach (Unit unit in dead)
                    Blocked.Remove(unit);
        }

        public static void Reset() => Blocked.Clear();
    }
}
