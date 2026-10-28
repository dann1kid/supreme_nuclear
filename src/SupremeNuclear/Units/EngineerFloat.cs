using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Плавучий инженер (v0.92): игра ТОПИТ наземку под водой
    /// (GroundVehicle-джоба: underwater → Networkdisabled), а её водитель
    /// вообще не заводит машину в воду (GetSteerpoint останавливает у кромки).
    /// Поэтому плав-контур включается по ВОДНОЙ ЦЕЛИ ПРИКАЗА ещё на суше:
    /// горизонтальная тяга стаскивает инженера с кромки в воду, PD-пружина
    /// держит корпус на клиренсе EngineerFloatHeightM (ниже клиренса — только
    /// тогда пружина/выравнивание, чтобы не мешать колёсам на суше), тяга с
    /// потолком EngineerWaterSpeed ведёт к головной точке GroundOrders (колёса
    /// в воде сцепления не дают). «Барахтается» — курс разворачивают колёса,
    /// в воде они висят. Хост-only: физика юнитов — серверная.</summary>
    public class EngineerFloat : MonoBehaviour
    {
        private readonly HashSet<Unit> _active = new HashSet<Unit>();
        private readonly HashSet<Unit> _logged = new HashSet<Unit>(); // был в контуре — для лога
        private float _nextScan;

        private const float SpringK = 12f;   // жёсткость держателя клиренса, 1/с²
        private const float SpringD = 4f;    // демпфер вертикали, 1/с
        private const float ThrustMss = 6f;  // горизонтальная тяга, м/с²
        private const float ShoveDv = 7f;    // береговой импульс: Δv раз в ShoveSec, м/с
        private const float ShoveSec = 1.5f; // период береговых импульсов, с
        private const float BrakeK = 1.5f;   // «вязкость» воды у цели, 1/с
        private const float LevelK = 8f;     // выравнивание крена, 1/с²
        private const float LevelD = 2f;     // демпфер вращения, 1/с
        private const float WaterTargetEps = 1f; // цель «в воде»: y < seaY + это, м

        private static readonly Dictionary<Unit, float> StallLogAt =
            new Dictionary<Unit, float>(); // столл-диагностика пловца, раз в 5 с
        private static readonly Dictionary<Unit, float> ShoveAt =
            new Dictionary<Unit, float>(); // момент последнего берегового импульса

        private static bool IsHost
        {
            get
            {
                var net = NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        private void FixedUpdate()
        {
            if (!GameApi.InMission || !IsHost)
                return;
            // Перечень пловцов — раз в 0.5 с; сами силы — каждый физический тик.
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.5f;
                _active.Clear();
                foreach (Unit unit in UnitRegistry.allUnits)
                {
                    if (unit == null || unit.disabled || !(unit is GroundVehicle) ||
                        !GameApi.IsEngineer(unit))
                        continue;
                    Vector3 pos = unit.transform.position;
                    bool inWater = pos.y < Datum.LocalSeaY + Settings.EngineerFloatHeightM;
                    bool waterTarget = HeadTarget(unit, out Vector3 head) &&
                                       head.y < Datum.LocalSeaY + WaterTargetEps;
                    if (inWater || waterTarget)
                        _active.Add(unit);
                }
                // Лог входа/выхода из плав-контура — плейтест-диагностика
                // («не понятно, есть ли ховер»): в BepInEx-логе видно всё.
                foreach (Unit unit in new List<Unit>(_logged))
                    if (!_active.Contains(unit) || unit == null || unit.disabled)
                    {
                        _logged.Remove(unit);
                        StallLogAt.Remove(unit);
                        ShoveAt.Remove(unit);
                        if (unit != null && !unit.disabled)
                            Log.Info("Float: инженер вышел на сушу — " + unit.NetworkunitName);
                    }
                foreach (Unit unit in _active)
                    if (_logged.Add(unit))
                        Log.Info("Float: инженер в плав-контуре (цель в воде/корпус в воде) — " +
                                 unit.NetworkunitName);
                // Столл-диагностика: пловец в контуре, но не сходится — раз в 5 с
                // снимок (дистанция до цели / скорость / погружение / высота).
                foreach (Unit unit in _active)
                {
                    if (unit == null || unit.disabled)
                        continue;
                    if (Time.unscaledTime -
                        (StallLogAt.TryGetValue(unit, out float at) ? at : -99f) < 5f)
                        continue;
                    StallLogAt[unit] = Time.unscaledTime;
                    Vector3 p = unit.transform.position;
                    float dist = HeadTarget(unit, out Vector3 head)
                        ? new Vector2(head.x - p.x, head.z - p.z).magnitude : -1f;
                    Vector3 v = unit.rb != null ? unit.rb.velocity : Vector3.zero;
                    Log.Info($"Float: {unit.NetworkunitName} — до цели {dist:F0} м, " +
                             $"скорость {new Vector2(v.x, v.z).magnitude:F1} м/с, " +
                             $"в воде={p.y < Datum.LocalSeaY + Settings.EngineerFloatHeightM}, " +
                             $"y={p.y:F1} (море {Datum.LocalSeaY:F1})");
                }
            }
            foreach (Unit unit in _active)
                FloatTick(unit);
        }

        /// <summary>Головная точка цепочки инженера (выданная — ещё жива).</summary>
        private static bool HeadTarget(Unit u, out Vector3 head)
        {
            head = default;
            if (!GroundOrders.TryGetQueue(u, out var queue, out int qi) || qi >= queue.Count)
                return false;
            head = queue[qi].ToLocalPosition();
            return true;
        }

        /// <summary>Силы одного пловца: пружина клиренса и выравнивание крена —
        /// ТОЛЬКО когда корпус ниже клиренса (иначе мешаем колёсам на суше);
        /// тяга к головной точке — и по суше (стаскивает в воду), с потолком
        /// скорости; тормоз у цели — только в воде.</summary>
        private static void FloatTick(Unit u)
        {
            Rigidbody rb = u.rb;
            if (rb == null || rb.isKinematic || u.disabled)
                return;
            Vector3 pos = u.transform.position;
            float targetY = Datum.LocalSeaY + Settings.EngineerFloatHeightM;
            bool submerged = pos.y < targetY;
            Vector3 vel = rb.velocity;
            Vector3 force = Vector3.zero;
            if (submerged)
                force += Vector3.up * ((targetY - pos.y) * SpringK - vel.y * SpringD) *
                         rb.mass;
            if (HeadTarget(u, out Vector3 head))
            {
                Vector3 to = head - pos;
                to.y = 0f;
                float dist = to.magnitude;
                Vector3 flat = vel;
                flat.y = 0f;
                if (dist > 6f)
                {
                    // Потолок скорости: на воде — EngineerWaterSpeed (плавно),
                    // на суше выше — въезд в воду накатом не должен отключать тягу.
                    float cap = submerged
                        ? Settings.EngineerWaterSpeed
                        : Mathf.Max(Settings.EngineerWaterSpeed, 12f);
                    if (flat.magnitude < cap)
                        force += to / dist * (ThrustMss * rb.mass);
                    // БЕРЕГОВОЙ ИМПУЛЬС (плейтест v0.95: непрерывной силе тормоза
                    // нативного водителя не проигрывают, грузовик стоит у кромки):
                    // раз в ShoveSec — прямая инъекция скорости (VelocityChange,
                    // масса не в счёте): машину сносит ЮЗОМ в воду, трение колёс
                    // уже не держит; +вверх — переехать бермен пляжа.
                    if (!submerged && flat.magnitude < 1f &&
                        Time.unscaledTime -
                        (ShoveAt.TryGetValue(u, out float shoved) ? shoved : -99f) > ShoveSec)
                    {
                        ShoveAt[u] = Time.unscaledTime;
                        rb.AddForce(to / dist * ShoveDv + Vector3.up * 1f,
                            ForceMode.VelocityChange);
                    }
                }
                else if (submerged)
                    force -= flat * (BrakeK * rb.mass); // встал у точки — вода держит
            }
            rb.AddForce(force);
            if (submerged)
                // Крен: не заваливаться набок; курса нет — колёса в воде висят.
                rb.AddTorque((Vector3.Cross(u.transform.up, Vector3.up) * LevelK -
                              rb.angularVelocity * LevelD) * rb.mass);
        }
    }
}
