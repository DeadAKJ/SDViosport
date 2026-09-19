using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using SDViOS.Diagnostics;

namespace SDViOS.Input
{
    public class TouchVirtualPad
    {
        public static TouchVirtualPad Instance { get; } = new TouchVirtualPad();

        public bool IsVisible { get; set; } = true;
        public bool AutoHideWhenGamepadConnected { get; set; } = false;

        // Joystick state
        public Vector2 LeftStick { get; private set; } = Vector2.Zero;
        public bool DPadUp => LeftStick.Y < -0.25f;
        public bool DPadDown => LeftStick.Y > 0.25f;
        public bool DPadLeft => LeftStick.X < -0.25f;
        public bool DPadRight => LeftStick.X > 0.25f;

        // Action buttons state
        public bool ButtonA { get; private set; } // Action / Talk / Check (X / Left Click / A)
        public bool ButtonX { get; private set; } // Use Tool (C / Right Click / X)
        public bool ButtonY { get; private set; } // Menu / Inventory (E / Y)
        public bool ButtonB { get; private set; } // Cancel / Back (Esc / B)
        public bool ButtonMenu { get; private set; } // Game Menu (Esc / Start)

        // Simulated mouse state
        public Point SimulatedMousePosition { get; private set; } = new Point(896, 414);
        public bool SimulatedMouseLeftDown { get; private set; }
        public bool SimulatedMouseRightDown { get; private set; }

        // Layout bounds (calculated dynamically based on viewport)
        private Rectangle _joystickBaseRect;
        private Vector2 _joystickCenter;
        private float _joystickRadius = 75f;
        private Vector2 _currentStickPos;
        private int _stickTouchId = -1;

        private Rectangle _btnARect;
        private Rectangle _btnXRect;
        private Rectangle _btnYRect;
        private Rectangle _btnBRect;
        private Rectangle _btnMenuRect;
        private Rectangle _btnKeyboardRect;
        private Rectangle _btnToggleRect;

        private Texture2D? _pixelTexture;

        // Reflection caches for Stardew Valley Game1.input & options
        private static bool _reflectionInitialized = false;
        private static object? _inputInstance = null;
        private static FieldInfo? _currentMouseStateField = null;
        private static FieldInfo? _currentGamepadStateField = null;
        private static FieldInfo? _currentKeyboardStateField = null;
        private static FieldInfo? _lastCursorMotionWasMouseField = null;
        private static FieldInfo? _oldMouseStateField = null;
        private static FieldInfo? _mousePrimaryWindowField = null;
        private static FieldInfo? _gameWindowMouseStateField = null;

        private static Type? _game1Type = null;
        private static PropertyInfo? _optionsProp = null;
        private static FieldInfo? _gamepadControlsField = null;
        private static MethodInfo? _setKeysMethod = null;

        public void Initialize(GraphicsDevice graphicsDevice)
        {
            UpdateLayout(graphicsDevice.Viewport.Width, graphicsDevice.Viewport.Height);
            GenerateTextures(graphicsDevice);
            EnsureReflection();
        }

        public void UpdateLayout(int width, int height)
        {
            if (width <= 0) width = 1792;
            if (height <= 0) height = 828;

            float scale = Math.Max(1.0f, height / 720.0f);
            float btnSize = 58f * scale;
            _joystickRadius = 75f * scale;

            // Joystick base in bottom-left
            float stickBaseX = 130f * scale;
            float stickBaseY = height - (130f * scale);
            _joystickCenter = new Vector2(stickBaseX, stickBaseY);
            _currentStickPos = _joystickCenter;
            _joystickBaseRect = new Rectangle((int)(stickBaseX - _joystickRadius), (int)(stickBaseY - _joystickRadius), (int)(_joystickRadius * 2), (int)(_joystickRadius * 2));

            // Buttons diamond in bottom-right
            float rightCenterX = width - (130f * scale);
            float rightCenterY = height - (120f * scale);
            float spacing = 58f * scale;

            _btnARect = new Rectangle((int)rightCenterX, (int)(rightCenterY + spacing / 1.4f), (int)btnSize, (int)btnSize); // Bottom: Action (A)
            _btnXRect = new Rectangle((int)(rightCenterX - spacing), (int)rightCenterY, (int)btnSize, (int)btnSize);          // Left: Tool (X)
            _btnYRect = new Rectangle((int)rightCenterX, (int)(rightCenterY - spacing), (int)btnSize, (int)btnSize);          // Top: Menu (Y)
            _btnBRect = new Rectangle((int)(rightCenterX + spacing), (int)rightCenterY, (int)btnSize, (int)btnSize);          // Right: Cancel (B)

            // Top utility buttons (Toggle, Keyboard, Menu)
            _btnToggleRect = new Rectangle(width - (int)(75f * scale), (int)(20f * scale), (int)(55f * scale), (int)(36f * scale));
            _btnKeyboardRect = new Rectangle(width - (int)(140f * scale), (int)(20f * scale), (int)(55f * scale), (int)(36f * scale));
            _btnMenuRect = new Rectangle(width - (int)(215f * scale), (int)(20f * scale), (int)(65f * scale), (int)(36f * scale));
        }

