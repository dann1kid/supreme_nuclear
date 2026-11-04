using System;
using System.Collections.Generic;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Чистая геометрия сценария (без Unity-вызовов кроме Vector2/Mathf; тестируется
    /// консольным раннером). Центральная симметрия карты: точка p и её пара 2c−p —
    /// генерируем ОДНУ сторону, отражаем (DESIGN §2.4 «генерируем половину, отражаем»).
    /// Пригодность точек (ровно/не вода) передаётся делегатом — рельеф живёт снаружи.
    /// </summary>
    public static class ScenarioLayout
    {
        public struct BaseSlot
        {
            public Vector2 Pos;
            public float YawDeg; // курс полосы: вдоль оси баз (на противника)
        }

        public sealed class Deposit
        {
            public Vector2 Pos;
            public int Tier = 1;
        }

        /// <summary>Центр карты.</summary>
        public static Vector2 Center(Vector2 min, Vector2 max) => (min + max) * 0.5f;

        /// <summary>Отражение точки относительно центра (пара для симметрии).</summary>
        public static Vector2 Mirror(Vector2 p, Vector2 center) => center * 2f - p;

        /// <summary>Образы точки при симметрии КАРТЫ (дополнительные к p точки
        /// орбиты; режимы — как HeightmapGen.Symmetrize: mirror = отражение по X,
        /// quadrant = ±X/±Z, rotational = точечный поворот 180°, none = пусто).
        /// Пары раскладки обязаны строиться ТЕМИ ЖЕ преобразованиями, что и
        /// рельеф: прежнее точечное Mirror на X-зеркальном рельефе давало
        /// «точка на сопке — пара в долине» («месторождения иногда
        /// несимметричны», плейтест 2026-11-03).</summary>
        public static Vector2[] SymmetryImages(Vector2 p, Vector2 c, string symmetry)
        {
            switch (HeightmapGen.SymmetryOf(symmetry))
            {
                case "mirror":
                    return new[] { new Vector2(2f * c.x - p.x, p.y) };
                case "quadrant":
                    return new[]
                    {
                        new Vector2(2f * c.x - p.x, p.y),
                        new Vector2(p.x, 2f * c.y - p.y),
                        new Vector2(2f * c.x - p.x, 2f * c.y - p.y),
                    };
                case "rotational":
                    return new[] { Mirror(p, c) };
                default:
                    return new Vector2[0];
            }
        }

        /// <summary>Единичный вектор длинной оси карты (куда ставить базы).</summary>
        public static Vector2 LongAxis(Vector2 min, Vector2 max) =>
            (max.x - min.x) >= (max.y - min.y) ? Vector2.right : Vector2.up;

        /// <summary>Симметричные смещения K баз стороны по перпендикуляру к оси:
        /// (i − (K−1)/2)·spread — множество переходит в себя при отражении (зеркальная
        /// симметрия карты сохраняется). Чистая — тестируется.</summary>
        public static float[] BaseOffsets(int count, float spreadM)
        {
            int k = Mathf.Max(1, count);
            var offs = new float[k];
            for (int i = 0; i < k; i++)
                offs[i] = (i - (k - 1) * 0.5f) * Mathf.Max(0f, spreadM);
            return offs;
        }

        /// <summary>
        /// Позиции баз: вдоль длинной оси, зеркально; курс полосы — вдоль оси.
        /// Непригодная точка — дуга кандидатов в секторе ±baseSearchDeg к центру карты;
        /// все кандидаты заняты — пустой список (генератор откажется с Notice).
        /// K = PairsOf(players) баз на сторону: первичная на оси + симметричные
        /// смещения BaseOffsets (siteOk не пустил — сжатие ×0.7 до 3 попыток,
        /// потом слот отбрасывается; минимум — одна база).
        /// </summary>
        public static List<BaseSlot> Bases(Vector2 min, Vector2 max, ScenarioTemplate t,
            System.Random rng, Func<Vector2, bool> siteOk)
        {
            var result = new List<BaseSlot>();
            Vector2 c = Center(min, max);
            Vector2 axis = LongAxis(min, max);
            float half = Mathf.Max(max.x - min.x, max.y - min.y) * 0.5f;
            float r = Mathf.Clamp(t.baseInset, 0.1f, 0.48f) * half;
            float yaw = axis == Vector2.right ? 90f : 0f; // курс в градусах: полоса вдоль оси

            // Кандидаты: осевая точка первая, дальше дуга отклонений в обе стороны.
            for (int step = 0; step <= 8; step++)
            {
                float deg = step * (Mathf.Clamp(t.baseSearchDeg, 5f, 90f) / 8f) *
                            (step % 2 == 1 ? -1f : 1f); // 0, +Δ, −Δ, +2Δ, −2Δ…
                float rad = deg * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(
                    axis.x * Mathf.Cos(rad) - axis.y * Mathf.Sin(rad),
                    axis.x * Mathf.Sin(rad) + axis.y * Mathf.Cos(rad));
                Vector2 a = c + dir * r;
                Vector2 b = Mirror(a, c);
                if (!siteOk(a) || !siteOk(b))
                    continue;

                result.Add(new BaseSlot { Pos = a, YawDeg = yaw }); // первичная (на оси)
                int pairs = ScenarioTemplate.PairsOf(t.players);
                if (pairs > 1)
                {
                    Vector2 perp = new Vector2(-dir.y, dir.x);
                    float spread = Mathf.Max(600f, t.baseSpreadM);
                    for (int shrink = 0; shrink < 3 && result.Count < pairs; shrink++, spread *= 0.7f)
                        foreach (float off in BaseOffsets(pairs, spread))
                        {
                            if (result.Count >= pairs || Mathf.Abs(off) < 1f)
                                continue; // нулевое смещение — это уже добавленная первичная
                            Vector2 p = a + perp * off;
                            if (siteOk(p) && siteOk(Mirror(p, c)))
                                result.Add(new BaseSlot { Pos = p, YawDeg = yaw });
                        }
                    if (result.Count > 1)
                        Log.Info("MapGen: баз на сторону " + result.Count + " из " + pairs +
                                 " (разбег " + t.baseSpreadM.ToString("F0") + " м)");
                }
                return result;
            }
            return result;
        }

        /// <summary>
        /// Месторождения: depositCount зеркальных ПАР (генерим на одной стороне,
        /// отражаем) + centerRichCount жирных (T2) у центра. Спейсинг считается по
        /// ВСЕМ поставленным точкам и по базам (мекс не в футпринте базы).
        /// FALLBACK-петля (регресс «масса 0», плейтест 2026-10-31: доля FlatOk
        /// на террасированном рельефе бывает ~10%, основной проход мог дать 0):
        /// мало точек — ослабляем спейсинг и расширяем кольца; всё ещё 0 —
        /// пригодность по siteLoose (суша/не склон без окна ровности).
        /// </summary>
        public static List<Deposit> Deposits(Vector2 min, Vector2 max, ScenarioTemplate t,
            System.Random rng, Func<Vector2, bool> siteOk, List<BaseSlot> bases,
            Func<Vector2, bool> siteLoose = null, string symmetry = null,
            List<Deposit> prefill = null)
        {
            var result = new List<Deposit>();
            Vector2 c = Center(min, max);
            Vector2 axis = LongAxis(min, max);
            float extent = Mathf.Max(max.x - min.x, max.y - min.y);
            if (prefill != null)
                result.AddRange(prefill); // спавн-мексы: случайные пары их видят (spacing)

            bool FarEnough(Vector2 p, float spacing)
            {
                float sq = spacing * spacing;
                foreach (Deposit d in result)
                    if ((d.Pos - p).sqrMagnitude < sq)
                        return false;
                foreach (BaseSlot b in bases)
                    if ((b.Pos - p).sqrMagnitude < sq)
                        return false;
                return true;
            }

            // Орбита точки: p + образы симметрии карты. Годится, только если
            // пригодны ВСЕ точки орбиты (иначе раскладка теряет симметрию).
            bool OrbitOk(Vector2 p, Func<Vector2, bool> ok, float spacing, List<Deposit> addTo)
            {
                var orbit = new List<Vector2> { p };
                orbit.AddRange(SymmetryImages(p, c, symmetry));
                foreach (Vector2 q in orbit)
                    if (!ok(q) || !FarEnough(q, spacing))
                        return false;
                for (int a = 0; a < orbit.Count; a++)
                    for (int b = a + 1; b < orbit.Count; b++)
                        if ((orbit[a] - orbit[b]).sqrMagnitude < 300f * 300f)
                            return false;
                foreach (Vector2 q in orbit)
                    addTo.Add(new Deposit { Pos = q, Tier = 1 });
                return true;
            }

            // Жирный центр: тир 2, компактное кольцо вокруг центра (центр часто
            // озеро/горы — жирные точки опциональны, фолбэку не подлежат).
            float[] rings = { 0.05f, 0.08f };
            int rich = Mathf.Max(0, t.centerRichCount);
            for (int i = 0; i < rich; i++)
            {
                float ring = extent * rings[i % rings.Length];
                float ang = i * 2.39996f; // золотой угол — без кластеров
                Vector2 p = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ring;
                var orbit = new List<Deposit>();
                if (!OrbitOk(p, siteOk, t.depositSpacing, orbit))
                    continue;
                foreach (Deposit d in orbit)
                    d.Tier = 2;
                result.AddRange(orbit);
            }

            // Пары по орбитам симметрии: кандидаты на СТОРОНЕ положительной полуоси
            // (dot > 0), добавляется вся орбита (образы — теми же осями, что рельеф).
            int want = Mathf.Max(0, t.depositCount);
            int target = result.Count + rich * 2 + want * 2;
            float[] std = { 0.16f, 0.26f, 0.36f, 0.30f, 0.20f };
            float[] wide = { 0.10f, 0.16f, 0.26f, 0.36f, 0.30f, 0.20f, 0.42f };
            int Pass(float spacingScale, float[] radii, Func<Vector2, bool> ok, int attempts)
            {
                int before = result.Count;
                for (int attempt = 0; attempt < attempts && result.Count < target; attempt++)
                {
                    float ring = radii[rng.Next(radii.Length)];
                    float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                    Vector2 p = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ring * extent;
                    if (Vector2.Dot(p - c, axis) <= 0f)
                        continue; // только своя половина
                    OrbitOk(p, ok, t.depositSpacing * spacingScale, result);
                }
                return result.Count - before;
            }

            Pass(1f, std, siteOk, 400);
            if (result.Count < Mathf.Max(2, want / 2))
                Pass(0.5f, wide, siteOk, 800); // террасы съели ровных — жмёмся и шире
            if (result.Count == 0 && siteLoose != null)
                Pass(0.5f, wide, siteLoose, 800); // совсем глухой рельеф — суша без окна
            return result;
        }

        /// <summary>
        /// Спавн-мексы (канон SupCom, юзер-канон 2026-11-03 «месторождения рядом
        /// со спавнами»): кольцо spawnMexCount точек в радиусе spawnMexRadiusM
        /// у КАЖДОЙ базы. Симметрия по образам карты: кольцо строится у одной
        /// базы орбиты, близнецы-образы получают отражённые точки (жителя rng
        /// у близнеца нет — раскладка симметрична по построению). Возврат —
        /// список для prefill в Deposits (случайные пары учтут их в spacing).
        /// Фолбэк siteLoose обязателен: без мексов у спавна старт недееспособен.
        /// </summary>
        public static List<Deposit> SpawnMexes(ScenarioTemplate t, System.Random rng,
            Vector2 min, Vector2 max, List<BaseSlot> bases,
            Func<Vector2, bool> siteOk, Func<Vector2, bool> siteLoose, string symmetry)
        {
            var result = new List<Deposit>();
            if (bases == null || bases.Count == 0 || t.spawnMexCount <= 0)
                return result;
            Vector2 c = Center(min, max);
            var covered = new List<Vector2>();

            bool Place(bool loose)
            {
                Func<Vector2, bool> ok = loose ? siteLoose : siteOk;
                if (ok == null)
                    return false;
                float gapSq = 380f * 380f;
                bool Far(Vector2 q)
                {
                    foreach (Deposit d in result)
                        if ((d.Pos - q).sqrMagnitude < gapSq)
                            return false;
                    return true;
                }
                foreach (BaseSlot b in bases)
                {
                    // Близнец-образ уже обработанной базы — его кольцо придёт образами.
                    foreach (Vector2 a in covered)
                        if ((a - b.Pos).sqrMagnitude <
                            4f * t.spawnMexRadiusM * t.spawnMexRadiusM)
                            goto NextBase;
                    covered.Add(b.Pos);
                    int placed = 0;
                    for (int attempt = 0; attempt < 300 && placed < t.spawnMexCount; attempt++)
                    {
                        float ang = attempt * 2.39996f + (float)rng.NextDouble() * 0.7f;
                        float r = t.spawnMexRadiusM * (0.7f + 0.5f * (float)rng.NextDouble());
                        Vector2 p = b.Pos + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r;
                        var orbit = new List<Vector2> { p };
                        orbit.AddRange(SymmetryImages(p, c, symmetry));
                        bool good = true;
                        foreach (Vector2 q in orbit)
                        {
                            if (!ok(q) || !Far(q) ||
                                (q - b.Pos).sqrMagnitude < 450f * 450f)
                            {
                                good = false;
                                break;
                            }
                        }
                        if (!good)
                            continue;
                        foreach (Vector2 q in orbit)
                            result.Add(new Deposit { Pos = q, Tier = 1 });
                        placed++;
                    }
                    NextBase: ;
                }
                return result.Count > 0;
            }

            if (!Place(false))
            {
                result.Clear();
                covered.Clear();
                Place(true); // глухой рельеф у базы — хотя бы по loose
            }
            return result;
        }

        /// <summary>
        /// Угольные точки (v3): зеркальные ПАРЫ У БАЗ — бутстрап энергии (дешёвый
        /// генератор с бустом строится на угле, как гидро в SupCom). Кольцо вокруг
        /// КАЖДОЙ базовой точки (round-robin), отражение даёт пары у зеркальных баз;
        /// спейсинг — к массе и базам. Радиусы абсолютные: близость угля к базе —
        /// геймплей, а не масштаб карты.
        /// </summary>
        public static List<Deposit> CoalSites(Vector2 min, Vector2 max, ScenarioTemplate t,
            System.Random rng, Func<Vector2, bool> siteOk, List<Deposit> mass, List<BaseSlot> bases,
            Func<Vector2, bool> siteLoose = null, string symmetry = null)
        {
            var result = new List<Deposit>();
            if (bases == null || bases.Count == 0 || t.coalPerSide <= 0)
                return result;
            Vector2 c = Center(min, max);
            float baseRMin = 900f, baseRMax = 1700f;

            int Pass(float coalGap, float massGap, float baseGap, Func<Vector2, bool> ok, int attempts)
            {
                float coalGapSq = coalGap * coalGap, massGapSq = massGap * massGap,
                      baseGapSq = baseGap * baseGap;
                bool Free(Vector2 q)
                {
                    foreach (Deposit d in result)
                        if ((d.Pos - q).sqrMagnitude < coalGapSq)
                            return false;
                    foreach (Deposit d in mass)
                        if ((d.Pos - q).sqrMagnitude < massGapSq)
                            return false;
                    foreach (BaseSlot b in bases)
                        if ((b.Pos - q).sqrMagnitude < baseGapSq)
                            return false;
                    return true;
                }
                int before = result.Count;
                for (int attempt = 0; attempt < attempts && result.Count < t.coalPerSide * 2; attempt++)
                {
                    Vector2 anchor = bases[(attempt / Mathf.Max(1, t.coalPerSide)) % bases.Count].Pos;
                    float ring = baseRMin + (float)rng.NextDouble() * (baseRMax - baseRMin);
                    float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                    Vector2 p = anchor + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ring;
                    // Орбита симметрии карты (не точечное зеркало — см. Deposits).
                    var orbit = new List<Vector2> { p };
                    orbit.AddRange(SymmetryImages(p, c, symmetry));
                    bool good = true;
                    foreach (Vector2 q in orbit)
                        if (!ok(q) || !Free(q))
                        { good = false; break; }
                    if (!good)
                        continue;
                    foreach (Vector2 q in orbit)
                        result.Add(new Deposit { Pos = q, Tier = 1 });
                }
                return result.Count - before;
            }

            // Уголь — бутстрап экономики: fallback ОБЯЗАТЕЛЕН (ноль угля у базы
            // ломает старт), вплоть до siteLoose и сжатых гэпов.
            Pass(1500f, 900f, 700f, siteOk, 300 * bases.Count);
            if (result.Count == 0)
                Pass(800f, 500f, 400f, siteOk, 400 * bases.Count);
            if (result.Count == 0 && siteLoose != null)
                Pass(800f, 500f, 400f, siteLoose, 400 * bases.Count);
            return result;
        }
    }
}
