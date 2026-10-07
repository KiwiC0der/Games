using HarmonyLib;
using PilotHeim.Pilot;
using UnityEngine;

namespace PilotHeim
{
    /// <summary>
    /// Harmony hooks. Only the local player's walking physics is replaced; every
    /// other Valheim system (swimming, riding, building, combat, inventory, food,
    /// stamina for actions, status effects, saving) keeps running untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class Patches
    {
        private static bool PilotActive(Character c, out PilotController pc)
        {
            pc = null;
            if (!(c is Player p) || p != Player.m_localPlayer) return false;
            pc = PilotController.Local;
            return pc != null && pc.Player == p && pc.Active;
        }

        // Replace UpdateWalking with the pilot motor, then keep Valheim's rotation/animation duties.
        [HarmonyPrefix, HarmonyPatch(typeof(Character), "UpdateWalking")]
        private static bool UpdateWalking(Character __instance, float dt)
        {
            if (!PilotActive(__instance, out var pc)) return true;
            pc.PhysicsTick(dt);
            var m = pc.Motor;
            var p = pc.Player;

            // body faces the aim like a Titanfall pilot
            float turn = p.UpdateRotation(p.m_runTurnSpeed * 4f, dt, smooth: false);
            p.UpdateEyeRotation();
            p.m_currentTurnVel = Mathf.SmoothDamp(p.m_currentTurnVel, turn, ref p.m_currentTurnVelChange, 0.5f, 99f);

            // Valheim's animator expects m/s in the character's local frame
            Vector3 v = p.m_body.linearVelocity;
            p.m_currentVel = new Vector3(v.x, 0f, v.z);
            float fwd = Vector3.Dot(v, p.transform.forward);
            float side = Vector3.Dot(v, p.transform.right);
            p.m_running = m.Sprinting && m.OnGround;
            p.m_walking = false;
            p.m_zanim.SetFloat(Character.s_forwardSpeed, m.Wallrunning ? Mathf.Max(fwd, new Vector3(v.x, 0, v.z).magnitude) : fwd);
            p.m_zanim.SetFloat(Character.s_sidewaySpeed, m.Wallrunning ? 0f : side);
            p.m_zanim.SetFloat(Character.s_turnSpeed, p.m_currentTurnVel);
            p.m_zanim.SetBool(Character.s_inWater, false);
            p.m_zanim.SetBool(Character.s_onGround, m.OnGround || m.Wallrunning);
            p.m_zanim.SetBool(Character.s_encumbered, p.IsEncumbered());
            p.m_zanim.SetBool(Character.s_flying, false);
            if (p.m_currentVel.magnitude > 0.1f) p.AddNoise(m.Sprinting ? 30f : 15f);
            return false;
        }

        // Titanfall pilots take no fall damage.
        [HarmonyPrefix, HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        private static void NoFallDamage(Character __instance)
        {
            if (PilotActive(__instance, out _)) __instance.m_maxAirAltitude = __instance.transform.position.y;
        }

        // The pilot motor owns jumping (double jump, wall jump, slide jump).
        [HarmonyPrefix, HarmonyPatch(typeof(Character), nameof(Character.Jump))]
        private static bool Jump(Character __instance) => !PilotActive(__instance, out _);

        // Frictionless body: Titanfall friction is applied by the motor, not by PhysX.
        [HarmonyPostfix, HarmonyPatch(typeof(Character), "UpdateBodyFriction")]
        private static void BodyFriction(Character __instance)
        {
            if (!PilotActive(__instance, out _)) return;
            var mat = __instance.m_collider.material;
            mat.staticFriction = 0f;
            mat.dynamicFriction = 0f;
            mat.frictionCombine = PhysicsMaterialCombine.Minimum;
            mat.bounciness = 0f;
            mat.bounceCombine = PhysicsMaterialCombine.Minimum;
        }

        // Ctrl is slide, not Valheim's sneak toggle, while piloting.
        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
        private static void SetControls(Player __instance, ref bool crouch, ref bool jump)
        {
            if (!PilotActive(__instance, out _)) return;
            crouch = false;
            jump = false;
            if (__instance.m_crouchToggled) __instance.SetCrouch(false);
        }

        // Wallrun camera tilt.
        [HarmonyPostfix, HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        private static void CameraRoll(GameCamera __instance)
        {
            var pc = PilotController.Local;
            if (pc == null || !pc.Active || Mathf.Abs(pc.Motor.ViewRoll) < 0.01f) return;
            var tr = __instance.transform;
            tr.rotation = tr.rotation * Quaternion.Euler(0f, 0f, pc.Motor.ViewRoll);
        }

        // Attach the controller to the local player as soon as it spawns.
        [HarmonyPostfix, HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        private static void OnLocalPlayer(Player __instance)
        {
            if (PilotController.Tuning == null) return;
            if (__instance.GetComponent<PilotController>() == null) __instance.gameObject.AddComponent<PilotController>();
        }
    }
}
