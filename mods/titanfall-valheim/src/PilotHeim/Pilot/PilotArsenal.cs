using System.Collections.Generic;
using System.IO;
using PilotHeim.Data;
using UnityEngine;

namespace PilotHeim.Pilot
{
    /// <summary>
    /// Pilot weapons, tactical and ordnance, all driven by the user's Titanfall 2
    /// weapon scripts (scripts/weapons/*.txt). Damage lands as Valheim HitData
    /// (pierce for bullets, blunt for explosions) so armour, resistances, skills,
    /// loot and death all stay Valheim's.
    /// </summary>
    public sealed class PilotArsenal
    {
        private const float U = PilotTuning.MetersPerUnit;
        public enum TacticalKind { Grapple, Cloak, Stim, PulseBlade }
        private const float StimSpeedBoost = 1f + 0.4f * 2f;   // sh_stim.gnut: severity 0.4 x movement_speedboost_extraScale 2.0
        private const float SonarPulseInterval = 1.3333f;       // mp_weapon_grenade_sonar.nut

        private readonly Player p;
        private readonly IMoverState m;
        private readonly PilotGrapple grapple;   // pilot only
        private readonly PilotTuning t;
        public readonly List<WeaponDef> Loadout = new List<WeaponDef>();
        public int Current;
        public bool Drawn;
        public WeaponDef Weapon => Override ?? (Loadout.Count > 0 ? Loadout[Current] : null);
        public readonly TacticalKind Tactical;
        public readonly WeaponDef TacticalDef, OrdnanceDef;
        private readonly int[] clips;

        private float nextFire, reloadEnd, lastFire, spreadKick, adsFrac, deployEnd;
        public bool Reloading => reloadEnd > 0f;
        public float AdsFrac => adsFrac;
        public float TacticalAmmo = 200f, OrdnanceAmmo = 200f;
        public float CloakedUntil, StimUntil;
        public bool Cloaked => m.Time < CloakedUntil;
        public bool Stimmed => m.Time < StimUntil;
        private bool semiLatched;
        private float baseFov = -1f;

        private readonly List<Bolt> bolts = new List<Bolt>();
        private readonly List<Grenade> grenades = new List<Grenade>();
        public readonly Dictionary<Character, float> Revealed = new Dictionary<Character, float>();
        /// <summary>Self-test probe: (damage before Valheim modifiers, target, distance in u).</summary>
        public System.Action<float, Character, float> OnHit;
        public int ShotsFired;
        public void SetCloakForTest(float seconds) => CloakedUntil = m.Time + seconds;

        private sealed class Bolt { public Vector3 Pos, Vel; public float Traveled, Gravity; public WeaponDef W; public LineRenderer Trail; }
        private sealed class Grenade
        {
            public Rigidbody Body; public float Explode; public WeaponDef W; public bool Sonar; public bool Stuck; public float NextPulse, Expire;
        }

        public PilotArsenal(Player p, IMoverState m, PilotTuning t, string weaponsDir, string[] loadout, TacticalKind tactical, bool sp,
                            PilotGrapple grapple = null, string ordnanceId = "mp_weapon_frag_grenade")
        {
            this.p = p; this.m = m; this.t = t; this.grapple = grapple;
            foreach (var id in loadout) Loadout.Add(WeaponDef.Load(weaponsDir, id.Trim(), sp));
            clips = new int[Loadout.Count];
            for (int i = 0; i < clips.Length; i++) clips[i] = (int)Loadout[i].ClipSize;
            Tactical = tactical;
            string tacId = tactical == TacticalKind.Cloak ? "mp_ability_cloak" : tactical == TacticalKind.Stim ? "mp_ability_heal"
                         : tactical == TacticalKind.PulseBlade ? "mp_weapon_grenade_sonar" : null;
            if (tacId != null) TacticalDef = WeaponDef.Load(weaponsDir, tacId, sp);
            OrdnanceDef = ordnanceId != null ? WeaponDef.Load(weaponsDir, ordnanceId, sp) : null;
            Effects.Init();
        }

