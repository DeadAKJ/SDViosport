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
        public static object? SMAPICoreInstance { get; set; }
        private static CoreAnimation.CADisplayLink? _engineDisplayLink;
        private static int _tickLogCount = 0;
        private static UIView? _activeGameView;
        private static MethodInfo? _makeCurrentMethod;
        private static MethodInfo? _presentMethod;
        private static MethodInfo? _threadingRunMethod;
        private static int _directTickCount = 0;

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
                    System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

                    // Register SMAPI internal assembly resolver if available
                    try
                    {
                        var progType = smapiAsm.GetType("StardewModdingAPI.Program");
                        var resolveMethod = progType?.GetMethod("CurrentDomain_AssemblyResolve", BindingFlags.Static | BindingFlags.NonPublic);
                        if (resolveMethod != null)
                        {
                            var handler = (ResolveEventHandler)Delegate.CreateDelegate(typeof(ResolveEventHandler), resolveMethod);
                            AppDomain.CurrentDomain.AssemblyResolve += handler;
                            EngineLogger.Log("[GameHost] Registered SMAPI CurrentDomain_AssemblyResolve.");
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Could not hook SMAPI resolver: {ex.Message}");
                    }

                    // On iOS, MonoGame Game.Run() is asynchronous (DefaultRunBehavior = Asynchronous).
                    // When running via Program.Main(), SCore is instantiated inside a `using (var core = new SCore(...))`
                    // block which calls core.Dispose() as soon as Game.Run() returns, disposing the entire Game and GamePlatform!
                    // By instantiating SCore directly and keeping a static reference, we ensure SCore and Game remain alive forever.
                    var scoreType = smapiAsm.GetType("StardewModdingAPI.Framework.SCore");
                    if (scoreType != null)
                    {
                        EngineLogger.Log("[GameHost] Instantiating persistent SCore (non-disposing)...");
                        // public SCore(string modsPath, bool writeToConsole, bool? overrideDeveloperMode)
                        var core = Activator.CreateInstance(scoreType, new object?[] { ModsDir, false, (bool?)false });
                        SMAPICoreInstance = core;

                        // 1. Configure Settings before RunInteractively to prevent background console thread and update checks
                        try
                        {
                            var settingsField = scoreType.GetField("Settings", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var settings = settingsField?.GetValue(core);
                            if (settings != null)
                            {
                                var st = settings.GetType();
                                st.GetProperty("ListenForConsoleInput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(settings, false);
                                st.GetProperty("CheckForUpdates", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(settings, false);
                                st.GetProperty("CheckForBlacklistUpdates", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(settings, false);
                                EngineLogger.Log("[GameHost] Configured SCore.Settings: ListenForConsoleInput=false, CheckForUpdates=false.");
                            }
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Failed to configure SCore.Settings: {ex.Message}");
                        }

                        // 2. Install NonDisposingStreamWriter on SMAPI LogFileManager before launch
                        ReviveSMAPILogFile(core);

                        var runMethod = scoreType.GetMethod("RunInteractively", BindingFlags.Public | BindingFlags.Instance);
                        runMethod?.Invoke(core, null);
                        EngineLogger.Log("[GameHost] SMAPI SCore.RunInteractively launched successfully.");

                        // 3. Post-RunInteractively: Neutralize SGameRunner.OnGameExiting and revive SCore state
                        try
                        {
                            var gameField = scoreType.GetField("Game", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var game = gameField?.GetValue(core);
                            if (game != null)
                            {
                                var onExitingField = game.GetType().GetField("OnGameExiting", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                                if (onExitingField != null)
                                {
                                    Action noopExit = () =>
                                    {
                                        EngineLogger.Log("[GameHost] SGameRunner.OnGameExiting intercepted and suppressed.");
                                    };
                                    onExitingField.SetValue(game, noopExit);
                                    EngineLogger.Log("[GameHost] Replaced SGameRunner.OnGameExiting with no-op handler.");
                                }
                            }

                            var isDispField = scoreType.GetField("IsDisposed", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            isDispField?.SetValue(core, false);

                            var isRunningField = scoreType.GetField("IsGameRunning", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            isRunningField?.SetValue(core, true);
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Post-RunInteractively patch warning: {ex.Message}");
                        }

                        ReviveSMAPILogFile(core);
                    }
                    else
                    {
                        EngineLogger.LogWarning("[GameHost] SCore type not found, falling back to EntryPoint...");
                        var entry = smapiAsm.EntryPoint;
                        if (entry != null)
                        {
                            object?[] invokeArgs = entry.GetParameters().Length > 0 ? new object?[] { smapiArgs } : Array.Empty<object>();
                            entry.Invoke(null, invokeArgs);
                        }
                    }

                    LinkGameWindowToScene();
                    AttachTouchOverlayToGameRunner();
                    UIApplication.SharedApplication.BeginInvokeOnMainThread(LinkGameWindowToScene);
                    NSTimer.CreateScheduledTimer(0.25, false, _ => LinkGameWindowToScene());
                    NSTimer.CreateScheduledTimer(1.0, false, _ => LinkGameWindowToScene());
                    NSTimer.CreateScheduledTimer(2.5, false, _ => LinkGameWindowToScene());
                    NSTimer.CreateScheduledTimer(5.0, false, _ => LinkGameWindowToScene());
                    return;
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
                ReviveSMAPILogFile(SMAPICoreInstance);

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

                // 1. Locate GamePlatform
                object? plat = null;
                if (runner != null)
                {
                    // Check Services dictionary
                    try
                    {
                        var svcProp = runner.GetType().GetProperty("Services", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                   ?? typeof(Game).GetProperty("Services");
                        var svcContainer = svcProp?.GetValue(runner) as GameServiceContainer;
                        if (svcContainer != null)
                        {
                            var dictField = typeof(GameServiceContainer).GetField("services", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (dictField?.GetValue(svcContainer) is System.Collections.IDictionary dict)
                            {
                                foreach (var k in dict.Keys)
                                {
                                    var v = dict[k];
                                    if (v != null && v.GetType().Name.Contains("Platform"))
                                    {
                                        plat = v;
                                        EngineLogger.Log($"[GameHost] Found GamePlatform via Services: {v.GetType().FullName}");
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // Check fields on runner
                    if (plat == null)
                    {
                        for (Type? currType = runner.GetType(); currType != null; currType = currType.BaseType)
                        {
                            foreach (var f in currType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                            {
                                if (f.FieldType.Name.Contains("Platform") || f.Name.ToLower().Contains("platform"))
                                {
                                    try
                                    {
                                        var val = f.GetValue(runner);
                                        if (val != null)
                                        {
                                            plat = val;
                                            EngineLogger.Log($"[GameHost] Found GamePlatform on {currType.Name}.{f.Name} ({val.GetType().FullName})");
                                            break;
                                        }
                                    }
                                    catch { }
                                }
                            }
                            if (plat != null) break;
                        }
                    }
                }

                // 2. Locate UIWindow and UIViewController
                UIWindow? window = null;
                UIViewController? vc = null;

                if (runner != null)
                {
                    window = runner.Services.GetService(typeof(UIWindow)) as UIWindow;
                    vc = runner.Services.GetService(typeof(UIViewController)) as UIViewController;
                }

                if (plat != null)
                {
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        if (window == null)
                        {
                            var wf = t.GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (wf?.GetValue(plat) is UIWindow w) window = w;
                        }
                        if (vc == null)
                        {
                            var vcf = t.GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (vcf?.GetValue(plat) is UIViewController v) vc = v;
                        }
                    }
                }

                if (window == null)
                {
                    foreach (var w in UIApplication.SharedApplication.Windows)
                    {
                        if (w != null)
                        {
                            window = w;
                            if (vc == null) vc = w.RootViewController;
                            break;
                        }
                    }
                }

                if (vc == null && window?.RootViewController != null)
                {
                    vc = window.RootViewController;
                }

                // Discover _activeGameView if not yet cached
                if (_activeGameView == null || _activeGameView.Handle == IntPtr.Zero)
                {
                    if (window?.Subviews != null)
                    {
                        foreach (var sv in window.Subviews)
                        {
                            if (sv != null && (sv.GetType().Name.Contains("GameView") || sv.GetType().FullName.Contains("iOSGameView")))
                            {
                                _activeGameView = sv;
                                EngineLogger.Log($"[GameHost] Discovered _activeGameView in window.Subviews: {sv.GetType().FullName}");
                                break;
                            }
                        }
                    }
                    if (_activeGameView == null && vc?.View != null)
                    {
                        if (vc.View.GetType().Name.Contains("GameView") || vc.View.GetType().FullName.Contains("iOSGameView"))
                        {
                            _activeGameView = vc.View;
                            EngineLogger.Log($"[GameHost] Discovered _activeGameView in vc.View: {vc.View.GetType().FullName}");
                        }
                    }
                }

                EngineLogger.Log($"[GameHost] Status: Window={window != null} (Bounds={window?.Bounds}), VC={vc != null}, Platform={plat != null}, GameView={_activeGameView != null}");

                // 3. Determine scene and screen geometry
                var activeScene = GetActiveWindowScene();
                CGRect screenBounds = CGRect.Empty;
                try
                {
                    if (activeScene?.Screen != null && !activeScene.Screen.Bounds.IsEmpty)
                        screenBounds = activeScene.Screen.Bounds;
                    else if (activeScene?.CoordinateSpace != null && !activeScene.CoordinateSpace.Bounds.IsEmpty)
                        screenBounds = activeScene.CoordinateSpace.Bounds;
                    else if (!UIScreen.MainScreen.Bounds.IsEmpty)
                        screenBounds = UIScreen.MainScreen.Bounds;
                }
                catch { }

                nfloat screenW = (nfloat)Math.Max((double)screenBounds.Width, (double)screenBounds.Height);
                nfloat screenH = (nfloat)Math.Min((double)screenBounds.Width, (double)screenBounds.Height);
                if (screenW <= 0 || screenH <= 0)
                {
                    screenW = 896;
                    screenH = 414;
                }
                var landscapeFrame = new CGRect(0, 0, screenW, screenH);
                EngineLogger.Log($"[GameHost] Target landscape frame: {landscapeFrame.Width}x{landscapeFrame.Height}");

                // 4. Scene attachment & Window Recreation
                bool windowNeedsRecreation = window == null || window.Bounds.IsEmpty || window.Bounds.Width <= 0 || window.Bounds.Height <= 0;
                if (activeScene != null && windowNeedsRecreation)
                {
                    EngineLogger.Log("[GameHost] Recreating UIWindow bound to active UIWindowScene...");
                    try
                    {
                        var newWindow = new UIWindow(activeScene);
                        newWindow.Frame = landscapeFrame;
                        newWindow.Bounds = landscapeFrame;
                        newWindow.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                        newWindow.Hidden = false;

                        if (vc != null)
                        {
                            try { vc.DangerousRetain(); } catch { }
                            newWindow.RootViewController = vc;
                            if (vc.View != null)
                            {
                                try { vc.View.DangerousRetain(); } catch { }
                                vc.View.Frame = landscapeFrame;
                                vc.View.Bounds = landscapeFrame;
                                vc.View.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                                vc.View.Hidden = false;
                                newWindow.AddSubview(vc.View);
                            }
                        }

                        newWindow.MakeKeyAndVisible();
                        window = newWindow;

                        // Update runner services
                        if (runner != null)
                        {
                            try { runner.Services.RemoveService(typeof(UIWindow)); } catch { }
                            runner.Services.AddService(typeof(UIWindow), newWindow);
                        }

                        // Update plat._mainWindow
                        if (plat != null)
                        {
                            for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                            {
                                var wf = t.GetField("_mainWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                                if (wf != null)
                                {
                                    wf.SetValue(plat, newWindow);
                                    EngineLogger.Log($"[GameHost] Updated {t.Name}._mainWindow to new UIWindow.");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogError($"[GameHost] Failed to recreate UIWindow: {ex}");
                    }
                }
                else if (window != null)
                {
                    if (activeScene != null && window.WindowScene != activeScene)
                    {
                        window.WindowScene = activeScene;
                        EngineLogger.Log($"[GameHost] Assigned window.WindowScene to {activeScene.Description}");
                    }
                    window.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                    window.Frame = landscapeFrame;
                    window.Bounds = landscapeFrame;
                    window.Hidden = false;
                    window.MakeKeyAndVisible();

                    if (vc != null)
                    {
                        try { vc.DangerousRetain(); } catch { }
                        if (window.RootViewController == null)
                        {
                            window.RootViewController = vc;
                        }
                        if (vc.View != null)
                        {
                            try { vc.View.DangerousRetain(); } catch { }
                            vc.View.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                            vc.View.Frame = landscapeFrame;
                            vc.View.Bounds = landscapeFrame;
                            vc.View.Hidden = false;
                            if (!window.Subviews.Contains(vc.View))
                            {
                                window.AddSubview(vc.View);
                            }
                        }
                    }
                }

                // Ensure _activeGameView is in window hierarchy and brought to front
                if (window != null && _activeGameView != null)
                {
                    try
                    {
                        _activeGameView.DangerousRetain();
                        _activeGameView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                        _activeGameView.Frame = landscapeFrame;
                        _activeGameView.Bounds = landscapeFrame;
                        _activeGameView.Hidden = false;
                        _activeGameView.Opaque = true;
                        if (!window.Subviews.Contains(_activeGameView))
                        {
                            window.AddSubview(_activeGameView);
                        }
                        window.BringSubviewToFront(_activeGameView);
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] _activeGameView window attachment warning: {ex.Message}");
                    }
                }

                if (window != null && UIApplication.SharedApplication.Delegate is AppDelegate appDelegate)
                {
                    if (appDelegate.Window != window)
                    {
                        appDelegate.Window = window;
                        EngineLogger.Log("[GameHost] Set AppDelegate.Window to UIWindow.");
                    }
                }

                // 5. Connect and repair Game <-> GamePlatform bidirectional link & IsActive
                Game? targetGame = runner;

                if (plat != null)
                {
                    // Check what Game the platform currently points to
                    var gProp = plat.GetType().GetProperty("Game", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                             ?? plat.GetType().BaseType?.GetProperty("Game", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var pGame = gProp?.GetValue(plat) as Game;
                    if (pGame != null) targetGame = pGame;

                    if (targetGame != null)
                    {
                        // Ensure plat.<Game>k__BackingField points to targetGame
                        for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                        {
                            var gf = t.GetField("<Game>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (gf != null)
                            {
                                gf.SetValue(plat, targetGame);
                                EngineLogger.Log($"[GameHost] Set {t.Name}.<Game> = targetGame ({targetGame.GetType().FullName})");
                            }
                        }
                    }

                    // Reset disposed = false on GamePlatform
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var dispF = t.GetField("disposed", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (dispF != null)
                        {
                            dispF.SetValue(plat, false);
                            EngineLogger.Log($"[GameHost] Reset {t.Name}.disposed = false on platform");
                        }
                    }

                    // Set _isActive = true on GamePlatform
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var actField = t.GetField("_isActive", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (actField != null)
                        {
                            actField.SetValue(plat, true);
                            EngineLogger.Log($"[GameHost] Set {t.Name}._isActive = true on platform");
                        }
                    }

                    // Inspect and repair _viewController on plat
                    FieldInfo? vcf = null;
                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        vcf = t.GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (vcf != null) break;
                    }

                    object? platVC = vcf?.GetValue(plat);
                    EngineLogger.Log($"[GameHost] Current plat._viewController: {platVC?.GetType().FullName ?? "null"}");

                    if (platVC == null)
                    {
                        if (vc != null && vcf != null && vcf.FieldType.IsInstanceOfType(vc))
                        {
                            vcf.SetValue(plat, vc);
                            platVC = vc;
                            EngineLogger.Log($"[GameHost] Assigned vc to plat._viewController ({vc.GetType().FullName})");
                        }
                        else if (vcf != null)
                        {
                            try
                            {
                                var newVC = Activator.CreateInstance(vcf.FieldType, new object[] { plat });
                                if (newVC != null)
                                {
                                    vcf.SetValue(plat, newVC);
                                    platVC = newVC;
                                    EngineLogger.Log($"[GameHost] Created and assigned new {vcf.FieldType.FullName} to plat._viewController");
                                    if (newVC is UIViewController createdUIVC && window != null)
                                    {
                                        window.RootViewController = createdUIVC;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                EngineLogger.LogWarning($"[GameHost] Could not instantiate {vcf.FieldType.FullName}: {ex.Message}");
                            }
                        }
                    }

                    // If platVC is a UIViewController, make sure its View points to _activeGameView
                    if (platVC is UIViewController pvc)
                    {
                        try
                        {
                            if (_activeGameView != null && pvc.View != _activeGameView)
                            {
                                pvc.View = _activeGameView;
                                EngineLogger.Log("[GameHost] Connected _activeGameView to plat._viewController.View");
                            }
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Set plat._viewController.View warning: {ex.Message}");
                        }

                        // Also synchronize iOSGameWindow._viewController if present
                        try
                        {
                            var winField = plat.GetType().GetField("_window", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                        ?? plat.GetType().BaseType?.GetField("_window", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            var pWindow = winField?.GetValue(plat);
                            if (pWindow != null)
                            {
                                var wVcf = pWindow.GetType().GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                                if (wVcf != null && wVcf.GetValue(pWindow) == null)
                                {
                                    wVcf.SetValue(pWindow, pvc);
                                    EngineLogger.Log($"[GameHost] Synchronized iOSGameWindow._viewController to {pvc.GetType().FullName}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            EngineLogger.LogWarning($"[GameHost] Set iOSGameWindow._viewController warning: {ex.Message}");
                        }
                    }

                    try
                    {
                        var didBecomeAct = plat.GetType().GetMethod("Application_DidBecomeActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        didBecomeAct?.Invoke(plat, new object?[] { null });
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] Application_DidBecomeActive error: {ex.Message}");
                    }
                }

                // Repair Platform field on targetGame and runner
                var gamesToFix = new System.Collections.Generic.HashSet<Game>();
                if (targetGame != null) gamesToFix.Add(targetGame);
                if (runner != null) gamesToFix.Add(runner);

                foreach (var g in gamesToFix)
                {
                    for (Type? t = g.GetType(); t != null; t = t.BaseType)
                    {
                        var pf = t.GetField("Platform", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (pf != null && plat != null)
                        {
                            pf.SetValue(g, plat);
                            EngineLogger.Log($"[GameHost] Successfully assigned {t.Name}.Platform = plat on {g.GetType().Name}");
                        }

                        var dispF = t.GetField("_isDisposed", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (dispF != null)
                        {
                            dispF.SetValue(g, false);
                            EngineLogger.Log($"[GameHost] Reset {t.Name}._isDisposed = false on {g.GetType().Name}");
                        }
                    }

                    // Also ensure Game._instance is set
                    try
                    {
                        var instField = typeof(Game).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
                        instField?.SetValue(null, g);
                    }
                    catch { }

                    bool act = false;
                    try { act = g.IsActive; } catch (Exception ex) { EngineLogger.LogWarning($"[GameHost] {g.GetType().Name}.IsActive threw: {ex.Message}"); }
                    EngineLogger.Log($"[GameHost] Check: {g.GetType().Name}.IsActive = {act}");
                }

                // 6. Create modern CADisplayLink driving iOSGamePlatform.Tick() with Direct Render Fallback
                if (plat != null && _engineDisplayLink == null)
                {
                    var tickMethod = plat.GetType().GetMethod("Tick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    EngineLogger.Log("[GameHost] Creating modern CADisplayLink for game tick pipeline...");
                    _engineDisplayLink = CoreAnimation.CADisplayLink.Create(() =>
                    {
                        ReviveSMAPILogFile(SMAPICoreInstance);
                        ReviveGraphicsDeviceAndInstances(runner, plat, null, _activeGameView);
                        bool directFallback = false;
                        try
                        {
                            if (_tickLogCount < 5)
                            {
                                _tickLogCount++;
                                EngineLogger.Log($"[GameHost] CADisplayLink tick #{_tickLogCount} executing...");
                            }
                            if (tickMethod != null)
                            {
                                tickMethod.Invoke(plat, null);
                            }
                            else
                            {
                                directFallback = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            directFallback = true;
                            if (_tickLogCount < 10)
                            {
                                _tickLogCount++;
                                EngineLogger.LogWarning($"[GameHost] plat.Tick() threw: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}. Engaging direct render pipeline.");
                            }
                        }

                        if (directFallback)
                        {
                            ExecuteDirectGameTick(runner, plat, _activeGameView);
                        }
                    });
                    try { _engineDisplayLink.PreferredFramesPerSecond = 60; } catch { }
                    _engineDisplayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
                    _engineDisplayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Default);
                    _engineDisplayLink.Paused = false;
                    EngineLogger.Log("[GameHost] Modern CADisplayLink registered and unpaused.");

                    for (Type? t = plat.GetType(); t != null; t = t.BaseType)
                    {
                        var dlf = t.GetField("_displayLink", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (dlf != null)
                        {
                            dlf.SetValue(plat, _engineDisplayLink);
                            EngineLogger.Log($"[GameHost] Assigned _engineDisplayLink to {t.Name}._displayLink");
                        }
                    }
                }

                // 7. Revive GraphicsDevice, iOSGameWindow, and Game1 instances
                ReviveGraphicsDeviceAndInstances(runner, plat, vc, _activeGameView);

                // 8. Force View Layout & OpenGL Framebuffer Allocation
                if (window != null)
                {
                    try
                    {
                        window.SetNeedsLayout();
                        window.LayoutIfNeeded();
                        var subviews = window.Subviews;
                        EngineLogger.Log($"[GameHost] window.Subviews count: {subviews?.Length ?? 0}");
                        if (subviews != null)
                        {
                            foreach (var sv in subviews)
                            {
                                try
                                {
                                    sv.DangerousRetain();
                                    sv.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                                    sv.Frame = landscapeFrame;
                                    sv.Bounds = landscapeFrame;
                                    sv.Hidden = false;
                                    sv.SetNeedsLayout();
                                    sv.LayoutIfNeeded();
                                    var lsMethod = sv.GetType().GetMethod("LayoutSubviews", BindingFlags.Public | BindingFlags.Instance);
                                    lsMethod?.Invoke(sv, null);
                                    EngineLogger.Log($"[GameHost] Subview {sv.GetType().Name}: Frame={sv.Frame}, Hidden={sv.Hidden}");
                                }
                                catch (Exception ex)
                                {
                                    EngineLogger.LogWarning($"[GameHost] Subview layout error: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        EngineLogger.LogWarning($"[GameHost] LayoutSubviews error: {ex.Message}");
                    }

                    EngineLogger.Log($"[GameHost] Window: Bounds={window.Bounds}, Frame={window.Frame}, Hidden={window.Hidden}, Key={window.IsKeyWindow}");
                }

                try
                {
                    var gd = runner?.GraphicsDevice;
                    if (gd != null)
                    {
                        EngineLogger.Log($"[GameHost] GraphicsDevice: Viewport={gd.Viewport.Width}x{gd.Viewport.Height}, BackBuffer={gd.PresentationParameters.BackBufferWidth}x{gd.PresentationParameters.BackBufferHeight}");
                    }
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[GameHost] GraphicsDevice query: {ex.Message}");
                }

                EngineLogger.Log("[GameHost] MonoGame UIWindow and CADisplayLink successfully linked.");
            }
            catch (Exception ex)
            {
                EngineLogger.LogError($"[GameHost] LinkGameWindowToScene failed: {ex}");
            }
        }

        private static void ExecuteDirectGameTick(Game? runner, object? plat, UIView? gameView)
        {
            try
            {
                if (runner == null) return;

                // 1. Ensure runner is marked active
                if (!runner.IsActive)
                {
                    for (Type? t = runner.GetType(); t != null; t = t.BaseType)
                    {
                        var actField = t.GetField("_isActive", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        actField?.SetValue(runner, true);
                    }
                }

                // If gameView is null, look for it in key window
                if (gameView == null || gameView.Handle == IntPtr.Zero)
                {
                    var kw = UIApplication.SharedApplication.KeyWindow ?? UIApplication.SharedApplication.Windows.FirstOrDefault(w => w != null);
                    if (kw?.Subviews != null)
                    {
                        foreach (var sv in kw.Subviews)
                        {
                            if (sv != null && (sv.GetType().Name.Contains("GameView") || sv.GetType().FullName.Contains("iOSGameView")))
                            {
                                _activeGameView = sv;
                                gameView = sv;
                                break;
                            }
                        }
                    }
                }

                // Revive graphics device, window viewController, and instance options
                ReviveGraphicsDeviceAndInstances(runner, plat, null, gameView);

                // 2. MakeCurrent on iOSGameView
                if (gameView != null && gameView.Handle != IntPtr.Zero)
                {
                    if (_makeCurrentMethod == null)
                    {
                        _makeCurrentMethod = gameView.GetType().GetMethod("MakeCurrent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    try
                    {
                        _makeCurrentMethod?.Invoke(gameView, null);
                    }
                    catch (Exception ex)
                    {
                        if (_directTickCount < 5) EngineLogger.LogWarning($"[GameHost] Direct MakeCurrent error: {ex.Message}");
                    }
                }

                // 3. Tick Game (Runs SMAPI + Stardew Valley Update & Draw)
                ReviveSMAPILogFile(SMAPICoreInstance);
                if (runner.GraphicsDevice != null)
                {
                    runner.Tick();
                }
                else
                {
                    if (_directTickCount < 5) EngineLogger.LogWarning("[GameHost] Skipping runner.Tick() because GraphicsDevice is null.");
                }

                // 4. Threading.Run
                if (_threadingRunMethod == null)
                {
                    var threadingType = typeof(Game).Assembly.GetType("Microsoft.Xna.Framework.Threading");
                    _threadingRunMethod = threadingType?.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                }
                try
                {
                    _threadingRunMethod?.Invoke(null, null);
                }
                catch { }

                // 5. Present GraphicsDevice
                try
                {
                    runner.GraphicsDevice?.Present();
                }
                catch (Exception ex)
                {
                    if (_directTickCount < 5) EngineLogger.LogWarning($"[GameHost] Direct GraphicsDevice.Present error: {ex.Message}");
                }

                // 6. Present iOSGameView (SwapBuffers)
                if (gameView != null && gameView.Handle != IntPtr.Zero)
                {
                    if (_presentMethod == null)
                    {
                        _presentMethod = gameView.GetType().GetMethod("Present", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    try
                    {
                        _presentMethod?.Invoke(gameView, null);
                    }
                    catch (Exception ex)
                    {
                        if (_directTickCount < 5) EngineLogger.LogWarning($"[GameHost] Direct iOSGameView.Present error: {ex.Message}");
                    }
                }

                if (_directTickCount < 5)
                {
                    _directTickCount++;
                    EngineLogger.Log($"[GameHost] Direct tick pipeline #{_directTickCount} executed successfully! (GD={runner.GraphicsDevice != null}, View={gameView != null})");
                }
            }
            catch (Exception ex)
            {
                if (_directTickCount < 10)
                {
                    _directTickCount++;
                    EngineLogger.LogError($"[GameHost] Direct tick pipeline error: {ex}");
                }
            }
        }

        public static void ReviveGraphicsDeviceAndInstances(Game? runner, object? plat, UIViewController? vc, UIView? gameView)
        {
            if (runner == null) return;
            try
            {
                // 1. Repair and synchronize iOSGameWindow._viewController and TouchPanel/Mouse PrimaryWindow
                Microsoft.Xna.Framework.GameWindow? xnaWindow = null;
                if (plat != null)
                {
                    try
                    {
                        var winField = plat.GetType().GetField("_window", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                    ?? plat.GetType().BaseType?.GetField("_window", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        var pWindow = winField?.GetValue(plat);
                        if (pWindow != null)
                        {
                            xnaWindow = pWindow as Microsoft.Xna.Framework.GameWindow;
                            var wVcf = pWindow.GetType().GetField("_viewController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (wVcf != null && (wVcf.GetValue(pWindow) == null || (vc != null && wVcf.GetValue(pWindow) != vc)))
                            {
                                if (vc != null)
                                {
                                    wVcf.SetValue(pWindow, vc);
                                    EngineLogger.Log($"[GameHost] Synchronized iOSGameWindow._viewController to {vc.GetType().FullName}");
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (xnaWindow == null && runner != null)
                {
                    try
                    {
                        xnaWindow = runner.Window as Microsoft.Xna.Framework.GameWindow;
                    }
                    catch { }
                }

                // Ensure TouchPanel.PrimaryWindow and Mouse.PrimaryWindow are initialized
                if (xnaWindow != null)
                {
                    try
                    {
                        var tpType = typeof(Microsoft.Xna.Framework.Input.Touch.TouchPanel);
                        var tpPwField = tpType.GetField("PrimaryWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                        if (tpPwField != null && tpPwField.GetValue(null) == null)
                        {
                            tpPwField.SetValue(null, xnaWindow);
                            EngineLogger.Log($"[GameHost] Set TouchPanel.PrimaryWindow = {xnaWindow.GetType().FullName}");
                        }

                        var mouseType = typeof(Microsoft.Xna.Framework.Input.Mouse);
                        var mPwField = mouseType.GetField("PrimaryWindow", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                        if (mPwField != null && mPwField.GetValue(null) == null)
                        {
                            mPwField.SetValue(null, xnaWindow);
                            EngineLogger.Log($"[GameHost] Set Mouse.PrimaryWindow = {xnaWindow.GetType().FullName}");
                        }

                        // Verify xnaWindow.TouchPanelState is present
                        var winType = xnaWindow.GetType();
                        FieldInfo? tpsField = null;
                        for (Type? t = winType; t != null; t = t.BaseType)
                        {
                            tpsField = t.GetField("TouchPanelState", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            if (tpsField != null) break;
                        }

                        if (tpsField != null && tpsField.GetValue(xnaWindow) == null)
                        {
                            var tpsType = tpsField.FieldType;
                            var tpsCtor = tpsType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new Type[] { typeof(Microsoft.Xna.Framework.GameWindow) }, null);
                            if (tpsCtor != null)
                            {
                                var newTps = tpsCtor.Invoke(new object[] { xnaWindow });
                                tpsField.SetValue(xnaWindow, newTps);
                                EngineLogger.Log("[GameHost] Instantiated and assigned TouchPanelState to GameWindow.");
                            }
                        }
                    }
                    catch (Exception winEx)
                    {
                        EngineLogger.LogWarning($"[GameHost] TouchPanel/Mouse PrimaryWindow initialization warning: {winEx.Message}");
                    }
                }

                // 2. Discover and revive Game1.graphics (GraphicsDeviceManager)
                object? gdm = null;
                FieldInfo? sdvGraphicsField = null;
                Type? game1Type = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    game1Type = asm.GetType("StardewValley.Game1");
                    if (game1Type != null)
                    {
                        sdvGraphicsField = game1Type.GetField("graphics", BindingFlags.Static | BindingFlags.Public);
                        gdm = sdvGraphicsField?.GetValue(null);
                        if (gdm != null) break;
                    }
                }

                if (gdm != null)
                {
                    var gdmType = gdm.GetType();
                    var dispField = gdmType.GetField("disposed", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (dispField != null && (bool)(dispField.GetValue(gdm) ?? false))
                    {
                        dispField.SetValue(gdm, false);
                        EngineLogger.Log("[GameHost] Cleared GraphicsDeviceManager.disposed = false");
                    }

                    var gdField = gdmType.GetField("_graphicsDevice", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    var currentGD = gdField?.GetValue(gdm) as Microsoft.Xna.Framework.Graphics.GraphicsDevice;

                    if (currentGD == null)
                    {
                        EngineLogger.Log("[GameHost] GraphicsDeviceManager._graphicsDevice is null! Attempting revival...");
                        try
                        {
                            var createDevMethod = gdmType.GetMethod("CreateDevice", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                            createDevMethod?.Invoke(gdm, null);
                            currentGD = gdField?.GetValue(gdm) as Microsoft.Xna.Framework.Graphics.GraphicsDevice;
                            EngineLogger.Log($"[GameHost] CreateDevice result: GD={currentGD != null}");
                        }
                        catch (Exception cEx)
                        {
                            EngineLogger.LogWarning($"[GameHost] CreateDevice invocation warning: {cEx.Message}");
                        }

                        if (currentGD == null)
                        {
                            try
                            {
                                var applyChangesMethod = gdmType.GetMethod("ApplyChanges", BindingFlags.Public | BindingFlags.Instance);
                                applyChangesMethod?.Invoke(gdm, null);
                                currentGD = gdField?.GetValue(gdm) as Microsoft.Xna.Framework.Graphics.GraphicsDevice;
                                EngineLogger.Log($"[GameHost] ApplyChanges result: GD={currentGD != null}");
                            }
                            catch (Exception aEx)
                            {
                                EngineLogger.LogWarning($"[GameHost] ApplyChanges invocation warning: {aEx.Message}");
                            }
                        }

                        if (currentGD == null)
                        {
                            try
                            {
                                var adapter = Microsoft.Xna.Framework.Graphics.GraphicsAdapter.DefaultAdapter;
                                var profile = Microsoft.Xna.Framework.Graphics.GraphicsProfile.Reach;
                                Microsoft.Xna.Framework.Graphics.PresentationParameters? pp = null;

                                try
                                {
                                    pp = new Microsoft.Xna.Framework.Graphics.PresentationParameters();
                                }
                                catch (Exception ppEx)
                                {
                                    EngineLogger.LogWarning($"[GameHost] PresentationParameters ctor warning: {ppEx.Message}. Using uninitialized allocation.");
                                    try
                                    {
                                        pp = (Microsoft.Xna.Framework.Graphics.PresentationParameters)
                                            System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Microsoft.Xna.Framework.Graphics.PresentationParameters));
                                    }
                                    catch { }
                                }

                                if (pp != null)
                                {
                                    pp.BackBufferWidth = 896;
                                    pp.BackBufferHeight = 414;
                                    pp.BackBufferFormat = Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color;
                                    pp.DepthStencilFormat = Microsoft.Xna.Framework.Graphics.DepthFormat.Depth24Stencil8;
                                    pp.IsFullScreen = true;
                                    if (gameView != null && gameView.Handle != IntPtr.Zero)
                                    {
                                        pp.DeviceWindowHandle = gameView.Handle;
                                    }
                                    currentGD = new Microsoft.Xna.Framework.Graphics.GraphicsDevice(adapter, profile, pp);
                                    gdField?.SetValue(gdm, currentGD);
                                    EngineLogger.Log("[GameHost] Instantiated and assigned fresh GraphicsDevice to GraphicsDeviceManager.");
                                }
                            }
                            catch (Exception devEx)
                            {
                                EngineLogger.LogError($"[GameHost] Direct GraphicsDevice instantiation error: {devEx}");
                            }
                        }
                    }

                    // 3. Ensure runner's internal graphics services point to gdm
                    for (Type? t = runner.GetType(); t != null; t = t.BaseType)
                    {
                        var gdmField = t.GetField("_graphicsDeviceManager", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (gdmField != null && gdmField.GetValue(runner) == null)
                        {
                            gdmField.SetValue(runner, gdm);
                            EngineLogger.Log("[GameHost] Set runner._graphicsDeviceManager = Game1.graphics");
                        }
                        var gdsField = t.GetField("_graphicsDeviceService", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (gdsField != null && gdsField.GetValue(runner) == null)
                        {
                            gdsField.SetValue(runner, gdm);
                            EngineLogger.Log("[GameHost] Set runner._graphicsDeviceService = Game1.graphics");
                        }
                    }
                }

                // 4. Ensure gameInstances have valid localMultiplayerWindow, instanceOptions, and Game1.game1
                object? defaultOptions = null;
                Type? optionsType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    optionsType = asm.GetType("StardewValley.Options");
                    if (optionsType != null)
                    {
                        try
                        {
                            defaultOptions = Activator.CreateInstance(optionsType);
                            // Set zoomLevel = 1.0f, uiScale = 1.0f if present
                            var zoomProp = optionsType.GetProperty("zoomLevel", BindingFlags.Public | BindingFlags.Instance)
                                        ?? optionsType.GetProperty("ZoomLevel", BindingFlags.Public | BindingFlags.Instance);
                            zoomProp?.SetValue(defaultOptions, 1.0f);

                            var uiScaleProp = optionsType.GetProperty("uiScale", BindingFlags.Public | BindingFlags.Instance)
                                           ?? optionsType.GetProperty("UiScale", BindingFlags.Public | BindingFlags.Instance);
                            uiScaleProp?.SetValue(defaultOptions, 1.0f);
                            break;
                        }
                        catch { }
                    }
                }

                // Ensure Game1.options static property/field has a non-null Options instance
                if (game1Type != null && defaultOptions != null)
                {
                    try
                    {
                        var g1OptProp = game1Type.GetProperty("options", BindingFlags.Static | BindingFlags.Public)
                                     ?? game1Type.GetProperty("Options", BindingFlags.Static | BindingFlags.Public);
                        if (g1OptProp != null && g1OptProp.GetValue(null) == null)
                        {
                            g1OptProp.SetValue(null, defaultOptions);
                            EngineLogger.Log("[GameHost] Set Game1.options static property to defaultOptions.");
                        }

                        var g1OptField = game1Type.GetField("options", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                      ?? game1Type.GetField("Options", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (g1OptField != null && g1OptField.GetValue(null) == null)
                        {
                            g1OptField.SetValue(null, defaultOptions);
                            EngineLogger.Log("[GameHost] Set Game1.options static field to defaultOptions.");
                        }
                    }
                    catch (Exception optEx)
                    {
                        EngineLogger.LogWarning($"[GameHost] Game1.options initialization warning: {optEx.Message}");
                    }
                }

                var instancesField = runner.GetType().GetField("gameInstances", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                  ?? runner.GetType().BaseType?.GetField("gameInstances", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (instancesField?.GetValue(runner) is System.Collections.IList instances && instances.Count > 0)
                {
                    foreach (var inst in instances)
                    {
                        if (inst == null) continue;
                        var instType = inst.GetType();
                        var lmwField = instType.GetField("localMultiplayerWindow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (lmwField != null)
                        {
                            var rect = (Rectangle)(lmwField.GetValue(inst) ?? Rectangle.Empty);
                            if (rect.Width <= 0 || rect.Height <= 0)
                            {
                                lmwField.SetValue(inst, new Rectangle(0, 0, 896, 414));
                                EngineLogger.Log("[GameHost] Initialized instance localMultiplayerWindow to 896x414");
                            }
                        }

                        // Check instance field "instanceOptions"
                        FieldInfo? instOptField = null;
                        for (Type? t = instType; t != null; t = t.BaseType)
                        {
                            instOptField = t.GetField("instanceOptions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (instOptField != null) break;
                        }

                        if (instOptField != null && instOptField.GetValue(inst) == null)
                        {
                            try
                            {
                                var newOpt = defaultOptions ?? Activator.CreateInstance(instOptField.FieldType);
                                instOptField.SetValue(inst, newOpt);
                                EngineLogger.Log("[GameHost] Assigned instanceOptions to game instance.");
                            }
                            catch (Exception ioEx)
                            {
                                EngineLogger.LogWarning($"[GameHost] instanceOptions field set error: {ioEx.Message}");
                            }
                        }

                        // Check property "options" or "Options"
                        var optProp = instType.GetProperty("options", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                   ?? instType.GetProperty("Options", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (optProp != null && optProp.GetValue(inst) == null)
                        {
                            try
                            {
                                var optInstance = defaultOptions ?? Activator.CreateInstance(optProp.PropertyType);
                                optProp.SetValue(inst, optInstance);
                                EngineLogger.Log($"[GameHost] Initialized instance options: {optProp.PropertyType.Name}");
                            }
                            catch { }
                        }

                        // Ensure Game1.game1 static field points to inst if null
                        if (game1Type != null)
                        {
                            try
                            {
                                var g1Field = game1Type.GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                                if (g1Field != null && g1Field.GetValue(null) == null && game1Type.IsInstanceOfType(inst))
                                {
                                    g1Field.SetValue(null, inst);
                                    EngineLogger.Log("[GameHost] Set Game1.game1 = instance.");
                                }
                            }
                            catch { }
                        }

                        // Ensure instance _screen and _uiScreen render targets are allocated
                        var currentGD = runner.GraphicsDevice ?? (gdm as GraphicsDeviceManager)?.GraphicsDevice;
                        if (currentGD != null)
                        {
                            int targetW = currentGD.PresentationParameters.BackBufferWidth > 0 ? currentGD.PresentationParameters.BackBufferWidth : 1792;
                            int targetH = currentGD.PresentationParameters.BackBufferHeight > 0 ? currentGD.PresentationParameters.BackBufferHeight : 828;

                            var screenProp = instType.GetProperty("screen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var screenField = instType.GetField("_screen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if ((screenProp != null && screenProp.GetValue(inst) == null) || (screenField != null && screenField.GetValue(inst) == null))
                            {
                                try
                                {
                                    var rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(
                                        currentGD, targetW, targetH, false,
                                        Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color,
                                        Microsoft.Xna.Framework.Graphics.DepthFormat.None,
                                        0,
                                        Microsoft.Xna.Framework.Graphics.RenderTargetUsage.PreserveContents);
                                    rt.Name = "@Game1.screen";
                                    if (screenProp?.GetSetMethod(true) != null) screenProp.SetValue(inst, rt);
                                    else screenField?.SetValue(inst, rt);
                                    EngineLogger.Log($"[GameHost] Initialized instance screen render target ({targetW}x{targetH}).");
                                }
                                catch (Exception sEx)
                                {
                                    EngineLogger.LogWarning($"[GameHost] Failed to initialize instance screen: {sEx.Message}");
                                }
                            }

                            var uiScreenProp = instType.GetProperty("uiScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var uiScreenField = instType.GetField("_uiScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if ((uiScreenProp != null && uiScreenProp.GetValue(inst) == null) || (uiScreenField != null && uiScreenField.GetValue(inst) == null))
                            {
                                try
                                {
                                    var rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(
                                        currentGD, targetW, targetH, false,
                                        Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color,
                                        Microsoft.Xna.Framework.Graphics.DepthFormat.None,
                                        0,
                                        Microsoft.Xna.Framework.Graphics.RenderTargetUsage.PreserveContents);
                                    rt.Name = "@Game1.uiScreen";
                                    if (uiScreenProp?.GetSetMethod(true) != null) uiScreenProp.SetValue(inst, rt);
                                    else uiScreenField?.SetValue(inst, rt);
                                    EngineLogger.Log($"[GameHost] Initialized instance uiScreen render target ({targetW}x{targetH}).");
                                }
                                catch (Exception uiEx)
                                {
                                    EngineLogger.LogWarning($"[GameHost] Failed to initialize instance uiScreen: {uiEx.Message}");
                                }
                            }
                        }
                    }
                }

                // 5. Ensure Game1.spriteBatch is valid
                if (game1Type != null)
                {
                    try
                    {
                        var sbField = game1Type.GetField("spriteBatch", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (sbField != null && sbField.GetValue(null) == null)
                        {
                            var currentGD = runner.GraphicsDevice ?? (gdm as GraphicsDeviceManager)?.GraphicsDevice;
                            if (currentGD != null)
                            {
                                var sb = new Microsoft.Xna.Framework.Graphics.SpriteBatch(currentGD);
                                sbField.SetValue(null, sb);
                                EngineLogger.Log("[GameHost] Initialized Game1.spriteBatch static instance.");
                            }
                        }
                    }
                    catch (Exception sbEx)
                    {
                        EngineLogger.LogWarning($"[GameHost] Game1.spriteBatch initialization warning: {sbEx.Message}");
                    }
                }

                // 6. Ensure Game1.defaultDeviceViewport is valid
                if (game1Type != null)
                {
                    var ddvField = game1Type.GetField("defaultDeviceViewport", BindingFlags.Static | BindingFlags.Public);
                    if (ddvField != null)
                    {
                        var vp = (Microsoft.Xna.Framework.Graphics.Viewport)(ddvField.GetValue(null) ?? default(Microsoft.Xna.Framework.Graphics.Viewport));
                        if (vp.Width <= 0 || vp.Height <= 0)
                        {
                            ddvField.SetValue(null, new Microsoft.Xna.Framework.Graphics.Viewport(0, 0, 896, 414));
                            EngineLogger.Log("[GameHost] Initialized Game1.defaultDeviceViewport to 896x414");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] ReviveGraphicsDeviceAndInstances warning: {ex.Message}");
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

        public class NonDisposingStreamWriter : StreamWriter
        {
            public NonDisposingStreamWriter(Stream stream, System.Text.Encoding encoding) : base(stream, encoding) { }

            protected override void Dispose(bool disposing)
            {
                try { Flush(); } catch { }
                // Keep stream alive forever - do not call base.Dispose(disposing)
            }

            public override void Close()
            {
                try { Flush(); } catch { }
                // Keep stream alive forever - do not close
            }
        }

        public static void ReviveSMAPILogFile(object? core)
        {
            if (core == null) return;
            try
            {
                var coreType = core.GetType();
                var lmField = coreType.GetField("LogManager", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var lm = lmField?.GetValue(core);
                if (lm != null)
                {
                    var lfmField = lm.GetType().GetField("LogFile", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    var lfm = lfmField?.GetValue(lm);
                    if (lfm != null)
                    {
                        var streamField = lfm.GetType().GetField("Stream", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        var stream = streamField?.GetValue(lfm) as StreamWriter;
                        var pathProp = lfm.GetType().GetProperty("Path", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        string? logPath = pathProp?.GetValue(lfm) as string;

                        bool needsRevival = false;
                        if (stream == null || !(stream is NonDisposingStreamWriter))
                        {
                            needsRevival = true;
                        }
                        else
                        {
                            try
                            {
                                if (stream.BaseStream == null || !stream.BaseStream.CanWrite)
                                {
                                    needsRevival = true;
                                }
                            }
                            catch
                            {
                                needsRevival = true;
                            }
                        }

                        if (needsRevival && !string.IsNullOrEmpty(logPath))
                        {
                            var dir = Path.GetDirectoryName(logPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }
                            var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                            var newWriter = new NonDisposingStreamWriter(fs, System.Text.Encoding.UTF8) { AutoFlush = true };
                            streamField?.SetValue(lfm, newWriter);
                            EngineLogger.Log($"[GameHost] Successfully installed NonDisposingStreamWriter to '{logPath}'.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[GameHost] ReviveSMAPILogFile error: {ex.Message}");
            }
        }
    }
}
