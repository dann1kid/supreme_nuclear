using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Салваж обломков (DESIGN §3-5, простейший формат): инженер, СТОЯЩИЙ в радиусе
    /// сбора от обломка, разбирает его; несколько инженеров ускоряют пропорционально.
    /// По завершении владелец инженеров получает массу (salvageValue профиля),
    /// обломок убирается нативным Wreckage.Disintegrate (аккуратное удаление:
    /// коллайдеры, решётка препятствий, список MissionManager). Хост-only.
    /// Ценность обломка фиксированная: у нативного Wreckage нет связи с ценой
    /// погибшего юнита. Обход раз в полсекунды (троттл, паттерн EnemyNearby §12-45);
    /// поиск — индексированный BattlefieldGrid.GetWrecksInRangeNonAlloc.
    /// MP-нюанс: Disintegrate локален — у клиентов обломок исчезнет своим decay
    /// (как у нативного WreckCollector); полный сетевой синк — долг §12.
    /// </summary>
    public static class SalvageService
    {
        private const float ScanInterval = 0.5f;

        private sealed class Progress
        {
            public float collected; // наработано инженеро-секунд
            public FactionHQ hq;    // кому платить (владелец инженеров)
        }

        private static readonly Dictionary<Wreckage, Progress> Progresses = new Dictionary<Wreckage, Progress>();
        private static readonly List<Wreckage> WreckBuffer = new List<Wreckage>();
        private static readonly List<Wreckage> DoneBuffer = new List<Wreckage>();
        private static float _nextScan;

        /// <summary>Снимок активных сборов для маркеров HUD: параллельные буферы,
        /// перезаполняются в тике (без аллокаций после прогрева).</summary>
        public static readonly List<Wreckage> MarkerWrecks = new List<Wreckage>();
        public static readonly List<float> MarkerProgress = new List<float>();

        public static void Reset()
        {
            Progresses.Clear();
            MarkerWrecks.Clear();
            MarkerProgress.Clear();
            _nextScan = 0f;
        }

        public static void Tick()
        {
            if (!GameApi.InMission)
            {
                if (Progresses.Count > 0)
                    Reset();
                return;
            }

            // Разбор — авторитетен только на хосте (в сингле мы и есть хост).
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;

            EconomyProfile profile = EconomyService.Profile;
            if (profile == null || profile.salvageValue <= 0f)
                return;
            if (Time.time < _nextScan)
                return;
            _nextScan = Time.time + ScanInterval;

            float radius = Mathf.Max(10f, profile.salvageRadiusM);
            float need = Mathf.Max(0.5f, profile.salvageSeconds);

            // Инженер → обломки в радиусе: стоящий (SupCom: остановился — собирает),
            // живой, своей фракции (нейтральных инженеров не бывает, но HQ нужен).
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || !GameApi.IsEngineer(unit))
                    continue;
                if (Mathf.Abs(unit.speed) > 1f)
                    continue; // едет мимо — не собирает
                FactionHQ hq = unit.NetworkHQ;
                if (hq == null)
                    continue;

                WreckBuffer.Clear();
                BattlefieldGrid.GetWrecksInRangeNonAlloc(
                    unit.transform.GlobalPosition(), radius, WreckBuffer);
                foreach (Wreckage wreck in WreckBuffer)
                {
                    if (wreck == null)
                        continue;
                    if (!Progresses.TryGetValue(wreck, out Progress p))
                        Progresses[wreck] = p = new Progress();
                    p.collected += ScanInterval;
                    p.hq = hq;
                }
            }

            // Завершение сбора и чистка исчезнувших обломков (нативный decay).
            DoneBuffer.Clear();
            foreach (KeyValuePair<Wreckage, Progress> kv in Progresses)
                if (kv.Key == null || kv.Value.collected >= need)
                    DoneBuffer.Add(kv.Key);
            foreach (Wreckage wreck in DoneBuffer)
            {
                Progress p = Progresses[wreck];
                Progresses.Remove(wreck);
                if (wreck == null)
                    continue;
                if (p.hq != null)
                {
                    p.hq.AddFunds(profile.salvageValue);
                    if (GameApi.TryGetLocalHQ(out FactionHQ local) && local == p.hq)
                        CommanderHud.Notice(Loc.F("Обломок собран: +{0}", profile.salvageValue.ToString("F0")));
                }
                Vector3 pos = wreck.transform.position;
                Log.Info($"Economy: салваж — обломок на ({pos.x:F0},{pos.z:F0}) разобран, " +
                         $"+{profile.salvageValue:F0} массы");
                wreck.Disintegrate();
            }

            SnapshotMarkers();
        }

        /// <summary>Перезаполнение буферов маркеров (раз в скан; HUD читает их каждый кадр).</summary>
        private static void SnapshotMarkers()
        {
            MarkerWrecks.Clear();
            MarkerProgress.Clear();
            EconomyProfile profile = EconomyService.Profile;
            float need = profile != null ? Mathf.Max(0.5f, profile.salvageSeconds) : 1f;
            foreach (KeyValuePair<Wreckage, Progress> kv in Progresses)
            {
                if (kv.Key == null)
                    continue;
                MarkerWrecks.Add(kv.Key);
                MarkerProgress.Add(Mathf.Clamp01(kv.Value.collected / need));
            }
        }
    }
}
