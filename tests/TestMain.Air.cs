using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    // Часть TestMain: планы авиации, шаблоны приказов, плюрализация.
    internal static partial class TestMain
    {
        // планы авиации (группы, служебные точки, курсор патруля) (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsAircraftOrders()
        {
            // ---------- AircraftOrders: группы приказа у точек плана ----------
            var airSeg = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Patrol };
            airSeg.Points.Add(new UnityEngine.Vector3(0f, 800f, 0f).ToGlobalPosition());
            airSeg.PointGroups.Add(0); // стартовая точка — индивидуальная
            airSeg.Points.Add(new UnityEngine.Vector3(100f, 800f, 0f).ToGlobalPosition());
            airSeg.PointGroups.Add(7);
            airSeg.Points.Add(new UnityEngine.Vector3(200f, 800f, 0f).ToGlobalPosition());
            airSeg.PointGroups.Add(8);
            var airPlan = new List<AircraftOrders.OrderSegment> { airSeg };
            Check("Авиагруппы: точка находится по groupId (индексы у каждого свои)",
                AircraftOrders.TryGetPointByGroupId(airPlan, 7, out int gs, out int gp) &&
                gs == 0 && gp == 1);
            Check("Авиагруппы: чужой groupId не найден",
                !AircraftOrders.TryGetPointByGroupId(airPlan, 99, out _, out _));
            Check("Авиагруппы: удаление по groupId убирает только её точки",
                AircraftOrders.RemovePointsWithGroup(airPlan, 7) == 1 &&
                airSeg.Points.Count == 2 && airSeg.PointGroups.Count == 2 &&
                airSeg.PointGroups[1] == 8);
            var airPlan2 = new List<AircraftOrders.OrderSegment>
            {
                new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit }
            };
            airPlan2[0].Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            airPlan2[0].PointGroups.Add(5);
            Check("Авиагруппы: опустевший сегмент удаляется",
                AircraftOrders.RemovePointsWithGroup(airPlan2, 5) == 1 && airPlan2.Count == 0);

            // Паритет группового удаления с одиночным (NormalizeAfterRemoval общий):
            var airSalvo = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Salvo };
            airSalvo.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            airSalvo.PointGroups.Add(5);
            airSalvo.SalvoTargets = new List<Unit> { null }; // залп-цели есть
            var airSalvoPlan = new List<AircraftOrders.OrderSegment> { airSalvo };
            Check("Авиагруппы: опустевший сегмент с залп-целями переживает удаление (паритет)",
                AircraftOrders.RemovePointsWithGroup(airSalvoPlan, 5) == 1 && airSalvoPlan.Count == 1);
            var airAtk = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Attack, Index = 2 };
            var airTr = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit, Index = 2 };
            foreach (AircraftOrders.OrderSegment s in new[] { airAtk, airTr })
                foreach (int g in new[] { 5, 0, 5 })
                {
                    s.Points.Add(new UnityEngine.Vector3(g, 0f, 0f).ToGlobalPosition());
                    s.PointGroups.Add(g);
                }
            var airAtkPlan = new List<AircraftOrders.OrderSegment> { airAtk };
            var airTrPlan = new List<AircraftOrders.OrderSegment> { airTr };
            AircraftOrders.RemovePointsWithGroup(airAtkPlan, 5);
            AircraftOrders.RemovePointsWithGroup(airTrPlan, 5);
            Check("Авиагруппы: курсор Attack не клампится (паритет), транзит — клампится",
                airAtk.Index == 1 && airAtk.Points.Count == 1 &&
                airTr.Index == 0 && airTr.Points.Count == 1);

            // ---------- AircraftOrders.IsServicePoint: служебные точки (сканер и маркеры) ----------
            var svcPatrol = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Patrol };
            svcPatrol.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcPatrol.Points.Add(new UnityEngine.Vector3(100f, 0f, 0f).ToGlobalPosition());
            svcPatrol.PointGroups.Add(0); // стартовая — служебная
            svcPatrol.PointGroups.Add(7); // точка приказа — обычный вейпоинт
            Check("Служебные точки: взлёт патруля — служебная, точка приказа — нет",
                AircraftOrders.IsServicePoint(svcPatrol, 0) &&
                !AircraftOrders.IsServicePoint(svcPatrol, 1));
            var svcStop = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit, ServiceStart = true };
            svcStop.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcStop.PointGroups.Add(0);
            var svcMove = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit };
            svcMove.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcMove.PointGroups.Add(0);
            var svcMoveG = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit };
            svcMoveG.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcMoveG.PointGroups.Add(3);
            Check("Служебные точки: стоп-старт отмены (флаг) — служебная; одиночная нога движения — обычный вейпоинт (v0.90)",
                AircraftOrders.IsServicePoint(svcStop, 0) &&
                !AircraftOrders.IsServicePoint(svcMove, 0) &&
                !AircraftOrders.IsServicePoint(svcMoveG, 0));
            var svcAtk = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Attack };
            svcAtk.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcAtk.PointGroups.Add(0);
            var svcRtb = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Rtb };
            svcRtb.Points.Add(new UnityEngine.Vector3(0f, 0f, 0f).ToGlobalPosition());
            svcRtb.PointGroups.Add(0);
            Check("Служебные точки: якоря атаки/RTB — служебные (крест/лента, не редактируются)",
                AircraftOrders.IsServicePoint(svcAtk, 0) && AircraftOrders.IsServicePoint(svcRtb, 0));

            // ---------- AircraftOrders: радиус зачёта вейпоинта и курсор патруля ----------
            Check("Радиус зачёта: реактивному 250 м/с — потолок 900 м (прежнее поведение)",
                AircraftOrders.WaypointReachRadius(250f) == 900f &&
                AircraftOrders.WaypointReachRadius(600f) == 900f);
            Check("Радиус зачёта: вертолёту 60 м/с — 240 м; висящему — пол 120 м",
                AircraftOrders.WaypointReachRadius(60f) == 240f &&
                AircraftOrders.WaypointReachRadius(0f) == 120f);
            var pd = new float[] { 100f, 700f, 3000f, 5000f };
            Check("Курсор патруля: текущая в радиусе, следующая вне — шаг вперёд",
                AircraftOrders.NextPatrolIndex(pd, 4, 0, 240f) == 1);
            Check("Курсор патруля: текущая вне радиуса — курсор стоит",
                AircraftOrders.NextPatrolIndex(pd, 4, 1, 240f) == 1);
            var pdClose = new float[] { 100f, 200f, 3000f, 5000f };
            Check("Курсор патруля: близкая пара зачитывается за один проход",
                AircraftOrders.NextPatrolIndex(pdClose, 4, 0, 240f) == 2);
            var pdWrap = new float[] { 100f, 5000f, 200f };
            Check("Курсор патруля: переход по кругу (последняя → нулевая → дальше)",
                AircraftOrders.NextPatrolIndex(pdWrap, 3, 2, 240f) == 1);
            var pdHang = new float[] { 100f, 150f, 120f, 140f };
            int hangNext = AircraftOrders.NextPatrolIndex(pdHang, 4, 2, 240f);
            Check("Курсор патруля: весь цикл в радиусе — цель самая дальняя точка, не зависание на стартовой",
                hangNext == 1 && hangNext != 2,
                "next=" + hangNext);
            Check("Курсор патруля: цикл из одной точки и вырожденные параметры",
                AircraftOrders.NextPatrolIndex(new float[] { 100f }, 1, 0, 240f) == 0 &&
                AircraftOrders.NextPatrolIndex(pd, 0, 3, 240f) == 3 &&
                AircraftOrders.NextPatrolIndex(pd, 4, -1, 240f) == -1);
        }
        // шаблоны приказов, конусы/дистанции, режимы бортов, плюрализация (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsOrdersAndPlural()
        {
            // ---------- OrderTemplates.ComputeOffsets: относительные позиции ----------
            var offs = OrderTemplates.ComputeOffsets(new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(100f, 0f, 200f),
                new UnityEngine.Vector3(130f, 0f, 200f),
                new UnityEngine.Vector3(100f, 0f, 260f),
            });
            Check("Offsets: первый — ноль", offs[0].x == 0f && offs[0].y == 0f);
            Check("Offsets: сосед +30x", offs[1].x == 30f && offs[1].y == 0f);
            Check("Offsets: сосед +60z", offs[2].x == 0f && offs[2].y == 60f);

            // ---------- OrderTemplates: JSON roundtrip ----------
            var template = new OrderTemplates.Template
            {
                name = "Шаблон 1",
                items = new List<OrderTemplates.Item>
                {
                    new OrderTemplates.Item { key = "sam", count = 9 },
                    new OrderTemplates.Item { key = "jack", count = 2 },
                }
            };
            var round = JsonConvert.DeserializeObject<OrderTemplates.Template>(
                JsonConvert.SerializeObject(template));
            Check("Шаблон JSON: имя", round.name == "Шаблон 1");
            Check("Шаблон JSON: позиции", round.items.Count == 2 && round.items[0].key == "sam" &&
                round.items[0].count == 9 && round.items[1].count == 2);

            // ---------- Util.WithinCone: конус атаки-движения ----------
            UnityEngine.Vector3 coneNorth = new UnityEngine.Vector3(0f, 0f, 1f);
            Check("Конус: цель по курсу",
                Util.WithinCone(coneNorth, new UnityEngine.Vector3(0f, 0f, 500f), 75f));
            Check("Конус: цель в пределах порога (30° < 75°)",
                Util.WithinCone(coneNorth, new UnityEngine.Vector3(500f, 0f, 866f), 75f));
            Check("Конус: цель за порогом (100° > 75°) — вето",
                !Util.WithinCone(coneNorth, new UnityEngine.Vector3(500f, 0f, -88f), 75f));
            Check("Конус: цель позади (180°) — вето",
                !Util.WithinCone(coneNorth, new UnityEngine.Vector3(0f, 0f, -500f), 75f));
            Check("Конус: вырожденный вектор курса не ветоит",
                Util.WithinCone(UnityEngine.Vector3.zero, coneNorth, 75f));
            Check("Конус: игнорирует высоту (вертикаль не считается углом)",
                Util.WithinCone(coneNorth, new UnityEngine.Vector3(0f, 5000f, 1f), 75f));

            // ---------- Util.ApproachDistance: дистанция подхода без «приказа назад» ----------
            Check("Подход: дальше дальности — 0.8×дальности",
                Math.Abs(Util.ApproachDistance(10000f, 5000f) - 4000f) < 0.01f);
            Check("Подход: УЖЕ в зоне — не отходит (0.9×текущей, ближе к цели)",
                Math.Abs(Util.ApproachDistance(3000f, 10000f) - 2700f) < 0.01f);
            Check("Подход: ровно на границе — слегка ближе",
                Math.Abs(Util.ApproachDistance(4000f, 5000f) - 3600f) < 0.01f);
            Check("Подход: нулевая дистанция — стоим",
                Util.ApproachDistance(0f, 5000f) == 0f);

            // ---------- AircraftOrders.ThrottleOf: скоростные режимы ----------
            Check("Тротл: Полный = 1.0", Math.Abs(AircraftOrders.ThrottleOf(0.85f, SpeedMode.Full) - 1f) < 1e-4f);
            Check("Тротл: Крейсер = бортовой cruiseThrottle",
                Math.Abs(AircraftOrders.ThrottleOf(0.85f, SpeedMode.Cruise) - 0.85f) < 1e-4f);
            Check("Тротл: Крейсер без данных борта — фолбэк 0.9",
                Math.Abs(AircraftOrders.ThrottleOf(0f, SpeedMode.Cruise) - 0.9f) < 1e-4f);
            Check("Тротл: Эконом = настройка",
                Math.Abs(AircraftOrders.ThrottleOf(0.85f, SpeedMode.Economy) - Settings.AircraftEconomyThrottle) < 1e-4f);

            // ---------- AircraftOrders.EffectiveFollowTerrain: тоггл «вдоль рельефа» ----------
            Check("Рельеф: авто = дефолт профиля",
                AircraftOrders.EffectiveFollowTerrain(true, TerrainHugMode.Profile) &&
                !AircraftOrders.EffectiveFollowTerrain(false, TerrainHugMode.Profile));
            Check("Рельеф: принудительно огибать — true даже у «Средне/Высоко»",
                AircraftOrders.EffectiveFollowTerrain(false, TerrainHugMode.Always));
            Check("Рельеф: принудительно напрямую — false даже у «Сверхнизко/Низко»",
                !AircraftOrders.EffectiveFollowTerrain(true, TerrainHugMode.Never));

            // ---------- Util.TrimSalvo: потолок ракет на цель в залпе ----------
            var salvo = new List<string> { "A", "A", "B", "A", "C", "C" };
            int trimmed = Util.TrimSalvo(salvo, 2);
            Check("Залп: потолок 2/цель — A×2 B×1 C×2, лишняя A снята",
                trimmed == 1 && salvo.SequenceEqual(new[] { "A", "A", "B", "C", "C" }),
                string.Join(",", salvo));
            var salvo1 = new List<string> { "A", "B", "A", "B" };
            Check("Залп: потолок 1/цель — по одной на цель",
                Util.TrimSalvo(salvo1, 1) == 2 && salvo1.SequenceEqual(new[] { "A", "B" }));
            var salvoAll = new List<string> { "A", "B" };
            Check("Залп: потолок не режет лишнего",
                Util.TrimSalvo(salvoAll, 4) == 0 && salvoAll.Count == 2);
            var salvoZero = new List<string> { "A", "A" };
            Check("Залп: потолок 0 — пустой залп", Util.TrimSalvo(salvoZero, 0) == 2 && salvoZero.Count == 0);

            // ---------- AircraftOrders.CanAppend: резка ног маршрута ----------
            var flightProfile = new AircraftOrders.FlightProfile(800f, false, "Средне");
            var legSame = new AircraftOrders.OrderSegment
            {
                Mode = AirOrderMode.Transit,
                AltAgl = 800f,
                FollowTerrain = false,
                Speed = SpeedMode.Full
            };
            Check("Ноги: тот же режим/профиль/скорость — точка дописывается",
                AircraftOrders.CanAppend(legSame, AirOrderMode.Transit, flightProfile, SpeedMode.Full));
            Check("Ноги: другая высота — новая нога",
                !AircraftOrders.CanAppend(legSame, AirOrderMode.Transit,
                    new AircraftOrders.FlightProfile(150f, true, "Низко"), SpeedMode.Full));
            Check("Ноги: другая скорость — новая нога",
                !AircraftOrders.CanAppend(legSame, AirOrderMode.Transit, flightProfile, SpeedMode.Cruise));
            Check("Ноги: атака никогда не сливается",
                !AircraftOrders.CanAppend(new AircraftOrders.OrderSegment
                    {
                        Mode = AirOrderMode.Attack, AltAgl = 800f, FollowTerrain = false, Speed = SpeedMode.Full
                    },
                    AirOrderMode.Attack, flightProfile, SpeedMode.Full));
            Check("Ноги: залп никогда не сливается",
                !AircraftOrders.CanAppend(legSame, AirOrderMode.Salvo, flightProfile, SpeedMode.Full));
            Check("Ноги: другой режим приказа — новая нога",
                !AircraftOrders.CanAppend(legSame, AirOrderMode.AttackMove, flightProfile, SpeedMode.Full));

            // ---------- AircraftOrders.RestampProfile: перештамповка живого патруля (§12-32) ----------
            var patrol = new AircraftOrders.OrderSegment
            {
                Mode = AirOrderMode.Patrol,
                AltAgl = 800f,
                FollowTerrain = false,
                Speed = SpeedMode.Full
            };
            patrol.Points.Add(new UnityEngine.Vector3(10f, 800f, 20f).ToGlobalPosition());
            patrol.Points.Add(new UnityEngine.Vector3(30f, 800f, 40f).ToGlobalPosition());
            patrol.PointGroups.Add(0);
            patrol.PointGroups.Add(7);
            AircraftOrders.RestampProfile(patrol,
                new AircraftOrders.FlightProfile(150f, true, "Низко"), SpeedMode.Cruise);
            bool restamped = patrol.AltAgl == 150f && patrol.FollowTerrain && patrol.Speed == SpeedMode.Cruise;
            for (int i = 0; i < patrol.Points.Count; i++)
            {
                UnityEngine.Vector3 l = patrol.Points[i].ToLocalPosition();
                restamped &= UnityEngine.Mathf.Approximately(l.y, 150f) &&
                             UnityEngine.Mathf.Approximately(l.x, i == 0 ? 10f : 30f) &&
                             UnityEngine.Mathf.Approximately(l.z, i == 0 ? 20f : 40f);
            }
            Check("Патруль §12-32: перештамповка цикла переписывает штамп и AGL точек (x/z живы)",
                restamped && patrol.Points.Count == 2 && patrol.PointGroups.Count == 2 &&
                patrol.PointGroups[1] == 7);
        }
    }
}
