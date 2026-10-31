using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using xTile;
using xTile.Display;

namespace SDViOSTouchControls
{
    /// <summary>
    /// Optimizes save load times and memory headroom by deferring GPU tilesheet texture loading
    /// for non-essential maps until the player actually warps to or enters them.
    ///
    /// Fully reversible via <see cref="Enabled"/> toggle or console command.
    /// Preserves 100% of game mechanics, NPC pathfinding, warps, and cutscenes.
    /// </summary>
    public static class DeferredMapLoader
    {
        public static bool Enabled { get; set; } = true;
        public static bool IsBulkLoading { get; set; } = true;
        public static GameLocation? CurrentLoadingLocation { get; set; } = null;
        public static IMonitor? ModMonitor { get; set; }

        private static readonly HashSet<Map> _realizedMaps = new();
        private static readonly HashSet<string> _realizedLocations = new(StringComparer.OrdinalIgnoreCase);

        private static int _deferredCount = 0;
        private static int _immediateCount = 0;

        public static void Initialize(IMonitor monitor)
        {
            ModMonitor = monitor;
            IsBulkLoading = true;
            _realizedMaps.Clear();
            _realizedLocations.Clear();
            _deferredCount = 0;
            _immediateCount = 0;
        }

        public static bool IsEssentialLocation(GameLocation? loc)
        {
            if (loc == null) return true;
            string name = loc.NameOrUniqueName;
            if (string.IsNullOrEmpty(name)) return true;

            // Core starting locations and player home
            if (name == "Farm" || name == "FarmHouse" || name == "Greenhouse" || name == "Cellar")
                return true;

            if (name.StartsWith("Cellar", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.StartsWith("Cabin", StringComparison.OrdinalIgnoreCase))
                return true;

            if (Game1.player != null)
            {
                if (loc == Game1.player.currentLocation || name == Game1.player.currentLocation?.NameOrUniqueName)
                    return true;

                var home = Utility.getHomeOfFarmer(Game1.player);
                if (home != null && (loc == home || name == home.NameOrUniqueName))
                    return true;
            }

            if (Game1.currentLocation != null)
            {
                if (loc == Game1.currentLocation || name == Game1.currentLocation.NameOrUniqueName)
                    return true;
            }

            return false;
        }

        public static void EnsureLocationRealized(GameLocation? loc)
        {
            if (loc == null || !Enabled) return;
            string locName = loc.NameOrUniqueName;
            if (string.IsNullOrEmpty(locName)) locName = loc.Name;

            if (_realizedLocations.Contains(locName) && loc.map != null && _realizedMaps.Contains(loc.map))
                return;

            var sw = Stopwatch.StartNew();

            if (loc.map == null && !string.IsNullOrEmpty(loc.mapPath?.Value))
            {
                loc.reloadMap();
            }

            if (loc.map != null)
            {
                try
                {
                    loc.updateSeasonalTileSheets(loc.map);
                    loc.map.LoadTileSheets(Game1.mapDisplayDevice);
                    loc.loadLights();
                    _realizedMaps.Add(loc.map);
                }
                catch (Exception ex)
                {
                    ModMonitor?.Log($"[DeferredMapLoader] Warning realizing tilesheets for '{locName}': {ex.Message}", LogLevel.Warn);
                }
            }

            _realizedLocations.Add(locName);
            sw.Stop();

            if (sw.ElapsedMilliseconds > 2)
            {
                ModMonitor?.Log($"[DeferredMapLoader] JIT realized map & tilesheets for '{locName}' in {sw.ElapsedMilliseconds} ms.", LogLevel.Trace);
            }
        }

        public static void OnReturnedToTitle()
        {
            IsBulkLoading = true;
            _realizedMaps.Clear();
            _realizedLocations.Clear();
            CurrentLoadingLocation = null;
            _deferredCount = 0;
            _immediateCount = 0;
        }

        public static void OnSaveLoaded()
        {
            IsBulkLoading = false;
            ModMonitor?.Log($"[DeferredMapLoader] Bulk loading complete: {_deferredCount} map tilesheet loads deferred, {_immediateCount} essential loaded immediately.", LogLevel.Info);
            EnsureLocationRealized(Game1.currentLocation);
            EnsureLocationRealized(Game1.player?.currentLocation);
        }

        public static void OnDayStarted()
        {
            IsBulkLoading = false;
            EnsureLocationRealized(Game1.currentLocation);
            EnsureLocationRealized(Game1.player?.currentLocation);
        }

        // =========================================================================
        // Harmony Patches
        // =========================================================================

        public static void ApplyHarmonyPatches(Harmony harmony)
        {
            try
            {
                // 1. Patch xTile.Map.LoadTileSheets to defer texture uploads during bulk loading
                var mLoadTileSheets = typeof(Map).GetMethod(nameof(Map.LoadTileSheets), new[] { typeof(IDisplayDevice) });
                if (mLoadTileSheets != null)
                {
                    harmony.Patch(
                        mLoadTileSheets,
                        prefix: new HarmonyMethod(typeof(DeferredMapLoader).GetMethod(nameof(PrefixLoadTileSheets), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
                    );
                    ModMonitor?.Log("[DeferredMapLoader] Harmony patched xTile.Map.LoadTileSheets.", LogLevel.Info);
                }

                // 2. Patch GameLocation.loadMap to track CurrentLoadingLocation
                var mLoadMap = typeof(GameLocation).GetMethod(nameof(GameLocation.loadMap), new[] { typeof(string), typeof(bool) });
                if (mLoadMap != null)
                {
                    harmony.Patch(
                        mLoadMap,
                        prefix: new HarmonyMethod(typeof(DeferredMapLoader).GetMethod(nameof(PrefixGameLocationLoadMap), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)),
                        finalizer: new HarmonyMethod(typeof(DeferredMapLoader).GetMethod(nameof(FinalizerGameLocationLoadMap), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
                    );
                    ModMonitor?.Log("[DeferredMapLoader] Harmony patched GameLocation.loadMap context.", LogLevel.Info);
                }

                // 3. Patch Game1.warpFarmer(LocationRequest, int, int, int) to realize destination map during warp fade
                var mWarp = typeof(Game1).GetMethod(nameof(Game1.warpFarmer), new[] { typeof(LocationRequest), typeof(int), typeof(int), typeof(int) });
                if (mWarp != null)
                {
                    harmony.Patch(
                        mWarp,
                        prefix: new HarmonyMethod(typeof(DeferredMapLoader).GetMethod(nameof(PrefixWarpFarmer), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
                    );
                    ModMonitor?.Log("[DeferredMapLoader] Harmony patched Game1.warpFarmer for JIT map realization.", LogLevel.Info);
                }

                // 4. Patch GameLocation.draw as a safety net: guaranteed realization before any tile drawing
                var mDraw = typeof(GameLocation).GetMethod(nameof(GameLocation.draw), new[] { typeof(SpriteBatch) });
                if (mDraw != null)
                {
                    harmony.Patch(
                        mDraw,
                        prefix: new HarmonyMethod(typeof(DeferredMapLoader).GetMethod(nameof(PrefixGameLocationDraw), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
                    );
                    ModMonitor?.Log("[DeferredMapLoader] Harmony patched GameLocation.draw safety net.", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                ModMonitor?.Log($"[DeferredMapLoader] Error applying Harmony patches: {ex.Message}", LogLevel.Error);
            }
        }

        private static bool PrefixLoadTileSheets(Map __instance, IDisplayDevice displayDevice)
        {
            if (!Enabled || !IsBulkLoading)
                return true;

            if (IsEssentialLocation(CurrentLoadingLocation))
            {
                _immediateCount++;
                if (CurrentLoadingLocation != null)
                {
                    _realizedLocations.Add(CurrentLoadingLocation.NameOrUniqueName);
                }
                _realizedMaps.Add(__instance);
                return true;
            }

            // Defer texture upload for non-essential expansion maps during bulk load
            _deferredCount++;
            return false;
        }

        private static void PrefixGameLocationLoadMap(GameLocation __instance, string mapPath, bool force_reload)
        {
            CurrentLoadingLocation = __instance;
        }

        private static void FinalizerGameLocationLoadMap(GameLocation __instance)
        {
            CurrentLoadingLocation = null;
        }

        private static void PrefixWarpFarmer(LocationRequest locationRequest, int tileX, int tileY, int facingDirectionAfterWarp)
        {
            if (!Enabled) return;

            try
            {
                if (locationRequest?.Location != null)
                {
                    EnsureLocationRealized(locationRequest.Location);
                }
                else if (!string.IsNullOrEmpty(locationRequest?.Name))
                {
                    var targetLoc = Game1.getLocationFromName(locationRequest.Name);
                    if (targetLoc != null)
                    {
                        EnsureLocationRealized(targetLoc);
                    }
                }
            }
            catch { }
        }

        private static void PrefixGameLocationDraw(GameLocation __instance, SpriteBatch b)
        {
            if (!Enabled || __instance == null || __instance.map == null) return;

            if (!_realizedMaps.Contains(__instance.map))
            {
                EnsureLocationRealized(__instance);
            }
        }
    }
}
