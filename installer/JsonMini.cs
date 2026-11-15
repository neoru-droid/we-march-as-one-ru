using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WmaoRuInstaller
{
    internal sealed class AchievementTranslation
    {
        public string NameRu;
        public string DescRu;
    }

    internal sealed class TranslationPayload
    {
        public readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        public readonly Dictionary<string, AchievementTranslation> SteamAchievements =
            new Dictionary<string, AchievementTranslation>(StringComparer.Ordinal);

        public static TranslationPayload LoadFromFile(string jsonPath)
        {
            if (!File.Exists(jsonPath))
            {
                throw new FileNotFoundException("Файл переводов не найден: " + jsonPath, jsonPath);
            }

            string raw = File.ReadAllText(jsonPath, BinaryUtils.Utf8NoBom);
            Dictionary<string, object> root = JsonMini.ParseObject(raw);

            TranslationPayload payload = new TranslationPayload();

            Dictionary<string, object> tablesContainer = root;
            object nestedTables;
            if (root.TryGetValue("tables", out nestedTables) && nestedTables is Dictionary<string, object>)
            {
                tablesContainer = (Dictionary<string, object>)nestedTables;
            }

            foreach (KeyValuePair<string, object> kv in tablesContainer)
            {
                if (kv.Key == "steam_achievements")
                {
                    continue;
                }
                Dictionary<string, object> rowMap = kv.Value as Dictionary<string, object>;
                if (rowMap == null)
                {
                    continue;
                }
                Dictionary<string, string> table = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object> row in rowMap)
                {
                    string val = row.Value as string;
                    if (val == null)
                    {
                        throw new InvalidDataException("Некорректная строка перевода в таблице " + kv.Key + ":" + row.Key);
                    }
                    table[row.Key] = val;
                }
                payload.Tables[kv.Key] = table;
            }

            object achObj;
            if (root.TryGetValue("steam_achievements", out achObj) && achObj is Dictionary<string, object>)
            {
                Dictionary<string, object> achMap = (Dictionary<string, object>)achObj;
                foreach (KeyValuePair<string, object> kv in achMap)
                {
                    Dictionary<string, object> item = kv.Value as Dictionary<string, object>;
                    if (item == null)
                    {
                        throw new InvalidDataException("Некорректная запись достижения " + kv.Key);
                    }
                    object nObj;
                    object dObj;
                    string nameRu = item.TryGetValue("name_ru", out nObj) ? (nObj as string) : null;
                    string descRu = item.TryGetValue("desc_ru", out dObj) ? (dObj as string) : null;
                    if (string.IsNullOrEmpty(nameRu) || string.IsNullOrEmpty(descRu))
                    {
                        throw new InvalidDataException("Пустой перевод достижения " + kv.Key);
                    }
                    payload.SteamAchievements[kv.Key] = new AchievementTranslation
                    {
                        NameRu = nameRu,
                        DescRu = descRu
                    };
                }
            }

            if (payload.Tables.Count == 0)
            {
                throw new InvalidDataException("В файле переводов отсутствуют таблицы локализации.");
            }
            return payload;
        }
    }

    internal static class JsonMini
    {
        public static Dictionary<string, object> ParseObject(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException("json");
            }
            int idx = 0;
            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                idx = 1;
            }
            object val = ParseValue(json, ref idx);
            SkipWhitespace(json, ref idx);
            if (idx != json.Length)
            {
                throw new InvalidDataException("Лишние символы после конца JSON на позиции " + idx);
            }
            Dictionary<string, object> dict = val as Dictionary<string, object>;
            if (dict == null)
            {
                throw new InvalidDataException("Ожидался корневой JSON-объект.");
            }
            return dict;
        }

        private static void SkipWhitespace(string s, ref int idx)
        {
            while (idx < s.Length)
            {
                char c = s[idx];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    idx++;
                }
                else
                {
                    break;
                }
            }
        }

        private static object ParseValue(string s, ref int idx)
        {
            SkipWhitespace(s, ref idx);
            if (idx >= s.Length)
            {
                throw new InvalidDataException("Неожиданный конец JSON.");
            }
            char c = s[idx];
            if (c == '{') return ParseDict(s, ref idx);
            if (c == '[') return ParseList(s, ref idx);
            if (c == '"') return ParseString(s, ref idx);
            if (c == 't') return ParseLiteral(s, ref idx, "true", true);
            if (c == 'f') return ParseLiteral(s, ref idx, "false", false);
            if (c == 'n') return ParseLiteral(s, ref idx, "null", null);
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref idx);
            throw new InvalidDataException("Неожиданный символ JSON '" + c + "' на позиции " + idx);
        }

        private static object ParseLiteral(string s, ref int idx, string literal, object result)
        {
            if (idx + literal.Length > s.Length || string.CompareOrdinal(s, idx, literal, 0, literal.Length) != 0)
            {
                throw new InvalidDataException("Ожидался литерал " + literal + " на позиции " + idx);
            }
            idx += literal.Length;
            return result;
        }

        private static object ParseNumber(string s, ref int idx)
        {
            int start = idx;
            if (s[idx] == '-') idx++;
            while (idx < s.Length && s[idx] >= '0' && s[idx] <= '9') idx++;
            bool isFloat = false;
            if (idx < s.Length && s[idx] == '.')
            {
                isFloat = true;
                idx++;
                while (idx < s.Length && s[idx] >= '0' && s[idx] <= '9') idx++;
            }
            if (idx < s.Length && (s[idx] == 'e' || s[idx] == 'E'))
            {
                isFloat = true;
                idx++;
                if (idx < s.Length && (s[idx] == '+' || s[idx] == '-')) idx++;
                while (idx < s.Length && s[idx] >= '0' && s[idx] <= '9') idx++;
            }
            string slice = s.Substring(start, idx - start);
            if (isFloat)
            {
                return double.Parse(slice, CultureInfo.InvariantCulture);
            }
            return long.Parse(slice, CultureInfo.InvariantCulture);
        }

        private static string ParseString(string s, ref int idx)
        {
            if (s[idx] != '"')
            {
                throw new InvalidDataException("Ожидалась кавычка строки на позиции " + idx);
            }
            idx++;
            StringBuilder sb = new StringBuilder();
            while (idx < s.Length)
            {
                char c = s[idx++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (idx >= s.Length)
                {
                    throw new InvalidDataException("Незавершённая escape-последовательность в JSON.");
                }
                char esc = s[idx++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (idx + 4 > s.Length)
                        {
                            throw new InvalidDataException("Неполный \\uXXXX код в JSON.");
                        }
                        ushort code = ushort.Parse(s.Substring(idx, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        idx += 4;
                        sb.Append((char)code);
                        break;
                    default:
                        throw new InvalidDataException("Неизвестный escape-символ \\" + esc);
                }
            }
            throw new InvalidDataException("Незакрытая JSON-строка.");
        }

        private static Dictionary<string, object> ParseDict(string s, ref int idx)
        {
            idx++; // '{'
            Dictionary<string, object> dict = new Dictionary<string, object>(StringComparer.Ordinal);
            SkipWhitespace(s, ref idx);
            if (idx < s.Length && s[idx] == '}')
            {
                idx++;
                return dict;
            }
            while (true)
            {
                SkipWhitespace(s, ref idx);
                if (idx >= s.Length || s[idx] != '"')
                {
                    throw new InvalidDataException("Ожидался строковый ключ объекта на позиции " + idx);
                }
                string key = ParseString(s, ref idx);
                SkipWhitespace(s, ref idx);
                if (idx >= s.Length || s[idx] != ':')
                {
                    throw new InvalidDataException("Ожидался символ ':' на позиции " + idx);
                }
                idx++;
                object val = ParseValue(s, ref idx);
                dict[key] = val;
                SkipWhitespace(s, ref idx);
                if (idx >= s.Length)
                {
                    throw new InvalidDataException("Незакрытый JSON-объект.");
                }
                if (s[idx] == '}')
                {
                    idx++;
                    return dict;
                }
                if (s[idx] != ',')
                {
                    throw new InvalidDataException("Ожидался разделитель ',' или '}' на позиции " + idx);
                }
                idx++;
            }
        }

        private static List<object> ParseList(string s, ref int idx)
        {
            idx++; // '['
            List<object> list = new List<object>();
            SkipWhitespace(s, ref idx);
            if (idx < s.Length && s[idx] == ']')
            {
                idx++;
                return list;
            }
            while (true)
            {
                list.Add(ParseValue(s, ref idx));
                SkipWhitespace(s, ref idx);
                if (idx >= s.Length)
                {
                    throw new InvalidDataException("Незакрытый JSON-массив.");
                }
                if (s[idx] == ']')
                {
                    idx++;
                    return list;
                }
                if (s[idx] != ',')
                {
                    throw new InvalidDataException("Ожидался разделитель ',' или ']' на позиции " + idx);
                }
                idx++;
            }
        }

        public static string SerializePretty(Dictionary<string, object> obj)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, obj, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteIndent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++)
            {
                sb.Append("  ");
            }
        }

        private static void WriteValue(StringBuilder sb, object val, int depth)
        {
            if (val == null)
            {
                sb.Append("null");
                return;
            }
            string strVal = val as string;
            if (strVal != null)
            {
                WriteString(sb, strVal);
                return;
            }
            if (val is bool)
            {
                sb.Append(((bool)val) ? "true" : "false");
                return;
            }
            if (val is int || val is long)
            {
                sb.Append(Convert.ToInt64(val, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            Dictionary<string, object> dict = val as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0)
                {
                    sb.Append("{}");
                    return;
                }
                sb.Append("{\n");
                List<string> keys = new List<string>(dict.Keys);
                keys.Sort(StringComparer.Ordinal);
                for (int i = 0; i < keys.Count; i++)
                {
                    WriteIndent(sb, depth + 1);
                    WriteString(sb, keys[i]);
                    sb.Append(": ");
                    WriteValue(sb, dict[keys[i]], depth + 1);
                    if (i + 1 < keys.Count)
                    {
                        sb.Append(',');
                    }
                    sb.Append('\n');
                }
                WriteIndent(sb, depth);
                sb.Append('}');
                return;
            }
            throw new NotSupportedException("Неподдерживаемый тип для JSON: " + val.GetType().FullName);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
