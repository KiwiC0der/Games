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

## Remaining
Grapple aim fix · weapons & tacticals (phase 3) · Titan call-in/embark (phase 4) ·
models/animations/sounds via Legion+ / runtime loader (phase 5).
