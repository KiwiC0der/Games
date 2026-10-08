using System.Collections.Generic;
using PilotHeim.Data;
using UnityEngine;

namespace PilotHeim.Pilot
{
    /// <summary>
    /// Titanfall 2 pilot movement running inside Valheim's Character physics tick.
    ///
    /// The tick order and every formula mirror Titanfall 2's server movement code
    /// (reverse engineered from server.dll): StartGravity, jump, grapple,
    /// WalkMove (ground or wall) / AirMove, FinishGravity, surface clip. State is
    /// kept in Source units (u, u/s) with Unity's axes (y up); PhysX replaces
    /// Source's TryPlayerMove for collision, so velocity is re-read from the
    /// rigidbody at the start of each tick.
    /// </summary>
    public sealed class PilotMotor : IMoverState
    {
        private const float U = PilotTuning.MetersPerUnit;
        private const float NonJumpVelocity = 140f;          // CategorizePosition: rising faster than this is airborne
        private const float WalkableNormalY = 0.7f;           // DAT_180880e2c
        private const float DecelFallbackScale = 0.6f;        // DAT_18087e114

        private readonly Player player;
        private readonly PilotTuning t;
        public readonly PilotGrapple Grapple;
        public PilotArsenal Arsenal;

        // --- kinematic state (Source units, Unity axes) ---
        public Vector3 Vel;
        public bool OnGround { get => onGround; set => onGround = value; }
        private bool onGround;
        public Vector3 GroundNormal = Vector3.up;
        public bool Wallrunning { get => wallrunning; set => wallrunning = value; }
        private bool wallrunning;
        public Vector3 WallNormal;
        public Collider WallCollider;
        public bool Sliding;
        public bool Crouched { get => crouched; set => crouched = value; }
        private bool crouched;
        public bool Sprinting { get => sprinting; set => sprinting = value; }
        private bool sprinting;
        public float Time { get => time; set => time = value; }
        private float time;

        private float wallrunStart, pushAwayStart, wallRetryUntil;
        private bool sameWall;
        private int wallrunCount;
        private readonly List<Vector3> wallNormalsSinceGround = new List<Vector3>();
        private Vector3 lastWallrunPos;
        private float lastSlideStart = -100f, slideBoost;
        private float leftGroundTime = -100f, landTime = -100f;
        private bool jumpedSinceGround;
        private int superjumpsUsed;
        private Vector3 expectedBodyVel;                       // what we handed PhysX last tick (u/s)
        private bool haveExpected;
        private bool jumpQueued, jumpHeld;
        private float forwardMove, sideMove;
        private Vector3 viewFwd, viewRight, yawFwd;
        public float ViewRoll;                                 // wallrun camera tilt (degrees)

        private string lastEvent = "";
        public float LastEventTime;
        /// <summary>Last movement event (jump, doublejump, wallrun, slide, land, grapple_*); each plays its Titanfall sound.</summary>
        public string LastEvent
        {
            get => lastEvent;
            set { lastEvent = value; LastEventTime = UnityEngine.Time.time; if (value != null && player != null) Effects.Sound("move:" + value.Split(':')[0], player.transform.position); }
        }

        public PilotMotor(Player player, PilotTuning tuning)
        {
            this.player = player;
            t = tuning;
            Grapple = new PilotGrapple(this, player, tuning);
        }

        /// <summary>Scripted input for the self-test (null = read the real player input).</summary>
        public sealed class InputState { public float Forward, Side; public bool Sprint, Crouch; public Vector3 Look = Vector3.forward; }
        public InputState Override;

        public void QueueJump() => jumpQueued = true;
        /// <summary>Teleports invalidate the velocity bookkeeping.</summary>
        public void ResetVelocity() { Vel = Vector3.zero; haveExpected = false; }
        public void SetJumpHeld(bool held) => jumpHeld = held;

