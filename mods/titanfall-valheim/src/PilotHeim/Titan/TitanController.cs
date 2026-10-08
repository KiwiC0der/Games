using System.Collections.Generic;
using PilotHeim.Assets;
using PilotHeim.Data;
using PilotHeim.Pilot;
using UnityEngine;

namespace PilotHeim.Titan
{
    /// <summary>
    /// The pilot's Titan (BT-7274 chassis by default). It is a real Valheim
    /// Character (built from the Troll prefab, faction Players, not persisted) so
    /// creatures fight it and Valheim's damage/death rules apply; its body is
    /// driven by TitanMotor. It drops from the sky (titanfall), guards or follows
    /// as an auto-Titan, and is piloted through Valheim's doodad-control system
    /// (the same mechanism as steering a ship).
    /// </summary>
    public sealed class TitanController : MonoBehaviour, IDoodadController
    {
        private const float U = PilotTuning.MetersPerUnit;
        public const float DropHeight = 3000f;         // u above the target where the hot drop starts
        public const float DropTime = 2.5f;             // s; exact value lives in the hotdrop animation (phase 5)
        public const float EmbarkDist = 250f;           // grapple_titanEmbarkDist
        private const float AiRange = 2400f;            // u
        private const float FollowDist = 500f;          // u
        private const float KneelWaitDist = 2000f;      // u: a fresh Titan waits kneeling for its pilot (Titanfall MP)

        public static TitanController Current;
        public static TitanTuning Tuning;

        public enum State { Dropping, Auto, Piloted }
        public State Phase { get; private set; }
        public bool Following = true;
        public Character Body { get; private set; }
        public TitanMotor Motor { get; private set; }
        public PilotArsenal Arsenal { get; private set; }
        public Player Owner { get; private set; }
        public float Shield, ShieldMax, CoreMeter, SalvoAmmo = 120f, SmokeAmmo = 100f;
        public float CoreUntil;
        public bool CoreActive => Motor != null && Motor.Time < CoreUntil;
        public float MaxHealth => Tuning.HealthPerSegment * Segments + Tuning.HealthDoomed;
        public int Segments => Mathf.Max(1, Mathf.RoundToInt(Tuning.Health / Tuning.HealthPerSegment));
        public bool Doomed => Body != null && Body.GetHealth() <= Tuning.HealthDoomed;

        // ------------------------------------------------------------- BT's voice
        private float voiceFreeAt, lastEngageLine = -100f;
        private bool wasDoomed, coreReadySaid;
        private readonly Dictionary<Character, float> recentlyHit = new Dictionary<Character, float>();
        public string LastLine { get; private set; }
        public readonly HashSet<string> Said = new HashSet<string>();

        /// <summary>One BT line at a time, with a short gap; returns false if he is still talking or has no line.</summary>
        public bool Say(string slot, bool urgent = false)
        {
            if (!urgent && Time.time < voiceFreeAt) return false;
            if (!PilotHeim.Assets.TfAudio.Play("bt:" + slot, transform.position + Vector3.up * 4f, 1f, 80f)) return false;
            voiceFreeAt = Time.time + 3.5f;
            LastLine = slot; Said.Add(slot);
            return true;
        }

        private void UpdateVoice()
        {
            // kills: anything BT hit in the last few seconds that has died (or already despawned)
            if (recentlyHit.Count > 0)
            {
                bool kill = false;
                var stale = new List<Character>();
                foreach (var kv in recentlyHit)
                {
                    bool gone = kv.Key == null || kv.Key.IsDead();
                    if (gone) { if (Time.time - kv.Value < 4f) kill = true; stale.Add(kv.Key); }
                    else if (Time.time - kv.Value > 4f) stale.Add(kv.Key);
                }
                foreach (var c in stale) recentlyHit.Remove(c);
                if (kill) Say("kill");
            }
            bool doomed = Doomed;
            if (doomed && !wasDoomed) Say("doomed", true);
            wasDoomed = doomed;
            if (CoreMeter >= 1f && !coreReadySaid && !CoreActive) coreReadySaid = Say("core_ready");
            if (CoreMeter < 1f) coreReadySaid = false;
        }

