using System;
using System.Collections.Generic;

namespace SupremeNuclear
{
    /// <summary>Тир-фильтр каталога (DESIGN 2.10): пул режется по цене на три равные части.
    /// Чистая функция — тестируется тест-раннером.</summary>
    internal static class Catalog
    {
        /// <summary>tier: 0 — без фильтра, 1..3 — терциль по цене (1 — дешёвый).
        /// Список правится на месте (сортировка по цене).</summary>
        public static void ApplyTierFilter<T>(List<T> defs, Func<T, float> valueOf, int tier)
        {
            if (tier <= 0 || defs.Count == 0)
                return;
            defs.Sort((a, b) => valueOf(a).CompareTo(valueOf(b)));
            int n = defs.Count;
            // Границы терцилей с защитой от крошечных каталогов: i1>=1, i1<i2<=n
            // (при n=1: T1=[единственный], T2/T3 пусты — но не падаем).
            int i1 = Math.Max(1, n / 3);
            int i2 = Math.Min(Math.Max(i1 + 1, n * 2 / 3), n);
            List<T> keep = tier == 1
                ? defs.GetRange(0, i1)
                : tier == 2 ? defs.GetRange(i1, i2 - i1)
                : defs.GetRange(i2, n - i2);
            defs.Clear();
            defs.AddRange(keep);
        }

        /// <summary>Короткая подпись плитки каталога/очереди: бортовой код
        /// (definition.code — то, что игра показывает в своём UI выбора), иначе
        /// первое слово имени (до 10 символов — влезает в плитку 60px).</summary>
        public static string ShortLabel(string code, string unitName)
        {
            if (!string.IsNullOrEmpty(code))
                return code;
            if (!string.IsNullOrEmpty(unitName))
            {
                int sp = unitName.IndexOf(' ');
                string word = sp > 0 ? unitName.Substring(0, sp) : unitName;
                return word.Length <= 10 ? word : word.Substring(0, 10);
            }
            return "?";
        }

        /// <summary>Бейдж типа наземки (левый-верх плитки): русские военные
        /// аббревиатуры различают «одинаковые квадратики» каталога депо.</summary>
        public static string BadgeOf(VehicleType type) => type switch
        {
            VehicleType.MBT => "ТНК",
            VehicleType.AFV => "ББМ",
            VehicleType.LCV => "ЛБМ",
            VehicleType.ART => "АРТ",
            VehicleType.AAA => "ЗУ",
            VehicleType.IR_SAM => "ПЗРК",
            VehicleType.R_SAM => "ЗРК",
            VehicleType.RDR => "РЛС",
            VehicleType.TRUCK => "ГРЗ",
            VehicleType.UGV => "БНП",
            _ => "",
        };

        /// <summary>Полное имя типа наземки для инфоокна.</summary>
        public static string TypeNameOf(VehicleType type) => type switch
        {
            VehicleType.MBT => "танк",
            VehicleType.AFV => "боевая бронированная машина",
            VehicleType.LCV => "лёгкая боевая машина",
            VehicleType.ART => "артиллерия",
            VehicleType.AAA => "зенитная установка",
            VehicleType.IR_SAM => "ПЗРК",
            VehicleType.R_SAM => "ЗРК",
            VehicleType.RDR => "РЛС",
            VehicleType.TRUCK => "грузовик",
            VehicleType.UGV => "беспилотная наземная платформа",
            _ => "техника",
        };

        /// <summary>Бейдж роли авиации: истребитель/ударный/многоролевой выводится
        /// из roleIdentity (какой цели угрожает), слабый бойец — ДРЛО/транспорт
        /// по typeIdentity (кем является).</summary>
        public static string AirBadge(RoleIdentity role, TypeIdentity type)
        {
            float aa = role.antiAir, asu = role.antiSurface;
            if (aa < 0.25f && asu < 0.25f)
                return type.radar >= 0.5f ? "РЛС" : "ТР";
            if (aa >= 0.45f && asu >= 0.45f)
                return "М";
            return aa > asu ? "И" : "У";
        }

        /// <summary>Полное имя роли авиации для инфоокна.</summary>
        public static string AirRoleName(RoleIdentity role, TypeIdentity type) =>
            AirBadge(role, type) switch
            {
                "И" => "истребитель",
                "У" => "ударный",
                "М" => "многоролевой",
                "РЛС" => "ДРЛО",
                "ТР" => "транспорт",
                _ => "авиация",
            };

        // ---------- сводка вооружения массового выделения (инфопанель) ----------

        /// <summary>Домены целей оружия для тега сводки группы; при слиянии станций
        /// одного имени биты OR-ятся. Пороговая логика — как у игры (MapToolTip
        /// классифицирует оружие просто antiAir/antiSurface &gt; 0).</summary>
        public static WeaponDomain WeaponDomains(float antiAir, float antiSurface,
            float antiMissile, bool nuclear, bool jammer)
        {
            var d = WeaponDomain.None;
            if (antiAir > 0f) d |= WeaponDomain.Air;
            if (antiSurface > 0f) d |= WeaponDomain.Surface;
            if (antiMissile > 0f) d |= WeaponDomain.Missile;
            if (nuclear) d |= WeaponDomain.Nuclear;
            if (jammer) d |= WeaponDomain.Jammer;
            return d;
        }

