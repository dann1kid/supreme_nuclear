using System.Collections.Generic;
using UnityEngine;


namespace SupremeNuclear
{
    // Часть CommanderHud: маршруты авиации и кривые планов.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; OnGUI-диспетчер — CommanderHud.cs.
    public sealed partial class CommanderHud
    {
        private void DrawAircraftRoutes(CommanderModeController controller)
        {
            Camera cam = GameApi.MainCam;
            if (cam == null || !GameApi.TryGetLocalHQ(out FactionHQ hq))
                return;
            bool shiftHeld = RtsInput.Shift;

            // Наземка/флот: ноги якорь→wp[qi] и wp[i]→wp[i+1]. Свои только:
            // AI-цепочки/планы делят те же реестры, чужие ленты не рисуем.
            List<Unit> gUnits = RouteUnits;
            List<IReadOnlyList<GlobalPosition>> gQueues = RouteQueues;
            List<int> gIdx = RouteIdx;
            GroundOrders.CollectAll(gUnits, gQueues, gIdx, hq);
            Color routeC = ModeColor(AirOrderMode.Transit);

            // Групповые вейпоинты (SupCom): групповая точка — свойство ТОЧКИ (groupId
            // клика), а НЕ выделения. У всех юнитов с этим groupId она рисуется одним
            // маркером-центроидом и общей лентой (дедуп между юнитами) — сколько бы
            // членов группы ни выделено (хоть один, хоть никто при Shift). Индивидуальные
            // точки (groupId=0) — только у своего юнита: личные ветки СХОДЯТСЯ к
            // магистрали группы и РАСХОДЯТСЯ из неё.
            // Накопители центроидов: xyz = сумма, w = счётчик.
            GidPointSums.Clear();
            GidAnchorSums.Clear();
            for (int gi = 0; gi < gUnits.Count; gi++)
            {
                Unit unit = gUnits[gi];
                IReadOnlyList<GlobalPosition> queue = gQueues[gi];
                int qi = gIdx[gi];
                // Патруль — цикл: центроид групповой точки должен учитывать её у ВСЕХ
                // членов, независимо от того, кто где в цикле (иначе маркер «уезжал»,
                // когда один член проходил точку, и возвращался при замыкании).
                if (GroundOrders.KindOf(unit) == GroundOrderKind.Patrol)
                    qi = Mathf.Min(qi, GroundOrders.PatrolLoopStartOf(unit));
                SeenGids.Clear();
                for (int i = qi; i < queue.Count; i++)
                {
                    int gid = GroundOrders.GroupIdOf(unit, i);
                    if (gid == 0)
                        continue;
                    Vector3 w = GroundVec(queue[i]);
                    GidPointSums[gid] = (GidPointSums.TryGetValue(gid, out Vector4 acc)
                        ? acc + new Vector4(w.x, w.y, w.z, 1f)
                        : new Vector4(w.x, w.y, w.z, 1f));
                    if (SeenGids.Add(gid)) // якорь юнита — по одному на группу приказа
                    {
                        Vector3 up = unit.transform.position;
                        GidAnchorSums[gid] = (GidAnchorSums.TryGetValue(gid, out Vector4 aacc)
                            ? aacc + new Vector4(up.x, 0f, up.z, 1f)
                            : new Vector4(up.x, 0f, up.z, 1f));
                    }
                }
            }

            DrawnGidLegs.Clear();
            DrawnGidMarkers.Clear();
            DrawnGidEntries.Clear();
            DrawnGroundTargets.Clear(); // цели-узлы: дедуп крестов и ног для всей группы
            DrawnTargetExits.Clear();
            DrawnEscortMarkers.Clear();
            DrawnRepairMarkers.Clear();
            // Два прохода: сначала выделенные (ярко), затем при Shift остальные —
            // общие ленты/маркеры достаются первому встретившему (дедуп).
            for (int pass = 0; pass < 2; pass++)
                for (int gi = 0; gi < gUnits.Count; gi++)
                {
                    Unit unit = gUnits[gi];
                    bool own = controller.Selection.Selected.Contains(unit);
                    if (own != (pass == 0) || (!own && !shiftHeld))
                        continue;
                    // Инженер с живой очередью: его цепочка — СЛУЖЕБНАЯ выданная голова
                    // (транспорт SetDestination), маршрут рисует проход очередей ниже;
                    // оба прохода рисовали одну и ту же голову дважды (v0.82.2).
                    if (BuildTasks.ChainIsIssuedHead(unit))
                        continue;
                    // Смешанная очередь (точки + цели на позициях серии) рисуется
                    // ЕДИНОЙ лентой через цель — DrawGroundChain, каждая нога — цвет
                    // вида своей точки. Патруль — циклом с замыкающей ногой на начало
                    // хвоста (PatrolLoopStart, конвертация «от точки»).
                    bool patrol = GroundOrders.KindOf(unit) == GroundOrderKind.Patrol;
                    // Патруль: лента от юнита к ТЕКУЩЕЙ точке, весь цикл — обёрткой
                    // (внутри DrawGroundChain), замыкание на текущую точку.
                    DrawGroundChain(cam, unit, gQueues[gi], GroundOrders.DrawStartIndex(unit),
                        patrol ? ModeColor(AirOrderMode.Patrol) : routeC,
                        own ? 0.87f : 0.2f,
                        patrol ? GroundOrders.DrawStartIndex(unit) : -1);
                }

            // Связи сопровождения наземки/флота (v0.97): эскорт-цепочки без точек —
            // CollectAll их не отдаёт; лента «эскорт → цель» + зелёный маркер на цели
            // (один на цель, дедуп общий с авиа-эскортом в DrawPlanCurve).
            DrawEscortLinks(cam, controller, shiftHeld);

            // ЕДИНАЯ очередь инженеров по порядку кликов: движения (оранж) и площадки
            // (бирюза) одной цепочкой. Запись с groupId — ГРУППОВАЯ (групповая точка —
            // свойство записи, не выделения): маркер и сегменты между соседними
            // групповыми записями рисуются ОДИН раз на всю группу приказа — сколько
            // бы инженеров ни было выделено (хоть один); подход к первой групповой —
            // одна лента от центроида инженеров группы. Индивидуальные записи —
            // личные ветки схождения/расхождения.
            EngSeqUnits.Clear();
            EngSeqs.Clear();
            foreach (Unit unit in BuildTasks.UnitsWithSequences(hq))
            {
                var seq = BuildTasks.SequenceOf(unit);
                if (seq != null && seq.Count > 0)
                {
                    EngSeqUnits.Add(unit);
                    EngSeqs.Add(seq);
                }
            }
            // Центроиды якорей инженерных групп (gid из общего счётчика с наземкой —
            // накопитель общий): xyz = сумма, w = счётчик.
            foreach (Unit unit in EngSeqUnits)
            {
                var seq = BuildTasks.SequenceOf(unit);
                SeenGids.Clear();
                for (int i = 0; i < seq.Count; i++)
                {
                    int gid = seq[i].GroupId;
                    if (gid == 0 || seq[i].Task != null || !SeenGids.Add(gid))
                        continue;
                    Vector3 up = unit.transform.position;
                    GidAnchorSums[gid] = (GidAnchorSums.TryGetValue(gid, out Vector4 aacc)
                        ? aacc + new Vector4(up.x, 0f, up.z, 1f)
                        : new Vector4(up.x, 0f, up.z, 1f));
                }
            }

            DrawnSites.Clear();
            DrawnGroupMoves.Clear();
            for (int ei = 0; ei < EngSeqs.Count; ei++)
            {
                Unit unit = EngSeqUnits[ei];
                bool own = controller.Selection.Selected.Contains(unit);
                if (!own && !shiftHeld)
                    continue;
                float a = own ? 0.87f : 0.2f;
                IReadOnlyList<BuildTasks.SeqEntry> seq = EngSeqs[ei];
                Vector3 prev = new Vector3(unit.transform.position.x, 0f, unit.transform.position.z);
                prev.y = GameApi.GroundY(prev.x, prev.z);
                var pts = EngPts;
                var cols = EngCols;
                pts.Clear();
                cols.Clear();
                foreach (var e in seq)
                {
                    Vector3 lp = (e.Task != null ? e.Task.Site : e.Move).ToLocalPosition();
                    pts.Add(new Vector3(lp.x, GameApi.GroundY(lp.x, lp.z), lp.z));
                    cols.Add(e.Task != null ? ModeColor(AirOrderMode.Patrol) : ModeColor(AirOrderMode.Transit));
                }
                ChainPts.Clear();
                ChainCols.Clear();
                ChainAlphas.Clear();
                int prevGid = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    bool sharedMove = seq[i].Task == null && seq[i].GroupId != 0;
                    int gid = seq[i].GroupId;
                    bool drawLeg = true;
                    if (i == 0 && sharedMove)
                    {
                        // Подход к первой групповой записи — от центроида якорей группы.
                        if (GidAnchorSums.TryGetValue(gid, out Vector4 aa))
                        {
                            prev = new Vector3(aa.x / aa.w, 0f, aa.z / aa.w);
                            prev.y = GameApi.GroundY(prev.x, prev.z);
                        }
                        drawLeg = DrawnGidEntries.Add(gid);
                    }
                    else if (prevGid != 0 && sharedMove)
                        // Общий сегмент (две соседние групповые записи) — один на группу.
                        drawLeg = DrawnGidLegs.Add(PairKey(prevGid, gid));

                    if (drawLeg)
                    {
                        if (ChainPts.Count == 0)
                            ChainPts.Add(prev);
                        ChainPts.Add(pts[i]);
                        ChainCols.Add(cols[i]);
                        ChainAlphas.Add(a);
                    }
                    else if (ChainPts.Count > 0)
                    {
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                    }
                    prev = pts[i];
                    prevGid = sharedMove ? gid : 0;

                    if (seq[i].Task != null)
                    {
                        if (!DrawnSites.Add(seq[i].Task))
                            continue; // маркер площадки — один на задачу
                    }
                    else if (sharedMove && !DrawnGroupMoves.Add(gid))
                        continue; // маркер группового движения — один на приказ группы
                    DrawWorldWaypointColored(cam, pts[i], Color.white, a); // канон FA
                }
                if (ChainPts.Count >= 2)
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
            }
            // Shift-режим видимости инженерных очередей уже покрыт циклом выше:
            // невыделенные рисуются приглушённо (a=0.3) тем же групповым слиянием.

