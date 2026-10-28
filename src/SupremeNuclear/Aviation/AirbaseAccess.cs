using System.Collections.Generic;

namespace SupremeNuclear
{
    /// <summary>
    /// Режим работы аэродрома (DESIGN §2.11b): тумблеры «доступен для посадки/взлёта» —
    /// разграничение ВПП по функционалу (одна база — вылеты, другая — приём; пропускная
    /// способность полосы общая для посадок и взлётов, нативный IsAvailableForTakeoff
    /// держит вылеты, пока идёт посадка). Enforcement — нативные флаги полос
    /// Airbase.Runway.Landing/Takeoff: их учитывают и поиск ближайшего аэродрома
    /// (FactionHQ.TryGetNearestAirbase), и выдача полосы на посадку/взлёт. Вертолётные
    /// вертикальные точки флагов не имеют — их глушит AirbaseAccessPatch. Взлёт
    /// закрывается только самолётам (руление к полосе): вертолёты взлетают вертикально
    /// и ВПП не занимают; посадка закрывается всем — вертолётная точка тоже посадочная
    /// ёмкость. До первого тумблера база ведёт себя ровно как ваниль (флаги полос не
    /// трогаем: у авианосцев полоса может быть нативно только взлётной — трамплин).
    /// Флаги живут на объекте Airbase — фракция-агностичны: захватил враг — его борта
    /// тоже подчиняются (физическое состояние полосы). Хост-авторитет: мутации — только
    /// на сервере, UI читает локально (командир = хост).
    /// </summary>
    internal static class AirbaseAccess
    {
        /// <summary>Базы с закрытой посадкой/взлётом; отсутствие = открыто (ваниль).</summary>
        private static readonly HashSet<Airbase> LandingClosedSet = new HashSet<Airbase>();
        private static readonly HashSet<Airbase> TakeoffClosedSet = new HashSet<Airbase>();

        /// <summary>Растёт при любом изменении — инвалидирует кэши UI.</summary>
        public static int Version;

        private static bool IsHost
        {
            get
            {
                var net = NuclearOption.Networking.NetworkManagerNuclearOption.i;
                return net != null && net.Server != null && net.Server.Active;
            }
        }

        /// <summary>Имя аэродрома для сообщений: DisplayName сейва, иначе «аэродром».</summary>
        public static string NameOf(Airbase airbase) =>
            airbase != null && airbase.SavedAirbase != null &&
            !string.IsNullOrEmpty(airbase.SavedAirbase.DisplayName)
                ? airbase.SavedAirbase.DisplayName
                : Loc.Tr("аэродром");

        public static bool IsLandingOpen(Airbase airbase) =>
            airbase != null && !LandingClosedSet.Contains(airbase);

        public static bool IsTakeoffOpen(Airbase airbase) =>
            airbase != null && !TakeoffClosedSet.Contains(airbase);

        /// <summary>Тумблер посадки: write-through в флаги полос (нативный конвейер
        /// посадки/поиска базы уважает их сам) + вертолётные точки глушит патч.
        /// Закрытие последней посадочной базы фракции разрешено, но предупреждается.</summary>
        public static void SetLanding(Airbase airbase, bool open)
        {
            if (airbase == null || open == IsLandingOpen(airbase))
                return;
            if (!IsHost)
            {
                CommanderHud.Notice(Loc.Tr("Тумблеры аэродрома — только на хосте"));
                return;
            }
            if (open)
            {
                LandingClosedSet.Remove(airbase);
                SetRunwayFlag(airbase, false, true);
                CommanderHud.Notice(Loc.F("Посадка на {0} открыта", NameOf(airbase)));
            }
            else
            {
                LandingClosedSet.Add(airbase);
                SetRunwayFlag(airbase, false, false);
                CommanderHud.Notice(Loc.F("Посадка на {0} закрыта", NameOf(airbase)));
                if (CountLandingOpen(airbase.CurrentHQ) == 0)
                    CommanderHud.Notice(
                        Loc.Tr("Последний аэродром с посадкой: садиться больше некуда — борта с малым топливом катапультируются"),
                        6f);
            }
            Version++;
        }

        /// <summary>Тумблер взлёта: write-through в флаги полос (GetTakeoffRunway сам не
        /// выдаст полосу — борт нативно паркуется); действует только на самолёты.</summary>
        public static void SetTakeoff(Airbase airbase, bool open)
        {
            if (airbase == null || open == IsTakeoffOpen(airbase))
                return;
            if (!IsHost)
            {
                CommanderHud.Notice(Loc.Tr("Тумблеры аэродрома — только на хосте"));
                return;
            }
            if (open)
            {
                TakeoffClosedSet.Remove(airbase);
                SetRunwayFlag(airbase, true, true);
                CommanderHud.Notice(Loc.F("Взлёт с {0} открыт", NameOf(airbase)));
            }
            else
            {
                TakeoffClosedSet.Add(airbase);
                SetRunwayFlag(airbase, true, false);
                CommanderHud.Notice(Loc.F("Взлёт с {0} закрыт", NameOf(airbase)));
            }
            Version++;
        }

        /// <summary>Write-through флага во все полосы базы (null-полосы пропускаем,
        /// массив бывает null — как в AnyNearAirbasePatch).</summary>
        private static void SetRunwayFlag(Airbase airbase, bool takeoff, bool value)
        {
            Airbase.Runway[] runways = airbase.runways;
            if (runways == null)
                return;
            foreach (Airbase.Runway runway in runways)
                if (runway != null)
                    if (takeoff)
                        runway.Takeoff = value;
                    else
                        runway.Landing = value;
        }

        /// <summary>Сколько баз фракции сейчас принимают (для предупреждения о последней).</summary>
        private static int CountLandingOpen(FactionHQ hq)
        {
            if (hq == null || FactionRegistry.airbaseLookup == null)
                return 0;
            int n = 0;
            foreach (Airbase airbase in FactionRegistry.airbaseLookup.Values)
                if (airbase != null && airbase.CurrentHQ == hq && IsLandingOpen(airbase))
                    n++;
            return n;
        }

        /// <summary>Полный сброс (смена сцены): тумблеры умершей сцены недействительны.</summary>
        public static void Reset()
        {
            LandingClosedSet.Clear();
            TakeoffClosedSet.Clear();
            Version++;
        }

        /// <summary>Мёртвые базы (Unity fake-null) вычищаются из наборов.</summary>
        public static void Prune()
        {
            if (LandingClosedSet.RemoveWhere(airbase => airbase == null) > 0)
                Version++;
            if (TakeoffClosedSet.RemoveWhere(airbase => airbase == null) > 0)
                Version++;
        }
    }
}
