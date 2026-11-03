using System.Collections.Generic;
using System.IO;
using BepInEx;
using NuclearOption.Networking;
using NuclearOption.SavedMission;

namespace SupremeNuclear
{
    /// <summary>
    /// «Скримиш» — запуск боя из главного меню, мимо редактора миссий. Генерация
    /// спец-карты шаблоном-пресетом стиля (MapGen/*.json с секцией terrain) и
    /// ПРЯМОЙ старт по нативному пути SinglePlayerMenu.StartMission: TryLoad из
    /// User-группы → SetMission → StartHost(Offline, SinglePlayer, MapKey) —
    /// дальше MissionManager.OnStartServer сам доводит до спавна юнитов.
    /// MP v1 — тегом Multiplayer в нативном лобби (см. ScenarioBuild): панель
    /// стартует только одиночный бой. Кнопка и панель — SkirmishPanel (UiRts).
    /// </summary>
    public static class SkirmishService
    {
        /// <summary>Порядок канона стилей (как CycleStyleAndGenerate).</summary>
        public static readonly string[] StyleOrder = { "land", "coast", "seas", "islands" };

        /// <summary>Стиль → шаблон-пресет: скан MapGen/*.json с секцией terrain
        /// (файлы существуют — ScenarioTemplate.Load образцов не создаёт); первый
        /// файл стиля выигрывает, дубликаты логируем. Юзер выбирает СТИЛЬ, а не
        /// файл — селектор файлов отвергнут плейтестом 2026-11-02.</summary>
        public static List<KeyValuePair<string, string>> ListStyles()
        {
            var byStyle = new Dictionary<string, string>();
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "MapGen");
            if (Directory.Exists(dir))
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    ScenarioTemplate t = ScenarioTemplate.Load(name);
                    if (t?.terrain == null)
                        continue; // шаблон без спец-карты (например, «duel» стоковой ветки)
                    string style = HeightmapGen.StyleOf(t.terrain.style);
                    if (byStyle.ContainsKey(style))
                    {
                        Log.Warn("Skirmish: пресет стиля '" + style + "' уже есть (" +
                                 byStyle[style] + ") — '" + name + "' игнорируется");
                        continue;
                    }
                    byStyle[style] = name;
                }
            var result = new List<KeyValuePair<string, string>>();
            foreach (string style in StyleOrder)
                if (byStyle.TryGetValue(style, out string template))
                    result.Add(new KeyValuePair<string, string>(style, template));
            return result;
        }

        /// <summary>Сид из текста панели: пусто/мусор = 0 (= случайный каждый
        /// бой, конвенция Settings.MapGenSeed). Чистая логика — тест.</summary>
        public static int ParseSeed(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            return int.TryParse(text.Trim(), out int v) ? v : 0;
        }

        /// <summary>Генерация + прямой старт. Вызов из главного меню (клиентских
        /// ограничений нет — оффлайн-хост); false + причина — отказ в статус-строку
        /// панели. Тумблер AI панель применяет отдельно (AiProfile.Save).</summary>
        public static bool Start(string templateName, int seed, int players, out string error)
        {
            error = null;
            try
            {
                if (!ScenarioBuild.Generate(templateName, seed, out string gen,
                        loadEditor: false, playersOverride: players, out string saveName))
                {
                    error = gen;
                    return false;
                }
                // Нативный путь MissionsPicker: сейв с диска (AfterLoad заполняет
                // RuntimeObjectives/LoadKey), затем ровно SinglePlayerMenu.StartMission.
                // (MissionGroup.User — инстанс группы; UserGroup — только тип.)
                var key = new MissionKey(saveName, MissionGroup.User);
                if (!key.TryLoad(out Mission mission, out string loadError))
                {
                    error = Loc.F("Миссия не загрузилась: {0}", loadError);
                    return false;
                }
                MissionManager.SetMission(mission, checkIfSame: false);
                NetworkManagerNuclearOption.i.StartHost(
                    new HostOptions(SocketType.Offline, GameState.SinglePlayer, mission.MapKey));
                // Режим командира поднимется сам при входе в миссию
                // (юзер-решение 2026-11-02; флаг с именем — самоинвалидируется).
                CommanderModeController.PendingAutoActivateMission = mission.Name;
                Log.Info("Skirmish: старт — " + mission.Name + " (шаблон " + templateName +
                         ", сид " + seed + ", игроков " + players + ")");
                return true;
            }
            catch (System.Exception e)
            {
                Log.Error("Skirmish: старт не удался — " + e);
                error = Loc.Tr("Старт не удался — подробности в логе");
                return false;
            }
        }
    }
}
