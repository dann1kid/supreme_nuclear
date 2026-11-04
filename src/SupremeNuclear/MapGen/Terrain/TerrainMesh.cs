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
        // 16×16 чанков: сетка 1024² (натив NO 81.9 км / 80 м) — это ~2.1 млн
        // треугольников; чанк 64×64 клеток (~8k трис) даёт кулингу и физике
        // разумные куски (8×8 на такой сетке было бы по 33k трис на коллайдер).
        private const int ChunksPerSide = 16;

        /// <param name="uvTileM">Метров на один UV-тик UV0: при sizeM — 0..1
        /// (макро-конвенция цветовой текстуры); при меньшем — «тайловый» UV
        /// ванильного террейна (uv0 = мир/uvTileM, шейдер Field тайлит деталь
        /// сам — как на родных картах). UV2 всегда 0..1 (макро-маска).</param>
        public static void Build(Transform parent, Heightmap hm, Material material, float uvTileM,
            System.Func<float, float, float, Color> vertexColor = null)
        {
            float tile = Mathf.Max(1f, uvTileM);
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
                    BuildChunk(parent, hm, material, tile, vertexColor, i0, i1, j0, j1, ci, cj);
                }
            }
        }

        private static void BuildChunk(Transform parent, Heightmap hm, Material material,
            float uvTileM, System.Func<float, float, float, Color> vertexColor,
            int i0, int i1, int j0, int j1, int ci, int cj)
        {
            int w = i1 - i0, h = j1 - j0;
            float half = hm.W * hm.Step * 0.5f;
            var verts = new Vector3[(w + 1) * (h + 1)];
            var uvs = new Vector3[verts.Length]; // TexCoord0 dim3 — как у ванили
            var uv2s = new Vector2[verts.Length];
            var colors = vertexColor != null ? new Color[verts.Length] : null;
            var tris = new int[w * h * 6];

            int v = 0;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++, v++)
                {
                    float x = i * hm.Step - half;
                    float z = j * hm.Step - half;
                    verts[v] = new Vector3(x, hm.SampleHeight(x, z), z);
                    // UV0 — тайловой конвенции материала (ваниль/наш albedo),
                    // UV2 — всегда макро 0..1 ((x+size/2)/size — как цветовая
                    // текстура и маска зон).
                    uvs[v] = new Vector3((x + half) / uvTileM, (z + half) / uvTileM, 0f);
                    uv2s[v] = new Vector2((x + half) / (half * 2f), (z + half) / (half * 2f));
                    if (colors != null)
                        colors[v] = vertexColor(x, z, verts[v].y);
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
                uv2 = uv2s,
                colors = colors,
                triangles = tris,
            };
            mesh.RecalculateNormals();
            // ТАНГЕНТЫ обязательны: normal-деталь террейна (field_n в шейдере
            // Field) без них мёртвая — поверхность «пластилиновая» без
            // микрорельефа (скрин-эксперимент 2026-11-03: сток-маска фактуры
            // НЕ дала при живом Field — корень в отсутствии тангенстов).
            // TexCoord0 dim3 (SetUVs — mesh.uv = dim2 выпускал бы Float32×2):
            // ваниль несёт TexCoord0:Float32 dim3 — шейдер Field семплит деталь
            // по нему (мировые метры / uvTileM; эксперимент 2026-11-03).
            mesh.SetUVs(0, new List<Vector3>(uvs));
            mesh.RecalculateTangents();
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

        /// <summary>Материал рельефа — ФОЛБЭК (сток-материал недоступен): URP Lit
        /// с albedo (fallback Standard). URP Lit читает _BaseMap — material.mainTexture
        /// (он же _MainTex) его не заполняет, и рельеф рендерился белым однотонным
        /// (плейтест 2026-10-29). Основной путь — TerrainMaterial.Resolve.</summary>
        internal static Material MakeUrpMaterial(Texture2D albedo)
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
