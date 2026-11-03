using System.Globalization;

namespace SupremeNuclear
{
    /// <summary>
    /// Формат ключа спец-карты (чистая логика без Unity — покрыт тестами):
    /// «SN-Terrain-&lt;шаблон&gt;-&lt;сид X8&gt;[-&lt;морф X8&gt;][-&lt;размер&gt;k][-p&lt;игроки&gt;]».
    /// Имя шаблона может содержать дефисы (Duel-Land) — сид ищется как первый
    /// 8-символьный hex-токен, фиксированные индексы Split не годятся (пресеты
    /// стилей с дефисом ломали разбор в EnsureRegistered). Суффиксы различаются
    /// по форме: 8 hex — морф, «Nk» — размер, «pN» — игроки.
    /// Игроки в ключе (v0.143, Скримиш): players масштабирует базы, но НЕ входит
    /// в MorphHash — без суффикса смена 1×1↔2×2 давала коллизию миров в кэше
    /// Built (ключ тот же, раскладка другая). Загрузчик (клиент MP в т.ч.) берёт
    /// игроков из ключа, а не из файла шаблона — файл мог уйти вперёд/назад.
    /// Старые ключи v1 (4-симв. сид последним токеном, без морфа) парсятся как
    /// раньше: игроки/размер берутся из файла шаблона.
    /// </summary>
    public static class MapGenKey
    {
        public const string Prefix = "SN-Terrain-";

        /// <summary>Разобранный ключ; Morph/SizeK/Players — null у старых ключей.</summary>
        public struct Parsed
        {
            public string Template; // имя шаблона (может содержать дефисы)
            public int Seed;        // полный сид (X8)
            public string Morph;    // хеш морфологии или null
            public int? SizeK;      // размер карты в км или null
            public int? Players;    // игроков или null (значит «как в файле шаблона»)
        }

        public static string Build(string template, int seed, string morph = null,
            int? sizeK = null, int? players = null) =>
            Prefix + template + "-" + (seed & 0xFFFFFFFF).ToString("X8") +
            (morph != null ? "-" + morph : "") +
            (sizeK != null ? "-" + sizeK.Value.ToString(CultureInfo.InvariantCulture) + "k" : "") +
            (players != null ? "-p" + players.Value.ToString(CultureInfo.InvariantCulture) : "");

        public static bool TryParse(string key, out Parsed parsed)
        {
            parsed = default(Parsed);
            if (string.IsNullOrEmpty(key) || !key.StartsWith(Prefix, System.StringComparison.Ordinal))
                return false;
            string[] tokens = key.Substring(Prefix.Length).Split('-');
            if (tokens.Length < 2)
                return false;

            // Сид v3+ — первый 8-символьный hex-токен; имя шаблона стоит до него.
            int seedIdx = -1;
            for (int i = 0; i < tokens.Length; i++)
                if (tokens[i] != null && tokens[i].Length == 8 && IsHex(tokens[i]))
                {
                    seedIdx = i;
                    break;
                }
            // Старый формат v1: 4-символьный сид — последний токен, хвоста нет.
            bool legacy = false;
            if (seedIdx < 0 && tokens.Length >= 2)
            {
                string last = tokens[tokens.Length - 1];
                if (last != null && last.Length == 4 && IsHex(last))
                {
                    seedIdx = tokens.Length - 1;
                    legacy = true;
                }
            }
            if (seedIdx <= 0) // сид не найден или шаблон перед ним пуст
                return false;

            parsed.Template = string.Join("-", tokens, 0, seedIdx);
            if (parsed.Template.Length == 0)
                return false; // «SN-Terrain--ABCD»: пустое имя шаблона — мусор
            if (!int.TryParse(tokens[seedIdx], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out int seed))
                return false;
            parsed.Seed = seed;
            if (legacy)
                return true;

            // Хвост после сида: морф/размер/игроки — по форме, в любом порядке.
            for (int i = seedIdx + 1; i < tokens.Length; i++)
            {
                string t = tokens[i];
                if (string.IsNullOrEmpty(t))
                    continue;
                if (t.Length == 8 && IsHex(t))
                    parsed.Morph = t;
                else if (t[t.Length - 1] == 'k' && int.TryParse(t.Substring(0, t.Length - 1),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int km))
                    parsed.SizeK = km;
                else if (t[0] == 'p' && t.Length > 1 && int.TryParse(t.Substring(1),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int pl))
                    parsed.Players = pl;
                // неизвестный токен — игнорируем (прямая совместимость)
            }
            return true;
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok)
                    return false;
            }
            return true;
        }
    }
}
