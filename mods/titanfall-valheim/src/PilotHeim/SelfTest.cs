using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using PilotHeim.Data;
using PilotHeim.Pilot;
using UnityEngine;

namespace PilotHeim
{
    /// <summary>
    /// Automated in-game verification (Debug.SelfTest = true). Creates a throwaway
    /// character + world, builds a test arena in the sky, drives scripted inputs
    /// through the pilot motor and checks each mechanic against the value implied
    /// by the Titanfall 2 data, then writes BepInEx/PilotHeim_selftest.txt and quits.
    /// Never touches the user's own characters or worlds.
    /// </summary>
    public sealed class SelfTest : MonoBehaviour
    {
        private const string ProfileName = "pilotheim_selftest";
        private const string WorldName = "PilotHeimTest";
        private const float U = PilotTuning.MetersPerUnit;

        private static bool started;
        public static GameObject WatchedObject;
        private readonly StringBuilder report = new StringBuilder();
        private int pass, fail;
        private PilotTuning t;

        public static void Tick()
        {
            if (started || !Plugin.SelfTest.Value) return;
            if (FejdStartup.instance == null || PilotController.Tuning == null) return;
            if (Time.realtimeSinceStartup < 8f) return;          // let the menu finish initialising
            started = true;
            var go = new GameObject("PilotHeim_SelfTest");
            DontDestroyOnLoad(go);
            go.AddComponent<SelfTest>();
        }

        private bool finished;

        private void Start()
        {
            StartCoroutine(Guard(Run(), "self-test"));
            StartCoroutine(Watchdog());
        }

        private IEnumerator Watchdog()
        {
            yield return new WaitForSecondsRealtime(300f);
            if (!finished) { Fail("watchdog", "self-test did not finish in 300 s"); Finish(); }
        }

        /// <summary>Runs a coroutine, turning any exception into a FAIL line instead of a silent hang.</summary>
        private IEnumerator Guard(IEnumerator inner, string name)
        {
            while (true)
            {
                object cur;
                try
                {
                    if (!inner.MoveNext()) break;
                    cur = inner.Current;
                }
                catch (Exception e)
                {
                    var root = e; while (root.InnerException != null) root = root.InnerException;
                    Fail(name, root.GetType().Name + ": " + root.Message + " @ " + root.StackTrace?.Split('\n')[0]);
                    Plugin.Log.LogError("[selftest] " + name + ": " + e);
                    yield break;
                }
                if (cur is IEnumerator nested) yield return Guard(nested, name);
                else yield return cur;
            }
        }

