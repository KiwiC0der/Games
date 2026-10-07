using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PilotHeim.Data
{
    /// <summary>
    /// Minimal Valve/Respawn KeyValues reader for Titanfall 2 .set and weapon .txt
    /// files: quoted or bare tokens, // comments, nested blocks and #base includes.
    /// </summary>
    public sealed class KvNode
    {
        public string Key;
        public string Value;                       // null for blocks
        public readonly List<KvNode> Children = new List<KvNode>();

        public KvNode Child(string key)
        {
            for (int i = Children.Count - 1; i >= 0; i--)
                if (string.Equals(Children[i].Key, key, StringComparison.OrdinalIgnoreCase)) return Children[i];
            return null;
        }

        public string Get(string key) => Child(key)?.Value;
    }

    public static class KeyValues
    {
        public static KvNode ParseFile(string path, List<string> bases = null)
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            return Parse(text, bases);
        }

        public static KvNode Parse(string text, List<string> bases = null)
        {
            var tokens = Tokenize(text, bases);
            int i = 0;
            var root = new KvNode { Key = "<root>" };
            ReadBlock(tokens, ref i, root);
            return root;
        }

        private static void ReadBlock(List<string> t, ref int i, KvNode parent)
        {
            while (i < t.Count)
            {
                string tok = t[i++];
                if (tok == "}") return;
                if (tok == "{") { ReadBlock(t, ref i, new KvNode()); continue; }   // stray block
                var node = new KvNode { Key = tok };
                if (i < t.Count && t[i] == "{")
                {
                    i++;
                    ReadBlock(t, ref i, node);
                }
                else if (i < t.Count && t[i] != "}")
                {
                    node.Value = t[i++];
                    // conditional suffix like [$X360] is not used in these files; skip if present
                    if (i < t.Count && t[i].StartsWith("[$")) i++;
                }
                parent.Children.Add(node);
            }
        }

        private static List<string> Tokenize(string s, List<string> bases)
        {
            var list = new List<string>();
            int n = s.Length, i = 0;
            while (i < n)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < n && s[i + 1] == '/') { while (i < n && s[i] != '\n') i++; continue; }
                if (c == '#')
                {
                    // #base "file"  /  #include "file"
                    int e = s.IndexOf('\n', i); if (e < 0) e = n;
                    string line = s.Substring(i, e - i).Trim();
                    int q1 = line.IndexOf('"'), q2 = line.LastIndexOf('"');
                    if (bases != null && q1 >= 0 && q2 > q1 && line.StartsWith("#base", StringComparison.OrdinalIgnoreCase))
                        bases.Add(line.Substring(q1 + 1, q2 - q1 - 1));
                    i = e;
                    continue;
                }
                if (c == '{' || c == '}') { list.Add(c.ToString()); i++; continue; }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < n && s[i] != '"')
                    {
                        if (s[i] == '\\' && i + 1 < n) { sb.Append(s[i + 1]); i += 2; continue; }
                        sb.Append(s[i++]);
                    }
                    i++;
                    list.Add(sb.ToString());
                    continue;
                }
                int start = i;
                while (i < n && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"') i++;
                list.Add(s.Substring(start, i - start));
            }
            return list;
        }
    }
}
