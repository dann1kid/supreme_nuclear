using BepInEx.Configuration;
using UnityEngine;

namespace SupremeNuclear
{
    internal static class Settings
    {
        public static bool ModEnabled = true;

        public static KeyCode ToggleCommanderKey = KeyCode.F10;

        // Камера
        public static KeyCode RotateModifierKey = KeyCode.Space; // осмотр: зажать и двигать мышь; отпустить — вернуть взгляд
        public static float ZoomPerNotch = 2.5f;          // доля изменения дистанции за щелчок колеса (шаг ограничен 0.3)
        public static float MinDistance = 25f;          // до фокуса, м
        public static float MinHeight = 10f;            // над рельефом/морем
        public static float MaxHeight = 30000f;         // минимум потолка; реальный потолок = вся карта в кадре
        public static float MaxAltitudeCeiling = 85000f; // жёсткий потолок: выше 90 км игра не может сдвигать origin (мировые пределы), начинается дрожь
        public static float RotateDegPerPixel = 0.25f;
        public static float PitchMin = 20f;           // ниже — почти в горизонт; доступно при осмотре на Space
        public static float PitchMax = 75f;
        public static float TopDownStartDistance = 2500f; // дальше — плавно к виду сверху
        public static float TopDownFullDistance = 6000f;
        public static float TopDownPitch = 87f;
        public static float CloseTiltPitch = 60f;          // автодоворот вниз при взгляде у самой земли, град
        public static float CloseTiltStartDistance = 350f; // ближе — камера доворачивается вниз
        public static float CloseTiltEndDistance = 120f;   // дистанция полного доворота вниз, м
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
        public static bool RouteUseMesh = false;    // экспериментальный меш-рендер лент (фолбек — IMGUI)
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
        public static float AircraftOrderAltitude = 800f; // высота полёта к точке, м над рельефом

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
        public static bool Build3dGhosts = true;         // 3D-призраки зданий (фолбэк — маркеры)

        // Отладка
        public static bool ShowDebugOverlay = false;

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

