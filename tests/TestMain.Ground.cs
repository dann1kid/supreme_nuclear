using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    // Часть TestMain: цепочки наземки, пулы.
    internal static partial class TestMain
    {
        // цепочки наземки (Disperse, серии, удаление, per-point, патруль) (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsGroundOrders()
        {
            // ---------- GroundOrders.DispersePick: точка группы в остатке очереди ----------
            var fanChain = new GroundOrders.Chain();
            foreach (int g in new[] { 7, 9, 7, 0 })
            {
                fanChain.Points.Add(new UnityEngine.Vector3(g, 0f, 0f).ToGlobalPosition());
                fanChain.PointGroups.Add(g);
            }
            fanChain.Index = 1; // первая точка пройдена
            Check("Наземка: группа находится в остатке (первая от Index)",
                GroundOrders.DispersePick(fanChain, 7) == 2 &&
                GroundOrders.DispersePick(fanChain, 9) == 1);
            Check("Наземка: индивидуальные точки (0) не распределяются",
                GroundOrders.DispersePick(fanChain, 0) == -1);
            Check("Наземка: чужой groupId — нет",
                GroundOrders.DispersePick(fanChain, 99) == -1);
            fanChain.Index = 4;
            Check("Наземка: пройденная очередь — пикер пуст",
                GroundOrders.DispersePick(fanChain, 7) == -1);

            // ---------- GroundOrders.TargetDue: цель дозревает на СВОЕЙ позиции серии ----------
            Check("Смешанная очередь: цель за точками на момент клика — ждёт их",
                !GroundOrders.TargetDue(1, 2, 3) && GroundOrders.TargetDue(2, 2, 3));
            Check("Смешанная очередь: точки, кликнутые ПОСЛЕ цели, её не задерживают",
                GroundOrders.TargetDue(1, 1, 5));
            Check("Смешанная очередь: цель первой в серии (точек до неё нет)",
                GroundOrders.TargetDue(0, 0, 2));
            Check("Смешанная очередь: удаления точек клампят позицию к длине очереди",
                GroundOrders.TargetDue(2, 9, 2));

            // ---------- GroundOrders.RemoveAtCore: ядро удаления точки ----------
            // (общее для одиночного Ctrl+Shift+ПКМ и группового удаления по groupId)
            var rmChain = new GroundOrders.Chain();
            foreach (int g in new[] { 1, 7, 2, 7 })
            {
                rmChain.Points.Add(new UnityEngine.Vector3(g * 10f, 0f, 0f).ToGlobalPosition());
                rmChain.PointGroups.Add(g);
            }
            rmChain.TargetAfter.Add(1); // позиция серии первой цели
            rmChain.TargetAfter.Add(3); // и второй
            rmChain.Index = 2;
            GroundOrders.RemoveAtCore(rmChain, 0); // удаление ДО текущей
            Check("Ядро удаления: точка до текущей — курсор и позиции целей сдвигаются",
                rmChain.Points.Count == 3 && rmChain.Index == 1 &&
                rmChain.TargetAfter[0] == 0 && rmChain.TargetAfter[1] == 2);
            GroundOrders.RemoveAtCore(rmChain, 1); // удаление самой текущей
            Check("Ядро удаления: текущая точка — курсор остаётся (указывает на следующую)",
                rmChain.Points.Count == 2 && rmChain.Index == 1 && rmChain.PointGroups[1] == 7);
            GroundOrders.RemoveAtCore(rmChain, 9);
            Check("Ядро удаления: невалидный индекс — no-op",
                rmChain.Points.Count == 2 && rmChain.Index == 1);

            // ---------- Per-point вид (v0.114): PointKinds параллельны Points ----------
            var ppChain = new GroundOrders.Chain { Index = 0 };
            foreach (GroundOrderKind k in new[] { GroundOrderKind.Move,
                    GroundOrderKind.AttackMove, GroundOrderKind.AttackMove, GroundOrderKind.Move })
            {
                ppChain.Points.Add(new UnityEngine.Vector3(ppChain.Points.Count * 10f, 0f, 0f)
                    .ToGlobalPosition());
                ppChain.PointGroups.Add(0);
                ppChain.PointKinds.Add(k);
            }
            Check("Per-point: вид точки читается по индексу, за границей — Move",
                GroundOrders.LinearKindAt(ppChain, 1) == GroundOrderKind.AttackMove &&
                GroundOrders.LinearKindAt(ppChain, 99) == GroundOrderKind.Move);
            ppChain.Kind = GroundOrderKind.Move;
            GroundOrders.RemoveAtCore(ppChain, 0); // удалили текущую Move — курсор на AttackMove
            Check("Per-point: удаление текущей синхронизирует Kind с новой текущей точкой",
                ppChain.Index == 0 && ppChain.PointKinds.Count == 3 &&
                ppChain.Kind == GroundOrderKind.AttackMove);
            GroundOrders.RemoveAtCore(ppChain, 0); // снова AttackMove текущая
            Check("Per-point: Kind следует виду точки при продвижении очереди удалениями",
                ppChain.Kind == GroundOrderKind.AttackMove && ppChain.Points.Count == 2);
            // Патрульный хвост: начало цикла сдвигается вместе с очередью
            var plChain = new GroundOrders.Chain { Kind = GroundOrderKind.Patrol, PatrolLoopStart = 2 };
            for (int pl = 0; pl < 4; pl++)
            {
                plChain.Points.Add(new UnityEngine.Vector3(pl * 10f, 0f, 0f).ToGlobalPosition());
                plChain.PointGroups.Add(0);
                plChain.PointKinds.Add(GroundOrderKind.Patrol);
            }
            GroundOrders.RemoveAtCore(plChain, 0);
            Check("PatrolLoopStart: удаление точки до начала цикла сдвигает начало",
                plChain.PatrolLoopStart == 1 && plChain.Points.Count == 3);

            // ---------- GroundOrders.CanClosePatrol: гейты конвертации хвостом ----------
            Check("Конвертация хвостом: линейная цепочка, точка не последняя — можно",
                GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, 2, 0, 0));
            Check("Конвертация хвостом: атака-движение тоже линейная — можно",
                GroundOrders.CanClosePatrol(GroundOrderKind.AttackMove, 4, 0, 0, 0));
            Check("Конвертация хвостом: последняя точка — циклу нужно ≥2 точек",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, 3, 0, 0));
            Check("Конвертация хвостом: ждущие цели серии запрещают (канон Moho)",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, 1, 2, 0));
            Check("Конвертация хвостом: ждущие эскорт-связи запрещают (§12-36а — вечный цикл их не дойдёт)",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, 1, 0, 1));
            Check("Конвертация хвостом: атака/патруль/эскорт/охрана не конвертятся",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Attack, 4, 1, 0, 0) &&
                !GroundOrders.CanClosePatrol(GroundOrderKind.Patrol, 4, 1, 0, 0) &&
                !GroundOrders.CanClosePatrol(GroundOrderKind.Escort, 4, 1, 0, 0) &&
                !GroundOrders.CanClosePatrol(GroundOrderKind.Guard, 4, 1, 0, 0));
            Check("Конвертация хвостом: отрицательный индекс — no-op",
                !GroundOrders.CanClosePatrol(GroundOrderKind.Move, 4, -1, 0, 0));

            // ---------- GroundOrders.FanBearingOffset: секторы полукруга атаки ----------
            Check("Полукруг: симметричный веер, центр без смещения",
                UnityEngine.Mathf.Abs(GroundOrders.FanBearingOffset(0, 3, 30f, 480f) +
                                      GroundOrders.FanBearingOffset(2, 3, 30f, 480f)) < 1e-5f &&
                GroundOrders.FanBearingOffset(1, 3, 30f, 480f) == 0f);
            Check("Полукруг: шаг дуги = arcStep/радиус",
                UnityEngine.Mathf.Abs(GroundOrders.FanBearingOffset(1, 2, 30f, 480f) - 15f / 480f) < 1e-5f);
            Check("Полукруг: одиночка — без смещения",
                GroundOrders.FanBearingOffset(0, 1, 30f, 480f) == 0f);
            Check("Полукруг: нулевой радиус не делит на ноль",
                !float.IsNaN(GroundOrders.FanBearingOffset(1, 3, 30f, 0f)) &&
                !float.IsInfinity(GroundOrders.FanBearingOffset(1, 3, 30f, 0f)));

            // ---------- AircraftOrders.TryDispersePoint: транспортные точки плана ----------
            var dpHead = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Transit, Index = 1 };
            dpHead.PointGroups.Add(5);
            dpHead.PointGroups.Add(6);
            var dpAtk = new AircraftOrders.OrderSegment { Mode = AirOrderMode.Attack };
            var dpTail = new AircraftOrders.OrderSegment { Mode = AirOrderMode.AttackMove };
            dpTail.PointGroups.Add(7);
            var dpPlan = new List<AircraftOrders.OrderSegment> { dpHead, dpAtk, dpTail };
            Check("Авиация: группа в хвостовом транспортном сегменте находится",
                AircraftOrders.TryDispersePoint(dpPlan, 7, out int ds, out int dp) && ds == 2 && dp == 0);
            Check("Авиация: пройденная точка головы пропускается (Index=1)",
                AircraftOrders.TryDispersePoint(dpPlan, 5, out _, out _) == false &&
                AircraftOrders.TryDispersePoint(dpPlan, 6, out _, out int dp6) && dp6 == 1);
            Check("Авиация: атака/залп/патруль/RTB в распределении не участвуют",
                !AircraftOrders.TryDispersePoint(new List<AircraftOrders.OrderSegment>
                {
                    new AircraftOrders.OrderSegment { Mode = AirOrderMode.Patrol }
                }, 5, out _, out _) &&
                !AircraftOrders.TryDispersePoint(dpPlan, 99, out _, out _));
        }
        // дедуп ралли-лент, пул зданий каталога (перенос серии секций Main,
        // расщепление без изменения логики 2026-11-07).
        private static void TestsRouteAndPool()
        {
            // ---------- Util.RouteSeenBefore: дедуп общего ралли пачки фабрик ----------
            var rA1 = new List<UnityEngine.Vector3> { new(10f, 0f, 10f), new(200f, 0f, 40f) };
            var rA2 = new List<UnityEngine.Vector3> { new(10f, 0f, 10f), new(200f, 0f, 40f) };
            var rB = new List<UnityEngine.Vector3> { new(10f, 0f, 10f), new(300f, 0f, 40f) };
            var rAshort = new List<UnityEngine.Vector3> { new(10f, 0f, 10f) };
            var seenFlat = new List<UnityEngine.Vector3>();
            var seenLens = new List<int>();
            Check("RouteSeenBefore: пустая история — не дубликат",
                !Util.RouteSeenBefore(seenFlat, seenLens, rA1));
            seenFlat.AddRange(rA1);
            seenLens.Add(rA1.Count);
            Check("RouteSeenBefore: тот же маршрут — дубликат",
                Util.RouteSeenBefore(seenFlat, seenLens, rA2));
            Check("RouteSeenBefore: другая точка — не дубликат",
                !Util.RouteSeenBefore(seenFlat, seenLens, rB));
            Check("RouteSeenBefore: префикс (другая длина) — не дубликат",
                !Util.RouteSeenBefore(seenFlat, seenLens, rAshort));
            seenFlat.AddRange(rB);
            seenLens.Add(rB.Count);
            Check("RouteSeenBefore: второй в истории ищется за первым",
                Util.RouteSeenBefore(seenFlat, seenLens, rB) &&
                Util.RouteSeenBefore(seenFlat, seenLens, rA1) &&
                !Util.RouteSeenBefore(seenFlat, seenLens, rAshort));

            // ---------- BuildingPool.Defaults: белый список каталога стройки ----------
            {
                var specs = BuildingPool.Defaults();
                Check("Пул: состав не пуст (≥10 ролей)", specs.Count >= 10,
                    specs.Count + " ролей");
                var roles = new HashSet<string>();
                var donors = new HashSet<string>();
                var defenseRoles = new HashSet<string> {
                    "mg", "atgm", "manpads", "aaa", "pillbox", "gabion", "tower" };
                bool unique = true, tiersOk = true, economy = false, heliOnce = false;
                foreach (PoolSpec s in specs)
                {
                    // Роли уникальны ВСЕ; доноры — вне обороны (мелкие доноры
                    // кончились: мекс делит gabionBunker1 с обороной — визуально
                    // два маленьких бокса с разными именами/иконками, 2026-11-04)
                    bool donorUnique = donors.Add(s.donor);
                    if (string.IsNullOrEmpty(s.role) || string.IsNullOrEmpty(s.donor) ||
                        !roles.Add(s.role) ||
                        !donorUnique && !defenseRoles.Contains(s.role))
                        unique = false;
                    if (s.tier < 1 || s.tier > 3)
                        tiersOk = false;
                    if (s.role == "mes" || s.role == "hyd" || s.role == "gen")
                        economy = true;
                    if (s.heliport && s.role != "heliport")
                        heliOnce = true; // флаг VTOL — только у роли heliport
                }
                Check("Пул: роли и доноры уникальны", unique);
                Check("Пул: тиры 1..3", tiersOk);
                Check("Пул: эконом-роли mes/hyd/gen есть", economy);
                Check("Пул: heliport-флаг только у вертолётной роли", !heliOnce);
                Check("Пул: доноры мекс/гидро — МЕЛКИЕ (storageTank/ammoDump)",
                    specs.Find(s => s.role == "mes").donor == "gabionBunker1" &&
                    specs.Find(s => s.role == "hyd").donor == "ammoDump");
                Check("Пул: крупные/мусорные НЕ в пуле",
                    specs.Find(s => s.donor.Contains("factory_") ||
                                    s.donor == "controlTower1" || s.donor == "radarStation1" ||
                                    s.donor == "shelter1" || s.donor == "enrichmentPlant1" ||
                                    s.donor == "refinery_main") == null);
            }
        }
    }
}
