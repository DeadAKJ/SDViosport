using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDViOS.Diagnostics;

namespace SDViOS.Input
{
    public class TouchOverlay : DrawableGameComponent
    {
        private SpriteBatch? _spriteBatch;
        private bool _padInitialized;
        private static FieldInfo? _sbBeginCalledField;

        public static void SafeResetSpriteBatch(SpriteBatch? sb)
        {
            if (sb == null) return;
            try
            {
                if (_sbBeginCalledField == null)
                {
                    _sbBeginCalledField = typeof(SpriteBatch).GetField("_beginCalled", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_sbBeginCalledField != null && (bool)(_sbBeginCalledField.GetValue(sb) ?? false))
                {
                    try { sb.End(); } catch { }
                    _sbBeginCalledField.SetValue(sb, false);
                }
            }
            catch { }
        }

        public TouchOverlay(Game game) : base(game)
        {
            DrawOrder = int.MaxValue; // Always render on top of game graphics
            UpdateOrder = int.MinValue; // Update before game handles input
        }

        public override void Initialize()
        {
            base.Initialize();
            try
            {
                TouchPanel.EnableMouseTouchPoint = true;
                TouchPanel.EnableMouseGestures = true;
            }
            catch { }
            TryInitializePad();
        }

        protected override void LoadContent()
        {
            TryInitializePad();
            if (GraphicsDevice != null && _spriteBatch == null)
            {
                try
                {
                    _spriteBatch = new SpriteBatch(GraphicsDevice);
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[TouchOverlay] Error creating SpriteBatch in LoadContent: {ex.Message}");
                }
            }
            base.LoadContent();
        }

        private void TryInitializePad()
        {
            if (!_padInitialized && GraphicsDevice != null)
            {
                try
                {
                    TouchVirtualPad.Instance.Initialize(GraphicsDevice);
                    _padInitialized = true;
                    EngineLogger.Log("[TouchOverlay] TouchVirtualPad initialized successfully.");
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[TouchOverlay] Error initializing TouchVirtualPad: {ex.Message}");
                }
            }
            else if (_padInitialized && GraphicsDevice != null)
            {
                TouchVirtualPad.Instance.UpdateLayout(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            }
        }

        public override void Update(GameTime gameTime)
        {
            TryInitializePad();
            if (_padInitialized)
            {
                TouchVirtualPad.Instance.Update(gameTime);
                TouchVirtualPad.Instance.ForwardInputToGame();
            }
            base.Update(gameTime);
        }

        public override void Draw(GameTime gameTime)
        {
            TryInitializePad();

            if (_spriteBatch == null && GraphicsDevice != null)
            {
                try
                {
                    _spriteBatch = new SpriteBatch(GraphicsDevice);
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[TouchOverlay] Error creating SpriteBatch in Draw: {ex.Message}");
                }
            }

            if (_spriteBatch != null && _padInitialized)
            {
                SafeResetSpriteBatch(_spriteBatch);
                bool began = false;
                try
                {
                    _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
                    began = true;
                    TouchVirtualPad.Instance.Draw(_spriteBatch);
                }
                catch (Exception ex)
                {
                    EngineLogger.LogWarning($"[TouchOverlay] Error in Draw: {ex.Message}");
                }
                finally
                {
                    if (began)
                    {
                        try
                        {
                            _spriteBatch.End();
                        }
                        catch
                        {
                            SafeResetSpriteBatch(_spriteBatch);
                        }
                    }
                }
            }

            base.Draw(gameTime);
        }
    }
}
