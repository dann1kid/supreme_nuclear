# Архитектура Supreme Nuclear (as-built)

**Версия кода:** 0.72.0 · **Дата среза:** 2026-10-27 · **Соответствует:** `src/SupremeNuclear/` целиком.

Этот документ описывает, **как код устроен сейчас** (as-built). Гейм-дизайн — в [DESIGN.md](DESIGN.md);
инструкция для пользователей — в [README.md](README.md). Перед любым изменением кода прочитай
свои разделы здесь (минимум: §3 порядок тиков, §4 состояние, §7 инварианты, §10 рецепт твоей задачи).
Реестр известных долгов — §12: оттуда берём работу, туда же добавляем новую.

---

## 1. Общая картина

BepInEx-плагин (`net472`), который добавляет в Nuclear Option режим RTS-командира в духе SupCom.
Три способа взаимодействия с игрой, в порядке предпочтения:

1. **Композиция без патчей** — собственный `CameraBaseState`, обычные вызовы игрового API
   (`UnitCommand.SetDestination`, `Airbase.TrySpawnAircraft`, ...). Основной путь.
2. **Harmony-патчи** — ровно два (реестр в §6), оба Prefix с guard-условием «нет условий мода → true».
3. **Reflection на приватные поля** — только там, где публичного API нет (поля пилота, порог
   floating origin, запуск захвата здания). Все `FieldInfo`/`MethodInfo` кэшируются статически.

```
Nuclear Option (Assembly-CSharp)
   ▲                                    ▲
   │ Harmony (2 патча)                  │ прямой вызов API
   │ FixedUpdate: руление авиации       │
   │ AssessTargetPriority: hold-fire    │
┌──┴────────────────────────────────────┴──────────────────────────┐
│ SupremeNuclear.dll (BepInEx)                                     │
│                                                                  │
│  SupremeNuclearPlugin (точка входа)                              │
│    ├─ CommanderModeController ← ЕДИНСТВЕННЫЙ оркестратор Update  │
│    │    ├─ SelectionService   ├─ MoveOrderService                │
│    │    └─ CommanderCameraState (тикается игрой из LateUpdate)   │
│    ├─ CommanderHud  (IMGUI, OnGUI)   — мировой оверлей           │
│    ├─ SupcomUi      (uGUI, Update)   — экранные панели           │
│    └─ статические сервисы: EconomyService, GroundOrders,         │
│       AircraftOrders, StanceService, PurchaseService,            │
│       RouteEditor, RouteRibbonMesh, GameApi, RtsInput            │
└──────────────────────────────────────────────────────────────────┘
```

Справочники вне мода: `decompiled/` — декомпилированный код игры (сверяться с API),
`GroundControlRts/` — бета Ground Control 0.9.9.4, **только референс, код не копировать** (лицензия).

---

## 2. Каталог модулей