        private IEnumerator Run()
        {
            t = PilotController.Tuning;
            Line($"PilotHeim self-test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  tuning values={t.Report.Count}");
            yield return StartWorld();
            float wait = 0f;
            while ((Player.m_localPlayer == null || PilotController.Local == null) && wait < 120f) { wait += Time.deltaTime; yield return null; }
            if (Player.m_localPlayer == null) { Fail("spawn", "player never spawned"); Finish(); yield break; }
            Player.m_localPlayer.SetGodMode(true);
            ClearTestCreatures();
            yield return new WaitForSeconds(3f);
            ClearTestCreatures();

            var player = Player.m_localPlayer;
            var pc = PilotController.Local;
            var m = pc.Motor;
            player.SetGodMode(true);
            var input = new PilotMotor.InputState();
            m.Override = input;

            // anchor on a fixed, dry, level meadow near the start temple, so every run uses the same terrain
            // (the throwaway profile otherwise starts wherever the previous run ended)
            Vector3 spawnPos = FindTestSite();
            Line($"   test site: {spawnPos}");
            yield return Teleport(player, spawnPos + Vector3.up * 2f);
            for (float zw = 0f; zw < 15f && !ZoneSystem.instance.IsZoneLoaded(spawnPos); zw += 0.25f) yield return new WaitForSeconds(0.25f);
            yield return new WaitForSeconds(1.5f);                 // trees and rocks spawn after the heightmap
            spawnPos.y = ZoneSystem.instance.GetGroundHeight(spawnPos);
            yield return Teleport(player, spawnPos + Vector3.up * 0.5f);
            Vector3 origin = BuildArena(spawnPos + new Vector3(0f, 250f, 0f));
            yield return Teleport(player, origin + new Vector3(0f, 0.2f, -20f));

            // --- T1 walk speed
            yield return Settle(player, input);
            input.Look = Vector3.forward; input.Forward = 1f; input.Sprint = false;
            float walk = 0f;
            yield return Measure(2.0f, 0.6f, () => m.HorizontalSpeed, v => walk = v);
            Check("walk speed (u/s)", walk, t.Speed, 0.03f);

            // --- T2 sprint speed
            input.Sprint = true;
            float sprint = 0f;
            yield return Measure(2.0f, 0.6f, () => m.HorizontalSpeed, v => sprint = v);
            Check("sprint speed (u/s)", sprint, t.SprintSpeed, 0.03f);
            float stam = player.GetStamina();
            yield return new WaitForSeconds(1f);
            Check("sprint uses no stamina", player.GetStamina() >= stam - 0.01f ? 1f : 0f, 1f, 0f);

            // --- T3 jump apex
            yield return Teleport(player, origin + new Vector3(-10f, 0.2f, -20f));
            yield return Settle(player, input);
            float y0 = player.transform.position.y;
            float apex = y0;
            m.QueueJump();
            for (float tt = 0; tt < 1.4f; tt += Time.fixedDeltaTime) { apex = Mathf.Max(apex, player.transform.position.y); yield return new WaitForFixedUpdate(); }
            float expectApex = t.Gravity * 2f * t.JumpHeight / (2f * t.Gravity * t.GravityScale);   // v^2 / (2 g_eff)
            Check("jump apex (u)", (apex - y0) / U, expectApex, 0.02f);

            // --- T4 double jump
            yield return Settle(player, input);
            y0 = player.transform.position.y; apex = y0;
            m.QueueJump();
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            while (m.Vel.y > 0f) { apex = Mathf.Max(apex, player.transform.position.y); yield return new WaitForFixedUpdate(); }
            m.QueueJump();
            for (float tt = 0; tt < 1.4f; tt += Time.fixedDeltaTime) { apex = Mathf.Max(apex, player.transform.position.y); yield return new WaitForFixedUpdate(); }
            float expectDouble = expectApex + t.Gravity * 2f * t.SuperjumpMaxHeight / (2f * t.Gravity * t.GravityScale);
            Check("double jump total apex (u)", (apex - y0) / U, expectDouble, 0.02f);

            // --- T5 slide boost
            yield return Teleport(player, origin + new Vector3(10f, 0.2f, -25f));
            yield return Settle(player, input);
            input.Forward = 1f; input.Sprint = true;
            yield return new WaitForSeconds(1.5f);
            float before = m.HorizontalSpeed;
            input.Crouch = true;
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            float boosted = m.HorizontalSpeed;
            Check("slide started", m.Sliding ? 1f : 0f, 1f, 0f);
            Check("slide boost speed (u/s)", boosted, Mathf.Min(before + t.SlideSpeedBoost, t.SlideSpeedBoostCap), 0.04f);
            float slideStart = Time.time;
            while (m.Sliding && Time.time - slideStart < 6f) yield return null;
            Check("slide ends below slideStopSpeed", m.Sliding ? 0f : (m.HorizontalSpeed <= t.SlideStopSpeed + 5f ? 1f : 0f), 1f, 0f);
            input.Crouch = false; input.Forward = 0f;
            yield return new WaitForSeconds(0.5f);

            // --- T6 wallrun along the arena wall (wall at x = +6, running toward +z)
            yield return Teleport(player, origin + new Vector3(4.2f, 0.2f, -22f));
            yield return Settle(player, input);
            input.Look = Quaternion.Euler(0f, 15f, 0f) * Vector3.forward; input.Forward = 1f; input.Sprint = true;
            yield return new WaitForSeconds(1.0f);
            m.QueueJump();
            float wrStart = -1f, wrEnd = -1f, wrMaxH = 0f, wrVyAtStart = 0f;
            for (float tt = 0; tt < 3.5f; tt += Time.fixedDeltaTime)
            {
                if (m.Wallrunning && wrStart < 0f) { wrStart = Time.time; wrVyAtStart = m.Vel.y; }
                if (m.Wallrunning) wrMaxH = Mathf.Max(wrMaxH, m.HorizontalSpeed);
                if (!m.Wallrunning && wrStart >= 0f && wrEnd < 0f) wrEnd = Time.time;
                yield return new WaitForFixedUpdate();
            }
            Check("wallrun engaged", wrStart >= 0f ? 1f : 0f, 1f, 0f);
            if (wrStart >= 0f)
            {
                if (wrEnd < 0f) wrEnd = Time.time;
                Check("wallrun duration <= limit (s)", Mathf.Min(wrEnd - wrStart, t.WallrunTimeLimit + 0.05f), Mathf.Min(wrEnd - wrStart, t.WallrunTimeLimit + 0.05f), 0f);
                Line($"   wallrun lasted {wrEnd - wrStart:0.00}s (limit {t.WallrunTimeLimit}), max horiz {wrMaxH:0} u/s (cap {t.WallrunMaxSpeedHorizontal}), vy at start {wrVyAtStart:0}");
                Check("wallrun horizontal speed <= cap", wrMaxH <= t.WallrunMaxSpeedHorizontal * 1.02f + 1f ? 1f : 0f, 1f, 0f);
                Check("wallrun up-wall boost applied", wrVyAtStart >= t.WrUpWallBoost * 0.6f ? 1f : 0f, 1f, 0f);
            }
            input.Forward = 0f; input.Sprint = false;
            yield return new WaitForSeconds(1f);

            // --- T7 grapple a real Valheim tree
            yield return Teleport(player, origin + new Vector3(-12f, 0.2f, 0f));
            yield return Settle(player, input);
            var tree = GameObject.Find("PilotHeim_TestTree");
            Line($"   test tree: {(tree != null ? tree.transform.position.ToString() : "none")} (arena floor y={origin.y:0.0})");
            bool treeOnArena = tree != null && Mathf.Abs(tree.transform.position.y - origin.y) < 1f;
            Vector3 aimAt = treeOnArena ? tree.transform.position + Vector3.up * 6f : origin + new Vector3(-12f, 6f, 15f);
            Line($"   grapple target: {(treeOnArena ? "Beech1 tree" : "arena pillar")}");
            Vector3 eye = player.transform.position + Vector3.up * 1.6f;
            input.Look = (aimAt - eye).normalized;
            yield return new WaitForFixedUpdate();
            float powerBefore = m.Grapple.Power;
            m.Grapple.Fire(eye, (aimAt - eye).normalized);
            bool attached = false; float maxAlong = 0f; float d0 = Vector3.Distance(player.transform.position, aimAt);
            int traced = 0; string lastEv = "";
            for (float tt = 0; tt < 4f; tt += Time.fixedDeltaTime)
            {
                if (m.Grapple.Attached) { attached = true; maxAlong = Mathf.Max(maxAlong, m.Vel.magnitude); }
                if (traced < 60 && (m.Grapple.Phase != PilotGrapple.State.Idle || m.LastEvent != lastEv))
                {
                    Line($"   t={tt:0.00} phase={m.Grapple.Phase} ground={m.OnGround} vel=({m.Vel.x:0},{m.Vel.y:0},{m.Vel.z:0}) pos=({player.transform.position.x:0.0},{player.transform.position.y:0.0},{player.transform.position.z:0.0}) ev={m.LastEvent} hook={m.Grapple.DebugHook}");
                    traced++; lastEv = m.LastEvent;
                }
                yield return new WaitForFixedUpdate();
            }
            float d1 = Vector3.Distance(player.transform.position, aimAt);
            Check("grapple attached", attached ? 1f : 0f, 1f, 0f);
            Check("grapple pulled toward target", d1 < d0 - 2f ? 1f : 0f, 1f, 0f);
            Check("grapple speed <= ramp max (u/s)", maxAlong <= t.GrappleSpeedRampMax * 1.15f + t.Gravity * 0.2f ? 1f : 0f, 1f, 0f);
            Check("grapple used power", powerBefore - m.Grapple.Power > 1f ? 1f : 0f, 1f, 0f);
            Line($"   grapple: start dist {d0:0.0} m -> {d1:0.0} m, peak speed {maxAlong:0} u/s, power {powerBefore:0} -> {m.Grapple.Power:0}");

            // --- T7b grapple a real world tree on real terrain
            yield return GrappleRealTree(player, m, input, spawnPos);

            // --- T8 no fall damage
            float hp = player.GetHealth();
            player.SetGodMode(false);
            yield return Teleport(player, origin + new Vector3(0f, 40f, 0f));
            float fallT = 0f;
            while (!m.OnGround && fallT < 8f) { fallT += Time.deltaTime; yield return null; }
            yield return new WaitForSeconds(0.5f);
            Check("no fall damage from 40 m", Mathf.Abs(player.GetHealth() - hp) < 0.01f ? 1f : 0f, 1f, 0f);
            player.SetGodMode(true);

            // --- T10.. pilot weapons against a real Valheim creature
            yield return Guard(WeaponTests(player, m, pc.Arsenal, input, spawnPos), "weapons");

            // --- T20.. Titan
            yield return Guard(TitanTests(player, m, pc, input, spawnPos), "titan");

            // --- T9 Valheim still Valheim
            Check("inventory intact", player.GetInventory() != null ? 1f : 0f, 1f, 0f);
            m.Override = null;
            Finish();
        }

