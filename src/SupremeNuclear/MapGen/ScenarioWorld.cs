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

        /// <summary>Первичные отрезки ровных трасс (v0.154): износ в сплат-маске
        /// и запрет деревьев/декалей в полосе. Зеркальные копии ScatterPlan
        /// достраивает сам по симметрии карты — здесь только primary.</summary>
        public List<CorridorSeg> Corridors = new List<CorridorSeg>();

        /// <summary>Зоны штампованных площадок (v0.154): базы — прямоугольники
        /// (обе стороны), точки/уголь — круги. Запрет деревьев/шрамов,
        /// кольца износа декалей — по кромке.</summary>
        public List<PadZone> Pads = new List<PadZone>();
    }

    /// <summary>Отрезок ровной трассы проезда: A→B + половина ширины (канон
    /// Heightmap.SmoothCorridor — max(2 клетки, widthM)).</summary>
    public struct CorridorSeg
    {
        public Vector2 A, B;
        public float HalfW;
    }

    /// <summary>Зона штампа площадки: база — прямоугольник (HalfA вдоль оси),
    /// точка массы/угля — круг (Circle). Хранит ровно то, что штамповал Generate
    /// — формулы радиусов не дублируются.</summary>
    public struct PadZone
    {
        public Vector2 Center;
        public float HalfA, HalfB;
        public bool AlongX;
        public bool Circle;

        /// <summary>Точка внутри зоны с запасом margin (метры).</summary>
        public bool Contains(float x, float z, float margin)
        {
            float dx = x - Center.x, dz = z - Center.y;
            if (Circle)
                return dx * dx + dz * dz <= (HalfA + margin) * (HalfA + margin);
            float a = AlongX ? dx : dz, b = AlongX ? dz : dx;
            return Math.Abs(a) <= HalfA + margin && Math.Abs(b) <= HalfB + margin;
        }

        /// <summary>Метры снаружи зоны (0 внутри) — кольцо износа по кромке
        /// в сплат-маске.</summary>
        public float DistanceOutside(float x, float z)
        {
            float dx = Math.Abs(x - Center.x), dz = Math.Abs(z - Center.y);
            if (Circle)
                return Mathf.Max(0f, Mathf.Sqrt(dx * dx + dz * dz) - HalfA);
            float ax = AlongX ? dx : dz, az = AlongX ? dz : dx;
            float ox = Mathf.Max(0f, ax - HalfA), oz = Mathf.Max(0f, az - HalfB);
            if (ox <= 0f && oz <= 0f)
                return 0f;
            if (ox <= 0f)
                return oz;
            if (oz <= 0f)
                return ox;
            return Mathf.Sqrt(ox * ox + oz * oz);
        }
    }

    /// <summary>
    /// Единый детерминированный пайплайн спец-карты (гарантия спавна,
    /// юзер-канон 2026-10-31 «ровная площадка, нет воды»):
    /// 1) рельеф HeightmapGen (шум → СТИЛЕВАЯ МОРФОЛОГИЯ v3 land/coast/seas/
    ///    islands → острова баз → террасы → рампы → маски);
    /// 2) выбор точек ПО МАСКАМ (паттерн Neroxis: суша/не-крутость/не-край,
    ///    без окна ровности — SiteOk; финальный фолбэк StampableOk = «не край»);
    ///    водные стили: базы — ГЕОМЕТРИЧЕСКИ до рельефа, сушу под ними поднимает
    ///    IslandBump (мыс/остров), мексовка/уголь — по SiteOk как обычно;
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
            // Число мексов растём ЛИНЕЙНО с размером карты (плотность на площадь
            // — как spacing; реалии NO: родной размер 81.9 км, плейтест 2026-11-02).
            int pairs = ScenarioTemplate.PairsOf(t.players);
            float sizeScale = Mathf.Max(1f, t.terrain.sizeM / 40000f);
            t.depositSpacing *= Mathf.Max(0.5f, t.terrain.sizeM / 40000f);
            t.depositCount = Mathf.RoundToInt(t.depositCount * pairs * sizeScale);
            t.coalPerSide *= pairs;

            var rng = new System.Random(seed);
            Vector2 min = new Vector2(-t.terrain.sizeM * 0.5f, -t.terrain.sizeM * 0.5f);
            Vector2 max = -min;

            // Водные стили (coast/seas/islands): базы выбираются ГЕОМЕТРИЧЕСКИ
            // (siteOk => true — детерминированная осевая точка/дуга), рельеф
            // получает их позиции и поднимает острова/мысы (IslandBump) — суша
            // под базой гарантирована ещё ДО SiteOk. Land: прежний порядок —
            // сначала рельеф, потом выбор пригодных точек по маскам.
            bool watery = HeightmapGen.IsWatery(t.terrain);
            List<ScenarioLayout.BaseSlot> bases;
            Heightmap hm;
            if (watery)
            {
                bases = ScenarioLayout.Bases(min, max, t, rng, p => true);
                var basePoints = new List<Vector2>(bases.Count);
                foreach (ScenarioLayout.BaseSlot b in bases)
                    basePoints.Add(b.Pos);
                hm = HeightmapGen.Generate(t.terrain, seed, basePoints);
            }
            else
            {
                hm = HeightmapGen.Generate(t.terrain, seed);
                bases = ScenarioLayout.Bases(min, max, t, rng, p => hm.SiteOk(p.x, p.y));
                if (bases.Count == 0)
                    bases = ScenarioLayout.Bases(min, max, t, rng, p => hm.StampableOk(p.x, p.y));
            }

            // Спавн-мексы — ПЕРВЫМИ (random-пары их видят в spacing), симметрия —
            // образами карты (не точечным зеркалом: оно на X-зеркальном рельефе
            // рвало симметрию раскладки, плейтест 2026-11-03).
            List<ScenarioLayout.Deposit> mass = ScenarioLayout.SpawnMexes(
                t, rng, min, max, bases, p => hm.SiteOk(p.x, p.y),
                p => hm.StampableOk(p.x, p.y), t.terrain.symmetry);
            List<ScenarioLayout.Deposit> extra = ScenarioLayout.Deposits(
                min, max, t, rng, p => hm.SiteOk(p.x, p.y), bases,
                p => hm.StampableOk(p.x, p.y), t.terrain.symmetry, mass);
            mass.AddRange(extra);
            if (mass.Count == 0)
            {
                mass = ScenarioLayout.Deposits(
                    min, max, t, rng, p => hm.StampableOk(p.x, p.y), bases,
                    symmetry: t.terrain.symmetry);
            }

            List<ScenarioLayout.Deposit> coal = ScenarioLayout.CoalSites(
                min, max, t, rng, p => hm.SiteOk(p.x, p.y), mass, bases,
                p => hm.StampableOk(p.x, p.y), t.terrain.symmetry);
            if (coal.Count == 0)
                coal = ScenarioLayout.CoalSites(
                    min, max, t, rng, p => hm.StampableOk(p.x, p.y), mass, bases,
                    symmetry: t.terrain.symmetry);

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

            // Ровные трассы проезда (юзер-канон 2026-11-02): каждая база —
            // магистраль к центру + к ближайшим точкам (масса/уголь). Коридоры
            // ДО штампов площадок: штамп перезапишет высоты своей точки —
            // площадки остаются идеально ровными, коридоры выходят ИЗ них.
            // Каждый коридор дублируется X-зеркальным отрезком — точная
            // симметрия карты сохраняется (как у штампов/бампов).
            var corridors = new List<CorridorSeg>();
            if (t.terrain.corridorWidthM > 1f)
            {
                foreach (ScenarioLayout.BaseSlot b in bases)
                {
                    CorridorPair(hm, corridors, b.Pos, c, t.terrain.corridorWidthM);
                    var targets = new List<ScenarioLayout.Deposit>(mass);
                    targets.AddRange(coal);
                    targets.Sort((x, y) => (x.Pos - b.Pos).sqrMagnitude.CompareTo((y.Pos - b.Pos).sqrMagnitude));
                    int link = 0;
                    foreach (ScenarioLayout.Deposit d in targets)
                    {
                        if (link >= 8)
                            break;
                        CorridorPair(hm, corridors, b.Pos, d.Pos, t.terrain.corridorWidthM);
                        link++;
                    }
                }
            }
            var pads = new List<PadZone>();
            foreach (ScenarioLayout.BaseSlot b in bases)
            {
                hm.StampPadRect(b.Pos.x, b.Pos.y, halfAlong, halfAcross, alongX);
                Vector2 m = ScenarioLayout.Mirror(b.Pos, c);
                hm.StampPadRect(m.x, m.y, halfAlong, halfAcross, alongX);
                pads.Add(new PadZone { Center = b.Pos, HalfA = halfAlong, HalfB = halfAcross, AlongX = alongX });
                pads.Add(new PadZone { Center = m, HalfA = halfAlong, HalfB = halfAcross, AlongX = alongX });
            }
            foreach (ScenarioLayout.Deposit d in mass)
            {
                hm.StampPad(d.Pos.x, d.Pos.y, padR);
                pads.Add(new PadZone { Center = d.Pos, HalfA = padR, HalfB = padR, Circle = true });
            }
            foreach (ScenarioLayout.Deposit d in coal)
            {
                hm.StampPad(d.Pos.x, d.Pos.y, padR);
                pads.Add(new PadZone { Center = d.Pos, HalfA = padR, HalfB = padR, Circle = true });
            }
            hm.RebuildMasks(t.terrain);

            return new WorldGen { Hm = hm, Bases = bases, Mass = mass, Coal = coal,
                                  Corridors = corridors, Pads = pads };
        }

        /// <summary>Коридор + его X-зеркальная копия (симметрия рельефа по X);
        /// первичный отрезок — в реестр для дрессинга (износ/деревья).</summary>
        private static void CorridorPair(Heightmap hm, List<CorridorSeg> into,
            Vector2 a, Vector2 b, float widthM)
        {
            hm.SmoothCorridor(a, b, widthM);
            hm.SmoothCorridor(new Vector2(-a.x, a.y), new Vector2(-b.x, b.y), widthM);
            into.Add(new CorridorSeg
            {
                A = a,
                B = b,
                HalfW = Mathf.Max(hm.Step * 2f, widthM) * 0.5f,
            });
        }
    }
}
