using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PilotHeim.Assets
{
    /// <summary>
    /// Builds Unity materials for Titanfall material names from textures the user exported from
    /// their own install (assets/materials/&lt;name&gt;/&lt;name&gt;_col|_nml|_ilm.png). The shader is
    /// Valheim's own creature shader (taken from the Troll prefab) so lighting, fog, shadows and
    /// wetness match the world; falls back to Standard if that is unavailable.
    /// </summary>
    public static class TfMaterials
    {
        private static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        private static readonly Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();
        private static Material template;
        public static string Root;                 // .../extracted/assets/materials
        public static int TexturesLoaded, Missing;

        private static Material Template()
        {
            if (template != null) return template;
            var troll = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Troll") : null;
            var smr = troll != null ? troll.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            if (smr != null && smr.sharedMaterial != null)
            {
                template = new Material(smr.sharedMaterial);
                var sh = template.shader;
                var props = new List<string>();
                for (int i = 0; i < sh.GetPropertyCount(); i++) props.Add(sh.GetPropertyName(i));
                Plugin.Log.LogInfo($"Titanfall materials use Valheim shader '{sh.name}': {string.Join(",", props)}");
            }
            else
            {
                template = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse"));
                Plugin.Log.LogWarning("Troll material not found; Titanfall materials use " + template.shader.name);
            }
            return template;
        }

        /// <summary>Raw PHTEX bytes read ahead by the asset worker thread (path -> bytes).</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Preloaded =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();

        /// <summary>GPU-ready DXT mip chain from tools/tf2_textures.py: no decode, just an upload.</summary>
        private static Texture2D FromPhtex(string file, string name, bool linear)
        {
            if (!Preloaded.TryRemove(file, out var b)) { if (!File.Exists(file)) return null; b = File.ReadAllBytes(file); }
            if (b.Length < 20 || b[0] != 'P' || b[1] != 'H' || b[2] != 'T' || b[3] != '1') return null;
            var fmt = (TextureFormat)System.BitConverter.ToInt32(b, 4);
            int w = System.BitConverter.ToInt32(b, 8), h = System.BitConverter.ToInt32(b, 12), mips = System.BitConverter.ToInt32(b, 16);
            var t = new Texture2D(w, h, fmt, mips, linear) { name = name, wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
            var data = new byte[b.Length - 20];
            System.Buffer.BlockCopy(b, 20, data, 0, data.Length);
            t.LoadRawTextureData(data);
            t.Apply(false, true);
            return t;
        }

        private static Texture2D Tex(string mat, string suffix, bool linear)
        {
            string file = Path.Combine(Root ?? "", mat, mat + "_" + suffix + ".png");
            if (texCache.TryGetValue(file, out var t)) return t;
            t = FromPhtex(Path.ChangeExtension(file, ".phtex"), mat + "_" + suffix, linear);
            if (t != null) { TexturesLoaded++; texCache[file] = t; return t; }
            if (File.Exists(file))
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = mat + "_" + suffix, wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
                if (!t.LoadImage(File.ReadAllBytes(file), false)) { Object.Destroy(t); t = null; }
                else
                {
                    t.Compress(false);              // DXT: a 4K map drops from ~85 MB to ~21 MB of VRAM
                    t.Apply(false, true);           // upload and free the CPU copy
                    TexturesLoaded++;
                }
            }
            texCache[file] = t;
            return t;
        }

        /// <summary>Material for a Titanfall material path such as models\titans\buddy\BT_a_all.</summary>
        public static Material Get(string tfMaterial)
        {
            string name = Path.GetFileName(tfMaterial.Replace('\\', '/'));
            if (cache.TryGetValue(name, out var m)) return m;
            m = new Material(Template()) { name = "TF_" + name };
            var col = Tex(name, "col", false);
            if (col != null)
            {
                m.mainTexture = col;
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", col);
                if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            }
            else Missing++;
            var nml = Tex(name, "nml", true);
            if (nml != null && m.HasProperty("_BumpMap")) { m.SetTexture("_BumpMap", nml); m.EnableKeyword("_NORMALMAP"); }
            var ilm = Tex(name, "ilm", false);
            if (ilm != null && m.HasProperty("_EmissionMap"))
            {
                m.SetTexture("_EmissionMap", ilm);
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.white);
                m.EnableKeyword("_EMISSION");
            }
            cache[name] = m;
            return m;
        }
    }
}
