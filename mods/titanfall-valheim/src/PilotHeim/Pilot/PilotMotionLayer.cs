using System.Collections.Generic;
using PilotHeim.Assets;
using UnityEngine;

namespace PilotHeim.Pilot
{
    /// <summary>
    /// Titanfall animation layer over the Valheim retarget. With the pilot gun drawn, or in any state
    /// Valheim has no animation for (wallrun, slide, jet jumps, grapple), the pilot plays the real
    /// Titanfall pilot clips (pilot_light_core rifle set); holstered on the ground it blends back to
    /// Valheim so tools, building and melee animate as in Valheim. Also holds the current pilot gun
    /// (the weapon's world model, gripped like Titanfall: its r_hand_ik frame on ja_r_propHand).
    /// </summary>
    public sealed class PilotMotionLayer
    {
        private const float PilotRunSpeed = 173.5f;     // u/s, Titanfall pilot run (the clips' authored speed)
        private const float BlendRate = 6f;             // per second toward the Titanfall layer and back

        private readonly PilotController pc;
        private readonly Transform[] bones;
        private readonly Transform root;
        private readonly Dictionary<string, PhClip> clips;
        private PhClip cur, prev;
        private float t, prevT, fade, fadeLen, weight;
        private float rate = 1f;
        public string Current { get; private set; }
        public float Weight => weight;

        // held gun
        private Transform hand, gunRoot, muzzle;
        private string gunId;
        private readonly List<Renderer> hiddenValheimItems = new List<Renderer>();

        public PilotMotionLayer(PilotController pc, Transform[] bones, Transform root, Dictionary<string, PhClip> clips)
        {
            this.pc = pc; this.bones = bones; this.root = root; this.clips = clips;
            foreach (var b in bones) if (b.name == "ja_r_propHand") hand = b;
        }

        private void Play(string key, float crossfade = 0.15f)
        {
            if (clips == null || !clips.TryGetValue(key, out var c) || c == cur) return;
            prev = cur; prevT = t; cur = c; t = 0f; Current = key;
            fadeLen = prev != null ? crossfade : 0f; fade = 0f;
        }

        private string Choose(PilotMotor m, out float r)
        {
            r = 1f;
            float since = Time.time - m.LastEventTime;
            if (m.Grapple != null && m.Grapple.Attached) return "grapple";
            if (m.Wallrunning)
            {
                Vector3 local = root.InverseTransformDirection(m.WallNormal);
                float up = m.Vel.y, along = new Vector3(m.Vel.x, 0f, m.Vel.z).magnitude;
                if (up > along * 1.2f) return "wallrun_up";
                return local.x > 0f ? "wallrun_l" : "wallrun_r";         // wall normal points right -> wall on the left
            }
            if (m.Sliding) return "slide";
            Vector3 v = root.InverseTransformDirection(new Vector3(m.Vel.x, 0f, m.Vel.z));
            if (!m.OnGround)
            {
                if (m.LastEvent == "doublejump" && since < 0.6f) return "doublejump";
                if ((m.LastEvent == "jump" || m.LastEvent == "skip" || m.LastEvent == "walljump") && since < 0.35f) return "jump";
                return v.z < -40f ? "float_b" : "float_f";
            }
            if (m.LastEvent == "land" && since < 0.25f) return "land";
            float speed = v.magnitude;
            if (speed < 15f) return "idle";
            string dir = Mathf.Abs(v.z) >= Mathf.Abs(v.x) ? (v.z >= 0f ? "f" : "b") : (v.x >= 0f ? "r" : "l");
            if (speed < 95f) { r = Mathf.Clamp(speed / 90f, 0.5f, 1.4f); return "walk_" + dir; }
            r = Mathf.Clamp(speed / PilotRunSpeed, 0.6f, 1.8f);
            return "run_" + dir;
        }

