using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace SDViOS.Input
{
    public static class OverlayFont
    {
        private static readonly Dictionary<char, byte[]> Glyphs = new Dictionary<char, byte[]>
        {
            { ' ', new byte[] { 0, 0, 0, 0, 0, 0, 0 } },
            { 'A', new byte[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 } },
            { 'B', new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 } },
            { 'C', new byte[] { 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110 } },
            { 'D', new byte[] { 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110 } },
            { 'E', new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 } },
            { 'F', new byte[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 } },
            { 'G', new byte[] { 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110 } },
            { 'H', new byte[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 } },
            { 'I', new byte[] { 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 } },
            { 'J', new byte[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100 } },
            { 'K', new byte[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001 } },
            { 'L', new byte[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 } },
            { 'M', new byte[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 } },
            { 'N', new byte[] { 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001 } },
            { 'O', new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 } },
            { 'P', new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 } },
            { 'Q', new byte[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101 } },
            { 'R', new byte[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 } },
            { 'S', new byte[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 } },
            { 'T', new byte[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 } },
            { 'U', new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 } },
            { 'V', new byte[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100 } },
            { 'W', new byte[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001 } },
            { 'X', new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 } },
            { 'Y', new byte[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 } },
            { 'Z', new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111 } },
            { '0', new byte[] { 0b01110, 0b10011, 0b10101, 0b10101, 0b11001, 0b10001, 0b01110 } },
            { '1', new byte[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 } },
            { '2', new byte[] { 0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111 } },
            { '3', new byte[] { 0b01110, 0b10001, 0b00001, 0b00110, 0b00001, 0b10001, 0b01110 } },
            { '4', new byte[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 } },
            { '5', new byte[] { 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110 } },
            { '6', new byte[] { 0b01110, 0b10000, 0b11110, 0b10001, 0b10001, 0b10001, 0b01110 } },
            { '7', new byte[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 } },
            { '8', new byte[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 } },
            { '9', new byte[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110 } },
            { '+', new byte[] { 0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000 } },
            { '-', new byte[] { 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000 } },
            { '%', new byte[] { 0b11001, 0b11010, 0b00100, 0b01000, 0b10000, 0b01011, 0b10011 } },
            { ':', new byte[] { 0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b00000 } },
            { '.', new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100 } },
            { '[', new byte[] { 0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110 } },
            { ']', new byte[] { 0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110 } },
            { '(', new byte[] { 0b00110, 0b01000, 0b10000, 0b10000, 0b10000, 0b01000, 0b00110 } },
            { ')', new byte[] { 0b01100, 0b00010, 0b00001, 0b00001, 0b00001, 0b00010, 0b01100 } },
            { '/', new byte[] { 0b00001, 0b00010, 0b00100, 0b00100, 0b01000, 0b10000, 0b10000 } },
            { '<', new byte[] { 0b00010, 0b00100, 0b01000, 0b10000, 0b01000, 0b00100, 0b00010 } },
            { '>', new byte[] { 0b01000, 0b00100, 0b00010, 0b00001, 0b00010, 0b00100, 0b01000 } },
            { '=', new byte[] { 0b00000, 0b11111, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000 } },
            { '!', new byte[] { 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100 } },
            { '?', new byte[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b00000, 0b00100 } },
            { '_', new byte[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111 } },
        };

        public static void DrawString(SpriteBatch sb, Texture2D pixel, string text, Vector2 position, Color color, int pixelSize = 2)
        {
            if (string.IsNullOrEmpty(text) || sb == null || pixel == null) return;

            int startX = (int)position.X;
            int currentX = startX;
            int currentY = (int)position.Y;
            int charWidth = 5 * pixelSize;
            int charHeight = 7 * pixelSize;
            int spacing = 1 * pixelSize;

            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToUpperInvariant(text[i]);
                if (c == '\n')
                {
                    currentX = startX;
                    currentY += charHeight + (3 * pixelSize);
                    continue;
                }

                if (Glyphs.TryGetValue(c, out var rows))
                {
                    for (int r = 0; r < 7; r++)
                    {
                        byte rowBits = rows[r];
                        for (int col = 0; col < 5; col++)
                        {
                            if ((rowBits & (1 << (4 - col))) != 0)
                            {
                                sb.Draw(pixel, new Rectangle(currentX + col * pixelSize, currentY + r * pixelSize, pixelSize, pixelSize), color);
                            }
                        }
                    }
                }

                currentX += charWidth + spacing;
            }
        }

        public static Vector2 MeasureString(string text, int pixelSize = 2)
        {
            if (string.IsNullOrEmpty(text)) return Vector2.Zero;
            int charWidth = 5 * pixelSize;
            int charHeight = 7 * pixelSize;
            int spacing = 1 * pixelSize;

            int maxWidth = 0;
            int currentWidth = 0;
            int totalHeight = charHeight;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    if (currentWidth > maxWidth) maxWidth = currentWidth;
                    currentWidth = 0;
                    totalHeight += charHeight + (3 * pixelSize);
                }
                else
                {
                    currentWidth += charWidth + spacing;
                }
            }
            if (currentWidth > maxWidth) maxWidth = currentWidth;
            return new Vector2(maxWidth, totalHeight);
        }

        public static void DrawCenteredString(SpriteBatch sb, Texture2D pixel, string text, Rectangle rect, Color color, int pixelSize = 2)
        {
            Vector2 size = MeasureString(text, pixelSize);
            Vector2 pos = new Vector2(rect.X + (rect.Width - size.X) / 2f, rect.Y + (rect.Height - size.Y) / 2f);
            DrawString(sb, pixel, text, pos, color, pixelSize);
        }
    }
}
