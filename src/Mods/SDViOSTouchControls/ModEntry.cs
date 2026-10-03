using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace SDViOSTouchControls
{
    public class ModEntry : Mod
    {
        private bool _initialized = false;
        private int _nativeWidth = 1792;
        private int _nativeHeight = 828;

        public override void Entry(IModHelper helper)
        {
            TouchOverlaySettings.LogAction = msg => Monitor.Log(msg, LogLevel.Info);
            TouchVirtualPad.LogAction = msg => Monitor.Log(msg, LogLevel.Info);

            Monitor.Log("[SDViOSTouchControls] Initializing native touch controls mod...", LogLevel.Info);

            // 1. Determine native hardware display resolution
            DetermineNativeResolution();

            // 2. Neutralize legacy SDViOS.Input.TouchVirtualPad from fighting with input
            NeutralizeOldTouchVirtualPad();

            // 3. Apply Harmony patches to route Mouse, Keyboard, and Gamepad to TouchVirtualPad & force CaseInsensitivePaths
            ApplyHarmonyPatches();

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

                Monitor.Log($"[SDViOSTouchControls] Detected hardware display resolution: {_nativeWidth}x{_nativeHeight}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _nativeWidth = 1792;
                _nativeHeight = 828;
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

            // 3. GamePad and Gamepad Mode Patches
            try
            {
                var mCheckGamepadMode = typeof(Game1).GetMethod("CheckGamepadMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (mCheckGamepadMode != null)
                {
                    harmony.Patch(mCheckGamepadMode, prefix: new HarmonyMethod(typeof(ModEntry).GetMethod(nameof(PrefixFalse), BindingFlags.Static | BindingFlags.NonPublic)));
                    Monitor.Log("[SDViOSTouchControls] Harmony patched Game1.CheckGamepadMode -> false.", LogLevel.Info);
                }

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
                Monitor.Log($"[SDViOSTouchControls] Note setting UseCaseInsensitivePaths: {ex.Message}", LogLevel.Warn);
            }
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
                return false;
            }
            return true;
        }

        private static bool PrefixGetGamePadState(ref GamePadState __result)
        {
            // Always report empty/disconnected gamepad state so iOS/MonoGame doesn't trigger CheckGamepadMode toast loop or open pause menu!
            __result = default(GamePadState);
            return false;
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
                if (Game1.options != null)
                {
                    Game1.options.gamepadMode = Options.GamepadModes.ForceOff;
                    Game1.options.gamepadControls = false;
                }
                EnsureNativeResolution(true);
                EnsurePadInitialized();
                EnsureModHooksWrapped();
                Monitor.Log("[SDViOSTouchControls] GameLaunched: Full native resolution locked, ForceOff gamepad enforced & touch overlay ready.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[SDViOSTouchControls] Error in GameLaunched: {ex.Message}", LogLevel.Error);
            }
        }

        private void OnMenuChanged(object? sender, MenuChangedEventArgs e)
        {
            // Menus automatically align to Game1.uiViewport and uiScale.
        }

        private bool _legacyNeutralized = false;
        private bool _drawnThisFrame = false;

        private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
        {
            try
            {
                _drawnThisFrame = false;

                // Enforce GamepadModes.ForceOff so Stardew never checks GamePad.GetState(), never shows toast, and never opens pause menu
                if (Game1.options != null)
                {
                    if (Game1.options.gamepadMode != Options.GamepadModes.ForceOff)
                    {
                        Game1.options.gamepadMode = Options.GamepadModes.ForceOff;
                    }
                    Game1.options.gamepadControls = false;
                }

                // Purge any lingering or queued gamepad messages
                if (Game1.hudMessages != null && Game1.hudMessages.Count > 0)
                {
                    for (int i = Game1.hudMessages.Count - 1; i >= 0; i--)
                    {
                        var msg = Game1.hudMessages[i];
                        if (msg?.message != null && (
                            msg.message.IndexOf("Gamepad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            msg.message.IndexOf("Game1.cs.257", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            Game1.hudMessages.RemoveAt(i);
                        }
                    }
                }

                if (!_legacyNeutralized && Game1.game1 != null)
                {
                    NeutralizeOldTouchVirtualPad();
                    _legacyNeutralized = true;
                }

                EnsurePadInitialized();
                EnsureModHooksWrapped();

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
                                     gd.PresentationParameters.BackBufferHeight != targetH);

                if (sizeNeedsSync || force)
                {
                    Game1.graphics.PreferredBackBufferWidth = targetW;
                    Game1.graphics.PreferredBackBufferHeight = targetH;
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