        /// <summary>One physics tick. Returns false if the pilot motor should not run (Valheim handles it).</summary>
        public void FixedStep(float dt, bool crouchHeld)
        {
            Time += dt;
            var body = player.m_body;
            // Source moves the player with the mid-step velocity (after StartGravity, before
            // FinishGravity) but carries the end-of-step velocity into the next tick. PhysX moves
            // with whatever we hand it, so hand it the mid-step value and fold back only what
            // collisions changed.
            Vector3 actual = body.linearVelocity / U;
            if (haveExpected) Vel += actual - expectedBodyVel;
            else Vel = actual;

            ReadInput();
            CategorizePosition(dt);
            UpdateCrouch(crouchHeld);

            // 1. StartGravity
            ApplyGravity(dt * 0.5f);

            // 2. Jump
            if (jumpQueued) { CheckJump(); jumpQueued = false; }

            // 3. Grapple
            Grapple.Tick(dt, OnGround, Wallrunning);

            // 4. Move
            if (OnGround || Wallrunning) WalkMove(dt, crouchHeld);
            else { AirMove(dt); TryStartWallrun(); }

            // the position update uses this velocity (TryPlayerMove happens between the gravity halves)
            Vector3 moveVel = Vel;
            if (OnGround) moveVel -= Vector3.Dot(moveVel, GroundNormal) * GroundNormal;
            else if (Wallrunning) moveVel -= Vector3.Dot(moveVel, WallNormal) * WallNormal;

            // 5. FinishGravity
            ApplyGravity(dt * 0.5f);

            // 6. Clip into the surface we stand/run on
            if (OnGround) Vel -= Vector3.Dot(Vel, GroundNormal) * GroundNormal;
            else if (Wallrunning) Vel -= Vector3.Dot(Vel, WallNormal) * WallNormal;

            ClampVelocity();
            StepUp(dt);
            body.useGravity = false;
            body.linearVelocity = moveVel * U;
            expectedBodyVel = moveVel;
            haveExpected = true;
            UpdateViewRoll(dt);
        }

        // ------------------------------------------------------------------ input
        private void ReadInput()
        {
            if (Override != null)
            {
                viewFwd = Override.Look.normalized;
                yawFwd = Vector3.ProjectOnPlane(viewFwd, Vector3.up).normalized;
                viewRight = Vector3.Cross(Vector3.up, yawFwd).normalized;
                forwardMove = Override.Forward; sideMove = Override.Side;
                Sprinting = Override.Sprint && forwardMove > 0.1f && !Crouched;
                player.m_lookDir = viewFwd;
                player.m_lookYaw = Quaternion.LookRotation(yawFwd);
                player.m_lookPitch = -Mathf.Asin(Mathf.Clamp(viewFwd.y, -1f, 1f)) * Mathf.Rad2Deg;
                player.m_moveDir = yawFwd * forwardMove + viewRight * sideMove;
                return;
            }
            Vector3 look = player.m_lookDir.sqrMagnitude > 0.0001f ? player.m_lookDir.normalized : player.transform.forward;
            viewFwd = look;
            yawFwd = Vector3.ProjectOnPlane(look, Vector3.up);
            if (yawFwd.sqrMagnitude < 1e-6f) yawFwd = player.transform.forward;
            yawFwd.Normalize();
            viewRight = Vector3.Cross(Vector3.up, yawFwd).normalized;
            Vector3 md = player.m_moveDir;
            forwardMove = Mathf.Clamp(Vector3.Dot(md, yawFwd), -1f, 1f);
            sideMove = Mathf.Clamp(Vector3.Dot(md, viewRight), -1f, 1f);
            // Titanfall sprint needs forward input
            Sprinting = player.m_run && forwardMove > 0.1f && !Crouched && !player.IsEncumbered();
        }

        // ------------------------------------------------------- ground detection
        private void CategorizePosition(float dt)
        {
            bool was = OnGround;
            var col = player.m_collider;
            float radius = col.radius * 0.95f;
            Vector3 origin = player.transform.position + Vector3.up * (radius + 0.05f);
            bool hit = Physics.SphereCast(origin, radius, Vector3.down, out var gh, 0.05f + 2f * U + 0.02f,
                                          Character.s_groundRayMask, QueryTriggerInteraction.Ignore);
            bool rising = Vel.y > NonJumpVelocity;
            OnGround = hit && gh.normal.y >= WalkableNormalY && !rising && !Grapple.LiftsOffGround;
            if (OnGround)
            {
                GroundNormal = gh.normal;
                if (!was) OnLand();
                if (Wallrunning) EndWallrun("ground");
            }
            else
            {
                if (was) leftGroundTime = Time;
            }
        }

