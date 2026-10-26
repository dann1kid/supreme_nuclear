# Архитектура Supreme Nuclear (as-built)

**Версия кода:** 0.34.0 · **Дата среза:** 2026-10-26 · **Соответствует:** `src/SupremeNuclear/` целиком.

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
| `Core/GameApi.cs` | `GameApi` | Тонкие обёртки игрового API: границы карты, лучи в землю, `GroundY`, HQ, размеры юнитов, монитор сдвигов origin; кэш `MapSettings` сбрасывается по сцене | static |
| `Core/SelfCheck.cs` | `SelfCheck` | Проверки предположений об API игры (стартовые + активационные); провалы показываются панелью в HUD | static |
| `Input/RtsInput.cs` | `RtsInput` | Весь пользовательский ввод: `Input.*` + защита от чата (`Typing`) + `PointerOverUi`; НЕ Rewired | static |
| `CameraRts/CommanderCameraState.cs` | `CommanderCameraState` | Стратегическая камера: `CameraBaseState` без патчей; СКМ-драг, зум-к-курсору, strategic zoom, edge-scroll; фокус хранится в `GlobalPosition` | класс (тикается игрой) |
| `CommanderModeController.cs` | `CommanderModeController` | Вкл/выкл режима командира (F10) и **единственный** тик-оркестратор (см. §3) | MonoBehaviour |
| `Selection/SelectionService.cs` | `SelectionService` | Клик/рамка/двойной клик по типу, клик по аэродрому, группы управления 1–9 | instance (внутри контроллера) + static `Groups` |
| `Orders/OrderModes.cs` | `OrderModes` | Текущий «заряженный» приказ `Pending` + подписи | static |
| `Orders/MoveOrderService.cs` | `MoveOrderService` | Точка выдачи всех приказов по ПКМ: пики врага, снап, формации, маркеры, отмена | instance (внутри контроллера) |
| `Orders/GroundOrders.cs` | `GroundOrders` | Цепочки вейпоинтов наземки/флота поверх `UnitCommand.SetDestination`; выдаёт следующую точку при прибытии (90 м) | static |
| `Orders/RouteEditor.cs` | `RouteEditor` | Drag-редактирование вейпоинтов (ЛКМ по точке), снап постановки к юнитам | static |
| `Orders/RouteRibbonMesh.cs` | `RouteRibbonMesh` | Меш-ленты маршрутов через `Graphics.DrawMesh` (эксперимент, по умолчанию ВЫКЛ — `RouteUseMesh=false`) | static |
| `Orders/WaypointScanner.cs` | `WaypointScanner` | ЕДИНЫЙ скан вейпоинтов под курсором (захват драга + удаление; приоритет: площадки → движения очередей → цепочки → планы) | static |
| `Core/Util.cs` | `Util` | Чистые функции (серии `Runs` для стаков плиток) — тестируются | static |
| `UiRts/Catalog.cs` | `Catalog` | Тир-фильтр каталога (терцили по цене) — тестируется | static |
| `Aviation/AircraftOrders.cs` | `AircraftOrders` | Планы полётов: сегменты (Transit/AttackMove/Patrol/Attack), профили высот (AGL + огибание), продвижение по маршруту | static |
| `Aviation/AircraftSteeringPatch.cs` | `AircraftSteeringPatch` | Harmony-Prefix руления борта с приказом (§6.1) | Harmony |
| `Economy/EconomyService.cs` | `EconomyService` | Экономика v1 (host-only): спавн экстракторов на месторождениях, нативный захват, тиковый доход, апгрейд тиров | static |
| `Economy/EconomyProfile.cs` | `EconomyProfile` | Баланс экономики из JSON + автосоздание образца | данные |
| `Units/PurchaseService.cs` | `PurchaseService` | Покупки за `factionFunds`: техника через депо, авиация через аэродром (host-only) | static |
| `Units/StanceService.cs` | `StanceService` | Стойка «не открывать огонь» (hold-fire) | static |
| `Units/TurretHoldFirePatch.cs` | `TurretHoldFirePatch` | Harmony-Prefix hold-fire (§6.2) | Harmony |
| `Tasks/BuildTasks.cs` | `BuildTasks` | Task-API v1: задачи постройки зданий (хост) — потоковая оплата, инженеры, спавн по готовности; режим установки `Pending` (§14) | static |
| `Tasks/ProductionTasks.cs` | `ProductionTasks` | Очереди производства «фабрик» (депо+аэродром): поток, пауза/цикл, ралли | static |
| `Tasks/BuildGhosts.cs` | `BuildGhosts` | 3D-призраки площадок ПО МОДЕЛИ GC: меши префаба через `Graphics.DrawMesh` (без GameObject) + встроенный `Hidden/Internal-Colored` (полупрозрачный силуэт); тинты стройка/валидно/занято; подача из `Render()` в Update | static |
| `Core/OrderTemplates.cs` | `OrderTemplates` | Персистентные шаблоны заказов (JSON в plugins/SupremeNuclear/Templates.json): снимок очереди фабрики → повторная постановка | static |
| `Ui/CommanderHud.cs` | `CommanderHud` | IMGUI-оверлей: иконки, кольца/скобки выделения, HP-полоски повреждённых, маршруты, маркеры приказов, notices, SelfCheck-панель, F11-debug | MonoBehaviour (OnGUI) |
| `UiRts/SupcomUi.cs` | `SupcomUi` | uGUI-панели (SupCom-раскладка): экономика (лево-верх), свойства выделения + командная карта (лево-низ), панель-табы Производство/Список/Апгрейд (центр+право-низ) | MonoBehaviour (uGUI) |
| `UiRts/UiKit.cs` | `UiKit`, `FloatingWindow` | Строительные блоки uGUI: плавающее окно (drag/ресайз/закрытие/вкладка восстановления), скролл-список, прогресс-бар | static + MonoBehaviour |
| `UiRts/MinimapPanel.cs` | `MinimapPanel` | Плавающая миникарта: фон `MapSettings.MapImage`, блипы юнитов с развед-гейтом (10 Гц), ромбы аэродромов/месторождений, рамка обзора камеры; ЛКМ — прыжок, ПКМ — приказ | MonoBehaviour (uGUI) |

