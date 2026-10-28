using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Залповый пуск по нескольким целям (DESIGN §2.11). Хост-тик сразу после
    /// SortieService: борт с головным Salvo-сегментом подлетает к цели/якорю
    /// (руление — патчи №1/№4), а в зоне боя SalvoService разом пускает выбранные
    /// ракетные станции: список целей строит нативное распределение
    /// CombatAI.LookForMissileTargets (цель клика + соседние в даталинке, расход —
    /// по CalcAttacksNeeded), потолок ракет на цель — Settings.SalvoMaxRoundsPerTarget.
    /// Пуск — через WeaponManager.Fire() (при списке &gt;1 цели игра сама уходит в
    /// нативный SalvoFire). Каждая станция пускает один раз за сегмент; после отстрела
    /// сегмент завершается, «сухой» борт уводит на базу SortieService.
    /// Бомбы/пушки — не залп: остаются нативному бою (долг — ARCHITECTURE §12).
    /// </summary>
    internal static class SalvoService
    {
        private static readonly Dictionary<Unit, HashSet<int>> Stations =
            new Dictionary<Unit, HashSet<int>>();

        private static readonly List<Unit> Units = new List<Unit>();
        private static readonly List<IReadOnlyList<AircraftOrders.OrderSegment>> Plans =
            new List<IReadOnlyList<AircraftOrders.OrderSegment>>();

        private static bool IsHost
        {
            get
            {
                var net = NuclearOption.Networking.NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        /// <summary>Станция борта участвует в залпе? Пустой whitelist (по умолчанию) —
        /// участвуют все ракетные станции с боеприпасами.</summary>
        public static bool StationEnabled(Unit aircraft, int stationIndex) =>
            !Stations.TryGetValue(aircraft, out HashSet<int> set) || set.Contains(stationIndex);

        /// <summary>Кнопка-чип станции (панель, одиночный борт): вкл/выкл в залпе.</summary>
        public static void ToggleStation(Unit aircraft, int stationIndex)
        {
            if (!Stations.TryGetValue(aircraft, out HashSet<int> set))
                set = Stations[aircraft] = new HashSet<int>();
            if (!set.Add(stationIndex))
                set.Remove(stationIndex);
        }

        /// <summary>Тикает всегда в миссии (не только в режиме командира) — как SortieService:
        /// залп-приказы живут независимо от того, открыт ли стол командира. Хост-only:
        /// пуски идут на сервере (свои борта локальны хосту).</summary>
        public static void Tick()
        {
            if (!IsHost)
                return;
            AircraftOrders.CollectAll(Units, Plans);
            for (int i = 0; i < Units.Count; i++)
            {
                AircraftOrders.OrderSegment seg = Plans[i].Count > 0 ? Plans[i][0] : null;
                if (seg == null || seg.Mode != AirOrderMode.Salvo || seg.SalvoFired)
                    continue;
                if (Units[i] is Aircraft aircraft)
                    TickSalvo(aircraft, seg);
            }
        }

        private static void TickSalvo(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            WeaponManager wm = aircraft.weaponManager;
            if (wm == null || aircraft.remoteSim)
                return;
            if (seg.SalvoStationsFired == null)
                seg.SalvoStationsFired = new List<int>();

            // Первичная цель: явная из сегмента (порядок кликов) либо ближайшая
            // разведанная у якоря (залп по площади). Целеуказание — только даталинк.
            // Поиск у якоря — троттл AnchorScanSec (§12-45г, v0.112): раньше скан
            // решётки шёл КАЖДЫЙ кадр, пока даталинк пуст; цель кэшируется,
            // мёртвая в окне — рескан немедленно.
            Unit primary = AircraftOrders.SalvoPrimary(seg);
            if (primary == null)
                primary = AnchorTarget(aircraft, seg);
            if (primary == null)
                return; // цели не разведаны — борт кружит у якоря, ждём даталинк

            // Подлёт ещё не закончен: цель вне зоны боя (дальность × фактор).
            if (!AircraftOrders.AttackEngaged(aircraft, primary))
                return;

            bool anyUsable = false;
            bool allFired = true;
            for (int idx = 0; idx < aircraft.weaponStations.Count; idx++)
            {
                WeaponStation st = aircraft.weaponStations[idx];
                if (!IsSalvoStation(aircraft, st, idx))
                    continue; // не ракетная/без боеприпасов/исключена игроком
                anyUsable = true;
                if (seg.SalvoStationsFired.Contains(idx))
                    continue;
                allFired = false;
                TryFireStation(aircraft, wm, seg, primary, st, idx);
            }

            // Все станции пустили (или ракетных не было вовсе — бомбовозу залп нечем):
            // сегмент завершён, дальше — план/RTB (SortieService уведёт «сухого»).
            if (!seg.SalvoFired && (!anyUsable || allFired))
            {
                seg.SalvoFired = true; // Step снимет сегмент
                AircraftOrders.Version++;
            }
        }

        /// <summary>Ракетная станция залпа: с боеприпасами, выбрана игроком, не занята.</summary>
        private static bool IsSalvoStation(Aircraft aircraft, WeaponStation st, int idx)
        {
            return st != null && !st.Cargo && st.WeaponInfo != null && st.WeaponInfo.missile &&
                   st.Ammo > 0 && StationEnabled(aircraft, idx) &&
                   WeaponGateService.IsAllowed(aircraft, st); // гейт главнее залпа (DESIGN §2.11)
        }

        /// <summary>Пуск одной станции по распределённому списку целей (нативный путь
        /// UseMissiles: LookForMissileTargets → TargetListChanged → Fire → SalvoFire).
        /// Станция отмечается отстрелянной только когда пуск реально состоялся.
        /// Стойка hold-fire и правило типа против цели залпа — станция отработана
        /// без пуска (MarkFired): предохранитель всё равно резал бы пуск, а сегмент
        /// не должен вечно ждать «сухую» станцию (v0.106).</summary>
        private static void TryFireStation(Aircraft aircraft, WeaponManager wm,
            AircraftOrders.OrderSegment seg, Unit primary, WeaponStation st, int idx)
        {
            if (StanceService.IsHoldFire(aircraft) ||
                !WeaponGateService.TypeAllows(aircraft, st, primary))
            {
                MarkFired(seg, idx); // огонь запрещён правилами — не ждём эту станцию
                return;
            }
            if (st.SalvoInProgress || !st.Ready() || st.SafetyIsOn(aircraft))
                return; // залп/перезарядка/шасси — повторим в следующем тике

            // Нос должен смотреть на цель в пределах выравнивания пуска (как нативный
            // UseMissiles); пока разворачивается — не тратим ракеты.
            if (aircraft.NetworkHQ != null &&
                aircraft.NetworkHQ.TryGetKnownPosition(primary, out GlobalPosition known))
            {
                Vector3 toTarget = known.ToLocalPosition() - aircraft.transform.position;
                if (Vector3.Angle(aircraft.transform.forward, toTarget) >
                    st.WeaponInfo.targetRequirements.minAlignment)
                    return;
            }

            List<Unit> targets = wm.GetTargetList();
            targets.Clear();
            int found = CombatAI.LookForMissileTargets(aircraft, primary, st, targets);
            if (found <= 0)
            {
                MarkFired(seg, idx); // станции нечего пускать по этим целям — не ждём её
                return;
            }
            int trimmed = Util.TrimSalvo(targets, Settings.SalvoMaxRoundsPerTarget);
            if (targets.Count == 0)
            {
                MarkFired(seg, idx);
                return;
            }

            wm.SetActiveStation((byte)idx);
            wm.TargetListChanged();
            wm.Fire(); // список >1 цели → нативный SalvoFire (по ракете на цель с интервалом)
            MarkFired(seg, idx);
            Log.Info($"Залп: {aircraft.definition.unitName} ст.{idx} " +
                     $"({st.WeaponInfo.shortName}) → {targets.Count} пуск(ов) по {CountDistinct(targets)} целям" +
                     (trimmed > 0 ? $", потолок {Settings.SalvoMaxRoundsPerTarget}/цель −{trimmed}" : ""));
        }

        private static void MarkFired(AircraftOrders.OrderSegment seg, int idx)
        {
            if (seg.SalvoStationsFired == null)
                return;
            if (!seg.SalvoStationsFired.Contains(idx))
                seg.SalvoStationsFired.Add(idx);
        }

        private static int CountDistinct(List<Unit> targets)
        {
            var set = new HashSet<Unit>();
            foreach (Unit t in targets)
                set.Add(t);
            return set.Count;
        }

        /// <summary>Залп по площади: ближайший разведанный (даталинк) враг у якоря —
        /// вокруг него нативное распределение размажет залп по соседям.
        /// Вызов — через кэш AnchorTarget (не каждый кадр).</summary>
        private const float AnchorScanSec = 0.5f;

        private struct AnchorScan
        {
            public float At;
            public Unit Target;
        }

        private static readonly Dictionary<Unit, AnchorScan> AnchorScans =
            new Dictionary<Unit, AnchorScan>();

        private static Unit AnchorTarget(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            if (AnchorScans.TryGetValue(aircraft, out AnchorScan s) &&
                Time.unscaledTime - s.At < AnchorScanSec)
                return s.Target != null && !s.Target.disabled ? s.Target : null;
            Unit target = NearestTrackedAtAnchor(aircraft, seg);
            AnchorScans[aircraft] = new AnchorScan { At = Time.unscaledTime, Target = target };
            return target;
        }

        private static Unit NearestTrackedAtAnchor(Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null || seg.Points.Count == 0)
                return null;
            float range = AircraftOrders.EngageRadius(aircraft) * Settings.AttackEngageFactor;
            if (range < 1f)
                return null;
            Vector3 anchor = seg.Points[seg.Points.Count - 1].ToLocalPosition();
            Unit best = null;
            float bestDist = float.MaxValue;
            foreach (Unit enemy in BattlefieldGrid.GetUnitsInRangeEnumerable(
                         anchor.ToGlobalPosition(), range))
            {
                if (enemy == null || enemy.disabled || enemy.NetworkHQ == null || enemy.NetworkHQ == hq)
                    continue;
                if (!(enemy is GroundVehicle || enemy is Ship || enemy is Aircraft || enemy is Building))
                    continue;
                if (hq.GetTrackingData(enemy.persistentID) == null)
                    continue; // даталинк: невидимого не видим (DESIGN §2.11)
                Vector3 d = enemy.transform.position - anchor;
                float dist = new Vector2(d.x, d.z).magnitude;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = enemy;
                }
            }
            return best;
        }

        /// <summary>Мёртвые борта (Unity fake-null) вычищаются каждый тик контроллера.</summary>
        public static void Prune()
        {
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, HashSet<int>> kv in Stations)
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead != null)
                foreach (Unit unit in dead)
                {
                    Stations.Remove(unit);
                    AnchorScans.Remove(unit);
                }
        }

        /// <summary>Смена сцены: борта умершей сцены недействительны (инвариант №11).</summary>
        public static void Reset()
        {
            Stations.Clear();
            AnchorScans.Clear();
        }
    }
}
