using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Audio;

namespace PilotHeim.Assets
{
    /// <summary>
    /// Titanfall 2 sounds exported from the user's install into assets/sounds/&lt;slot&gt;/*.wav
    /// (tools/tf2_sounds.py picks the waves per gameplay slot). WAVs are decoded on a worker thread,
    /// multichannel mixes are folded to mono for 3D placement, and playback goes through Valheim's
    /// own SFX mixer group so the in-game volume settings apply.
    /// </summary>
    public static class TfAudio
    {
        private sealed class Pcm { public string Name; public float[] Data; public int Rate; }
        private static readonly Dictionary<string, List<Pcm>> pcm = new Dictionary<string, List<Pcm>>();
        private static readonly Dictionary<string, List<AudioClip>> clips = new Dictionary<string, List<AudioClip>>();
        private static AudioMixerGroup mixer;
        private static bool mixerResolved;
        public static int Loaded { get; private set; }
        public static string Status = "no sounds";

        public static void Preload(string assetsDir)
        {
            string root = Path.Combine(assetsDir, "sounds");
            if (!Directory.Exists(root)) return;
            Task.Run(() =>
            {
                try
                {
                    int n = 0;
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        // slot folders use '-' for ':' (fire-mp_weapon_rspn101 -> fire:mp_weapon_rspn101)
                        string slot = Path.GetFileName(dir).Replace('-', ':');
                        var list = new List<Pcm>();
                        foreach (var f in Directory.GetFiles(dir, "*.wav"))
                        {
                            var p = Decode(f);
                            if (p != null) { list.Add(p); n++; }
                        }
                        if (list.Count > 0) lock (pcm) pcm[slot] = list;
                    }
                    Loaded = n;
                    Status = $"{n} Titanfall sounds in {pcm.Count} slots";
                }
                catch (Exception e) { Status = "sound load failed: " + e.Message; }
            });
        }

        /// <summary>RIFF/WAVE, PCM 16/24-bit or float32, any channel count; folded to mono.</summary>
        private static Pcm Decode(string path)
        {
            var b = File.ReadAllBytes(path);
            if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') return null;
            int pos = 12, fmt = 0, ch = 0, rate = 0, bits = 0, dataOff = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int len = BitConverter.ToInt32(b, pos + 4);
                if (id == "fmt ")
                {
                    fmt = BitConverter.ToUInt16(b, pos + 8); ch = BitConverter.ToUInt16(b, pos + 10);
                    rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToUInt16(b, pos + 22);
                    if (fmt == 0xFFFE && len >= 26) fmt = BitConverter.ToUInt16(b, pos + 32);   // WAVE_FORMAT_EXTENSIBLE
                }
                else if (id == "data") { dataOff = pos + 8; dataLen = Math.Min(len, b.Length - dataOff); break; }
                pos += 8 + len + (len & 1);
            }
            if (dataOff < 0 || ch <= 0 || rate <= 0) return null;
            int bps = bits / 8, frames = dataLen / (bps * ch);
            var mono = new float[frames];
            float norm = 1f / ch;
            for (int i = 0; i < frames; i++)
            {
                float acc = 0f;
                for (int c = 0; c < ch; c++)
                {
                    int o = dataOff + (i * ch + c) * bps;
                    float v;
                    if (fmt == 3 && bits == 32) v = BitConverter.ToSingle(b, o);
                    else if (bits == 16) v = BitConverter.ToInt16(b, o) / 32768f;
                    else if (bits == 24) v = ((b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16))) / 8388608f;
                    else if (bits == 8) v = (b[o] - 128) / 128f;
                    else return null;
                    acc += v;
                }
                mono[i] = acc * norm;
            }
            // folding 5.1 to mono loses level; restore peak to the source's loudest channel peak
            float peak = 0f;
            for (int i = 0; i < frames; i++) peak = Math.Max(peak, Math.Abs(mono[i]));
            if (ch > 1 && peak > 1e-4f && peak < 0.5f) { float g = Math.Min(2f, 0.7f / peak); for (int i = 0; i < frames; i++) mono[i] *= g; }
            return new Pcm { Name = Path.GetFileNameWithoutExtension(path), Data = mono, Rate = rate };
        }

        private static List<AudioClip> ClipsFor(string slot)
        {
            if (clips.TryGetValue(slot, out var list)) return list;
            List<Pcm> src;
            lock (pcm) { if (!pcm.TryGetValue(slot, out src)) return null; }
            list = new List<AudioClip>();
            foreach (var p in src)
            {
                var c = AudioClip.Create("TF_" + p.Name, p.Data.Length, 1, p.Rate, false);
                c.SetData(p.Data, 0);
                list.Add(c);
            }
            clips[slot] = list;
            return list;
        }

        private static AudioMixerGroup Mixer()
        {
            if (mixerResolved) return mixer;
            mixerResolved = true;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("sfx_arrow_hit") : null;
            var src = prefab != null ? prefab.GetComponentInChildren<AudioSource>(true) : null;
            mixer = src != null ? src.outputAudioMixerGroup : null;
            return mixer;
        }

        public static bool Has(string slot) { lock (pcm) return pcm.ContainsKey(slot); }

        /// <summary>Plays a random variant of the slot at a world position. Returns false if the slot is empty.</summary>
        public static bool Play(string slot, Vector3 pos, float volume = 1f, float maxDistance = 60f)
        {
            var list = ClipsFor(slot);
            if (list == null || list.Count == 0) return false;
            var clip = list[UnityEngine.Random.Range(0, list.Count)];
            var go = new GameObject("TF_sfx_" + slot);
            go.transform.position = pos;
            var a = go.AddComponent<AudioSource>();
            a.clip = clip;
            a.outputAudioMixerGroup = Mixer();
            a.spatialBlend = 1f;
            a.rolloffMode = AudioRolloffMode.Linear;
            a.minDistance = 2f;
            a.maxDistance = maxDistance;
            a.volume = volume * Plugin.SoundVolume.Value;
            a.pitch = UnityEngine.Random.Range(0.97f, 1.03f);
            a.Play();
            UnityEngine.Object.Destroy(go, clip.length + 0.1f);
            return true;
        }
    }
}
