using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Objects;

namespace SDViOSTouchControls
{
    public class QuickSaveState
    {
        public string SaveFolderName { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public int TimeOfDay { get; set; }
        public string LocationName { get; set; } = string.Empty;
        public int TileX { get; set; }
        public int TileY { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public int FacingDirection { get; set; }
        public bool IsSwimming { get; set; }
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public float Stamina { get; set; }
        public float MaxStamina { get; set; }
        public int MineLevel { get; set; } = -1;
        public string TimestampText { get; set; } = string.Empty;
        public string SavedAtUtc { get; set; } = string.Empty;
    }

    public class QuickSaveManager
    {
        public static QuickSaveManager? Instance { get; private set; }

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;

        private bool _isMiddaySaving = false;
        private bool _pendingSave = false;
        private SaveGameMenu? _activeSaveMenu;

        private string? _pendingReloadSaveFolder = null;
        private int _titleSettleTicks = 0;
        private int _reloadTimeoutTicks = 0;

        private readonly Dictionary<GameLocation, List<Monster>> _stashedMonsters = new();
        private Chest? _stashedShippingChest = null;
        private QuickSaveState? _cachedState = null;
        private Vector2? _pendingPositionRestore = null;
        private int _pendingFacingDirection = -1;

        public bool IsMiddaySaving => _isMiddaySaving;
        public bool IsReloading => !string.IsNullOrEmpty(_pendingReloadSaveFolder);

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            Instance = new QuickSaveManager(helper, monitor);
        }

        private QuickSaveManager(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;
        }

        private string GetStateFilePath(string? saveFolder = null)
        {
            try
            {
                if (string.IsNullOrEmpty(saveFolder))
                {
                    saveFolder = Constants.SaveFolderName;
                }
                if (string.IsNullOrEmpty(saveFolder) && _cachedState != null && !string.IsNullOrEmpty(_cachedState.SaveFolderName))
                {
                    saveFolder = _cachedState.SaveFolderName;
                }
                if (string.IsNullOrEmpty(saveFolder) && Game1.player != null)
                {
                    saveFolder = Game1.player.Name + "_" + Game1.uniqueIDForThisGame;
                }
                if (string.IsNullOrEmpty(saveFolder))
                {
                    saveFolder = "default";
                }

                string dir = Path.Combine(_helper.DirectoryPath, "data");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                return Path.Combine(dir, $"{saveFolder}_quicksave.json");
            }
            catch
            {
                return Path.Combine(_helper.DirectoryPath, "quicksave_backup.json");
            }
        }

        public bool HasSavedState()
        {
            if (_cachedState != null) return true;
            try
            {
                var smapiData = _helper.Data.ReadSaveData<QuickSaveState>("midday-quicksave");
                if (smapiData != null)
                {
                    _cachedState = smapiData;
                    return true;
                }

                string filePath = GetStateFilePath();
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var diskData = JsonSerializer.Deserialize<QuickSaveState>(json);
                    if (diskData != null)
                    {
                        _cachedState = diskData;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error checking saved state existence: {ex.Message}", LogLevel.Trace);
            }
            return false;
        }

        public QuickSaveState? GetSavedState()
        {
            if (_cachedState != null) return _cachedState;
            HasSavedState();
            return _cachedState;
        }

        public void SaveStateData(QuickSaveState state)
        {
            try
            {
                _cachedState = state;
                _helper.Data.WriteSaveData("midday-quicksave", state);

                string filePath = GetStateFilePath();
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);

                _monitor.Log($"[QuickSave] Saved midday state successfully: {state.TimestampText} at {state.LocationName} ({state.PositionX:F0},{state.PositionY:F0})", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error writing save state data: {ex.Message}", LogLevel.Error);
            }
        }

        public QuickSaveState? LoadStateData()
        {
            try
            {
                var smapiData = _helper.Data.ReadSaveData<QuickSaveState>("midday-quicksave");
                if (smapiData != null)
                {
                    _cachedState = smapiData;
                    return smapiData;
                }

                string filePath = GetStateFilePath();
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var diskData = JsonSerializer.Deserialize<QuickSaveState>(json);
                    if (diskData != null)
                    {
                        _cachedState = diskData;
                        return diskData;
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error reading save state data: {ex.Message}", LogLevel.Error);
            }
            return null;
        }

        public void ClearStateData()
        {
            try
            {
                _cachedState = null;
                _helper.Data.WriteSaveData<QuickSaveState>("midday-quicksave", null);

                string filePath = GetStateFilePath();
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                _monitor.Log("[QuickSave] Cleared midday save state (natural day end).", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error clearing save state data: {ex.Message}", LogLevel.Trace);
            }
        }

        public bool CanSaveNow(out string reason)
        {
            if (!Context.IsWorldReady || Game1.player == null || Game1.currentLocation == null)
            {
                reason = "World is not ready yet.";
                return false;
            }

            if (_isMiddaySaving || _pendingSave || IsReloading)
            {
                reason = "Save or reload is already in progress.";
                return false;
            }

            if (Game1.eventUp || Game1.currentLocation.currentEvent != null)
            {
                reason = "Cannot save during an active event or cutscene.";
                return false;
            }

            if (Game1.isFestival())
            {
                reason = "Cannot save during a festival.";
                return false;
            }

            if (Game1.activeClickableMenu != null && !(Game1.activeClickableMenu is GameMenu))
            {
                reason = "Please close open dialogs or minigames before saving.";
                return false;
            }

            if (Game1.IsMultiplayer && !Game1.IsMasterGame)
            {
                reason = "Only host can perform quick save in multiplayer.";
                return false;
            }

            if (Game1.currentLocation.characters != null && Game1.currentLocation.characters.Any(x => x.GetType().Name == "Junimo"))
            {
                reason = "The forest spirits prevent saving here.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public bool QuickSave()
        {
            if (IsReloading)
            {
                _monitor.Log("[QuickSave] Cannot quick save while reloading.", LogLevel.Warn);
                return false;
            }

            if (!CanSaveNow(out string reason))
            {
                _monitor.Log($"[QuickSave] Cannot save now: {reason}", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage(reason, HUDMessage.error_type));
                return false;
            }

            try
            {
                _monitor.Log("[QuickSave] Initiating midday quick save...", LogLevel.Info);

                // Close active menu if it was just the GameMenu
                if (Game1.activeClickableMenu != null)
                {
                    Game1.exitActiveMenu();
                }

                // 1. Stash all monsters from all locations to prevent XML serialization issues or attacks while saving
                CleanMonsters();

                // 2. Protect shipping bin items so the overnight shipping tally doesn't activate prematurely
                ProtectShippingBin();

                // 3. Capture current player & world state
                var player = Game1.player;
                var loc = player.currentLocation;
                string locName = loc.uniqueName.Value;
                if (string.IsNullOrEmpty(locName)) locName = loc.Name;

                int mineLevel = (loc is MineShaft mine) ? mine.mineLevel : -1;
                string timeStr = Game1.getTimeOfDayString(Game1.timeOfDay);
                string dayStr = Utility.getDateString();
                string displayTimestamp = $"{dayStr} - {timeStr}";

                var state = new QuickSaveState
                {
                    SaveFolderName = Constants.SaveFolderName ?? string.Empty,
                    PlayerName = player.Name,
                    TimeOfDay = Game1.timeOfDay,
                    LocationName = locName,
                    TileX = player.TilePoint.X,
                    TileY = player.TilePoint.Y,
                    PositionX = player.Position.X,
                    PositionY = player.Position.Y,
                    FacingDirection = player.FacingDirection,
                    IsSwimming = player.swimming.Value,
                    Health = player.health,
                    MaxHealth = player.maxHealth,
                    Stamina = player.stamina,
                    MaxStamina = player.maxStamina.Value,
                    MineLevel = mineLevel,
                    TimestampText = displayTimestamp,
                    SavedAtUtc = DateTime.UtcNow.ToString("o")
                };

                SaveStateData(state);

                // 4. Force collect RAM to avoid iOS Jetsam spikes during SaveGame.Save()
                MemoryWatchdog.ForceCollect("PreQuickSave", minIntervalMs: 200);

                // 5. Trigger the pending save in UpdateTicked
                _pendingSave = true;
                return true;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error during QuickSave initiation: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);
                Game1.addHUDMessage(new HUDMessage("Quick Save Failed: " + ex.Message, HUDMessage.error_type));
                RestoreMonsters();
                RestoreShippingBin();
                _pendingSave = false;
                _isMiddaySaving = false;
                return false;
            }
        }

        public bool QuickLoad()
        {
            if (!HasSavedState())
            {
                _monitor.Log("[QuickSave] No quick save state found to load.", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage("No Quick Save found for this farm!", HUDMessage.error_type));
                return false;
            }

            if (_isMiddaySaving || _pendingSave)
            {
                Game1.addHUDMessage(new HUDMessage("Cannot load while save is in progress.", HUDMessage.error_type));
                return false;
            }

            if (IsReloading)
            {
                _monitor.Log("[QuickSave] QuickLoad is already in progress.", LogLevel.Warn);
                return false;
            }

            try
            {
                var state = GetSavedState();
                string? saveFolder = state?.SaveFolderName;
                if (string.IsNullOrEmpty(saveFolder)) saveFolder = Constants.SaveFolderName;

                if (string.IsNullOrEmpty(saveFolder))
                {
                    Game1.addHUDMessage(new HUDMessage("Cannot identify save folder name.", HUDMessage.error_type));
                    return false;
                }

                _monitor.Log($"[QuickSave] QuickLoading save folder '{saveFolder}' ({state?.TimestampText})...", LogLevel.Info);

                if (Game1.activeClickableMenu != null)
                {
                    Game1.exitActiveMenu();
                }

                if (TouchVirtualPad.Instance != null)
                {
                    TouchVirtualPad.Instance.IsHotkeyDrawerOpen = false;
                }

                // If currently playing in a world, do NOT call SaveGame.Load directly while 750MB of assets are active!
                // Calling SaveGame.Load while in-game loads a second full copy of the world and causes iOS Jetsam OOM kill!
                // Instead, exit to title screen, wait for the title screen to settle and garbage-collect the old world,
                // then invoke SaveGame.Load into pristine ~160MB memory.
                if (Context.IsWorldReady || Game1.gameMode == 3)
                {
                    _monitor.Log("[QuickSave] In-game reload requested. Exiting to title screen to cleanly purge previous world memory...", LogLevel.Info);

                    _pendingReloadSaveFolder = saveFolder;
                    _titleSettleTicks = 0;
                    _reloadTimeoutTicks = 0;

                    ModEntry.PurgeTransientAssets();
                    MemoryWatchdog.ForceCollect("PreQuickLoadExit", minIntervalMs: 50);

                    Game1.playSound("bigSelect");
                    Game1.ExitToTitle();
                }
                else
                {
                    _monitor.Log($"[QuickSave] Title screen reload for '{saveFolder}'...", LogLevel.Info);
                    if (Game1.activeClickableMenu is TitleMenu)
                    {
                        TitleMenu.subMenu = null;
                    }
                    ModEntry.PurgeNonCoreAssets();
                    MemoryWatchdog.ForceCollect("PreQuickLoad", minIntervalMs: 50);
                    Game1.playSound("bigSelect");
                    SaveGame.Load(saveFolder);
                    Game1.exitActiveMenu();
                }

                return true;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error during QuickLoad: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);
                Game1.addHUDMessage(new HUDMessage("Quick Load Failed: " + ex.Message, HUDMessage.error_type));
                _pendingReloadSaveFolder = null;
                _titleSettleTicks = 0;
                _reloadTimeoutTicks = 0;
                return false;
            }
        }

        public void Update()
        {
            if (_pendingPositionRestore.HasValue && Game1.player != null && !Game1.isWarping)
            {
                Game1.player.Position = _pendingPositionRestore.Value;
                if (_pendingFacingDirection >= 0)
                {
                    Game1.player.faceDirection(_pendingFacingDirection);
                }
                _pendingPositionRestore = null;
                _pendingFacingDirection = -1;
            }

            // Ensure TitleMenu is never left lingering as activeClickableMenu during loadingMode (6) or playingGameMode (3),
            // because SMAPI's Context.IsSaveLoaded checks !(Game1.activeClickableMenu is TitleMenu).
            if ((Game1.gameMode == 6 || (Game1.gameMode == 3 && Game1.hasLoadedGame)) && Game1.activeClickableMenu is TitleMenu)
            {
                TitleMenu.subMenu = null;
                Game1.exitActiveMenu();
            }

            // Handle delayed in-game QuickLoad reload sequence
            if (!string.IsNullOrEmpty(_pendingReloadSaveFolder))
            {
                _reloadTimeoutTicks++;
                if (_reloadTimeoutTicks > 300) // 5 seconds safety timeout
                {
                    _monitor.Log("[QuickSave] QuickLoad reload timed out waiting for title screen. Aborting reload sequence.", LogLevel.Warn);
                    _pendingReloadSaveFolder = null;
                    _titleSettleTicks = 0;
                    _reloadTimeoutTicks = 0;
                    return;
                }

                // Wait until world is completely unloaded and TitleMenu is mounted
                if (!Context.IsWorldReady && Game1.activeClickableMenu is TitleMenu)
                {
                    _titleSettleTicks++;

                    if (_titleSettleTicks == 10)
                    {
                        _monitor.Log($"[QuickSave] TitleMenu mounted (settle tick {_titleSettleTicks}). Safely evicting map/character textures...", LogLevel.Info);
                        ModEntry.PurgeNonCoreAssets();
                        MemoryWatchdog.ForceCollect("TitleSettlePurge", minIntervalMs: 50);
                    }
                    else if (_titleSettleTicks == 20)
                    {
                        MemoryWatchdog.ForceCollect("TitleSettleDeepGC", minIntervalMs: 50);
                    }
                    else if (_titleSettleTicks >= 30) // ~0.5s: TitleMenu fully settled & memory compacted
                    {
                        string target = _pendingReloadSaveFolder;
                        _pendingReloadSaveFolder = null;
                        _titleSettleTicks = 0;
                        _reloadTimeoutTicks = 0;

                        long managedMb = GC.GetTotalMemory(false) / (1024 * 1024);
                        _monitor.Log($"[QuickSave] TitleMenu settled (managed memory={managedMb} MB). Calling SaveGame.Load('{target}')...", LogLevel.Info);

                        TitleMenu.subMenu = null;

                        MemoryWatchdog.ForceCollect("PreQuickLoadAfterTitle", minIntervalMs: 50);
                        Game1.playSound("bigSelect");
                        SaveGame.Load(target);
                        Game1.exitActiveMenu();
                    }
                }
                return;
            }

            if (!_pendingSave || Game1.activeClickableMenu != null)
                return;

            try
            {
                _pendingSave = false;
                _isMiddaySaving = true;

                Game1.newDaySync = new NewDaySynchronizer();
                Game1.newDaySync.start();
                Game1.weatherForTomorrow = Game1.getWeatherModificationsForDate(Game1.Date, Game1.weatherForTomorrow);

                _activeSaveMenu = new SaveGameMenu();
                Game1.activeClickableMenu = _activeSaveMenu;

                _monitor.Log("[QuickSave] SaveGameMenu mounted and saving initialized.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error launching SaveGameMenu: {ex.Message}", LogLevel.Error);
                _isMiddaySaving = false;
                _pendingSave = false;
                RestoreMonsters();
                RestoreShippingBin();
            }
        }

        public void OnSaving()
        {
            if (_isMiddaySaving)
            {
                _monitor.Log("[QuickSave] Midday save in progress...", LogLevel.Trace);
            }

            // Always ensure non-serializable custom monsters are stripped before XML SaveGame.Save()
            SanitizeNonVanillaMonsters(_monitor, "OnSaving");
        }

        public void OnSaved()
        {
            if (!_isMiddaySaving) return;

            try
            {
                _monitor.Log("[QuickSave] Midday save completed! Restoring monsters and shipping items...", LogLevel.Info);

                RestoreMonsters();
                RestoreShippingBin();

                if (Game1.player != null)
                {
                    Game1.player.CanMove = true;
                    Game1.player.freezePause = 0;
                }

                Game1.playSound("coin");
                var state = GetSavedState();
                string timeStr = state?.TimestampText ?? "Midday";
                Game1.addHUDMessage(new HUDMessage($"Quick Save Complete! ({timeStr})", 2));
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error in OnSaved: {ex.Message}", LogLevel.Warn);
            }
            finally
            {
                _isMiddaySaving = false;
                _pendingSave = false;
                _activeSaveMenu = null;
            }
        }

        public void OnSaveLoaded()
        {
            try
            {
                var state = LoadStateData();
                if (state == null)
                {
                    _monitor.Log("[QuickSave] No midday save state found for this save (starting at regular morning).", LogLevel.Trace);
                    return;
                }

                _monitor.Log($"[QuickSave] Restoring midday state: {state.TimestampText} at {state.LocationName} ({state.PositionX:F0},{state.PositionY:F0})...", LogLevel.Info);

                // 1. Restore shipping bin items from hidden chest if any
                RestoreShippingBin();

                // 2. Warp player to saved location
                string targetMap = state.LocationName;
                if (!string.IsNullOrEmpty(targetMap))
                {
                    var targetLoc = Game1.getLocationFromName(targetMap);
                    if (targetLoc != null)
                    {
                        Game1.warpFarmer(targetMap, state.TileX, state.TileY, false);
                    }
                    else if (targetMap.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase) && state.MineLevel > 0)
                    {
                        // Fallback warp for mines
                        Game1.warpFarmer("Mine", 18, 13, false);
                    }
                    else
                    {
                        _monitor.Log($"[QuickSave] Location '{targetMap}' not found; defaulting to FarmHouse.", LogLevel.Warn);
                        Game1.warpFarmer("FarmHouse", 1, 1, false);
                    }
                }

                // 3. Restore exact pixel position and facing
                if (Game1.player != null)
                {
                    if (state.PositionX > 0 || state.PositionY > 0)
                    {
                        Game1.player.Position = new Vector2(state.PositionX, state.PositionY);
                        _pendingPositionRestore = new Vector2(state.PositionX, state.PositionY);
                        _pendingFacingDirection = state.FacingDirection;
                    }
                    Game1.player.faceDirection(state.FacingDirection);

                    // 4. Restore health & stamina
                    if (state.Health > 0)
                    {
                        Game1.player.health = Math.Min(state.Health, Game1.player.maxHealth);
                    }
                    if (state.Stamina > 0)
                    {
                        Game1.player.stamina = Math.Min(state.Stamina, (float)Game1.player.maxStamina.Value);
                    }

                    // 5. Restore swimming status
                    if (state.IsSwimming)
                    {
                        try
                        {
                            Game1.player.changeIntoSwimsuit();
                            Game1.player.swimming.Value = true;
                        }
                        catch { }
                    }
                }

                // 6. Restore clock and daylight
                if (state.TimeOfDay >= 600 && state.TimeOfDay <= 2600)
                {
                    SafelySetTime(state.TimeOfDay);
                }

                Game1.addHUDMessage(new HUDMessage($"Quick Load: {state.TimestampText}", HUDMessage.achievement_type));
                _monitor.Log("[QuickSave] Midday state restored successfully.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error restoring midday state: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);
            }
        }

        public void OnDayEnding()
        {
            if (!_isMiddaySaving)
            {
                // Natural sleep at end of night -> clear midday state
                ClearStateData();
            }
        }

        private void CleanMonsters()
        {
            try
            {
                _stashedMonsters.Clear();
                if (Game1.locations == null) return;

                foreach (var location in Game1.locations)
                {
                    if (location == null || location.characters == null) continue;
                    var monsters = location.characters.OfType<Monster>().ToList();
                    if (monsters.Count > 0)
                    {
                        _stashedMonsters[location] = monsters;
                        foreach (var m in monsters)
                        {
                            location.characters.Remove(m);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error stashing monsters: {ex.Message}", LogLevel.Trace);
            }
        }

        public void RestoreMonsters()
        {
            try
            {
                if (_stashedMonsters.Count == 0) return;

                int restoredCount = 0;
                int skippedDuplicateCount = 0;

                foreach (var kvp in _stashedMonsters)
                {
                    var loc = kvp.Key;
                    if (loc == null || loc.characters == null) continue;
                    foreach (var m in kvp.Value)
                    {
                        if (m == null) continue;

                        // Prevent duplicate restoration if another mod (e.g. FTM AfterMidDaySave)
                        // or the game already respawned or maintained this monster:
                        bool alreadyExists = loc.characters.Any(existing =>
                            existing is Monster other && (
                                ReferenceEquals(other, m) ||
                                (other.id != 0 && other.id == m.id) ||
                                (other.Tile == m.Tile && other.Name == m.Name && other.GetType() == m.GetType())
                            ));

                        if (!alreadyExists && !loc.characters.Contains(m))
                        {
                            loc.addCharacter(m);
                            restoredCount++;
                        }
                        else
                        {
                            skippedDuplicateCount++;
                        }
                    }
                }
                _monitor.Log($"[QuickSave] Monster restore complete: {restoredCount} restored, {skippedDuplicateCount} skipped (already present/respawned).", LogLevel.Trace);
                _stashedMonsters.Clear();
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error restoring monsters: {ex.Message}", LogLevel.Trace);
            }
        }

        /// <summary>
        /// Scans all game locations and removes any Monster instance whose concrete runtime type
        /// is declared in an external mod assembly (e.g. FarmTypeManager.Monsters.MummyFTM, RockGolemFTM, etc.).
        /// Stardew Valley's XML SaveSerializer only knows vanilla types and will throw XmlUnxpectedType
        /// and crash/hang SaveGame.Save() if any custom monster subclass is present in characters.
        /// </summary>
        public static int SanitizeNonVanillaMonsters(IMonitor? monitor, string context)
        {
            int removedCount = 0;
            try
            {
                if (Game1.locations == null) return 0;
                var gameAssembly = typeof(Game1).Assembly;

                foreach (var location in Game1.locations)
                {
                    if (location == null || location.characters == null) continue;

                    for (int i = location.characters.Count - 1; i >= 0; i--)
                    {
                        var npc = location.characters[i];
                        if (npc is Monster && npc.GetType().Assembly != gameAssembly)
                        {
                            location.characters.RemoveAt(i);
                            removedCount++;
                            monitor?.Log($"[{context}] Removed non-serializable custom monster '{npc.GetType().FullName}' (id={npc.id}) from '{location.Name}' to prevent SaveSerializer crash.", LogLevel.Info);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                monitor?.Log($"[{context}] Error sanitizing non-vanilla monsters: {ex.Message}", LogLevel.Warn);
            }
            return removedCount;
        }

        private void ProtectShippingBin()
        {
            try
            {
                var farm = Game1.getFarm();
                if (farm == null) return;

                var shippingBin = farm.getShippingBin(Game1.player);
                if (shippingBin == null || shippingBin.Count == 0) return;

                _stashedShippingChest = new Chest(true, new Vector2(-100, -100));
                foreach (var item in shippingBin)
                {
                    if (item == null) continue;
                    item.modData["quickSaveSeller"] = Game1.player.UniqueMultiplayerID.ToString();
                    if (item == farm.lastItemShipped)
                    {
                        item.modData["quickSaveLast"] = "true";
                    }
                    _stashedShippingChest.addItem(item);
                }

                shippingBin.Clear();
                farm.setObject(new Vector2(-100, -100), _stashedShippingChest);
                if (farm.getObjectAtTile(-100, -100) != null)
                {
                    farm.getObjectAtTile(-100, -100).modData["Pathoschild.ChestsAnywhere/IsIgnored"] = "true";
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error protecting shipping bin: {ex.Message}", LogLevel.Trace);
            }
        }

        private void RestoreShippingBin()
        {
            try
            {
                var farm = Game1.getFarm();
                if (farm == null) return;

                if (farm.getObjectAtTile(-100, -100) is Chest tempChest)
                {
                    var shippingBin = farm.getShippingBin(Game1.player);
                    if (shippingBin != null)
                    {
                        foreach (var item in tempChest.Items)
                        {
                            if (item == null) continue;
                            if (item.modData.TryGetValue("quickSaveSeller", out string? seller) &&
                                seller == Game1.player.UniqueMultiplayerID.ToString())
                            {
                                if (item.modData.TryGetValue("quickSaveLast", out string? last) && last == "true")
                                {
                                    farm.lastItemShipped = item;
                                }
                                shippingBin.Add(item);
                            }
                        }
                    }
                    farm.removeObject(new Vector2(-100, -100), false);
                }
                _stashedShippingChest = null;
            }
            catch (Exception ex)
            {
                _monitor.Log($"[QuickSave] Error restoring shipping bin: {ex.Message}", LogLevel.Trace);
            }
        }

        private static void SafelySetTime(int time)
        {
            try
            {
                int intervals = Utility.CalculateMinutesBetweenTimes(Game1.timeOfDay, time) / 10;
                if (intervals > 0)
                {
                    for (int i = 0; i < intervals; i++)
                    {
                        Game1.performTenMinuteClockUpdate();
                    }
                }
                else if (intervals < 0)
                {
                    for (int i = 0; i > intervals; i--)
                    {
                        Game1.timeOfDay = Utility.ModifyTime(Game1.timeOfDay, -20);
                        Game1.performTenMinuteClockUpdate();
                    }
                }

                Game1.outdoorLight = Color.White;
                Game1.ambientLight = Color.White;
                Game1.gameTimeInterval = 0;
                Game1.UpdateGameClock(Game1.currentGameTime);
            }
            catch { }
        }
    }
}
