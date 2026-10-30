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

        /// <summary>Единичный вектор длинной оси карты (куда ставить базы).</summary>
        public static Vector2 LongAxis(Vector2 min, Vector2 max) =>
            (max.x - min.x) >= (max.y - min.y) ? Vector2.right : Vector2.up;

        /// <summary>
        /// Позиции баз: вдоль длинной оси, зеркально; курс полосы — вдоль оси.
        /// Непригодная точка — дуга кандидатов в секторе ±baseSearchDeg к центру карты;
        /// все кандидаты заняты — пустой список (генератор откажется с Notice).
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

                result.Add(new BaseSlot { Pos = a, YawDeg = yaw });
                for (int side = 2; side <= Mathf.Max(1, t.basesPerSide); side++)
                {
                    // Дуэль v1 — одна база на сторону; вторая и дальше — сдвиг по перпендикуляру.
                    Vector2 perp = new Vector2(-dir.y, dir.x);
                    float off = (side - 1) * 800f * (side % 2 == 0 ? 1f : -1f);
                    Vector2 p = a + perp * off;
                    if (siteOk(p) && siteOk(Mirror(p, c)))
                        result.Add(new BaseSlot { Pos = p, YawDeg = yaw });
                }
                return result;
            }
            return result;
        }

        /// <summary>
        /// Месторождения: depositCount зеркальных ПАР (генерим на одной стороне,
        /// отражаем) + centerRichCount жирных (T2) у центра. Спейсинг считается по
        /// ВСЕМ поставленным точкам и по базам (мекс не в футпринте базы).
        /// </summary>
        public static List<Deposit> Deposits(Vector2 min, Vector2 max, ScenarioTemplate t,
            System.Random rng, Func<Vector2, bool> siteOk, List<BaseSlot> bases)
        {
            var result = new List<Deposit>();
            Vector2 c = Center(min, max);
            Vector2 axis = LongAxis(min, max);
            float extent = Mathf.Max(max.x - min.x, max.y - min.y);
            float spacingSq = t.depositSpacing * t.depositSpacing;

            bool FarEnough(Vector2 p)
            {
                foreach (Deposit d in result)
                    if ((d.Pos - p).sqrMagnitude < spacingSq)
                        return false;
                foreach (BaseSlot b in bases)
                    if ((b.Pos - p).sqrMagnitude < spacingSq)
                        return false;
                return true;
            }

            // Жирный центр: тир 2, компактное кольцо вокруг центра.
            float[] rings = { 0.05f, 0.08f };
            int rich = Mathf.Max(0, t.centerRichCount);
            for (int i = 0; i < rich; i++)
            {
                float ring = extent * rings[i % rings.Length];
                float ang = i * 2.39996f; // золотой угол — без кластеров
                Vector2 p = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ring;
                Vector2 m = Mirror(p, c);
                if (!siteOk(p) || !siteOk(m) || !FarEnough(p) || !FarEnough(m))
                    continue;
                result.Add(new Deposit { Pos = p, Tier = 2 });
                result.Add(new Deposit { Pos = m, Tier = 2 });
            }

            // Зеркальные пары: точки на СТОРОНЕ положительной полуоси (dot > 0).
            int want = Mathf.Max(0, t.depositCount);
            float[] radii = { 0.16f, 0.26f, 0.36f, 0.30f, 0.20f };
            for (int attempt = 0; attempt < 400 && result.Count < rich * 2 + want * 2; attempt++)
            {
                float ring = radii[rng.Next(radii.Length)];
                float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                Vector2 p = c + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ring * extent;
                if (Vector2.Dot(p - c, axis) <= 0f)
                    continue; // только своя половина
                Vector2 m = Mirror(p, c);
                if (!siteOk(p) || !siteOk(m) || !FarEnough(p) || !FarEnough(m))
                    continue;
                result.Add(new Deposit { Pos = p, Tier = 1 });
                result.Add(new Deposit { Pos = m, Tier = 1 });
            }
            return result;
        }

        /// <summary>
        /// Угольные точки (v3): зеркальные ПАРЫ У БАЗ — бутстрап энергии (дешёвый
        /// генератор с бустом строится на угле, как гидро в SupCom). Кольцо вокруг
        /// первой базы, отражение даёт пару у второй; спейсинг — к массе и базе.
        /// Возвращает точки (Tier не имеет смысла — уголь бесстiers).
        /// </summary>
        public static List<Deposit> CoalSites(Vector2 min, Vector2 max, ScenarioTemplate t,
            System.Random rng, Func<Vector2, bool> siteOk, List<Deposit> mass, List<BaseSlot> bases)
        {
            var result = new List<Deposit>();
            if (bases == null || bases.Count == 0 || t.coalPerSide <= 0)
                return result;
            Vector2 c = Center(min, max);
            float coalGapSq = 1500f * 1500f; // уголь не слипается между собой
            float massGapSq = 900f * 900f;   // и не встаёт поверх точки массы
            float baseRMin = 900f, baseRMax = 1700f;

            bool Free(Vector2 q)
            {
                foreach (Deposit d in result)
                    if ((d.Pos - q).sqrMagnitude < coalGapSq)
                        return false;
                foreach (Deposit d in mass)
                    if ((d.Pos - q).sqrMagnitude < massGapSq)
                        return false;
                foreach (BaseSlot b in bases)
                    if ((b.Pos - q).sqrMagnitude < 700f * 700f)
                        return false;
                return true;
            }

            Vector2 anchor = bases[0].Pos;
            int want = t.coalPerSide * 2;
            for (int attempt = 0; attempt < 300 && result.Count < want; attempt++)
            {
                float ring = baseRMin + (float)rng.NextDouble() * (baseRMax - baseRMin);
                float ang = (float)rng.NextDouble() * Mathf.PI * 2f;
                Vector2 p = anchor + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * ring;
                Vector2 m = Mirror(p, c);
                if (!siteOk(p) || !siteOk(m) || !Free(p) || !Free(m))
                    continue;
                result.Add(new Deposit { Pos = p, Tier = 1 });
                result.Add(new Deposit { Pos = m, Tier = 1 });
            }
            return result;
        }
    }
}
