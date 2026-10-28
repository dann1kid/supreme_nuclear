using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Патчи пиннинга цели (реестр — ARCHITECTURE §6): самолёты (AIPilotCombatModes) и
    /// вертолёты/тильтвинги (AIHeloCombatState) — у обоих свой приватный currentTarget и
    /// своя 5-секундная оценка целей. Postfix после нативной оценки: у борта с приказом
    /// АТАКА выбранная цель подменяется на указанную игроком («ПКМ по цели = огонь
    /// доступными средствами», DESIGN §2.11), у борта в атаке-движении цель вне конуса
    /// вектора приказа снимается. Оригинал всегда отрабатывает полностью — вмешательство
    /// только в ВЫБОР цели, стрельба/уклонение/режимы остаются нативными. Двигаемся только
    /// по даталинку: цель вне трекинг-базы фракции не пиннится.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "AssessHQTargets")]
    internal static class AircraftTargetPinPatch
    {
        private static readonly FieldInfo AircraftField =
            AccessTools.Field(typeof(PilotBaseState), "aircraft");

        private static readonly FieldInfo PilotField =
            AccessTools.Field(typeof(PilotBaseState), "pilot");

        private static readonly FieldInfo CurrentTargetField =
            AccessTools.Field(typeof(AIPilotCombatModes), "currentTarget");

        private static readonly FieldInfo CurrentTargetTrackingField =
            AccessTools.Field(typeof(AIPilotCombatModes), "currentTargetTracking");

        private static readonly FieldInfo CurrentWeaponInfoField =
            AccessTools.Field(typeof(AIPilotCombatModes), "currentWeaponInfo");

        private static readonly FieldInfo AllowAssessmentField =
            AccessTools.Field(typeof(AIPilotCombatModes), "allowTargetAssessment");

        /// <summary>Все reflection-поля на месте (проверяется в SelfCheck).</summary>
        public static bool FieldsOk =>
            AircraftField != null && PilotField != null && CurrentTargetField != null &&
            CurrentTargetTrackingField != null && CurrentWeaponInfoField != null &&
            AllowAssessmentField != null;

        private static void Postfix(AIPilotCombatModes __instance)
        {
            Aircraft aircraft = AircraftField?.GetValue(__instance) as Aircraft;
            if (aircraft == null || aircraft.NetworkHQ == null || aircraft.weaponManager == null)
                return;
            void Register() =>
                RegisterClaim(aircraft, CurrentTargetField?.GetValue(__instance) as Unit);

            // Во время залпа/бомбометания/лазерного наведения оценка заблокирована нативно —
            // не вмешиваемся (иначе сломаем идущий пуск); цель борта при этом не меняется,
            // но в реестре занятости он остаётся со своей целью.
            if (AllowAssessmentField != null && !(bool)AllowAssessmentField.GetValue(__instance))
            {
                Register();
                return;
            }
            if (aircraft.weaponManager.currentWeaponStation != null &&
                aircraft.weaponManager.currentWeaponStation.SalvoInProgress)
            {
                Register(); // зеркалим нативный guard
                return;
            }

            // Гейт вооружения (DESIGN §2.11) — до выхода «нет приказа»: выключенный
            // класс не применяется даже свободным бортом (автоогонь по пути).
            ApplyWeaponGates(CurrentTargetField, CurrentWeaponInfoField, __instance, aircraft);

            AircraftOrders.OrderSegment seg =
                AircraftOrders.Step(aircraft, aircraft.transform.position);
            if (seg == null)
            {
                Register(); // нет приказа — выбор нативный
                return;
            }

            if (seg.Mode == AirOrderMode.Attack)
            {
                PinTarget(CurrentTargetField, CurrentWeaponInfoField, __instance, aircraft, seg.Target);
                if (CurrentTargetField?.GetValue(__instance) as Unit == seg.Target &&
                    CurrentTargetTrackingField != null)
                {
                    // натив ведёт вектора по TrackingInfo — подсунем и его
                    TrackingInfo tracking = aircraft.NetworkHQ.GetTrackingData(seg.Target.persistentID);
                    if (tracking != null)
                        CurrentTargetTrackingField.SetValue(__instance, tracking);
                }
            }
            else if (seg.Mode == AirOrderMode.AttackMove)
            {
                VetoOutsideCone(CurrentTargetField, CurrentWeaponInfoField, __instance, aircraft, seg);
                DistributeTargets(CurrentTargetField, CurrentWeaponInfoField, __instance,
                    aircraft, seg, cone: true);
            }
            else if (seg.Mode == AirOrderMode.Patrol)
            {
                DistributeTargets(CurrentTargetField, CurrentWeaponInfoField, __instance,
                    aircraft, seg, cone: false);
            }
            Register(); // итоговая цель борта — в реестр занятости
        }

        // ---- общее ядро пиннинга (используется и вертолётным близнецом ниже) ----

        /// <summary>Гейт вооружения (DESIGN §2.11): 1) борт в стойке hold-fire —
        /// цель снимается целиком, борт не вступает в бой (стойка гасит всё);
        /// 2) натив выбрал станцию, не проходящую правила (класс-гейт или правило
        /// типа против домена ЦЕЛИ), — перевыбор лучшей РАЗРЕШЁННОЙ по той же цели
        /// (нативный AnalyzeTarget); разрешённых средств нет — цель снимается.
        /// Работает без приказа мода: гейт главнее и для автоогня свободного борта.
        /// Страховка последней линии — префиксы WeaponUseGatePatch.</summary>
        internal static void ApplyWeaponGates(FieldInfo currentTargetField, FieldInfo weaponInfoField,
            object instance, Aircraft aircraft)
        {
            if (StanceService.IsHoldFire(aircraft))
            {
                currentTargetField?.SetValue(instance, null);
                weaponInfoField?.SetValue(instance, null);
                aircraft.weaponManager.ClearTargetList();
                return;
            }
            WeaponClass mask = WeaponGateService.BlockedOf(aircraft);
            if (mask == WeaponClass.None && !WeaponGateService.HasTypeRules(aircraft))
                return;
            Unit target = currentTargetField?.GetValue(instance) as Unit;
            WeaponStation current = aircraft.weaponManager.currentWeaponStation;
            if (current != null && (mask & Catalog.ClassOf(current.WeaponInfo)) == 0 &&
                WeaponGateService.TypeAllows(aircraft, current, target))
                return; // выбранное оружие разрешено — не вмешиваемся

            WeaponStation best = null;
            float bestOpportunity = 0f;
            if (target != null && !target.disabled &&
                aircraft.NetworkHQ.TryGetKnownPosition(target, out _))
            {
                TrackingInfo tracking = aircraft.NetworkHQ.GetTrackingData(target.persistentID);
                if (tracking != null)
                    foreach (WeaponStation station in aircraft.weaponStations)
                    {
                        if (station == null || station.Cargo || station.WeaponInfo == null ||
                            station.Ammo <= 0)
                            continue;
                        if ((mask & Catalog.ClassOf(station.WeaponInfo)) != 0)
                            continue; // класс выключен гейтом
                        if (!WeaponGateService.TypeAllows(aircraft, station, target))
                            continue; // тип не разрешён против домена этой цели
                        if (station.WeaponInfo.energy && aircraft.GetPowerSupply().GetCharge() < 0.6f)
                            continue; // как нативный ChooseHQTarget
                        OpportunityThreat ot = CombatAI.AnalyzeTarget(station, aircraft, tracking, 0f, -1f, 100f);
                        if (ot.opportunity > bestOpportunity)
                        {
                            bestOpportunity = ot.opportunity;
                            best = station;
                        }
                    }
            }
            if (best != null)
            {
                weaponInfoField?.SetValue(instance, best.WeaponInfo);
                aircraft.weaponManager.currentWeaponStation = best;
                aircraft.weaponManager.ClearTargetList();
                aircraft.weaponManager.AddTargetList(target);
                return;
            }
            currentTargetField?.SetValue(instance, null);
            weaponInfoField?.SetValue(instance, null);
            aircraft.weaponManager.ClearTargetList();
        }

        /// <summary>Пиннинг: цель приказа становится текущей целью боевого AI, станция —
        /// лучшая способная бить её (нативный AnalyzeTarget, даталинк-совместимость).</summary>
        internal static void PinTarget(FieldInfo currentTargetField, FieldInfo weaponInfoField,
            object instance, Aircraft aircraft, Unit target)
        {
            if (target == null || target.disabled)
                return;
            if (currentTargetField?.GetValue(instance) as Unit == target)
                return; // натив уже выбрал нашу цель
            if (!aircraft.NetworkHQ.TryGetKnownPosition(target, out _))
                return; // нет в даталинке — пиннить бессмысленно (AI не сможет стрелять)
            TrackingInfo tracking = aircraft.NetworkHQ.GetTrackingData(target.persistentID);
            if (tracking == null)
                return;

            WeaponStation best = null;
            float bestOpportunity = 0f;
            foreach (WeaponStation station in aircraft.weaponStations)
            {
                if (station == null || station.Cargo || station.WeaponInfo == null || station.Ammo <= 0)
                    continue;
                if (!WeaponGateService.IsAllowed(aircraft, station))
                    continue; // гейт главнее явного приказа (DESIGN §2.11)
                if (station.WeaponInfo.energy && aircraft.GetPowerSupply().GetCharge() < 0.6f)
                    continue; // как нативный ChooseHQTarget
                OpportunityThreat ot = CombatAI.AnalyzeTarget(station, aircraft, tracking, 0f, -1f, 100f);
                if (ot.opportunity > bestOpportunity)
                {
                    bestOpportunity = ot.opportunity;
                    best = station;
                }
            }
            if (best == null)
                return; // нет средств по этому классу цели — пусть натив разбирается сам

            // Перенос книги нативной оценки на нашу цель (как AssessHQTarget у оригинала).
            currentTargetField?.SetValue(instance, target);
            weaponInfoField?.SetValue(instance, best.WeaponInfo);
            aircraft.weaponManager.currentWeaponStation = best;
            aircraft.weaponManager.ClearTargetList();
            aircraft.weaponManager.AddTargetList(target);
            if (PilotField?.GetValue(instance) is Pilot pilot)
                pilot.flightInfo.EnemyContact = true;
        }

        /// <summary>Атака-движение — конус: цель вне конуса вектора приказа снимается,
        /// AI переищет цель в конусе на следующей оценке (стрельба «на проходе», без
        /// погони за целью позади курса).</summary>
        internal static void VetoOutsideCone(FieldInfo currentTargetField, FieldInfo weaponInfoField,
            object instance, Aircraft aircraft, AircraftOrders.OrderSegment seg)
        {
            Unit current = currentTargetField?.GetValue(instance) as Unit;
            if (current == null || current.disabled)
                return;
            Vector3 heading = AircraftOrders.SegmentHeading(seg, aircraft.transform.position);
            if (heading == Vector3.zero)
                return;
            Vector3 toTarget = current.transform.position - aircraft.transform.position;
            if (Util.WithinCone(heading, toTarget, Settings.AttackMoveConeDeg))
                return;
            currentTargetField?.SetValue(instance, null);
            weaponInfoField?.SetValue(instance, null);
            aircraft.weaponManager.ClearTargetList();
        }

        // ---- распределение целей в конусной атаке/патруле (DESIGN §2.11a) ----

        /// <summary>Реестр занятости целей: итоговая цель каждого нашего борта после
        /// гейтов/пиннинга/распределения. Пишется постфиксами при каждой оценке целей
        /// (раз в 5 с на борт), переживает сцену до ResetClaims (sceneLoaded), мёртвые
        /// борта вычищаются лениво при записи (Unity fake-null, §7.14).</summary>
        private static readonly Dictionary<Unit, Unit> Claims = new Dictionary<Unit, Unit>();

        private static readonly List<Unit> _claimsDead = new List<Unit>();

        /// <summary>Сброс реестра занятости (sceneLoaded — инвариант №11).</summary>
        internal static void ResetClaims()
        {
            Claims.Clear();
            _claimsDead.Clear();
        }

        /// <summary>Записать итоговую цель борта (null = не воюет); заодно вычистить
        /// мёртвые борта из реестра.</summary>
        internal static void RegisterClaim(Aircraft aircraft, Unit target)
        {
            if (Claims.Count > 0)
            {
                _claimsDead.Clear();
                foreach (KeyValuePair<Unit, Unit> kv in Claims)
                    if (kv.Key == null || kv.Key.disabled) // Unity fake-null
                        _claimsDead.Add(kv.Key);
                for (int i = 0; i < _claimsDead.Count; i++)
                    Claims.Remove(_claimsDead[i]);
            }
            Claims[aircraft] = target;
        }

        /// <summary>Сколько живых бортов (кроме except) сейчас держат цель.</summary>
        private static int ClaimsOn(Unit target, Aircraft except)
        {
            int n = 0;
            foreach (KeyValuePair<Unit, Unit> kv in Claims)
                if (kv.Key != null && kv.Key != except && !kv.Key.disabled &&
                    kv.Value != null && !kv.Value.disabled && kv.Value == target)
                    n++;
            return n;
        }

        /// <summary>Живые борта с живой целью — конкуренты за распределение.</summary>
        private static int EngagedCount()
        {
            int n = 0;
            foreach (KeyValuePair<Unit, Unit> kv in Claims)
                if (kv.Key != null && !kv.Key.disabled &&
                    kv.Value != null && !kv.Value.disabled)
                    n++;
            return n;
        }

        /// <summary>Распределение целей в атаке-движении/патруле («насколько хватает
        /// ЛА в группе»): нативный ChooseHQTarget ранжирует цели
        /// opportunity×(1+угроза)/дистанция — у плотной группы бортов скоры почти
        /// совпадают и вся пачка фокусит одну цель, пока остальные враги стоят
        /// нетронутыми (у наземки каждая турель пинает ближайшую свою, поэтому
        /// «давка» видна только у авиации). Правило: борт ДЕРЖИТ текущую цель, пока
        /// без него на ней меньше бортов, чем ёмкость ceil(N/M) (N — наши борта
        /// с целями, M — чужие живые цели в конусе приказа; у патруля конуса нет);
        /// «лишний» борт переходит на ЛУЧШУЮ по натив-скору недозанятую цель в
        /// конусе, недозанятых нет — остаётся в фокусе (излишек концентрируется).
        /// Явные цели (Attack-приказ/распределение/залп) бронируются своим пиннингом и в
        /// реестре считаются занятыми. Качелей нет: уходят только «лишние», на чьей
        /// цели хватает бортов и без них — остаются ровно первые по ёмкости.</summary>
        internal static void DistributeTargets(FieldInfo currentTargetField, FieldInfo weaponInfoField,
            object instance, Aircraft aircraft, AircraftOrders.OrderSegment seg, bool cone)
        {
            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null || hq.trackingDatabase.Count == 0)
                return;
            Unit current = currentTargetField?.GetValue(instance) as Unit;

            Vector3 pos = aircraft.transform.position;
            Vector3 heading = cone ? AircraftOrders.SegmentHeading(seg, pos) : Vector3.zero;
            bool hasHeading = heading != Vector3.zero;
            var candidates = new List<Unit>(hq.trackingDatabase.Count);
            foreach (KeyValuePair<PersistentID, TrackingInfo> item in hq.trackingDatabase)
            {
                if (!item.Value.TryGetUnit(out Unit unit) || unit == null || unit.disabled ||
                    unit.NetworkHQ == null || unit.NetworkHQ == hq)
                    continue;
                if (cone && hasHeading &&
                    !Util.WithinCone(heading, unit.transform.position - pos, Settings.AttackMoveConeDeg))
                    continue; // атака-движение: только цели в секторе вектора приказа
                candidates.Add(unit);
            }
            int m = candidates.Count;
            if (m == 0)
                return;

            int n = EngagedCount() + (current == null || current.disabled ? 1 : 0);
            int capacity = System.Math.Max(1, (n + m - 1) / m); // ceil: «насколько хватает»
            if (current != null && !current.disabled && ClaimsOn(current, aircraft) < capacity)
                return; // на цели хватает бортов и без нас — держим свою

            // Перебор «лишнего»/бесцельного борта: лучший скор среди недозанятых.
            // Формула — нативная ChooseHQTarget (opportunity×(1+угроза)/дистанция,
            // штраф ×0.5 вне 1.2×дальности станции), чтобы распределение не тянуло
            // борт на заведомо худшие цели.
            Unit best = null;
            float bestScore = 0f;
            for (int i = 0; i < m; i++)
            {
                Unit cand = candidates[i];
                if (cand == current || ClaimsOn(cand, aircraft) >= capacity)
                    continue; // цель уже укомплектована
                TrackingInfo tracking = hq.GetTrackingData(cand.persistentID);
                if (tracking == null || !hq.IsTargetPositionAccurate(cand, 1000f))
                    continue; // как нативный ChooseHQTarget
                float range = FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition());
                float score = 0f;
                foreach (WeaponStation station in aircraft.weaponStations)
                {
                    if (station == null || station.Cargo || station.WeaponInfo == null || station.Ammo <= 0)
                        continue;
                    if (!WeaponGateService.IsAllowed(aircraft, station))
                        continue; // гейт вооружения главнее (DESIGN §2.11)
                    if (station.WeaponInfo.energy && aircraft.GetPowerSupply().GetCharge() < 0.6f)
                        continue; // как нативный ChooseHQTarget
                    OpportunityThreat ot = CombatAI.AnalyzeTarget(station, aircraft, tracking, 0f, range, 100f);
                    if (ot.opportunity <= 0f)
                        continue; // станция не бьёт этот класс цели
                    float s = ot.opportunity * (1f + ot.threat) / range;
                    if (range > station.WeaponInfo.targetRequirements.maxRange * 1.2f)
                        s *= 0.5f;
                    if (s > score)
                        score = s;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = cand;
                }
            }
            if (best != null)
                PinTarget(currentTargetField, weaponInfoField, instance, aircraft, best);
        }
    }

    /// <summary>Вертолётный близнец пиннинга: AIHeloCombatState.AssessHQTarget без
    /// allowTargetAssessment/tracking-полей (guard — залп и уход в транспортный режим);
    /// ядро выбора/вето общее с самолётным.</summary>
    [HarmonyPatch(typeof(AIHeloCombatState), "AssessHQTargets")]
    internal static class HeloTargetPinPatch
    {
        private static readonly FieldInfo AircraftField =
            AccessTools.Field(typeof(PilotBaseState), "aircraft");

        private static readonly FieldInfo PilotField =
            AccessTools.Field(typeof(PilotBaseState), "pilot");

        private static readonly FieldInfo CurrentTargetField =
            AccessTools.Field(typeof(AIHeloCombatState), "currentTarget");

        private static readonly FieldInfo CurrentWeaponInfoField =
            AccessTools.Field(typeof(AIHeloCombatState), "currentWeaponInfo");

        public static bool FieldsOk =>
            AircraftField != null && PilotField != null && CurrentTargetField != null &&
            CurrentWeaponInfoField != null;

        private static void Postfix(AIHeloCombatState __instance)
        {
            Aircraft aircraft = AircraftField?.GetValue(__instance) as Aircraft;
            if (aircraft == null || aircraft.NetworkHQ == null || aircraft.weaponManager == null)
                return;
            void Register() =>
                AircraftTargetPinPatch.RegisterClaim(aircraft, CurrentTargetField?.GetValue(__instance) as Unit);

            // Нативный guard оценки: залп в процессе (цель не меняется — в реестре
            // занятости борт остаётся при своей); плюс транспортный режим — борт
            // не воюет, заявка снимается.
            if (aircraft.weaponManager.currentWeaponStation != null &&
                aircraft.weaponManager.currentWeaponStation.SalvoInProgress)
            {
                Register();
                return;
            }
            if (PilotField?.GetValue(__instance) is Pilot pilot && pilot.currentState != __instance)
            {
                AircraftTargetPinPatch.RegisterClaim(aircraft, null);
                return;
            }

            // Гейт вооружения — как у самолётного близнеца (до выхода «нет приказа»).
            AircraftTargetPinPatch.ApplyWeaponGates(CurrentTargetField, CurrentWeaponInfoField,
                __instance, aircraft);

            AircraftOrders.OrderSegment seg =
                AircraftOrders.Step(aircraft, aircraft.transform.position);
            if (seg == null)
            {
                Register();
                return;
            }

            if (seg.Mode == AirOrderMode.Attack)
                AircraftTargetPinPatch.PinTarget(CurrentTargetField, CurrentWeaponInfoField,
                    __instance, aircraft, seg.Target);
            else if (seg.Mode == AirOrderMode.AttackMove)
            {
                AircraftTargetPinPatch.VetoOutsideCone(CurrentTargetField, CurrentWeaponInfoField,
                    __instance, aircraft, seg);
                AircraftTargetPinPatch.DistributeTargets(CurrentTargetField, CurrentWeaponInfoField,
                    __instance, aircraft, seg, cone: true);
            }
            else if (seg.Mode == AirOrderMode.Patrol)
            {
                AircraftTargetPinPatch.DistributeTargets(CurrentTargetField, CurrentWeaponInfoField,
                    __instance, aircraft, seg, cone: false);
            }
            Register();
        }
    }
}
