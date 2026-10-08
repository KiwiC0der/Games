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
        public static Dictionary<string, PhClip> TitanClips, PilotClips;
        private static readonly Dictionary<string, PhModel> weapons = new Dictionary<string, PhModel>();

        /// <summary>A pilot weapon's world model (assets/weapons/&lt;id&gt;.phm2), loaded on first use.</summary>
        public static PhModel Weapon(string id)
        {
            lock (weapons)
            {
                if (weapons.TryGetValue(id, out var m)) return m;
                string f = Path.Combine(Dir ?? "", "weapons", id + ".phm2");
                try { m = File.Exists(f) ? PhModel.Load(f) : null; }
                catch (Exception e) { Plugin.Log.LogWarning($"weapon model {id}: {e.Message}"); m = null; }
                weapons[id] = m;
                return m;
            }
        }
        public static string Status = "not started";
        public static bool Ready { get; private set; }
        public static bool Loading { get; private set; }
        private static Task task;

        public static void Preload(string dataDir, string[] loadout = null)
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
                    string jacka = Path.Combine(Dir, "jack.pha");
                    if (File.Exists(jacka)) PilotClips = PhClip.LoadAll(jacka);
                    // read ahead the compressed textures of the models that will be built (BT, XO-16, the
                    // pilot and the loadout guns) so building them only uploads to the GPU
                    var used = new List<PhModel> { Titan, Pilot };
                    string xo = Path.Combine(Dir, "xo16.phm2");
                    if (File.Exists(xo)) used.Add(PhModel.Load(xo));
                    foreach (var id in loadout ?? new string[0]) used.Add(Weapon(id.Trim()));
                    if (Directory.Exists(TfMaterials.Root))
                        foreach (var model in used)
                        {
                            if (model == null) continue;
                            foreach (var sm in model.SubMeshes)
                            {
                                string mat = Path.Combine(TfMaterials.Root, Path.GetFileName(sm.Material.Replace('\\', '/')));
                                if (!Directory.Exists(mat)) continue;
                                foreach (var f in Directory.GetFiles(mat, "*.phtex")) TfMaterials.Preloaded[f] = File.ReadAllBytes(f);
                            }
                        }
                    Status = $"Titanfall assets ready: BT {(Titan != null ? "model" : "-")}, {TitanClips?.Count ?? 0} clips, pilot {(Pilot != null ? "model" : "-")} + {PilotClips?.Count ?? 0} clips, {TfMaterials.Preloaded.Count} textures read ahead ({sw.ElapsedMilliseconds} ms)";
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
