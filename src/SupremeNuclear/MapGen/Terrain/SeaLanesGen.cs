using System;
using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.SceneLoading;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Морские трассы спец-карт (v0.152.1, плейтест 2026-11-04 «морские юниты не
    /// слушаются»): ShipAI ищет путь ТОЛЬКО по графу LevelInfo.seaLanes
    /// (Pathfind: NoNetwork → пустые вейпоинты — корабль дрейфует). Стоковые
    /// карты несут граф ассетом (MapSettings.SeaLanes SO); наш MapSettings его
    /// не имеет → CreateSeaLanes() возвращает ПУСТУЮ сеть. Строим сетку трасс
    /// над судоходной водой сами (решётка ребер «восток/юг» с проверкой центра
    /// ребра — узкая мель между глубокими узлами не пролезет в граф); концы
    /// ребер совпадают точками → RegenerateNetwork сшивает их в узлы (InRange
    /// 10 м). Пересечений внутри сегмента нет (единичные шаги решётки) —
    /// CheckIntersection не нужен. Глубина сэмплируется рейкастами по
    /// Statics-коллайдерам рельефа; промах (открытый океан за кромкой меша) —
    /// судоходно. Ядро решётки чистое — тестируется раннером.
    /// </summary>
    internal static class SeaLanesGen
    {
        private const float StepM = 1500f;      // шаг решётки трасс
        private const float MinDepthM = 10f;    // глубина под килем для узла трассы

        /// <summary>Ребро трассы в плоских координатах (глобальное пространство).</summary>
        internal readonly struct Edge
        {
            public readonly Vector2 A;
            public readonly Vector2 B;
            public Edge(Vector2 a, Vector2 b) { A = a; B = b; }
        }

        /// <summary>Чистое ядро: решётка рёбер над судоходной водой. depthAt —
        /// глубина в точке (м; отрицательная = суша, огромная = открытый океан);
        /// ребро проходит, если ОБА узла и ЦЕНТР ребра не мельче minDepthM.</summary>
        internal static List<Edge> BuildEdges(Func<float, float, float> depthAt,
            float minX, float minZ, float maxX, float maxZ,
            float step = StepM, float minDepth = MinDepthM)
        {
            var edges = new List<Edge>();
            int nx = Mathf.Max(1, Mathf.FloorToInt((maxX - minX) / step));
            int nz = Mathf.Max(1, Mathf.FloorToInt((maxZ - minZ) / step));
            // Навигуемость узлов решётки (по строкам — кэш предыдущей строки).
            var row = new bool[nx + 1];
            var prev = new bool[nx + 1];
            for (int j = 0; j <= nz; j++)
            {
                float z = minZ + j * step;
                for (int i = 0; i <= nx; i++)
                    row[i] = depthAt(minX + i * step, z) >= minDepth;
                if (j > 0)
                    for (int i = 0; i <= nx; i++)
                        if (row[i] && prev[i] &&
                            Sailable(depthAt, minZ + (j - 1) * step, z, minX + i * step, minDepth, false))
                            edges.Add(new Edge(new Vector2(minX + i * step, minZ + (j - 1) * step),
                                new Vector2(minX + i * step, z)));
                for (int i = 0; i < nx; i++)
                    if (row[i] && row[i + 1] &&
                        Sailable(depthAt, minX + i * step, minX + (i + 1) * step, minZ + j * step, minDepth, true))
                        edges.Add(new Edge(new Vector2(minX + i * step, z), new Vector2(minX + (i + 1) * step, z)));
                (row, prev) = (prev, row); // swap буферов
                for (int i = 0; i <= nx; i++)
                    row[i] = false;
            }
            return edges;
        }

        /// <summary>Центр ребра судоходен (узкая мель/остров между узлами не
        /// проходит в граф). along* — координата, меняющаяся вдоль ребра;
        /// fixedCoord — поперечная.</summary>
        private static bool Sailable(Func<float, float, float> depthAt,
            float alongFrom, float alongTo, float fixedCoord, float minDepth, bool horizontal)
        {
            float mid = (alongFrom + alongTo) * 0.5f;
            return depthAt(horizontal ? mid : fixedCoord, horizontal ? fixedCoord : mid) >= minDepth;
        }

        /// <summary>Построить сеть и подсунуть LevelInfo (только если своя пустая).</summary>
        internal static void Install(MapSettings mapSettings)
        {
            try
            {
                LevelInfo info = NetworkSceneSingleton<LevelInfo>.i;
                if (info == null)
                {
                    Log.Warn("SeaLanes: LevelInfo не найден — трассы не установлены");
                    return;
                }
                if (info.seaLanes != null && info.seaLanes.Exists())
                    return; // сеть уже есть (нативная/повторный вход) — не трогаем
                RoadNetwork net = BuildNetwork(mapSettings);
                var setter = AccessTools.Property(typeof(LevelInfo), nameof(LevelInfo.seaLanes))
                    ?.GetSetMethod(true);
                if (setter == null)
                {
                    Log.Error("SeaLanes: нет сеттера LevelInfo.seaLanes — корабли без трасс");
                    return;
                }
                setter.Invoke(info, new object[] { net });
                Log.Info("MapGen: морские трассы построены — " + net.roads.Count + " рёбер, " +
                         net.nodes.Count + " узлов (шаг " + StepM.ToString("F0") + " м)");
            }
            catch (Exception e)
            {
                Log.Warn("SeaLanes: " + e.Message);
            }
        }

        /// <summary>Рантайм-обвязка: глубина рейкастом по Statics-коллайдерам
        /// (рельеф карты); промах — открытый океан (судоходно).</summary>
        private static RoadNetwork BuildNetwork(MapSettings mapSettings)
        {
            Vector2 size = mapSettings != null ? mapSettings.MapSize : new Vector2(81920f, 81920f);
            float half = Mathf.Max(size.x, size.y) * 0.5f;
            float seaY = Datum.LocalSeaY;
            float DepthAt(float x, float z)
            {
                Vector3 origin = new Vector3(x, seaY + 300f, z);
                if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 2000f,
                        PhysicsLayers.StaticsMask))
                    return float.MaxValue; // за кромкой меша — океан
                return seaY - hit.point.y;
            }

            List<Edge> edges = BuildEdges(DepthAt, -half, -half, half, half);
            var net = new RoadNetwork { AllowMerge = true };
            foreach (Edge e in edges)
            {
                var road = new Road();
                road.AddPoint(new Vector3(e.A.x, seaY, e.A.y).ToGlobalPosition());
                road.AddPoint(new Vector3(e.B.x, seaY, e.B.y).ToGlobalPosition());
                net.roads.Add(road);
            }
            net.RegenerateNetwork();
            return net;
        }
    }
}