        public int Clip => clips.Length > 0 ? clips[Current] : 0;

        /// <summary>Where shots leave from; the Titan overrides this with its chest gun.</summary>
        public System.Func<Vector3> MuzzleProvider;
        /// <summary>Every character this arsenal's bullets or explosions hit (BT uses it for kill callouts).</summary>
        public event System.Action<Character> HitCharacter;
        /// <summary>Colliders to ignore (the Titan's own body).</summary>
        public Transform IgnoreRoot;
        /// <summary>Aim ray provider (AI Titans); default is the camera.</summary>
        public System.Func<Ray> AimProvider;
        /// <summary>Swap the current weapon's definition temporarily (Titan core).</summary>
        public WeaponDef Override;

        /// <summary>Movement speed multiplier from ADS and stim, read by the motor.</summary>
        public float SpeedScale => Mathf.Lerp(1f, Weapon != null ? Weapon.AdsMoveSpeedScale : 1f, adsFrac) * (Stimmed ? StimSpeedBoost : 1f);

        // ------------------------------------------------------------- frame tick
        public void Update(float dt, bool input, bool fireHeld, bool adsHeld, bool reload, bool toggle, bool swap,
                           bool tactical, bool ordnance)
        {
            // regenerate offhands (regen_ammo_refill_rate per second, 200 max)
            if (TacticalDef != null && !Cloaked && !Stimmed) TacticalAmmo = Mathf.Min(200f, TacticalAmmo + TacticalDef.RegenRate * dt);
            if (OrdnanceDef != null) OrdnanceAmmo = Mathf.Min(200f, OrdnanceAmmo + OrdnanceDef.RegenRate * dt);
            if (Stimmed) p.Heal(p.GetMaxHealth() / Mathf.Max(0.5f, TacticalDef.FireDuration) * dt, false);
            ApplyCloakVisual();

            if (input && toggle) { Drawn = !Drawn; deployEnd = m.Time + (Weapon != null ? Weapon.DeployTime : 0f); reloadEnd = 0f; }
            // swap only while the gun is out: holstered, X stays Valheim's sit
            if (input && swap && Drawn && Loadout.Count > 1) { Current = (Current + 1) % Loadout.Count; deployEnd = m.Time + Weapon.DeployTime; reloadEnd = 0f; }
            if (input && tactical) UseTactical();
            if (input && ordnance && OrdnanceDef != null) ThrowGrenade(OrdnanceDef, false);

            var w = Weapon;
            UpdateAds(dt, Drawn && input && adsHeld && w != null);
            if (!Drawn || w == null) return;

            if (Reloading && m.Time >= reloadEnd) { clips[Current] = (int)w.ClipSize; reloadEnd = 0f; }
            if (input && reload && !Reloading && clips[Current] < w.ClipSize) StartReload(false);

            // spread kick decay
            if (m.Time - lastFire > w.SpreadDecayDelay) spreadKick = Mathf.Max(0f, spreadKick - w.SpreadDecayRate * dt);

            if (!fireHeld) semiLatched = false;
            if (input && fireHeld && !Reloading && m.Time >= deployEnd && m.Time >= nextFire && !(w.SemiAuto && semiLatched))
            {
                if (clips[Current] <= 0) { StartReload(true); return; }
                Fire(w);
                semiLatched = true;
            }
        }

        public void FixedTick(float dt)
        {
            for (int i = bolts.Count - 1; i >= 0; i--) if (StepBolt(bolts[i], dt)) { if (bolts[i].Trail) Object.Destroy(bolts[i].Trail.gameObject); bolts.RemoveAt(i); }
            for (int i = grenades.Count - 1; i >= 0; i--) if (StepGrenade(grenades[i])) grenades.RemoveAt(i);
            var dead = new List<Character>();
            foreach (var kv in Revealed) if (kv.Key == null || m.Time > kv.Value) dead.Add(kv.Key);
            foreach (var c in dead) Revealed.Remove(c);
        }