        private Vector3 dropFrom, dropTo;
        private float dropStart, lastDamaged = -100f, nextAiThink, salvoNext;
        private int salvoLeft;
        private Character aiTarget;
        private Transform cockpit, chestGun;
        private TitanVisual visual;
        public TitanVisual Visual => visual;
        /// <summary>First-person cockpit camera while piloted (toggle with the cockpit-view key).</summary>
        public bool CockpitView;
        /// <summary>Cockpit eye: the Titan set's stand viewheight above the feet, a little ahead of the hull centre.</summary>
        public Vector3 CockpitEye => transform.position + Vector3.up * (Tuning.EyeHeight * U) + transform.forward * 0.35f;
        private LineRenderer beacon;
        private Vector3 inMove, inLook = Vector3.forward;
        private bool inRun, dashQueued;
        private float savedCamMax = -1f, savedCamDist;
        private WeaponDef salvoDef, smokeDef, coreDef;
        private readonly List<(Vector3 pos, float until, float start)> smokes = new List<(Vector3, float, float)>();

        // ------------------------------------------------------------- spawning
        /// <summary>Titanfall: spawn the Titan above the target and start the hot drop.</summary>
        public static TitanController CallIn(Player owner, Vector3 target, PilotTuning pilotTuning)
        {
            var prefab = ZNetScene.instance.GetPrefab("Troll");
            if (prefab == null) { Plugin.Log.LogError("Titanfall: Troll prefab not found"); return null; }
            ZNetView.m_forceDisableInit = false;
            var go = Instantiate(prefab, target + Vector3.up * (DropHeight * U), Quaternion.LookRotation(Vector3.ProjectOnPlane(target - owner.transform.position, Vector3.up).normalized));
            go.name = "PilotHeim_Titan";
            foreach (var ai in go.GetComponentsInChildren<BaseAI>()) DestroyImmediate(ai);
            foreach (var d in go.GetComponentsInChildren<CharacterDrop>()) DestroyImmediate(d);
            var tame = go.GetComponent<Tameable>(); if (tame) DestroyImmediate(tame);
            var tc = go.AddComponent<TitanController>();
            tc.Init(owner, target, pilotTuning);
            return tc;
        }

