using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TcgFsmDump
{
    internal static class FsmDumper
    {
        public static string Dump(string label)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var safeLabel = Sanitize(string.IsNullOrEmpty(label) ? "scene" : label);
            var jsonPath = Path.Combine(Paths.OutputDir, "fsm_" + safeLabel + "_" + stamp + ".json");
            var summaryPath = Path.Combine(Paths.OutputDir, "fsm_" + safeLabel + "_" + stamp + ".summary.txt");

            var fsms = Collect();

            var eventNames = new Dictionary<string, int>();
            var stateNames = new Dictionary<string, int>();
            var varNames = new Dictionary<string, int>();
            var fsmNames = new Dictionary<string, int>();
            var pathHashes = new Dictionary<uint, string>();
            int collisions = 0, es2Present = 0, stateCount = 0, actionCount = 0, varCount = 0;

            using (var sw = new StreamWriter(jsonPath, false, new UTF8Encoding(false)))
            using (var j = new JsonWriter(sw))
            {
                j.StartObject();
                j.Prop("tool", "TcgFsmDump " + Plugin.Version);
                j.Prop("capturedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                j.Prop("unityVersion", Application.unityVersion);
                j.Prop("gameVersion", Application.version);
                j.Prop("activeScene", SceneManager.GetActiveScene().name);
                j.Prop("loadedSceneCount", SceneManager.sceneCount);

                j.StartArray("scenes");
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    j.StartObject();
                    j.Prop("name", s.name);
                    j.Prop("path", s.path);
                    j.Prop("buildIndex", s.buildIndex);
                    j.Prop("isLoaded", s.isLoaded);
                    j.Prop("rootCount", s.isLoaded ? s.rootCount : 0);
                    j.EndObject();
                }
                j.EndArray();

                j.Prop("fsmCount", fsms.Count);
                j.StartArray("fsms");

                foreach (var pm in fsms)
                {
                    GameObject go;
                    try { go = pm.gameObject; } catch { continue; }
                    if (go == null) continue;

                    var path = SceneId.Path(go.transform);
                    var hash = SceneId.Hash(path);
                    string existing;
                    if (pathHashes.TryGetValue(hash, out existing)) { if (existing != path) collisions++; }
                    else pathHashes[hash] = path;

                    int es2 = SceneId.Es2Id(go);
                    if (es2 >= 0) es2Present++;

                    j.StartObject();
                    j.Prop("path", path);
                    j.Prop("pathHash", hash);
                    j.Prop("scene", go.scene.IsValid() ? go.scene.name : "<none>");
                    j.Prop("go", go.name);
                    j.Prop("root", go.transform.root != null ? go.transform.root.name : go.name);
                    j.Prop("tag", SafeTag(go));
                    j.Prop("layer", LayerMask.LayerToName(go.layer));
                    j.Prop("activeInHierarchy", go.activeInHierarchy);

                    if (es2 >= 0) j.Prop("es2UniqueId", es2); else j.PropNull("es2UniqueId");

                    var p = go.transform.position;
                    j.StartObject("pos");
                    j.Prop("x", p.x); j.Prop("y", p.y); j.Prop("z", p.z);
                    j.EndObject();

                    // Network-relevance triage: what else is on this object?
                    WriteComponents(j, go);

                    string fsmName = "?";
                    try { fsmName = pm.FsmName; } catch { }
                    j.Prop("fsm", fsmName);
                    Bump(fsmNames, fsmName);

                    try { j.Prop("enabled", pm.enabled); } catch { }
                    try { j.Prop("active", pm.Active); } catch { }
                    try { j.Prop("activeState", pm.ActiveStateName); } catch { j.PropNull("activeState"); }
                    try { j.Prop("usesTemplate", pm.UsesTemplate); } catch { }

                    var fsm = SafeFsm(pm);

                    // ---- events the graph declares -------------------------------
                    j.StartArray("events");
                    try
                    {
                        var evts = pm.FsmEvents;
                        if (evts != null)
                        {
                            foreach (var e in evts)
                            {
                                if (e == null) continue;
                                j.StartObject();
                                j.Prop("name", e.Name);
                                j.Prop("system", e.IsSystemEvent);
                                j.EndObject();
                                if (!e.IsSystemEvent) Bump(eventNames, e.Name);
                            }
                        }
                    }
                    catch { }
                    j.EndArray();

                    // ---- global transitions --------------------------------------
                    j.StartArray("globalTransitions");
                    try
                    {
                        var gts = pm.FsmGlobalTransitions;
                        if (gts != null)
                            foreach (var t in gts) WriteTransition(j, t);
                    }
                    catch { }
                    j.EndArray();

                    // ---- states --------------------------------------------------
                    j.StartArray("states");
                    try
                    {
                        var states = pm.FsmStates;
                        if (states != null)
                        {
                            foreach (var st in states)
                            {
                                if (st == null) continue;
                                stateCount++;
                                Bump(stateNames, st.Name);

                                j.StartObject();
                                j.Prop("name", st.Name);
                                if (!string.IsNullOrEmpty(st.Description)) j.Prop("desc", st.Description);

                                j.StartArray("transitions");
                                try
                                {
                                    var trs = st.Transitions;
                                    if (trs != null)
                                        foreach (var t in trs) WriteTransition(j, t);
                                }
                                catch { }
                                j.EndArray();

                                if (Plugin.DumpActions)
                                {
                                    j.StartArray("actions");
                                    try
                                    {
                                        // Touching Actions forces PlayMaker's lazy action load.
                                        // A missing action type throws here, hence the guard.
                                        var acts = st.Actions;
                                        if (acts != null)
                                        {
                                            foreach (var a in acts)
                                            {
                                                if (a == null) { j.Value("<null>"); continue; }
                                                actionCount++;
                                                j.Value(a.GetType().Name);
                                            }
                                        }
                                    }
                                    catch (Exception ex) { j.Value("<load failed: " + ex.GetType().Name + ">"); }
                                    j.EndArray();
                                }

                                j.EndObject();
                            }
                        }
                    }
                    catch { }
                    j.EndArray();

                    // ---- variables -----------------------------------------------
                    j.StartArray("vars");
                    try
                    {
                        var vars = pm.FsmVariables;
                        if (vars != null)
                        {
                            var all = vars.GetAllNamedVariables();
                            if (all != null)
                            {
                                foreach (var v in all)
                                {
                                    if (v == null) continue;
                                    varCount++;
                                    Bump(varNames, v.Name);
                                    j.StartObject();
                                    j.Prop("name", v.Name);
                                    j.Prop("type", v.VariableType.ToString());
                                    if (Plugin.DumpVariableValues) j.Prop("value", SafeValue(v));
                                    j.EndObject();
                                }
                            }
                        }
                    }
                    catch { }
                    j.EndArray();

                    j.EndObject();
                }

                j.EndArray();
                j.EndObject();
            }

            WriteSummary(summaryPath, fsms.Count, stateCount, actionCount, varCount, es2Present,
                         collisions, fsmNames, eventNames, stateNames, varNames);

            return jsonPath;
        }

        // ------------------------------------------------------------------

        private static List<PlayMakerFSM> Collect()
        {
            var seen = new HashSet<int>();
            var result = new List<PlayMakerFSM>();

            try
            {
                foreach (var pm in PlayMakerFSM.FsmList)
                {
                    if (pm == null) continue;
                    if (seen.Add(pm.GetInstanceID())) result.Add(pm);
                }
            }
            catch (Exception ex) { Plugin.Warn("FsmList walk failed: " + ex.Message); }

            if (Plugin.IncludeInactive)
            {
                try
                {
                    foreach (var pm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                    {
                        if (pm == null) continue;
                        var go = pm.gameObject;
                        // Skip prefab assets and editor-only objects; keep real scene objects.
                        if (go == null || !go.scene.IsValid()) continue;
                        if (seen.Add(pm.GetInstanceID())) result.Add(pm);
                    }
                }
                catch (Exception ex) { Plugin.Warn("Inactive sweep failed: " + ex.Message); }
            }

            return result;
        }

        private static void WriteTransition(JsonWriter j, FsmTransition t)
        {
            if (t == null) return;
            j.StartObject();
            try { j.Prop("event", t.EventName); } catch { j.PropNull("event"); }
            try { j.Prop("to", t.ToState); } catch { j.PropNull("to"); }
            j.EndObject();
        }

        private static void WriteComponents(JsonWriter j, GameObject go)
        {
            j.StartArray("components");
            bool rb = false, col = false, anim = false, audio = false, rend = false;
            try
            {
                var comps = go.GetComponents<Component>();
                int written = 0;
                foreach (var c in comps)
                {
                    if (c == null) { continue; }
                    var n = c.GetType().Name;
                    if (n == "PlayMakerFSM") continue;
                    if (c is Rigidbody) rb = true;
                    else if (c is Collider) col = true;
                    else if (c is Animator || c is Animation) anim = true;
                    else if (n == "AudioSource") audio = true;   // by name: avoids a reference to UnityEngine.AudioModule
                    else if (c is Renderer) rend = true;
                    if (written++ < 24) j.Value(n);
                }
            }
            catch { }
            j.EndArray();

            j.StartObject("has");
            j.Prop("rigidbody", rb);
            j.Prop("collider", col);
            j.Prop("animator", anim);
            j.Prop("audio", audio);
            j.Prop("renderer", rend);
            j.EndObject();
        }

        private static Fsm SafeFsm(PlayMakerFSM pm)
        {
            try { return pm.Fsm; } catch { return null; }
        }

        private static string SafeTag(GameObject go)
        {
            try { return go.tag; } catch { return "<untagged>"; }
        }

        private static string SafeValue(NamedVariable v)
        {
            try
            {
                var raw = v.RawValue;
                if (raw == null) return null;
                var uo = raw as UnityEngine.Object;
                if (uo != null) return uo.name + " (" + raw.GetType().Name + ")";
                var s = raw.ToString();
                return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
            }
            catch { return "<unreadable>"; }
        }

        private static void Bump(Dictionary<string, int> d, string k)
        {
            if (string.IsNullOrEmpty(k)) return;
            int n;
            d[k] = d.TryGetValue(k, out n) ? n + 1 : 1;
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static void WriteSummary(string path, int fsmCount, int states, int actions, int vars,
                                         int es2Present, int collisions,
                                         Dictionary<string, int> fsmNames,
                                         Dictionary<string, int> events,
                                         Dictionary<string, int> stateNames,
                                         Dictionary<string, int> varNames)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.WriteLine("TcgFsmDump " + Plugin.Version + "  —  " + DateTime.Now.ToString("u", CultureInfo.InvariantCulture));
                w.WriteLine("Unity " + Application.unityVersion + "   scene: " + SceneManager.GetActiveScene().name);
                w.WriteLine();
                w.WriteLine("FSMs .................. " + fsmCount);
                w.WriteLine("States ................ " + states);
                w.WriteLine("Actions ............... " + (Plugin.DumpActions ? actions.ToString() : "(not dumped)"));
                w.WriteLine("Variables ............. " + vars);
                w.WriteLine("Objects w/ ES2UniqueID  " + es2Present + " / " + fsmCount);
                w.WriteLine("Path-hash collisions .. " + collisions + (collisions > 0 ? "   <-- NetId scheme needs 64-bit" : "   (32-bit path hash is safe here)"));
                w.WriteLine();
                Top(w, "FSM names", fsmNames, 60);
                Top(w, "Non-system events", events, 200);
                Top(w, "State names", stateNames, 80);
                Top(w, "Variable names", varNames, 120);
            }
        }

        private static void Top(TextWriter w, string title, Dictionary<string, int> d, int take)
        {
            var list = new List<KeyValuePair<string, int>>(d);
            list.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            w.WriteLine("== " + title + " (" + d.Count + " distinct) ==");
            for (int i = 0; i < list.Count && i < take; i++)
                w.WriteLine("  " + list[i].Value.ToString().PadLeft(6) + "  " + list[i].Key);
            if (list.Count > take) w.WriteLine("  … " + (list.Count - take) + " more (see JSON)");
            w.WriteLine();
        }
    }
}