            // Ралли-маршруты фабрик (депо/ангар/верфь/аэродром) — как цепочки
            // наземки; одинаковые маршруты пачки фабрик (общее ралли) рисуются ОДНОЙ
            // лентой и одним набором маркеров (дедуп DrawFactoryRally). Shift-режим
            // видимости рисует и НЕвыделенные фабрики приглушённо — как цепочки
            // юнитов (верфь/депо без выделения раньше были невидимы вовсе, §12-30).
            Airbase selAirbase = controller.Selection.SelectedAirbase;
            Color rallyColor = ModeColor(AirOrderMode.Transit);
            RallySeenPts.Clear();
            RallySeenLens.Clear();
            if (selAirbase != null)
                DrawFactoryRally(cam, selAirbase.transform.position, selAirbase, rallyColor, 0.87f);
            // Недострой-фабрика: ралли площадки видно как у готовой (v0.107).
            if (BuildTasks.IsFactorySite(BuildTasks.SelectedTask))
                DrawFactoryRally(cam, BuildTasks.SelectedTask.Site.ToLocalPosition(),
                    BuildTasks.SelectedTask, rallyColor, 0.85f);
            foreach (Unit unit in controller.Selection.Selected)
            {
                if (unit == null)
                    continue;
                if (unit is VehicleDepot depot)
                    DrawFactoryRally(cam, unit.transform.position, depot, rallyColor, 0.87f);
                else if (unit is Building b)
                {
                    Hangar hangar = b.GetComponent<Hangar>();
                    object factory = hangar != null ? (object)hangar
                        : BuildTasks.IsDock(b) ? (object)b : null;
                    if (factory != null)
                        DrawFactoryRally(cam, unit.transform.position, factory, rallyColor, 0.85f);
                }
            }
            if (shiftHeld)
            {
                ProductionTasks.CollectRallies(RallyAll, hq);
                for (int ri = 0; ri < RallyAll.Count; ri++)
                {
                    Vector3? anchor = RallyAnchor(RallyAll[ri]);
                    if (anchor.HasValue)
                        DrawFactoryRally(cam, anchor.Value, RallyAll[ri], rallyColor, 0.3f);
                }
            }

