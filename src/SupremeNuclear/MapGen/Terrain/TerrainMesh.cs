using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Меш-рельеф спец-карты: чанки MeshFilter+MeshRenderer+MeshCollider на слое
    /// Statics (6) — вся игра (физика, ИИ, GameApi.GroundY) берёт высоту рейкастами
    /// по Statics-коллайдерам, никакого Unity Terrain в Nuclear Option нет.
    /// Чанковая нарезка — для кулинга; коллайдер на каждый чанк.
    /// </summary>
    internal static class TerrainMesh
    {
        private const int ChunksPerSide = 8;

        public static void Build(Transform parent, Heightmap hm, Material material)
        {
            int cells = Mathf.Max(1, hm.W / ChunksPerSide);
            for (int cj = 0; cj < ChunksPerSide; cj++)
            {
                int j0 = cj * cells;
                int j1 = Mathf.Min(hm.H, j0 + cells);
                if (j0 >= j1)
                    break;
                for (int ci = 0; ci < ChunksPerSide; ci++)
                {
                    int i0 = ci * cells;
                    int i1 = Mathf.Min(hm.W, i0 + cells);
                    if (i0 >= i1)
                        break;
                    BuildChunk(parent, hm, material, i0, i1, j0, j1, ci, cj);
                }
            }
        }

        private static void BuildChunk(Transform parent, Heightmap hm, Material material,
            int i0, int i1, int j0, int j1, int ci, int cj)
        {
            int w = i1 - i0, h = j1 - j0;
            float half = hm.W * hm.Step * 0.5f;
            var verts = new Vector3[(w + 1) * (h + 1)];
            var uvs = new Vector2[verts.Length];
            var tris = new int[w * h * 6];

            int v = 0;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++, v++)
                {
                    float x = i * hm.Step - half;
                    float z = j * hm.Step - half;
                    verts[v] = new Vector3(x, hm.SampleHeight(x, z), z);
                    // UV как у цветовой текстуры: ((x+size/2)/size, (z+size/2)/size).
                    uvs[v] = new Vector2((x + half) / (half * 2f), (z + half) / (half * 2f));
                }

            int t = 0;
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int a = j * (w + 1) + i;
                    int b = a + 1;
                    int c = a + (w + 1);
                    int d = c + 1;
                    tris[t++] = a;
                    tris[t++] = c;
                    tris[t++] = b;
                    tris[t++] = b;
                    tris[t++] = c;
                    tris[t++] = d;
                }

            var mesh = new Mesh
            {
                name = $"SN-Terrain-{ci}-{cj}",
                vertices = verts,
                uv = uvs,
                triangles = tris,
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(mesh.name); // 3d-go
            go.transform.SetParent(parent, false);
            go.layer = PhysicsLayers.Statics; // источник высоты для всей игры
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            // Физматериал поверхности: без него колёса буксуют — юниты ползли
            // ~1 м/с по спец-карте (плейтест 2026-10-30). Копируем материал
            // нативного террейна (по трению), фолбэк — свой 1.0/1.0 Maximum.
            collider.sharedMaterial = SurfaceMaterial();
        }

        private static PhysicMaterial _surface;

        private static PhysicMaterial SurfaceMaterial()
        {
            if (_surface != null)
                return _surface;
            foreach (PhysicMaterial pm in UnityEngine.Resources.FindObjectsOfTypeAll<PhysicMaterial>())
            {
                if (pm == null || string.IsNullOrEmpty(pm.name))
                    continue;
                string n = pm.name.ToLowerInvariant();
                if (n.Contains("terrain") || n.Contains("ground") || n.Contains("dirt"))
                    return _surface = pm;
            }
            return _surface = new PhysicMaterial("SN-Terrain")
            {
                dynamicFriction = 1f,
                staticFriction = 1f,
                bounciness = 0f,
                frictionCombine = PhysicMaterialCombine.Maximum,
                bounceCombine = PhysicMaterialCombine.Minimum,
            };
        }

        /// <summary>Материал рельефа: URP Lit с текстурой цвета (fallback Standard).
        /// URP Lit читает _BaseMap — material.mainTexture (он же _MainTex) его не
        /// заполняет, и рельеф рендерился белым однотонным (плейтест 2026-10-29).</summary>
        public static Material MakeMaterial(Texture2D albedo)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            bool urp = shader != null;
            if (!urp)
                shader = Shader.Find("Standard");
            var material = new Material(shader) { name = "SN-Terrain" };
            material.mainTexture = albedo;               // Standard (_MainTex)
            if (urp)
                material.SetTexture("_BaseMap", albedo); // URP Lit
            return material;
        }
    }
}
