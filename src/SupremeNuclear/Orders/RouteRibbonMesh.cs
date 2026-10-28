using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Меш-лента маршрутов (как в SupCom), рисуется напрямую через Graphics.DrawMesh
    /// каждый кадр — без GameObject/MeshRenderer, поэтому ей не мешают culling mask и
    /// фильтры рендер-конвейера игры. Пересборка — по Version приказов + таймер 0.5 с.</summary>
    public static class RouteRibbonMesh
    {
        private const float RebuildSec = 0.5f;
        private const float HeightBias = 3f;

        private static Mesh[] _meshes;

        /// <summary>Число сабмешей-цветов: 0 ход/RTB, 1 атака-движение, 2 патруль,
        /// 3 атака, 4 залп, 5 сопровождение.</summary>
        private const int SubmeshCount = 6;
        private static Material[] _mats;
        private static int _layer;
        private static float _nextRebuild;
        private static float _nextDonorRetry;
        private static int _lastAirVersion = -1;
        private static int _lastGroundVersion = -1;
        private static bool _ready;

        private static readonly List<Vector3>[] Verts = { new List<Vector3>(), new List<Vector3>(), new List<Vector3>(), new List<Vector3>(), new List<Vector3>(), new List<Vector3>() };
        private static readonly List<Vector2>[] Uv = { new List<Vector2>(), new List<Vector2>(), new List<Vector2>(), new List<Vector2>(), new List<Vector2>(), new List<Vector2>() };
        private static readonly List<int>[] Tris = { new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>() };
        // Скругление углов цепочки перед сэмплированием рельефа (та же логика, что в IMGUI-ленте).
        private static readonly List<Vector3> ChainPts = new List<Vector3>();
        private static readonly List<Vector3> SmoothChain = new List<Vector3>();
        private static readonly List<int> SmoothLegs = new List<int>();

        private sealed class RouteDraw
        {
            public int Submesh;
            public readonly List<Vector3> World = new List<Vector3>();
        }

        private static Vector3 _lastOriginPos;

        public static void Tick()
        {
            if (!Settings.RouteUseMesh || !GameApi.InMission)
                return;

            // Сдвиг floating origin = смена системы координат: меш обязан пересобраться.
            Transform origin = Datum.origin;
            bool originMoved = origin != null && origin.position != _lastOriginPos;
            if (origin != null)
                _lastOriginPos = origin.position;

            bool dirty = originMoved ||
                         AircraftOrders.Version != _lastAirVersion ||
                         GroundOrders.Version != _lastGroundVersion;
            if (dirty || Time.unscaledTime >= _nextRebuild)
            {
                _nextRebuild = Time.unscaledTime + RebuildSec;
                _lastAirVersion = AircraftOrders.Version;
                _lastGroundVersion = GroundOrders.Version;

                var routes = new List<RouteDraw>();
                if (Settings.RouteDebugQuad)
                    routes.Add(DebugFrame());
                CollectGround(routes);
                CollectAir(routes);
                Rebuild(routes);
            }
        }

        /// <summary>Ежекадровая подача мешей в камеры (вызывается из Update контроллера после Tick).</summary>
        public static void Render()
        {
            if (!_ready || _meshes == null || _mats == null)
                return;
            for (int i = 0; i < SubmeshCount; i++) // все сабмеши: залп (4) раньше не подавался
            {
                if (_meshes[i] == null || _meshes[i].vertexCount == 0 || _mats[i] == null)
                    continue;
                Graphics.DrawMesh(_meshes[i], Matrix4x4.identity, _mats[i], _layer,
                    null, 0, null, UnityEngine.Rendering.ShadowCastingMode.Off, false);
            }
        }

        private static RouteDraw DebugFrame()
        {
            GameApi.TryGetMapBounds(out Vector2 bmin, out Vector2 bmax);
            Vector3 c = new Vector3((bmin.x + bmax.x) * 0.5f, 0f, (bmin.y + bmax.y) * 0.5f);
            float y = GameApi.GroundY(c.x, c.z) + 800f;
            var frame = new RouteDraw { Submesh = 0 };
            frame.World.Add(new Vector3(c.x - 1000f, y, c.z - 1000f));
            frame.World.Add(new Vector3(c.x + 1000f, y, c.z - 1000f));
            frame.World.Add(new Vector3(c.x + 1000f, y, c.z + 1000f));
            frame.World.Add(new Vector3(c.x - 1000f, y, c.z + 1000f));
            frame.World.Add(new Vector3(c.x - 1000f, y, c.z - 1000f));
            return frame;
        }

        private static readonly List<Unit> MeshEscortUnits = new List<Unit>();
        private static readonly List<Unit> MeshEscortTargets = new List<Unit>();

        private static void CollectGround(List<RouteDraw> routes)
        {
            var units = new List<Unit>();
            var queues = new List<IReadOnlyList<GlobalPosition>>();
            var indices = new List<int>();
            GroundOrders.CollectAll(units, queues, indices);
            for (int i = 0; i < units.Count; i++)
            {
                Unit unit = units[i];
                // Цепочка инженера с живой очередью — служебная выданная голова;
                // маршрут инженера рисует IMGUI-проход очередей, ленту не дублируем.
                if (BuildTasks.ChainIsIssuedHead(unit))
                    continue;
                bool patrol = GroundOrders.KindOf(unit) == GroundOrderKind.Patrol;
                ChainPts.Clear();
                Vector3 up = unit.transform.position;
                ChainPts.Add(new Vector3(up.x, GameApi.GroundY(up.x, up.z), up.z));
                Vector3 first = default;
                for (int q = GroundOrders.DrawStartIndex(unit); q < queues[i].Count; q++)
                {
                    Vector3 l = queues[i][q].ToLocalPosition();
                    Vector3 wp = new Vector3(l.x, GameApi.GroundY(l.x, l.z), l.z);
                    if (q == GroundOrders.DrawStartIndex(unit))
                        first = wp;
                    ChainPts.Add(wp);
                }
                if (patrol && ChainPts.Count > 2)
                    ChainPts.Add(first); // замыкающая нога цикла
                AddSmoothedRoute(routes, patrol ? 2 : 0, ChainPts);
            }

            // Связи сопровождения наземки/флота (v0.97): лента «эскорт → цель».
            GroundOrders.CollectEscorts(MeshEscortUnits, MeshEscortTargets);
            for (int i = 0; i < MeshEscortUnits.Count; i++)
            {
                Unit t = MeshEscortTargets[i];
                if (t == null || t.disabled)
                    continue;
                Vector3 up = MeshEscortUnits[i].transform.position;
                var r = new RouteDraw { Submesh = 5 };
                r.World.Add(new Vector3(up.x, GameApi.GroundY(up.x, up.z), up.z));
                Vector3 tp = t.transform.position;
                AppendSamples(r.World, new Vector3(tp.x, GameApi.GroundY(tp.x, tp.z), tp.z));
                if (r.World.Count > 1)
                    routes.Add(r);
            }
        }

        private static void CollectAir(List<RouteDraw> routes)
        {
            var units = new List<Unit>();
            var plans = new List<IReadOnlyList<AircraftOrders.OrderSegment>>();
            AircraftOrders.CollectAll(units, plans);
            for (int i = 0; i < units.Count; i++)
            {
                Unit unit = units[i];
                Vector3 up = unit.transform.position;
                Vector3 anchor = new Vector3(up.x, GameApi.GroundY(up.x, up.z), up.z);
                foreach (AircraftOrders.OrderSegment seg in plans[i])
                {
                    if (seg == null || (seg.Points.Count == 0 && seg.Target == null))
                        continue;
                    int sub = SubmeshOf(seg.Mode);
                    if (seg.Mode == AirOrderMode.Attack && seg.Target != null)
                    {
                        Vector3 tp = seg.Target.transform.position;
                        var r = new RouteDraw { Submesh = sub };
                        r.World.Add(anchor);
                        AppendSamples(r.World, new Vector3(tp.x, GameApi.GroundY(tp.x, tp.z), tp.z));
                        if (r.World.Count > 1)
                            routes.Add(r);
                        anchor = r.World[r.World.Count - 1];
                        continue;
                    }
                    if (seg.Mode == AirOrderMode.Escort && seg.Target != null)
                    {
                        // Эскорт-сегмент (v0.97): зелёная нога «борт → цель».
                        Vector3 tp = seg.Target.transform.position;
                        var r = new RouteDraw { Submesh = sub };
                        r.World.Add(anchor);
                        AppendSamples(r.World, new Vector3(tp.x, GameApi.GroundY(tp.x, tp.z), tp.z));
                        if (r.World.Count > 1)
                            routes.Add(r);
                        anchor = r.World[r.World.Count - 1];
                        continue;
                    }

                    var wps = new List<Vector3>(seg.Points.Count);
                    ChainPts.Clear();
                    ChainPts.Add(anchor);
                    for (int p = 0; p < seg.Points.Count; p++)
                    {
                        Vector3 l = seg.Points[p].ToLocalPosition();
                        Vector3 wp = new Vector3(l.x, GameApi.GroundY(l.x, l.z), l.z);
                        ChainPts.Add(wp);
                        wps.Add(wp);
                    }
                    if (seg.Mode == AirOrderMode.Patrol && wps.Count > 1)
                        ChainPts.Add(wps[0]); // замыкающая нога патруля
                    AddSmoothedRoute(routes, sub, ChainPts);
                    if (wps.Count > 0)
                        anchor = wps[wps.Count - 1];
                }
            }
        }

        /// <summary>Цепочка вейпоинтов → лента со скруглёнными углами: сглаживание
        /// в XZ (Util.RoundChainCorners), затем сэмплирование рельефа вдоль пути.</summary>
        private static void AddSmoothedRoute(List<RouteDraw> routes, int submesh, List<Vector3> chain)
        {
            int n = Util.RoundChainCorners(chain, Settings.RouteCornerRadiusM, SmoothChain, SmoothLegs);
            if (n < 2)
                return;
            var route = new RouteDraw { Submesh = submesh };
            route.World.Add(SmoothChain[0]);
            for (int j = 1; j < n; j++)
                AppendSamples(route.World, SmoothChain[j]);
            routes.Add(route);
        }

        private static void AppendSamples(List<Vector3> world, Vector3 to)
        {
            Vector3 from = world[world.Count - 1];
            float dist = new Vector2(to.x - from.x, to.z - from.z).magnitude;
            int steps = Mathf.Clamp(Mathf.CeilToInt(dist / Settings.RouteSampleStepM), 1, 80);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                float x = Mathf.Lerp(from.x, to.x, t);
                float z = Mathf.Lerp(from.z, to.z, t);
                world.Add(new Vector3(x, GameApi.GroundY(x, z), z));
            }
        }

        private static int SubmeshOf(AirOrderMode mode) =>
            mode switch
            {
                AirOrderMode.AttackMove => 1,
                AirOrderMode.Patrol => 2,
                AirOrderMode.Attack => 3,
                AirOrderMode.Salvo => 4, // залп — малиновый
                AirOrderMode.Escort => 5, // сопровождение — зелёный
                AirOrderMode.Rtb => 0, // цвет возврата даёт IMGUI; меш-режим экспериментальный
                _ => 0
            };

        private static void Rebuild(List<RouteDraw> routes)
        {
            if (!EnsureMaterials())
                return;

            for (int i = 0; i < SubmeshCount; i++)
            {
                Verts[i].Clear();
                Uv[i].Clear();
                Tris[i].Clear();
            }

            float half = Settings.RouteRibbonWidthM * 0.5f;
            foreach (RouteDraw route in routes)
            {
                var verts = Verts[route.Submesh];
                var uv = Uv[route.Submesh];
                var tris = Tris[route.Submesh];
                int baseIndex = verts.Count;
                float uAcc = 0f;
                Vector3 prevC = route.World[0];
                for (int i = 0; i < route.World.Count; i++)
                {
                    Vector3 c = route.World[i];
                    if (i > 0)
                        uAcc += Vector3.Distance(prevC, c);
                    prevC = c;
                    Vector3 next = i + 1 < route.World.Count ? route.World[i + 1] : c;
                    Vector3 prev = i > 0 ? route.World[i - 1] : c;
                    Vector3 dir = next - prev;
                    Vector3 flat = new Vector3(dir.x, 0f, dir.z);
                    Vector3 perp = flat.sqrMagnitude < 1e-6f
                        ? Vector3.right
                        : new Vector3(-flat.z, 0f, flat.x).normalized;
                    verts.Add(c + perp * half + Vector3.up * HeightBias);
                    verts.Add(c - perp * half + Vector3.up * HeightBias);
                    uv.Add(new Vector2(uAcc * 0.01f, 0f));
                    uv.Add(new Vector2(uAcc * 0.01f, 1f));
                }
                for (int i = 0; i + 1 < route.World.Count; i++)
                {
                    int a = baseIndex + i * 2;
                    tris.Add(a);
                    tris.Add(a + 1);
                    tris.Add(a + 2);
                    tris.Add(a + 1);
                    tris.Add(a + 3);
                    tris.Add(a + 2);
                }
            }

            for (int i = 0; i < SubmeshCount; i++)
            {
                if (_meshes[i] == null)
                    _meshes[i] = new Mesh { name = "RouteRibbon" + i };
                _meshes[i].Clear();
                _meshes[i].SetVertices(Verts[i]);
                _meshes[i].SetUVs(0, Uv[i]);
                _meshes[i].SetTriangles(Tris[i], 0);
                _meshes[i].RecalculateNormals();
                _meshes[i].RecalculateBounds();
            }
            _ready = true;
        }

        private static bool EnsureMaterials()
        {
            if (_mats != null)
                return true;

            Material baseMat = null;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                MeshRenderer r = unit != null ? unit.GetComponentInChildren<MeshRenderer>() : null;
                if (r != null && r.sharedMaterial != null)
                {
                    baseMat = r.sharedMaterial;
                    _layer = r.gameObject.layer;
                    break;
                }
            }
            if (baseMat == null)
            {
                MeshRenderer r = Object.FindObjectOfType<MeshRenderer>();
                if (r != null && r.sharedMaterial != null)
                {
                    baseMat = r.sharedMaterial;
                    _layer = r.gameObject.layer;
                }
            }
            if (baseMat == null)
            {
                if (Time.unscaledTime >= _nextDonorRetry)
                {
                    _nextDonorRetry = Time.unscaledTime + 2f;
                    Log.Warn("RouteRibbonMesh: донор материала не найден, повтор через 2 с");
                }
                return false;
            }
            Log.Info("RouteRibbonMesh: donor " + baseMat.name + " (" + baseMat.shader.name + "), layer " + _layer);

            var colors = new[]
            {
                new Color(1f, 0.8f, 0.25f),
                new Color(1f, 0.5f, 0.2f),
                new Color(0.3f, 0.95f, 0.85f),
                new Color(1f, 0.25f, 0.2f),
                new Color(0.95f, 0.3f, 0.8f), // залп
                new Color(0.55f, 0.95f, 0.4f) // сопровождение
            };
            // Белая 1x1 текстура вместо текстуры донора (иначе лента получает «асфальт» корпуса).
            var white = new Texture2D(1, 1);
            white.SetPixel(0, 0, Color.white);
            white.Apply();

            _mats = new Material[SubmeshCount];
            for (int i = 0; i < SubmeshCount; i++)
            {
                _mats[i] = new Material(baseMat);
                if (_mats[i].HasProperty("_BaseMap"))
                    _mats[i].SetTexture("_BaseMap", white);
                if (_mats[i].HasProperty("_MainTex"))
                    _mats[i].SetTexture("_MainTex", white);
                _mats[i].DisableKeyword("_NORMALMAP");
                if (_mats[i].HasProperty("_BaseColor"))
                    _mats[i].SetColor("_BaseColor", colors[i]);
                else
                    _mats[i].color = colors[i];
            }
            _meshes = new Mesh[SubmeshCount];
            return true;
        }

        public static void Destroy()
        {
            if (_meshes != null)
            {
                foreach (Mesh m in _meshes)
                    if (m != null)
                        Object.Destroy(m);
                _meshes = null;
            }
            if (_mats != null)
            {
                foreach (Material m in _mats)
                    if (m != null)
                        Object.Destroy(m);
                _mats = null;
            }
            _ready = false;
            _lastAirVersion = -1;
            _lastGroundVersion = -1;
        }
    }
}
