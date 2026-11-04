using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Стратегические иконки юнитов в духе SupCom FA (v0.153, юзер 2026-11-04:
    /// «на глобальной камере 3D-превью нечитабельны»): PNG-набор импортируется
    /// из набора Advanced strategic icons установки SupCom FA инструментом
    /// tools/import_supcom_icons.py в icons/supcom/ (деплой csproj — рядом с
    /// Locales/). Загрузка ленивая по имени (File.ReadAllBytes → LoadImage →
    /// Sprite), маппинг — категория юнита/здания + тир ростера (канон FA: тир
    /// в имени иконки — land1_/land2_/land3_, откат на plain-имя), персональные
    /// правки — Icons/overrides.json (code/имя юнита → stem).
    ///
    /// ЕДИНАЯ точка спрайта МИРА/миникарты — Of(Unit)/OfDef(def) (потребители:
    /// CommanderHud, MinimapPanel); фолбэки вызывающего — UnitPreview.PreviewOf →
    /// плоские спрайты игры. Плитки панелей остаются на 3D-превью (юзер:
    /// «нравятся»). Отключение — настройка Icons.StrategicIcons: мир/миникарта
    /// возвращаются на превью/плоские. Вызовы из Repaint-горячих циклов:
    /// разрешение стема кэшируется ПО ОПРЕДЕЛЕНИЮ (загрузка файла — один раз).
    /// Состояние конфигурационное (файлы + ассеты): sceneLoaded не трогает,
    /// как Loc. Поддиректория отсутствует — сервис молча отдаёт null (фолбэки).
    /// </summary>
    internal static class StrategicIcons
    {
        /// <summary>Стемы, которых нет в наборе (мимо файла — не ходим в диск повторно).</summary>
        private static readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        /// <summary>Разрешённые иконки по определению (горячий путь Repaint-циклов).</summary>
        private static readonly Dictionary<UnitDefinition, Sprite> _resolved = new Dictionary<UnitDefinition, Sprite>();
        private static Dictionary<string, string> _overrides;   // code/имя → stem (Icons/overrides.json)

        /// <summary>Спрайт стратегической иконки юнита или null (выключено/нет
        /// файла) — вызывающий рисует фолбэк-цепочку (превью → плоские спрайты).</summary>
        public static Sprite Of(Unit unit)
        {
            if (!Settings.StrategicIcons || unit == null || unit.definition == null)
                return null;
            return Resolve(unit.definition, GameApi.IsEngineer(unit));
        }

        /// <summary>То же без юнита (призраки зданий,def-only контексты).</summary>
        public static Sprite OfDef(UnitDefinition def)
        {
            if (!Settings.StrategicIcons || def == null)
                return null;
            return Resolve(def, engineer: false);
        }

        /// <summary>Чистое ядро маппинга: кандидаты stem по категории (cls_role)
        /// и тиру — tiered имя (land1_directfire) первым, plain (land_directfire)
        /// вторым. Тир клампится 1..3. Тестируется раннером.</summary>
        public static string[] StemCandidates(string category, int tier)
        {
            int sep = category.IndexOf('_');
            string cls = sep > 0 ? category.Substring(0, sep) : category;
            string role = sep > 0 ? category.Substring(sep + 1) : "";
            int t = tier < 1 ? 1 : tier > 3 ? 3 : tier;
            return string.IsNullOrEmpty(role)
                ? new[] { cls }
                : new[] { cls + t + "_" + role, cls + "_" + role };
        }

        private static Sprite Resolve(UnitDefinition def, bool engineer)
        {
            if (_resolved.TryGetValue(def, out Sprite cached))
                return cached != null ? cached : null; // fake-null (выгрузка) — ниже переразрешение
            Sprite sprite = null;
            string stem = OverrideStemOf(def);
            if (stem != null)
                sprite = LoadSprite(stem);
            if (sprite == null)
            {
                string category = CategoryOf(def, engineer);
                if (category != null)
                    foreach (string candidate in StemCandidates(category, UnitRoster.TierOf(def)))
                    {
                        sprite = LoadSprite(candidate);
                        if (sprite != null)
                            break;
                    }
            }
            _resolved[def] = sprite; // null тоже кэшим — горячий путь без диска
            return sprite;
        }

        /// <summary>Категория cls_role канона FA по типу определения (null — нет
        /// категории: ракеты и прочее остаются на фолбэках).</summary>
        private static string CategoryOf(UnitDefinition def, bool engineer)
        {
            if (def is BuildingDefinition b)
            {
                // Роль пула (mes/hyd/gen/depot/…): вольные здания игры — generic.
                string role = BuildingPool.SpecOf(b)?.role;
                switch (role)
                {
                    case "mes": return "structure_mass";
                    case "hyd": return "structure_energy";
                    case "gen": return "structure_energy";
                    case "depot": return "factory_land";
                    case "hangar": case "heliport": case "revetment": return "factory_air";
                    case "storage": return "structure_generic";
                    case "mg": case "atgm": case "pillbox": return "structure_directfire";
                    case "manpads": case "aaa": return "structure_antiair";
                    case "tower": return "structure_intel";
                    case "gabion": return "structure_wall";
                    default: return "structure_generic";
                }
            }
            if (engineer)
            {
                // Морской инженер LC — «инженерной» корабельной иконки в FA нет:
                // остаётся различимым кораблём (точечные правки — overrides.json).
                if (def is ShipDefinition)
                    return "ship_generic";
                return "land_engineer";
            }
            if (def is VehicleDefinition v)
                return v.vehicleType switch
                {
                    VehicleType.MBT => "land_directfire",
                    VehicleType.ART => "land_artillery",
                    VehicleType.AAA => "land_antiair",
                    VehicleType.IR_SAM => "land_antiair",
                    VehicleType.R_SAM => "land_missile",
                    VehicleType.RDR => "land_intel",
                    _ => "land_generic",
                };
            if (def is ShipDefinition s)
                return s.shipType switch
                {
                    ShipType.DDG => "ship_directfire",
                    ShipType.FFG => "ship_directfire",
                    ShipType.CV => "ship_air",
                    ShipType.LHA => "ship_air",
                    ShipType.LFD => "ship_air",
                    _ => "ship_generic",
                };
            if (def is AircraftDefinition a)
                return Catalog.RoleKey(a.roleIdentity, a.typeIdentity) switch
                {
                    "И" => "fighter_antiair",
                    "М" => "fighter_missile",
                    "У" => "bomber_directfire",
                    "РЛС" => "fighter_intel",
                    "ТР" => "gunship_transport",
                    _ => null,
                };
            return null;
        }

        /// <summary>Персональная правка маппинга (Icons/overrides.json: code или
        /// имя юнита → stem). Файл необязателен; битый JSON — warn раз.</summary>
        private static string OverrideStemOf(UnitDefinition def)
        {
            if (_overrides == null)
            {
                _overrides = new Dictionary<string, string>();
                try
                {
                    string path = Path.Combine(Paths.PluginPath, "SupremeNuclear",
                        "Icons", "overrides.json");
                    if (File.Exists(path))
                        _overrides = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                            File.ReadAllText(path)) ?? new Dictionary<string, string>();
                }
                catch (System.Exception e)
                {
                    Log.Warn("StrategicIcons: не прочитан overrides.json — " + e.Message);
                }
            }
            if (_overrides.Count == 0)
                return null;
            if (!string.IsNullOrEmpty(def.code) && _overrides.TryGetValue(def.code, out string stem))
                return stem;
            return !string.IsNullOrEmpty(def.unitName) && _overrides.TryGetValue(def.unitName, out stem)
                ? stem : null;
        }

        private static Sprite LoadSprite(string stem)
        {
            if (_sprites.TryGetValue(stem, out Sprite cached))
                return cached != null ? cached : null;
            Sprite sprite = null;
            try
            {
                string path = Path.Combine(Paths.PluginPath, "SupremeNuclear",
                    "Icons", "supcom", "icon_" + stem + ".png");
                if (File.Exists(path))
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    if (tex.LoadImage(File.ReadAllBytes(path)))
                        sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f), tex.width, 0, SpriteMeshType.FullRect);
                    else
                        Log.Warn("StrategicIcons: не распарсён PNG — " + stem);
                }
            }
            catch (System.Exception e)
            {
                Log.Warn("StrategicIcons: не загружена иконка '" + stem + "' — " + e.Message);
            }
            _sprites[stem] = sprite; // null кэшим: мимо файла второй раз не ходим
            return sprite;
        }
    }
}
