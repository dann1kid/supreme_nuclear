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
            public float sizeM = 81920f;        // сторона карты, м: РОДНОЙ размер NO (81.9 км;
                                                 // плейтест 2026-11-02: 40 км мелко для авиации NO)
            public float stepM = 80f;           // клетка высот/меша, м: 81.9 км / 80 = сетка 1024²
                                                // (было 200 — рельеф «размазывало», плейтест 2026-11-02)

            // ---- Стиль карты (Neroxis-канон v3): морфология воды/суши ----
            /// <summary>land — суша с редкими озёрами (краевые горы); coast —
            /// континент в океане («окружающая вода»); seas — полуморская (крупные
            /// внутренние моря, суша доминирует); islands — архипелаг в океане
            /// (острова баз + мелкие острова под мексовку).</summary>
            public string style = "land";
            /// <summary>Доля воды 0..1 внутри стиля: сдвигает порог маски воды
            /// (у coast — радиус континента, у seas/islands — порог пятен).</summary>
            public float waterAmount = 0f;
            /// <summary>Симметрия рельефа (Neroxis-канон: усреднение с отражением —
            /// строгая симметрия и ГЛАДКИЕ оси без изломов-швов; юзер 2026-11-03):
            /// mirror — 1 ось X (канон дуэлей); quadrant — 2 оси (X+Z, четверть
            /// отражается 4 раза); rotational — поворот 180°; none — без симметрии.</summary>
            public string symmetry = "mirror";
            public float oceanFloorM = -350f;    // высота океанского дна (до террас)
            public float coastRadius = 0.92f;    // coast: радиус континента, доля половины карты
            public float coastBand = 0.10f;      // coast: полуширина кромки континента (пляж/шельф)
            public float seaScaleM = 12000f;     // seas: характерный размер моря, м
            public float islandScaleM = 8000f;   // islands: характерный размер острова, м
            public float baseIslandRadiusM = 4500f; // водные стили: остров/мыс под каждую базу, м
            public int albedoRes = 0;             // «ползунок текстур» рельефа (фолбэк-материал):
                                                 // 1024..8192 (10 м/тексель на 82 км при 8192);
                                                 // слабые машины — вниз; 0 = дефолт 8192
            public float corridorWidthM = 180f;  // ровные трассы проезда техники: ширина коридора
                                                 // база↔центр/месторождения, м (0 = выкл;
                                                 // юзер-канон 2026-11-02 «участки под проезд —
                                                 // плоские и ровные»)
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
            // Зонная окраска v3 (пляж/скалы/мелководье/снег): hex-цвета шаблона.
            public string shallowColor = "#3f7d94";  // подводная кромка (мель)
            public string beachColor = "#c2b280";    // пляж у кромки воды
            public string cliffColor = "#6f6a5e";    // скалы (круче steepDeg)
            public float snowHeightM = 750f;         // вершины выше — к снежному
            public float beachWidthM = 350f;         // полоса пляжа от кромки воды, м
            public float shoreShelfM = 14f;           // ПОЛОГИЙ ШЕЛЬФ: глубина береговой
                                                      // полосы, м (0 = крутой берег как прежде;
                                                      // юзер-канон 2026-11-04: «инженеры
                                                      // скатываются на дно»)
            public float coastFlatM = 900f;           // БЕРЕГ-ЖИМ: полоса от уреза, м, где
                                                      // надводная высота клампится рампой
                                                      // shoreSlope (0 = выкл; плейтест
                                                      // 2026-11-04: «всё ещё очень круто,
                                                      // грузовики тонут с холма»)
            public float shoreSlope = 0.03f;          // уклон рампы берег-жима, м/м
                                                      // (900 м × 0.03 = берег не выше ~27 м)
            public float seaRampM = 350f;             // ГЛУБИННЫЙ ЖИМ: ширина мелкой
                                                      // аппарели от уреза, м (дальше —
                                                      // см. navDepthM; плейтест 2026-11-04:
                                                      // «корабли сели на мель, не слушаются»)
            public float navDepthM = 12f;             // гарантированная глубина за
                                                      // аппарелью, м (спавн-фильтр флота
                                                      // требует max(4, height·0.15) ≈ 9 —
                                                      // запас; 0 = выкл, мель как прежде)

            /// <summary>Хеш морфологии (все поля стиля/рельефа, FNV-1a): входит в
            /// ключ карты — правка JSON-шаблона не коллизирует миры в реестре Built
            /// (урок X8-сида: один ключ = один мир). Чистая — тестируется.</summary>
            public string MorphHash()
            {
                unchecked
                {
                    uint h = 2166136261u;
                    Mix(ref h, sizeM); Mix(ref h, stepM); Mix(ref h, waterAmount);
                    Mix(ref h, plainHeight); Mix(ref h, gentleAmp); Mix(ref h, plainFreq);
                    Mix(ref h, hillsAmp); Mix(ref h, hillsFreq); Mix(ref h, terraceStepM);
                    Mix(ref h, coastWobble); Mix(ref h, mountainHeight); Mix(ref h, mountainStart);
                    Mix(ref h, ridgeHeight); Mix(ref h, ridgeMaskStart); Mix(ref h, ridgeMaskEnd);
                    Mix(ref h, seaLevelM); Mix(ref h, steepDeg); Mix(ref h, edgeM);
                    Mix(ref h, oceanFloorM); Mix(ref h, coastRadius); Mix(ref h, coastBand);
                    Mix(ref h, seaScaleM); Mix(ref h, islandScaleM); Mix(ref h, baseIslandRadiusM);
                    Mix(ref h, snowHeightM); Mix(ref h, beachWidthM); Mix(ref h, shoreShelfM);
                    Mix(ref h, coastFlatM); Mix(ref h, shoreSlope);
                    Mix(ref h, seaRampM); Mix(ref h, navDepthM);
                    Mix(ref h, albedoRes); Mix(ref h, corridorWidthM);
                    Mix(ref h, style); Mix(ref h, symmetry); Mix(ref h, waterColor); Mix(ref h, shallowColor);
                    Mix(ref h, beachColor); Mix(ref h, cliffColor);
                    if (palette != null)
                        foreach (string p in palette)
                            Mix(ref h, p);
                    return h.ToString("X8");
                }
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
            private struct FloatIntUnion
            {
                [System.Runtime.InteropServices.FieldOffset(0)] public float F;
                [System.Runtime.InteropServices.FieldOffset(0)] public int I;
            }

            private static void Mix(ref uint h, float v)
            {
                var u = default(FloatIntUnion);
                u.F = v;
                Mix(ref h, u.I);
            }

            private static void Mix(ref uint h, int v)
            {
                h = (h ^ (uint)v) * 16777619u;
            }

            private static void Mix(ref uint h, string s)
            {
                if (string.IsNullOrEmpty(s))
                {
                    Mix(ref h, 0);
                    return;
                }
                foreach (char c in s)
                    h = (h ^ c) * 16777619u;
            }
        }

        /// <summary>Секция спец-карты: не null — сценарий строится на собственном
        /// рельефе (MapKey SN-Terrain…). Дефолт ВКЛЮЧЁН (основной путь мода);
        /// «дуэль на стоковой карте» — убрать секцию/оставить null в JSON шаблона.</summary>
        public TerrainSettings terrain = new TerrainSettings();

        public float baseInset = 0.40f;    // доля полудиагонали карты: где стоят базы
        public int depositCount = 14;      // зеркальных ПАР точек массы (T1)
        public int centerRichCount = 2;    // пар жирного центра (T2)
        public int coalPerSide = 2;        // зеркальных ПАР угольных точек — у баз (бутстрап энергии, v3)
        public int spawnMexCount = 4;      // мексов кольцом у КАЖДОЙ базы (канон SupCom, 2026-11-03)
        public float spawnMexRadiusM = 850f; // радиус кольца спавн-мексов от центра базы
        public float depositSpacing = 3000f;
        public float baseSearchDeg = 45f;  // сектор поиска пригодной площадки вокруг оси
        public float startFunds = 10000f; // запас на бутстрап: уголь + мекс + депо (поднят с 5000 для тестов, 2026-10-30)
        public float runwayLength = 1800f; // стартовая ВПП слота
        public float runwayWidth = 45f;

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
