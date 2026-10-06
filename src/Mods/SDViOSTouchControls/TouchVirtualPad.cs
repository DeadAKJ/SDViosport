using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using StardewValley;

namespace SDViOSTouchControls
{
    public class TouchVirtualPad
    {
        public static TouchVirtualPad Instance { get; } = new TouchVirtualPad();

        public TouchOverlaySettings Settings => TouchOverlaySettings.Instance;

        public bool IsVisible { get; set; } = true;
        public bool IsSettingsOpen { get; set; } = false;
        public bool IsEditLayoutMode { get; set; } = false;

        // Current virtual control states
        public Vector2 LeftStick { get; private set; } = Vector2.Zero;
        public bool ButtonA { get; private set; } = false;
        public bool ButtonB { get; private set; } = false;
        public bool ButtonX { get; private set; } = false;
        public bool ButtonY { get; private set; } = false;
        public bool ButtonMenu { get; private set; } = false;

        // Visual states for drawing
        public bool IsVisualButtonA => ButtonA;
        public bool IsVisualButtonX => ButtonX;
        public bool IsVisualButtonY => _yTouchActive || _yPulseFrames > 0;
        public bool IsVisualButtonB => _bTouchActive || _bPulseFrames > 0;
        public bool IsVisualButtonMenu => _menuTouchActive || _menuPulseFrames > 0;

        // Pulse and debounce state for menu toggle / cancel buttons
        private bool _yTouchActive = false;
        private int _yPulseFrames = 0;
        private double _lastYPressTime = 0;

        private bool _bTouchActive = false;
        private int _bPulseFrames = 0;
        private double _lastBPressTime = 0;

        private bool _menuTouchActive = false;
        private int _menuPulseFrames = 0;
        private double _lastMenuPressTime = 0;

        public bool DPadUp => LeftStick.Y < -Settings.Deadzone;
        public bool DPadDown => LeftStick.Y > Settings.Deadzone;
        public bool DPadLeft => LeftStick.X < -Settings.Deadzone;
        public bool DPadRight => LeftStick.X > Settings.Deadzone;

        public bool HasActiveInput => LeftStick != Vector2.Zero || ButtonA || ButtonB || ButtonX || ButtonY || ButtonMenu;

        private static void AddOptionKeys(HashSet<Keys> set, InputButton[]? buttons)
        {
            if (buttons == null) return;
            foreach (var b in buttons)
            {
                if (b.key != Keys.None)
                    set.Add(b.key);
            }
        }

        public KeyboardState CurrentSimulatedKeyboardState
        {
            get
            {
                var keys = new HashSet<Keys>();
                var opt = Game1.options;

                if (DPadUp)
                {
                    keys.Add(Keys.W);
                    keys.Add(Keys.Up);
                    AddOptionKeys(keys, opt?.moveUpButton);
                }
                if (DPadDown)
                {
                    keys.Add(Keys.S);
                    keys.Add(Keys.Down);
                    AddOptionKeys(keys, opt?.moveDownButton);
                }
                if (DPadLeft)
                {
                    keys.Add(Keys.A);
                    keys.Add(Keys.Left);
                    AddOptionKeys(keys, opt?.moveLeftButton);
                }
                if (DPadRight)
                {
                    keys.Add(Keys.D);
                    keys.Add(Keys.Right);
                    AddOptionKeys(keys, opt?.moveRightButton);
                }

                bool isGamepad = opt != null && opt.gamepadControls;

                if (ButtonA)
                {
                    keys.Add(Keys.X);
                    keys.Add(Keys.Space);
                    AddOptionKeys(keys, opt?.actionButton);
                }
                if (ButtonX)
                {
                    keys.Add(Keys.C);
                    AddOptionKeys(keys, opt?.useToolButton);
                }
                if (ButtonY && !isGamepad)
                {
                    keys.Add(Keys.E);
                    AddOptionKeys(keys, opt?.menuButton);
                }
                if ((ButtonB || ButtonMenu) && !isGamepad)
                {
                    keys.Add(Keys.Escape);
                    AddOptionKeys(keys, opt?.cancelButton);
                }

                return new KeyboardState(keys.ToArray());
            }
        }

        public GamePadState CurrentSimulatedGamePadState
        {
            get
            {
                bool isGamepad = Game1.options != null && Game1.options.gamepadControls;
                var buttons = new GamePadButtons(
                    (ButtonA ? Buttons.A : 0) |
                    ((ButtonB && isGamepad) ? Buttons.B : 0) |
                    (ButtonX ? Buttons.X : 0) |
                    ((ButtonY && isGamepad) ? Buttons.Y : 0) |
                    ((ButtonMenu && isGamepad) ? Buttons.Start : 0)
                );
                var dpad = new GamePadDPad(
                    DPadUp ? ButtonState.Pressed : ButtonState.Released,
                    DPadDown ? ButtonState.Pressed : ButtonState.Released,
                    DPadLeft ? ButtonState.Pressed : ButtonState.Released,
                    DPadRight ? ButtonState.Pressed : ButtonState.Released
                );
                var thumbsticks = new GamePadThumbSticks(new Vector2(LeftStick.X, -LeftStick.Y), Vector2.Zero);
                return new GamePadState(thumbsticks, new GamePadTriggers(), buttons, dpad);
            }
        }

        /// <summary>
        /// Gamepad state that is ALWAYS connected (IsConnected never changes).
        /// Vanilla Game1.CheckGamepadMode shows "gamepad connected/disconnected" and opens a GameMenu (pause on
        /// unplug) whenever IsConnected changes, and it also flips options.gamepadControls itself — so IsConnected
        /// must never depend on gamepadControls or touch state, or it becomes a menu-opening feedback loop.
        /// Keyboard/mouse mode: connected but idle (no buttons), so touching the pad never auto-switches to gamepad mode.
        /// Gamepad mode: connected with the simulated buttons/sticks.
        /// </summary>
        public GamePadState EffectiveGamePadState
        {
            get
            {
                bool isGamepad = Game1.options != null && Game1.options.gamepadControls;
                if (isGamepad)
                    return CurrentSimulatedGamePadState; // public ctor => IsConnected = true
                return IdleConnectedState;
            }
        }

