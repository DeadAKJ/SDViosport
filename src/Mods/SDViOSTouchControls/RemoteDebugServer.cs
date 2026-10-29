using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using StardewModdingAPI;
using StardewValley;

namespace SDViOSTouchControls
{
    /// <summary>
    /// Lightweight embedded HTTP server that provides wireless log viewing, memory inspection,
    /// and file/mod management over the local Wi-Fi network without requiring a USB cable or iTunes.
    /// </summary>
    public static class RemoteDebugServer
    {
        private static TcpListener? _listener;
        private static Thread? _thread;
        private static IMonitor? _monitor;
        private static string _baseDir = "";
        private static readonly object _backupLock = new();
        private static DateTime _lastBackupUtc = DateTime.MinValue;
        private const int Port = 8080;

        public static void Start(IMonitor monitor)
        {
            if (_thread != null) return;
            _monitor = monitor;

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

            _thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "SDViOS-RemoteDebugServer"
            };
            _thread.Start();

            // Log reachable IP addresses for user convenience
            LogServerAddresses();
        }

        private static void LogServerAddresses()
        {
            try
            {
                _monitor?.Log($"[RemoteServer] Starting embedded Wi-Fi debug server on port {Port}...", LogLevel.Info);
                _monitor?.Log($"[RemoteServer] Base Documents path: {_baseDir}", LogLevel.Info);

                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ip = addr.Address.ToString();
                            if (!ip.StartsWith("127."))
                            {
                                _monitor?.Log($"[RemoteServer] Available at: http://{ip}:{Port}/", LogLevel.Info);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[RemoteServer] Note logging network interfaces: {ex.Message}", LogLevel.Trace);
            }
        }

        private static void ListenLoop()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start(10);
                _monitor?.Log($"[RemoteServer] HTTP listener active on port {Port}.", LogLevel.Info);

                while (true)
                {
                    try
                    {
                        var client = _listener.AcceptTcpClient();
                        ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                    }
                    catch (Exception ex)
                    {
                        _monitor?.Log($"[RemoteServer] Accept error: {ex.Message}", LogLevel.Trace);
                        Thread.Sleep(250);
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[RemoteServer] Failed to bind port {Port}: {ex.Message}", LogLevel.Warn);
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;
                    using var stream = client.GetStream();

                    // Read HTTP request header
                    byte[] buffer = new byte[8192];
                    int totalRead = 0;
                    int headerEnd = -1;

                    while (totalRead < buffer.Length)
                    {
                        int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                        if (read <= 0) break;
                        totalRead += read;

                        // Check for \r\n\r\n or \n\n
                        headerEnd = FindHeaderEnd(buffer, totalRead);
                        if (headerEnd != -1) break;
                    }

                    if (headerEnd == -1) return;

                    string headerText = Encoding.UTF8.GetString(buffer, 0, headerEnd);
                    var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) return;

                    string requestLine = lines[0];
                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;

                    string method = parts[0].ToUpperInvariant();
                    string rawUrl = parts[1];

                    // Extract path and query
                    string path = rawUrl;
                    string query = "";
                    int qIdx = rawUrl.IndexOf('?');
                    if (qIdx != -1)
                    {
                        path = rawUrl.Substring(0, qIdx);
                        query = rawUrl.Substring(qIdx + 1);
                    }

                    var queryParams = ParseQuery(query);
                    var headers = ParseHeaders(lines);

                    // Route request
                    if (method == "GET")
                    {
                        HandleGet(stream, path, queryParams);
                    }
                    else if (method == "POST")
                    {
                        // Check content length
                        long contentLength = 0;
                        if (headers.TryGetValue("content-length", out var clStr))
                        {
                            long.TryParse(clStr, out contentLength);
                        }

                        // Payload start
                        int bodyAlreadyRead = totalRead - headerEnd;
                        HandlePost(stream, path, queryParams, headers, buffer, headerEnd, bodyAlreadyRead, contentLength);
                    }
                    else
                    {
                        SendResponse(stream, 405, "Method Not Allowed", "text/plain", Encoding.UTF8.GetBytes("Method Not Allowed"));
                    }
                }
                catch (Exception ex)
                {
                    _monitor?.Log($"[RemoteServer] Request handling exception: {ex.Message}", LogLevel.Trace);
                }
            }
        }

        private static int FindHeaderEnd(byte[] buf, int len)
        {
            for (int i = 0; i < len - 3; i++)
            {
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                    return i + 4;
            }
            for (int i = 0; i < len - 1; i++)
            {
                if (buf[i] == '\n' && buf[i + 1] == '\n')
                    return i + 2;
            }
            return -1;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;

            var pairs = query.Split('&');
            foreach (var pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq != -1)
                {
                    string k = WebUtility.UrlDecode(pair.Substring(0, eq));
                    string v = WebUtility.UrlDecode(pair.Substring(eq + 1));
                    dict[k] = v;
                }
                else
                {
                    dict[WebUtility.UrlDecode(pair)] = "";
                }
            }
            return dict;
        }

        private static Dictionary<string, string> ParseHeaders(string[] lines)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon != -1)
                {
                    string k = lines[i].Substring(0, colon).Trim();
                    string v = lines[i].Substring(colon + 1).Trim();
                    dict[k] = v;
                }
            }
            return dict;
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
                        _monitor?.Log($"[RemoteServer] Preserved log snapshot ({reason}) to SMAPI-previous.txt", LogLevel.Trace);
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
                    _monitor?.Log($"[RemoteServer] Note backing up log: {ex.Message}", LogLevel.Trace);
                }
            }
        }

        private static string EscapeJson(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void HandleGet(Stream stream, string path, Dictionary<string, string> q)
        {
            if (path == "/" || path == "/index.html")
            {
                string html = BuildDashboardHtml();
                SendResponse(stream, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            }
            else if (path == "/status")
            {
                long availMb = MemoryWatchdog.Available() / (1024 * 1024);
                long minMb = MemoryWatchdog.MinSeenMB == long.MaxValue ? availMb : MemoryWatchdog.MinSeenMB;
                long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
                string json = "{" +
                    $"\"avail_mb\": {availMb}, " +
                    $"\"min_avail_mb\": {minMb}, " +
                    $"\"managed_mb\": {managedMb}, " +
                    $"\"game_mode\": {ModEntry.CachedGameMode}, " +
                    $"\"world_ready\": {(ModEntry.CachedWorldReady ? "true" : "false")}, " +
                    $"\"time\": \"{EscapeJson(ModEntry.CachedTimeString)}\", " +
                    $"\"location\": \"{EscapeJson(ModEntry.CachedLocationName)}\", " +
                    $"\"active_menu\": \"{EscapeJson(ModEntry.CachedActiveMenuName)}\", " +
                    $"\"base_dir\": \"{EscapeJson(_baseDir)}\"" +
                    "}";
                SendResponse(stream, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
            }
            else if (path == "/api/list")
            {
                string relDir = q.GetValueOrDefault("dir", "");
                try
                {
                    string fullDir = ResolveSafePath(relDir);
                    if (!Directory.Exists(fullDir))
                    {
                        SendResponse(stream, 404, "Not Found", "application/json", Encoding.UTF8.GetBytes("{\"error\": \"Directory not found\"}"));
                        return;
                    }

                    var sb = new StringBuilder();
                    sb.Append("{\"dir\": \"").Append(EscapeJson(relDir)).Append("\", \"entries\": [");
                    bool first = true;
                    var dirInfo = new DirectoryInfo(fullDir);
                    foreach (var d in dirInfo.GetDirectories())
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        long mtime = new DateTimeOffset(d.LastWriteTimeUtc).ToUnixTimeSeconds();
                        sb.Append($"{{\"name\": \"{EscapeJson(d.Name)}\", \"is_dir\": true, \"size\": 0, \"mtime\": {mtime}}}");
                    }
                    foreach (var f in dirInfo.GetFiles())
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        long mtime = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds();
                        sb.Append($"{{\"name\": \"{EscapeJson(f.Name)}\", \"is_dir\": false, \"size\": {f.Length}, \"mtime\": {mtime}}}");
                    }
                    sb.Append("]}");
                    SendResponse(stream, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(sb.ToString()));
                }
                catch (Exception ex)
                {
                    SendResponse(stream, 400, "Bad Request", "application/json", Encoding.UTF8.GetBytes($"{{\"error\": \"{EscapeJson(ex.Message)}\"}}"));
                }
            }
            else if (path == "/log")
            {
                string logPath = FindLogFile("SMAPI-latest.txt");
                SendFileStream(stream, logPath, "text/plain; charset=utf-8", "SMAPI-latest.txt");
            }
            else if (path == "/log-previous" || path == "/previous-log")
            {
                string logPath = FindLogFile("SMAPI-previous.txt");
                if (!File.Exists(logPath))
                {
                    logPath = FindLogFile("engine-previous.log");
                }
                SendFileStream(stream, logPath, "text/plain; charset=utf-8", Path.GetFileName(logPath));
            }
            else if (path == "/engine-log")
            {
                string logPath = FindLogFile("engine-latest.log");
                SendFileStream(stream, logPath, "text/plain; charset=utf-8", "engine-latest.log");
            }
            else if (path == "/engine-previous-log")
            {
                string logPath = FindLogFile("engine-previous.log");
                SendFileStream(stream, logPath, "text/plain; charset=utf-8", "engine-previous.log");
            }
            else if (path == "/tail")
            {
                int linesCount = 100;
                if (q.TryGetValue("lines", out var lStr) && int.TryParse(lStr, out var n)) linesCount = Math.Max(1, Math.Min(n, 2000));
                string logPath = FindLogFile("SMAPI-latest.txt");
                string tailText = ReadLastLines(logPath, linesCount);
                SendResponse(stream, 200, "OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(tailText));
            }
            else if (path == "/tail-previous")
            {
                int linesCount = 100;
                if (q.TryGetValue("lines", out var lStr) && int.TryParse(lStr, out var n)) linesCount = Math.Max(1, Math.Min(n, 2000));
                string logPath = FindLogFile("SMAPI-previous.txt");
                if (!File.Exists(logPath))
                {
                    logPath = FindLogFile("engine-previous.log");
                }
                string tailText = ReadLastLines(logPath, linesCount);
                SendResponse(stream, 200, "OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(tailText));
            }
            else if (path == "/backup")
            {
                BackupLogs("ManualTrigger", minIntervalMs: 0);
                SendResponse(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes("{\"success\": true, \"message\": \"Logs backed up.\"}"));
            }
            else if (path == "/mem" || path == "/gc")
            {
                bool forceGc = path == "/gc" || q.ContainsKey("gc");
                long beforeAvail = MemoryWatchdog.Available() / (1024 * 1024);
                long beforeManaged = GC.GetTotalMemory(false) / (1024 * 1024);

                if (forceGc)
                {
                    MemoryWatchdog.ForceCollect("RemoteDebugServer", minIntervalMs: 500);
                }

                long afterAvail = MemoryWatchdog.Available() / (1024 * 1024);
                long afterManaged = GC.GetTotalMemory(false) / (1024 * 1024);

                string json = $"{{\"forced_gc\": {forceGc.ToString().ToLower()}, \"avail_mb_before\": {beforeAvail}, \"avail_mb_after\": {afterAvail}, \"managed_mb_before\": {beforeManaged}, \"managed_mb_after\": {afterManaged}}}";
                SendResponse(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes(json));
            }
            else if (path == "/api/quicksave" || path == "/quicksave")
            {
                bool success = false;
                string msg = "";
                try
                {
                    if (QuickSaveManager.Instance != null)
                    {
                        success = QuickSaveManager.Instance.QuickSave();
                        msg = success ? "Quick save initiated successfully" : "Quick save rejected (check world/player state)";
                    }
                    else
                    {
                        msg = "QuickSaveManager not initialized";
                    }
                }
                catch (Exception ex)
                {
                    msg = ex.Message;
                }

                string json = $"{{\"success\": {(success ? "true" : "false")}, \"message\": \"{msg}\"}}";
                SendResponse(stream, success ? 200 : 400, success ? "OK" : "Bad Request", "application/json", Encoding.UTF8.GetBytes(json));
            }
            else if (path == "/api/quickload" || path == "/quickload")
            {
                bool success = false;
                string msg = "";
                try
                {
                    if (QuickSaveManager.Instance != null)
                    {
                        success = QuickSaveManager.Instance.QuickLoad();
                        msg = success ? "Quick load initiated successfully" : "Quick load rejected (no save state found or busy)";
                    }
                    else
                    {
                        msg = "QuickSaveManager not initialized";
                    }
                }
                catch (Exception ex)
                {
                    msg = ex.Message;
                }

                string json = $"{{\"success\": {(success ? "true" : "false")}, \"message\": \"{msg}\"}}";
                SendResponse(stream, success ? 200 : 400, success ? "OK" : "Bad Request", "application/json", Encoding.UTF8.GetBytes(json));
            }
            else if (path == "/api/savestate")
            {
                var state = QuickSaveManager.Instance?.GetSavedState();
                string json = state != null
                    ? System.Text.Json.JsonSerializer.Serialize(state)
                    : "{\"hasState\": false}";
                SendResponse(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes(json));
            }
            else if (path == "/files")
            {
                string relDir = q.GetValueOrDefault("dir", "");
                try
                {
                    string fullDir = ResolveSafePath(relDir);
                    if (!Directory.Exists(fullDir))
                    {
                        SendResponse(stream, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes($"Directory not found: {relDir}"));
                        return;
                    }

                    string html = BuildDirectoryListingHtml(relDir, fullDir);
                    SendResponse(stream, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
                }
                catch (Exception ex)
                {
                    SendResponse(stream, 400, "Bad Request", "text/plain", Encoding.UTF8.GetBytes(ex.Message));
                }
            }
            else if (path == "/download")
            {
                string relFile = q.GetValueOrDefault("path", "");
                try
                {
                    string fullFile = ResolveSafePath(relFile);
                    if (!File.Exists(fullFile))
                    {
                        SendResponse(stream, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes($"File not found: {relFile}"));
                        return;
                    }

                    SendFileStream(stream, fullFile, "application/octet-stream", Path.GetFileName(fullFile));
                }
                catch (Exception ex)
                {
                    SendResponse(stream, 400, "Bad Request", "text/plain", Encoding.UTF8.GetBytes(ex.Message));
                }
            }
            else
            {
                SendResponse(stream, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("404 Not Found"));
            }
        }

        private static void HandlePost(Stream stream, string path, Dictionary<string, string> q, Dictionary<string, string> headers, byte[] buffer, int bodyOffset, int bodyAlreadyRead, long contentLength)
        {
            if (path == "/upload")
            {
                string relTarget = q.GetValueOrDefault("path", "");
                if (string.IsNullOrWhiteSpace(relTarget))
                {
                    SendResponse(stream, 400, "Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"success\": false, \"error\": \"Missing 'path' query parameter\"}"));
                    return;
                }

                if (headers.TryGetValue("content-type", out var ct) && ct.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                {
                    SendResponse(stream, 400, "Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"success\": false, \"error\": \"Send raw file bytes in POST body, not multipart/form-data\"}"));
                    return;
                }

                try
                {
                    string fullTarget = ResolveSafePath(relTarget);
                    string? dir = Path.GetDirectoryName(fullTarget);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string tempTarget = fullTarget + ".upload_tmp";
                    using (var fs = new FileStream(tempTarget, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        if (bodyAlreadyRead > 0)
                        {
                            int writeLen = (int)Math.Min(bodyAlreadyRead, contentLength > 0 ? contentLength : bodyAlreadyRead);
                            fs.Write(buffer, bodyOffset, writeLen);
                        }

                        long remaining = contentLength > 0 ? contentLength - bodyAlreadyRead : 0;
                        byte[] chunk = new byte[32768];
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(chunk.Length, remaining);
                            int read = stream.Read(chunk, 0, toRead);
                            if (read <= 0) break;
                            fs.Write(chunk, 0, read);
                            remaining -= read;
                        }
                    }

                    File.Move(tempTarget, fullTarget, overwrite: true);

                    long finalSize = new FileInfo(fullTarget).Length;
                    _monitor?.Log($"[RemoteServer] Successfully uploaded '{relTarget}' ({finalSize} bytes) over Wi-Fi.", LogLevel.Info);
                    string resp = $"{{\"success\": true, \"path\": \"{EscapeJson(relTarget)}\", \"bytes\": {finalSize}}}";
                    SendResponse(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes(resp));
                }
                catch (Exception ex)
                {
                    _monitor?.Log($"[RemoteServer] Upload failed for '{relTarget}': {ex.Message}", LogLevel.Warn);
                    SendResponse(stream, 500, "Internal Server Error", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": false, \"error\": \"{EscapeJson(ex.Message)}\"}}"));
                }
            }
            else if (path == "/api/mkdir")
            {
                string relTarget = q.GetValueOrDefault("path", "");
                if (string.IsNullOrWhiteSpace(relTarget))
                {
                    SendResponse(stream, 400, "Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"success\": false, \"error\": \"Missing 'path' query parameter\"}"));
                    return;
                }
                try
                {
                    string fullTarget = ResolveSafePath(relTarget);
                    Directory.CreateDirectory(fullTarget);
                    SendResponse(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": true, \"path\": \"{EscapeJson(relTarget)}\"}}"));
                }
                catch (Exception ex)
                {
                    SendResponse(stream, 500, "Internal Server Error", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": false, \"error\": \"{EscapeJson(ex.Message)}\"}}"));
                }
            }
            else if (path == "/api/delete")
            {
                string relTarget = q.GetValueOrDefault("path", "");
                if (string.IsNullOrWhiteSpace(relTarget))
                {
                    SendResponse(stream, 400, "Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"success\": false, \"error\": \"Missing 'path' query parameter\"}"));
                    return;
                }
                try
                {
                    string fullTarget = ResolveSafePath(relTarget);
                    string baseFull = Path.GetFullPath(_baseDir).TrimEnd('/', '\\');
                    if (string.Equals(fullTarget.TrimEnd('/', '\\'), baseFull, StringComparison.OrdinalIgnoreCase))
                    {
                        SendResponse(stream, 400, "Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"success\": false, \"error\": \"Cannot delete root Documents directory\"}"));
                        return;
                    }

                    if (File.Exists(fullTarget))
                    {
                        File.Delete(fullTarget);
                    }
                    else if (Directory.Exists(fullTarget))
                    {
                        Directory.Delete(fullTarget, recursive: true);
                    }
                    SendResponse(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": true, \"path\": \"{EscapeJson(relTarget)}\"}}"));
                }
                catch (Exception ex)
                {
                    SendResponse(stream, 500, "Internal Server Error", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": false, \"error\": \"{EscapeJson(ex.Message)}\"}}"));
                }
            }
            else if (path == "/api/rename")
            {
                string relFrom = q.GetValueOrDefault("from", "");
                string relTo = q.GetValueOrDefault("to", "");
                if (string.IsNullOrWhiteSpace(relFrom) || string.IsNullOrWhiteSpace(relTo))
                {
                    SendResponse(stream, 400, "Bad Request", "application/json", Encoding.UTF8.GetBytes("{\"success\": false, \"error\": \"Missing 'from' or 'to' query parameter\"}"));
                    return;
                }
                try
                {
                    string fullFrom = ResolveSafePath(relFrom);
                    string fullTo = ResolveSafePath(relTo);
                    if (Directory.Exists(fullFrom))
                    {
                        Directory.Move(fullFrom, fullTo);
                    }
                    else if (File.Exists(fullFrom))
                    {
                        File.Move(fullFrom, fullTo, overwrite: true);
                    }
                    else
                    {
                        SendResponse(stream, 404, "Not Found", "application/json", Encoding.UTF8.GetBytes("{\"success\": false, \"error\": \"Source path does not exist\"}"));
                        return;
                    }
                    SendResponse(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": true, \"from\": \"{EscapeJson(relFrom)}\", \"to\": \"{EscapeJson(relTo)}\"}}"));
                }
                catch (Exception ex)
                {
                    SendResponse(stream, 500, "Internal Server Error", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": false, \"error\": \"{EscapeJson(ex.Message)}\"}}"));
                }
            }
            else if (path == "/api/upload-zip")
            {
                string relDest = q.GetValueOrDefault("dest", "Mods");
                try
                {
                    string fullDest = ResolveSafePath(relDest);
                    Directory.CreateDirectory(fullDest);

                    string tempZip = Path.Combine(Path.GetTempPath(), $"sdvios_upload_{Guid.NewGuid():N}.zip");
                    try
                    {
                        using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            if (bodyAlreadyRead > 0)
                            {
                                int writeLen = (int)Math.Min(bodyAlreadyRead, contentLength > 0 ? contentLength : bodyAlreadyRead);
                                fs.Write(buffer, bodyOffset, writeLen);
                            }

                            long remaining = contentLength > 0 ? contentLength - bodyAlreadyRead : 0;
                            byte[] chunk = new byte[32768];
                            while (remaining > 0)
                            {
                                int toRead = (int)Math.Min(chunk.Length, remaining);
                                int read = stream.Read(chunk, 0, toRead);
                                if (read <= 0) break;
                                fs.Write(chunk, 0, read);
                                remaining -= read;
                            }
                        }

                        int extractedCount = 0;
                        string destRootFull = Path.GetFullPath(fullDest);
                        if (!destRootFull.EndsWith(Path.DirectorySeparatorChar.ToString()))
                            destRootFull += Path.DirectorySeparatorChar;

                        using (var archive = ZipFile.OpenRead(tempZip))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                if (string.IsNullOrEmpty(entry.FullName)) continue;
                                string entryRel = entry.FullName.Replace('\\', '/').TrimStart('/');
                                string targetPath = Path.GetFullPath(Path.Combine(fullDest, entryRel));
                                if (!targetPath.StartsWith(destRootFull, StringComparison.Ordinal))
                                    continue;

                                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                                {
                                    Directory.CreateDirectory(targetPath);
                                }
                                else
                                {
                                    string? entryDir = Path.GetDirectoryName(targetPath);
                                    if (!string.IsNullOrEmpty(entryDir))
                                        Directory.CreateDirectory(entryDir);
                                    entry.ExtractToFile(targetPath, overwrite: true);
                                    extractedCount++;
                                }
                            }
                        }

                        _monitor?.Log($"[RemoteServer] Extracted {extractedCount} files from ZIP upload into '{relDest}'.", LogLevel.Info);
                        SendResponse(stream, 200, "OK", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": true, \"dest\": \"{EscapeJson(relDest)}\", \"extracted_files\": {extractedCount}}}"));
                    }
                    finally
                    {
                        try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _monitor?.Log($"[RemoteServer] ZIP upload failed for '{relDest}': {ex.Message}", LogLevel.Warn);
                    SendResponse(stream, 500, "Internal Server Error", "application/json", Encoding.UTF8.GetBytes($"{{\"success\": false, \"error\": \"{EscapeJson(ex.Message)}\"}}"));
                }
            }
            else
            {
                SendResponse(stream, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("Not Found"));
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

        private static string ReadLastLines(string filePath, int lineCount)
        {
            if (!File.Exists(filePath)) return $"(Log file not found at: {filePath})";

            try
            {
                var lines = new List<string>();
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                        if (lines.Count > lineCount * 2)
                        {
                            lines.RemoveRange(0, lines.Count - lineCount);
                        }
                    }
                }

                int take = Math.Min(lines.Count, lineCount);
                var sub = lines.GetRange(lines.Count - take, take);
                return string.Join("\n", sub);
            }
            catch (Exception ex)
            {
                return $"(Error reading log: {ex.Message})";
            }
        }

        private static string ResolveSafePath(string relativePath)
        {
            string baseFull = Path.GetFullPath(_baseDir);
            string normalizedRel = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(normalizedRel))
                return baseFull;

            string combined = Path.GetFullPath(Path.Combine(baseFull, normalizedRel));
            string baseWithSep = baseFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? baseFull
                : baseFull + Path.DirectorySeparatorChar;

            if (!combined.Equals(baseFull, StringComparison.Ordinal) &&
                !combined.StartsWith(baseWithSep, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Access outside Documents directory is not permitted.");
            }
            return combined;
        }

        private static string BuildDashboardHtml()
        {
            long availMb = MemoryWatchdog.Available() / (1024 * 1024);
            long minMb = MemoryWatchdog.MinSeenMB == long.MaxValue ? availMb : MemoryWatchdog.MinSeenMB;
            long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
            string timeStr = WebUtility.HtmlEncode(ModEntry.CachedTimeString);
            string locStr = WebUtility.HtmlEncode(ModEntry.CachedLocationName);
            string menuStr = WebUtility.HtmlEncode(ModEntry.CachedActiveMenuName);

            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1'>
    <title>Stardew Valley iOS Debug Server</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: #1a1a24; color: #e6e6f0; margin: 0; padding: 20px; }}
        h1 {{ color: #4ade80; margin-top: 0; }}
        .card {{ background: #242436; border-radius: 8px; padding: 16px; margin-bottom: 16px; border: 1px solid #383854; }}
        .stat {{ display: flex; justify-content: space-between; padding: 8px 0; border-bottom: 1px solid #2e2e46; }}
        .stat:last-child {{ border-bottom: none; }}
        .stat-val {{ font-weight: bold; color: #60a5fa; }}
        .btn {{ display: inline-block; background: #2563eb; color: #fff; padding: 10px 16px; border-radius: 6px; text-decoration: none; margin-right: 8px; margin-bottom: 8px; font-weight: bold; border: none; cursor: pointer; }}
        .btn:hover {{ background: #1d4ed8; }}
        .btn-green {{ background: #16a34a; }}
        .btn-green:hover {{ background: #15803d; }}
        .btn-orange {{ background: #ea580c; }}
        .btn-orange:hover {{ background: #c2410c; }}
        pre {{ background: #12121a; padding: 12px; border-radius: 6px; overflow-x: auto; color: #a5b4fc; }}
    </style>
</head>
<body>
    <h1>Stardew Valley iOS Debug Server</h1>
    <div class='card'>
        <div class='stat'><span>iOS Available Memory (Headroom / Min Seen):</span><span class='stat-val'>{availMb} MB (min: {minMb} MB)</span></div>
        <div class='stat'><span>Managed Heap (.NET Memory):</span><span class='stat-val'>{managedMb} MB</span></div>
        <div class='stat'><span>Game Mode:</span><span class='stat-val'>{ModEntry.CachedGameMode}</span></div>
        <div class='stat'><span>World Ready:</span><span class='stat-val'>{ModEntry.CachedWorldReady}</span></div>
        <div class='stat'><span>In-Game Time:</span><span class='stat-val'>{timeStr}</span></div>
        <div class='stat'><span>Current Location:</span><span class='stat-val'>{locStr}</span></div>
        <div class='stat'><span>Active Menu:</span><span class='stat-val'>{menuStr}</span></div>
    </div>

    <div class='card'>
        <h3>QuickSave / Save State</h3>
        <a class='btn btn-green' href='/quicksave' target='_blank'>💾 Quick Save State</a>
        <a class='btn' href='/quickload' target='_blank'>🔄 Quick Load State</a>
        <a class='btn' href='/api/savestate' target='_blank'>📋 View Save State JSON</a>
    </div>

    <div class='card'>
        <h3>Actions</h3>
        <a class='btn btn-green' href='/log' target='_blank'>Download Current SMAPI Log</a>
        <a class='btn btn-orange' href='/log-previous' target='_blank'>Download Previous/Crash SMAPI Log</a>
        <a class='btn' href='/tail?lines=200' target='_blank'>Tail Current Log</a>
        <a class='btn' href='/tail-previous?lines=200' target='_blank'>Tail Previous Log</a>
        <a class='btn btn-green' href='/engine-log' target='_blank'>Download engine-latest.log</a>
        <a class='btn btn-orange' href='/engine-previous-log' target='_blank'>Download engine-previous.log</a>
        <a class='btn' href='/gc' target='_blank'>Force Compact GC</a>
        <a class='btn' href='/files'>Browse Documents Folder</a>
    </div>

    <div class='card'>
        <h3>Upload File Over Wi-Fi</h3>
        <form onsubmit='return doUpload(event)'>
            <p>Target Path (relative to Documents, e.g. <code>Mods/SDViOSTouchControls/SDViOSTouchControls.dll</code>):</p>
            <input type='text' id='targetPath' style='width: 100%; padding: 8px; margin-bottom: 12px; border-radius: 4px; border: 1px solid #444; background: #181822; color: #fff;' value='Mods/SDViOSTouchControls/SDViOSTouchControls.dll'><br>
            <input type='file' id='fileInput' style='margin-bottom: 12px;'><br>
            <button type='submit' class='btn btn-green'>Upload File</button>
            <span id='uploadStatus' style='margin-left: 12px; font-weight: bold;'></span>
        </form>
    </div>

    <script>
        async function doUpload(e) {{
            e.preventDefault();
            const target = document.getElementById('targetPath').value.trim();
            const fileInput = document.getElementById('fileInput');
            const status = document.getElementById('uploadStatus');
            if (!fileInput.files.length) {{ alert('Select a file first!'); return false; }}
            const file = fileInput.files[0];
            status.textContent = 'Uploading ' + file.name + ' (' + file.size + ' bytes)...';
            status.style.color = '#fbbf24';

            try {{
                const resp = await fetch('/upload?path=' + encodeURIComponent(target), {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/octet-stream' }},
                    body: file
                }});
                const res = await resp.json();
                if (res.success) {{
                    status.textContent = 'Uploaded ' + res.bytes + ' bytes successfully!';
                    status.style.color = '#4ade80';
                }} else {{
                    status.textContent = 'Error: ' + JSON.stringify(res);
                    status.style.color = '#ef4444';
                }}
            }} catch(err) {{
                status.textContent = 'Upload failed: ' + err;
                status.style.color = '#ef4444';
            }}
            return false;
        }}
    </script>
</body>
</html>";
        }

        private static string BuildDirectoryListingHtml(string relDir, string fullDir)
        {
            var sb = new StringBuilder();
            sb.Append($@"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Directory: {WebUtility.HtmlEncode(relDir)}</title>
<style>
body {{ font-family: -apple-system, sans-serif; background: #1a1a24; color: #e6e6f0; padding: 20px; }}
a {{ color: #60a5fa; text-decoration: none; }}
a:hover {{ text-decoration: underline; }}
table {{ width: 100%; border-collapse: collapse; margin-top: 16px; }}
th, td {{ padding: 8px 12px; text-align: left; border-bottom: 1px solid #2e2e46; }}
th {{ background: #242436; color: #9ca3af; }}
.btn {{ background: #2563eb; color: #fff; padding: 6px 12px; border-radius: 4px; text-decoration: none; font-size: 13px; }}
</style></head><body>");

            sb.Append($"<h2>Directory: /{WebUtility.HtmlEncode(relDir)}</h2>");
            sb.Append("<p><a class='btn' href='/'>&larr; Back to Dashboard</a>");
            if (!string.IsNullOrEmpty(relDir))
            {
                string parent = Path.GetDirectoryName(relDir.TrimEnd('/', '\\'))?.Replace('\\', '/') ?? "";
                sb.Append($" <a class='btn' href='/files?dir={WebUtility.UrlEncode(parent)}'>&uarr; Up Directory</a>");
            }
            sb.Append("</p>");

            sb.Append("<table><tr><th>Name</th><th>Size</th><th>Last Modified</th><th>Actions</th></tr>");

            try
            {
                var dirInfo = new DirectoryInfo(fullDir);
                foreach (var d in dirInfo.GetDirectories())
                {
                    string subRel = string.IsNullOrEmpty(relDir) ? d.Name : $"{relDir}/{d.Name}".Replace('\\', '/');
                    sb.Append($"<tr><td>📁 <a href='/files?dir={WebUtility.UrlEncode(subRel)}'>{WebUtility.HtmlEncode(d.Name)}/</a></td><td>-</td><td>{d.LastWriteTimeUtc:yyyy-MM-dd HH:mm}</td><td></td></tr>");
                }
                foreach (var f in dirInfo.GetFiles())
                {
                    string fRel = string.IsNullOrEmpty(relDir) ? f.Name : $"{relDir}/{f.Name}".Replace('\\', '/');
                    sb.Append($"<tr><td>📄 {WebUtility.HtmlEncode(f.Name)}</td><td>{f.Length:N0} bytes</td><td>{f.LastWriteTimeUtc:yyyy-MM-dd HH:mm}</td><td><a class='btn' href='/download?path={WebUtility.UrlEncode(fRel)}'>Download</a></td></tr>");
                }
            }
            catch (Exception ex)
            {
                sb.Append($"<tr><td colspan='4' style='color:#ef4444;'>Error listing directory: {WebUtility.HtmlEncode(ex.Message)}</td></tr>");
            }

            sb.Append("</table></body></html>");
            return sb.ToString();
        }

        private static void SendResponse(Stream stream, int code, string status, string contentType, byte[] body)
        {
            string header = $"HTTP/1.1 {code} {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }
            stream.Flush();
        }

        private static void SendFileStream(Stream stream, string filePath, string contentType, string downloadName)
        {
            if (!File.Exists(filePath))
            {
                SendResponse(stream, 404, "Not Found", "text/plain", Encoding.UTF8.GetBytes("File Not Found: " + filePath));
                return;
            }

            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long length = fs.Length;
                    string header = $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\nContent-Disposition: inline; filename=\"{downloadName}\"\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
                    byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                    stream.Write(headerBytes, 0, headerBytes.Length);

                    byte[] buf = new byte[32768];
                    int read;
                    while ((read = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        stream.Write(buf, 0, read);
                    }
                    stream.Flush();
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[RemoteServer] Stream file error: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}
