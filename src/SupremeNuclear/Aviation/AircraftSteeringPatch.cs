using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Первый Harmony-патч мода. Модель AircraftWaypoints: для борта с назначенной точкой
    /// подменяем штатный тик боевого режима — пишем destination, полный газ, рулит
    /// игровой AutoAim. Пока план жив — таймер «без цели» обнулён: возврат на базу
    /// происходит только по топливу/боеприпасам (SortieService), а не по 15-секундному
    /// простою нативного AI. Борт без приказа — оригинальное поведение.
    /// Атака (DESIGN §2.11): цель в зоне боя (даталинк + дальность вооружения) —
    /// нативный бой (цель запиннена AircraftTargetPinPatch); иначе мод ведёт подлёт
    /// к известной позиции цели либо к якорю клика. RTB — мод ведёт к аэродрому,
    /// у земли борт принимает нативная посадка (SortieService).
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
                    out GlobalPosition destination, out AirOrderMode mode, out bool followTerrain,
                    out Unit attackTarget, out float altitudeAgl, out SpeedMode speed))
                return true; // приказа нет — штатное поведение

            // Пока план жив — таймер «без цели» не тикает (иначе AI уйдёт на возврат
            // через 15 с даже посреди подлёта/атаки-движения).
            TimeWithoutTargetField?.SetValue(__instance, 0f);

            // Авто-защита «Авто» (DESIGN §2.11): ракетное предупреждение — руление и
            // контрмеры берёт нативный AI (манёвр увода + ловушки/джаммер по типу ГСН);
            // после ухода угрозы план продолжится этим же префиксом.
            if (mode != AirOrderMode.Rtb && DefenseService.ShouldEvadeNative(aircraft))
                return true;

            if (mode == AirOrderMode.Attack)
            {
                // В зоне боя — руление и стрельба нативные (цель уже запиннена);
                // иначе мод ведёт подлёт к цели/якорю, даталинк наполнится по пути
                // собственными сенсорами борта.
                if (AircraftOrders.AttackEngaged(aircraft, attackTarget))
                    return true;
            }
            else if (mode == AirOrderMode.AttackMove &&
                     AircraftOrders.EnemyNearby(aircraft, aircraft.NetworkHQ))
            {
                return true; // attack-move: рядом враг, руление у боевого AI, после боя продолжим план
            }
            else if (mode == AirOrderMode.Escort &&
                     AircraftOrders.EnemyNearby(aircraft, aircraft.NetworkHQ,
                         AircraftOrders.EngageRadius(aircraft)))
            {
                // Сопровождение: враг В ДАЛЬНОСТИ ВООРУЖЕНИЯ — нативный бой (борта не
                // уводит за собой через полкарты, только то, что достаёт отсюда);
                // после боя следование продолжится этим же префиксом.
                return true;
            }

            // Залп (Salvo): нативный бой НЕ включаем — рулит мод, пуски делает SalvoService.
            // Режим «Ловушки»: маршрут держим, контрмеры сбрасываем по интервалу.
            DefenseService.TickFlares(aircraft);

            DestinationField?.SetValue(__instance, destination);
            if (InputsField?.GetValue(__instance) is ControlInputs inputs)
                inputs.throttle = AircraftOrders.ThrottleOf(aircraft, speed); // режим скорости сегмента

            if (aircraft.autopilot != null)
            {
                // Высота: огибающие профили (Сверхнизко/Низко) — AGL, игровой
                // TerrainWaypoint кладёт altitudeHold НАД рельефом впереди (мировая
                // Y точки годилась только на плоской карте); «Средне/Высоко» —
                // мировая Y точки, как раньше.
                aircraft.autopilot.AutoAim(
                    destination,
                    aimVelocity: true,
                    ignoreCollisions: false,
                    runwayAlign: false,
                    effort: 0.75f,
                    bankAllowed: 120f,
                    followTerrain: followTerrain,
                    altitudeHold: followTerrain ? altitudeAgl : destination.ToLocalPosition().y,
                    targetVelocity: Vector3.zero);
            }
            return false;
        }
    }
}
