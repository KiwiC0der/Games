using PilotHeim.Data;
using PilotHeim.Pilot;
using UnityEngine;

namespace PilotHeim.Titan
{
    /// <summary>
    /// Titan movement: the same recovered Titanfall 2 walk/air code as the pilot
    /// (friction that spares the wish direction, accelerate capped at max(speed,
    /// wish), mid-step integration) with Titan class values, no jump/wallrun/slide,
    /// plus the dash (dodge*: speed, duration, power drain/regen).
    /// </summary>
    public sealed class TitanMotor : IMoverState
    {
        private const float U = PilotTuning.MetersPerUnit;
        private const float WalkableNormalY = 0.7f;
        private const float MaxPower = 100f;
        private const float DecelFallbackScale = 0.6f;

        private readonly Character body;
        private readonly TitanTuning t;

        public Vector3 Vel;
        public float Time { get; private set; }
        public bool OnGround { get; private set; }
        public bool Wallrunning => false;
        public bool Crouched => false;
        public bool Sprinting { get; private set; }
        public Vector3 GroundNormal = Vector3.up;
        public float Power = MaxPower;
        public bool Dashing => Time < dashEnd;

        private float dashEnd = -1f, lastDash = -100f, lastPowerUse = -100f;
        private Vector3 expectedBodyVel; private bool haveExpected;

        public TitanMotor(Character body, TitanTuning tuning) { this.body = body; t = tuning; }

        public void ResetVelocity() { Vel = Vector3.zero; haveExpected = false; }

        /// <summary>One physics tick. fwd/side in [-1,1] relative to yaw; dash edge-triggered.</summary>
        public void FixedStep(float dt, Vector3 yawForward, float fwd, float side, bool sprint, bool dash, float speedScale)
        {
            Time += dt;
            var rb = body.m_body;
            Vector3 actual = rb.linearVelocity / U;
            Vel = haveExpected ? Vel + (actual - expectedBodyVel) : actual;

            yawForward = Vector3.ProjectOnPlane(yawForward, Vector3.up).normalized;
            if (yawForward.sqrMagnitude < 0.5f) yawForward = body.transform.forward;
            Vector3 right = Vector3.Cross(Vector3.up, yawForward).normalized;

            Categorize();
            Sprinting = sprint && fwd > 0.1f;

            if (Time > lastPowerUse + t.DodgePowerDelay) Power = Mathf.Min(MaxPower, Power + t.PowerRegenRate * dt);

            Vel.y -= t.Gravity * t.GravityScale * dt * 0.5f;

            float m = Mathf.Sqrt(fwd * fwd + side * side);
            if (m > 1f) { fwd /= m; side /= m; }
            Vector3 wish = yawForward * fwd + right * side * t.SpeedScaleSide;
            if (fwd < 0f) wish = yawForward * fwd * t.SpeedScaleBack + right * side * t.SpeedScaleSide;

            if (dash) TryDash(wish.sqrMagnitude > 0.01f ? wish.normalized : yawForward);

            if (OnGround && !Dashing)
            {
                Vector3 n = GroundNormal;
                Vector3 w = wish - Vector3.Dot(wish, n) * n;
                float wl = w.magnitude;
                Vector3 wd = wl > 1e-5f ? w / wl : Vector3.zero;
                float wishSpeed = Mathf.Min(wl, 1f) * (Sprinting ? t.SprintSpeed : t.Speed) * speedScale;
                Friction(wd, wishSpeed, Decel(), dt);
                Accelerate(wd, wishSpeed, Accel(), dt);
            }
            else if (!OnGround)
            {
                AirAccelerate(wish, dt);
            }
            if (Dashing && new Vector3(Vel.x, 0f, Vel.z).magnitude < t.DodgeStopSpeed) dashEnd = -1f;

            Vector3 moveVel = Vel;
            if (OnGround) moveVel -= Vector3.Dot(moveVel, GroundNormal) * GroundNormal;
            Vel.y -= t.Gravity * t.GravityScale * dt * 0.5f;
            if (OnGround) Vel -= Vector3.Dot(Vel, GroundNormal) * GroundNormal;

            StepUp(dt);
            rb.useGravity = false;
            rb.linearVelocity = moveVel * U;
            expectedBodyVel = moveVel; haveExpected = true;
        }

        private void Categorize()
        {
            var col = body.m_collider;
            // a smaller sphere started well above the feet: the 1.5 m hull would begin inside sloped terrain,
            // and casts ignore colliders they start in
            float r = col.radius * 0.5f;
            const float lift = 1.0f;
            Vector3 o = body.transform.position + Vector3.up * (r + lift);
            bool hit = Physics.SphereCast(o, r, Vector3.down, out var gh, lift + 4f * U + 0.15f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore);
            bool contact = body.IsOnGround();                 // Valheim's collision-based ground contact
            OnGround = (hit && gh.normal.y >= WalkableNormalY || contact && body.m_groundContactNormal.y >= WalkableNormalY) && Vel.y <= 140f;
            if (OnGround) GroundNormal = hit ? gh.normal : body.m_groundContactNormal;
        }

