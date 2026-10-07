using System.IO;
using PilotHeim.Assets;
using PilotHeim.Data;
using UnityEngine;

namespace PilotHeim.Titan
{
    /// <summary>
    /// The Titan's body. With assets exported from the user's Titanfall 2 install this is the real
    /// BT-7274 model with its XO-16 (bone-merged on ja_c_propGun) driven by BT's own locomotion and
    /// dash clips; otherwise a primitive stand-in at the real hull size. Exposes the gun muzzle and
    /// rocket pod either way.
    /// </summary>
    public sealed class TitanVisual : MonoBehaviour
    {
        public Transform Muzzle, RocketPod;
        public bool RealModel { get; private set; }
        public PhAnimator Anim { get; private set; }
        private Transform modelRoot;
        private Transform legL, legR, shinL, shinR, torso, armR, armL;
        private float phase, torsoY;
        private static Material baseMat;

        private static readonly Color Olive = new Color(0.36f, 0.38f, 0.29f);
        private static readonly Color Dark = new Color(0.16f, 0.17f, 0.17f);
        private static readonly Color Militia = new Color(0.92f, 0.55f, 0.12f);
        private static readonly Color Eye = new Color(0.25f, 0.85f, 1f);

        public static Material Mat(Color c)
        {
            if (baseMat == null)
            {
                Shader sh = null;
                foreach (var n in new[] { "Custom/Piece", "Standard", "Legacy Shaders/Diffuse", "Unlit/Color", "Sprites/Default" })
                    if ((sh = Shader.Find(n)) != null) break;
                baseMat = new Material(sh);
                if (baseMat.HasProperty("_MainTex")) baseMat.SetTexture("_MainTex", Texture2D.whiteTexture);
                Plugin.Log.LogInfo("Titan stand-in material shader: " + (sh != null ? sh.name : "none"));
            }
            var m = new Material(baseMat);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            m.color = c;
            return m;
        }

