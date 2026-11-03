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
        /// ключ карты: мир обязан совпадать при пересборке из ключа).</summary>
        public static bool Generate(string templateName, int seed, out string result,
            bool loadEditor, int? playersOverride, out string saveName)
        {
            result = null;
            saveName = null;
            ScenarioTemplate template = ScenarioTemplate.Load(templateName);
            if (playersOverride != null && playersOverride.Value != template.players)
                template.players = playersOverride.Value;
            Mission current = MissionManager.CurrentMission;
            bool terrainMode = template.terrain != null;
            // Спец-карта самодостаточна (фракции доберём из энциклопедии) — её
            // можно собирать из главного меню, без открытой карты в редакторе;
            // стоковой ветке нужны границы и MapKey текущей карты редактора.
            if (current == null && !terrainMode)
            {
                result = Loc.Tr("Нет загруженной миссии — открой карту в редакторе миссий");
                return false;
            }
            if (seed == 0)
                seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            var rng = new System.Random(seed);

            // Адаптивность под игроков (юзер-канон 2026-10-31, уточнён тут же):
            // размер карты — ЯВНЫЙ terrain.sizeM; players масштабирует только
            // раскладку — базы/точки (масштаб применяет ScenarioWorld — детерминизм
            // от «сырого» шаблона в обеих сторонах пайплайна).
            Heightmap hm = null;
            string terrainKey = null;
            Vector2 min, max;
            List<ScenarioLayout.BaseSlot> bases;
            List<ScenarioLayout.Deposit> deposits;
            List<ScenarioLayout.Deposit> coalSites;
            if (terrainMode)
            {
                // Размер и игроки — в ключе карты: смена players/sizeM между
                // генерациями меняет мир — одинаковый ключ с разными мешами давал
                // бы коллизию. Хеш морфологии (style/waterAmount/stepM/…) разводит
                // правки шаблона, «-pN» — переключатель 1×1/2×2 (MapGenKey).
                terrainKey = RuntimeMap.MakeKey(templateName, seed, template.terrain,
                    template.players, (int)(template.terrain.sizeM * 0.001f));
                // ЕДИНЫЙ детерминированный пайплайн (выбор по маскам → ШТАМПЫ ровных
                // площадок → пересчёт масок): та же функция строит мир при загрузке
                // (RuntimeMap.EnsureRegistered) — раскладка и меш не разойдутся.
                WorldGen world = ScenarioWorld.Generate(template, seed);
                hm = world.Hm;
                bases = world.Bases;
                deposits = world.Mass;
                coalSites = world.Coal;
                float half = template.terrain.sizeM * 0.5f;
                min = new Vector2(-half, -half);
                max = new Vector2(half, half);
            }
            else
            {
                if (!GameApi.TryGetMapBounds(out min, out max))
                {
                    result = Loc.Tr("Карта ещё не готова — подожди загрузки");
                    return false;
                }
                // Масштаб точек (стоковая карта — здесь; спец-карта — в ScenarioWorld).
                int pairs = ScenarioTemplate.PairsOf(template.players);
                float extent = Mathf.Max(max.x - min.x, max.y - min.y);
                template.depositSpacing *= Mathf.Max(0.5f, extent / 40000f);
                template.depositCount *= pairs;
                template.coalPerSide *= pairs;
                bases = null;
                deposits = null;
                coalSites = null;
            }

            // Фракции: первые две текущей миссии (в меню её нет — сразу ростр);
            // если редактор дал одну — добираем из ростра.
            List<string> factions = new List<string>();
            if (current != null)
                foreach (MissionFaction f in current.factions)
                {
                    if (f != null && !string.IsNullOrEmpty(f.factionName) && !factions.Contains(f.factionName))
                        factions.Add(f.factionName);
                    if (factions.Count >= 2)
                        break;
                }
            if (factions.Count < 2 && Encyclopedia.i != null && Encyclopedia.i.factions != null)
                foreach (Faction f in Encyclopedia.i.factions)
                {
                    if (f == null || factions.Contains(f.factionName))
                        continue;
                    factions.Add(f.factionName);
                    if (factions.Count >= 2)
                        break;
                }
            if (factions.Count < 2)
            {
                result = Loc.Tr("Не нашлось двух фракций для дуэли");
                return false;
            }

            // Спец-карта знает высоты из штампованного hm (совпадает с мешом);
            // стоковая — рейкасты по текущей карте. Раскладка спец-карты приходит
            // из ScenarioWorld (гарантия ровных площадок), стоковая — местный
            // подбор по ровности (рельеф чужой, штампать нечем).
            float GroundY(Vector2 p) =>
                hm != null ? hm.SampleHeight(p.x, p.y) : GameApi.GroundY(p.x, p.y);
            bool siteOk(Vector2 p) => SiteAcceptable(p);
            bool siteLoose(Vector2 p) => SiteAcceptable(p);
            // Сейв хранит GlobalPosition = координату ОТ КАРТЫ (мир − Datum.origin,
            // см. GlobalPositionExtensions): спец-карта сама строит карто-локальные
            // точки (меш в координатах root карты), повторный вычет origin редактора
            // уносил базы на −origin (плейтест 2026-10-29: восток/центр вместо ±8000).
            // Стоковый режим получает мировые от рейкастов — конвертация нужна.
            GlobalPosition SavePos(Vector3 worldPoint) => terrainMode
                ? new GlobalPosition(worldPoint.x, worldPoint.y, worldPoint.z)
                : worldPoint.ToGlobalPosition();

            if (!terrainMode)
            {
                bases = ScenarioLayout.Bases(min, max, template, rng, siteOk);
                if (bases.Count == 0)
                {
                    result = Loc.Tr("Не нашлось ровной площадки под базы — попробуй другую карту");
                    return false;
                }
                deposits = ScenarioLayout.Deposits(min, max, template, rng, siteOk, bases, siteLoose);
                coalSites = ScenarioLayout.CoalSites(min, max, template, rng, siteOk, deposits, bases, siteLoose);
            }

            // Спец-карту строим ПОСЛЕ layout: HQ идут в состав карты scene-объектами
            // (позиции у баз уже известны).
            Vector2 mirrorA = ScenarioLayout.Mirror(bases[0].Pos, ScenarioLayout.Center(min, max));
            if (terrainMode && !RuntimeMap.EnsureRegistered(terrainKey,
                new List<KeyValuePair<string, Vector3>>
                {
                    new KeyValuePair<string, Vector3>(factions[0],
                        new Vector3(bases[0].Pos.x, GroundY(bases[0].Pos), bases[0].Pos.y)),
                    new KeyValuePair<string, Vector3>(factions[1],
                        new Vector3(mirrorA.x, GroundY(mirrorA), mirrorA.y)),
                }))
            {
                result = Loc.Tr("Не удалось построить спец-карту — подробности в логе");
                return false;
            }

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
            mission.factions.Add(new MissionFaction(factions[0])
            {
                startingBalance = template.startFunds,
                regularIncome = 0f, // потоком владеет мод (EconomyService)
            });
            mission.factions.Add(new MissionFaction(factions[1])
            {
                startingBalance = template.startFunds,
                regularIncome = 0f,
            });

            // Базы: сторона A из layout (K слотов), сторона B — зеркало каждого.
            Vector2 c = ScenarioLayout.Center(min, max);
            var starter = StarterRoster(template);
            var baseNames = new[] { new List<string>(), new List<string>() }; // A- и B-базы
            int mes = 0;
            int perSide = bases.Count;
            for (int side = 0; side < 2; side++)
            {
                string faction = factions[side];
                for (int k = 0; k < perSide; k++)
                {
                    Vector2 pos = side == 0 ? bases[k].Pos : ScenarioLayout.Mirror(bases[k].Pos, c);
                    string baseName = $"{BaseMarker}-{(side == 0 ? "A" : "B")}{(perSide > 1 ? (k + 1).ToString() : "")}";
                    baseNames[side].Add(baseName);
                    BuildAirbase(mission, baseName, faction, pos, bases[k].YawDeg,
                        template.runwayLength, template.runwayWidth, GroundY, SavePos);
                    BuildStarter(mission, baseName, faction, pos, bases[k].YawDeg, starter,
                        GroundY, SavePos, template.engineerAirdropAltM);
                    // Командирский борт (DESIGN §2.1): спавн в воздухе над ПЕРВИЧНОЙ
                    // базой стороны, орбиту/EMCON/ремонт ведёт CommanderBoardService.
                    if (template.commander && k == 0)
                        SpawnCommanderBoard(mission, faction, side == 0 ? "A" : "B",
                            pos, bases[k].YawDeg, template, GroundY, SavePos);
                }
            }

            // Месторождения v3: нейтральные МАРКЕРЫ-«недра» (не захватываются —
            // застраиваются): SN-MES — масса (тир в имени), SN-COAL — уголь у баз.
            string mesJsonKey = MissionDepositJsonKey();
            foreach (ScenarioLayout.Deposit d in deposits)
            {
                mes++;
                var sb = new SavedBuilding($"{DepositMarker}-T{d.Tier}-{mes}")
                {
                    type = mesJsonKey,
                    PlacementType = PlacementType.Custom,
                    globalPosition = SavePos(new Vector3(d.Pos.x, GroundY(d.Pos), d.Pos.y)),
                };
                mission.buildings.Add(sb);
            }
            int coal = 0;
            foreach (ScenarioLayout.Deposit d in coalSites)
            {
                coal++;
                mission.buildings.Add(new SavedBuilding($"{EconomyService.CoalMarker}-{coal}")
                {
                    type = mesJsonKey,
                    PlacementType = PlacementType.Custom,
                    globalPosition = SavePos(new Vector3(d.Pos.x, GroundY(d.Pos), d.Pos.y)),
                });
            }

            // Победа: захват ВСЕХ баз противника (один objective, CompleteAll —
            // дефолт CaptureAirbaseSavedObjective) → конец игры; второй путь —
            // убийство командирского борта (CommanderBoardService, гибрид §2.7).
            BuildVictory(mission, factions[0], baseNames[1], "A");
            BuildVictory(mission, factions[1], baseNames[0], "B");

            // Сейв напрямую в User-группу, БЕЗ MissionSaveLoad.SaveMission: тот дёргает
            // событие AfterSave, на которое редактор повесил обновление панели ошибок
            // СВОЕЙ миссии — на чужом сейве панель падала NRE (плейтест 2026-10-29).
            // Полный сид в имени (X8): короткие имена срезали его до 16 бит —
            // разные миры перезаписывали друг друга, а ключ карты расходился с
            // сидом генерации (см. RuntimeMap.MakeKey).
            saveName = $"{(terrainMode ? "SN-Terrain-" + templateName : "SN-Duel")}-{seed & 0xFFFFFFFF:X8}";
            MissionSaveLoad.ValidateName(ref saveName, allowEmpty: false);
            mission.BeforeSave();
            MissionGroup.UserGroup.SaveMission(saveName, mission);
            if (mission.SaveErrors != null && mission.SaveErrors.ErrorAndExceptionsCount > 0)
                Log.Warn($"MapGen: сохранено с ошибками ({mission.SaveErrors.ErrorAndExceptionsCount}) — см. лог игры");
            Log.Info($"MapGen: сценарий сохранён — '{saveName}' на карте " +
                     $"{(terrainKey ?? (current != null ? current.MapKey.Path : "меню"))}, игроков {template.players} (баз/сторону {perSide}), " +
                     $"базы {factions[0]}({bases[0].Pos.x:F0},{bases[0].Pos.y:F0})/" +
                     $"{factions[1]}({mirrorA.x:F0},{mirrorA.y:F0}), bounds " +
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

        /// <summary>Цикл СТИЛЯ карты (land → coast → seas → islands → …) в JSON
        /// текущего шаблона + СРАЗУ генерация (Ctrl+Shift+MapGenKey). Селектор
        /// шаблонов-файлов запутал юзера — «работает только один стиль»
        /// (плейтест 2026-11-02); стиль пишется В ФАЙЛ: обе стороны пайплайна
        /// (генерация и загрузка из ключа) читают один источник, MorphHash в
        /// ключе разводит миры. Чужие поля JSON сохраняются (JObject).</summary>
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

        /// <summary>Командирский борт стороны: SavedAircraft в воздухе над своей базой
        /// (нативный спавн-путь, AI-пилот со скиллом 1). Высота/скорость — из профиля
        /// борта (единый источник баланса), модель — шаблон или самый дорогой самолёт.</summary>
        private static void SpawnCommanderBoard(Mission mission, string faction, string sideTag,
            Vector2 pos, float yawDeg, ScenarioTemplate template,
            Func<Vector2, float> groundY, Func<Vector3, GlobalPosition> savePos)
        {
            string type = CommanderBoardType(template);
            if (string.IsNullOrEmpty(type))
            {
                Log.Warn("MapGen: в энциклопедии нет самолётов — командирский борт пропущен");
                return;
            }
            var profile = CommanderBoardProfile.Load();
            mission.aircraft.Add(new SavedAircraft($"{CommanderBoardService.BoardMarker}-{sideTag}")
            {
                type = type,
                faction = faction,
                PlacementType = PlacementType.Custom,
                globalPosition = savePos(new Vector3(pos.x, groundY(pos) + profile.orbitAltM, pos.y)),
                rotation = Quaternion.Euler(0f, yawDeg, 0f),
                playerControlled = false,
                fuel = template.commanderFuel,
                skill = 1f,
                bravery = 1f,
                startingSpeed = profile.spawnSpeed,
            });
            Log.Info($"MapGen: командирский борт стороны {sideTag} — {type}, " +
                     $"высота {profile.orbitAltM:F0} м над базой");
        }

        /// <summary>Модель борта: явный jsonKey шаблона, иначе самый дорогой ДОПУСТИМЫЙ
        /// самолёт энциклопедии (обычно стратегический — «летающий ACU» из тяжёлых
        /// машин). Фильтр IsAllowed(false) — тот же, что у Spawner: наш сценарий не
        /// включает ивентовый контент (allowEventContent не ставим), а «самый дорогой»
        /// без фильтра — UFO, его Spawner молча отказался спавнить (плейтест 2026-10-31
        /// «командирский борт не найден»).</summary>
        private static string CommanderBoardType(ScenarioTemplate template)
        {
            if (!string.IsNullOrEmpty(template.commanderJsonKey))
                return template.commanderJsonKey;
            Encyclopedia encyclopedia = Encyclopedia.i;
            if (encyclopedia?.aircraft == null || encyclopedia.aircraft.Count == 0)
                return null;
            AircraftDefinition best = null;
            foreach (AircraftDefinition a in encyclopedia.aircraft)
                if (a != null && a.IsAllowed(false) && (best == null || a.value > best.value))
                    best = a;
            return best != null ? best.jsonKey : null;
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
