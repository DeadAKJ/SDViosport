using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Foundation;
using Microsoft.Xna.Framework;
using UIKit;
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

            // Check if game files are placed directly in Documents/ (Files App root) or in Documents/StardewValley/
            string directDll = Path.Combine(DocumentsDir, "Stardew Valley.dll");
            string subDirDll = Path.Combine(DocumentsDir, "StardewValley", "Stardew Valley.dll");

            if (File.Exists(directDll))
            {
                GameRootDir = DocumentsDir;
            }
            else if (File.Exists(subDirDll))
            {
                GameRootDir = Path.Combine(DocumentsDir, "StardewValley");
            }
            else
            {
                // Default to Documents/ directly so users can just drop files into root of Files app
                GameRootDir = DocumentsDir;
            }

            ContentDir = Path.Combine(GameRootDir, "Content");
            ModsDir = Path.Combine(GameRootDir, "Mods");
            SavesDir = Path.Combine(GameRootDir, "Saves");
            LogsDir = Path.Combine(GameRootDir, "ErrorLogs");

            Directory.CreateDirectory(GameRootDir);
            Directory.CreateDirectory(ContentDir);
            Directory.CreateDirectory(ModsDir);
            Directory.CreateDirectory(SavesDir);
            Directory.CreateDirectory(LogsDir);

            EngineLogger.Initialize(LogsDir);
            EngineLogger.Log($"DocumentsDir: {DocumentsDir}");
            EngineLogger.Log($"GameRootDir: {GameRootDir}");
            EngineLogger.Log($"ContentDir: {ContentDir}");
            EngineLogger.Log($"ModsDir: {ModsDir}");

            // Copy bundled assets if present in App Bundle
            string bundlePath = NSBundle.MainBundle.BundlePath;
            EngineLogger.Log($"BundlePath: {bundlePath}");

            SyncBundledDirectory(Path.Combine(bundlePath, "Content"), ContentDir);
            SyncBundledDirectory(Path.Combine(bundlePath, "Mods"), ModsDir);
            SyncBundledDirectory(Path.Combine(bundlePath, "smapi-internal"), Path.Combine(GameRootDir, "smapi-internal"));

            foreach (var dll in Directory.GetFiles(bundlePath, "*.dll"))
            {
                string dest = Path.Combine(GameRootDir, Path.GetFileName(dll));
                if (!File.Exists(dest))
                {
                    try { File.Copy(dll, dest, true); } catch { }
                }
            }

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
                    try { File.Copy(file, dest, true); } catch { }
                }
            }
        }

        private static void SetupAssemblyResolver(string bundlePath)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string asmName = new AssemblyName(args.Name).Name + ".dll";

                string p1 = Path.Combine(GameRootDir, asmName);
                if (File.Exists(p1)) return Assembly.LoadFrom(p1);

                string p2 = Path.Combine(GameRootDir, "smapi-internal", asmName);
                if (File.Exists(p2)) return Assembly.LoadFrom(p2);

                string p3 = Path.Combine(bundlePath, asmName);
                if (File.Exists(p3)) return Assembly.LoadFrom(p3);

                string p4 = Path.Combine(bundlePath, "smapi-internal", asmName);
                if (File.Exists(p4)) return Assembly.LoadFrom(p4);

                // Also check Documents/ directly if GameRootDir is a subfolder
                string p5 = Path.Combine(DocumentsDir, asmName);
                if (File.Exists(p5)) return Assembly.LoadFrom(p5);

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
            if (!File.Exists(smapiPath))
            {
                smapiPath = Path.Combine(DocumentsDir, "StardewModdingAPI.dll");
            }

            string sdvPath = Path.Combine(GameRootDir, "Stardew Valley.dll");
            if (!File.Exists(sdvPath))
            {
                sdvPath = Path.Combine(DocumentsDir, "Stardew Valley.dll");
            }

            bool forceVanilla = File.Exists(Path.Combine(GameRootDir, "force_vanilla.txt")) ||
                                File.Exists(Path.Combine(DocumentsDir, "force_vanilla.txt"));

            if (forceVanilla)
            {
                EngineLogger.Log("force_vanilla.txt detected. Launching Pure Vanilla.");
                LaunchVanilla(sdvPath, args);
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
                }
                catch (Exception ex)
                {
                    EngineLogger.LogFatal("SMAPI Launch", ex);
                }
            }

            LaunchVanilla(sdvPath, args);
        }

        public static void LaunchVanilla(string sdvPath, string[] args)
        {
            if (!File.Exists(sdvPath))
            {
                EngineLogger.LogError("Stardew Valley.dll not found in " + GameRootDir + " or " + DocumentsDir);
                ShowMissingFilesAlert();
                return;
            }

            EngineLogger.Log("Launching Pure Vanilla Stardew Valley from: " + sdvPath);
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
                    object?[] invokeArgs = entry != null && entry.GetParameters().Length > 0 ? new object?[] { args } : Array.Empty<object>();
                    entry?.Invoke(null, invokeArgs);
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogFatal("Vanilla Launch", ex);
            }
        }

        private static void ShowMissingFilesAlert()
        {
            try
            {
                UIApplication.SharedApplication.InvokeOnMainThread(() =>
                {
                    var alert = UIAlertController.Create(
                        "Stardew Valley Files Missing",
                        "Please copy your Stardew Valley game files into the iOS Files app:\n\nOn My iPhone > Stardew Valley\n\nInclude Stardew Valley.dll and Content folder.",
                        UIAlertControllerStyle.Alert
                    );
                    alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
                    var rootVC = UIApplication.SharedApplication.KeyWindow?.RootViewController;
                    rootVC?.PresentViewController(alert, true, null);
                });
            }
            catch { }
        }
    }
}
