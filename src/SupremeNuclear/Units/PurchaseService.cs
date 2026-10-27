using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Временная механика покупок за деньги на складе-депо (до фабрик с очередями,
    /// DESIGN 2.3a). Цена — игровая UnitDefinition.value, оплата из factionFunds.</summary>
    public static class PurchaseService
    {
        public static bool IsHost
        {
            get
            {
                var net = NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        public static IEnumerable<VehicleDefinition> Catalog => Encyclopedia.i != null
            ? Encyclopedia.i.vehicles
            : null;

        /// <summary>Выкат борта из резерва ангара (DESIGN §2.11): бесплатно — самолёт уже
        /// наш, сервис (боезапас/баки) оплачен при возврате. Ёмкость ангаров проверяет
        /// игра (нативный спавн с выкатом из дверей); выкаченный борт таксует и взлетает,
        /// дальше — обычные приказы. Только со своего аэродрома: резерв захваченной
        /// базы уходит вместе с ней.</summary>
        public static void DeployReserve(Airbase airbase, AircraftDefinition definition)
        {
            if (!IsHost || airbase == null || definition == null)
                return;
            if (!GameApi.TryGetLocalHQ(out FactionHQ local) || airbase.CurrentHQ != local)
            {
                CommanderHud.Notice("Аэродром не наш — выкат невозможен");
                return;
            }
            if (!airbase.CanSpawnAircraft(definition))
            {
                CommanderHud.Notice("Ангары заняты — выкат невозможен");
                return;
            }
            if (!AirbaseReserve.TryTake(airbase, definition))
                return;
            int livery = definition.aircraftParameters.GetRandomLiveryForFaction(local.faction);
            airbase.TrySpawnAircraft(null, definition, new LiveryKey(livery), new Loadout(), 1f);
            CommanderHud.Notice($"{definition.unitName}: выкат из резерва");
        }
    }
}
