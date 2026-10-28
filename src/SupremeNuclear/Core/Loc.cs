using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace SupremeNuclear
{
    /// <summary>
    /// Локализация мода, gettext-подход: исходные строки в коде — русские, они же и ключи.
    /// Локаль = JSON-файл BepInEx/plugins/SupremeNuclear/Locales/&lt;имя&gt;.json вида
    /// {"_plural": "en", "русская строка": "перевод"}. Русской локали-файла не нужно
    /// (ключ = текст). Фолбэк: выбранная локаль → en → исходная строка, поэтому непереведённые
    /// ключи у англоязычного игрока видны по-русски (пропуск заметен, но не ломает UI).
    /// Новая локаль = новый JSON-файл; допустимые значения настройки General.Language
    /// собираются из папки при старте. Чистое ядро (Install/Tr/F/Plural) — тестируется.
    /// Состояние конфигурационное (не сценическое) — sceneLoaded его не трогает (как Settings).
    /// </summary>
    internal static class Loc
    {
        /// <summary>Текущий язык: "ru", "en" или имя файла локали без расширения.</summary>
        public static string Language = "ru";

        private static readonly Dictionary<string, string> Empty = new Dictionary<string, string>();
        private static Dictionary<string, string> _table = Empty; // выбранная локаль
        private static Dictionary<string, string> _en = Empty;    // таблица фолбэка en
        private static string _pluralRule = "ru"; // "ru" — 3 формы, "en" — one/other

        // ---------- загрузка (BepInEx/Unity; чистое ядро ниже) ----------

        /// <summary>Загрузка таблиц по настройке языка ("auto" = язык системы:
        /// русский → ru, прочие → en). Вызывается в Awake ДО Settings.Bind —
        /// описания остальных настроек тоже проходят через Tr.</summary>
        public static void Bootstrap(string preferred)
        {
            string auto = Application.systemLanguage == SystemLanguage.Russian ? "ru" : "en";
            string lang = string.IsNullOrEmpty(preferred) || preferred == "auto"
                ? auto
                : preferred.Trim().ToLowerInvariant();
            string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Locales");
            string en = ReadLocale(Path.Combine(dir, "en.json"));
            string sel = lang == "ru" || lang == "en"
                ? null
                : ReadLocale(Path.Combine(dir, lang + ".json"));
            Install(lang, en, sel);
            Log.Info("Loc: язык " + Language + " — строк переведено " + _table.Count +
                     ", фолбэк en — " + _en.Count);
        }

        /// <summary>Имена локалей в папке плагина (для списка допустимых значений настройки
        /// языка; ru/en доступны всегда, файлом не обязаны).</summary>
        public static string[] Available()
        {
            try
            {
                string dir = Path.Combine(Paths.PluginPath, "SupremeNuclear", "Locales");
                return Directory.Exists(dir)
                    ? Directory.GetFiles(dir, "*.json")
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(n => n != "ru" && n != "en")
                        .ToArray()
                    : new string[0];
            }
            catch (Exception e)
            {
                Log.Error("Loc: не прочитан список локалей: " + e.Message);
                return new string[0];
            }
        }

        private static string ReadLocale(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e)
            {
                Log.Error("Loc: не читается " + path + ": " + e.Message);
                return null;
            }
        }

        // ---------- чистое ядро (без движка — тестируется раннером) ----------

        /// <summary>Установка таблиц: language — выбранная локаль; enJson — таблица фолбэка;
        /// localeJson — таблица локали (null или "ru" → русская идентичность, "en" → en).
        /// ВАЖНО: ru — исходный язык (ключ = текст), фолбэк на en для него НЕ включается,
        /// иначе русский игрок увидит английский там, где en.json уже переведён.</summary>
        public static void Install(string language, string enJson, string localeJson)
        {
            if (language == "ru")
            {
                _en = Empty;
                _table = Empty;
                _pluralRule = "ru";
            }
            else
            {
                _en = Parse(enJson, out string enRule);
                if (localeJson == null || language == "en")
                {
                    _table = _en;
                    _pluralRule = enRule;
                }
                else
                {
                    _table = Parse(localeJson, out string rule);
                    _pluralRule = rule;
                }
            }
            Language = language;
        }

        /// <summary>Перевод строки: локаль → en → исходник; null/пустое — как есть.
        /// Без установленной таблицы — тождество (тесты, русский).</summary>
        public static string Tr(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            if (_table.Count > 0 && _table.TryGetValue(s, out string v))
                return v;
            if (!ReferenceEquals(_table, _en) && _en.Count > 0 && _en.TryGetValue(s, out v))
                return v;
            return s;
        }

        /// <summary>Форматная строка с переводом: Loc.F("Готово: {0}", n).
        /// Плейсхолдеры — только номера ({0}), не именованные.</summary>
        public static string F(string format, params object[] args)
        {
            string s = Tr(format);
            return args == null || args.Length == 0 ? s : string.Format(s, args);
        }

        /// <summary>Форма множественного числа: ключ — три русских формы
        /// (один|несколько|много); перевод локали — две формы через | ("type|types",
        /// правило one/other) либо свои три. Нет перевода — исходное русское правило.</summary>
        public static string Plural(int n, string one, string few, string many)
        {
            string key = one + "|" + few + "|" + many;
            string val = Tr(key);
            if (val == key)
                return Util.RuPlural(n, one, few, many);
            string[] forms = val.Split('|');
            if (forms.Length == 2)
                return (n == 1 ? forms[0] : forms[1]).Trim();
            if (forms.Length < 3)
                return forms[0].Trim();
            int idx = _pluralRule == "en"
                ? (n == 1 ? 0 : 2)
                : Util.RuPluralIndex(n);
            return forms[idx].Trim();
        }

        private static Dictionary<string, string> Parse(string json, out string pluralRule)
        {
            pluralRule = "ru";
            if (string.IsNullOrEmpty(json))
                return Empty;
            try
            {
                var raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (raw == null)
                    return Empty;
                if (raw.TryGetValue("_plural", out string rule) && rule == "en")
                    pluralRule = "en";
                var table = new Dictionary<string, string>(raw.Count, StringComparer.Ordinal);
                foreach (var kv in raw)
                    if (!kv.Key.StartsWith("_") && !string.IsNullOrEmpty(kv.Value) && !table.ContainsKey(kv.Key))
                        table[kv.Key] = kv.Value;
                return table;
            }
            catch (Exception e)
            {
                Log.Error("Loc: битый JSON локали — таблица игнорируется: " + e.Message);
                return Empty;
            }
        }
    }
}