| Путь | Класс | Ответственность | Природа |
|---|---|---|---|
| `SupremeNuclearPlugin.cs` | `SupremeNuclearPlugin` | Точка входа: лог, конфиг, SelfCheck, Harmony, `sceneLoaded`-хук, добавление компонентов | MonoBehaviour (BepInEx) |
| `PluginInfo.cs` | `PluginInfo` | Guid/Name/Version — версия меняется здесь | константы |
| `Core/Log.cs` | `Log` | Обёртка над BepInEx `ManualLogSource` | static |
| `Core/Settings.cs` | `Settings` | Все настройки + `Bind(ConfigFile)`; значения по умолчанию и Bind-имена — только здесь | static |
| `Core/GameApi.cs` | `GameApi` | Тонкие обёртки игрового API: границы карты, лучи в землю, `GroundY`, HQ, размеры юнитов, монитор сдвигов origin, гейт видимости контактов (`TryGetContact`/`IsInspectableForeign`: ракеты — всегда, чужие — по свежести трека `StaleContactSec`); кэш `MapSettings` сбрасывается по сцене | static |
| `Core/SelfCheck.cs` | `SelfCheck` | Проверки предположений об API игры (стартовые + активационные); провалы показываются панелью в HUD | static |
| `Core/CommanderAudio.cs` | `CommanderAudio` | Гашение эффекта Доплера в режиме командира: `AudioSource.dopplerLevel` → 0 у активных источников сцены (`Suppress` на входе, `Restore` на выходе, дочист в `Tick` 2 Гц ловит новые источники; исходные значения хранятся и возвращаются, мёртвые прунятся) | static |
| `Input/RtsInput.cs` | `RtsInput` | Весь пользовательский ввод: `Input.*` + защита от чата (`Typing`) + `PointerOverUi`; НЕ Rewired | static |
| `CameraRts/CommanderCameraState.cs` | `CommanderCameraState` | Стратегическая камера: `CameraBaseState` без патчей; СКМ-драг (пининг якоря по горизонтальной плоскости через точку захвата — математически точная 1:1-петля без разрывов рельефа/крыш; поправка горизонтальная, в текущий и целевой фокус), зум-к-курсору, strategic zoom, edge-scroll; автодовороты угла (сверху издалека/вниз у земли, `Util.ComposePitch`); Space (зажать) — осмотр мышью вокруг точки, при отпускании взгляд возвращается (автодовороты на время осмотра отключены); фокус хранится в `GlobalPosition` | класс (тикается игрой) |
| `CommanderModeController.cs` | `CommanderModeController` | Вкл/выкл режима командира (F10) и **единственный** тик-оркестратор (см. §3) | MonoBehaviour |
| `Selection/SelectionService.cs` | `SelectionService` | Клик/рамка/двойной клик по типу, клик по аэродрому, группы управления 1–9; ЛКМ по ВИДИМОМУ чужому юниту/ракете — осмотр (`Inspected`: луч по телу → пик по иконке контакта, гейт как у вражеских иконок); рамка — КАСКАД: без Shift при наличии боевых (вооружённые мобильные) берёт только их, иначе всех движущихся (здания — только кликом); Shift-рамка добавляет всё в порядке инженеры → логистика → оборонительные здания → производящие → боевые (CategoryOf: «вооружённость» = GetMaxRange ≥ 1) | instance (внутри контроллера) + static `Groups` |
| `Orders/OrderModes.cs` | `OrderModes` | Текущий «заряженный» приказ `Pending` + подписи | static |
| `Orders/MoveOrderService.cs` | `MoveOrderService` | Точка выдачи всех приказов по ПКМ: пики врага (радиус притягивания `EnemyPickRadiusPx`, ракеты — ×2.5 и пикаются как цели перехвата), снап, формации, маркеры, отмена; «Атака веером» (R: ПКМ по врагам копит `FanTargets`, ПКМ по земле — раздача группы по целям `Util.AssignBalanced`: ближайшая цель с балансом N/M) и Disperse Move (Alt+Shift+R: очередь групповых вейпоинтов распределяется — юнит i получает группу i mod G, у каждого остаётся одна точка-прямой вектор; `GroundOrders/AircraftOrders.DisperseKeep`) | instance (внутри контроллера) |
| `Orders/GroundOrders.cs` | `GroundOrders` | Цепочки вейпоинтов наземки/флота поверх `UnitCommand.SetDestination`; выдаёт следующую точку при прибытии (90 м). Виды цепочек: Move / AttackMove (stop-and-fire: цель в дальности ПРИГОДНОГО по цели оружия (`CalcOpportunityThreat`, даталинк) → стоп, нативный огонь турелей → продолжение; корабли НЕ останавливаются — дерутся на ходу, «стоп» кораблю = циркуляция) / Attack (преследование: подход на `min(0.9×текущей, 0.8×дальности пригодного оружия)` — НИКОГДА не дальше текущей позиции (приказ «назад» уводил корабли от берега); групповой клик назначает каждому свой АЗИМУТ-СЕКТОР полукруга — `Chain.ApproachBearing` (мир-фикс. угол, держится при движении цели; NaN — живой азимут юнит→цель у одиночных/дозревших), раздача: `MoveOrderService.IssueAttackOrders` сортирует атакующих по азимутам и веерит `FanBearingOffset(i, N, FormationSpacing, PursueRange)` — маршруты не пересекаются, дальнобойные встают внешним кольцом; ракеты кончились → дальность пересчитывается и юнит сам подтягивается внутрь). Shift-смешанные очереди (v0.73.0) исполняются В ПОРЯДКЕ КЛИКОВ (точка → цель → точка), как планы авиации: каждая цель очереди (`TargetQueue`+`TargetAfter`) несёт позицию серии — сколько точек стояло в очереди на момент клика; дозревшая цель поднимается `PromoteDue` (как только точки до её позиции пройдены), после смерти цели — следующая дозревшая (серия) или продолжение маршрута с точки промоушена (`ResumeKind`). Производной точки подхода в Points НЕТ — очередь реальных точек на время атаки сохраняется и редактируется (`DrawStartIndex`=Index). Отрисовка смешанной очереди (v0.73.2) — ЕДИНАЯ лента В ПОРЯДКЕ КЛИКОВ: цели — узлы очереди на своей позиции серии (`TargetAfterOf`), лента течёт ЧЕРЕЗ цель (как планы авиации/SupCom); нога до цели и от неё — по одному разу на цель (`DrawnGroundTargets`/`DrawnTargetExits`, CommanderHud.DrawGroundChain), текущая погоня — яркая с пульсирующим крестом, ждущие — приглушённые со статичным. `IsIdle` — «нет непройденных точек и целей» (пуш занятых из зоны стройки не рвёт приказ). Каждая точка несёт groupId клика (0 — индивидуальный приказ): групповой вейпоинт — свойство ТОЧКИ, не выделения (маркер/лента/драг/удаление — на всю группу приказа) | static |
| `Orders/RouteEditor.cs` | `RouteEditor` | Drag-редактирование вейпоинтов (ЛКМ по точке), снап постановки к юнитам; групповой драг — по groupId у ВСЕХ членов группы приказа (индексы точки у каждого свои), индивидуальная точка — только владелец | static |
| `Orders/RouteRibbonMesh.cs` | `RouteRibbonMesh` | Меш-ленты маршрутов через `Graphics.DrawMesh` (эксперимент, по умолчанию ВЫКЛ — `RouteUseMesh=false`; углы скруглены `Util.RoundChainCorners` как в IMGUI-ленте) | static |
| `Orders/WaypointScanner.cs` | `WaypointScanner` | ЕДИНЫЙ скан вейпоинтов под курсором (захват драга + удаление; приоритет: площадки → движения очередей → цепочки → планы); авиаточки сканятся на ЗЕМЛЕ, где рисуется маркер (y=GroundY), а не с AGL-высотой | static |
| `Core/Util.cs` | `Util` | Чистые функции (серии `Runs` для стаков плиток; `GroupPrefixCentroids` — слияние групповых маршрутов; `RoundChainCorners` — скругление углов лент Безье-дугами) — тестируются | static |
| `UiRts/Catalog.cs` | `Catalog` | Чистая логика плиток каталога: тир-фильтр (терцили по цене), короткие подписи (`code`/имя), бейджи типов наземки (VehicleType→ТНК/ЗРК/…), роль авиации из roleIdentity (И/У/М/ТР/РЛС), сводка вооружения группы (`WeaponTallyAccumulator` + домен-теги АА/АЗ/У/ПР/РЕБ/Я, `Util.RuPlural`) — тестируется | static |
| `Aviation/AircraftOrders.cs` | `AircraftOrders` | Планы полётов: сегменты (Transit/AttackMove/Patrol/Attack/Rtb), профили высот (AGL + огибание), продвижение по маршруту; Attack — миссия вылета (цель + якорь подлёта, бой отдаётся нативному AI когда цель в зоне боя); Rtb — вставляет/снимает SortieService; точки несут groupId приказа — групповой драг/удаление по нему (планы бортов асинхронны, индексы сегментов/точек у каждого свои; стартовая точка — группа 0, индивидуальная) | static |
| `Aviation/AircraftSteeringPatch.cs` | `AircraftSteeringPatch` | Harmony-Prefix руления борта с приказом (§6.1) | Harmony |
| `Aviation/HeloSteeringPatch.cs` | `HeloSteeringPatch` | Harmony-Prefix руления вертолёта/тильтвинга с приказом (§6.4): тот же план (`AircraftOrders`), но вертолётная перегрузка `AutoAim(dest, AGL, …)` | Harmony |
| `Aviation/AircraftTargetPinPatch.cs` | `AircraftTargetPinPatch`, `HeloTargetPinPatch` | Harmony-Postfix пиннинга цели приказа и конуса атаки-движения — самолёты (§6.3) и вертолёты (§6.5), общее ядро выбора/вето | Harmony |
| `Aviation/SortieService.cs` | `SortieService` | Миссии вылетов (host): RTB при «сухих» станциях/низком топливе, передача нативной посадке у аэродрома (самолёты — `AILandingState`, вертолёты — вертикальная `AIHeloLandingState`), аэродромный сервис (перевооружение/заправка за деньги, интерим до логистики), возобновление вылета после взлёта; ТЕРМИНАЛЬНЫЙ RTB (команда «на базу», план [Rtb] без продолжения): самолёт паркуется на такси на время сервиса, вертолёт — нативно, после сервиса — ДЕСПАВН в резерв ангара (`AirbaseReserve`); `Launch()` — выкат из паркинга по новому приказу (самолёты — такси, вертолёты — вертикальный взлёт) | static |
| `Aviation/AirbaseReserve.cs` | `AirbaseReserve` | Резерв ангаров аэродромов: вернувшиеся «на базу» борты после сервиса (деспавн) — невидимый инвентарь базы (тип × N, `Version` для кэша панели); выкат — `PurchaseService.DeployReserve` (нативный спавн из ангара, бесплатно, только со своего аэродрома); резерв теряется вместе с базой при захвате | static |
| `Economy/EconomyService.cs` | `EconomyService` | Экономика v1 (host-only): спавн экстракторов на месторождениях, нативный захват, тиковый доход, апгрейд тиров | static |
| `Economy/EconomyProfile.cs` | `EconomyProfile` | Баланс экономики из JSON + автосоздание образца | данные |
| `Units/PurchaseService.cs` | `PurchaseService` | Покупки за `factionFunds`: техника через депо, авиация через аэродром (host-only) | static |
| `Units/StanceService.cs` | `StanceService` | Стойка «не открывать огонь» (hold-fire) | static |
| `Units/TurretHoldFirePatch.cs` | `TurretHoldFirePatch` | Harmony-Prefix hold-fire (§6.2) | Harmony |
| `Tasks/BuildTasks.cs` | `BuildTasks` | Task-API v1: задачи постройки зданий (хост) — потоковая оплата, инженеры, спавн по готовности с поворотом (`YawDeg`, удержание ЛКМ — вращение); режим установки `Pending` (§14); ангары — к своему аэродрому в радиусе + дорисовка рулёжки (`ConnectTaxiway`), привязка нативным `Spawner.SpawnBuilding(..., airbase, ...)` (§14, этап 2) | static |
| `Tasks/ProductionTasks.cs` | `ProductionTasks` | Очереди производства «фабрик» (депо+аэродром): поток (аэродром — параллельно по ангарам, умеющим строить тип позиции, `PaySlots`; `CanBuild`-гейт заказа), пауза/цикл, ралли, правка стаков плиток очереди (`InsertAfter`/`RemoveTail` с возвратом денег) | static |
| `Tasks/BuildGhosts.cs` | `BuildGhosts` | 3D-призраки площадок ПО МОДЕЛИ GC: меши префаба через `Graphics.DrawMesh` (без GameObject) + встроенный `Hidden/Internal-Colored` (полупрозрачный силуэт); тинты стройка/валидно/занято; подача из `Render()` в Update | static |
| `Core/OrderTemplates.cs` | `OrderTemplates` | Персистентные шаблоны заказов (JSON в plugins/SupremeNuclear/Templates.json): снимок очереди фабрики → повторная постановка | static |
| `Ui/CommanderHud.cs` | `CommanderHud` | IMGUI-оверлей: иконки, кольца/скобки выделения, красные скобки осмотра чужой цели, HP-полоски повреждённых, маршруты, маркеры приказов, notices, SelfCheck-панель, F11-debug | MonoBehaviour (OnGUI) |
| `Ui/RangeRings.cs` | `RangeRings` | Кольца дальности выделенных юнитов (SupCom): атака-АС красное / атака-АА синее (макс. дальность РАЗРЕШЁННЫХ гейтом станций своего домена целей — effectiveness.antiSurface/antiAir > 0, порог домена как у MapToolTip игры) / разведка белое (радар+визуал детекторов юнита) / стройка охра (`BuildSiteRadiusM` инженеров); торпедных (зелёных) нет. РЕЖИМ «ОРУЖИЕ» (RangeRings.Weapons, чип под миникартой, v0.77): ВМЕСТО доменных колец атаки — кольца по КЛАССАМ вооружения (Пушки жёлтое / Рак.ВВ синее / Рак.АС красное / Бомбы+ЯЗ фиолетовое; `Catalog.ClassOf`, тоже только разрешённые гейтами станции) — визуализация гейтов: выключил класс — его кольцо исчезло. РЕНДЕР v0.79 — как в SupCom: НЕ текстура, а ВЕКТОРНАЯ ПОЛИЛИНИЯ в мире (ломаная 24..96 сегментов по экранному размеру кольца — вблизи видна ломаная, честно как в SupCom), вершины проектируются в экран КАЖДЫЙ Repaint из живых позиций юнитов (идеальная прибитость к миру, нулевая стоимость в покое-движении кроме проекций), рисуются ОДНИМ батчем GL.LINES (материал Internal-Colored как у силуэтов, пиксельная матрица GUI y-вниз, линия ~2 px двойным рядом вершин); сегменты за камерой пропускаются, из-под окна миникарты вырезаются `ClipOutRect` (Liang-Barsky дополнение, чистая, +5 тестов); ОБЪЕДИНЕНИЕ v0.80: сегмент скрыт, если его точка НА ДУГЕ (середина хорды, вытянутая до радиуса) лежит строго ВНУТРИ чужого круга ТОГО ЖЕ класса (`ArcInsideOther` по снимку живых центров Snap*) — внутренние дуги пересечений не рисуются, виден только внешний контур «пятна»; строгое «<» — совпадающие кольца рисуют общий контур, вложенное гаснет. Сброс выделения/выключение режима СТИРАЕТ кольца (ClearAll в ранних выходах Tick). Заливки НЕТ (v0.72–0.78: CPU-маска с заливкой — мягко, лагало на заливке, удалена вместе с UnionBand/RasterDiscInto/ComposePixel/RangeRings.Fill/Resolution). Сбор кругов (станции/сенсоры) — 10 Гц из `CommanderHud.Update`, отрисовка каждый OnGUI (Repaint) | static |
| `UiRts/SupcomUi.cs` | `SupcomUi` | uGUI-панели (SupCom-раскладка): экономика (лево-верх), свойства выделения + карточка осмотра чужой цели/ракеты + командная карта (лево-низ), панель-табы Производство/Список/Апгрейд (центр+право-низ); плитки 48×48 (`Tile`/`TileSpec`: иконка — авиация силуэтом `mapIcon`, подпись `code`, бейдж типа, ×N/цена, прогресс) и hover-карточка определения у курсора (опрос в Update, без uGUI-событий) | MonoBehaviour (uGUI) |
| `UiRts/UiKit.cs` | `UiKit`, `FloatingWindow` | Строительные блоки uGUI: плавающее окно (drag/ресайз/закрытие/вкладка восстановления), скролл-список, прогресс-бар | static + MonoBehaviour |
| `UiRts/MinimapPanel.cs` | `MinimapPanel` | Плавающая миникарта: фон `MapSettings.MapImage` (RawImage+uvRect) либо рельеф (высоты+горизонтали по сетке GroundY), иконки юнитов с развед-гейтом (спрайты как в IMGUI, растут с зумом, 10 Гц), ромбы аэродромов/месторождений, рамка обзора камеры (кламп в границы карты), зум колесом «в курсор»; ЛКМ — прыжок, ПКМ — приказ | MonoBehaviour (uGUI) |

---

## 3. Поток исполнения — кто когда тикает

### 3.1 Точка входа

`SupremeNuclearPlugin.Awake` (один раз при загрузке):
`Log.Init` → `Settings.Bind` → `ModEnabled`? → `SelfCheck.RunStartup` → `_harmony.PatchAll()` →
подписка на `sceneLoaded` → `AddComponent` × 3 (контроллер, CommanderHud, SupcomUi).

`sceneLoaded` (каждая смена сцены) — полный сброс статического состояния:
`GameApi.ResetSceneCache()`, `EconomyService.Reset()`, `BuildTasks.Reset()`,
`BuildGhosts.Reset()`, `ProductionTasks.Reset()`, `AircraftOrders.Reset()`,
`GroundOrders.Reset()`, `StanceService.Reset()`, `WeaponGateService.Reset()`,
`DefenseService.Reset()`, `SalvoService.Reset()`, `AirbaseReserve.Reset()`,
`SelectionService.ResetGroups()`, `RangeRings.Reset()`, `RouteRibbonMesh.Destroy()`,
`CommanderAudio.Restore()` (страховка доплера при выходе без Deactivate),
`OrderModes.Pending = None`. Новое статическое состояние
добавляй сюда же (инвариант №11).

### 3.2 `CommanderModeController.Update` — единственный оркестратор (порядок важен!)