        public static void Bind(ConfigFile config)
        {
            _configFile = config;
            ModEnabled = config.Bind("General", "Enabled", true, "Включить мод").Value;
            ToggleCommanderKey = config.Bind("General", "ToggleCommanderKey", KeyCode.F10,
                "Клавиша входа/выхода из режима командира").Value;

            RotateModifierKey = config.Bind("Camera", "RotateModifierKey", KeyCode.Space,
                "Клавиша осмотра: зажать и двигать мышь — вид вокруг точки; отпустить — камера возвращается").Value;
            ZoomPerNotch = config.Bind("Camera", "ZoomPerNotch", 2.5f, "Сила зума за щелчок колеса (доля расстояния)").Value;
            MinDistance = config.Bind("Camera", "MinDistance", 25f, "Минимальная дистанция камеры до точки фокуса, м").Value;
            MinHeight = config.Bind("Camera", "MinHeight", 10f, "Минимальная высота камеры над рельефом, м").Value;
            MaxHeight = config.Bind("Camera", "MaxHeight", 30000f,
                "Гарантированный потолок высоты, м (реальный = не меньше, чем вся карта в кадре)").Value;
            MaxAltitudeCeiling = config.Bind("Camera", "MaxAltitudeCeiling", 85000f,
                "Жёсткий потолок высоты, м — выше ~90 км игра не может сдвигать origin мировых объектов").Value;
            RotateDegPerPixel = config.Bind("Camera", "RotateDegPerPixel", 0.25f, "Скорость вращения (град/пиксель мыши)").Value;
            PitchMin = config.Bind("Camera", "PitchMinAngle", 20f, "Минимальный наклон камеры вниз, град").Value;
            PitchMax = config.Bind("Camera", "PitchMax", 75f).Value;
            TopDownStartDistance = config.Bind("Camera", "TopDownStartDistance", 2500f,
                "Дистанция, с которой угол начинает стремиться к виду сверху, м").Value;
            TopDownFullDistance = config.Bind("Camera", "TopDownFullDistance", 6000f,
                "Дистанция полного вида сверху, м").Value;
            TopDownPitch = config.Bind("Camera", "TopDownPitch", 87f).Value;
            CloseTiltPitch = config.Bind("Camera", "CloseTiltPitch", 60f,
                "Автодоворот камеры вниз при взгляде у самой земли, град").Value;
            CloseTiltStartDistance = config.Bind("Camera", "CloseTiltStartDistance", 350f,
                "Дистанция, ближе которой камера доворачивается вниз, м").Value;
            CloseTiltEndDistance = config.Bind("Camera", "CloseTiltEndDistance", 120f,
                "Дистанция полного доворота вниз у земли, м (меньше CloseTiltStartDistance)").Value;
            AutoTiltSmoothing = config.Bind("Camera", "AutoTiltSmoothing", 5f,
                "Скорость возврата к авто-углу после осмотра на Space (1/с)").Value;
            MoveSmoothing = config.Bind("Camera", "MoveSmoothing", 15f).Value;
            RotSmoothing = config.Bind("Camera", "RotSmoothing", 18f).Value;
            MapMargin = config.Bind("Camera", "MapMargin2", 1000f, "Запуск фокуса за край карты, м").Value;
            CloudHideAltitude = config.Bind("Camera", "CloudHideAltitude", 15000f,
                "Высота, выше которой облака скрываются (вид из космоса), м").Value;
            EdgeScroll = config.Bind("Camera", "EdgePanEnabled", true, "Панорамирование при курсоре у края экрана").Value;
            EdgePixels = config.Bind("Camera", "EdgePixels", 24f, "Ширина зоны края экрана, px").Value;
            EdgeSpeed = config.Bind("Camera", "EdgeSpeed", 1.2f,
                "Скорость у края: сколько высот вида в секунду").Value;

            IconsAlwaysOn = config.Bind("Icons", "IconsAlwaysOn", true, "Никогда не скрывать иконки (эксперимент)").Value;
            IconMinPx = config.Bind("Icons", "IconMinApparentPx", 6f,
                "Видимый размер юнита (px), мельче которого рисуется 2D-иконка вместо 3D-модели").Value;
            IconSizePx = config.Bind("Icons", "IconSizePx", 18f, "Базовый размер иконки при 1440p, px").Value;
            IconMaxUnits = config.Bind("Icons", "IconMaxUnits", 500).Value;
            RouteDebugQuad = config.Bind("Routes", "RouteDebugQuad", false, "Отладочная рамка 2 км над центром карты").Value;
            RouteUseMesh = config.Bind("Routes", "RouteUseMesh", false, "Рисовать ленты мировым мешом (эксперимент)").Value;
            RouteRibbonWidthM = config.Bind("Routes", "RouteLegWidthM", 40f, "Ширина ленты маршрута на земле, м").Value;
            RouteSampleStepM = config.Bind("Routes", "RouteSampleStepM", 200f, "Шаг сэмплирования рельефа вдоль маршрута, м").Value;
            RouteCornerRadiusM = config.Bind("Routes", "RouteCornerRadiusM", 120f, "Радиус скругления углов ленты маршрута у вейпоинта, м").Value;
            StaleContactSec = config.Bind("Icons", "StaleContactSec", 30f,
                "Сколько секунд показывать вражеский контакт без обновления разведки, с").Value;

            EconomyEnabled = config.Bind("Economy", "Enabled", true, "Включить экономику v1 (месторождения + экстракторы)").Value;
            EconomyProfile = config.Bind("Economy", "Profile", "default",
                "Имя JSON-профиля в BepInEx/plugins/SupremeNuclear/Economy/").Value;

            BoxDragThresholdPx = config.Bind("Selection", "BoxDragThresholdPx", 8f).Value;
            DoubleClickSec = config.Bind("Selection", "DoubleClickSec", 0.35f).Value;
            MaxSelected = config.Bind("Selection", "MaxSelected", 500).Value;

            FormationSpacing = config.Bind("Orders", "FormationSpacing", 30f, "Шаг решётки приказов, м").Value;
            EnemyPickRadiusPx = config.Bind("Orders", "EnemyPickRadiusPx", 24f,
                "Радиус захвата цели ПКМ по курсору, px (для ракет — в 2.5 раза шире)").Value;
            AircraftOrderAltitude = config.Bind("Orders", "AircraftOrderAltitude", 800f,
                "Высота полёта к точке назначения («Средне»), м над рельефом").Value;
            AircraftAltVeryLowAgl = config.Bind("Orders", "AircraftAltVeryLowAgl", 75f,
                "Профиль «Сверхнизко» (огибание рельефа), м над рельефом").Value;
            AircraftAltLowAgl = config.Bind("Orders", "AircraftAltLowAgl", 150f,
                "Профиль «Низко» (огибание рельефа), м над рельефом").Value;
            AircraftAltHighAgl = config.Bind("Orders", "AircraftAltHighAgl", 2500f,
                "Профиль «Высоко», м над рельефом").Value;
            AircraftEconomyThrottle = config.Bind("Orders", "AircraftEconomyThrottle", 0.55f,
                new ConfigDescription("Скоростной режим «Эконом»: доля максимального газа (только самолёты)",
                    new AcceptableValueRange<float>(0.2f, 0.95f))).Value;
            DefenseFlareIntervalSec = config.Bind("Orders", "DefenseFlareIntervalSec", 2f,
                new ConfigDescription("Режим защиты «Ловушки»: интервал авто-сброса при ракетном предупреждении, с",
                    new AcceptableValueRange<float>(0.5f, 10f))).Value;
            SalvoMaxRoundsPerTarget = config.Bind("Orders", "SalvoMaxRoundsPerTarget", 4,
                new ConfigDescription("Потолок ракет на одну цель в одном залпе (сверх — не поднимается)",
                    new AcceptableValueRange<int>(1, 16))).Value;

            AttackEngageFactor = config.Bind("Orders", "AttackEngageFactor", 1.25f,
                "«Цель в зоне боя» = дальность вооружения × этот фактор: ближе — бой отдаётся нативному AI").Value;
            AttackMoveConeDeg = config.Bind("Orders", "AttackMoveConeDeg", 75f,
                "Конус атаки-движения авиации: цели вне конуса вектора приказа не преследуются, град").Value;
            RtbFuelLevel = config.Bind("Orders", "RtbFuelLevel", 0.25f,
                new ConfigDescription("Доля топлива, ниже которой боевой вылет возвращается на базу (нативный порог посадки — 0.2)",
                    new AcceptableValueRange<float>(0.05f, 0.9f))).Value;
            LandingHandoffM = config.Bind("Orders", "LandingHandoffM", 4000f,
                "Дистанция до аэродрома, на которой борт передаётся нативной автопосадке, м").Value;
            AirbaseServiceRadiusM = config.Bind("Orders", "AirbaseServiceRadiusM", 800f,
                "Радиус у аэродрома, в котором идёт перевооружение/заправка на земле, м").Value;
            AirbaseServiceSeconds = config.Bind("Orders", "AirbaseServiceSeconds", 20f,
                "Время перевооружения/заправки борта на земле, с").Value;
            AirbaseRearmCost = config.Bind("Orders", "AirbaseRearmCost", 150f,
                "Цена обслуживания вылета (перевооружение + заправка; интерим до логистики)").Value;

            BuildDefaultCost = config.Bind("Build", "DefaultCost", 250f,
                "Цена постройки, если у здания в каталоге не задана value").Value;
            ProductionSecondsPerItem = config.Bind("Build", "ProductionSecondsPerItem", 12f,
                "Производство одной единицы на фабрике (депо), с").Value;
            BuildSecondsPerBuilder = config.Bind("Build", "SecondsPerBuilder", 20f,
                "Время постройки здания одним инженером, с (добавление инженеров ускоряет)").Value;
            BuildSiteSpacingM = config.Bind("Build", "SiteSpacingM", 60f,
                "Минимальная дистанция между площадками построек, м (со снап-раздвигом)").Value;
            HangarAttachRadiusM = config.Bind("Build", "HangarAttachRadiusM", 600f,
                "Ангар пристраивается к своему аэродрому не дальше этого радиуса, м").Value;
            Build3dGhosts = config.Bind("Build", "Ghost3D", true,
                "Призраки строящихся зданий — 3D-модели с голубым материалом (выкл — маркеры)").Value;
            BuildSiteRadiusM = config.Bind("Build", "SiteRadiusM", 140f,
                "Радиус площадки стройки: инженер внутри — идёт оплата, м").Value;

            ShowDebugOverlay = config.Bind("Debug", "ShowDebugOverlay", false).Value;

            UiScale = config.Bind("UI", "UiScale", 1f,
                new ConfigDescription("Масштаб всех uGUI-панелей (1 = как при 1080p); применяется при перезапуске",
                    new AcceptableValueRange<float>(0.75f, 2f))).Value;
            MinimapEnabled = config.Bind("UI", "MinimapEnabled", true, "Плавающая миникарта").Value;
            MinimapX = config.Bind("UI", "MinimapX", -1f,
                "Позиция окна миникарты X (внутренние px, левый нижний угол; -1 = право-верх по умолчанию)").Value;
            MinimapY = config.Bind("UI", "MinimapY", -1f, "Позиция окна миникарты Y (-1 = по умолчанию)").Value;
            MinimapSize = config.Bind("UI", "MinimapSize", 380f,
                new ConfigDescription("Размер окна миникарты, px", new AcceptableValueRange<float>(240f, 900f))).Value;
            MinimapVisible = config.Bind("UI", "MinimapVisible", true, "Окно миникарты открыто").Value;
            MinimapZoomMax = config.Bind("UI", "MinimapZoomMax", 16f,
                new ConfigDescription("Максимальный зум миникарты колесом (×)",
                    new AcceptableValueRange<float>(1f, 64f))).Value;
            MinimapRelief = config.Bind("UI", "MinimapRelief", false,
                "Фон миникарты: рельеф (высоты + горизонтали) вместо снимка карты").Value;
            UiTooltips = config.Bind("UI", "UiTooltips", true,
                "Инфоокно юнита при наведении на плитки каталога/очередей").Value;

            // Кольца дальности: записи удерживаем, чтобы чипы UI меняли и значение
            // конфига (SaveConfig пишет именно Value записи, не статическую копию).
            _rangeAttack = config.Bind("RangeRings", "Attack", true,
                "Красные кольца атаки по наземным/надводным целям (макс. дальность разрешённых гейтом станций)");
            _rangeAir = config.Bind("RangeRings", "Air", true,
                "Синие кольца атаки по воздушным целям (SupCom: синий = ПВО/воздух)");
            _rangeVision = config.Bind("RangeRings", "Vision", true,
                "Белые кольца разведки выделенных юнитов (радар/визуальный детектор)");
            _rangeBuild = config.Bind("RangeRings", "Build", true,
                "Охристые кольца стройки инженеров в выделении (радиус площадки)");
            _rangeWeapons = config.Bind("RangeRings", "Weapons", false,
                "Режим «Оружие»: кольца по классам вооружения (пушки/ракеты ВВ/ракеты АС/бомбы) вместо доменных колец атаки — видно, чем осталось бить при отключении классов гейтами");
            RangeRingAttack = _rangeAttack.Value;
            RangeRingAir = _rangeAir.Value;
            RangeRingVision = _rangeVision.Value;
            RangeRingBuild = _rangeBuild.Value;
            RangeRingWeapons = _rangeWeapons.Value;
            RangeRingMaxUnits = config.Bind("RangeRings", "MaxUnits", 40,
                new ConfigDescription("Потолок юнитов на класс колец дальности (перф отрисовки)",
                    new AcceptableValueRange<int>(4, 200))).Value;
            CommanderNoDoppler = config.Bind("Audio", "CommanderNoDoppler", true,
                "Гасить эффект Доплера в режиме командира: камера носится по карте на огромной скорости, и звук без этого летит «пилой»; вне режима звук не трогается").Value;
        }

        /// <summary>Немедленно сохранить конфиг (позиции окон и т.п.).</summary>
        public static void SaveConfig() => _configFile?.Save();
    }
}
