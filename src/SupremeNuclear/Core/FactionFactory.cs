using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Рантайм-фракции N армий лобби v3: у игры всего два ассета фракций
    /// (Boscali/Primeva), лобби же — стол до 4 армий. FactionHQ.faction —
    /// ПЛОСКОЕ поле scene-объекта спец-карты: выставляется при детерминированной
    /// сборке на каждой стороне отдельно, сетью не сериализуется (WriteFaction
    /// по индексу Encyclopedia в наших путях не участвует) — рантайм-ассет
    /// безопасен. Имена детерминированы (ArmyName) — обе стороны создают
    /// одинаковые ассеты в одинаковом порядке. Кэш переживает сцены: карта
    /// держит ссылки на эти же объекты (реестр состояния — ARCHITECTURE §4).
    /// </summary>
    internal static class FactionFactory
    {
        /// <summary>Детерминированное имя армии по её id (совпадает на всех пирах).</summary>
        public static string ArmyName(int armyId) => "SN Армия " + (armyId + 1);

        private static readonly Dictionary<string, Faction> Cache = new Dictionary<string, Faction>();

        /// <summary>Палитра армий (SupCom-стол: цвета различимы на миникарте).</summary>
        internal static readonly Color[] Palette =
        {
            new Color(0.20f, 0.55f, 0.95f), // 1 — синяя
            new Color(0.90f, 0.30f, 0.25f), // 2 — красная
            new Color(0.95f, 0.70f, 0.15f), // 3 — жёлтая
            new Color(0.35f, 0.75f, 0.35f), // 4 — зелёная
        };

        /// <summary>Фракция по имени армии (создаёт при первом обращении).
        /// null — Encyclopedia недоступна (ранний старт) — звонивший пропустит HQ.</summary>
        internal static Faction GetOrCreate(string armyName, int colorIdx)
        {
            if (Cache.TryGetValue(armyName, out Faction cached) && cached != null)
                return cached;
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia == null)
                return null;
            // Уже есть с таким именем (например, rebuild после смены сцен)?
            if (encyclopedia.factions != null)
                foreach (Faction f in encyclopedia.factions)
                    if (f != null && f.factionName == armyName)
                        return Cache[armyName] = f;
            var faction = ScriptableObject.CreateInstance<Faction>();
            faction.factionName = armyName;
            faction.factionTag = "SN" + Mathf.Abs(armyName.GetHashCode()) % 100;
            faction.color = Palette[((colorIdx % Palette.Length) + Palette.Length) % Palette.Length];
            faction.LeaderboardOrder = 10 + Cache.Count; // после Boscali/Primeva
            encyclopedia.factions?.Add(faction);
            Cache[armyName] = faction;
            Log.Info("FactionFactory: создана рантайм-фракция '" + armyName + "'");
            return faction;
        }

        /// <summary>Сброс кэша (sceneLoaded не зовёт: ассеты переживают сцены вместе с картой).</summary>
        internal static void Reset()
        {
            Cache.Clear();
        }
    }
}
