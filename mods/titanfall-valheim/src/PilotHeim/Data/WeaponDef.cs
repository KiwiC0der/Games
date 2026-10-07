using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace PilotHeim.Data
{
    /// <summary>
    /// A Titanfall 2 weapon script (scripts/weapons/*.txt) resolved for one
    /// profile: base WeaponData keys, then the MP_BASE or SP_BASE block on top
    /// ("&lt;KEEP_DEFAULT&gt;" keeps the base value). Distances are Source units.
    /// </summary>
    public sealed class WeaponDef
    {
        public readonly string Id;
        private readonly Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string PrintName, FireMode, FalloffType;
        public float FireRate, ClipSize, AmmoPerShot, AmmoMinToFire, ReloadTime, ReloadEmptyTime;
        public float DamageNear, DamageFar, DamageVeryFar, NearDist, FarDist, VeryFarDist, InverseDist, HeadshotScale;
        public float SpreadHip, SpreadAds, SpreadAirHip, SpreadAirAds, SpreadCrouchHip, SpreadSprintHip;
        public float SpreadKickHip, SpreadKickAds, SpreadMaxKickHip, SpreadMaxKickAds, SpreadDecayRate, SpreadDecayDelay;
        public float ZoomFov, ZoomTimeIn, AdsMoveSpeedScale, DeployTime, ImpulseForce, BurstCount;
        public float KickPitchBase, KickPitchRandom, KickYawBase, KickYawRandom;
        public bool IsProjectile, BoltGravity, IsShotgun, IsGrenade;
        public float BoltSpeed, BoltGravityAmount, LaunchSpeed, FuseTime, ExplosionDamage, ExplosionRadius, ExplosionInnerRadius;
        public float FireDuration, RegenRate;

        public static WeaponDef Load(string weaponsDir, string id, bool singlePlayerProfile)
        {
            var w = new WeaponDef(id);
            w.Merge(weaponsDir, id + ".txt", singlePlayerProfile, 0);
            w.Resolve();
            return w;
        }

        // #base files first, then this file's keys, then its profile block
        private void Merge(string weaponsDir, string file, bool sp, int depth)
        {
            if (depth > 8) throw new InvalidDataException(Id + ": #base chain too deep");
            var bases = new List<string>();
            var root = KeyValues.ParseFile(Path.Combine(weaponsDir, file), bases).Child("WeaponData")
                       ?? throw new InvalidDataException(file + ": no WeaponData block");
            foreach (var b in bases) Merge(weaponsDir, b, sp, depth + 1);
            foreach (var n in root.Children) if (n.Value != null) kv[n.Key] = n.Value;
            var profile = root.Child(sp ? "SP_BASE" : "MP_BASE");
            if (profile != null)
                foreach (var n in profile.Children)
                    if (n.Value != null && n.Value != "<KEEP_DEFAULT>") kv[n.Key] = n.Value;
        }

        private WeaponDef(string id) { Id = id; }

        public string S(string key, string fallback = null) => kv.TryGetValue(key, out var v) ? v : fallback;

        public float F(string key, float fallback = 0f)
        {
            var s = S(key);
            if (s == null) return fallback;
            s = s.Trim();
            if (s.StartsWith("*") || s.StartsWith("+")) return fallback;      // relative mod values only make sense on mods
            return float.TryParse(s.TrimEnd('f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;
        }

        private void Resolve()
        {
            PrintName = S("shortprintname", Id);
            FireMode = S("fire_mode", "auto");
            FalloffType = S("damage_falloff_type", "linear");
            FireRate = F("fire_rate", 1f);
            ClipSize = F("ammo_clip_size", 1f);
            AmmoPerShot = F("ammo_per_shot", 1f);
            AmmoMinToFire = F("ammo_min_to_fire", AmmoPerShot);
            ReloadTime = F("reload_time", 2f);
            ReloadEmptyTime = F("reloadempty_time", ReloadTime);
            DamageNear = F("damage_near_value");
            DamageFar = F("damage_far_value", DamageNear);
            DamageVeryFar = F("damage_very_far_value", DamageFar);
            NearDist = F("damage_near_distance", 1000f);
            FarDist = F("damage_far_distance", NearDist);
            VeryFarDist = F("damage_very_far_distance", FarDist);
            InverseDist = F("damage_inverse_distance", 100f);
            HeadshotScale = F("damage_headshot_scale", 1f);
            SpreadHip = F("spread_stand_hip");
            SpreadAds = F("spread_stand_ads");
            SpreadAirHip = F("spread_air_hip", SpreadHip);
            SpreadAirAds = F("spread_air_ads", SpreadAds);
            SpreadCrouchHip = F("spread_crouch_hip", SpreadHip);
            SpreadSprintHip = F("spread_stand_hip_sprint", SpreadHip);
            SpreadKickHip = F("spread_kick_on_fire_stand_hip");
            SpreadKickAds = F("spread_kick_on_fire_stand_ads");
            SpreadMaxKickHip = F("spread_max_kick_stand_hip");
            SpreadMaxKickAds = F("spread_max_kick_stand_ads");
            SpreadDecayRate = F("spread_decay_rate", 5f);
            SpreadDecayDelay = F("spread_decay_delay", 0.1f);
            ZoomFov = F("zoom_fov", 55f);
            ZoomTimeIn = F("zoom_time_in", 0.2f);
            AdsMoveSpeedScale = F("ads_move_speed_scale", 1f);
            DeployTime = F("deploy_time", 0.5f);
            ImpulseForce = F("impulse_force", 0f);
            BurstCount = F("burst_fire_count", 0f);
            KickPitchBase = F("viewkick_pitch_base"); KickPitchRandom = F("viewkick_pitch_random");
            KickYawBase = F("viewkick_yaw_base"); KickYawRandom = F("viewkick_yaw_random");
            BoltSpeed = F("bolt_speed", 0f);
            IsProjectile = BoltSpeed > 0f && S("projectilemodel") != null;
            BoltGravity = F("bolt_gravity_enabled") != 0f;
            BoltGravityAmount = F("bolt_gravity_amount", 1f);
            IsShotgun = (S("damage_flags", "") ?? "").Contains("DF_SHOTGUN");
            IsGrenade = S("OnWeaponTossReleaseAnimEvent") != null;
            LaunchSpeed = F("projectile_launch_speed", 1000f);
            FuseTime = F("grenade_fuse_time", 3f);
            ExplosionDamage = F("explosion_damage");
            ExplosionRadius = F("explosionradius");
            ExplosionInnerRadius = F("explosion_inner_radius");
            FireDuration = F("fire_duration");
            RegenRate = F("regen_ammo_refill_rate");
        }

        /// <summary>
        /// Damage at a distance (Source units): CalcBulletShotDamage (server.dll FUN_180235b10).
        /// Damage values are integers and every interpolation is truncated toward zero.
        /// A negative very-far value or distance means "no very-far band".
        /// </summary>
        public float DamageAt(float dist)
        {
            int near = (int)DamageNear, far = (int)DamageFar;
            int veryFar = S("damage_very_far_value") != null ? (int)DamageVeryFar : -1;
            float nearD = NearDist, farD = FarDist;
            float veryFarD = S("damage_very_far_distance") != null ? VeryFarDist : -1f;
            if (FalloffType == "inverse")
            {
                if (dist <= nearD) return near;
                if (farD <= dist)
                {
                    if (veryFar < 0 || veryFarD < 0f) return far;
                    near = far; nearD = farD;
                    if (veryFarD <= dist) return veryFar;
                }
                return (int)(near * InverseDist / ((dist - nearD) + InverseDist));
            }
            if (dist <= nearD) return near;
            if (dist < farD) return (int)((dist - nearD) / (farD - nearD) * (far - near) + near);
            if (veryFar < 0 || veryFarD < 0f) return far;
            if (veryFarD <= dist) return veryFar;
            return (int)((dist - farD) / (veryFarD - farD) * (veryFar - far) + far);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Max(0f, Math.Min(1f, t));

        public bool SemiAuto => FireMode.StartsWith("semi", StringComparison.OrdinalIgnoreCase) || FireMode == "single";
    }
}
