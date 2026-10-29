using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using xTile;
using xTile.Dimensions;
using xTile.Display;
using xTile.Layers;
using xTile.Tiles;

namespace SDViOSTouchControls
{
    /// <summary>
    /// Drop-in replacement for SMAPI's SDisplayDevice / XnaDisplayDevice on iOS.
    /// Explicitly implements IDisplayDevice.LoadTileSheet as a no-op during bulk Map.LoadTileSheets() calls
    /// so that background locations and unused tilesheets do not eagerly allocate uncompressed Texture2D
    /// assets in RAM/VRAM. Instead, tilesheet textures are loaded strictly on-demand in DrawTile() when
    /// a tile is actually rendered on screen, with automatic pruning when warping between locations.
    /// </summary>
    public sealed class LazyIosDisplayDevice : XnaDisplayDevice, IDisplayDevice
    {
        public static LazyIosDisplayDevice? Instance { get; private set; }

        public LazyIosDisplayDevice(ContentManager contentManager, GraphicsDevice graphicsDevice)
            : base(contentManager, graphicsDevice)
        {
            Instance = this;
        }

        public static void EnsureInstalled()
        {
            try
            {
                if (Game1.mapDisplayDevice is LazyIosDisplayDevice)
                    return;

                var content = Game1.content;
                var gd = Game1.graphics?.GraphicsDevice;
                if (content != null && gd != null && !gd.IsDisposed)
                {
                    Game1.mapDisplayDevice = new LazyIosDisplayDevice(content, gd);
                }
            }
            catch { }
        }

        /// <summary>
        /// Explicit interface implementation intercepted when xTile.Map.LoadTileSheets(IDisplayDevice) runs.
        /// Skips eager texture loading for all 120+ locations; DrawTile() loads only visible tilesheets on demand.
        /// </summary>
        void IDisplayDevice.LoadTileSheet(TileSheet tileSheet)
        {
            // Intentionally deferred until DrawTile() renders a tile from this TileSheet.
        }

        void IDisplayDevice.DisposeTileSheet(TileSheet tileSheet)
        {
            if (tileSheet != null)
            {
                m_tileSheetTextures.Remove(tileSheet);
            }
        }

        public void PruneTileSheets(Map? keepMap)
        {
            try
            {
                if (m_tileSheetTextures == null || m_tileSheetTextures.Count == 0)
                    return;

                if (keepMap == null)
                {
                    m_tileSheetTextures.Clear();
                    return;
                }

                List<TileSheet>? toRemove = null;
                foreach (var kvp in m_tileSheetTextures)
                {
                    if (kvp.Key == null || !ReferenceEquals(kvp.Key.Map, keepMap))
                    {
                        toRemove ??= new List<TileSheet>();
                        toRemove.Add(kvp.Key!);
                    }
                }

                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        m_tileSheetTextures.Remove(toRemove[i]);
                    }
                }
            }
            catch { }
        }

        public override void DrawTile(Tile? tile, Location location, float layerDepth)
        {
            if (tile == null || tile.TileSheet == null)
                return;

            xTile.Dimensions.Rectangle tileImageBounds = tile.TileSheet.GetTileImageBounds(tile.TileIndex);
            if (!m_tileSheetTextures.TryGetValue(tile.TileSheet, out Texture2D? value) || value == null || value.IsDisposed)
            {
                try
                {
                    base.LoadTileSheet(tile.TileSheet);
                    m_tileSheetTextures.TryGetValue(tile.TileSheet, out value);
                }
                catch
                {
                    return;
                }
            }

            if (value != null && !value.IsDisposed)
            {
                m_tilePosition.X = location.X;
                m_tilePosition.Y = location.Y;
                m_sourceRectangle.X = tileImageBounds.X;
                m_sourceRectangle.Y = tileImageBounds.Y;
                m_sourceRectangle.Width = tileImageBounds.Width;
                m_sourceRectangle.Height = tileImageBounds.Height;
                DrawImpl(tile, location, layerDepth, value);
            }
        }

        protected override void DrawImpl(Tile tile, Location location, float layerDepth, Texture2D tileSheetTexture)
        {
            float rotation = GetRotation(tile);
            SpriteEffects spriteEffects = GetSpriteEffects(tile);
            Microsoft.Xna.Framework.Rectangle sourceRectangle = m_sourceRectangle;
            Vector2 origin = new Vector2(sourceRectangle.Width / 2f, sourceRectangle.Height / 2f);
            m_tilePosition.X += origin.X * Layer.zoom;
            m_tilePosition.Y += origin.X * Layer.zoom;
            m_spriteBatchAlpha.Draw(tileSheetTexture, m_tilePosition, sourceRectangle, m_modulationColour, rotation, origin, Layer.zoom, spriteEffects, layerDepth);
        }

        private static SpriteEffects GetSpriteEffects(Tile tile)
        {
            if (!tile.Properties.TryGetValue("@Flip", out var value) || !int.TryParse(value, out int result))
            {
                return SpriteEffects.None;
            }
            return (SpriteEffects)result;
        }

        private static float GetRotation(Tile tile)
        {
            if (!tile.Properties.TryGetValue("@Rotation", out var value) || !int.TryParse(value, out int result))
            {
                return 0f;
            }
            result %= 360;
            if (result == 0)
            {
                return 0f;
            }
            return (float)(Math.PI / (180.0 / result));
        }
    }
}
