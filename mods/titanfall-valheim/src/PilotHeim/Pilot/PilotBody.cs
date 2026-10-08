using System.Collections.Generic;
using PilotHeim.Assets;
using UnityEngine;

namespace PilotHeim.Pilot
{
    /// <summary>
    /// Replaces the Valheim body with the Titanfall pilot (Jack Cooper, exported from the user's
    /// install). Valheim's humanoid animator keeps running - chopping, mining, building, swimming,
    /// sitting and every attack stay Valheim's - and each frame its human pose is retargeted onto
    /// Jack through a runtime humanoid avatar. Valheim's body and armour meshes are hidden; held
    /// weapons and tools stay on Valheim's (still animated) hand bones so hit timing is untouched.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class PilotBody : MonoBehaviour
    {
        public Player Player;
        public bool Ready { get; private set; }
        /// <summary>True for the copy that follows the death ragdoll instead of the live animator.</summary>
        public bool IsRagdoll { get; private set; }
        private Transform srcRoot;                                  // Valheim's "Visual" (or the ragdoll's equivalent)
        private Dictionary<string, Transform> ragdollBones;
        public Transform Root { get; private set; }

        private float nextHide;
        private readonly List<Renderer> jackRenderers = new List<Renderer>();
        private readonly HashSet<SkinnedMeshRenderer> hidden = new HashSet<SkinnedMeshRenderer>();
        private bool showingJack = true;

        // Titanfall pilot skeleton -> Unity humanoid
        private static readonly string[,] Map =
        {
            { "Hips", "def_c_hip" }, { "Spine", "def_c_spineA" }, { "Chest", "def_c_spineB" }, { "UpperChest", "def_c_spineC" },
            { "Neck", "def_c_neckA" }, { "Head", "def_c_head" },
            { "LeftShoulder", "def_l_clav" }, { "LeftUpperArm", "def_l_shoulder" }, { "LeftLowerArm", "def_l_elbow" }, { "LeftHand", "def_l_wrist" },
            { "RightShoulder", "def_r_clav" }, { "RightUpperArm", "def_r_shoulder" }, { "RightLowerArm", "def_r_elbow" }, { "RightHand", "def_r_wrist" },
            { "LeftUpperLeg", "def_l_thigh" }, { "LeftLowerLeg", "def_l_knee" }, { "LeftFoot", "def_l_ankle" }, { "LeftToes", "def_l_ball" },
            { "RightUpperLeg", "def_r_thigh" }, { "RightLowerLeg", "def_r_knee" }, { "RightFoot", "def_r_ankle" }, { "RightToes", "def_r_ball" },
            { "Left Thumb Proximal", "def_l_finThumbA" }, { "Left Thumb Intermediate", "def_l_finThumbB" }, { "Left Thumb Distal", "def_l_finThumbC" },
            { "Left Index Proximal", "def_l_finIndexA" }, { "Left Index Intermediate", "def_l_finIndexB" }, { "Left Index Distal", "def_l_finIndexC" },
            { "Left Middle Proximal", "def_l_finMidA" }, { "Left Middle Intermediate", "def_l_finMidB" }, { "Left Middle Distal", "def_l_finMidC" },
            { "Left Ring Proximal", "def_l_finRingA" }, { "Left Ring Intermediate", "def_l_finRingB" }, { "Left Ring Distal", "def_l_finRingC" },
            { "Left Little Proximal", "def_l_finPinkyA" }, { "Left Little Intermediate", "def_l_finPinkyB" }, { "Left Little Distal", "def_l_finPinkyC" },
            { "Right Thumb Proximal", "def_r_finThumbA" }, { "Right Thumb Intermediate", "def_r_finThumbB" }, { "Right Thumb Distal", "def_r_finThumbC" },
            { "Right Index Proximal", "def_r_finIndexA" }, { "Right Index Intermediate", "def_r_finIndexB" }, { "Right Index Distal", "def_r_finIndexC" },
            { "Right Middle Proximal", "def_r_finMidA" }, { "Right Middle Intermediate", "def_r_finMidB" }, { "Right Middle Distal", "def_r_finMidC" },
            { "Right Ring Proximal", "def_r_finRingA" }, { "Right Ring Intermediate", "def_r_finRingB" }, { "Right Ring Distal", "def_r_finRingC" },
            { "Right Little Proximal", "def_r_finPinkyA" }, { "Right Little Intermediate", "def_r_finPinkyB" }, { "Right Little Distal", "def_r_finPinkyC" },
        };

        public static PilotBody Attach(Player p)
        {
            AssetLibrary.Wait();
            if (AssetLibrary.Pilot == null || !Plugin.PilotBodyEnabled.Value) return null;
            var an = p.m_animator;
            if (an == null || an.avatar == null || !an.avatar.isHuman) { Plugin.Log.LogWarning("Pilot body: Valheim avatar is not humanoid"); return null; }
            var body = p.gameObject.AddComponent<PilotBody>();
            body.Player = p;
            try { body.Build(); }
            catch (System.Exception e) { Plugin.Log.LogError("Pilot body failed, keeping the Valheim body: " + e); body.Teardown(); Destroy(body); return null; }
            return body;
        }

        /// <summary>The pilot body for the death ragdoll: the same retarget, driven by the ragdoll's physics bones.</summary>
        public static PilotBody AttachRagdoll(Player p, Ragdoll ragdoll)
        {
            if (AssetLibrary.Pilot == null || ragdoll == null || p.m_animator == null || p.m_animator.avatar == null) return null;
            var body = ragdoll.gameObject.AddComponent<PilotBody>();
            body.Player = p; body.IsRagdoll = true;
            body.ragdollBones = new Dictionary<string, Transform>();
            foreach (var t in ragdoll.GetComponentsInChildren<Transform>(true)) body.ragdollBones[t.name] = t;
            try { body.Build(); }
            catch (System.Exception e) { Plugin.Log.LogError("Pilot ragdoll body failed: " + e); body.Teardown(); Destroy(body); return null; }
            return body;
        }

        /// <summary>Valheim bone for a humanoid slot: the live animator's, or the ragdoll bone of the same name.</summary>
        private Transform SrcBone(HumanBodyBones hb)
        {
            if (!IsRagdoll) return Player.m_animator.GetBoneTransform(hb);
            string human = hb.ToString();
            foreach (var h in Player.m_animator.avatar.humanDescription.human)
                if (h.humanName.Replace(" ", "") == human) return ragdollBones.TryGetValue(h.boneName, out var t) ? t : null;
            return null;
        }

        private void Build()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (IsRagdoll)
            {
                var hips = SrcBone(HumanBodyBones.Hips);
                if (hips == null) throw new System.Exception("ragdoll has no hips");
                srcRoot = hips.parent != null && hips.parent.parent != null ? hips.parent.parent : transform;   // Visual/Armature/Hips
            }
            else srcRoot = Player.m_animator.transform;
            var bones = AssetLibrary.Pilot.Build(srcRoot, TfMaterials.Get, out var smr);
            jackBones = bones;
            Root = bones[0].parent;
            Root.localPosition = Vector3.zero; Root.localRotation = Quaternion.identity; Root.localScale = Vector3.one;
            jackRenderers.AddRange(Root.GetComponentsInChildren<Renderer>(true));
            foreach (var r in jackRenderers) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.gameObject.layer = smrLayer(); }

