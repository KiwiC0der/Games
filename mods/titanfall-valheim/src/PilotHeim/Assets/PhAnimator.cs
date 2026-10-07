using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PilotHeim.Assets
{
    /// <summary>Baked Titanfall clips (PHA1): per frame, per bone local position + rotation in Unity space.</summary>
    public sealed class PhClip
    {
        public string Key;
        public float Fps;
        public bool Loop;
        public float SpeedUnits;       // Titanfall units/s of the jx_c_start motion tracker (0 = in place)
        public float Mark;             // event time in seconds (hot drop: ground impact)
        public int Frames, Bones;
        public float[] Data;           // frames * bones * 7
        public float Length => Mathf.Max(1, Frames - 1) / Fps;

        public static Dictionary<string, PhClip> LoadAll(string path)
        {
            var clips = new Dictionary<string, PhClip>();
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                string magic = Encoding.ASCII.GetString(r.ReadBytes(4));
                if (magic != "PHA1" && magic != "PHA2") throw new InvalidDataException(path + ": not PHA1/PHA2");
                bool v2 = magic == "PHA2";
                int nb = r.ReadInt32(), nc = r.ReadInt32();
                for (int c = 0; c < nc; c++)
                {
                    var k = new PhClip { Key = Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16())), Bones = nb };
                    k.Fps = r.ReadSingle(); k.Loop = r.ReadInt32() != 0; k.SpeedUnits = r.ReadSingle();
                    if (v2) k.Mark = r.ReadSingle();
                    k.Frames = r.ReadInt32();
                    k.Data = new float[k.Frames * nb * 7];
                    var raw = r.ReadBytes(k.Data.Length * 4);
                    System.Buffer.BlockCopy(raw, 0, k.Data, 0, raw.Length);
                    clips[k.Key] = k;
                }
            }
            return clips;
        }

        public void Sample(float time, int bone, out Vector3 pos, out Quaternion rot)
        {
            float f = time * Fps;
            if (Loop) { float last = Frames - 1; f = last > 0 ? Mathf.Repeat(f, last) : 0f; }
            else f = Mathf.Clamp(f, 0f, Frames - 1);
            int a = Mathf.Min((int)f, Frames - 1), b = Mathf.Min(a + 1, Frames - 1);
            float t = f - a;
            int ia = (a * Bones + bone) * 7, ib = (b * Bones + bone) * 7;
            var d = Data;
            pos = new Vector3(Mathf.Lerp(d[ia], d[ib], t), Mathf.Lerp(d[ia + 1], d[ib + 1], t), Mathf.Lerp(d[ia + 2], d[ib + 2], t));
            var qa = new Quaternion(d[ia + 3], d[ia + 4], d[ia + 5], d[ia + 6]);
            var qb = new Quaternion(d[ib + 3], d[ib + 4], d[ib + 5], d[ib + 6]);
            rot = Quaternion.Slerp(qa, qb, t);
        }
    }

    /// <summary>
    /// Plays baked Titanfall clips on a bone hierarchy with crossfades. Runs in LateUpdate so it
    /// owns the bones after any Unity Animator, and costs ~200 bone writes per frame.
    /// </summary>
    public sealed class PhAnimator : MonoBehaviour
    {
        public Transform[] Bones;
        public Dictionary<string, PhClip> Clips;
        public string Current { get; private set; }
        public float Rate = 1f;
        public float Time01 => cur != null ? Mathf.Clamp01(time / Mathf.Max(0.01f, cur.Length)) : 0f;

        private PhClip cur, prev;
        private float time, prevTime, fade, fadeLen;

        public bool Has(string key) => Clips != null && Clips.ContainsKey(key);

        public void Play(string key, float crossfade = 0.2f, bool restart = false, float startTime = 0f)
        {
            if (Clips == null || !Clips.TryGetValue(key, out var c)) return;
            if (c == cur && !restart) return;
            prev = cur; prevTime = time;
            cur = c; time = Mathf.Max(0f, startTime); Current = key;
            fadeLen = prev != null ? Mathf.Max(0.01f, crossfade) : 0f; fade = 0f;
        }

        public bool Finished => cur != null && !cur.Loop && time >= cur.Length;

        private void LateUpdate()
        {
            if (cur == null || Bones == null) return;
            float dt = UnityEngine.Time.deltaTime;
            time += dt * Rate;
            if (prev != null) { prevTime += dt * Rate; fade += dt; if (fade >= fadeLen) prev = null; }
            float w = prev != null ? Mathf.SmoothStep(0f, 1f, fade / fadeLen) : 1f;
            for (int i = 0; i < Bones.Length; i++)
            {
                var b = Bones[i];
                if (b == null) continue;
                cur.Sample(time, i, out var p, out var q);
                if (prev != null)
                {
                    prev.Sample(prevTime, i, out var pp, out var pq);
                    p = Vector3.Lerp(pp, p, w); q = Quaternion.Slerp(pq, q, w);
                }
                b.localPosition = p; b.localRotation = q;
            }
        }
    }
}
