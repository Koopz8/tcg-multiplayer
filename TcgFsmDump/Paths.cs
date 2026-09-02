using System;
using System.IO;
using UnityEngine;

namespace TcgFsmDump
{
    internal static class Paths
    {
        private static string _dir;

        /// <summary>&lt;game root&gt;/TcgFsmDump — sits next to TheCoinGame.exe so it's easy to find.</summary>
        public static string OutputDir
        {
            get
            {
                if (_dir != null) return _dir;
                string root;
                try { root = Path.GetDirectoryName(Application.dataPath); }
                catch { root = Directory.GetCurrentDirectory(); }
                if (string.IsNullOrEmpty(root)) root = Directory.GetCurrentDirectory();
                _dir = Path.Combine(root, "TcgFsmDump");
                try { Directory.CreateDirectory(_dir); } catch { }
                return _dir;
            }
        }
    }
}
