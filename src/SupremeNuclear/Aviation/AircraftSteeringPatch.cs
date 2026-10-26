using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Первый Harmony-патч мода. Модель AircraftWaypoints: для борта с назначенной точкой
    /// подменяем штатный тик боевого режима — пишем destination, обнуляем «таймер без цели»
    /// (иначе AI уйдёт на возврат через 15 с), полный газ и рулит игровой AutoAim.
    /// Борт без приказа — оригинальное поведение без единого изменения.
    /// (В Harmony этой версии нет FieldRef-делегатов — кэшированные FieldInfo.)
    /// </summary>
    [HarmonyPatch(typeof(AIPilotCombatModes), "FixedUpdateState")]
    internal static class AircraftSteeringPatch
    {
        private static readonly FieldInfo DestinationField =
            AccessTools.Field(typeof(PilotBaseState), "destination");

        private static readonly FieldInfo AircraftField =
            AccessTools.Field(typeof(PilotBaseState), "aircraft");

        private static readonly FieldInfo InputsField =
            AccessTools.Field(typeof(PilotBaseState), "controlInputs");

        private static readonly FieldInfo TimeWithoutTargetField =
            AccessTools.Field(typeof(AIPilotCombatModes), "timeWithoutTarget");

        private static bool Prefix(AIPilotCombatModes __instance)
        {
            Aircraft aircraft = AircraftField?.GetValue(__instance) as Aircraft;
            if (aircraft == null)
                return true;
            if (!AircraftOrders.TryGetDestination(aircraft, aircraft.transform.position,
                    out GlobalPosition destination, out AirOrderMode mode, out bool followTerrain))
                return true; // приказа нет — штатное поведение
            if (mode == AirOrderMode.Attack)
                return true; // атака: штатный боевой AI сам выбирает цель и стреляет
            if (mode == AirOrderMode.AttackMove &&
                AircraftOrders.EnemyNearby(aircraft, aircraft.NetworkHQ))
                return true; // attack-move: рядом враг, руление у боевого AI, после боя продолжим план

            DestinationField?.SetValue(__instance, destination);
            if (TimeWithoutTargetField != null)
                TimeWithoutTargetField.SetValue(__instance, 0f);
            if (InputsField?.GetValue(__instance) is ControlInputs inputs)
                inputs.throttle = 1f;

            if (aircraft.autopilot != null)
            {
                aircraft.autopilot.AutoAim(
                    destination,
                    aimVelocity: true,
                    ignoreCollisions: false,
                    runwayAlign: false,
                    effort: 0.75f,
                    bankAllowed: 120f,
                    followTerrain: followTerrain,
                    altitudeHold: destination.ToLocalPosition().y,
                    targetVelocity: Vector3.zero);
            }
            return false;
        }
    }
}
