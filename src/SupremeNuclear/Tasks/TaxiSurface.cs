using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Поверхность дорисованных рулёжек. ФИЗИКА — один непрерывный
    /// MeshCollider-лента на кусок (дорога/апрон): вершины лежат на рельефе
    /// (`GroundYNoUnits` + подъём), швов и ступенек нет — на стыках наклонённых
    /// BoxCollider шасси ловило кромку и ломалось (плейтест v0.84). Для шасси игры
    /// любой коллайдер с материалом ≠ террейну = тармак (LandingGear.onTarmac),
    /// материала нет → «бетон». ВИЗУАЛ — тот же меш через Graphics.DrawMesh
    /// (GO+MeshRenderer в этой игре не рендерится) + разметка геометрией:
    /// асфальт, белые кромки, жёлтый пунктир; превью маршрута — без физики.
    /// Хост-only: физика бортов — серверная.</summary>
    internal static class TaxiSurface
    {
        private class Piece
        {
            public Transform Transform;               // носитель MeshCollider
            public Mesh Mesh;                         // единая поверхность куска
            public Vector3[] Line;                    // осевая (дорога) — разметка
            public Vector3[] Left, Right;             // кромки — линии разметки
            public float Width;                       // ширина ленты (для TopAt)
        }

        private static Transform _root;
        private static readonly List<Piece> Pieces = new List<Piece>();
        private static readonly List<Mesh> Meshes = new List<Mesh>();
        private static Mesh _quad;
        private static Material _mat;       // асфальт: непрозрачный
        private static Material _decalMat;  // разметка/превью: прозрачный
        private static MaterialPropertyBlock _block;

        private const float WidthM = 24f;   // ширина полосы: габарит+крыло с запасом
        private const float LiftM = 0.12f;  // подъём над рельефом: не z-fight с террейном
        private const float StepM = 15f;    // шаг сэмплов — гладкость за рельефом
        private const float MaxGrade = 0.12f; // макс. уклон грейдированного полотна (12%)

        private static readonly Color Asfalt = new Color(0.34f, 0.34f, 0.36f, 1f);
        private static readonly Color Edge = new Color(0.85f, 0.85f, 0.85f, 0.8f);
        private static readonly Color Dash = new Color(0.95f, 0.8f, 0.2f, 0.85f);
        private static readonly Color PreviewTint = new Color(0.6f, 0.8f, 1f, 0.3f);

        private static readonly int ColorId = UnityEngine.Shader.PropertyToID("_Color");

        private static float SurfaceY(float x, float z) =>
            GameApi.GroundYNoUnits(x, z) + LiftM;

        /// <summary>Дорога-лента вдоль полилинии (точки — глобальные).
        /// graded=true — ВЫРАВНИВАНИЕ: сечение плоское, высота — «терраса»
        /// (наименьший профиль НАД рельефом с лимитом уклона) — ВПП/площадки;
        /// иначе лента обтекает рельеф (короткие рулёжки).
        /// Грейдированная лента НЕ МОЖЕТ лечь ниже УЖЕ построенных лент в местах
        /// перекрытия (каждая станция прижимается к TopAt — стык с ВПП заподлицо
        /// по всей зоне пересечения, не только на концах).
        /// startY — «штифт» высоты начала (полотно апрона ангара): профиль
        /// гарантированно НЕ НИЖЕ его — рулёжка сходит с плиты пандусом
        /// ≤ MaxGrade, а не обрывом юбки.</summary>
        public static void Register(IReadOnlyList<GlobalPosition> points, float width = WidthM,
            bool graded = false, float startY = float.NaN)
        {
            if (points == null || points.Count < 2)
                return;
            var center = new List<Vector3>();
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 a = points[i].ToLocalPosition();
                Vector3 b = points[i + 1].ToLocalPosition();
                float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
                if (len < 1f)
                    continue;
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / StepM));
                for (int s = 0; s < steps; s++) // последний шаг добавит следующий сегмент
                {
                    Vector3 p = Vector3.Lerp(a, b, s / (float)steps);
                    center.Add(new Vector3(p.x, 0f, p.z));
                }
            }
            Vector3 last = points[points.Count - 1].ToLocalPosition();
            center.Add(new Vector3(last.x, 0f, last.z));
            if (center.Count < 2)
                return;

            // Осевая с высотами + перпендикуляры. Грейдинг сэмплирует ВСЁ СЕЧЕНИЕ
            // (ось + обе кромки) и берёт МАКСИМУМ: сечение плоское, террейн нельзя
            // деформировать — любой сэмпл НИЖЕ полотна прорежет его дюной.
            var dirs = new Vector2[center.Count];
            var terrain = new float[center.Count];
            for (int i = 0; i < center.Count; i++)
            {
                Vector3 prev = center[Mathf.Max(0, i - 1)];
                Vector3 next = center[Mathf.Min(center.Count - 1, i + 1)];
                Vector2 dir = new Vector2(next.x - prev.x, next.z - prev.z).normalized;
                dirs[i] = dir;
                Vector2 perp = new Vector2(-dir.y, dir.x) * (width * 0.5f);
                Vector3 c = center[i];
                float mid = GameApi.GroundYNoUnits(c.x, c.z);
                float l = GameApi.GroundYNoUnits(c.x + perp.x, c.z + perp.y);
                float r = GameApi.GroundYNoUnits(c.x - perp.x, c.z - perp.y);
                float t = Mathf.Max(mid, Mathf.Max(l, r));
                if (graded)
                {
                    // Прижатие к построенным лентам (ВПП, другие рулёжки) по ВСЕМУ
                    // сечению станции: новая лента не имеет права лечь ниже
                    // существующей в месте перекрытия — иначе ступенька на стыке
                    // и кромка под шасси (скошенный стык у торца — плейтест v0.96.1).
                    float pinM = TopAt(c);
                    if (!float.IsNaN(pinM))
                        t = Mathf.Max(t, pinM);
                    float pinL = TopAt(new Vector3(c.x + perp.x, 0f, c.z + perp.y));
                    if (!float.IsNaN(pinL))
                        t = Mathf.Max(t, pinL);
                    float pinR = TopAt(new Vector3(c.x - perp.x, 0f, c.z - perp.y));
                    if (!float.IsNaN(pinR))
                        t = Mathf.Max(t, pinR);
                }
                terrain[i] = t;
            }
            // Штифт старта: профиль выйдет НЕ НИЖЕ штифта (член j=0 конуса);
            // если рельеф рядом и так выше — штифт тихо игнорируется.
            if (!float.IsNaN(startY) && startY - LiftM > terrain[0])
                terrain[0] = startY - LiftM;
            float[] top = graded ? Grade(terrain) : null;

            var line = new Vector3[center.Count];
            var left = new Vector3[center.Count];
            var right = new Vector3[center.Count];
            var skirtL = new Vector3[center.Count];
            var skirtR = new Vector3[center.Count];
            for (int i = 0; i < center.Count; i++)
            {
                Vector2 perp = new Vector2(-dirs[i].y, dirs[i].x) * (width * 0.5f);
                Vector3 c = center[i];
                float yLine = top != null ? top[i] : SurfaceY(c.x, c.z);
                float yL = top != null ? top[i] : SurfaceY(c.x + perp.x, c.z + perp.y);
                float yR = top != null ? top[i] : SurfaceY(c.x - perp.x, c.z - perp.y);
                line[i] = new Vector3(c.x, yLine, c.z);
                left[i] = new Vector3(c.x + perp.x, yL, c.z + perp.y);
                right[i] = new Vector3(c.x - perp.x, yR, c.z - perp.y);
                // Юбка-насыпь: внешний край полотна опускается до рельефа —
                // приподнятое полотно читается как насыпь, а не парящая плита.
                skirtL[i] = new Vector3(c.x + perp.x,
                    GameApi.GroundYNoUnits(c.x + perp.x, c.z + perp.y) - 0.2f, c.z + perp.y);
                skirtR[i] = new Vector3(c.x - perp.x,
                    GameApi.GroundYNoUnits(c.x - perp.x, c.z - perp.y) - 0.2f, c.z - perp.y);
            }
            BuildPiece(left, right, line, skirtL, skirtR);
        }

        /// <summary>Терраса над рельефом: наименьший профиль с уклоном ≤ MaxGrade,
        /// ВСЮДУ не ниже рельефа — max конусов h[i] = max_j(terrain[j] + Lift − |i−j|·уклон).
        /// Член j=i гарантирует «не ниже» в каждой станции (террейн нельзя деформировать,
        /// резать его нельзя — через гребень дюны полотно переваливает насыпью).
        /// Прежние варианты проваливались именно здесь: скользящий минимум врезал
        /// полосу в гребень, а двухпроходная мин-клампировка сбривала подходы
        /// к высокой дюне ниже песка — дюны прорезали полотно рваными треугольниками
        /// (плейтесты v0.91.1–v0.91.2, скрин). n² при n = длина/15 м — копейки.</summary>
        private static float[] Grade(float[] terrain)
        {
            int n = terrain.Length;
            float delta = MaxGrade * StepM;
            var h = new float[n];
            for (int i = 0; i < n; i++)
            {
                float best = float.MinValue;
                for (int j = 0; j < n; j++)
                {
                    float v = terrain[j] + LiftM - Mathf.Abs(i - j) * delta;
                    if (v > best)
                        best = v;
                }
                h[i] = best;
            }
            return h;
        }

        /// <summary>Двухточечная лента (ВПП).</summary>
        public static void Register(GlobalPosition a, GlobalPosition b, float width,
            bool graded = false) =>
            Register(new[] { a, b }, width, graded);

        /// <summary>Высота полотна зарегистрированной ЛЕНТЫ в точке (NaN — ленты
        /// нет): прижатие новых грейдированных лент к построенным в местах
        /// перекрытия (стык с ВПП заподлицо по всей зоне, не только на концах).
        /// Апроны (ленты без осевой) не опрашиваются — они плоские, их высоту
        /// рулёжке передаёт явный startY-штифт.</summary>
        private static float TopAt(Vector3 p)
        {
            foreach (Piece piece in Pieces)
            {
                Vector3[] line = piece.Line;
                if (line == null || line.Length < 2 || piece.Width <= 0f)
                    continue;
                for (int i = 0; i < line.Length - 1; i++)
                {
                    Vector3 a = line[i], b = line[i + 1];
                    Vector2 ab = new Vector2(b.x - a.x, b.z - a.z);
                    float len2 = ab.sqrMagnitude;
                    if (len2 < 1e-6f)
                        continue;
                    Vector2 ap = new Vector2(p.x - a.x, p.z - a.z);
                    float t = Vector2.Dot(ap, ab) / len2;
                    if (t < 0f || t > 1f)
                        continue;
                    if ((ap - ab * t).magnitude > piece.Width * 0.5f + 2f)
                        continue;
                    return Mathf.Lerp(a.y, b.y, t);
                }
            }
            return float.NaN;
        }

        /// <summary>Апрон под ангаром: ПЛОСКАЯ плита на МАКСИМАЛЬНОЙ высоте площадки
        /// (+юбки по периметру до рельефа) — плита нигде не зарывается в рельеф,
        /// на косогоре читается насыпью. Возвращает высоту полотна — на неё
        /// ставится здание (ангар спавнится на выровненном апроне, не в бугре).</summary>
        public static float Pad(Vector3 center, float yawDeg, float sizeX, float sizeZ) =>
            BuildGridPad(center, Quaternion.Euler(0f, yawDeg, 0f), sizeX, sizeZ, null);

        /// <summary>Платформа верфи на воде (SupCom naval yard): плоская плита
        /// НА УРОВНЕ МОРЯ (+LiftM); юбки уходят до дна — читается опорами свай,
        /// а не парящей доской. Возвращает высоту верха — на неё ставится корпус
        /// верфи. Хост-only (физика — серверная).</summary>
        public static float WaterPad(Vector3 center, float yawDeg, float sizeX, float sizeZ) =>
            BuildGridPad(center, Quaternion.Euler(0f, yawDeg, 0f), sizeX, sizeZ,
                Datum.LocalSeaY + LiftM);

        /// <summary>Плоская плита-площадка: сетка sizeX×sizeZ (шаг StepM); верх —
        /// forcedTop (платформа на воде) либо МАКСИМУМ рельефа площадки + LiftM
        /// (грейдинг — плита не зарывается); юбки до рельефа/дна, периметр — разметка.</summary>
        private static float BuildGridPad(Vector3 center, Quaternion rot, float sizeX, float sizeZ,
            float? forcedTop)
        {
            EnsureRoot();
            if (_root == null)
                return forcedTop ?? GameApi.GroundYNoUnits(center.x, center.z) + LiftM;
            int nx = Mathf.Max(1, Mathf.CeilToInt(sizeX / StepM));
            int nz = Mathf.Max(1, Mathf.CeilToInt(sizeZ / StepM));
            var rows = new List<Vector3[]>();
            float max = float.MinValue;
            for (int j = 0; j <= nz; j++)
            {
                var row = new Vector3[nx + 1];
                for (int i = 0; i <= nx; i++)
                {
                    Vector3 local = new Vector3(
                        -sizeX * 0.5f + sizeX * i / nx, 0f,
                        -sizeZ * 0.5f + sizeZ * j / nz);
                    Vector3 w = rot * local + new Vector3(center.x, 0f, center.z);
                    float y = GameApi.GroundYNoUnits(w.x, w.z);
                    if (y > max)
                        max = y;
                    row[i] = new Vector3(w.x, y, w.z); // рельеф — ниже перезапишем
                }
                rows.Add(row);
            }
            float top = forcedTop ?? max + LiftM;
            foreach (Vector3[] row in rows)
                for (int i = 0; i < row.Length; i++)
                    row[i] = new Vector3(row[i].x, top, row[i].z);
            // Меш: склейка строк сетки в одну поверхность (полоса за полосой).
            var verts = new List<Vector3>();
            var tris = new List<int>();
            foreach (Vector3[] row in rows)
            {
                int baseIdx = verts.Count;
                verts.AddRange(row);
                if (baseIdx > 0)
                    for (int i = 0; i < nx; i++)
                    {
                        int a = baseIdx - row.Length + i, b = a + 1;
                        int c = baseIdx + i, d = c + 1;
                        tris.AddRange(new[] { a, c, b, b, c, d });
                    }
            }
            // Юбки по периметру: с края плиты вниз до рельефа.
            void Skirt(Vector3 from, Vector3 to)
            {
                int i0 = verts.Count;
                verts.Add(from);
                verts.Add(new Vector3(from.x,
                    GameApi.GroundYNoUnits(from.x, from.z) - 0.2f, from.z));
                verts.Add(to);
                verts.Add(new Vector3(to.x,
                    GameApi.GroundYNoUnits(to.x, to.z) - 0.2f, to.z));
                tris.AddRange(new[] { i0, i0 + 2, i0 + 1, i0 + 1, i0 + 2, i0 + 3 });
            }
            for (int i = 0; i < nx; i++) Skirt(rows[0][i], rows[0][i + 1]);
            for (int j = 0; j < nz; j++) Skirt(rows[j][nx], rows[j + 1][nx]);
            for (int i = 0; i < nx; i++) Skirt(rows[nz][i + 1], rows[nz][i]);
            for (int j = 0; j < nz; j++) Skirt(rows[j + 1][0], rows[j][0]);

            // Периметр — замкнутая ломаная по краю сетки (линия разметки апрона).
            var line = new List<Vector3>();
            for (int i = 0; i <= nx; i++) line.Add(rows[0][i]);          // передний край
            for (int j = 1; j <= nz; j++) line.Add(rows[j][nx]);         // правый
            for (int i = nx - 1; i >= 0; i--) line.Add(rows[nz][i]);     // задний
            for (int j = nz - 1; j >= 1; j--) line.Add(rows[j][0]);      // левый
            BuildPieceFromMesh(verts, tris, null, null, ToArrayClosed(line));
            return top;
        }

        private static Vector3[] ToArrayClosed(List<Vector3> loop)
        {
            var arr = new Vector3[loop.Count + 1];
            for (int i = 0; i < loop.Count; i++)
                arr[i] = loop[i];
            arr[loop.Count] = loop[0];
            return arr;
        }

        private static void BuildPiece(Vector3[] left, Vector3[] right, Vector3[] line,
            Vector3[] skirtL = null, Vector3[] skirtR = null)
        {
            var verts = new List<Vector3>(left.Length * 4);
            var tris = new List<int>();
            for (int i = 0; i < left.Length; i++)
            {
                int l = verts.Count;
                verts.Add(left[i]);
                verts.Add(right[i]);
                if (i > 0)
                {
                    int pl = l - 2;
                    tris.AddRange(new[] { pl, l, pl + 1, pl + 1, l, l + 1 });
                }
            }
            // Юбки: кромка полотна → рельеф (вертикальные ленты по обоим краям).
            if (skirtL != null && skirtL.Length == left.Length)
            {
                for (int i = 0; i < left.Length; i++)
                {
                    int l = verts.Count;
                    verts.Add(left[i]);
                    verts.Add(skirtL[i]);
                    if (i > 0)
                    {
                        int pl = l - 2;
                        tris.AddRange(new[] { pl, l, pl + 1, pl + 1, l, l + 1 });
                    }
                }
            }
            if (skirtR != null && skirtR.Length == right.Length)
            {
                for (int i = 0; i < right.Length; i++)
                {
                    int l = verts.Count;
                    verts.Add(right[i]);
                    verts.Add(skirtR[i]);
                    if (i > 0)
                    {
                        int pl = l - 2;
                        tris.AddRange(new[] { pl, l, pl + 1, pl + 1, l, l + 1 });
                    }
                }
            }
            BuildPieceFromMesh(verts, tris, left, right, line);
        }

        private static void BuildPieceFromMesh(List<Vector3> verts, List<int> tris,
            Vector3[] left, Vector3[] right, Vector3[] line)
        {
            EnsureRoot();
            if (_root == null || verts.Count < 3)
                return;
            var mesh = new Mesh { name = "taxi-surface" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            Meshes.Add(mesh);

            var go = new GameObject("taxi-surface"); // 3D-GO: физическая лента, не UI
            go.transform.SetParent(_root, false);
            go.layer = PhysicsLayers.Statics; // слой карты: GroundY и шасси видят
            var collider = go.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh; // материал нет: null ≠ terrainMaterial = тармак

            Pieces.Add(new Piece
            {
                Transform = go.transform,
                Mesh = mesh,
                Line = line,
                Left = left,
                Right = right,
                Width = left != null && right != null && left.Length > 0
                    ? Vector3.Distance(left[0], right[0])
                    : 0f,
            });
        }

        private static void EnsureRoot()
        {
            if (_root != null)
                return;
            var go = new GameObject("SupremeNuclear.TaxiSurface"); // 3D-GO: корень сцен
            _root = go.transform; // корень сцены: сдвиг FloatingOrigin несёт его с миром
        }

        /// <summary>Поверхности + разметка + превью — каждый кадр из Update ДО
        /// рендера (правило №16: DrawMesh из OnGUI мерцает).</summary>
        public static void Render()
        {
            if (!GameApi.InMission || (Pieces.Count == 0 && Preview.Count < 2))
                return;
            if (_mat == null && !EnsureMaterial())
                return;
            for (int i = Pieces.Count - 1; i >= 0; i--)
            {
                Transform t = Pieces[i].Transform;
                if (t == null)
                {
                    Pieces.RemoveAt(i); // сцена умерла — не копим мусор
                    continue;
                }
                Piece p = Pieces[i];
                QuadMatrix(p.Mesh, t.localToWorldMatrix, Asfalt);
                if (p.Left != null)
                {
                    Line(p.Left, 0.6f, Edge, false);
                    Line(p.Right, 0.6f, Edge, false);
                    Line(p.Line, 1.1f, Dash, true);
                }
                else
                    Line(p.Line, 0.6f, Edge, false); // периметр апрона
            }
            DrawPreview();
        }

        /// <summary>Линия/пунктир по ломаной: сегменты сэмплов, плоские квады.</summary>
        private static void Line(Vector3[] pts, float width, Color c, bool dashed)
        {
            if (pts == null || pts.Length < 2)
                return;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                if (dashed && (i & 1) == 1)
                    continue; // через сегмент — пунктир
                Vector3 delta = pts[i + 1] - pts[i];
                float len = delta.magnitude;
                if (len < 0.1f)
                    continue;
                Vector3 mid = (pts[i] + pts[i + 1]) * 0.5f + Vector3.up * 0.06f;
                Quad(mid, Quaternion.LookRotation(delta), width, len, c);
            }
        }

        private static void QuadMatrix(Mesh mesh, Matrix4x4 m, Color c)
        {
            _block.SetColor(ColorId, c);
            Graphics.DrawMesh(mesh, m, _mat, 0, null, 0, _block);
        }

        private static void Quad(Vector3 pos, Quaternion rot, float sx, float sz, Color c)
        {
            _block.SetColor(ColorId, c);
            Graphics.DrawMesh(_quad, Matrix4x4.TRS(pos, rot, new Vector3(sx, 1f, sz)),
                _decalMat, 0, null, 0, _block);
        }

        // Превью будущей рулёжки на призраке установки (без коллайдеров).
        private static readonly List<GlobalPosition> Preview = new List<GlobalPosition>();
        private static float _previewWidth = WidthM;
        private static bool _previewRunway; // рисовать пороговые планки на торцах

        public static void SetPreview(List<GlobalPosition> points)
        {
            Preview.Clear();
            _previewWidth = WidthM;
            _previewRunway = false;
            if (points != null)
                Preview.AddRange(points);
        }

        /// <summary>Превью ВПП: широкая полоса + белые пороги на торцах.</summary>
        public static void SetRunwayPreview(GlobalPosition a, GlobalPosition b)
        {
            Preview.Clear();
            Preview.Add(a);
            Preview.Add(b);
            _previewWidth = Settings.RunwayWidthM;
            _previewRunway = true;
        }

        public static void ClearPreview()
        {
            Preview.Clear();
            _previewRunway = false;
        }

        private static void DrawPreview()
        {
            if (Preview.Count < 2)
                return;
            for (int i = 0; i < Preview.Count - 1; i++)
            {
                Vector3 a = Preview[i].ToLocalPosition();
                Vector3 b = Preview[i + 1].ToLocalPosition();
                Vector2 d = new Vector2(b.x - a.x, b.z - a.z);
                float len = d.magnitude;
                if (len < 1f)
                    continue;
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / StepM));
                for (int s = 0; s < steps; s++)
                {
                    Vector3 p0 = Vector3.Lerp(a, b, s / (float)steps);
                    Vector3 p1 = Vector3.Lerp(a, b, (s + 1) / (float)steps);
                    Vector3 mid = new Vector3((p0.x + p1.x) * 0.5f, 0f, (p0.z + p1.z) * 0.5f);
                    Vector3 fwd = p1 - p0;
                    mid.y = SurfaceY(mid.x, mid.z) + 0.3f;
                    Quad(mid, Quaternion.LookRotation(fwd.normalized), _previewWidth,
                        fwd.magnitude + 2f, PreviewTint);
                }
            }
            if (_previewRunway)
            {
                // Пороги: белые планки поперёк обоих торцов
                Vector3 first = Preview[0].ToLocalPosition();
                Vector3 last = Preview[Preview.Count - 1].ToLocalPosition();
                Vector3 fwd = (last - first).normalized;
                Threshold(first, fwd);
                Threshold(last, fwd);
            }
        }

        private static void Threshold(Vector3 at, Vector3 fwd)
        {
            Quaternion rot = Quaternion.LookRotation(fwd);
            Vector3 pos = new Vector3(at.x, SurfaceY(at.x, at.z) + 0.35f, at.z);
            Quad(pos, rot, _previewWidth * 0.8f, 3f, Edge);
        }

        private static bool EnsureMaterial()
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null)
                return false;
            _block = new MaterialPropertyBlock();
            // Асфальт НЕПРОЗРАЧНЫЙ (blend One/Zero + запись глубины): полупрозрачная
            // плита просвечивала — грунт/подложка была видна сквозь «бетон».
            _mat = new Material(sh) { renderQueue = 2000 };
            _mat.SetFloat("_SrcBlend", 1f);
            _mat.SetFloat("_DstBlend", 0f);
            _mat.SetFloat("_ZWrite", 1f);
            _mat.SetFloat("_Cull", 0f);
            // Разметка/превью — прозрачный проход ПОСЛЕ плиты (глубина уже написана
            // асфальтом, квады проходят ZTest и лягут строго на свою поверхность).
            _decalMat = new Material(sh) { renderQueue = 3000 };
            _decalMat.SetFloat("_SrcBlend", 5f);
            _decalMat.SetFloat("_DstBlend", 10f);
            _decalMat.SetFloat("_ZWrite", 0f);
            _decalMat.SetFloat("_Cull", 0f);
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
            Pieces.Clear();
            Preview.Clear();
            foreach (Mesh mesh in Meshes)
                if (mesh != null)
                    Object.Destroy(mesh);
            Meshes.Clear();
            if (_root != null)
            {
                Object.Destroy(_root.gameObject);
                _root = null;
            }
        }
    }
}
