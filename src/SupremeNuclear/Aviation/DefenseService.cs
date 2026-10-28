using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Режим авто-защиты борта (DESIGN §2.11, «РЭБ-авто»).</summary>
    public enum DefenseMode
    {
        /// <summary>При ракетном предупреждении руление отдаётся нативному уклонению:
        /// самолёты — манёвр увода + авто-ловушки (EvadeModeRadar/IR), вертолёты —
        /// нотчинг + снижение + «defensive jamming» (AIHeloCombatState.EvadeMissiles).
        /// Джаммер против радиолокационных ГСН выбирает нативный ChooseCountermeasure.</summary>
        Auto,
        /// <summary>Маршрут держим, по предупреждению автоматически сбрасываем ловушки
        /// (по типу ГСН входящей ракеты) с интервалом Settings.DefenseFlareIntervalSec.</summary>
        Flares,
        /// <summary>Не реагировать (скрытный прорыв — расчёт на то, что не увидят).</summary>
        Off
    }

    /// <summary>
    /// Авто-защита бортов с планом полёта. Статическое состояние (режимы/таймеры сброса)
    /// переживает сцены — сбрасывается в sceneLoaded (инвариант №11). Вызов из
    /// патчей руления №1/№4 (контекст FixedUpdate борта) и по хоткеям/кнопкам.
    /// </summary>
    internal static class DefenseService
    {
        private static readonly Dictionary<Unit, DefenseMode> Modes = new Dictionary<Unit, DefenseMode>();
        private static readonly Dictionary<Unit, float> NextPopAt = new Dictionary<Unit, float>();

        public static DefenseMode ModeOf(Unit unit) =>
            Modes.TryGetValue(unit, out DefenseMode mode) ? mode : DefenseMode.Auto;

        /// <summary>Цикл режимов (Alt+D / кнопки панели).</summary>
        public static DefenseMode Cycle(Unit unit)
        {
            DefenseMode next = ModeOf(unit) switch
            {
                DefenseMode.Auto => DefenseMode.Flares,
                DefenseMode.Flares => DefenseMode.Off,
                _ => DefenseMode.Auto
            };
            Modes[unit] = next;
            return next;
        }

        /// <summary>Явная установка режима (кнопки панели).</summary>
        public static void Set(Unit unit, DefenseMode mode) => Modes[unit] = mode;

        /// <summary>«Авто» + ракетное предупреждение — отдать руление нативному уклонению.</summary>
        public static bool ShouldEvadeNative(Aircraft aircraft)
        {
            if (aircraft == null || ModeOf(aircraft) != DefenseMode.Auto)
                return false;
            MissileWarning warning = aircraft.GetMissileWarningSystem();
            return warning != null && warning.IsWarning();
        }

        /// <summary>Режим «Ловушки»: маршрут держим, по предупреждению сбрасываем
        /// контрмеры по типу ГСН (джаммер против ARH/SARH выбирает игра).</summary>
        public static void TickFlares(Aircraft aircraft)
        {
            if (aircraft == null || ModeOf(aircraft) != DefenseMode.Flares)
                return;
            MissileWarning warning = aircraft.GetMissileWarningSystem();
            if (warning == null || !warning.IsWarning())
                return;
            if (!NextPopAt.TryGetValue(aircraft, out float nextAt) || Time.time < nextAt)
                return;
            if (PopCountermeasures(aircraft, warning))
                NextPopAt[aircraft] = Time.time + Settings.DefenseFlareIntervalSec;
        }

        /// <summary>Ручной/авто сброс: выбрать станцию контрмер под входящую ракету
        /// (нет ракеты — ИК-ловушки) и пустить её. true — сброс выполнен.</summary>
        public static bool PopCountermeasures(Aircraft aircraft, MissileWarning warning)
        {
            CountermeasureManager cm = aircraft.countermeasureManager;
            if (cm == null)
                return false;
            if (warning != null && warning.TryGetNearestIncoming(out Missile incoming) && incoming != null)
                cm.ChooseCountermeasure(incoming); // подберёт джаммер/чафф/ловушки под ГСН
            cm.DeployCountermeasure(aircraft);
            return true;
        }

        /// <summary>Кнопка «Ловушки!»/X: немедленный сброс по всем выделенным бортам.</summary>
        public static int PopNow(List<Unit> selected)
        {
            int popped = 0;
            foreach (Unit unit in selected)
                if (unit is Aircraft aircraft && aircraft != null && !aircraft.disabled &&
                    PopCountermeasures(aircraft, aircraft.GetMissileWarningSystem()))
                    popped++;
            return popped;
        }

        /// <summary>Мёртвые борта (Unity fake-null) вычищаются каждый тик контроллера.</summary>
        public static void Prune()
        {
            List<Unit> dead = null;
            foreach (KeyValuePair<Unit, DefenseMode> kv in Modes)
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead != null)
                foreach (Unit unit in dead)
                    Modes.Remove(unit);
            dead = null;
            foreach (KeyValuePair<Unit, float> kv in NextPopAt)
                if (kv.Key == null || kv.Key.disabled)
                    (dead ??= new List<Unit>()).Add(kv.Key);
            if (dead != null)
                foreach (Unit unit in dead)
                    NextPopAt.Remove(unit);
        }

        /// <summary>Смена сцены: борта умершей сцены недействительны (инвариант №11).</summary>
        public static void Reset()
        {
            Modes.Clear();
            NextPopAt.Clear();
        }
    }
}
