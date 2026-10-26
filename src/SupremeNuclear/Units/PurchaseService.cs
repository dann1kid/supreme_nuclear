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
    }
}
