using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    /// <summary>Консольный тест-раннер чистой логики мода: без фреймворков,
    /// ненулевой код выхода при провале. Запускается из tools/build.py.</summary>
    internal static class TestMain
    {
        private static int _failed;

        private static void Check(string name, bool ok, string details = null)
        {
            Console.WriteLine((ok ? "  [OK]   " : "  [FAIL] ") + name + (details != null ? " — " + details : ""));
            if (!ok)
                _failed++;
        }

        private static int Main()
        {
            Console.WriteLine("SupremeNuclear.Tests");

            // ---------- Util.Runs: серии подряд идущих ----------
            var runs = Util.Runs(new[] { "SAM", "SAM", "JCK", "SAM", "SAM", "SAM" }, (a, b) => a == b);
            Check("Runs: 3 серии", runs.Count == 3, string.Join(",", runs.Select(r => r.key + "x" + r.count)));
            Check("Runs: счётчики 2/1/3",
                runs[0].count == 2 && runs[1].count == 1 && runs[2].count == 3);
            Check("Runs: порядок сохранён",
                runs[0].key == "SAM" && runs[1].key == "JCK" && runs[2].key == "SAM");
            Check("Runs: пустой список", Util.Runs(new string[0], (a, b) => a == b).Count == 0);

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

            // ---------- Util.RoundChainCorners: скругление углов лент ----------
            var rcSmooth = new List<UnityEngine.Vector3>();
            var rcLegs = new List<int>();
            var rcCorner = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 100f),
            };
            int rcn = Util.RoundChainCorners(rcCorner, 20f, rcSmooth, rcLegs);
            bool RcHas(float x, float z) => rcSmooth.Any(v =>
                Math.Abs(v.x - x) < 0.01f && Math.Abs(v.z - z) < 0.01f);
            Check("Скругление: концы цепочки на месте",
                rcSmooth[0] == rcCorner[0] && rcSmooth[rcn - 1] == rcCorner[2], "n=" + rcn);
            Check("Скругление: точки касания на смежных ногах",
                RcHas(80f, 0f) && RcHas(100f, 20f),
                string.Join(" ", rcSmooth.Select(v => v.x + ";" + v.z)));
            Check("Скругление: угол заменён дугой, самой вершины нет", !RcHas(100f, 0f));
            Check("Скругление: середина дуги = контрольная точка Безье", RcHas(95f, 5f));
            Check("Скругление: legStart ведёт ногу от конца дуги",
                rcLegs.Count == 2 && rcLegs[0] == 0 && rcLegs[1] == 3,
                string.Join(",", rcLegs));

            var rcLine = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(50f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
            };
            int rcLn = Util.RoundChainCorners(rcLine, 20f, rcSmooth, rcLegs);
            Check("Скругление: коллинеарная цепочка проходит как есть",
                rcLn == 3 && rcSmooth[1] == rcLine[1] && rcLegs.Count == 2 && rcLegs[1] == 1);

            var rcClamp = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(10f, 0f, 0f),
                new UnityEngine.Vector3(10f, 0f, 1000f),
            };
            int rcCn = Util.RoundChainCorners(rcClamp, 100f, rcSmooth, rcLegs);
            bool RcClampHas(float x, float z) => rcSmooth.Take(rcCn).Any(v =>
                Math.Abs(v.x - x) < 0.01f && Math.Abs(v.z - z) < 0.01f);
            Check("Скругление: радиус зажат 45% короткой ноги (t=4.5)",
                RcClampHas(5.5f, 0f) && RcClampHas(10f, 4.5f),
                string.Join(" ", rcSmooth.Take(rcCn).Select(v => v.x + ";" + v.z)));

            var rcZero = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 100f),
            };
            int rcZn = Util.RoundChainCorners(rcZero, 0f, rcSmooth, rcLegs);
            Check("Скругление: нулевой радиус — цепочка без изменений", rcZn == 3);

            var rcUturn = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(0f, 0f, 0f),
            };
            int rcUn = Util.RoundChainCorners(rcUturn, 20f, rcSmooth, rcLegs);
            Check("Скругление: точный разворот — острый угол, без артефактов",
                rcUn == 3 && rcSmooth[1] == rcUturn[1]);

            var rcDup = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 100f),
            };
            int rcDn = Util.RoundChainCorners(rcDup, 20f, rcSmooth, rcLegs);
            Check("Скругление: вырожденная нога пропущена, конец на месте",
                rcDn > 0 && rcSmooth[rcDn - 1] == rcDup[3] && rcLegs.Count == 3 && rcLegs[0] == 0 && rcLegs[1] == 0,
                "n=" + rcDn + " legs=" + string.Join(",", rcLegs));

            var rcTwo = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f),
            };
            int rcTn = Util.RoundChainCorners(rcTwo, 20f, rcSmooth, rcLegs);
            Check("Скругление: одна нога — без изменений",
                rcTn == 2 && rcSmooth[0] == rcTwo[0] && rcSmooth[1] == rcTwo[1] && rcLegs.Count == 1 && rcLegs[0] == 0);

            // ---------- Catalog.ApplyTierFilter: терцили по цене ----------
            var defs = Enumerable.Range(1, 9).Select(i => (Id: i, Price: (float)i * 10)).ToList();
            var t1 = defs.ToList(); Catalog.ApplyTierFilter(t1, d => d.Price, 1);
            var t2 = defs.ToList(); Catalog.ApplyTierFilter(t2, d => d.Price, 2);
            var t3 = defs.ToList(); Catalog.ApplyTierFilter(t3, d => d.Price, 3);
            var t0 = defs.ToList(); Catalog.ApplyTierFilter(t0, d => d.Price, 0);
            Check("Тир1 = 3 дешёвых", t1.Count == 3 && t1.All(d => d.Price <= 30), t1.Count.ToString());
            Check("Тир2 = 3 средних", t2.Count == 3 && t2.All(d => d.Price > 30 && d.Price <= 60));
            Check("Тир3 = 3 дорогих", t3.Count == 3 && t3.All(d => d.Price > 60));
            Check("Тир0 = без фильтра", t0.Count == 9);
            var one = new List<(int Id, float Price)> { (1, 5f) };
            Catalog.ApplyTierFilter(one, d => d.Price, 1);
            Check("Тир: один элемент не теряется (T1)", one.Count == 1);
            var one3 = new List<(int Id, float Price)> { (1, 5f) };
            Catalog.ApplyTierFilter(one3, d => d.Price, 3);
            Check("Тир: крошечный каталог не падает (T3 пуст)", one3.Count == 0);

            // ---------- Catalog.ShortLabel: короткая подпись плитки ----------
            Check("Подпись: код приоритетен", Catalog.ShortLabel("F-16C", "Fighting Falcon") == "F-16C");
            Check("Подпись: нет кода — первое слово имени",
                Catalog.ShortLabel("", "Extractor Mk2") == "Extractor");
            Check("Подпись: длинное слово обрезается до 10",
                Catalog.ShortLabel(null, "Бронированный автомобиль").Length == 10);
            Check("Подпись: пусто — ?", Catalog.ShortLabel(null, null) == "?");

            // ---------- Catalog.BadgeOf/TypeNameOf: бейджи типов наземки ----------
            Check("Бейдж: танк", Catalog.BadgeOf(VehicleType.MBT) == "ТНК");
            Check("Бейдж: ПЗРК", Catalog.BadgeOf(VehicleType.IR_SAM) == "ПЗРК");
            Check("Бейдж: ЗРК", Catalog.BadgeOf(VehicleType.R_SAM) == "ЗРК");
            Check("Бейдж: все типы непустые", Enum.GetValues(typeof(VehicleType))
                .Cast<VehicleType>().All(t => !string.IsNullOrEmpty(Catalog.BadgeOf(t))));
            Check("Бейдж: имя типа есть у всех", Enum.GetValues(typeof(VehicleType))
                .Cast<VehicleType>().All(t => !string.IsNullOrEmpty(Catalog.TypeNameOf(t))));

            // ---------- Catalog.AirBadge/AirRoleName: роль авиации ----------
            RoleIdentity Fighter() => new RoleIdentity { antiAir = 1f, antiSurface = 0.1f };
            RoleIdentity Strike() => new RoleIdentity { antiAir = 0.05f, antiSurface = 0.9f };
            RoleIdentity Multi() => new RoleIdentity { antiAir = 0.7f, antiSurface = 0.6f };
            RoleIdentity Heli() => new RoleIdentity { antiAir = 0.1f, antiSurface = 0.1f };
            var asType = new TypeIdentity { surface = 1f, air = 0f };
            var radarType = new TypeIdentity { radar = 1f };
            Check("Роль: истребитель", Catalog.AirBadge(Fighter(), asType) == "И");
            Check("Роль: ударный", Catalog.AirBadge(Strike(), asType) == "У");
            Check("Роль: многоролевой", Catalog.AirBadge(Multi(), asType) == "М");
            Check("Роль: слабый боец — транспорт", Catalog.AirBadge(Heli(), asType) == "ТР");
            Check("Роль: слабый боец-радар — ДРЛО", Catalog.AirBadge(Heli(), radarType) == "РЛС");
            Check("Роль: полное имя", Catalog.AirRoleName(Fighter(), asType) == "истребитель");

            // ---------- BuildTasks.Task.Progress01: доля оплаты ----------
            var task = new BuildTasks.Task { Cost = 250f, Paid = 100f };
            Check("Progress01: 100/250 = 0.4", Math.Abs(task.Progress01 - 0.4f) < 0.001f);
            task.Paid = 999f;
            Check("Progress01: кламп сверху", Math.Abs(task.Progress01 - 1f) < 0.001f);
            task.Paid = -5f;
            Check("Progress01: кламп снизу", Math.Abs(task.Progress01 - 0f) < 0.001f);

            // ---------- EconomyProfile.Rate: тиры и клампы ----------
            var profile = new EconomyProfile();
            Check("Rate: T1/T2/T3", profile.Rate(1) == 150f && profile.Rate(2) == 400f && profile.Rate(3) == 1000f);
            Check("Rate: кламп тира", profile.Rate(0) == 150f && profile.Rate(99) == 1000f);
            profile.incomePerMinute = new[] { 10f };
            Check("Rate: один тир", profile.Rate(3) == 10f);

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

            // ---------- RuPlural: русская плюрализация ----------
            Check("Plural: 1/2/5 носителей",
                Util.RuPlural(1, "носитель", "носителя", "носителей") == "носитель" &&
                Util.RuPlural(2, "носитель", "носителя", "носителей") == "носителя" &&
                Util.RuPlural(5, "носитель", "носителя", "носителей") == "носителей");
            Check("Plural: 11–14 всегда «много», 21 — «один»",
                Util.RuPlural(11, "тип", "типа", "типов") == "типов" &&
                Util.RuPlural(14, "тип", "типа", "типов") == "типов" &&
                Util.RuPlural(21, "тип", "типа", "типов") == "тип" &&
                Util.RuPlural(112, "тип", "типа", "типов") == "типов");

            // ---------- FitLine: однострочная подгонка под ширину панели ----------
            Check("FitLine: короткая строка без изменений",
                Util.FitLine("авиация", 48) == "авиация" &&
                Util.FitLine("авиация", 7) == "авиация");
            Check("FitLine: длинная обрезается с «…» ровно до maxChars",
                Util.FitLine("Приказ: Патруль ×12 → Атака-движ. ×3 → На базу", 20).Length == 20 &&
                Util.FitLine("Приказ: Патруль ×12 → Атака-движ. ×3 → На базу", 20).EndsWith("…"));
            Check("FitLine: пустая/null и maxChars<1",
                Util.FitLine(null, 48) == "" && Util.FitLine("", 48) == "" &&
                Util.FitLine("любой текст", 0) == "");

            // ---------- Гейты вооружения: классификация станции ----------
            Check("Класс: ядерное главнее вида (Я-бомба/Я-ракета → Nuclear)",
                Catalog.ClassOf(true, true, false, false, false, 0f, 0f) == WeaponClass.Nuclear &&
                Catalog.ClassOf(true, false, false, false, true, 0f, 1f) == WeaponClass.Nuclear);
            Check("Класс: бомбы/планирующие → Bomb",
                Catalog.ClassOf(false, true, false, false, false, 0f, 1f) == WeaponClass.Bomb &&
                Catalog.ClassOf(false, false, true, false, false, 0f, 1f) == WeaponClass.Bomb);
            Check("Класс: пушки → Gun (даже ПР/двухдоменные)",
                Catalog.ClassOf(false, false, false, true, false, 1f, 1f) == WeaponClass.Gun &&
                Catalog.ClassOf(false, false, false, true, false, 0f, 0f) == WeaponClass.Gun);
            Check("Класс: ракеты по домену (ВВ/АС/мультидомен/без домена)",
                Catalog.ClassOf(false, false, false, false, true, 0.9f, 0.1f) == WeaponClass.MissileAA &&
                Catalog.ClassOf(false, false, false, false, true, 0.1f, 0.9f) == WeaponClass.MissileAS &&
                Catalog.ClassOf(false, false, false, false, true, 0.5f, 0.5f) == WeaponClass.MissileAS &&
                Catalog.ClassOf(false, false, false, false, true, 0f, 0f) == WeaponClass.MissileAS);
            Check("Класс: лейблы всех классов",
                Catalog.ClassLabel(WeaponClass.Nuclear) == "ЯД" &&
                Catalog.ClassLabel(WeaponClass.Bomb) == "Бомбы" &&
                Catalog.ClassLabel(WeaponClass.Gun) == "Пушки" &&
                Catalog.ClassLabel(WeaponClass.MissileAA) == "Рак.ВВ" &&
                Catalog.ClassLabel(WeaponClass.MissileAS) == "Рак.АС");

            // ---------- Переводы состояний AI (инфопанель) ----------
            Check("Состояния пилота: известные переводятся",
                Catalog.RuPilotState("Bombing") == "бомбометание" &&
                Catalog.RuPilotState("Evading Missiles") == "уклонение от ракет" &&
                Catalog.RuPilotState("taking off") == "взлёт" &&
                Catalog.RuPilotState("parked") == "на стоянке");
            Check("Состояния: неизвестное — как есть, пустое — пусто",
                Catalog.RuPilotState("Final Approach Overshoot") == "Final Approach Overshoot" &&
                Catalog.RuPilotState("") == "" && Catalog.RuPilotState(null) == "" &&
                Catalog.RuShipState("attacking") == "атака" &&
                Catalog.RuShipState("docked") == "у причала" &&
                Catalog.RuShipState("weird") == "weird");

            // ---------- Сводка вооружения группы (инфопанель) ----------
            var guns = new WeaponTallyAccumulator();
            guns.BeginUnit(); // борт 1: две станции AIM-120 + бомбы
            guns.AddStation("AIM-120C", 4, 4, WeaponDomain.Air);
            guns.AddStation("AIM-120C", 2, 2, WeaponDomain.Air);
            guns.AddStation("Mk-82", 4, 6, WeaponDomain.Surface);
            guns.BeginUnit(); // борт 2: одна станция AIM-120 (расстреляна) + пушка
            guns.AddStation("AIM-120C", 0, 4, WeaponDomain.Air);
            guns.AddStation("M61", 480, 500, WeaponDomain.Air | WeaponDomain.Surface);
            Check("Сводка: станции одного имени сливаются (заряд/ёмкость суммируются)",
                guns.Rows.Count == 3 &&
                guns.Rows[0].Name == "AIM-120C" && guns.Rows[0].Ammo == 6 && guns.Rows[0].Full == 10);
            Check("Сводка: носители считаются по юнитам, не по станциям",
                guns.Rows[0].Carriers == 2 && guns.Rows[1].Carriers == 1 && guns.Rows[2].Carriers == 1);
            Check("Сводка: порядок первого появления сохраняется",
                guns.Rows[0].Name == "AIM-120C" && guns.Rows[1].Name == "Mk-82" &&
                guns.Rows[2].Name == "M61");
            Check("Сводка: домены станций OR-ятся в маску",
                guns.Rows[2].Domains == (WeaponDomain.Air | WeaponDomain.Surface));

            var acc2 = new WeaponTallyAccumulator();
            acc2.BeginUnit();
            acc2.AddStation("", 4, 4, WeaponDomain.Air);
            acc2.AddStation("Пустышка", 1, 0, WeaponDomain.Air);
            acc2.AddStation("AIM-9", 2, 2, WeaponDomain.Air);
            Check("Сводка: null/пустое имя и нулевая ёмкость игнорируются",
                acc2.Rows.Count == 1 && acc2.Rows[0].Name == "AIM-9");
            acc2.Clear();
            Check("Сводка: Clear сбрасывает всё", acc2.Rows.Count == 0);
            acc2.BeginUnit();
            acc2.AddStation("AIM-9", 1, 2, WeaponDomain.Air);
            Check("Сводка: после Clear счёт носителей с нуля", acc2.Rows[0].Carriers == 1);

            // ---------- Домен-теги оружия ----------
            Check("Теги: АА/АЗ/У по эффективности",
                Catalog.DomainTag(WeaponDomain.Air) == "АА" &&
                Catalog.DomainTag(WeaponDomain.Surface) == "АЗ" &&
                Catalog.DomainTag(WeaponDomain.Air | WeaponDomain.Surface) == "У");
            Check("Теги: противоракетное/РЭБ/ядерный префикс/пусто",
                Catalog.DomainTag(WeaponDomain.Missile) == "ПР" &&
                Catalog.DomainTag(WeaponDomain.Jammer) == "РЕБ" &&
                Catalog.DomainTag(WeaponDomain.Nuclear | WeaponDomain.Surface) == "Я АЗ" &&
                Catalog.DomainTag(WeaponDomain.None) == "");
            Check("Теги: порог эффективности — как у игры (> 0)",
                Catalog.WeaponDomains(0.01f, 0f, 0f, false, false) == WeaponDomain.Air &&
                Catalog.WeaponDomains(0f, 0.5f, 0f, true, false) ==
                    (WeaponDomain.Surface | WeaponDomain.Nuclear) &&
                Catalog.WeaponDomains(0f, 0f, 0f, false, false) == WeaponDomain.None);

            // ---------- Формат строки сводки ----------
            Check("Строка: «АА AIM-120C: 6/10 · 2 носителя»",
                Catalog.FormatWeaponRow(guns.Rows[0]) == "АА  AIM-120C: 6/10 · 2 носителя",
                Catalog.FormatWeaponRow(guns.Rows[0]));
            Check("Строка: без тега — имя сразу, ядерная — префикс",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "B61", Ammo = 1, Full = 2, Carriers = 1,
                    Domains = WeaponDomain.Nuclear | WeaponDomain.Surface
                }) == "Я АЗ B61: 1/2 · 1 носитель",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "B61", Ammo = 1, Full = 2, Carriers = 1,
                    Domains = WeaponDomain.Nuclear | WeaponDomain.Surface
                }));
            Check("Строка: расстрелянный тип помечается «пусто»",
                Catalog.FormatWeaponRow(new WeaponTally
                {
                    Name = "Mk-82", Ammo = 0, Full = 6, Carriers = 1, Domains = WeaponDomain.Surface
                }).EndsWith("· пусто"));

            // ---------- Util.ComposePitch: угол камеры + автодовороты ----------
            Check("Угол: без автодоворотов — пользовательский напрямую",
                Util.ComposePitch(40f, 87f, 65f, 0f, 0f) == 40f);
            Check("Угол: полный доворот к виду сверху",
                Util.ComposePitch(40f, 87f, 65f, 1f, 0f) == 87f);
            Check("Угол: полный доворот вниз у земли",
                Util.ComposePitch(40f, 87f, 65f, 0f, 1f) == 65f);
            Check("Угол: полосы не пересекаются — close перебивает top (у земли top=0)",
                Util.ComposePitch(40f, 87f, 65f, 0f, 0.5f) == 52.5f);
            Check("Угол: ручное вращение (оба фактора 0) даёт и «в горизонт» ниже авто-профиля",
                Util.ComposePitch(20f, 87f, 65f, 0f, 0f) == 20f);
            Check("Угол: монотонность по фактору вида сверху",
                Util.ComposePitch(40f, 87f, 65f, 0.25f, 0f) > 40f &&
                Util.ComposePitch(40f, 87f, 65f, 0.5f, 0f) > Util.ComposePitch(40f, 87f, 65f, 0.25f, 0f));

            // ---------- Util.AssignBalanced: раздача «веера» по целям ----------
            var fanUnits = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(10f, 0f, 0f),
                new UnityEngine.Vector3(100f, 0f, 0f), new UnityEngine.Vector3(110f, 0f, 0f)
            };
            var fanTargets = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(5f, 0f, 0f), new UnityEngine.Vector3(105f, 0f, 0f)
            };
            int[] fan = Util.AssignBalanced(fanUnits, fanTargets);
            Check("Веер: 4/2 — по двое к ближним (ёмкости 2+2)",
                fan != null && fan.SequenceEqual(new[] { 0, 0, 1, 1 }),
                fan != null ? string.Join(",", fan) : "null");

            var fan5u = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 1f, 0f), new UnityEngine.Vector3(0f, 2f, 0f),
                new UnityEngine.Vector3(0f, 3f, 0f), new UnityEngine.Vector3(100f, 1f, 0f),
                new UnityEngine.Vector3(100f, 2f, 0f)
            };
            var fan2t = new List<UnityEngine.Vector3>
            {
                new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(100f, 0f, 0f)
            };
            int[] fan5 = Util.AssignBalanced(fan5u, fan2t);
            Check("Веер: 5/2 — ёмкости 3+2, лишний к первой цели (порядок клика)",
                fan5.SequenceEqual(new[] { 0, 0, 0, 1, 1 }),
                string.Join(",", fan5));

            int[] fan1 = Util.AssignBalanced(
                new List<UnityEngine.Vector3> { new UnityEngine.Vector3(90f, 0f, 0f) },
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(100f, 0f, 0f)
                });
            Check("Веер: единственный атакующий — к ближней цели", fan1[0] == 1);

            int[] fanTie = Util.AssignBalanced(
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(0f, 0f, 0f)
                },
                new List<UnityEngine.Vector3>
                {
                    new UnityEngine.Vector3(10f, 0f, 0f), new UnityEngine.Vector3(-10f, 0f, 0f)
                });
            Check("Веер: эквидистанта — детерминизм (меньшие индексы вперёд)",
                fanTie.SequenceEqual(new[] { 0, 1 }), string.Join(",", fanTie));
            Check("Веер: без целей — null", Util.AssignBalanced(fanUnits, new List<UnityEngine.Vector3>()) == null);

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

            // ---------- RangeRings.ClipOutRect: вырезание окна миникарты из отрезка ----------
            {
                var rect = new UnityEngine.Rect(10f, 10f, 20f, 20f); // x 10..30, y 10..30
                int n = RangeRings.ClipOutRect(0f, 15f, 40f, 15f, rect,
                    out float ax0, out float ay0, out float ax1, out float ay1,
                    out float bx0, out float by0, out float bx1, out float by1);
                Check("Клип: сквозной отрезок — два наружных куска по краям окна",
                    n == 2 && ax0 == 0f && ax1 == 10f && bx0 == 30f && bx1 == 40f &&
                    ay0 == 15f && by1 == 15f,
                    "n=" + n + " A=" + ax0 + "," + ay0 + "-" + ax1 + "," + ay1 + " B=" + bx0 + "," + by0 + "-" + bx1 + "," + by1);
                n = RangeRings.ClipOutRect(15f, 15f, 15f, 15f, rect,
                    out ax0, out ay0, out ax1, out ay1, out bx0, out by0, out bx1, out by1);
                Check("Клип: целиком внутри окна — рисовать нечего", n == 0, "n=" + n);
                n = RangeRings.ClipOutRect(0f, 40f, 40f, 40f, rect,
                    out ax0, out ay0, out ax1, out ay1, out bx0, out by0, out bx1, out by1);
                Check("Клип: мимо окна — один кусок, отрезок не тронут",
                    n == 1 && ax0 == 0f && ay0 == 40f && ax1 == 40f && ay1 == 40f,
                    "n=" + n + " A=" + ax0 + "," + ay0 + "-" + ax1 + "," + ay1);
                n = RangeRings.ClipOutRect(15f, 15f, 40f, 15f, rect,
                    out ax0, out ay0, out ax1, out ay1, out bx0, out by0, out bx1, out by1);
                Check("Клип: начинается внутри, выходит — только хвост",
                    n == 1 && ax0 == 30f && ay0 == 15f && ax1 == 40f && ay1 == 15f,
                    "n=" + n + " A=" + ax0 + "," + ay0 + "-" + ax1 + "," + ay1);
                n = RangeRings.ClipOutRect(0f, 15f, 20f, 15f, rect,
                    out ax0, out ay0, out ax1, out ay1, out bx0, out by0, out bx1, out by1);
                Check("Клип: заканчивается внутри — только голова",
                    n == 1 && ax0 == 0f && ax1 == 10f && ay1 == 15f,
                    "n=" + n + " A=" + ax0 + "," + ay0 + "-" + ax1 + "," + ay1);
            }

            Console.WriteLine(_failed == 0 ? "ВСЕ ТЕСТЫ ПРОЙДЕНЫ" : "ПРОВАЛОВ: " + _failed);
            return _failed == 0 ? 0 : 1;
        }
    }
}
