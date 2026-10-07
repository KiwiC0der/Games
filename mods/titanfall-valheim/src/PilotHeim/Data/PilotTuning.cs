using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PilotHeim.Data
{
    /// <summary>
    /// Every number the pilot motor uses, resolved exactly as Titanfall 2 does:
    /// the class .set chain first, then the native default compiled into
    /// server.dll. All distances are Source units (1 u = 1 inch), times in
    /// seconds. Missing values throw instead of being guessed.
    /// </summary>
    public sealed class PilotTuning
    {
        public const float MetersPerUnit = 0.0254f;
        public const float UnitsPerMeter = 1f / MetersPerUnit;

        private readonly PlayerSettings ps;
        private readonly NativeDefaults nd;
        public readonly List<string> Report = new List<string>();

        // --- ground / air ---
        public float Speed, SprintSpeed, CrouchSpeed, Acceleration, Deceleration, LowSpeed, LowAcceleration,
                     SprintAcceleration, SprintDeceleration, SpeedScaleSide, SpeedScaleBack, StepHeight;
        public float AirSpeed, AirAcceleration, ExtraAirAccel;
        public float Gravity, GravityScale, MaxVelocity;
        public float HullRadius, HullHeight, CrouchHullHeight;
        // --- jump ---
        public float JumpHeight, JumpGracePeriod, SkipTime, SkipJumpHeightFraction, SkipSpeedRetain, SkipSpeedReduce;
        public bool DoubleJumpEnabled, SkipReplenishDoubleJump;
        public float SuperjumpMinHeight, SuperjumpMaxHeight, SuperjumpHorzSpeed, SuperjumpLimit, SuperjumpMinHeightFraction;
        // --- slide ---
        public bool SlideEnabled;
        public float SlideRequiredStartSpeed, SlideSpeedBoost, SlideSpeedBoostCap, SlideDecel, SlideVelocityDecay,
                     SlideWantToStopDecel, SlideAccel, SlideStopSpeed, SlideMaxStopSpeed, SlideMaxJumpSpeed, SlideJumpHeight,
                     SlideBoostCooldown, SlideMaxAngleDot;
        // --- wallrun ---
        public bool WallrunEnabled;
        public float WallrunTimeLimit, WallrunHangTimeLimit, WallrunJumpOutwardSpeed, WallrunJumpUpSpeed, WallrunJumpInputDirSpeed,
                     WallrunMaxSpeedVertical, WallrunMaxSpeedHorizontal, WallrunMaxSpeedHorizontalBackward,
                     WallrunAccelerateVertical, WallrunAccelerateHorizontal, WallrunFriction;
        public float WrAllowedWallDist, WrGravityRampUpTime, WrSlipStartTime, WrSlipDuration, WrNoInputSlipFrac,
                     WrHangSlipStartTime, WrHangSlipDuration, WrHangStopTime, WrUpwardAutoPush, WrFallAwaySpeed,
                     WrPushAwayFallOffTime, WrSameWallDist, WrSameWallDot, WrUpWallBoost, WrMinAngleAir, WrMaxViewTilt,
                     WrViewTiltSpeed, WrRetryInterval;
        // --- grapple ---
        public float GrappleMaxLength, GrapplePowerRequired, GrapplePowerUseRate, GrapplePowerRegenDelay, GrapplePowerRegenRate,
                     GrappleAirSpeedMax, GrappleAirAccel, GrappleAccel, GrappleDecel, GrappleInitialImpulse, GrappleInitialImpulseOffGround,
                     GrappleInitialSlowFrac, GrappleInitialSlowFracVert, GrappleInitialSpeedMin, GrappleSpeedRampMin, GrappleSpeedRampMax,
                     GrappleSpeedRampTime, GrapplePullDelay, GrappleLift, GrappleShootVel, GrappleRetractVel, GrappleGravityFracMin, GrappleGravityFracMax,
                     GrappleDetachLengthMin, GrappleDetachLengthMax, GrappleDetachAwaySpeed, GrappleDetachVerticalBoost,
                     GrappleDetachVerticalMaxSpeed, GrappleDetachSpeedLoss, GrappleDetachSpeedLossMin, GrappleDetachLowSpeedThreshold,
                     GrappleDetachLowSpeedTime, GrappleDetachLowSpeedWallTime, GrappleDetachLowSpeedGroundTime,
                     GrappleImpactVerticalBoost, GrappleImpactVerticalMaxSpeed, GrappleJumpFrac, GrappleGracePeriod;
        public bool GrappleDontFightGravity;
        // --- mantle ---
        public bool AutoMantle;
        public float MantleHeight, MantleSearchDist, MantleMinDist, MantleCooldown;
        // --- life ---
        public float Health;

        public PilotTuning(PlayerSettings ps, NativeDefaults nd, KvNode grappleWeapon)
        {
            this.ps = ps;
            this.nd = nd;

            Speed = Stance("stand", "speed");
            SprintSpeed = Stance("stand", "sprintspeed");
            CrouchSpeed = Stance("crouch", "speed");
            Acceleration = Stance("stand", "acceleration");
            Deceleration = Stance("stand", "deceleration");
            LowSpeed = Stance("stand", "lowSpeed");
            LowAcceleration = Stance("stand", "lowAcceleration");
            SprintAcceleration = Stance("stand", "sprintAcceleration");
            SprintDeceleration = Stance("stand", "sprintDeceleration");
            var hullMax = Vec(ps.Str("stand", "hull_max")); var hullMin = Vec(ps.Str("stand", "hull_min"));
            HullRadius = hullMax[0]; HullHeight = hullMax[2] - hullMin[2];
            CrouchHullHeight = Vec(ps.Str("crouch", "hull_max"))[2];

            SpeedScaleSide = Set("speedScaleSide"); SpeedScaleBack = Set("speedScaleBack");
            StepHeight = Set("stepHeight");
            AirSpeed = Set("airSpeed"); AirAcceleration = Set("airAcceleration");
            ExtraAirAccel = CV("player_extraairaccelleration");
            Gravity = CV("sv_gravity"); GravityScale = Set("gravityScale"); MaxVelocity = CV("sv_maxvelocity");

            JumpHeight = Set("jumpHeight"); JumpGracePeriod = CV("jump_graceperiod");
            SkipTime = CV("skip_time"); SkipJumpHeightFraction = CV("skip_jump_height_fraction");
            SkipSpeedRetain = CV("skip_speed_retain"); SkipSpeedReduce = CV("skip_speed_reduce");
            SkipReplenishDoubleJump = CV("skip_replenish_double_jump") != 0f;
            DoubleJumpEnabled = SetB("doubleJump");
            SuperjumpMinHeight = Set("superjumpMinHeight"); SuperjumpMaxHeight = Set("superjumpMaxHeight");
            SuperjumpHorzSpeed = Set("superjumpHorzSpeed"); SuperjumpLimit = Set("superjumpLimit");
            SuperjumpMinHeightFraction = CV("superjump_min_height_fraction");

            SlideEnabled = SetB("slide");
            SlideRequiredStartSpeed = Set("slideRequiredStartSpeed"); SlideSpeedBoost = Set("slideSpeedBoost");
            SlideSpeedBoostCap = Set("slideSpeedBoostCap"); SlideDecel = Set("slideDecel");
            SlideVelocityDecay = Set("slideVelocityDecay"); SlideWantToStopDecel = Set("slideWantToStopDecel");
            SlideAccel = Set("slideAccel"); SlideStopSpeed = Set("slideStopSpeed"); SlideMaxStopSpeed = Set("slideMaxStopSpeed");
            SlideMaxJumpSpeed = Set("slideMaxJumpSpeed"); SlideJumpHeight = Set("slideJumpHeight");
            SlideBoostCooldown = CV("slide_boost_cooldown"); SlideMaxAngleDot = CV("slide_max_angle_dot");

            WallrunEnabled = SetB("wallrun");
            WallrunTimeLimit = Set("wallrun_timeLimit"); WallrunHangTimeLimit = Set("wallrun_hangTimeLimit");
            WallrunJumpOutwardSpeed = Set("wallrunJumpOutwardSpeed"); WallrunJumpUpSpeed = Set("wallrunJumpUpSpeed");
            WallrunJumpInputDirSpeed = Set("wallrunJumpInputDirSpeed");
            WallrunMaxSpeedVertical = Set("wallrunMaxSpeedVertical"); WallrunMaxSpeedHorizontal = Set("wallrunMaxSpeedHorizontal");
            WallrunMaxSpeedHorizontalBackward = Set("wallrunMaxSpeedHorizontalBackward");
            WallrunAccelerateVertical = Set("wallrunAccelerateVertical"); WallrunAccelerateHorizontal = Set("wallrunAccelerateHorizontal");
            WallrunFriction = Set("wallrunFriction");
            WrAllowedWallDist = CV("wallrun_allowed_wall_dist"); WrGravityRampUpTime = CV("wallrun_gravityRampUpTime");
            WrSlipStartTime = CV("wallrun_slipstarttime"); WrSlipDuration = CV("wallrun_slipduration");
            WrNoInputSlipFrac = CV("wallrun_noInputSlipFrac"); WrHangSlipStartTime = CV("wallrun_hangslipstarttime");
            WrHangSlipDuration = CV("wallrun_hangslipduration"); WrHangStopTime = CV("wallrun_hangStopTime");
            WrUpwardAutoPush = CV("wallrun_upwardAutoPush"); WrFallAwaySpeed = CV("wallrun_fallAwaySpeed");
            WrPushAwayFallOffTime = CV("wallrun_pushAwayFallOffTime"); WrSameWallDist = CV("wallrun_sameWallDist");
            WrSameWallDot = CV("wallrun_sameWallDot"); WrUpWallBoost = CV("wallrun_upWallBoost");
            WrMinAngleAir = CV("wallrun_minAngle_air"); WrMaxViewTilt = CV("wallrun_maxViewTilt");
            WrViewTiltSpeed = CV("wallrun_viewTiltSpeed"); WrRetryInterval = CV("wallrun_retry_interval");

            var wd = grappleWeapon;
            GrappleMaxLength = WeaponF(wd, "grapple_maxLength"); GrapplePowerRequired = WeaponF(wd, "grapple_power_required");
            GrapplePowerUseRate = WeaponF(wd, "grapple_power_use_rate");
            GrapplePowerRegenDelay = Set("grapple_power_regen_delay"); GrapplePowerRegenRate = Set("grapple_power_regen_rate");
            GrappleAirSpeedMax = Set("grapple_airSpeedMax"); GrappleAirAccel = Set("grapple_airAccel");
            GrappleAccel = CV("grapple_accel_human"); GrappleDecel = CV("grapple_decel_human");
            GrappleInitialImpulse = CV("grapple_initialImpulse_human"); GrappleInitialImpulseOffGround = CV("grapple_initialImpulseOffGround_human");
            GrappleInitialSlowFrac = CV("grapple_initialSlowFrac_human"); GrappleInitialSlowFracVert = CV("grapple_initialSlowFracVert_human");
            GrappleInitialSpeedMin = CV("grapple_initialSpeedMin_human"); GrappleSpeedRampMin = CV("grapple_speedRampMin_human");
            GrappleSpeedRampMax = CV("grapple_speedRampMax_human"); GrappleSpeedRampTime = CV("grapple_speedRampTime_human");
            GrapplePullDelay = CV("grapple_pullDelay_human"); GrappleLift = CV("grapple_lift"); GrappleShootVel = CV("grapple_shootVel"); GrappleRetractVel = CV("grapple_retractVel");
            GrappleGravityFracMin = Set("grapple_gravityFracMin"); GrappleGravityFracMax = Set("grapple_gravityFracMax");
            GrappleDetachLengthMin = Set("grapple_detachLengthMin"); GrappleDetachLengthMax = Set("grapple_detachLengthMax");
            GrappleDetachAwaySpeed = Set("grapple_detachAwaySpeed"); GrappleDetachVerticalBoost = Set("grapple_detachVerticalBoost");
            GrappleDetachVerticalMaxSpeed = Set("grapple_detachVerticalMaxSpeed"); GrappleDetachSpeedLoss = Set("grapple_detachSpeedLoss");
            GrappleDetachSpeedLossMin = Set("grapple_detachSpeedLossMin"); GrappleDetachLowSpeedThreshold = Set("grapple_detachLowSpeedThreshold");
            GrappleDetachLowSpeedTime = Set("grapple_detachLowSpeedTime"); GrappleDetachLowSpeedWallTime = Set("grapple_detachLowSpeedWallTime");
            GrappleDetachLowSpeedGroundTime = Set("grapple_detachLowSpeedGroundTime");
            GrappleImpactVerticalBoost = Set("grapple_impactVerticalBoost"); GrappleImpactVerticalMaxSpeed = Set("grapple_impactVerticalMaxSpeed");
            GrappleJumpFrac = CV("grapple_jumpFrac"); GrappleGracePeriod = CV("grapple_gracePeriod");
            GrappleDontFightGravity = CV("grapple_dontFightGravity") != 0f;

            AutoMantle = SetB("automantle");
            MantleHeight = CV("automantle_height"); MantleSearchDist = CV("automantle_searchdist");
            MantleMinDist = CV("automantle_mindist"); MantleCooldown = CV("automantle_cooldown");
            Health = Set("health");
        }

        // class setting (global section) -> native settings default
        private float Set(string key)
        {
            var s = ps.Str("global", key);
            string src = ps.Name;
            if (s == null && nd.Settings.TryGetValue(key, out var d)) { s = d; src = "server.dll"; }
            if (s == null) throw new KeyNotFoundException($"player setting '{key}' not found in {ps.Name} or server.dll defaults");
            float v = NativeDefaults.ParseF(s);
            Report.Add($"{key} = {v.ToString(CultureInfo.InvariantCulture)} [{src}]");
            return v;
        }

        private bool SetB(string key)
        {
            var s = ps.Str("global", key);
            if (s == null && nd.Settings.TryGetValue(key, out var d)) s = d;
            if (s == null) throw new KeyNotFoundException($"player setting '{key}' not found");
            s = s.Trim();
            bool b = s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            Report.Add($"{key} = {b}");
            return b;
        }

        private float Stance(string stance, string key)
        {
            var s = ps.Str(stance, key);
            string src = ps.Name;
            if (s == null && nd.Stance.TryGetValue(key, out var d)) { s = d; src = "server.dll"; }
            if (s == null) throw new KeyNotFoundException($"stance setting '{stance}.{key}' not found");
            float v = NativeDefaults.ParseF(s);
            Report.Add($"{stance}.{key} = {v.ToString(CultureInfo.InvariantCulture)} [{src}]");
            return v;
        }

        private float CV(string name)
        {
            float v = nd.ConVar(name);
            Report.Add($"{name} = {v.ToString(CultureInfo.InvariantCulture)} [convar]");
            return v;
        }

        private float WeaponF(KvNode weaponData, string key)
        {
            var s = weaponData?.Get(key) ?? throw new KeyNotFoundException("grapple weapon key '" + key + "' missing");
            float v = NativeDefaults.ParseF(s);
            Report.Add($"{key} = {v.ToString(CultureInfo.InvariantCulture)} [mp_ability_grapple.txt]");
            return v;
        }

        private static float[] Vec(string s)
        {
            if (s == null) throw new KeyNotFoundException("hull vector missing");
            var p = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return new[] { NativeDefaults.ParseF(p[0]), NativeDefaults.ParseF(p[1]), NativeDefaults.ParseF(p[2]) };
        }
    }
}
