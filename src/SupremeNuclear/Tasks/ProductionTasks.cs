using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Task-API v1.6 — очереди производства «фабрик» ЛЮБОГО типа (DESIGN 2.3a):
    /// депо (техника), ангар (авиация) и верфь (флот). Потоковая оплата головной позиции;
    /// нет денег — очередь стоит; пауза/цикл/отмена — на фабрике (кнопки нижней панели,
    /// DESIGN 2.10), при мультивыделении — сразу всем фабрикам выделения. Ралли-маршруты —
    /// у любой фабрики: депо отдаёт маршрут вышедшему юниту, верфь — спущенному кораблю,
    /// ангар — свежему борту по смене приватного Hangar.spawnedObject (SelfCheck; отложенный
    /// спавн авианосца ждёт смены ссылки в Tick), ангар без своего ралли наследует ралли
    /// аэродрома. Хост-авторитет, как BuildTasks.</summary>
    public static class ProductionTasks
    {
        public sealed class QueueItem
        {
            public VehicleDepot Depot;          // фабрика «наземная»...
            public Hangar Hangar;               // ...или ангар (индивидуальная авиафабрия)
            public Building Dock;               // ...или верфь (флот, v0.92)
            public VehicleDefinition GroundDef;
            public AircraftDefinition AirDef;
            public ShipDefinition NavalDef;
            public Loadout Loadout;             // лодаут авиазаказа (null = «Стандарт») — запечён при постановке
            public float Fuel = 1f;             // уровень топлива заказа (FuelRatio пресета)
            public float Cost;
            public float Paid;
            public object Factory => Depot != null ? (object)Depot
                : Hangar != null ? (object)Hangar : (object)Dock;
            public UnitDefinition UnitDef => (UnitDefinition)GroundDef
                ?? (UnitDefinition)AirDef ?? NavalDef;
            public float Progress01 => Cost > 0f ? Mathf.Clamp01(Paid / Cost) : 0f;
        }

        private sealed class FactoryState
        {
            public bool Paused;
            public bool Cycling;
            public readonly List<UnitDefinition> CycleItems = new List<UnitDefinition>();
        }

        private static readonly List<QueueItem> Queue = new List<QueueItem>();
        private static readonly Dictionary<object, FactoryState> Factories =
            new Dictionary<object, FactoryState>();

        // Ралли-маршруты фабрик: сложные цепочки, как приказы юнитам (DESIGN 2.3a).
        // Каждая точка несёт groupId клика (0 — нет): спущенный юнит получает цепочку
        // ГРУППОВЫМИ вейпоинтами — маркер/лента/драг/удаление общие на пачку
        // выпущенных одним маршрутом, как у приказов юнитам напрямую (§12-30).
        public sealed class RallyRoute
        {
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            /// <summary>Группа приказа каждой точки, параллельно Points.</summary>
            public readonly List<int> Groups = new List<int>();
            public int Count => Points.Count;
        }
        private static readonly Dictionary<object, RallyRoute> Rally =
            new Dictionary<object, RallyRoute>();
        private static readonly HashSet<Unit> Routed = new HashSet<Unit>();

        // Ралли авиации: TrySpawnResult борт не отдаёт — приватный Hangar.spawnedObject
        // перезаписывается игрой в SpawnAircraft. Немедленный спавн: ссылка свежая в тот
        // же кадр; отложенный (двери авианосца) — ждём смены ссылки (запомнив ПРОШЛУЮ).
        private static readonly System.Reflection.FieldInfo HangarSpawnedField =
            HarmonyLib.AccessTools.Field(typeof(Hangar), "spawnedObject");
        private sealed class AirRallyWait
        {
            public GameObject Prev;
            public float Deadline;
        }
        private static readonly Dictionary<Hangar, AirRallyWait> AirRallyWaits =
            new Dictionary<Hangar, AirRallyWait>();
        private const float AirRallyWaitSec = 30f;
        private static readonly List<Hangar> _settledWaits = new List<Hangar>();

        public static int Version { get; private set; }

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

        public static IReadOnlyList<QueueItem> All => Queue;

        private static bool IsHost
        {
            get
            {
                var net = NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        public static void Reset()
        {
            Version++;
            Queue.Clear();
            Factories.Clear();
            Rally.Clear();
            Routed.Clear();
            AirRallyWaits.Clear();
        }

        private static FactoryState StateOf(object factory)
        {
            if (!Factories.TryGetValue(factory, out FactoryState state))
                Factories[factory] = state = new FactoryState();
            return state;
        }

        public static int CountFor(object factory)
        {
            int n = 0;
            foreach (QueueItem item in Queue)
                if (item.Factory == factory)
                    n++;
            return n;
        }

        public static bool IsPaused(object factory) =>
            Factories.TryGetValue(factory, out FactoryState s) && s.Paused;

        public static bool IsCycling(object factory) =>
            Factories.TryGetValue(factory, out FactoryState s) && s.Cycling;

        public static void EnqueueGround(VehicleDepot depot, VehicleDefinition def)
        {
            if (!IsHost || depot == null || def == null)
                return;
            Queue.Add(new QueueItem
            {
                Depot = depot,
                GroundDef = def,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            });
            Version++;
        }

        /// <summary>Заказ в КОНКРЕТНЫЙ ангар (индивидуальная фабрия, v0.88):
        /// каталог и слот — только его собственные. Группа ангаров = по одному
        /// в каждый способный (SupCom-паттерн, см. SupcomUi).</summary>
        public static void EnqueueAir(Hangar hangar, AircraftDefinition def)
        {
            if (!EnqueueAirSilent(hangar, def))
                CommanderHud.Notice(Loc.F("Ангар не строит: {0}",
                    Catalog.ShortLabel(def.code, def.unitName)));
        }

        /// <summary>Постановка без уведомлений — групповой заказ (отчёт ведёт вызов).</summary>
        public static bool EnqueueAirSilent(Hangar hangar, AircraftDefinition def)
        {
            if (!IsHost || !CanBuild(hangar, def))
                return false;
            var item = new QueueItem
            {
                Hangar = hangar,
                AirDef = def,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            };
            BakeLoadout(item, hangar.parentAirbase);
            Queue.Add(item);
            Version++;
            return true;
        }

        /// <summary>Заказ в «подходящий» ангар аэродрома (панель аэропорта и шаблоны):
        /// живой ангар, умеющий строить тип, с самой короткой очередью.</summary>
        public static bool EnqueueAirDistributed(Airbase airbase, AircraftDefinition def)
        {
            if (!IsHost || airbase == null || def == null)
                return false;
            Hangar best = null;
            int bestCount = int.MaxValue;
            foreach (Hangar hangar in airbase.hangars)
            {
                Unit unit = hangar != null ? hangar.attachedUnit : null;
                if (unit == null || unit.disabled || !CanBuild(hangar, def))
                    continue;
                int count = CountFor(hangar);
                if (count < bestCount)
                {
                    bestCount = count;
                    best = hangar;
                }
            }
            if (best == null)
            {
                CommanderHud.Notice(Loc.F("Нет работоспособного ангара под: {0}",
                    Catalog.ShortLabel(def.code, def.unitName)));
                return false;
            }
            EnqueueAir(best, def);
            return true;
        }

        /// <summary>Отмена позиции очереди с возвратом уплаченного (семантика SupCom).</summary>
        public static void Cancel(QueueItem item)
        {
            if (!IsHost || item == null || !Queue.Remove(item))
                return;
            Version++;
            if (item.Paid > 0f)
                FactionOf(item)?.AddFunds(item.Paid);
        }

        /// <summary>Добавить count копий позиции СРАЗУ после якоря — визуально в тот же
        /// стак очереди (ЛКМ по плитке стака). Хост-only.</summary>
        public static void InsertAfter(QueueItem anchor, int count)
        {
            if (!IsHost || anchor == null || count <= 0)
                return;
            int idx = Queue.IndexOf(anchor);
            if (idx < 0)
                return;
            idx++;
            for (int i = 0; i < count; i++)
                Queue.Insert(idx + i, new QueueItem
                {
                    Depot = anchor.Depot,
                    Hangar = anchor.Hangar,
                    Dock = anchor.Dock,
                    GroundDef = anchor.GroundDef,
                    AirDef = anchor.AirDef,
                    NavalDef = anchor.NavalDef,
                    Loadout = anchor.Loadout,   // стак = та же конфигурация
                    Fuel = anchor.Fuel,
                    Cost = anchor.Cost,
                });
            Version++;
        }

        /// <summary>Снять до count ПОСЛЕДНИХ позиций стака def у фабрики (ПКМ по плитке
        /// очереди): уплаченное за снятые возвращается; головная позиция стака (в работе)
        /// снимается последней, когда счётчик до неё доходит.</summary>
        public static void RemoveTail(object factory, UnitDefinition def, int count)
        {
            if (!IsHost || factory == null || def == null || count <= 0)
                return;
            for (int i = Queue.Count - 1; i >= 0 && count > 0; i--)
            {
                QueueItem item = Queue[i];
                if (item.Factory != factory || item.UnitDef != def)
                    continue;
                Queue.RemoveAt(i);
                Version++;
                if (item.Paid > 0f)
                    FactionOf(item)?.AddFunds(item.Paid);
                count--;
            }
        }

        private static FactionHQ FactionOf(QueueItem item) =>
            item.Depot != null ? item.Depot.NetworkHQ
                : item.Hangar != null ? item.Hangar?.attachedUnit?.NetworkHQ
                : item.Dock?.NetworkHQ;

        /// <summary>Заказ корабля на ВЕРФЬ (v0.92): каталог — весь флот
        /// Encyclopedia.ships; время — NavalProductionSecondsPerItem (корабли
        /// тяжелее техники). Спуск — NavalDock (кольца воды у платформы);
        /// занятая вода не сжигает позицию — ждёт и повторяет.</summary>
        public static void EnqueueShip(Building dock, ShipDefinition def)
        {
            if (!IsHost || dock == null || def == null)
                return;
            Queue.Add(new QueueItem
            {
                Dock = dock,
                NavalDef = def,
                Cost = def.value > 0f ? def.value : Settings.BuildDefaultCost,
            });
            Version++;
        }

        /// <summary>Групповая пауза (мультивыделение фабрик): состояние УНИФИЦИРУЕТСЯ —
        /// все на паузу либо все сняты, а не инверсия каждого (смешанное выделение
        /// не должно переворачиваться в противоположное смешанное).</summary>
        public static void TogglePauseAll(IReadOnlyList<object> factories)
        {
            if (!IsHost || factories == null || factories.Count == 0)
                return;
            foreach (object factory in factories)
                if (factory != null && !IsPaused(factory))
                {
                    SetPausedForAll(factories, true);
                    return;
                }
            SetPausedForAll(factories, false);
        }

        private static void SetPausedForAll(IReadOnlyList<object> factories, bool paused)
        {
            foreach (object factory in factories)
                if (factory != null)
                    StateOf(factory).Paused = paused;
            Version++;
        }

        /// <summary>Все фабрики списка на паузе (тон кнопки; пустой список — false).</summary>
        public static bool AllPaused(IReadOnlyList<object> factories)
        {
            if (factories == null || factories.Count == 0)
                return false;
            foreach (object factory in factories)
                if (factory != null && !IsPaused(factory))
                    return false;
            return true;
        }

        /// <summary>Цикл (зацикленная очередь) фабрики: включение снимает текущую
        /// очередь; когда опустеет — снимок ставится заново.</summary>
        public static void SetCycling(object factory, bool cycling)
        {
            if (!IsHost || factory == null)
                return;
            Version++;
            FactoryState state = StateOf(factory);
            state.Cycling = cycling;
            state.CycleItems.Clear();
            if (!cycling)
                return;
            foreach (QueueItem item in Queue)
                if (item.Factory == factory && item.UnitDef != null)
                    state.CycleItems.Add(item.UnitDef);
        }

        /// <summary>Групповой цикл (мультивыделение): унификация, как у паузы —
        /// включение снимает текущую очередь каждой фабрии, выключение чистит снимки.</summary>
        public static void ToggleCycleAll(IReadOnlyList<object> factories)
        {
            if (!IsHost || factories == null || factories.Count == 0)
                return;
            bool anyOff = false;
            foreach (object factory in factories)
                if (factory != null && !IsCycling(factory))
                {
                    anyOff = true;
                    break;
                }
            foreach (object factory in factories)
                if (factory != null)
                    SetCycling(factory, anyOff);
        }

        /// <summary>Все фабрики списка в цикле (тон кнопки; пустой список — false).</summary>
        public static bool AllCycling(IReadOnlyList<object> factories)
        {
            if (factories == null || factories.Count == 0)
                return false;
            foreach (object factory in factories)
                if (factory != null && !IsCycling(factory))
                    return false;
            return true;
        }

        public static RallyRoute RallyOf(object factory) =>
            Rally.TryGetValue(factory, out RallyRoute route) ? route : null;

        /// <summary>Все фабрики с живыми ралли-маршрутами (отрисовка Shift-режима
        /// видимости: невыделенные фабрики — приглушённо, как цепочки юнитов).</summary>
        public static void CollectRallies(List<object> factories)
        {
            factories.Clear();
            foreach (KeyValuePair<object, RallyRoute> kv in Rally)
                if (kv.Value.Count > 0)
                    factories.Add(kv.Key);
        }

        public static void SetRally(object factory, Vector3 point, int groupId)
        {
            if (factory == null)
                return;
            var route = new RallyRoute();
            route.Points.Add(point.ToGlobalPosition());
            route.Groups.Add(groupId);
            Rally[factory] = route;
            Version++;
        }

        public static void EnqueueRally(object factory, Vector3 point, int groupId)
        {
            if (factory == null)
                return;
            if (!Rally.TryGetValue(factory, out RallyRoute route))
                Rally[factory] = route = new RallyRoute();
            route.Points.Add(point.ToGlobalPosition());
            route.Groups.Add(groupId);
            Version++;
        }

        public static void ClearRally(object factory)
        {
            if (factory != null && Rally.Remove(factory))
                Version++;
        }

        /// <summary>Ключ состояния очереди фабрики для кэша панели.</summary>
        public static int KeyFor(object factory)
        {
            int key = (IsPaused(factory) ? 1 : 0) + (IsCycling(factory) ? 2 : 0);
            foreach (QueueItem item in Queue)
                if (item.Factory == factory)
                    key = key * 31 + (item.UnitDef != null ? item.UnitDef.GetInstanceID() * 7 : 0) +
                         (int)(item.Progress01 * 20f);
            return key;
        }

        /// <summary>Умеет ли КОНКРЕТНЫЙ ангар строить тип (его собственный список;
        /// у вертолётных и самолётных площадок он разный).</summary>
        public static bool CanBuild(Hangar hangar, AircraftDefinition def) =>
            hangar != null && def != null && hangar.GetAvailableAircraft() != null &&
            System.Array.IndexOf(hangar.GetAvailableAircraft(), def) >= 0;

        /// <summary>Слотов оплаты у индивидуальной фабрии — один (ангара/депо):
        /// параллельность рождается из числа ангаров с очередями (v0.88 — пер-
        /// ангарные очереди вместо аэродромных; решено по плейтесту: группа ангаров
        /// + пакетный заказ, и физика обломков требует рассредоточения).</summary>
        private static int PaySlots(QueueItem item) => 1;

        /// <summary>Хост-тик: потоковая оплата головной позиции каждой фабрики
        /// (ангара/депо — по одному потоку на фабрику, параллельность = число
        /// фабрик), выпуск по готовности (ангар занят циклом дверей — оплаченная
        /// позиция ждёт и повторяет попытку).</summary>
        public static void Tick()
        {
            if (!GameApi.InMission || !IsHost)
                return;
            PumpAirRallyWaits(); // двери авианосца могли выпустить борт после сдачи позиции
            if (Queue.Count == 0)
                return;
            float dt = Time.deltaTime;
            var paidSlots = new Dictionary<(object factory, UnitDefinition def), int>();

            for (int i = 0; i < Queue.Count; )
            {
                QueueItem item = Queue[i];
                FactionHQ hq = FactionOf(item);
                bool dead = hq == null ||
                            (item.Depot == null && item.Hangar == null && item.Dock == null) ||
                            (item.Depot != null && item.Depot.disabled) ||
                            (item.Hangar != null && item.Hangar.Disabled) ||
                            (item.Dock != null && item.Dock.disabled);
                if (dead)
                {
                    hq?.AddFunds(item.Paid); // фабрика умерла — возврат
                    Queue.RemoveAt(i);
                    Version++;
                    continue;
                }

                // Слот гейтит только ОПЛАТУ: выпуск оплаченных позиций не гейтится —
                // ангар мог заняться/занят и после оплаты, оплаченное ждёт.
                // Потоки: ангар — ПЕР-ТИПОВОЙ (v0.74, вертолётный/самолётный пулы
                // независимы); депо и ВЕРФЬ — ОДИН поток на фабрику (SupCom-канон:
                // наливается головная позиция, параллельность = число фабрик;
                // плейтест v0.96: верфь с разными типами кораблей платилась в 2+).
                object factory = item.Factory;
                var slotKey = (factory: factory,
                    def: item.Depot != null || item.Dock != null ? null : item.UnitDef);
                paidSlots.TryGetValue(slotKey, out int usedSlot);
                bool canPay = usedSlot < PaySlots(item);
                if (canPay)
                    paidSlots[slotKey] = usedSlot + 1;

                FactoryState state = StateOf(factory);
                if (canPay && !state.Paused)
                {
                    // Флот тяжелее техники: свой темп производства (Settings/Naval).
                    float seconds = item.NavalDef != null
                        ? Settings.NavalProductionSecondsPerItem
                        : Settings.ProductionSecondsPerItem;
                    float rate = item.Cost / Mathf.Max(1f, seconds);
                    float pay = Mathf.Min(rate * dt, item.Cost - item.Paid, hq.factionFunds);
                    if (pay > 0f)
                    {
                        item.Paid += pay;
                        hq.AddFunds(-pay);
                    }
                }

                if (item.Paid >= item.Cost - 0.01f && !state.Paused)
                {
                    bool spawned = false;
                    Ship launched = null;
                    if (item.Depot != null)
                        spawned = item.Depot.TrySpawnVehicle(item.GroundDef);
                    else if (item.Dock != null && item.NavalDef != null)
                        spawned = NavalDock.TryLaunch(item.Dock, item.NavalDef, hq, out launched);
                    else if (item.Hangar != null && item.AirDef != null &&
                             item.Hangar.CanSpawnAircraft(item.AirDef))
                    {
                        // Лодаут позиции (запечён при постановке); к моменту спавна
                        // склад мог измениться — перегружаем нелегальный в «Стандарт».
                        Loadout lo = item.Loadout;
                        float fuel = item.Fuel;
                        if (lo != null &&
                            !LoadoutAllowed(item.AirDef, lo, hq))
                        {
                            lo = null;
                            fuel = 1f;
                        }
                        int livery = item.AirDef.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
                        GameObject prevSpawn = HangarSpawnedOf(item.Hangar);
                        var result = item.Hangar.TrySpawnAircraft(
                            null, item.AirDef, new LiveryKey(livery),
                            lo != null ? lo : new Loadout(), fuel);
                        spawned = result.Allowed;
                        if (spawned)
                            AfterAirSpawn(item.Hangar, prevSpawn, result.DelayedSpawn);
                    }

                    if (spawned)
                    {
                        Queue.RemoveAt(i);
                        Version++;
                        if (item.Depot != null)
                            DispatchByRally(item.Depot, item.GroundDef);
                        else if (launched != null)
                            DispatchShipByRally(item.Dock, launched);
                        OnQueueAdvanced(factory);
                        continue; // на этом месте уже следующая позиция
                    }
                    // занято (кулдаун депо / цикл дверей ангара / нет воды у верфи) — оплачено, повторим
                }
                i++;
            }
        }

        /// <summary>Ралли спущенного корабля: NavalDock вернул ссылку — сразу
        /// цепочка GroundOrders (искать по реестру, как у депо, не нужно).
        /// Точки несут groupId клика ралли — пачка кораблей одного маршрута
        /// рисуется/редактируется как групповой приказ (§12-30).</summary>
        private static void DispatchShipByRally(Building dock, Ship ship)
        {
            if (ship == null || !Rally.TryGetValue(dock, out RallyRoute route) ||
                route.Count == 0)
                return;
            GroundOrders.Set(ship, route.Points[0], false, route.Groups[0]);
            for (int i = 1; i < route.Count; i++)
                GroundOrders.Enqueue(ship, route.Points[i], false, route.Groups[i]);
        }

        /// <summary>Захват вышедшего юнита депо (ссылку не отдаёт — ищем свежий
        /// GroundVehicle нужного определения в зоне фабрики, ещё не отправленный).
        /// Точки — с groupId клика ралли (групповой приказ пачки, §12-30).</summary>
        private static void DispatchByRally(VehicleDepot depot, VehicleDefinition def)
        {
            if (!Rally.TryGetValue(depot, out RallyRoute route) || route.Count == 0)
                return;
            Vector3 p = depot.transform.position;
            const float radiusSq = 80f * 80f;
            Unit spawned = null;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || Routed.Contains(unit))
                    continue;
                if (!(unit is GroundVehicle) || unit.definition != def)
                    continue;
                if (unit.NetworkHQ != depot.NetworkHQ)
                    continue;
                Vector3 q = unit.transform.position;
                if (new Vector2(q.x - p.x, q.z - p.z).sqrMagnitude < radiusSq)
                {
                    spawned = unit;
                    break;
                }
            }
            if (spawned == null)
                return;
            Routed.Add(spawned);
            GroundOrders.Set(spawned, route.Points[0], false, route.Groups[0]);
            for (int i = 1; i < route.Count; i++)
                GroundOrders.Enqueue(spawned, route.Points[i], false, route.Groups[i]);
        }

        private static GameObject HangarSpawnedOf(Hangar hangar) =>
            hangar != null && HangarSpawnedField != null
                ? HangarSpawnedField.GetValue(hangar) as GameObject
                : null;

        /// <summary>Маршрут ралли ангара: свой; нет своего — ралли аэродрома
        /// (панель аэропорта задаёт общий маршрут всем своим ангарам).</summary>
        private static RallyRoute RouteOf(Hangar hangar)
        {
            if (Rally.TryGetValue(hangar, out RallyRoute route) && route.Count > 0)
                return route;
            Airbase airbase = hangar.parentAirbase;
            if (airbase != null && Rally.TryGetValue(airbase, out route) && route.Count > 0)
                return route;
            return null;
        }

        /// <summary>Ралли свежего борта ангара. Немедленный спавн — ссылка на борт уже
        /// сменилась в тот же кадр; отложенный (двери авианосца) — ждём смены ссылки
        /// в Tick (Prev запоминается ДО вызова спавна: spawnedObject держит прошый борт).</summary>
        private static void AfterAirSpawn(Hangar hangar, GameObject prev, bool delayed)
        {
            if (hangar == null)
                return;
            if (!delayed)
            {
                GameObject fresh = HangarSpawnedOf(hangar);
                if (fresh != null && !ReferenceEquals(fresh, prev))
                    DispatchAirByRally(fresh.GetComponent<Aircraft>(), hangar);
                return;
            }
            AirRallyWaits[hangar] = new AirRallyWait
            { Prev = prev, Deadline = Time.time + AirRallyWaitSec };
        }

        /// <summary>Отложенные спавны: spawnedObject ангара сменился — борт вышел,
        /// выдаём ему ралли. Мёртвый ангар/таймаут — ожидание снимается тихо.</summary>
        private static void PumpAirRallyWaits()
        {
            if (AirRallyWaits.Count == 0)
                return;
            _settledWaits.Clear();
            foreach (var kv in AirRallyWaits)
            {
                if (kv.Key == null || Time.time > kv.Value.Deadline) // Unity fake-null
                {
                    _settledWaits.Add(kv.Key);
                    continue;
                }
                GameObject fresh = HangarSpawnedOf(kv.Key);
                if (fresh == null || ReferenceEquals(fresh, kv.Value.Prev))
                    continue;
                _settledWaits.Add(kv.Key);
                DispatchAirByRally(fresh.GetComponent<Aircraft>(), kv.Key);
            }
            foreach (Hangar done in _settledWaits)
                AirRallyWaits.Remove(done);
        }

        /// <summary>Ралли борта: план Transit по точкам маршрута (AircraftOrders.Set
        /// сам выкатит стоящий борт — такси и взлёт, дальше маршрут). Точки — с
        /// groupId клика ралли: пачка бортов одного маршрута — групповые
        /// вейпоинты плана, как приказы напрямую (§12-30).</summary>
        private static void DispatchAirByRally(Aircraft aircraft, Hangar hangar)
        {
            RallyRoute route = RouteOf(hangar);
            if (aircraft == null || route == null)
                return;
            AircraftOrders.Set(aircraft, AirOrderMode.Transit,
                route.Points[0].ToLocalPosition(), route.Groups[0]);
            for (int i = 1; i < route.Count; i++)
                AircraftOrders.Enqueue(aircraft, AirOrderMode.Transit,
                    route.Points[i].ToLocalPosition(), route.Groups[i]);
        }

        private static void OnQueueAdvanced(object factory)
        {
            if (!Factories.TryGetValue(factory, out FactoryState state) ||
                !state.Cycling || CountFor(factory) > 0)
                return;
            foreach (UnitDefinition def in state.CycleItems)
            {
                if (def is VehicleDefinition vd && factory is VehicleDepot depot)
                    Queue.Add(new QueueItem
                    {
                        Depot = depot,
                        GroundDef = vd,
                        Cost = vd.value > 0f ? vd.value : Settings.BuildDefaultCost,
                    });
                else if (def is AircraftDefinition ad && factory is Hangar hangar &&
                         CanBuild(hangar, ad))
                    Queue.Add(new QueueItem
                    {
                        Hangar = hangar,
                        AirDef = ad,
                        Cost = ad.value > 0f ? ad.value : Settings.BuildDefaultCost,
                    });
                else if (def is ShipDefinition sd && factory is Building dock &&
                         BuildTasks.IsDock(dock))
                    Queue.Add(new QueueItem
                    {
                        Dock = dock,
                        NavalDef = sd,
                        Cost = sd.value > 0f ? sd.value : Settings.BuildDefaultCost,
                    });
            }
            if (CountFor(factory) > 0)
                Version++;
        }
    }
}
