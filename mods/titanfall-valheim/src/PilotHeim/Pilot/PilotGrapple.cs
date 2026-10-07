using PilotHeim.Data;
using UnityEngine;

namespace PilotHeim.Pilot
{
    /// <summary>
    /// Titanfall 2 grapple (mp_ability_grapple + CPlayer::Grapple), hooking any
    /// Valheim collider: tree trunks, rocks, buildings, ships or creatures.
    ///
    /// Recovered behaviour (server.dll): the hook flies at grapple_shootVel; on
    /// attach the perpendicular velocity is slowed (initialSlowFrac / Vert) and an
    /// impulse lifts the speed toward the hook to speedRampMin; after pullDelay the
    /// target pull speed ramps speedRampMin -> speedRampMax over speedRampTime,
    /// the along-rope speed accelerates at grapple_accel and the perpendicular
    /// speed decays at grapple_decel without fighting gravity. Power: max 100,
    /// requires grapple_power_required, drains grapple_power_use_rate/s after the
    /// grace period up to grapple_power_required, regenerates after a delay.
    /// </summary>
    public sealed class PilotGrapple
    {
        private const float U = PilotTuning.MetersPerUnit;
        private const float MaxPower = 100f;                 // DAT_18087ebe8

        private readonly PilotMotor motor;
        private readonly Player player;
        private readonly PilotTuning t;

        public enum State { Idle, Shooting, Attached, Retracting }
        public State Phase { get; private set; } = State.Idle;
        public bool Attached => Phase == State.Attached;
        public bool PullingUpward { get; private set; }
        /// <summary>While attached to a hook above the feet the player is airborne (Titanfall clears the ground entity on attach).</summary>
        public bool LiftsOffGround => Phase == State.Attached && HookPoint.y > player.transform.position.y + 0.3f;
        public float Power { get; private set; } = MaxPower;

        private Transform hookParent;
        private Vector3 hookLocal, hookWorld, ropeTip;
        private float attachTime, shootTime, lastUseTime = -100f, consumed, lowSpeedTime;
        private bool passedPoint;
        private LineRenderer rope;

        public PilotGrapple(PilotMotor motor, Player player, PilotTuning tuning)
        {
            this.motor = motor;
            this.player = player;
            t = tuning;
        }

        public string DebugHook => $"{HookPoint.x:0.0},{HookPoint.y:0.0},{HookPoint.z:0.0} parent={(hookParent != null ? hookParent.name : "-")}";
        private Vector3 HookPoint => hookParent != null ? hookParent.TransformPoint(hookLocal) : hookWorld;
        private Vector3 Hand => player.transform.position + Vector3.up * (player.m_collider.height * 0.75f);

