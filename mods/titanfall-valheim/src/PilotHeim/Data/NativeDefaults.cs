using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PilotHeim.Data
{
    /// <summary>
    /// Defaults compiled into Titanfall 2's server.dll (player-settings schema,
    /// stance schema and movement ConVars), read from the locally generated
    /// native_defaults.json (see tools/build_native_defaults.py).
    /// </summary>
    public sealed class NativeDefaults
    {
        public readonly Dictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Stance = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ConVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static NativeDefaults Load(string path)
        {
            var nd = new NativeDefaults();
            var root = MiniJson.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>
                       ?? throw new InvalidDataException("native_defaults.json: root is not an object");
            Fill(root, "settings", nd.Settings);
            Fill(root, "stance", nd.Stance);
            Fill(root, "convars", nd.ConVars);
            return nd;
        }

        private static void Fill(Dictionary<string, object> root, string key, Dictionary<string, string> into)
        {
            if (!root.TryGetValue(key, out var o) || !(o is Dictionary<string, object> d))
                throw new InvalidDataException("native_defaults.json: missing '" + key + "'");
            foreach (var kv in d) into[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
        }

        public float ConVar(string name)
        {
            if (!ConVars.TryGetValue(name, out var s))
                throw new KeyNotFoundException("ConVar '" + name + "' not in native_defaults.json");
            return ParseF(s);
        }

        internal static float ParseF(string s) =>
            float.Parse(s.Trim().TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Just enough JSON for flat objects of strings/numbers.</summary>
    internal static class MiniJson
    {
        public static object Parse(string s) { int i = 0; var v = Value(s, ref i); return v; }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            int st = i;
            while (i < s.Length && ",}] \r\n\t".IndexOf(s[i]) < 0) i++;
            string tok = s.Substring(st, i - st);
            if (tok == "true") return true;
            if (tok == "false") return false;
            if (tok == "null") return null;
            return tok;
        }

        private static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++;
            while (true)
            {
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                string k = Str(s, ref i);
                Ws(s, ref i); i++;                 // ':'
                d[k] = Value(s, ref i);
                Ws(s, ref i);
                if (s[i] == ',') i++;
            }
        }

        private static List<object> Arr(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            while (true)
            {
                Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                if (s[i] == ',') i++;
            }
        }

        private static string Str(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (s[i] != '"')
            {
                if (s[i] == '\\')
                {
                    i++;
                    char e = s[i];
                    if (e == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; }
                    else sb.Append(e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e);
                    i++;
                    continue;
                }
                sb.Append(s[i++]);
            }
            i++;
            return sb.ToString();
        }
    }
}
