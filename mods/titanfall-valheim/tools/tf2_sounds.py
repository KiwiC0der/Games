"""
Pick Titanfall 2 waves for PilotHeim's gameplay sound slots.

Input : a Legion+ export of the user's own general.mbnk (folder of .wav files, any depth).
Output: <assets>/sounds/<slot>/*.wav, a handful of variants per slot (":" in slot names is
        written as "-" in folder names). Nothing here is committed to the repository.

Usage: python tf2_sounds.py <legion audio export dir> <assets dir>
"""
import os
import re
import shutil
import sys

MAX_VARIANTS = 6

# slot -> ordered list of regexes; the first pattern with matches wins
SLOTS = {
    "fire:mp_weapon_rspn101": [r"^wpn_r101_1p_wpnfire_firstshot_core", r"^wpn_r101_1p_wpnfire_loop_core2"],
    "fire:mp_weapon_wingman": [r"^wpn_wingman_1p_wpnfire_shot"],
    "fire:mp_weapon_shotgun": [r"^wpn_shotgun_1p_wpnfire_reduced", r"^wpn_shotgun_1p_wpnfire"],
    "fire:mp_weapon_sniper": [r"^wpn_krabersniper_1p_wpnfire_(midshot|airmechbass)", r"^wpn_krabersniper_1p_wpnfire_lsrstail_start"],
    "fire:mp_titanweapon_xo16_shorty": [r"^wpn_xo16_1p_wpnfire_firstshot_core", r"^wpn_xo16_1p_wpnfire"],
    "fire:mp_titanweapon_salvo_rockets": [r"^wpn_salvocore_1p_shots_c_"],
    "reload": [r"^3p_wpn_r101_reload_maginsert", r"^wpn_r101_emptyreload", r"_1p_reload_magin"],
    "grapple_fire": [r"^grapple_fire_"],
    "move:grapple_attach": [r"^extract_grapple_concrete", r"^grapple_(attach|impact|hit)"],
    "move:doublejump": [r"^jumpjet_jetstart_chuff_st", r"^jumpjet_jetstart"],
    "move:walljump": [r"^jumpjet_jetstart_chuff_small", r"^jumpjet_jetstart_chuff"],
    "move:wallrun": [r"^wallrun_start_", r"^1wallrun_start"],
    "move:slide": [r"^pilot_med_mvmt_bumpslide_grass", r"^pilot_med_mvmt_bumpslide"],
    "cloak": [r"^cloak_1p_activate_2ch", r"^cloak_1p_activate"],
    "stim": [r"^pilot_stim_1p_activate", r"^stimpack_1p_activate"],
    "sonar": [r"^.*pulse.*(blade|sonar).*(ping|pulse)", r"sonar.*pulse"],
    "throw": [r"^.*frag.*(throw|toss)", r"^.*grenade.*throw"],
    "explosion": [r"^explo_40mm_close", r"^explo_40mm_splash_impact_close", r"^ai_reaper_explo_close"],
    "titan:inbound": [r"^titan_warpfall_warptolanding_2ch"],
    "titan:land": [r"^tday_intro_titanfall_landing", r"^titan_.*land"],
    "titan:embark": [r"^bt_embark_casual_behind_2ch", r"^bt_embark_casual"],
    "titan:disembark": [r"^bt_disembark_1p"],
    "titan:dash": [r"^heavy_titan_dash_2ch", r"^light_titan_dash"],
    # BT-7274's voice (dry takes only; _L2/_L3 are the radio-filtered layers)
    "bt:embark": [r"^diag_gs_titanbt_embark_\d+[a-z]?$"],
    "bt:disembark": [r"^diag_gs_titanbt_disembark_\d+[a-z]?$"],
    "bt:kill": [r"^diag_gs_titanbt_elimtarget_\d+[a-z]?$"],
    "bt:critical": [r"^diag_gs_titanbt_briefcriticaldamage_\d+[a-z]?$"],
    "bt:doomed": [r"^diag_gs_titanbt_doomstate_\d+[a-z]?$"],
    "bt:core_ready": [r"^diag_gs_titanbt_coreburstready_\d+[a-z]?$", r"^diag_gs_titanbt_coreready_\d+[a-z]?$"],
    "bt:core": [r"^diag_gs_titanbt_coreburstactivated_\d+[a-z]?$", r"^diag_gs_titanbt_coreactivated_\d+[a-z]?$"],
    "bt:engage": [r"^diag_gs_titanbt_autoengagegrunt_\d+[a-z]?$"],
}


def main(src, assets):
    waves = {}
    for root, _, files in os.walk(src):
        for f in files:
            if f.lower().endswith(".wav"):
                waves.setdefault(os.path.splitext(f)[0].lower(), os.path.join(root, f))
    print(f"{len(waves)} waves in export")
    out_root = os.path.join(assets, "sounds")
    os.makedirs(out_root, exist_ok=True)
    total = 0
    for slot, patterns in SLOTS.items():
        picked = []
        for pat in patterns:
            rx = re.compile(pat, re.I)
            picked = sorted(n for n in waves if rx.search(n))
            if picked:
                break
        folder = os.path.join(out_root, slot.replace(":", "-"))
        if os.path.isdir(folder):
            # empty and reuse (removing the folder itself fails while anything holds a handle on it)
            for f in os.listdir(folder):
                os.remove(os.path.join(folder, f))
        if not picked:
            print(f"  {slot:36s} -- no match")
            continue
        os.makedirs(folder, exist_ok=True)
        for n in picked[:MAX_VARIANTS]:
            shutil.copy2(waves[n], os.path.join(folder, os.path.basename(waves[n])))
            total += 1
        print(f"  {slot:36s} {min(len(picked), MAX_VARIANTS)} of {len(picked)}  e.g. {picked[0]}")
    print(f"copied {total} waves into {out_root}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
