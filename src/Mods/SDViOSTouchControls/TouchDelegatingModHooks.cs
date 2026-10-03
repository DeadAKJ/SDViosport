using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Mods;
using StardewModdingAPI.Utilities;

namespace SDViOSTouchControls
{
    public class TouchDelegatingModHooks : DelegatingModHooks
    {
        public TouchDelegatingModHooks(ModHooks parent) : base(parent)
        {
        }

        public override void OnGame1_UpdateControlInput(ref KeyboardState keyboardState, ref MouseState mouseState, ref GamePadState gamePadState, Action action)
        {
            var pad = TouchVirtualPad.Instance;
            if (pad != null)
            {
                // If player is sitting or in bed and input is active, wake them up
                var player = Game1.player;
                if (player != null && player.IsSitting() && (pad.DPadUp || pad.DPadDown || pad.DPadLeft || pad.DPadRight || pad.ButtonA || pad.ButtonX || pad.LeftStick != Vector2.Zero))
                {
                    try
                    {
                        player.StopSitting(false);
                        player.isSitting.Value = false;
                        player.sittingFurniture = null;
                    }
                    catch { }
                }

                // Inject simulated keyboard keys (WASD, Arrows, Action buttons)
                if (pad.HasActiveInput)
                {
                    var keys = new HashSet<Keys>(keyboardState.GetPressedKeys());
                    foreach (var k in pad.CurrentSimulatedKeyboardState.GetPressedKeys())
                    {
                        keys.Add(k);
                    }
                    keyboardState = new KeyboardState(keys.ToArray());
                }

                // Inject simulated mouse state if overridden
                if (pad.IsMouseOverridden)
                {
                    mouseState = pad.CurrentSimulatedMouseState;
                }
            }

            base.OnGame1_UpdateControlInput(ref keyboardState, ref mouseState, ref gamePadState, action);
        }

        public override void OnGameLocation_ResetForPlayerEntry(GameLocation location, Action action)
        {
            if (location != null && location.map == null)
            {
                try
                {
                    location.reloadMap();
                }
                catch { }
            }
            base.OnGameLocation_ResetForPlayerEntry(location, action);
        }
    }
}
