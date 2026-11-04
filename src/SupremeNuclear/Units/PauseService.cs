using System.Collections.Generic;

namespace SupremeNuclear
{
    /// <summary>
    /// Пауза юнита (v0.151, канон SupCom — у юнита с работой есть пауза):
    /// инженер на паузе НЕ наливает — стройка (BuildTasks), ремонт
    /// (RepairService), сбор обломков (SalvageService) и захват
    /// (CaptureService) замирают, деньги/энергия не тратятся; движение и
    /// приказы живут (доедет к площадке и стоит). Пауза фабрик — своя
    /// (ProductionTasks.FactoryState.Paused, очереди производства); кнопка
    /// «Пауза» контекста действует на ВСЁ подходящее выделение сразу —
    /// фабрики + инженеры (юзер-канон 2026-11-04 «на всех, к кому подходит»;
    /// унификация состояния как у фабрик v0.100: все на паузу ↔ все сняты).
    /// </summary>
    internal static class PauseService
    {
        private static readonly HashSet<Unit> Paused = new HashSet<Unit>();

        /// <summary>Юнит на паузе (мёртвый/fake-null — нет).</summary>
        public static bool IsPaused(Unit unit) => unit != null && Paused.Contains(unit);

        /// <summary>Все ли живые из списка на паузе; пустой/мёртвый список —
        /// true (унификация состояния кнопки, как ProductionTasks.AllPaused).</summary>
        public static bool AllPaused(List<Unit> units)
        {
            for (int i = 0; i < units.Count; i++)
            {
                Unit u = units[i];
                if (u == null)
                    continue;
                if (!Paused.Contains(u))
                    return false;
            }
            return true;
        }

        public static void SetPaused(List<Unit> units, bool paused)
        {
            foreach (Unit u in units)
            {
                if (u == null)
                    continue;
                if (paused)
                    Paused.Add(u);
                else
                    Paused.Remove(u);
            }
            Paused.RemoveWhere(u => u == null);
        }

        /// <summary>Сброс сцены: юниты умерли (зовётся из sceneLoaded).</summary>
        public static void Reset() => Paused.Clear();
    }
}