```
если НЕ InMission            → Deactivate(silent) и return
TickOriginShiftMonitor()     — всегда в миссии (диагностика F11)
EconomyService.Tick()        — всегда в миссии (host: старт/доход)
SortieService.Tick()         — всегда в миссии (host: миссии вылетов — RTB/сервис/повтор)
SalvoService.Tick()          — всегда в миссии (host: залп-приказы — пуск ракетных станций)
WeaponGateService.Tick()     — всегда в миссии (host: LastFiredTime-блок выключенных гейтом станций)
RouteRibbonMesh.Tick()+Render() — всегда в миссии
F10                          → Activate()/Deactivate()
если НЕ IsActive             → return
камера сменилась не нами      → Deactivate(silent) и return   (вселенение в кабину и т.п.)
CommanderAudio.Tick()         — дочист доплера (новые источники: взлёт/ракеты), 2 Гц
RouteEditor.Tick(this)       ← СТРОГО ДО Selection: Consumed гасит выделение на кадр драга
BuildGhosts.Render()         — подача силуэтов площадок ДО рендера (DrawMesh из OnGUI мерцает!)
режим установки здания (BuildTasks.NewFrame/Pending: ЛКМ — площадка, удержание ЛКМ —
                              поворот выходом к курсору, ПКМ/Esc — отмена;
                              без Shift — новая очередь, с Shift — серия)
режим призраков шаблона (OrderTemplates.PendingTemplate: ЛКМ строит набор, ПКМ/Esc)
Selection.Tick()             — ЛКМ: клик/рамка (пропускает кадр при Consumed)
MoveOrders.Tick()            — ПКМ: ВЫДАЧА ПРИКАЗОВ (только по событию!)
GroundOrders.Tick()          — продвижение цепочек наземки; GroundOrders.Prune()
AircraftOrders.Prune(); StanceService.Prune(); DefenseService.Prune(); SalvoService.Prune(); WeaponGateService.Prune()
TickControlGroups()          — Ctrl+1..9 назначить, 1..9 вызвать (без модификаторов), дабл-тап = камера
Alt+1..4                     — профиль высоты (штамп + ApplyModesToCurrent головному сегменту)
Shift+1..3                   — скоростной режим (самолёты); Alt+D — цикл авто-защиты
H                            — hold-fire выделенных; X — ручной сброс ловушек
M/A/U/T/V/R/S/Esc (при выделении) — заряд режима приказа / стоп (V — залп,
                              R — «Атака веером»: ПКМ по врагам копит цели, ПКМ по земле — раздача)
Alt+Shift+R                  — Disperse Move: очередь вейпоинтов группы → прямые векторы (i mod G)
```

### 3.3 Остальные пути исполнения (вне Update контроллера)

| Что | Кто вызывает | Частота |
|---|---|---|
| `CommanderCameraState.UpdateState/FixedUpdateState` | игра, `CameraStateManager` (LateUpdate) | каждый кадр в режиме командира |
| `AircraftSteeringPatch.Prefix` | игра, `AIPilotCombatModes.FixedUpdateState` | физический тик (~50 Гц) на каждый AI-самолёт |
| `HeloSteeringPatch.Prefix` | игра, `AIHeloCombatState.FixedUpdateState` | физический тик (~50 Гц) на каждый AI-вертолёт/тильтвинг |
| `AircraftTargetPinPatch.Postfix` | игра, `AIPilotCombatModes.AssessHQTargets` (On5sCheck пилота) | раз в 5 с на каждый AI-самолёт в боевом состоянии |
| `HeloTargetPinPatch.Postfix` | игра, `AIHeloCombatState.AssessHQTargets` (On5sCheck пилота) | раз в 5 с на каждый AI-вертолёт в боевом состоянии |
| `TurretHoldFirePatch.Prefix` | игра, `Turret.AssessTargetPriority` | по потребности игры |
| `CommanderHud.OnGUI` | Unity | **2+ раза за кадр** (Layout/Repaint-события) |
| `SupcomUi.Update` | Unity | каждый кадр, рефреш контента раз в 0.2 с |
| `EconomyService.PayIncome` | внутренний таймер | раз в `tickSeconds` (5 c) |

`Activate()`/`Deactivate()` контроллера и `EnterState`/`LeaveState` камеры строго парны —
см. инвариант №9 (§7).

---

## 4. Владение состоянием

Правило: **у каждого куска состояния один владелец**; чужое состояние меняем только через его API.
Статическое состояние переживает смены сцен — проверяй очистку при добавлении нового.

| Состояние | Природа | Пишут | Читают | Очистка |
|---|---|---|---|---|
| `SelectionService.Selected` (+`SelectedAirbase`) | instance | ClickSelect, BoxSelect, RecallGroup, SelectTypeStatic, Reset | все сервисы и оба UI | `Reset()` при входе/выходе; prune null/disabled в каждом `Tick()` |
| `SelectionService.Inspected` | instance | ClickSelect (осмотр ЛКМ), RouteEditor (клик по площадке/мимо), Reset | SupcomUi (карточка осмотра), CommanderHud (скобки) | `Reset()`; в `Tick()` — цель погибла/ушла из разведки → null |
| `SelectionService.Groups` | **static** | AssignGroup | RecallGroup (чистит null/disabled) | `ResetGroups()` по sceneLoaded |
| `OrderModes.Pending` | static | хоткеи, кнопки UI, MoveOrderService | MoveOrderService, SupcomUi | сброс при пустом выделении и по sceneLoaded |
| `AircraftOrders.Plans` | **static** | Set/Enqueue/SetPatrol/Attack/EnqueueAttack/Salvo/SalvoArea/EnqueueSalvoTarget/Cancel/ConvertToPatrol/SetPoint/ApplyModesToCurrent/Forget (RTB вставляет SortieService; SalvoFired ставит SalvoService) | Step, TryGetDestination, TryGetPlan, CollectAll, AircraftTargetPinPatch, SalvoService, SortieService | `Prune()` (disabled); `Reset()` по sceneLoaded |
| `AircraftOrders.ProfileIndex`/`SpeedIndex` | static | Alt+1..4/Shift+1..3, кнопки UI, SetProfile/SetSpeed (+`ApplyModesToCurrent`) | MakeSegment/MakeAttackSegment/MakeSalvoSegment/Enqueue(CanAppend) | — (осознанно живут: предпочтение игрока) |
| `DefenseService.Modes`/`NextPopAt` | **static** | Cycle/Set (Alt+D, панель), PopNow (X) | steering-патчи №1/№4 (ShouldEvadeNative/TickFlares), панель | Prune (disabled); `Reset()` по sceneLoaded ✔ |
| `SalvoService.Stations` (whitelist станций залпа) | **static** | ToggleStation (чипы панели) | SalvoService.Tick, панель (подсветка чипов) | Prune (disabled); `Reset()` по sceneLoaded ✔ |
| `AircraftOrders.Version`, `GroundOrders.Version`, `RouteEditor.DirtyVersion` | static, монотонные | любое изменение планов/цепочек/точек | RouteRibbonMesh, кэши кривых | никогда (только рост) |
| `GroundOrders.Chains` (Points+Kind+Target) | **static** | Set/Enqueue/SetAttack/SetQueuePoint/Clear (статус stop-and-fire — Tick) | Tick, TryGetQueue, CollectAll, KindOf/TargetOf (UI) | Prune/Clear (disabled); `Reset()` по sceneLoaded |
| `StanceService.HoldFire` | **static** | Toggle | TurretHoldFirePatch, CountHolding | Prune (disabled); `Reset()` по sceneLoaded |
| `WeaponGateService.Blocked` (маска выключенных классов оружия, DESIGN §2.11) | **static** | ToggleForSelection (кнопки-гейты) | патч №2 (турель), №3/№5 (гейт-перевыбор станции), `SalvoService.IsSalvoStation`, `WeaponGateService.Tick` (блок `LastFiredTime`), SupcomUi (кнопки/подсветка) | Prune (disabled); `Reset()` по sceneLoaded |
| `AirbaseReserve.Reserve` (+`Version`) | **static** | Add (SortieService: деспавн терминального RTB), TryTake (DeployReserve) | панель аэродрома (SupcomUi, плитки «РЕЗ ×N»; `Version` в хэше контекста) | Prune (мёртвые базы); `Reset()` по sceneLoaded ✔ |
| `MoveOrderService.Markers` | instance | IssuePending | CommanderHud.DrawOrderMarkers | PruneMarkers (2.5 с) |
| `MoveOrderService.LastDestinations` | instance | IssueFormationOrders, CancelAll | задел на будущее | PruneMarkers (disabled) |
| `MoveOrderService.FanTargets` (цели «Атаки веером») | instance | ChargeFanAttack (R/кнопка, с очисткой), Tick (ПКМ по врагу в режиме — добавление), IssueFanAttack (очищает) | IssueFanAttack, CommanderHud.DrawFanTargets | заряд режима и выдача; мёртвые прунятся при выдаче/отрисовке |
| `EconomyService.Extractors`/`_profile` | static | TryStart, TryUpgrade, PayIncome | TierOf/Income*/UI | `Reset()` по sceneLoaded ✔ |
| `RouteRibbonMesh._meshes/_mats` | static | Rebuild/EnsureMaterials | Render | `Destroy()` по sceneLoaded (донор умершей сцены недействителен) |
| `RangeRings` (кольца дальности: круги с юнитами/маска/текстура экрана) | **static** | Tick из CommanderHud.Update при выделении: сбор кругов 10 Гц + покадровая растеризация по dirty-детекту | Draw (CommanderHud) | `Reset()` по sceneLoaded (круги держат юнитов — чистим; текстура экранная живёт) |
| `Settings.RangeRingAttack/Air/Vision/Build/Weapons` (+удержанные записи конфига) | static + ConfigEntry | `SetRangeRing`/`SetRangeWeapons` (чипы MinimapPanel) | RangeRings, MinimapPanel | конфиг (единственный тоггл, пишущий Value записи напрямую — см. долг №25) |
| `SupcomUi._selectionHash/_contextHash` | instance | RefreshRoster/RefreshContext | — | EnsureUi при пересборке канваса |
| `CommanderHud.Notices` | static List | `Notice()` | OnGUI | само по истечении времени |

`RouteEditor.Consumed`/`IsDragging` — семафор «ЛКМ занят редактором» на текущий кадр,
читается `SelectionService.Tick()` **после** `RouteEditor.Tick()`.

---

## 5. Контракты с Nuclear Option (API-поверхность)

Всё, к чему мод прикасается в игре. Меняются патчами игры — проверять при обновлении (риск в DESIGN §6).
Новые предположения об API сопровождаются проверкой в `SelfCheck`.

**Юниты и приказы**
- `UnitCommand.SetDestination(GlobalPosition, playerCommand: true)` — единственный способ движения
  наземки/флота; сеть (host/client) решается игрой сама.
- `Unit`: `NetworkHQ`, `NetworkunitName`, `definition`, `persistentID`, `maxRadius`, `disabled`,
  `GetComponent<UnitCommand>()`; типы: `GroundVehicle`, `Ship`, `Aircraft`, `Building`, `VehicleDepot`, `Missile`.
- `UnitDefinition`: `friendlyIcon`, `hostileIcon`, `mapIcon`, `mapOrient`, `unitName`, `value`, `jsonKey`;
  `VehicleDefinition.spawnOffset`; `AircraftDefinition.aircraftParameters.GetRandomLiveryForFaction(faction)`.
- `UnitRegistry.allUnits` — итерация всех юнитов (иконки, пики, сканы). Не аллоцировать на кадр.

