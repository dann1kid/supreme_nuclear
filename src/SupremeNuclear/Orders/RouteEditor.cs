using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Редактор плана: ЛКМ по вейпоинту выделенного борта = схватить и тащить.
    /// Точка снапится к юниту/экстрактору под курсором (16px), иначе ложится на рельеф.</summary>
    public static class RouteEditor
    {
        private const float GrabPx = 14f;
        private const float SnapPx = 16f;

        public static bool IsDragging { get; private set; }

        /// <summary>Растёт при перетаскивании точек — кэши кривых инвалидируются.</summary>
        public static int DirtyVersion;
        public static bool Consumed { get; private set; }

        private static Unit _unit;
        private static int _segment;
        private static int _point;
        private static BuildTasks.Task _buildTask; // ссылка, не индекс: список задач
        // сжимается при завершении стройки — индекс за время драга протухает
        private static Vector2 _grabPos;           // клик без движения = выделение площадки
        private const float ClickPx = 6f;

        // Групповой драг (SupCom): вейпоинт формации один на всех — тащим ЛЮБУЮ точку,
        // вся выделенная группа смещает свои соответствующие точки на ту же дельту.
        private static readonly List<(Unit unit, Vector3 orig)> Group = new List<(Unit, Vector3)>();
        private static readonly List<(Unit unit, int index)> GroupIndices = new List<(Unit, int)>();
        private static Vector3 _grabWorld;

        /// <summary>Диагностика последней попытки захвата (строка F11).</summary>
        public static string LastGrabDebug = "-";

        public static void Tick(CommanderModeController controller)
        {
            if (!GameApi.InMission)
            {
                Reset();
                return;
            }
            Camera cam = GameApi.MainCam;
            if (cam == null)
            {
                Reset();
                return;
            }

            if (!IsDragging)
            {
                if (RtsInput.LeftDown)
                {
                    bool overUi = RtsInput.PointerOverUi;
                    _buildTask = null;
                    bool found = !overUi &&
                                 TryGrab(controller, cam, out _unit, out _segment, out _point);
                    LastGrabDebug = (overUi ? "курсор над UI" : found
                        ? $"захват: сегмент={(_segment == -2 ? "стройка" : _segment == -1 ? "наземка" : "авиация")}"
                        : "рядом нет вейпоинта");
                    Consumed = found;
                    if (found)
                    {
                        IsDragging = true;
                        _grabPos = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
                        CaptureGroup(controller, cam);
                    }
                }
                else
                {
                    Consumed = false;
                }
                return;
            }

            Consumed = true;
            if (RtsInput.LeftHeld && (_unit != null || _segment == -2))
            {
                Vector3 snapped = SnapPoint(cam);
                DirtyVersion++;
                if (_segment == -2)
                {
                    if (_buildTask == null || _buildTask.Paid <= 0f)
                        BuildTasks.MoveSite(_buildTask, snapped); // начатая — не двигается
                }
                else if (_segment == -3)
                {
                    BuildTasks.SetSequenceMove(_unit, _point, snapped);
                }
                else if (Group.Count > 1)
                {
                    // Групповой вейпоинт: общая дельта для всей формации.
                    Vector3 delta = snapped - _grabWorld;
                    if (_segment == -3)
                    {
                        // Записи-движения очередей инженеров: индексы у каждого свои.
                        for (int m = 0; m < Group.Count && m < GroupIndices.Count; m++)
                            BuildTasks.SetSequenceMove(GroupIndices[m].unit, GroupIndices[m].index,
                                Group[m].orig + delta);
                    }
                    else
                    {
                        foreach ((Unit member, Vector3 orig) in Group)
                        {
                            Vector3 target = orig + delta;
                            if (_segment < 0)
                                GroundOrders.SetQueuePoint(member, _point, target);
                            else if (member is Aircraft)
                                AircraftOrders.SetPoint(member, _segment, _point, target);
                        }
                    }
                }
                else if (_segment < 0)
                {
                    GroundOrders.SetQueuePoint(_unit, _point, snapped);
                }
                else
                {
                    AircraftOrders.SetPoint(_unit, _segment, _point, snapped);
                }
            }
            if (!RtsInput.LeftHeld)
            {
                var ctrl0 = CommanderModeController.Instance;
                if (!RtsInput.Shift &&
                    Vector2.Distance(new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y), _grabPos) < ClickPx)
                {
                    // Клик без движения по площадке = ВЫДЕЛИТЬ недострой; по любому ДРУГОМУ
                    // вейпоинту — сброс выделения (SupCom: клик мимо = пусто).
                    // Shift удерживается — режим ДОБАВЛЕНИЯ: выделение не трогаем.
                    if (_segment == -2 && _buildTask != null)
                    {
                        ctrl0?.Selection.Selected.Clear();
                        BuildTasks.SelectedTask = _buildTask;
                    }
                    else
                    {
                        ctrl0?.Selection.Selected.Clear();
                        if (ctrl0 != null)
                            ctrl0.Selection.SelectedAirbase = null;
                    }
                }
                IsDragging = false;
            }
        }

        public static void Reset()
        {
            IsDragging = false;
            Consumed = false;
            Group.Clear();
            GroupIndices.Clear();
        }

        /// <summary>Собрать группу для драга: выделенные юниты с точкой на том же месте
        /// (индекс в цепочке / сегмент+индекс в плане), с исходными позициями точек.</summary>
        private static void CaptureGroup(CommanderModeController controller, Camera cam)
        {
            Group.Clear();
            _grabWorld = Vector3.zero;
            if (_segment == -2)
                return; // площадки — общие через задачу
            if (_segment == -3)
            {
                // Групповой драг записей-движений: члены группы = та же запись у других
                // выделенных инженеров (по groupId).
                int gid = BuildTasks.GroupIdOf(_unit, _point);
                if (gid != 0)
                    foreach (Unit unit in controller.Selection.Selected)
                    {
                        var seq = BuildTasks.SequenceOf(unit);
                        if (seq == null)
                            continue;
                        for (int i = 0; i < seq.Count; i++)
                            if (seq[i].GroupId == gid && seq[i].Task == null)
                            {
                                Group.Add((unit, seq[i].Move.ToLocalPosition()));
                                GroupIndices.Add((unit, i));
                                break;
                            }
                    }
                if (Group.Count == 0 && _unit != null)
                {
                    var seq0 = BuildTasks.SequenceOf(_unit);
                    if (seq0 != null && _point < seq0.Count)
                    {
                        Group.Add((_unit, seq0[_point].Move.ToLocalPosition()));
                        GroupIndices.Add((_unit, _point));
                    }
                }
                if (Group.Count > 0)
                    _grabWorld = Group[0].orig;
                return;
            }
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                if (_segment == -1)
                {
                    if (GroundOrders.TryGetQueue(unit, out var q, out _) && _point < q.Count)
                    {
                        Vector3 w = q[_point].ToLocalPosition();
                        Group.Add((unit, w));
                    }
                }
                else if (unit is Aircraft && AircraftOrders.TryGetPlan(unit, out var plan) &&
                         _segment < plan.Count && _point < plan[_segment].Points.Count)
                {
                    Vector3 w = plan[_segment].Points[_point].ToLocalPosition();
                    Group.Add((unit, w));
                }
            }
            if (Group.Count > 0)
                _grabWorld = Group[0].orig; // опорная точка (захваченная)
        }

        private static bool TryGrab(CommanderModeController controller, Camera cam,
            out Unit unit, out int segment, out int point)
        {
            unit = null;
            segment = 0;
            point = 0;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);

            // ТЕЛО 3D-призрака: тянуть площадку можно за любую часть модели,
            // не только за маркер-вейпоинт. НАЧАТАЯ постройка не перемещается.
            foreach (BuildTasks.Task t in BuildTasks.All)
                if (t.Paid <= 0f && BuildGhosts.ScreenContains(t, cam, mouse))
                {
                    _buildTask = t;
                    segment = -2;
                    return true;
                }

            // ЕДИНЫЙ скан (консолидация v0.48): тот же, что у удаления вейпоинтов.
            WaypointHit hit = WaypointScanner.Scan(controller, cam, mouse, GrabPx, RtsInput.Shift);
            switch (hit.Kind)
            {
                case WaypointKind.BuildSite:
                    if (hit.Task.Paid <= 0f)
                    {
                        _buildTask = hit.Task;
                        segment = -2;
                        return true;
                    }
                    return false; // начатая постройка — не перемещается
                case WaypointKind.SequenceMove:
                    unit = hit.Owner;
                    segment = -3;
                    point = hit.Index;
                    return true;
                case WaypointKind.GroundPoint:
                    unit = hit.Owner;
                    segment = -1;
                    point = hit.Index;
                    return true;
                case WaypointKind.AirPoint:
                    unit = hit.Owner;
                    segment = hit.Segment;
                    point = hit.Index;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Публичный снап для постановки вейпоинтов: ближайший юнит/здание (16px) или рельеф.</summary>
        public static Vector3 SnapAt(Camera cam) => SnapPoint(cam);

        /// <summary>Снап: ближайший юнит/здание к курсору — берём точку под ним, иначе рельеф.</summary>
        private static Vector3 SnapPoint(Camera cam)
        {
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            Unit best = null;
            float bestDist = SnapPx;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit is Aircraft || unit is Missile)
                    continue;
                Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(unit));
                if (sp.z <= 0f)
                    continue;
                float d = Vector2.Distance(new Vector2(sp.x, sp.y), mouse);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = unit;
                }
            }
            if (best != null)
            {
                Vector3 p = best.transform.position;
                p.y = GameApi.GroundY(p.x, p.z);
                return p;
            }
            if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 ground))
                return ground;
            return Vector3.zero;
        }
    }
}