        /// <summary>Тег домена строки сводки: «АА» (по воздуху), «АЗ» (по земле),
        /// «У» (универсальное), «ПР» (противоракетное), «РЕБ» (постановщик помех);
        /// ядерная БЧ — префикс «Я » (Я АЗ). Пусто — эффективности нет, не боевой.</summary>
        public static string DomainTag(WeaponDomain d)
        {
            if ((d & WeaponDomain.Jammer) != 0)
                return "РЕБ";
            bool air = (d & WeaponDomain.Air) != 0, srf = (d & WeaponDomain.Surface) != 0;
            string core;
            if (air && srf) core = "У";
            else if (air) core = "АА";
            else if (srf) core = "АЗ";
            else if ((d & WeaponDomain.Missile) != 0) core = "ПР";
            else return "";
            return (d & WeaponDomain.Nuclear) != 0 ? "Я " + core : core;
        }

        /// <summary>Строка сводки группы: «АА AIM-120C: 12/16 · 4 носителя»;
        /// полностью расстрелянный тип помечается «пусто» (пора на перезарядку).</summary>
        public static string FormatWeaponRow(WeaponTally t)
        {
            string tag = DomainTag(t.Domains);
            string head = string.IsNullOrEmpty(tag) ? t.Name : tag.PadRight(3) + " " + t.Name;
            return head + ": " + t.Ammo + "/" + t.Full + " · " + t.Carriers + " " +
                   Util.RuPlural(t.Carriers, "носитель", "носителя", "носителей") +
                   (t.Ammo <= 0 ? " · пусто" : "");
        }

        // ---------- гейты вооружения (вкл/выкл классов по выделению, DESIGN §2.11) ----------

        /// <summary>Класс оружия (чистое ядро на примитивах — тестируется; WeaponInfo —
        /// ScriptableObject, в раннере не создаётся): приоритет — ядерное &gt; бомбы &gt;
        /// пушки &gt; ракеты по домену. Ракеты без явного домена — «по земле» (основная
        /// масса АС-средств); None — не гейтится.</summary>
        public static WeaponClass ClassOf(bool nuclear, bool bomb, bool glideBomb, bool gun,
            bool missile, float antiAir, float antiSurface)
        {
            if (nuclear)
                return WeaponClass.Nuclear;
            if (bomb || glideBomb)
                return WeaponClass.Bomb;
            if (gun)
                return WeaponClass.Gun;
            if (missile)
                return antiAir > antiSurface ? WeaponClass.MissileAA : WeaponClass.MissileAS;
            return WeaponClass.None;
        }

        /// <summary>Класс станции по её WeaponInfo.</summary>
        public static WeaponClass ClassOf(WeaponInfo wi) =>
            wi == null ? WeaponClass.None
            : ClassOf(wi.nuclear, wi.bomb, wi.glideBomb, wi.gun, wi.missile,
                wi.effectiveness.antiAir, wi.effectiveness.antiSurface);

        /// <summary>Кнопочный лейбл класса: «ЯД», «Бомбы», «Пушки», «Рак.ВВ», «Рак.АС».</summary>
        public static string ClassLabel(WeaponClass cls) => cls switch
        {
            WeaponClass.Nuclear => "ЯД",
            WeaponClass.Bomb => "Бомбы",
            WeaponClass.Gun => "Пушки",
            WeaponClass.MissileAA => "Рак.ВВ",
            WeaponClass.MissileAS => "Рак.АС",
            _ => "?",
        };

        // ---------- переводы состояний AI (инфопанель: строка «состояние») ----------

        /// <summary>Нативные stateDisplayName пилота (PilotBaseState.GetCurrentState) —
        /// английские фразы; неизвестное значение возвращаем как есть.</summary>
        private static readonly Dictionary<string, string> RuPilotStates =
            new Dictionary<string, string>
            {
                { "Awaiting Cargo Mission", "ожидает грузомиссию" },
                { "Bombing", "бомбометание" },
                { "Breaking Off Attack", "выходит из атаки" },
                { "Delivering Naval Supplies", "доставка снабжения (море)" },
                { "Delivering Supplies", "доставка снабжения" },
                { "Evading Missiles", "уклонение от ракет" },
                { "Flying To Target", "идёт к цели" },
                { "Glide Bombing", "сброс планирующих бомб" },
                { "Gunship", "огонь с висения" },
                { "Joining Pattern", "заход в круг посадки" },
                { "No Target", "нет цели" },
                { "Retreating to Standoff", "отходит на дистанцию" },
                { "Short Landing Approach", "короткий заход" },
                { "Short Landing Touchdown", "короткая посадка: касание" },
                { "Short Landing", "короткая посадка" },
                { "Transporting Vehicles (contact)", "перевозка техники (контакт)" },
                { "Transporting Vehicles (objective)", "перевозка техники (цель)" },
                { "Using Boresight Weapon", "оружие по оси" },
                { "Using Energy Weapon", "энергооружие" },
                { "Using Guns", "стрельба из пушки" },
                { "Using Laser Guided Weapon", "лазерное оружие" },
                { "Using Missiles", "пуски ракет" },
                { "Using Radar Jammer", "РЭБ-подавление" },
                { "continuing to search for airbase", "ищет аэродром" },
                { "defensive jamming", "защитное РЭБ" },
                { "entering takeoff runway", "выруливает на полосу" },
                { "landing", "посадка" },
                { "orienting to nearest airbase", "на ближайший аэродром" },
                { "parked", "на стоянке" },
                { "player controlled", "пилот-игрок" },
                { "taking off", "взлёт" },
                { "taxiing to resupply", "руление на сервис" },
                { "taxiing to runway", "руление на полосу" },
                { "taxiing to takeoff", "руление на взлёт" },
                { "taxiing", "руление" },
                { "touching down", "касание" },
                { "transporting cargo", "перевозка груза" },
                { "waiting at runway crossing", "ждёт на пересечении полос" },
                { "waiting for takeoff clearance", "ждёт разрешения на взлёт" },
                { "waiting to land", "ждёт посадки" },
            };

