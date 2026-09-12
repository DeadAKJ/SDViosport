using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDViOS.Diagnostics;

namespace SDViOS.Input
{
    public class TouchOverlay : DrawableGameComponent
    {
        private SpriteBatch? _spriteBatch;
        private bool _padInitialized;

        public TouchOverlay(Game game) : base(game)
        {
            DrawOrder = int.MaxValue; // Always render on top of game graphics
            UpdateOrder = int.MinValue; // Update before game handles input
        }

        public override void Initialize()
        {
            base.Initialize();
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
        }

        public override void Update(GameTime gameTime)
        {
            TryInitializePad();
            if (_padInitialized)
            {
                TouchVirtualPad.Instance.Update(gameTime);
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
                try
                {
                    _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
                    TouchVirtualPad.Instance.Draw(_spriteBatch);
                    _spriteBatch.End();
                }
                catch
                {
                    // Ignore transient draw errors during scene transitions
                }
            }

            base.Draw(gameTime);
        }
    }
}