        // ------------------------------------------------------------------ firing
        private void Fire(WeaponDef w)
        {
            if (Override == null) clips[Current] -= Mathf.Max(1, (int)w.AmmoPerShot);
            ShotsFired++;
            nextFire = m.Time + 1f / Mathf.Max(0.01f, w.FireRate);
            lastFire = m.Time;
            Ray ray = AimProvider != null ? AimProvider() : new Ray(GameCamera.instance.transform.position, GameCamera.instance.transform.forward);
            Vector3 origin = ray.origin, aim = ray.direction;
            Vector3 muzzle = MuzzleProvider != null ? MuzzleProvider()
                           : p.transform.position + Vector3.up * 1.45f + p.transform.right * 0.25f + p.transform.forward * 0.4f;
            float spread = CurrentSpread(w);

            if (w.IsShotgun) ShotgunBlast(w, origin, aim, muzzle, spread);
            else if (w.IsProjectile) SpawnBolt(w, muzzle, RandomInCone(aim, spread * 0.5f));
            else HitscanBullet(w, origin, RandomInCone(aim, spread * 0.5f), muzzle);

            // spread kick + view kick
            bool ads = adsFrac > 0.5f;
            spreadKick = Mathf.Min(spreadKick + (ads ? w.SpreadKickAds : w.SpreadKickHip), ads ? w.SpreadMaxKickAds : w.SpreadMaxKickHip);
            float kickScale = ads ? 1.5f : 1.75f;
            p.m_lookPitch = Mathf.Clamp(p.m_lookPitch + (w.KickPitchBase + Random.Range(-w.KickPitchRandom, w.KickPitchRandom) * 0.5f) * kickScale, -89f, 89f);
            p.m_lookYaw *= Quaternion.Euler(0f, (w.KickYawBase + Random.Range(-w.KickYawRandom, w.KickYawRandom)) * kickScale * 0.5f, 0f);
            Effects.Muzzle(muzzle, aim);
            Effects.Sound("fire:" + w.Id, muzzle);
            if (Override == null && clips[Current] <= 0) StartReload(true);
        }

        /// <summary>Fire one shot now regardless of input (AI Titans and burst offhands).</summary>
        /// <summary>Reload completion and spread recovery without player input (AI Titan); leaves the camera alone.</summary>
        public void BackgroundTick(float dt)
        {
            var w = Weapon;
            if (w == null) return;
            if (Reloading && m.Time >= reloadEnd) { clips[Current] = (int)w.ClipSize; reloadEnd = 0f; }
            if (m.Time - lastFire > w.SpreadDecayDelay) spreadKick = Mathf.Max(0f, spreadKick - w.SpreadDecayRate * dt);
        }

        public bool TryFireNow()
        {
            var w = Weapon;
            if (w == null || Reloading || m.Time < nextFire || m.Time < deployEnd) return false;
            if (Override == null && clips[Current] <= 0) { StartReload(true); return false; }
            Fire(w);
            return true;
        }

        /// <summary>Launch an explosive rocket (salvo rockets, core shells).</summary>
        public void SpawnRocket(WeaponDef w, Vector3 from, Vector3 dir)
        {
            float speed = w.F("projectile_launch_speed", w.BoltSpeed > 0f ? w.BoltSpeed : 4000f);
            var b = new Bolt { Pos = from, Vel = dir.normalized * speed, W = w, Gravity = 0f };
            b.Trail = Effects.NewLine(0.08f, new Color(1f, 0.6f, 0.25f, 0.9f));
            bolts.Add(b);
        }