        private bool WantsTitanfall(PilotMotor m)
        {
            if (!pc.Active || clips == null) return false;
            if (pc.Arsenal != null && pc.Arsenal.Drawn) return true;
            if (m.Wallrunning || m.Sliding || (m.Grapple != null && m.Grapple.Attached)) return true;
            if (!m.OnGround && (m.LastEvent == "doublejump" || m.LastEvent == "walljump" || m.LastEvent.StartsWith("wallrun")
                                || m.LastEvent.StartsWith("grapple"))) return true;
            return false;
        }

        /// <summary>Called after the Valheim retarget each LateUpdate.</summary>
        public void Apply(float dt)
        {
            var m = pc.Motor;
            UpdateGun();
            if (m == null) return;
            bool want = WantsTitanfall(m);
            weight = Mathf.MoveTowards(weight, want ? 1f : 0f, BlendRate * dt);
            if (weight <= 0f) { cur = prev = null; Current = null; return; }
            var key = Choose(m, out var r);
            Play(key);
            rate = Mathf.Lerp(rate, r, 10f * dt);
            if (cur == null) return;
            t += dt * rate;
            if (prev != null) { prevT += dt * rate; fade += dt; if (fade >= fadeLen) prev = null; }
            float fw = prev != null ? Mathf.SmoothStep(0f, 1f, fade / fadeLen) : 1f;
            for (int i = 0; i < bones.Length && i < cur.Bones; i++)
            {
                var b = bones[i];
                cur.Sample(t, i, out var p, out var q);
                if (prev != null) { prev.Sample(prevT, i, out var pp, out var pq); p = Vector3.Lerp(pp, p, fw); q = Quaternion.Slerp(pq, q, fw); }
                if (weight >= 1f) { b.localPosition = p; b.localRotation = q; }
                else { b.localPosition = Vector3.Lerp(b.localPosition, p, weight); b.localRotation = Quaternion.Slerp(b.localRotation, q, weight); }
            }
        }

        // ------------------------------------------------------------------ the gun in the pilot's hand
        private void UpdateGun()
        {
            var a = pc.Arsenal;
            bool drawn = a != null && a.Drawn && pc.Active && a.Weapon != null;
            string want = drawn ? a.Weapon.Id : null;
            if (want != gunId)
            {
                if (gunRoot != null) Object.Destroy(gunRoot.gameObject);
                gunRoot = null; muzzle = null; gunId = want;
                if (want != null && hand != null)
                {
                    var model = AssetLibrary.Weapon(want);
                    if (model != null)
                    {
                        var gb = model.Build(hand, TfMaterials.Get, out _);
                        gunRoot = gb[0].parent;
                        int grip = System.Array.IndexOf(model.BoneNames, "r_hand_ik");
                        if (grip >= 0) { var bp = model.BindPoses[grip]; gunRoot.localPosition = bp.GetColumn(3); gunRoot.localRotation = bp.rotation; }
                        foreach (var b in gb) if (b.name == "muzzle_flash") muzzle = b;
                        foreach (var rr in gunRoot.GetComponentsInChildren<Renderer>()) rr.gameObject.layer = hand.gameObject.layer;
                    }
                }
                if (a != null) a.MuzzleProvider = muzzle != null ? (System.Func<Vector3>)(() => muzzle.position) : null;
            }
            // the Valheim item in hand is put away while the pilot gun is out
            if (drawn) HideValheimHandItems(); else RestoreValheimHandItems();
        }

        private void HideValheimHandItems()
        {
            var ve = pc.Player.m_visEquipment;
            if (ve == null) return;
            foreach (var go in new[] { ve.m_rightItemInstance, ve.m_leftItemInstance })
            {
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    if (r.enabled) { r.enabled = false; hiddenValheimItems.Add(r); }
            }
        }

        private void RestoreValheimHandItems()
        {
            if (hiddenValheimItems.Count == 0) return;
            foreach (var r in hiddenValheimItems) if (r != null) r.enabled = true;
            hiddenValheimItems.Clear();
        }

        public bool HasGun => gunRoot != null;

        public void Destroy()
        {
            RestoreValheimHandItems();
            if (gunRoot != null) Object.Destroy(gunRoot.gameObject);
            if (pc != null && pc.Arsenal != null) pc.Arsenal.MuzzleProvider = null;
        }
    }
}
