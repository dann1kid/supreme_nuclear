using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть BuildTasks: постановка площадок: TryPlace/полоса/верфь/Place.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр задач и Task-API — BuildTasks.cs.
    public static partial class BuildTasks
    {
        /// <summary>Поставить площадку и отправить выделенных инженеров (хост).
        /// yawDeg — поворот здания: контроллер наберёт его удержанием ЛКМ.
        /// Площадка назначается ВСЕМ выделенным инженерам (SupCom: пачка едет
        /// вместе и строит серию по очереди; заказ одному/подмножеству — у каждого
        /// своя цепочка, см. §14).</summary>
        public static bool TryPlace(Vector3 groundPoint, List<Unit> selected, FactionHQ hq,
            float yawDeg = 0f)
        {
            if (!IsHost)
            {
                CommanderHud.Notice(Loc.Tr("Стройка пока только на хосте"));
                return false;
            }
            if (Pending == null || hq == null)
                return false;
            var builders = new List<Unit>();
            foreach (Unit unit in selected)
                if (GameApi.IsEngineer(unit))
                    builders.Add(unit);
            if (builders.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Нужен инженер (грузовик) в выделении"));
                return false;
            }
            return Place(Pending, groundPoint, builders, hq, notice: true, yawDeg: yawDeg);
        }

        /// <summary>Поставить площадку ВПП (драг «первый торец → второй»): по готовности
        /// спавнит НОВЫЙ аэродром (свой очередь/грид ангаров). Длина клампится в
        /// [RunwayMinM, RunwayMaxM] вдоль направления драга. Хост.</summary>
        public static bool TryPlaceRunway(Vector3 start, Vector3 end, List<Unit> selected, FactionHQ hq)
        {
            if (!IsHost)
            {
                CommanderHud.Notice(Loc.Tr("Стройка пока только на хосте"));
                return false;
            }
            if (hq == null)
                return false;
            var builders = new List<Unit>();
            foreach (Unit unit in selected)
                if (GameApi.IsEngineer(unit))
                    builders.Add(unit);
            if (builders.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Нужен инженер (грузовик) в выделении"));
                return false;
            }
            Vector2 dir = new Vector2(end.x - start.x, end.z - start.z);
            float len = dir.magnitude;
            if (len < 30f)
            {
                CommanderHud.Notice(Loc.F("ВПП: протяните полосу от торца до торца ({0}–{1} м)",
                    Settings.RunwayMinM.ToString("F0"), Settings.RunwayMaxM.ToString("F0")));
                return false;
            }
            dir /= len;
            len = Mathf.Clamp(len, Settings.RunwayMinM, Settings.RunwayMaxM);
            Vector3 finish = new Vector3(start.x + dir.x * len, 0f, start.z + dir.y * len);
            // ВПП — тоже сухопутная стройка: старт с воды = полоса на дне (как у
            // зданий, см. Place).
            if (start.y < Datum.LocalSeaY - 0.5f)
            {
                CommanderHud.Notice("ВПП: стройка — только с суши");
                return false;
            }
            // Денежного гейта НЕТ (семантика SupCom, юзер 2026-10-27): форму задаём
            // с любым балансом; наливает что есть, скорость = приток денег.

            var task = new Task
            {
                Faction = hq,
                Definition = null,
                IsRunway = true,
                Site = start.ToGlobalPosition(),
                RunwayEnd = finish.ToGlobalPosition(),
                Cost = Settings.RunwayCost,
            };
            task.Builders.AddRange(builders);
            Active.Add(task);
            Version++;
            AssignApproachBearings(task);
            foreach (Unit b in builders)
            {
                if (!Sequences.TryGetValue(b, out List<SeqEntry> seq))
                    Sequences[b] = seq = new List<SeqEntry>();
                AdoptChainTail(b, seq);
                seq.Add(new SeqEntry { Task = task });
            }
            CommanderHud.Notice(Loc.F("Стройка: ВПП {0} м — {1} ({2} инженер.)",
                len.ToString("F0"), Settings.RunwayCost.ToString("F0"), builders.Count));
            Log.Info($"Build: ВПП на ({start.x:F0},{start.z:F0})→({finish.x:F0},{finish.z:F0}), " +
                     $"длина {len:F0}, строителей {builders.Count}");
            return true;
        }

        /// <summary>Поставить площадку ВЕРФИ на воде (SupCom naval yard, v0.92):
        /// точка клика — дно под водой (глубина ≥ DockMinDepthM); по готовности —
        /// меш-платформа на уровне моря + корпус-депо (реестр верфей). Строят
        /// инженеры: морской инженер (катер LC) доходит нативным ховером,
        /// наземный — с кромки воды в радиусе площадки.</summary>
        public static bool TryPlaceDock(Vector3 waterPoint, List<Unit> selected, FactionHQ hq,
            float yawDeg = 0f)
        {
            if (!IsHost)
            {
                CommanderHud.Notice(Loc.Tr("Стройка пока только на хосте"));
                return false;
            }
            BuildingDefinition hull = DockDefinition();
            if (hq == null || hull == null)
            {
                CommanderHud.Notice(Loc.Tr("Верфь: в каталоге нет здания-депо для корпуса"));
                return false;
            }
            var builders = new List<Unit>();
            foreach (Unit unit in selected)
                if (GameApi.IsEngineer(unit))
                    builders.Add(unit);
            if (builders.Count == 0)
            {
                CommanderHud.Notice(Loc.Tr("Нужен инженер (грузовик) в выделении"));
                return false;
            }
            // Точка под водой и достаточно глубоко: клик по воде даёт дно
            // (вода не в StaticsMask); платформа прежней верфи перехватит луч
            // и «водой» не считается — верфь на верфь не поставить.
            if (waterPoint.y > Datum.LocalSeaY - Settings.DockMinDepthM)
            {
                CommanderHud.Notice(Loc.F("Верфь: нужна вода глубже {0} м", Settings.DockMinDepthM.ToString("F0")));
                return false;
            }

            var task = new Task
            {
                Faction = hq,
                Definition = hull, // корпус-депо: призрак/габариты; производит флот через реестр
                IsDock = true,
                Site = new Vector3(waterPoint.x, Datum.LocalSeaY, waterPoint.z).ToGlobalPosition(),
                Cost = Settings.DockCost,
                YawDeg = yawDeg,
            };
            task.Builders.AddRange(builders);
            Active.Add(task);
            Version++;
            AssignApproachBearings(task);
            BuildGhosts.Show(task);
            foreach (Unit b in builders)
            {
                if (!Sequences.TryGetValue(b, out List<SeqEntry> seq))
                    Sequences[b] = seq = new List<SeqEntry>();
                AdoptChainTail(b, seq);
                seq.Add(new SeqEntry { Task = task });
            }
            CommanderHud.Notice(Loc.F("Стройка: Верфь — {0} ({1} инженер.)", Settings.DockCost.ToString("F0"), builders.Count));
            Log.Info($"Build: Верфь на ({waterPoint.x:F0},{waterPoint.z:F0}), глубина " +
                     $"{Datum.LocalSeaY - waterPoint.y:F0} м, строителей {builders.Count}");
            return true;
        }

        /// <summary>Ядро установки площадки (расстановка и шаблоны): задача + маршрут
        /// СВОБОДНЫМ инженерам (фокус-модель); деньги проверяются на месте.
        /// Площадка назначается ВСЕМ переданным инженерам (SupCom-канон, §14:
        /// пачка едет вместе и строит серию по очереди; передал одного/подмножество —
        /// ровно они и строят).</summary>
        public static bool Place(BuildingDefinition def, Vector3 groundPoint,
            List<Unit> builders, FactionHQ hq, bool notice = false, float yawDeg = 0f)
        {
            if (def == null || hq == null || builders == null || builders.Count == 0)
                return false;
            float cost = def.value > 0f ? def.value : Settings.BuildDefaultCost;
            // Денежного гейта НЕТ (семантика SupCom): форма стройки — с любым
            // балансом, наливает сколько есть; темп = приток денег × инженеры.

            // Тир здания ≤ тиру ЛУЧШЕГО инженера (канон SupCom, §2.3-1): T1-грузовик
            // не ставит T2-здания — рост строек идёт инженерами с апнутых фабрик.
            int engTier = 1;
            foreach (Unit b in builders)
            {
                int t = EngineerService.TierOf(b != null ? b.definition : null);
                if (t > engTier)
                    engTier = t;
            }
            int buildTier = UnitRoster.BuildingTierOf(def);
            if (buildTier > engTier)
            {
                if (notice)
                    CommanderHud.Notice(Loc.F("Здание T{0}: нужен инженер T{0} или выше", buildTier));
                return false;
            }

            // Вода — только верфи: клик по воде даёт точку на ДНЕ (вода не ловится
            // лучом), обычное здание спавнилось бы на дне морском — техника из
            // такого депо рождалась «под землёй» (плейтест v0.94).
            if (groundPoint.y < Datum.LocalSeaY - 0.5f)
            {
                if (notice)
                    CommanderHud.Notice("Стройка — только на суше (верфь — на воде)");
                return false;
            }

            // Месторождения v3: экстрактор — ТОЛЬКО снапом на свободную точку массы
            // (SupCom-канон: «видишь точку — инженер строит мекс»); генератор — где
            // угодно, но при свободном угле рядом — снап на него (буст). Спейсинг
            // для снапа НЕ применяется: точка — единственное законное место.
            EconomyService.DepositSite deposit = null;
            bool isExtractor = def == EconomyService.ExtractorDefinition;
            bool isHydro = def == EconomyService.HydroDefinition;
            float snapRadius = EconomyService.Profile != null ? EconomyService.Profile.siteSnapRadiusM : 150f;
            if (isExtractor || isHydro) // Т1-ген (Fuel Depot) строится где угодно
            {
                var kind = isExtractor ? EconomyService.DepositKind.Mass : EconomyService.DepositKind.Coal;
                EconomyService.DepositSite site = EconomyService.FreeSiteNear(groundPoint, kind, snapRadius);
                if (site == null)
                {
                    if (notice)
                        CommanderHud.Notice(isExtractor
                            ? Loc.Tr("Экстрактор — только на месторождение")
                            : Loc.Tr("Гидрокарбонка — только на угольной точке"));
                    return false;
                }
                deposit = site;
                groundPoint = site.Pos.ToLocalPosition();
            }

            // Анти-стены: площадки ближе минимальной дистанции раздвигаются (снап):
            // новая точка отталкивается от существующих, пока не встанет свободно.
            Vector3 placed = deposit != null ? groundPoint : EnforceSpacing(groundPoint);

            // Ангары достраиваются ТОЛЬКО к своему аэродрому в радиусе: без него ангар —
            // сирота вне мультифабрики. Рулёжку от ангара мод дорисовывает сам (по
            // завершении стройки), так что площадка не обязана стоять на карте-
            // бетоне (пустынные аэропорты) — физику грунта игра считает сама.
            if (IsHangarDefinition(def) &&
                NearestOwnAirbase(placed, hq, Settings.HangarAttachRadiusM) == null)
            {
                if (notice)
                    CommanderHud.Notice(Loc.F("Ангар: своего аэродрома нет в радиусе {0} м",
                            Settings.HangarAttachRadiusM.ToString("F0")));
                return false;
            }
            // Хранилище БЧ — та же пристройка к аэродрому (наполнение — WarheadService).
            if (IsStorageDefinition(def) &&
                NearestOwnAirbase(placed, hq, Settings.HangarAttachRadiusM) == null)
            {
                if (notice)
                    CommanderHud.Notice(Loc.F("Хранилище БЧ: своего аэродрома нет в радиусе {0} м",
                            Settings.HangarAttachRadiusM.ToString("F0")));
                return false;
            }

            // Пул-вариант «фабрика T2/T3 напрямую» (юзер-канон 2026-11-04):
            // инженер тира хватает (гейт выше), нужен ещё ЖИВОЙ HQ типа этого
            // тира — зеркало гейта дешёвого апгрейда.
            PoolSpec pspec = BuildingPool.SpecOf(def);
            if (pspec != null && pspec.factory && pspec.tier > 1)
            {
                // Вид фабрики по донору роли (def — клон-определение, не юнит):
                // депо → land, ангары/площадки → air (канон KindOfKey).
                string kind = pspec.donor == "VehicleDepot1" ? "land" : "air";
                if (!FactoryTierService.AnyHq(hq, pspec.tier, kind))
                {
                    if (notice)
                        CommanderHud.Notice(Loc.F("Фабрика T{0} напрямую: нужен живой ШТАБ T{0} этого типа", pspec.tier));
                    return false;
                }
            }

            // Наклон под рельеф — только сухопутное депо: ангар встаёт на плоский
            // апрон (TaxiSurface.Pad), верфь — на платформу у уровня моря (TryPlaceDock).
            float pitch = 0f, roll = 0f;
            if (IsProducingDefinition(def) && !IsHangarDefinition(def))
                TerrainTilt(def, placed, yawDeg, out pitch, out roll);

            var task = new Task
            {
                Faction = hq,
                Definition = def,
                Site = placed.ToGlobalPosition(),
                Deposit = deposit,
                Cost = cost,
                YawDeg = yawDeg,
                PitchDeg = pitch,
                RollDeg = roll,
            };
            task.Builders.AddRange(builders);
            Active.Add(task);
            Version++;
            AssignApproachBearings(task);
            BuildGhosts.Show(task);

            // Площадка дописывается В ОБЩУЮ ОЧЕРЕДЬ каждого инженера (порядок кликов).
            foreach (Unit b in builders)
            {
                if (!Sequences.TryGetValue(b, out List<SeqEntry> seq))
                    Sequences[b] = seq = new List<SeqEntry>();
                AdoptChainTail(b, seq);
                seq.Add(new SeqEntry { Task = task });
            }
            if (notice)
            {
                CommanderHud.Notice(Loc.F("Стройка: {0} — {1} ({2} инженер.)", def.unitName, cost.ToString("F0"), builders.Count));
                Log.Info($"Build: {def.unitName} на ({groundPoint.x:F0},{groundPoint.z:F0}), " +
                         $"цена {cost:F0}, строителей {builders.Count}");
            }
            return true;
        }

        // ---------- ЛИНИЯ ПОСТРОЕК (v0.171, DESIGN §2.17): Ctrl+drag при Pending ----------
        // Стенки/пачка заводов одним жестом: N копий вдоль линии, yaw — вдоль линии,
        // распределение round-robin по выделенным инженерам (у каждого своя очередь).

        /// <summary>Шаг копий линии: диагональ футпринта здания ×1.15 (как в
        /// FootprintHalfM — width/length определения), минимум 20 м.</summary>
        public static float LineSpacing(BuildingDefinition def)
        {
            float d = 30f;
            if (def != null && def.width > 0f && def.length > 0f)
                d = Mathf.Sqrt(def.width * def.width + def.length * def.length) * 1.15f;
            return Mathf.Max(d, 20f);
        }

        /// <summary>Заполнить превью линии точками шага копий (вызывает контроллер
        /// из Update при Ctrl+drag; HUD рисует без аллокаций).</summary>
        public static void FillLinePreview(BuildingDefinition def, GlobalPosition start, GlobalPosition end)
        {
            LinePreview.Clear();
            Vector3 a = start.ToLocalPosition();
            Vector3 b = end.ToLocalPosition();
            float dist = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            float spacing = LineSpacing(def);
            int n = Mathf.Clamp((int)(dist / spacing) + 1, 1, 12);
            for (int k = 0; k < n; k++)
            {
                float t = n == 1 ? 0f : k / (float)(n - 1);
                LinePreview.Add(Vector3.Lerp(a, b, t));
            }
        }

        /// <summary>Линия копий вдоль отрезка: N = длина/шаг (кламп 1..12), yaw —
        /// вдоль линии, площадка k назначается инженеру k mod M (round-robin —
        /// «пачка заводов на несколько инженеров одним приказом»). Неудачные точки
        /// (уклон/вода/чужая зона) пропускаются — TryPlace сам отзовёт false.
        /// Возвращает число поставленных.</summary>
        public static int PlaceLine(GlobalPosition start, GlobalPosition end,
            List<Unit> selected, FactionHQ hq)
        {
            var engs = new List<Unit>();
            foreach (Unit unit in selected)
                if (GameApi.IsEngineer(unit))
                    engs.Add(unit);
            if (engs.Count == 0)
                return 0;
            Vector3 a = start.ToLocalPosition();
            Vector3 b = end.ToLocalPosition();
            Vector2 delta = new Vector2(b.x - a.x, b.z - a.z);
            float spacing = LineSpacing(Pending);
            int n = Mathf.Clamp((int)(delta.magnitude / spacing) + 1, 1, 12);
            float yaw = delta.sqrMagnitude > 4f
                ? Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg
                : PendingYawDeg;
            int placed = 0;
            var one = new List<Unit>();
            for (int k = 0; k < n; k++)
            {
                float t = n == 1 ? 0f : k / (float)(n - 1);
                Vector3 p = Vector3.Lerp(a, b, t);
                one.Clear();
                one.Add(engs[k % engs.Count]);
                if (TryPlace(p, one, hq, yaw))
                    placed++;
            }
            return placed;
        }
    }
}
