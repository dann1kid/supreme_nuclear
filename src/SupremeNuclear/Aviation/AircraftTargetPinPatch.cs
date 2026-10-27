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
            // Во время залпа/бомбометания/лазерного наведения оценка заблокирована нативно —
            // не вмешиваемся (иначе сломаем идущий пуск).
            if (AllowAssessmentField != null && !(bool)AllowAssessmentField.GetValue(__instance))
                return;
            Aircraft aircraft = AircraftField?.GetValue(__instance) as Aircraft;
            if (aircraft == null || aircraft.NetworkHQ == null || aircraft.weaponManager == null)
                return;
            if (aircraft.weaponManager.currentWeaponStation != null &&
                aircraft.weaponManager.currentWeaponStation.SalvoInProgress)
                return; // зеркалим нативный guard

            // Гейт вооружения (DESIGN §2.11) — до выхода «нет приказа»: выключенный
            // класс не применяется даже свободным бортом (автоогонь по пути).
            ApplyWeaponGates(CurrentTargetField, CurrentWeaponInfoField, __instance, aircraft);

            AircraftOrders.OrderSegment seg =
                AircraftOrders.Step(aircraft, aircraft.transform.position);
            if (seg == null)
                return; // нет приказа — выбор нативный

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
                VetoOutsideCone(CurrentTargetField, CurrentWeaponInfoField, __instance, aircraft, seg);
        }

        // ---- общее ядро пиннинга (используется и вертолётным близнецом ниже) ----

        /// <summary>Гейт вооружения (DESIGN §2.11): натив выбрал станцию выключенного
        /// класса — перевыбор лучшей РАЗРЕШЁННОЙ по той же цели (нативный AnalyzeTarget);
        /// разрешённых средств нет — цель снимается, борт не вступает в бой. Работает
        /// без приказа мода: гейт главнее и для автоогня свободного борта.</summary>
        internal static void ApplyWeaponGates(FieldInfo currentTargetField, FieldInfo weaponInfoField,
            object instance, Aircraft aircraft)
        {
            WeaponClass mask = WeaponGateService.BlockedOf(aircraft);
            if (mask == WeaponClass.None)
                return;
            WeaponStation current = aircraft.weaponManager.currentWeaponStation;
            if (current == null || (mask & Catalog.ClassOf(current.WeaponInfo)) == 0)
                return; // выбранное оружие разрешено — не вмешиваемся

            WeaponStation best = null;
            float bestOpportunity = 0f;
            Unit target = currentTargetField?.GetValue(instance) as Unit;
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
            // Нативный guard оценки: залп в процессе; плюс транспортный режим — если
            // оригинал увёл борт в AIHeloTransportState, не вмешиваемся.
            if (aircraft.weaponManager.currentWeaponStation != null &&
                aircraft.weaponManager.currentWeaponStation.SalvoInProgress)
                return;
            if (PilotField?.GetValue(__instance) is Pilot pilot && pilot.currentState != __instance)
                return;

            // Гейт вооружения — как у самолётного близнеца (до выхода «нет приказа»).
            AircraftTargetPinPatch.ApplyWeaponGates(CurrentTargetField, CurrentWeaponInfoField,
                __instance, aircraft);

            AircraftOrders.OrderSegment seg =
                AircraftOrders.Step(aircraft, aircraft.transform.position);
            if (seg == null)
                return;

            if (seg.Mode == AirOrderMode.Attack)
                AircraftTargetPinPatch.PinTarget(CurrentTargetField, CurrentWeaponInfoField,
                    __instance, aircraft, seg.Target);
            else if (seg.Mode == AirOrderMode.AttackMove)
                AircraftTargetPinPatch.VetoOutsideCone(CurrentTargetField, CurrentWeaponInfoField,
                    __instance, aircraft, seg);
        }
    }
}
