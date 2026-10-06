using System;
using System.Runtime.InteropServices;
using System.Threading;
using StardewModdingAPI;
using StardewValley;

namespace SDViOSTouchControls
{
    /// <summary>
    /// Background watchdog that samples the real iOS memory headroom (os_proc_available_memory = bytes left
    /// before Jetsam kills us) and forces a compacting GC when headroom gets low. Runs on its own thread so
    /// it keeps working during long synchronous loads (e.g. SMAPI's Load_Locations task) where no
    /// UpdateTicking events fire.
    /// </summary>
    internal static class MemoryWatchdog
    {
        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern UIntPtr os_proc_available_memory();

        private const long MB = 1024 * 1024;
        private const long LowWaterBytes = 350 * MB;   // force GC below this
        private const long CriticalBytes = 200 * MB;   // log every sample below this

        private static Thread? _thread;
        private static IMonitor? _monitor;
        private static bool _nativeAvailable = true;
        private static long _minSeen = long.MaxValue;

        public static void Start(IMonitor monitor)
        {
            if (_thread != null) return;
            _monitor = monitor;
            _thread = new Thread(Loop) { IsBackground = true, Name = "SDViOS-MemWatchdog", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            monitor.Log($"[MEM_DEBUG] Watchdog started. Available now: {Available() / MB} MB", LogLevel.Info);
        }

        /// <summary>Bytes left before Jetsam, or -1 if unavailable.</summary>
        public static long Available()
        {
            if (!_nativeAvailable) return -1;
            try { return (long)os_proc_available_memory().ToUInt64(); }
            catch { _nativeAvailable = false; return -1; }
        }

        private static void Loop()
        {
            DateTime lastLog = DateTime.MinValue;
            DateTime lastGc = DateTime.MinValue;

            while (true)
            {
                try
                {
                    bool loading = Game1.gameMode == 6 || !Context.IsWorldReady;
                    long avail = Available();
                    long managed = GC.GetTotalMemory(false);
                    var now = DateTime.UtcNow;

                    if (avail >= 0 && avail < _minSeen) _minSeen = avail;

                    double gcInterval = loading ? 750 : 5000;
                    if (avail >= 0 && avail < LowWaterBytes && (now - lastGc).TotalMilliseconds > gcInterval)
                    {
                        lastGc = DateTime.UtcNow;
                        _monitor?.Log($"[MEM_DEBUG] LOW MEMORY: avail={avail / MB} MB, managed={managed / MB} MB -> starting GC", LogLevel.Warn);
                        try { System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce; }
                        catch (Exception sex) { LogErr("GCSettings.LOHCompactionMode", sex); }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try { GC.Collect(2, GCCollectionMode.Forced, true, true); }
                        catch (Exception gex) { LogErr("GC.Collect(compacting)", gex); try { GC.Collect(); } catch (Exception g2) { LogErr("GC.Collect()", g2); } }
                        long after = Available();
                        _monitor?.Log($"[MEM_DEBUG] LOW MEMORY GC done in {sw.ElapsedMilliseconds} ms: avail {avail / MB} -> {after / MB} MB, managed {managed / MB} -> {GC.GetTotalMemory(false) / MB} MB", LogLevel.Warn);
                        lastLog = DateTime.UtcNow;
                    }
                    else
                    {
                        double logEvery = (avail >= 0 && avail < CriticalBytes) ? 0.25 : loading ? 2 : 15;
                        if ((now - lastLog).TotalSeconds >= logEvery)
                        {
                            _monitor?.Log($"[MEM_DEBUG] avail={avail / MB} MB (min {_minSeen / MB} MB), managed={managed / MB} MB, gameMode={Game1.gameMode}, worldReady={Context.IsWorldReady}", LogLevel.Info);
                            lastLog = now;
                        }
                    }

                    Thread.Sleep(loading ? 200 : 1000);
                }
                catch (Exception ex)
                {
                    LogErr("loop", ex);
                    Thread.Sleep(1000);
                }
            }
        }

        private static int _errCount;
        private static void LogErr(string where, Exception ex)
        {
            if (_errCount++ < 10)
                _monitor?.Log($"[MEM_DEBUG] Watchdog error in {where}: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
        }
    }
}
