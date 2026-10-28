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
            VehicleType.MBT => Loc.Tr("ТНК"),
            VehicleType.AFV => Loc.Tr("ББМ"),
            VehicleType.LCV => Loc.Tr("ЛБМ"),
            VehicleType.ART => Loc.Tr("АРТ"),
            VehicleType.AAA => Loc.Tr("ЗУ"),
            VehicleType.IR_SAM => Loc.Tr("ПЗРК"),
            VehicleType.R_SAM => Loc.Tr("ЗРК"),
            VehicleType.RDR => Loc.Tr("РЛС"),
            VehicleType.TRUCK => Loc.Tr("ГРЗ"),
            VehicleType.UGV => Loc.Tr("БНП"),
            _ => "",
        };

        /// <summary>Бейдж типа корабля (нативный enum ShipType): русские
        /// аббревиатуры классов надводных сил.</summary>
        public static string BadgeOf(ShipType type) => type switch
        {
            ShipType.CV => Loc.Tr("АВ"),   // авианосец
            ShipType.LHA => Loc.Tr("УДК"), // универсальный десантный корабль
            ShipType.LFD => Loc.Tr("ДКД"), // доковый корабль десанта
            ShipType.DDG => Loc.Tr("ЭМ"),  // эсминец
            ShipType.FFG => Loc.Tr("ФР"),  // фрегат
            ShipType.FFL => Loc.Tr("КР"),  // корвет
            ShipType.LC => Loc.Tr("ДКА"),  // десантный катер
            ShipType.PB => Loc.Tr("КА"),   // катер
            _ => "",
        };

        /// <summary>Полное имя типа корабля для инфоокна.</summary>
        public static string TypeNameOf(ShipType type) => type switch
        {
            ShipType.CV => Loc.Tr("авианосец"),
            ShipType.LHA => Loc.Tr("универсальный десантный корабль"),
            ShipType.LFD => Loc.Tr("доковый корабль десанта"),
            ShipType.DDG => Loc.Tr("эсминец"),
            ShipType.FFG => Loc.Tr("фрегат"),
            ShipType.FFL => Loc.Tr("корвет"),
            ShipType.LC => Loc.Tr("десантный катер"),
            ShipType.PB => Loc.Tr("патрульный катер"),
            _ => Loc.Tr("корабль"),
        };

        /// <summary>Полное имя типа наземки для инфоокна.</summary>
        public static string TypeNameOf(VehicleType type) => type switch
        {
            VehicleType.MBT => Loc.Tr("танк"),
            VehicleType.AFV => Loc.Tr("боевая бронированная машина"),
            VehicleType.LCV => Loc.Tr("лёгкая боевая машина"),
            VehicleType.ART => Loc.Tr("артиллерия"),
            VehicleType.AAA => Loc.Tr("зенитная установка"),
            VehicleType.IR_SAM => Loc.Tr("ПЗРК"),
            VehicleType.R_SAM => Loc.Tr("ЗРК"),
            VehicleType.RDR => Loc.Tr("РЛС"),
            VehicleType.TRUCK => Loc.Tr("грузовик"),
            VehicleType.UGV => Loc.Tr("беспилотная наземная платформа"),
            _ => Loc.Tr("техника"),
        };

        /// <summary>Ключ роли авиации — нелокализуемые метки: по ним же switch
        /// полного имени ниже, поэтому сами через Tr не проходят.</summary>
        private static string RoleKey(RoleIdentity role, TypeIdentity type)
        {
            float aa = role.antiAir, asu = role.antiSurface;
            if (aa < 0.25f && asu < 0.25f)
                return type.radar >= 0.5f ? "РЛС" : "ТР";
            if (aa >= 0.45f && asu >= 0.45f)
                return "М";
            return aa > asu ? "И" : "У";
        }

        /// <summary>Бейдж роли авиации: истребитель/ударный/многоролевой выводится
        /// из roleIdentity (какой цели угрожает), слабый бойец — ДРЛО/транспорт
        /// по typeIdentity (кем является).</summary>
        public static string AirBadge(RoleIdentity role, TypeIdentity type) =>
            Loc.Tr(RoleKey(role, type));

        /// <summary>Полное имя роли авиации для инфоокна.</summary>
        public static string AirRoleName(RoleIdentity role, TypeIdentity type) =>
            RoleKey(role, type) switch
            {
                "И" => Loc.Tr("истребитель"),
                "У" => Loc.Tr("ударный"),
                "М" => Loc.Tr("многоролевой"),
                "РЛС" => Loc.Tr("ДРЛО"),
                "ТР" => Loc.Tr("транспорт"),
                _ => Loc.Tr("авиация"),
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
                return Loc.Tr("РЕБ");
            bool air = (d & WeaponDomain.Air) != 0, srf = (d & WeaponDomain.Surface) != 0;
            string core;
            if (air && srf) core = Loc.Tr("У");
            else if (air) core = Loc.Tr("АА");
            else if (srf) core = Loc.Tr("АЗ");
            else if ((d & WeaponDomain.Missile) != 0) core = Loc.Tr("ПР");
            else return "";
            return (d & WeaponDomain.Nuclear) != 0 ? Loc.Tr("Я") + " " + core : core;
        }

        /// <summary>Строка сводки группы: «АА AIM-120C: 12/16 · 4 носителя»;
        /// полностью расстрелянный тип помечается «пусто» (пора на перезарядку).</summary>
        public static string FormatWeaponRow(WeaponTally t)
        {
            string tag = DomainTag(t.Domains);
            string head = string.IsNullOrEmpty(tag) ? t.Name : tag.PadRight(3) + " " + t.Name;
            string row = Loc.F("{0}: {1}/{2} · {3} {4}", head, t.Ammo, t.Full, t.Carriers,
                Loc.Plural(t.Carriers, "носитель", "носителя", "носителей"));
            return t.Ammo <= 0 ? row + Loc.Tr(" · пусто") : row;
        }

        // ---------- hover-карточка определения (SupCom-инфо у плитки покупки) ----------

        /// <summary>Верхняя строка карточки: класс/роль · цена · оценка времени
        /// (фабрика платит константные SecondsPerItem на единицу; стройка — темп
        /// одного инженера, несколько ускоряют). seconds&lt;=0 — время не показываем.</summary>
        public static string FormatTipClass(string cls, float value, float seconds, bool building)
        {
            string head = cls + " · " + value.ToString("F0");
            if (seconds <= 0f)
                return head;
            return head + Loc.F(" · {0}{1} с", building ? Loc.Tr("стройка ~") : "~", seconds.ToString("0"));
        }

        /// <summary>Строки ТТХ карточки закупки — ФАКТ-НА-СТРОКУ (плейтест: без
        /// склейки всего через «·» и обрезок): скорость / обзор / броня·экипаж·масса.
        /// Только ненулевые факты; км с десятыми — инвариантно. Пустой список — данных нет.</summary>
        public static List<string> TipStatRows(float visionM, float speedMs,
            float armorTier, float crew, float massKg)
        {
            var rows = new List<string>(3);
            if (speedMs > 0f)
                rows.Add(Loc.F("скорость {0} м/с", speedMs.ToString("0")));
            if (visionM > 0f)
            {
                if (visionM >= 1000f)
                    rows.Add(Loc.F("обзор {0} км",
                        (visionM / 1000f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
                else
                    rows.Add(Loc.F("обзор {0} м", visionM.ToString("0")));
            }
            string rest = "";
            if (armorTier > 0f)
                rest = Loc.F("броня {0}", armorTier.ToString("0"));
            if (crew > 0f)
                rest += (rest.Length > 0 ? " · " : "") + Loc.F("экипаж {0}", crew.ToString("0"));
            if (massKg > 1000f)
                rest += (rest.Length > 0 ? " · " : "") + Loc.F("масса ≈{0} т",
                    (massKg / 1000f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            else if (massKg > 0f)
                rest += (rest.Length > 0 ? " · " : "") + Loc.F("масса ≈{0} кг", massKg.ToString("0"));
            if (rest.Length > 0)
                rows.Add(rest);
            return rows;
        }

        /// <summary>Строка оружия карточки: «АА AIM-120C ×4» — домен-тег + имя +
        /// суммарный боезапас (у определения нагрузка всегда полная, «4/4» не нужно).</summary>
        public static string FormatTipWeaponRow(WeaponTally t)
        {
            string tag = DomainTag(t.Domains);
            string head = string.IsNullOrEmpty(tag) ? t.Name : tag.PadRight(3) + " " + t.Name;
            return Loc.F("{0} ×{1}", head, t.Full);
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
            WeaponClass.Nuclear => Loc.Tr("ЯД"),
            WeaponClass.Bomb => Loc.Tr("Бомбы"),
            WeaponClass.Gun => Loc.Tr("Пушки"),
            WeaponClass.MissileAA => Loc.Tr("Рак.ВВ"),
            WeaponClass.MissileAS => Loc.Tr("Рак.АС"),
            _ => "?",
        };

        // ---------- правила применения по типам вооружения (v0.106, DESIGN §2.11) ----------

        /// <summary>Правило чипа типа для одной платформы (platformAir — авиация):
        /// какие домены целей разрешены этому юниту для этого типа вооружения.
        /// Both — ограничений нет (сервис такое правило не хранит), None — выкл.
        /// Платформенные пресеты — макрос: «пуск только с наземных» = авиации выкл,
        /// наземке свободно. Чистое ядро — тестируется.</summary>
        public static TargetDomain RuleFor(TypeGateState state, bool platformAir) => state switch
        {
            TypeGateState.SurfaceOnly => TargetDomain.Surface,
            TypeGateState.AirOnly => TargetDomain.Air,
            TypeGateState.GroundLaunch => platformAir ? TargetDomain.None : TargetDomain.Both,
            TypeGateState.AirLaunch => platformAir ? TargetDomain.Both : TargetDomain.None,
            TypeGateState.Off => TargetDomain.None,
            _ => TargetDomain.Both,
        };

        /// <summary>Сводка правил выделения к состоянию чипа: в каждой платформенной
        /// группе (авиация/прочее) правило едино; обе группы: пара «одна выкл, другая
        /// свободна» — платформенный пресет, прочие расхождения — null (±, вперемешку).
        /// Одна группа — её правило и есть состояние; пусто — None.</summary>
        public static TypeGateState? AggregateState(List<(bool platformAir, TargetDomain rule)> units)
        {
            TargetDomain? airRule = null, groundRule = null;
            foreach (var (platformAir, rule) in units)
            {
                if (platformAir)
                {
                    if (airRule == null)
                        airRule = rule;
                    else if (airRule != rule)
                        return null;
                }
                else
                {
                    if (groundRule == null)
                        groundRule = rule;
                    else if (groundRule != rule)
                        return null;
                }
            }
            if (airRule != null && groundRule != null)
            {
                TargetDomain a = airRule.Value, g = groundRule.Value;
                if (a == g)
                    return StateOfRule(a);
                if (a == TargetDomain.None && g == TargetDomain.Both)
                    return TypeGateState.GroundLaunch;
                if (g == TargetDomain.None && a == TargetDomain.Both)
                    return TypeGateState.AirLaunch;
                return null;
            }
            if (airRule != null)
                return StateOfRule(airRule.Value);
            if (groundRule != null)
                return StateOfRule(groundRule.Value);
            return TypeGateState.None;
        }

        /// <summary>Правило-без-платформы → состояние: Both → None, Surface →
        /// SurfaceOnly, Air → AirOnly, 0 → Off.</summary>
        private static TypeGateState StateOfRule(TargetDomain rule) => rule switch
        {
            TargetDomain.Both => TypeGateState.None,
            TargetDomain.Surface => TypeGateState.SurfaceOnly,
            TargetDomain.Air => TypeGateState.AirOnly,
            _ => TypeGateState.Off,
        };

        /// <summary>Следующее состояние цикла чипа (вперёд/назад); вперемешку (null)
        /// трактуется как None — клик начинает цикл с начала.</summary>
        public static TypeGateState Next(TypeGateState? current, bool forward)
        {
            var order = new[]
            {
                TypeGateState.None, TypeGateState.SurfaceOnly, TypeGateState.AirOnly,
                TypeGateState.GroundLaunch, TypeGateState.AirLaunch, TypeGateState.Off,
            };
            int i = Array.IndexOf(order, current ?? TypeGateState.None);
            i = forward ? (i + 1) % order.Length : (i + order.Length - 1) % order.Length;
            return order[i];
        }

        /// <summary>Суффикс чипа типа: пусто (без правил), «·Н»/«·В» (по наземным/
        /// воздушным целям), «·з»/«·в» (пуск только с наземных/воздушных), «×» (выкл),
        /// «±» (вперемешку). Символы Latin-1 (игровой шрифт), не переводятся —
        /// как «+/−» класс-гейтов.</summary>
        public static string TypeGateSuffix(TypeGateState? state) => state switch
        {
            null => "±",
            TypeGateState.None => "",
            TypeGateState.SurfaceOnly => "·Н",
            TypeGateState.AirOnly => "·В",
            TypeGateState.GroundLaunch => "·з",
            TypeGateState.AirLaunch => "·в",
            _ => "×",
        };

        /// <summary>Игроковая фраза состояния для Notice («AGM-48: только по наземным
        /// целям») — локализуется.</summary>
        public static string TypeGateLabel(TypeGateState state) => state switch
        {
            TypeGateState.SurfaceOnly => Loc.Tr("только по наземным целям"),
            TypeGateState.AirOnly => Loc.Tr("только по воздушным целям"),
            TypeGateState.GroundLaunch => Loc.Tr("пуск только с наземных"),
            TypeGateState.AirLaunch => Loc.Tr("пуск только с воздушных"),
            TypeGateState.Off => Loc.Tr("запрещено"),
            _ => Loc.Tr("без ограничений"),
        };

        // ---------- переводы состояний AI (инфопанель: строка «состояние») ----------

        /// <summary>Нативные stateDisplayName пилота (PilotBaseState.GetCurrentState) —
        /// английские фразы; неизвестное значение возвращаем как есть. Словарь даёт
        /// русскую строку-ключ, дальше Loc.Tr (en-локаль возвращает оригинал).</summary>
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
            RuPilotStates.TryGetValue(s, out string ru) ? Loc.Tr(ru) : s;

        /// <summary>Состояние корабля по-русски (нативный enum ShipAIState).</summary>
        public static string RuShipState(string s) =>
            string.IsNullOrEmpty(s) ? "" :
            RuShipStates.TryGetValue(s, out string ru) ? Loc.Tr(ru) : s;
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

    /// <summary>Домен ЦЕЛИ для правил применения по типам (v0.106): Air — летящий
    /// борт/ракета, Surface — техника/корабли/постройки/паркованные. Правило — биты
    /// разрешённых доменов: 0 — выкл, оба — свободно. Не путать с WeaponDomain
    /// (домены ЭФФЕКТИВНОСТИ оружия для тегов сводки).</summary>
    [Flags]
    internal enum TargetDomain : byte
    {
        None = 0,
        Air = 1,
        Surface = 2,
        Both = Air | Surface,
    }

    /// <summary>Состояние чипа типа вооружения (цикл ЛКМ/колесом, DESIGN §2.11):
    /// без правил → только по наземным → только по воздушным → пуск только
    /// с наземных → пуск только с воздушных → выкл.</summary>
    internal enum TypeGateState : byte
    {
        None = 0,
        SurfaceOnly,
        AirOnly,
        GroundLaunch,
        AirLaunch,
        Off,
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