        public void Update(GameTime gameTime)
        {
            TouchCollection touches = TouchPanel.GetState();

            // Reset momentary states
            ButtonA = false;
            ButtonX = false;
            ButtonY = false;
            ButtonB = false;
            ButtonMenu = false;
            SimulatedMouseLeftDown = false;
            SimulatedMouseRightDown = false;

            bool stickTouchFound = false;

            foreach (var touch in touches)
            {
                Vector2 pos = touch.Position;
                Point pt = new Point((int)pos.X, (int)pos.Y);

                // Toggle visibility button check
                if (touch.State == TouchLocationState.Pressed && _btnToggleRect.Contains(pt))
                {
                    IsVisible = !IsVisible;
                    continue;
                }

                // Keyboard manual summon button check
                if (touch.State == TouchLocationState.Pressed && _btnKeyboardRect.Contains(pt))
                {
                    VirtualKeyboardManager.PromptManualInput(text =>
                    {
                        FeedTextToGame(text);
                    });
                    continue;
                }

                if (!IsVisible)
                {
                    // If overlay hidden, all touches act as direct mouse taps/drags
                    SimulatedMousePosition = pt;
                    SimulatedMouseLeftDown = (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved);
                    continue;
                }

                // Joystick tracking
                if (_stickTouchId == touch.Id)
                {
                    if (touch.State == TouchLocationState.Released)
                    {
                        _stickTouchId = -1;
                        _currentStickPos = _joystickCenter;
                        LeftStick = Vector2.Zero;
                    }
                    else
                    {
                        stickTouchFound = true;
                        UpdateJoystickVector(pos);
                    }
                    continue;
                }
                else if (_stickTouchId == -1 && touch.State == TouchLocationState.Pressed && _joystickBaseRect.Contains(pt))
                {
                    _stickTouchId = touch.Id;
                    stickTouchFound = true;
                    UpdateJoystickVector(pos);
                    continue;
                }

                // Virtual gamepad buttons
                if (_btnARect.Contains(pt))
                {
                    ButtonA = true;
                }
                else if (_btnXRect.Contains(pt))
                {
                    ButtonX = true;
                }
                else if (_btnYRect.Contains(pt))
                {
                    ButtonY = true;
                }
                else if (_btnBRect.Contains(pt))
                {
                    ButtonB = true;
                }
                else if (_btnMenuRect.Contains(pt))
                {
                    ButtonMenu = true;
                }
                else
                {
                    // Direct screen tap outside virtual controls
                    SimulatedMousePosition = pt;
                    SimulatedMouseLeftDown = (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved);
                }
            }

            if (!stickTouchFound && _stickTouchId != -1)
            {
                _stickTouchId = -1;
                _currentStickPos = _joystickCenter;
                LeftStick = Vector2.Zero;
            }

            // Immediately push mouse, keyboard & gamepad events to MonoGame and Stardew Valley
            ForwardInputToGame();
        }

