using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Кольца дальности выделенных юнитов (SupCom): атака-АС (красное —
    /// макс. дальность РАЗРЕШЁННЫХ гейтом станций по НАЗЕМНЫМ/надводным целям),
    /// атака-АА (синее — по ВОЗДУШНЫМ; домен цели — effectiveness.antiSurface/
    /// antiAir > 0, тот же порог, которым игра красит MapToolTip), разведка
    /// (белое — радар/визуал детекторов юнита, паттерн MapToolTip игры), стройка
    /// (охра — инженерный радиус площадки BuildSiteRadiusM). Торпедных (зелёных)
    /// в этой игре нет. Режим «Оружие» (RangeRings.Weapons): вместо доменных
    /// колец — кольца по КЛАССАМ вооружения, как кнопки гейтов (Пушки жёлтое /
    /// Рак.ВВ синее / Рак.АС красное / Бомбы+ЯД фиолетовое; тоже только
    /// разрешённые станции) — видно, чем осталось бить после отключения класса.
    /// РЕНДЕР — как в SupCom (v0.79): НЕ текстура, а ВЕКТОРНАЯ ПОЛИЛИНИЯ в мире —
    /// ломаная из отрезков (24..160 сегментов — по экранному размеру кольца и
    /// шагу рельефа,
    /// вблизи видна ломаная — честно, как в SupCom), вершины проектируются в
    /// экран КАЖДЫЙ Repaint из живых позиций юнитов (идеальная прибитость к
    /// миру, никакой растеризации и загрузки текстур — лагать нечему), рисуются
    /// одним батчем GL.LINES поверх сцены; окно миникарты вырезается усечением
    /// отрезков (ClipOutRect, чистая — тестируется). «Скольжение по
    /// поверхности» (v0.81): вершины ломаной лежат на высоте РЕЛЬЕФА — кэш
    /// ячеек 40 м с ленивым бюджетным снятием по периметрам колец, билинейная
    /// интерполяция; база — УРОВЕНЬ МОРЯ (v0.82: неснятый рельеф не подвешивает
    /// кольцо в воздухе), снизу кламп морем (кольцо не ныряет под воду).
    /// Гибрид v0.90–v0.91: профили высот вершин для ВСЕХ колец — вблизи камеры
    /// (RangeRings.ExactTerrainDist) ТОЧНЫЕ высоты прямыми рейкастами по мешу
    /// (как SupCom, сглаживания нет; съём только сдвинувшихся колец бюджетом
    /// на тик), на стратегическом зуме — дёшево из кэша на тике; отрисовка
    /// читает только массивы профилей и НИЧЕГО не рейкастит. Пересечения кругов
    /// ОДНОГО класса сливаются (v0.80): сегмент скрыт, если его точка на дуге
    /// лежит внутри чужого круга класса — виден только ВНЕШНИЙ контур
    /// объединения («пятно» SupCom). Сброс выделения / выключение режима
    /// стирает кольца. Заливки нет — в SupCom только линии (v0.72–0.78 рисовали
    /// CPU-маской: мягко и лагало на заливке).</summary>
    internal static class RangeRings
    {
        private struct Circle
        {
            internal Unit Unit;    // владелец: центр читается при отрисовке — кольцо прибито к юниту
            internal float PlaneY; // базовая плоскость кольца = уровень моря (рельеф поверх)
            internal float R;      // радиус, м
        }

        internal const int Kinds = 8;
        internal static readonly Color[] Colors =
        {
            new Color(1f, 0.30f, 0.22f),    // атака-АС — красный (наземные/надводные цели, SupCom)
            new Color(0.35f, 0.55f, 1f),    // атака-АА — синий (воздушные цели, SupCom)
            new Color(0.92f, 0.92f, 0.92f), // разведка — белый (радар/визуал)
            new Color(0.90f, 0.62f, 0.25f), // стройка — охристый (инженерный модуль, SupCom)
            new Color(1f, 0.80f, 0.15f),    // Оружие: пушки — жёлтый
            new Color(0.35f, 0.55f, 1f),    // Оружие: ракеты ВВ — синий (режим взаимно исключает АА)
            new Color(1f, 0.30f, 0.22f),    // Оружие: ракеты АС — красный (режим взаимно исключает АС)
            new Color(0.72f, 0.40f, 1f)     // Оружие: бомбы и ЯЗ — фиолетовый (радиус сброса)
        };
        private const float LineAlpha = 0.95f;

        private static readonly List<Circle>[] Circles =
        {
            new List<Circle>(), new List<Circle>(), new List<Circle>(), new List<Circle>(),
            new List<Circle>(), new List<Circle>(), new List<Circle>(), new List<Circle>()
        };
        private static readonly List<TargetDetector> DetectorBuf = new List<TargetDetector>();

        // Снимок кругов класса на кадр (центры из живых позиций юнитов) — для
        // теста «сегмент внутри чужого круга того же класса» без чтения
        // трансформов во внутреннем цикле. Размер = потолок RangeRingMaxUnits.
        private const int MaxCircles = 200;
        private static readonly float[] SnapX = new float[MaxCircles];
        private static readonly float[] SnapZ = new float[MaxCircles];
        private static readonly float[] SnapY = new float[MaxCircles];
        private static readonly float[] SnapR = new float[MaxCircles];
        private static readonly Unit[] SnapU = new Unit[MaxCircles]; // для профиля точных высот

        private static Material _lineMat; // Internal-Colored (как силуэты BuildGhosts), альфа-блендинг
        private static float _nextRefresh;

        // «Скольжение по поверхности» (SupCom): база кольца — УРОВЕНЬ МОРЯ
        // (неснятый рельеф и вода не подвешивают кольцо в воздухе), где рельеф
        // снят — вершины ложатся на местность, снизу кламп морем (физлуч над
        // водой бьёт в дно). Кэш высот сеткой ячеек CellM, высоты снимаются
        // ЛЕНИВО бюджетом на тик только по ПЕРИМЕТРАМ
        // колец (полоса, а не весь диск — двигается колонна, новая полоса
        // набегает сама), вершины читают билинейно по центрам ячеек. Кольцо
        // «оседает» на рельеф за секунды, как рельеф миникарты. Рельеф
        // статичен — чистим по sceneLoaded.
        internal const float CellM = 40f;
        private const int SamplesPerTick = 400;
        private const int MaxCells = 200000;
        private static readonly Dictionary<long, float> Heights = new Dictionary<long, float>();

        // Профили высот вершин для ВСЕХ колец (v0.91): кадр читает только массив
        // профиля — словарь высот в отрисовке не трогается (на большой пачке это
        // были десятки тысяч lookup'ов в кадр). ВБЛИЗИ камеры
        // (RangeRingExactDist) профиль — ТОЧНЫЕ высоты прямыми рейкастами по
        // мешу (честный рельеф без сглаживания, как SupCom), переснимается
        // ТОЛЬКО когда кольцо сдвинулось/изменилось (стоящая техника не жжёт
        // лучи), бюджетом на тик, атомарно; ДАЛЬШЕ — билинейно из ячеистого кэша
        // прямо на тике (без физики), обновляясь по версии кэша. Профиль живёт
        // ПО ЮНИТУ (переживает пересборки списков), массивы из пула; высоты —
        // на момент тика (кольцо следует за юнитом живьём, данные ≤100 мс).
        private const int FineSegsMax = 160;
        private const int FineRaysPerTick = 600;
        private sealed class FineProfile
        {
            internal readonly float[] H = new float[FineSegsMax];
            internal float Cx, Cz, R;
            internal int Segs;
            internal int Ver; // версия кэша высот, из которой профиль снят (дальний режим)
        }
        private static readonly Dictionary<Unit, FineProfile> Fine = new Dictionary<Unit, FineProfile>();
        private static readonly Stack<FineProfile> FinePool = new Stack<FineProfile>();
        private static int _heightsVersion; // растёт с каждой НОВОЙ ячейкой кэша — дальние профили обновляются

        // ВРЕМЕННАЯ диагностика (убрать после разбора «нет голубого кольца ВВ на ЛА»):
        // раз в 5 с дампим станции первого выделенного самолёта.
        private static float _nextDiag;

        // Кэш сенсорных дальностей (разведка): не ходить по иерархии детекторов 10 раз/с;
        // развёрнутый радар появится с задержкой кэша (2 с).
        private static readonly Dictionary<Unit, Vector2> VisionCache = new Dictionary<Unit, Vector2>();
        private const float VisionCacheSec = 2f;

        /// <summary>Сбор кругов 10 Гц (радиусы/классы меняются редко) — из
        /// CommanderHud.Update; позиции юнитов читаются при отрисовке, поэтому
        /// между сборами кольца всё равно идеально следуют за юнитами.
        /// Выделение пустое / режим выключен — круги стираем (иначе линии
        /// оставались на экране после сброса выделения).</summary>
        internal static void Tick(CommanderModeController controller)
        {
            if (!(Settings.RangeRingAttack || Settings.RangeRingAir || Settings.RangeRingWeapons ||
                  Settings.RangeRingVision || Settings.RangeRingBuild) ||
                controller == null || !controller.IsActive)
            {
                ClearAll();
                return;
            }
            List<Unit> selected = controller.Selection.Selected;
            if (selected == null || selected.Count == 0)
            {
                ClearAll();
                return;
            }
            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + 0.1f;
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            CollectCircles(selected);
            SampleTerrain();
            SampleFine(cam);
        }

        /// <summary>Ключ ячейки сетки высот (чистая — тестируется).</summary>
        internal static long CellKey(float x, float z)
        {
            return ((long)Mathf.FloorToInt(x / CellM) << 32) | (uint)Mathf.FloorToInt(z / CellM);
        }

        /// <summary>Билинейная высота по 4 центрам ячеек: h00=(ix,iz),
        /// h10/h01 — соседи по x/z, h11 — диагональ; fx/fz — доли (чистая —
        /// тестируется).</summary>
        internal static float Bilinear(float h00, float h10, float h01, float h11, float fx, float fz)
        {
            float a = h00 + (h10 - h00) * fx;
            float b = h01 + (h11 - h01) * fx;
            return a + (b - a) * fz;
        }

        /// <summary>Высота поверхности в точке (билинейно по центрам ячеек кэша;
        /// в кэше уже кламп по морю — поверхность любого типа: суша/вода).
        /// Хоть одна из четырёх ячеек ещё не снята — базовая плоскость (море).</summary>
        private static float TerrainAt(float x, float z, float fallback)
        {
            if (Heights.Count == 0)
                return fallback;
            float gx = x / CellM - 0.5f, gz = z / CellM - 0.5f;
            int ix = Mathf.FloorToInt(gx), iz = Mathf.FloorToInt(gz);
            if (!TryHeight(ix, iz, out float h00) || !TryHeight(ix + 1, iz, out float h10) ||
                !TryHeight(ix, iz + 1, out float h01) || !TryHeight(ix + 1, iz + 1, out float h11))
                return fallback;
            return Bilinear(h00, h10, h01, h11, gx - ix, gz - iz);
        }

        private static bool TryHeight(int cx, int cz, out float h) =>
            Heights.TryGetValue(((long)cx << 32) | (uint)cz, out h);

        /// <summary>Снятие высот бюджетом на тик по ПЕРИМЕТРАМ колец (шаг — как
        /// рельефная часть тесселяции отрисовки, кап 160). Вокруг каждой точки —
        /// БЛОК 3×3 ячейки: билинейной интерполяции вершины нужен квадрат 2×2
        /// ПО ОБЕ СТОРОНЫ линии кольца, одной полосы ячеек мало — недостающие
        /// ячейки проваливали кольцо под землю. Ячейка снимается физлучом один
        /// раз, дальше из кэша.</summary>
        private static void SampleTerrain()
        {
            if (Heights.Count >= MaxCells)
                return;
            int budget = SamplesPerTick;
            for (int k = 0; k < Kinds && budget > 0; k++)
            {
                if (!KindOn(k))
                    continue;
                List<Circle> list = Circles[k];
                for (int ci = 0; ci < list.Count && budget > 0; ci++)
                {
                    Circle c = list[ci];
                    if (c.Unit == null || c.Unit.disabled) // Unity fake-null
                        continue;
                    Vector3 p = c.Unit.transform.position;
                    int steps = Mathf.Clamp(Mathf.CeilToInt(6.2832f * c.R / CellM), 8, 160);
                    for (int i = 0; i < steps && budget > 0; i++)
                    {
                        float a = (float)i / steps * 6.2832f;
                        SampleBlock(p.x + Mathf.Cos(a) * c.R, p.z + Mathf.Sin(a) * c.R, ref budget);
                    }
                }
            }
        }

        private static void SampleBlock(float x, float z, ref int budget)
        {
            int cx = Mathf.FloorToInt(x / CellM), cz = Mathf.FloorToInt(z / CellM);
            for (int dz = -1; dz <= 1 && budget > 0; dz++)
                for (int dx = -1; dx <= 1 && budget > 0; dx++)
                    SampleCell(cx + dx, cz + dz, ref budget);
        }

        private static void SampleCell(int cx, int cz, ref int budget)
        {
            long key = ((long)cx << 32) | (uint)cz;
            if (Heights.ContainsKey(key))
                return;
            // снимаем в ЦЕНТРЕ ячейки — по центрам же читает билинейная интерполяция;
            // снизу кламп морем: физлуч над водой бьёт в ДНО, поверхность = море
            float h = GameApi.GroundY((cx + 0.5f) * CellM, (cz + 0.5f) * CellM);
            float sea = Datum.LocalSeaY;
            Heights[key] = h < sea ? sea : h;
            _heightsVersion++;
            budget--;
        }

        /// <summary>Профили высот вершин для ВСЕХ колец (v0.91: кадр читает только
        /// массивы профиля, словарь высот в отрисовке не трогается). ВБЛИЗИ камеры
        /// (RangeRingExactDist) профиль — ТОЧНЫЕ высоты прямыми рейкастами по мешу
        /// (как SupCom; только сдвинувшиеся кольца, атомарно, бюджетом на тик);
        /// ДАЛЬШЕ — билинейно из ячеистого кэша (без физики), обновляясь по
        /// версии кэша (ячейки под кольцом доснялись — профиль пересобран).
        /// Профили живут ПО ЮНИТУ (переживают пересборки списков), массивы из
        /// пула; мёртвые юниты — профили в пул.</summary>
        private static void SampleFine(Camera cam)
        {
            if (!AnyCircle() && Fine.Count == 0)
                return;
            // мёртвые юниты — профили в пул (Unity fake-null)
            if (Fine.Count > 0)
            {
                List<Unit> dead = null;
                foreach (KeyValuePair<Unit, FineProfile> kv in Fine)
                    if (kv.Key == null || kv.Key.disabled)
                        (dead ?? (dead = new List<Unit>())).Add(kv.Key);
                if (dead != null)
                    foreach (Unit u in dead)
                    {
                        FinePool.Push(Fine[u]);
                        Fine.Remove(u);
                    }
            }
            Vector3 camPos = cam.transform.position;
            float tanH = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float h = Screen.height;
            int limit = Settings.RangeRingExactDist;
            float limitSq = (float)limit * limit;
            float sea = Datum.LocalSeaY;
            int budget = FineRaysPerTick;
            for (int k = 0; k < Kinds; k++)
            {
                if (!KindOn(k))
                    continue;
                List<Circle> list = Circles[k];
                for (int ci = 0; ci < list.Count; ci++)
                {
                    Circle c = list[ci];
                    if (c.Unit == null || c.Unit.disabled) // Unity fake-null
                        continue;
                    Vector3 p = c.Unit.transform.position;
                    float dx = p.x - camPos.x, dy = c.PlaneY - camPos.y, dz = p.z - camPos.z;
                    float distSq = dx * dx + dy * dy + dz * dz;
                    bool near = limit > 0 && distSq <= limitSq;
                    float dist = Mathf.Sqrt(distSq);
                    float worldPerPx = Mathf.Max(1e-3f, 2f * dist * tanH / h);
                    int segs = Mathf.Clamp(Mathf.CeilToInt(6.2832f * c.R / worldPerPx / 12f), 24, FineSegsMax);
                    if (!Fine.TryGetValue(c.Unit, out FineProfile prof))
                    {
                        prof = FinePool.Count > 0 ? FinePool.Pop() : new FineProfile();
                        prof.Segs = 0; // форсируем первый съём
                        Fine[c.Unit] = prof;
                    }
                    float mdx = p.x - prof.Cx, mdz = p.z - prof.Cz;
                    bool moved = mdx * mdx + mdz * mdz > 4f; // 2 м
                    bool stale = prof.Segs != segs || prof.R != c.R || moved;
                    if (!near)
                        stale = stale || prof.Ver != _heightsVersion; // кэш под кольцом доснялся
                    if (!stale)
                        continue;
                    if (near && budget < segs)
                        continue; // атомарно: полупустой профиль не оставляем
                    float ang = 2f * Mathf.PI / segs;
                    float cA = Mathf.Cos(ang), sA = Mathf.Sin(ang);
                    float vx = c.R, vz = 0f;
                    for (int i = 0; i < segs; i++)
                    {
                        float wx = p.x + vx, wz = p.z + vz;
                        if (near)
                        {
                            float gy = GameApi.GroundY(wx, wz);
                            prof.H[i] = gy < sea ? sea : gy;
                        }
                        else
                            prof.H[i] = TerrainAt(wx, wz, sea); // кэш без физики
                        float nvx = vx * cA - vz * sA;
                        vz = vx * sA + vz * cA;
                        vx = nvx;
                    }
                    prof.Cx = p.x;
                    prof.Cz = p.z;
                    prof.R = c.R;
                    prof.Segs = segs;
                    prof.Ver = _heightsVersion;
                    if (near)
                        budget -= segs;
                }
            }
        }

        private static void ClearAll()
        {
            for (int k = 0; k < Kinds; k++)
                Circles[k].Clear();
        }

        /// <summary>Отрисовка каждый OnGUI (CommanderHud): полилинии GL одним
        /// батчем. Только Repaint (Layout-проход ничего не рисует).</summary>
        internal static void Draw()
        {
            if (Event.current.type != EventType.Repaint || !AnyCircle())
                return;
            Camera cam = GameApi.MainCam;
            if (cam == null)
                return;
            if (_lineMat == null && !CreateLineMaterial())
                return;

            Transform t = cam.transform;
            Vector3 camPos = t.position;
            Vector3 fwd = t.forward, rightT = t.right, upT = t.up;
            float tanH = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float aspect = Mathf.Max(cam.aspect, 0.01f);
            float w = Screen.width, h = Screen.height;
            Rect m = MinimapPanel.HudMaskRect;
            bool masked = m.width > 1f && m.height > 1f;

            _lineMat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, w, h, 0f); // пиксельная СИСТЕМА КООРДИНАТ GUI (y вниз)
            GL.Begin(GL.LINES);
            for (int k = 0; k < Kinds; k++)
            {
                if (!KindOn(k))
                    continue;
                List<Circle> list = Circles[k];
                if (list.Count == 0)
                    continue;
                // Снимок живых центров класса на кадр — тест объединения идёт по
                // массивам, без чтения трансформов во внутреннем цикле.
                int n = 0;
                for (int ci = 0; ci < list.Count && n < MaxCircles; ci++)
                {
                    Circle c = list[ci];
                    if (c.Unit == null || c.Unit.disabled) // Unity fake-null
                        continue;
                    Vector3 p = c.Unit.transform.position;
                    SnapX[n] = p.x;
                    SnapZ[n] = p.z;
                    SnapY[n] = c.PlaneY;
                    SnapR[n] = c.R;
                    SnapU[n] = c.Unit;
                    n++;
                }
                Color col = Colors[k];
                col.a = LineAlpha;
                GL.Color(col);
                for (int i = 0; i < n; i++)
                {
                    // Тесселяция: хорда ~12 px (та же формула в SampleProfiles).
                    // Отдельный «шаг рельефа» не нужен: когда 12 px грубее ячейки
                    // кэша (общий план), детали рельефа между вершинами
                    // субпиксельны (v0.91: прежний max(.., 2πR/CellM) держал 160
                    // сегментов у больших колец даже на стратегическом зуме — ×3
                    // нагрузки на большую выделенную пачку).
                    float ddx = SnapX[i] - camPos.x, ddy = SnapY[i] - camPos.y, ddz = SnapZ[i] - camPos.z;
                    float dist = Mathf.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                    float worldPerPx = Mathf.Max(1e-3f, 2f * dist * tanH / h);
                    int segs = Mathf.Clamp(Mathf.CeilToInt(6.2832f * SnapR[i] / worldPerPx / 12f), 24, FineSegsMax);
                    // высоты вершин — из профиля юнита (вблизи камеры он снят
                    // рейкастами по мешу, дальше — билинейно из кэша на тике);
                    // профиль валиден при совпадении тесселяции, иначе разовый
                    // fallback на кэш прямо в кадре (переходный момент)
                    float[] fineH = null;
                    if (Fine.TryGetValue(SnapU[i], out FineProfile prof) && prof.Segs == segs)
                        fineH = prof.H;
                    DrawPolygon(i, n, segs, fineH, camPos, fwd, rightT, upT, tanH, aspect, w, h, masked, m);
                }
            }
            GL.End();
            GL.PopMatrix();
        }

        private static bool AnyCircle()
        {
            for (int k = 0; k < Kinds; k++)
                if (KindOn(k) && Circles[k].Count > 0)
                    return true;
            return false;
        }

        /// <summary>Класс рисуется сейчас (зеркало условий сбора — на 100 мс
        /// тогглы могут опережать сбор, отрисовка честно гейтится).</summary>
        private static bool KindOn(int k)
        {
            switch (k)
            {
                case 0: return Settings.RangeRingAttack && !Settings.RangeRingWeapons;
                case 1: return Settings.RangeRingAir && !Settings.RangeRingWeapons;
                case 2: return Settings.RangeRingVision;
                case 3: return Settings.RangeRingBuild;
                default: return Settings.RangeRingWeapons;
            }
        }

        /// <summary>Один круг (из снимка Snap*): ломаная на segs сегментов
        /// (считает вызывающий — та же формула в SampleFine), вершины
        /// поворачиваются инкрементально (без тригонометрии на вершину),
        /// проекция лучом камеры. Высота вершины: точный профиль по мешу
        /// (fineH, вблизи камеры) либо билинейный кэш рельефа. Сегменты за
        /// камерой пропускаются, ВНУТРЕННИЕ дуги пересечений с кругами того же
        /// класса скрываются (ArcInsideOther — SupCom-объединение), куски под
        /// окном миникарты вырезаются (ClipOutRect). Двойной ряд вершин =
        /// ~2 px линия. Без аллокаций.</summary>
        private static void DrawPolygon(int self, int count, int segs, float[] fineH,
            Vector3 camPos, Vector3 fwd, Vector3 rightT, Vector3 upT, float tanH, float aspect,
            float w, float h, bool masked, Rect m)
        {
            float cx = SnapX[self], cz = SnapZ[self], planeY = SnapY[self], r = SnapR[self];
            float ang = 2f * Mathf.PI / segs;
            float cA = Mathf.Cos(ang), sA = Mathf.Sin(ang);
            float dx = r, dz = 0f; // вершина на угле 0, далее поворот на ang за шаг
            float prevWx = 0f, prevWz = 0f, prevX = 0f, prevY = 0f;
            bool prevValid = false;
            for (int i = 0; i <= segs; i++)
            {
                float wx = cx + dx, wz = cz + dz;
                // «скольжение по поверхности»: вблизи — ТОЧНАЯ высота профиля
                // (рейкасты по мешу, замыкающая вершина = вершина 0), дальше —
                // билинейный кэш рельефа
                float ey = (fineH != null ? fineH[i >= segs ? 0 : i] : TerrainAt(wx, wz, planeY)) - camPos.y;
                float ex = wx - camPos.x, ez = wz - camPos.z;
                float z = ex * fwd.x + ey * fwd.y + ez * fwd.z;
                bool valid = z > 0.05f;
                float sx = 0f, sy = 0f;
                if (valid)
                {
                    float invZ = 1f / z;
                    sx = ((ex * rightT.x + ey * rightT.y + ez * rightT.z) * invZ / (tanH * aspect) * 0.5f + 0.5f) * w;
                    float syB = ((ex * upT.x + ey * upT.y + ez * upT.z) * invZ / tanH * 0.5f + 0.5f) * h;
                    sy = h - syB; // экранные y снизу → GUI y сверху
                }
                if (prevValid && valid &&
                    !ArcInsideOther(self, count, cx, cz, r, (prevWx + wx) * 0.5f, (prevWz + wz) * 0.5f))
                    EmitSegment(prevX, prevY, sx, sy, masked, m);
                prevWx = wx;
                prevWz = wz;
                prevX = sx;
                prevY = sy;
                prevValid = valid;
                float ndx = dx * cA - dz * sA;
                dz = dx * sA + dz * cA;
                dx = ndx;
            }
        }

        /// <summary>SupCom-объединение: точка ДУГИ (середина хорды, вытянутая от
        /// центра до радиуса) лежит строго ВНУТРИ чужого круга того же класса?
        /// Такие сегменты — внутренние дуги пересечения — не рисуются: остаётся
        /// только ВНЕШНИЙ контур «слившегося пятна». Строгое «&lt;»: точка НА
        /// границе чужого круга видима — полностью совпадающие кольца рисуют
        /// общий контур, а не гасят друг друга; круг целиком внутри другого
        /// гаснет целиком.</summary>
        private static bool ArcInsideOther(int self, int count, float cx, float cz, float r, float mx, float mz)
        {
            if (count <= 1)
                return false;
            float ex = mx - cx, ez = mz - cz;
            float el = Mathf.Sqrt(ex * ex + ez * ez);
            if (el > 1e-6f)
            {
                ex = cx + ex / el * r; // точка НА дуге, а не середина хорды внутри круга
                ez = cz + ez / el * r;
            }
            for (int j = 0; j < count; j++)
            {
                if (j == self)
                    continue;
                float ddx = ex - SnapX[j], ddz = ez - SnapZ[j];
                float rj = SnapR[j];
                if (ddx * ddx + ddz * ddz < rj * rj)
                    return true;
            }
            return false;
        }

        /// <summary>Отрезок в батч (два ряда пикселей — линия ~2 px); из-под окна
        /// миникарты вырезается пересечение (ClipOutRect: 0 — рисовать нечего,
        /// 1 — кусок A, 2 — куски A и B).</summary>
        private static void EmitSegment(float x0, float y0, float x1, float y1, bool masked, Rect m)
        {
            if (!masked)
            {
                Verts(x0, y0, x1, y1);
                return;
            }
            int n = ClipOutRect(x0, y0, x1, y1, m,
                out float ax0, out float ay0, out float ax1, out float ay1,
                out float bx0, out float by0, out float bx1, out float by1);
            if (n == 0)
                return;
            Verts(ax0, ay0, ax1, ay1);
            if (n == 2)
                Verts(bx0, by0, bx1, by1);
        }

        private static void Verts(float x0, float y0, float x1, float y1)
        {
            GL.Vertex3(x0, y0, 0f);
            GL.Vertex3(x1, y1, 0f);
            GL.Vertex3(x0, y0 - 1f, 0f); // второй ряд — линия ~2 px
            GL.Vertex3(x1, y1 - 1f, 0f);
        }

        /// <summary>Вырезать из отрезка (x0,y0)-(x1,y1) его часть ВНУТРИ прямоугольника
        /// m (окно миникарты): вернуть до двух наружных кусков (Liang-Barsky —
        /// t-интервал пересечения, наружное = дополнение). Возвращает 0 (всё внутри),
        /// 1 (кусок A, B нулевой) или 2 (оба). Чистая — тестируется.</summary>
        internal static int ClipOutRect(float x0, float y0, float x1, float y1, Rect m,
            out float ax0, out float ay0, out float ax1, out float ay1,
            out float bx0, out float by0, out float bx1, out float by1)
        {
            ax0 = x0; ay0 = y0; ax1 = x1; ay1 = y1;
            bx0 = by0 = bx1 = by1 = 0f;
            float dx = x1 - x0, dy = y1 - y0;
            float ta = 0f, tb = 1f;
            // t-интервал ВНУТРИ rect (Liang-Barsky, p·t <= q):
            // x >= xMin → -dx·t <= x0-xMin; x <= xMax → dx·t <= xMax-x0; аналогично y
            if (!ClipEdge(-dx, x0 - m.xMin, ref ta, ref tb)) return 1;
            if (!ClipEdge(dx, m.xMax - x0, ref ta, ref tb)) return 1;
            if (!ClipEdge(-dy, y0 - m.yMin, ref ta, ref tb)) return 1;
            if (!ClipEdge(dy, m.yMax - y0, ref ta, ref tb)) return 1;
            if (ta <= 0f && tb >= 1f)
                return 0; // целиком внутри — рисовать нечего
            if (ta > 0f)
            {
                ax0 = x0; ay0 = y0;
                ax1 = x0 + dx * ta; ay1 = y0 + dy * ta;
            }
            if (tb < 1f)
            {
                if (ta > 0f)
                {
                    bx0 = x0 + dx * tb; by0 = y0 + dy * tb;
                    bx1 = x1; by1 = y1;
                    return 2;
                }
                ax0 = x0 + dx * tb; ay0 = y0 + dy * tb; // один кусок — хвост
                ax1 = x1; ay1 = y1;
            }
            return 1;
        }

        /// <summary>Один край Liang-Barsky: p·t <= q на [0,1]; false — пересечения нет.</summary>
        private static bool ClipEdge(float p, float q, ref float ta, ref float tb)
        {
            if (Mathf.Abs(p) < 1e-9f)
                return q >= 0f;
            float r = q / p;
            if (p < 0f)
            {
                if (r > tb) return false;
                if (r > ta) ta = r;
            }
            else
            {
                if (r < ta) return false;
                if (r < tb) tb = r;
            }
            return true;
        }

        private static bool CreateLineMaterial()
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null)
            {
                Log.Error("RangeRings: Hidden/Internal-Colored не найден — кольца выключены");
                return false;
            }
            _lineMat = new Material(sh)
            {
                renderQueue = 4000 // поверх всего GUI-контента мода
            };
            _lineMat.SetFloat("_SrcBlend", 5f);  // SrcAlpha
            _lineMat.SetFloat("_DstBlend", 10f); // OneMinusSrcAlpha
            _lineMat.SetFloat("_ZWrite", 0f);
            return true;
        }

        /// <summary>Круги классов от выделения (потолок юнитов на класс — перф).</summary>
        private static void CollectCircles(List<Unit> selected)
        {
            for (int k = 0; k < Kinds; k++)
                Circles[k].Clear();
            int budget = Settings.RangeRingMaxUnits;
            foreach (Unit unit in selected)
            {
                if (unit == null || unit.disabled) // Unity fake-null
                    continue;
                Vector3 p = unit.transform.position;
                // Базовая плоскость кольца — УРОВЕНЬ МОРЯ: неснятый рельеф и вода не
                // подвешивают кольцо в воздухе (высота под юнитом на холме это
                // делала); где рельеф снят — вершины ложатся на местность.
                float planeY = Datum.LocalSeaY;

                // ВРЕМЕННАЯ диагностика: станции первого выделенного ЛА/корабля (см. поле _nextDiag)
                if (Time.unscaledTime >= _nextDiag && (unit is Aircraft || unit is Ship) && unit.weaponStations != null)
                {
                    _nextDiag = Time.unscaledTime + 5f;
                    var sb = new System.Text.StringBuilder("RangeRings-диагностика (").Append(unit.name).Append("):");
                    foreach (WeaponStation st in unit.weaponStations)
                    {
                        if (st == null || st.WeaponInfo == null)
                        {
                            sb.Append(" [станция/WeaponInfo null]");
                            continue;
                        }
                        var e = st.WeaponInfo.effectiveness;
                        sb.Append(" [").Append(st.WeaponInfo.shortName ?? st.WeaponInfo.weaponName ?? "?")
                            .Append(" аВВ=").Append(e.antiAir).Append(" аЗ=").Append(e.antiSurface)
                            .Append(" дальность=").Append(st.WeaponInfo.targetRequirements.maxRange)
                            .Append(WeaponGateService.IsAllowed(unit, st) ? "" : " ГЕЙТ-ВЫКЛ")
                            .Append(']');
                    }
                    sb.Append(" || настройки: Атака=").Append(Settings.RangeRingAttack)
                        .Append(" Воздух=").Append(Settings.RangeRingAir)
                        .Append(" Оружие=").Append(Settings.RangeRingWeapons);
                    Log.Info(sb.ToString());
                }

                if (Settings.RangeRingWeapons)
                {
                    // Режим «Оружие»: кольца по КЛАССАМ (как кнопки гейтов «Разрешить
                    // огонь»), только РАЗРЕШЁННЫЕ станции — выключил класс, его кольцо
                    // исчезло/съёжилось до следующего. Бомбы и ЯЗ — один радиус сброса.
                    float rGun = 0f, rMaa = 0f, rMas = 0f, rBmb = 0f;
                    if (unit.weaponStations != null)
                        foreach (WeaponStation st in unit.weaponStations)
                        {
                            if (st == null || st.WeaponInfo == null || !WeaponGateService.IsAllowed(unit, st))
                                continue;
                            float mr = st.WeaponInfo.targetRequirements.maxRange;
                            if (mr < 1f)
                                continue;
                            switch (Catalog.ClassOf(st.WeaponInfo))
                            {
                                case WeaponClass.Gun: if (mr > rGun) rGun = mr; break;
                                case WeaponClass.MissileAA: if (mr > rMaa) rMaa = mr; break;
                                case WeaponClass.MissileAS: if (mr > rMas) rMas = mr; break;
                                case WeaponClass.Bomb:
                                case WeaponClass.Nuclear: if (mr > rBmb) rBmb = mr; break;
                            }
                        }
                    if (rGun >= 1f && Circles[4].Count < budget)
                        Circles[4].Add(new Circle { Unit = unit, PlaneY = planeY, R = rGun });
                    if (rMaa >= 1f && Circles[5].Count < budget)
                        Circles[5].Add(new Circle { Unit = unit, PlaneY = planeY, R = rMaa });
                    if (rMas >= 1f && Circles[6].Count < budget)
                        Circles[6].Add(new Circle { Unit = unit, PlaneY = planeY, R = rMas });
                    if (rBmb >= 1f && Circles[7].Count < budget)
                        Circles[7].Add(new Circle { Unit = unit, PlaneY = planeY, R = rBmb });
                }
                else if (Settings.RangeRingAttack || Settings.RangeRingAir)
                {
                    // Атака: максимум по станциям, РАЗРЕШЁННЫМ гейтами вооружения —
                    // выключенный класс не применяется никогда, кольца честные.
                    // Домен цели — по ДОМИНИРУЮЩЕМУ весу effectiveness (веса НЕ
                    // бинарные: у AGM-68 antiAir=0.5 при antiSurface=0.814; порог
                    // «>0» зачислял ракету в ОБА кольца — красное и синее совпадали
                    // радиусом и рисовались друг на друге, сверху синее). Правило
                    // зеркально Catalog.ClassOf: ВВ — только при строгом перевесе.
                    float rAs = 0f, rAa = 0f;
                    if (unit.weaponStations != null)
                        foreach (WeaponStation st in unit.weaponStations)
                        {
                            if (st == null || st.WeaponInfo == null || !WeaponGateService.IsAllowed(unit, st))
                                continue;
                            float mr = st.WeaponInfo.targetRequirements.maxRange;
                            if (mr < 1f)
                                continue;
                            var eff = st.WeaponInfo.effectiveness; // RoleIdentity-структура, не бывает null
                            if (Settings.RangeRingAttack && eff.antiSurface >= eff.antiAir && mr > rAs)
                                rAs = mr;
                            if (Settings.RangeRingAir && eff.antiAir > eff.antiSurface && mr > rAa)
                                rAa = mr;
                        }
                    if (rAs >= 1f && Circles[0].Count < budget)
                        Circles[0].Add(new Circle { Unit = unit, PlaneY = planeY, R = rAs });
                    if (rAa >= 1f && Circles[1].Count < budget)
                        Circles[1].Add(new Circle { Unit = unit, PlaneY = planeY, R = rAa });
                }

                if (Settings.RangeRingVision && Circles[2].Count < budget)
                {
                    // Разведка: пер-юнитный сенсор — максимум радара/визуала детекторов
                    // (паттерн MapToolTip игры); неактивные (сложенные) радары
                    // не считаются — развёрнутый появляется с развертыванием.
                    float r = VisionRangeOf(unit);
                    if (r >= 1f)
                        Circles[2].Add(new Circle { Unit = unit, PlaneY = planeY, R = r });
                }

                if (Settings.RangeRingBuild && Circles[3].Count < budget && GameApi.IsEngineer(unit))
                    Circles[3].Add(new Circle { Unit = unit, PlaneY = planeY, R = Settings.BuildSiteRadiusM });
            }
        }

        /// <summary>Дальность разведки юнита с кэшем (максимум радара/визуала
        /// АКТИВНЫХ детекторов — паттерн MapToolTip игры).</summary>
        private static float VisionRangeOf(Unit unit)
        {
            float now = Time.unscaledTime;
            if (VisionCache.TryGetValue(unit, out Vector2 hit) && hit.x > now)
                return hit.y;
            float r = 0f;
            DetectorBuf.Clear();
            unit.GetComponentsInChildren(false, DetectorBuf);
            foreach (TargetDetector d in DetectorBuf)
            {
                if (d == null)
                    continue;
                if (d is Radar radar)
                {
                    float rr = radar.GetRadarRange();
                    if (rr > r)
                        r = rr;
                }
                float vis = d.GetVisualRange();
                if (vis > r)
                    r = vis;
            }
            VisionCache[unit] = new Vector2(now + VisionCacheSec, r);
            return r;
        }

        /// <summary>Сброс по sceneLoaded (круги держат юнитов — чистим; кэш
        /// сенсоров держит юнитов — чистим; кэш высот и профили точных высот —
        /// новая карта, профили в пул; материал без юнитов — живёт).</summary>
        public static void Reset()
        {
            for (int k = 0; k < Kinds; k++)
                Circles[k].Clear();
            DetectorBuf.Clear();
            VisionCache.Clear();
            Heights.Clear();
            foreach (KeyValuePair<Unit, FineProfile> kv in Fine)
                FinePool.Push(kv.Value);
            Fine.Clear();
            _nextRefresh = 0f;
        }
    }
}
