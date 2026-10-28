using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Прямой маршрут наземки (SupCom-стиль): нативный Pathfind строит путь
    /// ТОЛЬКО по графу шоссе и едет напрямую лишь когда прямая дистанция короче
    /// расстояния цель→ближайшая дорога (RoadPathfinder.TryPathfind) — на картах с
    /// плотной сеткой юнит заезжает на трассу даже для переезда на 5 м. Guard-Prefix
    /// (§7.3): своему GroundVehicle при включённой настройке кладём ОДНУ прямую
    /// точку — Shortcut, тот же режим, что игра сама включает при NoRoadNearTarget.
    /// Прямая, пересекающая воду, отдаётся нативу: только дорожный граф знает мосты
    /// (а нативный GetSteerpoint сам остановит юнит у берега, если вода всё же
    /// случилась). Авиация на рулёжке (GetTaxiNetwork) и флот пользуются ТЕМ ЖЕ
    /// PathfindingAgent — фильтр `is GroundVehicle` обязателен. Вызывается только
    /// из события SetDestination (натив/наш GroundOrders/сценарный AI), не из тика.</summary>
    [HarmonyPatch(typeof(PathfindingAgent), "Pathfind")]
    internal static class GroundPathfindPatch
    {
        private const int MaxWaterSamples = 24; // потолок сэмплов воды на один приказ (перф)

        private static readonly FieldInfo UnitField = AccessTools.Field(typeof(PathfindingAgent), "unit");

        /// <summary>SelfCheck: поле агент→юнит найдено (иначе фильтр наземки мёртв).</summary>
        public static bool FieldsOk => UnitField != null;

        private static bool Prefix(PathfindingAgent __instance, GlobalPosition targetPos)
        {
            if (!Settings.GroundDirectRouting)
                return true;
            Unit unit = (Unit)UnitField?.GetValue(__instance);
            if (!(unit is GroundVehicle))
                return true; // рулёжка авиации/морские лузы — натив
            if (!GameApi.TryGetLocalHQ(out FactionHQ hq) || !GameApi.IsFriendlyUnit(unit, hq))
                return true; // чужая наземка: сценарный ИИ ездит как ездил
            if (CrossesWater(unit.GlobalPosition(), targetPos))
                return true; // прямая через воду — натив ведёт по дорогам/мостам
            __instance.Shortcut(targetPos);
            return false;
        }

        /// <summary>Есть ли вода на прямой старт→цель: сэмплы рельефа вдоль отрезка
        /// (последний — сама цель). «Мокро» = луч не достал до статики ИЛИ точка ниже
        /// уровня моря — критерий нативной остановки у берега из GetSteerpoint.</summary>
        private static bool CrossesWater(GlobalPosition from, GlobalPosition to)
        {
            Vector3 a = from.ToLocalPosition();
            Vector3 b = to.ToLocalPosition();
            float dist = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            int steps = Util.SegmentSteps(dist, Settings.GroundDirectWaterStepM, MaxWaterSamples);
            for (int i = 1; i <= steps; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, (float)i / steps);
                if (!PathfindingAgent.RaycastTerrain(p, out RaycastHit hit) ||
                    hit.point.y < Datum.LocalSeaY)
                    return true;
            }
            return false;
        }
    }
}