        private void Init(Player owner, Vector3 target, PilotTuning pilotTuning)
        {
            Owner = owner;
            Body = GetComponent<Character>();
            var nv = Body.m_nview;
            if (nv != null && nv.IsValid()) nv.GetZDO().Persistent = false;     // never saved into the world
            Body.m_name = "BT-7274";
            Body.m_faction = Character.Faction.Players;
            Body.m_canSwim = false;                                   // Titans wade; the troll's swim code would take control away
            Body.m_damageModifiers = new HitData.DamageModifiers();
            Body.m_boss = false;
            Body.SetMaxHealth(MaxHealth);
            Body.SetHealth(MaxHealth);
            ShieldMax = Tuning.HealthShield; Shield = ShieldMax;
            var col = Body.m_collider;
            col.radius = Tuning.HullRadius * U;
            col.height = Tuning.HullHeight * U;
            col.center = new Vector3(0f, col.height * 0.5f, 0f);
            Body.m_body.mass = Tuning.PhysicsMass * 0.4536f;                      // lb -> kg
            Body.m_body.isKinematic = true;
            // the destroyed AI/drop components left their callbacks on the Character
            Body.m_onDamaged = null;
            Body.m_onDeath = null;
            Body.m_baseAI = null;
            foreach (var r in Body.m_visual.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            visual = TitanVisual.Build(transform, Tuning);
            cockpit = new GameObject("cockpit").transform;
            cockpit.SetParent(transform, false);
            cockpit.localPosition = new Vector3(0f, Tuning.EyeHeight * U - 1.1f, 0.2f);
            chestGun = visual.Muzzle;
            Motor = new TitanMotor(Body, Tuning);
            var weapons = PilotArsenal.WeaponsDir;
            Arsenal = new PilotArsenal(owner, Motor, pilotTuning, weapons, new[] { "mp_titanweapon_xo16_shorty" },
                                       PilotArsenal.TacticalKind.Grapple, Plugin.CampaignWeaponProfile.Value, null, null);
            Arsenal.Drawn = true;
            Arsenal.HitCharacter += c => { if (c != null && c != Body) recentlyHit[c] = Time.time; };
            Arsenal.MuzzleProvider = () => chestGun.position;
            Arsenal.IgnoreRoot = transform;
            salvoDef = WeaponDef.Load(weapons, "mp_titanweapon_salvo_rockets", Plugin.CampaignWeaponProfile.Value);
            smokeDef = WeaponDef.Load(weapons, "mp_titanability_smoke", Plugin.CampaignWeaponProfile.Value);
            coreDef = WeaponDef.Load(weapons, "mp_titancore_amp_core", Plugin.CampaignWeaponProfile.Value);
            SalvoAmmo = salvoDef.ClipSize; SmokeAmmo = smokeDef.ClipSize;
            dropTo = target;
            dropFrom = transform.position;
            dropStart = Time.time;
            Phase = State.Dropping;
            beacon = Effects.NewLine(0.25f, new Color(1f, 0.15f, 0.1f, 0.85f));
            Current = this;
            Plugin.Log.LogInfo($"Titanfall inbound at {target} (hp {MaxHealth}, shield {ShieldMax})");
            Effects.Sound("titan:inbound", owner.transform.position);
            // BT's own hot-drop sequence, offset so its ground impact frame lands with the simulated drop
            if (visual.RealModel && AssetLibrary.TitanClips.TryGetValue("hotdrop", out var hd))
                visual.PlayAction("hotdrop", Mathf.Max(0f, hd.Mark - DropTime), 0f);
        }

        // ------------------------------------------------------------- frame tick
        private void Update()
        {
            if (Body == null || Owner == null) { Net.Destroy(gameObject); return; }
            if (dying) { if (Time.time >= destroyAt) ZNetScene.instance.Destroy(gameObject); return; }
            float dt = Time.deltaTime;
            UpdateDrop();
            if (Phase == State.Dropping) return;

            // offhand regeneration (regen_ammo_refill_rate per second)
            SalvoAmmo = Mathf.Min(salvoDef.ClipSize, SalvoAmmo + salvoDef.RegenRate * dt);
            SmokeAmmo = Mathf.Min(smokeDef.ClipSize, SmokeAmmo + smokeDef.RegenRate * dt);
            if (!CoreActive) CoreMeter = Mathf.Min(1f, CoreMeter + dt / Mathf.Max(10f, Plugin.TitanCoreChargeSeconds.Value));
            Arsenal.Override = CoreActive ? coreDef : null;
            if (Motor.Time > lastDamaged + Tuning.RegenDelay && Shield < ShieldMax)
                Shield = Mathf.Min(ShieldMax, Shield + ShieldMax * Tuning.RegenPercent / 100f * dt);

            UpdateSalvo();
            UpdateSmokes(dt);
            UpdateVoice();
            lastAiTarget = aiTarget;

            if (Phase == State.Piloted)
            {
                bool input = Owner.TakeInput() && !InventoryGui.IsVisible() && !Menu.IsVisible() && !Minimap.IsOpen();
                Arsenal.Update(dt, input, Input.GetMouseButton(0), Input.GetMouseButton(1), Input.GetKeyDown(Plugin.KeyReload.Value),
                               false, false, false, false);
                if (input && ZInput.GetButtonDown("Jump")) dashQueued = true;
                if (input && Input.GetKeyDown(Plugin.KeyOrdnance.Value)) FireSalvo();
                if (input && Input.GetKeyDown(Plugin.KeyTactical.Value)) DeploySmoke();
                if (input && Input.GetKeyDown(Plugin.KeyTitanfall.Value) && CoreMeter >= 1f) StartCore();
                if (input && Input.GetKeyDown(Plugin.KeyEmbark.Value)) Disembark(false);
                if (input && Input.GetKeyDown(Plugin.KeyCockpitView.Value)) CockpitView = !CockpitView;
                Owner.m_maxAirAltitude = Owner.transform.position.y;
            }
            else
            {
                Arsenal.BackgroundTick(dt);
                AiThink();
                UpdateStuck(dt);
                // a kneeling BT gets up when he has to follow the pilot or fight
                if (visual.Kneeling && (aiTarget != null || (Following && AiDistToOwner() > KneelWaitDist)))
                {
                    Plugin.Log.LogInfo($"BT stands up: {(aiTarget != null ? "enemy " + aiTarget.name + $" at {Vector3.Distance(aiTarget.transform.position, transform.position):0} m" : $"pilot {AiDistToOwner():0} u away")}");
                    visual.StandUp();
                }
            }
        }

        // UpdateWalking patch routes the Titan's physics tick here.
        public void PhysicsTick(float dt)
        {
            if (Phase == State.Dropping || dying) return;
            Vector3 look = Phase == State.Piloted ? inLook : AiLook();
            Vector3 yaw = Vector3.ProjectOnPlane(look, Vector3.up);
            if (yaw.sqrMagnitude < 1e-4f) yaw = transform.forward;
            yaw.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, yaw);
            Vector3 move = Phase == State.Piloted ? inMove : AiMove();
            if (visual.Locked)                                                           // sequences play in place
            {
                move = Vector3.zero; dashQueued = false;
                yaw = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized; right = Vector3.Cross(Vector3.up, yaw);
            }
            float fwd = Vector3.Dot(move, yaw), side = Vector3.Dot(move, right);
            bool sprint = Phase == State.Piloted ? inRun : (Following && aiTarget == null && AiDistToOwner() > FollowDist * 2f);
            bool wasDashing = Motor.Dashing;
            Motor.FixedStep(dt, yaw, fwd, side, sprint, dashQueued, Mathf.Lerp(1f, 0.5f, Arsenal.AdsFrac));
            if (Motor.Dashing && !wasDashing) Effects.Sound("titan:dash", transform.position);
            dashQueued = false;
            // the Titan turns its whole body toward the aim
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(yaw), 180f * dt);
            visual.Animate(Motor, dt);
            Body.m_zanim.SetFloat(Character.s_forwardSpeed, 0f);
        }

