using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Ремонт инженером (v0.149, канон SupCom — 4-я функция инженерного сьюта
    /// build/repair/reclaim/capture; DESIGN §2.3-1): инженеры со связью Repair
    /// (GroundOrders.SetRepair — явный ПКМ-приказ или патруль-подхват раненых)
    /// стоят в радиусе налива (EconomyProfile.repairRadiusM) и восстанавливают
    /// HP цели за массу. Скорость = СУММА факторов мощи инженеров (как стройка),
    /// стоимость = доля восстановленного HP × цену юнита × repairCostFactor
    /// (EcoFlow.RepairCost). Налив — EconomyService.Drain: дефицит массы/энергии
    /// режет скорость (потоковая семантика стройки). Завершение (HP ≥ 99.5%) —
    /// Notice владельцу, патрульным инженерам возвращается маршрут (зеркало
    /// патруль-ассиста недостроев BuildTasks.TickPatrolAssist). Хост-only.
    /// MP-нюанс: восстановление hitPoints не реплицируется — клиенты увидят
    /// полные HP после следующего RpcDamage (класс проблемы §12-46б, долг §12).
    /// </summary>
    public static class RepairService
    {
        private const float ScanInterval = 0.5f; // троттл патруль-подхвата
        private const float DoneHp01 = 0.995f;   // «полностью исправен»

        /// <summary>Снятый патруль инженера-ремонтника (возврат по завершении,
        /// как PatrolAssist у недостроев).</summary>
        private sealed class PatrolDrop
        {
            public readonly List<GlobalPosition> Points = new List<GlobalPosition>();
            public readonly List<int> Groups = new List<int>();
            public int Index;
            public int LoopStart;
        }

        private static readonly Dictionary<Unit, PatrolDrop> PatrolDrops =
            new Dictionary<Unit, PatrolDrop>();

        // Налив: связи из GroundOrders пересобираются в «цель → инженеры» каждый тик.
        private static readonly List<Unit> EngBuf = new List<Unit>();
        private static readonly List<Unit> TgtBuf = new List<Unit>();
        private static readonly Dictionary<Unit, List<Unit>> Pumps = new Dictionary<Unit, List<Unit>>();
        private static readonly List<Unit> DoneBuf = new List<Unit>();
        private static readonly List<Unit> PatrolScan = new List<Unit>();

        /// <summary>Снимок активных ремонтов для маркеров HUD: параллельные буферы
        /// (цель / HP на начало работы / текущий HP), перезаполняются в тике.</summary>
        public static readonly List<Unit> MarkerTargets = new List<Unit>();
        public static readonly List<float> MarkerFrom01 = new List<float>();
        public static readonly List<float> MarkerHp01 = new List<float>();
        private static readonly Dictionary<Unit, float> StartedAt01 = new Dictionary<Unit, float>();

        private static float _nextPatrolScan;

        public static void Reset()
        {
            PatrolDrops.Clear();
            StartedAt01.Clear();
            MarkerTargets.Clear();
            MarkerFrom01.Clear();
            MarkerHp01.Clear();
            _nextPatrolScan = 0f;
        }

        public static void Tick(float dt)
        {
            if (!GameApi.InMission)
            {
                if (PatrolDrops.Count > 0 || StartedAt01.Count > 0)
                    Reset();
                return;
            }

            // Восстановление HP — авторитетно только на хосте (в сингле мы и есть хост).
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;

            EconomyProfile profile = EconomyService.Profile;
            if (profile == null || profile.repairHpPerSecond <= 0f)
                return;

            TickPumps(dt, profile);
            FinishDrops();
            TickPatrolAssist();
        }

        /// <summary>Налив: цель → сумма факторов СТОЯЩИХ в радиусе инженеров;
        /// масса/энергия через Drain, фактически налитое распределяется по частям.</summary>
        private static void TickPumps(float dt, EconomyProfile profile)
        {
            GroundOrders.CollectRepairs(EngBuf, TgtBuf);
            Pumps.Clear();
            for (int i = 0; i < EngBuf.Count; i++)
            {
                Unit eng = EngBuf[i];
                if (eng == null || eng.disabled || TgtBuf[i] == null)
                    continue;
                if (!Pumps.TryGetValue(TgtBuf[i], out List<Unit> list))
                    Pumps[TgtBuf[i]] = list = new List<Unit>();
                list.Add(eng);
            }

            MarkerTargets.Clear();
            MarkerFrom01.Clear();
            MarkerHp01.Clear();
            DoneBuf.Clear();

            float energyFactor = EconomyService.EnergyCostFactor;
            foreach (KeyValuePair<Unit, List<Unit>> kv in Pumps)
            {
                Unit target = kv.Key;
                if (target == null || target.disabled)
                {
                    DoneBuf.Add(target); // цель погибла — связи снимет тик GroundOrders
                    continue;
                }
                float hp01 = GameApi.HpFraction(target);
                if (!StartedAt01.TryGetValue(target, out float from01))
                    StartedAt01[target] = from01 = hp01;

                FactionHQ hq = null; // платит фракция первого живого инженера
                float power = 0f;
                float radius = GroundOrders.RepairRadiusM + target.maxRadius;
                Vector3 tp = target.transform.position;
                foreach (Unit eng in kv.Value)
                {
                    if (eng == null || eng.disabled)
                        continue;
                    if (PauseService.IsPaused(eng))
                        continue; // пауза юнита — не наливает
                    if (hq == null)
                        hq = eng.NetworkHQ;
                    if (Mathf.Abs(eng.speed) > 1f)
                        continue; // ещё едет к цели — не льёт (как салваж/стройка)
                    Vector3 ep = eng.transform.position;
                    if (new Vector2(ep.x - tp.x, ep.z - tp.z).sqrMagnitude > radius * radius)
                        continue;
                    power += EngineerService.FactorOf(eng.definition);
                }

                if (power > 0f && hp01 < DoneHp01)
                {
                    float step01 = Mathf.Min(profile.repairHpPerSecond * power * dt, DoneHp01 - hp01);
                    float value = target.definition != null ? target.definition.value : 0f;
                    float massWant = EcoFlow.RepairCost(value, hp01, hp01 + step01, profile.repairCostFactor);
                    EcoFlow.Paid paid = EconomyService.Drain(hq, massWant, massWant * energyFactor);
                    // Дефицит режет скорость: фактически налитая доля = пропорция paid.
                    float actual01 = massWant > 0.01f ? step01 * Mathf.Clamp01(paid.Mass / massWant) : step01;
                    Heal(target, actual01);
                    hp01 = Mathf.Min(DoneHp01, hp01 + actual01); // кэш GameApi догонит к маркерам
                }

                if (hp01 >= DoneHp01)
                {
                    DoneBuf.Add(target);
                    if (hq != null && GameApi.TryGetLocalHQ(out FactionHQ local) && local == hq)
                        CommanderHud.Notice(Loc.F("Ремонт завершён: {0}",
                            target.definition != null ? target.definition.unitName : "?"));
                    continue;
                }
                MarkerTargets.Add(target);
                MarkerFrom01.Add(from01);
                MarkerHp01.Add(hp01);
            }

            if (DoneBuf.Count > 0)
            {
                // Инженерам завершённых целей — снять связь (патруль вернёт FinishDrops).
                // RepairFinished (не Clear): охранник с GuardResume вернётся к охране
                // живой цели (канон SupCom guard — авто-ремонт подопечного).
                GroundOrders.CollectRepairs(EngBuf, TgtBuf);
                foreach (Unit target in DoneBuf)
                {
                    StartedAt01.Remove(target);
                    for (int i = 0; i < EngBuf.Count; i++)
                        if (TgtBuf[i] == target && EngBuf[i] != null)
                            GroundOrders.RepairFinished(EngBuf[i]);
                }
            }
        }

        /// <summary>Распределить долю восстановления по повреждённым частям:
        /// пропорционально недостаче, максимум части = 100 × масштаб клона
        /// (EngineerService.MaxHpScaleOf — цель-клон с канонным HP ×2.67/×5.33).</summary>
        private static void Heal(Unit target, float fraction01)
        {
            if (fraction01 <= 0f)
                return;
            List<UnitPart> parts = target.partLookup;
            if (parts == null || parts.Count == 0)
                return;
            float scale = EngineerService.MaxHpScaleOf(target);
            float totalMax = 0f, totalMissing = 0f;
            for (int i = 0; i < parts.Count; i++)
            {
                UnitPart p = parts[i];
                if (p == null)
                    continue;
                totalMax += 100f * scale;
                totalMissing += Mathf.Max(0f, 100f * scale - p.hitPoints);
            }
            if (totalMissing <= 0f || totalMax <= 0f)
                return;
            float add = Mathf.Min(fraction01 * totalMax, totalMissing);
            for (int i = 0; i < parts.Count; i++)
            {
                UnitPart p = parts[i];
                if (p == null)
                    continue;
                float missing = Mathf.Max(0f, 100f * scale - p.hitPoints);
                if (missing <= 0f)
                    continue;
                p.hitPoints += add * (missing / totalMissing);
            }
        }

        /// <summary>Возврат снятых патрулей. Живую ремонт-связь держим; связь ушла
        /// (HP полон — Clear здесь, цель погибла — тик GroundOrders) → инженеру без
        /// ДРУГИХ приказов возвращаем маршрут; перебитому приказом игрока (не Idle)
        /// — снимаем дроп без возврата (канон патруль-ассиста недостроев).</summary>
        private static void FinishDrops()
        {
            if (PatrolDrops.Count == 0)
                return;
            List<Unit> restore = null;
            foreach (KeyValuePair<Unit, PatrolDrop> kv in PatrolDrops)
            {
                Unit eng = kv.Key;
                if (eng == null || eng.disabled)
                    continue; // мёртв — чистим ниже одним проходом
                if (GroundOrders.RepairTargetOf(eng) != null)
                    continue; // ещё чинит
                PatrolDrop drop = kv.Value;
                if (GroundOrders.IsIdle(eng))
                {
                    GroundOrders.RestorePatrol(eng, drop.Points, drop.Groups, drop.Index, drop.LoopStart);
                    Log.Info("Repair: патруль возвращён — " + eng.NetworkunitName);
                }
                else
                    Log.Info("Repair: патруль не возвращён — инженер перебит приказом: " +
                             eng.NetworkunitName);
                (restore ??= new List<Unit>()).Add(eng);
            }
            // Чистка: обработанные + мёртвые (fake-null) — Remove внутри foreach
            // по словарю запрещён (InvalidOperationException), как в GroundOrders.Tick.
            if (restore != null)
                foreach (Unit eng in restore)
                    PatrolDrops.Remove(eng);
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, PatrolDrop> kv in PatrolDrops)
                if (kv.Key == null)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead != null)
                foreach (Unit eng in dead)
                    PatrolDrops.Remove(eng);
        }

        /// <summary>Патруль-подхват раненых (канон SupCom, зеркало авто-достройки
        /// недостроев): свободный инженер в патруле снимает маршрут, чинит
        /// ближайшего раненого своей фракции в радиусе настройки и возвращается.
        /// Троттл 0.5 с; радиус — Settings.BuildPatrolAssistRadiusM (0 = выкл, общая
        /// настройка обоих подхватов).</summary>
        private static void TickPatrolAssist()
        {
            float radius = Settings.BuildPatrolAssistRadiusM;
            if (radius <= 0f || Time.unscaledTime < _nextPatrolScan)
                return;
            _nextPatrolScan = Time.unscaledTime + ScanInterval;
            GroundOrders.CollectPatrols(PatrolScan);
            float radiusSq = radius * radius;
            foreach (Unit eng in PatrolScan)
            {
                if (eng == null || eng.disabled || !GameApi.IsEngineer(eng))
                    continue;
                if (PatrolDrops.ContainsKey(eng) || BuildTasks.IsBuilderBusy(eng))
                    continue; // уже чинит/строит/есть очередь
                if (SalvageService.IsCollectingNear(eng))
                    continue; // стоит у недособранного обломка — занят сбором
                FactionHQ hq = eng.NetworkHQ;
                if (hq == null)
                    continue;
                Vector3 pos = eng.transform.position;
                Unit best = null;
                float bestSq = radiusSq;
                foreach (Unit u in UnitRegistry.allUnits)
                {
                    if (u == null || u.disabled || u == eng || u.NetworkHQ != hq)
                        continue;
                    if (!(u is GroundVehicle || u is Ship || u is Building))
                        continue; // авиацию чинит аэродромный сервис (§3-10)
                    if (GameApi.HpFraction(u) >= DoneHp01)
                        continue;
                    Vector3 q = u.transform.position;
                    float dSq = new Vector2(q.x - pos.x, q.z - pos.z).sqrMagnitude;
                    if (dSq < bestSq)
                    {
                        bestSq = dSq;
                        best = u;
                    }
                }
                if (best == null)
                    continue;
                var drop = new PatrolDrop();
                if (!GroundOrders.TryGetPatrolSnapshot(eng, drop.Points, drop.Groups,
                        out int idx, out int loopStart))
                    continue;
                drop.Index = idx;
                drop.LoopStart = loopStart;
                PatrolDrops[eng] = drop;
                GroundOrders.SetRepair(eng, best);
                Log.Info("Repair: патруль-подхват — " + eng.NetworkunitName + " → " +
                         (best.definition != null ? best.definition.unitName : "?") +
                         " (" + Mathf.Sqrt(bestSq).ToString("F0") + " м)");
            }
        }
    }
}
