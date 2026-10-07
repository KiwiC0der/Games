using PilotHeim.Data;
using UnityEngine;

namespace PilotHeim.Pilot
{
    /// <summary>
    /// Lives on the local Player. Owns the motor, latches edge-triggered input in
    /// Update (physics runs in FixedUpdate), and draws the pilot HUD.
    /// </summary>
    public sealed class PilotController : MonoBehaviour
    {
        public static PilotController Local { get; private set; }
        public Player Player { get; private set; }
        public PilotMotor Motor { get; private set; }
        public static PilotTuning Tuning;

        public bool Active => Plugin.Enabled.Value && Tuning != null && Player != null && !Player.IsDead()
                              && !Player.IsSwimming() && !Player.IsAttached() && !Player.InIntro() && !Player.IsDebugFlying()
                              && !Player.IsTeleporting();

        private bool crouchHeld;
        private float storedRadius, storedHeight;
        private Vector3 storedCenter;
        private bool colliderStored;

        private void Awake()
        {
            Player = GetComponent<Player>();
            Motor = new PilotMotor(Player, Tuning);
            Local = this;
        }

        private void OnDestroy()
        {
            Motor?.Grapple.Destroy();
            RestoreCollider();
            if (Local == this) Local = null;
        }

        private void Update()
        {
            if (Player != Player.m_localPlayer) return;
            if (!Active) { RestoreCollider(); return; }
            StoreCollider();
            if (Motor.Override != null) return;           // self-test drives the inputs
            bool input = Player.TakeInput();
            crouchHeld = input && Input.GetKey(Plugin.KeySlide.Value);
            if (input && ZInput.GetButtonDown("Jump")) Motor.QueueJump();
            Motor.SetJumpHeld(input && ZInput.GetButton("Jump"));
            if (input && Input.GetKeyDown(Plugin.KeyTactical.Value))
            {
                var cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
                if (cam != null) Motor.Grapple.Fire(cam.position, cam.forward);
            }
        }

        /// <summary>Called from the UpdateWalking patch inside Valheim's FixedUpdate.</summary>
        public void PhysicsTick(float dt) => Motor.FixedStep(dt, Motor.Override != null ? Motor.Override.Crouch : crouchHeld);

        private void StoreCollider()
        {
            if (colliderStored) return;
            var c = Player.m_collider;
            storedRadius = c.radius; storedHeight = c.height; storedCenter = c.center;
            colliderStored = true;
        }

        private void RestoreCollider()
        {
            if (!colliderStored || Player == null) return;
            var c = Player.m_collider;
            c.radius = storedRadius; c.height = storedHeight; c.center = storedCenter;
            colliderStored = false;
            if (Motor != null) { Motor.Crouched = false; Motor.Sliding = false; Motor.Wallrunning = false; }
        }

        private void OnGUI()
        {
            if (Player != Player.m_localPlayer || !Active || Hud.IsUserHidden()) return;
            const int w = 220;
            float x = Screen.width - w - 24, y = Screen.height - 120;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(x - 8, y - 8, w + 16, 92), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var m = Motor;
            float kmh = m.HorizontalSpeed * PilotTuning.MetersPerUnit * 3.6f;
            string state = m.Wallrunning ? "WALLRUN" : m.Sliding ? "SLIDE" : m.OnGround ? (m.Sprinting ? "SPRINT" : "GROUND") : "AIR";
            GUI.Label(new Rect(x, y, w, 20), $"PILOT  {kmh:0} km/h  {state}");
            GUI.Label(new Rect(x, y + 22, w, 20), "GRAPPLE");
            Bar(new Rect(x + 70, y + 26, w - 70, 10), m.Grapple.Power / 100f, new Color(0.35f, 0.75f, 1f));
            GUI.Label(new Rect(x, y + 44, w, 20), "TITAN");
            Bar(new Rect(x + 70, y + 48, w - 70, 10), TitanMeter.Fraction, new Color(1f, 0.62f, 0.2f));
            GUI.Label(new Rect(x, y + 64, w, 20), TitanMeter.Ready ? $"Titan ready - press {Plugin.KeyTitanfall.Value}" : "");
        }

        private static void Bar(Rect r, float frac, Color c)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.2f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }

    /// <summary>Titan meter placeholder until the Titan module lands (phase 4).</summary>
    public static class TitanMeter
    {
        public static float Fraction;
        public static bool Ready => Fraction >= 1f;
    }
}