        private void OnLand()
        {
            landTime = Time;
            superjumpsUsed = 0;
            jumpedSinceGround = false;
            wallNormalsSinceGround.Clear();
            wallrunCount = 0;
            LastEvent = "land";
        }

        // --------------------------------------------------------------- gravity
        private void ApplyGravity(float halfDt)
        {
            float g = 1f;
            if (Wallrunning) g = WallrunGravityFactor(Time - wallrunStart);
            if (Grapple.Attached) g *= Grapple.GravityFraction(forwardMove, sideMove, viewFwd);
            Vel.y -= t.Gravity * t.GravityScale * halfDt * g;
        }

        // FUN_180186c70: gravity ramps in over wallrun_gravityRampUpTime unless this is the same wall again
        private float WallrunGravityFactor(float timeOnWall)
        {
            float ramp = t.WrGravityRampUpTime;
            if (timeOnWall < 0f) timeOnWall = 0f;
            if (timeOnWall >= ramp) return 1f;
            float strengthLoss = sameWall ? 0f : 1f;
            return 1f - (1f - timeOnWall / ramp) * strengthLoss;
        }

        // ------------------------------------------------------------------ speed
        // Valheim's own movement modifiers (water, attacking, status effects, armour), as a factor.
        // These play the role of Titanfall's speed-scale getters (FUN_1805d2510 / FUN_18016e9e0).
        private float SpeedModifiers()
        {
            float vs = 1f;
            player.ApplyLiquidResistance(ref vs);
            vs *= player.GetAttackSpeedFactorMovement();
            player.m_seman.ApplyStatusEffectSpeedMods(ref vs, player.m_moveDir);
            vs *= Mathf.Clamp01(1f + player.GetEquipmentMovementModifier());
            if (Arsenal != null) vs *= Arsenal.SpeedScale;           // ADS slow-down, stim boost
            return vs;
        }

        private float MaxSpeed()
        {
            float s = Crouched ? t.CrouchSpeed : (Sprinting ? t.SprintSpeed : t.Speed);   // sliding is a crouched stance
            s *= SpeedModifiers();
            if (player.IsEncumbered()) s = Mathf.Min(s, t.CrouchSpeed);
            return s;
        }

        // FUN_18016ea60
        private float GroundAccel(float speedSqr)
        {
            if (Sliding) return t.SlideAccel;
            if (t.SprintAcceleration >= 0f && speedSqr > (t.Speed - 1f) * (t.Speed - 1f)) return t.SprintAcceleration;
            if (t.LowAcceleration >= 0f && t.LowSpeed > 0f && speedSqr < t.LowSpeed * t.LowSpeed) return t.LowAcceleration;
            return t.Acceleration;
        }

        // FUN_18016eda0
        private float GroundDecel(Vector3 wishVel, bool crouchHeld)
        {
            float decel = -1f;
            if (Vel.sqrMagnitude > t.Speed * t.Speed)
            {
                if (t.SprintDeceleration >= 0f) decel = t.SprintDeceleration;
                else if (t.SprintAcceleration >= 0f) decel = t.SprintAcceleration;
            }
            if (Sliding)
            {
                bool wantsToStop = !crouchHeld || Vector3.Dot(wishVel, Vel) < 0f;
                decel = wantsToStop ? t.SlideWantToStopDecel : t.SlideDecel;
            }
            if (decel < 0f)
            {
                decel = t.Deceleration;
                if (decel < 0f) decel = t.Acceleration * DecelFallbackScale;
            }
            return decel;
        }