        private float CurrentSpread(WeaponDef w)
        {
            bool air = !m.OnGround && !m.Wallrunning;
            float hip = air ? w.SpreadAirHip : m.Crouched ? w.SpreadCrouchHip : m.Sprinting ? w.SpreadSprintHip : w.SpreadHip;
            float ads = air ? w.SpreadAirAds : w.SpreadAds;
            return Mathf.Lerp(hip, ads, adsFrac) + spreadKick;
        }

        private void StartReload(bool empty)
        {
            var w = Weapon;
            reloadEnd = m.Time + (empty ? w.ReloadEmptyTime : w.ReloadTime);
            Effects.Sound("reload", p.transform.position);
        }

        private void HitscanBullet(WeaponDef w, Vector3 origin, Vector3 dir, Vector3 muzzle)
        {
            float range = Mathf.Max(w.VeryFarDist, w.FarDist) * 1.5f * U + 10f;
            if (FirstHit(origin, dir, range, out var hit))
            {
                float dist = Vector3.Distance(muzzle, hit.point) / U;
                ApplyBullet(w, hit.collider, hit.point, dir, w.DamageAt(dist));
                Effects.Tracer(muzzle, hit.point);
                Effects.Impact(hit.point, hit.normal);
            }
            else Effects.Tracer(muzzle, origin + dir * range);
        }

        // _weapon_utility.nut ShotgunBlast: up to 8 entities in a cone of half the spread each take one hit
        private void ShotgunBlast(WeaponDef w, Vector3 origin, Vector3 aim, Vector3 muzzle, float spread)
        {
            float maxAngle = Mathf.Max(0.5f, spread * 0.5f);
            float maxDist = w.FarDist * U;
            int blasts = 8;
            var seen = new HashSet<IDestructible>();
            foreach (var col in Physics.OverlapSphere(origin, maxDist, Character.s_characterLayerMask | Character.s_groundRayMask, QueryTriggerInteraction.Ignore))
            {
                if (blasts <= 0) break;
                if (col.GetComponentInParent<Player>() == p) continue;
                var dest = col.GetComponentInParent<IDestructible>();
                if (!Damageable(dest) || seen.Contains(dest)) continue;
                Vector3 c = col.bounds.center;
                if (Vector3.Angle(aim, c - origin) > maxAngle * 1.1f) continue;
                if (!FirstHit(origin, (c - origin).normalized, maxDist + 2f, out var hit) || hit.collider.GetComponentInParent<IDestructible>() != dest) continue;
                seen.Add(dest);
                blasts--;
                float dist = Vector3.Distance(muzzle, hit.point) / U;
                ApplyBullet(w, hit.collider, hit.point, (hit.point - origin).normalized, w.DamageAt(dist));
                Effects.Tracer(muzzle, hit.point);
                Effects.Impact(hit.point, hit.normal);
            }
            for (int i = 0; i < Mathf.Min(blasts, 8); i++)
            {
                Vector3 d = RandomInCone(aim, maxAngle);
                Effects.Tracer(muzzle, FirstHit(origin, d, maxDist, out var h) ? h.point : origin + d * maxDist);
            }
        }

        private void SpawnBolt(WeaponDef w, Vector3 muzzle, Vector3 dir)
        {
            var b = new Bolt { Pos = muzzle, Vel = dir * w.BoltSpeed, W = w, Gravity = w.BoltGravity ? w.BoltGravityAmount * t.Gravity : 0f };
            b.Trail = Effects.NewLine(0.03f, new Color(1f, 0.85f, 0.55f, 0.9f));
            bolts.Add(b);
        }

        public string LastBoltImpact = "none";

