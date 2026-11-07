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

        /// <summary>Режим лобби v3: «уничтожение» — активен; «захват»/«бесконечная»
        /// — сервис пассивен (захват везут objectives миссии). Ставит Launch.</summary>
        internal static volatile bool AnnihilationMode;

        /// <summary>Штаб → имя фракции-армии (для командных союзов).</summary>
        private static readonly Dictionary<FactionHQ, string> FactionName = new Dictionary<FactionHQ, string>();

        /// <summary>Конфигурация от лобби (хост, при СТАРТе): режим + имена фракций
        /// армий (союзные команды различает SnLobbyService.AlliedFactions).</summary>
        internal static void Configure(bool annihilation)
        {
            AnnihilationMode = annihilation;
            FactionName.Clear();
            foreach (FactionHQ hq in FactionRegistry.HQLookup.Values)
                if (hq != null && hq.faction != null)
                    FactionName[hq] = hq.faction.factionName;
        }

        // Фракции, у которых хоть раз было имущество: защищает от ложного поражения
        // на старте (стартовый аирдроп спавнится секунды) и у пустых слотов-наблюдателей.
        private static readonly HashSet<FactionHQ> _hadAssets = new HashSet<FactionHQ>();
        private static float _nextCheck;

        public static void Tick()
        {
            if (!GameApi.InMission || !AnnihilationMode)
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
                Log.Info("Victory: армия '" + name + "' аннигилирована (не осталось " +
                         "юнитов и зданий)");
                CommanderHud.Notice(Loc.F("Армия «{0}» полностью уничтожена", name));
                // Лобби v3 — КОМАНДНАЯ победа без патча: гасим HQ погибшей армии
                // (Defeat), а если её КОМАНДА умерла целиком — DeclareEndGame(Victory)
                // на каждый живой HQ чужих команд: свой исход получит каждый клиент
                // через свой HQ (FinishGame игнорирует повторы).
                if (TeamAlive(hq))
                    continue; // союзники ещё держатся — команда в игре
                foreach (FactionHQ other in FactionRegistry.HQLookup.Values)
                {
                    if (other == null || other.faction == null)
                        continue;
                    bool sameTeam = AlliedHq(other, hq);
                    bool otherAlive = other.factionUnits.Count > 0 || other.factionPlayers.Count > 0;
                    if (sameTeam)
                        other.DeclareEndGame(EndType.Defeat);
                    else if (otherAlive)
                        other.DeclareEndGame(EndType.Victory);
                }
                AnnihilationMode = false; // исход объявлен — петлю заглушить
                return;
            }
        }

        /// <summary>Жива ли команда HQ (есть живые армии-союзники или игроки).</summary>
        private static bool TeamAlive(FactionHQ dead)
        {
            foreach (FactionHQ hq in FactionRegistry.HQLookup.Values)
            {
                if (hq == null || hq == dead || !AlliedHq(hq, dead))
                    continue;
                if (hq.factionUnits.Count > 0 || hq.factionPlayers.Count > 0)
                    return true;
            }
            return false;
        }

        private static bool AlliedHq(FactionHQ a, FactionHQ b)
        {
            string na = NameOf(a), nb = NameOf(b);
            return !string.IsNullOrEmpty(na) && !string.IsNullOrEmpty(nb) &&
                   SnLobbyService.AlliedFactions(na, nb);
        }

        private static string NameOf(FactionHQ hq) =>
            hq != null && hq.faction != null ? hq.faction.factionName : null;

        /// <summary>Сцена умерла — реестр «имевших имущество» тоже.</summary>
        public static void Reset()
        {
            _hadAssets.Clear();
            _nextCheck = 0f;
        }
    }
}