        // -------------------------------------------------------------- walk move
        private void WalkMove(float dt, bool crouchHeld)
        {
            Vector3 n = OnGround ? GroundNormal : WallNormal;

            // plane basis from the view (FUN_180185230)
            Vector3 rightP = Vector3.Cross(n, viewFwd);
            if (rightP.sqrMagnitude < 1e-6f) rightP = viewRight;
            rightP.Normalize();
            Vector3 fwdP = Vector3.Cross(rightP, n).normalized;
            if (OnGround)
            {
                // on the ground Titanfall builds the basis from the yaw (no pitch): forward along the slope
                rightP = Vector3.Cross(n, yawFwd).normalized;
                fwdP = Vector3.Cross(rightP, n).normalized;
            }

            float fm = forwardMove, sm = sideMove;
            if (Wallrunning)
            {
                // wallrun_upwardAutoPush: running forward along the wall pushes you up it
                Vector3 tangent = Vector3.Cross(Vector3.up, n).normalized;
                float align = Mathf.Abs(Vector3.Dot(fwdP, tangent));
                float push = Mathf.Max(fm, 0f) * align * t.WrUpwardAutoPush;
                // rightP is vertical on a wall; add the push in its upward sense
                sm += rightP.y >= 0f ? push : -push;
                if ((rightP.y < -0.01f && sm > 0f) || (rightP.y > 0.01f && sm < 0f)) sm = 0f;
            }
            else
            {
                float mag = Mathf.Sqrt(fm * fm + sm * sm);
                if (mag > 1f) { fm /= mag; sm /= mag; }
                sm *= t.SpeedScaleSide;
                if (fm < 0f) fm *= t.SpeedScaleBack;
            }

            Vector3 wish = fwdP * fm + rightP * sm;
            wish -= Vector3.Dot(wish, n) * n;

            if (!Wallrunning)
            {
                if (crouchHeld && !Sliding && OnGround) TryStartSlide(wish);
                float wishLen = wish.magnitude;
                Vector3 wishDir = wishLen > 1e-5f ? wish / wishLen : Vector3.zero;
                float wishSpeed = Mathf.Min(wishLen, 1f) * MaxSpeed();
                Friction(wishDir, wishSpeed, GroundDecel(wish, crouchHeld), dt);
                Accelerate(wishDir, wishSpeed, GroundAccel(Vel.sqrMagnitude), dt);
                if (Sliding && Vel.magnitude < t.SlideStopSpeed) EndSlide();
            }
            else
            {
                WallrunMove(wish, fm, sm, dt);
            }
        }

        // FUN_1801696b0: only the velocity NOT along the wish direction is decelerated
        private void Friction(Vector3 wishDir, float wishSpeed, float decel, float dt)
        {
            float speedSqr = Vel.sqrMagnitude;
            if (speedSqr < 1e-6f) return;
            Vector3 kept = Vector3.zero;
            float d = Vector3.Dot(wishDir, Vel);
            if (d > 0f)
            {
                float frac = Mathf.Min(1f, d * wishSpeed / speedSqr);
                kept = wishDir * (d * frac);
            }
            Vector3 rem = Vel - kept;
            float remLen = rem.magnitude;
            float newLen = Mathf.Max(0f, remLen - decel * dt);
            if (Sliding) newLen *= Mathf.Pow(t.SlideVelocityDecay, dt);
            if (newLen < remLen && remLen > 1e-6f) Vel = kept + rem * (newLen / remLen);
        }

        // FUN_180160230: Source Accelerate, but speed may never rise above max(current, wishspeed)
        private void Accelerate(Vector3 wishDir, float wishSpeed, float accel, float dt)
        {
            if (wishDir == Vector3.zero) return;
            float capSqr = Mathf.Max(Vel.sqrMagnitude, wishSpeed * wishSpeed);
            float add = wishSpeed - Vector3.Dot(Vel, wishDir);
            if (add <= 0f) return;
            float accelSpeed = Mathf.Min(add, accel * dt);
            Vel += wishDir * accelSpeed;
            float s2 = Vel.sqrMagnitude;
            if (s2 > capSqr) Vel *= Mathf.Sqrt(capSqr / s2);
        }

