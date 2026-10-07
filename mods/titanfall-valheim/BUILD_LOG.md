# PilotHeim — Titanfall 2 pilot in Valheim (build log)

Local-only mod (BepInEx 5 / Harmony) that replaces Valheim's player movement with a
reverse-engineered Titanfall 2 pilot. **No game files are in this repo**: tuning is read at
runtime from the user's own Titanfall 2 install (extracted locally) and `native_defaults.json`
generated from their own `server.dll`.

## 2026-10-07 — session 1
- Recon: Titanfall 2 v2.0.11.0 (Respawn Source fork, VPK 2.3, 1441 rpaks, no anti-cheat);
  Valheim on Unity 6000.0.61f1 (Mono). Saves backed up; BepInExPack_Valheim 5.4.2351 installed.
- Extracted scripts (pilot/titan .set files, weapon .txt, Squirrel) with TFVPKTool.
- Ghidra 12.1.4 headless on server.dll: recovered the player-settings schema (401 entries with
  defaults + offsets), 489 ConVar defaults, and the movement code (AirMove, WalkMove/wallrun,
  friction, accelerate, jump/double jump/wall jump/skip, slide start, wallrun attach/detach,
  grapple impulse/ramp/accel/power).
- Implemented `PilotMotor` (Source tick order, mid-step integration), `PilotGrapple`, HUD,
  Harmony patches (UpdateWalking, fall damage, jump, friction, crouch, camera roll).
- Automated in-game self-test (throwaway character + sky arena):
  walk 173.5/173.5, sprint 260/260, jump apex 74.999/75.0, double jump 152.9/150,
  slide boost + stop, wallrun engage/limit/cap/boost, no fall damage, zero log errors —
  **17/18 pass**. Open issue: grapple ray hits the floor ~2.7 m ahead in the test (hook at feet
  height), so no pull — aim/ray filtering to fix next.

## 2026-10-07 � grapple fixed (phases 1-2 complete)
- Root causes: (1) Titanfall moves the player with the *mid-step* velocity (between the two
  gravity halves); handing PhysX the end-of-step velocity under-shot the jump apex by exactly
  sum(288-12k)*0.02 = 72.0 u. Fixed: apex now 75.0 u, matching Titanfall's discrete value.
  (2) Sliding is a crouched stance, so the protected wish speed is crouch speed (80).
  (3) Attaching the grapple clears the ground entity; friction no longer cancels the pull.
  (4) Self-test: Valheim tree prefabs snap to terrain, so the arena grapple now targets a pillar
  and a second test grapples a real world tree with a clear line of sight.
- Self-test: **20/20 pass, zero errors.** Real tree: hooked at 14.7 m, pulled to 2.6 m, peak
  675 u/s (ramp target 800).

## 2026-10-07 � phase 3: weapons, tacticals, ordnance
- Weapons load from the user's scripts/weapons/*.txt (base + SP_BASE or MP_BASE profile).
  Default loadout R-201, Wingman, EVA-8, Kraber; tactical on Q (Grapple/Cloak/Stim/PulseBlade);
  frag on G. Z draws/holsters (holstered = Valheim weapons/tools/building), X swaps, R reloads.
- Damage falloff recovered exactly from server.dll CalcBulletShotDamage (FUN_180235b10):
  integer damage, linear near->far->very-far, and inverse falloff near*inv/(d-near+inv).
- EVA-8 follows ShotgunBlast (one hit per target in a cone of half the spread, max 8);
  Kraber fires ballistic bolts (bolt_speed, bolt_gravity_amount*sv_gravity).
- Hits are Valheim HitData (pierce for bullets, blunt for explosions) so resistances,
  skills (Crossbows), loot and death stay Valheim's. Cloak hides you from AI senses.
- Stim speed = 1 + 0.4 x 2.0 (sh_stim.gnut). Stim healing rate and pulse-blade
  radius (explosionradius) / active time (grenade_ignition_time) are best readings
  of the data, not confirmed constants.
- Self-test: **30/30 pass, zero errors.** R-201 13 shots/s (13.5), exact falloff damage,
  empty reload 2.921 s (2.92); EVA-8 one hit per blast; Kraber 200; frag 74 at 203 u
  (= 200 x linear falloff); cloak blocks troll senses.

## Remaining
Grapple aim fix · weapons & tacticals (phase 3) · Titan call-in/embark (phase 4) ·
models/animations/sounds via Legion+ / runtime loader (phase 5).
