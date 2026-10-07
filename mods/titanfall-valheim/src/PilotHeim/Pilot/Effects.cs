using System.Collections.Generic;
using UnityEngine;

namespace PilotHeim.Pilot
{
    /// <summary>
    /// Visual and audio feedback. Uses Valheim's own effect prefabs when they exist
    /// (resolved by name at runtime) and simple procedural lines/lights otherwise.
    /// Titanfall audio replaces the sound slots once the asset pipeline (phase 5)
    /// has produced them; see AudioBank.
    /// </summary>
    internal static class Effects
    {
        private static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        private static bool initialised;
        private static Material lineMat;

        private static readonly Dictionary<string, string[]> candidates = new Dictionary<string, string[]>
        {
            ["impact"] = new[] { "vfx_arrowhit", "vfx_HitSparks", "vfx_clubhit" },
            ["explosion"] = new[] { "vfx_GoblinShaman_Explosion", "fx_DvergerMage_Fire_hit", "vfx_bonemass_aoe", "fx_eikthyr_stomp" },
            ["sfx_fire"] = new[] { "sfx_crossbow_fire", "sfx_bow_fire" },
            ["sfx_impact"] = new[] { "sfx_arrow_hit" },
            ["sfx_explosion"] = new[] { "sfx_goblinbrute_groundslam", "sfx_troll_rock_destroyed", "sfx_rock_destroyed" },
        };

        public static void Init()
        {
            if (initialised || ZNetScene.instance == null) return;
            initialised = true;
            foreach (var kv in candidates)
                foreach (var name in kv.Value)
                {
                    var go = ZNetScene.instance.GetPrefab(name);
                    if (go != null) { prefabs[kv.Key] = go; break; }
                }
            Plugin.Log.LogInfo("Effects resolved: " + string.Join(", ", prefabs.Keys));
        }

        private static void Spawn(string key, Vector3 pos, Quaternion rot)
        {
            if (prefabs.TryGetValue(key, out var go) && go != null) Object.Instantiate(go, pos, rot);
        }

        public static LineRenderer NewLine(float width, Color c)
        {
            if (lineMat == null)
            {
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                lineMat = sh != null ? new Material(sh) : null;
            }
            var go = new GameObject("PilotHeim_Line");
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.startWidth = width; lr.endWidth = width * 0.6f;
            if (lineMat != null) lr.material = lineMat;
            lr.startColor = c; lr.endColor = new Color(c.r, c.g, c.b, 0f);
            return lr;
        }

        public static void Tracer(Vector3 from, Vector3 to)
        {
            var lr = NewLine(0.02f, new Color(1f, 0.9f, 0.6f, 0.85f));
            lr.SetPosition(0, from); lr.SetPosition(1, to);
            Object.Destroy(lr.gameObject, 0.05f);
        }

        public static void Muzzle(Vector3 pos, Vector3 dir)
        {
            var go = new GameObject("PilotHeim_Muzzle");
            go.transform.position = pos + dir * 0.2f;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.range = 4f; l.intensity = 3f; l.color = new Color(1f, 0.8f, 0.5f);
            Object.Destroy(go, 0.04f);
        }

        public static void Impact(Vector3 pos, Vector3 normal)
        {
            Spawn("impact", pos, Quaternion.LookRotation(normal));
            Spawn("sfx_impact", pos, Quaternion.identity);
        }

        public static void Explosion(Vector3 pos, float radius)
        {
            Spawn("explosion", pos, Quaternion.identity);
            Spawn("sfx_explosion", pos, Quaternion.identity);
            var go = new GameObject("PilotHeim_Blast");
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.range = radius * 1.5f; l.intensity = 6f; l.color = new Color(1f, 0.6f, 0.3f);
            Object.Destroy(go, 0.15f);
            GameCamera.instance?.AddShake(pos, radius * 3f, 2f, false);
        }

        public static void Pulse(Vector3 pos, float radius)
        {
            var lr = NewLine(0.05f, new Color(1f, 0.55f, 0.1f, 0.9f));
            const int n = 48;
            lr.positionCount = n + 1;
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                lr.SetPosition(i, pos + new Vector3(Mathf.Cos(a), 0.05f, Mathf.Sin(a)) * radius * 0.25f);
            }
            Object.Destroy(lr.gameObject, 0.4f);
        }

        public static void Sound(string slot, Vector3 pos)
        {
            if (AudioBank.TryPlay(slot, pos)) return;
            if (slot == "fire") Spawn("sfx_fire", pos, Quaternion.identity);
        }
    }

    /// <summary>Titanfall 2 sounds exported locally by the asset pipeline (phase 5). Empty until then.</summary>
    internal static class AudioBank
    {
        public static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        public static bool TryPlay(string slot, Vector3 pos)
        {
            if (!Clips.TryGetValue(slot, out var clip) || clip == null) return false;
            AudioSource.PlayClipAtPoint(clip, pos, 1f);
            return true;
        }
    }
}
