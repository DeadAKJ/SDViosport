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

        public TouchOverlaySettings Settings => TouchOverlaySettings.Instance;

        public bool IsVisible { get; set; } = true;
        public bool IsSettingsOpen { get; set; } = false;
        public bool IsEditLayoutMode { get; set; } = false;

        // Joystick state
        public Vector2 LeftStick { get; private set; } = Vector2.Zero;
        public bool DPadUp => LeftStick.Y < -Settings.Deadzone;
        public bool DPadDown => LeftStick.Y > Settings.Deadzone;
        public bool DPadLeft => LeftStick.X < -Settings.Deadzone;
        public bool DPadRight => LeftStick.X > Settings.Deadzone;

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

        public void ResetSimulatedMouse()
        {
            SimulatedMousePosition = new Point(-1000, -1000);
            SimulatedMouseLeftDown = false;
            SimulatedMouseRightDown = false;
        }

        // Viewport bounds
        private int _viewportWidth = 1792;
        private int _viewportHeight = 828;

        // Layout bounds
        private Rectangle _joystickBaseRect;
        private Vector2 _joystickCenter;
        private float _joystickRadius = 75f;
        private Vector2 _currentStickPos;
        private int _stickTouchId = -1;

        private Vector2 _buttonsCenter;
        private Rectangle _btnARect;
        private Rectangle _btnXRect;
        private Rectangle _btnYRect;
        private Rectangle _btnBRect;
        private Rectangle _btnMenuRect;
        private Rectangle _btnKeyboardRect;
        private Rectangle _btnToggleRect;
        private Rectangle _btnSettingsRect;

        // Trackpad mode state (Steam Link style relative touch mouse)
        private Vector2 _trackpadCursorPos = new Vector2(896, 414);
        private int _trackpadPrimaryTouchId = -1;
        private Vector2 _trackpadLastTouchPos;
        private float _trackpadTouchStartTime;
        private float _trackpadTotalDistMoved;
        private int _trackpadSecondTouchId = -1;
        private int _trackpadLeftClickFrames = 0;
        private int _trackpadRightClickFrames = 0;

        // Edit layout mode tracking
        private int _activeDragTarget = 0; // 0: None, 1: Joystick, 2: Buttons
        private int _dragTouchId = -1;
        private Rectangle _btnDoneEditRect;

        // Settings modal rectangles (recalculated dynamically)
        private Rectangle _settingsModalRect;
        private Rectangle _settingsCloseRect;
        private Rectangle _btnOpacityMinus;
        private Rectangle _btnOpacityPlus;
        private Rectangle _btnScaleMinus;
        private Rectangle _btnScalePlus;
        private Rectangle _btnHandedness;
        private Rectangle _btnDeadzone;
        private Rectangle _btnToggleKeyboard;
        private Rectangle _btnToggleMenu;
        private Rectangle _btnToggleMouseMode;
        private Rectangle _btnSensitivityMinus;
        private Rectangle _btnSensitivityPlus;
        private Rectangle _btnEditLayout;
        private Rectangle _btnResetDefaults;
        private Rectangle _btnSaveClose;

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
            Settings.Load();
            _trackpadCursorPos = new Vector2(graphicsDevice.Viewport.Width / 2f, graphicsDevice.Viewport.Height / 2f);
            UpdateLayout(graphicsDevice.Viewport.Width, graphicsDevice.Viewport.Height);
            GenerateTextures(graphicsDevice);
            EnsureReflection();
        }

        public void UpdateLayout(int width, int height)
        {
            if (width <= 0) width = 1792;
            if (height <= 0) height = 828;

            _viewportWidth = width;
            _viewportHeight = height;

            float baseScale = Math.Max(1.0f, height / 720.0f) * Settings.Scale;
            float btnSize = 58f * baseScale;
            _joystickRadius = 75f * baseScale;
            float spacing = 58f * baseScale;

            if (Settings.CustomPositionsSet)
            {
                _joystickCenter = new Vector2(Settings.JoystickNormX * width, Settings.JoystickNormY * height);
                _buttonsCenter = new Vector2(Settings.ButtonsNormX * width, Settings.ButtonsNormY * height);
            }
            else
            {
                if (!Settings.LeftHanded)
                {
                    // Default right-handed: Joystick on bottom-left, buttons on bottom-right
                    _joystickCenter = new Vector2(130f * baseScale, height - (130f * baseScale));
                    _buttonsCenter = new Vector2(width - (130f * baseScale), height - (120f * baseScale));
                }
                else
                {
                    // Left-handed: Joystick on bottom-right, buttons on bottom-left
                    _joystickCenter = new Vector2(width - (130f * baseScale), height - (130f * baseScale));
                    _buttonsCenter = new Vector2(130f * baseScale, height - (120f * baseScale));
                }
            }

            _currentStickPos = _joystickCenter;
            _joystickBaseRect = new Rectangle(
                (int)(_joystickCenter.X - _joystickRadius),
                (int)(_joystickCenter.Y - _joystickRadius),
                (int)(_joystickRadius * 2),
                (int)(_joystickRadius * 2)
            );

            // Diamond layout for Action buttons
            _btnARect = new Rectangle((int)(_buttonsCenter.X - btnSize / 2f), (int)(_buttonsCenter.Y + spacing / 1.3f - btnSize / 2f), (int)btnSize, (int)btnSize); // Bottom: A
            _btnXRect = new Rectangle((int)(_buttonsCenter.X - spacing - btnSize / 2f), (int)(_buttonsCenter.Y - btnSize / 2f), (int)btnSize, (int)btnSize);          // Left: X
            _btnYRect = new Rectangle((int)(_buttonsCenter.X - btnSize / 2f), (int)(_buttonsCenter.Y - spacing - btnSize / 2f), (int)btnSize, (int)btnSize);          // Top: Y
            _btnBRect = new Rectangle((int)(_buttonsCenter.X + spacing - btnSize / 2f), (int)(_buttonsCenter.Y - btnSize / 2f), (int)btnSize, (int)btnSize);          // Right: B

            // Top utility buttons
            float topY = 16f;
            float topBtnH = 34f;
            float topBtnW = 54f;
            float curX = width - 16f;

            // 1. Toggle visibility button
            curX -= topBtnW;
            _btnToggleRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);

            // 2. Settings button
            curX -= (topBtnW + 8f);
            _btnSettingsRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);

            // 3. Keyboard button
            if (Settings.ShowKeyboardBtn)
            {
                curX -= (topBtnW + 8f);
                _btnKeyboardRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);
            }
            else
            {
                _btnKeyboardRect = Rectangle.Empty;
            }

            // 4. Menu button
            if (Settings.ShowMenuBtn)
            {
                float menuW = 64f;
                curX -= (menuW + 8f);
                _btnMenuRect = new Rectangle((int)curX, (int)topY, (int)menuW, (int)topBtnH);
            }
            else
            {
                _btnMenuRect = Rectangle.Empty;
            }

            // Edit mode "DONE" button
            _btnDoneEditRect = new Rectangle(width / 2 - 60, 20, 120, 44);

            // Settings Modal layout
            int modalW = Math.Min(680, width - 40);
            int modalH = Math.Min(530, height - 30);
            _settingsModalRect = new Rectangle((width - modalW) / 2, (height - modalH) / 2, modalW, modalH);

            _settingsCloseRect = new Rectangle(_settingsModalRect.Right - 38, _settingsModalRect.Top + 8, 30, 30);

            int startY = _settingsModalRect.Top + 48;
            int rowSpacing = 37;
            int ctrlX = _settingsModalRect.Left + 260;

            // Row 0: Opacity
            _btnOpacityMinus = new Rectangle(ctrlX, startY, 40, 28);
            _btnOpacityPlus = new Rectangle(ctrlX + 110, startY, 40, 28);

            // Row 1: Scale
            _btnScaleMinus = new Rectangle(ctrlX, startY + rowSpacing, 40, 28);
            _btnScalePlus = new Rectangle(ctrlX + 110, startY + rowSpacing, 40, 28);

            // Row 2: Handedness
            _btnHandedness = new Rectangle(ctrlX, startY + rowSpacing * 2, 170, 28);

            // Row 3: Deadzone
            _btnDeadzone = new Rectangle(ctrlX, startY + rowSpacing * 3, 170, 28);

            // Row 4: Extra Buttons
            _btnToggleKeyboard = new Rectangle(ctrlX, startY + rowSpacing * 4, 80, 28);
            _btnToggleMenu = new Rectangle(ctrlX + 90, startY + rowSpacing * 4, 80, 28);

            // Row 5: Mouse Mode (Point & Click / Trackpad / Disabled)
            _btnToggleMouseMode = new Rectangle(ctrlX, startY + rowSpacing * 5, 170, 28);

            // Row 6: Trackpad Sensitivity
            _btnSensitivityMinus = new Rectangle(ctrlX, startY + rowSpacing * 6, 40, 28);
            _btnSensitivityPlus = new Rectangle(ctrlX + 110, startY + rowSpacing * 6, 40, 28);

            // Row 7: Reposition controls
            _btnEditLayout = new Rectangle(_settingsModalRect.Left + 28, startY + rowSpacing * 7 + 4, modalW - 56, 32);

            // Row 8: Reset & Close
            int botBtnW = (modalW - 70) / 2;
            int botY = _settingsModalRect.Bottom - 44;
            _btnResetDefaults = new Rectangle(_settingsModalRect.Left + 28, botY, botBtnW, 34);
            _btnSaveClose = new Rectangle(_settingsModalRect.Left + 42 + botBtnW, botY, botBtnW, 34);
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

            // Handle momentary click frames from trackpad taps
            if (_trackpadLeftClickFrames > 0)
            {
                SimulatedMouseLeftDown = true;
                _trackpadLeftClickFrames--;
            }
            if (_trackpadRightClickFrames > 0)
            {
                SimulatedMouseRightDown = true;
                _trackpadRightClickFrames--;
            }

            // 1. Process Settings Menu Interaction
            // 1. Process Settings Menu Interaction
            if (IsSettingsOpen)
            {
                _trackpadPrimaryTouchId = -1;
                _trackpadSecondTouchId = -1;
                _stickTouchId = -1;
                UpdateSettingsTouches(touches);
                ForwardInputToGame();
                return;
            }

            // 2. Process Edit Layout (Drag & Reposition) Interaction
            if (IsEditLayoutMode)
            {
                _trackpadPrimaryTouchId = -1;
                _trackpadSecondTouchId = -1;
                _stickTouchId = -1;
                UpdateEditModeTouches(touches);
                ForwardInputToGame();
                return;
            }

            // 3. Normal Overlay Interaction
            // Watchdog: verify active touch IDs still exist in the current touches collection
            bool trackpadPrimaryFound = false;
            bool trackpadSecondFound = false;
            bool stickTouchFound = false;

            foreach (var touch in touches)
            {
                if (touch.Id == _trackpadPrimaryTouchId && touch.State != TouchLocationState.Released)
                    trackpadPrimaryFound = true;
                if (touch.Id == _trackpadSecondTouchId && touch.State != TouchLocationState.Released)
                    trackpadSecondFound = true;
                if (touch.Id == _stickTouchId && touch.State != TouchLocationState.Released)
                    stickTouchFound = true;
            }

            if (!trackpadPrimaryFound && _trackpadPrimaryTouchId != -1)
            {
                _trackpadPrimaryTouchId = -1;
            }
            if (!trackpadSecondFound && _trackpadSecondTouchId != -1)
            {
                _trackpadSecondTouchId = -1;
            }
            if (!stickTouchFound && _stickTouchId != -1)
            {
                _stickTouchId = -1;
                _currentStickPos = _joystickCenter;
                LeftStick = Vector2.Zero;
            }

            // Clamp cursor position and prevent NaN / Infinity
            if (float.IsNaN(_trackpadCursorPos.X) || float.IsInfinity(_trackpadCursorPos.X) ||
                float.IsNaN(_trackpadCursorPos.Y) || float.IsInfinity(_trackpadCursorPos.Y))
            {
                _trackpadCursorPos = new Vector2(_viewportWidth / 2f, _viewportHeight / 2f);
            }
            _trackpadCursorPos.X = Math.Clamp(_trackpadCursorPos.X, 0f, Math.Max(10f, _viewportWidth - 1));
            _trackpadCursorPos.Y = Math.Clamp(_trackpadCursorPos.Y, 0f, Math.Max(10f, _viewportHeight - 1));

            foreach (var touch in touches)
            {
                Vector2 pos = touch.Position;
                Point pt = new Point((int)pos.X, (int)pos.Y);

                // If this touch is already claimed as trackpad touch, process it directly
                if (touch.Id == _trackpadPrimaryTouchId || touch.Id == _trackpadSecondTouchId)
                {
                    HandleBackgroundTouch(touch, pt, gameTime);
                    continue;
                }

                // Settings button check
                if (touch.State == TouchLocationState.Pressed && _btnSettingsRect.Contains(pt))
                {
                    IsSettingsOpen = true;
                    _trackpadPrimaryTouchId = -1;
                    _trackpadSecondTouchId = -1;
                    _stickTouchId = -1;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    continue;
                }

                // Toggle visibility button check
                if (touch.State == TouchLocationState.Pressed && _btnToggleRect.Contains(pt))
                {
                    IsVisible = !IsVisible;
                    continue;
                }

                // Keyboard manual summon button check
                if (Settings.ShowKeyboardBtn && touch.State == TouchLocationState.Pressed && _btnKeyboardRect.Contains(pt))
                {
                    VirtualKeyboardManager.PromptManualInput(text =>
                    {
                        FeedTextToGame(text);
                    });
                    continue;
                }

                // Game Menu button check (top bar)
                if (Settings.ShowMenuBtn && touch.State == TouchLocationState.Pressed && _btnMenuRect.Contains(pt))
                {
                    ButtonMenu = true;
                    continue;
                }

                if (!IsVisible)
                {
                    HandleBackgroundTouch(touch, pt, gameTime);
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
                        UpdateJoystickVector(pos);
                    }
                    continue;
                }
                else if (_stickTouchId == -1 && touch.State == TouchLocationState.Pressed && _joystickBaseRect.Contains(pt))
                {
                    _stickTouchId = touch.Id;
                    UpdateJoystickVector(pos);
                    continue;
                }

                // Virtual gamepad action buttons
                if (_btnARect.Contains(pt))
                {
                    ButtonA = true;
                    continue;
                }
                if (_btnXRect.Contains(pt))
                {
                    ButtonX = true;
                    continue;
                }
                if (_btnYRect.Contains(pt))
                {
                    ButtonY = true;
                    continue;
                }
                if (_btnBRect.Contains(pt))
                {
                    ButtonB = true;
                    continue;
                }

                // Touch on background area (outside virtual buttons)
                HandleBackgroundTouch(touch, pt, gameTime);
            }

            ForwardInputToGame();
        }

        private void HandleBackgroundTouch(TouchLocation touch, Point pt, GameTime gameTime)
        {
            if (Settings.MouseControlMode == MouseMode.Disabled)
            {
                return;
            }

            if (Settings.MouseControlMode == MouseMode.PointAndClick)
            {
                // Direct Point and Click: cursor jumps to touch coordinate
                SimulatedMousePosition = pt;
                SimulatedMouseLeftDown = (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved);
                return;
            }

            if (Settings.MouseControlMode == MouseMode.Trackpad)
            {
                HandleTrackpadTouch(touch, gameTime);
            }
        }

        private void HandleTrackpadTouch(TouchLocation touch, GameTime gameTime)
        {
            float curTime = (float)gameTime.TotalGameTime.TotalSeconds;

            if (touch.State == TouchLocationState.Pressed || (_trackpadPrimaryTouchId == -1 && touch.State == TouchLocationState.Moved))
            {
                if (_trackpadPrimaryTouchId == -1)
                {
                    _trackpadPrimaryTouchId = touch.Id;
                    _trackpadLastTouchPos = touch.Position;
                    _trackpadTouchStartTime = curTime;
                    _trackpadTotalDistMoved = 0f;
                }
                else if (_trackpadSecondTouchId == -1 && touch.Id != _trackpadPrimaryTouchId)
                {
                    // Second finger touch down
                    _trackpadSecondTouchId = touch.Id;
                }
            }
            else if (touch.State == TouchLocationState.Moved)
            {
                if (touch.Id == _trackpadPrimaryTouchId)
                {
                    Vector2 delta = touch.Position - _trackpadLastTouchPos;
                    _trackpadLastTouchPos = touch.Position;
                    _trackpadTotalDistMoved += delta.Length();

                    _trackpadCursorPos += delta * Settings.TrackpadSensitivity;
                    _trackpadCursorPos.X = Math.Clamp(_trackpadCursorPos.X, 0f, Math.Max(10f, _viewportWidth - 1));
                    _trackpadCursorPos.Y = Math.Clamp(_trackpadCursorPos.Y, 0f, Math.Max(10f, _viewportHeight - 1));
                }
            }
            else if (touch.State == TouchLocationState.Released)
            {
                if (touch.Id == _trackpadPrimaryTouchId)
                {
                    float duration = curTime - _trackpadTouchStartTime;

                    // Check if two-finger tap occurred (Right Click)
                    if (_trackpadSecondTouchId != -1)
                    {
                        _trackpadRightClickFrames = 8;
                        _trackpadSecondTouchId = -1;
                    }
                    // Single-finger tap (Left Click): short tap with minimal movement
                    else if (duration < 0.35f && _trackpadTotalDistMoved < 25f)
                    {
                        _trackpadLeftClickFrames = 8;
                    }

                    _trackpadPrimaryTouchId = -1;
                }
                else if (touch.Id == _trackpadSecondTouchId)
                {
                    // Second finger released while primary was down: trigger Right Click
                    if (_trackpadPrimaryTouchId != -1)
                    {
                        _trackpadRightClickFrames = 8;
                    }
                    _trackpadSecondTouchId = -1;
                }
            }
        }

        private void UpdateSettingsTouches(TouchCollection touches)
        {
            foreach (var touch in touches)
            {
                if (touch.State != TouchLocationState.Pressed) continue;

                Point pt = new Point((int)touch.Position.X, (int)touch.Position.Y);

                // Close button / Save & Close
                if (_settingsCloseRect.Contains(pt) || _btnSaveClose.Contains(pt))
                {
                    Settings.Save();
                    IsSettingsOpen = false;
                    break;
                }

                // Opacity controls
                if (_btnOpacityMinus.Contains(pt))
                {
                    Settings.Opacity = (float)Math.Round(Math.Clamp(Settings.Opacity - 0.10f, 0.20f, 1.0f), 2);
                    Settings.Save();
                    break;
                }
                if (_btnOpacityPlus.Contains(pt))
                {
                    Settings.Opacity = (float)Math.Round(Math.Clamp(Settings.Opacity + 0.10f, 0.20f, 1.0f), 2);
                    Settings.Save();
                    break;
                }

                // Scale controls
                if (_btnScaleMinus.Contains(pt))
                {
                    Settings.Scale = (float)Math.Round(Math.Clamp(Settings.Scale - 0.15f, 0.60f, 1.50f), 2);
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnScalePlus.Contains(pt))
                {
                    Settings.Scale = (float)Math.Round(Math.Clamp(Settings.Scale + 0.15f, 0.60f, 1.50f), 2);
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }

                // Handedness toggle
                if (_btnHandedness.Contains(pt))
                {
                    Settings.LeftHanded = !Settings.LeftHanded;
                    Settings.CustomPositionsSet = false;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }

                // Deadzone toggle (0.15 -> 0.25 -> 0.35 -> 0.15)
                if (_btnDeadzone.Contains(pt))
                {
                    if (Settings.Deadzone <= 0.18f) Settings.Deadzone = 0.25f;
                    else if (Settings.Deadzone <= 0.28f) Settings.Deadzone = 0.35f;
                    else Settings.Deadzone = 0.15f;
                    Settings.Save();
                    break;
                }

                // Keyboard button visibility toggle
                if (_btnToggleKeyboard.Contains(pt))
                {
                    Settings.ShowKeyboardBtn = !Settings.ShowKeyboardBtn;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }

                // Menu button visibility toggle
                if (_btnToggleMenu.Contains(pt))
                {
                    Settings.ShowMenuBtn = !Settings.ShowMenuBtn;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }

                // Mouse Mode toggle (Point & Click -> Trackpad -> Disabled -> Point & Click)
                if (_btnToggleMouseMode.Contains(pt))
                {
                    if (Settings.MouseControlMode == MouseMode.PointAndClick)
                        Settings.MouseControlMode = MouseMode.Trackpad;
                    else if (Settings.MouseControlMode == MouseMode.Trackpad)
                        Settings.MouseControlMode = MouseMode.Disabled;
                    else
                        Settings.MouseControlMode = MouseMode.PointAndClick;

                    Settings.Save();
                    break;
                }

                // Trackpad Sensitivity controls
                if (_btnSensitivityMinus.Contains(pt))
                {
                    Settings.TrackpadSensitivity = (float)Math.Round(Math.Clamp(Settings.TrackpadSensitivity - 0.20f, 0.40f, 3.0f), 1);
                    Settings.Save();
                    break;
                }
                if (_btnSensitivityPlus.Contains(pt))
                {
                    Settings.TrackpadSensitivity = (float)Math.Round(Math.Clamp(Settings.TrackpadSensitivity + 0.20f, 0.40f, 3.0f), 1);
                    Settings.Save();
                    break;
                }

                // Enter Edit Layout mode
                if (_btnEditLayout.Contains(pt))
                {
                    IsSettingsOpen = false;
                    IsEditLayoutMode = true;
                    _activeDragTarget = 0;
                    _dragTouchId = -1;
                    break;
                }

                // Reset defaults
                if (_btnResetDefaults.Contains(pt))
                {
                    Settings.ResetToDefaults();
                    _trackpadCursorPos = new Vector2(_viewportWidth / 2f, _viewportHeight / 2f);
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    break;
                }
            }
        }

        private void UpdateEditModeTouches(TouchCollection touches)
        {
            foreach (var touch in touches)
            {
                Vector2 pos = touch.Position;
                Point pt = new Point((int)pos.X, (int)pos.Y);

                // Done button check
                if (touch.State == TouchLocationState.Pressed && _btnDoneEditRect.Contains(pt))
                {
                    IsEditLayoutMode = false;
                    _activeDragTarget = 0;
                    _dragTouchId = -1;
                    Settings.Save();
                    return;
                }

                if (_dragTouchId == touch.Id)
                {
                    if (touch.State == TouchLocationState.Released)
                    {
                        _dragTouchId = -1;
                        _activeDragTarget = 0;
                        Settings.Save();
                    }
                    else
                    {
                        if (_activeDragTarget == 1) // Dragging Joystick
                        {
                            _joystickCenter = new Vector2(
                                Math.Clamp(pos.X, _joystickRadius + 10, _viewportWidth - _joystickRadius - 10),
                                Math.Clamp(pos.Y, _joystickRadius + 10, _viewportHeight - _joystickRadius - 10)
                            );
                            Settings.JoystickNormX = _joystickCenter.X / _viewportWidth;
                            Settings.JoystickNormY = _joystickCenter.Y / _viewportHeight;
                            Settings.CustomPositionsSet = true;
                            UpdateLayout(_viewportWidth, _viewportHeight);
                        }
                        else if (_activeDragTarget == 2) // Dragging Buttons Diamond
                        {
                            _buttonsCenter = new Vector2(
                                Math.Clamp(pos.X, 90f + 10, _viewportWidth - 90f - 10),
                                Math.Clamp(pos.Y, 90f + 10, _viewportHeight - 90f - 10)
                            );
                            Settings.ButtonsNormX = _buttonsCenter.X / _viewportWidth;
                            Settings.ButtonsNormY = _buttonsCenter.Y / _viewportHeight;
                            Settings.CustomPositionsSet = true;
                            UpdateLayout(_viewportWidth, _viewportHeight);
                        }
                    }
                }
                else if (_dragTouchId == -1 && (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved))
                {
                    // Check distance to Joystick center
                    if (Vector2.Distance(pos, _joystickCenter) <= _joystickRadius + 20)
                    {
                        _dragTouchId = touch.Id;
                        _activeDragTarget = 1;
                    }
                    // Check distance to Buttons center
                    else if (Vector2.Distance(pos, _buttonsCenter) <= 90f)
                    {
                        _dragTouchId = touch.Id;
                        _activeDragTarget = 2;
                    }
                }
            }
        }

        public void ForwardInputToGame()
        {
            try
            {
                EnsureReflection();

                // If configuring in settings or edit mode, clear all virtual buttons
                if (IsSettingsOpen || IsEditLayoutMode)
                {
                    ButtonA = false;
                    ButtonB = false;
                    ButtonX = false;
                    ButtonY = false;
                    ButtonMenu = false;
                    SimulatedMouseLeftDown = false;
                    SimulatedMouseRightDown = false;
                    LeftStick = Vector2.Zero;
                }

                // In Trackpad mode, mouse position is tied to the trackpad cursor
                if (Settings.MouseControlMode == MouseMode.Trackpad)
                {
                    SimulatedMousePosition = new Point((int)_trackpadCursorPos.X, (int)_trackpadCursorPos.Y);
                }

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
                if (DPadUp) activeKeys.Add(Keys.W);
                if (DPadDown) activeKeys.Add(Keys.S);
                if (DPadLeft) activeKeys.Add(Keys.A);
                if (DPadRight) activeKeys.Add(Keys.D);

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
                            DPadUp ? ButtonState.Pressed : ButtonState.Released,
                            DPadDown ? ButtonState.Pressed : ButtonState.Released,
                            DPadLeft ? ButtonState.Pressed : ButtonState.Released,
                            DPadRight ? ButtonState.Pressed : ButtonState.Released
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
                    if (Settings.MouseControlMode == MouseMode.Trackpad || SimulatedMouseLeftDown)
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
                        if (controlsActive && Settings.MouseControlMode != MouseMode.Trackpad)
                        {
                            _gamepadControlsField.SetValue(options, true);
                        }
                        else if (SimulatedMouseLeftDown || Settings.MouseControlMode == MouseMode.Trackpad)
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

            // 1. Draw Edit Layout Mode
            if (IsEditLayoutMode)
            {
                DrawEditLayoutScreen(spriteBatch);
                return;
            }

            // 2. Draw Settings Modal Dialog
            if (IsSettingsOpen)
            {
                DrawSettingsModal(spriteBatch);
                return;
            }

            // 3. Normal Gameplay Overlay
            float alpha = Settings.Opacity;

            // Draw utility buttons (top bar)
            DrawButton(spriteBatch, _btnToggleRect, IsVisible ? "HIDE" : "PAD", Color.DarkSlateGray * Math.Max(0.5f, alpha), Color.White, 2);
            DrawButton(spriteBatch, _btnSettingsRect, "SET", Color.DarkSlateGray * Math.Max(0.5f, alpha), Color.White, 2);

            if (Settings.ShowKeyboardBtn && !_btnKeyboardRect.IsEmpty)
            {
                DrawButton(spriteBatch, _btnKeyboardRect, "KEY", Color.DarkSlateBlue * Math.Max(0.5f, alpha), Color.White, 2);
            }

            if (Settings.ShowMenuBtn && !_btnMenuRect.IsEmpty)
            {
                DrawButton(spriteBatch, _btnMenuRect, "MENU", ButtonMenu ? Color.Orange * 0.9f : Color.DarkOrange * Math.Max(0.5f, alpha), Color.White, 2);
            }

            if (IsVisible)
            {
                // Draw joystick base & thumb
                DrawFilledRect(spriteBatch, _joystickBaseRect, Color.Black * (alpha * 0.5f));
                DrawRectBorder(spriteBatch, _joystickBaseRect, 2, Color.White * (alpha * 0.4f));

                Rectangle stickThumbRect = new Rectangle((int)(_currentStickPos.X - 25), (int)(_currentStickPos.Y - 25), 50, 50);
                DrawFilledRect(spriteBatch, stickThumbRect, Color.White * (alpha * 0.85f));
                DrawRectBorder(spriteBatch, stickThumbRect, 2, Color.Black * (alpha * 0.6f));

                // Draw Action buttons with text labels (A, X, Y, B)
                DrawButton(spriteBatch, _btnARect, "A", ButtonA ? Color.Lime * 0.95f : Color.DarkGreen * alpha, Color.White, 3);
                DrawButton(spriteBatch, _btnXRect, "X", ButtonX ? Color.CornflowerBlue * 0.95f : Color.DarkBlue * alpha, Color.White, 3);
                DrawButton(spriteBatch, _btnYRect, "Y", ButtonY ? Color.Yellow * 0.95f : Color.DarkGoldenrod * alpha, Color.White, 3);
                DrawButton(spriteBatch, _btnBRect, "B", ButtonB ? Color.Red * 0.95f : Color.DarkRed * alpha, Color.White, 3);
            }

            // Draw Trackpad virtual mouse cursor pointer on screen
            if (Settings.MouseControlMode == MouseMode.Trackpad)
            {
                OverlayFont.DrawMouseCursor(spriteBatch, _pixelTexture, _trackpadCursorPos, 2);

                // Small indicator when clicking
                if (SimulatedMouseLeftDown || ButtonA)
                {
                    Rectangle clickDot = new Rectangle((int)_trackpadCursorPos.X + 2, (int)_trackpadCursorPos.Y + 2, 6, 6);
                    DrawFilledRect(spriteBatch, clickDot, Color.Lime * 0.9f);
                }
                else if (SimulatedMouseRightDown || ButtonX)
                {
                    Rectangle clickDot = new Rectangle((int)_trackpadCursorPos.X + 2, (int)_trackpadCursorPos.Y + 2, 6, 6);
                    DrawFilledRect(spriteBatch, clickDot, Color.Cyan * 0.9f);
                }
            }
        }

        private void DrawEditLayoutScreen(SpriteBatch sb)
        {
            if (_pixelTexture == null) return;

            // Dim backdrop
            DrawFilledRect(sb, new Rectangle(0, 0, _viewportWidth, _viewportHeight), Color.Black * 0.5f);

            // Instructions banner
            Rectangle bannerRect = new Rectangle(_viewportWidth / 2 - 240, 20, 340, 44);
            DrawFilledRect(sb, bannerRect, Color.Black * 0.85f);
            DrawRectBorder(sb, bannerRect, 2, Color.Gold);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "DRAG CONTROLS TO MOVE", bannerRect, Color.Gold, 2);

            // DONE Button
            DrawButton(sb, _btnDoneEditRect, "DONE", Color.DarkGreen, Color.White, 2);
            DrawRectBorder(sb, _btnDoneEditRect, 2, Color.Lime);

            // Joystick with dashed highlight
            DrawFilledRect(sb, _joystickBaseRect, Color.Black * 0.4f);
            DrawRectBorder(sb, _joystickBaseRect, 3, _activeDragTarget == 1 ? Color.Lime : Color.Cyan);
            Rectangle stickThumbRect = new Rectangle((int)(_joystickCenter.X - 25), (int)(_joystickCenter.Y - 25), 50, 50);
            DrawFilledRect(sb, stickThumbRect, Color.White * 0.7f);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "STICK", _joystickBaseRect, Color.White * 0.9f, 2);

            // Buttons diamond with dashed highlight
            Rectangle buttonsBounds = new Rectangle(
                (int)(_buttonsCenter.X - 100),
                (int)(_buttonsCenter.Y - 100),
                200,
                200
            );
            DrawFilledRect(sb, buttonsBounds, Color.Black * 0.25f);
            DrawRectBorder(sb, buttonsBounds, 3, _activeDragTarget == 2 ? Color.Lime : Color.Cyan);

            DrawButton(sb, _btnARect, "A", Color.DarkGreen * 0.8f, Color.White, 3);
            DrawButton(sb, _btnXRect, "X", Color.DarkBlue * 0.8f, Color.White, 3);
            DrawButton(sb, _btnYRect, "Y", Color.DarkGoldenrod * 0.8f, Color.White, 3);
            DrawButton(sb, _btnBRect, "B", Color.DarkRed * 0.8f, Color.White, 3);
        }

        private void DrawSettingsModal(SpriteBatch sb)
        {
            if (_pixelTexture == null) return;

            // Dark full-screen overlay
            DrawFilledRect(sb, new Rectangle(0, 0, _viewportWidth, _viewportHeight), Color.Black * 0.75f);

            // Modal window body
            DrawFilledRect(sb, _settingsModalRect, new Color(24, 28, 36) * 0.98f);
            DrawRectBorder(sb, _settingsModalRect, 3, new Color(70, 90, 130));

            // Header title
            Rectangle titleRect = new Rectangle(_settingsModalRect.Left, _settingsModalRect.Top + 14, _settingsModalRect.Width, 24);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "TOUCH CONTROLS SETTINGS", titleRect, Color.Gold, 2);

            // Close button (X)
            DrawButton(sb, _settingsCloseRect, "X", Color.DarkRed * 0.9f, Color.White, 2);

            int startX = _settingsModalRect.Left + 28;
            int startY = _settingsModalRect.Top + 48;
            int rowSpacing = 37;

            // Row 0: Opacity
            OverlayFont.DrawString(sb, _pixelTexture, "OVERLAY OPACITY", new Vector2(startX, startY + 6), Color.White, 2);
            DrawButton(sb, _btnOpacityMinus, "-", Color.DarkSlateGray, Color.White, 2);
            string opacityStr = $"{(int)(Settings.Opacity * 100)}%";
            Rectangle opTextRect = new Rectangle(_btnOpacityMinus.Right, startY, _btnOpacityPlus.Left - _btnOpacityMinus.Right, 28);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, opacityStr, opTextRect, Color.Yellow, 2);
            DrawButton(sb, _btnOpacityPlus, "+", Color.DarkSlateGray, Color.White, 2);

            // Row 1: Scale
            OverlayFont.DrawString(sb, _pixelTexture, "OVERLAY SCALE", new Vector2(startX, startY + rowSpacing + 6), Color.White, 2);
            DrawButton(sb, _btnScaleMinus, "-", Color.DarkSlateGray, Color.White, 2);
            string scaleStr = $"{(int)(Settings.Scale * 100)}%";
            Rectangle scTextRect = new Rectangle(_btnScaleMinus.Right, startY + rowSpacing, _btnScalePlus.Left - _btnScaleMinus.Right, 28);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, scaleStr, scTextRect, Color.Yellow, 2);
            DrawButton(sb, _btnScalePlus, "+", Color.DarkSlateGray, Color.White, 2);

            // Row 2: Handedness
            OverlayFont.DrawString(sb, _pixelTexture, "LAYOUT PRESET", new Vector2(startX, startY + rowSpacing * 2 + 6), Color.White, 2);
            string handStr = Settings.LeftHanded ? "LEFT-HANDED" : "RIGHT-HANDED";
            DrawButton(sb, _btnHandedness, handStr, Settings.LeftHanded ? Color.Purple : Color.SteelBlue, Color.White, 2);

            // Row 3: Deadzone
            OverlayFont.DrawString(sb, _pixelTexture, "STICK DEADZONE", new Vector2(startX, startY + rowSpacing * 3 + 6), Color.White, 2);
            string dzStr = Settings.Deadzone <= 0.18f ? "LOW (0.15)" : (Settings.Deadzone <= 0.28f ? "NORMAL (0.25)" : "HIGH (0.35)");
            DrawButton(sb, _btnDeadzone, dzStr, Color.DarkSlateBlue, Color.White, 2);

            // Row 4: Extra Buttons
            OverlayFont.DrawString(sb, _pixelTexture, "KEYBOARD / MENU", new Vector2(startX, startY + rowSpacing * 4 + 6), Color.White, 2);
            DrawButton(sb, _btnToggleKeyboard, Settings.ShowKeyboardBtn ? "KEY: ON" : "KEY: OFF", Settings.ShowKeyboardBtn ? Color.DarkGreen : Color.DarkSlateGray, Color.White, 2);
            DrawButton(sb, _btnToggleMenu, Settings.ShowMenuBtn ? "MENU: ON" : "MENU: OFF", Settings.ShowMenuBtn ? Color.DarkGreen : Color.DarkSlateGray, Color.White, 2);

            // Row 5: Mouse Mode (Point & Click / Trackpad / Disabled)
            OverlayFont.DrawString(sb, _pixelTexture, "MOUSE MODE", new Vector2(startX, startY + rowSpacing * 5 + 6), Color.White, 2);
            string modeStr = Settings.MouseControlMode switch
            {
                MouseMode.PointAndClick => "POINT & CLICK",
                MouseMode.Trackpad => "TRACKPAD",
                _ => "DISABLED"
            };
            Color modeCol = Settings.MouseControlMode switch
            {
                MouseMode.PointAndClick => Color.DarkGreen,
                MouseMode.Trackpad => Color.Indigo,
                _ => Color.DarkSlateGray
            };
            DrawButton(sb, _btnToggleMouseMode, modeStr, modeCol, Color.White, 2);

            // Row 6: Trackpad Sensitivity
            OverlayFont.DrawString(sb, _pixelTexture, "TRACKPAD SPEED", new Vector2(startX, startY + rowSpacing * 6 + 6), Settings.MouseControlMode == MouseMode.Trackpad ? Color.White : Color.Gray, 2);
            DrawButton(sb, _btnSensitivityMinus, "-", Settings.MouseControlMode == MouseMode.Trackpad ? Color.DarkSlateGray : Color.Black * 0.4f, Color.White, 2);
            string sensStr = $"{Settings.TrackpadSensitivity:0.0}X";
            Rectangle sensTextRect = new Rectangle(_btnSensitivityMinus.Right, startY + rowSpacing * 6, _btnSensitivityPlus.Left - _btnSensitivityMinus.Right, 28);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, sensStr, sensTextRect, Settings.MouseControlMode == MouseMode.Trackpad ? Color.Yellow : Color.Gray, 2);
            DrawButton(sb, _btnSensitivityPlus, "+", Settings.MouseControlMode == MouseMode.Trackpad ? Color.DarkSlateGray : Color.Black * 0.4f, Color.White, 2);

            // Row 7: Reposition Controls button
            DrawButton(sb, _btnEditLayout, "REPOSITION CONTROLS (DRAG & DROP)", Color.Indigo, Color.White, 2);
            DrawRectBorder(sb, _btnEditLayout, 2, Color.MediumPurple);

            // Row 8: Reset & Close buttons
            DrawButton(sb, _btnResetDefaults, "RESET TO DEFAULTS", Color.DarkRed * 0.8f, Color.White, 2);
            DrawButton(sb, _btnSaveClose, "SAVE & CLOSE", Color.DarkGreen, Color.White, 2);
            DrawRectBorder(sb, _btnSaveClose, 2, Color.Lime);
        }

        private void DrawButton(SpriteBatch sb, Rectangle rect, string text, Color bgColor, Color textColor, int fontPixelSize)
        {
            if (_pixelTexture == null || rect.IsEmpty) return;

            DrawFilledRect(sb, rect, bgColor);
            DrawRectBorder(sb, rect, 2, Color.White * 0.25f);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, text, rect, textColor, fontPixelSize);
        }

        private void DrawFilledRect(SpriteBatch sb, Rectangle rect, Color color)
        {
            if (_pixelTexture != null && !rect.IsEmpty)
            {
                sb.Draw(_pixelTexture, rect, color);
            }
        }

        private void DrawRectBorder(SpriteBatch sb, Rectangle rect, int thickness, Color color)
        {
            if (_pixelTexture == null || rect.IsEmpty || thickness <= 0) return;

            // Top
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            // Bottom
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            // Left
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            // Right
            sb.Draw(_pixelTexture, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }

        private void GenerateTextures(GraphicsDevice gd)
        {
            _pixelTexture = new Texture2D(gd, 1, 1);
            _pixelTexture.SetData(new[] { Color.White });
        }
    }
}
