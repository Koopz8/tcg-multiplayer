using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Builds a remote player body by cloning "PLAYER/LARRY Mesh" and stripping
    /// everything that would make the clone behave like a player.
    ///
    /// Stripping is the whole job. The rig carries PlayMaker graphs, IK
    /// controllers, colliders, rigidbodies, audio and hand-held props — every one
    /// of which would either drive the clone independently or shove the real
    /// player around. What we keep is the skeleton, the renderers and the Animator.
    /// </summary>
    internal static class AvatarFactory
    {
        private static bool _loggedAnimatorParams;

        public static GameObject Build(Transform sourceMesh, string label)
        {
            if (sourceMesh == null) return null;

            GameObject clone;
            try { clone = UnityEngine.Object.Instantiate(sourceMesh.gameObject); }
            catch (Exception ex) { Plugin.Warn("Avatar clone failed: " + ex.Message); return null; }

            clone.name = "TCGMP_Avatar_" + label;
            clone.transform.SetParent(null, true);
            UnityEngine.Object.DontDestroyOnLoad(clone);

            var stripped = Strip(clone);
            var animator = clone.GetComponentInChildren<Animator>();

            if (animator != null && !_loggedAnimatorParams)
            {
                _loggedAnimatorParams = true;
                LogAnimatorParameters(animator);
            }

            Plugin.Log("Built avatar for " + label + " (stripped " + stripped + " components"
                       + (animator != null ? ", animator present)" : ", NO animator)"));
            return clone;
        }

        private static int Strip(GameObject root)
        {
            int removed = 0;

            // Anything that runs logic, collides, makes noise or lights the scene.
            // Renderers, the Animator and the Transform hierarchy stay.
            var comps = root.GetComponentsInChildren<Component>(true);
            foreach (var c in comps)
            {
                if (c == null) continue;
                if (c is Transform) continue;
                if (c is Renderer) continue;
                if (c is MeshFilter) continue;
                if (c is Animator) continue;

                var name = c.GetType().Name;
                bool kill =
                    c is Collider ||
                    c is Rigidbody ||
                    c is Joint ||
                    c is Light ||
                    name == "AudioSource" ||
                    name == "AudioListener" ||
                    name == "Camera" ||
                    name == "PlayMakerFSM" ||
                    name.StartsWith("PlayMaker", StringComparison.Ordinal) ||
                    name.StartsWith("IKControl", StringComparison.Ordinal) ||
                    name.StartsWith("Enviro", StringComparison.Ordinal) ||
                    name == "VolumetricLightBeam" ||
                    name == "NavMeshObstacle" ||
                    name == "NavMeshAgent" ||
                    name == "DragRigidbody" ||
                    name == "ES2UniqueID";

                if (!kill) continue;

                try { UnityEngine.Object.DestroyImmediate(c); removed++; }
                catch { /* some components refuse; harmless */ }
            }

            return removed;
        }

        /// <summary>
        /// We don't yet know what the Larry animator's parameters are called, and
        /// guessing would produce a T-posing ghost with no explanation. So log them
        /// once and map by what's actually there.
        /// </summary>
        private static void LogAnimatorParameters(Animator a)
        {
            try
            {
                var sb = new StringBuilder("Animator parameters on the player rig: ");
                var ps = a.parameters;
                if (ps == null || ps.Length == 0) { sb.Append("(none)"); }
                else
                {
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(ps[i].name).Append(':').Append(ps[i].type);
                    }
                }
                Plugin.Log(sb.ToString());
            }
            catch (Exception ex) { Plugin.Warn("Could not read animator parameters: " + ex.Message); }
        }

        /// <summary>
        /// Picks the animator parameters that look like "how fast am I moving" and
        /// "am I on the ground", by name, from whatever the rig actually has.
        /// </summary>
        public static AnimatorBinding BindAnimator(Animator a)
        {
            var b = new AnimatorBinding();
            if (a == null) return b;

            try
            {
                foreach (var p in a.parameters)
                {
                    var n = p.name.ToLowerInvariant();
                    if (p.type == AnimatorControllerParameterType.Float && b.SpeedParam == null &&
                        (n.Contains("speed") || n.Contains("forward") || n.Contains("velocity") || n.Contains("move")))
                        b.SpeedParam = p.name;

                    if (p.type == AnimatorControllerParameterType.Bool && b.GroundedParam == null &&
                        (n.Contains("ground") || n.Contains("air")))
                        b.GroundedParam = p.name;

                    if (p.type == AnimatorControllerParameterType.Bool && b.RunParam == null &&
                        (n.Contains("run") || n.Contains("sprint")))
                        b.RunParam = p.name;

                    if (p.type == AnimatorControllerParameterType.Bool && b.WalkParam == null &&
                        n.Contains("walk"))
                        b.WalkParam = p.name;
                }
            }
            catch { }

            return b;
        }
    }

    internal sealed class AnimatorBinding
    {
        public string SpeedParam;
        public string GroundedParam;
        public string RunParam;
        public string WalkParam;

        public bool Any { get { return SpeedParam != null || WalkParam != null || RunParam != null; } }

        public override string ToString()
        {
            var parts = new List<string>();
            if (SpeedParam != null) parts.Add("speed=" + SpeedParam);
            if (WalkParam != null) parts.Add("walk=" + WalkParam);
            if (RunParam != null) parts.Add("run=" + RunParam);
            if (GroundedParam != null) parts.Add("grounded=" + GroundedParam);
            return parts.Count == 0 ? "no usable parameters" : string.Join(", ", parts.ToArray());
        }
    }
}