        public void ForwardInputToGame()
        {
            try
            {
                EnsureReflection();

                bool isLeftDown = SimulatedMouseLeftDown || ButtonA;
                bool isRightDown = SimulatedMouseRightDown || ButtonX;

                var mouseState = new MouseState(
                    SimulatedMousePosition.X,
                    SimulatedMousePosition.Y,
                    0,
                    isLeftDown ? ButtonState.Pressed : ButtonState.Released,
                    ButtonState.Released,
                    isRightDown ? ButtonState.Pressed : ButtonState.Released,
                    ButtonState.Released,
                    ButtonState.Released
                );

                // 1. Update MonoGame PrimaryWindow.MouseState
                try
                {
                    if (_mousePrimaryWindowField == null)
                    {
                        _mousePrimaryWindowField = typeof(Mouse).GetField("PrimaryWindow", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    }
                    var win = _mousePrimaryWindowField?.GetValue(null);
                    if (win != null)
                    {
                        if (_gameWindowMouseStateField == null)
                        {
                            _gameWindowMouseStateField = win.GetType().GetField("MouseState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                                      ?? typeof(GameWindow).GetField("MouseState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        }
                        _gameWindowMouseStateField?.SetValue(win, mouseState);
                    }
                }
                catch { }

                // 2. Build active keys for hardware keyboard emulation (WASD, Action, Tool, Menu, Escape)
                var activeKeys = new List<Keys>();
                if (LeftStick.Y < -0.25f) activeKeys.Add(Keys.W);
                if (LeftStick.Y > 0.25f) activeKeys.Add(Keys.S);
                if (LeftStick.X < -0.25f) activeKeys.Add(Keys.A);
                if (LeftStick.X > 0.25f) activeKeys.Add(Keys.D);

                if (ButtonA)
                {
                    activeKeys.Add(Keys.X);
                    activeKeys.Add(Keys.Space);
                }
                if (ButtonX)
                {
                    activeKeys.Add(Keys.C);
                }
                if (ButtonY)
                {
                    activeKeys.Add(Keys.E);
                }
                if (ButtonB)
                {
                    activeKeys.Add(Keys.Escape);
                }
                if (ButtonMenu)
                {
                    activeKeys.Add(Keys.Escape);
                }

                // Inject into MonoGame's Keyboard engine static cache
                try
                {
                    if (_setKeysMethod == null)
                    {
                        _setKeysMethod = typeof(Microsoft.Xna.Framework.Input.Keyboard).GetMethod("SetKeys", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    }
                    _setKeysMethod?.Invoke(null, new object[] { activeKeys });
                }
                catch { }

                // 3. Update Stardew Valley Game1.input directly
                if (_inputInstance != null)
                {
                    if (_currentMouseStateField != null)
                    {
                        _currentMouseStateField.SetValue(_inputInstance, mouseState);
                    }

                    if (_currentKeyboardStateField != null)
                    {
                        _currentKeyboardStateField.SetValue(_inputInstance, new KeyboardState(activeKeys.ToArray()));
                    }

                    // Feed GamePadState
                    if (_currentGamepadStateField != null)
                    {
                        var buttons = new GamePadButtons(
                            (ButtonA ? Buttons.A : 0) |
                            (ButtonB ? Buttons.B : 0) |
                            (ButtonX ? Buttons.X : 0) |
                            (ButtonY ? Buttons.Y : 0) |
                            (ButtonMenu ? Buttons.Start : 0)
                        );
                        var dpad = new GamePadDPad(
                            (LeftStick.Y < -0.3f || DPadUp) ? ButtonState.Pressed : ButtonState.Released,
                            (LeftStick.Y > 0.3f || DPadDown) ? ButtonState.Pressed : ButtonState.Released,
                            (LeftStick.X < -0.3f || DPadLeft) ? ButtonState.Pressed : ButtonState.Released,
                            (LeftStick.X > 0.3f || DPadRight) ? ButtonState.Pressed : ButtonState.Released
                        );
                        var thumbsticks = new GamePadThumbSticks(new Vector2(LeftStick.X, -LeftStick.Y), Vector2.Zero);
                        var gpState = new GamePadState(thumbsticks, new GamePadTriggers(), buttons, dpad);
                        _currentGamepadStateField.SetValue(_inputInstance, gpState);
                    }
                }

                // 4. Update Game1.lastCursorMotionWasMouse & Game1.options.gamepadControls
                bool controlsActive = (LeftStick != Vector2.Zero || ButtonA || ButtonB || ButtonX || ButtonY || ButtonMenu);
                if (_lastCursorMotionWasMouseField != null)
                {
                    if (SimulatedMouseLeftDown)
                    {
                        _lastCursorMotionWasMouseField.SetValue(null, true);
                    }
                    else if (controlsActive)
                    {
                        _lastCursorMotionWasMouseField.SetValue(null, false);
                    }
                }

                if (_gamepadControlsField != null && _optionsProp != null)
                {
                    var options = _optionsProp.GetValue(null);
                    if (options != null)
                    {
                        if (controlsActive)
                        {
                            _gamepadControlsField.SetValue(options, true);
                        }
                        else if (SimulatedMouseLeftDown)
                        {
                            _gamepadControlsField.SetValue(options, false);
                        }
                    }
                }
            }
            catch { }
        }

        private void FeedTextToGame(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                EnsureReflection();
                if (_game1Type != null)
                {
                    var dispProp = _game1Type.GetProperty("keyboardDispatcher", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? _game1Type.GetField("instanceKeyboardDispatcher", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as PropertyInfo;
                    var dispatcher = dispProp?.GetValue(null);
                    if (dispatcher != null)
                    {
                        var subProp = dispatcher.GetType().GetProperty("Subscriber", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var subscriber = subProp?.GetValue(dispatcher);
                        if (subscriber != null)
                        {
                            var textProp = subscriber.GetType().GetProperty("Text");
                            textProp?.SetValue(subscriber, text);

                            var recvMethod = subscriber.GetType().GetMethod("RecieveTextInput", new[] { typeof(string) });
                            recvMethod?.Invoke(subscriber, new object[] { text });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[TouchVirtualPad] FeedTextToGame warning: {ex.Message}");
            }
        }

        private static void EnsureReflection()
        {
            if (_reflectionInitialized && _inputInstance != null) return;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var g1Type = asm.GetType("StardewValley.Game1");
                    if (g1Type != null)
                    {
                        _game1Type = g1Type;
                        var inputField = g1Type.GetField("input", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _inputInstance = inputField?.GetValue(null);

                        _lastCursorMotionWasMouseField = g1Type.GetField("lastCursorMotionWasMouse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _oldMouseStateField = g1Type.GetField("oldMouseState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _optionsProp = g1Type.GetProperty("options", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                        if (_optionsProp != null)
                        {
                            var optType = _optionsProp.PropertyType;
                            _gamepadControlsField = optType.GetField("gamepadControls", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }

                        if (_inputInstance != null)
                        {
                            var inputType = _inputInstance.GetType();
                            _currentMouseStateField = inputType.GetField("_currentMouseState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            _currentGamepadStateField = inputType.GetField("_currentGamepadState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            _currentKeyboardStateField = inputType.GetField("_currentKeyboardState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            _reflectionInitialized = true;
                            EngineLogger.Log("[TouchVirtualPad] Successfully hooked Stardew Valley input & options fields.");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[TouchVirtualPad] Reflection hook warning: {ex.Message}");
            }
        }

        private void UpdateJoystickVector(Vector2 touchPos)
        {
            Vector2 delta = touchPos - _joystickCenter;
            float dist = delta.Length();
            if (dist > _joystickRadius)
            {
                delta.Normalize();
                _currentStickPos = _joystickCenter + delta * _joystickRadius;
                LeftStick = delta;
            }
            else
            {
                _currentStickPos = touchPos;
                LeftStick = delta / _joystickRadius;
            }
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            if (_pixelTexture == null) return;

            // Draw toggle button (always accessible)
            DrawRoundedButton(spriteBatch, _btnToggleRect, Color.DarkSlateGray * 0.6f);

            // Draw manual keyboard summon button
            DrawRoundedButton(spriteBatch, _btnKeyboardRect, Color.DarkSlateBlue * 0.6f);

            if (!IsVisible) return;

            // Draw joystick base & thumb
            DrawRoundedButton(spriteBatch, _joystickBaseRect, Color.Black * 0.28f);
            Rectangle stickThumbRect = new Rectangle((int)(_currentStickPos.X - 25), (int)(_currentStickPos.Y - 25), 50, 50);
            DrawRoundedButton(spriteBatch, stickThumbRect, Color.White * 0.55f);

            // Draw action buttons
            DrawRoundedButton(spriteBatch, _btnARect, ButtonA ? Color.Lime * 0.85f : Color.DarkGreen * 0.45f);
            DrawRoundedButton(spriteBatch, _btnXRect, ButtonX ? Color.CornflowerBlue * 0.85f : Color.DarkBlue * 0.45f);
            DrawRoundedButton(spriteBatch, _btnYRect, ButtonY ? Color.Yellow * 0.85f : Color.DarkGoldenrod * 0.45f);
            DrawRoundedButton(spriteBatch, _btnBRect, ButtonB ? Color.Red * 0.85f : Color.DarkRed * 0.45f);
            DrawRoundedButton(spriteBatch, _btnMenuRect, ButtonMenu ? Color.Orange * 0.85f : Color.SaddleBrown * 0.45f);
        }

        private void DrawRoundedButton(SpriteBatch sb, Rectangle rect, Color color)
        {
            if (_pixelTexture != null)
            {
                sb.Draw(_pixelTexture, rect, color);
            }
        }

        private void GenerateTextures(GraphicsDevice gd)
        {
            _pixelTexture = new Texture2D(gd, 1, 1);
            _pixelTexture.SetData(new[] { Color.White });
        }
    }
}
