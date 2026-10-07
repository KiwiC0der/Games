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

        private static Texture2D Tex(string mat, string suffix, bool linear)
        {
            string file = Path.Combine(Root ?? "", mat, mat + "_" + suffix + ".png");
            if (texCache.TryGetValue(file, out var t)) return t;
            t = null;
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
