using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using SupremeNuclear;

namespace SupremeNuclear.Tests
{
    /// <summary>Консольный тест-раннер чистой логики мода: без фреймворков,
    /// ненулевой код выхода при провале. Запускается из tools/build.py.</summary>
    internal static partial class TestMain
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

            TestsUtilGeometry();

            TestsAircraftOrders();

            TestsHermite();

            TestsCatalogEconomy();

            TestsScenario();

            TestsOrdersAndPlural();

            TestsLocAndFitLine();

            TestsWeaponsInfo();

            TestsCameraAssign();

            TestsGroundOrders();

            TestsRingsFilters();

            TestsRouteAndPool();

            TestsMapGenFog();

            TestsIconsAndScatter();

            TestsLobby();

            Console.WriteLine(_failed == 0 ? "ВСЕ ТЕСТЫ ПРОЙДЕНЫ" : "ПРОВАЛОВ: " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        /// <summary>Носитель приватных полей для проверки Util.FieldSetter.</summary>
        private sealed class FieldHolder
        {
            private string _text;
            private UnityEngine.Vector3 _vec;

            public string Text => _text;
            public UnityEngine.Vector3 Vec => _vec;
        }

        /// <summary>Сколько ячеек сетки тумана проштамповано.</summary>
        private static int CountCells(bool[] cells)
        {
            int n = 0;
            for (int i = 0; i < cells.Length; i++)
                if (cells[i])
                    n++;
            return n;
        }
    }
}