---

## 3. Поток исполнения — кто когда тикает

### 3.1 Точка входа

`SupremeNuclearPlugin.Awake` (один раз при загрузке):
`Log.Init` → `Settings.Bind` → `ModEnabled`? → `SelfCheck.RunStartup` → `_harmony.PatchAll()` →
подписка на `sceneLoaded` → `AddComponent` × 3 (контроллер, CommanderHud, SupcomUi).

`sceneLoaded` (каждая смена сцены) — полный сброс статического состояния:
`GameApi.ResetSceneCache()`, `EconomyService.Reset()`, `AircraftOrders.Reset()`,
`GroundOrders.Reset()`, `StanceService.Reset()`, `SelectionService.ResetGroups()`,
`RouteRibbonMesh.Destroy()`, `OrderModes.Pending = None`. Новое статическое состояние
добавляй сюда же (инвариант №11).

### 3.2 `CommanderModeController.Update` — единственный оркестратор (порядок важен!)

```
если НЕ InMission            → Deactivate(silent) и return
TickOriginShiftMonitor()     — всегда в миссии (диагностика F11)
EconomyService.Tick()        — всегда в миссии (host: старт/доход)
RouteRibbonMesh.Tick()+Render() — всегда в миссии
F10                          → Activate()/Deactivate()
если НЕ IsActive             → return
камера сменилась не нами      → Deactivate(silent) и return   (вселенение в кабину и т.п.)
RouteEditor.Tick(this)       ← СТРОГО ДО Selection: Consumed гасит выделение на кадр драга
BuildGhosts.Render()         — подача силуэтов площадок ДО рендера (DrawMesh из OnGUI мерцает!)
режим установки здания (BuildTasks.NewFrame/Pending: ЛКМ — площадка, ПКМ/Esc — отмена;
                              без Shift — новая очередь, с Shift — серия)
режим призраков шаблона (OrderTemplates.PendingTemplate: ЛКМ строит набор, ПКМ/Esc)
Selection.Tick()             — ЛКМ: клик/рамка (пропускает кадр при Consumed)
MoveOrders.Tick()            — ПКМ: ВЫДАЧА ПРИКАЗОВ (только по событию!)
GroundOrders.Tick()          — продвижение цепочек наземки; GroundOrders.Prune()
AircraftOrders.Prune(); StanceService.Prune()
TickControlGroups()          — Ctrl+1..9 назначить, 1..9 вызвать, дабл-тап = камера
Alt+1/2/3                    — профиль высоты авиации
H                            — hold-fire выделенных
M/A/U/T/S/Esc (при выделении) — заряд режима приказа / стоп
```

