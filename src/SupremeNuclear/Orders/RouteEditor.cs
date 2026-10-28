using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Редактор плана: ЛКМ по вейпоинту выделенного борта = схватить и тащить.
    /// Точка снапится к юниту/экстрактору под курсором (16px), иначе ложится на рельеф.
    /// Разделение ролей: поиск вейпоинта — WaypointScanner.Scan, запись перемещения —
    /// WaypointOps.Move/сервисы; здесь — только ввод: порог клик/драг, снап, буферы
    /// группового драга. Вид захваченного — WaypointHit.Kind, без перекодировок.</summary>
    public static class RouteEditor
    {
        private const float SnapPx = 16f;

        public static bool IsDragging { get; private set; }

        public static bool Consumed { get; private set; }

        private static WaypointHit _hit;        // захваченный вейпоинт (Task — площадка)
        private static Vector2 _grabPos;        // клик без движения = выделение площадки
        private static bool _dragMoved;         // порог ClickPx пройден: это драг, не клик
        private const float ClickPx = 6f;

        // Групповой драг (SupCom): вейпоинт формации один на всех — тащим ЛЮБУЮ точку,
        // вся выделенная группа смещает свои соответствующие точки на ту же дельту.
        private static readonly List<(Unit unit, Vector3 orig)> Group = new List<(Unit, Vector3)>();
        private static readonly List<(Unit unit, int index)> GroupIndices = new List<(Unit, int)>();
        // Авиация: свои (сегмент, точка) у каждого борта — планы асинхронны,
        // групповая семантика по groupId приказа.
        private static readonly List<(Unit unit, int seg, int idx, Vector3 orig)> GroupAir =
            new List<(Unit, int, int, Vector3)>();
        // Буферы сбора членов группы приказа (CollectGroup-примитивы сервисов).
        private static readonly List<Unit> GroupMembers = new List<Unit>();
        private static readonly List<int> GroupMemberIdx = new List<int>();
        private static readonly List<Unit> AirMembers = new List<Unit>();
        private static readonly List<int> AirSegs = new List<int>();
        private static readonly List<int> AirPts = new List<int>();
        // Ралли: владелец точки — фабрика (депо/ангар/верфь/аэродром), не юнит;
        // группа клика = все фабрики, получившие точку этим кликом.
        private static readonly List<(object factory, int index, Vector3 orig)> GroupRally =
            new List<(object, int, Vector3)>();
        private static readonly List<object> RallyGroupFactories = new List<object>();
        private static readonly List<int> RallyGroupIdx = new List<int>();
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
                    bool found = !overUi && TryGrab(controller, cam, out _hit);
                    LastGrabDebug = overUi ? "курсор над UI" : found
                        ? "захват: " + KindLabel(_hit.Kind)
                        : "рядом нет вейпоинта";
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
            if (RtsInput.LeftHeld && (_hit.Owner != null || _hit.Kind == WaypointKind.BuildSite ||
                                       _hit.Kind == WaypointKind.RallyPoint))
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
                if (_dragMoved && TrySnapPoint(cam, _hit.Kind != WaypointKind.BuildSite, out Vector3 snapped))
                {
                    if (_hit.Kind == WaypointKind.BuildSite)
                        WaypointOps.Move(_hit, snapped); // guard «начатая не двигается» внутри
                    else if (_hit.Kind == WaypointKind.SequenceMove && Group.Count > 1)
                    {
                        // Групповой драг записей-движений очередей инженеров (groupId):
                        // индексы у каждого свои, дельта общая. Ветка обязана идти ДО
                        // одиночной, иначе перехватывала только захваченную запись.
                        Vector3 delta = snapped - _grabWorld;
                        for (int m = 0; m < Group.Count && m < GroupIndices.Count; m++)
                            BuildTasks.SetSequenceMove(GroupIndices[m].unit, GroupIndices[m].index,
                                Group[m].orig + delta);
                    }
                    else if (GroupAir.Count > 1)
                    {
                        // Групповой вейпоинт авиации: общая дельта, у каждого борта СВОИ
                        // сегмент/индекс точки (планы бортов асинхронны — groupId приказа).
                        Vector3 delta = snapped - _grabWorld;
                        foreach ((Unit member, int seg, int idx, Vector3 orig) in GroupAir)
                            AircraftOrders.SetPoint(member, seg, idx, orig + delta);
                    }
                    else if (GroupIndices.Count > 1)
                    {
                        // Групповой вейпоинт наземки: общая дельта, у каждого юнита свой
                        // индекс точки (в цепочки могли вклиниться индивидуальные приказы).
                        Vector3 delta = snapped - _grabWorld;
                        for (int m = 0; m < Group.Count && m < GroupIndices.Count; m++)
                            GroundOrders.SetQueuePoint(GroupIndices[m].unit, GroupIndices[m].index,
                                Group[m].orig + delta);
                    }
                    else if (GroupRally.Count > 1)
                    {
                        // Групповая точка ралли: один клик задаёт точку ВСЕМ фабрикам
                        // выделения (лента дедупится в одну) — драг правит все копии
                        // разом, общая дельта, у каждой фабрики свой индекс точки.
                        Vector3 delta = snapped - _grabWorld;
                        foreach ((object factory, int index, Vector3 orig) in GroupRally)
                            ProductionTasks.SetRallyPoint(factory, index, orig + delta);
                    }
                    else
                        WaypointOps.Move(_hit, snapped); // одиночная точка любого вида
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
                    if (_hit.Kind == WaypointKind.BuildSite && _hit.Task != null)
                    {
                        ctrl0?.Selection.Selected.Clear();
                        if (ctrl0 != null)
                            ctrl0.Selection.SelectedAirbase = null;
                        if (ctrl0 != null)
                            ctrl0.Selection.Inspected = null; // карточку осмотра сменяет недострой
                        BuildTasks.SelectedTask = _hit.Task;
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
            _hit = default;
            Group.Clear();
            GroupIndices.Clear();
            GroupAir.Clear();
            GroupRally.Clear();
        }

        /// <summary>Собрать группу для драга: групповая точка приказа = ВСЕ юниты
        /// с этим groupId (индексы точки у каждого свои), индивидуальная — только
        /// владелец. Выделение не участвует: корректировать группу можно, выделив
        /// любого её юнита. Члены группы — только через CollectGroup-примитивы
        /// сервисов (зеркально групповому удалению в WaypointOps.DeleteAt).</summary>
        private static void CaptureGroup()
        {
            Group.Clear();
            GroupIndices.Clear(); // без очистки прошлый захват протухает: одиночный драг
            // получает чужие индексы, и SetQueuePoint молча уходит по guard'у
            GroupAir.Clear();
            GroupRally.Clear(); // тот же класс бага: протухший ралли-захват перехватывал
            // одиночный драг техники веткой GroupRally.Count > 1 — точка юнита не
            // двигалась, а при следующем ралли-драге копии накапливались и тянули
            // соседние точки маршрута
            _grabWorld = Vector3.zero;
            switch (_hit.Kind)
            {
                case WaypointKind.BuildSite:
                    return; // площадки — общие через задачу
                case WaypointKind.SequenceMove:
                {
                    // Групповой драг записей-движений: члены группы приказа = ВСЕ инженеры
                    // с этим groupId (не только выделенные — корректировать можно, выделив
                    // любого инженера группы), индексы у каждого свои.
                    int gid = BuildTasks.GroupIdOf(_hit.Owner, _hit.Index);
                    bool anchored = false;
                    if (gid != 0)
                    {
                        BuildTasks.CollectGroup(gid, GroupMembers, GroupMemberIdx);
                        for (int m = 0; m < GroupMembers.Count; m++)
                        {
                            var seq = BuildTasks.SequenceOf(GroupMembers[m]);
                            if (seq == null)
                                continue;
                            Vector3 w = seq[GroupMemberIdx[m]].Move.ToLocalPosition();
                            Group.Add((GroupMembers[m], w));
                            GroupIndices.Add((GroupMembers[m], GroupMemberIdx[m]));
                            if (GroupMembers[m] == _hit.Owner && !anchored)
                            {
                                _grabWorld = w; // дельту считаем от захваченной записи
                                anchored = true;
                            }
                        }
                    }
                    if (Group.Count == 0 && _hit.Owner != null)
                    {
                        var seq0 = BuildTasks.SequenceOf(_hit.Owner);
                        if (seq0 != null && _hit.Index < seq0.Count)
                        {
                            Group.Add((_hit.Owner, seq0[_hit.Index].Move.ToLocalPosition()));
                            GroupIndices.Add((_hit.Owner, _hit.Index));
                        }
                    }
                    if (!anchored && Group.Count > 0)
                        _grabWorld = Group[0].orig;
                    return;
                }
                case WaypointKind.GroundPoint:
                {
                    // Наземка: групповая точка приказа = ВСЕ цепочки с этим groupId
                    // (индексы свои у каждого), индивидуальная (groupId=0) — только владелец.
                    int gid = GroundOrders.GroupIdOf(_hit.Owner, _hit.Index);
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
                                if (GroupMembers[m] == _hit.Owner)
                                {
                                    _grabWorld = w;
                                    anchored = true;
                                }
                            }
                    }
                    if (!anchored && GroundOrders.TryGetQueue(_hit.Owner, out var ownQ, out _) &&
                        _hit.Index < ownQ.Count)
                    {
                        Group.Add((_hit.Owner, ownQ[_hit.Index].ToLocalPosition()));
                        GroupIndices.Add((_hit.Owner, _hit.Index));
                        _grabWorld = Group[Group.Count - 1].orig;
                    }
                    if (Group.Count > 1)
                    {
                        // Якорь — ЦЕНТРОИД формации (там, где маркер группы): захват
                        // по маркеру-центроиду не прыгает на точку владельца.
                        Vector3 sum = Vector3.zero;
                        for (int m = 0; m < Group.Count; m++)
                            sum += Group[m].orig;
                        _grabWorld = sum / Group.Count;
                    }
                    return;
                }
                case WaypointKind.AirPoint:
                    // Авиация: НЕ по индексам (планы бортов асинхронны — сегменты сливаются
                    // по режиму, стартовая точка патруля у каждого своя), а по groupId приказа;
                    // члены группы = ВСЕ борта с этой точкой, у каждого свои (сегмент, точка).
                    if (_hit.Owner != null && _hit.Owner is Aircraft &&
                        AircraftOrders.TryGetPlan(_hit.Owner, out var ownPlan) &&
                        _hit.Segment < ownPlan.Count && _hit.Index < ownPlan[_hit.Segment].Points.Count)
                    {
                        Vector3 own = ownPlan[_hit.Segment].Points[_hit.Index].ToLocalPosition();
                        _grabWorld = own; // опорная — захваченная точка
                        int gid = AircraftOrders.GroupIdOf(_hit.Owner, _hit.Segment, _hit.Index);
                        if (gid == 0)
                        {
                            GroupAir.Add((_hit.Owner, _hit.Segment, _hit.Index, own)); // индивидуальная — одна
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
                            if (AirMembers[m] == _hit.Owner)
                                _grabWorld = w;
                        }
                        if (GroupAir.Count > 1)
                        {
                            // Якорь — ЦЕНТРОИД формации (там, где маркер группы):
                            // захват по маркеру-центроиду не прыгает на точку владельца.
                            Vector3 sum = Vector3.zero;
                            for (int m = 0; m < GroupAir.Count; m++)
                                sum += GroupAir[m].orig;
                            _grabWorld = sum / GroupAir.Count;
                        }
                    }
                    return;
                case WaypointKind.RallyPoint:
                {
                    // Ралли: группа клика — все фабрики, получившие эту точку одним
                    // ПКМ (мультивыделение фабрик = общий маршрут); дельту считаем от
                    // захваченной точки. Индивидуальной (groupId=0) у ралли не бывает
                    // (клик всегда несёт NextGroupId), но фолбэк оставлен зеркально
                    // остальным видам — одиночная точка через WaypointOps.Move.
                    int gid = ProductionTasks.RallyGroupIdOf(_hit.RallyFactory, _hit.Index);
                    bool anchored = false;
                    if (gid != 0)
                    {
                        ProductionTasks.CollectRallyGroup(gid, RallyGroupFactories, RallyGroupIdx);
                        for (int m = 0; m < RallyGroupFactories.Count; m++)
                        {
                            ProductionTasks.RallyRoute route =
                                ProductionTasks.RallyOf(RallyGroupFactories[m]);
                            if (route == null || RallyGroupIdx[m] >= route.Points.Count)
                                continue;
                            Vector3 w = route.Points[RallyGroupIdx[m]].ToLocalPosition();
                            GroupRally.Add((RallyGroupFactories[m], RallyGroupIdx[m], w));
                            if (ReferenceEquals(RallyGroupFactories[m], _hit.RallyFactory) &&
                                RallyGroupIdx[m] == _hit.Index)
                            {
                                _grabWorld = w; // дельту считаем от захваченной точки
                                anchored = true;
                            }
                        }
                    }
                    if (!anchored)
                    {
                        ProductionTasks.RallyRoute own = ProductionTasks.RallyOf(_hit.RallyFactory);
                        if (own != null && _hit.Index < own.Points.Count)
                        {
                            Vector3 w = own.Points[_hit.Index].ToLocalPosition();
                            GroupRally.Add((_hit.RallyFactory, _hit.Index, w));
                            _grabWorld = w;
                        }
                    }
                    return;
                }
            }
        }

        private static bool TryGrab(CommanderModeController controller, Camera cam, out WaypointHit hit)
        {
            // ЕДИНЫЙ скан (консолидация v0.48) — тот же, что у удаления вейпоинтов и
            // конвертации в патруль; тело 3D-призрака площадки сканер отдаёт как
            // BuildSite под тем же фильтром «свои» (маркер и тело — одна сущность).
            Vector2 mouse = new Vector2(RtsInput.MousePos.x, RtsInput.MousePos.y);
            hit = WaypointScanner.Scan(controller, cam, mouse, WaypointScanner.PickPx, RtsInput.Shift);
            if (hit.Kind == WaypointKind.EscortLink)
            {
                // Связь сопровождения не таскается (Ctrl+Shift+ПКМ — снять): ЛКМ по
                // цели должен ВЫДЕЛЯТЬ её, а не сбрасывать выделение как «клик мимо».
                hit = default;
                return false;
            }
            return hit.Kind != WaypointKind.None;
        }

        private static string KindLabel(WaypointKind kind)
        {
            switch (kind)
            {
                case WaypointKind.BuildSite: return "стройка";
                case WaypointKind.SequenceMove: return "очередь инженера";
                case WaypointKind.GroundPoint: return "наземка";
                case WaypointKind.AirPoint: return "авиация";
                case WaypointKind.RallyPoint: return "ралли";
                default: return "-";
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
