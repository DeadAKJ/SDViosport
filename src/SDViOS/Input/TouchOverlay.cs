using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SDViOS.Input
{
    public class TouchOverlay : DrawableGameComponent
    {
        private SpriteBatch? _spriteBatch;

        public TouchOverlay(Game game) : base(game)
        {
            DrawOrder = int.MaxValue; // Always render on top of game graphics
            UpdateOrder = int.MinValue; // Update before game handles input
        }

        public override void Initialize()
        {
            base.Initialize();
            TouchVirtualPad.Instance.Initialize(GraphicsDevice);
        }

        protected override void LoadContent()
        {
            _spriteBatch = new SpriteBatch(GraphicsDevice);
            base.LoadContent();
        }

        public override void Update(GameTime gameTime)
        {
            TouchVirtualPad.Instance.Update(gameTime);
            base.Update(gameTime);
        }

        public override void Draw(GameTime gameTime)
        {
            if (_spriteBatch == null) return;

            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            TouchVirtualPad.Instance.Draw(_spriteBatch);
            _spriteBatch.End();

            base.Draw(gameTime);
        }
    }
}