            var byName = new Dictionary<string, Transform>();
            foreach (var b in bones) byName[b.name] = b;
            TPose(byName, Root);

            // Valheim's T-pose (from its humanoid avatar description), in the animator root's space
            var an = Player.m_animator;
            var vRoot = srcRoot;
            var tposeLocal = new Dictionary<string, Quaternion>();
            foreach (var sb in an.avatar.humanDescription.skeleton) tposeLocal[sb.name] = sb.rotation;
            Quaternion VTPose(Transform t)
            {
                Quaternion q = Quaternion.identity;
                for (var c = t; c != null && c != vRoot; c = c.parent)
                    q = (tposeLocal.TryGetValue(c.name, out var lr) ? lr : c.localRotation) * q;
                return q;
            }
            var pairs = new List<Pair>();
            for (int i = 0; i < Map.GetLength(0); i++)
            {
                var hb = HumanBodyBoneFromName(Map[i, 0]);
                if (hb == HumanBodyBones.LastBone || !byName.TryGetValue(Map[i, 1], out var jt)) continue;
                var vt = SrcBone(hb);
                if (vt == null) continue;
                pairs.Add(new Pair
                {
                    V = vt, J = jt,
                    Offset = Quaternion.Inverse(VTPose(vt)) * (Quaternion.Inverse(Root.rotation) * jt.rotation),
                });
            }
            map = pairs.ToArray();
            vHips = SrcBone(HumanBodyBones.Hips);
            jHips = byName["def_c_hip"];
            // hip height ratio scales the root motion of the hips (Valheim T-pose hip height vs the pilot's)
            Vector3 vHipT = Vector3.zero;
            foreach (var sb in an.avatar.humanDescription.skeleton) if (sb.name == vHips.name) vHipT = sb.position;
            float vh = Mathf.Max(0.1f, vRoot.InverseTransformPoint(vHips.position).y);
            float jh = Mathf.Max(0.1f, Root.InverseTransformPoint(jHips.position).y);
            hipScale = jh / vh;
            // Valheim culls bone updates when its (now hidden) body is not rendered; the pilot needs them
            if (!IsRagdoll) { savedCulling = an.cullingMode; an.cullingMode = AnimatorCullingMode.AlwaysAnimate; }
            Ready = true;
            if (IsRagdoll) { HideNow(); Plugin.Log.LogInfo("Pilot body follows the death ragdoll"); return; }
            Plugin.Log.LogInfo($"Pilot body built in {sw.ElapsedMilliseconds} ms: Jack Cooper retargeted from Valheim ({map.Length} bones mapped, {bones.Length} bones, {smr.sharedMesh.vertexCount} verts, hip scale {hipScale:0.00})");
        }

        private Transform[] jackBones;
        public PilotMotionLayer Motion { get; private set; }

        private struct Pair { public Transform V, J; public Quaternion Offset; }
        private Pair[] map;
        private Transform vHips, jHips;
        private float hipScale = 1f;
        private AnimatorCullingMode savedCulling = AnimatorCullingMode.CullUpdateTransforms;

        private static HumanBodyBones HumanBodyBoneFromName(string human)
        {
            string n = human.Replace(" ", "");
            n = n.Replace("Little", "Little").Replace("Proximal", "Proximal");
            foreach (HumanBodyBones hb in System.Enum.GetValues(typeof(HumanBodyBones)))
                if (hb != HumanBodyBones.LastBone && hb.ToString() == n) return hb;
            return HumanBodyBones.LastBone;
        }

        /// <summary>Per-bone rotation retarget: pilot = valheim * inverse(valheim T-pose) * pilot T-pose.</summary>
        private void Retarget()
        {
            var vRoot = srcRoot;
            Quaternion vInv = Quaternion.Inverse(vRoot.rotation);
            Quaternion jRot = Root.rotation;
            for (int i = 0; i < map.Length; i++)
            {
                ref var p = ref map[i];
                p.J.rotation = jRot * (vInv * p.V.rotation * p.Offset);
            }
            Vector3 hip = vRoot.InverseTransformPoint(vHips.position) * hipScale;
            jHips.position = Root.TransformPoint(hip);
        }

        private int smrLayer() => Player.m_visual.layer;

        /// <summary>Straightens the authored A-pose into the T-pose a humanoid avatar expects.</summary>
        private static void TPose(Dictionary<string, Transform> b, Transform frame)
        {
            void Align(string bone, string child, Vector3 dir)
            {
                if (!b.TryGetValue(bone, out var t) || !b.TryGetValue(child, out var c)) return;
                Vector3 cur = c.position - t.position;
                if (cur.sqrMagnitude < 1e-8f) return;
                t.rotation = Quaternion.FromToRotation(cur, dir) * t.rotation;
            }
            Vector3 left = -frame.right, right = frame.right, down = -frame.up;
            foreach (var s in new[] { "l", "r" })
            {
                Vector3 side = s == "l" ? left : right;
                Align($"def_{s}_shoulder", $"def_{s}_elbow", side);
                Align($"def_{s}_elbow", $"def_{s}_wrist", side);
                Align($"def_{s}_wrist", $"def_{s}_finMidA", side);
                foreach (var f in new[] { "Index", "Mid", "Ring", "Pinky" })
                {
                    Align($"def_{s}_fin{f}A", $"def_{s}_fin{f}B", side);
                    Align($"def_{s}_fin{f}B", $"def_{s}_fin{f}C", side);
                }
                Align($"def_{s}_thigh", $"def_{s}_knee", down);
                Align($"def_{s}_knee", $"def_{s}_ankle", down);
            }
        }

        private void LateUpdate()
        {
            if (!Ready || Player == null) return;
            if (IsRagdoll) { Retarget(); return; }
            bool want = Plugin.Enabled.Value;                       // F8 (pilot mode off) brings the Viking back
            if (want != showingJack) { showingJack = want; Root.gameObject.SetActive(want); if (!want) RestoreValheimBody(); }
            if (!want) return;
            Retarget();
            if (Motion == null && jackBones != null && Player.GetComponent<PilotController>() is PilotController pcx)
                Motion = new PilotMotionLayer(pcx, jackBones, Root, AssetLibrary.PilotClips);
            Motion?.Apply(Time.deltaTime);
            if (Time.time >= nextHide) { nextHide = Time.time + 0.25f; HideValheimBody(); }
        }

        /// <summary>Hide Valheim's skinned body, hair, beard and armour (re-created whenever gear changes).</summary>
        private void HideValheimBody()
        {
            var scope = IsRagdoll ? transform : Player.m_visual.transform;
            foreach (var r in scope.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r.enabled && !jackRenderers.Contains(r) && !Embarked()) { r.enabled = false; hidden.Add(r); }
        }

        /// <summary>Hide right away (gear changed, or the Titan just showed every renderer again).</summary>
        public void HideNow() { if (Ready && showingJack) HideValheimBody(); }

        // inside the Titan every pilot renderer is hidden by the Titan itself; only that is left alone
        private bool Embarked()
        {
            if (IsRagdoll) return false;
            var t = PilotHeim.Titan.TitanController.Current;
            return t != null && t.Owner == Player && t.Phase == PilotHeim.Titan.TitanController.State.Piloted;
        }

        private void RestoreValheimBody()
        {
            foreach (var r in hidden) if (r != null) r.enabled = true;
            hidden.Clear();
        }

        private void Teardown()
        {
            Motion?.Destroy(); Motion = null;
            if (Root != null) Destroy(Root.gameObject);
            RestoreValheimBody();
            if (Ready && !IsRagdoll && Player != null && Player.m_animator != null) Player.m_animator.cullingMode = savedCulling;
            Ready = false;
        }

        private void OnDestroy() => Teardown();
    }
}
