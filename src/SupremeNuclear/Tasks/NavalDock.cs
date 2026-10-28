using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Спуск кораблей с верфи (v0.92): кольцевой поиск свободной воды от
    /// платформы (глубина лучом до дна, габарит по углам, занятость CheckBox'ом
    /// по статике и кораблям) + нативный Spawner.SpawnShip. Хост-only; схема
    /// валидации переизобретена по мотивам Ground Control (код не копируется).
    /// Ралли-маршрут выданной позиции ведёт ProductionTasks (ссылку на корабль
    /// спавнер отдаёт — искать по реестру, как у депо, не нужно).</summary>
    internal static class NavalDock
    {
        private const int Directions = 16; // кандидатов на кольцо

        /// <summary>Спустить корабль у верфи: первая валидная точка воды кольцами
        /// от платформы (дальше Settings.DockSpawnSearchM не ищем). false — воды
        /// нет (оплаченная позиция подождёт и повторит, как занятой ангар).</summary>
        public static bool TryLaunch(Building dock, ShipDefinition def, FactionHQ hq, out Ship ship)
        {
            ship = null;
            Spawner spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null || def == null || def.unitPrefab == null || hq == null)
                return false;
            if (!TryFindWaterSpot(dock, def, out GlobalPosition pos, out Quaternion rot))
                return false;
            try
            {
                ship = spawner.SpawnShip(def.unitPrefab, pos, rot, hq,
                    "SN-SHIP-" + System.Guid.NewGuid().ToString("N").Substring(0, 6), 1f, false);
            }
            catch (System.Exception e)
            {
                Log.Error("Верфь: спавн не удался — " + e.GetType().Name + ": " + e.Message);
                return false;
            }
            return ship != null;
        }

        /// <summary>Кольца кандидатов вокруг платформы: старт — от кромки
        /// (полудиагональ верфи + полкорабля), шаг — ширина корабля.</summary>
        private static bool TryFindWaterSpot(Building dock, ShipDefinition def,
            out GlobalPosition pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            Vector3 center = dock.transform.position;
            float dockDiag = dock.definition != null
                ? Mathf.Sqrt(dock.definition.length * dock.definition.length +
                             dock.definition.width * dock.definition.width)
                : 60f;
            float inner = dockDiag * 0.5f + def.length * 0.5f + 10f;
            float step = Mathf.Max(20f, def.width);
            foreach (Vector2 offset in Util.RingPoints(inner, step, Directions,
                         Settings.DockSpawnSearchM))
            {
                Vector3 dir = new Vector3(offset.x, 0f, offset.y);
                Vector3 local = center + dir;
                Quaternion heading = Quaternion.LookRotation(dir.sqrMagnitude > 1f
                    ? dir.normalized : Vector3.forward, Vector3.up);
                if (WaterIsClear(local, heading, def))
                {
                    local.y = Datum.LocalSeaY + def.spawnOffset.y;
                    pos = local.ToGlobalPosition();
                    rot = heading;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Вода в точке пригодна: под килем достаточно глубины (луч до
        /// дна), углы габарита не цепляют мель, бокс корпуса свободен от статики
        /// и других кораблей.</summary>
        private static bool WaterIsClear(Vector3 spot, Quaternion heading, ShipDefinition def)
        {
            float minDepth = Mathf.Max(4f, def.height * 0.15f);
            Vector3 down = spot;
            down.y = Datum.LocalSeaY + 5f;
            if (Physics.Raycast(down, Vector3.down, out RaycastHit hit, 2000f,
                    PhysicsLayers.StaticsMask) &&
                Datum.LocalSeaY - hit.point.y < minDepth)
                return false;
            // Углы габарита (нос/корма × борта, с запасом 20%): мелководье у ANY
            // угла — корабль сядет брюхом на мель при выходе.
            Vector3 fwd = heading * Vector3.forward;
            Vector3 right = heading * Vector3.right;
            float halfL = def.length * 0.5f + Mathf.Max(10f, def.length * 0.2f);
            float halfW = def.width * 0.5f + Mathf.Max(10f, def.length * 0.2f);
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    if (i == 0 && j == 0)
                        continue;
                    Vector3 corner = spot + fwd * (i * halfL) + right * (j * halfW);
                    if (GameApi.GroundYNoUnits(corner.x, corner.z) >
                        Datum.LocalSeaY - minDepth * 0.5f)
                        return false;
                }
            Vector3 boxCenter = spot + Vector3.up * Mathf.Max(4f, def.height * 0.3f);
            Vector3 half = new Vector3(
                Mathf.Max(15f, def.width * 0.65f),
                Mathf.Max(6f, def.height * 0.35f),
                Mathf.Max(25f, def.length * 0.65f));
            return !Physics.CheckBox(boxCenter, half, heading,
                PhysicsLayers.StaticsMask | PhysicsLayers.ShipsMask);
        }
    }
}