            // Авиация: планы по сегментам. Групповые точки (groupId≠0) — маркер ОДИН
            // на приказ в центроиде группы бортов (формационная решётка слипается).
            List<Unit> airUnits = RouteAirUnits;
            List<IReadOnlyList<AircraftOrders.OrderSegment>> airPlans = RouteAirPlans;
            AircraftOrders.CollectAll(airUnits, airPlans, hq);
            AirGidCentroids.Clear();
            AirGidAnchors.Clear();
            for (int ai = 0; ai < airUnits.Count; ai++)
                for (int s = 0; s < airPlans[ai].Count; s++)
                {
                    var seg = airPlans[ai][s];
                    for (int p = 0; p < seg.PointGroups.Count; p++)
                    {
                        int gid = seg.PointGroups[p];
                        if (gid == 0)
                            continue;
                        Vector3 w = GroundVec(seg.Points[p]);
                        AirGidCentroids[gid] = (AirGidCentroids.TryGetValue(gid, out Vector4 acc)
                            ? acc + new Vector4(w.x, w.y, w.z, 1f)
                            : new Vector4(w.x, w.y, w.z, 1f));
                    }
                }
            // Якорь члена группы — по одному на группу приказа (вход в магистраль
            // рисуется от центроида якорей, не от каждого борта).
            for (int ai = 0; ai < airUnits.Count; ai++)
            {
                SeenAirGids.Clear();
                for (int s = 0; s < airPlans[ai].Count; s++)
                {
                    var seg = airPlans[ai][s];
                    for (int p = 0; p < seg.PointGroups.Count; p++)
                    {
                        int gid = seg.PointGroups[p];
                        if (gid == 0 || !SeenAirGids.Add(gid))
                            continue;
                        Vector3 up = airUnits[ai].transform.position;
                        AirGidAnchors[gid] = (AirGidAnchors.TryGetValue(gid, out Vector4 aacc)
                            ? aacc + new Vector4(up.x, 0f, up.z, 1f)
                            : new Vector4(up.x, 0f, up.z, 1f));
                    }
                }
            }
            DrawnAirGid.Clear();
            DrawnAirLegs.Clear();
            DrawnAirEntries.Clear();
            DrawnAirTargets.Clear();
            DrawnRtbBases.Clear();
            for (int ai = 0; ai < airUnits.Count; ai++)
            {
                Unit unit = airUnits[ai];
                bool own = controller.Selection.Selected.Contains(unit);
                if (!own && !shiftHeld)
                    continue;
                DrawPlanCurve(cam, unit, airPlans[ai], own ? 0.87f : 0.2f);
            }
        }

