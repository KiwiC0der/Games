using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PilotHeim.Assets
{
    /// <summary>
    /// Titanfall assets exported from the user's own install (tools/tf2_export.py + Legion+),
    /// read from DataDir/assets. Files are parsed on a worker thread at startup; Unity objects are
    /// created on the main thread when first needed. Everything is optional: without the files
    /// the mod uses its primitive stand-ins.
    /// </summary>
    public static class AssetLibrary
    {
        public static string Dir;
        public static PhModel Titan, Pilot;
        public static Dictionary<string, PhClip> TitanClips;
        public static string Status = "not started";
        public static bool Ready { get; private set; }
        public static bool Loading { get; private set; }
        private static Task task;

        public static void Preload(string dataDir)
        {
            Dir = Path.Combine(dataDir, "assets");
            TfMaterials.Root = Path.Combine(Dir, "materials");
            if (!Directory.Exists(Dir)) { Status = "no assets folder (stand-ins in use)"; return; }
            Loading = true; Status = "loading Titanfall assets…";
            task = Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string bt = Path.Combine(Dir, "bt.phm2"), bta = Path.Combine(Dir, "bt.pha");
                    if (File.Exists(bt)) Titan = PhModel.Load(bt);
                    if (File.Exists(bta)) TitanClips = PhClip.LoadAll(bta);
                    string jack = Path.Combine(Dir, "jack.phm2");
                    if (File.Exists(jack)) Pilot = PhModel.Load(jack);
                    Status = $"Titanfall assets ready: BT {(Titan != null ? "model" : "-")}, {TitanClips?.Count ?? 0} clips, pilot {(Pilot != null ? "model" : "-")} ({sw.ElapsedMilliseconds} ms)";
                    Ready = true;
                }
                catch (Exception e) { Status = "asset load failed: " + e.Message; }
                finally { Loading = false; }
            });
        }

        /// <summary>Blocks briefly if the worker is still parsing (called right before a Titan spawns).</summary>
        public static void Wait(int ms = 5000)
        {
            try { task?.Wait(ms); } catch (AggregateException) { }
        }
    }
}
