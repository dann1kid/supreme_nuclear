using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть ProductionTasks: лодауты заказов авиации (колесо выбора, редактор ОСНАЩЕНИЕ).
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; общее состояние и сводка API — ProductionTasks.cs.
    public static partial class ProductionTasks
    {
        // ---------- лодауты заказов авиации (колесо над плиткой каталога, DESIGN §2) ----------

        /// <summary>Липкий выбор пресета по типу борта: 0 — «Стандарт» (пустой
        /// лодаут, игра берёт свой дефолт — поведение до v0.88), 1..N — именованные
        /// пресеты StandardLoadouts. Осознанно переживает сцены, как профиль
        /// высоты (предпочтение игрока); сбрасывать нечего — ключи immutable.</summary>
        private static readonly Dictionary<AircraftDefinition, int> _loadoutIndex = new();
        private static readonly Dictionary<AircraftDefinition, List<StandardLoadout>> _presets = new();
        private static readonly Dictionary<AircraftDefinition, HardpointSet[]> _hardpoints = new();
        private static readonly Dictionary<AircraftDefinition, WeaponManager> _weaponManagers = new();
        /// <summary>Слот «Своего» лодаута: сама нагрузка + топливо заказа
        /// (сколько угодно на борт — кнопка «+» редактора ОСНАЩЕНИЕ).</summary>
        public sealed class CustomSlot
        {
            public Loadout Loadout;
            public float Fuel = 1f;
        }

        /// <summary>«Свои» лодауты (редактор ОСНАЩЕНИЕ, СКМ по плитке): правки станций
        /// и топлива; идут в цикле колеса после авторских пресетов. Ключи/объекты
        /// immutable-статичны, состояние живёт как предпочтение игрока (см. _loadoutIndex).</summary>
        private static readonly Dictionary<AircraftDefinition, List<CustomSlot>> _customs = new();
        public static string CustomName => Loc.Tr("Свой");

        /// <summary>«Свои» лодауты борта (пустой список — пока не создавали).</summary>
        public static List<CustomSlot> CustomsOf(AircraftDefinition def)
        {
            if (def != null && _customs.TryGetValue(def, out List<CustomSlot> list))
                return list;
            return EmptyCustoms;
        }

        private static readonly List<CustomSlot> EmptyCustoms = new();

        /// <summary>Есть ли хоть один «Свой» лодаут у борта.</summary>
        public static bool HasCustomLoadout(AircraftDefinition def) => CustomsOf(def).Count > 0;
        private static readonly List<WeaponMount> _mountScratch = new();

        /// <summary>Растёт при смене выбора (колесо/редактор) — в хэш каталога (бейдж
        /// плитки) и в ключ карточки закупки (живой пересчёт вооружения).</summary>
        public static int LoadoutsVersion { get; private set; }

        /// <summary>WeaponManager борта с ПРЕФАБА (сидит на детёныше — ищем в глубину);
        /// кэш по определению. null — данные о станциях недоступны (редактор закрыт).</summary>
        public static WeaponManager PrefabWeaponManager(AircraftDefinition def)
        {
            if (def == null)
                return null;
            if (_weaponManagers.TryGetValue(def, out WeaponManager cached))
                return cached;
            WeaponManager wm = def.unitPrefab != null
                ? def.unitPrefab.GetComponentInChildren<WeaponManager>(true) : null;
            _weaponManagers[def] = wm;
            return wm;
        }

        /// <summary>Хардпоинт-сеты борта (сериализованы на префабе), кэш; null/пусто — нет данных.</summary>
        public static HardpointSet[] HardpointSetsOf(AircraftDefinition def)
        {
            if (def == null)
                return null;
            if (_hardpoints.TryGetValue(def, out HardpointSet[] cached))
                return cached;
            WeaponManager wm = PrefabWeaponManager(def);
            HardpointSet[] sets = wm != null && wm.hardpointSets != null && wm.hardpointSets.Length > 0
                ? wm.hardpointSets : null;
            _hardpoints[def] = sets;
            return sets;
        }

        /// <summary>Именованные пресеты борта (без disabled/пустых), кэш по определению.</summary>
        public static List<StandardLoadout> UsablePresets(AircraftDefinition def)
        {
            var empty = new List<StandardLoadout>();
            if (def == null)
                return empty;
            if (_presets.TryGetValue(def, out List<StandardLoadout> cached))
                return cached;
            var list = new List<StandardLoadout>(4);
            StandardLoadout[] arr = def.aircraftParameters != null
                ? def.aircraftParameters.StandardLoadouts
                : null;
            if (arr != null)
                foreach (StandardLoadout sl in arr)
                    if (sl != null && !sl.disabled && sl.loadout != null)
                        list.Add(sl);
            _presets[def] = list;
            return list;
        }

        /// <summary>Число вариантов выбора: 1 («Стандарт») + именованные пресеты
        /// + «Свои» (сколько создал игрок, «+» в редакторе).</summary>
        public static int LoadoutCount(AircraftDefinition def) =>
            1 + UsablePresets(def).Count + CustomsOf(def).Count;

        /// <summary>Текущий индекс выбора (0 = Стандарт; пресеты; дальше «Свои»), зажат в границы.</summary>
        public static int LoadoutIndexOf(AircraftDefinition def)
        {
            int n = LoadoutCount(def);
            if (n <= 1)
                return 0;
            int i = _loadoutIndex.TryGetValue(def, out int v) ? v : 0;
            return Mathf.Clamp(i, 0, n - 1);
        }

        /// <summary>Цикл выбора колесом (±1, по кругу); у борта без вариантов — тихо.</summary>
        public static void CycleLoadout(AircraftDefinition def, int dir)
        {
            int n = LoadoutCount(def);
            if (n <= 1 || def == null)
                return;
            int i = LoadoutIndexOf(def) + (dir >= 0 ? 1 : -1);
            _loadoutIndex[def] = (i % n + n) % n;
            LoadoutsVersion++;
        }

        /// <summary>Выбранный «Свой» слот (null — выбран не «Свой»).</summary>
        public static CustomSlot SelectedCustom(AircraftDefinition def)
        {
            int i = LoadoutIndexOf(def) - 1 - UsablePresets(def).Count;
            var customs = CustomsOf(def);
            return i >= 0 && i < customs.Count ? customs[i] : null;
        }

        /// <summary>Выбранный лодаут заказа: null = «Стандарт»; fuel — уровень топлива
        /// заказа (FuelRatio пресета / топливо «Своего»; нулевое значение — полный бак).</summary>
        public static Loadout SelectedLoadout(AircraftDefinition def, out float fuel)
        {
            fuel = 1f;
            int i = LoadoutIndexOf(def);
            if (i <= 0)
                return null;
            List<StandardLoadout> presets = UsablePresets(def);
            if (i - 1 < presets.Count)
            {
                StandardLoadout sl = presets[i - 1];
                fuel = sl.FuelRatio > 0.05f ? sl.FuelRatio : 1f;
                return sl.loadout;
            }
            CustomSlot slot = SelectedCustom(def);
            if (slot != null)
            {
                fuel = slot.Fuel > 0.05f ? slot.Fuel : 1f;
                return slot.Loadout;
            }
            return null;
        }

        /// <summary>Создать НОВЫЙ «Свой» слот (кнопка «+» или первая правка при
        /// выбранном пресете/стандарте): клон текущего выбора (пресет / «Свой» /
        /// игровой дефолт loadouts[1]) + выбор его. null — нет данных о станциях.</summary>
        public static CustomSlot AddCustom(AircraftDefinition def)
        {
            HardpointSet[] sets = HardpointSetsOf(def);
            if (sets == null)
                return null;
            Loadout src = SelectedLoadout(def, out float fuel);
            if (src == null && def.aircraftParameters != null &&
                def.aircraftParameters.loadouts != null && def.aircraftParameters.loadouts.Count > 0)
                src = def.aircraftParameters.loadouts.Count > 1
                    ? def.aircraftParameters.loadouts[1]
                    : def.aircraftParameters.loadouts[0];
            var lo = new Loadout();
            for (int i = 0; i < sets.Length; i++)
                lo.weapons.Add(src != null && i < src.weapons.Count ? src.weapons[i] : null);
            var slot = new CustomSlot { Loadout = lo, Fuel = fuel > 0.05f ? fuel : 1f };
            if (!_customs.TryGetValue(def, out List<CustomSlot> list))
                _customs[def] = list = new List<CustomSlot>();
            list.Add(slot);
            _loadoutIndex[def] = LoadoutCount(def) - 1;   // выбрать новый (последний)
            LoadoutsVersion++;
            return slot;
        }

        /// <summary>Объект для правок: выбранный «Свой» — правится на месте;
        /// пресет/стандарт — правка рождает НОВЫЙ «Свой»-клон (AddCustom).
        /// null — нет данных о станциях, редактор недоступен.</summary>
        public static Loadout BeginEdit(AircraftDefinition def)
        {
            CustomSlot slot = SelectedCustom(def);
            if (slot != null)
                return slot.Loadout;
            slot = AddCustom(def);
            return slot != null ? slot.Loadout : null;
        }

        /// <summary>Удалить ВЫБРАННЫЙ «Свой» слот (кнопка «−»; выбор — на «Стандарт»).</summary>
        public static void RemoveSelectedCustom(AircraftDefinition def)
        {
            int i = LoadoutIndexOf(def) - 1 - UsablePresets(def).Count;
            var customs = CustomsOf(def);
            if (i < 0 || i >= customs.Count || !_customs.TryGetValue(def, out List<CustomSlot> list))
                return;
            list.RemoveAt(i);
            _loadoutIndex[def] = 0;
            LoadoutsVersion++;
        }

        /// <summary>Топливо выбранного «Своего» (чипы редактора; не «Свой» — тихо).</summary>
        public static void SetCustomFuel(AircraftDefinition def, float fuel)
        {
            CustomSlot slot = SelectedCustom(def);
            if (slot == null)
                return;
            slot.Fuel = Mathf.Clamp01(fuel);
            LoadoutsVersion++;
        }

        /// <summary>Прямой выбор индекса (кнопки пресетов в редакторе ОСНАЩЕНИЕ).</summary>
        public static void SelectLoadoutIndex(AircraftDefinition def, int idx)
        {
            if (def == null)
                return;
            _loadoutIndex[def] = Mathf.Clamp(idx, 0, LoadoutCount(def) - 1);
            LoadoutsVersion++;
        }

        /// <summary>Цикл станции «Своего» лодаута (клик по строке в редакторе):
        /// варианты = легальные на аэродроме + «пусто», пропущенные через
        /// взаимоисключения пилонах (precludingHardpointSets в обе стороны).
        /// Правка создаёт «Свой» и выбирает его; возвращает установленный вариант.</summary>
        public static WeaponMount CycleStation(AircraftDefinition def, int setIdx, int dir,
            Airbase airbase, FactionHQ hq)
        {
            HardpointSet[] sets = HardpointSetsOf(def);
            if (sets == null || setIdx < 0 || setIdx >= sets.Length)
                return null;
            Loadout lo = BeginEdit(def);
            if (lo == null)
                return null;
            List<WeaponMount> opts = MountOptionsFor(def, setIdx, airbase, hq);
            // «Не перечисленные в weaponOptions» маунты (внутренние пушки) — в цикл:
            // ТЕКУЩИЙ слота и заводской из дефолт-лодаута, вперёд списка (иначе
            // «пусто» снимет пулемёт навсегда — планировщик игры их тоже не предлагает).
            AddUnlistedOption(opts, lo.weapons[setIdx]);
            AddUnlistedOption(opts, DefaultMountOf(def, setIdx));
            int count = opts.Count + 1;                       // + «пусто»
            int cur = opts.Count;                             // по умолчанию — «пусто»
            for (int k = 0; k < opts.Count; k++)
                if (ReferenceEquals(opts[k], lo.weapons[setIdx]))
                {
                    cur = k;
                    break;
                }
            WeaponMount saved = lo.weapons[setIdx];
            for (int step = 0; step < count; step++)
            {
                cur = ((cur + (dir >= 0 ? 1 : -1)) % count + count) % count;
                WeaponMount cand = cur < opts.Count ? opts[cur] : null;
                lo.weapons[setIdx] = cand;
                if (!StationConflicts(sets, lo, setIdx))
                {
                    LoadoutsVersion++;
                    return cand;
                }
            }
            lo.weapons[setIdx] = saved;                       // всё занято — оставляем
            return saved;
        }

        /// <summary>Занятая станция i конфликтует с другими: precluding-списки
        /// проверяются в обе стороны (i исключает j / j исключает i).</summary>
        private static bool StationConflicts(HardpointSet[] sets, Loadout lo, int i)
        {
            if (lo.weapons[i] == null || sets[i] == null)
                return false;
            for (int j = 0; j < sets.Length; j++)
            {
                if (j == i || sets[j] == null)
                    continue;
                if (sets[i].precludingHardpointSets != null &&
                    sets[i].precludingHardpointSets.Contains((byte)j) && lo.weapons[j] != null)
                    return true;
                if (sets[j].precludingHardpointSets != null &&
                    sets[j].precludingHardpointSets.Contains((byte)i) && lo.weapons[j] != null)
                    return true;
            }
            return false;
        }

        /// <summary>Легальные варианты пилона сета на аэродроме: weaponOptions сета,
        /// пропущенные через WeaponChecker (как у планировщика игры); «пусто» (null)
        /// UI добавляет последним шагом цикла. Событийные вызовы — без кэша.</summary>
        public static List<WeaponMount> MountOptionsFor(AircraftDefinition def, int setIdx,
            Airbase airbase, FactionHQ hq)
        {
            var result = new List<WeaponMount>(6);
            HardpointSet[] sets = HardpointSetsOf(def);
            if (sets == null || setIdx >= sets.Length || sets[setIdx] == null)
                return result;
            HardpointSet set = sets[setIdx];
            if (set.weaponOptions == null || airbase == null || hq == null)
                return result;
            WeaponChecker.GetAvailableWeaponsNonAlloc(null, set, airbase, hq, false, _mountScratch);
            // порядок — как в options сета (стабильный цикл), только легальные
            foreach (WeaponMount opt in set.weaponOptions)
                if (opt != null && _mountScratch.Contains(opt))
                    result.Add(opt);
            return result;
        }

        /// <summary>Маунт станции в ДЕФОЛТНОМ лодауте игры (loadouts[1] — то, что
        /// летает «Стандартом»): внутренние пушки и прочее оружие, которого может
        /// НЕ БЫТЬ в weaponOptions сета (подсказка игры: «другие маунты можно
        /// грузить через json миссии» — планировщик их не предлагает, мы вернём
        /// в цикл, иначе «пусто» — дверь в одну сторону).</summary>
        public static WeaponMount DefaultMountOf(AircraftDefinition def, int setIdx)
        {
            var ap = def.aircraftParameters;
            if (ap == null || ap.loadouts == null || ap.loadouts.Count == 0)
                return null;
            Loadout src = ap.loadouts.Count > 1 ? ap.loadouts[1] : ap.loadouts[0];
            return setIdx < src.weapons.Count ? src.weapons[setIdx] : null;
        }

        private static void AddUnlistedOption(List<WeaponMount> opts, WeaponMount m)
        {
            if (m != null && m.info != null && !opts.Contains(m))
                opts.Insert(0, m);
        }

        /// <summary>Есть ли у станции варианты (клик-цикл осмыслен): легальные
        /// options, либо ТЕКУЩИЙ/заводской маунт (снять/вернуть).</summary>
        public static bool StationConfigurable(AircraftDefinition def, int setIdx,
            Airbase airbase, FactionHQ hq)
        {
            if (MountOptionsFor(def, setIdx, airbase, hq).Count > 0)
                return true;
            Loadout cur = SelectedLoadout(def, out _);
            if (cur != null && setIdx < cur.weapons.Count && cur.weapons[setIdx] != null)
                return true;
            return DefaultMountOf(def, setIdx) != null;
        }

        /// <summary>Легален ли лодаут: ТОЛЬКО гейты самой игры (AllowedByHQ —
        /// запрещённое/эвент-контент/ядерные квоты). Склад обычного оружия игра
        /// при спавне НЕ проверяет (LoadHardpointSet гейтит лишь restricted,
        /// WeaponChecker — фильтр планировщика, не спавна), «Стандарт»/null — всегда.</summary>
        public static bool LoadoutAllowed(AircraftDefinition def, Loadout lo, FactionHQ hq)
        {
            if (lo == null)
                return true;
            if (def == null || def.unitPrefab == null)
                return false;
            WeaponManager wm = PrefabWeaponManager(def);
            return wm != null && wm.hardpointSets != null &&
                   lo.weapons.Count >= wm.hardpointSets.Length && lo.AllowedByHQ(wm, hq);
        }

        /// <summary>Запечённый лодаут позиции заказа: выбор на момент постановки;
        /// нелегальный на аэродроме пресет молча деградирует в «Стандарт»
        /// (карточка закупки подписывает недоступность заранее).</summary>
        private static void BakeLoadout(QueueItem item, Airbase airbase)
        {
            Loadout lo = SelectedLoadout(item.AirDef, out float fuel);
            if (lo != null && !LoadoutAllowed(item.AirDef, lo, airbase != null ? airbase.CurrentHQ : null))
            {
                lo = null;
                fuel = 1f;
            }
            item.Loadout = lo;
            item.Fuel = fuel;
        }
    }
}