        // --------------------------------------------------------------- air move
        // FUN_180161170 + FUN_180160f40
        private void AirMove(float dt)
        {
            Vector3 wish = yawFwd * forwardMove + viewRight * sideMove;
            float frac = Mathf.Min(wish.magnitude, 1f);
            if (frac < 1e-4f) return;
            Vector3 wishDir = wish.normalized;
            float airSpeed = Grapple.Attached ? t.GrappleAirSpeedMax : t.AirSpeed;
            float airAccel = Grapple.Attached ? t.GrappleAirAccel : t.AirAcceleration;
            float wishSpeed = airSpeed * frac * SpeedModifiers();
            float add = wishSpeed - Vector3.Dot(Vel, wishDir);
            if (add > 0f)
            {
                Vel += wishDir * Mathf.Min(add, airAccel * dt);
            }
            else
            {
                float capSqr = Mathf.Max(Vel.sqrMagnitude, wishSpeed * wishSpeed);
                Vel += wishDir * (dt * t.ExtraAirAccel);
                float s2 = Vel.sqrMagnitude;
                if (s2 > capSqr) Vel *= Mathf.Sqrt(capSqr / s2);
            }
        }

        // ------------------------------------------------------------------ jump
        // FUN_180172870
        private void CheckJump()
        {
            if (!player.CanMove() || player.IsAttached()) return;
            bool onSurface = OnGround || Wallrunning;
            bool grace = !onSurface && !jumpedSinceGround && (Time - leftGroundTime) < t.JumpGracePeriod;
            bool groundJump = onSurface || grace;
            bool doubleJump = !groundJump && t.DoubleJumpEnabled && superjumpsUsed < t.SuperjumpLimit && !player.IsEncumbered();
            if (!groundJump && !doubleJump) return;

            float height;
            if (doubleJump)
            {
                height = t.SuperjumpMaxHeight;          // power fixed at 1 (lerp(min,max,1))
                float rise = Vel.y >= 0f ? Vel.y * Vel.y / (t.Gravity * 2f) : 0f;
                float h2 = height * t.SuperjumpMinHeightFraction;
                if (h2 + rise < height) { Vel.y = 0f; h2 = height; }
                height = h2;
                superjumpsUsed++;
                LastEvent = "doublejump";
            }
            else
            {
                height = Sliding ? t.SlideJumpHeight : t.JumpHeight;
                if (Time - landTime < t.SkipTime && !Wallrunning)
                {
                    height *= t.SkipJumpHeightFraction;
                    Vector3 h = new Vector3(Vel.x, 0f, Vel.z);
                    float hs = h.magnitude;
                    if (hs > t.SkipSpeedRetain)
                    {
                        float ns = Mathf.Max(t.SkipSpeedRetain, hs - t.SkipSpeedReduce);
                        Vel.x *= ns / hs; Vel.z *= ns / hs;
                    }
                    if (t.SkipReplenishDoubleJump) superjumpsUsed = 0;
                    LastEvent = "skip";
                }
                if (Sliding) SlideJumpAdjust();
                LastEvent = Wallrunning ? "walljump" : (LastEvent == "skip" ? "skip" : "jump");
            }

            float v = Mathf.Sqrt(t.Gravity * 2f * height);
            if (Grapple.Attached) v *= t.GrappleJumpFrac;
            jumpedSinceGround = true;

            if (Wallrunning)
            {
                WallJump();
                EndWallrun("jump");
            }
            else
            {
                Vector3 n = OnGround ? GroundNormal : Vector3.up;
                if (doubleJump) n = Vector3.up;
                Vel += n * v;
                if (doubleJump)
                {
                    Vector3 wish = yawFwd * forwardMove + viewRight * sideMove;
                    if (wish.sqrMagnitude > 1f) wish.Normalize();
                    Vector3 add = wish * t.SuperjumpHorzSpeed;
                    Vector3 h = new Vector3(Vel.x, 0f, Vel.z);
                    float old = h.magnitude, addLen = add.magnitude;
                    float k = addLen < old ? 1f - addLen / old : 0f;
                    Vel.x = Vel.x * k + add.x;
                    Vel.z = Vel.z * k + add.z;
                }
            }
            OnGround = false;
            Sliding = false;
            player.m_zanim.SetTrigger("jump");
        }

