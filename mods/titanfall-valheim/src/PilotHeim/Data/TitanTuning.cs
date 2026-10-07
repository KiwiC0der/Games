using System.Collections.Generic;
using System.Globalization;

namespace PilotHeim.Data
{
    /// <summary>
    /// The Titan's class settings (default titan_buddy = BT-7274 chassis, #base
    /// titan_base), resolved like PilotTuning: .set chain first, then server.dll
    /// defaults. Distances in Source units, times in seconds.
    /// </summary>
    public sealed class TitanTuning
    {
        private readonly PlayerSettings ps;
        private readonly NativeDefaults nd;
        public readonly List<string> Report = new List<string>();

        public float Speed, SprintSpeed, CrouchSpeed, Acceleration, Deceleration, LowSpeed, LowAcceleration,
                     SprintAcceleration, SprintDeceleration, SpeedScaleSide, SpeedScaleBack, StepHeight, GravityScale, Gravity;
        public float AirSpeed, AirAcceleration, ExtraAirAccel;
        public float HullRadius, HullHeight, EyeHeight;
        public float DodgeSpeed, DodgeDuration, DodgeStopSpeed, DodgeHeight, DodgePowerDrain, PowerRegenRate, DodgePowerDelay, DodgeInterval, DodgeKeepSpeedFrac;
        public float Health, HealthShield, HealthPerSegment, HealthDoomed, BuildTime, RegenDelay, RegenPercent;
        public float PhysicsMass;

        // titanfall (scripts/damage/damagedefs.txt, _replacement_titans_drop.gnut)
        public float FallDamage, FallInnerRadius, FallRadius, HotdropDamage, HotdropInnerRadius, HotdropRadius;
        public const float DropLosDist = 2000f, DropGroundSearchForward = 350f, DropGroundSearchDist = 1000f, DropFallbackDist = 150f;

        public TitanTuning(PlayerSettings ps, NativeDefaults nd, KvNode damageDefs, string titanDefsName)
        {
            this.ps = ps; this.nd = nd;
            Speed = Stance("stand", "speed"); SprintSpeed = Stance("stand", "sprintspeed"); CrouchSpeed = Stance("crouch", "speed");
            Acceleration = Stance("stand", "acceleration"); Deceleration = Stance("stand", "deceleration");
            LowSpeed = Stance("stand", "lowSpeed"); LowAcceleration = Stance("stand", "lowAcceleration");
            SprintAcceleration = Stance("stand", "sprintAcceleration"); SprintDeceleration = Stance("stand", "sprintDeceleration");
            var hmax = Vec(ps.Str("stand", "hull_max")); var hmin = Vec(ps.Str("stand", "hull_min"));
            HullRadius = hmax[0]; HullHeight = hmax[2] - hmin[2];
            EyeHeight = Vec(ps.Str("stand", "viewheight"))[2];
            SpeedScaleSide = Set("speedScaleSide"); SpeedScaleBack = Set("speedScaleBack");
            StepHeight = Set("stepHeight"); GravityScale = Set("gravityScale");
            Gravity = nd.ConVar("sv_gravity");
            AirSpeed = Set("airSpeed"); AirAcceleration = Set("airAcceleration"); ExtraAirAccel = nd.ConVar("player_extraairaccelleration");
            DodgeSpeed = Set("dodgeSpeed"); DodgeDuration = Set("dodgeDuration"); DodgeStopSpeed = Set("dodgeStopSpeed");
            DodgeHeight = Set("dodgeHeight"); DodgePowerDrain = Set("dodgePowerDrain"); PowerRegenRate = Set("powerRegenRate");
            DodgePowerDelay = Set("dodgePowerDelay"); DodgeInterval = Set("dodgeInterval"); DodgeKeepSpeedFrac = Set("dodgeKeepSpeedFrac");
            Health = Set("health"); HealthShield = Set("healthShield"); HealthPerSegment = Set("healthPerSegment"); HealthDoomed = Set("healthDoomed");
            PhysicsMass = Set("physicsMass");
            BuildTime = Top("titan_build_time"); RegenDelay = Top("titan_regen_delay"); RegenPercent = Top("titan_regen_percent");

            var fall = damageDefs.Child("damagedef_titan_fall");
            var hot = damageDefs.Child("damagedef_titan_hotdrop");
            FallDamage = F(fall, "damage"); FallInnerRadius = F(fall, "inner_radius"); FallRadius = F(fall, "radius");
            HotdropDamage = F(hot, "damage"); HotdropInnerRadius = F(hot, "inner_radius"); HotdropRadius = F(hot, "radius");
            Report.Add($"titanfall: {FallDamage} in {FallInnerRadius}-{FallRadius} u, hotdrop {HotdropDamage} in {HotdropInnerRadius}-{HotdropRadius} u");
        }

        private float Set(string key)
        {
            var s = ps.Str("global", key);
            if (s == null && nd.Settings.TryGetValue(key, out var d)) s = d;
            if (s == null) throw new KeyNotFoundException($"titan setting '{key}' not found");
            float v = NativeDefaults.ParseF(s);
            Report.Add($"titan {key} = {v.ToString(CultureInfo.InvariantCulture)}");
            return v;
        }

        // keys that live at the top level of the .set (outside any section)
        private float Top(string key)
        {
            var s = ps.Str("", key);
            if (s == null) throw new KeyNotFoundException($"titan top-level key '{key}' not found");
            float v = NativeDefaults.ParseF(s);
            Report.Add($"titan {key} = {v.ToString(CultureInfo.InvariantCulture)}");
            return v;
        }

        private float Stance(string stance, string key)
        {
            var s = ps.Str(stance, key);
            if (s == null && nd.Stance.TryGetValue(key, out var d)) s = d;
            if (s == null) throw new KeyNotFoundException($"titan stance '{stance}.{key}' not found");
            float v = NativeDefaults.ParseF(s);
            Report.Add($"titan {stance}.{key} = {v.ToString(CultureInfo.InvariantCulture)}");
            return v;
        }

        private static float F(KvNode n, string key)
        {
            var s = n?.Get(key) ?? throw new KeyNotFoundException("damagedef key '" + key + "' missing");
            return NativeDefaults.ParseF(s);
        }

        private static float[] Vec(string s)
        {
            var p = s.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            return new[] { NativeDefaults.ParseF(p[0]), NativeDefaults.ParseF(p[1]), NativeDefaults.ParseF(p[2]) };
        }
    }
}