        // ------------------------------------------------------------------ world
        private IEnumerator StartWorld()
        {
            var fs = FejdStartup.instance;
            if (!PlayerProfile.HaveProfile(ProfileName))
            {
                var prof = new PlayerProfile(ProfileName, FileHelpers.FileSource.Local);
                prof.SetName("Pilot");
                prof.m_firstSpawn = false;
                prof.Save();
            }
            Game.SetProfile(ProfileName, FileHelpers.FileSource.Local);
            var world = World.GetCreateWorld(WorldName, FileHelpers.FileSource.Local);
            ZNet.m_onlineBackend = OnlineBackendType.Steamworks;
            fs.m_startingWorld = true;
            ZNet.SetServer(true, false, false, world.m_name, "", world);
            ZNet.ResetServerHost();
            Line($"world '{world.m_name}' seed '{world.m_seedName}', profile '{ProfileName}'");
            fs.TransitionToMainScene();
            yield return null;
        }

        private static Vector3 BuildArena(Vector3 c)
        {
            int layer = LayerMask.NameToLayer("static_solid");
            void Box(string name, Vector3 pos, Vector3 size)
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                g.name = name;
                g.layer = layer;
                g.transform.position = pos;
                g.transform.localScale = size;
                g.GetComponent<Renderer>().sharedMaterial = PilotHeim.Titan.TitanVisual.Mat(new Color(0.5f, 0.5f, 0.52f));
            }
            Box("PilotHeim_Floor", c + new Vector3(0f, -0.5f, 0f), new Vector3(80f, 1f, 80f));
            Box("PilotHeim_Wall", c + new Vector3(6.5f, 6f, 0f), new Vector3(1f, 12f, 60f));
            Box("PilotHeim_Pillar", c + new Vector3(-12f, 10f, 15f), new Vector3(1.2f, 20f, 1.2f));
            return c;
        }

