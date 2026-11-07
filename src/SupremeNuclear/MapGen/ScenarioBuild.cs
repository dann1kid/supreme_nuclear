using System;
using System.Collections.Generic;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Objectives;
using NuclearOption.SavedMission.Outcomes;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Сборка сценария-миссии (host/Editor-only): дуэльная раскладка на выбранной
    /// стоковой карте — зеркальные базы со стартовым комплектом, нейтральная
    /// захватываемая мексовка (юниты миссии — работают и без мода), победа =
    /// захват базы противника (нативные objectives). Роли слотов НЕ задаются:
    /// они рождаются из географии (DESIGN §2.2 «без жёстких ролей»).
    /// Вход — хоткей в редакторе миссий (Settings.MapGenKey); результат — файл миссии
    /// в User-группе (меню MissionsPicker / редактор), CurrentMission юзера не трогаем.
    /// </summary>
    public static class ScenarioBuild
    {
        public const string BaseMarker = "SN-BASE";
        public const string DepositMarker = "SN-MES";

        /// <summary>Генерация и сохранение миссии; false + причина — отказ.
        /// Инженерный вход редактора (Ctrl+MapGenKey): после сейва — LoadEditor.</summary>
        public static bool Generate(string templateName, int seed, out string result) =>
            Generate(templateName, seed, out result, loadEditor: true, playersOverride: null, out _);

        /// <summary>Полный вход (Скримиш): loadEditor=false — сейв без редактора
        /// (старт напрямую из меню), playersOverride — игроки из панели (уходят в
        /// ключ карты: мир обязан совпадать при пересборке из ключа).
        /// Синхронный драйвер: те же ступени, что у асинхронного (Prepare → мир →
        /// карта → миссия), только без воркера и бюджета кадров.</summary>
        public static bool Generate(string templateName, int seed, out string result,
            bool loadEditor, int? playersOverride, out string saveName)
        {
            result = null;
            saveName = null;
            var p = new Prep();
            if (!Prepare(templateName, seed, playersOverride, p))
            {
                result = p.Result;
                return false;
            }
            if (p.TerrainMode)
            {
                RunWorldPart(p); // синхронно здесь; асинхронный путь — на воркере
                // Спец-карту строим ПОСЛЕ layout: HQ идут в состав карты
                // scene-объектами (позиции у баз уже известны). Уже построена
                // (кэш Built — повторная генерация того же сида) — не пересобираем.
                if (!RuntimeMap.IsBuilt(p.TerrainKey))
                {
                    var job = new MapBuildJob(p.TerrainKey, HqPositions(p), p.World, p.Pixels);
                    foreach (float _ in job.Steps())
                    {
                    }
                    if (!job.Success)
                    {
                        result = Loc.Tr("Не удалось построить спец-карту — подробности в логе");
                        return false;
                    }
                }
                RuntimeMap.EnsureRegistered(p.TerrainKey); // перерегистрация в лоадере
            }
            else
            {
                var rng = new System.Random(p.Seed);
                p.Bases = ScenarioLayout.Bases(p.Min, p.Max, p.Template, rng, SiteAcceptable);
                if (p.Bases.Count == 0)
                {
                    result = Loc.Tr("Не нашлось ровной площадки под базы — попробуй другую карту");
                    return false;
                }
                p.Deposits = ScenarioLayout.Deposits(p.Min, p.Max, p.Template, rng, SiteAcceptable,
                    p.Bases, SiteAcceptable);
                p.Coal = ScenarioLayout.CoalSites(p.Min, p.Max, p.Template, rng, SiteAcceptable,
                    p.Deposits, p.Bases, SiteAcceptable);
            }
            return MissionFrom(p, loadEditor, out result, out saveName);
        }

        /// <summary>Общая подготовка обоих режимов (главный поток): шаблон, игроки,
        /// сид, ключ карты, фракции; стоковый режим — сразу границы/масштаб.
        /// false + p.Result — отказ (текст для панели/редактора).</summary>
        private static bool Prepare(string templateName, int seed, int? playersOverride, Prep p)
        {
            p.TemplateName = templateName;
            p.Template = ScenarioTemplate.Load(templateName);
            if (playersOverride != null && playersOverride.Value != p.Template.players)
                p.Template.players = playersOverride.Value;
            Mission current = MissionManager.CurrentMission;
            p.TerrainMode = p.Template.terrain != null;
            // Спец-карта самодостаточна (фракции доберём из энциклопедии) — её
            // можно собирать из главного меню, без открытой карты в редакторе;
            // стоковой ветке нужны границы и MapKey текущей карты редактора.
            if (current == null && !p.TerrainMode)
            {
                p.Result = Loc.Tr("Нет загруженной миссии — открой карту в редакторе миссий");
                return false;
            }
            if (seed == 0)
                seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            p.Seed = seed;

            // Адаптивность под игроков (юзер-канон 2026-10-31, уточнён тут же):
            // размер карты — ЯВНЫЙ terrain.sizeM; players масштабирует только
            // раскладку — базы/точки (масштаб применяет ScenarioWorld — детерминизм
            // от «сырого» шаблона в обеих сторонах пайплайна).
            if (p.TerrainMode)
            {
                // Размер и игроки — в ключе карты: смена players/sizeM между
                // генерациями меняет мир — одинаковый ключ с разными мешами давал
                // бы коллизию. Хеш морфологии (style/waterAmount/stepM/…) разводит
                // правки шаблона, «-pN» — переключатель 1×1/2×2 (MapGenKey).
                p.TerrainKey = RuntimeMap.MakeKey(templateName, seed, p.Template.terrain,
                    p.Template.players, (int)(p.Template.terrain.sizeM * 0.001f));
                // Разрешение albedo — SystemInfo (главный поток), число уходит воркеру.
                p.AlbedoRes = MapTextures.AlbedoResOf(p.Template.terrain);
                float half = p.Template.terrain.sizeM * 0.5f;
                p.Min = new Vector2(-half, -half);
                p.Max = new Vector2(half, half);
            }
            else
            {
                if (!GameApi.TryGetMapBounds(out p.Min, out p.Max))
                {
                    p.Result = Loc.Tr("Карта ещё не готова — подожди загрузки");
                    return false;
                }
                // Масштаб точек (стоковая карта — здесь; спец-карта — в ScenarioWorld).
                int pairs = ScenarioTemplate.PairsOf(p.Template.players);
                float extent = Mathf.Max(p.Max.x - p.Min.x, p.Max.y - p.Min.y);
                p.Template.depositSpacing *= Mathf.Max(0.5f, extent / 40000f);
                p.Template.depositCount *= pairs;
                p.Template.coalPerSide *= pairs;
            }
            return ResolveFactions(p, current);
        }

        /// <summary>Фракции: первые две текущей миссии (в меню её нет — сразу
        /// ростр); если редактор дал одну — добираем из ростра.</summary>
        private static bool ResolveFactions(Prep p, Mission current)
        {
            p.Factions = new List<string>();
            if (p.Armies != null)
            {
                // N армий лобби: фракции — рантайм-имена (FactionFactory,
                // детерминизм по id); ростр игры не участвует.
                foreach (LobbyState.Army a in p.Armies)
                    if (a != null && a.controller != LobbyState.CtrlClosed)
                        p.Factions.Add(FactionFactory.ArmyName(a.id));
                return p.Factions.Count >= 1;
            }
            if (current != null)
                foreach (MissionFaction f in current.factions)
                {
                    if (f != null && !string.IsNullOrEmpty(f.factionName) && !p.Factions.Contains(f.factionName))
                        p.Factions.Add(f.factionName);
                    if (p.Factions.Count >= 2)
                        break;
                }
            if (p.Factions.Count < 2 && Encyclopedia.i != null && Encyclopedia.i.factions != null)
                foreach (Faction f in Encyclopedia.i.factions)
                {
                    if (f == null || p.Factions.Contains(f.factionName))
                        continue;
                    p.Factions.Add(f.factionName);
                    if (p.Factions.Count >= 2)
                        break;
                }
            if (p.Factions.Count < 2)
            {
                p.Result = Loc.Tr("Не нашлось двух фракций для дуэли");
                return false;
            }
            return true;
        }

        /// <summary>Мир спец-карты — ЧИСТАЯ математика + пиксели текстур:
        /// ВЫЗЫВАЕТСЯ НА ВОРКЕРЕ (MapGenAsync) или синхронно на главном (Ctrl+G).
        /// ScenarioWorld.Generate детерминирован от (шаблон, сид) и использует
        /// только System.Random/Mathf — движковых вызовов нет.</summary>
        internal static void RunWorldPart(Prep p)
        {
            // ЕДИНЫЙ детерминированный пайплайн (выбор по маскам → ШТАМПЫ ровных
            // площадок → пересчёт масок): та же функция строит мир при загрузке
            // (RuntimeMap.EnsureRegistered) — раскладка и меш не разойдутся.
            p.World = ScenarioWorld.Generate(p.Template, p.Seed);
            p.Bases = p.World.Bases;
            p.Deposits = p.World.Mass;
            p.Coal = p.World.Coal;
            p.Pixels = MapTextures.ComputePixels(p.World.Hm, p.Template.terrain, p.AlbedoRes);
        }

        /// <summary>Позиции штабов сторон (сцена-объекты карты): сторона A — база 0
        /// раскладки, сторона B — её зеркало (тот же расчёт, что у миссии).</summary>
        internal static List<KeyValuePair<string, Vector3>> HqPositions(Prep p)
        {
            // Лобби v3: N армий, каждая на своей позиции пула (индекс →
            // сторона/база раскладки). Легаси (Armies==null): 2 фракции —
            // база 0 + зеркало (как при Ctrl+G).
            var result = new List<KeyValuePair<string, Vector3>>();
            Vector2 c = ScenarioLayout.Center(p.Min, p.Max);
            int perSide = System.Math.Max(1, p.Bases.Count);
            if (p.Armies != null)
            {
                foreach (LobbyState.Army a in p.Armies)
                {
                    if (a == null || a.controller == LobbyState.CtrlClosed)
                        continue;
                    Vector2 pos = BaseOfPool(p, a.pos, perSide, c);
                    result.Add(new KeyValuePair<string, Vector3>(FactionFactory.ArmyName(a.id),
                        new Vector3(pos.x, GroundY(p, pos), pos.y)));
                }
                return result;
            }
            Vector2 mirrorA = ScenarioLayout.Mirror(p.Bases[0].Pos, c);
            result.Add(new KeyValuePair<string, Vector3>(p.Factions[0],
                new Vector3(p.Bases[0].Pos.x, GroundY(p, p.Bases[0].Pos), p.Bases[0].Pos.y)));
            result.Add(new KeyValuePair<string, Vector3>(p.Factions[1],
                new Vector3(mirrorA.x, GroundY(p, mirrorA), mirrorA.y)));
            return result;
        }

        /// <summary>Мировая точка позиции пула (индекс → сторона A/B раскладки,
        /// k-я база стороны; B — зеркало A). Индексы за пулом клампятся.</summary>
        internal static Vector2 BaseOfPool(Prep p, int poolIndex, int perSide, Vector2 c)
        {
            int idx = System.Math.Max(0, System.Math.Min(perSide * 2 - 1, poolIndex));
            int side = perSide > 0 ? idx / perSide : 0;
            int k = perSide > 0 ? idx % perSide : 0;
            Vector2 pos = p.Bases[System.Math.Min(k, p.Bases.Count - 1)].Pos;
            return side == 0 ? pos : ScenarioLayout.Mirror(pos, c);
        }

        /// <summary>Сборка и сохранение миссии-JSON (главный поток, после готовой
        /// карты). Мир/раскладка приходят из Prep — обе стороны пайплайна видят
        /// ОДИН WorldGen (генерация не повторяется).</summary>
        private static bool MissionFrom(Prep p, bool loadEditor, out string result, out string saveName)
        {
            result = null;
            saveName = null;
            ScenarioTemplate template = p.Template;
            int seed = p.Seed;
            string terrainKey = p.TerrainKey;
            bool terrainMode = p.TerrainMode;
            Mission current = MissionManager.CurrentMission;

            var mission = new Mission("SN-Scenario", skipAfterLoad: true);
            mission.MapKey = terrainMode
                ? NuclearOption.SceneLoading.MapKey.GameWorldPrefab(terrainKey)
                : current.MapKey; // та же карта редактора
            mission.missionSettings = new MissionSettings
            {
                description = (terrainMode
                        ? Loc.Tr("Сгенерировано Supreme Nuclear (спец-карта)")
                        : Loc.Tr("Сгенерировано Supreme Nuclear (дуэль)")) + $" [{seed}]",
                playerMode = PlayerMode.SingleAndMultiplayer,
                // ЯО (§2.14-3): нативная эскалация НЕ используется — гейт
                // ядерного оружия = запас БЧ (производство за экономику).
                nuclearEscalationThreshold = 0f,
                strategicEscalationThreshold = 0f,
                minRankTacticalWarhead = 0,
                minRankStrategicWarhead = 0,
            };
            mission.missionSettings.Tags.Add(MissionTag.SinglePlayer);
            mission.missionSettings.Tags.Add(MissionTag.PVP);
            // MP v1 (Скримиш): нативный пикер создания лобби фильтрует по этому
            // тегу (CreateLobbyModal) — сгенерированные миссии хостятся как есть;
            // карта у клиентов строится тем же модом по ключу (детерминизм §12-48б).
            mission.missionSettings.Tags.Add(MissionTag.Multiplayer);
            Vector2 c = ScenarioLayout.Center(p.Min, p.Max);
            var starter = StarterRoster(template);
            int mes = 0;
            int perSide = p.Bases.Count;

            if (p.Armies != null)
            {
                // Лобби v3: N армий. Базы — ВЕСЬ пул позиций (2×perSide): занятая
                // армией позиция — база её фракции со старт-комплектом; свободная —
                // НЕЙТРАЛЬНАЯ захватываемая база (без стартера). Фракции — рантайм
                // имена армий (FactionFactory). Победа — по командам (ниже).
                foreach (LobbyState.Army a in p.Armies)
                    if (a != null && a.controller != LobbyState.CtrlClosed)
                        mission.factions.Add(new MissionFaction(FactionFactory.ArmyName(a.id))
                        {
                            startingBalance = template.startFunds,
                            regularIncome = 0f, // потоком владеет мод (EconomyService)
                        });
                var ownerByPos = new Dictionary<int, LobbyState.Army>();
                foreach (LobbyState.Army a in p.Armies)
                    if (a != null && a.controller != LobbyState.CtrlClosed)
                        ownerByPos[a.pos] = a;
                for (int side = 0; side < 2; side++)
                    for (int k = 0; k < perSide; k++)
                    {
                        int poolIdx = side * perSide + k;
                        Vector2 pos = side == 0 ? p.Bases[k].Pos : ScenarioLayout.Mirror(p.Bases[k].Pos, c);
                        string baseName = $"{BaseMarker}-P{poolIdx + 1}";
                        string owner = ownerByPos.TryGetValue(poolIdx, out LobbyState.Army own)
                            ? FactionFactory.ArmyName(own.id) : null; // null — нейтральная
                        BuildAirbase(mission, baseName, owner, pos, p.Bases[k].YawDeg,
                            template.runwayLength, template.runwayWidth, v => GroundY(p, v), v => SavePos(p, v));
                        if (owner != null)
                            BuildStarter(mission, baseName, owner, pos, p.Bases[k].YawDeg, starter,
                                v => GroundY(p, v), v => SavePos(p, v), template.engineerAirdropAltM);
                    }
                // Победа (захват): каждой армии — objective «захватить ВСЕ базы
                // ЧУЖИХ команд» (союзные базы не цель). endless/annihilate —
                // objectives не строятся (уничтожение везёт VictoryService).
                if (LobbyState.EffectiveVictory(new LobbyState
                {
                    armies = p.Armies,
                    victory = p.Victory,
                }) == LobbyState.VictoryCapture)
                {
                    foreach (LobbyState.Army a in p.Armies)
                    {
                        if (a == null || a.controller == LobbyState.CtrlClosed)
                            continue;
                        var targets = new List<string>();
                        for (int side2 = 0; side2 < 2; side2++)
                            for (int k2 = 0; k2 < perSide; k2++)
                            {
                                int poolIdx = side2 * perSide + k2;
                                if (ownerByPos.TryGetValue(poolIdx, out LobbyState.Army o) && o.team != a.team)
                                    targets.Add($"{BaseMarker}-P{poolIdx + 1}");
                            }
                        if (targets.Count > 0)
                            BuildVictory(mission, FactionFactory.ArmyName(a.id), targets, "P" + (a.id + 1));
                    }
                }
            }
            else
            {
                // Легаси (Ctrl+G): 2 фракции ростра, базы сторон A/B.
                mission.factions.Add(new MissionFaction(p.Factions[0])
                {
                    startingBalance = template.startFunds,
                    regularIncome = 0f, // потоком владеет мод (EconomyService)
                });
                mission.factions.Add(new MissionFaction(p.Factions[1])
                {
                    startingBalance = template.startFunds,
                    regularIncome = 0f,
                });
                var baseNames = new[] { new List<string>(), new List<string>() }; // A- и B-базы
                for (int side = 0; side < 2; side++)
                {
                    string faction = p.Factions[side];
                    for (int k = 0; k < perSide; k++)
                    {
                        Vector2 pos = side == 0 ? p.Bases[k].Pos : ScenarioLayout.Mirror(p.Bases[k].Pos, c);
                        string baseName = $"{BaseMarker}-{(side == 0 ? "A" : "B")}{(perSide > 1 ? (k + 1).ToString() : "")}";
                        baseNames[side].Add(baseName);
                        BuildAirbase(mission, baseName, faction, pos, p.Bases[k].YawDeg,
                            template.runwayLength, template.runwayWidth, v => GroundY(p, v), v => SavePos(p, v));
                        BuildStarter(mission, baseName, faction, pos, p.Bases[k].YawDeg, starter,
                            v => GroundY(p, v), v => SavePos(p, v), template.engineerAirdropAltM);
                    }
                }
                BuildVictory(mission, p.Factions[0], baseNames[1], "A");
                BuildVictory(mission, p.Factions[1], baseNames[0], "B");
            }

            // Месторождения v3: нейтральные МАРКЕРЫ-«недра» (не захватываются —
            // застраиваются): SN-MES — масса (тир в имени), SN-COAL — уголь у баз.
            string mesJsonKey = MissionDepositJsonKey();
            foreach (ScenarioLayout.Deposit d in p.Deposits)
            {
                mes++;
                var sb = new SavedBuilding($"{DepositMarker}-T{d.Tier}-{mes}")
                {
                    type = mesJsonKey,
                    PlacementType = PlacementType.Custom,
                    globalPosition = SavePos(p, new Vector3(d.Pos.x, GroundY(p, d.Pos), d.Pos.y)),
                };
                mission.buildings.Add(sb);
            }
            int coal = 0;
            foreach (ScenarioLayout.Deposit d in p.Coal)
            {
                coal++;
                mission.buildings.Add(new SavedBuilding($"{EconomyService.CoalMarker}-{coal}")
                {
                    type = mesJsonKey,
                    PlacementType = PlacementType.Custom,
                    globalPosition = SavePos(p, new Vector3(d.Pos.x, GroundY(p, d.Pos), d.Pos.y)),
                });
            }

            // Сейв напрямую в User-группу, БЕЗ MissionSaveLoad.SaveMission: тот дёргает
            // событие AfterSave, на которое редактор повесил обновление панели ошибок
            // СВОЕЙ миссии — на чужом сейве панель падала NRE (плейтест 2026-10-29).
            // Полный сид в имени (X8): короткие имена срезали его до 16 бит —
            // разные миры перезаписывали друг друга, а ключ карты расходился с
            // сидом генерации (см. RuntimeMap.MakeKey).
            saveName = $"{(terrainMode ? "SN-Terrain-" + p.TemplateName : "SN-Duel")}-{seed & 0xFFFFFFFF:X8}";
            MissionSaveLoad.ValidateName(ref saveName, allowEmpty: false);
            mission.BeforeSave();
            MissionGroup.UserGroup.SaveMission(saveName, mission);
            if (mission.SaveErrors != null && mission.SaveErrors.ErrorAndExceptionsCount > 0)
                Log.Warn($"MapGen: сохранено с ошибками ({mission.SaveErrors.ErrorAndExceptionsCount}) — см. лог игры");
            Vector2 mirrorA = ScenarioLayout.Mirror(p.Bases[0].Pos, c);
            Log.Info($"MapGen: сценарий сохранён — '{saveName}' на карте " +
                     $"{(terrainKey ?? (current != null ? current.MapKey.Path : "меню"))}, игроков {template.players} (баз/сторону {perSide}), " +
                     $"базы {p.Factions[0]}({p.Bases[0].Pos.x:F0},{p.Bases[0].Pos.y:F0})/" +
                     $"{p.Factions[1]}({mirrorA.x:F0},{mirrorA.y:F0}), bounds " +
                     $"{(terrainMode ? "спец-карта ±" + (template.terrain.sizeM * 0.5f).ToString("F0") : "стоковая")}, " +
                     $"масса {mes}, уголь {coal}, сид {seed}");

            if (loadEditor)
            {
                // Редактор перезагружается с готовой миссией (нативный LoadEditor): юниты
                // спавнятся в сцену — раскладку видно, её можно править и Play.
                _ = MissionEditor.LoadEditor(mission);
                result = Loc.F("Сценарий {0}: раскладка загружается в редактор", saveName);
            }
            else
            {
                // Скримиш: сейв есть, редактор не нужен — старт зовёт SkirmishService
                // (TryLoad из User-группы → SetMission → StartHost, как MissionsPicker).
                result = Loc.F("Сценарий {0} готов", saveName);
            }
            return true;
        }

        /// <summary>Вход асинхронной генерации (MapGenAsync): Prepare + гейт «нужна
        /// спец-карта» — стоковый режим из главного меню невозможен.</summary>
        internal static bool PrepareForAsync(string templateName, int seed, int players,
            Prep p, out string error)
        {
            error = null;
            if (!Prepare(templateName, seed, players, p))
            {
                error = p.Result;
                return false;
            }
            if (!p.TerrainMode)
            {
                error = Loc.Tr("Асинхронная генерация — только пресеты со спец-картой (секция terrain)");
                return false;
            }
            return true;
        }

        /// <summary>Миссия-JSON из готового Prep (MapGenAsync — карта уже построена
        /// ступенями; при повторном заходе EnsureRegistered внутри сработает как кэш).</summary>
        internal static bool MissionFromPrebuilt(Prep p, out string result, out string saveName) =>
            MissionFrom(p, loadEditor: false, out result, out saveName);

        /// <summary>Общий контекст одной генерации (обе стороны пайплайна).</summary>
        internal sealed class Prep
        {
            public string TemplateName; // имя файла-пресета (уходит в saveName/ключ)
            public ScenarioTemplate Template;
            // Лобби v3: армии-слоты (имя/команда/позиция/контроллер). null —
            // легаси-вход Ctrl+G (2 фракции ростра, как раньше).
            public List<LobbyState.Army> Armies;
            public string Victory = LobbyState.VictoryCapture; // действует и на легаси (захват)
            public int Seed;
            public bool TerrainMode;
            public string TerrainKey;   // спец-карта
            public int AlbedoRes;       // отобрано на главном (SystemInfo)
            public WorldGen World;      // мир (воркер/синхронно)
            public MapPixelBundle Pixels; // пиксели текстур (воркер/синхронно)
            public Vector2 Min, Max;
            public List<ScenarioLayout.BaseSlot> Bases;
            public List<ScenarioLayout.Deposit> Deposits;
            public List<ScenarioLayout.Deposit> Coal;
            public List<string> Factions;
            public string Result;       // текст отказа (панель/редактор)
        }

        /// <summary>Высота точки: спец-карта знает высоты из штампованного hm
        /// (совпадает с мешом); стоковая — рейкасты по текущей карте.</summary>
        private static float GroundY(Prep p, Vector2 v) =>
            p.TerrainMode ? p.World.Hm.SampleHeight(v.x, v.y) : GameApi.GroundY(v.x, v.y);

        /// <summary>Сейв хранит GlobalPosition = координату ОТ КАРТЫ (мир −
        /// Datum.origin, см. GlobalPositionExtensions): спец-карта сама строит
        /// карто-локальные точки (меш в координатах root карты), повторный вычет
        /// origin редактора уносил базы на −origin (плейтест 2026-10-29: восток/
        /// центр вместо ±8000). Стоковый режим получает мировые от рейкастов —
        /// конвертация нужна.</summary>
        private static GlobalPosition SavePos(Prep p, Vector3 worldPoint) => p.TerrainMode
            ? new GlobalPosition(worldPoint.x, worldPoint.y, worldPoint.z)
            : worldPoint.ToGlobalPosition();

        /// <summary>Цикл СТИЛЯ карты (land → coast → seas → islands → …) в JSON
        /// текущего шаблона + СРАЗУ генерация (Ctrl+Shift+MapGenKey). Селектор
        /// шаблонов-файлов запутал юзера — «работает только один стиль»
        /// (плейтест 2026-11-02); стиль пишется В ФАЙЛ: обе стороны пайплайна
        /// (генерация и загрузка из ключа) читают один источник, MorphHash в
        /// ключе разводит миры. Чужие поля JSON сохраняются (JObject).</summary>
        public static string CycleStyle(string templateName, out string template)
        {
            template = templateName;
            string[] order = { "land", "coast", "seas", "islands" };
            try
            {
                // Load создаёт образец, если файла ещё нет.
                ScenarioTemplate.Load(templateName);
                string path = System.IO.Path.Combine(
                    BepInEx.Paths.PluginPath, "SupremeNuclear", "MapGen", templateName + ".json");
                var doc = Newtonsoft.Json.Linq.JObject.Parse(
                    System.IO.File.ReadAllText(path));
                var terrain = (Newtonsoft.Json.Linq.JObject)doc["terrain"];
                if (terrain == null)
                    return Loc.Tr("В шаблоне нет секции terrain — стиль переключать нечего");
                string current = HeightmapGen.StyleOf((string)terrain["style"] ?? "land");
                string next = order[(System.Array.IndexOf(order, current) + 1) % order.Length];
                terrain["style"] = next;
                System.IO.File.WriteAllText(path,
                    doc.ToString(Newtonsoft.Json.Formatting.Indented));
                Log.Info("MapGen: стиль карты → " + next + " (шаблон " + templateName + ")");
                template = templateName;
                return Loc.F("Стиль карты: {0} — генерирую", next);
            }
            catch (System.Exception e)
            {
                Log.Error("MapGen: смена стиля не удалась — " + e.Message);
                template = null;
                return Loc.Tr("Не удалось переключить стиль карты — см. лог");
            }
        }

        /// <summary>Редакторский хвост (LoadEditor после сейва) для асинхронного
        /// пути MapGenAsync (_loadEditor).</summary>
        internal static bool MissionFromPrebuiltEditor(Prep p, out string result, out string saveName) =>
            MissionFrom(p, loadEditor: true, out result, out saveName);

        [System.Obsolete("редактор: CycleStyle + MapGenAsync.RequestForEditor; скримиш: MapGenAsync.Request")]
        public static string CycleStyleAndGenerate(string templateName, int seed, out bool ok)
        {
            string[] order = { "land", "coast", "seas", "islands" };
            try
            {
                // Load создаёт образец, если файла ещё нет.
                ScenarioTemplate.Load(templateName);
                string path = System.IO.Path.Combine(
                    BepInEx.Paths.PluginPath, "SupremeNuclear", "MapGen", templateName + ".json");
                var doc = Newtonsoft.Json.Linq.JObject.Parse(
                    System.IO.File.ReadAllText(path));
                var terrain = (Newtonsoft.Json.Linq.JObject)doc["terrain"];
                if (terrain == null)
                {
                    ok = false;
                    return Loc.Tr("В шаблоне нет секции terrain — стиль переключать нечего");
                }
                string current = HeightmapGen.StyleOf((string)terrain["style"] ?? "land");
                string next = order[(System.Array.IndexOf(order, current) + 1) % order.Length];
                terrain["style"] = next;
                System.IO.File.WriteAllText(path,
                    doc.ToString(Newtonsoft.Json.Formatting.Indented));
                Log.Info("MapGen: стиль карты → " + next + " (шаблон " + templateName + ")");
                ok = Generate(templateName, seed, out string gen);
                return Loc.F("Стиль карты: {0} — генерирую", next) + "\n" + gen;
            }
            catch (System.Exception e)
            {
                ok = false;
                Log.Error("MapGen: смена стиля не удалась — " + e.Message);
                return Loc.Tr("Не удалось переключить стиль карты — см. лог");
            }
        }

        /// <summary>Пригодность точки: суша + не склон (пробы ±70 м, как экономика).</summary>
        private static bool SiteAcceptable(Vector2 p)
        {
            if (GameApi.GroundY(p.x, p.y) <= Datum.LocalSeaY + 3f)
                return false;
            float h0 = GameApi.GroundY(p.x, p.y);
            const float probe = 70f;
            return Mathf.Abs(GameApi.GroundY(p.x + probe, p.y) - h0) <= 25f &&
                   Mathf.Abs(GameApi.GroundY(p.x - probe, p.y) - h0) <= 25f &&
                   Mathf.Abs(GameApi.GroundY(p.x, p.y + probe) - h0) <= 25f &&
                   Mathf.Abs(GameApi.GroundY(p.x, p.y - probe) - h0) <= 25f;
        }

        /// <summary>Стартовая база слота: SavedAirbase с реверсивной ВПП, съездами 1/3+2/3
        /// и сервис-точками — геометрия BuildTasks.SpawnRunway, спавн при загрузке нативный.</summary>
        private static void BuildAirbase(Mission mission, string uniqueName, string faction,
            Vector2 center, float yawDeg, float len, float width, Func<Vector2, float> groundY,
            Func<Vector3, GlobalPosition> toSave)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 dir = rot * Vector3.forward;
            Vector3 c3 = new Vector3(center.x, groundY(center), center.y);
            Vector3 a = c3 - dir * (len * 0.5f);
            Vector3 b = c3 + dir * (len * 0.5f);
            Vector3 right = new Vector3(dir.z, 0f, -dir.x);
            const float ExitSideM = 12f;
            var exits = new GlobalPosition[4];
            for (int i = 0; i < 2; i++)
            {
                float f = (i + 1) / 3f;
                Vector3 p = a + dir * (len * f);
                exits[i] = toSave(p + right * ExitSideM);
                exits[i + 2] = toSave(p - right * ExitSideM);
            }

            var airbase = new SavedAirbase
            {
                UniqueName = uniqueName,
                DisplayName = Loc.Tr("База") + " " + faction,
                faction = faction,
                Center = toSave(c3),
                SelectionPosition = toSave(c3),
                CaptureRange = len * 0.5f + Settings.HangarAttachRadiusM,
            };
            airbase.runways.Add(new SavedRunway
            {
                Name = uniqueName + "-RWY",
                Reversable = true,
                Takeoff = true,
                Landing = true,
                Width = width,
                Start = toSave(a),
                End = toSave(b),
                exitPoints = exits,
            });
            // Сервис-точки у торцов в стороне от полотна (посадка не паркуется на полосе).
            float sideM = width * 0.5f + 45f;
            foreach (float f in new[] { 0.12f, 0.88f })
            {
                Vector3 p = a + dir * (len * f);
                Vector3 s1 = p + right * sideM;
                Vector3 s2 = p - right * sideM;
                airbase.ServicePoints.Add(toSave(new Vector3(s1.x, groundY(new Vector2(s1.x, s1.z)), s1.z)));
                airbase.ServicePoints.Add(toSave(new Vector3(s2.x, groundY(new Vector2(s2.x, s2.z)), s2.z)));
            }
            mission.airbases.Add(airbase);
        }

        /// <summary>Стартовый комплект слота. Юзер-канон (2026-10-31, отменяет
        /// «у месторождений» от 2026-10-30): стартовая техника — ОДНИМ аирдропом
        /// на точку спавна у своей ВПП; по месторождениям инженеров разводит игрок
        /// вручную. Нативные парашюты включаются сами: под точкой спавна пусто
        /// в 10 м → CargoDeploymentSystem (мягкая посадка, у наземки нет урона от
        /// падения) — юниты не проваливаются сквозь карту.</summary>
        private static void BuildStarter(Mission mission, string baseName, string faction,
            Vector2 center, float yawDeg, List<ScenarioTemplate.StarterItem> starter,
            Func<Vector2, float> groundY, Func<Vector3, GlobalPosition> toSave, float airdropAltM)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 dir = rot * Vector3.forward;
            Vector3 right = new Vector3(dir.z, 0f, -dir.x);
            Vector3 c3 = new Vector3(center.x, groundY(center), center.y);
            // Линия юнитов сбоку от полосы (за «на полосе»-габаритом), депо — позади торца.
            Vector3 line = c3 + right * 170f;
            Quaternion face = Quaternion.LookRotation(dir, Vector3.up);
            float alt = Mathf.Max(50f, airdropAltM);

            int n = 0;
            foreach (ScenarioTemplate.StarterItem item in starter)
            {
                for (int i = 0; i < Mathf.Max(1, item.count); i++, n++)
                {
                    // StarterRoster уже прописал jsonKey по ролям; пусто и здесь — пропускаем.
                    string jsonKey = !string.IsNullOrEmpty(item.jsonKey)
                        ? item.jsonKey
                        : JsonKeyOf(item.role, starter);
                    if (jsonKey == null)
                        continue;
                    Vector3 p = line + dir * (n * 70f - 70f);
                    p.y = groundY(new Vector2(p.x, p.z));
                    if (item.role == "depot")
                    {
                        p = c3 - dir * 260f + right * 170f;
                        p.y = groundY(new Vector2(p.x, p.z));
                        mission.buildings.Add(new SavedBuilding($"{baseName}-DEPOT")
                        {
                            type = jsonKey,
                            faction = faction,
                            PlacementType = PlacementType.Custom,
                            globalPosition = toSave(p),
                            rotation = face,
                        });
                    }
                    else
                    {
                        // Аирдроп: высота над точкой — парашюты нативно раскроются.
                        p.y += alt;
                        mission.vehicles.Add(new SavedVehicle($"{baseName}-U{n}")
                        {
                            type = jsonKey,
                            faction = faction,
                            PlacementType = PlacementType.Custom,
                            globalPosition = toSave(p),
                            rotation = face,
                            skill = 0.8f,
                        });
                    }
                }
            }
        }

        /// <summary>Победа стороны: захват ВСЕХ баз противника (один objective,
        /// completeOrder=CompleteAll по умолчанию) → EndGame(Victory).</summary>
        private static void BuildVictory(Mission mission, string faction, List<string> targetBases, string tag)
        {
            string outcomeName = $"SN-END-{tag}";
            var objective = (CaptureAirbaseSavedObjective)SavedObjective.CreateSavedObjective(
                ObjectiveType.CaptureAirbase, $"SN-WIN-{tag}");
            objective.Faction = faction;
            objective.DisplayName = Loc.Tr("Захват баз противника");
            foreach (string b in targetBases)
                objective.targetAirbases.Add(b);
            objective.Outcomes.Add(outcomeName);
            mission.objectives.Add(objective);
            mission.outcomes.Add(new EndGameSavedOutcome
            {
                UniqueName = outcomeName,
                endType = EndType.Victory,
                endDelay = 2f,
            });
        }

        /// <summary>jsonKey здания-мины миссии (тот же поиск, что у экономики).</summary>
        private static string MissionDepositJsonKey()
        {
            Encyclopedia encyclopedia = Encyclopedia.i;
            BuildingDefinition def = encyclopedia != null && encyclopedia.buildings != null
                ? EconomyService.FindExtractorDefinition(encyclopedia)
                : null;
            return def != null ? def.jsonKey : "";
        }

        /// <summary>Ростр старта: роль → jsonKey ( depot = корпус верфи/депо, engineer = TRUCK,
        /// tank = MBT→AFV по возрастанию цены). Явный jsonKey в шаблоне приоритетен.</summary>
        private static List<ScenarioTemplate.StarterItem> StarterRoster(ScenarioTemplate template)
        {
            foreach (ScenarioTemplate.StarterItem item in template.starter)
                if (string.IsNullOrEmpty(item.jsonKey))
                    item.jsonKey = JsonKeyOf(item.role, template.starter);
            return template.starter;
        }

        private static string JsonKeyOf(string role, List<ScenarioTemplate.StarterItem> starter)
        {
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia == null || encyclopedia.vehicles == null)
                return null;
            switch (role)
            {
                case "depot":
                {
                    BuildingDefinition hull = BuildTasks.DockDefinition();
                    return hull != null ? hull.jsonKey : null;
                }
                case "engineer":
                    foreach (VehicleDefinition v in encyclopedia.vehicles)
                        if (v != null && v.vehicleType == VehicleType.TRUCK)
                            return v.jsonKey;
                    return null;
                case "tank":
                {
                    VehicleDefinition best = null;
                    foreach (VehicleDefinition v in encyclopedia.vehicles)
                    {
                        if (v == null || v.vehicleType != VehicleType.MBT)
                            continue;
                        if (best == null || v.value < best.value)
                            best = v;
                    }
                    return best != null ? best.jsonKey : null;
                }
                default:
                    return null;
            }
        }
    }
}
