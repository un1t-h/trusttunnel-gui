using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TrustTunnelGui
{
    /// <summary>
    /// Небольшой TOML-парсер для импорта конфигов. Возвращает плоский словарь,
    /// где ключ — полный путь через точку ("endpoint.hostname", "listener.tun.mtu_size").
    /// Значения: string, bool, long, double, List&lt;object&gt;, Dictionary&lt;string, object&gt;.
    /// </summary>
    public class TomlParser
    {
        readonly string s;
        int i;
        readonly Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);

        TomlParser(string text) { s = text.Replace("\r\n", "\n"); }

        public static Dictionary<string, object> Parse(string text)
        {
            var p = new TomlParser(text ?? "");
            p.Run();
            return p.result;
        }

        void Run()
        {
            string section = "";
            while (true)
            {
                SkipWsNl();
                if (i >= s.Length) break;
                char c = s[i];
                if (c == '[')
                {
                    bool arrayTable = i + 1 < s.Length && s[i + 1] == '[';
                    i += arrayTable ? 2 : 1;
                    section = ReadKeyPath(']');
                    Expect(']');
                    if (arrayTable) Expect(']');
                    SkipLineRest();
                    continue;
                }
                string key = ReadKeyPath('=');
                SkipWs();
                Expect('=');
                SkipWs();
                object v = ParseValue();
                result[section.Length == 0 ? key : section + "." + key] = v;
                SkipLineRest();
            }
        }

        string ReadKeyPath(char terminator)
        {
            var parts = new List<string>();
            while (true)
            {
                SkipWs();
                if (i >= s.Length) throw Err("неожиданный конец файла в имени ключа");
                string part;
                if (s[i] == '"') { i++; part = ReadBasicString(); }
                else if (s[i] == '\'') { i++; part = ReadLiteralString(); }
                else
                {
                    int st = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '-')) i++;
                    if (i == st) throw Err("ожидалось имя ключа");
                    part = s.Substring(st, i - st);
                }
                parts.Add(part);
                SkipWs();
                if (i < s.Length && s[i] == '.') { i++; continue; }
                if (i < s.Length && s[i] == terminator) break;
                throw Err("ожидалось '" + terminator + "'");
            }
            return string.Join(".", parts);
        }

        object ParseValue()
        {
            if (i >= s.Length) throw Err("ожидалось значение");
            char c = s[i];
            if (Starts("\"\"\"")) { i += 3; return ReadMultiBasic(); }
            if (Starts("'''")) { i += 3; return ReadMultiLiteral(); }
            if (c == '"') { i++; return ReadBasicString(); }
            if (c == '\'') { i++; return ReadLiteralString(); }
            if (c == '[')
            {
                i++;
                var list = new List<object>();
                while (true)
                {
                    SkipWsNlComments();
                    if (i >= s.Length) throw Err("незакрытый массив");
                    if (s[i] == ']') { i++; break; }
                    list.Add(ParseValue());
                    SkipWsNlComments();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    SkipWsNlComments();
                    Expect(']');
                    break;
                }
                return list;
            }
            if (c == '{')
            {
                i++;
                var dict = new Dictionary<string, object>();
                SkipWs();
                if (i < s.Length && s[i] == '}') { i++; return dict; }
                while (true)
                {
                    string key = ReadKeyPath('=');
                    SkipWs(); Expect('='); SkipWs();
                    dict[key] = ParseValue();
                    SkipWs();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect('}');
                    break;
                }
                return dict;
            }
            int st = i;
            while (i < s.Length && "\n,]}#".IndexOf(s[i]) < 0) i++;
            string tok = s.Substring(st, i - st).Trim();
            if (tok == "true") return true;
            if (tok == "false") return false;
            string num = tok.Replace("_", "");
            long l;
            if (long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return tok; // даты и прочее — как строка
        }

        string ReadBasicString()
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length || s[i] == '\n') throw Err("незакрытая строка");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\') ReadEscape(sb);
                else sb.Append(c);
            }
        }

        string ReadLiteralString()
        {
            int st = i;
            while (i < s.Length && s[i] != '\'' && s[i] != '\n') i++;
            if (i >= s.Length || s[i] != '\'') throw Err("незакрытая строка");
            string r = s.Substring(st, i - st);
            i++;
            return r;
        }

        string ReadMultiBasic()
        {
            if (i < s.Length && s[i] == '\n') i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw Err("незакрытая многострочная строка");
                if (Starts("\"\"\""))
                {
                    i += 3;
                    while (i < s.Length && s[i] == '"') { sb.Append('"'); i++; }
                    return sb.ToString();
                }
                char c = s[i++];
                if (c == '\\')
                {
                    int j = i;
                    while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
                    if (j < s.Length && s[j] == '\n')
                    {
                        i = j;
                        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                        continue;
                    }
                    ReadEscape(sb);
                }
                else sb.Append(c);
            }
        }

        string ReadMultiLiteral()
        {
            if (i < s.Length && s[i] == '\n') i++;
            int end = s.IndexOf("'''", i, StringComparison.Ordinal);
            if (end < 0) throw Err("незакрытая многострочная строка");
            while (end + 3 < s.Length && s[end + 3] == '\'') end++;
            string r = s.Substring(i, end - i);
            i = end + 3;
            return r;
        }

        void ReadEscape(StringBuilder sb)
        {
            if (i >= s.Length) throw Err("обрыв escape-последовательности");
            char e = s[i++];
            switch (e)
            {
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case 'e': sb.Append('\x1B'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u':
                case 'U':
                    {
                        int n = e == 'u' ? 4 : 8;
                        if (i + n > s.Length) throw Err("некорректный \\u");
                        int cp = int.Parse(s.Substring(i, n), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        sb.Append(char.ConvertFromUtf32(cp));
                        i += n;
                        break;
                    }
                default: throw Err("неизвестная escape-последовательность \\" + e);
            }
        }

        bool Starts(string t) { return string.CompareOrdinal(s, i, t, 0, t.Length) == 0; }

        void SkipWs() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r')) i++; }

        void SkipWsNl()
        {
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i])) i++;
                else if (s[i] == '#') { while (i < s.Length && s[i] != '\n') i++; }
                else break;
            }
        }

        void SkipWsNlComments() { SkipWsNl(); }

        void SkipLineRest()
        {
            SkipWs();
            if (i < s.Length && s[i] == '#') while (i < s.Length && s[i] != '\n') i++;
            if (i < s.Length && s[i] != '\n') throw Err("лишние символы в конце строки");
        }

        void Expect(char c)
        {
            if (i >= s.Length || s[i] != c) throw Err("ожидалось '" + c + "'");
            i++;
        }

        Exception Err(string msg)
        {
            int line = 1;
            for (int k = 0; k < i && k < s.Length; k++) if (s[k] == '\n') line++;
            return new FormatException("Ошибка TOML (строка " + line + "): " + msg);
        }
    }

    public static class TomlWriter
    {
        public static string Str(string v)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in v ?? "")
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7F) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string Arr(IEnumerable<string> items)
        {
            var parts = new List<string>();
            foreach (var x in items) parts.Add(Str(x));
            return "[" + string.Join(", ", parts) + "]";
        }

        public static string Bool(bool b) { return b ? "true" : "false"; }
    }
}
