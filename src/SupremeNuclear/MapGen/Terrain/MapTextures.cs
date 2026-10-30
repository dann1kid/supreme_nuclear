using System;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// CPU-текстуры спец-карты из heightmap. TerrainColorMap ОБЯЗАН оставаться
    /// readable: MapSettings.GetTerrainColorAtCoordinate делает GetPixel по
    /// uv = ((x + size/2)/size, (z + size/2)/size) — клетка heightmap (i, j) с
    /// мировыми координатами x = (i + 0.5)·step − size/2 даёт ровно пиксель (i, j).
    /// Палитра — hex-строки шаблона, от низин к вершинам; вода — отдельным цветом.
    /// </summary>
    internal static class MapTextures
    {
        public static Texture2D TerrainColor(Heightmap hm, ScenarioTemplate.TerrainSettings t)
        {
            Color[] palette = ParsePalette(t.palette);
            Color water = ParseHex(t.waterColor);
            var tex = new Texture2D(hm.W, hm.H, TextureFormat.RGBA32, false, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "SN-TerrainColor";
            var px = new Color[hm.W * hm.H];
            float span = Math.Max(1e-3f, hm.Max - hm.Min);
            for (int j = 0; j < hm.H; j++)
                for (int i = 0; i < hm.W; i++)
                {
                    if (hm.Water[i, j])
                    {
                        px[j * hm.W + i] = water;
                        continue;
                    }
                    Color c = Sample(palette, (hm.Height[i, j] - hm.Min) / span);
                    // Читаемость рельефа: затенение по крутизне (склоны темнее) + мелкий
                    // яркостный шум — плоский по высоте цвет не давал разглядеть рельеф.
                    c *= Shade(hm, i, j);
                    px[j * hm.W + i] = c;
                }
            tex.SetPixels(px);
            tex.Apply(false, false); // makeNoLongerReadable = false — пиксели нужны игре
            return tex;
        }

        /// <summary>Множитель цвета клетки: 1 на равнине, темнее на склоне
        /// (градиент высоты к соседям), ±шум для детализации.</summary>
        private static float Shade(Heightmap hm, int i, int j)
        {
            int i1 = Math.Min(hm.W - 1, i + 1), j1 = Math.Min(hm.H - 1, j + 1);
            float dh = Math.Max(
                Math.Abs(hm.Height[i, j] - hm.Height[i1, j]),
                Math.Abs(hm.Height[i, j] - hm.Height[i, j1])) / hm.Step;
            float slope = Mathf.Clamp01(dh * 3f); // 0 на равнине, ~1 у 18°+
            float noise = Noise2D(i * 7, j * 13) - 0.5f;
            return 1f - 0.35f * slope + noise * 0.12f;
        }

        private static float Noise2D(int x, int y)
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }

        /// <summary>Глубины океана (для воды за пределами суши): серая шкала 0..1.</summary>
        public static Texture2D OceanDepth(Heightmap hm, float maxDepthM)
        {
            var tex = new Texture2D(hm.W, hm.H, TextureFormat.RGBA32, false, true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "SN-OceanDepth";
            var px = new Color[hm.W * hm.H];
            float maxDepth = Math.Max(1f, maxDepthM);
            for (int j = 0; j < hm.H; j++)
                for (int i = 0; i < hm.W; i++)
                {
                    float d = hm.Water[i, j]
                        ? Mathf.Clamp01((hm.SeaLevel - hm.Height[i, j]) / maxDepth)
                        : 0f;
                    px[j * hm.W + i] = new Color(d, d, d, 1f);
                }
            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Одноцветная «базовая» текстура океана (макро-цвет воды).</summary>
        public static Texture2D OceanBase(ScenarioTemplate.TerrainSettings t)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            tex.name = "SN-OceanBase";
            Color c = ParseHex(t.waterColor);
            var px = new Color[16];
            for (int i = 0; i < 16; i++)
                px[i] = c;
            tex.SetPixels(px);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Спрайт миникарты (MapImage) — те же цвета, квадрат.</summary>
        public static Sprite MapSprite(Heightmap hm, ScenarioTemplate.TerrainSettings t)
        {
            int res = 512;
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
            tex.name = "SN-MapImage";
            Color[] palette = ParsePalette(t.palette);
            Color water = ParseHex(t.waterColor);
            var px = new Color[res * res];
            float span = Math.Max(1e-3f, hm.Max - hm.Min);
            for (int j = 0; j < res; j++)
                for (int i = 0; i < res; i++)
                {
                    int si = (int)((i + 0.5f) / res * hm.W);
                    int sj = (int)((j + 0.5f) / res * hm.H);
                    px[j * res + i] = hm.Water[si, sj]
                        ? water
                        : Sample(palette, (hm.Height[si, sj] - hm.Min) / span);
                }
            tex.SetPixels(px);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0f, 0f, res, res), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Color Sample(Color[] palette, float f01)
        {
            if (palette == null || palette.Length == 0)
                return Color.green;
            if (palette.Length == 1)
                return palette[0];
            float x = Mathf.Clamp01(f01) * (palette.Length - 1);
            int i = Mathf.Min(palette.Length - 2, (int)x);
            return Color.Lerp(palette[i], palette[i + 1], x - i);
        }

        private static Color[] ParsePalette(string[] hex)
        {
            if (hex == null || hex.Length == 0)
                return new[] { new Color(0.3f, 0.45f, 0.25f) };
            var colors = new Color[hex.Length];
            for (int i = 0; i < hex.Length; i++)
                colors[i] = ParseHex(hex[i]);
            return colors;
        }

        private static Color ParseHex(string hex)
        {
            if (!ColorUtility.TryParseHtmlString(hex, out Color c))
            {
                Log.Warn("MapGen: не разобрать цвет '" + hex + "' — использую зелёный");
                c = new Color(0.3f, 0.45f, 0.25f);
            }
            return c;
        }
    }
}