### 3.3 Остальные пути исполнения (вне Update контроллера)

| Что | Кто вызывает | Частота |
|---|---|---|
| `CommanderCameraState.UpdateState/FixedUpdateState` | игра, `CameraStateManager` (LateUpdate) | каждый кадр в режиме командира |
| `AircraftSteeringPatch.Prefix` | игра, `AIPilotCombatModes.FixedUpdateState` | физический тик (~50 Гц) на каждый AI-борт |
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
| `SelectionService.Groups` | **static** | AssignGroup | RecallGroup (чистит null/disabled) | `ResetGroups()` по sceneLoaded |
| `OrderModes.Pending` | static | хоткеи, кнопки UI, MoveOrderService | MoveOrderService, SupcomUi | сброс при пустом выделении и по sceneLoaded |
| `AircraftOrders.Plans` | **static** | Set/Enqueue/SetPatrol/Attack/Cancel/ConvertToPatrol/SetPoint/Forget | Step, TryGetDestination, TryGetPlan, CollectAll | `Prune()` (disabled); `Reset()` по sceneLoaded |
| `AircraftOrders.ProfileIndex` | static | Alt+1/2/3, кнопки UI | MakeSegment/Cancel | — (осознанно живёт: предпочтение игрока) |
| `AircraftOrders.Version`, `GroundOrders.Version`, `RouteEditor.DirtyVersion` | static, монотонные | любое изменение планов/цепочек/точек | RouteRibbonMesh, кэши кривых | никогда (только рост) |
| `GroundOrders.Queues`/`Indices` | **static** | Set/Enqueue/SetQueuePoint/Clear | Tick, TryGetQueue, CollectAll | Prune/Clear (disabled); `Reset()` по sceneLoaded |
| `StanceService.HoldFire` | **static** | Toggle | TurretHoldFirePatch, CountHolding | Prune (disabled); `Reset()` по sceneLoaded |
| `MoveOrderService.Markers` | instance | IssuePending | CommanderHud.DrawOrderMarkers | PruneMarkers (2.5 с) |
| `MoveOrderService.LastDestinations` | instance | IssueFormationOrders, CancelAll | задел на будущее | PruneMarkers (disabled) |
| `EconomyService.Extractors`/`_profile` | static | TryStart, TryUpgrade, PayIncome | TierOf/Income*/UI | `Reset()` по sceneLoaded ✔ |
| `RouteRibbonMesh._meshes/_mats` | static | Rebuild/EnsureMaterials | Render | `Destroy()` по sceneLoaded (донор умершей сцены недействителен) |
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
  followTerrain, altitudeHold, targetVelocity)` — игровой автопилот рулит, мы только даём точку.
- `AIPilotCombatModes` (reflection-поля): `PilotBaseState.destination`, `.aircraft`, `.controlInputs`,
  `AIPilotCombatModes.timeWithoutTarget` — сброс таймера «без цели», иначе AI уйдёт на возврат.

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
| 1 | `AIPilotCombatModes.FixedUpdateState` | Prefix | `Aviation/AircraftSteeringPatch.cs` | у борта есть план (`AircraftOrders.TryGetDestination`) и режим не Attack / не AttackMove-с-врагом-рядом. Пишет `destination`, сбрасывает `timeWithoutTarget`, gas 100%, зовёт `AutoAim` | без плана — `true`, штатный AI |
| 2 | `Turret.AssessTargetPriority` | Prefix | `Units/TurretHoldFirePatch.cs` | юнит турели в стойке hold-fire (`StanceService.IsHoldFire`) | иначе `true` |

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
жёлтые пульсирующие маркеры незахваченных месторождений; кольца выделения на земле;
скобки у крупных юнитов; маршруты (ленты DrawLegQuad с адаптивной шириной, вейпоинты,
шевроны); маркеры приказов (кресты, тающие 2.5 с); notices; панель SelfCheck-провалов;
F11-debug (fps, счётчики, origin-монитор).

### SupcomUi (uGUI, `UiRts/SupcomUi.cs`) — раскладка в духе SupCom FA
Canvas (`sortingOrder=30000`, `DontDestroyOnLoad`, пересобирается лениво через `EnsureUi`;
referenceResolution делится на `Settings.UiScale` — глобальный масштаб панелей), обновление
контента раз в 0.2 с, `canvas.enabled = режим активен`:
- **лево-верх** — экономика (деньги/доход/мин, экстракторы);
- **лево-низ** (снизу вверх) — командная карта с подсказкой режима (стандартные команды
  движение/атака-движение/патруль/атака/стоп + индивидуальные: hold-fire всегда, профили
  высоты — только когда в выделении есть авиация) и над ней **панель свойств выделения**
  (один юнит: HP/скорость/высота/топливо/состояние AI/вооружение/текущий приказ; группа:
  средние HP и боезапас, состав по родам);
- **центр+право-низ** — панель (фикс 196 px, обе строки всегда видны): колонка ВКЛАДОК
  слева ($ производство, # список, ^ апгрейд, Ш шаблоны), за ней контекст-колонка
  (Пауза/Цикл/Шаблон+), затем стрелки «◀◀ ◀ ▶ ▶▶» и две строки-ленты: верхняя —
  каталог/шаблоны (иконки без подписей, тиры слева над ней), нижняя — очередь (стройки
  или фабрики; стак ×N, у головной прогресс-бар; клик — камера/отмена). Вкладка «Ш»:
  верх — сохранённые шаблоны (персистентный JSON), низ — текущий заказ/набор.
  Панели видимы только при выделении (контекст); панель НЕ растёт — строки постоянны. Вкладка
  выбирается кликом (ручной выбор уважается, пока выделение не сменилось) или автоматически:
  депо/аэродром → Производство, экстрактор → Апгрейд, иное выделение → Список. Группы —
  боковыми заголовками (колонка заголовка слева, позиции справа — экономит высоту).
  Перестройка кэшируется хэшем (вкладка + состав + деньги с шагом 100 + тир),
  `ForceUpdateCanvases` вызывается только если список прокручен.

### MinimapPanel + UiKit (`UiRts/`)
Плавающая миникарта в окне `FloatingWindow` (заголовок — перетащить, угол — ресайз, «×» —
закрыть; закрытое окно возвращается вкладкой «+ Карта» у левого края; позиция/размер/
видимость сохраняются в конфиг `UI.*`). Фон — готовый `MapSettings.MapImage` (фолбэк —
заливка), блипы юнитов с развед-гейтом как у IMGUI-иконок (обновление 10 Гц, свои зелёные,
враги красные/серые по давности, ракеты мигают), ромбы аэродромов/месторождений, рамка
текущего обзора камеры. ЛКМ — прыжок камеры, ПКМ — приказ выделенным
(`MoveOrderService.IssueAtWorld`), Shift — очередь. `UiKit` — общие строители
(окно/скролл/прогресс-бар) — новые панели собирать из них. Родная карта игры
(`DynamicMap`) скрывается на время режима командира (контроллер запоминает и возвращает
состояние) — иначе дублируется с миникартой мода.

### Горячие клавиши (сводка)
F10 — режим · ЛКМ — выделение/рамка/драг вейпоинта · ПКМ — приказ (Shift — очередь/серия) ·
Ctrl+K — уничтожить выделенное (свои юниты/здания — игровой урон, обломки; недострой — снос) ·
СКМ — драг карты (тап — центрировать) · колесо — зум · Space+ЛКМ — угол · M/A/U/T — заряд
приказа, S — стоп, H — hold-fire · Ctrl+1..9 / 1..9 (дабл-тап — камера) · Alt+1/2/3 — профиль
высоты · Home — центр карты · F11 — debug · миникарта: ЛКМ — прыжок камеры, ПКМ — приказ.

---

## 9. Конфигурация, сборка, деплой

- **Сборка — конвейер `python tools/build.py`**: полная пересборка с `-warnaserror` →
  линт накопленных классов багов (двойной `Text`; `new GameObject` без `typeof(RectTransform)`
  в UI-коде; застрявшая версия) → тесты чистой логики (`tests/SupremeNuclear.Tests`,
  консольный раннер: серии `Util.Runs`, терцили `Catalog.ApplyTierFilter`, `Progress01`,
  `EconomyProfile.Rate`, JSON-шаблоны) → проверка версии (исходник → bin DLL → игровой DLL
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
3. Хоткей в `CommanderModeController.Update` (блок `M/A/U/T/S`) и кнопка в
   `SupcomUi.BuildCommandButtons` (+ `HighlightCommand` в `RefreshCommands`).
4. Отрисовка: цвет в `CommanderHud.ModeColor`, подмешивание в `SubmeshOf` (RouteRibbonMesh).
5. Подсказка в `_modeHint` (`SupcomUi.RefreshCommands`).

**Новая настройка:** поле + дефолт в `Settings` → строка в `Bind` (секция/имя/описание по-русски)
→ использование только через `Settings.X`.

**Новый Harmony-патч:** попробуй обойтись без него (§1, §7.3) → guard-Prefix-класс →
строка в реестр §6 → при экзотике — проверка в `SelfCheck`.

**Новая покупка:** `PurchaseService.TryBuyX` (host-guard → деньги → игровой спавн-вызов →
`AddFunds(-цена)` → Notice/Log) → строка-кнопка в `SupcomUi.RefreshContext`.

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
| 3. Экономика v1 | ✅ с оговоркой | один ресурс `factionFunds` вместо массы+энергии; доход тиковый, апгрейд тиров; потоковая семантика (DESIGN 2.2) — не начата |
| 4. Производство | ✅ v1 | депо = фабрика с очередью (ProductionTasks: поток, пауза/цикл, отмена); авиация аэродромов — мгновенная покупка до фабрик ангаров |
| 5. Стройка BuildTask | 🟡 v1 | Task-API v1 (§14): здания из Encyclopedia, инженер=TRUCK, потоковая оплата деньгами, площадка-маркер; фабрики/очереди/ассист — этап B |
| 6. AI-командир | ⬜ | |
| 8. Командирский борт/связь/EMCON | ⬜ | |
| 9. Генератор карт | ⬜ | разметка месторождений сейчас — авторазметка кольцами/JSON |
| 10. Салваж, БЧ, MP-расширения | ⬜ | нативные warhead-гейты отключать будем на шаге БЧ (DESIGN 2.6) |
| Сверх базового плана | ➕ | стойки hold-fire, меш-ленты (эксперим.), SelfCheck-панель |

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
| 11 | Тултипы UI | UiKit/панели | отложено сознательно до этапа D (полировка): без потребителей это мёртвый код — заводить вместе с первым реальным использованием (описания юнитов/оружий при наведении) | отложено (этап D) |
| 12 | Сериализация задач | `Tasks/BuildTasks.cs` | записи data-shaped, но записи в сейвы/MP-синхронизация не подключены (runtime-ссылки FactionHQ/Unit); сделать на этапе сейвов | открыт |
| 13 | Инженер = TRUCK | `GameApi.IsEngineer` | временная роль грузовика до модовых определений юнитов; нет ассиста/ремонта/требований рельефа площадки | открыт (осознанно) |

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
- Завершение: `Spawner.SpawnFromUnitDefinitionInEditor` (проверено экономикой); отмена — возврат
  уплаченного.
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

### Что добавлять сюда дальше (этап B+)
- `ProductionTask` (фабрики/очереди, DESIGN 2.3a) — РЕАЛИЗОВАН v0.36.0 в виде
  `ProductionTasks` (депо-фабрики): очередь = список позиций, потоковая оплата головной
  позиции, пауза/цикл (кнопки нижней панели 2.10), отмена кликом с возвратом,
  выпуск нативным `TrySpawnVehicle`; нет денег — очередь стоит (SupCom).
- Ассист (несколько инженеров на задачу — уже работает через `Builders`, добавить UI-назначение).
- Тиры фабрик, шаблоны, «цикл» — по §2.10.
- Сериализация в сейвы + MP-репликация (Mirage-сообщения) — когда появятся сейвы.
- AI-командир потребляет ТОТ ЖЕ API (`Place/Cancel/Tick`) — поэтому API публичный и data-shaped.