**Авиация**
- `aircraft.autopilot.AutoAim(dest, aimVelocity, ignoreCollisions, runwayAlign, effort, bankAllowed,
  followTerrain, altitudeHold, targetVelocity)` — игровой автопилот рулит, мы только даём точку
  (САМОЛЁТЫ: altitudeHold — мировая Y).
- **Вертолёты/тильтвинги**: `AutopilotHelo`/`AutopilotTiltwing` перекрывают только 5-аргументную
  перегрузку `AutoAim(dest, altitudeHold AGL, aimDir, targetVel, followTerrain)` — самолётная
  9-аргументная для них НЕ годится; там же altitudeHold — высота НАД РЕЛЬЕФОМ, throttle вертолёту
  не пишем (автопилот ведёт collective сам). Боевой AI — `AIHeloCombatState` (свои `currentTarget/
  currentWeaponInfo/timeWithoutTarget`, своей tracking-поля нет); посадка — вертикальная
  `AIHeloLandingState` (садится к ближайшему аэродрому, запрашивает точку сам).
- `AIPilotCombatModes` (reflection-поля): `PilotBaseState.destination`, `.aircraft`, `.controlInputs`,
  `AIPilotCombatModes.timeWithoutTarget` — сброс таймера «без цели», иначе AI уйдёт на возврат;
  `currentTarget`/`currentTargetTracking`/`currentWeaponInfo`/`allowTargetAssessment` — пиннинг цели (§6.3).
- **Целеуказание/даталинк**: `FactionHQ.TryGetKnownPosition(unit, out pos)` / `GetTrackingData(id)` —
  цель видна, только если её держит трекинг фракции (сенсоры); огонь вне даталинка не открывается
  (кроме точечных вооружений — бомбы/баллистика/артиллерия). `TrackingInfo`: `lastSpottedTime`
  (свежесть контакта; живой < 4 с), `GetPosition()` — актуальная позиция под живым контактом,
  иначе последняя известная (якорь вражеских иконок и пика осмотра).
- **Ракеты/бомбы** (карточка осмотра): `Missile.GetWeaponInfo()/EngineOn()/GetSeekerType()/GetYield()/
  GetThrustDuration()/targetID/GetRemainingBurnTime()`, `Unit.speed/radarAlt`,
  `Vector3.GlobalY()` — проверки в SelfCheck (активационные). Цель ГСН показываем только
  если она наша (RWR-принцип: чужие цели не подсматриваем).
- **Боевой AI**: `CombatAI.AnalyzeTarget(station, unit, trackingInfo, …)` — совместимость станции
  вооружения с целью (opportunity>0 = может бить); `weaponManager.currentWeaponStation`/`ClearTargetList`/
  `AddTargetList` — назначение огневых целей; ракетный залп нативно распределяется по соседним целям.
- **Вылеты/логистика**: `Unit.GetMaxRange()`, `Aircraft.GetFuelLevel()`, `WeaponStation.Ammo/FullAmmo/
  GetAmmoTotal/Cargo/WeaponInfo.targetRequirements`; `Unit.RpcRearm(RearmEventArgs{Stations})` — сетевое
  перевооружение (нативный `RequestRearm` закрыт для AI-бортов гвардой `Player != null`); `Aircraft.Refuel
  (null)` — заправка; `Pilot.pilots[0].SwitchState(pilot.AILandingState)` — нативная автопосадка;
  `FactionHQ.GetNearestAirbase(pos, RunwayQuery)`/`Airbase.center/GetRadius/CurrentHQ`.
- `BattlefieldGrid.GetUnitsInRangeEnumerable(GlobalPosition, range)` — сетевой запрос юнитов в радиусе
  (сканы наземки), дешевле ручного перебора `allUnits`.

**Деньги и покупки (host-only!)**
- `FactionHQ`: `factionFunds`, `AddFunds`, `faction`, `GetTrackingData(persistentID)` (разведка).
- `VehicleDepot.TrySpawnVehicle(def)`; `Airbase.CanSpawnAircraft(def)` / `TrySpawnAircraft(...)`;
  `Airbase.CurrentHQ`; `FactionRegistry.airbaseLookup`.
- `Spawner.SpawnFromUnitDefinitionInEditor(def, GlobalPosition, rot, null, label)` — спавн экстракторов.
- `Building.capturable`, `Networkairbase`; приватный `Building.CheckForCapture` (reflection) — запуск
  нативного цикла захвата у заспавненного со стока здания.

**Мир**
- `Datum.origin`, `Datum.LocalSeaY`; `GlobalPosition` ↔ local через `ToGlobalPosition()/ToLocalPosition()`.
- `PhysicsLayers.StaticsMask` — рельеф для лучей; `MapSettings.MapSize` — границы карты
  (центр берётся с трансформа префаба, не с нуля!).
- `FloatingOrigin.Instance` + приватное поле `threshold` (reflection): в режиме командира порог
  поднимается до 1e9 (замораживаем сдвиги origin), при выходе — восстанавливается.

**Состояние/сеть/камера**
- `GameManager.gameState` (`SinglePlayer`/`Multiplayer` = InMission), `GameManager.GetLocalHQ`.
- `NetworkManagerNuclearOption.i.Server.Active` — признак хоста (авторитет экономики/покупок).
- `SceneSingleton<CameraStateManager>.i`: `SwitchState`, `mainCamera`, `freeState/cockpitState/
  controlledState/TVState/chaseState`, `CheckOriginShift`, `allowInputs`, `windNoiseExternal`,
  `cameraPivot`, `cameraVelocity`; `CameraBaseState` (4 виртуальных метода — наша камера).
- `FlightHud.EnableCanvas(false)` — прячет кабину на входе в режим.
- `InputFieldChecker.InsideInputField` — игрок печатает в чате (гасим ввод).
- `Encyclopedia.i`: `vehicles`, `aircraft`, `buildings` — каталоги для покупок/спавна.

---

## 6. Реестр Harmony-патчей

Добавление нового патча — крайняя мера; сначала композиция (§1). Каждый патч: guard-Prefix,
возврат `true` = оригинал, запись сюда.

| # | Цель | Тип | Файл | Условие перехвата | Откат |
|---|---|---|---|---|---|
| 1 | `AIPilotCombatModes.FixedUpdateState` | Prefix | `Aviation/AircraftSteeringPatch.cs` | у борта есть план (`AircraftOrders.TryGetDestination`); таймер `timeWithoutTarget` сбрасывается при ЛЮБОМ живом плане (возврат — только по топливу/боеприпасам через SortieService). **Авто-защита «Авто»** (DefenseService): ракетное предупреждение → `true` (нативное уклонение/контрмеры, v0.68.0). Attack: цель в зоне боя (даталинк + дальность × `AttackEngageFactor`) → `true` (нативный бой, цель запиннена §6.3), иначе мод ведёт подлёт к цели/якорю. AttackMove: рядом враг → `true`. Salvo: нативный бой НЕ включается — рулит мод, пуски делает SalvoService. Прочее: пишет `destination`, тротл скоростного режима сегмента (`ThrottleOf`), зовёт `AutoAim`; режим «Ловушки» — `DefenseService.TickFlares` | без плана — `true`, штатный AI |
| 2 | `Turret.AssessTargetPriority` | Prefix | `Units/TurretHoldFirePatch.cs` | юнит турели в стойке hold-fire (`StanceService.IsHoldFire`) ИЛИ станция турели в выключенном классе гейта (`WeaponGateService.BlockedOf`) — турель не берёт цель и не целится | иначе `true` |
| 3 | `AIPilotCombatModes.AssessHQTargets` | **Postfix** | `Aviation/AircraftTargetPinPatch.cs` | оригинал ВСЕГДА отрабатывает; после — если `allowTargetAssessment` и нет залпа: Attack-сегмент → цель приказа становится `currentTarget` (станция — лучшая по `CombatAI.AnalyzeTarget`, даталинк-гейт: незатрекнутую не пинним); AttackMove → цель вне конуса вектора приказа (`AttackMoveConeDeg`) снимается. **Гейт вооружения (v0.71.0)**: ДО ветки приказа — натив выбрал станцию выключенного класса → перевыбор лучшей разрешённой по той же цели (`ApplyWeaponGates`), разрешённых нет → цель снимается (действует и на свободные борта — автоогонь) | без приказа и без гейта — ничего |
| 4 | `AIHeloCombatState.FixedUpdateState` | Prefix | `Aviation/HeloSteeringPatch.cs` | вертолётный близнец №1: тот же план, тот же сброс `timeWithoutTarget`, авто-защита и логика атаки/attack-move/залпа; отличие — вызов вертолётной перегрузки `AutoAim(destination, AGL профиля, …)` (самолётная 9-аргументная не перекрыта `AutopilotHelo` и для вертолёта неверна; throttle не трогаем — автопилот ведёт сам, скоростные режимы вертолётам не назначаются) | без плана — `true`, штатный AI |
| 5 | `AIHeloCombatState.AssessHQTargets` | **Postfix** | `Aviation/AircraftTargetPinPatch.cs` (`HeloTargetPinPatch`) | вертолётный близнец №3: общее ядро PinTarget/VetoOutsideCone; guards — залп и уход оригинала в транспортный режим (`currentState != instance`); tracking-поля у вертолётного состояния нет; тот же гейт-перевыбор (`ApplyWeaponGates`) | без приказа и без гейта — ничего |

---

## 7. Инварианты — не ломать

Нарушение любого пункта ниже — источник «загадочных» багов. Проверяй по этому списку диф.

1. **Floating origin.** Все долгоживущие мировые точки (приказы, планы, якоря) — только
   `GlobalPosition`. Локальный `Vector3` протухает при сдвиге origin (порог 1024 м по оси).
   Исключение — одноразовые вычисления в пределах кадра.
2. **Host-only мутации.** Деньги (`AddFunds`), спавн, апгрейды — только при
   `NetworkManagerNuclearOption.i.Server.Active`. Клиент выдаёт лишь приказы с
   `playerCommand: true`. Иначе десинк/тишина в MP.
3. **Guard-Prefix.** Harmony-Prefix обязан вернуть `true`, если мод не берёт управление.
4. **Приказы — только по событию** (`RtsInput.RightDown` и т.п.). Ничего игрового — на каждый кадр.
5. **Порядок тиков** (§3.2) фиксировать нельзя: `RouteEditor.Tick` строго до `Selection.Tick`
   (иначе драг вейпоинта превратится в рамку выделения); оркестратор — только `CommanderModeController.Update`.
6. **Ввод через `RtsInput`**, не `Input` напрямую — там `Typing`-защита от чата и `PointerOverUi`.
7. **Кэши инвалидировать Version-счётчиками** (`AircraftOrders.Version`, `GroundOrders.Version`,
   `RouteEditor.DirtyVersion`), а не «по времени» или флагами.
8. **OnGUI вызывается многократно за кадр** — никаких аллокаций/накопления состояния в нём
   (текущие аллокации маршрутов — известный долг №9).