        // ---------------------------------------------------------------- drop
        private void UpdateDrop()
        {
            if (Phase != State.Dropping) { if (beacon) Destroy(beacon.gameObject); return; }
            float k = Mathf.Clamp01((Time.time - dropStart) / DropTime);
            Vector3 pos = Vector3.Lerp(dropFrom, dropTo, k * k);           // accelerating fall, like the hot drop
            Body.m_body.position = pos;
            transform.position = pos;
            if (beacon) { beacon.SetPosition(0, dropTo); beacon.SetPosition(1, dropTo + Vector3.up * 400f); }
            if (k >= 1f) Land();
        }

        private void Land()
        {
            Phase = State.Auto;
            Body.m_body.isKinematic = false;
            Motor.ResetVelocity();
            // damagedef_titan_fall (crush) + damagedef_titan_hotdrop (shockwave); never the owner
            var hitOnce = new HashSet<IDestructible>();
            foreach (var col in Physics.OverlapSphere(dropTo, Tuning.HotdropRadius * U, Character.s_characterLayerMask | Character.s_groundRayMask, QueryTriggerInteraction.Ignore))
            {
                if (col.transform.IsChildOf(transform)) continue;
                var dest = col.GetComponentInParent<IDestructible>();
                if (!PilotArsenal.Damageable(dest) || !hitOnce.Add(dest)) continue;
                var c = col.GetComponentInParent<Character>();
                if (c != null && (c == Owner || c.IsTamed() || c.m_faction == Character.Faction.Players)) continue;
                float d = Vector3.Distance(dropTo, col.ClosestPoint(dropTo)) / U;
                float dmg = Falloff(Tuning.HotdropDamage, Tuning.HotdropInnerRadius, Tuning.HotdropRadius, d)
                          + Falloff(Tuning.FallDamage, Tuning.FallInnerRadius, Tuning.FallRadius, d);
                if (dmg <= 0f) continue;
                var hit = new HitData();
                hit.m_damage.m_blunt = dmg * Plugin.DamageScale.Value;
                hit.m_point = col.ClosestPoint(dropTo);
                hit.m_dir = (hit.m_point - dropTo).normalized;
                hit.m_pushForce = 200f;
                hit.m_hitCollider = col;
                hit.SetAttacker(Owner);
                dest.Damage(hit);
                LastLandingHits++;
            }
            Effects.Explosion(dropTo, Tuning.HotdropRadius * U);
            Owner.Message(MessageHud.MessageType.Center, "BT-7274 online");
            Effects.Sound("titan:land", dropTo, 1f);
        }

