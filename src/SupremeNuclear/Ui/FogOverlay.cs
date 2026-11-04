using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Слой затемнения тумана войны (DESIGN §2.15, канон SupCom — 2 состояния:
    /// карта известна, памяти «чёрного непройденного» нет): под сенсорами —
    /// прозрачно, вне покрытия — затемнение. Два РАЗНЫХ разрешения:
    /// ЛОГИКА (сетка знания — штамп кругов Util.FogStampCircle по сенсорам
    /// FogOfWarService) и ТЕКСТУРА альфы — грубая (GridMax ячеек, билинейный
    /// фильтр сглаживает), ГЕОМЕТРИЯ драпа — МЕЛКАЯ с ТОЧНЫМИ высотами
    /// (MeshMax узлов на сторону): вуаль обязана лежать НАД рельефом всей
    /// картой — грубый драп (v0.146.1) тонул в дюнах между узлами, и полкарты
    /// «прорезалось» светлыми многоугольниками (плейтест 2026-11-04).
    /// Источник высот: RuntimeMap.LastHeightmap (генераторы карт) — выборка
    /// ТРЕУГОЛЬНАЯ, ровно по триангуляции меша террейна → поверхность
    /// совпадает с землёй до метра (сверяем пробами против GroundYNoUnits:
    /// расхождение = сдвиг origin после генерации → фолбэк); иначе — пакетные
    /// рейкасты (SampleBatch точек за скан, прогресс ~2–6 с; до готовности
    /// мировой меш не рисуем, вуаль миникарты работает сразу) + сепарабельный
    /// max-фильтр ±1 узел против провисания хорды. Подъём: 3 м + 0.1×шаг узла.
    /// Шаги меша по осям СВОИ и кратны домену текстуры (UV выровнены точно).
    /// Отрисовка — Graphics.DrawMesh из Update ДО рендера (№16); та же
    /// текстура — вуаль миникарты (RawImage uvRect окна зума). Вершины
    /// запечены относительно корня сцены (FloatingOrigin несёт его — урок
    /// TaxiSurface). Локальная визуализация, без сети.
    /// </summary>
    internal static class FogOverlay
    {
        private const int GridMax = 320;   // логическая сетка/текстура: ячеек по большей стороне
        private const float GridM = 150f;  // целевой размер ЛОГИЧЕСКОЙ ячейки, м
        private const int MeshMax = 256;   // геометрия драпа: узлов по большей стороне (~66k вершин)
        private const float LiftM = 3f;    // базовый подъём над рельефом/морем
        private const float LiftPerStepM = 0.1f; // запас на провисание хорды между узлами (фолбэк)
        private const int SampleBatch = 4096; // рейкастов высот за скан (фолбэк), ~2–6 с на карту
        private const float ProbeEpsM = 8f;  // допуск пробы «heightmap совпадает с миром», м

        private static bool[] _cells;
        private static Color32[] _pixels;
        private static Texture2D _texture;
        private static Mesh _mesh;
        private static Material _mat;
        private static Transform _root;      // корень сцены: FloatingOrigin несёт его
        private static Vector2 _min;
        private static float _cellM;
        private static int _nx, _nz;
        private static bool _built;

        // Высоты драпа: (meshNx+1)×(meshNz+1); raycast-фолбэк заполняет порциями.
        private static float[] _heights;
        private static int _meshNx, _meshNz;
        private static float _stepX, _stepZ; // шаги узлов; meshNx*_stepX == _nx*_cellM (домен текстуры)
        private static int _sampled;
        private static bool _heightsReady;

        /// <summary>Текстура затемнения для вуали миникарты (null до первой сборки).</summary>
        internal static Texture2D Texture => _texture;

        /// <summary>Пересчёт сетки знания по сенсорам текущего скана (звонит
        /// FogOfWarService.Tick); настройка затемнения выключена — оверлей гаснет.</summary>
        internal static void Update(List<Vector2> sensorPos, List<float> sensorRange)
        {
            if (!Settings.FogDimming)
            {
                SetEnabled(false);
                return;
            }
            EnsureBuilt();
            if (!_built)
                return;
            if (!_heightsReady)
                SampleHeightsBatch(); // мировой меш ещё строится — вуаль миникарты уже живёт

            for (int i = 0; i < _cells.Length; i++)
                _cells[i] = false;
            // ПРОСТРАНСТВО: сенсоры — в текущих мировых координатах, а меш запечён
            // в мировых НА МОМЕНТ СБОРКИ и потом физически едет с корнем сцены
            // (FloatingOrigin двигает корневые GO). Индекс ячейки = позиция в
            // системе меша: мировая − позиция корня (на сборке корень в нуле).
            // Плюс одна ячейка запаса — граница круга не мерцает при движении
            // юнита между сканами (0.25 с).
            Vector3 rootPos = _root.position;
            for (int i = 0; i < sensorPos.Count; i++)
            {
                int cx = Mathf.RoundToInt((sensorPos[i].x - rootPos.x - _min.x) / _cellM);
                int cz = Mathf.RoundToInt((sensorPos[i].y - rootPos.z - _min.y) / _cellM);
                Util.FogStampCircle(_cells, _nx, _nz, cx, cz,
                    Mathf.Max(1, Mathf.CeilToInt(sensorRange[i] / _cellM)) + 1);
            }

            byte dim = (byte)Mathf.RoundToInt(Mathf.Clamp01(Settings.FogDimAlpha) * 255f);
            var clearPx = new Color32(0, 0, 0, 0);
            var fogPx = new Color32(0, 0, 0, dim);
            for (int i = 0; i < _cells.Length; i++)
                _pixels[i] = _cells[i] ? clearPx : fogPx;
            _texture.SetPixels32(_pixels);
            _texture.Apply(false, false);
            _active = true;
        }

        private static bool _active;

        internal static void SetEnabled(bool on) => _active = on;

        /// <summary>Затемнение рисуется каждый кадр из Update ДО рендера (№16) —
        /// вызов из активной части CommanderModeController.UpdateSafe. Мировой
        /// меш — только после готовности высот (генераторы карт — сразу,
        /// сток-карты — рейкасты пару секунд).</summary>
        public static void Render()
        {
            if (!_active || !_heightsReady || _mesh == null || _root == null)
                return;
            Graphics.DrawMesh(_mesh, _root.localToWorldMatrix, _mat, 0, null, 0, null,
                UnityEngine.Rendering.ShadowCastingMode.Off, false);
        }

        private static void EnsureBuilt()
        {
            if (_built)
                return;
            if (!GameApi.TryGetMapBounds(out Vector2 min, out Vector2 max))
                return;
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null)
                return;

            _min = min;
            float sizeX = max.x - min.x, sizeZ = max.y - min.y;
            _cellM = Mathf.Max(GridM, Mathf.Max(sizeX, sizeZ) / GridMax);
            _nx = Mathf.Max(2, Mathf.CeilToInt(sizeX / _cellM));
            _nz = Mathf.Max(2, Mathf.CeilToInt(sizeZ / _cellM));

            // Домен тумана = _nx×_cellM по X (≥ размер карты); меш покрывает его
            // ЦЕЛИКОМ своим числом узлов — UV меша и текстуры выровнены точно.
            float spanX = _cellM * _nx, spanZ = _cellM * _nz;
            _meshNx = Mathf.Max(2, Mathf.CeilToInt(spanX / Mathf.Max(spanX, spanZ) * MeshMax));
            _meshNz = Mathf.Max(2, Mathf.CeilToInt(spanZ / Mathf.Max(spanX, spanZ) * MeshMax));
            _stepX = spanX / _meshNx;
            _stepZ = spanZ / _meshNz;

            // Корень сцены: после первого сдвига OriginShift (порог 1024 м) его
            // позиция ≠ 0 — вершины запекаем ОТНОСИТЕЛЬНО него (урок TaxiSurface).
            var rootGo = new GameObject("SupremeNuclear.FogOverlay"); // 3D-GO, не UI
            _root = rootGo.transform;

            _cells = new bool[_nx * _nz];
            _pixels = new Color32[_nx * _nz];
            _texture = new Texture2D(_nx, _nz, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _mat = new Material(sh) { renderQueue = 2980, mainTexture = _texture };
            _built = true;

            // Высоты драпа: heightmap генератора (точно по мешу террейна) либо
            // пакетные рейкасты. Пробами сверяем heightmap с текущим миром:
            // сдвиг origin после генерации расползается на километры.
            _heights = new float[(_meshNx + 1) * (_meshNz + 1)];
            Heightmap hm = RuntimeMap.LastHeightmap;
            bool hmOk = hm != null && ProbeHeightmap(hm, min, max);
            if (hmOk)
            {
                for (int j = 0; j <= _meshNz; j++)
                    for (int i = 0; i <= _meshNx; i++)
                        _heights[j * (_meshNx + 1) + i] = hm.SampleHeight(
                            min.x + _stepX * i, min.y + _stepZ * j) + Datum.LocalSeaY;
                _heightsReady = true;
                BuildMesh();
                Log.Info("FogOverlay: драп по heightmap генератора (" + _meshNx + "×" + _meshNz +
                         " узлов, сетка знания " + _nx + "×" + _nz + ", ячейка " + _cellM.ToString("F0") + " м)");
            }
            else
            {
                _sampled = 0;
                Log.Info("FogOverlay: heightmap нет/не совпал с миром — драп рейкастами " +
                         ((_meshNx + 1) * (_meshNz + 1)).ToString() + " точек по " + SampleBatch + " за скан");
            }
        }

        /// <summary>Пробы «heightmap в текущем мире»: 5 точек карты против
        /// GroundYNoUnits; расхождение > ProbeEpsM — мир сдвинут origin'ом после
        /// генерации (или карта стоковая) → честный рейкаст-фолбэк.</summary>
        private static bool ProbeHeightmap(Heightmap hm, Vector2 min, Vector2 max)
        {
            for (int p = 0; p < 5; p++)
            {
                float fx = p == 4 ? 0.5f : (p % 3) * 0.35f + 0.15f;
                float fz = p < 3 ? (p == 1 ? 0.5f : 0.2f) : 0.8f;
                float x = Mathf.Lerp(min.x, max.x, fx);
                float z = Mathf.Lerp(min.y, max.y, fz);
                float ray = GameApi.GroundYNoUnits(x, z);
                float sampled = hm.SampleHeight(x, z) + Datum.LocalSeaY;
                if (Mathf.Abs(ray - sampled) > ProbeEpsM)
                    return false;
            }
            return true;
        }

        /// <summary>Пакетное сэмплирование высот рейкастами (фолбэк без
        /// heightmap): SampleBatch точек за вызов, меш строится по готовности.</summary>
        private static void SampleHeightsBatch()
        {
            int stride = _meshNx + 1;
            int total = stride * (_meshNz + 1);
            int done = 0;
            while (_sampled < total && done < SampleBatch)
            {
                int i = _sampled % stride;
                int j = _sampled / stride;
                _heights[_sampled] = GameApi.GroundYNoUnits(
                    _min.x + _stepX * i, _min.y + _stepZ * j);
                _sampled++;
                done++;
            }
            if (_sampled < total)
                return;
            // Сепарабельный max-фильтр ±1 узел (два прохода): верхняя огибающая
            // против провисания хорды между разреженными узлами — вуаль не
            // зарывается в выпуклостях между сэмплами.
            var tmp = new float[_heights.Length];
            for (int j = 0; j <= _meshNz; j++)
                for (int i = 0; i <= _meshNx; i++)
                {
                    float h = _heights[j * stride + i];
                    if (i > 0) h = Mathf.Max(h, _heights[j * stride + i - 1]);
                    if (i < _meshNx) h = Mathf.Max(h, _heights[j * stride + i + 1]);
                    tmp[j * stride + i] = h;
                }
            for (int j = 0; j <= _meshNz; j++)
                for (int i = 0; i <= _meshNx; i++)
                {
                    float h = tmp[j * stride + i];
                    if (j > 0) h = Mathf.Max(h, tmp[(j - 1) * stride + i]);
                    if (j < _meshNz) h = Mathf.Max(h, tmp[(j + 1) * stride + i]);
                    _heights[j * stride + i] = h;
                }
            _heightsReady = true;
            BuildMesh();
            Log.Info("FogOverlay: драп по рейкастам готов (" + _meshNx + "×" + _meshNz + " узлов)");
        }

        /// <summary>Меш драпа: (meshN+1)² вершин на рельефе (море — уровень
        /// LocalSeaY) + подъём; UV — нормализованные по домену текстуры знания.
        /// Запечено минус позиция корня (ездит с миром после сдвигов).</summary>
        private static void BuildMesh()
        {
            int stride = _meshNx + 1;
            int vw = stride, vh = _meshNz + 1;
            float lift = LiftM + Mathf.Max(_stepX, _stepZ) * LiftPerStepM;
            var verts = new Vector3[vw * vh];
            var uv = new Vector2[vw * vh];
            var tris = new int[_meshNx * _meshNz * 6];
            for (int j = 0; j < vh; j++)
            {
                for (int i = 0; i < vw; i++)
                {
                    float x = _min.x + _stepX * i;
                    float z = _min.y + _stepZ * j;
                    float y = Mathf.Max(_heights[j * stride + i], Datum.LocalSeaY) + lift;
                    verts[j * vw + i] = new Vector3(x, y, z);
                    uv[j * vw + i] = new Vector2(i / (float)_meshNx, j / (float)_meshNz);
                }
            }
            Vector3 rootPos = _root.position;
            for (int i = 0; i < verts.Length; i++)
                verts[i] -= rootPos;
            int t = 0;
            for (int j = 0; j < _meshNz; j++)
            {
                for (int i = 0; i < _meshNx; i++)
                {
                    int a = j * vw + i, b = a + 1, c = a + vw, d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }
            _mesh = new Mesh { name = "fog-overlay" };
            _mesh.SetVertices(verts);
            _mesh.SetUVs(0, uv);
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateBounds();
        }

        /// <summary>Сцена умерла — меш/текстура/корень тоже (границы новой карты
        /// другие, пересборка ленивая при следующем скане).</summary>
        public static void Reset()
        {
            _built = false;
            _active = false;
            _heightsReady = false;
            _sampled = 0;
            _heights = null;
            _cells = null;
            _pixels = null;
            if (_texture != null)
            {
                Object.Destroy(_texture);
                _texture = null;
            }
            if (_mesh != null)
            {
                Object.Destroy(_mesh);
                _mesh = null;
            }
            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
                _root = null;
            }
            _mat = null;
        }
    }
}
