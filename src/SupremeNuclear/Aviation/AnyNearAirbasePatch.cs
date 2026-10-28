using System.Collections;
using System.Reflection;
using Mirage;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Постфикс FactionHQ.AnyNearAirbase: игра отдаёт ПЕРВЫЙ аэродром
    /// из НЕСОРТИРОВАННОГО списка, чей радиус (SavedAirbase.CaptureRange) накрывает
    /// точку. Нативный аэропорт регистрируется раньше нашей построенной ВПП — в зоне
    /// перекрытия радиусов борта из наших ангаров уезжали рулить к соседу (плейтест
    /// v0.92.0: «едут на ближайший соседний аэропорт»). Заменяем результат на
    /// БЛИЖАЙШИЙ К ПОЛОСЕ из накрывающих — метрика как у NearestOwnAirbase:
    /// дистанция до ближайшей полосы (отрезок торцов), фолбэк — центр. Все вызовы
    /// игры (руление к взлёту, RTB/посадка, оверлей, цели, высадка пилота) передают
    /// позицию борта и ждут «ближний аэродром» — «первый из списка» там произволен,
    /// уточнение безопасно для каждого. Постфикс: оригинал не нашёл — не трогаем.
    /// Реестр патчей — ARCHITECTURE §6.</summary>
    [HarmonyLib.HarmonyPatch(typeof(FactionHQ), nameof(FactionHQ.AnyNearAirbase))]
    internal static class AnyNearAirbasePatch
    {
        private static readonly FieldInfo AirbasesField =
            HarmonyLib.AccessTools.Field(typeof(FactionHQ), "airbasesUnsorted");

        private static void Postfix(FactionHQ __instance, Vector3 fromPosition,
            ref Airbase airbase)
        {
            if (airbase == null || AirbasesField == null ||
                !(AirbasesField.GetValue(__instance) is IEnumerable list))
                return; // оригинал ничего не нашёл / поле переименовали — как есть
            Airbase best = null;
            float bestSq = float.MaxValue;
            foreach (object item in list)
            {
                if (!(item is NetworkBehaviorSyncvar<Airbase> slot))
                    continue;
                Airbase ab = slot.Value;
                if (ab == null || ab.center == null)
                    continue; // Unity fake-null: дохлые ссылки из синк-листа
                if (!FastMath.InRange(fromPosition, ab.center.position, ab.GetRadius()))
                    continue; // не накрывает — не кандидат (как в оригинале)
                float distSq = SqXZ(ab.center.position, fromPosition);
                Airbase.Runway[] runways = ab.runways;
                if (runways != null)
                    foreach (Airbase.Runway runway in runways)
                    {
                        if (runway == null || runway.Start == null || runway.End == null)
                            continue;
                        float rSq = Util.DistSqToSegmentXZ(XZ(fromPosition),
                            XZ(runway.Start.position), XZ(runway.End.position));
                        if (rSq < distSq)
                            distSq = rSq;
                    }
                if (distSq < bestSq)
                {
                    bestSq = distSq;
                    best = ab;
                }
            }
            if (best != null)
                airbase = best;
        }

        private static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);

        private static float SqXZ(Vector3 a, Vector3 b) =>
            new Vector2(a.x - b.x, a.z - b.z).sqrMagnitude;
    }
}