        /// <summary>Fire the grapple along the camera aim (Q).</summary>
        public void Fire(Vector3 aimOrigin, Vector3 aimDir)
        {
            if (Phase != State.Idle) { if (Attached) Detach("refire"); return; }
            if (Power < t.GrapplePowerRequired) { player.Message(MessageHud.MessageType.Center, "Grapple recharging"); return; }
            float range = t.GrappleMaxLength * U;
            var mask = Character.s_groundRayMask | Character.s_characterLayerMask;
            ropeTip = Hand;
            shootTime = motor.Time;
            hookParent = null;
            if (Physics.Raycast(aimOrigin, aimDir, out var hit, range, mask, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponentInParent<Player>() != player)
            {
                hookWorld = hit.point;
                var target = hit.collider.attachedRigidbody != null ? hit.collider.attachedRigidbody.transform
                           : hit.collider.GetComponentInParent<Character>()?.transform;
                if (target != null) { hookParent = target; hookLocal = target.InverseTransformPoint(hit.point); }
                Phase = State.Shooting;
            }
            else
            {
                hookWorld = aimOrigin + aimDir * range;    // miss: rope flies out and comes back
                Phase = State.Retracting;
            }
            EnsureRope();
            lastUseTime = motor.Time;
        }

        public void Detach(string why)
        {
            if (Phase == State.Attached)
            {
                // grapple_detach*: losing speed when letting go, and a small hop past the point
                Vector3 v = motor.Vel;
                float s = v.magnitude;
                float ns = Mathf.Max(Mathf.Min(s, t.GrappleDetachSpeedLossMin), s - t.GrappleDetachSpeedLoss);
                if (s > 1e-3f && ns < s) motor.Vel = v * (ns / s);
                if (passedPoint && motor.Vel.y < t.GrappleDetachVerticalMaxSpeed)
                    motor.Vel.y = Mathf.Min(t.GrappleDetachVerticalMaxSpeed, motor.Vel.y + t.GrappleDetachVerticalBoost);
            }
            Phase = State.Retracting;
            PullingUpward = false;
            motor.LastEvent = "grapple_detach:" + why;
        }

        /// <summary>Called by the motor every tick, after the jump check.</summary>
        public void Tick(float dt, bool onGround, bool wallrunning)
        {
            // power regeneration (FUN_1805e23e0)
            if (motor.Time > lastUseTime + t.GrapplePowerRegenDelay && Phase == State.Idle)
                Power = Mathf.Min(MaxPower, Power + t.GrapplePowerRegenRate * dt);

            switch (Phase)
            {
                case State.Shooting:
                {
                    Vector3 to = HookPoint - ropeTip;
                    float step = t.GrappleShootVel * U * dt;
                    if (to.magnitude <= step) { ropeTip = HookPoint; Attach(onGround); }
                    else ropeTip += to.normalized * step;
                    break;
                }
                case State.Attached:
                    Pull(dt, onGround, wallrunning);
                    break;
                case State.Retracting:
                {
                    Vector3 to = Hand - ropeTip;
                    float step = t.GrappleRetractVel * U * dt;
                    if (to.magnitude <= step) { Phase = State.Idle; }
                    else ropeTip += to.normalized * step;
                    break;
                }
            }
            UpdateRope();
        }

        // FUN_18060c0c0 + FUN_18060c3e0
        private void Attach(bool onGround)
        {
            Phase = State.Attached;
            attachTime = motor.Time;
            consumed = 0f;
            lowSpeedTime = 0f;
            passedPoint = false;
            Vector3 dir = (HookPoint - Hand).normalized;
            Vector3 v = motor.Vel;
            float along = Mathf.Max(0f, Vector3.Dot(v, dir));
            Vector3 alongV = dir * along;
            Vector3 perp = v - alongV;
            perp.x *= t.GrappleInitialSlowFrac;
            perp.z *= t.GrappleInitialSlowFrac;
            perp.y *= t.GrappleInitialSlowFracVert;
            v = perp + alongV;
            float cur = Vector3.Dot(v, dir);
            float rampMin = t.GrappleSpeedRampMin;
            if (cur < rampMin)
            {
                float target = Mathf.Clamp(cur + t.GrappleInitialImpulse, t.GrappleInitialSpeedMin, rampMin);
                v += dir * (target - cur);
            }
            if (onGround) v.y += t.GrappleInitialImpulseOffGround;   // initialImpulseOffGround
            motor.Vel = v;
            motor.LastEvent = "grapple_attach";
        }

        // FUN_18060b9a0 + FUN_18060c750 + FUN_18060dad0 (power) + pilot_base grapple_detach* rules
        private void Pull(float dt, bool onGround, bool wallrunning)
        {
            Vector3 hook = HookPoint + Vector3.up * (t.GrappleLift * U);
            Vector3 toHook = (hook - Hand) / U;                 // Source units
            float len = toHook.magnitude;
            if (len < 1e-3f) { Detach("arrived"); return; }
            Vector3 dir = toHook / len;
            Vector3 v = motor.Vel;
            float along = Vector3.Dot(v, dir);
            PullingUpward = dir.y > 0.2f && along > 0f;

            // power: drained after the grace period, up to grapple_power_required per grapple
            if (motor.Time > attachTime + t.GrappleGracePeriod)
            {
                float use = Mathf.Min(t.GrapplePowerUseRate * dt, t.GrapplePowerRequired - consumed);
                if (use > 0f) { consumed += use; Power = Mathf.Max(0f, Power - use); }
                if (consumed >= t.GrapplePowerRequired - 0.001f) { Detach("power"); return; }
            }

            // detach rules
            if (len < t.GrappleDetachLengthMin || (len < t.GrappleDetachLengthMax && along >= t.GrappleSpeedRampMax * 0.95f))
            {
                ImpactBoost();
                Detach("length");
                return;
            }
            if (-along > t.GrappleDetachAwaySpeed) { Detach("away"); return; }
            if (v.magnitude < t.GrappleDetachLowSpeedThreshold)
            {
                lowSpeedTime += dt;
                float limit = onGround ? t.GrappleDetachLowSpeedGroundTime : (wallrunning ? t.GrappleDetachLowSpeedWallTime : t.GrappleDetachLowSpeedTime);
                if (lowSpeedTime > limit) { Detach("lowspeed"); return; }
            }
            else lowSpeedTime = 0f;
            if (len > t.GrappleMaxLength * 1.25f) { Detach("stretched"); return; }

            if (motor.Time < attachTime + t.GrapplePullDelay) return;

            // perpendicular decay that never fights gravity
            Vector3 perp = v - dir * along;
            float down = 0f;
            if (t.GrappleDontFightGravity && perp.y < 0f) { down = perp.y; perp.y = 0f; }
            float pl = perp.magnitude;
            float npl = Mathf.Max(0f, pl - t.GrappleDecel * dt);
            Vector3 perpOut = pl > 1e-4f ? perp * (npl / pl) : Vector3.zero;

            // along-rope speed ramps toward the target
            float ramp = Mathf.Clamp01((motor.Time - attachTime) / t.GrappleSpeedRampTime);
            float target = Mathf.Lerp(t.GrappleSpeedRampMin, t.GrappleSpeedRampMax, ramp);
            if (along < target) along = Mathf.Min(target, along + t.GrappleAccel * dt);

            Vector3 nv = dir * along + perpOut;
            nv.y += down;
            if (t.GrappleDontFightGravity && nv.y < v.y) nv.y = v.y;
            motor.Vel = nv;

            // remember whether we swung past the hook (used for the detach hop)
            if (Vector3.Dot(nv, dir) < 0f) passedPoint = true;
        }

        private void ImpactBoost()
        {
            if (motor.Vel.y < t.GrappleImpactVerticalMaxSpeed)
                motor.Vel.y = Mathf.Min(t.GrappleImpactVerticalMaxSpeed, motor.Vel.y + t.GrappleImpactVerticalBoost);
        }

        /// <summary>FUN_180170e60: fraction of gravity felt while grappling.</summary>
        public float GravityFraction(float fwd, float side, Vector3 view)
        {
            Vector3 dir = HookPoint - Hand; dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f) return t.GrappleGravityFracMax;   // directly under the hook
            dir.Normalize();
            Vector3 wish = Vector3.ProjectOnPlane(view, Vector3.up).normalized * fwd + Vector3.Cross(Vector3.up, Vector3.ProjectOnPlane(view, Vector3.up).normalized) * side;
            float away = Mathf.Clamp01(-Vector3.Dot(wish, dir));
            return Mathf.Lerp(t.GrappleGravityFracMin, t.GrappleGravityFracMax, away);
        }

        // ------------------------------------------------------------------ visuals
        private void EnsureRope()
        {
            if (rope != null) return;
            var go = new GameObject("PilotHeim_GrappleRope");
            rope = go.AddComponent<LineRenderer>();
            rope.positionCount = 2;
            rope.startWidth = 0.025f;
            rope.endWidth = 0.02f;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader != null) rope.material = new Material(shader) { color = new Color(0.12f, 0.12f, 0.13f) };
            rope.startColor = rope.endColor = new Color(0.12f, 0.12f, 0.13f);
        }

        private void UpdateRope()
        {
            if (rope == null) return;
            bool show = Phase != State.Idle;
            rope.enabled = show;
            if (!show) return;
            rope.SetPosition(0, Hand);
            rope.SetPosition(1, Phase == State.Attached ? HookPoint : ropeTip);
        }

        public void Destroy()
        {
            if (rope != null) Object.Destroy(rope.gameObject);
        }
    }
}