        private static void DrawPlanCurve(Camera cam, Unit unit, IReadOnlyList<AircraftOrders.OrderSegment> plan, float alpha)
        {
            Vector3 up = unit.transform.position;
            Vector3 anchor = new Vector3(up.x, GameApi.GroundY(up.x, up.z), up.z);

            // Прогоны: подряд идущие сегменты одного режима — ОДНА лента, стыки между
            // ними скругляются (shift-очередь приказов не даёт лесенок на смене сегмента);
            // смена режима (цвет) или атака по цели режет ленту. Патруль замыкает прогон.
            ChainPts.Clear();
            ChainCols.Clear();
            ChainAlphas.Clear();
            ChainWidths.Clear();
            ChainFlow.Clear();
            AirOrderMode runMode = AirOrderMode.Transit;
            bool runOpen = false;
            // Групповое слияние (SupCom-магистраль, зеркально наземке): групповая
            // точка (gid≠0) — узел-ЦЕНТРОИД формационных точек группы; ноги между
            // соседними узлами группы, вход в группу, ноги атак и RTB — ОДИН раз на
            // приказ (сколько бы бортов ни рисовало); индивидуальные точки — личные
            // ветки схождения/расхождения от магистрали. Пачка вертолётов рисует
            // одну магистраль, а не N параллельных цепочек по решётке формации.
            int prevGid = 0;
            bool firstNode = true;

            for (int si = 0; si < plan.Count; si++)
            {
                AircraftOrders.OrderSegment seg = plan[si];
                if (seg == null || (seg.Points.Count == 0 && seg.Target == null))
                    continue;
                Color c = ModeColor(seg.Mode);

                if (seg.Mode == AirOrderMode.Attack && seg.Target != null)
                {
                    if (runOpen)
                    {
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
                        runOpen = false;
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        ChainWidths.Clear();
                        ChainFlow.Clear();
                    }
                    Vector3 tp = seg.Target.transform.position;
                    Vector3 tgt = new Vector3(tp.x, GameApi.GroundY(tp.x, tp.z), tp.z);
                    // Групповая атака: нога и крест — один раз на цель (пачка бортов
                    // не рисует N линий в одну и ту же цель).
                    if (DrawnAirTargets.Add(seg.Target))
                    {
                        ChainPts.Add(anchor);
                        ChainPts.Add(tgt);
                        ChainCols.Add(c);
                        ChainAlphas.Add(alpha);
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        Vector3 sp = cam.WorldToScreenPoint(GameApi.UnitAnchorWorld(seg.Target));
                        if (sp.z > 0f)
                        {
                            Vector2 scr = new Vector2(sp.x, Screen.height - sp.y);
                            float pulse = 10f + 8f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 3f));
                            DrawCross(scr, pulse * 2f, c);
                            DrawCross(scr, pulse, c);
                        }
                    }
                    anchor = tgt;
                    prevGid = 0;
                    firstNode = false;
                    continue;
                }