        /// <summary>Нативные состояния ShipAI (enum ShipAIState).</summary>
        private static readonly Dictionary<string, string> RuShipStates =
            new Dictionary<string, string>
            {
                { "launching", "выход в море" },
                { "holding", "держит позицию" },
                { "navigating", "в переходе" },
                { "attacking", "атака" },
                { "landing", "посадка/высадка" },
                { "unloading", "разгрузка" },
                { "returning", "возврат" },
                { "docking", "стыковка" },
                { "docked", "у причала" },
            };

        /// <summary>Состояние пилота по-русски (нативные stateDisplayName);
        /// неизвестное — как есть (честный фолбэк, не пустышка).</summary>
        public static string RuPilotState(string s) =>
            string.IsNullOrEmpty(s) ? "" :
            RuPilotStates.TryGetValue(s, out string ru) ? ru : s;

        /// <summary>Состояние корабля по-русски (нативный enum ShipAIState).</summary>
        public static string RuShipState(string s) =>
            string.IsNullOrEmpty(s) ? "" :
            RuShipStates.TryGetValue(s, out string ru) ? ru : s;
    }

    /// <summary>Класс вооружения для гейтов (DESIGN §2.11): биты — выключенные
    /// классы складываются в маску per-unit. None — «прочее», гейтом не владеет.</summary>
    [Flags]
    internal enum WeaponClass : byte
    {
        None = 0,
        Nuclear = 1,
        Bomb = 2,
        Gun = 4,
        MissileAA = 8,
        MissileAS = 16,
    }

    /// <summary>Домены целей оружия (битовая маска) для тега в сводке вооружения группы.</summary>
    [Flags]
    internal enum WeaponDomain : byte
    {
        None = 0,
        Air = 1,
        Surface = 2,
        Missile = 4,
        Nuclear = 8,
        Jammer = 16,
    }

    /// <summary>Сводка одного типа оружия по выделению: имя, заряд/ёмкость по группе,
    /// число носителей (юнитов с хотя бы одной станцией этого оружия).</summary>
    internal struct WeaponTally
    {
        public string Name;
        public int Ammo;
        public int Full;
        public int Carriers;
        public WeaponDomain Domains;
    }

    /// <summary>Накопитель сводки вооружения выделения: станции складываются по имени
    /// оружия, носители считаются по юнитам (две станции AIM-9 на одном борту — один
    /// носитель), BeginUnit разделяет юнитов. Строки идут в порядке первого появления
    /// (порядок станций нагрузки), не сортируются — состав стабилен между кадрами.
    /// Чистая логика без движка — тестируется раннером.</summary>
    internal sealed class WeaponTallyAccumulator
    {
        public readonly List<WeaponTally> Rows = new List<WeaponTally>(16);
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(16);
        private readonly HashSet<string> _unitNames = new HashSet<string>();

        public void Clear()
        {
            Rows.Clear();
            _index.Clear();
            _unitNames.Clear();
        }

        /// <summary>Начало следующего юнита: имена его станций снова считаются
        /// новыми носителями (по +1 на юнит на имя).</summary>
        public void BeginUnit() => _unitNames.Clear();

        /// <summary>Сложить станцию; null/пустое имя и нулевая ёмкость игнорируются.</summary>
        public void AddStation(string name, int ammo, int full, WeaponDomain domains)
        {
            if (string.IsNullOrEmpty(name) || full <= 0)
                return;
            if (!_index.TryGetValue(name, out int i))
            {
                i = Rows.Count;
                Rows.Add(new WeaponTally { Name = name, Domains = domains });
                _index[name] = i;
            }
            WeaponTally row = Rows[i];
            row.Ammo += ammo;
            row.Full += full;
            row.Domains |= domains;
            if (_unitNames.Add(name))
                row.Carriers++;
            Rows[i] = row;
        }
    }
}
