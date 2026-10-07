using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PilotHeim.Data
{
    /// <summary>
    /// A resolved Titanfall 2 player settings class (e.g. pilot_grapple_male):
    /// the #base chain is flattened so each section (global / stand / crouch)
    /// holds the most-derived value of every key. ClassMods are ignored.
    /// </summary>
    public sealed class PlayerSettings
    {
        public readonly string Name;
        public readonly List<string> Chain = new List<string>();
        private readonly Dictionary<string, Dictionary<string, string>> sections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private PlayerSettings(string name) { Name = name; }

        public static PlayerSettings Load(string playersDir, string setName)
        {
            var ps = new PlayerSettings(setName);
            ps.LoadRecursive(playersDir, setName + ".set", 0);
            return ps;
        }

        private void LoadRecursive(string dir, string file, int depth)
        {
            if (depth > 16) throw new InvalidDataException("#base chain too deep at " + file);
            string path = Path.Combine(dir, file);
            if (!File.Exists(path)) throw new FileNotFoundException("Titanfall 2 settings file missing", path);
            var bases = new List<string>();
            var root = KeyValues.ParseFile(path, bases);
            foreach (var b in bases) LoadRecursive(dir, b, depth + 1);      // bases first, then override
            Chain.Add(Path.GetFileNameWithoutExtension(file));
            foreach (var cls in root.Children)                                  // "pilot_mp" { ... }
            {
                if (cls.Value != null) continue;
                foreach (var sec in cls.Children)                               // "global" / "stand" / "crouch"
                {
                    if (sec.Value != null) continue;
                    if (!sections.TryGetValue(sec.Key, out var dict))
                        sections[sec.Key] = dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in sec.Children)
                        if (kv.Value != null) dict[kv.Key] = kv.Value;
                }
            }
        }

        public bool Has(string section, string key) =>
            sections.TryGetValue(section, out var d) && d.ContainsKey(key);

        public string Str(string section, string key, string fallback = null) =>
            sections.TryGetValue(section, out var d) && d.TryGetValue(key, out var v) ? v : fallback;

        /// <summary>Float value; `required` keys throw so a missing value is never silently guessed.</summary>
        public float F(string section, string key, float? fallback = null)
        {
            var s = Str(section, key);
            if (s == null)
            {
                if (fallback.HasValue) return fallback.Value;
                throw new KeyNotFoundException($"{Name}: '{section}.{key}' not found in {string.Join(" <- ", Chain)}");
            }
            return float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        public bool B(string section, string key, bool fallback = false)
        {
            var s = Str(section, key);
            if (s == null) return fallback;
            s = s.Trim();
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public IEnumerable<KeyValuePair<string, string>> All(string section) =>
            sections.TryGetValue(section, out var d) ? d : (IEnumerable<KeyValuePair<string, string>>)Array.Empty<KeyValuePair<string, string>>();
    }
}
