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
        public static string BundleDir { get; private set; } = string.Empty;

        public static void InitializeFileSystem()
        {
            DocumentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            BundleDir = NSBundle.MainBundle.BundlePath;

            // Check if game files are placed directly in Documents/, Documents/StardewValley/, or in Bundle
            string directDll = Path.Combine(DocumentsDir, "Stardew Valley.dll");
            string subDirDll = Path.Combine(DocumentsDir, "StardewValley", "Stardew Valley.dll");
            string bundleDll = Path.Combine(BundleDir, "Stardew Valley.dll");

            if (File.Exists(directDll))
            {
                GameRootDir = DocumentsDir;
            }
            else if (File.Exists(subDirDll))
            {
                GameRootDir = Path.Combine(DocumentsDir, "StardewValley");
            }
            else if (File.Exists(bundleDll))
            {
                // Bundled inside the IPA!
                GameRootDir = DocumentsDir; // Keep user data in Documents
            }
            else
            {
                GameRootDir = DocumentsDir;
            }

            ContentDir = Path.Combine(GameRootDir, "Content");
            ModsDir = Path.Combine(DocumentsDir, "Mods");
            SavesDir = Path.Combine(DocumentsDir, "Saves");
            LogsDir = Path.Combine(DocumentsDir, "ErrorLogs");

            Directory.CreateDirectory(GameRootDir);
            Directory.CreateDirectory(ModsDir);
            Directory.CreateDirectory(SavesDir);
            Directory.CreateDirectory(LogsDir);

            EngineLogger.Initialize(LogsDir);
            EngineLogger.Log($"[Engine] Initialized file system.");
            EngineLogger.Log($"BundleDir: {BundleDir}");
            EngineLogger.Log($"DocumentsDir: {DocumentsDir}");
            EngineLogger.Log($"ModsDir: {ModsDir}");

            // If bundled Mods directory exists and user Documents/Mods is empty, copy default bundled mods (only a few KB)
            string bundledMods = Path.Combine(BundleDir, "Mods");
            if (Directory.Exists(bundledMods) && Directory.GetFileSystemEntries(ModsDir).Length == 0)
            {
                try
                {
                    SyncBundledDirectory(bundledMods, ModsDir);
                    EngineLogger.Log("[Engine] Seeded default mods to Documents/Mods.");
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[Engine] Failed to seed default mods: {ex.Message}");
                }
            }

            // Clean up any stale BCL DLL that may have been placed into Documents in older builds
            string staleCoreLib = Path.Combine(GameRootDir, "System.Private.CoreLib.dll");
            if (File.Exists(staleCoreLib))
            {
                try { File.Delete(staleCoreLib); } catch { }
            }

            Environment.SetEnvironmentVariable("APPDATA", DocumentsDir);
            Environment.SetEnvironmentVariable("STARDEW_VALLEY_MODS_PATH", ModsDir);
            Environment.SetEnvironmentVariable("SMAPI_MODS_PATH", ModsDir);
            Environment.SetEnvironmentVariable("SMAPI_NO_TERMINAL", "1");
            Environment.SetEnvironmentVariable("MONO_STRICT_MS_COMPLIANT", "yes");
            Directory.SetCurrentDirectory(GameRootDir);

            SetupAssemblyResolver(BundleDir);
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

                if (!File.Exists(dest) || File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(dest))
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

                // Never resolve core runtime libraries from user directories
                if (asmName.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) ||
                    asmName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string p1 = Path.Combine(bundlePath, asmName);
                if (File.Exists(p1)) return Assembly.LoadFrom(p1);

                string p2 = Path.Combine(bundlePath, "smapi-internal", asmName);
                if (File.Exists(p2)) return Assembly.LoadFrom(p2);

                string p3 = Path.Combine(GameRootDir, asmName);
                if (File.Exists(p3)) return Assembly.LoadFrom(p3);

                string p4 = Path.Combine(GameRootDir, "smapi-internal", asmName);
                if (File.Exists(p4)) return Assembly.LoadFrom(p4);

                string p5 = Path.Combine(DocumentsDir, asmName);
                if (File.Exists(p5)) return Assembly.LoadFrom(p5);

                EngineLogger.LogWarning($"[AssemblyResolve] Unresolved assembly: {args.Name}");
                return null;
            };

            AppDomain.CurrentDomain.TypeResolve += (sender, args) =>
            {
                // If a type failed to resolve because of a trimmed or forwarded assembly,
                // scan loaded assemblies (like System.Private.Xml) for the type.
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType(args.Name, false);
                        if (t != null)
                        {
                            EngineLogger.Log($"[TypeResolve] Resolved '{args.Name}' from assembly '{asm.GetName().Name}'");
                            return asm;
                        }
                    }
                    catch { }
                }

                EngineLogger.LogWarning($"[TypeResolve] Unresolved type: {args.Name}");
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

        public static bool TryFindGameBinary(out string sdvPath, out string smapiPath)
        {
            sdvPath = string.Empty;
            smapiPath = string.Empty;

            string[] searchPaths = new[] { BundleDir, GameRootDir, DocumentsDir, Path.Combine(DocumentsDir, "StardewValley") };
            foreach (var dir in searchPaths)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string checkSdv = Path.Combine(dir, "Stardew Valley.dll");
                if (File.Exists(checkSdv) && string.IsNullOrEmpty(sdvPath))
                {
                    sdvPath = checkSdv;
                }
                string checkSmapi = Path.Combine(dir, "StardewModdingAPI.dll");
                if (File.Exists(checkSmapi) && string.IsNullOrEmpty(smapiPath))
                {
                    smapiPath = checkSmapi;
                }
            }

            return !string.IsNullOrEmpty(sdvPath);
        }

        public static void Launch(string[] args)
        {
            if (!TryFindGameBinary(out string sdvPath, out string smapiPath))
            {
                EngineLogger.LogError($"Stardew Valley.dll not found in any search path!");
                return;
            }

            bool forceVanilla = File.Exists(Path.Combine(GameRootDir, "force_vanilla.txt")) ||
                                File.Exists(Path.Combine(DocumentsDir, "force_vanilla.txt"));

            if (forceVanilla)
            {
                EngineLogger.Log("force_vanilla.txt detected. Launching Pure Vanilla.");
                LaunchVanilla(sdvPath, args);
                return;
            }

            if (!string.IsNullOrEmpty(smapiPath) && File.Exists(smapiPath))
            {
                EngineLogger.Log($"Found StardewModdingAPI.dll at '{smapiPath}'. Bootstrapping SMAPI...");
                try
                {
                    Environment.SetEnvironmentVariable("SMAPI_MODS_PATH", ModsDir);
                    Environment.SetEnvironmentVariable("SMAPI_NO_TERMINAL", "1");
                    Environment.SetEnvironmentVariable("STARDEW_VALLEY_MODS_PATH", ModsDir);

                    string[] smapiArgs = new string[] { "--no-terminal", "--mods-path", ModsDir };

                    var smapiAsm = Assembly.LoadFrom(smapiPath);
                    var entry = smapiAsm.EntryPoint;
                    if (entry != null)
                    {
                        EngineLogger.Log($"Invoking SMAPI EntryPoint: {entry.DeclaringType?.FullName}.{entry.Name}");
                        object?[] invokeArgs = entry.GetParameters().Length > 0 ? new object?[] { smapiArgs } : Array.Empty<object>();
                        entry.Invoke(null, invokeArgs);

                        LinkGameWindowToScene();
                        AttachTouchOverlayToGameRunner();
                        UIApplication.SharedApplication.BeginInvokeOnMainThread(LinkGameWindowToScene);
                        NSTimer.CreateScheduledTimer(0.25, false, _ => LinkGameWindowToScene());
                        NSTimer.CreateScheduledTimer(1.0, false, _ => LinkGameWindowToScene());
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
            EngineLogger.Log("Launching Pure Vanilla Stardew Valley from: " + sdvPath);
            try
            {
                var sdvAsm = Assembly.LoadFrom(sdvPath);
                var runnerType = sdvAsm.GetType("StardewValley.GameRunner");
                if (runnerType != null)
                {
                    EngineLogger.Log("Instantiating StardewValley.GameRunner with TouchOverlay...");
                    System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                    var runner = (Game)Activator.CreateInstance(runnerType)!;

                    var instanceField = runnerType.GetField("instance", BindingFlags.Static | BindingFlags.Public);
                    instanceField?.SetValue(null, runner);

                    AttachTouchOverlay(runner);
                    runner.Run();

                    LinkGameWindowToScene();
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(LinkGameWindowToScene);
                    NSTimer.CreateScheduledTimer(0.25, false, _ => LinkGameWindowToScene());
                    NSTimer.CreateScheduledTimer(1.0, false, _ => LinkGameWindowToScene());
                }
                else
                {
                    var entry = sdvAsm.EntryPoint;
                    object?[] invokeArgs = entry != null && entry.GetParameters().Length > 0 ? new object?[] { args } : Array.Empty<object>();
                    entry?.Invoke(null, invokeArgs);
                    LinkGameWindowToScene();
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(LinkGameWindowToScene);
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogFatal("Vanilla Launch", ex);
                throw;
            }
        }

        public static UIWindowScene? GetActiveWindowScene()
        {
            try
            {
                var scenes = UIApplication.SharedApplication.ConnectedScenes;
                if (scenes == null) return null;

                foreach (var scene in scenes)
                {
                    if (scene is UIWindowScene ws && ws.ActivationState == UISceneActivationState.ForegroundActive)
                        return ws;
                }

                foreach (var scene in scenes)
                {
                    if (scene is UIWindowScene ws && ws.ActivationState == UISceneActivationState.ForegroundInactive)
                        return ws;
                }

                foreach (var scene in scenes)
                {
                    if (scene is UIWindowScene ws)
                        return ws;
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] Error getting UIWindowScene: {ex.Message}");
            }
            return null;
        }

        public static void LinkGameWindowToScene()
        {
            try
            {
                EngineLogger.Log("[GameHost] LinkGameWindowToScene: linking window...");

                Game? runner = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var runnerType = asm.GetType("StardewValley.GameRunner");
                    if (runnerType != null)
                    {
                        var instanceField = runnerType.GetField("instance", BindingFlags.Static | BindingFlags.Public);
                        runner = instanceField?.GetValue(null) as Game;
                        if (runner != null) break;
                    }
                }

                UIWindow? window = null;
                UIViewController? vc = null;

                if (runner != null)
                {
                    window = runner.Services.GetService(typeof(UIWindow)) as UIWindow;
                    vc = runner.Services.GetService(typeof(UIViewController)) as UIViewController;
                    EngineLogger.Log($"[GameHost] Runner services: Window={window != null}, VC={vc != null}");
                }

                if (window == null)
                {
                    foreach (var w in UIApplication.SharedApplication.Windows)
                    {
                        if (w != null)
                        {
                            window = w;
                            vc = w.RootViewController;
                            EngineLogger.Log($"[GameHost] Found window from UIApplication.Windows: {window}");
                            break;
                        }
                    }
                }

                if (window == null)
                {
                    EngineLogger.LogWarning("[GameHost] No UIWindow found to link yet.");
                    return;
                }

                var activeScene = GetActiveWindowScene();
                if (activeScene != null)
                {
                    if (window.WindowScene != activeScene)
                    {
                        EngineLogger.Log($"[GameHost] Assigning window.WindowScene to {activeScene.Description} (State: {activeScene.ActivationState})");
                        window.WindowScene = activeScene;
                    }
                }
                else
                {
                    EngineLogger.LogWarning("[GameHost] No connected UIWindowScene found yet.");
                }

                if (window.RootViewController == null && vc != null)
                {
                    window.RootViewController = vc;
                }

                if (UIApplication.SharedApplication.Delegate is AppDelegate appDelegate)
                {
                    if (appDelegate.Window != window)
                    {
                        appDelegate.Window = window;
                        EngineLogger.Log("[GameHost] Set AppDelegate.Window to MonoGame UIWindow.");
                    }
                }

                window.Hidden = false;
                window.MakeKeyAndVisible();

                try
                {
                    window.SetNeedsLayout();
                    window.LayoutIfNeeded();
                }
                catch { }

                EngineLogger.Log("[GameHost] MonoGame UIWindow successfully linked and made key.");
            }
            catch (Exception ex)
            {
                EngineLogger.LogError($"[GameHost] LinkGameWindowToScene failed: {ex}");
            }
        }

        public static void AttachTouchOverlayToGameRunner()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var runnerType = asm.GetType("StardewValley.GameRunner");
                    if (runnerType != null)
                    {
                        var instanceField = runnerType.GetField("instance", BindingFlags.Static | BindingFlags.Public);
                        var runner = instanceField?.GetValue(null) as Game;
                        if (runner != null && runner.Components != null)
                        {
                            foreach (var comp in runner.Components)
                            {
                                if (comp is TouchOverlay)
                                {
                                    EngineLogger.Log("[GameHost] TouchOverlay already attached to GameRunner.");
                                    return;
                                }
                            }

                            AttachTouchOverlay(runner);
                            EngineLogger.Log("[GameHost] Successfully attached TouchOverlay to GameRunner.");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] Could not attach TouchOverlay to GameRunner: {ex.Message}");
            }
        }
    }
}
