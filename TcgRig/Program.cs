using System;
using System.Diagnostics;

namespace TcgRig
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            bool verbose = Array.IndexOf(args, "-v") >= 0;

            int seedArg = Array.IndexOf(args, "--seed");
            if (seedArg >= 0 && seedArg + 1 < args.Length)
                Rig.DefaultSeed = int.Parse(args[seedArg + 1]);

            Console.WriteLine();
            Console.WriteLine("  The Coin Game Multiplayer — two-peer rig");
            Console.WriteLine("  Real Session objects, fake Steam, controllable clock.");
            Console.WriteLine("  seed " + Rig.DefaultSeed);
            Console.WriteLine();

            int passed = 0, failed = 0;
            var sw = Stopwatch.StartNew();

            var all = new System.Collections.Generic.List<Func<Check>>();
            all.AddRange(Scenarios.All);
            all.AddRange(DiagnosisScenarios.All);

            foreach (var scenario in all)
            {
                TcgMultiplayer.Plugin.Echo = verbose;
                var c = scenario();

                if (c.Passed)
                {
                    passed++;
                    Console.WriteLine("  PASS  " + c.Name);
                    if (c.Detail != null) Console.WriteLine("        " + c.Detail);
                }
                else
                {
                    failed++;
                    Console.WriteLine("  FAIL  " + c.Name);
                    Console.WriteLine("        " + c.Error);
                }
            }

            sw.Stop();
            Console.WriteLine();
            Console.WriteLine("  " + passed + " passed, " + failed + " failed, in "
                              + sw.ElapsedMilliseconds + " ms");
            Console.WriteLine();
            Console.WriteLine("  What this does NOT cover:");
            Console.WriteLine("    * Steam actually delivering a byte between two machines.");
            Console.WriteLine("    * Two real world states — machines, wallets and unlocks");
            Console.WriteLine("      live in the Unity scene and are not loaded here.");
            Console.WriteLine("    * Physics replication fidelity, and how spectating looks.");
            Console.WriteLine("    * Anything about frame rate.");
            Console.WriteLine("  Those still need two people and two copies of the game.");
            Console.WriteLine();

            return failed == 0 ? 0 : 1;
        }
    }
}
