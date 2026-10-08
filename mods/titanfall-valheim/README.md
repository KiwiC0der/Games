# PilotHeim — the Titanfall 2 pilot in Valheim

A BepInEx mod that replaces the Valheim character with a Titanfall 2 pilot: Titanfall
movement physics, grapple, weapons, tacticals, frags, and a callable Titan (BT-7274) that
you can embark, drive, dash and fight with — inside Valheim's open world, with every
Valheim system (building, crafting, food, skills, inventory, saving, enemies) intact.

Everything Titanfall comes **from your own installed copy of Titanfall 2** at build/run
time: movement values and weapon stats from its scripts and `server.dll`, models,
animations, textures and sounds exported locally. This repository contains source code
only, no game files.

## What it does

| Area | Details |
| --- | --- |
| Movement | Respawn's walk/air/friction/accelerate code (recovered from `server.dll`), sprint, slide and slide-jump, double jump, wallrun (attach, slip, gravity ramp, 1.75 s limit), wall jump, bhop, no fall damage. Mid-step integration reproduces the 75.0 u jump apex exactly. |
| Grapple | Impulse, 50→800 u/s ramp, detach rules, power drain and regen. Grapple Valheim trees, rocks and buildings. |
| Weapons | R-201, Wingman, EVA-8 (cone blast), Kraber (ballistic bolt) and more from the weapon scripts; exact integer damage falloff; spread, kick, ADS, reloads. Hits are Valheim `HitData`, so resistances, skills and loot work. |
| Tacticals | Grapple, cloak (hides you from AI senses), stim, pulse blade; frag grenades. |
| Titan | Meter builds over `titan_build_time`; **V** calls in a 2.5 s hot drop that crushes what's under it; **E** embarks and disembarks; Titan walk/sprint/dash from the Titan set file; XO-16, salvo rockets, electric smoke, core; auto-titan follows or guards and fights; shields, doomed state and ejection. |
| Visuals | The real BT-7274 with his own animations (idle, walk/run/sprint, dash, hot drop and kneel, embark, disembark, death) and a first-person cockpit view. Jack Cooper replaces the Valheim body: with the pilot gun out, or wallrunning, sliding, jet-jumping or grappling, he plays Titanfall's pilot animations and holds the real weapon model; holstered, Valheim's animator drives him through a per-bone retarget, so chopping, mining, building, swimming and attacks animate as usual. The death ragdoll wears the pilot too. |
| Sound | Titanfall weapon, movement, grapple, cloak, stim, Titanfall and embark sounds, and BT-7274's voice (embark, disembark, kills, shields down, doomed, core), through Valheim's SFX mixer. |
| Your base | Pilot guns, explosions and Titanfall landings don't damage your buildings (option `WeaponsDamageBuildings`). A stuck or far-behind BT re-drops beside you. |
| HUD | Titanfall's own weapon names (from its localization), shields, hull, dash, salvo, smoke and core meters. The Titan meter is saved with your character. |

Without exported assets the mod still works with stand-in visuals and Valheim sounds.

## Controls (configurable)

| Key | Action | Key | Action |
| --- | --- | --- | --- |
| Space | Jump / double jump / wall jump | Left Ctrl | Slide (while sprinting) |
| Q | Tactical (grapple by default) | G | Frag grenade |
| Z | Draw / holster pilot gun | X | Swap weapon |
| R | Reload | V | Titanfall / toggle follow-guard |
| E | Embark / disembark | B | Cockpit / chase view (in the Titan) |
| F8 | Toggle pilot mode | | |

Holster the gun (Z) to use Valheim weapons, tools and building as normal. While pilot mode is on,
Q, G and V belong to the pilot (Valheim's autorun, radial menu and auto-pickup are bound to the same
keys by default), R and X belong to the gun only while it is drawn, and E embarks only next to BT;
rebinding either side in its own settings turns the overlap off.

## Setup

1. Install [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/).
2. Copy `src/PilotHeim/local.props.example` to `local.props` and set `ValheimDir`.
3. `dotnet build -c Release src/PilotHeim` builds and deploys into `BepInEx/plugins/PilotHeim`.
4. Produce the local data folder (default `Documents/PilotHeim-local/extracted`, set by `DataDir`):
   - Titanfall scripts: extract `scripts/` from `englishclient_mp_common.bsp.pak000_dir.vpk`
     with a Respawn VPK tool into `extracted/mp_common/scripts`.
   - Engine defaults: `tools/build_native_defaults.py` writes `native_defaults.json` from the
     `server.dll` analysis reports (see `BUILD_LOG.md`).
5. Optional assets (models, animations, textures, sounds) into `extracted/assets`:
   - `tools/tf2_export.py bt <titan_buddy.mdl> <titan_buddy_sp_core.mdl,...> <assets>`
   - `tools/tf2_export.py mesh <mlt_hero_jack.mdl> <assets> jack "mri=-1,head=0" --yup`
   - `tools/tf2_export.py mesh <w_xo16shorty.mdl> <assets> xo16`
   - Textures: Legion+ `--export common.rpak --loadimages --loadmaterials --imgfmt png`, then copy
     the material folders the models use into `assets/materials`, then
     `tools/tf2_textures.py <assets>/materials` to pre-compress them (fast loading).
   - Sounds: Legion+ `--export general.mbnk`, then `tools/tf2_sounds.py <export> <assets>`.
   - Pilot animations: `tools/tf2_export.py pilot <mlt_hero_jack.mdl> <pilot_light_core.mdl> <assets>`.
   - Pilot guns: `tools/tf2_weapons.py <scripts/weapons> <vpk dir> <tfvpktool dir> <Legion materials> <assets>`.
   - HUD names: extract `resource/r1_english.txt` into the data folder.
   - `tools/phm_preview.py` renders an exported mesh to PNG for a quick check.

## Tools

| File | Purpose |
| --- | --- |
| `tools/mdl53.py` | Titanfall 2 `studiomdl` v53 reader: bones, embedded VVD/VTX, LOD fixups, materials. |
| `tools/anim53.py` | v53 animation decoder: per-bone RLE tracks, Quaternion64, half-float raw values, sections. |
| `tools/tf2_export.py` | Writes Unity-space `.phm2` meshes and baked `.pha` clips. |
| `tools/tf2_sounds.py` | Picks waves per gameplay sound slot. |
| `tools/tf2_textures.py` | Pre-compresses textures to DXT mip chains (PHTEX). |
| `tools/tf2_weapons.py` | Exports every pilot weapon's world model and its materials. |
| `tools/build_native_defaults.py` | Builds engine defaults from the reverse-engineering reports. |

## Self-test

Set `Debug.SelfTest = true` in `BepInEx/config/tech.anteneh.pilotheim.cfg` and launch Valheim.
The mod creates a throwaway character and world, runs the movement, grapple, weapon, pilot
body and Titan checks on a sky arena and real terrain, writes
`BepInEx/PilotHeim_selftest.txt` (plus screenshots) and quits. See `BUILD_LOG.md` for results.

## Notes

- For single-player and private use with games you own. Not affiliated with Respawn,
  Electronic Arts or Iron Gate.
- Do not commit extracted or exported game files; `.gitignore` excludes them.