9. **Парность Enter/Leave.** Камера: farClip, порог FloatingOrigin, TrackIR, облака — что изменил
   на входе, восстанови на выходе (включая тихий выход при смене камеры игрой).
   Звук: доплер источников гасится на входе в режим (`CommanderAudio.Suppress`) и
   возвращается в `Deactivate` на любом пути выхода (+ страховка в sceneLoaded).
10. **UI-разделение труда**: `CommanderHud` (IMGUI) — мировой оверлей (иконки/кольца/маршруты/маркеры)
    + notices + debug; `SupcomUi` (uGUI) — экранные панели. Новый мировой маркер → CommanderHud,
    новая панель/кнопка → SupcomUi.
11. **Все балансные числа — в JSON** (`EconomyProfile`) или `Settings`, не в коде (принцип DESIGN §4).
12. **Язык проекта — русский**: комментарии, сообщения UI, логи.
13. **Каждое новое предположение об API игры** — проверка в `SelfCheck` (стартовая или активационная).

---

## 8. UI: два слоя и кто за что отвечает

### CommanderHud (IMGUI, `Ui/CommanderHud.cs`)
Рисует поверх экрана каждый OnGUI-вызов, только когда режим активен: рамка выделения;
стратегические иконки (свои зелёные; чужие — ЛЮБЫЕ, включая здания и экстракторы, —
только по данным разведки `GetTrackingData`: красные под наблюдением, серые тающие,
без контакта скрыты; ромбы аэродромов: свой зелёный, чужой красный только под разведкой,
иначе жёлтый «неизвестно»; ничейные месторождения жёлтые всегда — фича карты; ракеты обеих сторон);
точечные элементы (иконки/ромбы/ракеты/месторождения/HP-полоски/скобки/кольца/маркеры
приказов/вейпоинты) ГАСНУТ под окном миникарты (`UnderMinimapWindow` по маске
`MinimapPanel.HudMaskRect` — IMGUI рисуется поверх uGUI и без маски штампуется на
миникарту; ленты маршрутов и рамки площадок — непрерывная мировая геометрия, не гасим);
кольца дальности выделения (`RangeRings`, v0.72.0, самый нижний слой оверлея — рисуются
ДО иконок: атака/обзор/стройка, объединение пересечений SupCom, плоскость земли круга —
лучи через пиксели, поэтому кольца честно перспективны и видны сквозь сопки, как ленты;
под окно миникарты вырезаются четырьмя кусками UV);
жёлтые пульсирующие маркеры незахваченных месторождений; кольца выделения
(«пояс»: у кораблей/наземки/зданий — плоскость на середине высоты модели
(`GameApi.RingBeltOffset`, кэш смещения — не мировой Y, чтобы не протухал при
сдвиге FloatingOrigin), у авиации — по земле; точки кладутся равномерно по всей
окружности, шаг растёт с радиусом до потолка 80 — фиксированный бюджет 26 точек
обрезал дугу у крупных юнитов);
скобки у крупных юнитов; красные скобки осмотра на инспектируемой чужой цели/ракете
(якорь совпадает с иконкой контакта: протухший — на последней известной позиции); маршруты (ленты DrawChainRibbon: вся цепочка — одна непрерывная
полоса, углы скруглены `Util.RoundChainCorners` (радиус `RouteCornerRadiusM` — нижний
порог, эффективный радиус добирается до ~28px на экране, иначе на стратегическом зуме
скругление невидимо; зажим 45% короткой ноги), адаптивная ширина, экраные квады
расширены на полпикселя от центроида — стыки через разные GUI.matrix не щелятся, шеврон
бежит вдоль всей цепочки; у авиации подряд идущие сегменты одного режима — один прогон,
стык сегментов скруглён; вейпоинты;
групповые участки — ГРУППОВАЯ ТОЧКА = СВОЙСТВО ТОЧКИ (groupId клика, не выделения):
у всех юнитов с этим groupId — один маркер-центроид и общая лента (дедуп сегментов
между юнитами), вход в группу — одна нога от центроида якорей её членов,
индивидуальные точки (groupId=0) — личные ветки схождения/расхождения; то же для
очередей инженеров (запись с groupId — маркер и сегменты один раз) и точек планов
авиации (маркер один на приказ в центроиде группы бортов; служебные точки — взлёт
патруля и одиночный стоп-старт отмены — не рисуются и не сканируются); Shift-режим
видимости рисует все очереди тем же слиянием — без россыпи вейпоинтов); маркеры приказов (кресты, тающие 2.5 с); notices; панель SelfCheck-провалов;
F11-debug (fps, счётчики, origin-монитор).