        private void SlideJumpAdjust()
        {
            Vector3 h = new Vector3(Vel.x, 0f, Vel.z);
            float hs = h.magnitude;
            if (hs < 1e-3f) return;
            if (Time - lastSlideStart < 0.4f && slideBoost > 0f)
            {
                float ns = Mathf.Max(0f, hs - slideBoost);
                Vel *= ns / hs;
                hs = ns;
            }
            float max = t.SlideMaxJumpSpeed;
            if (hs > max * 0.85f && hs < max && hs > 1e-3f) { Vel.x *= max / hs; Vel.z *= max / hs; }
            slideBoost = 0f;
        }

        // FUN_180173a00
        private void WallJump()
        {
            Vector3 n = WallNormal;
            Vector3 wish = yawFwd * forwardMove + viewRight * sideMove;
            Vector3 input = wish * t.WallrunJumpInputDirSpeed;
            float viewInto = Mathf.Min(0f, Vector3.Dot(n, viewFwd));
            float wishInto = Mathf.Min(0f, Vector3.Dot(n, wish));
            float outward = (1f - viewInto * wishInto * 0.8f) * t.WallrunJumpOutwardSpeed;
            float along = Vector3.Dot(input, n);
            if (along < outward) input += n * (outward - along);
            Vel += input;
            // Titanfall clears the same-wall flag before computing strength, so this is always the full value
            float up = t.WallrunJumpUpSpeed;
            if (Vel.y < up) Vel.y = Mathf.Min(up, Vel.y + up * 1.5f);
        }

        // ----------------------------------------------------------------- slide
        // FUN_180183180
        private void TryStartSlide(Vector3 wish)
        {
            if (!t.SlideEnabled || GroundNormal.y < WalkableNormalY) return;
            Vector3 h = new Vector3(Vel.x, 0f, Vel.z);
            float hs2 = h.sqrMagnitude;
            if (hs2 < t.SlideRequiredStartSpeed * t.SlideRequiredStartSpeed) return;
            if (Vector3.Dot(yawFwd, wish) < t.SlideMaxAngleDot) return;
            Sliding = true;
            if (Time - lastSlideStart > t.SlideBoostCooldown)
            {
                float hs = Mathf.Sqrt(hs2);
                float ns = Mathf.Min(hs + t.SlideSpeedBoost, t.SlideSpeedBoostCap);
                if (ns > hs) { slideBoost = ns - hs; Vel.x *= ns / hs; Vel.z *= ns / hs; }
                else slideBoost = 0f;
            }
            lastSlideStart = Time;
            LastEvent = "slide";
        }

        private void EndSlide() { Sliding = false; LastEvent = "slide_end"; }

        private void UpdateCrouch(bool crouchHeld)
        {
            bool want = crouchHeld || Sliding;
            if (want == Crouched) return;
            var col = player.m_collider;
            if (!want)
            {
                // stand up only if there is head room
                float standH = t.HullHeight * U;
                Vector3 bottom = player.transform.position + Vector3.up * col.radius;
                if (Physics.SphereCast(bottom, col.radius * 0.9f, Vector3.up, out _, standH - col.radius * 2f,
                                       Character.s_groundRayMask, QueryTriggerInteraction.Ignore)) return;
            }
            Crouched = want;
            float h = (want ? t.CrouchHullHeight : t.HullHeight) * U;
            col.height = h;
            col.center = new Vector3(0f, h * 0.5f, 0f);
        }

        // --------------------------------------------------------------- wallrun
        // FUN_180174890 + FUN_1801835c0
        private void TryStartWallrun()
        {
            if (!t.WallrunEnabled || Time < wallRetryUntil || player.IsEncumbered()) return;
            if (Time - leftGroundTime < 0.5f && Vel.y <= 0f && !jumpedSinceGround) return;
            Vector3 h = new Vector3(Vel.x, 0f, Vel.z);
            Vector3 wish = yawFwd * forwardMove + viewRight * sideMove;
            if (h.sqrMagnitude < 1f && wish.sqrMagnitude < 0.01f) return;
            var col = player.m_collider;
            float reach = t.WrAllowedWallDist * U;
            Vector3 p1 = player.transform.position + Vector3.up * (col.radius + 0.1f);
            Vector3 p2 = player.transform.position + Vector3.up * (col.height - col.radius);
            foreach (var dir in CandidateDirs(h, wish))
            {
                if (!Physics.CapsuleCast(p1, p2, col.radius * 0.9f, dir, out var hit, reach + h.magnitude * U * UnityEngine.Time.fixedDeltaTime,
                                         Character.s_blockedRayMask, QueryTriggerInteraction.Ignore)) continue;
                Vector3 n = hit.normal;
                if (n.y > WalkableNormalY || n.y < -0.3f) continue;          // floors and steep ceilings are not walls
                // wallrun_minAngle_air: must be moving into the wall
                Vector3 vdir = h.sqrMagnitude > 1f ? h.normalized : wish.normalized;
                float minCos = -Mathf.Cos(t.WrMinAngleAir * 0.5f * Mathf.Deg2Rad);
                if (Vector3.Dot(vdir, n) >= minCos && Vector3.Dot(vdir, n) >= 0f) continue;
                if (IsRejectedSameWall(n)) continue;
                StartWallrun(n, hit.collider);
                return;
            }
        }