        private static readonly GamePadState IdleConnectedState =
            new GamePadState(new GamePadThumbSticks(Vector2.Zero, Vector2.Zero), new GamePadTriggers(0f, 0f), new GamePadButtons((Buttons)0),
                new GamePadDPad(ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released));


        // Mouse simulation
        public Point SimulatedMousePosition { get; private set; } = new Point(896, 414);
        public bool SimulatedMouseLeftDown { get; private set; } = false;
        public bool SimulatedMouseRightDown { get; private set; } = false;
        private int _pointClickTouchId = -1;
        private int _pointClickHoldFrames = 0;

        public bool IsMouseOverridden => !IsSettingsOpen && !IsEditLayoutMode && Settings.MouseControlMode != MouseMode.Disabled;

        public bool IsLeftClickActive => SimulatedMouseLeftDown || (Settings.MouseControlMode == MouseMode.Trackpad && ButtonA);
        public bool IsRightClickActive => SimulatedMouseRightDown || (Settings.MouseControlMode == MouseMode.Trackpad && ButtonX);

        public MouseState CurrentSimulatedMouseState
        {
            get
            {
                int mx = Math.Clamp(SimulatedMousePosition.X, 0, _viewportWidth > 0 ? _viewportWidth - 1 : 1791);
                int my = Math.Clamp(SimulatedMousePosition.Y, 0, _viewportHeight > 0 ? _viewportHeight - 1 : 827);

                bool isLeft = IsLeftClickActive;
                bool isRight = IsRightClickActive;

                return new MouseState(
                    mx,
                    my,
                    0,
                    isLeft ? ButtonState.Pressed : ButtonState.Released,
                    ButtonState.Released,
                    isRight ? ButtonState.Pressed : ButtonState.Released,
                    ButtonState.Released,
                    ButtonState.Released
                );
            }
        }

        public void OnGameSetMousePosition(int x, int y)
        {
            float uiScale = 1.0f;
            try
            {
                if (Game1.options != null && Game1.options.uiScale > 0.01f)
                {
                    uiScale = Game1.options.uiScale;
                }
            }
            catch { }

            int screenX = (int)Math.Round(x / uiScale);
            int screenY = (int)Math.Round(y / uiScale);
            SimulatedMousePosition = new Point(screenX, screenY);
            _trackpadCursorPos = new Vector2(screenX, screenY);
        }

        // Layout bounds
        private int _viewportWidth = 1792;
        private int _viewportHeight = 828;
        private int _rawScreenWidth = 1792;
        private int _rawScreenHeight = 828;

        private Vector2 _joystickCenter;
        private float _joystickRadius = 80f;
        private Vector2 _buttonsCenter;

        private Rectangle _joystickBaseRect;
        private Rectangle _btnARect;
        private Rectangle _btnBRect;
        private Rectangle _btnXRect;
        private Rectangle _btnYRect;
        private Rectangle _btnToggleRect;
        private Rectangle _btnSettingsRect;
        private Rectangle _btnKeyboardRect;
        private Rectangle _btnMenuRect;
        private Rectangle _btnDoneEditRect;

        // Settings modal rectangles
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

        // Trackpad state
        private Vector2 _trackpadCursorPos = new Vector2(896, 414);
        private int _trackpadPrimaryTouchId = -1;
        private int _trackpadSecondTouchId = -1;
        private Vector2 _trackpadLastTouchPos;
        private float _trackpadTouchStartTime;
        private float _trackpadTotalDistMoved;
        private bool _trackpadHadSecondTouch;
        private int _trackpadLeftClickFrames = 0;
        private int _trackpadRightClickFrames = 0;
        private float _lastLeftClickTime = 0f;
        private float _lastRightClickTime = 0f;

        // Active touches
        private int _stickTouchId = -1;
        private Vector2 _currentStickPos;
        private int _activeDragTarget = 0;
        private int _dragTouchId = -1;

        private Texture2D? _pixelTexture;

        // Reflection caches for Stardew Valley Game1.input & options
        private static bool _reflectionInitialized = false;
        private static object? _inputInstance = null;
        private static FieldInfo? _currentMouseStateField = null;
        private static FieldInfo? _currentGamepadStateField = null;
        private static FieldInfo? _currentKeyboardStateField = null;
        private static FieldInfo? _simulatedMousePositionField = null;
        private static FieldInfo? _sMouseStateBackingField = null;
        private static FieldInfo? _lastCursorMotionWasMouseField = null;
        private static FieldInfo? _mousePrimaryWindowField = null;
        private static FieldInfo? _gameWindowMouseStateField = null;

        private static Type? _game1Type = null;
        private static PropertyInfo? _optionsProp = null;
        private static FieldInfo? _gamepadControlsField = null;
        private static MethodInfo? _setKeysMethod = null;

        public static Action<string>? LogAction { get; set; }

        private static void Log(string message)
        {
            LogAction?.Invoke(message);
        }

        public void Initialize(GraphicsDevice graphicsDevice)
        {
            Settings.Load();
            _viewportWidth = graphicsDevice.Viewport.Width > 0 ? graphicsDevice.Viewport.Width : 1792;
            _viewportHeight = graphicsDevice.Viewport.Height > 0 ? graphicsDevice.Viewport.Height : 828;
            _rawScreenWidth = _viewportWidth;
            _rawScreenHeight = _viewportHeight;

            _trackpadCursorPos = new Vector2(_viewportWidth / 2f, _viewportHeight / 2f);
            UpdateLayout(_viewportWidth, _viewportHeight);
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
                    // Default right-handed: Joystick on bottom-left, buttons on bottom-right (ergonomically placed near bottom corners)
                    _joystickCenter = new Vector2(130f * baseScale, height - (95f * baseScale));
                    _buttonsCenter = new Vector2(width - (130f * baseScale), height - (95f * baseScale));
                }
                else
                {
                    // Left-handed: Joystick on bottom-right, buttons on bottom-left
                    _joystickCenter = new Vector2(width - (130f * baseScale), height - (95f * baseScale));
                    _buttonsCenter = new Vector2(130f * baseScale, height - (95f * baseScale));
                }
            }

