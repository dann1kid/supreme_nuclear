using System.Collections.Generic;
using NuclearOption.Effects;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace SupremeNuclear
{
    /// <summary>
    /// Визуальный дрессинг спец-карты (v0.154): ДЕКАЛИ при сборке — DecalProjector
    /// из стокового scorchMarkDecal с тинтом под вид пятна (грязь берега / износ
    /// у коридоров и баз / проталины равнины); ШРАМЫ при включении карты —
    /// AddBlast в BlastManager стека: выжженное несёт _Global_BlastMap (сериал
    /// читает наш сплат-шейдер), трава выцветает и деревья вырубаются нативно.
    /// Споты — ScatterPlan (детерминизм сида, симметрия карты). Декали идут
    /// МИМО EffectManager (его очередь 20 — для транзиентов), живут в префабе
    /// карты и умирают с ним.
    /// </summary>
    internal static class TerrainDressing
    {
        /// <summary>Тинты видов декалей (Kind из ScatterPlan.Blob).</summary>
        private static readonly Color[] Tints =
        {
            new Color(0.45f, 0.38f, 0.28f), // 0 — грязь берега
            new Color(0.24f, 0.22f, 0.19f), // 1 — износ (потёртость)
            new Color(0.66f, 0.60f, 0.44f), // 2 — проталина (светлее грунта)
        };

        /// <summary>Декали в «префаб» карты (root неактивен — включится нативным
        /// Instantiate клона). Вызывается из RuntimeMap.EnsureRegistered.</summary>
        public static void BuildDecals(GameObject root, Heightmap hm, WorldGen world,
            ScenarioTemplate.TerrainSettings t, int seed)
        {
            try
            {
                GameObject proto = GameAssets.i != null ? GameAssets.i.scorchMarkDecal : null;
                DecalProjector protoProjector = proto == null
                    ? null
                    : proto.GetComponent<DecalProjector>();
                if (protoProjector == null)
                {
                    Log.Info("MapDress: scorchMarkDecal стока недоступен — декали пропущены");
                    return;
                }
                ScatterPlan.Constraints c = ScatterPlan.From(hm, world, t);
                List<ScatterPlan.Blob> blobs = ScatterPlan.Decals(c, t, seed);
                int placed = 0;
                foreach (ScatterPlan.Blob b in blobs)
                {
                    float h = hm.SampleHeight(b.X, b.Z);
                    GameObject go = Object.Instantiate(proto, root.transform);
                    go.name = "SN-Decal" + placed;
                    go.transform.localPosition = new Vector3(b.X, h + 0.6f, b.Z);
                    go.transform.localRotation = Quaternion.LookRotation(Vector3.down); // канон Shockwave
                    var pr = go.GetComponent<DecalProjector>();
                    if (pr == null)
                    {
                        Object.Destroy(go);
                        continue;
                    }
                    pr.size = new Vector3(b.Radius * 2f, b.Radius * 2f,
                        Mathf.Max(4f, b.Radius * 0.5f));
                    pr.material = new Material(pr.material); // свой тинт на декаль
                    Color tint = Tints[Mathf.Clamp(b.Kind, 0, Tints.Length - 1)];
                    pr.material.color = tint * Mathf.Lerp(0.7f, 1f, b.Strength);
                    placed++;
                }
                Log.Info($"MapDress: декалей поставлено {placed} (спотов {blobs.Count})");
            }
            catch (System.Exception e)
            {
                Log.Warn("MapDress: декали не построились — " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Шрамы при включении карты (RuntimeMapPatch.AfterApplyMapSettings):
        /// очередь AddBlast разберёт DetailRenderer.LateUpdate — воронки на сплате
        /// + нативная реакция травы/деревьев. Сид — тот же, что строил мир.</summary>
        public static void OnMapEnabled(MapSettings mapSettings, ScatterPlan.Constraints c,
            ScenarioTemplate.TerrainSettings t, int seed)
        {
            try
            {
                if (mapSettings == null || c == null || t == null || t.battleScars <= 0f)
                    return;
                BlastManager bm = mapSettings.GetComponentInChildren<BlastManager>(true);
                if (bm == null)
                {
                    Log.Info("MapDress: BlastManager в стеке нет — шрамы пропущены");
                    return;
                }
                if (bm.mapSettings != mapSettings)
                    bm.mapSettings = mapSettings; // UV воронок — строго по НАШЕМУ MapSize
                List<ScatterPlan.Blob> scars = ScatterPlan.Scars(c, t, seed);
                int queued = 0;
                foreach (ScatterPlan.Blob s in scars)
                {
                    // Порог DrawBlast — radius×2 (множитель AddBlast) ≥ 80 м;
                    // планировщик кладёт минимум 60 м — все проходят.
                    bm.AddBlast(new GlobalPosition(s.X, c.SampleHeight(s.X, s.Z), s.Z), s.Radius);
                    queued++;
                }
                Log.Info($"MapDress: шрамов в очередь воронок — {queued}");
            }
            catch (System.Exception e)
            {
                Log.Warn("MapDress: шрамы не запеклись — " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
