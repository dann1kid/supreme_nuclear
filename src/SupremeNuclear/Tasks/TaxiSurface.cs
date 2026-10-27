using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Достраивание ПОВЕРХНОСТИ рулёжки (пара к дороге в графе из
    /// BuildTasks.ConnectTaxiway): шасси игры считает «бетоном» любой коллайдер
    /// с материалом ≠ террейну (LandingGear.onTarmac), поэтому вдоль каждой
    /// дорисованной дороги лежит тонкая плита-коллайдер на слое Statics — колёса
    /// не вязнут в грунте, GroundY видит поверхность. Сверху — серая полоса-визуал
    /// через Graphics.DrawMesh (GameObject+MeshRenderer в этой игре не рендерится —
    /// факт из саги лент маршрутов). Хост-only: физика бортов — серверная.</summary>
    internal static class TaxiSurface
    {
        private struct Strip
        {
            public Transform Transform;
            public float Length;
        }

        private static Transform _root;
        private static readonly List<Strip> Strips = new List<Strip>();
        private static Mesh _quad;
        private static Material _mat;

        private const float WidthM = 24f;   // ширина полосы: габарит+крыло с запасом
        private const float ThickM = 0.4f;  // толщина плиты — луч шасси не проскочит
        private const float LiftM = 0.12f;  // подъём над рельефом: не z-fight с террейном
        private const float StepM = 50f;    // шаг подсыпки: каждая плита на своём GroundY

        private static readonly Color Asfalt = new Color(0.16f, 0.16f, 0.17f, 0.6f);

        /// <summary>Построить поверхность вдоль полилинии дороги (точки — глобальные).</summary>
        public static void Register(IReadOnlyList<GlobalPosition> points)
        {
            if (points == null || points.Count < 2)
                return;
            EnsureRoot();
            if (_root == null)
                return;
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 a = points[i].ToLocalPosition();
                Vector3 b = points[i + 1].ToLocalPosition();
                float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (len < 1f)
                    continue;
                // Подсыпка сегментами: плиты наклоняются за рельефом, а не летят
                // над оврагом одним длинным боксом.
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / StepM));
                for (int s = 0; s < steps; s++)
                {
                    AddSlab(Vector3.Lerp(a, b, s / (float)steps),
                        Vector3.Lerp(a, b, (s + 1) / (float)steps));
                }
            }
        }

        private static void AddSlab(Vector3 p0, Vector3 p1)
        {
            Vector3 from = new Vector3(p0.x, GameApi.GroundY(p0.x, p0.z), p0.z);
            Vector3 to = new Vector3(p1.x, GameApi.GroundY(p1.x, p1.z), p1.z);
            Vector3 delta = to - from;
            if (new Vector2(delta.x, delta.z).sqrMagnitude < 0.25f)
                return;
            Vector3 mid = (from + to) * 0.5f + Vector3.up * LiftM;
            var go = new GameObject("taxi-surface");
            go.transform.SetParent(_root, false);
            go.transform.SetPositionAndRotation(mid, Quaternion.LookRotation(delta.normalized));
            // Слой карты: GroundY (StaticsMask) и лучи шасси (все, кроме
            // ExclusionZones/IgnoreCollisions) видят плиту; материала нет —
            // null ≠ terrainMaterial, для шасси это тармак.
            go.layer = PhysicsLayers.Statics;
            var box = go.AddComponent<BoxCollider>();
            float len = delta.magnitude + 4f; // нахлёст на стыках — без щелей
            box.size = new Vector3(WidthM, ThickM, len);
            Strips.Add(new Strip { Transform = go.transform, Length = len });
        }

        private static void EnsureRoot()
        {
            if (_root != null)
                return;
            var go = new GameObject("SupremeNuclear.TaxiSurface");
            _root = go.transform; // корень сцены: сдвиг FloatingOrigin несёт его с миром
        }

        /// <summary>Серые полосы поверх плит — каждый кадр из Update ДО рендера
        /// (правило №16: DrawMesh из OnGUI мерцает).</summary>
        public static void Render()
        {
            if (Strips.Count == 0 || !GameApi.InMission)
                return;
            if (_mat == null && !EnsureMaterial())
                return;
            for (int i = Strips.Count - 1; i >= 0; i--)
            {
                Transform t = Strips[i].Transform;
                if (t == null)
                {
                    Strips.RemoveAt(i); // сцена/плиты умерли — не копим мусор
                    continue;
                }
                Matrix4x4 m = Matrix4x4.TRS(
                    t.position + Vector3.up * 0.22f, t.rotation,
                    new Vector3(WidthM, 1f, Strips[i].Length));
                Graphics.DrawMesh(_quad, m, _mat, 0, null, 0);
            }
        }

        private static bool EnsureMaterial()
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null)
                return false;
            _mat = new Material(sh) { renderQueue = 3000 };
            _mat.SetFloat("_SrcBlend", 5f);
            _mat.SetFloat("_DstBlend", 10f);
            _mat.SetFloat("_ZWrite", 0f);
            _mat.SetFloat("_Cull", 0f); // плоскость видна с обеих сторон
            _mat.SetColor("_Color", Asfalt);
            _quad = new Mesh { name = "taxi-surface-quad" };
            _quad.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f),
            };
            _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _quad.RecalculateBounds();
            return true;
        }

        public static void Reset()
        {
            Strips.Clear();
            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
                _root = null;
            }
        }
    }
}
