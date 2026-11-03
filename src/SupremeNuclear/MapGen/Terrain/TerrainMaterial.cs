using System;
using NuclearOption.Effects;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Резолвер материала рельефа спец-карты. Приоритет — СТОК-материал террейна
    /// игры (найденный нативным маркером IsTerrain — физ-материал коллайдера в
    /// префабном TerrainHeightMap.terrainPhysicMaterials; плейтесты 2026-11-02:
    /// «крупнейший меш» = дерево, «слой Statics» = дорога). Террейн NO —
    /// Shader Graphs/Field: _Basecolor 2048² + _Normal 2048² (детальные) и
    /// _Colormask 1024² (маска зон), все scale 1×1 — на нашем UV 0..1 они
    /// растянуты на ВСЮ карту. Поэтому сток берём КЛОНОМ: _Colormask ← наша
    /// зональная карта (пляжи/скалы/провинции в нативном шейдере), детальные
    /// текстуры — тайлом по миру (SetTextureScale = размер/период). Клон
    /// безопасен для IsTerrain: матч у сток-карт идёт по физ-материалам
    /// КОЛЛАЙДЕРОВ, которые мы не трогаем. Недоступен — URP Lit + наш albedo.
    /// Свойства сток-шедера дампов в лог при первом захвате (правило 13).
    /// </summary>
    internal static class TerrainMaterial
    {
        private static bool _dumped;

        /// <summary>Albedo (зоны+зерно, высокое разрешение) последней спец-карты:
        /// префаб DontDestroyOnLoad живёт, статик переживает сцены — UpgradeClone
        /// отдаёт его в _Colormask клона (TerrainColorMap 1024² на весь мир
        /// слишком мелок: 80 м/тексель, плейтест 2026-11-03 «очень низкое
        /// разрешение» — шейдер Field красит верхним слоем маски).</summary>
        private static Texture2D _lastAlbedo;

        internal static void RememberAlbedo(Texture2D albedo) => _lastAlbedo = albedo;

        /// <summary>План материала рельефа: сам материал + период UV0 нашего меша
        /// (метров на один UV-тик: uv0 = мир/UvTileM; при UvTileM = sizeM — 0..1).</summary>
        public sealed class Plan
        {
            public Material Mat;
            public float UvTileM;
        }

        /// <summary>Материал рельефа и UV-канон нашего меша. Сток с «тайловым»
        /// UV0 (uv0max &gt; 1.5, как у ванили): клон БЕЗ tiling (шейдер читает
        /// UV как есть), наш меш получает uv0 = мир/период — шейдер работает
        /// ровно как на родной карте. Сток с UV 0..1 (семпл по миру): меш 0..1 +
        /// страховочный tiling клона. Фолбэк — URP Lit + albedo (меш 0..1).
        /// Маска зон подменяется нашей, только если у ванильного чанка есть
        /// второй UV-канал (макро-маска не обязана жить в тайл-UV0).</summary>
        public static Plan ResolvePlan(Texture2D albedo, Texture2D colorMask,
            float sizeM, float tileM)
        {
            // Разведка: доступен ли Complex Lit (detail-слой albedo) и какие
            // URP-шейдеры вообще живут в памяти билда (стрipped-проверка).
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (Shader sh in Resources.FindObjectsOfTypeAll<Shader>())
                    if (sh.name.Contains("Universal Render Pipeline"))
                        sb.Append(sh.name.Substring(sh.name.LastIndexOf('/') + 1)).Append(' ');
                Log.Info("MapGen: URP-шейдеры в памяти: " + sb);
            }
            catch (System.Exception e)
            {
                Log.Warn("MapGen: перечисление шейдеров не удалось — " + e.Message);
            }
            ChunkInfo chunk = FindStockChunk();
            if (chunk != null)
            {
                Dump(chunk.Mat, 1);
                // v4.20 (разведка 2026-11-03): Complex Lit ВЫРЕЗАН из билда,
                // палитра стока = 1 материал (field) — двухслойный albedo и
                // разные покрытия недоступны. Финальная схема — ГИБРИД: наш
                // albedo (зоны + ПЯТНИСТАЯ суб-текстура, юзер-канон «как
                // Neroxis») + ванильная нормал-деталь field_n тайлом 1 м.
                Texture detailNormal = chunk.Mat.HasProperty("_Normal")
                    ? chunk.Mat.GetTexture("_Normal")
                    : null;
                if (detailNormal != null)
                {
                    Material hybrid = TerrainMesh.MakeUrpMaterial(albedo);
                    hybrid.name = "SN-TerrainHybrid";
                    hybrid.SetTextureScale("_BaseMap", new Vector2(1f / sizeM, 1f / sizeM));
                    hybrid.SetTexture("_BumpMap", detailNormal);
                    hybrid.SetTextureScale("_BumpMap", Vector2.one); // тайл 1 м (UV меша — метры)
                    if (hybrid.HasProperty("_BumpScale"))
                        hybrid.SetFloat("_BumpScale", 1f);
                    // DETAIL-слой URP Lit (плейтест 2026-11-03 «вблизи размазано,
                    // вид из кабины»): R-канал detail-карты модулирует albedo СВОИМ
                    // тайлом — цепляем ванильную ЦВЕТОВУЮ фактуру field_b периодом
                    // 1 м: микро-деталь цвета у земли, наши зоны — базой.
                    Texture detailAlbedo = chunk.Mat.HasProperty("_Basecolor")
                        ? chunk.Mat.GetTexture("_Basecolor")
                        : null;
                    if (detailAlbedo != null && hybrid.HasProperty("_DetailMap"))
                    {
                        hybrid.SetTexture("_DetailMap", detailAlbedo);
                        hybrid.SetTextureScale("_DetailMap", Vector2.one); // тайл 1 м
                        if (hybrid.HasProperty("_DetailAlbedoMapScale"))
                            hybrid.SetFloat("_DetailAlbedoMapScale", 1f);
                        Log.Info("MapGen: Detail-слой жив: " + detailAlbedo.name + " (1 м)");
                    }
                    else
                    {
                        Log.Info("MapGen: Detail-слой недоступен (" +
                                 (detailAlbedo != null ? "нет свойства _DetailMap" : "нет field_b") +
                                 ") — только нормал-деталь");
                    }
                    Log.Info("MapGen: материал рельефа — ГИБРИД: albedo " + AlbedoSize(albedo) +
                             " + нормал-деталь " + detailNormal.name + " (1 м)");
                    return new Plan { Mat = hybrid, UvTileM = 1f };
                }
                Material variant = new Material(chunk.Mat) { name = "SN-TerrainStock" };
                if (albedo != null && variant.HasProperty("_Colormask"))
                {
                    variant.SetTexture("_Colormask", albedo);
                    variant.SetTextureScale("_Colormask", new Vector2(1f / sizeM, 1f / sizeM));
                }
                Log.Info("MapGen: нормаль стока недоступна — Field-клон с нашей маской");
                return new Plan { Mat = variant, UvTileM = 1f };
            }
            Log.Info("MapGen: сток-материал недоступен — URP Lit + albedo " +
                     AlbedoSize(albedo));
            return new Plan { Mat = TerrainMesh.MakeUrpMaterial(albedo), UvTileM = sizeM };
        }

        private static string AlbedoSize(Texture2D albedo) =>
            albedo != null ? albedo.width + "×" + albedo.height : "?";

        /// <summary>Сведения о ванильном террейн-чанке: материал + UV-конвенция
        /// (uv0 max — «тайловый» ли меш; uv2 — канал макро-маски).</summary>
        internal sealed class ChunkInfo
        {
            public Material Mat;
            public float Uv0Max;
            public bool HasUv2;
            public int Vertices;
            public float SizeM;
            public float MapSizeM; // MapSize.x ванильной карты (период детализации Field)
            public string MapName;
        }

        private static ChunkInfo FindStockChunk()
        {
            try
            {
                var field = HarmonyLib.AccessTools.Field(typeof(TerrainHeightMap), "terrainMaterials");
                foreach (TerrainHeightMap thm in UnityEngine.Object.FindObjectsOfType<TerrainHeightMap>(true))
                {
                    if (thm == null || thm.terrainPhysicMaterials == null || thm.terrainPhysicMaterials.Length == 0)
                        continue;
                    ChunkInfo info = InspectTerrainChunk(thm.gameObject, thm.terrainPhysicMaterials);
                    if (info != null)
                    {
                        info.MapName = "сцена:" + thm.gameObject.name;
                        return info;
                    }
                }
                foreach (MapSettingsManager manager in
                         UnityEngine.Object.FindObjectsOfType<MapSettingsManager>(true))
                {
                    if (manager?.Maps == null)
                        continue;
                    foreach (MapSettingsManager.Map entry in manager.Maps)
                    {
                        TerrainHeightMap thm = entry?.Prefab == null
                            ? null
                            : entry.Prefab.GetComponentInChildren<TerrainHeightMap>(true);
                        if (thm == null || thm.terrainPhysicMaterials == null)
                            continue;
                        ChunkInfo info = InspectTerrainChunk(entry.Prefab.gameObject, thm.terrainPhysicMaterials);
                        if (info != null)
                        {
                            info.MapName = entry.Details != null ? entry.Details.PrefabName : "?";
                            info.MapSizeM = entry.Prefab.MapSize.x; // период детализации Field
                            return info;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn("MapGen: поиск сток-чанка не удался — " + e.GetType().Name + ": " + e.Message);
            }
            return null;
        }

        /// <summary>Террейн-чанк по нативному маркеру IsTerrain (физ-материал
        /// коллайдера) + UV-инспекция крупнейшего: uv0/uv2-диапазоны в лог.</summary>
        private static ChunkInfo InspectTerrainChunk(MapSettings prefab)
        {
            if (prefab == null)
                return null;
            TerrainHeightMap thm = prefab.GetComponentInChildren<TerrainHeightMap>(true);
            if (thm == null || thm.terrainPhysicMaterials == null)
                return null;
            ChunkInfo info = InspectTerrainChunk(prefab.gameObject, thm.terrainPhysicMaterials);
            if (info != null)
                info.MapSizeM = prefab.MapSize.x;
            return info;
        }

        private static ChunkInfo InspectTerrainChunk(GameObject root, PhysicMaterial[] phys)
        {
            Material best = null;
            Mesh bestMesh = null;
            float bestSize = 0f;
            int vertices = 0;
            bool hasUv0 = false;
            Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue), uvMax = new Vector2(float.MinValue, float.MinValue);
            bool hasUv2 = false;
            foreach (MeshCollider col in root.GetComponentsInChildren<MeshCollider>(true))
            {
                if (col == null || col.sharedMaterial == null)
                    continue;
                if (!System.Array.Exists(phys, p => p == col.sharedMaterial))
                    continue;
                MeshRenderer r = col.GetComponent<MeshRenderer>();
                MeshFilter filter = col.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (r == null || mesh == null || r.sharedMaterial == null || r.sharedMaterial.shader == null)
                    continue;
                Vector3 s = Vector3.Scale(mesh.bounds.size, r.transform.lossyScale);
                float size = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                if (size > bestSize)
                {
                    bestSize = size;
                    best = r.sharedMaterial;
                    bestMesh = mesh;
                    vertices = mesh.vertexCount;
                    Vector2[] uv = mesh.uv;
                    hasUv0 = uv != null && uv.Length > 0;
                    uvMin = new Vector2(float.MaxValue, float.MaxValue);
                    uvMax = new Vector2(float.MinValue, float.MinValue);
                    if (hasUv0)
                        foreach (Vector2 v in uv)
                        {
                            if (v.x < uvMin.x) uvMin.x = v.x;
                            if (v.y < uvMin.y) uvMin.y = v.y;
                            if (v.x > uvMax.x) uvMax.x = v.x;
                            if (v.y > uvMax.y) uvMax.y = v.y;
                        }
                    hasUv2 = mesh.uv2 != null && mesh.uv2.Length > 0;
                }
            }
            if (best == null)
                return null;
            // Плотность сетки ванили (м/вершину по кромке) — сравнение с нашей.
            float density = vertices > 0 ? bestSize / Mathf.Sqrt(vertices) : 0f;
            // ПОЛНЫЙ дамп vertex-атрибутов: Shader Graph может семплить деталь
            // по кастомному каналу (TEXCOORD3+), которого у нашего меша нет.
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (UnityEngine.Rendering.VertexAttributeDescriptor attr in
                         bestMesh.GetVertexAttributes())
                    sb.Append(attr.attribute).Append(':')
                      .Append(attr.format).Append(' ');
                Log.Info("MapGen:   атрибуты меша ванили: " + sb);
                // Семантика TexCoord0 (dimension 3/4 — mesh.uv его не видит):
                // первые вершины UV против локальных позиций — масштаб/смещение.
                var uvs4 = new System.Collections.Generic.List<Vector4>();
                bestMesh.GetUVs(0, uvs4);
                if (uvs4.Count > 0)
                {
                    Vector4 u0 = uvs4[0], u1 = uvs4[Mathf.Min(uvs4.Count - 1, 1000)];
                    Vector3 p0 = bestMesh.vertices[0],
                            p1 = bestMesh.vertices[Mathf.Min(uvs4.Count - 1, 1000)];
                    int dim = 0;
                    foreach (UnityEngine.Rendering.VertexAttributeDescriptor attr in
                             bestMesh.GetVertexAttributes())
                        if (attr.attribute == UnityEngine.Rendering.VertexAttribute.TexCoord0)
                            dim = attr.dimension;
                    Log.Info("MapGen:   TexCoord0 dim " + dim + ": v0 uv " + u0 + " pos " + p0 +
                             " | v1000 uv " + u1 + " pos " + p1);
                }
            }
            catch (System.Exception e)
            {
                Log.Warn("MapGen: дамп атрибутов не удался — " + e.Message);
            }
            Log.Info("MapGen: ванильный террейн-чанк — материал " + best.name +
                     ", uv0 " + (hasUv0
                         ? uvMin.x.ToString("F1") + ".." + uvMax.x.ToString("F1") +
                           " × " + uvMin.y.ToString("F1") + ".." + uvMax.y.ToString("F1")
                         : "НЕТ (шейдер по мировым координатам)") +
                     ", uv2 " + (hasUv2 ? "есть" : "нет") +
                     ", габарит " + bestSize.ToString("F0") + " м, вершин " + vertices +
                     " (~" + density.ToString("F1") + " м/верш)");
            return new ChunkInfo
            {
                Mat = best,
                Uv0Max = hasUv0 ? Mathf.Max(uvMax.x, uvMax.y) : 0f,
                HasUv2 = hasUv2,
                Vertices = vertices,
                SizeM = bestSize,
            };
        }

        /// <summary>Апгрейд материала клона карты ПОСЛЕ включения в GameWorld-сцене
        /// (патч №12 Postfix ApplyMapSettings): карта строится в редакторе, где
        /// сток-карты недоступны — префаб получает URP-фолбэк. Здесь сцена живая:
        /// фолбэк-чанки (их меши с UV 0..1!) получают клон стока с tiling —
        /// если ваниль юзает тайловый UV0, масштаб равен ванильному uv0Max;
        /// иначе sizeM/DetailTileM. Идемпотентно (имя уже не SN-Terrain).</summary>
        public static void UpgradeClone(MapSettings mapSettings)
        {
            if (mapSettings == null)
                return;
            try
            {
                ChunkInfo chunk = FindStockChunk();
                if (chunk == null)
                {
                    Log.Info("MapGen: сток-материал террейна недоступен и в сцене игры — " +
                             "рельеф остаётся на URP Lit + albedo");
                    return;
                }
                // v4.18: Field-клон с нашей маской на всю карту (меш старого
                // фолбэка — UV 0..1, масштаб маски 1/sizeM как раз 0..1 и даёт;
                // свежие Ctrl+G строят меш с UV-метрами — формула та же).
                Material variant = new Material(chunk.Mat) { name = "SN-TerrainStock" };
                Texture2D mask = _lastAlbedo != null ? _lastAlbedo : mapSettings.TerrainColorMap;
                if (mask != null && variant.HasProperty("_Colormask"))
                {
                    variant.SetTexture("_Colormask", mask);
                    variant.SetTextureScale("_Colormask",
                        new Vector2(1f / mapSettings.MapSize.x, 1f / mapSettings.MapSize.x));
                }
                int upgraded = 0;
                foreach (MeshRenderer r in mapSettings.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r == null || r.sharedMaterial == null || r.sharedMaterial.name != "SN-Terrain")
                        continue;
                    r.sharedMaterial = variant;
                    upgraded++;
                }
                if (upgraded > 0)
                    Log.Info("MapGen: материал рельефа апгрейднут на Field-клон с нашей маской (" +
                             chunk.Mat.shader.name + ") у " + upgraded + " чанков");
                // Контрольная лампа (скрины 2026-11-03: детальная фактура Field
                // на карте юзера не видна вовсе): что ФАКТИЧЕСКИ висит на чанках.
                MeshRenderer probe = null;
                foreach (MeshRenderer r in mapSettings.GetComponentsInChildren<MeshRenderer>(true))
                    if (r != null && r.sharedMaterial != null)
                    {
                        probe = r;
                        break;
                    }
                if (probe != null)
                    Log.Info("MapGen: фактический материал чанков клона — '" +
                             probe.sharedMaterial.name + "' / " + probe.sharedMaterial.shader.name +
                             " (маска " + (variant.HasProperty("_Colormask")
                                 ? (variant.GetTexture("_Colormask") != null
                                     ? variant.GetTexture("_Colormask").name + " " +
                                       variant.GetTexture("_Colormask").width + "×" +
                                       variant.GetTexture("_Colormask").height
                                     : "null")
                                 : "нет свойства") + ")");
            }
            catch (Exception e)
            {
                Log.Warn("MapGen: апгрейд материала клона не удался — " +
                         e.GetType().Name + ": " + e.Message);
            }
        }


        /// <summary>Однократный дамп свойств сток-материала в лог: по нему
        /// выбирается слот подстановки сгенерированных макро-текстур (итерация 2
        /// текстур v3 — свойства шейдера ассетные, из кода не видны).</summary>
        /// <summary>Снять 16×16 копию не-readable маски с GPU и дампнуть
        /// значения каналов: сетка 4×4 + средние по RGBA.</summary>
        private static void DumpMaskPixels(Texture2D src)
        {
            try
            {
                const int n = 16;
                var rt = RenderTexture.GetTemporary(n, n, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                UnityEngine.GL.sRGBWrite = false;
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var copy = new Texture2D(n, n, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, n, n), 0, 0);
                copy.Apply(false, false); // НЕ выгружать пиксели — читаем их ниже
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                Color[] px = copy.GetPixels();
                float r = 0f, g = 0f, b = 0f, a = 0f;
                foreach (Color c in px) { r += c.r; g += c.g; b += c.b; a += c.a; }
                int cnt = px.Length;
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < 16; k++)
                    sb.Append(px[k * 17].ToString("F2")).Append(" ");
                Log.Info("MapGen:   маска " + src.name + " 16×16 средние R " +
                         (r / cnt).ToString("F3") + " G " + (g / cnt).ToString("F3") +
                         " B " + (b / cnt).ToString("F3") + " A " + (a / cnt).ToString("F3") +
                         "; семплы: " + sb);
                UnityEngine.Object.Destroy(copy);
            }
            catch (System.Exception e)
            {
                Log.Warn("MapGen: GPU-съём маски не удался — " + e.Message);
            }
        }

        private static void Dump(Material m, int arrayLen)
        {
            if (_dumped)
                return;
            _dumped = true;
            try
            {
                Log.Info("MapGen: дамп сток-материала террейна — " + m.name +
                         " (массив " + arrayLen + "), шейдер " + m.shader.name);
                foreach (string prop in m.GetTexturePropertyNames())
                {
                    Texture tex = m.GetTexture(prop);
                    Vector2 scale = m.GetTextureScale(prop);
                    // field_c — не-readable ассет: снимаем GPU-копию (Blit →
                    // ReadPixels) и дампним каналы — семантика _Colormask
                    // (RGB-веса слоёв? альфа-интенсивность?) решает, как генерить
                    // нашу маску (плейтест 2026-11-03: макро-пятна не видны).
                    if (tex is Texture2D t2d && prop == "_Colormask")
                        DumpMaskPixels(t2d);
                    Log.Info("MapGen:   текстура " + prop + " = " +
                             (tex != null ? tex.name + " " + tex.width + "×" + tex.height : "null") +
                             ", scale " + scale.x.ToString("F2") + "×" + scale.y.ToString("F2"));
                }
                // Числовые свойства шейдера: масштабы детальности/тайлов Field
                // живут именно здесь (текстурные scale у него 1×1).
                Shader sh = m.shader;
                int count = sh.GetPropertyCount();
                for (int p = 0; p < count; p++)
                {
                    string name = sh.GetPropertyName(p);
                    var type = sh.GetPropertyType(p);
                    if (type == UnityEngine.Rendering.ShaderPropertyType.Float ||
                        type == UnityEngine.Rendering.ShaderPropertyType.Range)
                        Log.Info("MapGen:   число " + name + " = " + m.GetFloat(name).ToString("F4"));
                    else if (type == UnityEngine.Rendering.ShaderPropertyType.Vector)
                        Log.Info("MapGen:   вектор " + name + " = " + m.GetVector(name).ToString("F3"));
                }
            }
            catch (Exception e)
            {
                Log.Warn("MapGen: дамп материала не удался — " + e.Message);
            }
        }
    }
}
