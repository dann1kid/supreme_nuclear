using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Инженерный захват (v3, SupCom-канон юзера 2026-10-30): захватывают ТОЛЬКО
    /// инженеры — стоя в радиусе точки, каждый ускоряет прогресс, операция ТРАТИТ
    /// ЭНЕРГИЮ из стока фракции (дефицит режет скорость); войска ничего не
    /// захватывают — только сносят. Цель — модовая постройка (экстрактор/
    /// генератор/депо/фабрика: нативный capturable у них выключен); аэродромы
    /// живут нативным захватом (победа). Смена владельца — прямое присваивание
    /// NetworkHQ на хосте, как в нативном Building.CheckForCapture.
    /// Хост-only, скан раз в полсекунды (паттерн SalvageService).
    /// </summary>
    public static class CaptureService
    {
        private const float ScanInterval = 0.5f;

        private sealed class Progress
        {
            public float worked;    // инженеро-секунд с учётом энергодефицита
            public FactionHQ by;    // кто ведёт захват (лидер по числу инженеров)
            public int engineers;   // сколько инженеров ведут (для HUD)
        }

        private static readonly Dictionary<Building, Progress> Progresses = new Dictionary<Building, Progress>();
        private static readonly List<Building> DoneBuffer = new List<Building>();
        private static float _nextScan;

        /// <summary>Снапшот активных захватов для маркеров HUD: параллельные буферы.</summary>
        public static readonly List<Building> MarkerTargets = new List<Building>();
        public static readonly List<float> MarkerProgress = new List<float>();

        public static void Reset()
        {
            Progresses.Clear();
            DoneBuffer.Clear();
            MarkerTargets.Clear();
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

            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active)
                return;

            EconomyProfile profile = EconomyService.Profile;
            if (profile == null || profile.captureSeconds <= 0f || !EconomyService.Running)
                return;
            if (Time.time < _nextScan)
                return;
            _nextScan = Time.time + ScanInterval;

            float radiusSq = profile.captureRadiusM * profile.captureRadiusM;
            float need = profile.captureSeconds;

            // Инженер → цель: стоящий (|v|<1) инженер фракции F в радиусе от
            // чужой/нейтральной модовой постройки (не «недра», не нативно-захватываемая).
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || !GameApi.IsEngineer(unit))
                    continue;
                if (PauseService.IsPaused(unit))
                    continue; // пауза юнита — не захватывает
                if (Mathf.Abs(unit.speed) > 1f)
                    continue; // едет мимо — не захватывает
                FactionHQ hq = unit.NetworkHQ;
                if (hq == null)
                    continue;

                Building best = null;
                float bestSq = radiusSq;
                Vector3 p = unit.transform.position;
                foreach (Unit other in UnitRegistry.allUnits)
                {
                    if (other == null || other.disabled || !(other is Building b) || b.capturable)
                        continue; // нативный захват (аэродромы/миссии) живёт своей логикой
                    if (other.NetworkHQ == null || other.NetworkHQ == hq)
                        continue; // «недра»-маркеры нейтральны — не цели; своё не берём
                    if (EconomyService.IsSiteMarker(other))
                        continue;
                    float sq = (other.transform.position - p).sqrMagnitude;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = b;
                    }
                }
                if (best == null)
                    continue;

                if (!Progresses.TryGetValue(best, out Progress pr))
                    Progresses[best] = pr = new Progress();
                pr.by = hq; // несколько фракций у одной цели — v1: последняя запись
                pr.engineers++;
            }

            // Налив: энергия со стока лидера режет скорость; без инженеров — откат.
            DoneBuffer.Clear();
            foreach (KeyValuePair<Building, Progress> kv in Progresses)
            {
                Building b = kv.Key;
                Progress pr = kv.Value;
                if (b == null || b.disabled || b.NetworkHQ == pr.by)
                {
                    DoneBuffer.Add(b); // здание умерло или уже наше — прогресс снят
                    continue;
                }
                if (pr.engineers > 0 && pr.by != null)
                {
                    float energyWant = profile.captureEnergyPerSecond * pr.engineers * ScanInterval;
                    EcoFlow.Paid paid = EconomyService.Drain(pr.by, 0f, energyWant);
                    float speed = energyWant > 0.01f ? paid.Energy / energyWant : 1f;
                    pr.worked += ScanInterval * pr.engineers * speed;
                    if (pr.worked >= need)
                    {
                        FactionHQ old = b.NetworkHQ;
                        b.NetworkHQ = pr.by; // как нативный CheckForCapture — прямое присваивание
                        DoneBuffer.Add(b);
                        CommanderHud.Notice(Loc.F("Захвачено: {0}", b.NetworkunitName));
                        Log.Info("Capture: " + b.NetworkunitName + " → " +
                                 (pr.by.faction != null ? pr.by.faction.factionName : "?") +
                                 " (было " + (old != null && old.faction != null ? old.faction.factionName : "нейтр.") + ")");
                    }
                }
                else
                {
                    pr.worked = Mathf.Max(0f, pr.worked - ScanInterval); // никого нет — откат
                    if (pr.worked <= 0f)
                        DoneBuffer.Add(b);
                }
                pr.engineers = 0; // пересчитывается следующим сканом
            }
            foreach (Building b in DoneBuffer)
                Progresses.Remove(b);
            DoneBuffer.Clear();

            // Снапшот маркеров (HUD рисует каждый кадр).
            MarkerTargets.Clear();
            MarkerProgress.Clear();
            foreach (KeyValuePair<Building, Progress> kv in Progresses)
            {
                if (kv.Key == null || kv.Value.worked <= 0f)
                    continue;
                MarkerTargets.Add(kv.Key);
                MarkerProgress.Add(kv.Value.worked / need);
            }
        }
    }
}
