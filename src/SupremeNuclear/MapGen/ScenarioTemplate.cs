using System.Collections.Generic;
using System.IO;
using UnityEngine;
using BepInEx;
using Newtonsoft.Json;

namespace SupremeNuclear
{
    /// <summary>
    /// Внешний шаблон сценария для генератора миссий: все числа здесь, не в коде.
    /// Файл: BepInEx/plugins/SupremeNuclear/MapGen/&lt;Template&gt;.json; отсутствующий
    /// файл создаётся-образец при первой генерации. Роли слотов НЕ задаются шаблоном —
    /// они рождаются из географии (позиция + ближайшие месторождения, канон DESIGN §2.2
    /// «принцип без жёстких ролей»): стартовые комплекты у всех слотов одинаковые.
    /// </summary>
    public sealed class ScenarioTemplate
    {
        public sealed class StarterItem
        {
            /// <summary>jsonKey из Encyclopedia; пусто — авто-подбор по role.</summary>
            public string jsonKey;
            /// <summary>engineer | tank | depot — авто-подбор ростера, если jsonKey пуст.</summary>
            public string role;
            public int count = 1;
        }

        /// <summary>
        /// Собственный террейн (спец-карта, рантайм-генерация): не null — сценарий
        /// строится на сгенерированном рельефе (MapKey SN-Terrain-…), а не на стоковой карте.
        /// </summary>
        public sealed class TerrainSettings
        {
            public float sizeM = 48000f;         // сторона карты, м (явная ручка; нативный эталон 81.9 км)
            public float stepM = 200f;           // клетка высот/меша, м
            public float plainHeight = 40f;      // базовая высота равнин над морем
            public float gentleAmp = 70f;        // амплитуда холмов равнин
            public float plainFreq = 0.03f;      // частота холмов (1/клетку; 0.03 ≈ волна 6.5 км)
            public float hillsAmp = 120f;        // холмы центра (без них середина «мёртвая»)
            public float hillsFreq = 0.012f;     // частота холмов центра (≈ волна 8 км)
            public float terraceStepM = 70f;     // террасы SupCom (0 — выкл): высота ступени-
                                                 // плато; переходы-рампы — половина ступени
                                                 // (юзер-канон 2026-10-30: «плоскости и плато»)
            public float coastWobble = 0.12f;    // возмущение радиуса кромки гор (изрезанный берег,
                                                 // не ровная рама; 0 = идеальное кольцо)
            public float mountainHeight = 900f;  // высота краевых гор
            public float mountainStart = 0.62f;  // доля половины размера: дальше — горы
            public float ridgeHeight = 400f;     // редкие внутренние хребты (авиационная игра)
            public float ridgeMaskStart = 0.55f; // порог маски пятен хребтов (fBm 0..1)
            public float ridgeMaskEnd = 0.72f;
            public float seaLevelM = 0f;         // ниже — вода (y=0 = уровень моря)
            public float steepDeg = 25f;         // круче — непроходимо (маска Steep)
            public float edgeM = 2000f;          // краевая зона без раскладки
            // Палитра высот для цвета террейна/миникарты (hex, от низин к вершинам).
            public string[] palette =
                { "#3f5b2e", "#6b7a3a", "#9b8f5c", "#8a8a8a", "#d9dde0" };
            public string waterColor = "#1d4e6e";
        }

        /// <summary>Секция спец-карты: не null — сценарий строится на собственном
        /// рельефе (MapKey SN-Terrain…). Дефолт ВКЛЮЧЁН (основной путь мода);
        /// «дуэль на стоковой карте» — убрать секцию/оставить null в JSON шаблона.</summary>
        public TerrainSettings terrain = new TerrainSettings();

        public float baseInset = 0.40f;    // доля полудиагонали карты: где стоят базы
        public int depositCount = 14;      // зеркальных ПАР точек массы (T1)
        public int centerRichCount = 2;    // пар жирного центра (T2)
        public int coalPerSide = 2;        // зеркальных ПАР угольных точек — у баз (бутстрап энергии, v3)
        public float depositSpacing = 3000f;
        public float baseSearchDeg = 45f;  // сектор поиска пригодной площадки вокруг оси
        public float startFunds = 10000f; // запас на бутстрап: уголь + мекс + депо (поднят с 5000 для тестов, 2026-10-30)
        public float runwayLength = 1800f; // стартовая ВПП слота
        public float runwayWidth = 45f;
        public bool commander = true;      // командирский борт — условие победы «убей борта» (DESIGN §2.1)
        public string commanderJsonKey = ""; // модель борта; пусто = самый дорогой самолёт энциклопедии
        public float commanderFuel = 1f;

        // Адаптивность «под количество игроков» (юзер-канон 2026-10-31, уточнён
        // им же: размер — отдельная ручка гибкости): players задаёт РАСКЛАДКУ —
        // 2 команды по players/2 баз зеркально, точки/уголь ×пары; РАЗМЕР карты —
        // явный terrain.sizeM (пресеты размеров под рандом-ладдер — долг лобби).
        public int players = 2;
        public float baseSpreadM = 2800f;  // разбег баз стороны по перпендикуляру к оси
        public float engineerAirdropAltM = 150f; // аирдроп стартовой техники (нативные парашюты)
        public float depositPadRadiusM = 130f; // штамп ровной площадки под точку массы/угля

        public List<StarterItem> starter = DefaultStarter();

        /// <summary>Число «пар» (баз на сторону): players/2, минимум 1. Чистая.</summary>
        public static int PairsOf(int players) => Mathf.Max(1, players / 2);

        public static ScenarioTemplate Load(string name)
        {
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "MapGen");
            string path = Path.Combine(dir, name + ".json");
            try
            {
                if (File.Exists(path))
                {
                    var template = JsonConvert.DeserializeObject<ScenarioTemplate>(File.ReadAllText(path));
                    if (template != null)
                    {
                        Log.Info("MapGen: шаблон загружен — " + path);
                        return template;
                    }
                }

                var fallback = new ScenarioTemplate();
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(fallback, Formatting.Indented));
                Log.Info("MapGen: шаблон не найден, создан образец с дефолтами — " + path);
                return fallback;
            }
            catch (System.Exception e)
            {
                Log.Error("MapGen: ошибка шаблона " + path + ": " + e.Message + " — использую дефолты");
                return new ScenarioTemplate();
            }
        }

        private static List<StarterItem> DefaultStarter() => new List<StarterItem>
        {
            // Канон SupCom: старт от «кома» — у нас его интерим, дроп инженеров + запас
            // ресурсов (startFunds/energyStart); первую фабрику и оборону игрок строит сам.
            new StarterItem { role = "engineer", count = 5 },
        };
    }
}
