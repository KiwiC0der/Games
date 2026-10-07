using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using PilotHeim.Data;
using UnityEngine;

namespace PilotHeim
{
    /// <summary>
    /// PilotHeim: replaces Valheim's player with a Titanfall 2 pilot. All tuning
    /// values are read at runtime from the user's own Titanfall 2 install (extracted
    /// locally by tools/extract-tf2-data); nothing from either game ships in this DLL.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tech.anteneh.pilotheim";
        public const string Name = "PilotHeim";
        public const string Version = "0.1.0";

        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        // Config
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<string> DataDir;
        public static ConfigEntry<string> PilotClass;
        public static ConfigEntry<KeyCode> KeySlide;
        public static ConfigEntry<KeyCode> KeyTactical;
        public static ConfigEntry<KeyCode> KeyOrdnance;
        public static ConfigEntry<KeyCode> KeyTitanfall;
        public static ConfigEntry<KeyCode> KeyEmbark;
        public static ConfigEntry<KeyCode> KeyToggleMod;
        public static ConfigEntry<bool> SelfTest;

        public static PlayerSettings Pilot { get; private set; }
        public static string ScriptsDir => Path.Combine(DataDir.Value, "mp_common", "scripts");

        private Harmony harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            string defaultData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PilotHeim-local", "extracted");
            Enabled = Config.Bind("General", "Enabled", true, "Play as a Titanfall 2 pilot.");
            DataDir = Config.Bind("General", "DataDir", defaultData, "Folder produced by tools/extract-tf2-data from your Titanfall 2 install.");
            PilotClass = Config.Bind("General", "PilotClass", "pilot_grapple_male", "Titanfall 2 player settings class to load.");
            KeySlide = Config.Bind("Controls", "Crouch/Slide", KeyCode.LeftControl, "Crouch; slide while sprinting.");
            KeyTactical = Config.Bind("Controls", "Tactical", KeyCode.Q, "Pilot tactical ability (grapple).");
            KeyOrdnance = Config.Bind("Controls", "Ordnance", KeyCode.G, "Throw ordnance.");
            KeyTitanfall = Config.Bind("Controls", "Titanfall", KeyCode.V, "Call in your Titan when the meter is full.");
            KeyEmbark = Config.Bind("Controls", "Embark", KeyCode.F, "Embark / disembark your Titan.");
            KeyToggleMod = Config.Bind("Controls", "TogglePilot", KeyCode.F8, "Switch between pilot and vanilla Valheim movement.");
            SelfTest = Config.Bind("Debug", "SelfTest", false, "Load a test world, run scripted movement checks and quit, writing results next to the log.");

            try
            {
                Pilot = PlayerSettings.Load(Path.Combine(ScriptsDir, "players", "mp"), PilotClass.Value);
                Log.LogInfo($"Loaded {Pilot.Name} ({string.Join(" <- ", Pilot.Chain)})");
                var native = NativeDefaults.Load(Path.Combine(DataDir.Value, "native_defaults.json"));
                var grapple = KeyValues.ParseFile(Path.Combine(ScriptsDir, "weapons", "mp_ability_grapple.txt")).Child("WeaponData");
                var tuning = new PilotTuning(Pilot, native, grapple);
                PilotHeim.Pilot.PilotController.Tuning = tuning;
                File.WriteAllLines(Path.Combine(Paths.BepInExRootPath, "PilotHeim_tuning.txt"), tuning.Report);
                Log.LogInfo($"Pilot tuning resolved: {tuning.Report.Count} values (see BepInEx/PilotHeim_tuning.txt)");
            }
            catch (Exception e)
            {
                Log.LogError("Titanfall 2 data not usable - run tools/extract-tf2-data first. Pilot mode disabled. " + e);
                Enabled.Value = false;
            }

            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void Update()
        {
            PilotHeim.SelfTest.Tick();
            if (Input.GetKeyDown(KeyToggleMod.Value) && Pilot != null)
            {
                Enabled.Value = !Enabled.Value;
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, Enabled.Value ? "Pilot mode" : "Viking mode");
            }
        }

        private void OnDestroy() => harmony?.UnpatchSelf();
    }
}
