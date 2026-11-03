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

        public static AiProfile Load()
        {
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "AI");
            string path = Path.Combine(dir, "commander.json");
            try
            {
                if (File.Exists(path))
                {
                    var profile = JsonConvert.DeserializeObject<AiProfile>(File.ReadAllText(path));
                    if (profile != null)
                    {
                        Log.Info("AI: профиль загружен — " + path);
                        return profile;
                    }
                }

                var fallback = new AiProfile();
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(fallback, Formatting.Indented));
                Log.Info("AI: профиль не найден, создан образец с дефолтами — " + path);
                return fallback;
            }
            catch (System.Exception e)
            {
                Log.Warn("AI: профиль не прочитан (" + e.Message + ") — работаю на дефолтах");
                return new AiProfile();
            }
        }

        /// <summary>Сохранить профиль (тумблер «AI-командир» панели Скримиша).
        /// Перечитывается сервисом при следующем входе в миссию (Reset в sceneLoaded).</summary>
        public static void Save(AiProfile profile)
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "AI");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "commander.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(profile, Formatting.Indented));
            }
            catch (System.Exception e)
            {
                Log.Warn("AI: профиль не записан (" + e.Message + ")");
            }
        }
    }
}
