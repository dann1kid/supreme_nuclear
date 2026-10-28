using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace SupremeNuclear
{
    internal static class Settings
    {
        public static bool ModEnabled = true;

        public static KeyCode ToggleCommanderKey = KeyCode.F10;

        // Язык интерфейса мода: auto (язык системы), ru, en или имя файла в Locales/
        public static string Language = "auto";

        // Камера
        public static KeyCode RotateModifierKey = KeyCode.Space; // осмотр: зажать и двигать мышь; отпустить — вернуть взгляд
        public static float ZoomPerNotch = 2.5f;          // доля изменения дистанции за щелчок колеса (шаг ограничен 0.3)
        public static float MinDistance = 25f;          // до фокуса, м
        public static float MinHeight = 10f;            // над рельефом/морем
        public static float MaxHeight = 30000f;         // минимум потолка; реальный потолок = вся карта в кадре
        public static float MaxAltitudeCeiling = 85000f; // жёсткий потолок: выше 90 км игра не может сдвигать origin (мировые пределы), начинается дрожь
        public static float RotateDegPerPixel = 0.45f; // скорость осмотра на Space, град/пиксель
        public static float PitchMin = 20f;           // ниже — почти в горизонт; доступно при осмотре на Space
        public static float PitchMax = 75f;
        public static float TopDownFullDistance = 6000f;  // дальше этой дистанции — полный вид сверху
        public static float TopDownPitch = 87f;
        public static float CloseTiltPitch = 60f;          // наклон у поверхности (низ кривой зума), град
        public static float CloseTiltStartDistance = 350f; // ближе — наклон равен CloseTiltPitch (пологий низ кривой)
        public static float AutoTiltSmoothing = 5f;        // скорость входа/выхода из осмотра на Space (1/с)
        public static float MoveSmoothing = 15f;
        public static float RotSmoothing = 18f;
        public static float MapMargin = 1000f;          // запас за краем карты для фокуса
        public static float CloudHideAltitude = 15000f; // выше — скрываем облака (вид из космоса)
        public static bool EdgeScroll = true;           // панорамирование у края экрана
        public static float EdgePixels = 24f;           // зона края, px
        public static float EdgeSpeed = 1.2f;           // скоростей: высот вида в секунду

        // Иконки юнитов на стратегическом зуме
        public static bool IconsAlwaysOn = true;        // иконки не выключаются никогда (эксперимент)
        public static float IconMinPx = 6f;             // мельче этого видимого размера — рисуем иконку (если IconsAlwaysOn=false)
        public static float IconSizePx = 18f;           // базовый размер иконки при 1440p
        public static int IconMaxUnits = 500;
        public static float StaleContactSec = 30f;      // сколько секунд показывать врага без контакта
        public static float SelectionRingMinPx = 12f;   // минимальный радиус кольца выделения, px
        public static float RouteRibbonWidthM = 40f;
        public static bool RouteUseMesh = false;    // экспериментальный меш-рендер лент (фолбэк — IMGUI)
        public static bool RouteDebugQuad = false;  // отладочная рамка 2 км над центром карты
        public static float RouteSampleStepM = 200f;        // шаг сэмплирования рельефа вдоль пути, м
        public static float RouteCornerRadiusM = 120f;      // радиус скругления углов ленты у вейпоинта, м

        // Экономика v1
        public static bool EconomyEnabled = true;
        public static string EconomyProfile = "default";

        // Выделение
        public static float BoxDragThresholdPx = 8f;
        public static float DoubleClickSec = 0.35f;
        public static int MaxSelected = 500;

        // Приказы
        public static float FormationSpacing = 30f;
        public static float EnemyPickRadiusPx = 24f;   // радиус «притягивания» цели ПКМ (ракеты — ×2.5), px
        public static float WaypointPickPx = 24f;      // пик-радиус вейпоинта (захват драга/удаление/конвертация/подсветка), px
        public static bool GroundDirectRouting = true;  // наземка своих едет напрямую, не заезжая на шоссе (кроме прямой через воду — там нативные мосты)
        public static float GroundDirectWaterStepM = 150f; // шаг проверки прямой трассы наземки на воду, м
        public static float AircraftOrderAltitude = 800f; // высота полёта к точке, м над рельефом
        public static float EscortStandoffM = 60f;   // сопровождение: дистанция следования от цели (плюс габариты), м
        public static float EscortAirDistM = 900f;   // сопровождение: смещение авиаэскорта от цели (сектор веера), м
        public static float EscortTrailSampleM = 25f;   // сопровождение: шаг следа цели (breadcrumbs сплайна), м
        public static float EscortTrailLengthM = 600f;  // сопровождение: длина следа цели (сплайн-якорь колонны), м

        // Режимы полёта: профили высоты (штампуются на участок маршрута) и скорость
        public static float AircraftAltVeryLowAgl = 75f;  // «Сверхнизко»: огибание рельефа, м над рельефом
        public static float AircraftAltLowAgl = 150f;     // «Низко»: огибание рельефа, м над рельефом
        public static float AircraftAltHighAgl = 2500f;   // «Высоко», м над рельефом
        public static float AircraftEconomyThrottle = 0.55f; // «Эконом»: доля максимального газа (самолёты)

        // Авто-защита (DESIGN §2.11: РЭБ-авто — джаммер/ловушки по типу угрозы)
        public static float DefenseFlareIntervalSec = 2f; // режим «Ловушки»: интервал авто-сброса, с

        // Залп (DESIGN §2.11: распределение залпа — нативное LookForMissileTargets)
        public static int SalvoMaxRoundsPerTarget = 4;    // потолок ракет на цель в одном залпе

        // Приказы атаки и вылеты (DESIGN §2.11)
        public static float AttackEngageFactor = 1.25f;   // «цель в зоне боя» = дальность вооружения × фактор
        public static float AttackMoveConeDeg = 75f;      // конус атаки-движения авиации, град
        public static float RtbFuelLevel = 0.25f;         // топливо ниже — вылет домой
        public static float LandingHandoffM = 4000f;      // дистанция до аэродрома для передачи нативной посадке, м
        public static float AirbaseServiceRadiusM = 800f; // радиус обслуживания у аэродрома, м
        public static float AirbaseServiceSeconds = 20f;  // время перевооружения/заправки на земле, с
        public static float AirbaseRearmCost = 150f;      // цена обслуживания вылета

        // Стройка (Task-API v1)
        public static float BuildSecondsPerBuilder = 20f; // время постройки одним инженером, с
        public static float BuildDefaultCost = 250f;      // цена, если у здания не задан value
        public static float ProductionSecondsPerItem = 12f; // производство одной единицы, с
        public static float BuildSiteRadiusM = 140f;      // радиус площадки: инженер в нём — строит
        public static float BuildSiteSpacingM = 60f;     // мин. дистанция между площадками (анти-стены)
        public static float HangarAttachRadiusM = 600f;  // дальность пристраивания ангара к своему аэродрому, м
        public static float RunwayCost = 1500f;          // цена постройки ВПП (новый аэродром)
        public static float RunwayWidthM = 45f;          // ширина построенной ВПП, м
        public static float RunwayMinM = 900f;           // мин. длина ВПП, м
        public static float RunwayMaxM = 4000f;          // макс. длина ВПП, м
        public static bool Build3dGhosts = true;         // 3D-призраки зданий (фолбэк — маркеры)
        public static float BuildTerrainTiltMaxDeg = 15f; // макс. наклон депо под рельеф, град (0 — плоско)
        public static float BuildApproachMarginM = 15f;  // запас кольца остановки инженера от габарита здания, м
        public static float BuildPatrolAssistRadiusM = 500f; // радиус авто-достройки недостроев инженерами в патруле, м (0 — выкл)

        // Флот: верфь-платформа и плавучий инженер (v0.92)
        public static float DockCost = 800f;             // цена верфи (платформа + депо-корпус)
        public static float DockMinDepthM = 5f;          // мин. глубина под платформой, м
        public static float DockPlatformMarginM = 24f;   // запас платформы вокруг габарита корпуса, м
        public static float DockSpawnSearchM = 400f;     // радиус поиска воды для спуска корабля, м
        public static float NavalProductionSecondsPerItem = 45f; // производство корабля, с
        public static float EngineerWaterSpeed = 6f;     // скорость инженера по воде, м/с
        public static float EngineerFloatHeightM = 0.6f; // клиренс корпуса инженера над водой, м

        // Отладка
        public static bool ShowDebugOverlay = false;
        public static float DebugFunds = 10000f;      // Ctrl+M: сколько денег добавить (плейтесты)

        // UI
        public static float UiScale = 1f;            // масштаб всех uGUI-панелей (1 = как при 1080p)
        public static bool MinimapEnabled = true;
        public static float MinimapX = -1f;          // < 0 — дефолт (право-верх), иначе позиция окна
        public static float MinimapY = -1f;
        public static float MinimapSize = 380f;
        public static bool MinimapVisible = true;
        public static float MinimapZoomMax = 16f;    // макс. зум миникарты колесом (×)
        public static bool MinimapRelief = false;    // фон миникарты: высоты + горизонтали по сетке GroundY
        public static bool UiTooltips = true;        // инфоокно юнита при наведении на плитки каталога/очередей

        // Кольца дальности выделенных юнитов (SupCom): чипы под миникартой
        public static bool RangeRingAttack = true;   // атака-АС (красное): наземные/надводные цели
        public static bool RangeRingAir = true;      // атака-АА (синее): воздушные цели
        public static bool RangeRingVision = true;   // разведка (белое): радар/визуал детекторов юнита
        public static bool RangeRingBuild = true;    // стройка (охра): инженерный радиус площадки
        public static bool RangeRingWeapons = false; // режим «Оружие»: кольца по классам вместо доменных
        public static int RangeRingMaxUnits = 40;    // потолок юнитов на класс колец (перф отрисовки)
        public static int RangeRingExactDist = 4000; // ближе этой дистанции к камере (м) кольца — точные высоты по мешу

        // Звук
        public static bool CommanderNoDoppler = true; // гасить эффект Доплера в режиме командира

        private static ConfigFile _configFile;
        private static ConfigEntry<bool> _rangeAttack;
        private static ConfigEntry<bool> _rangeAir;
        private static ConfigEntry<bool> _rangeVision;
        private static ConfigEntry<bool> _rangeBuild;
        private static ConfigEntry<bool> _rangeWeapons;

        /// <summary>Тоггл колец дальности из UI (0 атака-АС / 1 атака-АА / 2 разведка /
        /// 3 стройка): пишем и статическое поле, и запись конфига (персистентность).</summary>
        public static void SetRangeRing(int kind, bool on)
        {
            switch (kind)
            {
                case 0: RangeRingAttack = on; _rangeAttack.Value = on; break;
                case 1: RangeRingAir = on; _rangeAir.Value = on; break;
                case 2: RangeRingVision = on; _rangeVision.Value = on; break;
                case 3: RangeRingBuild = on; _rangeBuild.Value = on; break;
            }
        }

        /// <summary>Режим «Оружие» (кольца по классам вооружения) вкл/выкл.</summary>
        public static void SetRangeWeapons(bool on)
        {
            RangeRingWeapons = on;
            _rangeWeapons.Value = on;
        }

        /// <summary>Раннее чтение языка — ДО основного Bind и ДО загрузки таблиц локалей
        /// в Awake: описания остальных настроек локализуются через Loc.Tr.</summary>
        public static string BindLanguage(ConfigFile config)
        {
            var options = new List<string> { "auto", "ru", "en" };
            foreach (string locale in Loc.Available())
                if (!options.Contains(locale))
                    options.Add(locale);
            Language = config.Bind("General", "Language", "auto",
                new ConfigDescription("Language · Язык интерфейса мода. auto — язык системы " +
                    "(русский → ru, прочие → en); принудительно: ru, en или имя файла из " +
                    "BepInEx/plugins/SupremeNuclear/Locales/. Применяется при запуске",
                    new AcceptableValueList<string>(options.ToArray()))).Value;
            return Language;
        }

        public static void Bind(ConfigFile config)
        {
            _configFile = config;
            ModEnabled = config.Bind("General", "Enabled", true, Loc.Tr("Включить мод")).Value;
            ToggleCommanderKey = config.Bind("General", "ToggleCommanderKey", KeyCode.F10,
                Loc.Tr("Клавиша входа/выхода из режима командира")).Value;

            RotateModifierKey = config.Bind("Camera", "RotateModifierKey", KeyCode.Space,
                Loc.Tr("Клавиша осмотра: зажать и двигать мышь — вид вокруг точки; отпустить — камера возвращается")).Value;
            ZoomPerNotch = config.Bind("Camera", "ZoomPerNotch", 2.5f, Loc.Tr("Сила зума за щелчок колеса (доля расстояния)")).Value;
            MinDistance = config.Bind("Camera", "MinDistance", 25f, Loc.Tr("Минимальная дистанция камеры до точки фокуса, м")).Value;
            MinHeight = config.Bind("Camera", "MinHeight", 10f, Loc.Tr("Минимальная высота камеры над рельефом, м")).Value;
            MaxHeight = config.Bind("Camera", "MaxHeight", 30000f,
                Loc.Tr("Гарантированный потолок высоты, м (реальный = не меньше, чем вся карта в кадре)")).Value;
            MaxAltitudeCeiling = config.Bind("Camera", "MaxAltitudeCeiling", 85000f,
                Loc.Tr("Жёсткий потолок высоты, м — выше ~90 км игра не может сдвигать origin мировых объектов")).Value;
            RotateDegPerPixel = config.Bind("Camera", "RotateDegPerPixel", 0.45f, Loc.Tr("Скорость вращения (град/пиксель мыши)")).Value;
            PitchMin = config.Bind("Camera", "PitchMinAngle", 20f, Loc.Tr("Минимальный наклон камеры вниз, град (осмотр на Space)")).Value;
            PitchMax = config.Bind("Camera", "PitchMax", 75f).Value;
            TopDownFullDistance = config.Bind("Camera", "TopDownFullDistance", 6000f,
                Loc.Tr("Дистанция полного вида сверху (верх кривой наклона), м")).Value;
            TopDownPitch = config.Bind("Camera", "TopDownPitch", 87f).Value;
            CloseTiltPitch = config.Bind("Camera", "CloseTiltPitch", 60f,
                Loc.Tr("Наклон у поверхности — низ кривой наклона, град (горизонта не видно)")).Value;
            CloseTiltStartDistance = config.Bind("Camera", "CloseTiltStartDistance", 350f,
                Loc.Tr("Дистанция, ниже которой наклон равен CloseTiltPitch, м")).Value;
            AutoTiltSmoothing = config.Bind("Camera", "AutoTiltSmoothing", 5f,
                Loc.Tr("Скорость входа/выхода из осмотра на Space (1/с)")).Value;
            MoveSmoothing = config.Bind("Camera", "MoveSmoothing", 15f).Value;
            RotSmoothing = config.Bind("Camera", "RotSmoothing", 18f).Value;
            MapMargin = config.Bind("Camera", "MapMargin2", 1000f, Loc.Tr("Запуск фокуса за край карты, м")).Value;
            CloudHideAltitude = config.Bind("Camera", "CloudHideAltitude", 15000f,
                Loc.Tr("Высота, выше которой облака скрываются (вид из космоса), м")).Value;
            EdgeScroll = config.Bind("Camera", "EdgePanEnabled", true, Loc.Tr("Панорамирование при курсоре у края экрана")).Value;
            EdgePixels = config.Bind("Camera", "EdgePixels", 24f, Loc.Tr("Ширина зоны края экрана, px")).Value;
            EdgeSpeed = config.Bind("Camera", "EdgeSpeed", 1.2f,
                Loc.Tr("Скорость у края: сколько высот вида в секунду")).Value;

            IconsAlwaysOn = config.Bind("Icons", "IconsAlwaysOn", true, Loc.Tr("Никогда не скрывать иконки (эксперимент)")).Value;
            IconMinPx = config.Bind("Icons", "IconMinApparentPx", 6f,
                Loc.Tr("Видимый размер юнита (px), мельче которого рисуется 2D-иконка вместо 3D-модели")).Value;
            IconSizePx = config.Bind("Icons", "IconSizePx", 18f, Loc.Tr("Базовый размер иконки при 1440p, px")).Value;
            IconMaxUnits = config.Bind("Icons", "IconMaxUnits", 500).Value;
            RouteDebugQuad = config.Bind("Routes", "RouteDebugQuad", false, Loc.Tr("Отладочная рамка 2 км над центром карты")).Value;
            RouteUseMesh = config.Bind("Routes", "RouteUseMesh", false, Loc.Tr("Рисовать ленты мировым мешом (эксперимент)")).Value;
            RouteRibbonWidthM = config.Bind("Routes", "RouteLegWidthM", 40f, Loc.Tr("Ширина ленты маршрута на земле, м")).Value;
            RouteSampleStepM = config.Bind("Routes", "RouteSampleStepM", 200f, Loc.Tr("Шаг сэмплирования рельефа вдоль маршрута, м")).Value;
            RouteCornerRadiusM = config.Bind("Routes", "RouteCornerRadiusM", 120f, Loc.Tr("Радиус скругления углов ленты маршрута у вейпоинта, м")).Value;
            StaleContactSec = config.Bind("Icons", "StaleContactSec", 30f,
                Loc.Tr("Сколько секунд показывать вражеский контакт без обновления разведки, с")).Value;

            EconomyEnabled = config.Bind("Economy", "Enabled", true, Loc.Tr("Включить экономику v1 (месторождения + экстракторы)")).Value;
            EconomyProfile = config.Bind("Economy", "Profile", "default",
                Loc.Tr("Имя JSON-профиля в BepInEx/plugins/SupremeNuclear/Economy/")).Value;

            BoxDragThresholdPx = config.Bind("Selection", "BoxDragThresholdPx", 8f).Value;
            DoubleClickSec = config.Bind("Selection", "DoubleClickSec", 0.35f).Value;
            MaxSelected = config.Bind("Selection", "MaxSelected", 500).Value;

            FormationSpacing = config.Bind("Orders", "FormationSpacing", 30f, Loc.Tr("Шаг решётки приказов, м")).Value;
            WaypointPickPx = config.Bind("Orders", "WaypointPickPx", 24f,
                Loc.Tr("Радиус захвата вейпоинта мышью (драг/удаление/конвертация/подсветка), px")).Value;
            EnemyPickRadiusPx = config.Bind("Orders", "EnemyPickRadiusPx", 24f,
                Loc.Tr("Радиус захвата цели ПКМ по курсору, px (для ракет — в 2.5 раза шире)")).Value;
            GroundDirectRouting = config.Bind("Orders", "GroundDirectRouting", true,
                Loc.Tr("Наземная техника своих едет к цели напрямую, не заезжая на шоссе ради коротких перегонов; прямая через воду — по-нативному, через мосты")).Value;
            GroundDirectWaterStepM = config.Bind("Orders", "GroundDirectWaterStepM", 150f,
                new ConfigDescription(Loc.Tr("Шаг проверки прямой трассы наземки на воду, м (потолок сэмплов на приказ — 24)"),
                    new AcceptableValueRange<float>(50f, 500f))).Value;
            AircraftOrderAltitude = config.Bind("Orders", "AircraftOrderAltitude", 800f,
                Loc.Tr("Высота полёта к точке назначения («Средне»), м над рельефом")).Value;
            EscortStandoffM = config.Bind("Orders", "EscortStandoffM", 60f,
                Loc.Tr("Сопровождение: дистанция следования от цели (плюс габариты юнитов), м")).Value;
            EscortAirDistM = config.Bind("Orders", "EscortAirDistM", 900f,
                Loc.Tr("Сопровождение: смещение авиации от цели (сектор веера группы), м")).Value;
            EscortTrailSampleM = config.Bind("Orders", "EscortTrailSampleM", 25f,
                Loc.Tr("Сопровождение: шаг следа цели (точки сплайна движения колонны), м")).Value;
            EscortTrailLengthM = config.Bind("Orders", "EscortTrailLengthM", 600f,
                Loc.Tr("Сопровождение: длина следа цели для сплайна движения колонны, м")).Value;
            AircraftAltVeryLowAgl = config.Bind("Orders", "AircraftAltVeryLowAgl", 75f,
                Loc.Tr("Профиль «Сверхнизко» (огибание рельефа), м над рельефом")).Value;
            AircraftAltLowAgl = config.Bind("Orders", "AircraftAltLowAgl", 150f,
                Loc.Tr("Профиль «Низко» (огибание рельефа), м над рельефом")).Value;
            AircraftAltHighAgl = config.Bind("Orders", "AircraftAltHighAgl", 2500f,
                Loc.Tr("Профиль «Высоко», м над рельефом")).Value;
            AircraftEconomyThrottle = config.Bind("Orders", "AircraftEconomyThrottle", 0.55f,
                new ConfigDescription(Loc.Tr("Скоростной режим «Эконом»: доля максимального газа (только самолёты)"),
                    new AcceptableValueRange<float>(0.2f, 0.95f))).Value;
            DefenseFlareIntervalSec = config.Bind("Orders", "DefenseFlareIntervalSec", 2f,
                new ConfigDescription(Loc.Tr("Режим защиты «Ловушки»: интервал авто-сброса при ракетном предупреждении, с"),
                    new AcceptableValueRange<float>(0.5f, 10f))).Value;
            SalvoMaxRoundsPerTarget = config.Bind("Orders", "SalvoMaxRoundsPerTarget", 4,
                new ConfigDescription(Loc.Tr("Потолок ракет на одну цель в одном залпе (сверх — не поднимается)"),
                    new AcceptableValueRange<int>(1, 16))).Value;

            AttackEngageFactor = config.Bind("Orders", "AttackEngageFactor", 1.25f,
                Loc.Tr("«Цель в зоне боя» = дальность вооружения × этот фактор: ближе — бой отдаётся нативному AI")).Value;
            AttackMoveConeDeg = config.Bind("Orders", "AttackMoveConeDeg", 75f,
                Loc.Tr("Конус атаки-движения авиации: цели вне конуса вектора приказа не преследуются, град")).Value;
            RtbFuelLevel = config.Bind("Orders", "RtbFuelLevel", 0.25f,
                new ConfigDescription(Loc.Tr("Доля топлива, ниже которой боевой вылет возвращается на базу (нативный порог посадки — 0.2)"),
                    new AcceptableValueRange<float>(0.05f, 0.9f))).Value;
            LandingHandoffM = config.Bind("Orders", "LandingHandoffM", 4000f,
                Loc.Tr("Дистанция до аэродрома, на которой борт передаётся нативной автопосадке, м")).Value;
            AirbaseServiceRadiusM = config.Bind("Orders", "AirbaseServiceRadiusM", 800f,
                Loc.Tr("Радиус у аэродрома, в котором идёт перевооружение/заправка на земле, м")).Value;
            AirbaseServiceSeconds = config.Bind("Orders", "AirbaseServiceSeconds", 20f,
                Loc.Tr("Время перевооружения/заправки борта на земле, с")).Value;
            AirbaseRearmCost = config.Bind("Orders", "AirbaseRearmCost", 150f,
                Loc.Tr("Цена обслуживания вылета (перевооружение + заправка; интерим до логистики)")).Value;

            BuildDefaultCost = config.Bind("Build", "DefaultCost", 250f,
                Loc.Tr("Цена постройки, если у здания в каталоге не задана value")).Value;
            ProductionSecondsPerItem = config.Bind("Build", "ProductionSecondsPerItem", 12f,
                Loc.Tr("Производство одной единицы на фабрике (депо), с")).Value;
            BuildSecondsPerBuilder = config.Bind("Build", "SecondsPerBuilder", 20f,
                Loc.Tr("Время постройки здания одним инженером, с (добавление инженеров ускоряет)")).Value;
            BuildSiteSpacingM = config.Bind("Build", "SiteSpacingM", 60f,
                Loc.Tr("Минимальная дистанция между площадками построек, м (со снап-раздвигом)")).Value;
            HangarAttachRadiusM = config.Bind("Build", "HangarAttachRadiusM", 600f,
                Loc.Tr("Ангар пристраивается к своему аэродрому не дальше этого радиуса, м")).Value;
            RunwayCost = config.Bind("Build", "RunwayCost", 1500f,
                Loc.Tr("Цена постройки ВПП (создаёт новый аэродром-мультифабрику)")).Value;
            RunwayWidthM = config.Bind("Build", "RunwayWidthM", 45f,
                Loc.Tr("Ширина построенной ВПП, м")).Value;
            RunwayMinM = config.Bind("Build", "RunwayMinM", 900f,
                Loc.Tr("Минимальная длина ВПП, м (короче — тяжёлым бортам не хватит разбега)")).Value;
            RunwayMaxM = config.Bind("Build", "RunwayMaxM", 4000f,
                Loc.Tr("Максимальная длина ВПП, м")).Value;
            Build3dGhosts = config.Bind("Build", "Ghost3D", true,
                Loc.Tr("Призраки строящихся зданий — 3D-модели с голубым материалом (выкл — маркеры)")).Value;
            BuildTerrainTiltMaxDeg = config.Bind("Build", "TerrainTiltMaxDeg", 15f,
                Loc.Tr("Максимальный наклон депо под рельеф, градусы (0 — всегда плоско)")).Value;
            BuildSiteRadiusM = config.Bind("Build", "SiteRadiusM", 140f,
                Loc.Tr("Радиус площадки стройки: инженер внутри — идёт оплата, м")).Value;
            BuildApproachMarginM = config.Bind("Build", "ApproachMarginM", 15f,
                Loc.Tr("Запас остановки инженера от габарита здания (кольцо подхода), м")).Value;
            BuildPatrolAssistRadiusM = config.Bind("Build", "PatrolAssistRadiusM", 500f,
                Loc.Tr("Радиус авто-достройки недостроев инженерами в патруле, м (0 — выкл)")).Value;
            DockCost = config.Bind("Naval", "DockCost", 800f,
                Loc.Tr("Цена верфи (платформа на воде, SupCom-паттерн)")).Value;
            DockMinDepthM = config.Bind("Naval", "DockMinDepthM", 5f,
                Loc.Tr("Минимальная глубина воды под платформой верфи, м")).Value;
            DockPlatformMarginM = config.Bind("Naval", "DockPlatformMarginM", 24f,
                Loc.Tr("Запас платформы верфи вокруг корпуса, м")).Value;
            DockSpawnSearchM = config.Bind("Naval", "DockSpawnSearchM", 400f,
                Loc.Tr("Радиус поиска свободной воды для спуска корабля, м")).Value;
            NavalProductionSecondsPerItem = config.Bind("Naval", "ProductionSecondsPerItem", 45f,
                Loc.Tr("Производство одного корабля на верфи, с")).Value;
            EngineerWaterSpeed = config.Bind("Naval", "EngineerWaterSpeed", 6f,
                Loc.Tr("Скорость инженера по воде (плавучесть), м/с")).Value;
            EngineerFloatHeightM = config.Bind("Naval", "EngineerFloatHeightM", 0.6f,
                Loc.Tr("Клиренс корпуса инженера над водой, м (слишком низко — игра топит технику)")).Value;

            ShowDebugOverlay = config.Bind("Debug", "ShowDebugOverlay", false).Value;
            DebugFunds = config.Bind("Debug", "Funds", 10000f,
                "Сколько денег добавляет Ctrl+M в режиме командира (плейтесты, хост)").Value;

            UiScale = config.Bind("UI", "UiScale", 1f,
                new ConfigDescription(Loc.Tr("Масштаб всех uGUI-панелей (1 = как при 1080p); применяется при перезапуске"),
                    new AcceptableValueRange<float>(0.75f, 2f))).Value;
            MinimapEnabled = config.Bind("UI", "MinimapEnabled", true, Loc.Tr("Плавающая миникарта")).Value;
            MinimapX = config.Bind("UI", "MinimapX", -1f,
                Loc.Tr("Позиция окна миникарты X (внутренние px, левый нижний угол; -1 = право-верх по умолчанию)")).Value;
            MinimapY = config.Bind("UI", "MinimapY", -1f, Loc.Tr("Позиция окна миникарты Y (-1 = по умолчанию)")).Value;
            MinimapSize = config.Bind("UI", "MinimapSize", 380f,
                new ConfigDescription(Loc.Tr("Размер окна миникарты, px"), new AcceptableValueRange<float>(240f, 900f))).Value;
            MinimapVisible = config.Bind("UI", "MinimapVisible", true, Loc.Tr("Окно миникарты открыто")).Value;
            MinimapZoomMax = config.Bind("UI", "MinimapZoomMax", 16f,
                new ConfigDescription(Loc.Tr("Максимальный зум миникарты колесом (×)"),
                    new AcceptableValueRange<float>(1f, 64f))).Value;
            MinimapRelief = config.Bind("UI", "MinimapRelief", false,
                Loc.Tr("Фон миникарты: рельеф (высоты + горизонтали) вместо снимка карты")).Value;
            UiTooltips = config.Bind("UI", "UiTooltips", true,
                Loc.Tr("Инфоокно юнита при наведении на плитки каталога/очередей")).Value;

            // Кольца дальности: записи удерживаем, чтобы чипы UI меняли и значение
            // конфига (SaveConfig пишет именно Value записи, не статическую копию).
            _rangeAttack = config.Bind("RangeRings", "Attack", true,
                Loc.Tr("Красные кольца атаки по наземным/надводным целям (макс. дальность разрешённых гейтом станций)"));
            _rangeAir = config.Bind("RangeRings", "Air", true,
                Loc.Tr("Синие кольца атаки по воздушным целям (SupCom: синий = ПВО/воздух)"));
            _rangeVision = config.Bind("RangeRings", "Vision", true,
                Loc.Tr("Белые кольца разведки выделенных юнитов (радар/визуальный детектор)"));
            _rangeBuild = config.Bind("RangeRings", "Build", true,
                Loc.Tr("Охристые кольца стройки инженеров в выделении (радиус площадки)"));
            _rangeWeapons = config.Bind("RangeRings", "Weapons", false,
                Loc.Tr("Режим «Оружие»: кольца по классам вооружения (пушки/ракеты ВВ/ракеты АС/бомбы) вместо доменных колец атаки — видно, чем осталось бить при отключении классов гейтами"));
            RangeRingAttack = _rangeAttack.Value;
            RangeRingAir = _rangeAir.Value;
            RangeRingVision = _rangeVision.Value;
            RangeRingBuild = _rangeBuild.Value;
            RangeRingWeapons = _rangeWeapons.Value;
            RangeRingMaxUnits = config.Bind("RangeRings", "MaxUnits", 40,
                new ConfigDescription(Loc.Tr("Потолок юнитов на класс колец дальности (перф отрисовки)"),
                    new AcceptableValueRange<int>(4, 200))).Value;
            RangeRingExactDist = config.Bind("RangeRings", "ExactTerrainDist", 4000,
                new ConfigDescription(Loc.Tr("Дистанция до камеры (м), ближе которой кольца следуют мешу поверхности точными рейкастами; дальше — сглаженный кэш высот (0 — всегда кэш)"),
                    new AcceptableValueRange<int>(0, 20000))).Value;
            CommanderNoDoppler = config.Bind("Audio", "CommanderNoDoppler", true,
                Loc.Tr("Гасить эффект Доплера в режиме командира: камера носится по карте на огромной скорости, и звук без этого летит «пилой»; вне режима звук не трогается")).Value;
        }

        /// <summary>Немедленно сохранить конфиг (позиции окон и т.п.).</summary>
        public static void SaveConfig() => _configFile?.Save();
    }
}
