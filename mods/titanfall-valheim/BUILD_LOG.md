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

## 2026-10-07 — phase 5: real Titanfall models, animations, textures and sounds
- **Models.** `tools/mdl53.py` reads Titanfall 2 `studiomdl` v53 directly from the user's VPKs:
  header is Source's plus a name offset (+4 on every later field); bones are 244 bytes (adds
  scale/scalescale); VTX (0x1ac), VVD (0x1b0) and PHY (0x1b8) are embedded; v7 strip groups are 33
  bytes. BT's bind skeleton is "exploded" (hand bones in cm vs anims in inches, ratio 2.54), so the
  `@ref` animation is used as the rest pose.
- **Animations.** `tools/anim53.py` decodes v53 per-bone RLE records: posscale, bone, flags
  (0x02 raw pos Vector48, 0x04 raw rot Quaternion64, 0x08 raw scale, 0x10 rest rotation), Source
  RLE streams with per-bone `rotscale` and rest added unless delta, sections for long clips.
  Verified by skinning BT into `@ref` and walk poses offline (`tools/phm_preview.py`).
- **Export.** `tools/tf2_export.py` writes Unity-space PHM2 meshes (metres, Y up, mirrored axes,
  flipped winding) and PHA2 clips. Clip speeds come from the `jx_c_start` motion tracker
  (walk 274 u/s, run 474 u/s, matching the set file's 280/420 class speeds). Hot-drop impact
  from the fastest hip drop: **5.03 s** into `@at_hotdrop_drop_2knee_turbo`.
- **BT-7274 in game.** Real model on Valheim's `Custom/Creature` shader with the user's
  Legion+-exported textures, XO-16 on `ja_r_propHand` through the gun's `r_hand_ik` frame
  (Titanfall IK-pins the hand to the gun; we do the inverse). Clips: idle, walk/run x4 directions,
  sprint, dash x4 with ground-speed sync; hot drop timed so its impact frame meets the 2.5 s drop;
  kneels and waits for the pilot (stands up for enemies or a pilot > 2000 u away); embark from
  the kneel (`@at_MP_embark`, 3.8 s) or kneel-down from the pilot's side; disembark; death fall.
- **Jack Cooper replaces the Valheim body.** Valheim's player is a Unity Humanoid
  (`player_maleAvatar`); Unity's runtime-avatar retarget misread the A-pose, so the mod uses a
  per-bone rotation retarget: pilot = valheim × inverse(valheim T-pose) × pilot T-pose, hips scaled
  by leg height. Every Valheim action still animates; weapons stay on Valheim's hand bones.
  Found by the self-test: Valheim's animator culls bone updates once its body is hidden
  (`CullUpdateTransforms`) — it is set to always animate while the pilot body shows.
- **Sounds.** Legion+ exported the user's `general.mbnk` (34,402 waves); `tools/tf2_sounds.py`
  picks 65 for 23 gameplay slots (per-weapon fire, grapple, jump jets, wallrun, slide, cloak,
  stim, explosions, Titanfall inbound/landing, embark, disembark, Titan dash). WAVs decode on a
  worker thread, 5.1 mixes fold to mono, playback goes through Valheim's SFX mixer.
- **Performance.** `tools/tf2_textures.py` pre-compresses textures to DXT1/DXT5 (normals as
  DXT5nm) with mip chains, read on the worker thread and uploaded raw: building BT went from
  **7.5 s to 0.2 s**, the pilot body from 3.5 s to 0.09 s. A loading line shows while assets parse.
- Self-test hardening: fresh random test world every run, weapon tests on the sky arena (a tree
  once blocked the line of fire on terrain), retarget sampled after `LateUpdate`.
- Self-test: **62/62 pass, zero errors**, on three consecutive fresh random worlds.

## Remaining
Optional polish: Titan cockpit (first-person) view, pilot-specific Titanfall animations for
wallrun/slide layered over Valheim's, BT voice lines.
