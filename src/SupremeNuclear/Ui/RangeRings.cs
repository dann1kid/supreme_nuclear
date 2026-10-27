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
    /// ломаная из отрезков (24..96 сегментов по экранному размеру кольца,
    /// вблизи видна ломаная — честно, как в SupCom), вершины проектируются в
    /// экран КАЖДЫЙ Repaint из живых позиций юнитов (идеальная прибитость к
    /// миру, никакой растеризации и загрузки текстур — лагать нечему), рисуются
    /// одним батчем GL.LINES поверх сцены; окно миникарты вырезается усечением
    /// отрезков (ClipOutRect, чистая — тестируется). Пересечения кругов
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
            internal float PlaneY; // высота плоскости кольца (земля под юнитом)
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

        private static Material _lineMat; // Internal-Colored (как силуэты BuildGhosts), альфа-блендинг
        private static float _nextRefresh;

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
            if (GameApi.MainCam == null)
                return;
            CollectCircles(selected);
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
                    n++;
                }
                Color col = Colors[k];
                col.a = LineAlpha;
                GL.Color(col);
                for (int i = 0; i < n; i++)
                    DrawPolygon(i, n, camPos, fwd, rightT, upT, tanH, aspect, w, h, masked, m);
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

        /// <summary>Один круг (из снимка Snap*): ломаная 24..96 сегментов
        /// (адаптивно по экранному размеру — хорда ~12 px; вблизи ломаная видна,
        /// как в SupCom), вершины поворачиваются инкрементально (без тригонометрии
        /// на вершину), проекция лучом камеры; сегменты за камерой пропускаются,
        /// ВНУТРЕННИЕ дуги пересечений с кругами того же класса скрываются
        /// (ArcInsideOther — SupCom-объединение), куски под окном миникарты
        /// вырезаются (ClipOutRect). Двойной ряд вершин = ~2 px линия.
        /// Без аллокаций.</summary>
        private static void DrawPolygon(int self, int count,
            Vector3 camPos, Vector3 fwd, Vector3 rightT, Vector3 upT, float tanH, float aspect,
            float w, float h, bool masked, Rect m)
        {
            float cx = SnapX[self], cz = SnapZ[self], planeY = SnapY[self], r = SnapR[self];
            float dist = Mathf.Sqrt((cx - camPos.x) * (cx - camPos.x) + (planeY - camPos.y) * (planeY - camPos.y) +
                                    (cz - camPos.z) * (cz - camPos.z));
            float worldPerPx = Mathf.Max(1e-3f, 2f * dist * tanH / h);
            int segs = Mathf.Clamp(Mathf.CeilToInt(6.2832f * r / worldPerPx / 12f), 24, 96);

            float ang = 2f * Mathf.PI / segs;
            float cA = Mathf.Cos(ang), sA = Mathf.Sin(ang);
            float dx = r, dz = 0f; // вершина на угле 0, далее поворот на ang за шаг
            float prevWx = 0f, prevWz = 0f, prevX = 0f, prevY = 0f;
            bool prevValid = false;
            for (int i = 0; i <= segs; i++)
            {
                float wx = cx + dx, wz = cz + dz;
                float ex = wx - camPos.x, ey = planeY - camPos.y, ez = wz - camPos.z;
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
                float planeY = float.NaN; // высота земли под юнитом — лениво (рейкаст), только если круг будет добавлен
                float PlaneY() => float.IsNaN(planeY) ? planeY = GameApi.GroundY(p.x, p.z) : planeY;

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
                        Circles[4].Add(new Circle { Unit = unit, PlaneY = PlaneY(), R = rGun });
                    if (rMaa >= 1f && Circles[5].Count < budget)
                        Circles[5].Add(new Circle { Unit = unit, PlaneY = PlaneY(), R = rMaa });
                    if (rMas >= 1f && Circles[6].Count < budget)
                        Circles[6].Add(new Circle { Unit = unit, PlaneY = PlaneY(), R = rMas });
                    if (rBmb >= 1f && Circles[7].Count < budget)
                        Circles[7].Add(new Circle { Unit = unit, PlaneY = PlaneY(), R = rBmb });
                }
                else if (Settings.RangeRingAttack || Settings.RangeRingAir)
                {
                    // Атака: максимум по станциям, РАЗРЕШЁННЫМ гейтами вооружения —
                    // выключенный класс не применяется никогда, кольца честные.
                    // Домен цели — как в самой игре (MapToolTip/АА-скан красят по
                    // effectiveness > 0): одна станция может дать оба кольца.
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
                            if (Settings.RangeRingAttack && eff.antiSurface > 0f && mr > rAs)
                                rAs = mr;
                            if (Settings.RangeRingAir && eff.antiAir > 0f && mr > rAa)
                                rAa = mr;
                        }
                    if (rAs >= 1f && Circles[0].Count < budget)
                        Circles[0].Add(new Circle { Unit = unit, PlaneY = PlaneY(), R = rAs });
                    if (rAa >= 1f && Circles[1].Count < budget)
                        Circles[1].Add(new Circle { Unit = unit, PlaneY = PlaneY(), R = rAa });
                }

                if (Settings.RangeRingVision && Circles[2].Count < budget)
                {
                    // Разведка: пер-юнитный сенсор — максимум радара/визуала детекторов
                    // (паттерн MapToolTip игры); неактивные (сложенные) радары
                    // не считаются — развёрнутый появляется с развертыванием.
                    float r = VisionRangeOf(unit);
                    if (r >= 1f)
                        Circles[2].Add(new Circle { Unit = unit, PlaneY = PlaneY(), R = r });
                }

                if (Settings.RangeRingBuild && Circles[3].Count < budget && GameApi.IsEngineer(unit))
                    Circles[3].Add(new Circle { Unit = unit, PlaneY = PlaneY(), R = Settings.BuildSiteRadiusM });
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
        /// сенсоров держит юнитов — чистим; материал без юнитов — живёт).</summary>
        public static void Reset()
        {
            for (int k = 0; k < Kinds; k++)
                Circles[k].Clear();
            DetectorBuf.Clear();
            VisionCache.Clear();
            _nextRefresh = 0f;
        }
    }
}
