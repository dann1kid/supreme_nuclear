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

            Console.WriteLine(_failed == 0 ? "ВСЕ ТЕСТЫ ПРОЙДЕНЫ" : "ПРОВАЛОВ: " + _failed);
            return _failed == 0 ? 0 : 1;
        }
    }
}
