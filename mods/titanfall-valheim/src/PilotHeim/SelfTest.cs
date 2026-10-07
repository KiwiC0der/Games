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

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            t = PilotController.Tuning;
            Line($"PilotHeim self-test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  tuning values={t.Report.Count}");
            yield return StartWorld();
            float wait = 0f;
            while ((Player.m_localPlayer == null || PilotController.Local == null) && wait < 120f) { wait += Time.deltaTime; yield return null; }
            if (Player.m_localPlayer == null) { Fail("spawn", "player never spawned"); Finish(); yield break; }
            yield return new WaitForSeconds(3f);

            var player = Player.m_localPlayer;
            var pc = PilotController.Local;
            var m = pc.Motor;
            player.SetGodMode(true);
            var input = new PilotMotor.InputState();
            m.Override = input;

            Vector3 origin = BuildArena(player.transform.position + new Vector3(0f, 250f, 0f));
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
            Vector3 aimAt = tree != null ? tree.transform.position + Vector3.up * 6f : origin + new Vector3(-12f, 6f, 15f);
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
            Check("grapple attached to tree", attached ? 1f : 0f, 1f, 0f);
            Check("grapple pulled toward tree", d1 < d0 - 2f ? 1f : 0f, 1f, 0f);
            Check("grapple speed <= ramp max (u/s)", maxAlong <= t.GrappleSpeedRampMax * 1.15f + t.Gravity * 0.2f ? 1f : 0f, 1f, 0f);
            Check("grapple used power", powerBefore - m.Grapple.Power > 1f ? 1f : 0f, 1f, 0f);
            Line($"   grapple: start dist {d0:0.0} m -> {d1:0.0} m, peak speed {maxAlong:0} u/s, power {powerBefore:0} -> {m.Grapple.Power:0}");

            // --- T8 no fall damage
            float hp = player.GetHealth();
            player.SetGodMode(false);
            yield return Teleport(player, origin + new Vector3(0f, 40f, 0f));
            float fallT = 0f;
            while (!m.OnGround && fallT < 8f) { fallT += Time.deltaTime; yield return null; }
            yield return new WaitForSeconds(0.5f);
            Check("no fall damage from 40 m", Mathf.Abs(player.GetHealth() - hp) < 0.01f ? 1f : 0f, 1f, 0f);
            player.SetGodMode(true);

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
            }
            Box("PilotHeim_Floor", c + new Vector3(0f, -0.5f, 0f), new Vector3(80f, 1f, 80f));
            Box("PilotHeim_Wall", c + new Vector3(6.5f, 6f, 0f), new Vector3(1f, 12f, 60f));
            Box("PilotHeim_Pillar", c + new Vector3(-12f, 10f, 15f), new Vector3(1.2f, 20f, 1.2f));
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Beech1") : null;
            if (prefab != null)
            {
                var tree = Instantiate(prefab, c + new Vector3(-12f, 0f, 15f), Quaternion.identity);
                tree.name = "PilotHeim_TestTree";
            }
            return c;
        }

        // ----------------------------------------------------------------- helpers
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

        private void Finish()
        {
            Line($"RESULT: {pass} passed, {fail} failed");
            File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "PilotHeim_selftest.txt"), report.ToString());
            Application.Quit();
        }
    }
}
