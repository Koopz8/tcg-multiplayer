using System;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace TcgMultiplayer.Game
{
    /// <summary>
    /// Candidate identity scheme for the eventual network object registry.
    ///
    /// The game already has ES2UniqueID, but its id is handed out in Awake order
    /// (uniqueIDList[last].id + 1), not baked into the scene — so it is NOT
    /// guaranteed to agree across two machines. Hierarchy path is. We record
    /// both so we can diff two dumps and find out empirically.
    /// </summary>
    internal static class NetId
    {
        private static Type _es2Type;
        private static FieldInfo _es2IdField;
        private static bool _es2Probed;

        /// <summary>Full path from scene root, e.g. "Island/Arcade/SkeeBall_01/Body".</summary>
        public static string Path(Transform t)
        {
            if (t == null) return "";
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null)
            {
                sb.Insert(0, '/').Insert(0, p.name);
                p = p.parent;
            }
            return sb.ToString();
        }

        /// <summary>FNV-1a 32. Deterministic, stable across processes and runs.</summary>
        public static uint Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
                return h;
            }
        }

        /// <summary>ES2UniqueID.id via reflection, or -1. No hard reference to Assembly-CSharp.</summary>
        public static int Es2Id(GameObject go)
        {
            if (!_es2Probed)
            {
                _es2Probed = true;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("ES2UniqueID", false);
                        if (t == null) continue;
                        _es2Type = t;
                        _es2IdField = t.GetField("id", BindingFlags.Public | BindingFlags.Instance);
                        break;
                    }
                }
                catch { /* absent is fine */ }
            }

            if (_es2Type == null || _es2IdField == null) return -1;
            try
            {
                var c = go.GetComponent(_es2Type);
                if (c == null) return -1;
                return (int)_es2IdField.GetValue(c);
            }
            catch { return -1; }
        }
    }
}
