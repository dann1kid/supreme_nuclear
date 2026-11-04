using System.Collections.Generic;
using NuclearOption;
using NuclearOption.Networking;
using NuclearOption.SavedMission.Outcomes;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Условие победы «annihilation» (DESIGN §2.7, юзер-решение 2026-11-03): фракция,
    /// у которой после старта не осталось НИ ОДНОГО юнита/здания, проигрывает матч —
    /// DeclareEndGame(Defeat) (Rpc разворачивает результат остальным сторонам).
    /// Счётчик — нативный HQ.factionUnits: Register/RemoveFactionUnit сами ведут список
    /// (гибель, деспавн в инвентарь убирают юнит; здания — тоже Unit). Резерв авиации
    /// в ангара фракцию не спасает: без ангаров (юнитов) резерв не поднять. Второй
    /// путь победы — нативный objective «захват всех баз» (ScenarioBuild.BuildVictory);
    /// гонку каналов гасит gameResolution != Ongoing (натив игнорирует повторное
    /// завершение). Host-only (инвариант №2).
    /// </summary>
    internal static class VictoryService
    {
        private const float CheckInterval = 2f;

        // Фракции, у которых хоть раз было имущество: защищает от ложного поражения
        // на старте (стартовый аирдроп спавнится секунды) и у пустых слотов-наблюдателей.
        private static readonly HashSet<FactionHQ> _hadAssets = new HashSet<FactionHQ>();
        private static float _nextCheck;

        public static void Tick()
        {
            if (!GameApi.InMission)
                return;
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;
            if (GameManager.gameResolution != GameResolution.Ongoing)
                return;
            if (Time.time < _nextCheck)
                return;
            _nextCheck = Time.time + CheckInterval;
            foreach (FactionHQ hq in FactionRegistry.HQLookup.Values)
            {
                if (hq == null)
                    continue;
                if (hq.factionUnits.Count > 0)
                {
                    _hadAssets.Add(hq);
                    continue;
                }
                if (!_hadAssets.Contains(hq))
                    continue;
                string name = hq.faction != null ? hq.faction.factionName : "?";
                Log.Info("Victory: сторона '" + name + "' аннигилирована (не осталось " +
                         "юнитов и зданий) — DeclareEndGame(Defeat)");
                CommanderHud.Notice(Loc.F("Сторона «{0}» полностью уничтожена", name));
                hq.DeclareEndGame(EndType.Defeat);
            }
        }

        /// <summary>Сцена умерла — реестр «имевших имущество» тоже.</summary>
        public static void Reset()
        {
            _hadAssets.Clear();
            _nextCheck = 0f;
        }
    }
}
