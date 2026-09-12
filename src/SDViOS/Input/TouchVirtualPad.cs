using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace SDViOS.Input
{
    public class TouchVirtualPad
    {
        public static TouchVirtualPad Instance { get; } = new TouchVirtualPad();

        public bool IsVisible { get; set; } = true;
        public bool AutoHideWhenGamepadConnected { get; set; } = true;

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
        public Point SimulatedMousePosition { get; private set; } = Point.Zero;
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
        private Texture2D? _circleTexture;

        public void Initialize(GraphicsDevice graphicsDevice)
        {
            UpdateLayout(graphicsDevice.Viewport.Width, graphicsDevice.Viewport.Height);
            GenerateTextures(graphicsDevice);
        }

        public void UpdateLayout(int width, int height)
        {
            float scale = Math.Max(1.0f, height / 720.0f);
            float btnSize = 54f * scale;
            _joystickRadius = 65f * scale;

            // Joystick base in bottom-left
            float stickBaseX = 110f * scale;
            float stickBaseY = height - (110f * scale);
            _joystickCenter = new Vector2(stickBaseX, stickBaseY);
            _currentStickPos = _joystickCenter;
            _joystickBaseRect = new Rectangle((int)(stickBaseX - _joystickRadius), (int)(stickBaseY - _joystickRadius), (int)(_joystickRadius * 2), (int)(_joystickRadius * 2));

            // Buttons diamond in bottom-right
            float rightCenterX = width - (120f * scale);
            float rightCenterY = height - (110f * scale);
            float spacing = 50f * scale;

            _btnARect = new Rectangle((int)rightCenterX, (int)(rightCenterY + spacing / 1.5f), (int)btnSize, (int)btnSize); // Bottom: Action (A)
            _btnXRect = new Rectangle((int)(rightCenterX - spacing), (int)rightCenterY, (int)btnSize, (int)btnSize);          // Left: Tool (X)
            _btnYRect = new Rectangle((int)rightCenterX, (int)(rightCenterY - spacing), (int)btnSize, (int)btnSize);          // Top: Menu (Y)
            _btnBRect = new Rectangle((int)(rightCenterX + spacing), (int)rightCenterY, (int)btnSize, (int)btnSize);          // Right: Cancel (B)

            // Top utility buttons
            _btnMenuRect = new Rectangle(width - (int)(140f * scale), (int)(15f * scale), (int)btnSize, (int)(btnSize * 0.75f));
            _btnToggleRect = new Rectangle(width - (int)(60f * scale), (int)(15f * scale), (int)(45f * scale), (int)(30f * scale));
        }

        public void Update(GameTime gameTime)
        {
            var gpState = GamePad.GetState(PlayerIndex.One);
            if (AutoHideWhenGamepadConnected && gpState.IsConnected)
            {
                // When gamepad is active, skip virtual controls
                ResetStates();
                return;
            }

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
                    // If overlay hidden, all touches act as mouse taps/drags
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

                // Button checks
                if (_btnARect.Contains(pt)) ButtonA = true;
                else if (_btnXRect.Contains(pt)) ButtonX = true;
                else if (_btnYRect.Contains(pt)) ButtonY = true;
                else if (_btnBRect.Contains(pt)) ButtonB = true;
                else if (_btnMenuRect.Contains(pt)) ButtonMenu = true;
                else
                {
                    // Touch outside controls acts as direct game interaction / mouse
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
            spriteBatch.Draw(_pixelTexture, _btnToggleRect, Color.Black * 0.4f);

            if (!IsVisible) return;

            // Draw joystick base & thumb
            spriteBatch.Draw(_pixelTexture, _joystickBaseRect, Color.Black * 0.25f);
            Rectangle stickThumbRect = new Rectangle((int)(_currentStickPos.X - 25), (int)(_currentStickPos.Y - 25), 50, 50);
            spriteBatch.Draw(_pixelTexture, stickThumbRect, Color.White * 0.5f);

            // Draw buttons
            DrawButton(spriteBatch, _btnARect, ButtonA ? Color.Lime * 0.7f : Color.Black * 0.35f);
            DrawButton(spriteBatch, _btnXRect, ButtonX ? Color.CornflowerBlue * 0.7f : Color.Black * 0.35f);
            DrawButton(spriteBatch, _btnYRect, ButtonY ? Color.Yellow * 0.7f : Color.Black * 0.35f);
            DrawButton(spriteBatch, _btnBRect, ButtonB ? Color.Red * 0.7f : Color.Black * 0.35f);
            DrawButton(spriteBatch, _btnMenuRect, ButtonMenu ? Color.Orange * 0.7f : Color.Black * 0.35f);
        }

        private void DrawButton(SpriteBatch sb, Rectangle rect, Color color)
        {
            sb.Draw(_pixelTexture!, rect, color);
        }

        private void GenerateTextures(GraphicsDevice gd)
        {
            _pixelTexture = new Texture2D(gd, 1, 1);
            _pixelTexture.SetData(new[] { Color.White });
        }
    }
}
