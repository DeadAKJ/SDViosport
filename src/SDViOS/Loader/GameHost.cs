using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Foundation;
using Microsoft.Xna.Framework;
using SDViOS.Diagnostics;
using SDViOS.Input;

namespace SDViOS.Loader
{
    public static class GameHost
    {
        public static string DocumentsDir { get; private set; } = string.Empty;
        public static string GameRootDir { get; private set; } = string.Empty;
        public static string ContentDir { get; private set; } = string.Empty;
        public static string ModsDir { get; private set; } = string.Empty;
        public static string SavesDir { get; private set; } = string.Empty;
        public static string LogsDir { get; private set; } = string.Empty;

        public static void InitializeFileSystem()
        {
            DocumentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            GameRootDir = Path.Combine(DocumentsDir, "StardewValley");
            ContentDir = Path.Combine(GameRootDir, "Content");
            ModsDir = Path.Combine(GameRootDir, "Mods");
            SavesDir = Path.Combine(GameRootDir, "Saves");
            LogsDir = Path.Combine(GameRootDir, "ErrorLogs");

            // Ensure essential directories exist in user-accessible Files directory
            Directory.CreateDirectory(GameRootDir);
            Directory.CreateDirectory(ContentDir);
            Directory.CreateDirectory(ModsDir);
            Directory.CreateDirectory(SavesDir);
            Directory.CreateDirectory(LogsDir);

            // Initialize comprehensive engine logging
            EngineLogger.Initialize(LogsDir);
            EngineLogger.Log($"GameRootDir: {GameRootDir}");
            EngineLogger.Log($"ContentDir: {ContentDir}");
            EngineLogger.Log($"ModsDir: {ModsDir}");
            EngineLogger.Log($"SavesDir: {SavesDir}");

            // Copy bundled assets if present in App Bundle
            string bundlePath = NSBundle.MainBundle.BundlePath;
            EngineLogger.Log($"BundlePath: {bundlePath}");

            SyncBundledDirectory(Path.Combine(bundlePath, "Content"), ContentDir);
            SyncBundledDirectory(Path.Combine(bundlePath, "Mods"), ModsDir);
            SyncBundledDirectory(Path.Combine(bundlePath, "smapi-internal"), Path.Combine(GameRootDir, "smapi-internal"));

            // Copy any loose DLLs from bundle to GameRootDir if not already there
            foreach (var dll in Directory.GetFiles(bundlePath, "*.dll"))
            {
                string dest = Path.Combine(GameRootDir, Path.GetFileName(dll));
                if (!File.Exists(dest))
                {
                    try
                    {
                        File.Copy(dll, dest, true);
                        EngineLogger.Log($"Copied bundle DLL to GameRoot: {Path.GetFileName(dll)}");
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogError($"Failed copying DLL {dll}: {ex.Message}");
                    }
                }
            }

            // Set environment and current working directory
            Environment.SetEnvironmentVariable("APPDATA", DocumentsDir);
            Environment.SetEnvironmentVariable("STARDEW_VALLEY_MODS_PATH", ModsDir);
            Environment.SetEnvironmentVariable("MONO_STRICT_MS_COMPLIANT", "yes");
            Directory.SetCurrentDirectory(GameRootDir);

            SetupAssemblyResolver(bundlePath);
        }

        private static void SyncBundledDirectory(string sourceDir, string targetDir)
        {
            if (!Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(targetDir);

            EngineLogger.Log($"Syncing {sourceDir} -> {targetDir}");
            foreach (string file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDir, file);
                string dest = Path.Combine(targetDir, relative);
                string? destFolder = Path.GetDirectoryName(dest);
                if (destFolder != null && !Directory.Exists(destFolder))
                {
                    Directory.CreateDirectory(destFolder);
                }

                if (!File.Exists(dest))
                {
                    try
                    {
                        File.Copy(file, dest, true);
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogError($"Failed syncing file {file}: {ex.Message}");
                    }
                }
            }
        }

        private static void SetupAssemblyResolver(string bundlePath)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string asmName = new AssemblyName(args.Name).Name + ".dll";

                // Check game root
                string p1 = Path.Combine(GameRootDir, asmName);
                if (File.Exists(p1))
                {
                    EngineLogger.Log($"[AssemblyResolve] Loaded '{asmName}' from GameRoot: {p1}");
                    return Assembly.LoadFrom(p1);
                }

