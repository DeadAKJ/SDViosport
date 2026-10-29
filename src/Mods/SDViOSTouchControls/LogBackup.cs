using System;
using System.IO;
using StardewModdingAPI;

namespace SDViOSTouchControls
{
    /// <summary>
    /// Utility for creating emergency and day-transition backup snapshots of game and SMAPI logs
    /// without opening any network ports or running any servers.
    /// </summary>
    public static class LogBackup
    {
        private static readonly object _backupLock = new();
        private static DateTime _lastBackupUtc = DateTime.MinValue;
        private static string _baseDir = "";

        static LogBackup()
        {
            try
            {
                _baseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(_baseDir) || !Directory.Exists(_baseDir))
                {
                    _baseDir = AppContext.BaseDirectory;
                }
            }
            catch
            {
                _baseDir = AppContext.BaseDirectory;
            }
        }

        public static void BackupLogs(string reason, int minIntervalMs = 2000)
        {
            lock (_backupLock)
            {
                var now = DateTime.UtcNow;
                if (minIntervalMs > 0 && (now - _lastBackupUtc).TotalMilliseconds < minIntervalMs)
                {
                    return;
                }
                _lastBackupUtc = now;

                try
                {
                    string latest = FindLogFile("SMAPI-latest.txt");
                    if (File.Exists(latest) && new FileInfo(latest).Length > 0)
                    {
                        string dir = Path.GetDirectoryName(latest)!;
                        string dest = Path.Combine(dir, "SMAPI-previous.txt");
                        using (var src = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        {
                            src.CopyTo(dst);
                        }
                        ModEntry.ModMonitor?.Log($"[LogBackup] Preserved log snapshot ({reason}) to SMAPI-previous.txt", LogLevel.Trace);
                    }

                    string engine = FindLogFile("engine-latest.log");
                    if (File.Exists(engine) && new FileInfo(engine).Length > 0)
                    {
                        string dir = Path.GetDirectoryName(engine)!;
                        string dest = Path.Combine(dir, "engine-previous.log");
                        using (var src = new FileStream(engine, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                        {
                            src.CopyTo(dst);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.ModMonitor?.Log($"[LogBackup] Note backing up log: {ex.Message}", LogLevel.Trace);
                }
            }
        }

        private static string FindLogFile(string fileName)
        {
            var candidates = new[]
            {
                Path.Combine(_baseDir, "ErrorLogs", fileName),
                Path.Combine(_baseDir, fileName),
                Path.Combine(AppContext.BaseDirectory, "ErrorLogs", fileName),
                Path.Combine(AppContext.BaseDirectory, fileName)
            };

            foreach (var cand in candidates)
            {
                if (File.Exists(cand)) return cand;
            }

            return candidates[0];
        }
    }
}