        private IEnumerator WeaponTests(Player player, PilotMotor m, PilotArsenal a, PilotMotor.InputState input, Vector3 ground)
        {
            if (a == null) { Fail("weapons", "arsenal not created"); yield break; }
            // fight on real terrain, a fresh troll per weapon
            Vector3 stand = ground; stand.y = ZoneSystem.instance.GetGroundHeight(stand);
            Vector3 spot = stand + Vector3.forward * 15f; spot.y = ZoneSystem.instance.GetGroundHeight(spot);
            var prefab = ZNetScene.instance.GetPrefab("Troll");
            if (prefab == null) { Fail("weapons", "Troll prefab missing"); yield break; }
            GameObject trollGo = null; Character troll = null;
            var hits = new List<(float dmg, float dist)>();
            a.OnHit = (d, c, dist) => { if (troll != null && c == troll) hits.Add((d, dist)); };

            IEnumerator Spawn(Vector3 at)
            {
                if (trollGo != null) Destroy(trollGo);
                trollGo = Instantiate(prefab, at + Vector3.up * 0.3f, Quaternion.Euler(0f, 180f, 0f));
                troll = trollGo.GetComponent<Character>();
                var ai = trollGo.GetComponent<MonsterAI>();
                if (ai != null) ai.enabled = false;
                hits.Clear();
                yield return new WaitForSeconds(0.8f);
            }
            bool Alive() => troll != null && troll.m_nview != null && troll.m_nview.IsValid() && !troll.IsDead();
            void Aim()
            {
                if (troll == null) return;
                input.Look = ((troll.transform.position + Vector3.up * 2.2f) - GameCamera.instance.transform.position).normalized;
            }
            IEnumerator Equip(System.Predicate<WeaponDef> pick)
            {
                a.Current = Mathf.Max(0, a.Loadout.FindIndex(pick));
                a.Drawn = true;
                yield return new WaitForSeconds(a.Weapon.DeployTime + 0.15f);
                Aim(); yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            }
            void Pull(bool fire, bool ordnance = false) => a.Update(Time.deltaTime, true, fire, false, false, false, false, false, ordnance);

            yield return Teleport(player, stand + Vector3.up * 0.3f);
            yield return Settle(player, input);

            // --- R-201: fire rate, falloff damage, kill, reload
            yield return Spawn(spot);
            yield return Equip(w => w.Id == "mp_weapon_rspn101");
            var r201 = a.Weapon;
            float hp0 = troll.GetHealth();
            int shots0 = a.ShotsFired;
            float t0 = Time.time;
            while (Time.time - t0 < 1.0f) { Aim(); Pull(true); yield return null; }
            int fired = a.ShotsFired - shots0;
            Check("R-201 fire rate (shots/s)", fired / 1.0f, r201.FireRate, 0.12f);
            bool exact = hits.Count > 0;
            foreach (var h in hits)
            {
                float body = r201.DamageAt(h.dist);
                if (Mathf.Abs(h.dmg - body) > 0.01f && Mathf.Abs(h.dmg - body * r201.HeadshotScale) > 0.01f) exact = false;
            }
            Line($"   R-201: {fired} shots, {hits.Count} hits, first {(hits.Count > 0 ? hits[0].dmg : 0):0.#} at {(hits.Count > 0 ? hits[0].dist : 0):0} u");
            Check("R-201 hits use exact falloff damage", exact ? 1f : 0f, 1f, 0f);
            yield return new WaitForSeconds(0.3f);
            bool dead = !Alive();
            Line($"   troll after 1 s of R-201: {(dead ? "killed" : $"hp {troll.GetHealth():0}/{hp0:0}")}");
            Check("troll damaged or killed by R-201", dead || troll.GetHealth() < hp0 ? 1f : 0f, 1f, 0f);
            while (a.Clip > 0 && Time.time - t0 < 8f) { Pull(true); yield return null; }
            float reloadStart = Time.time;
            while (a.Reloading && Time.time - reloadStart < 10f) { Pull(false); yield return null; }
            Check("R-201 empty reload time (s)", Time.time - reloadStart, r201.ReloadEmptyTime, 0.08f);
            Check("R-201 clip refilled", a.Clip, r201.ClipSize, 0f);

            // --- EVA-8: one cone blast = one hit per target
            yield return Spawn(spot);
            yield return Equip(w => w.IsShotgun);
            Pull(false); Pull(true);
            yield return new WaitForFixedUpdate();
            Check("EVA-8 blast hits troll exactly once", hits.Count, 1f, 0f);
            if (hits.Count > 0) Line($"   EVA-8: {hits[0].dmg:0} at {hits[0].dist:0} u (inverse falloff, near {a.Weapon.DamageNear} to {a.Weapon.NearDist} u)");

            // --- Kraber: ballistic bolt
            yield return Spawn(spot);
            yield return Equip(w => w.IsProjectile);
            int kShots0 = a.ShotsFired;
            a.LastBoltImpact = "none";
            for (float k0 = 0f; k0 < 3f && hits.Count == 0; k0 += Time.deltaTime)
            {
                Aim();
                Pull(((int)(k0 * 10f)) % 2 == 0);       // tap the semi-auto trigger until a bolt connects
                yield return null;
            }
            Line($"   Kraber: {a.ShotsFired - kShots0} shots, last impact {a.LastBoltImpact}");
            Check("Kraber bolt hits troll", hits.Count > 0 ? 1f : 0f, 1f, 0f);
            if (hits.Count > 0) Line($"   Kraber: {hits[0].dmg:0} at {hits[0].dist:0} u");

            // --- Frag: lobbed at the troll's feet from 7 m
            yield return Spawn(spot);
            a.Drawn = false;
            a.OrdnanceAmmo = 200f;
            yield return Teleport(player, troll.transform.position + new Vector3(0f, 0.3f, -7f));
            yield return Settle(player, input);
            input.Look = ((troll.transform.position + Vector3.up * 0.3f) - GameCamera.instance.transform.position).normalized;
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            Pull(false, true);
            float ft = Time.time;
            while (hits.Count == 0 && Time.time - ft < a.OrdnanceDef.FuseTime + 2f) yield return new WaitForFixedUpdate();
            Check("frag explosion damages troll", hits.Count > 0 ? 1f : 0f, 1f, 0f);
            if (hits.Count > 0) Line($"   frag: {hits[0].dmg:0} blunt at {hits[0].dist:0} u (explosion_damage {a.OrdnanceDef.ExplosionDamage}, radius {a.OrdnanceDef.ExplosionRadius} u)");

            // --- Cloak vs the troll's senses
            yield return Spawn(spot);
            var ai2 = trollGo.GetComponent<MonsterAI>();
            if (ai2 != null)
            {
                ai2.enabled = true;
                a.SetCloakForTest(5f);
                Check("cloaked pilot not sensed by troll", ai2.CanSenseTarget(player) ? 0f : 1f, 1f, 0f);
                a.SetCloakForTest(0f);
                yield return null;
                Check("visible pilot sensed by troll", ai2.CanSenseTarget(player) ? 1f : 0f, 1f, 0f);
            }
            a.OnHit = null;
            a.Drawn = false;
            if (trollGo != null) Destroy(trollGo);
        }