        private static Transform Part(Transform parent, string name, PrimitiveType type, Vector3 pos, Vector3 scale, Color c, Vector3 euler = default)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());          // the Titan's capsule does the colliding
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.GetComponent<Renderer>().sharedMaterial = Mat(c);
            return go.transform;
        }

        private static Transform Pivot(Transform parent, string name, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            return t;
        }

        public static TitanVisual Build(Transform root, TitanTuning tuning)
        {
            var real = TryBuildReal(root);
            if (real != null) return real;
            float h = tuning.HullHeight * PilotTuning.MetersPerUnit;      // ~5.97 m
            float w = tuning.HullRadius * 2f * PilotTuning.MetersPerUnit; // ~3.05 m
            var holder = new GameObject("PilotHeim_TitanVisual").transform;
            holder.SetParent(root, false);
            var v = holder.gameObject.AddComponent<TitanVisual>();
            float hip = h * 0.42f;

            // legs (pivot at the hip)
            v.legL = Pivot(holder, "legL", new Vector3(-w * 0.22f, hip, 0f));
            v.legR = Pivot(holder, "legR", new Vector3(w * 0.22f, hip, 0f));
            foreach (var leg in new[] { v.legL, v.legR })
            {
                Part(leg, "thigh", PrimitiveType.Cube, new Vector3(0f, -hip * 0.27f, 0f), new Vector3(w * 0.22f, hip * 0.55f, w * 0.26f), Olive);
                var knee = Pivot(leg, "knee", new Vector3(0f, -hip * 0.55f, 0f));
                Part(knee, "shin", PrimitiveType.Cube, new Vector3(0f, -hip * 0.22f, 0.05f), new Vector3(w * 0.2f, hip * 0.45f, w * 0.24f), Dark);
                Part(knee, "foot", PrimitiveType.Cube, new Vector3(0f, -hip * 0.43f, 0.18f), new Vector3(w * 0.26f, hip * 0.08f, w * 0.42f), Dark);
                if (leg == v.legL) v.shinL = knee; else v.shinR = knee;
            }
            // torso and cockpit hatch with BT's single eye
            v.torso = Pivot(holder, "torso", new Vector3(0f, hip, 0f));
            v.torsoY = hip;
            Part(v.torso, "pelvis", PrimitiveType.Cube, new Vector3(0f, h * 0.04f, 0f), new Vector3(w * 0.62f, h * 0.1f, w * 0.42f), Dark);
            Part(v.torso, "chest", PrimitiveType.Cube, new Vector3(0f, h * 0.28f, 0f), new Vector3(w * 0.9f, h * 0.34f, w * 0.62f), Olive);
            Part(v.torso, "hatch", PrimitiveType.Cube, new Vector3(0f, h * 0.3f, w * 0.32f), new Vector3(w * 0.5f, h * 0.24f, w * 0.06f), Olive);
            Part(v.torso, "stripe", PrimitiveType.Cube, new Vector3(0f, h * 0.2f, w * 0.355f), new Vector3(w * 0.52f, h * 0.025f, w * 0.01f), Militia);
            var eye = Part(v.torso, "eye", PrimitiveType.Sphere, new Vector3(0f, h * 0.4f, w * 0.34f), Vector3.one * w * 0.13f, Eye);
            var l = eye.gameObject.AddComponent<Light>(); l.color = Eye; l.range = 6f; l.intensity = 2.5f;
            // arms (pivot at the shoulder); XO-16 in the right hand, rocket pod on the left shoulder
            v.armR = Pivot(v.torso, "armR", new Vector3(w * 0.55f, h * 0.38f, 0f));
            v.armL = Pivot(v.torso, "armL", new Vector3(-w * 0.55f, h * 0.38f, 0f));
            foreach (var arm in new[] { v.armR, v.armL })
            {
                Part(arm, "shoulder", PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(w * 0.24f, w * 0.24f, w * 0.3f), Olive);
                Part(arm, "upper", PrimitiveType.Cube, new Vector3(0f, -h * 0.12f, 0f), new Vector3(w * 0.16f, h * 0.2f, w * 0.18f), Dark);
                Part(arm, "fore", PrimitiveType.Cube, new Vector3(0f, -h * 0.24f, w * 0.12f), new Vector3(w * 0.18f, w * 0.18f, h * 0.18f), Olive);
            }
            Part(v.armR, "xo16", PrimitiveType.Cylinder, new Vector3(w * 0.05f, -h * 0.25f, h * 0.28f), new Vector3(w * 0.12f, h * 0.17f, w * 0.12f), Dark, new Vector3(90f, 0f, 0f));
            v.Muzzle = Pivot(v.armR, "muzzle", new Vector3(w * 0.05f, -h * 0.25f, h * 0.46f));
            v.RocketPod = Pivot(v.armL, "pod", new Vector3(0f, w * 0.22f, 0f));
            Part(v.armL, "podbox", PrimitiveType.Cube, new Vector3(0f, w * 0.22f, 0f), new Vector3(w * 0.26f, w * 0.18f, w * 0.34f), Militia);
            return v;
        }

        /// <summary>The exported BT model + XO-16, or null when the assets are missing or fail to build.</summary>
        private static TitanVisual TryBuildReal(Transform root)
        {
            AssetLibrary.Wait();
            if (AssetLibrary.Titan == null || AssetLibrary.TitanClips == null) return null;
            try
            {
                var holder = new GameObject("PilotHeim_TitanVisual").transform;
                holder.SetParent(root, false);
                var v = holder.gameObject.AddComponent<TitanVisual>();
                var bones = AssetLibrary.Titan.Build(holder, TfMaterials.Get, out _);
                v.modelRoot = bones[0].parent;
                v.Anim = holder.gameObject.AddComponent<PhAnimator>();
                v.Anim.Bones = bones;
                v.Anim.Clips = AssetLibrary.TitanClips;
                v.Anim.Play("idle", 0f);
                Transform Bone(string n) { foreach (var b in bones) if (b.name == n) return b; return null; }
                v.RocketPod = Bone("def_l_missile_box") ?? holder;
                // XO-16: Titanfall pins the titan's hand to the gun's r_hand_ik with runtime IK; without IK
                // we do the inverse and mount the gun so its r_hand_ik frame sits on BT's ja_r_propHand.
                string gunPath = Path.Combine(AssetLibrary.Dir, "xo16.phm2");
                var hand = Bone("ja_r_propHand");
                if (File.Exists(gunPath) && hand != null)
                {
                    var gun = PhModel.Load(gunPath);
                    var gb = gun.Build(hand, TfMaterials.Get, out _);
                    int grip = System.Array.IndexOf(gun.BoneNames, "r_hand_ik");
                    var gunRoot = gb[0].parent;
                    if (grip >= 0)
                    {
                        var bp = gun.BindPoses[grip];                 // = inverse of the grip's bind frame
                        gunRoot.localPosition = bp.GetColumn(3);
                        gunRoot.localRotation = bp.rotation;
                    }
                    foreach (var b in gb) if (b.name == "muzzle_flash") v.Muzzle = b;
                }
                if (v.Muzzle == null) v.Muzzle = Bone("ja_r_propHand") ?? holder;
                foreach (var r in holder.GetComponentsInChildren<Renderer>()) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = true; }
                v.RealModel = true;
                Plugin.Log.LogInfo($"BT-7274 model built: {bones.Length} bones, {TfMaterials.TexturesLoaded} textures ({TfMaterials.Missing} missing)");
                return v;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError("BT model failed, using the stand-in: " + e);
                return null;
            }
        }

        /// <summary>Picks BT's clip from the motor (Titanfall-style: idle, walk/run in 4 directions, sprint, dash).</summary>
        private void AnimateReal(TitanMotor motor)
        {
            Vector3 v = new Vector3(motor.Vel.x, 0f, motor.Vel.z);
            float speed = v.magnitude;                                           // u/s
            Vector3 local = transform.InverseTransformDirection(v);
            string dir = Mathf.Abs(local.z) >= Mathf.Abs(local.x) ? (local.z >= 0f ? "f" : "b") : (local.x >= 0f ? "r" : "l");
            string key;
            if (motor.Dashing || dashHold > 0f)
            {
                if (motor.Dashing && dashHold <= 0f) { dashDir = dir; dashHold = 0.55f; Anim.Play("dash_" + dashDir, 0.08f, true); }
                dashHold -= Time.deltaTime;
                Anim.Rate = 1f;
                return;
            }
            if (speed < 25f) key = "idle";
            else if (motor.Sprinting && dir == "f") key = "sprint_f";
            else if (speed > 330f) key = "run_" + dir;
            else key = "walk_" + dir;
            if (!Anim.Has(key)) key = "idle";
            Anim.Play(key, 0.25f);
            var clip = Anim.Clips[key];
            // match foot speed to ground speed (clip speed from Titanfall's motion tracker)
            Anim.Rate = clip.SpeedUnits > 1f ? Mathf.Clamp(speed / clip.SpeedUnits, 0.5f, 1.8f) : 1f;
        }

        private float dashHold;
        private string dashDir = "f";

        public void Animate(TitanMotor motor, float dt)
        {
            if (RealModel) { AnimateReal(motor); return; }
            float speed = new Vector3(motor.Vel.x, 0f, motor.Vel.z).magnitude;   // u/s
            float stride = Mathf.Clamp01(speed / 280f);
            phase += dt * speed / 55f;
            float swing = Mathf.Sin(phase) * 26f * stride;
            legL.localRotation = Quaternion.Euler(swing, 0f, 0f);
            legR.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            shinL.localRotation = Quaternion.Euler(Mathf.Max(0f, -Mathf.Sin(phase)) * 30f * stride, 0f, 0f);
            shinR.localRotation = Quaternion.Euler(Mathf.Max(0f, Mathf.Sin(phase)) * 30f * stride, 0f, 0f);
            torso.localPosition = new Vector3(0f, torsoY + Mathf.Abs(Mathf.Cos(phase)) * 0.06f * stride, 0f);   // bob around the hip, never accumulate
            armL.localRotation = Quaternion.Euler(-swing * 0.5f, 0f, 0f);
            armR.localRotation = Quaternion.Euler(-8f, 0f, 0f);                 // forearm and XO-16 already point forward; slight aim lift
        }

        /// <summary>Electric smoke stand-in: translucent pulsing shells for the smoke lifetime.</summary>
        public static void SmokeCloud(Vector3 pos, float radius, float life)
        {
            var root = new GameObject("PilotHeim_ElectricSmoke");
            root.transform.position = pos;
            for (int i = 0; i < 5; i++)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(s.GetComponent<Collider>());
                s.transform.SetParent(root.transform, false);
                s.transform.localPosition = Random.insideUnitSphere * radius * 0.3f;
                s.transform.localScale = Vector3.one * radius * Random.Range(0.8f, 1.3f);
                var sh = Shader.Find("Sprites/Default");
                if (sh != null) s.GetComponent<Renderer>().material = new Material(sh) { color = new Color(0.6f, 0.75f, 1f, 0.12f) };
            }
            var l = root.AddComponent<Light>(); l.color = new Color(0.5f, 0.7f, 1f); l.range = radius * 1.5f; l.intensity = 3f;
            Object.Destroy(root, life);
        }
    }
}
