using BepInEx.Configuration;
using UnityEngine;

namespace SupremeNuclear
{
    internal static class Settings
    {
        public static bool ModEnabled = true;

        public static KeyCode ToggleCommanderKey = KeyCode.F10;

        // Камера
        public static KeyCode RotateModifierKey = KeyCode.Space;
        public static float ZoomPerNotch = 2.5f;          // доля изменения дистанции за щелчок колеса (шаг ограничен 0.3)
        public static float MinDistance = 60f;          // до фокуса, м
        public static float MinHeight = 25f;            // над рельефом/морем
        public static float MaxHeight = 30000f;         // минимум потолка; реальный потолок = вся карта в кадре
        public static float MaxAltitudeCeiling = 85000f; // жёсткий потолок: выше 90 км игра не может сдвигать origin (мировые пределы), начинается дрожь
        public static float RotateDegPerPixel = 0.25f;
        public static float PitchMin = 30f;           // ниже — слишком «в горизонт»
        public static float PitchMax = 60f;
        public static float TopDownStartDistance = 2500f; // дальше — плавно к виду сверху
        public static float TopDownFullDistance = 6000f;
        public static float TopDownPitch = 87f;
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

        // Экономика v1
        public static bool EconomyEnabled = true;
        public static string EconomyProfile = "default";

        // Выделение
        public static float BoxDragThresholdPx = 8f;
        public static float DoubleClickSec = 0.35f;
        public static int MaxSelected = 500;

        // Приказы
        public static float FormationSpacing = 30f;
        public static float AircraftOrderAltitude = 800f; // высота полёта к точке, м над рельефом

        // Стройка (Task-API v1)
        public static float BuildSecondsPerBuilder = 20f; // время постройки одним инженером, с
        public static float BuildDefaultCost = 250f;      // цена, если у здания не задан value
        public static float ProductionSecondsPerItem = 12f; // производство одной единицы, с
        public static float BuildSiteRadiusM = 140f;      // радиус площадки: инженер в нём — строит
        public static float BuildSiteSpacingM = 60f;     // мин. дистанция между площадками (анти-стены)
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

        private static ConfigFile _configFile;

        public static void Bind(ConfigFile config)
        {
            _configFile = config;
            ModEnabled = config.Bind("General", "Enabled", true, "Включить мод").Value;
            ToggleCommanderKey = config.Bind("General", "ToggleCommanderKey", KeyCode.F10,
                "Клавиша входа/выхода из режима командира").Value;

            RotateModifierKey = config.Bind("Camera", "RotateModifierKey", KeyCode.Space,
                "Клавиша-модификатор: зажать + ЛКМ = менять угол камеры").Value;
            ZoomPerNotch = config.Bind("Camera", "ZoomPerNotch", 2.5f, "Сила зума за щелчок колеса (доля расстояния)").Value;
            MinDistance = config.Bind("Camera", "MinDistance", 60f, "Минимальная дистанция камеры до точки фокуса, м").Value;
            MinHeight = config.Bind("Camera", "MinHeight", 25f, "Минимальная высота камеры над рельефом, м").Value;
            MaxHeight = config.Bind("Camera", "MaxHeight", 30000f,
                "Гарантированный потолок высоты, м (реальный = не меньше, чем вся карта в кадре)").Value;
            MaxAltitudeCeiling = config.Bind("Camera", "MaxAltitudeCeiling", 85000f,
                "Жёсткий потолок высоты, м — выше ~90 км игра не может сдвигать origin мировых объектов").Value;
            RotateDegPerPixel = config.Bind("Camera", "RotateDegPerPixel", 0.25f, "Скорость вращения (град/пиксель мыши)").Value;
            PitchMin = config.Bind("Camera", "PitchMinAngle", 30f, "Минимальный наклон камеры вниз, град").Value;
            PitchMax = config.Bind("Camera", "PitchMax", 60f).Value;
            TopDownStartDistance = config.Bind("Camera", "TopDownStartDistance", 2500f,
                "Дистанция, с которой угол начинает стремиться к виду сверху, м").Value;
            TopDownFullDistance = config.Bind("Camera", "TopDownFullDistance", 6000f,
                "Дистанция полного вида сверху, м").Value;
            TopDownPitch = config.Bind("Camera", "TopDownPitch", 87f).Value;
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
            StaleContactSec = config.Bind("Icons", "StaleContactSec", 30f,
                "Сколько секунд показывать вражеский контакт без обновления разведки, с").Value;

            EconomyEnabled = config.Bind("Economy", "Enabled", true, "Включить экономику v1 (месторождения + экстракторы)").Value;
            EconomyProfile = config.Bind("Economy", "Profile", "default",
                "Имя JSON-профиля в BepInEx/plugins/SupremeNuclear/Economy/").Value;

            BoxDragThresholdPx = config.Bind("Selection", "BoxDragThresholdPx", 8f).Value;
            DoubleClickSec = config.Bind("Selection", "DoubleClickSec", 0.35f).Value;
            MaxSelected = config.Bind("Selection", "MaxSelected", 500).Value;

            FormationSpacing = config.Bind("Orders", "FormationSpacing", 30f, "Шаг решётки приказов, м").Value;
            AircraftOrderAltitude = config.Bind("Orders", "AircraftOrderAltitude", 800f,
                "Высота полёта к точке назначения, м над рельефом").Value;

            BuildDefaultCost = config.Bind("Build", "DefaultCost", 250f,
                "Цена постройки, если у здания в каталоге не задана value").Value;
            ProductionSecondsPerItem = config.Bind("Build", "ProductionSecondsPerItem", 12f,
                "Производство одной единицы на фабрике (депо), с").Value;
            BuildSecondsPerBuilder = config.Bind("Build", "SecondsPerBuilder", 20f,
                "Время постройки здания одним инженером, с (добавление инженеров ускоряет)").Value;
            BuildSiteSpacingM = config.Bind("Build", "SiteSpacingM", 60f,
                "Минимальная дистанция между площадками построек, м (со снап-раздвигом)").Value;
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
        }

        /// <summary>Немедленно сохранить конфиг (позиции окон и т.п.).</summary>
        public static void SaveConfig() => _configFile?.Save();
    }
}
