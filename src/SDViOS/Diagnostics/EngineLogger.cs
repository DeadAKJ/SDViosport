using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SDViOS.Diagnostics
{
    public static class EngineLogger
    {
        private static readonly object _lock = new object();
        private static StreamWriter? _fileWriter;
        private static TextWriter? _originalOut;
        private static TextWriter? _originalError;
        public static string LogFilePath { get; private set; } = string.Empty;

        public static void Initialize(string logsDirectory)
        {
            try
            {
                Directory.CreateDirectory(logsDirectory);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                LogFilePath = Path.Combine(logsDirectory, "engine-latest.log");
                string sessionLogPath = Path.Combine(logsDirectory, $"engine-{timestamp}.log");

                // Open with shared read access so iOS Files app can inspect while running
                var fileStream = new FileStream(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _fileWriter = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };

                _originalOut = Console.Out;
                _originalError = Console.Error;

                Console.SetOut(new DualWriter(_originalOut, _fileWriter, "[STDOUT] "));
                Console.SetError(new DualWriter(_originalError, _fileWriter, "[STDERR] "));

                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    LogFatal("Unhandled Exception", e.ExceptionObject as Exception);
                };

                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    LogFatal("Unobserved Task Exception", e.Exception);
                };

                Log("=== Stardew Valley iOS Engine Session Started ===");
                Log($"Timestamp: {DateTime.Now:O}");
                Log($"OS: {Environment.OSVersion}");
                Log($"Runtime: .NET {Environment.Version}");
                Log($"Working Directory: {Directory.GetCurrentDirectory()}");
                Log($"Log File: {LogFilePath}");
                Log("==================================================");
            }
            catch (Exception ex)
            {
                _originalError?.WriteLine($"[EngineLogger] Failed to initialize file logger: {ex}");
            }
        }

        public static void Log(string message, string tag = "INFO")
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{tag}] {message}";
            lock (_lock)
            {
                _originalOut?.WriteLine(line);
                _fileWriter?.WriteLine(line);
            }
        }

        public static void LogWarning(string message) => Log(message, "WARN");
        public static void LogError(string message) => Log(message, "ERROR");

        public static void LogFatal(string context, Exception? ex)
        {
            string msg = $"FATAL CRASH in {context}: {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}";
            if (ex?.InnerException != null)
            {
                msg += $"\nInner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
            }
            Log(msg, "CRASH");
            _fileWriter?.Flush();
        }

        private class DualWriter : TextWriter
        {
            private readonly TextWriter _console;
            private readonly StreamWriter _file;
            private readonly string _prefix;

            public DualWriter(TextWriter console, StreamWriter file, string prefix)
            {
                _console = console;
                _file = file;
                _prefix = prefix;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void WriteLine(string? value)
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string line = $"[{timestamp}] {_prefix}{value}";
                lock (_lock)
                {
                    _console.WriteLine(line);
                    _file.WriteLine(line);
                }
            }

            public override void Write(string? value)
            {
                lock (_lock)
                {
                    _console.Write(value);
                    _file.Write(value);
                }
            }
        }
    }
}