                if (seg.Mode == AirOrderMode.Escort && seg.Target != null)
                {
                    // Эскорт-сегмент (v0.97): зелёная нога «борт → цель» и маркер на
                    // цели (один на цель — дедуп общий с наземными связями).
                    if (runOpen)
                    {
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
                        runOpen = false;
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        ChainWidths.Clear();
                        ChainFlow.Clear();
                    }
                    Vector3 etp = seg.Target.transform.position;
                    Vector3 etgt = new Vector3(etp.x, GameApi.GroundY(etp.x, etp.z), etp.z);
                    if (DrawnEscortMarkers.Add(seg.Target))
                    {
                        ChainPts.Add(anchor);
                        ChainPts.Add(etgt);
                        ChainCols.Add(c);
                        ChainAlphas.Add(alpha);
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                        DrawWorldWaypointColored(cam, etgt, Color.white, alpha); // белый — канон FA
                    }
                    anchor = etgt;
                    prevGid = 0;
                    firstNode = false;
                    continue;
                }

                var wps = RouteWps;
                wps.Clear();
                for (int i = 0; i < seg.Points.Count; i++)
                    wps.Add(GroundVec(seg.Points[i]));
                if (wps.Count == 0)
                    continue;

                if (runOpen && seg.Mode != runMode)
                {
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                    runOpen = false;
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                }
                if (!runOpen)
                {
                    runMode = seg.Mode;
                    runOpen = true;
                }