        private IEnumerator TitanTests(Player player, PilotMotor m, PilotController pc, PilotMotor.InputState input, Vector3 ground)
        {
            var tt = PilotHeim.Titan.TitanController.Tuning;
            if (tt == null) { Fail("titan", "titan tuning missing"); yield break; }
            Vector3 stand = ground; stand.y = ZoneSystem.instance.GetGroundHeight(stand);
            yield return Teleport(player, stand + Vector3.up * 0.3f);
            yield return Settle(player, input);
            input.Look = Vector3.forward;
            yield return new WaitForSeconds(0.3f);

            // troll waiting under the drop point (titanfall damage)
            Vector3 drop = stand + Vector3.forward * 22f; drop.y = ZoneSystem.instance.GetGroundHeight(drop);
            var trollPrefab = ZNetScene.instance.GetPrefab("Troll");
            var victim = Instantiate(trollPrefab, drop + new Vector3(4f, 0.3f, 0f), Quaternion.identity);
            var vai = victim.GetComponent<MonsterAI>(); if (vai) vai.enabled = false;
            var vch = victim.GetComponent<Character>();
            yield return new WaitForSeconds(0.5f);
            float vhp = vch.GetHealth();

            TitanMeter.Fraction = 1f;
            float called = Time.time;
            var titan = PilotHeim.Titan.TitanController.CallIn(player, drop, PilotController.Tuning);
            Check("titan called in", titan != null ? 1f : 0f, 1f, 0f);
            if (titan == null) yield break;
            while (titan.Phase == PilotHeim.Titan.TitanController.State.Dropping && Time.time - called < 10f) yield return null;
            float landT = Time.time - called;
            Check("titanfall lands after the drop time (s)", landT, PilotHeim.Titan.TitanController.DropTime, 0.1f);
            yield return new WaitForSeconds(0.4f);
            Check("titanfall hit the troll", titan.LastLandingHits > 0 ? 1f : 0f, 1f, 0f);
            Line($"   titanfall: landing hits {titan.LastLandingHits}, troll hp {vhp:0} -> {(vch != null && vch.m_nview.IsValid() ? vch.GetHealth().ToString("0") : "dead")}");
            Check("titan health = segments + doomed", titan.Body.GetHealth(), titan.MaxHealth, 0.001f);
            Check("titan shield", titan.Shield, tt.HealthShield, 0f);
            bool assets = System.IO.File.Exists(System.IO.Path.Combine(PilotHeim.Assets.AssetLibrary.Dir ?? "", "bt.phm2"));
            Line($"   assets: {PilotHeim.Assets.AssetLibrary.Status}; textures {PilotHeim.Assets.TfMaterials.TexturesLoaded}, missing {PilotHeim.Assets.TfMaterials.Missing}");
            if (assets)
            {
                Check("BT is the exported Titanfall model", titan.Visual.RealModel ? 1f : 0f, 1f, 0f);
                Check("BT textures loaded", PilotHeim.Assets.TfMaterials.TexturesLoaded > 0 && PilotHeim.Assets.TfMaterials.Missing == 0 ? 1f : 0f, 1f, 0f);
            }
            if (victim != null) Destroy(victim);


            // embark
            m.Override = null;                                   // the Titan reads Valheim's doodad controls
            yield return Teleport(player, titan.transform.position + titan.transform.right * 3f + Vector3.up * 0.3f);
            yield return new WaitForSeconds(0.3f);
            Check("can embark beside titan", titan.CanEmbark(player) ? 1f : 0f, 1f, 0f);
            titan.Embark(player);
            yield return new WaitForSeconds(0.5f);
            Check("pilot embarked (attached)", player.IsAttached() && titan.Phase == PilotHeim.Titan.TitanController.State.Piloted ? 1f : 0f, 1f, 0f);

            // drive: walk, sprint, dash
            // drive tests on a flat sky runway: a 3 m Titan cannot walk through a Valheim beech forest
            Vector3 runway = stand + new Vector3(0f, 45f, 160f);      // above the beech canopy, below the cloud layer
            {
                var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                g.name = "PilotHeim_TitanRunway"; g.layer = LayerMask.NameToLayer("static_solid");
                g.transform.position = runway + new Vector3(0f, -0.5f, 0f);
                g.transform.localScale = new Vector3(90f, 1f, 220f);
                g.GetComponent<Renderer>().sharedMaterial = PilotHeim.Titan.TitanVisual.Mat(new Color(0.45f, 0.47f, 0.5f));
            }
            Vector3 runStart = runway + new Vector3(0f, 0.1f, -100f);
            titan.Body.m_body.position = runStart; titan.transform.position = runStart;
            titan.transform.rotation = Quaternion.identity;
            titan.Motor.ResetVelocity();
            yield return new WaitForSeconds(0.5f);
            Vector3 fwd = Vector3.forward;
            titan.ExternalDrive = true;
            for (float t0 = 0; t0 < 1.2f; t0 += Time.fixedDeltaTime) { titan.Drive(Vector3.zero, fwd, false); yield return new WaitForFixedUpdate(); }
            float Speed() => new Vector3(titan.Motor.Vel.x, 0f, titan.Motor.Vel.z).magnitude;
            for (float t0 = 0; t0 < 3f; t0 += Time.fixedDeltaTime) { titan.Drive(fwd, fwd, false); yield return new WaitForFixedUpdate(); }
            Line($"   titan walk: onGround {titan.Motor.OnGround}, normal {titan.Motor.GroundNormal}, dir {fwd}, pos {titan.transform.position}");
            Check("titan walk speed (u/s)", titan.Motor.Vel.magnitude, tt.Speed, 0.05f);
            if (titan.Visual.RealModel) { Line($"   BT clip while walking: {titan.Visual.Anim.Current} x{titan.Visual.Anim.Rate:0.00}"); Check("BT plays its walk clip", titan.Visual.Anim.Current == "walk_f" ? 1f : 0f, 1f, 0f); }
            for (float t0 = 0; t0 < 4f; t0 += Time.fixedDeltaTime) { titan.Drive(fwd, fwd, true); yield return new WaitForFixedUpdate(); }
            Check("titan sprint speed (u/s)", titan.Motor.Vel.magnitude, tt.SprintSpeed, 0.05f);   // along the ground plane
            if (titan.Visual.RealModel) { Line($"   BT clip while sprinting: {titan.Visual.Anim.Current} x{titan.Visual.Anim.Rate:0.00}"); Check("BT plays its sprint clip", titan.Visual.Anim.Current == "sprint_f" ? 1f : 0f, 1f, 0f); }
            float powerBefore = titan.Motor.Power;
            var dashField = typeof(PilotHeim.Titan.TitanController).GetField("dashQueued", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Vector3 side = Vector3.Cross(Vector3.up, fwd);
            Vector3 dashFrom = titan.transform.position;
            titan.Drive(side, fwd, false);
            dashField.SetValue(titan, true);
            yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
            Line($"   dash: horizontal {Speed():0} u/s right after (dodgeSpeed {tt.DodgeSpeed}), power {powerBefore:0} -> {titan.Motor.Power:0}");
            if (titan.Visual.RealModel) Check("BT plays its dash clip (right)", titan.Visual.Anim.Current == "dash_r" ? 1f : 0f, 1f, 0f);
            Check("titan dash reaches dodgeSpeed", Speed() >= tt.DodgeSpeed * 0.9f ? 1f : 0f, 1f, 0f);
            Check("dash used dodgePowerDrain", powerBefore - titan.Motor.Power, tt.DodgePowerDrain, 0.1f);
            for (float t0 = 0; t0 < 1.5f; t0 += Time.fixedDeltaTime) { titan.Drive(Vector3.zero, fwd, false); yield return new WaitForFixedUpdate(); }
            titan.ExternalDrive = false;
            Line($"   dash travel incl. slide-out: {Vector3.Distance(titan.transform.position, dashFrom):0.0} m");

            // XO-16 + salvo vs a troll in front
            Vector3 tp = titan.transform.position + Vector3.forward * 18f; tp.y = runway.y;
            var target = Instantiate(trollPrefab, tp + Vector3.up * 0.3f, Quaternion.identity);
            var tai = target.GetComponent<MonsterAI>(); if (tai) tai.enabled = false;
            var tch = target.GetComponent<Character>();
            yield return new WaitForSeconds(0.6f);
            int hits = 0;
            titan.Arsenal.OnHit = (d, c, dist) => { if (c == tch) hits++; };
            titan.Arsenal.AimProvider = () => { var e = titan.transform.position + Vector3.up * 4.5f; return new Ray(e, ((tch != null ? tch.transform.position + Vector3.up * 2f : e + Vector3.forward) - e).normalized); };
            for (float t0 = 0; t0 < 1f; t0 += Time.deltaTime) { titan.Arsenal.TryFireNow(); yield return null; }
            Check("XO-16 hits troll", hits > 0 ? 1f : 0f, 1f, 0f);
            Line($"   XO-16: {hits} hits in 1 s (fire_rate {titan.Arsenal.Weapon.FireRate})");
            titan.Arsenal.AimProvider = null;
            titan.Arsenal.OnHit = null;
            if (target != null) Destroy(target);

            // disembark
            titan.Disembark(false);
            yield return new WaitForSeconds(0.5f);
            Check("pilot disembarked", !player.IsAttached() && titan.Phase == PilotHeim.Titan.TitanController.State.Auto ? 1f : 0f, 1f, 0f);
            Check("pilot movement active again", pc.Active ? 1f : 0f, 1f, 0f);

            // screenshot for visual review: pilot at the Titan's front quarter, open sky behind
            m.Override = input;
            yield return Teleport(player, titan.transform.position + titan.transform.forward * 9f + titan.transform.right * 4f + Vector3.up * 0.3f);
            input.Look = (titan.transform.position + Vector3.up * 3f - GameCamera.instance.transform.position).normalized;
            yield return new WaitForSeconds(1.2f);
            string shot = System.IO.Path.Combine(Paths.BepInExRootPath, "PilotHeim_titan.png");
            ScreenCapture.CaptureScreenshot(shot);
            yield return new WaitForSeconds(0.5f);
            Line($"   screenshot: {shot}");
            var chest = titan.transform.Find("PilotHeim_TitanVisual/torso");
            if (!titan.Visual.RealModel)
                Check("titan torso stays on the hips (m)", chest != null ? chest.localPosition.y : -1f, tt.HullHeight * PilotTuning.MetersPerUnit * 0.42f, 0.05f);

            // auto-titan defends: troll near the titan gets shot without input
            Vector3 ap = titan.transform.position + Vector3.forward * 15f; ap.y = runway.y;
            var enemy = Instantiate(trollPrefab, ap + Vector3.up * 0.3f, Quaternion.identity);
            var eai = enemy.GetComponent<MonsterAI>(); if (eai) eai.enabled = false;
            var ech = enemy.GetComponent<Character>();
            yield return new WaitForSeconds(0.5f);
            float ehp = ech.GetHealth();
            yield return new WaitForSeconds(3f);
            bool hurt = ech == null || !ech.m_nview.IsValid() || ech.GetHealth() < ehp;
            Line($"   auto-titan: target {(titan.AiTarget != null ? titan.AiTarget.name : "none")}, troll hp {ehp:0} -> {(ech != null && ech.m_nview.IsValid() ? ech.GetHealth().ToString("0") : "dead")} {titan.AiDebug}");
            Check("auto-titan engages enemies", hurt ? 1f : 0f, 1f, 0f);
            if (enemy != null) Destroy(enemy);

            // titan death: eject + cleanup
            titan.Embark(player);
            yield return new WaitForSeconds(0.3f);
            titan.Shield = 0f;
            titan.Body.SetHealth(1f);
            var kill = new HitData(); kill.m_damage.m_blunt = 50f; kill.m_point = titan.transform.position;
            titan.Body.Damage(kill);
            yield return new WaitForSeconds(1.0f);
            Check("titan destroyed on death", PilotHeim.Titan.TitanController.Current == null ? 1f : 0f, 1f, 0f);
            Check("pilot ejected on titan death", !player.IsAttached() ? 1f : 0f, 1f, 0f);
            yield return new WaitForSeconds(3f);
            m.Override = input;
        }

        private IEnumerator GrappleRealTree(Player player, PilotMotor m, PilotMotor.InputState input, Vector3 near)
        {
            var trees = new List<TreeBase>();
            foreach (var tb in FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
            {
                Vector3 dd = tb.transform.position - near; dd.y = 0f;
                if (dd.magnitude < 150f && Physics.Raycast(tb.transform.position + Vector3.up * 2f, Vector3.down, 6f, Character.s_groundRayMask)) trees.Add(tb);
            }
            trees.Sort((x, y) => Vector3.Distance(x.transform.position, near).CompareTo(Vector3.Distance(y.transform.position, near)));
            Line($"   grounded trees within 150 m: {trees.Count}");
            int tried = 0;
            foreach (var tb in trees)
            {
                if (tried++ >= 12) break;
                Vector3 trunk = tb.transform.position;
                Vector3 away = Vector3.ProjectOnPlane(near - trunk, Vector3.up);
                if (away.sqrMagnitude < 0.01f) away = Vector3.back;
                away = Quaternion.Euler(0f, 37f * (tried - 1), 0f) * away.normalized;   // a different approach angle per try
                Vector3 stand = trunk + away * 12f;
                stand.y = ZoneSystem.instance.GetGroundHeight(stand);
                if (Mathf.Abs(stand.y - trunk.y) > 2f || stand.y < ZoneSystem.instance.m_waterLevel + 0.5f)
                { Line($"   tree {tried}: stand point not level/dry ({stand.y - trunk.y:0.0} m), next"); continue; }
                yield return Teleport(player, stand + Vector3.up * 0.3f);
                yield return Settle(player, input);
                Vector3 eye = player.transform.position + Vector3.up * 1.6f;
                Vector3 aim = trunk + Vector3.up * 4f;
                Vector3 dir = (aim - eye).normalized;
                // any tree is a fine grapple target in a dense forest; only terrain/rocks block the shot
                if (!Physics.Raycast(eye, dir, out var los, 40f, Character.s_groundRayMask | Character.s_characterLayerMask, QueryTriggerInteraction.Ignore)
                    || los.collider.GetComponentInParent<TreeBase>() == null || los.distance < 5f)
                {
                    Line($"   tree {tried}: no clear line of sight ({(los.collider != null ? los.collider.transform.root.name + "/" + los.collider.name + $" at {los.distance:0.0} m" : "nothing")}), next");
                    continue;
                }
                // the body must fit along the pull path too (bushes, rocks and branches stop a pilot, not a ray)
                Vector3 body0 = player.transform.position + Vector3.up * 0.9f;
                Vector3 toHook = los.point - body0;
                bool pathBlocked = false;
                foreach (var ph in Physics.SphereCastAll(body0, 0.45f, toHook.normalized, Mathf.Max(0f, toHook.magnitude - 1.5f), Character.s_groundRayMask | Character.s_blockedRayMask, QueryTriggerInteraction.Ignore))
                    if (ph.collider.GetComponentInParent<Player>() == null && ph.collider.GetComponentInParent<TreeBase>() != los.collider.GetComponentInParent<TreeBase>() && ph.distance > 0f) { pathBlocked = true; Line($"   tree {tried}: pull path blocked by {ph.collider.transform.root.name}, next"); break; }
                if (pathBlocked) continue;
                input.Look = dir;
                yield return new WaitForFixedUpdate();
                Vector3 hook = los.point;
                float d0 = Vector3.Distance(player.transform.position, hook);
                m.Grapple.Fire(eye, dir);
                bool attached = false; float minD = d0, peak = 0f;
                for (float tt = 0; tt < 4f; tt += Time.fixedDeltaTime)
                {
                    if (m.Grapple.Attached) { attached = true; peak = Mathf.Max(peak, m.Vel.magnitude); }
                    minD = Mathf.Min(minD, Vector3.Distance(player.transform.position, hook));
                    yield return new WaitForFixedUpdate();
                }
                Line($"   real tree '{tb.name}' at {trunk}: hook {los.point}, dist {d0:0.0} -> closest {minD:0.0} m, peak {peak:0} u/s, detach={m.Grapple.LastDetach}, last={m.LastEvent}");
                Check("grapple attached to real tree", attached ? 1f : 0f, 1f, 0f);
                Check("grapple pulled to real tree", minD < d0 * 0.4f ? 1f : 0f, 1f, 0f);
                yield break;
            }
            Fail("real-tree grapple", "no tree with a clear shot");
        }

        // ----------------------------------------------------------------- helpers
        /// <summary>Deterministic test site: the first dry, flat Meadows point in a spiral around the start temple.</summary>
        private static Vector3 FindTestSite()
        {
            Vector3 c = ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out var temple) ? temple.m_position : Vector3.zero;
            var wg = WorldGenerator.instance;
            float water = ZoneSystem.instance.m_waterLevel;
            for (float r = 40f; r <= 400f; r += 10f)
                for (float a = 0f; a < 360f; a += 15f)
                {
                    Vector3 q = c + Quaternion.Euler(0f, a, 0f) * Vector3.forward * r;
                    if (wg.GetBiome(q.x, q.z) != Heightmap.Biome.Meadows) continue;
                    float h = wg.GetHeight(q.x, q.z);
                    if (h < water + 4f) continue;
                    bool flat = true;
                    for (float ox = -30f; ox <= 30f && flat; ox += 10f)
                        for (float oz = -30f; oz <= 30f && flat; oz += 10f)
                        {
                            float hh = wg.GetHeight(q.x + ox, q.z + oz);
                            if (Mathf.Abs(hh - h) > 3f || hh < water + 3f) flat = false;
                        }
                    if (flat) return new Vector3(q.x, h, q.z);
                }
            return c;
        }

        /// <summary>Horizontal direction with the longest unobstructed, walkable run (real terrain has trees and rocks).</summary>
        private static Vector3 OpenDir(Vector3 from, float dist, float radius, Transform ignore)
        {
            Vector3 bestDir = Vector3.forward; float bestScore = -1f;
            for (int i = 0; i < 24; i++)
            {
                Vector3 d = Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward;
                float free = dist;
                foreach (var h in Physics.SphereCastAll(from + Vector3.up * (radius + 1.5f), radius, d, dist, Character.s_blockedRayMask | Character.s_characterLayerMask, QueryTriggerInteraction.Ignore))
                    if (!h.collider.transform.IsChildOf(ignore) && h.collider.GetComponentInParent<Player>() == null && h.distance > 0f) free = Mathf.Min(free, h.distance);
                Vector3 end = from + d * free;
                if (ZoneSystem.instance.GetGroundHeight(end) < ZoneSystem.instance.m_waterLevel + 1f
                    || ZoneSystem.instance.GetGroundHeight(from + d * free * 0.5f) < ZoneSystem.instance.m_waterLevel + 1f) continue;   // stay out of the sea
                float climb = Mathf.Abs(ZoneSystem.instance.GetGroundHeight(end) - from.y) / Mathf.Max(1f, free);
                float score = free * (1f - Mathf.Clamp01(climb * 2f));
                if (score > bestScore) { bestScore = score; bestDir = d; }
            }
            return bestDir;
        }

        private static IEnumerator Teleport(Player p, Vector3 pos)
        {
            p.m_body.position = pos;
            p.transform.position = pos;
            p.m_body.linearVelocity = Vector3.zero;
            p.m_maxAirAltitude = pos.y;
            PilotController.Local?.Motor.ResetVelocity();
            yield return new WaitForSeconds(0.6f);
        }

        private static IEnumerator Settle(Player p, PilotMotor.InputState input)
        {
            input.Forward = 0f; input.Side = 0f; input.Sprint = false; input.Crouch = false;
            float tt = 0f;
            var m = PilotController.Local.Motor;
            while ((!m.OnGround || m.HorizontalSpeed > 1f) && tt < 4f) { tt += Time.deltaTime; yield return null; }
            yield return new WaitForSeconds(0.3f);
        }

        private static IEnumerator Measure(float total, float window, Func<float> sample, Action<float> done)
        {
            float sum = 0f; int n = 0;
            for (float tt = 0; tt < total; tt += Time.fixedDeltaTime)
            {
                if (tt >= total - window) { sum += sample(); n++; }
                yield return new WaitForFixedUpdate();
            }
            done(n > 0 ? sum / n : 0f);
        }

        private void Check(string name, float measured, float expected, float relTol)
        {
            bool ok = relTol <= 0f ? Mathf.Approximately(measured, expected) : Mathf.Abs(measured - expected) <= Mathf.Abs(expected) * relTol;
            if (ok) pass++; else fail++;
            Line($"{(ok ? "PASS" : "FAIL")}  {name}: measured {measured.ToString("0.###", CultureInfo.InvariantCulture)}  expected {expected.ToString("0.###", CultureInfo.InvariantCulture)}{(relTol > 0 ? $" ±{relTol * 100:0}%" : "")}");
        }

        private void Fail(string name, string why) { fail++; Line($"FAIL  {name}: {why}"); }

        private void Line(string s) { report.AppendLine(s); Plugin.Log.LogInfo("[selftest] " + s); }

        /// <summary>Remove creatures earlier (crashed) runs may have left in the throwaway test world.</summary>
        private static void ClearTestCreatures()
        {
            var me = Player.m_localPlayer;
            foreach (var c in new List<Character>(Character.GetAllCharacters()))
            {
                if (c == null || c.IsPlayer()) continue;
                if (c.gameObject.name.StartsWith("Troll") || c.gameObject.name.StartsWith("PilotHeim_"))
                    ZNetScene.instance.Destroy(c.gameObject);
            }
        }

        private void Finish()
        {
            ClearTestCreatures();
            if (finished) return;
            finished = true;
            Line($"RESULT: {pass} passed, {fail} failed");
            File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "PilotHeim_selftest.txt"), report.ToString());
            Application.Quit();
        }
    }
}
