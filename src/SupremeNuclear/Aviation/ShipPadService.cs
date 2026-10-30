using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>ВЕРТОЛЁТНЫЕ ПАЛУБЫ (v0.115, юзер-канон «посадка + сервис»): кораблям
    /// с посадочной площадкой, но БЕЗ компонента Airbase (Shard corvette — площадка
    /// декоративная, игра их в авиационную систему не подключала), поднимается
    /// настоящий аэродром рантаймом: вертикальная точка на кормовой палубе +
    /// сервисная точка → вертолёты садятся/заправляются/перевооружаются на корабле
    /// (механизмы посадки/сервиса наши же — AirbaseAccess-тумблер, SortieService).
    /// Заказ с борта НЕТ (юзер выбрал вариант 1). Путь — нативный SpawnCustomAirbase
    /// (как наши ВПП v0.87): SavedAirbase с VerticalLandingPoints, сетевой спавн
    /// через ServerObjectManager; аэродродром НЕ захвачаемый, умирает с кораблём.
    /// HOST-ONLY; MP-клиенты палубу не видят (позиция аэродрома не реплицируется —
    /// общий host-only долг мода).</summary>
    internal static class ShipPadService
    {
        /// <summary>Корабли с площадкой: матч по имени определения (данные игры,
        /// не код — Shard единственный известный; расширение списком).</summary>
        private static readonly string[] PadShips = { "Shard" };

        /// <summary>Смещение палубы от центра корабля (локально): корма, над палубой.</summary>
        private const float PadAftFactor = 0.55f; // × maxRadius — назад по курсу
        private const float PadHeightM = 5f;      // над центром корпуса (палуба)
        private const float PadSize = 28f;        // размер вертикальной точки

        private static readonly Dictionary<Unit, Airbase> Pads = new Dictionary<Unit, Airbase>();
        private static readonly Dictionary<Airbase, SavedAirbase> Saved = new Dictionary<Airbase, SavedAirbase>();
        private static float _nextScan;

        public static void Tick()
        {
            var net = NetworkManagerNuclearOption.i;
            if (net == null || net.Server == null || !net.Server.Active || !GameApi.InMission)
                return;
            float now = Time.unscaledTime;
            if (now < _nextScan)
                return;
            _nextScan = now + 2f;

            // Новые палубы: корабль с площадкой, жив, Airbase нет, ещё не поднят
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                Ship ship = unit as Ship;
                if (ship == null || ship.disabled || Pads.ContainsKey(ship) ||
                    ship.GetAirbase() != null || !IsPadShip(ship))
                    continue;
                TryRaisePad(ship);
            }

            // Палубы мёртвых кораблей сносятся вместе с ними
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, Airbase> kv in Pads)
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead == null)
                return;
            foreach (Unit ship in dead)
                DropPad(ship);
        }

        /// <summary>Полный сброс (смена сцены).</summary>
        public static void Reset()
        {
            Pads.Clear();
            Saved.Clear();
            _nextScan = 0f;
        }

        private static bool IsPadShip(Ship ship)
        {
            string name = ship.definition != null ? ship.definition.unitName : "";
            if (string.IsNullOrEmpty(name))
                return false;
            foreach (string key in PadShips)
                if (name.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        /// <summary>Поднять палубу: SavedAirbase (вертикальная + сервисная точки у
        /// кормы) → нативный SpawnCustomAirbase → сажаем объект ПОД корабль
        /// (позиция следует за корпусом; SavedAirbase-глобалы валидны на подъём).</summary>
        private static void TryRaisePad(Ship ship)
        {
            Mission mission = MissionManager.CurrentMission;
            var net = NetworkManagerNuclearOption.i;
            if (mission == null || net == null || net.Server == null || !net.Server.Active)
                return;
            FactionHQ hq = ship.NetworkHQ;
            if (hq == null || hq.faction == null)
                return;

            // Кормовая палуба в мировых координатах (SavedAirbase говорит глобалами)
            Vector3 fwd = ship.transform.forward;
            Vector3 flat = new Vector3(fwd.x, 0f, fwd.z).normalized;
            Vector3 pad = ship.transform.position - flat * (ship.maxRadius * PadAftFactor) +
                          Vector3.up * PadHeightM;
            GlobalPosition padGlobal = pad.ToGlobalPosition();

            var saved = new SavedAirbase
            {
                SavedInMission = true,
                UniqueName = "SN-PAD-" + ship.GetInstanceID(),
                DisplayName = Loc.Tr("Вертолётная палуба"),
                faction = hq.faction.factionName,
                Capturable = false,     // палуба — свойство корабля, не цель захвата
                CaptureRange = 250f,    // «своя база рядом» для RTB-подбора
                Center = padGlobal,
                SelectionPosition = padGlobal,
            };
            saved.VerticalLandingPoints.Add(padGlobal);
            // Сервис — чуть в сторону от точки посадки (не толпиться на пятне)
            Vector3 side = new Vector3(flat.z, 0f, -flat.x) * 12f;
            saved.ServicePoints.Add((pad + side).ToGlobalPosition());
            saved.ServicePoints.Add((pad - side).ToGlobalPosition());

            mission.airbases.Add(saved);
            Airbase airbase = mission.SpawnCustomAirbase(saved, net.ServerObjectManager);
            if (airbase == null)
            {
                Log.Error("ShipPad: аэродром палубы не создан для " + ship.NetworkunitName);
                mission.airbases.Remove(saved);
                return;
            }
            // Под корабль: вертикальные/сервисные трансформы — дети аэродрома,
            // аэродром — ребёнок корпуса → всё следует за кораблём на хосте.
            airbase.transform.SetParent(ship.transform, worldPositionStays: true);
            Pads[ship] = airbase;
            Saved[airbase] = saved;
            Log.Info("ShipPad: вертолётная палуба поднята на " + ship.NetworkunitName);
            CommanderHud.Notice(Loc.F("Вертолётная палуба: {0}", ship.NetworkunitName));
        }

        /// <summary>Снести палубу мёртвого корабля (сетевое уничтожение + чистка
        /// списка миссии, иначе сейв потянет осиротевший аэродром).</summary>
        private static void DropPad(Unit ship)
        {
            if (Pads.TryGetValue(ship, out Airbase airbase))
            {
                Pads.Remove(ship);
                if (Saved.TryGetValue(airbase, out SavedAirbase saved))
                {
                    MissionManager.CurrentMission?.airbases.Remove(saved);
                    Saved.Remove(airbase);
                }
                if (airbase != null)
                {
                    var net = NetworkManagerNuclearOption.i;
                    if (net != null && net.Server != null && net.Server.Active && airbase.Identity != null)
                        net.ServerObjectManager.Destroy(airbase.Identity);
                    else
                        Object.Destroy(airbase.gameObject);
                    Log.Info("ShipPad: палуба снесена (корабль погиб)");
                }
            }
        }
    }
}