### SupcomUi (uGUI, `UiRts/SupcomUi.cs`) — раскладка в духе SupCom FA
Canvas (`sortingOrder=30000`, `DontDestroyOnLoad`, пересобирается лениво через `EnsureUi`;
referenceResolution делится на `Settings.UiScale` — глобальный масштаб панелей), обновление
контента раз в 0.2 с, `canvas.enabled = режим активен`:
- **лево-верх** — экономика (деньги/доход/мин, экстракторы);
- **лево-низ** (снизу вверх) — командная карта с подсказкой режима (стандартные команды
  движение/атака-движение/патруль/атака/стоп + индивидуальные: hold-fire всегда, профили
  высоты — только когда в выделении есть авиация); внизу карточки — **полоса гейтов
  вооружения** v0.71.0: кнопка на каждый класс станций выделения (`Catalog.ClassOf`:
  ЯД/Бомбы/Пушки/Рак.ВВ/Рак.АС), клик — тоггл всему выделению (`WeaponGateService.
  ToggleForSelection`), подсветка зелёный/красный/жёлтый + живой префикс подписи
  «+/−/±» (всем разрешён/выключен/вперемешку); секции подписаны заголовками
  «ПРИКАЗЫ» и «РАЗРЕШЁННОЕ ВООРУЖЕНИЕ» (объясняемость зон); строка-подсказка
  действий живёт ТОЛЬКО в активном спец-режиме (стройка/залп/веер/приказ) — в покое
  её нет и «ПРИКАЗЫ» встаёт к верхнему краю (ApplyCommandLayout, v0.71.4); над ней — **панель свойств выделения**
  (v0.71.4: заголовок зоны «ВЫДЕЛЕНИЕ», шапка имя/класс/HP(+топливо у авиации)
  фиксирована, ТЕЛО КАРТОЧКИ ПОТОЧНОЕ — `FlowInfoBody` раздаёт позиции только
  активным непустым строкам (пустые state/приказы группы не оставляют дыр), высота
  панели поджимается под контент; один юнит: HP (подписан «HP …»)/скорость/высота/
  топливо/состояние AI ПО-РУССКИ (`Catalog.RuPilotState`/`RuShipState`, v0.71.1;
  неизвестное состояние — как есть; групповой «боезапас» клэмпится — энергостанции
  дают долю &gt;1)/вооружение/текущий приказ; группа:
  средние HP и боезапас, состав по родам + **сводка вооружения** v0.68 — станции живых
  юнитов слиты по имени оружия (`WeaponTallyAccumulator`, чистая логика в `Catalog`):
  домен-тег АА/АЗ/У/ПР/РЕБ (порог `effectiveness.antiAir/antiSurface/antiMissile > 0`
  как у игры, ядерное — префикс «Я»), заряд/ёмкость группы `6/10`, носители по юнитам,
  «пусто» у расстрелянных, грузовые/десантные станции — мимо; при &gt;8 типов последняя
  строка «+ ещё N типов»; **осмотр** при пустом выделении — `Inspected`:
  чужой юнит — HP/скорость/высота только под живым наблюдением, протухший — имя/класс
  и возраст контакта; ракета/бомба — скорость/высота, двигатель, ГСН, БЧ, «ЦЕЛЬ: …»
  если ГСН держит наш юнит. Все строки панели — однострочные: перенос запрещён
  (`Label singleLine`, горизонтальный Overflow), длинный текст обрезается «…»
  (`Util.FitLine`, заголовок 36/строки 50 символов — иначе перенос наезжает на
  соседние ряды и вылезает за подложку);
- **центр+право-низ** — панель (фикс 146 px, обе строки всегда видны; компакт v0.64.0: плитки 48, ленты 52): колонка ВКЛАДОК
  слева ($ производство, # список, ^ апгрейд, Ш шаблоны), за ней контекст-колонка
  (Пауза/Цикл/Шаблон+), затем стрелки «◀◀ ◀ ▶ ▶▶» и две строки-ленты: верхняя —
  каталог/шаблоны (тиры слева над ней), нижняя — очередь (стройки или фабрики).
  Плитки 48×48 — ЕДИНЫЙ строитель `Tile(TileSpec)` (консолидация v0.48; компакт v0.64.0): иконка
  (авиация — индивидуальный силуэт `mapIcon`, как на макрокамере/миникарте и в нативном
  меню выбора борта; прочее — визор-маркер), короткая подпись юнита внизу (`code`,
  иначе первое слово имени — `Catalog.ShortLabel`), бейдж типа слева-сверху с цветом
  по классу (наземка из `VehicleType`: ТНК/ББМ/АРТ/ЗУ/ПЗРК/ЗРК/РЛС/ГРЗ…; авиация —
  роль из `roleIdentity`: И/У/М, слабый боец — ТР или РЛС-ДРЛО), справа-сверху ×N
  стака либо цена каталога, у головной позиции очереди — прогресс-бар. Клики: каталог —
  ЛКМ в очередь; ОЧЕРЕДЬ ФАБРИКИ — ЛКМ +1 в стак, ПКМ −1 с конца (Shift ×5,
  уплаченное возвращается — `ProductionTasks.InsertAfter/RemoveTail`); очередь стройки —
  ЛКМ камера к площадке; ростер — ЛКМ подвыделение типа, набор зданий — ПКМ убрать.
  **Hover-карточка** (0.35 с, `Settings.UiTooltips`): имя/класс-роль/цена/описание
  определения у курсора — опрос каждой плитки в `UpdateTileHover` каждый кадр
  (`RectangleContainsScreenPoint` по плитке И её ленте-вьюпорту, снесённые плитки
  выпадают по Unity fake-null; uGUI Enter/Exit-события игнорируются — правило №18).
  Вкладка «Ш»: верх — сохранённые шаблоны (персистентный JSON), низ — текущий
  заказ/набор. Панели видимы только при выделении (контекст); панель НЕ растёт —
  строки постоянны. Вкладка выбирается кликом (ручной выбор уважается, пока выделение
  не сменилось) или автоматически: депо/аэродром → Производство, экстрактор → Апгрейд,
  иное выделение → Список. Перестройка кэшируется хэшем (вкладка + состав + деньги
  с шагом 100 + тир), `ForceUpdateCanvases` вызывается только если список прокручен.

### MinimapPanel + UiKit (`UiRts/`)
Плавающая миникарта в окне `FloatingWindow` (заголовок — перетащить, угол — ресайз, «×» —
закрыть; закрытое окно возвращается вкладкой «+ Карта» у левого края; позиция/размер/
видимость сохраняются в конфиг `UI.*`; ресайз за нижний-правый угол: нижний/правый края
идут за мышью, верх/лево неподвижны — пивот окна левый-нижний, рост вниз компенсируется
позицией, иначе вертикаль инвертирована). Окно строится не раньше 0.5 с после появления
канваса и держится ЦЕЛИКОМ на нём (`FloatingWindow.ClampToParent`: драг/ресайз +
самовосстановление 10 Гц) — в кадре создания канвас его rect в физических пикселях,
CanvasScaler применится позже, и посчитанная по нему позиция «торчит уголком» за краем.
Полоска под картой ДВЕ строки (v0.76): верх — слева «Рельеф» (фон карты), справа
«Оружие» (режим колец по классам вооружения, v0.77 — доменные кольца атаки
заменяются классовыми, чипы «Назем»/«Воздух» при этом призрачно приглушены;
v0.79 чип «Заливка» убран вместе с заливкой — кольца всегда линии, как в SupCom);
низ — четыре чипа-переключателя колец дальности
«Назем/Воздух/Разведка/Стройка», каждый на четверть ширины полоски (цвет чипа =
цвет колец — легенда; клик — `Settings.SetRangeRing` + SaveConfig, состояние колец
читает `RangeRings`).
Фон — готовый `MapSettings.MapImage` на `RawImage` (фолбэк — заливка), иконки юнитов с
развед-гейтом как у IMGUI-иконок (обновление 10 Гц): спрайты визор-маркеров
friendly/hostileIcon (свои зелёные, враги красные/серые по давности), авиация с
mapOrient — ориентированный по курсу силуэт mapIcon (миникарта «север вверх», без
поворота в кадр камеры); ракеты — мигающие квадраты, аэродромы/месторождения — ромбы.
**Зум колесом при наведении на карту** («в курсор», `MinimapZoomMax`): окно обзора
хранится в нормализованных координатах карты 0..1 (`_viewMin/_viewSize`), фон показывает
под-прямоугольник через `RawImage.uvRect`, `RectMask2D` на слое иконок режет вылезающее,
блипы вне окна гасятся (без churn — записи живут), ЛКМ/ПКМ через те же координаты;
камера над uGUI не зумится (`PointerOverUi`) — двойного действия нет; при пересборке
окна зум сбрасывается. Иконки/ромбы РАСТУТ с зумом (×1..`IconScaleMax`=4). **Режим
«Рельеф»** (кнопка под картой, `MinimapRelief`): сетка высот 128×128 по `GameApi.GroundY`
сэмплируется лениво порциями (512 рейкастов на тик 10 Гц, ~3 с на карту), из неё —
текстура: море синей шкалой глубины, суша гипсометрической зелёно-буро-белой шкалой,
горизонтали тёмной линией на смене высотного пояса (шаг 1/2/5×10^k от диапазона);
текстура подменяет фон (тот же uvRect-зум), при пересборке окна/новой карте
пересчитывается. Рамка текущего обзора камеры: углы трассируются в землю
(рейкаст), луч выше горизонта ведётся по направлению до карты (`RayToMapEdge`), точки
клампятся в границы карты — рамка обрезается о край миникарты и не замирает при
взгляде вдаль. ЛКМ — прыжок камеры, ПКМ — приказ выделенным
(`MoveOrderService.IssueAtWorld`), Shift — очередь. `UiKit` — общие строители
(окно/скролл/прогресс-бар) — новые панели собирать из них. Родная карта игры
(`DynamicMap`) скрывается на время режима командира (контроллер запоминает и возвращает
состояние) — иначе дублируется с миникартой мода.

### Горячие клавиши (сводка)
F10 — режим · ЛКМ — выделение/рамка/драг вейпоинта (по видимому чужому юниту или ракете —
осмотр в инфопанели) · ПКМ — приказ (Shift — очередь/серия) ·
Ctrl+Shift+ПКМ — удалить вейпоинт (Shift — искать по всем юнитам) ·
Ctrl+K — уничтожить выделенное (свои юниты/здания — игровой урон, обломки; недострой — снос) ·
СКМ — драг карты (тап — центрировать) · колесо — зум · Space (зажать) — осмотр мышью ·
M/A/U/T/V — заряд режима приказа (движение/атака-движение/патруль/атака/залп), R — «Атака
веером» (ПКМ по врагам копит цели, ПКМ по земле — раздать группу по целям), S — стоп,
H — hold-fire, X — сброс ловушек, G — борта на базу (терминальный RTB: посадка/
обслуживание/паркинг до следующего приказа) · Alt+Shift+R — Disperse Move (очередь вейпоинтов
группы распределяется прямыми векторами, FAF-стиль) ·
Ctrl+1..9 / 1..9 (дабл-тап — камера) · Alt+1..4 — профиль высоты · Shift+1..3 — скорость ·
Alt+D — цикл авто-защиты · Home — центр карты · F11 — debug · миникарта: ЛКМ — прыжок камеры,
ПКМ — приказ, колесо при наведении — зум «в курсор».

---

## 9. Конфигурация, сборка, деплой

- **Сборка — конвейер `python tools/build.py`**: полная пересборка с `-warnaserror` →
  линт накопленных классов багов (двойной `Text`; `new GameObject` без `typeof(RectTransform)`
  в UI-коде; застрявшая версия) → тесты чистой логики (`tests/SupremeNuclear.Tests`,
  консольный раннер: серии `Util.Runs`, терцили `Catalog.ApplyTierFilter`, `Progress01`,
  `EconomyProfile.Rate`, JSON-шаблоны, плюрализация `Util.RuPlural`, сводка вооружения
  `WeaponTallyAccumulator`/домен-теги, границы объединения `Util.UnionBand`) → проверка версии (исходник → bin DLL → игровой DLL
  байтами) и идентичности bin/деплой. `--fast` — без чистки obj. Автодеплой `AfterTargets=Build` копирует DLL
  в `<GameDir>\BepInEx\plugins\SupremeNuclear\`. Опции: `-p:GameDir="..."`, `-p:Deploy=false`.
- **Зависимости** — из папки игры: `Assembly-CSharp`, `Mirage`, `Newtonsoft.Json`, BepInEx-ядро,
  модули UnityEngine (см. `.csproj`). NuGet — только reference-сборки net472.
- **Конфиг игрока:** `BepInEx/config/com.supremenuclear.rts.cfg` (генерируется из `Settings.Bind`).
  Добавил настройку — проверь, что Bind-имя осмысленно, дефолт в коде и в Bind совпадают.
- **Профиль экономики:** `BepInEx/plugins/SupremeNuclear/Economy/<Profile>.json`;
  отсутствующий файл создаётся-образец при первом запуске миссии (host).

---

## 10. Рецепты типовых изменений

**Новый тип приказа** (пример: «load on transport» когда-нибудь):
1. `OrderMode` enum + `OrderModes.Label`.
2. Логика выдачи: кейс в `MoveOrderService.IssuePending` (авиация → новый `AirOrderMode` +
   методы `AircraftOrders`; наземка → `GroundOrders`).
3. Хоткей в `CommanderModeController.Update` (блок `M/A/U/T/V/S`) и кнопка в
   `SupcomUi.BuildCommandButtons` — панель контекстная (v0.68.0): кнопки
   перестраиваются по контекст-ключу состава выделения, ряды авиации только при
   авиации (+ `HighlightCommand` в `RefreshCommands`).
4. Отрисовка: цвет в `CommanderHud.ModeColor`, подмешивание в `SubmeshOf` (RouteRibbonMesh).
5. Подсказка в `_modeHint` (`SupcomUi.RefreshCommands`).

**Новый режим полёта (высота/скорость) или режим защиты:** профиль/режим в
`AircraftOrders` (`Profiles`/`SpeedMode`, штамп в `MakeSegment*`, резка ног в `CanAppend`)
или `DefenseService` → хоткей+кнопка → `ApplyModesToCurrent` для мгновенного применения
к головному сегменту выделенных → при новом игровом API — SelfCheck.

**Новая боевая механика с пуском/спавном (залп и т.п.):** host-only статический сервис
(образец — `SalvoService.Tick`: CollectAll → готовность по даталинку → нативный путь
пуска `LookForMissileTargets`→`Fire`), регистрация тика в §3.2 сразу после SortieService.

**Новая настройка:** поле + дефолт в `Settings` → строка в `Bind` (секция/имя/описание по-русски)
→ использование только через `Settings.X`.

**Новый Harmony-патч:** попробуй обойтись без него (§1, §7.3) → guard-Prefix-класс →
строка в реестр §6 → при экзотике — проверка в `SelfCheck`.

**Новая покупка:** `PurchaseService.TryBuyX` (host-guard → деньги → игровой спавн-вызов →
`AddFunds(-цена)` → Notice/Log) → строка-кнопка в `SupcomUi.RefreshContext`.

**Новый класс атакуемой цели:** тип в фильтр `MoveOrderService.IsAttackable` (пик ПКМ),
`AircraftOrders.EnemyNearby` (переключение боевого AI), `GroundOrders.NearestEnemyInWeaponRange`
(stop-and-fire) — три места синхронно.

**Изменение баланса:** только `EconomyProfile` JSON (доход/цены/тиры) или `Settings`
(камера/иконки/формации). Код не трогать.

**Новый мировой маркер/иконка** → `CommanderHud` (OnGUI, рисование примитивами `DrawRect` и др.).
**Новая экранная панель** → `SupcomUi` (Build*, Refresh*, кэш по хэшу как у существующих).

---

## 11. Реализованность vs DESIGN.md

| Шаг плана (DESIGN §4) | Статус | Заметки |
|---|---|---|
| 1. Каркас + камера | ✅ | strategic zoom, СКМ-драг, origin-фикс, edge-scroll |
| 2. Выделение + приказы наземки | ✅ | клик/рамка/дабл-клик/группы; формации; цепочки; drag-редактор |
| 7. Авиация (AircraftWaypoints-модель) | ✅ | сегменты, профили высот AGL, attack-move, патруль, attack, конвертация в патруль ПКМ по вейпоинту |
| 7a. Приказы атаки (DESIGN §2.11) | ✅ v0.62.0 | пиннинг цели (§6.3), конус атаки-движения, подлёт по даталинку/якорю, миссия вылета (RTB → аэродромный сервис → повтор), наземные attack-move stop-and-fire и атака цели подходом |
| 7b. Режимы полёта/авто-защита/залп (DESIGN §2.11a) | ✅ v0.68.0 | высота Alt+1..4 и скорость Shift+1..3 штампом на участок (+`ApplyModesToCurrent`); авто-защита Авто/Ловушки/Выкл в steering-патчах №1/№4; залп V по цели/площади/Shift-мультицели (SalvoService, нативное распределение), авто-RTB сухого борта; контекстная командная панель |
| 7c. Веерная атака + Disperse Move (DESIGN §2.11a) | ✅ v0.70.0 | R «Атака веером»: ПКМ по врагам копит цели (маркеры-кресты с номерами), ПКМ по земле — раздача `Util.AssignBalanced` (ближайшая цель, ёмкость N/M, атакующих меньше целей — ближайшая свободная); Alt+Shift+R Disperse Move (FAF-стиль): очередь групповых вейпоинтов → по одной точке на юнита (группа i mod G), наземка и авиация независимо |
| 3. Экономика v1 | ✅ с оговоркой | один ресурс `factionFunds` вместо массы+энергии; доход тиковый, апгрейд тиров; потоковая семантика (DESIGN 2.2) — не начата |
| 4. Производство | ✅ v1 (v0.67.0 — параллельная оплата; v0.74.x — слоты по типу борта) | депо = фабрика с очередью (ProductionTasks: поток, пауза/цикл, отмена); авиация — очередь аэродрома, платит параллельно по ангарам, умеющим строить тип позиции (пулы вертолётных/самолётных площадок независимы; каталог аэродрома = юнион ангаров, неподдерживаемый тип заказать нельзя) |
| 5. Стройка BuildTask | 🟡 v1 | Task-API v1 (§14): здания из Encyclopedia, инженер=TRUCK, потоковая оплата деньгами, площадка-маркер; фабрики/очереди/ассист — этап B |
| 6. AI-командир | ⬜ | |
| 8. Командирский борт/связь/EMCON | ⬜ | |
| 9. Генератор карт | ⬜ | разметка месторождений сейчас — авторазметка кольцами/JSON |
| 10. Салваж, БЧ, MP-расширения | ⬜ | нативные warhead-гейты отключать будем на шаге БЧ (DESIGN 2.6) |
| Сверх базового плана | ➕ | стойки hold-fire, меш-ленты (эксперим.), SelfCheck-панель, гашение доплера в режиме командира |

---

## 12. Реестр известных долгов

Отсюда берём работу; сюда же записываем новые (с датой). Закрытые — помечаем ✅ с датой
и версией, строку оставляем для истории.

| # | Долг | Где | Суть | Статус |
|---|---|---|---|---|
| 1 | Панель апгрейда устаревает | `SupcomUi.RefreshContext` | ✅ закрыт 2026-10-26 (v0.31.1): хэш панели включает тир экстрактора, флаг Running и floor(factionFunds) — после апгрейда/трат кнопки перестраиваются, при Running=false панель экстра cleaned | ✅ |
| 2 | Мёртвые в выделении | `SelectionService`, `MoveOrderService.CancelAll`, `CommanderModeController.Update` | ✅ закрыт 2026-10-26 (v0.31.1): prune null/disabled в `SelectionService.Tick`, null-guard в `CancelAll`, `Update` обёрнут в троттленный try/catch (без самоотключения) | ✅ |
| 3 | Лог-спам экономики | `EconomyService.TryStart` | ✅ закрыт 2026-10-26 (v0.31.1): «ждём сцену» — один Info, дальше молчаливый ретрай; «не из чего спавнить» — один Error и `_gaveUp` до смены сцены; `EconomyEnabled` проверяется в Tick | ✅ |
| 4 | Статическое состояние и сцены | `SupremeNuclearPlugin.sceneLoaded` | ✅ закрыт 2026-10-26 (v0.31.1): Reset() у AircraftOrders/GroundOrders/StanceService, ResetGroups() у SelectionService, RouteRibbonMesh.Destroy(), Pending=None | ✅ |
| 5 | `RouteRibbonMesh` и конфиг | `RouteRibbonMesh.AppendSamples` | ✅ закрыт 2026-10-26 (v0.31.1): шаг берётся из `Settings.RouteSampleStepM` | ✅ |
| 6 | Мёртвый код | CommanderHud, RouteRibbonMesh | ✅ закрыт 2026-10-26 (v0.31.1): удалены DotTexture/_dot, AppendTerrainSamples, DrawDotLine, пустая `int y`; `RouteRibbonMesh.Destroy()` подключён к sceneLoaded | ✅ |
| 7 | `EnemyNearby` O(N) | `AircraftOrders.EnemyNearby` | на каждый FixedUpdate каждого борта в attack-move; расти будет квадратично — нужна сетка/кэш контактов | открыт |
| 8 | OnGUI-аллокации | `CommanderHud.DrawAircraftRoutes` | списки на юнита на каждое OnGUI-событие (2+/кадр) — GC-давление | открыт |
| 9 | Таймер пересборки меша | `RouteRibbonMesh.Tick` | пересборка каждые 0.5 с всегда, даже без изменений (Version есть, но `\|\|` с таймером побеждает); на длинных картах — статтеры; меш по умолчанию выключен | открыт |
| 10 | README обновлён | — | ✅ закрыт 2026-10-26 (документирование) | ✅ |
| 11 | Тултипы UI | UiKit/панели | ✅ частично закрыт 2026-10-26 (v0.60.0): hover-карточка определения юнита на плитках каталога/очередей/ростера (`SupcomUi.UpdateTileHover`, опрос в Update — uGUI-события не нужны); общий тултип-слой UiKit для прочих элементов (кнопки/панели) — по-прежнему отложено до реального потребителя | ✅ (частично) |
| 12 | Сериализация задач | `Tasks/BuildTasks.cs` | записи data-shaped, но записи в сейвы/MP-синхронизация не подключены (runtime-ссылки FactionHQ/Unit); сделать на этапе сейвов | открыт |
| 13 | Инженер = TRUCK | `GameApi.IsEngineer` | временная роль грузовика до модовых определений юнитов; нет ассиста/ремонта/требований рельефа площадки | открыт (осознанно) |
| 14 | Меш-ленты без слияния групп | `RouteRibbonMesh.CollectGround/CollectAir` | эксперим. меш-рендер (`RouteUseMesh=false`) рисует по ленте на юнита: групповые маршруты дают N параллельных лент формации; IMGUI-рендер слияет через `Util.GroupPrefixCentroids` (v0.59.0) — при включении меша перенести ту же логику | открыт (меш выключен) |
| 15 | Приказ «Атака» не назначает цель | `AircraftTargetPinPatch`, `AircraftSteeringPatch` | ✅ закрыт 2026-10-26 (v0.62.0): пиннинг цели Postfix `AssessHQTarget` (§6.3) с подбором станции по `AnalyzeTarget` и даталинк-гейтом; подлёт к цели/якорю до входа в зону боя | ✅ |
| 16 | Дефекты пика цели ПКМ | `MoveOrderService.TryPickEnemyAt` | ✅ закрыт 2026-10-26 (v0.62.0): `Building` в фильтре, единый радиус 16px, пик врага РАНЬШЕ конвертации вейпоинтов/недостроев, Shift — очередь атак (`EnqueueAttack`), наземка получает приказ атаки цели (`GroundOrders.SetAttack`), Attack/Rtb не конвертятся в патруль | ✅ |
| 17 | AI-борта не перевооружается/не заправляется | `SortieService` (интерим) | ✅ интерим закрыт 2026-10-26 (v0.62.0): аэродромный сервис на земле (`RpcRearm` + `Refuel`, время/цена в Settings) в цикле миссии вылета. Остаётся ОТКРЫТОЙ экономика логистики (мини-игра командующего: доставка, электростанции, экстракторы) — DESIGN §3-7 | интерим ✅ / экономика открыта |
| 18 | Сканы врагов O(N) на юнита | `GroundOrders.NearestEnemyInWeaponRange`, `AircraftOrders.EnemyNearby` | наземные сканы — по `BattlefieldGrid.GetUnitsInRangeEnumerable` (нативная сетка), но троттл 0.5 с/юнит и скан в `EnemyNearby` на каждый FixedUpdate борта в attack-move; рост квадратичный — тот же класс долга, что №7 (кэш контактов) | открыт (2026-10-26) |
| 19 | Disperse Move не охватывает инженеров | `MoveOrderService.IssueDisperseMove` | очереди движений инженеров живут в `BuildTasks.Sequences` (общая очередь стройки) — распределение на них не действует; завести при появлении самостоятельных маршрутных очередей инженеров | открыт (2026-10-27, осознанно) |
| 20 | Веер не комбинируется с залпом (V) | `MoveOrderService.IssueFanAttack` | раздача веера всегда выдаёт Attack-приказы (авиация — атака-вылет); залповые борта в смешанной группе идут обычной атакой — комбинировать при реальной потребности | открыт (2026-10-27, осознанно) |
| 21 | Бомбовый залп | `SalvoService` | залп пускает только ракетные станции (`WeaponInfo.missile`): бомбам нужен расчёт точки сброса (нативный бомбовый заход) — бомбовозы в режиме «Залп» деградируют в атаку-движение; сделать «залповый бомбосброс» по образцу нативного `LookForBombingTargets` | открыт (2026-10-26, v0.68.0) |
| 22 | Скоростные режимы вертолётов | `HeloSteeringPatch` | игра не даёт моду тротл на вертолётах (hover-контур сам считает газ); вертолётам доступны высоты, скорости — нет (зафиксировано DESIGN §2.11a); пересмотреть при появлении API скорости вертолёта | открыт осознанно (2026-10-26, v0.68.0) |
| 23 | Whitelist станций залпа не синхронизирован | `SalvoService.Stations` | выбор станций (чипы панели) — мод-local состояние хоста-командира; в MP клиент-командир не заложен (командирский режим сейчас хост-only), при появлении клиента — синхронизировать приказом | открыт (2026-10-26, v0.68.0) |
| 24 | Гейты вооружения не синхронизированы | `WeaponGateService.Blocked` | маска выключенных классов — мод-local состояние хоста; тот же класс долга, что №23; enforcement хост-side: ручные пуски асов-людей идут мимо гейта (CmdLaunchMissile → LaunchMount без проверки Ready) | открыт (2026-10-27, v0.71.0; DESIGN §3-11) |
| 25 | Рантайм-тогглы миникарты не персистятся | `Settings`/`MinimapPanel` | UI пишет статические копии (`Settings.MinimapRelief/X/Y/Size/Visible = …`) и зовёт `SaveConfig()` — но `ConfigFile.Save` пишет Value ЗАПИСЕЙ, которые никто не обновляет: тогглы/геометрия окна живут до перезапуска. Паттерн-эталон уже есть: `Settings.SetRangeRing` (удержанные записи `RangeRings.*`); перенести на него ключи `UI.*` | открыт (2026-10-27, обнаружен при v0.72.0) |
| 26 | ~~Кольца атаки — один максимум на домен~~ ЗАКРЫТ | `RangeRings.CollectCircles` | v0.76 развёл по доменам (АС/АА); v0.77 — режим «Оружие» (RangeRings.Weapons): кольца по классам вооружения (Пушки/Рак.ВВ/Рак.АС/Бомбы+ЯЗ) с учётом гейтов — рабочий сценарий пользователя «выключил AGM → вылет из-за холма на пулемётах → включил AGM → снёс стационарные» | закрыт (2026-10-27, v0.77) |

---

## 13. Справочные материалы

- `decompiled/` — декомпилированный `Assembly-CSharp` игры: единственный источник правды об API
  из §5. Перед использованием незнакомого вызова — свериться здесь.
- `GroundControlRts/` — бета Ground Control 0.9.9.4: паттерны (депо-покупки, supply, патчи),
  **код не копировать** (лицензия, DESIGN §6).
- `DESIGN.md` — гейм-дизайн и план (v0.4): что и в каком порядке строим дальше.
- Памятка по происхождению решений — комментарии в коде («GC патчил этот же метод» и т.п.).

## 14. Task-API — слой задач (веха стройки, v1 от 2026-10-26)

Ключевое архитектурное решение DESIGN §2.3: **вся стройка/производство — сериализуемые задачи**,
общие для UI игрока, AI-командира, мультиплеера и сейвов. Приказы (`AircraftOrders`/`GroundOrders`)
остаются runtime-рулением юнитов; задачи — отдельный слой НАД ними.

### Модель (v1 — постройка зданий)
- `BuildTasks.Task` — data-shaped запись: `Id` (GUID), `Definition` (jsonKey-совместимо), `Site`
  (`GlobalPosition`), `Cost/Paid` (деньги), `Builders` (runtime-ссылки до этапа сейвов — долг §12-12).
- Хранилище `Active` — host-only; `Version` инвалидирует кэши отрисовки (инвариант №7).
- Потоковая оплата (семантика SupCom): пока ≥1 инженер в радиусе `BuildSiteRadiusM` — деньги
  утекают со скоростью `Cost / BuildSecondsPerBuilder × N`; нет денег/инженеров — задача стоит.
  Это ядро будущей потоковой экономики (mass/energy лягут в тот же механизм drain).
- Завершение: `Spawner.SpawnFromUnitDefinitionInEditor` с фракцией задачи `t.Faction`
  (v0.59.1: null-HQ делал здание нейтральным — не выделялось, и техника из такого
  депо наследовала чужую фракцию); отмена — возврат уплаченного.
- Инженер v1: `GameApi.IsEngineer` = TRUCK (долг §12-13: модовые определения позже).

### Ввод/UI (этап A+B-срез)
- Режим установки: клик позиции в каталоге «Производство» (виден при инженере в выделении) →
  `BuildTasks.Pending` → призрак под курсором (CommanderHud) → ЛКМ по земле = площадка
  (клик `Consumed` как у RouteEditor — выделение пропускает кадр), ПКМ/Esc — отмена.
- Маркер площадки: рамка + заливка прогресса + имя/процент (CommanderHud.DrawBuildMarkers).
- Инженеры автоматически едут к площадке (`GroundOrders.Set`).

### Реализованные надстройки (v0.43–v0.58)
- **ЕДИНАЯ ОЧЕРЕДЬ инженера по порядку кликов** (`Sequences`: записи Move/Task вперемешку,
  аванцер выдаёт точки; групповые движения — общий groupId: драг/удаление на всю группу).
  При появлении первой записи (постановка `Place`/достройка `TryResume`) хвост обычной
  цепочки `GroundOrders` переезжает в очередь ЦЕЛИКОМ (с groupId точек), цепочке остаётся
  одна текущая точка (v0.80.1: хвост в двух хранилищах давал задвоение маркеров и
  повторный проезд маршрута после исчерпания цепочки).
- **Недострой**: площадка бессрочна (снос — Ctrl+K/кнопка/тело Ctrl+Shift); возврата денег
  нет; достройка — ПКМ по телу призрака/маркеру выделенными инженерами.
- **Кольцевой подход** инженеров (точка со своей стороны ~0.65 радиуса), стоп-затем-строй
  (оплата при скорости ≤ 3 м/с), раздвиг площадок ≥ 60 м, зона застройки (блокеры — выталкивание).
- **Шаблоны** (вкладка «Ш»): персистентные JSON; наборы зданий с ОТНОСИТЕЛЬНЫМИ позициями;
  инженером — призраки набора (занятое красным, пропускается), фабрикой — набор в конец очереди.
- **Ctrl+K** — уничтожение выделенного (юниты/здания — игровой урон с обломками;
  аэродром — убийство его зданий).
- **3D-призраки** (модель GC): DrawMesh-силуэты (Built-in шейдер), тинты
  стройка/валидно/занято; драг за тело (экран-прямоугольник от размеров определения);
  начатые — выделить/снести, не переместить.
- **Драг вейпоинтов/площадок** (v0.58.5): перемещение начинается только после порога
  6 px (ClickPx) — клик без движения = выделение (недострой) и ничего не ездит;
  промах луча по рельефу (курсор над небом) кадр пропускает — телепорта точки в
  мировой ноль больше нет; площадки при драге НЕ снапятся к юнитам (юнит — блокер
  зоны, не цель снапа). Захват разрешён и начатым площадкам (Paid>0) — перемещение
  блокирует guard в драг-ветке.
- **Общие правила управления вейпоинтами** (v0.61.2): вейпоинт управляется там, где
  рисуется — авиаточки сканятся на земле (y=GroundY), не с AGL-высотой (раньше при
  наклонной камере захват патруля промахивался на сотни пикселей); групповые
  операции едины для всех типов приказов — по groupId клика (наземка: синхронные
  индексы, инженеры: groupId записей, авиация: groupId точек плана — у каждого
  борта свои сегмент/индекс, драг — общая дельта, удаление Ctrl+Shift+ПКМ — у всей
  выделенной группы). Заодно устранён дубль выдачи авиации (IssueForTarget выдавал
  точку ДО формационной решётки IssueFormationOrders, причём Transit вместо
  AttackMove) — теперь авиация получает одну точку клика с правильным режимом.
- **Групповая точка — свойство ТОЧКИ, не выделения** (v0.62.0): groupId есть у точек
  всех трёх хранилищ (GroundOrders.PointGroups, BuildTasks.SeqEntry.GroupId,
  AircraftOrders.PointGroups) из ОДНОГО счётчика. Драг/удаление групповой точки
  действуют на ВСЕХ членов группы приказа (GroundOrders.CollectGroup /
  BuildTasks.UnitsWithSequences / AircraftOrders.CollectGroup) — корректировать
  можно, выделив любого члена группы или вообще не выделяя (Shift-режим).
  Индивидуальные точки (groupId=0) — только у владельца. Рендер (CommanderHud)
  слияет независимо от выделения: групповая точка — один маркер-центроид, сегменты
  между соседними групповыми точками — дедуп между юнитами (магистраль рисует
  первый), вход в группу — одна нога от центроида якорей членов, индивидуальные
  точки — личные ветки схождения/расхождения. Настройка GroupWaypointSpreadM
  упразднена (слияние по groupId, не по разбросу формации).
- **Параллельная оплата аэродрома** (v0.67.0, доработано v0.74.x — слоты по типу
  борта): слотов оплаты позиции = живые ангары, УМЕЮЩИЕ строить её тип
  (`PaySlots` перебирает `Airbase.hangars` и `Hangar.GetAvailableAircraft`;
  вертолётные и самолётные площадки — независимые пулы). Раньше аэродром платил
  только головную позицию — 5 ангаров не ускоряли очередь; теперь до K позиций
  наливаются одновременно (суммарный дрейн = min(очередь типа, K) × rate), выпуск
  по-прежнему нативный `TrySpawnAircraft` в первый свободный ангар по приоритету.
  Ключ слота = (аэродром, тип борта) — очереди разных типов наливаются параллельно;
  депо — ключ без типа, строго один поток. Слот гейтит только оплату: выпуск
  оплаченного не гейтится (ангары могли умереть после оплаты). v0.74.x заодно:
  `EnqueueAir` отклоняет тип, который аэродром не строит (`CanBuild`, Notice),
  а каталог вкладки «Производство» аэродрома = нативный юнион `GetAvailableAircraft`
  (раньше показывал ВСЮ авиацию Encyclopedia — заказ нестроимого типа вешал
  оплаченную позицию вечно). Самопроверка: SelfCheck(startup) —
  `hangars`/`attachedUnit`/`GetAvailableAircraft`.
- **Достройка ангаров к аэродрому** (v0.78.0, переработано v0.80.0 по плейтесту — этап 2
  «мультифабрика»): 4 здания-ангара в `Encyclopedia.buildings` (SelfCheck-лог печатает их
  ИМЕНА) строятся инженером как обычные здания. Гейт постановки один (`BuildTasks.Place`):
  свой аэродром в радиусе `HangarAttachRadiusM` (600 м, `NearestOwnAirbase` по
  `FactionRegistry.airbaseLookup`/`CurrentHQ`; RunwayQuery НЕ используется — пустой запрос
  игра считает неподходящим). Бетон-гейт v0.78.0 УБРАН (пустынные аэропорты без апрона) —
  вместо него по завершении стройки мод ДОРИСОВЫВАЕТ РУЛЁЖКУ: дорога «выход ангара →
  ближний узел такси-сети» (`ConnectTaxiway`: `Road.AddPoint` + `RegenerateNetwork`,
  концы в радиусе 10 м склеиваются в общий граф), а при пустой сети — к ближнему торцу
  ВПП; физику грунта (мягкие колёса у тяжёлых) игра считает сама. Спавн — нативный
  `Spawner.SpawnBuilding(prefab, pos, rot, HQ, airbase, ...)` (SetAirbase-синк +
  AddHangar-слот, `PaySlots` подхватывает сам); аэродром, потерянный за стройку, — возврат.
  ПОВОРОТ площадки (v0.80.0, все здания): ЛКМ фиксирует точку → удержание крути выход
  курсором (`PlacingActive/PlacingSite/PendingYawDeg` → `Task.YawDeg` → спавн с поворотом);
  ангару по умолчанию — выходом к аэродрому; весь жест гасит выделение (Consumed каждый
  кадр — рамка не стартует). ПРИЗРАК: стрелка ВЫХОДА ангара (жёлтая, от `Hangar.
  GetSpawnTransform()` префаба, без кульинга) на режиме установки и на площадках; призрак
  ангара краснеет и вне радиуса своего аэродрома. Плитки ангаров — бейдж «АНГ».
  Известное ограничение: рулёжка дорисовывается только на хосте (клиент сеть не синкает;
  AI-руление — серверное). Этап 3 — свои аэродромы: `Mission.SpawnCustomAirbase`
  (полоса из размещения, рулёжка генерируется нами) + гриды VTOL-площадок.

### Что добавлять сюда дальше (этап B+)
- `ProductionTask` (фабрики/очереди, DESIGN 2.3a) — РЕАЛИЗОВАН v0.36.0 в виде
  `ProductionTasks` (депо-фабрики): очередь = список позиций, потоковая оплата головной
  позиции, пауза/цикл (кнопки нижней панели 2.10), отмена кликом с возвратом,
  выпуск нативным `TrySpawnVehicle`; нет денег — очередь стоит (SupCom).
- Ассист (несколько инженеров на задачу — уже работает через `Builders`, добавить UI-назначение).
- Тиры фабрик, шаблоны, «цикл» — по §2.10.
- Сериализация в сейвы + MP-репликация (Mirage-сообщения) — когда появятся сейвы.
- AI-командир потребляет ТОТ ЖЕ API (`Place/Cancel/Tick`) — поэтому API публичный и data-shaped.