            if (_stickTouchId == -1)
            {
                _currentStickPos = _joystickCenter;
            }
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

            // Top utility buttons (top-left aligned to avoid notch, rounded corners, and Stardew clock/money HUD)
            float topY = 16f;
            float topBtnH = 34f;
            float topBtnW = 54f;
            float curX = 40f;

            // 1. Toggle visibility button
            _btnToggleRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);
            curX += (topBtnW + 8f);

            // 2. Settings button
            _btnSettingsRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);
            curX += (topBtnW + 8f);

            // 3. Keyboard button
            if (Settings.ShowKeyboardBtn)
            {
                _btnKeyboardRect = new Rectangle((int)curX, (int)topY, (int)topBtnW, (int)topBtnH);
                curX += (topBtnW + 8f);
            }
            else
            {
                _btnKeyboardRect = Rectangle.Empty;
            }

            // 4. Menu button
            if (Settings.ShowMenuBtn)
            {
                float menuW = 64f;
                _btnMenuRect = new Rectangle((int)curX, (int)topY, (int)menuW, (int)topBtnH);
                curX += (menuW + 8f);
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

            _btnOpacityMinus = new Rectangle(ctrlX, startY, 40, 28);
            _btnOpacityPlus = new Rectangle(ctrlX + 110, startY, 40, 28);
            _btnScaleMinus = new Rectangle(ctrlX, startY + rowSpacing, 40, 28);
            _btnScalePlus = new Rectangle(ctrlX + 110, startY + rowSpacing, 40, 28);
            _btnHandedness = new Rectangle(ctrlX, startY + rowSpacing * 2, 170, 28);
            _btnDeadzone = new Rectangle(ctrlX, startY + rowSpacing * 3, 170, 28);
            _btnToggleKeyboard = new Rectangle(ctrlX, startY + rowSpacing * 4, 80, 28);
            _btnToggleMenu = new Rectangle(ctrlX + 90, startY + rowSpacing * 4, 80, 28);
            _btnToggleMouseMode = new Rectangle(ctrlX, startY + rowSpacing * 5, 170, 28);
            _btnSensitivityMinus = new Rectangle(ctrlX, startY + rowSpacing * 6, 40, 28);
            _btnSensitivityPlus = new Rectangle(ctrlX + 110, startY + rowSpacing * 6, 40, 28);
            _btnEditLayout = new Rectangle(_settingsModalRect.Left + 28, startY + rowSpacing * 7 + 4, modalW - 56, 32);

            int botBtnW = (modalW - 70) / 2;
            int botY = _settingsModalRect.Bottom - 44;
            _btnResetDefaults = new Rectangle(_settingsModalRect.Left + 28, botY, botBtnW, 34);
            _btnSaveClose = new Rectangle(_settingsModalRect.Left + 42 + botBtnW, botY, botBtnW, 34);
        }

        public void Update(GameTime gameTime)
        {
            TouchCollection touches = TouchPanel.GetState();

            // Detect raw screen dimensions from backbuffer
            int rawW = 1792;
            int rawH = 828;
            try
            {
                if (Game1.graphics?.GraphicsDevice != null)
                {
                    rawW = Game1.graphics.GraphicsDevice.PresentationParameters.BackBufferWidth;
                    rawH = Game1.graphics.GraphicsDevice.PresentationParameters.BackBufferHeight;
                }
            }
            catch { }
            _rawScreenWidth = rawW > 0 ? rawW : _viewportWidth;
            _rawScreenHeight = rawH > 0 ? rawH : _viewportHeight;

            double curTime = gameTime.TotalGameTime.TotalSeconds;

            if (_yPulseFrames > 0) _yPulseFrames--;
            if (_bPulseFrames > 0) _bPulseFrames--;
            if (_menuPulseFrames > 0) _menuPulseFrames--;

            bool yTouchFound = false;
            bool bTouchFound = false;
            bool menuTouchFound = false;

            // Reset momentary states
            ButtonA = false;
            ButtonX = false;
            SimulatedMouseRightDown = false;

            // Update hold frames for point-and-click tap recognition
            if (_pointClickHoldFrames > 0)
            {
                SimulatedMouseLeftDown = true;
                _pointClickHoldFrames--;
            }
            else if (_pointClickTouchId == -1)
            {
                SimulatedMouseLeftDown = false;
            }

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
            if (IsSettingsOpen)
            {
                _trackpadPrimaryTouchId = -1;
                _trackpadSecondTouchId = -1;
                _stickTouchId = -1;
                _pointClickTouchId = -1;
                _pointClickHoldFrames = 0;
                SimulatedMouseLeftDown = false;
                SimulatedMouseRightDown = false;
                UpdateSettingsTouches(touches);
                ForwardInputToGame();
                return;
            }

            // 2. Process Edit Layout Interaction
            if (IsEditLayoutMode)
            {
                _trackpadPrimaryTouchId = -1;
                _trackpadSecondTouchId = -1;
                _stickTouchId = -1;
                _pointClickTouchId = -1;
                _pointClickHoldFrames = 0;
                SimulatedMouseLeftDown = false;
                SimulatedMouseRightDown = false;
                UpdateEditModeTouches(touches);
                ForwardInputToGame();
                return;
            }

            // 3. Process Gamepad & Mouse Touches
            bool stickTouchPresent = false;
            bool pointClickTouchPresent = false;

            foreach (var touch in touches)
            {
                // Map raw touch coordinate to viewport coordinate
                Point pt = MapTouchToViewport(touch.Position);

                // Top utility bar check
                if (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved)
                {
                    if (_btnToggleRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed)
                            IsVisible = !IsVisible;
                        continue;
                    }
                    if (_btnSettingsRect.Contains(pt))
                    {
                        if (touch.State == TouchLocationState.Pressed)
                            IsSettingsOpen = true;
                        continue;
                    }
                    if (Settings.ShowKeyboardBtn && !_btnKeyboardRect.IsEmpty && _btnKeyboardRect.Contains(pt))
                    {
                        // Open keyboard
                        continue;
                    }
                    if (Settings.ShowMenuBtn && !_btnMenuRect.IsEmpty && _btnMenuRect.Contains(pt))
                    {
                        menuTouchFound = true;
                        if (!_menuTouchActive && (curTime - _lastMenuPressTime > 0.25))
                        {
                            _menuPulseFrames = 1;
                            _lastMenuPressTime = curTime;
                            Log($"[TVP_DEBUG] Touch on Button Menu! touchState={touch.State}, touchId={touch.Id}, pt=({pt.X},{pt.Y}), curMenu={Game1.activeClickableMenu?.GetType().Name ?? "none"}, isGamepad={Game1.options?.gamepadControls}");
                        }
                        continue;
                    }
                }

                // If overlay is hidden, treat all touches as screen clicks
                if (!IsVisible)
                {
                    HandleBackgroundTouch(touch, pt, gameTime);
                    continue;
                }

                // Check Action Buttons
                bool hitButton = false;
                if (_btnARect.Contains(pt)) { ButtonA = true; hitButton = true; }
                if (_btnXRect.Contains(pt)) { ButtonX = true; hitButton = true; }

                if (_btnYRect.Contains(pt))
                {
                    yTouchFound = true;
                    hitButton = true;
                    if (!_yTouchActive && (curTime - _lastYPressTime > 0.25))
                    {
                        _yPulseFrames = 1;
                        _lastYPressTime = curTime;
                        Log($"[TVP_DEBUG] Touch on Button Y! touchState={touch.State}, touchId={touch.Id}, pt=({pt.X},{pt.Y}), curMenu={Game1.activeClickableMenu?.GetType().Name ?? "none"}, isGamepad={Game1.options?.gamepadControls}");
                    }
                }

                if (_btnBRect.Contains(pt))
                {
                    bTouchFound = true;
                    hitButton = true;
                    if (!_bTouchActive && (curTime - _lastBPressTime > 0.25))
                    {
                        _bPulseFrames = 1;
                        _lastBPressTime = curTime;
                        Log($"[TVP_DEBUG] Touch on Button B! touchState={touch.State}, touchId={touch.Id}, pt=({pt.X},{pt.Y}), curMenu={Game1.activeClickableMenu?.GetType().Name ?? "none"}, isGamepad={Game1.options?.gamepadControls}");
                    }
                }

                if (hitButton) continue;

                // Check Joystick
                if (_stickTouchId == touch.Id)
                {
                    if (touch.State == TouchLocationState.Released)
                    {
                        _stickTouchId = -1;
                        LeftStick = Vector2.Zero;
                        _currentStickPos = _joystickCenter;
                    }
                    else
                    {
                        stickTouchPresent = true;
                        UpdateJoystickVector(new Vector2(pt.X, pt.Y));
                    }
                    continue;
                }
                else if (_stickTouchId == -1 && (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved))
                {
                    float dist = Vector2.Distance(new Vector2(pt.X, pt.Y), _joystickCenter);
                    if (_joystickBaseRect.Contains(pt) || dist <= _joystickRadius * 2.0f)
                    {
                        _stickTouchId = touch.Id;
                        stickTouchPresent = true;
                        if (_trackpadPrimaryTouchId == touch.Id) _trackpadPrimaryTouchId = -1;
                        if (_trackpadSecondTouchId == touch.Id) _trackpadSecondTouchId = -1;
                        if (_pointClickTouchId == touch.Id) _pointClickTouchId = -1;
                        UpdateJoystickVector(new Vector2(pt.X, pt.Y));
                        continue;
                    }
                }

                // Check active point & click touch
                if (touch.Id == _pointClickTouchId)
                {
                    pointClickTouchPresent = true;
                }

                // Touch on background area (outside virtual pad buttons)
                HandleBackgroundTouch(touch, pt, gameTime);
            }

            if (!stickTouchPresent && _stickTouchId != -1)
            {
                _stickTouchId = -1;
                LeftStick = Vector2.Zero;
                _currentStickPos = _joystickCenter;
            }

            if (!pointClickTouchPresent && _pointClickTouchId != -1)
            {
                _pointClickTouchId = -1;
            }

            _yTouchActive = yTouchFound;
            _bTouchActive = bTouchFound;
            _menuTouchActive = menuTouchFound;

            ButtonY = _yPulseFrames > 0;
            ButtonB = _bPulseFrames > 0;
            ButtonMenu = _menuPulseFrames > 0;

            ForwardInputToGame();
        }

        private Point MapTouchToViewport(Vector2 rawPos)
        {
            if (_rawScreenWidth <= 0 || _rawScreenHeight <= 0 || _viewportWidth <= 0 || _viewportHeight <= 0)
            {
                return new Point((int)rawPos.X, (int)rawPos.Y);
            }

            float normX = rawPos.X / _rawScreenWidth;
            float normY = rawPos.Y / _rawScreenHeight;
            return new Point((int)(normX * _viewportWidth), (int)(normY * _viewportHeight));
        }

        private void HandleBackgroundTouch(TouchLocation touch, Point pt, GameTime gameTime)
        {
            if (Settings.MouseControlMode == MouseMode.Disabled) return;

            // Never process touches on the virtual pad as background mouse events!
            if (IsVisible)
            {
                if (_joystickBaseRect.Contains(pt) || Vector2.Distance(new Vector2(pt.X, pt.Y), _joystickCenter) <= _joystickRadius * 2.0f)
                    return;
                if (_btnARect.Contains(pt) || _btnBRect.Contains(pt) || _btnXRect.Contains(pt) || _btnYRect.Contains(pt) ||
                    Vector2.Distance(new Vector2(pt.X, pt.Y), _buttonsCenter) <= 125f)
                    return;
                if (_btnToggleRect.Contains(pt) || _btnSettingsRect.Contains(pt) ||
                    (!_btnKeyboardRect.IsEmpty && _btnKeyboardRect.Contains(pt)) ||
                    (!_btnMenuRect.IsEmpty && _btnMenuRect.Contains(pt)))
                    return;
            }

            if (Game1.activeClickableMenu != null)
            {
                Log($"[TVP_DEBUG] HandleBackgroundTouch while menu={Game1.activeClickableMenu.GetType().Name}! touchState={touch.State}, touchId={touch.Id}, pt=({pt.X},{pt.Y}), mouseMode={Settings.MouseControlMode}");
            }

            if (Settings.MouseControlMode == MouseMode.PointAndClick)
            {
                SimulatedMousePosition = pt;

                if (touch.State == TouchLocationState.Pressed)
                {
                    _pointClickTouchId = touch.Id;
                    SimulatedMouseLeftDown = true;
                    _pointClickHoldFrames = 2; // Hold for 2 ticks to guarantee clean rising & falling edge
                    Game1.lastCursorMotionWasMouse = true;
                }
                else if (touch.State == TouchLocationState.Moved)
                {
                    if (_pointClickTouchId == touch.Id || _pointClickTouchId == -1)
                    {
                        _pointClickTouchId = touch.Id;
                        SimulatedMouseLeftDown = true;
                        _pointClickHoldFrames = 2;
                        Game1.lastCursorMotionWasMouse = true;
                    }
                }
                else if (touch.State == TouchLocationState.Released)
                {
                    if (_pointClickTouchId == -1)
                    {
                        // Ultra-fast micro-tap delivered directly as Released!
                        SimulatedMouseLeftDown = true;
                        _pointClickHoldFrames = 2;
                        Game1.lastCursorMotionWasMouse = true;
                    }
                    else if (_pointClickTouchId == touch.Id)
                    {
                        _pointClickTouchId = -1;
                    }
                }
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

            if (_trackpadPrimaryTouchId == -1)
            {
                if (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved)
                {
                    _trackpadPrimaryTouchId = touch.Id;
                    _trackpadLastTouchPos = touch.Position;
                    _trackpadTouchStartTime = curTime;
                    _trackpadTotalDistMoved = 0f;
                    _trackpadHadSecondTouch = false;
                    Game1.lastCursorMotionWasMouse = true;
                }
            }
            else if (touch.Id == _trackpadPrimaryTouchId)
            {
                if (touch.State == TouchLocationState.Moved)
                {
                    Vector2 delta = touch.Position - _trackpadLastTouchPos;
                    _trackpadLastTouchPos = touch.Position;
                    _trackpadTotalDistMoved += delta.Length();

                    _trackpadCursorPos += delta * Settings.TrackpadSensitivity;
                    _trackpadCursorPos.X = Math.Clamp(_trackpadCursorPos.X, 0, _viewportWidth - 1);
                    _trackpadCursorPos.Y = Math.Clamp(_trackpadCursorPos.Y, 0, _viewportHeight - 1);
                    SimulatedMousePosition = new Point((int)_trackpadCursorPos.X, (int)_trackpadCursorPos.Y);
                    Game1.lastCursorMotionWasMouse = true;
                }
                else if (touch.State == TouchLocationState.Released)
                {
                    float duration = curTime - _trackpadTouchStartTime;
                    if (!_trackpadHadSecondTouch && duration < 0.45f && _trackpadTotalDistMoved < 30f && (curTime - _lastLeftClickTime > 0.15f))
                    {
                        _trackpadLeftClickFrames = 3;
                        SimulatedMouseLeftDown = true;
                        _lastLeftClickTime = curTime;
                        Game1.lastCursorMotionWasMouse = true;
                    }
                    _trackpadPrimaryTouchId = -1;
                }
            }
            else if (_trackpadSecondTouchId == -1)
            {
                if (touch.State == TouchLocationState.Pressed || touch.State == TouchLocationState.Moved)
                {
                    _trackpadSecondTouchId = touch.Id;
                    _trackpadHadSecondTouch = true;
                }
            }
            else if (touch.Id == _trackpadSecondTouchId)
            {
                if (touch.State == TouchLocationState.Released)
                {
                    float duration = curTime - _trackpadTouchStartTime;
                    if (duration < 0.55f && _trackpadTotalDistMoved < 50f && (curTime - _lastRightClickTime > 0.25f))
                    {
                        _trackpadRightClickFrames = 3;
                        SimulatedMouseRightDown = true;
                        _lastRightClickTime = curTime;
                        Game1.lastCursorMotionWasMouse = true;
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
                Point pt = MapTouchToViewport(touch.Position);

                if (_settingsCloseRect.Contains(pt) || _btnSaveClose.Contains(pt))
                {
                    Settings.Save();
                    IsSettingsOpen = false;
                    break;
                }
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
                if (_btnHandedness.Contains(pt))
                {
                    Settings.LeftHanded = !Settings.LeftHanded;
                    Settings.CustomPositionsSet = false;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnDeadzone.Contains(pt))
                {
                    if (Settings.Deadzone <= 0.18f) Settings.Deadzone = 0.25f;
                    else if (Settings.Deadzone <= 0.28f) Settings.Deadzone = 0.35f;
                    else Settings.Deadzone = 0.15f;
                    Settings.Save();
                    break;
                }
                if (_btnToggleKeyboard.Contains(pt))
                {
                    Settings.ShowKeyboardBtn = !Settings.ShowKeyboardBtn;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
                if (_btnToggleMenu.Contains(pt))
                {
                    Settings.ShowMenuBtn = !Settings.ShowMenuBtn;
                    UpdateLayout(_viewportWidth, _viewportHeight);
                    Settings.Save();
                    break;
                }
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
                if (_btnEditLayout.Contains(pt))
                {
                    IsSettingsOpen = false;
                    IsEditLayoutMode = true;
                    _activeDragTarget = 0;
                    _dragTouchId = -1;
                    break;
                }
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
                Point pt = MapTouchToViewport(touch.Position);
                Vector2 pos = new Vector2(pt.X, pt.Y);

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
                        if (_activeDragTarget == 1)
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
                        else if (_activeDragTarget == 2)
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
                else if (_dragTouchId == -1 && touch.State == TouchLocationState.Pressed)
                {
                    if (Vector2.Distance(pos, _joystickCenter) <= _joystickRadius * 1.5f)
                    {
                        _activeDragTarget = 1;
                        _dragTouchId = touch.Id;
                    }
                    else if (Vector2.Distance(pos, _buttonsCenter) <= 120f)
                    {
                        _activeDragTarget = 2;
                        _dragTouchId = touch.Id;
                    }
                }
            }
        }

        public void ForwardInputToGame()
        {
            try
            {
                EnsureReflection();

                var mouseState = CurrentSimulatedMouseState;

                // 1. MonoGame PrimaryWindow.MouseState
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

                // 2. StardewValley Game1.input fields & SMAPI MouseState backing field
                if (_inputInstance != null)
                {
                    _currentMouseStateField?.SetValue(_inputInstance, mouseState);
                    _simulatedMousePositionField?.SetValue(_inputInstance, SimulatedMousePosition);
                    _sMouseStateBackingField?.SetValue(_inputInstance, mouseState);
                }

                // 3. StardewValley Game1 mouse visibility & motion flags
                try
                {
                    if (IsMouseOverridden && (SimulatedMouseLeftDown || SimulatedMouseRightDown || _trackpadPrimaryTouchId != -1 || _pointClickTouchId != -1))
                    {
                        Game1.lastCursorMotionWasMouse = true;
                        Game1.mouseCursorTransparency = 1f;
                        Game1.wasMouseVisibleThisFrame = true;
                    }
                }
                catch { }

                // 4. Hardware Keyboard Emulation
                var activeKeys = new List<Keys>();
                if (DPadUp) { activeKeys.Add(Keys.W); activeKeys.Add(Keys.Up); }
                if (DPadDown) { activeKeys.Add(Keys.S); activeKeys.Add(Keys.Down); }
                if (DPadLeft) { activeKeys.Add(Keys.A); activeKeys.Add(Keys.Left); }
                if (DPadRight) { activeKeys.Add(Keys.D); activeKeys.Add(Keys.Right); }

                bool isGamepad = Game1.options != null && Game1.options.gamepadControls;

                if (ButtonA)
                {
                    activeKeys.Add(Keys.X);
                    activeKeys.Add(Keys.Space);
                }
                if (ButtonX) activeKeys.Add(Keys.C);
                if (ButtonY && !isGamepad) activeKeys.Add(Keys.E);
                if ((ButtonB || ButtonMenu) && !isGamepad) activeKeys.Add(Keys.Escape);

                if (ButtonY || ButtonB || ButtonMenu)
                {
                    Log($"[TVP_DEBUG] ForwardInputToGame: ButtonY={ButtonY}, ButtonB={ButtonB}, ButtonMenu={ButtonMenu}, isGamepad={isGamepad}, activeKeys=[{string.Join(",", activeKeys)}], curMenu={Game1.activeClickableMenu?.GetType().Name ?? "none"}");
                }

                try
                {
                    if (_setKeysMethod == null)
                    {
                        _setKeysMethod = typeof(Microsoft.Xna.Framework.Input.Keyboard).GetMethod("SetKeys", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    }
                    _setKeysMethod?.Invoke(null, new object[] { activeKeys });
                }
                catch { }

                // 5. Game1.input GamePad & Keyboard state
                if (_inputInstance != null)
                {
                    if (_currentKeyboardStateField != null)
                    {
                        _currentKeyboardStateField.SetValue(_inputInstance, new KeyboardState(activeKeys.ToArray()));
                    }
                    if (_currentGamepadStateField != null)
                    {
                        _currentGamepadStateField.SetValue(_inputInstance, EffectiveGamePadState);
                    }
                }

                // 6. Direct Farmer Movement Injection & Bed Waking
                try
                {
                    var player = Game1.player;
                    if (player != null)
                    {
                        // If player is sitting/in bed in the morning, waking them up enables movement!
                        if (player.IsSitting())
                        {
                            if (DPadUp || DPadDown || DPadLeft || DPadRight || ButtonA || ButtonX || LeftStick != Vector2.Zero)
                            {
                                player.StopSitting(false);
                                player.isSitting.Value = false;
                                player.sittingFurniture = null;
                            }
                        }

                        if (player.CanMove && Game1.activeClickableMenu == null && !Game1.eventUp && !Game1.freezeControls)
                        {
                            if (DPadUp) player.SetMovingUp(true);
                            else if (player.movementDirections.Contains(0)) player.SetMovingUp(false);

                            if (DPadRight) player.SetMovingRight(true);
                            else if (player.movementDirections.Contains(1)) player.SetMovingRight(false);

                            if (DPadDown) player.SetMovingDown(true);
                            else if (player.movementDirections.Contains(2)) player.SetMovingDown(false);

                            if (DPadLeft) player.SetMovingLeft(true);
                            else if (player.movementDirections.Contains(3)) player.SetMovingLeft(false);

                            if (!DPadUp && !DPadRight && !DPadDown && !DPadLeft && LeftStick == Vector2.Zero)
                            {
                                if (player.movementDirections.Count > 0)
                                {
                                    player.Halt();
                                }
                            }
                        }
                    }
                }
                catch { }

                // 7. Ensure Game1 cursor remains visible
                if (_lastCursorMotionWasMouseField != null && IsMouseOverridden && (SimulatedMouseLeftDown || SimulatedMouseRightDown || _trackpadPrimaryTouchId != -1 || _pointClickTouchId != -1))
                {
                    _lastCursorMotionWasMouseField.SetValue(null, true);
                }
            }
            catch { }
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
                        _optionsProp = g1Type.GetProperty("options", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                        if (_optionsProp != null)
                        {
                            var optType = _optionsProp.PropertyType;
                            _gamepadControlsField = optType.GetField("gamepadControls", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }

                        if (_inputInstance != null)
                        {
                            Type? cur = _inputInstance.GetType();
                            while (cur != null && cur != typeof(object))
                            {
                                if (_currentMouseStateField == null)
                                    _currentMouseStateField = cur.GetField("_currentMouseState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (_currentGamepadStateField == null)
                                    _currentGamepadStateField = cur.GetField("_currentGamepadState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (_currentKeyboardStateField == null)
                                    _currentKeyboardStateField = cur.GetField("_currentKeyboardState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (_simulatedMousePositionField == null)
                                    _simulatedMousePositionField = cur.GetField("_simulatedMousePosition", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (_sMouseStateBackingField == null)
                                    _sMouseStateBackingField = cur.GetField("<MouseState>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                cur = cur.BaseType;
                            }
                            _reflectionInitialized = true;
                            Log($"[TouchVirtualPad] Successfully hooked Stardew Valley input fields (mouseField={_currentMouseStateField != null}, simField={_simulatedMousePositionField != null}, sBackingField={_sMouseStateBackingField != null}).");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[TouchVirtualPad] Reflection hook warning: {ex.Message}");
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
            if (_pixelTexture == null)
            {
                GenerateTextures(spriteBatch.GraphicsDevice);
            }
            if (_pixelTexture == null) return;

            if (spriteBatch.GraphicsDevice != null)
            {
                var vp = spriteBatch.GraphicsDevice.Viewport;
                if (vp.Width > 0 && vp.Height > 0 && (_viewportWidth != vp.Width || _viewportHeight != vp.Height))
                {
                    UpdateLayout(vp.Width, vp.Height);
                }
            }

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

            // Top utility buttons
            DrawButton(spriteBatch, _btnToggleRect, IsVisible ? "HIDE" : "PAD", Color.DarkSlateGray * Math.Max(0.5f, alpha), Color.White, 2);
            DrawButton(spriteBatch, _btnSettingsRect, "SET", Color.DarkSlateGray * Math.Max(0.5f, alpha), Color.White, 2);

            if (Settings.ShowKeyboardBtn && !_btnKeyboardRect.IsEmpty)
            {
                DrawButton(spriteBatch, _btnKeyboardRect, "KEY", Color.DarkSlateBlue * Math.Max(0.5f, alpha), Color.White, 2);
            }

            if (Settings.ShowMenuBtn && !_btnMenuRect.IsEmpty)
            {
                DrawButton(spriteBatch, _btnMenuRect, "MENU", IsVisualButtonMenu ? Color.Orange * 0.9f : Color.DarkOrange * Math.Max(0.5f, alpha), Color.White, 2);
            }

            if (IsVisible)
            {
                // Draw joystick base & thumb
                DrawFilledRect(spriteBatch, _joystickBaseRect, Color.Black * (alpha * 0.65f));
                DrawRectBorder(spriteBatch, _joystickBaseRect, 2, Color.White * (alpha * 0.7f));

                Rectangle stickThumbRect = new Rectangle((int)(_currentStickPos.X - 28), (int)(_currentStickPos.Y - 28), 56, 56);
                DrawFilledRect(spriteBatch, stickThumbRect, Color.White * Math.Max(0.85f, alpha));
                DrawRectBorder(spriteBatch, stickThumbRect, 2, Color.Black * 0.9f);

                // Draw Action buttons with text labels (A, X, Y, B)
                DrawButton(spriteBatch, _btnARect, "A", ButtonA ? Color.Lime * 0.95f : Color.DarkGreen * Math.Max(0.75f, alpha), Color.White, 3);
                DrawButton(spriteBatch, _btnXRect, "X", ButtonX ? Color.CornflowerBlue * 0.95f : Color.DarkBlue * Math.Max(0.75f, alpha), Color.White, 3);
                DrawButton(spriteBatch, _btnYRect, "Y", IsVisualButtonY ? Color.Yellow * 0.95f : Color.DarkGoldenrod * Math.Max(0.75f, alpha), Color.White, 3);
                DrawButton(spriteBatch, _btnBRect, "B", IsVisualButtonB ? Color.Red * 0.95f : Color.DarkRed * Math.Max(0.75f, alpha), Color.White, 3);
            }

            // Visual feedback for trackpad clicks and point-and-click taps (Native Stardew Valley cursor draws the pointer)
            if (Settings.MouseControlMode == MouseMode.Trackpad)
            {
                if (IsLeftClickActive)
                {
                    Rectangle clickDot = new Rectangle(SimulatedMousePosition.X + 2, SimulatedMousePosition.Y + 2, 6, 6);
                    DrawFilledRect(spriteBatch, clickDot, Color.Lime * 0.9f);
                }
                else if (IsRightClickActive)
                {
                    Rectangle clickDot = new Rectangle(SimulatedMousePosition.X + 2, SimulatedMousePosition.Y + 2, 6, 6);
                    DrawFilledRect(spriteBatch, clickDot, Color.Cyan * 0.9f);
                }
            }
            else if (Settings.MouseControlMode == MouseMode.PointAndClick)
            {
                if (SimulatedMouseLeftDown)
                {
                    DrawFilledRect(spriteBatch, new Rectangle(SimulatedMousePosition.X - 5, SimulatedMousePosition.Y - 5, 10, 10), Color.Lime * 0.4f);
                    DrawRectBorder(spriteBatch, new Rectangle(SimulatedMousePosition.X - 8, SimulatedMousePosition.Y - 8, 16, 16), 2, Color.White * 0.6f);
                }
            }
        }

        private void DrawEditLayoutScreen(SpriteBatch sb)
        {
            if (_pixelTexture == null) return;

            DrawFilledRect(sb, new Rectangle(0, 0, _viewportWidth, _viewportHeight), Color.Black * 0.5f);

            Rectangle bannerRect = new Rectangle(_viewportWidth / 2 - 240, 20, 340, 44);
            DrawFilledRect(sb, bannerRect, Color.Black * 0.85f);
            DrawRectBorder(sb, bannerRect, 2, Color.Gold);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "DRAG CONTROLS TO MOVE", bannerRect, Color.Gold, 2);

            DrawFilledRect(sb, _btnDoneEditRect, Color.DarkGreen * 0.9f);
            DrawRectBorder(sb, _btnDoneEditRect, 2, Color.Lime);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "DONE", _btnDoneEditRect, Color.White, 2);

            // Highlight Joystick draggable zone
            DrawFilledRect(sb, _joystickBaseRect, Color.SlateBlue * 0.4f);
            DrawRectBorder(sb, _joystickBaseRect, 2, Color.MediumSlateBlue);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "STICK", _joystickBaseRect, Color.White, 2);

            // Highlight Buttons draggable zone
            Rectangle buttonsBounds = new Rectangle(
                (int)(_buttonsCenter.X - 110f),
                (int)(_buttonsCenter.Y - 110f),
                220,
                220
            );
            DrawFilledRect(sb, buttonsBounds, Color.SlateBlue * 0.4f);
            DrawRectBorder(sb, buttonsBounds, 2, Color.MediumSlateBlue);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "BUTTONS", buttonsBounds, Color.White, 2);
        }

        private void DrawSettingsModal(SpriteBatch sb)
        {
            if (_pixelTexture == null) return;

            DrawFilledRect(sb, new Rectangle(0, 0, _viewportWidth, _viewportHeight), Color.Black * 0.5f);

            // Modal box
            DrawFilledRect(sb, _settingsModalRect, new Color(20, 24, 34) * 0.95f);
            DrawRectBorder(sb, _settingsModalRect, 3, Color.Goldenrod);

            // Header
            Rectangle headerRect = new Rectangle(_settingsModalRect.Left + 20, _settingsModalRect.Top + 14, 300, 24);
            OverlayFont.DrawString(sb, _pixelTexture, "TOUCH CONTROLS SETTINGS", new Vector2(headerRect.X, headerRect.Y), Color.Gold, 2);

            // Close (X) button
            DrawFilledRect(sb, _settingsCloseRect, Color.DarkRed * 0.8f);
            DrawRectBorder(sb, _settingsCloseRect, 2, Color.Red);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, "X", _settingsCloseRect, Color.White, 2);

            int startX = _settingsModalRect.Left + 28;
            int startY = _settingsModalRect.Top + 48;
            int rowSpacing = 37;

            // Row 0: Opacity
            OverlayFont.DrawString(sb, _pixelTexture, "OPACITY", new Vector2(startX, startY + 6), Color.White, 2);
            DrawButton(sb, _btnOpacityMinus, "-", Color.DarkSlateGray, Color.White, 2);
            string opStr = $"{(int)(Settings.Opacity * 100)}%";
            Rectangle opTextRect = new Rectangle(_btnOpacityMinus.Right, startY, _btnOpacityPlus.Left - _btnOpacityMinus.Right, 28);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, opStr, opTextRect, Color.Yellow, 2);
            DrawButton(sb, _btnOpacityPlus, "+", Color.DarkSlateGray, Color.White, 2);

            // Row 1: Scale
            OverlayFont.DrawString(sb, _pixelTexture, "PAD SCALE", new Vector2(startX, startY + rowSpacing + 6), Color.White, 2);
            DrawButton(sb, _btnScaleMinus, "-", Color.DarkSlateGray, Color.White, 2);
            string scStr = $"{Settings.Scale:0.00}X";
            Rectangle scTextRect = new Rectangle(_btnScaleMinus.Right, startY + rowSpacing, _btnScalePlus.Left - _btnScaleMinus.Right, 28);
            OverlayFont.DrawCenteredString(sb, _pixelTexture, scStr, scTextRect, Color.Yellow, 2);
            DrawButton(sb, _btnScalePlus, "+", Color.DarkSlateGray, Color.White, 2);

            // Row 2: Handedness
            OverlayFont.DrawString(sb, _pixelTexture, "HANDEDNESS", new Vector2(startX, startY + rowSpacing * 2 + 6), Color.White, 2);
            DrawButton(sb, _btnHandedness, Settings.LeftHanded ? "LEFT HANDED" : "RIGHT HANDED", Color.DarkSlateBlue, Color.White, 2);

            // Row 3: Deadzone
            OverlayFont.DrawString(sb, _pixelTexture, "STICK DEADZONE", new Vector2(startX, startY + rowSpacing * 3 + 6), Color.White, 2);
            string dzStr = Settings.Deadzone <= 0.18f ? "LOW (0.15)" : (Settings.Deadzone <= 0.28f ? "NORMAL (0.25)" : "HIGH (0.35)");
            DrawButton(sb, _btnDeadzone, dzStr, Color.DarkSlateBlue, Color.White, 2);

            // Row 4: Extra Buttons
            OverlayFont.DrawString(sb, _pixelTexture, "KEYBOARD / MENU", new Vector2(startX, startY + rowSpacing * 4 + 6), Color.White, 2);
            DrawButton(sb, _btnToggleKeyboard, Settings.ShowKeyboardBtn ? "KEY: ON" : "KEY: OFF", Settings.ShowKeyboardBtn ? Color.DarkGreen : Color.DarkSlateGray, Color.White, 2);
            DrawButton(sb, _btnToggleMenu, Settings.ShowMenuBtn ? "MENU: ON" : "MENU: OFF", Settings.ShowMenuBtn ? Color.DarkGreen : Color.DarkSlateGray, Color.White, 2);

            // Row 5: Mouse Mode
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

            // Row 7: Reposition Controls
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
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            sb.Draw(_pixelTexture, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            sb.Draw(_pixelTexture, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }

        private void GenerateTextures(GraphicsDevice gd)
        {
            if (_pixelTexture == null && gd != null)
            {
                _pixelTexture = new Texture2D(gd, 1, 1);
                _pixelTexture.SetData(new[] { Color.White });
            }
        }
    }
}