                int hover = HoverWaypointIndex(cam, wps);
                bool loop = seg.Mode == AirOrderMode.Patrol && wps.Count > 1;
                Vector3 firstNodePos = wps[0];
                int firstGid = AircraftOrders.GroupIdOf(unit, si, 0);
                for (int i = 0; i < wps.Count; i++)
                {
                    int gid = AircraftOrders.GroupIdOf(unit, si, i);
                    Vector3 node = gid != 0 && AirGidCentroids.TryGetValue(gid, out Vector4 pc)
                        ? new Vector3(pc.x / pc.w, pc.y / pc.w, pc.z / pc.w)
                        : wps[i];
                    if (i == 0)
                        firstNodePos = node;
                    bool drawLeg;
                    if (firstNode && gid != 0)
                    {
                        // Вход в группу — от центроида якорей её членов, один раз на группу.
                        if (AirGidAnchors.TryGetValue(gid, out Vector4 anc))
                        {
                            anchor = new Vector3(anc.x / anc.w,
                                GameApi.GroundY(anc.x / anc.w, anc.z / anc.w), anc.z / anc.w);
                        }
                        drawLeg = DrawnAirEntries.Add(gid);
                    }
                    else if (prevGid != 0 && gid != 0)
                        drawLeg = DrawnAirLegs.Add(PairKey(prevGid, gid)); // сегмент магистрали
                    else if (seg.Mode == AirOrderMode.Rtb && seg.Base != null)
                        drawLeg = DrawnRtbBases.Add(seg.Base); // домой — одна нога на базу
                    else
                        drawLeg = true; // индивидуальные ветки — свои у каждого борта
                    firstNode = false;
                    if (drawLeg)
                    {
                        if (ChainPts.Count == 0)
                            ChainPts.Add(anchor); // нога от конца предыдущего сегмента прогона
                        ChainPts.Add(node);
                        ChainCols.Add(c);
                        // Hover подсвечивает ХВОСТ от наведённого узла (единая
                        // семантика с наземкой, v0.114).
                        ChainAlphas.Add(hover >= 0 && i >= hover
                            ? Mathf.Max(alpha, 1f) : alpha);
                        // Магистраль группы — толще: √n бортов приказа (кламп настройки).
                        ChainWidths.Add(gid != 0 && AirGidCentroids.TryGetValue(gid, out Vector4 gc)
                            ? Mathf.Min(Mathf.Sqrt(Mathf.Max(gc.w, 1f)), Settings.RouteGroupWidthMax)
                            : 1f);
                        ChainFlow.Add(seg.Mode == AirOrderMode.AttackMove ||
                                      seg.Mode == AirOrderMode.Patrol);
                    }
                    else if (ChainPts.Count > 0)
                    {
                        // Разрыв: общий сегмент уже нарисован другим членом группы.
                        DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas);
                        ChainPts.Clear();
                        ChainCols.Clear();
                        ChainAlphas.Clear();
                    }
                    prevGid = gid;
                    anchor = node;
                }
                if (loop)
                {
                    // Замыкающая нога патруля — тоже сегмент магистрали (дедуп).
                    bool drawClose = firstGid != 0 && prevGid != 0
                        ? DrawnAirLegs.Add(PairKey(prevGid, firstGid))
                        : true;
                    if (drawClose)
                    {
                        if (ChainPts.Count == 0)
                            ChainPts.Add(anchor);
                        ChainPts.Add(firstNodePos);
                        ChainCols.Add(c);
                        ChainAlphas.Add(alpha);
                        ChainWidths.Add(firstGid != 0 && AirGidCentroids.TryGetValue(firstGid, out Vector4 fc)
                            ? Mathf.Min(Mathf.Sqrt(Mathf.Max(fc.w, 1f)), Settings.RouteGroupWidthMax)
                            : 1f);
                        ChainFlow.Add(seg.Mode == AirOrderMode.AttackMove ||
                                      seg.Mode == AirOrderMode.Patrol);
                    }
                }
                // Маркеры: групповая точка (groupId≠0) — ОДНА на приказ группы бортов
                // (в центроиде их формационных точек, крупнее — √n), индивидуальная —
                // своя; форма = вид сегмента (квадрат/ромб/цикл, v0.114).
                // Служебные точки — НЕ вейпоинты приказа: маркер не рисуем (единое
                // правило со сканером — AircraftOrders.IsServicePoint).
                WaypointShape shape = ShapeOf(seg.Mode);
                for (int i = 0; i < wps.Count; i++)
                {
                    int gid = AircraftOrders.GroupIdOf(unit, si, i);
                    if (AircraftOrders.IsServicePoint(seg, i))
                        continue;
                    if (gid != 0)
                    {
                        if (!DrawnAirGid.Add(gid))
                            continue;
                        if (AirGidCentroids.TryGetValue(gid, out Vector4 acc))
                            DrawWorldWaypointColored(cam,
                                new Vector3(acc.x / acc.w, acc.y / acc.w, acc.z / acc.w),
                                Color.white, alpha, // маркер белый — вид несёт форма (канон FA)
                                shape,
                                Mathf.Min(Mathf.Sqrt(Mathf.Max(acc.w, 1f)), 1.6f));
                    }
                    else
                        DrawWorldWaypointColored(cam, wps[i], Color.white, alpha, shape);
                }
                if (loop)
                {
                    DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
                    runOpen = false;
                    ChainPts.Clear();
                    ChainCols.Clear();
                    ChainAlphas.Clear();
                    ChainWidths.Clear();
                    ChainFlow.Clear();
                }
            }
            if (runOpen)
                DrawChainRibbon(cam, ChainPts, ChainCols, ChainAlphas, ChainWidths, ChainFlow);
        }
    }
}
