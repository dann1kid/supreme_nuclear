using System;
using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>Результат генерации мира: штампованный рельеф + раскладка
    /// (базы/масса/уголь). Детерминирован от (шаблон, сид) — единственный
    /// источник истины для ОБЕИХ сторон пайплайна (ScenarioBuild при генерации
    /// и RuntimeMap при загрузке миссии), иначе раскладка разойдётся с мешом.</summary>
    public sealed class WorldGen
    {
        public Heightmap Hm;
        public List<ScenarioLayout.BaseSlot> Bases;
        public List<ScenarioLayout.Deposit> Mass;
        public List<ScenarioLayout.Deposit> Coal;
    }

    /// <summary>
    /// Единый детерминированный пайплайн спец-карты (гарантия спавна,
    /// юзер-канон 2026-10-31 «ровная площадка, нет воды»):
    /// 1) рельеф HeightmapGen (шум → террасы → рампы → маски);
    /// 2) выбор точек ПО МАСКАМ (паттерн Neroxis: суша/не-крутость/не-край,
    ///    без окна ровности — SiteOk; финальный фолбэк StampableOk = «не край»);
    /// 3) ШТАМПЫ ровных площадок под каждую точку (Neroxis рельеф не трогает —
    ///    у него нет террас; нашу плоскость гарантирует только штамп) и
    ///    пересчёт масок: FlatOk/SiteOk внутри площадок — по построению.
    /// </summary>
    public static class ScenarioWorld
    {
        public static WorldGen Generate(ScenarioTemplate t, int seed)
        {
            // Масштаб точек ВНУТРИ пайплайна: RuntimeMap строит мир из «сырого»
            // шаблона — детерминизм требует одинаковых параметров на обеих сторонах.
            int pairs = ScenarioTemplate.PairsOf(t.players);
            t.depositSpacing *= Mathf.Max(0.5f, t.terrain.sizeM / 40000f);
            t.depositCount *= pairs;
            t.coalPerSide *= pairs;

            Heightmap hm = HeightmapGen.Generate(t.terrain, seed);
            var rng = new System.Random(seed);
            Vector2 min = new Vector2(-t.terrain.sizeM * 0.5f, -t.terrain.sizeM * 0.5f);
            Vector2 max = -min;

            // Выбор точек: сначала честные маски, при глухом рельефе — фолбэк
            // «не край» (штамп всё равно осушит и выровняет).
            List<ScenarioLayout.BaseSlot> bases =
                ScenarioLayout.Bases(min, max, t, rng, p => hm.SiteOk(p.x, p.y));
            if (bases.Count == 0)
                bases = ScenarioLayout.Bases(min, max, t, rng, p => hm.StampableOk(p.x, p.y));

            List<ScenarioLayout.Deposit> mass = ScenarioLayout.Deposits(
                min, max, t, rng, p => hm.SiteOk(p.x, p.y), bases, p => hm.StampableOk(p.x, p.y));
            if (mass.Count == 0)
                mass = ScenarioLayout.Deposits(
                    min, max, t, rng, p => hm.StampableOk(p.x, p.y), bases);

            List<ScenarioLayout.Deposit> coal = ScenarioLayout.CoalSites(
                min, max, t, rng, p => hm.SiteOk(p.x, p.y), mass, bases, p => hm.StampableOk(p.x, p.y));
            if (coal.Count == 0)
                coal = ScenarioLayout.CoalSites(
                    min, max, t, rng, p => hm.StampableOk(p.x, p.y), mass, bases);

            // Штампы: базы — прямоугольник под полосу+старт-линию+депо (обе
            // стороны зеркала), точки — круг. Кромки мягкие (см. Heightmap.StampPad).
            // Радиус не меньше 3 клеток: FlatOk смотрит окно 5×5 клеток (±2 по
            // обеим осям — до √2·2·Step ≈ 2.83·Step по диагонали) — кромка не
            // должна попадать в окно, иначе точка «ровная», а окно — нет.
            bool alongX = ScenarioLayout.LongAxis(min, max) == Vector2.right;
            Vector2 c = ScenarioLayout.Center(min, max);
            float minPad = hm.Step * 3f;
            float halfAlong = Mathf.Max(t.runwayLength * 0.5f + 360f, minPad);
            float halfAcross = Mathf.Max(270f, minPad);
            float padR = Mathf.Max(t.depositPadRadiusM, minPad);
            foreach (ScenarioLayout.BaseSlot b in bases)
            {
                hm.StampPadRect(b.Pos.x, b.Pos.y, halfAlong, halfAcross, alongX);
                Vector2 m = ScenarioLayout.Mirror(b.Pos, c);
                hm.StampPadRect(m.x, m.y, halfAlong, halfAcross, alongX);
            }
            foreach (ScenarioLayout.Deposit d in mass)
                hm.StampPad(d.Pos.x, d.Pos.y, padR);
            foreach (ScenarioLayout.Deposit d in coal)
                hm.StampPad(d.Pos.x, d.Pos.y, padR);
            hm.RebuildMasks(t.terrain);

            return new WorldGen { Hm = hm, Bases = bases, Mass = mass, Coal = coal };
        }
    }
}
