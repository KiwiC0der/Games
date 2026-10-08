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
        public PilotArsenal Arsenal { get; private set; }
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
            try
            {
                Arsenal = new PilotArsenal(Player, Motor, Tuning, PilotArsenal.WeaponsDir, Plugin.Loadout.Value.Split(','),
                                           Plugin.TacticalAbility.Value, Plugin.CampaignWeaponProfile.Value, Motor.Grapple);
                Motor.Arsenal = Arsenal;
            }
            catch (System.Exception e) { Plugin.Log.LogError("Pilot weapons unavailable: " + e); }
            Local = this;
        }

        public PilotBody Body { get; private set; }

        // the Titanfall pilot body needs Valheim's animator and ZNetScene (for the shader), ready by Start
        private void Start()
        {
            TitanMeter.Load(Player);
            Body = PilotBody.Attach(Player);
        }

        private float nextMeterStore;

        private void OnDestroy()
        {
            Motor?.Grapple.Destroy();
            Arsenal?.Destroy();
            RestoreCollider();
            if (Local == this) Local = null;
        }

        private void Update()
        {
            if (Player != Player.m_localPlayer) return;
            if (!Active) { RestoreCollider(); Arsenal?.RestoreFov(); return; }
            StoreCollider();
            UpdateTitanMeter();
            if (Motor.Override != null) return;           // self-test drives the inputs
            bool input = Player.TakeInput();
            crouchHeld = input && Input.GetKey(Plugin.KeySlide.Value);
            if (input && ZInput.GetButtonDown("Jump")) Motor.QueueJump();
            Motor.SetJumpHeld(input && ZInput.GetButton("Jump"));
            bool ui = InventoryGui.IsVisible() || Minimap.IsOpen() || Menu.IsVisible() || Player.InPlaceMode() || TextInput.IsVisible() || global::Console.IsVisible();
            bool gunInput = input && !ui;
            if (gunInput && Input.GetKeyDown(Plugin.KeyTitanfall.Value)) TitanButton();
            if (gunInput && Input.GetKeyDown(Plugin.KeyEmbark.Value)) EmbarkButton();
            Arsenal?.Update(Time.deltaTime, gunInput, Input.GetMouseButton(0), Input.GetMouseButton(1),
                            Input.GetKeyDown(Plugin.KeyReload.Value), Input.GetKeyDown(Plugin.KeyWeaponToggle.Value),
                            Input.GetKeyDown(Plugin.KeyWeaponSwap.Value), Input.GetKeyDown(Plugin.KeyTactical.Value),
                            Input.GetKeyDown(Plugin.KeyOrdnance.Value));
        }

        /// <summary>Called from the UpdateWalking patch inside Valheim's FixedUpdate.</summary>
        public void PhysicsTick(float dt)
        {
            Motor.FixedStep(dt, Motor.Override != null ? Motor.Override.Crouch : crouchHeld);
            Arsenal?.FixedTick(dt);
        }

        // --------------------------------------------------------------- Titan
        private void UpdateTitanMeter()
        {
            var tt = PilotHeim.Titan.TitanController.Tuning;
            if (tt == null) return;
            if (PilotHeim.Titan.TitanController.Current == null)
                TitanMeter.Fraction = Mathf.Min(1f, TitanMeter.Fraction + Time.deltaTime / (tt.BuildTime * Mathf.Max(0.01f, Plugin.TitanBuildTimeScale.Value)));
            if (Time.time >= nextMeterStore) { nextMeterStore = Time.time + 2f; TitanMeter.Store(Player); }
        }

        private void TitanButton()
        {
            var titan = PilotHeim.Titan.TitanController.Current;
            if (titan != null)
            {
                titan.Following = !titan.Following;
                Player.Message(MessageHud.MessageType.Center, titan.Following ? "Titan: follow mode" : "Titan: guard mode");
                return;
            }
            if (!TitanMeter.Ready) { Player.Message(MessageHud.MessageType.TopLeft, $"Titan {TitanMeter.Fraction * 100f:0}%"); return; }
            if (TryFindTitanfallPoint(out var point))
            {
                TitanMeter.Fraction = 0f;
                PilotHeim.Titan.TitanController.CallIn(Player, point, Tuning);
                Player.Message(MessageHud.MessageType.Center, "Standby for Titanfall");
            }
            else Player.Message(MessageHud.MessageType.TopLeft, "No room for Titanfall here");
        }

        /// <summary>_replacement_titans_drop.gnut: look-at point within TITANDROP_LOS_DIST, else a diagonal ground search.</summary>
        public bool TryFindTitanfallPoint(out Vector3 point)
        {
            const float U = PilotTuning.MetersPerUnit;
            var tt = PilotHeim.Titan.TitanController.Tuning;
            var cam = GameCamera.instance.transform;
            Vector3 eye = cam.position, view = cam.forward;
            float len2D = new Vector2(view.x, view.z).magnitude;
            float titanRadius = tt.HullRadius * 1.2f * U;
            point = Vector3.zero;
            Vector3 candidate;
            if (len2D > 0.05f && Physics.Raycast(eye, view, out var hit, PilotHeim.Data.TitanTuning.DropLosDist / len2D * U, Character.s_groundRayMask, QueryTriggerInteraction.Ignore))
                candidate = hit.point - view * titanRadius + hit.normal * titanRadius;
            else
            {
                Vector3 fwd2 = new Vector3(view.x, 0f, view.z).normalized;
                Vector3 start = eye + fwd2 * (PilotHeim.Data.TitanTuning.DropGroundSearchForward * U);
                Vector3 diag = new Vector3(fwd2.x, -1f, fwd2.z);
                candidate = Physics.Raycast(start, diag.normalized, out var g, PilotHeim.Data.TitanTuning.DropGroundSearchDist * U * diag.magnitude, Character.s_groundRayMask, QueryTriggerInteraction.Ignore)
                    ? g.point + fwd2 * (PilotHeim.Data.TitanTuning.DropFallbackDist * U)
                    : eye + fwd2 * (PilotHeim.Data.TitanTuning.DropFallbackDist * U);
            }
            if (!Physics.Raycast(candidate + Vector3.up * 40f, Vector3.down, out var ground, 120f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore)) return false;
            if (ground.normal.y < 0.6f) return false;
            // the Titan needs head room and clear space for its hull
            if (Physics.CheckCapsule(ground.point + Vector3.up * (titanRadius + 0.3f), ground.point + Vector3.up * (tt.HullHeight * U - titanRadius),
                                     tt.HullRadius * U * 0.9f, Character.s_blockedRayMask, QueryTriggerInteraction.Ignore)) return false;
            point = ground.point;
            return true;
        }

        private void EmbarkButton()
        {
            var titan = PilotHeim.Titan.TitanController.Current;
            if (titan != null && titan.CanEmbark(Player)) titan.Embark(Player);
        }

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
            if (Player != Player.m_localPlayer || Hud.IsUserHidden()) return;
            var tc = PilotHeim.Titan.TitanController.Current;
            if (tc != null && tc.Phase == PilotHeim.Titan.TitanController.State.Piloted) { DrawTitanHud(tc); return; }
            if (!Active) return;
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
            var titanNow = PilotHeim.Titan.TitanController.Current;
            string titanLine = titanNow != null
                ? (titanNow.CanEmbark(Player) ? $"Embark - {Plugin.KeyEmbark.Value}" : $"Titan {(titanNow.Following ? "following" : "guarding")} ({Plugin.KeyTitanfall.Value} toggles)")
                : TitanMeter.Ready ? $"Titan ready - press {Plugin.KeyTitanfall.Value}" : "";
            GUI.Label(new Rect(x, y + 64, w, 20), titanLine);
            if (PilotHeim.Assets.AssetLibrary.Loading)                              // loading state for the Titanfall models
                GUI.Label(new Rect(x, y - 20, w, 20), PilotHeim.Assets.AssetLibrary.Status);
            DrawArsenalHud(x, y, w);
        }

        private void DrawArsenalHud(float x, float y, int w)
        {
            var a = Arsenal;
            if (a == null) return;
            var m = Motor;
            float y2 = y - 74;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(x - 8, y2 - 8, w + 16, 66), Texture2D.whiteTexture);
            GUI.color = Color.white;
            string gun = a.Weapon != null ? PilotHeim.Data.Localization.Get(a.Weapon.PrintName) : "-";
            GUI.Label(new Rect(x, y2, w, 20), a.Drawn && a.Weapon != null
                ? $"{gun}   {a.Clip}/{(int)a.Weapon.ClipSize}{(a.Reloading ? "  RELOADING" : "")}"
                : $"{gun} (holstered - {Plugin.KeyWeaponToggle.Value})");
            string tac = a.Tactical == PilotArsenal.TacticalKind.Grapple ? "GRAPPLE" : a.Tactical.ToString().ToUpperInvariant();
            float tf = a.Tactical == PilotArsenal.TacticalKind.Grapple ? m.Grapple.Power / 100f : a.TacticalAmmo / 200f;
            GUI.Label(new Rect(x, y2 + 20, w, 20), a.Cloaked ? "CLOAKED" : a.Stimmed ? "STIM" : tac);
            Bar(new Rect(x + 70, y2 + 24, w - 70, 10), tf, new Color(0.35f, 0.75f, 1f));
            GUI.Label(new Rect(x, y2 + 38, w, 20), "FRAG");
            Bar(new Rect(x + 70, y2 + 42, w - 70, 10), a.OrdnanceAmmo / 200f, new Color(0.9f, 0.9f, 0.3f));
            // pulse blade reveals are drawn through walls, like Titanfall's sonar highlight
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var kv in a.Revealed)
            {
                if (kv.Key == null) continue;
                Vector3 sp = cam.WorldToScreenPoint(kv.Key.GetCenterPoint());
                if (sp.z <= 0f) continue;
                GUI.color = new Color(1f, 0.35f, 0.15f, 0.85f);
                GUI.DrawTexture(new Rect(sp.x - 6, Screen.height - sp.y - 6, 12, 12), Texture2D.whiteTexture);
            }
            GUI.color = Color.white;
        }

        private void DrawTitanHud(PilotHeim.Titan.TitanController tc)
        {
            const int w = 260;
            float x = Screen.width - w - 24, y = Screen.height - 150;
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(x - 8, y - 8, w + 16, 136), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var a = tc.Arsenal;
            GUI.Label(new Rect(x, y, w, 20), $"BT-7274   {(tc.CoreActive ? "BURST CORE" : $"{PilotHeim.Data.Localization.Get(a.Weapon.PrintName)} {a.Clip}/{(int)a.Weapon.ClipSize}")}{(a.Reloading ? " RELOADING" : "")}");
            GUI.Label(new Rect(x, y + 20, w, 20), "SHIELD"); Bar(new Rect(x + 70, y + 24, w - 70, 10), tc.Shield / Mathf.Max(1f, tc.ShieldMax), new Color(0.4f, 0.8f, 1f));
            GUI.Label(new Rect(x, y + 38, w, 20), tc.Doomed ? "DOOMED" : "HULL"); Bar(new Rect(x + 70, y + 42, w - 70, 10), tc.Body.GetHealth() / tc.MaxHealth, tc.Doomed ? Color.red : new Color(0.9f, 0.9f, 0.9f));
            GUI.Label(new Rect(x, y + 56, w, 20), "DASH"); Bar(new Rect(x + 70, y + 60, w - 70, 10), tc.Motor.Power / 100f, new Color(0.3f, 1f, 0.5f));
            GUI.Label(new Rect(x, y + 74, w, 20), "SALVO"); Bar(new Rect(x + 70, y + 78, w - 70, 10), tc.SalvoAmmo / 120f, Color.yellow);
            GUI.Label(new Rect(x, y + 92, w, 20), "SMOKE"); Bar(new Rect(x + 70, y + 96, w - 70, 10), tc.SmokeAmmo / 100f, new Color(0.6f, 0.7f, 1f));
            GUI.Label(new Rect(x, y + 110, w, 20), tc.CoreMeter >= 1f ? $"CORE READY ({Plugin.KeyTitanfall.Value})" : "CORE"); Bar(new Rect(x + 70, y + 114, w - 70, 10), tc.CoreMeter, new Color(1f, 0.6f, 0.2f));
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
        private const string Key = "PilotHeim.TitanMeter";

        /// <summary>The meter lives in the character's save (Player.m_customData) so it survives relogging.</summary>
        public static void Load(Player p)
        {
            if (p != null && p.m_customData.TryGetValue(Key, out var v)
                && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                Fraction = UnityEngine.Mathf.Clamp01(f);
        }

        public static void Store(Player p)
        {
            if (p != null) p.m_customData[Key] = Fraction.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