        private bool StepBolt(Bolt b, float dt)
        {
            b.Vel.y -= b.Gravity * dt;
            Vector3 step = b.Vel * dt * U;
            float len = step.magnitude;
            if (FirstHit(b.Pos, step / len, len, out var hit))
            {
                b.Traveled += hit.distance / U;
                LastBoltImpact = $"{hit.collider.transform.root.name}/{hit.collider.name} at {b.Traveled:0} u";
                ApplyBullet(b.W, hit.collider, hit.point, step / len, b.W.DamageAt(b.Traveled));
                if (b.W.ExplosionDamage > 0f && b.W.ExplosionRadius > 0f) Explode(hit.point, b.W);
                else Effects.Impact(hit.point, hit.normal);
                return true;
            }
            if (b.Trail) { b.Trail.SetPosition(0, b.Pos); b.Trail.SetPosition(1, b.Pos + step); }
            b.Pos += step;
            b.Traveled += len / U;
            return b.Traveled > 40000f;
        }

        private bool FirstHit(Vector3 origin, Vector3 dir, float range, out RaycastHit best)
        {
            best = default;
            float bestD = float.MaxValue;
            var hits = Physics.RaycastAll(origin, dir, range, Character.s_characterLayerMask | Character.s_groundRayMask, QueryTriggerInteraction.Ignore);
            foreach (var h in hits)
            {
                if (h.distance >= bestD) continue;
                if (h.collider.GetComponentInParent<Player>() == p) continue;
                if (IgnoreRoot != null && h.collider.transform.IsChildOf(IgnoreRoot)) continue;
                best = h; bestD = h.distance;
            }
            return bestD < float.MaxValue;
        }

        private static readonly System.Collections.Generic.HashSet<string> headlessLogged = new System.Collections.Generic.HashSet<string>();

        /// <summary>Head position; creatures with a non-humanoid rig have no head bone, so use the top of the capsule.</summary>
        private static Vector3 HeadPoint(Character c, float headR)
        {
            if (c.m_head != null) return c.m_head.position;
            if (headlessLogged.Add(c.name)) Plugin.Log.LogInfo($"no head bone on {c.name}; headshots use the capsule top");
            return c.GetTopPoint() - Vector3.up * headR;
        }

        /// <summary>Building pieces are only hit when the WeaponsDamageBuildings option is on.</summary>
        public static bool Damageable(IDestructible dest) =>
            dest != null && (Plugin.WeaponsDamageBuildings.Value || !(dest is WearNTear));

        private void ApplyBullet(WeaponDef w, Collider col, Vector3 point, Vector3 dir, float damage)
        {
            var dest = col.GetComponentInParent<IDestructible>();
            if (!Damageable(dest)) return;
            var target = col.GetComponentInParent<Character>();
            if (target != null)
            {
                if (target == p || target.IsTamed() || (target.IsPlayer() && !p.IsPVPEnabled())) return;
                if (target.m_faction == Character.Faction.Players && !target.IsPlayer()) return;   // your own Titan
                // headshot: within the head sphere of the target
                float headR = Mathf.Max(0.18f, target.GetRadius() * 0.6f);
                if (Vector3.Distance(point, HeadPoint(target, headR)) <= headR) damage *= w.HeadshotScale;
            }
            OnHit?.Invoke(damage, target, Vector3.Distance(p.transform.position, point) / U);
            if (target != null) HitCharacter?.Invoke(target);
            var hit = new HitData();
            hit.m_damage.m_pierce = damage * Plugin.DamageScale.Value;
            hit.m_point = point;
            hit.m_dir = dir;
            hit.m_pushForce = w.ImpulseForce * 0.02f;
            hit.m_skill = Skills.SkillType.Crossbows;
            hit.m_hitCollider = col;
            hit.SetAttacker(p);
            dest.Damage(hit);
            if (target != null) p.RaiseSkill(Skills.SkillType.Crossbows, 0.2f);
        }

