using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace SupremeNuclear
{
    // Часть BuildTasks: хост-тик оплаты и спавн зданий/полос/верфей.
    // Расщепление без изменения логики (2026-11-07): механический перенос
    // членов в partial-файл; реестр задач и Task-API — BuildTasks.cs.
    public static partial class BuildTasks
    {
        /// <summary>Хост-тик: потоковая оплата, спавн по завершении. Зовётся из контроллера.</summary>
        public static void Tick()
        {
            AdvanceSequences(); // очереди живут и без хоста? — только хост платит;
            // аванцер оставляем здесь (вызовы Tick только в миссии)
            BuildGhosts.Sync(); // призраки следуют за площадками и умирают с задачами
            if (!GameApi.InMission || !IsHost)
                return;
            TickFootprintBarrier(); // однополосный барьер площадок (host-физика, троттл 0.1 с)
            TickPatrolAssist(); // до Active.Count-гарда: возврат патрулей и на пустом списке
            TickEscortAssist(); // возврат сопровождений — тоже не зависит от Active.Count
            if (Active.Count == 0)
                return;
            float dt = Time.deltaTime;
            var focus = FocusMap; // Clear — при пересборке ниже (первая задача всегда собирает)
            bool focusBuilt = false;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Task t = Active[i];
                for (int b = t.Builders.Count - 1; b >= 0; b--)
                    if (t.Builders[b] == null || t.Builders[b].disabled)
                    {
                        t.ApproachBearings.Remove(t.Builders[b]);
                        t.Builders.RemoveAt(b);
                    }
                // ПЛОЩАДКА БЕЗ СТРОИТЕЛЕЙ ЖИВЁТ (юзер-канон 2026-11-08, сменил
                // «сирота-снос» 2026-11-04): инженер ушёл по новому приказу —
                // недострой и налитая масса ОСТАЮТСЯ; достройка — ПКМ по призраку
                // (TryResume), явный снос — Ctrl+Shift+ПКМ (WaypointOps, без
                // возврата). Умирает только с гибелью фракции (Faction==null ниже).

                focusBuilt = false; // состав меняется по ходу — пересобираем фокус каждый тик

                // Мёртвая задача = нет фракции; ВПП-задача БЕЗ определения (Definition
                // null) — законна! Раньше выметалась здесь же на первом тике — ВПП
                // «не строилась» во всех плейтестах, молча (корень бага v0.87–v0.90).
                if (t.Faction == null || (!t.IsRunway && !t.IsDock && t.Definition == null))
                {
                    foreach (Unit b in t.Builders)
                        if (b is Ship sea)
                            sea.SetHoldPosition(false); // задача умерла — с якоря
                    Active.RemoveAt(i);
                    Version++;
                    continue;
                }

                // ФОКУС инженера (канон SupCom): каждый инженер вливается ТОЛЬКО в свою
                // первую невыполненную задачу — даже стоя в радиусе соседних площадок.
                // Фокус = первая задача (по порядку очереди), где он числится строителем.
                if (!focusBuilt)
                {
                    focusBuilt = true;
                    focus.Clear();
                    // Фокус = первая активная площадка В ОЧЕРЕДИ инженера (порядок кликов);
                    // без очереди — fallback на первую активную задачу.
                    foreach (KeyValuePair<Unit, List<SeqEntry>> kv in Sequences)
                    {
                        Task ft = FocusTaskOf(kv.Key, kv.Value);
                        if (ft != null)
                            focus[kv.Key] = ft;
                    }
                    foreach (Task ft in Active)
                        foreach (Unit fb in ft.Builders)
                            if (fb != null && !focus.ContainsKey(fb))
                                focus[fb] = ft;
                }

                Vector3 site = t.Site.ToLocalPosition();
                float radiusSq = Settings.BuildSiteRadiusM * Settings.BuildSiteRadiusM;
                int onSite = 0;
                float buildPower = 0f; // сумма факторов строй-мощи (T2 ×2, T3 ×4)
                foreach (Unit b in t.Builders)
                {
                    if (!focus.TryGetValue(b, out Task bFocus) || bFocus != t)
                        continue; // инженер сфокусирован на другой площадке
                    if (PauseService.IsPaused(b))
                        continue; // пауза юнита: числится строителем, но не наливает
                    // SupCom: сначала ОСТАНОВИЛСЯ в радиусе, потом строит — на ходу не капает.
                    // Морской инженер (катер) НЕ умеет стоять сам — «стоп» корабля это
                    // циркуляция: в радиусе площадки ставим на якорь (нативный
                    // SetHoldPosition), порог «остановился» мягче (лёгкий дрейф/качка).
                    float stillGate = b is Ship ? 8f : 3f;
                    if (Mathf.Abs(b.speed) > stillGate)
                        continue;
                    Vector3 p = b.transform.position;
                    if (SiteDistSq(t, p) < radiusSq)
                    {
                        if (b is Ship sea)
                            sea.SetHoldPosition(true);
                        onSite++;
                        buildPower += EngineerService.FactorOf(b.definition);
                    }
                }
                // Зона стройки: пока внутри чужая наземка/флот (кроме строителей;
                // авиация не считается) — стройка не начинается. Пассивных своих выпихиваем.
                // Скан — раз в ZoneScanIntervalSec (кэш на задаче), не каждый кадр.
                Unit blocker = t.ZoneBlockerCache;
                if (Time.unscaledTime >= t.NextZoneScanAt)
                {
                    t.NextZoneScanAt = Time.unscaledTime + ZoneScanIntervalSec;
                    t.ZoneBlockerCache = blocker = FindZoneBlocker(t);
                }
                t.ZoneBlocked = blocker != null;
                if (t.ZoneBlocked && IsIdleFriendly(blocker, t.Faction) &&
                    TryPushOutOfZone(blocker, t))
                    t.ZoneBlocked = false; // выпихнули — следующим тиком можно строить

                // ДИСЦИПЛИНА ПЛОЩАДКИ (v0.152, плейтест «въезжают внутрь, строятся
                // поверх себя»): футпринт занят ЛЮБЫМ мобильным (включая инженеров —
                // их место на кольце) — налив стоит, ждём пока выйдет (барьер
                // FootprintBarrier мягко выдавливает). Маркер «зона не расчищена»
                // показывается по ZoneBusy.
                if (Time.unscaledTime >= t.NextFootprintScanAt)
                {
                    t.NextFootprintScanAt = Time.unscaledTime + ZoneScanIntervalSec;
                    t.FootprintCache = FootprintOccupant(t);
                }

                if (onSite > 0 && !t.ZoneBusy)
                {
                    // Мощь стройки = сумма факторов инженеров на площадке: T1-грузовик 1,
                    // T2 ×2, T3 ×4 (SupCom: старший инженер строит быстрее).
                    float rate = t.Cost / Mathf.Max(1f, Settings.BuildSecondsPerBuilder) * buildPower;
                    float massWant = Mathf.Min(rate * dt, t.Cost - t.PaidMass);
                    // Налив двух валют: дефицит энергии режет массу пропорционально
                    // (EconomyService.Drain → EcoFlow.DrainStep, DESIGN §2.2).
                    EcoFlow.Paid paid = EconomyService.Drain(
                        t.Faction, massWant, massWant * EconomyService.EnergyCostFactor);
                    if (paid.Mass > 0f)
                    {
                        // Первая капля — отметка в лог: плейтест-диагностика «не строится»
                        // (нет записи = инженеры не дошли/не остановились).
                        if (t.PaidMass <= 0f)
                            Log.Info($"Build: оплата начата — {t.Label}" +
                                     $" ({onSite} инженер. на площадке)");
                        t.PaidMass += paid.Mass;
                        t.PaidEnergy += paid.Energy;
                    }
                }
                else if (t.PaidMass <= 0f && Time.unscaledTime -
                         (StallDiagAt.TryGetValue(t, out float diagAt) ? diagAt : -99f) > 5f)
                {
                    // Площадка стоит без оплаты — раз в 5 с снимок причин в лог:
                    // d=дистанция до площадки, v=скорость, f=фокус совпал, зона/на_площадке.
                    StallDiagAt[t] = Time.unscaledTime;
                    var why = new System.Text.StringBuilder();
                    foreach (Unit b in t.Builders)
                    {
                        if (b == null)
                            continue;
                        Vector3 bp = b.transform.position;
                        focus.TryGetValue(b, out Task bf);
                        why.Append("d=").Append(Mathf.Sqrt(SiteDistSq(t, bp)).ToString("F0"))
                           .Append(" v=").Append(Mathf.Abs(b.speed).ToString("F1"))
                           .Append(" f=").Append(bf == t ? "+" : "-").Append("; ");
                    }
                    Log.Info($"Build: ожидание — {t.Label}" +
                             $" зона={t.ZoneBlocked} на_площадке={onSite} " +
                             $"деньги={t.Faction.factionFunds:F0} | {why}");
                }

                if (t.PaidMass >= t.Cost - 0.01f && t.PaidEnergy >= t.EnergyCost - 0.01f)
                {
                    // Очередь недостроя-фабрики переезжает на живую фабрику
                    // (null — спавн не удался: ProductionTasks чистит своё сам).
                    // ВПП — не фабрика: очереди у неё не бывает.
                    if (t.IsRunway)
                        SpawnRunway(t);
                    else if (t.IsDock)
                        ProductionTasks.AdoptSite(t, SpawnDock(t));
                    else
                        ProductionTasks.AdoptSite(t, SpawnBuilding(t));
                    PushBuildersOut(t);
                    BuildGhosts.Hide(t);
                    StallDiagAt.Remove(t);
                    Active.RemoveAt(i); // аванцер снимет протухшую голову очереди сам
                    Version++;
                }
            }
        }

        /// <summary>Стоящие в футпринте строители выталкиваются за радиус площадки —
        /// здание спавнится не на них (SupCom: строители стоят кольцом).</summary>
        /// <summary>Раздвиг площадок: если новая ближе BuildSiteSpacingM к существующей,
        /// отталкивается наружу (до 6 итераций; без места — как есть).</summary>
        private static Vector3 EnforceSpacing(Vector3 point)
        {
            float min = Settings.BuildSiteSpacingM;
            if (min <= 0f || Active.Count == 0 && UnitRegistry.allUnits.Count == 0)
                return point;
            for (int iter = 0; iter < 6; iter++)
            {
                Vector3 push = Vector3.zero;
                bool conflict = false;
                foreach (Task t in Active)
                {
                    Vector3 s = t.Site.ToLocalPosition();
                    Vector2 d = new Vector2(point.x - s.x, point.z - s.z);
                    float dist = d.magnitude;
                    if (dist >= min)
                        continue;
                    conflict = true;
                    Vector2 dir = dist < 1f ? Vector2.up : d / dist;
                    push += new Vector3(dir.x, 0f, dir.y) * (min - dist);
                }
                // ГОТОВЫЕ здания тоже расталкивают (юзер-плейтест 2026-11-04
                // «AI настроил стек зданий»): задача исчезает из Active при
                // достройке, быстрый AI ставил следующую площадку в ту же точку
                // (GroundY давал КРЫШУ готового — здания росли стеком вверх).
                foreach (Unit u in UnitRegistry.allUnits)
                {
                    if (!(u is Building) || u == null || u.disabled ||
                        EconomyService.IsSiteMarker(u))
                        continue; // маркеры-«недра» живут на точках — их не трогаем
                    Vector3 p = u.transform.position;
                    Vector2 d = new Vector2(point.x - p.x, point.z - p.z);
                    float dist = d.magnitude;
                    if (dist >= min)
                        continue;
                    conflict = true;
                    Vector2 dir = dist < 1f ? Vector2.up : d / dist;
                    push += new Vector3(dir.x, 0f, dir.y) * (min - dist);
                }
                if (!conflict)
                    break;
                point += push;
                point.y = GameApi.GroundY(point.x, point.z);
            }
            return point;
        }

        private static void PushBuildersOut(Task t)
        {
            Vector3 site = t.Site.ToLocalPosition();
            // Внутренний радиус = полу-диагональ футпринта здания (длина/ширина
            // определения) + запас: фиксированных 30 м крупным зданиям не хватало,
            // техника оставалась внутри стен при спавне.
            float half = 30f;
            if (t.Definition != null)
                half = Mathf.Max(30f, 0.5f * Mathf.Sqrt(
                    t.Definition.length * t.Definition.length +
                    t.Definition.width * t.Definition.width) + 6f);
            float inner = half;
            foreach (Unit b in t.Builders)
            {
                if (b == null || b.disabled)
                    continue;
                if (b is Ship sea)
                    sea.SetHoldPosition(false); // с якоря — строителей отводит приказ
                Vector3 p = b.transform.position;
                Vector2 dir = new Vector2(p.x - site.x, p.z - site.z);
                if (dir.sqrMagnitude >= inner * inner)
                    continue;
                dir = dir.sqrMagnitude < 1f ? Vector2.up : dir.normalized;
                float r = Settings.BuildSiteRadiusM + 40f;
                GroundOrders.Set(b, new Vector3(site.x + dir.x * r, 0f, site.z + dir.y * r)
                    .ToGlobalPosition());
            }
        }

        private static object SpawnBuilding(Task t)
        {
            Spawner spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null)
            {
                Log.Error("Build: Spawner недоступен — здание не создано, деньги возвращены");
                t.Faction.AddFunds(t.PaidMass);
                EconomyService.Refund(t.Faction, t.PaidEnergy);
                return null;
            }
            Vector3 local = t.Site.ToLocalPosition();
            // Наклон (Pitch/Roll ≠ 0 — только у депо): здание снапится к рельефу,
            // как стояло бы «по склону»; ангар плоский на своём апроне.
            Quaternion rot = Quaternion.Euler(t.PitchDeg, t.YawDeg, t.RollDeg);
            // Ангар — нативная привязка к аэродрому: SpawnBuilding с airbase делает
            // SetAirbase (сетевой синк клиентам) + AddHangar — ангар становится слотом
            // мультифабрики (ProductionTasks.PaySlots увидит его без нашей помощи).
            if (IsHangarDefinition(t.Definition))
            {
                Airbase airbase = NearestOwnAirbase(local, t.Faction, Settings.HangarAttachRadiusM);
                if (airbase == null)
                {
                    Log.Warn("Build: аэродром потерян за время стройки ангара — возврат денег");
                    t.Faction.AddFunds(t.PaidMass);
                    EconomyService.Refund(t.Faction, t.PaidEnergy);
                    return null;
                }
                // Апрон строим ПЕРВЫМ и ПЛОСКИМ (МАКС рельефа площадки + юбки —
                // v0.91.3, min зарывал верхний край плиты в бугор): здание ставится
                // на выровненное полотно, борт — на бетон с первого колеса.
                float padY = TaxiSurface.Pad(local, t.YawDeg,
                    t.Definition.width + 12f, t.Definition.length + 12f);
                Vector3 pos = new Vector3(local.x, padY, local.z) +
                              (Vector3)t.Definition.spawnOffset;
                Building hangar = spawner.SpawnBuilding(
                    t.Definition.unitPrefab, pos.ToGlobalPosition(), rot,
                    t.Faction, airbase, t.Definition.unitName, false, null);
                if (hangar == null)
                {
                    Log.Error("Build: ангар не создан — возврат денег");
                    t.Faction.AddFunds(t.PaidMass);
                    EconomyService.Refund(t.Faction, t.PaidEnergy);
                    return null;
                }
                // Вертолётная площадка — БЕЗ рулёжки (VTOL, юзер-канон
                // 2026-11-03): флаг РОЛИ ПУЛА (v0.148), плита-площадка остаётся.
                bool heliport = IsHeliportDef(t.Definition);
                Log.Info("Build: ангар " + t.Definition.unitName + " — " +
                         (heliport ? "ВЕРТОЛЁТНАЯ площадка, без рулёжки" : "рулёжка строится"));
                if (!heliport)
                    ConnectTaxiway(airbase, hangar, padY);
                FactoryTierService.SpawnedAs(hangar.GetComponent<Hangar>(),
                    BuildingPool.SpecOf(t.Definition)?.tier ?? 1);
                CommanderHud.Notice(Loc.F("Построено: {0} → {1}", t.Definition.unitName, airbase.name));
                Log.Info($"Build: ангар {t.Definition.unitName} пристыкован к {airbase.name} " +
                         $"на ({local.x:F0},{local.z:F0}), поворот {t.YawDeg:F0}°, " +
                         $"полотно y={padY:F1}");
                return hangar.GetComponent<Hangar>(); // фабрика для AdoptSite
            }
            // Хранилище БЧ — нативная привязка к аэродрому тем же вызовом:
            // SpawnBuilding(..., airbase, ...) делает SetAirbase + AddBuilding,
            // а AddBuilding детектит WarheadStorage и регистрирует склад нативно.
            if (IsStorageDefinition(t.Definition))
            {
                Airbase airbase = NearestOwnAirbase(local, t.Faction, Settings.HangarAttachRadiusM);
                if (airbase != null)
                {
                    Vector3 posS = new Vector3(local.x, GameApi.GroundY(local.x, local.z), local.z) +
                                   (Vector3)t.Definition.spawnOffset;
                    Building storage = spawner.SpawnBuilding(
                        t.Definition.unitPrefab, posS.ToGlobalPosition(), rot,
                        t.Faction, airbase, t.Definition.unitName, false, null);
                    if (storage != null)
                    {
                        CommanderHud.Notice(Loc.F("Построено: {0} → {1}",
                            t.Definition.unitName, airbase.name));
                        Log.Info("Build: хранилище БЧ пристыковано к " + airbase.name);
                        return null; // не фабрика: очереди AdoptSite не нужно
                    }
                    Log.Error("Build: хранилище БЧ не создано — возврат денег");
                    t.Faction.AddFunds(t.PaidMass);
                    EconomyService.Refund(t.Faction, t.PaidEnergy);
                    return null;
                }
                Log.Warn("Build: аэродром потерян за время стройки хранилища — возврат");
                t.Faction.AddFunds(t.PaidMass);
                EconomyService.Refund(t.Faction, t.PaidEnergy);
                return null;
            }
            Vector3 pos2 = new Vector3(local.x, GameApi.GroundY(local.x, local.z), local.z) +
                           (Vector3)t.Definition.spawnOffset;
            // Фракция обязательна: здание с null-HQ выходит нейтральным — не выделяется,
            // и техника из такого депо спавнится тоже нейтральной (TrySpawnVehicle
            // наследует NetworkHQ депо).
            Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                t.Definition, pos2.ToGlobalPosition(), rot, t.Faction, t.Definition.unitName);
            if (unit == null)
            {
                Log.Error("Build: здание не создано — возврат денег");
                t.Faction.AddFunds(t.PaidMass);
                EconomyService.Refund(t.Faction, t.PaidEnergy);
                return null;
            }
            // Нативная Factory (Large/Vertical): без FactoryOptions здание мертво —
            // конфигурируем под дешёвый MBT (только здания НЕ из пула: поведение
            // пула задают роли, донорские Factory не оживляем, v0.148);
            // ProduceUnit кладёт юнита в саплай
            // фракции, DeployUnits сама выкатывает через депо (медленный интервал
            // — нативный тик бесплатен, деньги берёт постройка; экономика-интеграция — §12).
            Factory factory = unit.GetComponent<Factory>();
            if (factory != null && !BuildingPool.IsPool(t.Definition))
            {
                VehicleDefinition tank = AiCommanderService.TankDefinition();
                if (tank != null)
                {
                    factory.SetFactory(tank.jsonKey, Settings.ProductionSecondsPerItem * 10f);
                    Log.Info("Build: нативная фабрика → " + tank.jsonKey);
                }
            }
            // Построенная энергопостройка попадает в энергосистему (имя-маркер
            // «Generator N»): гидрокарбонка — с угольным бустом, Т1-ген — базовая.
            if (t.Definition == EconomyService.HydroDefinition ||
                t.Definition == EconomyService.GeneratorDefinition)
                EconomyService.RegisterGenerator(unit, t.Definition == EconomyService.HydroDefinition);
            // SC-глубина экономики (v0.175, DESIGN §2.17): конвертер и хранилище
            // массы — в реестры потока/буста по роли пула.
            var poolSpec = BuildingPool.SpecOf(t.Definition);
            if (poolSpec != null && poolSpec.role == "fab")
                EconomyService.RegisterConverter(unit);
            else if (poolSpec != null && poolSpec.role == "ms")
                EconomyService.RegisterMassStore(unit);
            // Экстрактор — в реестр дохода с тиrom ТОЧКИ; точка занимается
            // (маркер-«недра» скрывается у всех).
            if (t.Definition == EconomyService.ExtractorDefinition)
            {
                EconomyService.RegisterExtractor(unit, t.Deposit?.Tier ?? 1);
                if (t.Deposit != null)
                    EconomyService.TakeSite(t.Deposit);
            }
            else if (t.CoalDeposit != null)
                EconomyService.TakeSite(t.CoalDeposit);
            FactoryTierService.SpawnedAs(unit, poolSpec?.tier ?? 1);
            CommanderHud.Notice(Loc.F("Построено: {0}", t.Definition.unitName));
            Log.Info($"Build: готово {t.Definition.unitName} на ({local.x:F0},{local.z:F0}) за {t.PaidMass:F0}");
            return unit;
        }

        /// <summary>ВПП готова: нативный SpawnCustomAirbase — новый аэродром со своей
        /// очередью/гридом ангаров (паттерн «несколько фабрик» SupCom). Полоса
        /// реверсивная взлёт/посадка; аэродром ЗАХВАТЫВАЕМЫЙ (стратегический объект).
        /// Под полосу кладётся поверхность (тяжёлым катиться по грунту нельзя).</summary>
        private static void SpawnRunway(Task t)
        {
            Mission mission = MissionManager.CurrentMission;
            var net = NetworkManagerNuclearOption.i;
            if (mission == null || net == null || net.Server == null || !net.Server.Active)
            {
                Log.Error("Build: ВПП — миссия/сервер не готовы, возврат денег");
                t.Faction.AddFunds(t.PaidMass);
                EconomyService.Refund(t.Faction, t.PaidEnergy);
                return;
            }
            Vector3 a = t.Site.ToLocalPosition();
            Vector3 b = t.RunwayEnd.ToLocalPosition();
            float len = new Vector2(b.x - a.x, b.z - a.z).magnitude;
            Vector3 dir = len > 0.01f
                ? new Vector3(b.x - a.x, 0f, b.z - a.z).normalized
                : Vector3.forward;

            var saved = new SavedAirbase
            {
                SavedInMission = true,
                UniqueName = "SN-RWY-" + t.Id.Substring(0, 6),
                DisplayName = Loc.Tr("ВПП"),
                faction = t.Faction.faction != null ? t.Faction.faction.factionName : "",
            };
            saved.Center = new Vector3((a.x + b.x) * 0.5f, 0f, (a.z + b.z) * 0.5f).ToGlobalPosition();
            saved.SelectionPosition = saved.Center;
            // Радиус аэродрома = его CaptureRange — И критерий игры «AnyNearAirbase»
            // (руление/RTB ищут аэродром чей радиус накрывает борт). Дефолт 1000 м:
            // у длинной ВПП ангары на торце ВНЕ радиуса — борта уезжали рулить к
            // соседнему нативному аэропорту. Полдлины + дальность привязки ангаров
            // = вся территория аэродрома (и честный радиус захвата).
            saved.CaptureRange = len * 0.5f + Settings.HangarAttachRadiusM;
            // Съезды с полосы: садящийся борт держит runway.landingList (а с ним
            // IsAvailableForTakeoff = false для ВСЕХ взлетающих), пока не доедет
            // до exit-точки. Без съездов FromSaved подставляет торцы — борт катится
            // всю полосу до дальнего конца, запирая вылеты на пару минут с посадки.
            // Точки на 1/3 и 2/3 в ОБОИХ направлениях (TryGetExitTaxiPoint матчит
            // по dot(exitPoint.forward, курс борта), а FromSaved создаёт трансформы
            // без поворота — доворачиваем после спавна), со сдвигом 12 м от осевой
            // в правую по курсу сторону: после снятия с полосы борт встаёт у съезда
            // (до парковки/сервиса) — со сдвигом он не лежит на линии разбега
            // следующих; направления разведены по разным сторонам полосы.
            Vector3 right = new Vector3(dir.z, 0f, -dir.x);
            const float ExitSideM = 12f;
            GlobalPosition[] exits = new GlobalPosition[4];
            for (int i = 0; i < 2; i++)
            {
                float f = (i + 1) / 3f;
                Vector3 p = a + dir * (len * f);
                p.y = Mathf.Lerp(a.y, b.y, f);
                exits[i] = (p + right * ExitSideM).ToGlobalPosition();     // курс a→b
                exits[i + 2] = (p - right * ExitSideM).ToGlobalPosition(); // курс b→a
            }
            saved.runways.Add(new SavedRunway
            {
                Name = "SN-" + t.Id.Substring(0, 4),
                Reversable = true,
                Takeoff = true,
                Landing = true,
                Width = Settings.RunwayWidthM,
                Start = a.ToGlobalPosition(),
                End = b.ToGlobalPosition(),
                exitPoints = exits,
            });
            // Сервисные точки (парковка после посадки): без них ветка «к точке
            // обслуживания» в SearchForAirbase даёт disembark ПРЯМО НА ПОЛОСЕ —
            // севшие борта копятся на полотне и физически запирают выкат. Четыре
            // точки у торцов, в стороне от полотна (за пределами «на полосе»
            // нативного AircraftIsOnRunway ≈ 1.5 ширины).
            float sideM = Settings.RunwayWidthM * 0.5f + 45f;
            foreach (float f in new[] { 0.12f, 0.88f })
            {
                Vector3 p = a + dir * (len * f);
                Vector3 s1 = p + right * sideM;
                Vector3 s2 = p - right * sideM;
                saved.ServicePoints.Add(new Vector3(s1.x, GameApi.GroundY(s1.x, s1.z), s1.z).ToGlobalPosition());
                saved.ServicePoints.Add(new Vector3(s2.x, GameApi.GroundY(s2.x, s2.z), s2.z).ToGlobalPosition());
            }
            mission.airbases.Add(saved);
            Airbase airbase = mission.SpawnCustomAirbase(saved, net.ServerObjectManager);
            if (airbase == null)
            {
                Log.Error("Build: ВПП — аэродром не создан, возврат денег");
                mission.airbases.Remove(saved);
                t.Faction.AddFunds(t.PaidMass);
                EconomyService.Refund(t.Faction, t.PaidEnergy);
                return;
            }
            // Доворот съездов: FromSaved создаёт exit-трансформы с единичной
            // ориентацией (положение есть, поворота нет) — TryGetExitTaxiPoint
            // матчит их по forward, без доворота «съезд не найден» почти всегда.
            // Порядок соответствует exits выше: [0],[1] — курс a→b, [2],[3] — b→a.
            if (airbase.runways != null && airbase.runways.Length > 0 &&
                airbase.runways[0].exitPoints != null &&
                airbase.runways[0].exitPoints.Length == 4)
            {
                Quaternion fwd = Quaternion.LookRotation(dir, Vector3.up);
                Quaternion back = Quaternion.LookRotation(-dir, Vector3.up);
                airbase.runways[0].exitPoints[0].rotation = fwd;
                airbase.runways[0].exitPoints[1].rotation = fwd;
                airbase.runways[0].exitPoints[2].rotation = back;
                airbase.runways[0].exitPoints[3].rotation = back;
            }
            // Поверхность полосы: непрозрачный меш-«тармак», ВЫРОВНЕННЫЙ терассой
            // (плоское сечение, лимит уклона, юбки-насыпи) — разбег по ровному.
            TaxiSurface.Register(saved.runways[0].Start, saved.runways[0].End,
                Settings.RunwayWidthM * 0.9f, graded: true);
            CommanderHud.Notice(Loc.F("Построено: ВПП {0} м — новый аэродром", len.ToString("F0")));
            Log.Info($"Build: ВПП-аэродром {saved.UniqueName} на " +
                     $"({saved.Center.ToLocalPosition().x:F0},{saved.Center.ToLocalPosition().z:F0}), " +
                     $"длина {len:F0} м, съезды 1/3+2/3 (оба направления), 4 сервис-точки");
        }

        /// <summary>Верфь готова: меш-платформа на уровне моря (физика+визуал
        /// TaxiSurface) и корпус-депо сверху (SpawnFromUnitDefinitionInEditor,
        /// некапчуруемый, ренейм «Верфь»). Корпус попадает в реестр верфей —
        /// панели показывают ФЛОТ, ProductionTasks спускает корабли (NavalDock).</summary>
        private static object SpawnDock(Task t)
        {
            Spawner spawner = NetworkSceneSingleton<Spawner>.i;
            BuildingDefinition hull = t.Definition;
            if (spawner == null || hull == null)
            {
                Log.Error("Build: Верфь — Spawner/корпус недоступны, возврат денег");
                t.Faction.AddFunds(t.PaidMass);
                EconomyService.Refund(t.Faction, t.PaidEnergy);
                return null;
            }
            Vector3 local = t.Site.ToLocalPosition();
            Quaternion rot = Quaternion.Euler(0f, t.YawDeg, 0f);
            float sizeX = hull.width + Settings.DockPlatformMarginM;
            float sizeZ = hull.length + Settings.DockPlatformMarginM;
            float deckY = TaxiSurface.WaterPad(local, t.YawDeg, sizeX, sizeZ);
            Vector3 pos = new Vector3(local.x, deckY, local.z) + (Vector3)hull.spawnOffset;
            Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                hull, pos.ToGlobalPosition(), rot, t.Faction, Loc.Tr("Верфь") + " SN-" + t.Id.Substring(0, 6));
            if (unit == null)
            {
                Log.Error("Build: Верфь — корпус не создан, возврат денег");
                t.Faction.AddFunds(t.PaidMass);
                EconomyService.Refund(t.Faction, t.PaidEnergy);
                return null;
            }
            unit.NetworkunitName = Loc.Tr("Верфь");
            Docks.Add(unit);
            CommanderHud.Notice(Loc.Tr("Построено: Верфь — флот в очередь"));
            Log.Info($"Build: Верфь на ({local.x:F0},{local.z:F0}), платформа " +
                     $"{sizeX:F0}×{sizeZ:F0}, палуба y={deckY:F1}");
            return unit;
        }
    }
}
