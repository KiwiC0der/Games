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

## 2026-10-07 — grapple fixed (phases 1-2 complete)
- Root causes: (1) Titanfall moves the player with the *mid-step* velocity (between the two
  gravity halves); handing PhysX the end-of-step velocity under-shot the jump apex by exactly
  sum(288-12k)*0.02 = 72.0 u. Fixed: apex now 75.0 u, matching Titanfall's discrete value.
  (2) Sliding is a crouched stance, so the protected wish speed is crouch speed (80).
  (3) Attaching the grapple clears the ground entity; friction no longer cancels the pull.
  (4) Self-test: Valheim tree prefabs snap to terrain, so the arena grapple now targets a pillar
  and a second test grapples a real world tree with a clear line of sight.
- Self-test: **20/20 pass, zero errors.** Real tree: hooked at 14.7 m, pulled to 2.6 m, peak
  675 u/s (ramp target 800).

## 2026-10-07 — phase 3: weapons, tacticals, ordnance
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

## 2026-10-07 — phase 4: Titanfall, BT-7274, embark
- Titan values come from the user's titan_buddy set file (stand speed 280, sprint 420,
  accel/decel/sprint accel, airSpeed, stepHeight 80, dodge* dash, health 9000 = 5 segments x 1800
  + doomed 2500 = 11500, shield 1000, titan_build_time 180 s, shield regen delay 6 s / 2.5 %/s)
  and damagedefs.txt (titan_fall 400 in 90-120 u, titan_hotdrop 150 in 80-250 u).
- V calls in the Titan when the meter is full (build time x BuildTimeScale): a red beacon, a
  2.5 s hot drop from 3000 u, then landing damage with linear falloff on everything but the owner.
  E embarks/disembarks; Space dashes (dodgeSpeed 685 u/s, 50 power, regen 12/s after 0.2 s);
  LMB XO-16, G salvo rockets, Q electric smoke, V core. Outside, the auto-titan follows (V toggles
  guard) and fights enemies it can see.
- The Titan is a real Valheim Character (troll clone: AI, drops, taming stripped; Players faction;
  non-persistent ZDO), so Valheim damage, status effects and physics work on it. Shields absorb
  first; on death the pilot ejects skyward and the wreck is removed.
- Movement shares the pilot's recovered walk code (`IMoverState`); dash uses the exact dodge
  formula (add = dir x dodgeSpeed, keep k = 1-|add|/|old| blended by dodgeKeepSpeedFrac, vertical
  sqrt(2 g dodgeHeight)). Measured slide-out after a dash: 18.2 m.
- Body is a primitive BT stand-in (hull-sized, militia stripe, cyan eye) until phase 5.
- Bugs found by the self-test and fixed:
  - Valheim's doodad controls overwrote test input each frame (test now drives via `Drive`).
  - Troll AI/drop callbacks (`m_onDamaged`, `m_onDeath`) outlived the destroyed components:
    any hit on the Titan threw in `MonsterAI.SetAlerted`. Cleared on spawn.
  - Destroying the Titan inside `OnDeath` broke `Character.ApplyDamage` (reads the ZDO after);
    destruction is now deferred one frame.
  - `EnemyHud` assumes every non-player has a BaseAI: skipped for the Titan (1865 NREs/run).
  - Creatures without a humanoid head bone threw in headshot checks: capsule-top fallback.
  - Auto-titan reloads never completed outside the cockpit (`BackgroundTick`).
  - Titan ground probe started inside sloped terrain: smaller, higher sphere + Valheim contact.
  - Torso bob accumulated every frame (BT's upper body floated away) — found from a screenshot.
- Self-test now uses a fixed dry meadow site near the start temple (the throwaway profile used
  to start wherever the last run ended — once underwater) and a flat sky runway for Titan
  driving (a 3 m hull can't walk through a beech forest).
- Self-test: **48/48 pass, zero errors.** Titanfall lands at 2.50 s and kills the troll below;
  health 11500, shield 1000; walk 280/280, sprint 420/420 (+-5 %), dash 685/685 and 50 power;
  XO-16 hits; disembark restores pilot movement; auto-titan kills a troll; death ejects the pilot.

## Remaining
Models, animations and sounds from the user's install (Legion+ / runtime loader, phase 5) ·
final polish and README.
