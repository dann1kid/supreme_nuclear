using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using NuclearOption.Networking;
using NuclearOption.SavedMission;

namespace SupremeNuclear
{
    /// <summary>
    /// «Скримиш» — пресеты стилей карт и сид-конвенция для ЕДИНОГО лобби (v3):
    /// селект стилей (MapGen/*.json) и ParseSeed. Старт (и соло, и MP) идёт
    /// через SnLobbyService.Launch (комната → карта → нативный каскад);
    /// редакторский Ctrl+G — ScenarioBuild.Generate напрямую.
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

        // ---------- библиотека сохранённых карт (лобби v3) ----------

        /// <summary>Карта из библиотеки: тройка (шаблон, сид, пул позиций) из
        /// имени SN-сейва. Миссия пересобирается под ТЕКУЩИЙ план армий, сейв
        /// задаёт только детерминированный мир.</summary>
        public sealed class SavedMap
        {
            public string Name;     // имя сейва (SN-Terrain-…)
            public string Template;
            public int Seed;
            public int Players;     // пул позиций (2×пары); -1 — легаси-ключ без «-pN»
        }

        /// <summary>Парс имени SN-сейва → SavedMap (чистая логика — тест).
        /// null — не SN-ключ (чужие миссии игнорируются).</summary>
        public static SavedMap ParseSavedName(string saveName)
        {
            if (string.IsNullOrEmpty(saveName) ||
                !saveName.StartsWith("SN-Terrain-", StringComparison.Ordinal) ||
                !MapGenKey.TryParse(saveName, out MapGenKey.Parsed parsed))
                return null;
            return new SavedMap
            {
                Name = saveName,
                Template = parsed.Template,
                Seed = parsed.Seed,
                Players = parsed.Players ?? -1, // легаси: пул из файла шаблона
            };
        }

        /// <summary>Библиотека: SN-сейвы User-группы (нативный список
        /// persistentDataPath/Missions), отсортированы по имени. Пусто —
        /// папки нет/ни одной карты.</summary>
        public static List<SavedMap> ListSavedMaps()
        {
            var result = new List<SavedMap>();
            try
            {
                foreach (MissionKey key in MissionGroup.User.GetMissions())
                {
                    SavedMap map = ParseSavedName(key.Name);
                    if (map != null)
                        result.Add(map);
                }
            }
            catch (System.Exception e)
            {
                Log.Error("Skirmish: библиотека карт не читается — " + e.Message);
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }
    }
}