        private IEnumerable<Vector3> CandidateDirs(Vector3 h, Vector3 wish)
        {
            if (h.sqrMagnitude > 1f) yield return h.normalized;
            if (wish.sqrMagnitude > 0.01f) yield return wish.normalized;
            yield return viewRight;
            yield return -viewRight;
        }

        private bool IsRejectedSameWall(Vector3 n)
        {
            foreach (var prev in wallNormalsSinceGround)
            {
                if (Vector3.Dot(prev, n) > t.WrSameWallDot)
                {
                    Vector3 d = player.transform.position - lastWallrunPos; d.y = 0f;
                    if (d.magnitude / U <= t.WrSameWallDist) return true;
                }
            }
            return false;
        }

        private void StartWallrun(Vector3 n, Collider c)
        {
            sameWall = false;
            foreach (var prev in wallNormalsSinceGround)
                if (Vector3.Dot(prev, n) > t.WrSameWallDot) { sameWall = true; break; }
            Wallrunning = true;
            WallNormal = n;
            WallCollider = c;
            wallrunStart = Time;
            pushAwayStart = 0f;
            wallrunCount++;
            wallNormalsSinceGround.Add(n);
            lastWallrunPos = player.transform.position;
            superjumpsUsed = 0;                                   // touching a wall restores the double jump
            Sliding = false;
            // wallrun_upWallBoost along the wall's upward tangent
            float strength = sameWall ? 0f : 1f;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, n).normalized;
            float boost = t.WrUpWallBoost * strength;
            float cur = Vector3.Dot(Vel, up);
            if (cur < boost) Vel += up * (boost - Mathf.Max(cur, 0f));
            LastEvent = "wallrun";
        }

        private void EndWallrun(string why)
        {
            if (!Wallrunning) return;
            Wallrunning = false;
            WallCollider = null;
            wallRetryUntil = Time + t.WrRetryInterval;
            if (why == "timeout") Vel += WallNormal * t.WrFallAwaySpeed;
            LastEvent = "wallrun_end:" + why;
        }

