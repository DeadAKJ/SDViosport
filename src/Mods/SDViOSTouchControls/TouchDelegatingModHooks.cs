using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Mods;
using StardewValley.Tools;
using StardewModdingAPI.Utilities;

namespace SDViOSTouchControls
{
    public class TouchDelegatingModHooks : DelegatingModHooks
    {
        private readonly HashSet<Keys> _mergedKeys = new HashSet<Keys>();

        private int _sittingTicks = 0;
        private bool _prevButtonX = false;
        private static FieldInfo? _anotherClickField;

        public TouchDelegatingModHooks(ModHooks parent) : base(parent)
        {
        }

        private static void SetAnotherClick(MeleeWeapon weapon)
        {
            try
            {
                _anotherClickField ??= typeof(MeleeWeapon).GetField("anotherClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                _anotherClickField?.SetValue(weapon, true);
            }
            catch { }
        }

        public override void OnGame1_UpdateControlInput(ref KeyboardState keyboardState, ref MouseState mouseState, ref GamePadState gamePadState, Action action)
        {
            var pad = TouchVirtualPad.Instance;
            if (pad != null)
            {
                var player = Game1.player;
                if (player != null)
                {
                    // Sitting logic: allow sitting naturally, stand up with animation only after cooldown when moving
                    if (player.IsSitting())
                    {
                        _sittingTicks++;
                        bool hasDirectionalInput = pad.DPadUp || pad.DPadDown || pad.DPadLeft || pad.DPadRight ||
                                                  (pad.LeftStick != Vector2.Zero && pad.LeftStick.LengthSquared() > 0.05f);
                        if (!player.isStopSitting && _sittingTicks > 20 && hasDirectionalInput)
                        {
                            try
                            {
                                player.StopSitting(true);
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        _sittingTicks = 0;
                    }

                    // Dismiss passive object dialogue (e.g. sign, calendar, clock, text bar) when player moves or cancels
                    if (Game1.activeClickableMenu is DialogueBox db && db.characterDialogue == null && !db.isQuestion)
                    {
                        bool hasDirectionalInput = pad.DPadUp || pad.DPadDown || pad.DPadLeft || pad.DPadRight ||
                                                  (pad.LeftStick != Vector2.Zero && pad.LeftStick.LengthSquared() > 0.05f);
                        if (hasDirectionalInput || pad.ButtonB)
                        {
                            try
                            {
                                db.closeDialogue();
                            }
                            catch { }
                            Game1.activeClickableMenu = null;
                            Game1.dialogueUp = false;
                            player.CanMove = true;
                        }
                    }

                    // Watchdog: If activeClickableMenu is null but dialogueUp remained stuck true, clear it and restore movement!
                    if (Game1.activeClickableMenu == null && Game1.dialogueUp && !Game1.eventUp)
                    {
                        Game1.dialogueUp = false;
                        if (!player.CanMove)
                        {
                            player.CanMove = true;
                        }
                    }
                }

                // Inject simulated keyboard keys (WASD, Arrows, Action buttons) without unnecessary allocations
                if (pad.HasActiveInput)
                {
                    var simState = pad.CurrentSimulatedKeyboardState;
                    var existingKeys = keyboardState.GetPressedKeys();
                    if (existingKeys == null || existingKeys.Length == 0)
                    {
                        keyboardState = simState;
                    }
                    else
                    {
                        _mergedKeys.Clear();
                        for (int i = 0; i < existingKeys.Length; i++)
                            _mergedKeys.Add(existingKeys[i]);
                        var simKeys = simState.GetPressedKeys();
                        for (int i = 0; i < simKeys.Length; i++)
                            _mergedKeys.Add(simKeys[i]);
                        keyboardState = new KeyboardState(_mergedKeys.ToArray());
                    }
                }

                // Inject simulated mouse state if overridden
                if (pad.IsMouseOverridden)
                {
                    mouseState = pad.CurrentSimulatedMouseState;
                }

                // Forward gamepad buttons and triggers so SMAPI and GamePadState consumers register them
                if (pad.ButtonX || pad.ButtonLT || pad.ButtonRT)
                {
                    gamePadState = pad.EffectiveGamePadState;
                }
            }

            base.OnGame1_UpdateControlInput(ref keyboardState, ref mouseState, ref gamePadState, action);

            // Post-input handling for Button X tool / melee weapon swinging
            bool buttonX = pad != null && pad.ButtonX;
            bool justPressedX = buttonX && !_prevButtonX;
            _prevButtonX = buttonX;

            if (buttonX && Game1.activeClickableMenu == null && !Game1.eventUp && !Game1.freezeControls)
            {
                Game1.lastCursorMotionWasMouse = false;
                var player = Game1.player;
                if (player != null && !player.isEating && !player.isRidingHorse() && !player.IsSitting())
                {
                    if (player.CurrentTool is MeleeWeapon weapon)
                    {
                        if (player.CanMove && !player.UsingTool)
                        {
                            try
                            {
                                player.Halt();
                                player.BeginUsingTool();
                            }
                            catch { }
                        }
                        else if (player.UsingTool)
                        {
                            SetAnotherClick(weapon);
                            try
                            {
                                weapon.leftClick(player);
                            }
                            catch { }
                        }
                    }
                    else if (player.CurrentTool != null)
                    {
                        if ((player.CanMove && !player.UsingTool) || (player.UsingTool && player.CurrentTool is FishingRod && justPressedX))
                        {
                            bool oldMouseVis = Game1.wasMouseVisibleThisFrame;
                            Game1.wasMouseVisibleThisFrame = false;
                            try
                            {
                                Game1.pressUseToolButton();
                            }
                            catch { }
                            finally
                            {
                                Game1.wasMouseVisibleThisFrame = oldMouseVis;
                            }
                        }
                    }
                }
            }
        }

        public override void OnGameLocation_ResetForPlayerEntry(GameLocation location, Action action)
        {
            if (location != null)
            {
                ModEntry.EnsureLocationMapAndTileSheetsLoaded(location);
                base.OnGameLocation_ResetForPlayerEntry(location, action);
            }
            else
            {
                action?.Invoke();
            }
        }
    }
}
