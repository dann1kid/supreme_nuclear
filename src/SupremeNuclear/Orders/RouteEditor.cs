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
        private static bool _dragMoved;            // порог ClickPx пройден: это драг, не клик
        private const float ClickPx = 6f;

        // Групповой драг (SupCom): вейпоинт формации один на всех — тащим ЛЮБУЮ точку,
        // вся выделенная группа смещает свои соответствующие точки на ту же дельту.
        private static readonly List<(Unit unit, Vector3 orig)> Group = new List<(Unit, Vector3)>();
        private static readonly List<(Unit unit, int index)> GroupIndices = new List<(Unit, int)>();
        // Авиация: свои (сегмент, точка) у каждого борта — планы асинхронны,
        // групповая семантика по groupId приказа.
        private static readonly List<(Unit unit, int seg, int idx, Vector3 orig)> GroupAir =
            new List<(Unit, int, int, Vector3)>();
        // Буферы сбора членов группы приказа (CollectGroup-вызовы сервисов).
        private static readonly List<Unit> GroupMembers = new List<Unit>();
        private static readonly List<int> GroupMemberIdx = new List<int>();
        private static readonly List<Unit> AirMembers = new List<Unit>();
        private static readonly List<int> AirSegs = new List<int>();
        private static readonly List<int> AirPts = new List<int>();
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
                        _dragMoved = false;
                        CaptureGroup();
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
                // Порог «клик vs драг» тот же, что у классификации на отпускании:
                // пока курсор не сдвинулся на ClickPx — захват только взведён (клик по
                // площадке = выделение). Без порога ЛЮБОЕ нажатие в прямоугольнике тела
                // успевало утянуть площадку в точку под курсором («здание прыгает»).
                if (!_dragMoved && Vector2.Distance(
                        new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y), _grabPos) >= ClickPx)
                    _dragMoved = true;
                // Площадки не снапятся к юнитам (юнит — блокер зоны, не цель), и промах
                // луча по рельефу (курсор над небом) кадр пропускает — фолбэк (0,0,0)
                // телепортировал площадку в мировой ноль посреди драга.
                if (_dragMoved && TrySnapPoint(cam, _segment != -2, out Vector3 snapped))
                {
                    DirtyVersion++;
                    if (_segment == -2)
                    {
                        if (_buildTask == null || _buildTask.Paid <= 0f)
                            BuildTasks.MoveSite(_buildTask, snapped); // начатая — не двигается
                    }
                    else if (_segment == -3 && Group.Count > 1)
                    {
                        // Групповой драг записей-движений очередей инженеров (groupId):
                        // индексы у каждого свои, дельта общая. Ветка обязана идти ДО
                        // одиночной -3, иначе перехватывала только захваченную запись.
                        Vector3 delta = snapped - _grabWorld;
                        for (int m = 0; m < Group.Count && m < GroupIndices.Count; m++)
                            BuildTasks.SetSequenceMove(GroupIndices[m].unit, GroupIndices[m].index,
                                Group[m].orig + delta);
                    }
                    else if (_segment == -3)
                    {
                        BuildTasks.SetSequenceMove(_unit, _point, snapped);
                    }
                    else if (GroupAir.Count > 1)
                    {
                        // Групповой вейпоинт авиации: общая дельта, у каждого борта СВОИ
                        // сегмент/индекс точки (планы бортов асинхронны — groupId приказа).
                        Vector3 delta = snapped - _grabWorld;
                        foreach ((Unit member, int seg, int idx, Vector3 orig) in GroupAir)
                            AircraftOrders.SetPoint(member, seg, idx, orig + delta);
                    }
                    else if (GroupIndices.Count > 1 && _segment < 0)
                    {
                        // Групповой вейпоинт наземки: общая дельта, у каждого юнита свой
                        // индекс точки (в цепочки могли вклиниться индивидуальные приказы).
                        Vector3 delta = snapped - _grabWorld;
                        for (int m = 0; m < Group.Count && m < GroupIndices.Count; m++)
                            GroundOrders.SetQueuePoint(GroupIndices[m].unit, GroupIndices[m].index,
                                Group[m].orig + delta);
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
            }
            if (!RtsInput.LeftHeld)
            {
                var ctrl0 = CommanderModeController.Instance;
                if (!_dragMoved && !RtsInput.Shift)
                {
                    // Клик без движения по площадке = ВЫДЕЛИТЬ недострой; по любому ДРУГОМУ
                    // вейпоинту — сброс выделения (SupCom: клик мимо = пусто).
                    // Shift удерживается — режим ДОБАВЛЕНИЯ: выделение не трогаем.
                    if (_segment == -2 && _buildTask != null)
                    {
                        ctrl0?.Selection.Selected.Clear();
                        if (ctrl0 != null)
                            ctrl0.Selection.SelectedAirbase = null;
                        if (ctrl0 != null)
                            ctrl0.Selection.Inspected = null; // карточку осмотра сменяет недострой
                        BuildTasks.SelectedTask = _buildTask;
                    }
                    else
                    {
                        ctrl0?.Selection.Selected.Clear();
                        if (ctrl0 != null)
                            ctrl0.Selection.SelectedAirbase = null;
                        if (ctrl0 != null)
                            ctrl0.Selection.Inspected = null; // клик мимо = пусто
                    }
                }
                IsDragging = false;
                _dragMoved = false;
            }
        }

        public static void Reset()
        {
            IsDragging = false;
            _dragMoved = false;
            Consumed = false;
            Group.Clear();
            GroupIndices.Clear();
            GroupAir.Clear();
        }

        /// <summary>Собрать группу для драга: групповая точка приказа = ВСЕ юниты
        /// с этим groupId (индексы точки у каждого свои), индивидуальная — только
        /// владелец. Выделение не участвует: корректировать группу можно, выделив
        /// любого её юнита.</summary>
        private static void CaptureGroup()
        {
            Group.Clear();
            GroupIndices.Clear(); // без очистки прошлый захват протухает: одиночный драг
            // получает чужие индексы, и SetQueuePoint молча уходит по guard'у
            GroupAir.Clear();
            _grabWorld = Vector3.zero;
            if (_segment == -2)
                return; // площадки — общие через задачу
            if (_segment == -3)
            {
                // Групповой драг записей-движений: члены группы приказа = ВСЕ инженеры
                // с этим groupId (не только выделенные — корректировать можно, выделив
                // любого инженера группы), индексы у каждого свои.
                int gid = BuildTasks.GroupIdOf(_unit, _point);
                bool anchored = false;
                if (gid != 0)
                    foreach (Unit unit in BuildTasks.UnitsWithSequences())
                    {
                        var seq = BuildTasks.SequenceOf(unit);
                        if (seq == null)
                            continue;
                        for (int i = 0; i < seq.Count; i++)
                            if (seq[i].GroupId == gid && seq[i].Task == null)
                            {
                                Vector3 w = seq[i].Move.ToLocalPosition();
                                Group.Add((unit, w));
                                GroupIndices.Add((unit, i));
                                if (unit == _unit && !anchored)
                                {
                                    _grabWorld = w; // дельту считаем от захваченной записи
                                    anchored = true;
                                }
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
                if (!anchored && Group.Count > 0)
                    _grabWorld = Group[0].orig;
                return;
            }
            if (_segment == -1)
            {
                // Наземка: групповая точка приказа = ВСЕ цепочки с этим groupId
                // (индексы свои у каждого), индивидуальная (groupId=0) — только владелец.
                int gid = GroundOrders.GroupIdOf(_unit, _point);
                bool anchored = false;
                if (gid != 0)
                {
                    GroundOrders.CollectGroup(gid, GroupMembers, GroupMemberIdx);
                    for (int m = 0; m < GroupMembers.Count; m++)
                        if (GroundOrders.TryGetQueue(GroupMembers[m], out var q, out _))
                        {
                            Vector3 w = q[GroupMemberIdx[m]].ToLocalPosition();
                            Group.Add((GroupMembers[m], w));
                            GroupIndices.Add((GroupMembers[m], GroupMemberIdx[m]));
                            if (GroupMembers[m] == _unit)
                            {
                                _grabWorld = w;
                                anchored = true;
                            }
                        }
                }
                if (!anchored && GroundOrders.TryGetQueue(_unit, out var ownQ, out _) &&
                    _point < ownQ.Count)
                {
                    Group.Add((_unit, ownQ[_point].ToLocalPosition()));
                    GroupIndices.Add((_unit, _point));
                    _grabWorld = Group[Group.Count - 1].orig;
                }
                return;
            }
            foreach (Unit unit in new[] { _unit })
            // Авиация: НЕ по индексам (планы бортов асинхронны — сегменты сливаются
            // по режиму, стартовая точка патруля у каждого своя), а по groupId приказа;
            // члены группы = ВСЕ борта с этой точкой, у каждого свои (сегмент, точка).
            if (_unit != null && _unit is Aircraft &&
                AircraftOrders.TryGetPlan(_unit, out var ownPlan) &&
                _segment < ownPlan.Count && _point < ownPlan[_segment].Points.Count)
            {
                Vector3 own = ownPlan[_segment].Points[_point].ToLocalPosition();
                _grabWorld = own; // опорная — захваченная точка
                int gid = AircraftOrders.GroupIdOf(_unit, _segment, _point);
                if (gid == 0)
                {
                    GroupAir.Add((_unit, _segment, _point, own)); // индивидуальная — одна
                    return;
                }
                AircraftOrders.CollectGroup(gid, AirMembers, AirSegs, AirPts);
                for (int m = 0; m < AirMembers.Count; m++)
                {
                    if (!AircraftOrders.TryGetPlan(AirMembers[m], out var mp) ||
                        AirSegs[m] >= mp.Count || AirPts[m] >= mp[AirSegs[m]].Points.Count)
                        continue;
                    Vector3 w = mp[AirSegs[m]].Points[AirPts[m]].ToLocalPosition();
                    GroupAir.Add((AirMembers[m], AirSegs[m], AirPts[m], w));
                    if (AirMembers[m] == _unit)
                        _grabWorld = w;
                }
            }
        }

        private static bool TryGrab(CommanderModeController controller, Camera cam,
            out Unit unit, out int segment, out int point)
        {
            unit = null;
            segment = 0;
            point = 0;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);

            // ТЕЛО 3D-призрака: тянуть площадку можно за любую часть модели,
            // не только за маркер-вейпоинт. Захват разрешён и НАЧАТОЙ постройке —
            // кликом она выделяется (§14 «начатые — выделить/снести»); перемещение
            // блокирует guard по Paid в драг-ветке.
            foreach (BuildTasks.Task t in BuildTasks.All)
                if (BuildGhosts.ScreenContains(t, cam, mouse))
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
                    _buildTask = hit.Task;
                    segment = -2;
                    return true;
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
        public static Vector3 SnapAt(Camera cam) =>
            TrySnapPoint(cam, true, out Vector3 p) ? p : Vector3.zero;

        /// <summary>Снап: ближайший юнит/здание к курсору — берём точку под ним, иначе рельеф.
        /// false = снапа нет и луч мимо рельефа (небо/горизонт) — вызывающий кадр пропускает;
        /// прежний фолбэк (0,0,0) телепортировал точки в мировой ноль.</summary>
        private static bool TrySnapPoint(Camera cam, bool snapUnits, out Vector3 point)
        {
            point = Vector3.zero;
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            if (snapUnits)
            {
                // Снап — ТОЛЬКО к своим: постановка «на» чужой корабль у берега клала
                // вейпоинт в море; чужие и в редакторе точек не притягиваем.
                GameApi.TryGetLocalHQ(out FactionHQ localHq);
                Unit best = null;
                float bestDist = SnapPx;
                foreach (Unit unit in UnitRegistry.allUnits)
                {
                    if (unit == null || unit.disabled || unit is Aircraft || unit is Missile)
                        continue;
                    if (unit.NetworkHQ == null || unit.NetworkHQ != localHq)
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
                    point = p;
                    return true;
                }
            }
            if (GameApi.TryGetGroundPoint(GameApi.ScreenRay(RtsInput.MousePos), out Vector3 ground))
            {
                point = ground;
                return true;
            }
            return false;
        }
    }
}
