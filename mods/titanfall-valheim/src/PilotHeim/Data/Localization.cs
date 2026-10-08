using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PilotHeim.Data
{
    /// <summary>
    /// Titanfall 2's English strings (resource/r1_english.txt from the user's install, UTF-16 key/values),
    /// so the HUD shows "R-201 Carbine" for #WPN_RSPN101 like the game does.
    /// </summary>
    public static class Localization
    {
        private static readonly Dictionary<string, string> tokens = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        // one "key" "value" per line; values may contain escaped quotes, so parse per line, not file-wide
        private static readonly Regex Pair = new Regex(@"^\s*""([^""]+)""\s+""(.*)""\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        public static int Count => tokens.Count;

        public static void Load(string path)
        {
            if (!File.Exists(path)) return;
            var bytes = File.ReadAllBytes(path);
            string text = bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes)
                        : bytes.Length > 1 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(bytes)
                        : Encoding.UTF8.GetString(bytes);
            foreach (Match m in Pair.Matches(text.Replace("\r", "")))
                if (!tokens.ContainsKey(m.Groups[1].Value)) tokens[m.Groups[1].Value] = m.Groups[2].Value.Replace("\\\"", "\"");
        }

        /// <summary>"#WPN_RSPN101" -> "R-201 Carbine"; falls back to a tidied token.</summary>
        public static string Get(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            string key = token.TrimStart('#');
            if (tokens.TryGetValue(key, out var v) && v.Length > 0) return v;
            return key.Replace("WPN_", "").Replace("TITAN_", "").Replace('_', ' ');
        }
    }
}
