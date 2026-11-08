using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace SupremeNuclear
{
    /// <summary>
    /// Балансный профиль AI-командира v1 (DESIGN §3-3: наземка-only): все числа
    /// здесь, не в коде (инвариант §7.11 — AI читает внешние профили, как
    /// EconomyProfile). Файл: BepInEx/plugins/SupremeNuclear/AI/commander.json;
    /// при отсутствии создаётся образец с дефолтами. Определения depotJsonKey/
    /// tankJsonKey: null = автоподбор (корпус-депо / самый дешёвый MBT).
    /// </summary>
    public sealed class AiProfile
    {
        public bool enabled = true;
        public float tickSec = 1f;          // период решений (троттл, не каждый кадр)

        // Экономика v3: план от точек месторождений (уголь у базы → экстракторы → депо).
        public int powersInitial = 2;       // обычных генераторов в плане (после депо)
        public int extractorsTarget = 3;    // мексов до депо
        public int powersPerWave = 1;       // +генератор к каждой волне (энергия догоняет армию)

        // Производство (очередь мода в депо; деньги — та же экономика).
        public int maxQueue = 3;            // позиций в депо
        public float massReserveFactor = 1.5f; // заказ при массе ≥ стоимость × фактор
        public int engineersMin = 2;        // держать инженеров не меньше (погибли все → экономика мертва)
        public string engineerJsonKey;      // переопределение автоподбора (первый TRUCK)

        // Волны (DESIGN §3-3 «экономика → фабрика → волны + гарнизон»).
        public int waveSize = 6;            // столько собравшихся → волна не дожидаясь таймера
        public float waveIntervalSec = 210f;
        public int garrisonSize = 2;        // боевых всегда остаётся защищать базу

        // Геометрия (метры).
        public float buildRadiusMinM = 200f;
        public float buildRadiusMaxM = 380f; // веер кандидатов площадок вокруг HQ
        public float rallyOffsetM = 500f;    // точка сбора: от HQ в сторону врага
        public float gatherRadiusM = 1300f;  // «у сбора» — кандидаты волны

        // Переопределения автоподбора (jsonKey определения).
        public string depotJsonKey;
        public string tankJsonKey;
        public string fighterJsonKey;   // борт авиакрыла; пусто = самый дешёвый допустимый из каталога ангара

        // Авиакрыло v1 (2026-10-31): CAP над базой + перехват + удары по экономике.
        public bool airEnabled = true;
        public int airCap = 4;              // держать живых бортов
        public float airReserveFactor = 1.5f; // заказ при массе ≥ цена × фактор
        public float capRadiusM = 3500f;    // кольцо CAP над своей базой
        public int capPoints = 4;
        public float interceptRadiusM = 6000f; // перехват воздушных угроз у базы
        public float airStrikeIntervalSec = 240f;
        public int airStrikeSize = 2;       // бортов в авиаударе по экономике

        // Боевая реакция v2 (§12-49, v0.160): защита экономики и отступление волн.
        public float econGuardRadiusM = 1400f; // враг ближе к своей точке экономики — перехват гарнизоном дальних точек
        public float econGuardScanSec = 8f;    // троттл скана рейдов (пойдя по всем своим зданиям)
        public float econGuardMinDistM = 1200f; // точки ближе к базе уже под гарнизоном — не дублируем реакцию
        public int econGuardMaxUnits = 3;      // сколько стоящих юнитов посылать на перехват
        public float retreatAliveFrac = 0.35f; // волна потеряла больше (1 − frac) — остатки отступают к базе
        public bool fleetsInWaves = false;     // корабли в волнах (руление флота нативное; эксперимент — плейтеста не было)

        /// <summary>Пресеты лобби (aiType слота, DESIGN §2.12 ветки 2–3): файл
        /// AI/&lt;type&gt;.json, «commander» — базовый. Значения — осознанные
        /// пресеты сложности/доктрины, тюнятся игроком правкой JSON (§7.11).</summary>
        public static readonly string[] Types =
        {
            "commander", "commander-easy", "commander-hard",
            "commander-econ", "commander-rush"
        };

        /// <summary>Метка пресета для UI лобби (контекст-меню ИИ-слота).</summary>
        public static string LabelOf(string type)
        {
            switch (type)
            {
                case "commander-easy": return Loc.Tr("Лёгкий");
                case "commander-hard": return Loc.Tr("Жёсткий");
                case "commander-econ": return Loc.Tr("Экономист");
                case "commander-rush": return Loc.Tr("Агрессор");
                default: return Loc.Tr("Стандарт");
            }
        }

        /// <summary>Профиль пресета: AI/&lt;type&gt;.json; нет файла — дефолты
        /// класса С ЗНАЧЕНИЯМИ ПРЕСЕТА (образцы файлов создаются на диск, чтобы
        /// игрок видел и правил все пресеты). Неизвестный тип — commander.json.</summary>
        public static AiProfile Load(string type)
        {
            if (string.IsNullOrEmpty(type) || System.Array.IndexOf(Types, type) < 0)
                type = "commander";
            AiProfile profile = PresetDefaults(type);
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "AI");
            string path = Path.Combine(dir, type + ".json");
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<AiProfile>(File.ReadAllText(path));
                    if (loaded != null)
                    {
                        Log.Info("AI: профиль загружен — " + path);
                        return loaded;
                    }
                }
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(profile, Formatting.Indented));
                Log.Info("AI: профиль '" + type + "' не найден — создан образец пресета: " + path);
                return profile;
            }
            catch (System.Exception e)
            {
                Log.Warn("AI: профиль '" + type + "' не прочитан (" + e.Message + ") — пресет-дефолты");
                return profile;
            }
        }

        /// <summary>Значения-пресеты по типу (осознанные, не «все дефолты»):
        /// сложность = темп решений/экономики/волн; доктрина = вес эко против атаки.</summary>
        private static AiProfile PresetDefaults(string type)
        {
            var p = new AiProfile();
            switch (type)
            {
                case "commander-easy": // лёгкий: медленный, экономит меньше, волн редко
                    p.tickSec = 1.5f;
                    p.maxQueue = 2;
                    p.extractorsTarget = 2;
                    p.powersInitial = 1;
                    p.waveSize = 8;
                    p.waveIntervalSec = 300f;
                    p.garrisonSize = 3;
                    p.airCap = 2;
                    p.airStrikeIntervalSec = 360f;
                    break;
                case "commander-hard": // жёсткий: быстрый темп, ранние частые волны
                    p.tickSec = 0.5f;
                    p.maxQueue = 4;
                    p.extractorsTarget = 4;
                    p.powersInitial = 3;
                    p.waveSize = 5;
                    p.waveIntervalSec = 150f;
                    p.garrisonSize = 2;
                    p.airCap = 6;
                    p.airStrikeIntervalSec = 180f;
                    break;
                case "commander-econ": // «Экономист» (§2.12 ветка 2): эко-приоритет, поздние волны
                    p.tickSec = 0.75f;
                    p.extractorsTarget = 5;
                    p.powersInitial = 3;
                    p.powersPerWave = 2;
                    p.waveSize = 8;
                    p.waveIntervalSec = 300f;
                    p.garrisonSize = 3;
                    p.engineersMin = 3;
                    p.airCap = 4;
                    break;
                case "commander-rush": // «Агрессор» (§2.12 ветка 3): ранние мелкие волны, малый гарнизон
                    p.tickSec = 0.5f;
                    p.extractorsTarget = 2;
                    p.powersInitial = 1;
                    p.maxQueue = 3;
                    p.waveSize = 4;
                    p.waveIntervalSec = 120f;
                    p.garrisonSize = 1;
                    p.airCap = 2;
                    p.airStrikeIntervalSec = 200f;
                    break;
            }
            return p;
        }
    }
}