        // ------------------------------------------------------------- tactical
        private void UseTactical()
        {
            if (Tactical == TacticalKind.Grapple)
            {
                var cam = GameCamera.instance.transform;
                grapple?.Fire(cam.position, cam.forward);
                return;
            }
            var d = TacticalDef;
            if (TacticalAmmo < d.AmmoMinToFire) { p.Message(MessageHud.MessageType.TopLeft, $"{Tactical} recharging"); return; }
            TacticalAmmo -= d.AmmoPerShot;
            switch (Tactical)
            {
                case TacticalKind.Cloak: CloakedUntil = m.Time + d.FireDuration; Effects.Sound("cloak", p.transform.position); break;
                case TacticalKind.Stim: StimUntil = m.Time + d.FireDuration; Effects.Sound("stim", p.transform.position); break;
                case TacticalKind.PulseBlade: ThrowGrenade(d, true); break;
            }
        }

        private readonly List<Renderer> cloakHidden = new List<Renderer>();
        private void ApplyCloakVisual()
        {
            if (Cloaked)
            {
                if (cloakHidden.Count == 0)
                    foreach (var r in p.m_visual.GetComponentsInChildren<Renderer>(false))
                        if (r.enabled) { r.enabled = false; cloakHidden.Add(r); }
                bool shimmer = Time.frameCount / 6 % 9 == 0;          // faint shimmer, like the Titanfall cloak
                foreach (var r in cloakHidden) if (r) r.enabled = shimmer;
            }
            else if (cloakHidden.Count > 0)
            {
                foreach (var r in cloakHidden) if (r) r.enabled = true;
                cloakHidden.Clear();
            }
        }