                // Check smapi-internal
                string p2 = Path.Combine(GameRootDir, "smapi-internal", asmName);
                if (File.Exists(p2))
                {
                    EngineLogger.Log($"[AssemblyResolve] Loaded '{asmName}' from smapi-internal: {p2}");
                    return Assembly.LoadFrom(p2);
                }

                // Check bundle root
                string p3 = Path.Combine(bundlePath, asmName);
                if (File.Exists(p3))
                {
                    EngineLogger.Log($"[AssemblyResolve] Loaded '{asmName}' from Bundle: {p3}");
                    return Assembly.LoadFrom(p3);
                }

                // Check bundle smapi-internal
                string p4 = Path.Combine(bundlePath, "smapi-internal", asmName);
                if (File.Exists(p4))
                {
                    EngineLogger.Log($"[AssemblyResolve] Loaded '{asmName}' from Bundle smapi-internal: {p4}");
                    return Assembly.LoadFrom(p4);
                }

                EngineLogger.LogWarning($"[AssemblyResolve] Unresolved assembly: {args.Name}");
                return null;
            };
        }

        public static void AttachTouchOverlay(Game game)
        {
            if (game != null)
            {
                EngineLogger.Log("Attaching TouchOverlay to Game instance.");
                var overlay = new TouchOverlay(game);
                game.Components.Add(overlay);
            }
        }

        public static void Launch(string[] args)
        {
            string smapiPath = Path.Combine(GameRootDir, "StardewModdingAPI.dll");
            string sdvPath = Path.Combine(GameRootDir, "Stardew Valley.dll");
            bool forceVanilla = File.Exists(Path.Combine(GameRootDir, "force_vanilla.txt"));

            if (forceVanilla)
            {
                EngineLogger.Log("force_vanilla.txt detected. Skipping SMAPI to launch Pure Vanilla.");
                LaunchVanilla(args);
                return;
            }

            if (File.Exists(smapiPath))
            {
                EngineLogger.Log("Found StardewModdingAPI.dll. Bootstrapping SMAPI on iOS...");
                try
                {
                    var smapiAsm = Assembly.LoadFrom(smapiPath);
                    var entry = smapiAsm.EntryPoint;
                    if (entry != null)
                    {
                        EngineLogger.Log($"Invoking SMAPI EntryPoint: {entry.DeclaringType?.FullName}.{entry.Name}");
                        object?[] invokeArgs = entry.GetParameters().Length > 0 ? new object?[] { args } : Array.Empty<object>();
                        entry.Invoke(null, invokeArgs);
                        return;
                    }
                    else
                    {
                        EngineLogger.LogError("SMAPI EntryPoint is null! Falling back to Vanilla.");
                    }
                }
                catch (Exception ex)
                {
                    EngineLogger.LogFatal("SMAPI Launch", ex);
                    EngineLogger.Log("Falling back to Vanilla launch after SMAPI error...");
                }
            }

            LaunchVanilla(args);
        }

        public static void LaunchVanilla(string[] args)
        {
            string sdvPath = Path.Combine(GameRootDir, "Stardew Valley.dll");
            if (!File.Exists(sdvPath))
            {
                EngineLogger.LogWarning("No Stardew Valley.dll found! Please place game files into iOS Files app (On My iPhone > Stardew Valley).");
                return;
            }

            EngineLogger.Log("Launching Pure Vanilla Stardew Valley...");
            try
            {
                var sdvAsm = Assembly.LoadFrom(sdvPath);
                var runnerType = sdvAsm.GetType("StardewValley.GameRunner");
                if (runnerType != null)
                {
                    EngineLogger.Log("Instantiating StardewValley.GameRunner with TouchOverlay...");
                    var runner = (Game)Activator.CreateInstance(runnerType)!;
                    AttachTouchOverlay(runner);
                    runner.Run();
                }
                else
                {
                    var entry = sdvAsm.EntryPoint;
                    EngineLogger.Log($"Invoking Vanilla EntryPoint: {entry?.DeclaringType?.FullName}.{entry?.Name}");
                    object?[] invokeArgs = entry != null && entry.GetParameters().Length > 0 ? new object?[] { args } : Array.Empty<object>();
                    entry?.Invoke(null, invokeArgs);
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogFatal("Vanilla Launch", ex);
            }
        }
    }
}
