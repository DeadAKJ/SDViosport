using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace SDViOSTouchControls
{
    public class ModEntry : Mod
    {
        private bool _initialized = false;
        private int _nativeWidth = 1792;
        private int _nativeHeight = 828;
        private static int _staticNativeWidth = 1792;
        private static int _staticNativeHeight = 828;

        private static int _assetEventCount = 0;

        public static IMonitor? ModMonitor { get; private set; }
        private static IClickableMenu? _lastTickMenu = null;

        public static string GetCompactStackTrace(int skipFrames = 2, int maxFrames = 6)
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(skipFrames, false);
                var frames = st.GetFrames();
                if (frames == null || frames.Length == 0) return "unknown";
                var list = new System.Collections.Generic.List<string>();
                for (int i = 0; i < Math.Min(frames.Length, maxFrames); i++)
                {
                    var m = frames[i].GetMethod();
                    if (m != null)
                    {
                        list.Add($"{m.DeclaringType?.Name}.{m.Name}");
                    }
                }
                return string.Join(" -> ", list);
            }
            catch
            {
                return "stack_err";
            }
        }

        public override void Entry(IModHelper helper)
        {
            ModMonitor = Monitor;
            TouchOverlaySettings.LogAction = msg => Monitor.Log(msg, LogLevel.Info);
            TouchVirtualPad.LogAction = msg => Monitor.Log(msg, LogLevel.Info);

            Monitor.Log("[SDViOSTouchControls] Initializing native touch controls mod...", LogLevel.Info);

            // 1. Determine native hardware display resolution
            DetermineNativeResolution();

            // 2. Neutralize legacy SDViOS.Input.TouchVirtualPad from fighting with input
            NeutralizeOldTouchVirtualPad();

            // 3. Apply Harmony patches to route Mouse, Keyboard, and Gamepad to TouchVirtualPad & force CaseInsensitivePaths
            ApplyHarmonyPatches();

            // Force native full-screen resolution immediately at mod startup
            EnsureNativeResolution(force: true);

            // 4. Disable raw MonoGame touch-to-mouse translation to stop cursor spazzing
            try
            {
                TouchPanel.EnableMouseTouchPoint = false;
                TouchPanel.EnableMouseGestures = false;
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Warning setting TouchPanel flags: {ex.Message}", LogLevel.Warn);
            }

            // 5. Wrap Game1.hooks with TouchDelegatingModHooks
            EnsureModHooksWrapped();

            // 6. Register SMAPI events
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.UpdateTicking += OnUpdateTicking; // Critical: Runs BEFORE Game1.Update()!
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.Display.RenderedStep += OnRenderedStep;
            helper.Events.Display.Rendered += OnRendered;
            helper.Events.Content.AssetReady += OnAssetReady;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;

            Monitor.Log("[SDViOSTouchControls] Mod initialized successfully.", LogLevel.Info);
        }

        private void DetermineNativeResolution()
        {
            try
            {
                var dm = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
                int maxDim = Math.Max(dm.Width, dm.Height);
                int minDim = Math.Min(dm.Width, dm.Height);

                if (maxDim > 1000 && minDim > 500)
                {
                    _nativeWidth = maxDim;
                    _nativeHeight = minDim;
                }
                else
                {
                    _nativeWidth = 1792;
                    _nativeHeight = 828;
                }

                _staticNativeWidth = _nativeWidth;
                _staticNativeHeight = _nativeHeight;

                Monitor.Log($"[SDViOSTouchControls] Detected hardware display resolution: {_nativeWidth}x{_nativeHeight}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _nativeWidth = 1792;
                _nativeHeight = 828;
                _staticNativeWidth = 1792;
                _staticNativeHeight = 828;
                Monitor.Log($"[SDViOSTouchControls] Using default iPhone landscape resolution: 1792x828 ({ex.Message})", LogLevel.Info);
            }
        }

        private void NeutralizeOldTouchVirtualPad()
        {
            try
            {
                var sdvIosAsm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SDViOS");
                if (sdvIosAsm != null)
                {
                    // 1. Remove TouchOverlay from Game1.game1.Components
                    try
                    {
                        var game = Game1.game1;
                        if (game != null)
                        {
                            for (int i = game.Components.Count - 1; i >= 0; i--)
                            {
                                var c = game.Components[i];
                                if (c != null && c.GetType().FullName != null && c.GetType().FullName.Contains("TouchOverlay"))
                                {
                                    if (c is GameComponent gc) gc.Enabled = false;
                                    if (c is DrawableGameComponent dgc) dgc.Visible = false;
                                    game.Components.RemoveAt(i);
                                    Monitor.Log($"[SDViOSTouchControls] Removed legacy {c.GetType().FullName} from Game.Components.", LogLevel.Info);
                                }
                            }
                        }
                    }
                    catch (Exception cex)
                    {
                        Monitor.Log($"[SDViOSTouchControls] Note removing legacy component: {cex.Message}", LogLevel.Trace);
                    }

                    // 2. Neutralize TouchOverlay & TouchVirtualPad
                    var overlayType = sdvIosAsm.GetType("SDViOS.Input.TouchOverlay");
                    var tvpType = sdvIosAsm.GetType("SDViOS.Input.TouchVirtualPad");

                    if (tvpType != null)
                    {
                        // Nullify static reflection hooks so legacy TVP never forwards input
                        tvpType.GetField("_reflectionInitialized", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, true);
                        tvpType.GetField("_inputInstance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_currentMouseStateField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_currentGamepadStateField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_currentKeyboardStateField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_lastCursorMotionWasMouseField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_mousePrimaryWindowField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_gameWindowMouseStateField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_gamepadControlsField", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);
                        tvpType.GetField("_setKeysMethod", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(null, null);

                        Monitor.Log("[SDViOSTouchControls] Successfully neutralized legacy SDViOS.Input.TouchVirtualPad fields.", LogLevel.Info);
                    }

                    // 3. Harmony detours to skip all legacy execution
                    try
                    {
                        var harmony = new Harmony("com.deadakj.sdviostouchcontrols");

                        if (overlayType != null)
                        {
                            var mOverlayUpdate = overlayType.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
                            if (mOverlayUpdate != null)
                                harmony.Patch(mOverlayUpdate, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));

                            var mOverlayDraw = overlayType.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance);
                            if (mOverlayDraw != null)
                                harmony.Patch(mOverlayDraw, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));
                        }

                        if (tvpType != null)
                        {
                            var forwardMethod = tvpType.GetMethod("ForwardInputToGame", BindingFlags.Public | BindingFlags.Instance);
                            if (forwardMethod != null)
                                harmony.Patch(forwardMethod, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));

                            var mTvpUpdate = tvpType.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
                            if (mTvpUpdate != null)
                                harmony.Patch(mTvpUpdate, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));

                            var mTvpDraw = tvpType.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance);
                            if (mTvpDraw != null)
                                harmony.Patch(mTvpDraw, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDisableLegacyForward), BindingFlags.Static | BindingFlags.NonPublic)));
                        }

                        Monitor.Log("[SDViOSTouchControls] Successfully applied Harmony detours to disable legacy TouchOverlay & TouchVirtualPad.", LogLevel.Info);
                    }
                    catch (Exception hex)
                    {
                        Monitor.Log($"[SDViOSTouchControls] Harmony legacy patch note: {hex.Message}", LogLevel.Trace);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error neutralizing legacy touch pad: {ex.Message}", LogLevel.Warn);
            }
        }

        private static bool PrefixDisableLegacyForward()
        {
            return false;
        }

        private void ApplyHarmonyPatches()
        {
            var harmony = new Harmony("com.deadakj.sdviostouchcontrols");

            // 1. Mouse Patches
            try
            {
                var mGetState0 = typeof(Mouse).GetMethod("GetState", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mGetState0 != null)
                {
                    harmony.Patch(mGetState0, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Mouse.GetState().", LogLevel.Info);
                }

                var mGetStateWin = typeof(Mouse).GetMethod("GetState", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameWindow) }, null);
                if (mGetStateWin != null)
                {
                    harmony.Patch(mGetStateWin, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Mouse.GetState(GameWindow).", LogLevel.Info);
                }

                var mPlatformGetState = typeof(Mouse).GetMethod("PlatformGetState", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(GameWindow) }, null);
                if (mPlatformGetState != null)
                {
                    harmony.Patch(mPlatformGetState, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Mouse.PlatformGetState(GameWindow).", LogLevel.Info);
                }

                var mInputStateGetMouseState = typeof(InputState).GetMethod("GetMouseState", BindingFlags.Public | BindingFlags.Instance);
                if (mInputStateGetMouseState != null)
                {
                    harmony.Patch(mInputStateGetMouseState, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched InputState.GetMouseState().", LogLevel.Info);
                }

                var mSetMousePosRaw = typeof(Game1).GetMethod("setMousePositionRaw", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);
                if (mSetMousePosRaw != null)
                {
                    harmony.Patch(mSetMousePosRaw, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetMousePositionRaw), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.setMousePositionRaw().", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error applying Harmony mouse patches: {ex.Message}", LogLevel.Error);
            }

            // 2. Keyboard Focus & State Patches
            try
            {
                var mHasKbFocus = typeof(Game1).GetMethod("HasKeyboardFocus", BindingFlags.Public | BindingFlags.Instance);
                if (mHasKbFocus != null)
                {
                    harmony.Patch(mHasKbFocus, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixTrue), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.HasKeyboardFocus -> true.", LogLevel.Info);
                }

                var mG1GetKb = typeof(Game1).GetMethod("GetKeyboardState", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mG1GetKb != null)
                {
                    harmony.Patch(mG1GetKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.GetKeyboardState().", LogLevel.Info);
                }

                var mInputGetKb = typeof(InputState).GetMethod("GetKeyboardState", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mInputGetKb != null)
                {
                    harmony.Patch(mInputGetKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched InputState.GetKeyboardState().", LogLevel.Info);
                }

                var mXnaKb = typeof(Microsoft.Xna.Framework.Input.Keyboard).GetMethod("GetState", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mXnaKb != null)
                {
                    harmony.Patch(mXnaKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Microsoft.Xna.Framework.Input.Keyboard.GetState().", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in keyboard patches: {ex.Message}", LogLevel.Error);
            }

            // 3. GamePad State Patches
            try
            {
                var mInputGetPad = typeof(InputState).GetMethod("GetGamePadState", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mInputGetPad != null)
                {
                    harmony.Patch(mInputGetPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetGamePadState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched InputState.GetGamePadState().", LogLevel.Info);
                }

                var mXnaPad = typeof(Microsoft.Xna.Framework.Input.GamePad).GetMethod("GetState", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(PlayerIndex) }, null);
                if (mXnaPad != null)
                {
                    harmony.Patch(mXnaPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetGamePadState), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Microsoft.Xna.Framework.Input.GamePad.GetState(PlayerIndex).", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in GamePad patches: {ex.Message}", LogLevel.Error);
            }

            // 4. ModHooks wrapping handled via TouchDelegatingModHooks (non-Harmony native subclass)
            EnsureModHooksWrapped();

            // 5. SMAPI SInputState Patches
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var sInputStateType = asm.GetType("StardewModdingAPI.Framework.Input.SInputState");
                    if (sInputStateType != null)
                    {
                        var sGetKb = sInputStateType.GetMethod("GetKeyboardState", BindingFlags.Public | BindingFlags.Instance);
                        if (sGetKb != null)
                            harmony.Patch(sGetKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sPropKb = sInputStateType.GetProperty("KeyboardState", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                        if (sPropKb != null)
                            harmony.Patch(sPropKb, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetKeyboardState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sGetMouse = sInputStateType.GetMethod("GetMouseState", BindingFlags.Public | BindingFlags.Instance);
                        if (sGetMouse != null)
                            harmony.Patch(sGetMouse, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sPropMouse = sInputStateType.GetProperty("MouseState", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                        if (sPropMouse != null)
                            harmony.Patch(sPropMouse, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMouseGetState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sGetPad = sInputStateType.GetMethod("GetGamePadState", BindingFlags.Public | BindingFlags.Instance);
                        if (sGetPad != null)
                            harmony.Patch(sGetPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetGamePadState), BindingFlags.Static | BindingFlags.NonPublic)));

                        var sPropPad = sInputStateType.GetProperty("GamePadState", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                        if (sPropPad != null)
                            harmony.Patch(sPropPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGetGamePadState), BindingFlags.Static | BindingFlags.NonPublic)));

                        Monitor.Log("[SDViOSTouchControls] Harmony patched SInputState (Mouse, Keyboard, GamePad).", LogLevel.Info);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching SInputState: {ex.Message}", LogLevel.Warn);
            }

            // 6. SMAPI Case-Insensitive Path Resolution
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var sConfigType = asm.GetType("StardewModdingAPI.Framework.Models.SConfig");
                    if (sConfigType != null)
                    {
                        var prop = sConfigType.GetProperty("UseCaseInsensitivePaths", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                        if (prop != null)
                        {
                            harmony.Patch(prop, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixTrue), BindingFlags.Static | BindingFlags.NonPublic)));
                            Monitor.Log("[SDViOSTouchControls] Harmony patched SConfig.UseCaseInsensitivePaths -> true.", LogLevel.Info);
                        }
                    }

                    var scoreType = asm.GetType("StardewModdingAPI.Framework.SCore");
                    if (scoreType != null)
                    {
                        var instProp = scoreType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        var scoreInst = instProp?.GetValue(null);
                        if (scoreInst != null)
                        {
                            var settingsProp = scoreType.GetProperty("Settings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var settings = settingsProp?.GetValue(scoreInst);
                            if (settings != null)
                            {
                                var prop = settings.GetType().GetProperty("UseCaseInsensitivePaths", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                prop?.SetValue(settings, true);
                                Monitor.Log("[SDViOSTouchControls] Successfully enforced SCore.Instance.Settings.UseCaseInsensitivePaths = true via reflection!", LogLevel.Info);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note setting case insensitive paths: {ex.Message}", LogLevel.Warn);
            }

            // 7. GraphicsDeviceManager and Window Resolution Patches
            try
            {
                var gdmType = typeof(GraphicsDeviceManager);
                var pWidthGet = gdmType.GetProperty("PreferredBackBufferWidth", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                if (pWidthGet != null)
                {
                    harmony.Patch(pWidthGet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixPreferredBackBufferWidth), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.get_PreferredBackBufferWidth -> native.", LogLevel.Info);
                }

                var pWidthSet = gdmType.GetProperty("PreferredBackBufferWidth", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod();
                if (pWidthSet != null)
                {
                    harmony.Patch(pWidthSet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetPreferredWidth), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.set_PreferredBackBufferWidth -> native.", LogLevel.Info);
                }

                var pHeightGet = gdmType.GetProperty("PreferredBackBufferHeight", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                if (pHeightGet != null)
                {
                    harmony.Patch(pHeightGet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixPreferredBackBufferHeight), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.get_PreferredBackBufferHeight -> native.", LogLevel.Info);
                }

                var pHeightSet = gdmType.GetProperty("PreferredBackBufferHeight", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod();
                if (pHeightSet != null)
                {
                    harmony.Patch(pHeightSet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetPreferredHeight), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.set_PreferredBackBufferHeight -> native.", LogLevel.Info);
                }

                var pFull = gdmType.GetProperty("IsFullScreen", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                if (pFull != null)
                {
                    harmony.Patch(pFull, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixFalseBool), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched GraphicsDeviceManager.IsFullScreen -> false.", LogLevel.Info);
                }

                var mSetWindowSize = typeof(Game1).GetMethod("SetWindowSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
                if (mSetWindowSize != null)
                {
                    harmony.Patch(mSetWindowSize, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixSetWindowSize), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.SetWindowSize -> locked to 1792x828.", LogLevel.Info);
                }

                var mClientChanged = typeof(Game1).GetMethod("Window_ClientSizeChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(object), typeof(EventArgs) }, null);
                if (mClientChanged != null)
                {
                    harmony.Patch(mClientChanged, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixWindowClientSizeChanged), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.Window_ClientSizeChanged.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching GraphicsDeviceManager: {ex.Message}", LogLevel.Warn);
            }

            // 8. Content Patcher Animations Exception Silencer
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var cpAnimType = asm.GetType("ContentPatcherAnimations.Mod");
                    if (cpAnimType != null)
                    {
                        var mUpdateTicked = cpAnimType.GetMethod("OnUpdateTicked", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mUpdateTicked != null)
                        {
                            harmony.Patch(mUpdateTicked, finalizer: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(FinalizerSilenceException), BindingFlags.Static | BindingFlags.NonPublic)));
                            Monitor.Log("[SDViOSTouchControls] Harmony patched ContentPatcherAnimations.Mod.OnUpdateTicked with exception silencer.", LogLevel.Info);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching ContentPatcherAnimations: {ex.Message}", LogLevel.Trace);
            }

            // 9. Memory Optimization: Defer Tilesheet Preloading & Skip Map Live-Reload during Save Load
            try
            {
                var mMapLoadTileSheets = typeof(xTile.Map).GetMethod("LoadTileSheets", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(xTile.Display.IDisplayDevice) }, null);
                if (mMapLoadTileSheets != null)
                {
                    harmony.Patch(mMapLoadTileSheets, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixMapLoadTileSheets), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched xTile.Map.LoadTileSheets -> deferred loading during save load.", LogLevel.Info);
                }

                var mDevLoadTileSheet = typeof(xTile.Display.XnaDisplayDevice).GetMethod("LoadTileSheet", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(xTile.Tiles.TileSheet) }, null);
                if (mDevLoadTileSheet != null)
                {
                    harmony.Patch(mDevLoadTileSheet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixDevLoadTileSheet), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched XnaDisplayDevice.LoadTileSheet -> deferred loading during save load.", LogLevel.Info);
                }

                // CoreAssetPropagator and ModContentManager
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var propType = asm.GetType("StardewModdingAPI.Metadata.CoreAssetPropagator");
                        if (propType != null)
                        {
                            var mPropMap = propType.GetMethod("PropagateMap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (mPropMap != null)
                            {
                                harmony.Patch(mPropMap, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixPropagateMap), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched CoreAssetPropagator.PropagateMap -> skip during save load.", LogLevel.Info);
                            }

                            var mPropTex = propType.GetMethod("PropagateTexture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (mPropTex != null)
                            {
                                harmony.Patch(mPropTex, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixPropagateMap), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched CoreAssetPropagator.PropagateTexture -> skip during save load.", LogLevel.Info);
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Note patching CoreAssetPropagator: {ex.Message}", LogLevel.Trace);
                }

                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var mcmType = asm.GetType("StardewModdingAPI.Framework.ContentManagers.ModContentManager");
                        if (mcmType != null)
                        {
                            var mTryGetTilesheet = mcmType.GetMethod("TryGetTilesheetAssetName", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (mTryGetTilesheet != null)
                            {
                                harmony.Patch(mTryGetTilesheet, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixTryGetTilesheetAssetName), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched ModContentManager.TryGetTilesheetAssetName -> skip eager texture load during save load.", LogLevel.Info);
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Note patching ModContentManager: {ex.Message}", LogLevel.Trace);
                }

                try
                {
                    foreach (var method in typeof(GameLocation).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (method.Name == "reloadMap")
                        {
                            try
                            {
                                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixLocationReloadMap), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched GameLocation.reloadMap -> defer non-essential maps during save load.", LogLevel.Info);
                            }
                            catch (Exception ex)
                            {
                                Monitor.Log($"[SDViOSTouchControls] Could not patch GameLocation.reloadMap: {ex.Message}", LogLevel.Trace);
                            }
                        }
                    }

                    var mGetMap = typeof(GameLocation).GetProperty("Map", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    if (mGetMap != null)
                    {
                        harmony.Patch(mGetMap, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixLocationGetMap), BindingFlags.Static | BindingFlags.NonPublic)));
                        Monitor.Log("[SDViOSTouchControls] Harmony patched GameLocation.get_Map -> on-demand load if deferred.", LogLevel.Info);
                    }

                    foreach (var method in typeof(GameLocation).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (method.Name == "loadMap")
                        {
                            try
                            {
                                harmony.Patch(method, postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixLocationLoadMap), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log("[SDViOSTouchControls] Harmony patched GameLocation.loadMap -> periodic GC during save load.", LogLevel.Info);
                            }
                            catch (Exception ex)
                            {
                                Monitor.Log($"[SDViOSTouchControls] Could not patch GameLocation.loadMap: {ex.Message}", LogLevel.Trace);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error patching GameLocation map hooks: {ex.Message}", LogLevel.Warn);
                }

                try
                {
                    foreach (var method in typeof(NPC).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (method.Name == "reloadSprite" || method.Name == "reloadData")
                        {
                            try
                            {
                                harmony.Patch(method, postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixNpcReloadSprite), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log($"[SDViOSTouchControls] Harmony patched NPC.{method.Name} -> periodic GC during save load.", LogLevel.Info);
                            }
                            catch (Exception ex)
                            {
                                Monitor.Log($"[SDViOSTouchControls] Could not patch NPC.{method.Name}: {ex.Message}", LogLevel.Trace);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error patching NPC load hooks: {ex.Message}", LogLevel.Warn);
                }

                try
                {
                    foreach (var method in typeof(Game1).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    {
                        if (method.Name == "newDayAfterFade" || method.Name == "_newDayAfterFade")
                        {
                            try
                            {
                                harmony.Patch(method,
                                    prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixNewDayAfterFade), BindingFlags.Static | BindingFlags.NonPublic)),
                                    postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixNewDayAfterFade), BindingFlags.Static | BindingFlags.NonPublic)));
                                Monitor.Log($"[SDViOSTouchControls] Harmony patched Game1.{method.Name} -> LOH compaction around new day transition.", LogLevel.Info);
                            }
                            catch (Exception ex)
                            {
                                Monitor.Log($"[SDViOSTouchControls] Could not patch Game1.{method.Name}: {ex.Message}", LogLevel.Trace);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error patching Game1.newDayAfterFade: {ex.Message}", LogLevel.Warn);
                }

                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name?.IndexOf("FarmTypeManager", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foreach (var type in asm.GetTypes())
                            {
                                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                                {
                                    if (m.Name == "ForageGeneration" || m.Name == "MonsterGeneration" || m.Name == "OreGeneration" || m.Name == "LargeObjectGeneration" || m.Name == "ProcessObjectExpiration")
                                    {
                                        try
                                        {
                                            harmony.Patch(m, postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixForceGC), BindingFlags.Static | BindingFlags.NonPublic)));
                                            Monitor.Log($"[SDViOSTouchControls] Harmony patched FarmTypeManager.{type.Name}.{m.Name} -> LOH compaction.", LogLevel.Info);
                                        }
                                        catch { }
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Note hooking FarmTypeManager: {ex.Message}", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error patching memory hooks: {ex.Message}", LogLevel.Error);
            }

            // 10. Diagnostics for Menu Lifecycle and Input Interception
            try
            {
                var mExitActiveMenu = typeof(Game1).GetMethod("exitActiveMenu", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (mExitActiveMenu != null)
                {
                    harmony.Patch(mExitActiveMenu, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixExitActiveMenu), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.exitActiveMenu for diagnostics.", LogLevel.Info);
                }

                var mExitThisMenu = typeof(IClickableMenu).GetMethod("exitThisMenu", BindingFlags.Public | BindingFlags.Instance);
                if (mExitThisMenu != null)
                {
                    harmony.Patch(mExitThisMenu, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixExitThisMenu), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.exitThisMenu for diagnostics.", LogLevel.Info);
                }

                var mExitNoSound = typeof(IClickableMenu).GetMethod("exitThisMenuNoSound", BindingFlags.Public | BindingFlags.Instance);
                if (mExitNoSound != null)
                {
                    harmony.Patch(mExitNoSound, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixExitThisMenuNoSound), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.exitThisMenuNoSound for diagnostics.", LogLevel.Info);
                }

                var mRecvLeftClick = typeof(IClickableMenu).GetMethod("receiveLeftClick", BindingFlags.Public | BindingFlags.Instance);
                if (mRecvLeftClick != null)
                {
                    harmony.Patch(mRecvLeftClick, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixReceiveLeftClick), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.receiveLeftClick for diagnostics.", LogLevel.Info);
                }

                var mRecvKey = typeof(IClickableMenu).GetMethod("receiveKeyPress", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Keys) }, null);
                if (mRecvKey != null)
                {
                    harmony.Patch(mRecvKey, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixReceiveKeyPress), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.receiveKeyPress for diagnostics.", LogLevel.Info);
                }

                var mRecvPad = typeof(IClickableMenu).GetMethod("receiveGamePadButton", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Buttons) }, null);
                if (mRecvPad != null)
                {
                    harmony.Patch(mRecvPad, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixReceiveGamePadButton), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched IClickableMenu.receiveGamePadButton for diagnostics.", LogLevel.Info);
                }

                var mUpdateActiveMenu = typeof(Game1).GetMethod("updateActiveMenu", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameTime) }, null);
                if (mUpdateActiveMenu != null)
                {
                    harmony.Patch(mUpdateActiveMenu,
                        prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixUpdateActiveMenu), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PostfixUpdateActiveMenu), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.updateActiveMenu for diagnostics.", LogLevel.Info);
                }

                // Memory: compact heap right before GameMenu allocates all its pages (prevents Jetsam kill on Y press)
                foreach (var ctor in typeof(GameMenu).GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixGameMenuCtor), BindingFlags.Static | BindingFlags.NonPublic)));
                        Monitor.Log($"[SDViOSTouchControls] Harmony patched GameMenu ctor ({ctor.GetParameters().Length} params) -> pre-allocation GC.", LogLevel.Info);
                    }
                    catch (Exception cex)
                    {
                        Monitor.Log($"[SDViOSTouchControls] Could not patch GameMenu ctor: {cex.Message}", LogLevel.Warn);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Note patching menu diagnostics: {ex.Message}", LogLevel.Warn);
            }
        }

        private static bool PrefixTryGetTilesheetAssetName(
            object __instance,
            string modRelativeMapFolder,
            string relativePath,
            ref IAssetName? assetName,
            ref string? error,
            ref bool __result)
        {
            if (Game1.gameMode == 6)
            {
                error = null;
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    assetName = null;
                    __result = true;
                    return false;
                }

                try
                {
                    // Normalize leading ./
                    string fileName = System.IO.Path.GetFileName(relativePath);
                    if (fileName.StartsWith('.'))
                    {
                        string? dir = System.IO.Path.GetDirectoryName(relativePath);
                        relativePath = System.IO.Path.Combine(dir ?? "", fileName.TrimStart('.'));
                    }

                    var mcmType = __instance.GetType();

                    // 1. If it's a mod-relative file (does not start with or contain ..)
                    if (!relativePath.StartsWith("..") && !relativePath.Contains(".."))
                    {
                        string modPath = System.IO.Path.Combine(modRelativeMapFolder ?? "", relativePath);
                        var mGetModFile = mcmType.GetMethod("GetModFile", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (mGetModFile != null)
                        {
                            var mGeneric = mGetModFile.MakeGenericMethod(typeof(Texture2D));
                            var fileInfo = mGeneric.Invoke(__instance, new object[] { modPath }) as System.IO.FileInfo;
                            if (fileInfo != null && fileInfo.Exists)
                            {
                                var mGetInternal = mcmType.GetMethod("GetInternalAssetKey", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                assetName = mGetInternal?.Invoke(__instance, new object[] { modPath }) as IAssetName;
                                __result = true;
                                return false;
                            }
                        }
                    }

                    // 2. Otherwise it's a content asset (e.g. Maps/ZCCC_Entrance_Tilesheet)
                    // Resolve asset name WITHOUT loading the full Texture2D through GameContentManager!
                    var mGetContentKey = mcmType.GetMethod("GetContentKeyForTilesheetImageSource", BindingFlags.Instance | BindingFlags.NonPublic);
                    string? contentKey = mGetContentKey?.Invoke(__instance, new object[] { relativePath }) as string;
                    if (contentKey != null)
                    {
                        var fCoord = mcmType.BaseType?.GetField("Coordinator", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var coord = fCoord?.GetValue(__instance);
                        if (coord != null)
                        {
                            var mParse = coord.GetType().GetMethod("ParseAssetName", new[] { typeof(string), typeof(bool) });
                            if (mParse != null)
                            {
                                assetName = mParse.Invoke(coord, new object[] { contentKey, false }) as IAssetName;
                                __result = true;
                                return false; // Return success immediately without loading texture into RAM!
                            }
                        }
                    }
                }
                catch
                {
                    return true; // Fall back to original method if reflection fails
                }
            }
            return true;
        }

        private static bool _forceMapLoad = false;
        private static int _loadedMapsCount = 0;
        private static int _loadedNpcCount = 0;

        private static bool IsEssentialLocation(GameLocation? loc)
        {
            if (loc == null) return false;
            if (loc is StardewValley.Locations.FarmHouse || loc is Farm) return true;
            string? name = loc.Name;
            if (name != null)
            {
                if (name == "FarmHouse" || name == "Farm" || name.StartsWith("Cabin")) return true;
            }
            if (Game1.player != null)
            {
                string? sleepLoc = Game1.player.lastSleepLocation?.Value;
                if (!string.IsNullOrEmpty(sleepLoc) && string.Equals(name, sleepLoc, StringComparison.OrdinalIgnoreCase)) return true;
                string? currLoc = Game1.player.currentLocation?.Name;
                if (!string.IsNullOrEmpty(currLoc) && string.Equals(name, currLoc, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsEssentialMap(xTile.Map? map)
        {
            if (map == null) return false;
            string? id = map.Id ?? map.assetPath;
            if (!string.IsNullOrEmpty(id))
            {
                if (id.IndexOf("FarmHouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("Cabin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.EndsWith("Farm", StringComparison.OrdinalIgnoreCase) ||
                    id.EndsWith("Farm_Shadow", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            if (Game1.currentLocation != null && map == Game1.currentLocation.Map)
            {
                return true;
            }
            return false;
        }

        private static bool PrefixLocationReloadMap(GameLocation __instance)
        {
            if (Game1.gameMode == 6 && !_forceMapLoad)
            {
                if (!IsEssentialLocation(__instance))
                {
                    return false;
                }
            }
            return true;
        }

        private static void PrefixLocationGetMap(GameLocation __instance)
        {
            if (_forceMapLoad) return;

            if (__instance != null && __instance.map == null)
            {
                if (Game1.gameMode == 6 && !IsEssentialLocation(__instance))
                {
                    return;
                }

                try
                {
                    _forceMapLoad = true;
                    __instance.reloadMap();
                }
                catch { }
                finally
                {
                    _forceMapLoad = false;
                }
            }
        }

        private static void PostfixLocationLoadMap(GameLocation __instance)
        {
            _loadedMapsCount++;
            if (_loadedMapsCount % 2 == 0)
            {
                try
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                }
                catch { }
            }
        }

        private static void PostfixNpcReloadSprite(NPC __instance)
        {
            _loadedNpcCount++;
            if (_loadedNpcCount % 2 == 0)
            {
                try
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                }
                catch { }
            }
        }

        private static void PrefixNewDayAfterFade()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                ModMonitor?.Log("[SDViOSTouchControls] PrefixNewDayAfterFade: Compacted LOH before day transition.", LogLevel.Info);
            }
            catch { }
        }

        private static void PostfixNewDayAfterFade()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                ModMonitor?.Log("[SDViOSTouchControls] PostfixNewDayAfterFade: Compacted LOH after day transition.", LogLevel.Info);
            }
            catch { }
        }

        private static void PostfixForceGC()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                ModMonitor?.Log("[SDViOSTouchControls] Compacted LOH and collected GC gen 2 after heavy operation.", LogLevel.Info);
            }
            catch { }
        }

        private static bool PrefixMapLoadTileSheets(xTile.Map __instance)
        {
            if (!_forceMapLoad)
            {
                if (!IsEssentialMap(__instance))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool PrefixDevLoadTileSheet(xTile.Tiles.TileSheet tileSheet)
        {
            if (!_forceMapLoad)
            {
                if (tileSheet == null || !IsEssentialMap(tileSheet.Map))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool PrefixPropagateMap(ref bool __result)
        {
            __result = false;
            return false;
        }

        private static Exception? FinalizerSilenceException(Exception? __exception)
        {
            return null; // Suppresses repeating exceptions from broken mods like ContentPatcherAnimations
        }

        private static void PrefixSetWindowSize(ref int w, ref int h)
        {
            w = _staticNativeWidth;
            h = _staticNativeHeight;
        }

        private static bool PrefixWindowClientSizeChanged(Game1 __instance)
        {
            return false;
        }

        private static void PrefixSetPreferredWidth(ref int value)
        {
            value = _staticNativeWidth;
        }

        private static void PrefixSetPreferredHeight(ref int value)
        {
            value = _staticNativeHeight;
        }

        private static bool PrefixPreferredBackBufferWidth(ref int __result)
        {
            __result = _staticNativeWidth;
            return false;
        }

        private static bool PrefixPreferredBackBufferHeight(ref int __result)
        {
            __result = _staticNativeHeight;
            return false;
        }

        private static bool PrefixFalseBool(ref bool __result)
        {
            __result = false;
            return false;
        }

        private static bool PrefixTrue(ref bool __result)
        {
            __result = true;
            return false;
        }

        private static bool PrefixFalse()
        {
            return false;
        }

        private static bool PrefixGetKeyboardState(ref KeyboardState __result)
        {
            var pad = TouchVirtualPad.Instance;
            if (pad != null && pad.HasActiveInput)
            {
                __result = pad.CurrentSimulatedKeyboardState;
                if (pad.ButtonY || pad.ButtonB || pad.ButtonMenu)
                {
                    ModMonitor?.Log($"[PAD_DEBUG] PrefixGetKeyboardState: keys=[{string.Join(",", __result.GetPressedKeys())}], menu={Game1.activeClickableMenu?.GetType().Name ?? "none"}", LogLevel.Info);
                }
                return false;
            }
            return true;
        }

        private static bool PrefixGetGamePadState(ref GamePadState __result)
        {
            var pad = TouchVirtualPad.Instance;
            if (pad == null)
                return true;

            // Always return a state with a STABLE IsConnected value. Previously we returned a
            // connected state only while touching and fell back to native (disconnected) on release,
            // which made the game spam "gamepad connected/disconnected" notifications.
            __result = pad.EffectiveGamePadState;
            if (pad.ButtonY || pad.ButtonB || pad.ButtonMenu)
            {
                ModMonitor?.Log($"[PAD_DEBUG] PrefixGetGamePadState: connected={__result.IsConnected}, buttons={__result.Buttons}, menu={Game1.activeClickableMenu?.GetType().Name ?? "none"}", LogLevel.Info);
            }
            return false;
        }

        private static void PrefixGameMenuCtor()
        {
            try
            {
                long before = GC.GetTotalMemory(false);
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                long after = GC.GetTotalMemory(false);
                ModMonitor?.Log($"[MEM_DEBUG] Pre-GameMenu GC: {before / 1048576} MB -> {after / 1048576} MB", LogLevel.Info);
            }
            catch { }
        }

        private static void PrefixExitActiveMenu()
        {
            var menu = Game1.activeClickableMenu;
            string menuName = menu != null ? menu.GetType().Name : "null";
            ModMonitor?.Log($"[MENU_DEBUG] Game1.exitActiveMenu() called! activeClickableMenu={menuName}, CallStack: {GetCompactStackTrace(2, 8)}", LogLevel.Info);
        }

        private static void PrefixExitThisMenu(IClickableMenu __instance)
        {
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            ModMonitor?.Log($"[MENU_DEBUG] IClickableMenu.exitThisMenu() on {menuName}! CallStack: {GetCompactStackTrace(2, 8)}", LogLevel.Info);
        }

        private static void PrefixExitThisMenuNoSound(IClickableMenu __instance)
        {
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            ModMonitor?.Log($"[MENU_DEBUG] IClickableMenu.exitThisMenuNoSound() on {menuName}! CallStack: {GetCompactStackTrace(2, 8)}", LogLevel.Info);
        }

        private static void PrefixReceiveLeftClick(IClickableMenu __instance, int x, int y)
        {
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            bool inside = __instance != null && __instance.isWithinBounds(x, y);
            ModMonitor?.Log($"[MENU_DEBUG] {menuName}.receiveLeftClick({x}, {y}, inside={inside})", LogLevel.Info);
        }

        private static void PrefixReceiveKeyPress(IClickableMenu __instance, Keys key)
        {
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            ModMonitor?.Log($"[MENU_DEBUG] {menuName}.receiveKeyPress({key})! CallStack: {GetCompactStackTrace(2, 6)}", LogLevel.Info);
        }

        private static void PrefixReceiveGamePadButton(IClickableMenu __instance, Buttons b)
        {
            string menuName = __instance != null ? __instance.GetType().Name : "unknown";
            ModMonitor?.Log($"[MENU_DEBUG] {menuName}.receiveGamePadButton({b})! CallStack: {GetCompactStackTrace(2, 6)}", LogLevel.Info);
        }

        private static void PrefixUpdateActiveMenu(out IClickableMenu? __state)
        {
            __state = Game1.activeClickableMenu;
        }

        private static void PostfixUpdateActiveMenu(IClickableMenu? __state)
        {
            if (__state != null && Game1.activeClickableMenu == null)
            {
                ModMonitor?.Log($"[MENU_DEBUG] Game1.updateActiveMenu CLOSED menu! Was: {__state.GetType().Name}, Now: null!", LogLevel.Info);
            }
        }

        private static FieldInfo? _hooksField = null;

        private void EnsureModHooksWrapped()
        {
            try
            {
                if (_hooksField == null)
                {
                    _hooksField = typeof(Game1).GetField("hooks", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (_hooksField != null)
                {
                    var curHooks = _hooksField.GetValue(null) as StardewValley.Mods.ModHooks;
                    if (curHooks != null && !(curHooks is TouchDelegatingModHooks))
                    {
                        var wrapped = new TouchDelegatingModHooks(curHooks);
                        _hooksField.SetValue(null, wrapped);
                        Monitor.Log("[SDViOSTouchControls] Successfully wrapped Game1.hooks with TouchDelegatingModHooks!", LogLevel.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error wrapping Game1.hooks: {ex.Message}", LogLevel.Error);
            }
        }

        private static bool PrefixMouseGetState(ref MouseState __result)
        {
            if (TouchVirtualPad.Instance != null && TouchVirtualPad.Instance.IsMouseOverridden)
            {
                __result = TouchVirtualPad.Instance.CurrentSimulatedMouseState;
                return false; // Skip original method, return our simulated mouse state!
            }
            return true;
        }

        private static bool PrefixSetMousePositionRaw(int x, int y)
        {
            if (TouchVirtualPad.Instance != null && TouchVirtualPad.Instance.IsMouseOverridden)
            {
                // In Trackpad or Point & Click mode, do NOT let Game1 forcibly snap the mouse to center or reset lastCursorMotionWasMouse!
                return false;
            }
            return true;
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            try
            {
                EnsureNativeResolution(true);
                EnsurePadInitialized();
                EnsureModHooksWrapped();
                Monitor.Log("[SDViOSTouchControls] GameLaunched: Full native resolution locked & touch overlay ready.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in GameLaunched: {ex.Message}", LogLevel.Error);
            }
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            string oldMenu = e.OldMenu != null ? e.OldMenu.GetType().Name : "null";
            string newMenu = e.NewMenu != null ? e.NewMenu.GetType().Name : "null";
            Monitor.Log($"[MENU_DEBUG] OnMenuChanged: {oldMenu} -> {newMenu}", LogLevel.Info);

            // Free the closed GameMenu (MapPage/SocialPage textures etc.) immediately
            if (e.OldMenu is GameMenu && e.NewMenu == null)
            {
                try
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    Monitor.Log($"[MEM_DEBUG] Post-GameMenu-close GC: {GC.GetTotalMemory(false) / 1048576} MB", LogLevel.Info);
                }
                catch { }
            }
        }

        private void OnAssetReady(object? sender, AssetReadyEventArgs e)
        {
            _assetEventCount++;

            if (Game1.gameMode == 6)
            {
                if (_assetEventCount % 4 == 0)
                {
                    try
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                    }
                    catch { }
                }
            }
            else if (Game1.activeClickableMenu is StardewValley.Menus.TitleMenu)
            {
                if (_assetEventCount % 12 == 0)
                {
                    try
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                    }
                    catch { }
                }
            }
        }

        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            _loadedMapsCount = 0;
            _loadedNpcCount = 0;
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                Monitor.Log("[SDViOSTouchControls] Save loaded successfully. Memory compacted.", LogLevel.Info);
            }
            catch { }
        }

        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                Monitor.Log("[SDViOSTouchControls] Day started: Large Object Heap compacted.", LogLevel.Info);
            }
            catch { }
        }

        private bool _legacyNeutralized = false;
        private bool _drawnThisFrame = false;

        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            try
            {
                _drawnThisFrame = false;

                // Periodically compact LOH during loading to stay under iOS 2GB jetsam limit
                if (Game1.gameMode == 6 && e.IsMultipleOf(10))
                {
                    try
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                    }
                    catch { }
                }

                // Ensure native resolution remains locked
                EnsureNativeResolution(false);

                if (!_legacyNeutralized && Game1.game1 != null)
                {
                    NeutralizeOldTouchVirtualPad();
                    _legacyNeutralized = true;
                }

                EnsurePadInitialized();
                EnsureModHooksWrapped();

                if (_lastTickMenu != null && Game1.activeClickableMenu == null)
                {
                    Monitor.Log($"[MENU_DEBUG] Menu became null before UpdateTicking! Was: {_lastTickMenu.GetType().Name}", LogLevel.Info);
                }
                _lastTickMenu = Game1.activeClickableMenu;

                // Process virtual pad touches, point & click, and trackpad BEFORE Game1.Update runs!
                TouchVirtualPad.Instance.Update(Game1.currentGameTime);
            }
            catch (Exception ex)
            {
                if (e.IsOneSecond)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error in UpdateTicking: {ex.Message}", LogLevel.Warn);
                }
            }
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (_lastTickMenu != null && Game1.activeClickableMenu == null)
            {
                Monitor.Log($"[MENU_DEBUG] Menu became null during Game1.Update! Was: {_lastTickMenu.GetType().Name}", LogLevel.Info);
            }
            _lastTickMenu = Game1.activeClickableMenu;
        }

        private void OnRenderedStep(object? sender, RenderedStepEventArgs e)
        {
            try
            {
                // RenderSteps.Overlays runs inside Game1.DrawOverlays while uiScreen is the active render target,
                // right after drawMouseCursor(). This draws OVER EVERYTHING (including all HUD, chat, and dialogs)!
                if (e.Step == StardewValley.Mods.RenderSteps.Overlays)
                {
                    EnsurePadInitialized();
                    TouchVirtualPad.Instance.Draw(e.SpriteBatch);
                    _drawnThisFrame = true;
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in RenderedStep: {ex.Message}", LogLevel.Warn);
            }
        }

        private void OnRendered(object? sender, RenderedEventArgs e)
        {
            try
            {
                // Fallback for screens where RenderSteps.Overlays is not invoked (e.g. Title screen, load menu)
                if (!_drawnThisFrame)
                {
                    EnsurePadInitialized();
                    TouchVirtualPad.Instance.Draw(e.SpriteBatch);
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in Rendered: {ex.Message}", LogLevel.Warn);
            }
        }

        private void EnsureNativeResolution(bool force)
        {
            try
            {
                var gd = Game1.graphics?.GraphicsDevice;
                if (Game1.graphics == null || gd == null || Game1.game1 == null) return;

                int targetW = _nativeWidth;
                int targetH = _nativeHeight;

                bool sizeNeedsSync = (Game1.graphics.PreferredBackBufferWidth != targetW ||
                                     Game1.graphics.PreferredBackBufferHeight != targetH ||
                                     gd.PresentationParameters.BackBufferWidth != targetW ||
                                     gd.PresentationParameters.BackBufferHeight != targetH ||
                                     Game1.graphics.IsFullScreen);

                if (sizeNeedsSync || force)
                {
                    Game1.graphics.PreferredBackBufferWidth = targetW;
                    Game1.graphics.PreferredBackBufferHeight = targetH;
                    Game1.graphics.IsFullScreen = false;
                    gd.PresentationParameters.BackBufferWidth = targetW;
                    gd.PresentationParameters.BackBufferHeight = targetH;

                    // Let Stardew Valley natively allocate screen & uiScreen render targets
                    // and update viewport & uiViewport with zoom & uiScale calculations!
                    Game1.game1.SetWindowSize(targetW, targetH);

                    Monitor.Log($"[SDViOSTouchControls] Synchronized game native resolution to {targetW}x{targetH} via SetWindowSize.", LogLevel.Info);
                }

                TouchVirtualPad.Instance?.UpdateLayout(targetW, targetH);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in EnsureNativeResolution: {ex.Message}", LogLevel.Warn);
            }
        }

        private void EnsurePadInitialized()
        {
            if (!_initialized && Game1.graphics?.GraphicsDevice != null)
            {
                try
                {
                    var gd = Game1.graphics.GraphicsDevice;
                    TouchVirtualPad.Instance.Initialize(gd);
                    TouchVirtualPad.Instance.UpdateLayout(_nativeWidth, _nativeHeight);
                    _initialized = true;
                    Monitor.Log($"[SDViOSTouchControls] TouchVirtualPad initialized for Viewport={_nativeWidth}x{_nativeHeight}.", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"[SDViOSTouchControls] Error initializing TouchVirtualPad: {ex.Message}", LogLevel.Error);
                }
            }
        }
    }
}