        // ------------------------------------------------------------ grenades
        private void ThrowGrenade(WeaponDef w, bool sonar)
        {
            if (!sonar)
            {
                if (OrdnanceAmmo < w.AmmoMinToFire) { p.Message(MessageHud.MessageType.TopLeft, "Frag recharging"); return; }
                OrdnanceAmmo -= w.AmmoPerShot;
            }
            var cam = GameCamera.instance.transform;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = sonar ? "PilotHeim_PulseBlade" : "PilotHeim_Frag";
            go.transform.localScale = Vector3.one * (sonar ? 0.12f : 0.16f);
            go.transform.position = p.transform.position + Vector3.up * 1.5f + cam.forward * 0.6f;
            var rend = go.GetComponent<Renderer>();
            if (rend != null) rend.material.color = sonar ? new Color(1f, 0.55f, 0.1f) : new Color(0.25f, 0.3f, 0.2f);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
            rb.linearDamping = 0.05f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            foreach (var pc in p.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(go.GetComponent<Collider>(), pc);
            rb.linearVelocity = (cam.forward + Vector3.up * 0.12f).normalized * w.LaunchSpeed * U + p.m_body.linearVelocity;
            var g = new Grenade { Body = rb, W = w, Sonar = sonar };
            if (sonar) { g.Expire = m.Time + 30f; }
            else g.Explode = m.Time + w.FuseTime;
            grenades.Add(g);
            Effects.Sound("throw", go.transform.position);
        }

        private bool StepGrenade(Grenade g)
        {
            if (g.Body == null) return true;
            if (g.Sonar)
            {
                if (!g.Stuck && Physics.CheckSphere(g.Body.position, 0.15f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore))
                {
                    g.Stuck = true;
                    g.Body.isKinematic = true;
                    g.NextPulse = m.Time;
                    g.Expire = m.Time + g.W.F("grenade_ignition_time", 4.5f);
                }
                if (g.Stuck && m.Time >= g.NextPulse)
                {
                    g.NextPulse = m.Time + SonarPulseInterval;
                    float r = g.W.ExplosionRadius * U;
                    foreach (var c in Character.GetAllCharacters())
                        if (c != p && !c.IsDead() && !c.IsTamed() && Vector3.Distance(c.transform.position, g.Body.position) <= r)
                            Revealed[c] = m.Time + SonarPulseInterval + 0.5f;
                    Effects.Pulse(g.Body.position, r);
                    Effects.Sound("sonar", g.Body.position);
                }
                if (m.Time > g.Expire) { Object.Destroy(g.Body.gameObject); return true; }
                return false;
            }
            if (m.Time < g.Explode) return false;
            Explode(g.Body.position, g.W);
            Object.Destroy(g.Body.gameObject);
            return true;
        }

        public void Explode(Vector3 pos, WeaponDef w)
        {
            float outer = w.ExplosionRadius * U, inner = w.ExplosionInnerRadius * U;
            var done = new HashSet<IDestructible>();
            foreach (var col in Physics.OverlapSphere(pos, outer, Character.s_characterLayerMask | Character.s_groundRayMask, QueryTriggerInteraction.Ignore))
            {
                var dest = col.GetComponentInParent<IDestructible>();
                if (!Damageable(dest) || !done.Add(dest)) continue;
                var tc = col.GetComponentInParent<Character>();
                if (tc != null && (tc.IsTamed() || (tc.m_faction == Character.Faction.Players && !tc.IsPlayer()))) continue;
                if (IgnoreRoot != null && col.transform.IsChildOf(IgnoreRoot)) continue;
                if (tc == p && w.S("projectile_damages_owner", "1") == "0") continue;
                Vector3 c = col.ClosestPoint(pos);
                float d = Vector3.Distance(pos, c);
                float frac = d <= inner ? 1f : Mathf.Clamp01(1f - (d - inner) / Mathf.Max(0.01f, outer - inner));
                if (frac <= 0f) continue;
                OnHit?.Invoke(w.ExplosionDamage * frac, tc, d / U);
                var hit = new HitData();
                hit.m_damage.m_blunt = w.ExplosionDamage * frac * Plugin.DamageScale.Value;
                hit.m_point = c;
                hit.m_dir = (c - pos).normalized;
                hit.m_pushForce = w.ImpulseForce * 0.05f * frac;
                hit.m_hitCollider = col;
                hit.SetAttacker(p);
                dest.Damage(hit);
            }
            Effects.Explosion(pos, outer);
        }

        // --------------------------------------------------------------- helpers
        private void UpdateAds(float dt, bool held)
        {
            var w = Weapon;
            float speed = w != null ? 1f / Mathf.Max(0.05f, w.ZoomTimeIn) : 5f;
            adsFrac = Mathf.MoveTowards(adsFrac, held ? 1f : 0f, speed * dt);
            var gc = GameCamera.instance;
            if (gc == null) return;
            if (baseFov < 0f) baseFov = gc.m_fov;
            // Titanfall zoom_fov is relative to its 70-degree default
            float target = w != null ? baseFov * w.ZoomFov / 70f : baseFov;
            gc.m_fov = Mathf.Lerp(baseFov, target, adsFrac);
        }

        public void RestoreFov()
        {
            if (baseFov > 0f && GameCamera.instance != null) GameCamera.instance.m_fov = baseFov;
        }

        private static Vector3 RandomInCone(Vector3 dir, float halfAngleDeg)
        {
            if (halfAngleDeg <= 0.001f) return dir;
            float a = Mathf.Sqrt(Random.value) * halfAngleDeg;
            float r = Random.value * 360f;
            var q = Quaternion.LookRotation(dir) * Quaternion.Euler(Mathf.Sin(r * Mathf.Deg2Rad) * a, Mathf.Cos(r * Mathf.Deg2Rad) * a, 0f);
            return q * Vector3.forward;
        }

        public void Destroy()
        {
            foreach (var b in bolts) if (b.Trail) Object.Destroy(b.Trail.gameObject);
            foreach (var g in grenades) if (g.Body) Object.Destroy(g.Body.gameObject);
            bolts.Clear(); grenades.Clear();
            RestoreFov();
            CloakedUntil = 0f;
            ApplyCloakVisual();
        }

        public static string WeaponsDir => Path.Combine(Plugin.ScriptsDir, "weapons");
    }
}