        public int LastLandingHits;

        private static float Falloff(float dmg, float inner, float outer, float d)
        {
            if (d <= inner) return dmg;
            if (d >= outer) return 0f;
            return dmg * (1f - (d - inner) / (outer - inner));
        }

        // ------------------------------------------------------------ embark
        public bool CanEmbark(Player p) => Phase == State.Auto && p != null && !p.IsAttached()
                                           && Vector3.Distance(p.transform.position, transform.position) / U < EmbarkDist + Tuning.HullRadius;

        public void Embark(Player p)
        {
            Phase = State.Piloted;
            p.AttachStart(cockpit, gameObject, true, false, false, "attach_chair", Vector3.zero);
            p.StartDoodadControl(this);
            foreach (var r in p.m_visual.GetComponentsInChildren<Renderer>()) r.enabled = false;
            var cam = GameCamera.instance;
            if (cam != null) { savedCamMax = cam.m_maxDistance; savedCamDist = cam.m_distance; cam.m_maxDistance = 14f; cam.m_distance = 10f; }
            inLook = p.m_lookDir;
            Owner.Message(MessageHud.MessageType.Center, "Pilot embarked");
            CockpitView = Plugin.CockpitViewDefault.Value;
            if (visual.RealModel)
            {
                // kneeling BT stands up with the pilot inside; a standing BT kneels on the pilot's side first
                Vector3 l = transform.InverseTransformPoint(p.transform.position);
                string side = Mathf.Abs(l.z) >= Mathf.Abs(l.x) ? (l.z >= 0f ? "f" : "b") : (l.x >= 0f ? "r" : "l");
                visual.PlayAction(visual.Kneeling ? "embark_kneel" : "embark_" + side);
            }
            Effects.Sound("titan:embark", transform.position);
            Say("embark", true);
        }

