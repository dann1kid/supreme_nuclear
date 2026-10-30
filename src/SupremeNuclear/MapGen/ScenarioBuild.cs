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

        /// <summary>Генерация и сохранение миссии; false + причина — отказ.</summary>
        public static bool Generate(string templateName, int seed, out string result)
        {
            result = null;
            Mission current = MissionManager.CurrentMission;
            if (current == null)
            {
                result = Loc.Tr("Нет загруженной миссии — открой карту в редакторе миссий");
                return false;
            }

            ScenarioTemplate template = ScenarioTemplate.Load(templateName);
            if (seed == 0)
                seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            var rng = new System.Random(seed);

            // Режим спец-карты: шаблон с секцией terrain строит собственный рельеф
            // (ключ SN-Terrain-…, высоты/маски из heightmap), иначе — стоковая карта редактора.
            bool terrainMode = template.terrain != null;
            Heightmap hm = null;
            string terrainKey = null;
            Vector2 min, max;
            if (terrainMode)
            {
                terrainKey = RuntimeMap.MakeKey(templateName, seed);
                hm = HeightmapGen.Generate(template.terrain, seed); // тот же сид — тот же мир
                float half = template.terrain.sizeM * 0.5f;
                min = new Vector2(-half, -half);
                max = new Vector2(half, half);
            }
            else if (!GameApi.TryGetMapBounds(out min, out max))
            {
                result = Loc.Tr("Карта ещё не готова — подожди загрузки");
                return false;
            }

            // Фракции: первые две текущей миссии; если редактор дал одну — добираем из ростра.
            List<string> factions = new List<string>();
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

            // Спец-карта знает высоты и пригодность из масок (Neroxis-паттерн);
            // стоковая — рейкасты по текущей карте.
            float GroundY(Vector2 p) =>
                hm != null ? hm.SampleHeight(p.x, p.y) : GameApi.GroundY(p.x, p.y);
            bool siteOk(Vector2 p) =>
                hm != null ? hm.FlatOk(p.x, p.y) : SiteAcceptable(p);
            // Сейв хранит GlobalPosition = координату ОТ КАРТЫ (мир − Datum.origin,
            // см. GlobalPositionExtensions): спец-карта сама строит карто-локальные
            // точки (меш в координатах root карты), повторный вычет origin редактора
            // уносил базы на −origin (плейтест 2026-10-29: восток/центр вместо ±8000).
            // Стоковый режим получает мировые от рейкастов — конвертация нужна.
            GlobalPosition SavePos(Vector3 worldPoint) => terrainMode
                ? new GlobalPosition(worldPoint.x, worldPoint.y, worldPoint.z)
                : worldPoint.ToGlobalPosition();

            var bases = ScenarioLayout.Bases(min, max, template, rng, siteOk);
            if (bases.Count == 0)
            {
                result = Loc.Tr("Не нашлось ровной площадки под базы — попробуй другую карту");
                return false;
            }
            var deposits = ScenarioLayout.Deposits(min, max, template, rng, siteOk, bases);

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
            };
            mission.missionSettings.Tags.Add(MissionTag.SinglePlayer);
            mission.missionSettings.Tags.Add(MissionTag.PVP);
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

            // Базы: сторона A из layout, сторона B — зеркало относительно центра.
            Vector2 c = ScenarioLayout.Center(min, max);
            var starter = StarterRoster(template);
            // Уголь — до цикла баз: старт-инженеры стороны B получают зеркальные списки.
            var coalSites = ScenarioLayout.CoalSites(min, max, template, rng, siteOk, deposits, bases);
            int mes = 0;
            for (int side = 0; side < 2; side++)
            {
                Vector2 pos = side == 0 ? bases[0].Pos : ScenarioLayout.Mirror(bases[0].Pos, c);
                string faction = factions[side];
                string baseName = $"{BaseMarker}-{(side == 0 ? "A" : "B")}";
                BuildAirbase(mission, baseName, faction, pos, bases[0].YawDeg,
                    template.runwayLength, template.runwayWidth, GroundY, SavePos);
                BuildStarter(mission, baseName, faction, pos, bases[0].YawDeg, starter,
                    GroundY, SavePos, side == 0 ? deposits : MirrorDeposits(deposits, c),
                    side == 0 ? coalSites : MirrorDeposits(coalSites, c));
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

            // Победа: захват базы противника → конец игры (нативные objectives/outcomes).
            BuildVictory(mission, factions[0], $"{BaseMarker}-B", "A");
            BuildVictory(mission, factions[1], $"{BaseMarker}-A", "B");

            // Сейв напрямую в User-группу, БЕЗ MissionSaveLoad.SaveMission: тот дёргает
            // событие AfterSave, на которое редактор повесил обновление панели ошибок
            // СВОЕЙ миссии — на чужом сейве панель падала NRE (плейтест 2026-10-29).
            string saveName = $"{(terrainMode ? "SN-Terrain-" + templateName : "SN-Duel")}-{seed & 0xFFFF:X}";
            MissionSaveLoad.ValidateName(ref saveName, allowEmpty: false);
            mission.BeforeSave();
            MissionGroup.UserGroup.SaveMission(saveName, mission);
            if (mission.SaveErrors != null && mission.SaveErrors.ErrorAndExceptionsCount > 0)
                Log.Warn($"MapGen: сохранено с ошибками ({mission.SaveErrors.ErrorAndExceptionsCount}) — см. лог игры");
            Log.Info($"MapGen: сценарий сохранён — '{saveName}' на карте " +
                     $"{(terrainKey ?? current.MapKey.Path)}, базы {factions[0]}({bases[0].Pos.x:F0},{bases[0].Pos.y:F0})/" +
                     $"{factions[1]}({mirrorA.x:F0},{mirrorA.y:F0}), bounds " +
                     $"{(terrainMode ? "спец-карта ±" + (template.terrain.sizeM * 0.5f).ToString("F0") : "стоковая")}, " +
                     $"масса {mes}, уголь {coal}, сид {seed}");

            // Редактор перезагружается с готовой миссией (нативный LoadEditor): юниты
            // спавнятся в сцену — раскладку видно, её можно править и Play.
            _ = MissionEditor.LoadEditor(mission);
            result = Loc.F("Сценарий {0}: раскладка загружается в редактор", saveName);
            return true;
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

        /// <summary>Стартовый комплект слота: все юниты одинаковые (роли — от географии).</summary>
        private static void BuildStarter(Mission mission, string baseName, string faction,
            Vector2 center, float yawDeg, List<ScenarioTemplate.StarterItem> starter,
            Func<Vector2, float> groundY, Func<Vector3, GlobalPosition> toSave,
            List<ScenarioLayout.Deposit> mass = null, List<ScenarioLayout.Deposit> coal = null)
        {
            Quaternion rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 dir = rot * Vector3.forward;
            Vector3 right = new Vector3(dir.z, 0f, -dir.x);
            Vector3 c3 = new Vector3(center.x, groundY(center), center.y);
            // Линия юнитов сбоку от полосы (за «на полосе»-габаритом), депо — позади торца.
            Vector3 line = c3 + right * 170f;
            Quaternion face = Quaternion.LookRotation(dir, Vector3.up);

            // Юзер-канон (2026-10-30): стартовые инженеры стоят У МЕСТОРОЖДЕНИЙ —
            // первые двое у ближайшего угля, остальные — по ближайшим точкам массы
            // (SupCom: ACU стартует среди мексов). Высота каждой позиции — из
            // рельефа (groundY), никаких «линий на весу».
            var engineerSpots = new List<Vector2>();
            if (mass != null || coal != null)
            {
                Vector2 c2 = center;
                System.Func<List<ScenarioLayout.Deposit>, Vector2> nearest = list =>
                {
                    Vector2 best = c2;
                    float bestSq = float.MaxValue;
                    if (list != null)
                        foreach (ScenarioLayout.Deposit d in list)
                        {
                            if (d == null) continue;
                            float sq = (d.Pos - c2).sqrMagnitude;
                            if (sq < bestSq) { bestSq = sq; best = d.Pos; }
                        }
                    return best;
                };
                Vector2 coalPos = nearest(coal);
                engineerSpots.Add(coalPos + new Vector2(60f, 0f));
                engineerSpots.Add(coalPos + new Vector2(-60f, 60f));
                var massSorted = new List<ScenarioLayout.Deposit>(mass ?? new List<ScenarioLayout.Deposit>());
                massSorted.Sort((a, b) => (a.Pos - c2).sqrMagnitude.CompareTo((b.Pos - c2).sqrMagnitude));
                foreach (ScenarioLayout.Deposit d in massSorted)
                {
                    if (engineerSpots.Count >= 10) break;
                    engineerSpots.Add(d.Pos + new Vector2(60f, 0f));
                }
            }

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
                    if (item.role == "engineer" && engineerSpots.Count > 0)
                    {
                        Vector2 sp = engineerSpots[0];
                        engineerSpots.RemoveAt(0);
                        p = new Vector3(sp.x, 0f, sp.y);
                    }
                    if (item.role == "depot")
                        p = c3 - dir * 260f + right * 170f;
                    p.y = groundY(new Vector2(p.x, p.z));
                    if (item.role == "depot")
                    {
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

        /// <summary>Победа стороны: захват базы противника → EndGame(Victory).</summary>
        private static void BuildVictory(Mission mission, string faction, string targetBase, string tag)
        {
            string outcomeName = $"SN-END-{tag}";
            var objective = (CaptureAirbaseSavedObjective)SavedObjective.CreateSavedObjective(
                ObjectiveType.CaptureAirbase, $"SN-WIN-{tag}");
            objective.Faction = faction;
            objective.DisplayName = Loc.Tr("Захват базы противника");
            objective.targetAirbases.Add(targetBase);
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

        /// <summary>Зеркальный список точек для стороны B (раскладка генерится
        /// для стороны A; сторона B получает отражённые позиции).</summary>
        private static List<ScenarioLayout.Deposit> MirrorDeposits(
            List<ScenarioLayout.Deposit> list, Vector2 c)
        {
            var result = new List<ScenarioLayout.Deposit>();
            if (list == null)
                return result;
            foreach (ScenarioLayout.Deposit d in list)
                if (d != null)
                    result.Add(new ScenarioLayout.Deposit
                    {
                        Pos = ScenarioLayout.Mirror(d.Pos, c),
                        Tier = d.Tier,
                    });
            return result;
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