        // wall branch of FUN_180185230 + detach rules of FUN_180186d60
        private void WallrunMove(Vector3 wish, float fm, float sm, float dt)
        {
            float onWall = Time - wallrunStart;
            if (onWall > t.WallrunTimeLimit) { EndWallrun("timeout"); return; }
            if (!RefreshWall()) { EndWallrun("lost"); return; }
            // pushing away from the wall for wallrun_pushAwayFallOffTime detaches
            if (Vector3.Dot(wish, WallNormal) > WalkableNormalY)
            {
                if (pushAwayStart == 0f) pushAwayStart = Time;
                if (Time - pushAwayStart > t.WrPushAwayFallOffTime) { EndWallrun("pushaway"); return; }
            }
            else pushAwayStart = 0f;

            // slip after wallrun_slipstarttime
            float slip = Mathf.Clamp01((onWall - t.WrSlipStartTime) / t.WrSlipDuration);
            if (Mathf.Abs(fm) < 0.1f && Mathf.Abs(sm) < 0.1f && slip <= t.WrNoInputSlipFrac) slip = t.WrNoInputSlipFrac;

            float speed = Vel.magnitude;
            if (speed > 0f)
            {
                float vzBefore = Vel.y;
                float ns = Mathf.Max(0f, speed - speed * dt * t.WallrunFriction);
                Vel *= ns / speed;
                if (slip != 0f && vzBefore < 0f) Vel.y = Mathf.Lerp(Vel.y, vzBefore, slip);
            }

            float speedScale = SpeedModifiers();
            // vertical
            float wz = wish.y;
            if (Mathf.Abs(wz) > 0.01f)
            {
                float mag = Mathf.Min(Mathf.Abs(wz), 1f);
                Accelerate(new Vector3(0f, Mathf.Sign(wz), 0f), mag * t.WallrunMaxSpeedVertical * speedScale,
                           (1f - slip) * t.WallrunAccelerateVertical, dt);
            }
            // horizontal
            Vector3 wh = new Vector3(wish.x, 0f, wish.z);
            if (wh.sqrMagnitude > 0.0001f)
            {
                float mag = Mathf.Min(wh.magnitude, 1f);
                Vector3 dir = wh.normalized;
                bool backward = Vector3.Dot(dir, yawFwd) <= -0.5f;
                float max = backward ? t.WallrunMaxSpeedHorizontalBackward : t.WallrunMaxSpeedHorizontal;
                Accelerate(dir, mag * max * speedScale, t.WallrunAccelerateHorizontal, dt);
            }
        }

        // keep hugging curved surfaces (tree trunks, rocks): re-trace the wall every tick
        private bool RefreshWall()
        {
            var col = player.m_collider;
            Vector3 c = player.transform.position + Vector3.up * (col.height * 0.5f);
            float reach = col.radius + t.WrAllowedWallDist * U + 0.05f;
            if (Physics.SphereCast(c, col.radius * 0.5f, -WallNormal, out var hit, reach, Character.s_blockedRayMask, QueryTriggerInteraction.Ignore)
                && hit.normal.y <= WalkableNormalY && hit.normal.y >= -0.3f)
            {
                WallNormal = hit.normal;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- helpers
        private void ClampVelocity()
        {
            float m = t.MaxVelocity;
            Vel.x = Mathf.Clamp(Vel.x, -m, m); Vel.y = Mathf.Clamp(Vel.y, -m, m); Vel.z = Mathf.Clamp(Vel.z, -m, m);
        }

        // Source steps up ledges lower than stepheight; PhysX does not, so do it explicitly.
        private void StepUp(float dt)
        {
            if (!OnGround) return;
            Vector3 h = new Vector3(Vel.x, 0f, Vel.z) * U;
            float d = h.magnitude * dt;
            if (d < 1e-4f) return;
            var col = player.m_collider;
            Vector3 dir = h.normalized;
            Vector3 feet = player.transform.position;
            Vector3 low = feet + Vector3.up * (col.radius + 0.02f);
            if (!Physics.SphereCast(low, col.radius * 0.9f, dir, out var block, d + 0.05f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore)) return;
            if (block.normal.y >= WalkableNormalY) return;
            float step = t.StepHeight * U;
            Vector3 high = low + Vector3.up * step;
            if (Physics.SphereCast(high, col.radius * 0.9f, dir, out _, d + 0.05f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore)) return;
            // find the ledge top
            Vector3 probe = high + dir * (d + col.radius * 0.5f);
            if (!Physics.Raycast(probe, Vector3.down, out var top, step + 0.05f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore)) return;
            if (top.normal.y < WalkableNormalY) return;
            float rise = top.point.y - feet.y;
            if (rise <= 0.01f || rise > step) return;
            player.m_body.position += Vector3.up * (rise + 0.01f);
        }

        private void UpdateViewRoll(float dt)
        {
            float target = 0f;
            if (Wallrunning)
            {
                float side = Vector3.Dot(WallNormal, viewRight);   // wall on the left -> normal points right
                target = -side * t.WrMaxViewTilt;
            }
            ViewRoll = Mathf.Lerp(ViewRoll, target, 1f - Mathf.Exp(-t.WrViewTiltSpeed * dt));
        }

        public float HorizontalSpeed => new Vector3(Vel.x, 0f, Vel.z).magnitude;
    }
}
