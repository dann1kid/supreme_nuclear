using System.Collections.Generic;

namespace SupremeNuclear
{
    /// <summary>
    /// Резерв ангаров аэродромов (DESIGN §2.11): борт, вернувшийся командой «на базу»,
    /// после обслуживания ДЕСПАВНИТСЯ в резерв — невидимый инвентарь базы (ничего не
    /// блокирует на полосах, не тикает в симуляции). Выкат — нативным спавном из ангара
    /// (бесплатно, ёмкость ангаров проверяет игра; борт выкатывается с полным боезапасом
    /// и баками — их оплатил сервис). Резерв привязан к базе и переходит вместе с ней
    /// при захвате. Хост-авторитет: UI читает локально (командир = хост).
    /// </summary>
    internal static class AirbaseReserve
    {
        public sealed class Entry
        {
            public AircraftDefinition Definition;
            public int Count;
        }

        private static readonly Dictionary<Airbase, List<Entry>> Reserve =
            new Dictionary<Airbase, List<Entry>>();

        /// <summary>Растёт при любом изменении резерва — инвалидирует кэш панели аэродрома.</summary>
        public static int Version;

        public static void Add(Airbase airbase, AircraftDefinition definition)
        {
            if (airbase == null || definition == null)
                return;
            if (!Reserve.TryGetValue(airbase, out List<Entry> list))
                list = Reserve[airbase] = new List<Entry>();
            foreach (Entry e in list)
            {
                if (e.Definition == definition)
                {
                    e.Count++;
                    Version++;
                    return;
                }
            }
            list.Add(new Entry { Definition = definition, Count = 1 });
            Version++;
        }

        public static List<Entry> Of(Airbase airbase) =>
            Reserve.TryGetValue(airbase, out List<Entry> list) ? list : null;

        /// <summary>Снять один борт из резерва (выкат). false — резерва нет.</summary>
        public static bool TryTake(Airbase airbase, AircraftDefinition definition)
        {
            List<Entry> list = Of(airbase);
            if (list == null || definition == null)
                return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Definition != definition)
                    continue;
                Version++;
                if (--list[i].Count <= 0)
                    list.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>Полный сброс (смена сцены): резервы умершей сцены недействительны.</summary>
        public static void Reset()
        {
            Reserve.Clear();
            Version++;
        }

        /// <summary>Мёртвые базы (Unity fake-null) вычищаются из ключей.</summary>
        public static void Prune()
        {
            List<Airbase> dead = null;
            foreach (Airbase airbase in Reserve.Keys)
                if (airbase == null)
                    (dead ??= new List<Airbase>()).Add(airbase);
            if (dead == null)
                return;
            foreach (Airbase airbase in dead)
                Reserve.Remove(airbase);
            Version++;
        }
    }
}
