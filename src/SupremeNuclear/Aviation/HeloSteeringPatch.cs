using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Четвёртый патч мода — вертолётный близнец AircraftSteeringPatch (реестр — §6).
    /// У вертолётов/тильтвингов свой боевой AI (AIHeloCombatState) и своя перегрузка
    /// автопилота: AutoAim(destination, altitudeHold AGL, …) — самолётная 9-аргументная
    /// версия не перекрыта AutopilotHelo и для вертолёта неверна. Логика та же (DESIGN
    /// §2.11): план есть → таймер «без цели» обнулён (иначе 15 с → автопосадка), атака в
    /// зоне боя — нативный бой (цель запиннена HeloTargetPinPatch), иначе мод ведёт подлёт
    /// к цели/якорю; attack-move рядом с врагом — нативное руление. Без приказа — оригинал.
    /// </summary>
    [HarmonyPatch(typeof(AIHeloCombatState), "FixedUpdateState")]
    internal static class HeloSteeringPatch
    {
        private static readonly FieldInfo DestinationField =
            AccessTools.Field(typeof(PilotBaseState), "destination");

        private static readonly FieldInfo AircraftField =
            AccessTools.Field(typeof(PilotBaseState), "aircraft");

        private static readonly FieldInfo TimeWithoutTargetField =
            AccessTools.Field(typeof(AIHeloCombatState), "timeWithoutTarget");

        // Сеттеры скомпилированы (Util.FieldSetter): без боксинга структур в
        // физическом тике (как у самолётного патча №1).
        private static readonly Action<object, GlobalPosition> SetDestination =
            Util.FieldSetter<GlobalPosition>(DestinationField);

        private static readonly Action<object, float> SetTimeWithoutTarget =
            Util.FieldSetter<float>(TimeWithoutTargetField);

        /// <summary>Все reflection-поля на месте (проверяется в SelfCheck).</summary>
        public static bool FieldsOk =>
            DestinationField != null && AircraftField != null && TimeWithoutTargetField != null;

        private static bool Prefix(AIHeloCombatState __instance)
        {
            Aircraft aircraft = AircraftField?.GetValue(__instance) as Aircraft;
            if (aircraft == null)
                return true;
            if (!AircraftOrders.TryGetDestination(aircraft, aircraft.transform.position,
                    out GlobalPosition destination, out AirOrderMode mode, out bool followTerrain,
                    out Unit attackTarget, out float altitudeAgl, out _))
                return true; // приказа нет — штатное поведение

            // Пока план жив — таймер «без цели» не тикает (иначе вертолёт уйдёт в
            // автопосадку через 15 с возле базы).
            SetTimeWithoutTarget?.Invoke(__instance, 0f);

            // Авто-защита «Авто»: предупреждение — нативный EvadeMissiles вертолёта
            // (нотчинг + снижение + defensive jamming); после угрозы план продолжится.
            if (mode != AirOrderMode.Rtb && DefenseService.ShouldEvadeNative(aircraft))
                return true;

            if (mode == AirOrderMode.Attack)
            {
                // В зоне боя — нативный бой; иначе мод ведёт подлёт (сенсоры по пути
                // наполнят даталинк).
                if (AircraftOrders.AttackEngaged(aircraft, attackTarget))
                    return true;
            }
            else if (mode == AirOrderMode.AttackMove &&
                     AircraftOrders.EnemyNearby(aircraft, aircraft.NetworkHQ))
            {
                return true;
            }
            else if (mode == AirOrderMode.Escort &&
                     AircraftOrders.EnemyNearby(aircraft, aircraft.NetworkHQ,
                         AircraftOrders.EngageRadius(aircraft)))
            {
                // Сопровождение: враг в дальности вооружения — нативный бой, не уводим
                // борт от цели дальше выстрела (зеркально самолётному патчу №1).
                return true;
            }

            // Залп (Salvo): нативный бой НЕ включаем — рулит мод, пуски делает SalvoService.
            // Режим «Ловушки»: маршрут держим, контрмеры сбрасываем по интервалу.
            DefenseService.TickFlares(aircraft);

            SetDestination?.Invoke(__instance, destination);
            if (aircraft.autopilot != null)
            {
                // Вертолётная перегрузка: altitudeHold — ВЫСОТА НАД РЕЛЬЕФОМ (AGL
                // профиля сегмента), throttle не трогаем (hover-контур ведёт газ).
                // followTerrain для вертолётов ВСЕГДА true (DESIGN §2.11a, как у
                // нативного AIHeloCombatState): при false автопилот считает высоту
                // как «Y борта − Y точки − altitudeHold», а наши точки уже несут
                // AGL в мировой Y — профиль вычитался бы ДВАЖДЫ, борт полез к
                // двойной высоте, коллектив охотился за ней и болтал борт (v0.92).
                aircraft.autopilot.AutoAim(destination, altitudeAgl,
                    Vector3.zero, Vector3.zero, true);
            }
            return false;
        }
    }
}
