using System;
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
        public bool DPadUp => LeftStick.Y < -0.3f;
        public bool DPadDown => LeftStick.Y > 0.3f;
        public bool DPadLeft => LeftStick.X < -0.3f;
        public bool DPadRight => LeftStick.X > 0.3f;

        // Action buttons state
        public bool ButtonA { get; private set; } // Action / Talk / Check (Left Click)
        public bool ButtonX { get; private set; } // Use Tool (Right Click / C)
        public bool ButtonY { get; private set; } // Menu / Inventory (E / Esc)
        public bool ButtonB { get; private set; } // Cancel / Back
        public bool ButtonMenu { get; private set; }
        public bool ButtonJournal { get; private set; }

        // Simulated mouse state
        public Point SimulatedMousePosition { get; private set; } = new Point(896, 414);
        public bool SimulatedMouseLeftDown { get; private set; }
        public bool SimulatedMouseRightDown { get; private set; }

        // Layout bounds (calculated dynamically based on viewport)
        private Rectangle _joystickBaseRect;
        private Vector2 _joystickCenter;
        private float _joystickRadius = 70f;
        private Vector2 _currentStickPos;
        private int _stickTouchId = -1;

        private Rectangle _btnARect;
        private Rectangle _btnXRect;
        private Rectangle _btnYRect;
        private Rectangle _btnBRect;
        private Rectangle _btnMenuRect;
        private Rectangle _btnToggleRect;

        private Texture2D? _pixelTexture;

        // Reflection caches for Stardew Valley Game1.input
        private static bool _reflectionInitialized = false;
        private static object? _inputInstance = null;
        private static FieldInfo? _currentMouseStateField = null;
        private static FieldInfo? _currentGamepadStateField = null;
        private static FieldInfo? _currentKeyboardStateField = null;
        private static FieldInfo? _lastCursorMotionWasMouseField = null;
        private static FieldInfo? _oldMouseStateField = null;
        private static MethodInfo? _setMousePositionMethod = null;

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

            // Top utility buttons
            _btnMenuRect = new Rectangle(width - (int)(160f * scale), (int)(20f * scale), (int)btnSize, (int)(btnSize * 0.75f));
            _btnToggleRect = new Rectangle(width - (int)(75f * scale), (int)(20f * scale), (int)(55f * scale), (int)(36f * scale));
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

                if (!IsVisible)
                {
                    // If overlay hidden, all touches act as pure direct mouse taps/drags
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

            // Immediately push mouse & gamepad events to MonoGame and Stardew Valley
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
                    if (Mouse.PrimaryWindow != null)
                    {
                        Mouse.PrimaryWindow.MouseState = mouseState;
                    }
                }
                catch { }

                // 2. Update GameRunner.instance?.Window.MouseState
                try
                {
                    var runnerWindow = Microsoft.Xna.Framework.Input.Mouse.PrimaryWindow;
                    if (runnerWindow != null)
                    {
                        runnerWindow.MouseState = mouseState;
                    }
                }
                catch { }

                // 3. Update Stardew Valley Game1.input directly
                if (_inputInstance != null)
                {
                    if (_currentMouseStateField != null)
                    {
                        _currentMouseStateField.SetValue(_inputInstance, mouseState);
                    }

                    // Also feed GamePadState if joystick or buttons are active
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
                            DPadUp ? ButtonState.Pressed : ButtonState.Released,
                            DPadDown ? ButtonState.Pressed : ButtonState.Released,
                            DPadLeft ? ButtonState.Pressed : ButtonState.Released,
                            DPadRight ? ButtonState.Pressed : ButtonState.Released
                        );
                        var thumbsticks = new GamePadThumbSticks(LeftStick, Vector2.Zero);
                        var gpState = new GamePadState(thumbsticks, new GamePadTriggers(), buttons, dpad);
                        _currentGamepadStateField.SetValue(_inputInstance, gpState);
                    }

                    // Feed KeyboardState for menu buttons (Y = E / Esc, B = Esc)
                    if (_currentKeyboardStateField != null)
                    {
                        if (ButtonY)
                        {
                            _currentKeyboardStateField.SetValue(_inputInstance, new KeyboardState(Keys.E, Keys.Escape));
                        }
                        else if (ButtonB)
                        {
                            _currentKeyboardStateField.SetValue(_inputInstance, new KeyboardState(Keys.Escape));
                        }
                    }
                }

                // 4. Update Game1.lastCursorMotionWasMouse
                if (_lastCursorMotionWasMouseField != null)
                {
                    if (SimulatedMouseLeftDown || touchesActive())
                    {
                        _lastCursorMotionWasMouseField.SetValue(null, true);
                    }
                    else if (LeftStick != Vector2.Zero || ButtonA || ButtonB || ButtonX || ButtonY)
                    {
                        _lastCursorMotionWasMouseField.SetValue(null, false);
                    }
                }
            }
            catch { }
        }

        private bool touchesActive()
        {
            try
            {
                return TouchPanel.GetState().Count > 0;
            }
            catch { return false; }
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
                        var inputField = g1Type.GetField("input", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _inputInstance = inputField?.GetValue(null);

                        _lastCursorMotionWasMouseField = g1Type.GetField("lastCursorMotionWasMouse", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        _oldMouseStateField = g1Type.GetField("oldMouseState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                        if (_inputInstance != null)
                        {
                            var inputType = _inputInstance.GetType();
                            _currentMouseStateField = inputType.GetField("_currentMouseState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            _currentGamepadStateField = inputType.GetField("_currentGamepadState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            _currentKeyboardStateField = inputType.GetField("_currentKeyboardState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            _setMousePositionMethod = inputType.GetMethod("SetMousePosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            _reflectionInitialized = true;
                            EngineLogger.Log("[TouchVirtualPad] Successfully hooked Stardew Valley Game1.input fields.");
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

        private void ResetStates()
        {
            LeftStick = Vector2.Zero;
            _currentStickPos = _joystickCenter;
            _stickTouchId = -1;
            ButtonA = false;
            ButtonX = false;
            ButtonY = false;
            ButtonB = false;
            ButtonMenu = false;
            SimulatedMouseLeftDown = false;
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            if (_pixelTexture == null) return;

            // Draw toggle button (always accessible)
            DrawRoundedButton(spriteBatch, _btnToggleRect, Color.Black * 0.45f);

            if (!IsVisible) return;

            // Draw joystick base & thumb
            DrawRoundedButton(spriteBatch, _joystickBaseRect, Color.Black * 0.25f);
            Rectangle stickThumbRect = new Rectangle((int)(_currentStickPos.X - 25), (int)(_currentStickPos.Y - 25), 50, 50);
            DrawRoundedButton(spriteBatch, stickThumbRect, Color.White * 0.5f);

            // Draw action buttons
            DrawRoundedButton(spriteBatch, _btnARect, ButtonA ? Color.Lime * 0.75f : Color.Black * 0.35f);
            DrawRoundedButton(spriteBatch, _btnXRect, ButtonX ? Color.CornflowerBlue * 0.75f : Color.Black * 0.35f);
            DrawRoundedButton(spriteBatch, _btnYRect, ButtonY ? Color.Yellow * 0.75f : Color.Black * 0.35f);
            DrawRoundedButton(spriteBatch, _btnBRect, ButtonB ? Color.Red * 0.75f : Color.Black * 0.35f);
            DrawRoundedButton(spriteBatch, _btnMenuRect, ButtonMenu ? Color.Orange * 0.75f : Color.Black * 0.35f);
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