        public void Disembark(bool eject)
        {
            if (Phase != State.Piloted) return;
            Phase = State.Auto;
            var p = Owner;
            p.StopDoodadControl();
            p.AttachStop();
            foreach (var r in p.m_visual.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            Vector3 outPos = transform.position + transform.right * (Tuning.HullRadius * U + 1.2f) + Vector3.up * (eject ? Tuning.HullHeight * U : 0.4f);
            p.m_body.position = outPos; p.transform.position = outPos;
            p.m_body.linearVelocity = eject ? Vector3.up * (1500f * U) : Vector3.zero;   // eject launches the pilot skyward
            p.m_maxAirAltitude = outPos.y + 200f;
            PilotController.Local?.Motor.ResetVelocity();
            RestoreCamera();
            Arsenal.RestoreFov();
            Effects.Sound("titan:disembark", transform.position);
            if (!eject) Say("disembark", true);
            PilotController.Local?.Body?.HideNow();               // the renderer loop above re-enabled Valheim's body too
            visual.PlayAction("disembark");
        }

        private void RestoreCamera()
        {
            var cam = GameCamera.instance;
            if (cam != null && savedCamMax > 0f) { cam.m_maxDistance = savedCamMax; cam.m_distance = savedCamDist; savedCamMax = -1f; }
        }

        // IDoodadController ------------------------------------------------------
        public void OnUseStop(Player player) { if (Phase == State.Piloted) Disembark(false); }
        public void ApplyControlls(Vector3 moveDir, Vector3 lookDir, bool run, bool autoRun, bool block)
        { if (!ExternalDrive) Drive(moveDir, lookDir, run); }

        /// <summary>Self-test: when set, Valheim's per-frame doodad controls are ignored and only Drive() steers.</summary>
        public bool ExternalDrive;
        public void Drive(Vector3 moveDir, Vector3 lookDir, bool run) { inMove = moveDir; inLook = lookDir; inRun = run; }
        public Component GetControlledComponent() => this;
        public Vector3 GetPosition() => cockpit != null ? cockpit.position : transform.position;
        public bool IsValid() => this != null && Body != null && !Body.IsDead();

        // ------------------------------------------------------------ offhands
        private void FireSalvo()
        {
            float per = salvoDef.AmmoPerShot;
            int count = Mathf.Max(1, (int)salvoDef.BurstCount);
            if (SalvoAmmo < salvoDef.AmmoMinToFire) { Owner.Message(MessageHud.MessageType.TopLeft, "Salvo rockets recharging"); return; }
            SalvoAmmo -= per * count;
            salvoLeft = count;
            salvoNext = 0f;
        }

        private void UpdateSalvo()
        {
            if (salvoLeft <= 0 || Time.time < salvoNext) return;
            salvoNext = Time.time + 1f / Mathf.Max(1f, salvoDef.FireRate);
            salvoLeft--;
            Ray aim = Phase == State.Piloted ? new Ray(GameCamera.instance.transform.position, GameCamera.instance.transform.forward) : AiRay();
            Vector3 target = Physics.Raycast(aim, out var h, 400f, Character.s_groundRayMask | Character.s_characterLayerMask) ? h.point : aim.origin + aim.direction * 200f;
            Vector3 from = visual.RocketPod.position + Random.insideUnitSphere * 0.3f;
            Vector3 dir = (target - from).normalized + Random.insideUnitSphere * 0.02f;
            Arsenal.SpawnRocket(salvoDef, from, dir);
            Effects.Sound("fire", from);
        }

        private void DeploySmoke()
        {
            if (SmokeAmmo < smokeDef.AmmoMinToFire) { Owner.Message(MessageHud.MessageType.TopLeft, "Electric smoke recharging"); return; }
            SmokeAmmo -= smokeDef.AmmoPerShot;
            smokes.Add((transform.position + Vector3.up * 2f, Time.time + SmokeLifetime, Time.time));
            TitanVisual.SmokeCloud(transform.position + Vector3.up * 2f, SmokeOuterRadius * U, SmokeLifetime);
        }

        // mp_titanability_smoke.nut: lifetime 5, damage radius 320-375, 45 dps to pilot-size targets, after 1 s
        public const float SmokeLifetime = 5f, SmokeInnerRadius = 320f, SmokeOuterRadius = 375f, SmokeDps = 45f, SmokeDelay = 1f;
        private void UpdateSmokes(float dt)
        {
            for (int i = smokes.Count - 1; i >= 0; i--)
            {
                var s = smokes[i];
                if (Time.time > s.until) { smokes.RemoveAt(i); continue; }
                if (Time.time < s.start + SmokeDelay) continue;
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsDead() || c == Owner || c.m_faction == Character.Faction.Players || c.IsTamed()) continue;
                    float d = Vector3.Distance(c.GetCenterPoint(), s.pos) / U;
                    float f = d <= SmokeInnerRadius ? 1f : d >= SmokeOuterRadius ? 0f : 1f - (d - SmokeInnerRadius) / (SmokeOuterRadius - SmokeInnerRadius);
                    if (f <= 0f) continue;
                    var hit = new HitData();
                    hit.m_damage.m_lightning = SmokeDps * f * dt * Plugin.DamageScale.Value;
                    hit.m_point = c.GetCenterPoint();
                    hit.m_hitCollider = c.m_collider;
                    hit.SetAttacker(Owner);
                    c.Damage(hit);
                }
            }
        }

        private void StartCore()
        {
            CoreMeter = 0f;
            Say("core", true);
            CoreUntil = Motor.Time + coreDef.F("core_duration", 5.5f);
            Owner.Message(MessageHud.MessageType.Center, "Burst Core online");
        }

        // ------------------------------------------------------------------ AI
        private float AiDistToOwner() => Owner != null ? Vector3.Distance(Owner.transform.position, transform.position) / U : 0f;

        private void AiThink()
        {
            if (Time.time >= nextAiThink)
            {
                nextAiThink = Time.time + 0.5f;
                aiTarget = null;
                float best = AiRange * U;
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsDead() || c == Body || c.IsPlayer() || c.IsTamed() || c.m_faction == Character.Faction.Players) continue;
                    if (!BaseAI.IsEnemy(Body, c)) continue;
                    float d = Vector3.Distance(c.transform.position, transform.position);
                    if (d >= best) continue;
                    Vector3 eye = transform.position + Vector3.up * (Tuning.EyeHeight * U);
                    if (Physics.Linecast(eye, c.GetCenterPoint(), out var block, Character.s_blockedRayMask, QueryTriggerInteraction.Ignore)
                        && !block.collider.transform.IsChildOf(c.transform) && !block.collider.transform.IsChildOf(transform))
                    { AiDebug = $"{c.name} blocked by {block.collider.transform.root.name}"; continue; }
                    best = d; aiTarget = c;
                }
            }
            if (aiTarget != null && !aiTarget.IsDead())
            {
                if (aiTarget != lastAiTarget && Time.time - lastEngageLine > 20f && Say("engage")) lastEngageLine = Time.time;
                Arsenal.AimProvider = AiRay;
                Arsenal.TryFireNow();
                if (SalvoAmmo >= salvoDef.ClipSize && Random.value < 0.01f) FireSalvo();
            }
            else Arsenal.AimProvider = null;
        }

        public Character AiTarget => aiTarget;
        public string AiDebug = "";

        private Character lastAiTarget;
        private float stuckTime;
        public int Redeploys { get; private set; }

        /// <summary>
        /// The auto-titan follows in a straight line; when a forest or cliff holds him back he dashes to
        /// break free, and if he is still stuck (or the pilot is far away) he drops in again beside the pilot.
        /// </summary>
        private void UpdateStuck(float dt)
        {
            if (!Following || visual.Locked || Owner == null) { stuckTime = 0f; return; }
            float dist = AiDistToOwner();
            float speed = new Vector3(Motor.Vel.x, 0f, Motor.Vel.z).magnitude;
            bool wantsToMove = dist > FollowDist * 1.5f;
            if (wantsToMove && speed < 40f) stuckTime += dt; else stuckTime = Mathf.Max(0f, stuckTime - dt * 2f);
            if (stuckTime > 2f && stuckTime - dt <= 2f && Motor.Power >= Tuning.DodgePowerDrain) dashQueued = true;
            if (stuckTime > 8f || dist > 4000f) Redeploy();
        }

        /// <summary>Titanfall again, next to the pilot (landing damage spares the pilot as on the first drop).</summary>
        public void Redeploy()
        {
            if (Owner == null || Phase == State.Piloted || Phase == State.Dropping) return;
            Vector3 side = Vector3.ProjectOnPlane(transform.position - Owner.transform.position, Vector3.up);
            side = side.sqrMagnitude > 0.01f ? side.normalized : -Owner.transform.forward;
            Vector3 target = Owner.transform.position + side * 9f;
            // the surface beside the pilot (terrain, rock, a building roof...), not just the heightmap
            if (Physics.Raycast(target + Vector3.up * 8f, Vector3.down, out var gh, 40f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore))
                target.y = gh.point.y;
            else if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(target, out float gy)) target.y = gy;
            stuckTime = 0f; Redeploys++;
            Motor.ResetVelocity();
            Body.m_body.isKinematic = true;
            dropTo = target;
            dropFrom = target + Vector3.up * (DropHeight * U);
            Body.m_body.position = dropFrom; transform.position = dropFrom;
            dropStart = Time.time;
            Phase = State.Dropping;
            beacon = Effects.NewLine(0.25f, new Color(1f, 0.15f, 0.1f, 0.85f));
            Effects.Sound("titan:inbound", Owner.transform.position);
            if (visual.RealModel && AssetLibrary.TitanClips.TryGetValue("hotdrop", out var hd))
                visual.PlayAction("hotdrop", Mathf.Max(0f, hd.Mark - DropTime), 0f);
            Plugin.Log.LogInfo($"BT redeploys beside the pilot (stuck or {AiDistToOwner():0} u behind)");
        }

        private Ray AiRay()
        {
            Vector3 eye = chestGun != null ? chestGun.position : transform.position + Vector3.up * 4f;
            Vector3 at = aiTarget != null ? aiTarget.GetCenterPoint() : eye + transform.forward * 50f;
            return new Ray(eye, (at - eye).normalized);
        }

        private Vector3 AiLook()
        {
            if (aiTarget != null && !aiTarget.IsDead()) return (aiTarget.transform.position - transform.position).normalized;
            if (Following && Owner != null && AiDistToOwner() > FollowDist) return (Owner.transform.position - transform.position).normalized;
            return transform.forward;
        }

        private Vector3 AiMove()
        {
            if (!Following || Owner == null || AiDistToOwner() <= FollowDist) return Vector3.zero;
            Vector3 d = Owner.transform.position - transform.position; d.y = 0f;
            return d.normalized;
        }

        // ------------------------------------------------------------- damage
        /// <summary>Shields absorb damage first (healthShield), like Titanfall.</summary>
        public void OnDamaged(HitData hit)
        {
            lastDamaged = Motor != null ? Motor.Time : 0f;
            float total = hit.GetTotalDamage();
            if (total <= 0f || Shield <= 0f) return;
            float absorbed = Mathf.Min(Shield, total);
            Shield -= absorbed;
            if (Shield <= 0f) Say("critical", true);              // shields down
            float keep = (total - absorbed) / total;
            hit.m_damage.Modify(keep);
        }

        public void OnTitanDeath()
        {
            if (Phase == State.Piloted) Disembark(true);
            Effects.Explosion(transform.position + Vector3.up * 2f, Tuning.HotdropRadius * U);
            Owner?.Message(MessageHud.MessageType.Center, "Titan lost");
            TitanMeter.Fraction = 0f;
            dying = true;
            // Valheim still reads the ZDO after OnDeath, so the wreck goes at the earliest next frame;
            // the real BT falls with his death sequence first
            float fall = visual != null ? visual.PlayAction("death") : 0f;
            if (fall <= 0f && visual != null) visual.gameObject.SetActive(false);
            destroyAt = Time.time + Mathf.Min(fall, 2.2f);
        }

        private bool dying;
        private float destroyAt;

        private void OnDestroy()
        {
            // a Titan that was not destroyed in combat (its zone unloaded far behind the pilot, the
            // world closed) is still owed to the pilot: the meter comes back full
            if (!dying && Phase != State.Dropping) { TitanMeter.Fraction = 1f; TitanMeter.Store(Owner); }
            if (Phase == State.Piloted && Owner != null) { Owner.StopDoodadControl(); Owner.AttachStop(); RestoreCamera(); }
            Arsenal?.Destroy();
            if (beacon) Destroy(beacon.gameObject);
            if (Current == this) Current = null;
        }
    }
}
