using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Task-API v1 — веха стройки (DESIGN §2.3, архитектура — ARCHITECTURE §14).
    /// Хост-авторитетные задачи постройки зданий. Записи — data-shaped (цена/позиция/прогресс),
    /// чтобы будущее сериализуемое хранилище/AI/сеть работали с ними без ссылок; runtime-ссылки
    /// (FactionHQ/Unit) живут до этапа сейвов (долг в §12).
    /// Оплата потоковая в духе SupCom: пока инженер(ы) стоят на площадке — деньги утекают
    /// равномерно; кончились деньги или инженеры — задача просто стоит.</summary>
    public static partial class BuildTasks
    {
        public sealed class Task
        {
            public readonly string Id = Guid.NewGuid().ToString("N");
            public FactionHQ Faction;
            public BuildingDefinition Definition; // null у ВПП-задачи; у верфи — корпус-депо
            public GlobalPosition Site;
            public float YawDeg;    // поворот здания (выход — куда смотрит)
            public float PitchDeg;  // наклон под рельеф (депо; ангар/верфь — плоские: апрон/платформа)
            public float RollDeg;
            public bool IsRunway;   // задача-ВПП: по готовности спавнит аэродром
            public bool IsDock;     // задача-ВЕРФЬ: платформа на воде + корпус-депо (v0.92)
            public EconomyService.DepositSite Deposit; // точка месторождения (v3): экстрактор —
                                                       // только на точке массы; генератор на угле — буст
            public EconomyService.DepositSite CoalDeposit => Deposit != null && Deposit.Kind == EconomyService.DepositKind.Coal
                ? Deposit : null;
            public GlobalPosition RunwayEnd; // второй торец полосы
            public float Cost;
            public float PaidMass;   // налито массы (factionFunds)
            public float PaidEnergy; // налито энергии (сток EconomyService)
            /// <summary>Полная энергопотребность задачи = стоимость × фактор профиля.</summary>
            public float EnergyCost => Cost * EconomyService.EnergyCostFactor;
            public readonly List<Unit> Builders = new List<Unit>();
            /// <summary>Стабильные азимуты кольца подхода (веер строителей вокруг
            /// площадки, v0.95): пересчитываются при каждом изменении состава
            /// Builders. Нет азимута — инженер идёт к ближней точке кольца.</summary>
            public readonly Dictionary<Unit, float> ApproachBearings = new Dictionary<Unit, float>();
            public bool ZoneBlocked; // зона не расчищена (кто-то из наземки/флота внутри)
            /// <summary>Кэш скана зоны (троттл ZoneScanIntervalSec): найденный блокер
            /// и время следующего скана. FindZoneBlocker — полный обход реестра, его
            /// звали на каждую задачу каждый кадр; занятость зоны меняется секундами
            /// (юниты едут) — полусекундной свежести достаточно (§12-перф).</summary>
            public Unit ZoneBlockerCache;
            public float NextZoneScanAt;
            public Unit FootprintCache;   // кто стоит/ехал ВНУТРИ футпринта (наливы стоят)
            public float NextFootprintScanAt;

            /// <summary>Зона занята для МАРКЕРА/налива: внешний блокер (радиус) ИЛИ
            /// кто-то внутри футпринта (дисциплина площадки, v0.152: стройка не
            /// капает, пока футпринт не пуст — инженеры выстраиваются на кольце).</summary>
            public bool ZoneBusy => ZoneBlocked || FootprintCache != null;
            public float Progress01 => Cost > 0f ? Mathf.Clamp01(PaidMass / Cost) : 0f;
            public string Label => IsRunway ? Loc.Tr("ВПП") : IsDock ? Loc.Tr("Верфь") : Definition?.unitName;
        }

        private static readonly List<Task> Active = new List<Task>();
        public static int Version { get; private set; }
        public static int Count => Active.Count;

        /// <summary>Режим установки: здание, которое поставит следующий ЛКМ по миру.</summary>
        public static BuildingDefinition Pending;

        /// <summary>Режим установки ВПП: ЛКМ — первый торец, протянуть — второй.</summary>
        public static bool PendingRunway;

        /// <summary>Режим установки ВЕРФИ: ЛКМ по воде — платформа, поворот удержанием.</summary>
        public static bool PendingDock;

        // Реестр построенных верфей (корпус = здание-депо, но производит ФЛОТ):
        // панели и ProductionTasks отличают верфь от обычного депо по нему.
        private static readonly List<Unit> Docks = new List<Unit>();

        /// <summary>Это здание — верфь (платформа на воде, производит корабли)?</summary>
        public static bool IsDock(Unit unit)
        {
            if (unit == null || unit.disabled)
                return false;
            return Docks.Contains(unit);
        }

        private static BuildingDefinition _dockDef;
        private static bool _dockDefResolved;

        /// <summary>Корпус верфи: первое здание-депо из энциклопедии (на префабе
        /// VehicleDepot) — визуально «склад у воды», true-фабрикой кораблей делает
        /// его наш реестр, не нативный депо.</summary>
        public static BuildingDefinition DockDefinition()
        {
            if (_dockDefResolved)
                return _dockDef;
            _dockDefResolved = true;
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia == null || encyclopedia.buildings == null)
                return null;
            foreach (BuildingDefinition def in encyclopedia.buildings)
                if (def != null && def.unitPrefab != null &&
                    def.unitPrefab.GetComponent<VehicleDepot>() != null)
                {
                    _dockDef = def;
                    break;
                }
            return _dockDef;
        }

        /// <summary>Жест поворота площадки/протяжки ВПП: ЛКМ удерживается после клика —
        /// позиция зафиксирована (PlacingSite), выход/второй торец ведём курсором.
        /// Заполняется контроллером, читается призраками.</summary>
        public static bool PlacingActive;
        public static GlobalPosition PlacingSite;
        public static float PendingYawDeg;
        public static GlobalPosition PlacingRunwayEnd;

        /// <summary>Выделенный НЕДОСТРОЙ (клик-без-движения по площадке): контекстные
        /// панели показывают его как объект (SupCom-семантика).</summary>
        public static Task SelectedTask;

        /// <summary>Клик этого кадра съеден установкой здания (выделение пропускает кадр).</summary>
        public static bool Consumed { get; private set; }

        /// <summary>ПКМ этого кадра съеден отменой расстановки: без флага приказы
        /// движения успевали обработать тот же клик (контроллер гасит расстановку
        /// ДО тика приказов).</summary>
        public static bool RmbEaten { get; private set; }

        private static bool IsHost
        {
            get
            {
                var net = NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        public static void Reset()
        {
            Version++;
            Active.Clear();
            BuildGhosts.HideAll();
            PushedAt.Clear();
            ReRouteAt.Clear();
            StallDiagAt.Clear();
            Sequences.Clear();
            Pending = null;
            PendingRunway = false;
            PendingDock = false;
            PlacingActive = false;
            PendingYawDeg = 0f;
            MainlineNodes.Clear();
            Docks.Clear();
            _dockDefResolved = false;
            Consumed = false;
            PatrolAssists.Clear();
            _nextPatrolScan = 0f;
            EscortAssists.Clear();
            _nextEscortScan = 0f;
        }

        public static void NewFrame()
        {
            Consumed = false;
            RmbEaten = false;
        }

        public static void MarkConsumed() => Consumed = true;

        public static void MarkRmbEaten() => RmbEaten = true;

        private static readonly Dictionary<BuildingDefinition, bool> HangarDefs =
            new Dictionary<BuildingDefinition, bool>();

        /// <summary>Здание-ангар (на префабе есть компонент Hangar): пристраивается
        /// к своему аэродрому и становится слотом производства мультифабрики.</summary>
        /// <summary>Вертолётная площадка: ФЛАГ РОЛИ ПУЛА (heliport=true в
        /// Roster/buildings.json, v0.148). Прежние детекты (имя здания, каталог
        /// живого ангара — пуст сразу после спавна, автопилот префаба —
        /// рантайм-компонент) заменены явной ролью: наш каталог = только пул,
        /// значит у каждой построенной площадки есть спецификация.</summary>
        internal static bool IsHeliportDef(BuildingDefinition def) =>
            BuildingPool.IsHeliport(def);

        public static bool IsHangarDefinition(BuildingDefinition def)
        {
            if (def == null)
                return false;
            if (HangarDefs.TryGetValue(def, out bool isHangar))
                return isHangar;
            return HangarDefs[def] = def.unitPrefab != null &&
                                      def.unitPrefab.GetComponent<Hangar>() != null;
        }

        private static readonly Dictionary<BuildingDefinition, bool> ProducingDefs =
            new Dictionary<BuildingDefinition, bool>();

        /// <summary>Производящее здание (ангар ИЛИ депо-фабрика техники; корпус верфи —
        /// депо): имеет точку выхода — призрак показывает стрелку выката и крутится
        /// выходом к курсору; депо на суше дополнительно наклоняется под рельеф.</summary>
        public static bool IsProducingDefinition(BuildingDefinition def)
        {
            if (def == null)
                return false;
            if (ProducingDefs.TryGetValue(def, out bool producing))
                return producing;
            return ProducingDefs[def] = IsHangarDefinition(def) ||
                                         (def.unitPrefab != null &&
                                          def.unitPrefab.GetComponent<VehicleDepot>() != null);
        }

        private static readonly Dictionary<BuildingDefinition, bool> StorageDefs =
            new Dictionary<BuildingDefinition, bool>();

        /// <summary>Здание-ХРАНИЛИЩЕ БЧ (на префабе нативный WarheadStorage,
        /// §2.14-3): пристраивается к своему аэродрому (как ангар) и само наливает
        /// боеголовки (WarheadService). Строит T3-инженер (тир здания 3 в ростере).</summary>
        public static bool IsStorageDefinition(BuildingDefinition def)
        {
            if (def == null)
                return false;
            if (StorageDefs.TryGetValue(def, out bool isStorage))
                return isStorage;
            return StorageDefs[def] = def.unitPrefab != null &&
                def.unitPrefab.GetComponentInChildren<WarheadStorage>(true) != null;
        }

        /// <summary>Площадка — ФАБРИКА (депо/верфь/ангар): принимает очередь
        /// производства как готовая, выпуск — по завершении стройки (v0.107,
        /// ProductionTasks.EnqueueSite/AdoptSite). ВПП — НЕ фабрика: её
        /// исполнители (ангары) строятся отдельно уже после аэродрома.</summary>
        public static bool IsFactorySite(Task t) =>
            t != null && (t.IsDock ||
                (!t.IsRunway && t.Definition != null && IsProducingDefinition(t.Definition)));

        /// <summary>Площадка ещё в реестре активных? Очередь ProductionTasks
        /// так проверяет свои позиции недостроя каждый тик.</summary>
        public static bool IsActive(Task t) => t != null && Active.Contains(t);

        // ЕДИНАЯ ОЧЕРЕДЬ инженера ПО ПОРЯДКУ КЛИКОВ (SupCom): записи Move/Task вперемешку.
        // Аванцер выдаёт следующую точку, когда текущая исполнена (движение доехало,
        // стройка достроена). Заменяет хвосты и цепочки прошлых версий.
        public sealed class SeqEntry
        {
            public GlobalPosition Move;   // либо движение…
            public Task Task;             // …либо площадка (одно из двух)
            public bool Issued;           // аванцер уже выдал эту точку в GroundOrders
            public int GroupId;           // групповой вейпоинт (SupCom): один приказ группе
            public bool IsStale => Task != null && !Active.Contains(Task);
        }

        private static int _groupSeq;
        public static int NextGroupId() => ++_groupSeq;

        private static readonly Dictionary<Unit, List<SeqEntry>> Sequences =
            new Dictionary<Unit, List<SeqEntry>>();

        public static IReadOnlyList<SeqEntry> SequenceOf(Unit builder) =>
            Sequences.TryGetValue(builder, out List<SeqEntry> seq) ? seq : null;

        /// <summary>Активные задачи (маркеры площадок в HUD).</summary>
        public static IReadOnlyList<Task> All => Active;
    }
}