        // Titanfall dodge (FUN_180172870, dodge branch): add = wishdir*dodgeSpeed; keep the old horizontal
        // velocity scaled by k = 1-|add|/|old| (0 if slower), blended toward 1 by dodgeKeepSpeedFrac;
        // vertical boost sqrt(2*sv_gravity*dodgeHeight) like a jump (no gravityScale).
        private void TryDash(Vector3 dir)
        {
            if (Power < t.DodgePowerDrain || Time - lastDash < t.DodgeInterval) return;
            Power -= t.DodgePowerDrain;
            lastPowerUse = lastDash = Time;
            dashEnd = Time + t.DodgeDuration;
            Vector3 add = new Vector3(dir.x, 0f, dir.z).normalized * t.DodgeSpeed;
            Vector3 h = new Vector3(Vel.x, 0f, Vel.z);
            float old = h.magnitude;
            float k = add.magnitude < old ? 1f - add.magnitude / old : 0f;
            k += (1f - k) * t.DodgeKeepSpeedFrac;
            Vel.x = h.x * k + add.x;
            Vel.z = h.z * k + add.z;
            if (OnGround) Vel.y += Mathf.Sqrt(2f * t.Gravity * t.DodgeHeight);
            OnGround = false;
        }

        private float Accel()
        {
            float s2 = Vel.sqrMagnitude;
            if (t.SprintAcceleration >= 0f && s2 > (t.Speed - 1f) * (t.Speed - 1f)) return t.SprintAcceleration;
            if (t.LowAcceleration >= 0f && t.LowSpeed > 0f && s2 < t.LowSpeed * t.LowSpeed) return t.LowAcceleration;
            return t.Acceleration;
        }

        private float Decel()
        {
            float d = -1f;
            if (Vel.sqrMagnitude > t.Speed * t.Speed)
            {
                if (t.SprintDeceleration >= 0f) d = t.SprintDeceleration;
                else if (t.SprintAcceleration >= 0f) d = t.SprintAcceleration;
            }
            if (d < 0f) d = t.Deceleration >= 0f ? t.Deceleration : t.Acceleration * DecelFallbackScale;
            return d;
        }

        private void Friction(Vector3 wishDir, float wishSpeed, float decel, float dt)
        {
            float s2 = Vel.sqrMagnitude;
            if (s2 < 1e-6f) return;
            Vector3 kept = Vector3.zero;
            float d = Vector3.Dot(wishDir, Vel);
            if (d > 0f) kept = wishDir * (d * Mathf.Min(1f, d * wishSpeed / s2));
            Vector3 rem = Vel - kept;
            float rl = rem.magnitude, nl = Mathf.Max(0f, rl - decel * dt);
            if (nl < rl && rl > 1e-6f) Vel = kept + rem * (nl / rl);
        }

        private void Accelerate(Vector3 wishDir, float wishSpeed, float accel, float dt)
        {
            if (wishDir == Vector3.zero) return;
            float cap = Mathf.Max(Vel.sqrMagnitude, wishSpeed * wishSpeed);
            float add = wishSpeed - Vector3.Dot(Vel, wishDir);
            if (add <= 0f) return;
            Vel += wishDir * Mathf.Min(add, accel * dt);
            float s2 = Vel.sqrMagnitude;
            if (s2 > cap) Vel *= Mathf.Sqrt(cap / s2);
        }

        private void AirAccelerate(Vector3 wish, float dt)
        {
            float frac = Mathf.Min(wish.magnitude, 1f);
            if (frac < 1e-4f) return;
            Vector3 wd = wish.normalized;
            float ws = t.AirSpeed * frac;
            float add = ws - Vector3.Dot(Vel, wd);
            if (add > 0f) Vel += wd * Mathf.Min(add, t.AirAcceleration * dt);
            else
            {
                float cap = Mathf.Max(Vel.sqrMagnitude, ws * ws);
                Vel += wd * dt * t.ExtraAirAccel;
                if (Vel.sqrMagnitude > cap) Vel *= Mathf.Sqrt(cap / Vel.sqrMagnitude);
            }
        }

        private void StepUp(float dt)
        {
            if (!OnGround) return;
            Vector3 h = new Vector3(Vel.x, 0f, Vel.z) * U;
            float d = h.magnitude * dt;
            if (d < 1e-4f) return;
            var col = body.m_collider;
            Vector3 dir = h.normalized, feet = body.transform.position;
            Vector3 low = feet + Vector3.up * (col.radius + 0.05f);
            if (!Physics.SphereCast(low, col.radius * 0.9f, dir, out var block, d + 0.1f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore)) return;
            if (block.normal.y >= WalkableNormalY) return;
            float step = t.StepHeight * U;
            Vector3 high = low + Vector3.up * step;
            if (Physics.SphereCast(high, col.radius * 0.9f, dir, out _, d + 0.1f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore)) return;
            Vector3 probe = high + dir * (d + col.radius * 0.5f);
            if (!Physics.Raycast(probe, Vector3.down, out var top, step + 0.1f, Character.s_groundRayMask, QueryTriggerInteraction.Ignore)) return;
            if (top.normal.y < WalkableNormalY) return;
            float rise = top.point.y - feet.y;
            if (rise > 0.01f && rise <= step) body.m_body.position += Vector3.up * (rise + 0.01f);
        }
    }
}
