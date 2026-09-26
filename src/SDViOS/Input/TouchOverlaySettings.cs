using System;
using System.Globalization;
using System.IO;
using SDViOS.Diagnostics;
using SDViOS.Loader;

namespace SDViOS.Input
{
    public enum MouseMode
    {
        PointAndClick = 0,
        Trackpad = 1,
        Disabled = 2
    }

    public class TouchOverlaySettings
    {
        public static TouchOverlaySettings Instance { get; } = new TouchOverlaySettings();

        public float Opacity { get; set; } = 0.6f;
        public float Scale { get; set; } = 1.0f;
        public bool LeftHanded { get; set; } = false;
        public float Deadzone { get; set; } = 0.25f;
        public bool ShowKeyboardBtn { get; set; } = true;
        public bool ShowMenuBtn { get; set; } = true;
        public bool SimulateMouseOnTap { get; set; } = true;

        // Mouse modes
        public MouseMode MouseControlMode { get; set; } = MouseMode.PointAndClick;
        public float TrackpadSensitivity { get; set; } = 1.2f;

        public bool CustomPositionsSet { get; set; } = false;
        public float JoystickNormX { get; set; } = 0.15f;
        public float JoystickNormY { get; set; } = 0.82f;
        public float ButtonsNormX { get; set; } = 0.85f;
        public float ButtonsNormY { get; set; } = 0.82f;

        private static string GetConfigPath()
        {
            string dir = !string.IsNullOrEmpty(GameHost.DocumentsDir) && Directory.Exists(GameHost.DocumentsDir)
                ? GameHost.DocumentsDir
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(dir, "touch_overlay_config.json");
        }

        public void Load()
        {
            try
            {
                string path = GetConfigPath();
                if (!File.Exists(path))
                {
                    EngineLogger.Log("[TouchOverlaySettings] No config file found; using default settings.");
                    return;
                }

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return;

                foreach (var rawLine in json.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Trim().TrimEnd(',');
                    var parts = line.Split(':');
                    if (parts.Length != 2) continue;

                    string key = parts[0].Trim().Trim('"');
                    string val = parts[1].Trim().Trim('"');

                    if (key.Equals("Opacity", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float op))
                        Opacity = Math.Clamp(op, 0.15f, 1.0f);
                    else if (key.Equals("Scale", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float sc))
                        Scale = Math.Clamp(sc, 0.5f, 2.0f);
                    else if (key.Equals("LeftHanded", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool lh))
                        LeftHanded = lh;
                    else if (key.Equals("Deadzone", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float dz))
                        Deadzone = Math.Clamp(dz, 0.05f, 0.6f);
                    else if (key.Equals("ShowKeyboardBtn", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool sk))
                        ShowKeyboardBtn = sk;
                    else if (key.Equals("ShowMenuBtn", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool sm))
                        ShowMenuBtn = sm;
                    else if (key.Equals("SimulateMouseOnTap", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool smt))
                        SimulateMouseOnTap = smt;
                    else if (key.Equals("MouseControlMode", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<MouseMode>(val, true, out var mm)) MouseControlMode = mm;
                        else if (int.TryParse(val, out int mmi)) MouseControlMode = (MouseMode)Math.Clamp(mmi, 0, 2);
                    }
                    else if (key.Equals("TrackpadSensitivity", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float ts))
                        TrackpadSensitivity = Math.Clamp(ts, 0.4f, 3.0f);
                    else if (key.Equals("CustomPositionsSet", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool cps))
                        CustomPositionsSet = cps;
                    else if (key.Equals("JoystickNormX", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float jx))
                        JoystickNormX = Math.Clamp(jx, 0.05f, 0.95f);
                    else if (key.Equals("JoystickNormY", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float jy))
                        JoystickNormY = Math.Clamp(jy, 0.05f, 0.95f);
                    else if (key.Equals("ButtonsNormX", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float bx))
                        ButtonsNormX = Math.Clamp(bx, 0.05f, 0.95f);
                    else if (key.Equals("ButtonsNormY", StringComparison.OrdinalIgnoreCase) && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float by))
                        ButtonsNormY = Math.Clamp(by, 0.05f, 0.95f);
                }

                EngineLogger.Log($"[TouchOverlaySettings] Loaded settings: Opacity={Opacity:F2}, Scale={Scale:F2}, LeftHanded={LeftHanded}, Deadzone={Deadzone:F2}, MouseMode={MouseControlMode}, Sensitivity={TrackpadSensitivity:F1}");
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[TouchOverlaySettings] Error loading config: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                string path = GetConfigPath();
                string json = "{\n" +
                    $"  \"Opacity\": {Opacity.ToString("0.00", CultureInfo.InvariantCulture)},\n" +
                    $"  \"Scale\": {Scale.ToString("0.00", CultureInfo.InvariantCulture)},\n" +
                    $"  \"LeftHanded\": {(LeftHanded ? "true" : "false")},\n" +
                    $"  \"Deadzone\": {Deadzone.ToString("0.00", CultureInfo.InvariantCulture)},\n" +
                    $"  \"ShowKeyboardBtn\": {(ShowKeyboardBtn ? "true" : "false")},\n" +
                    $"  \"ShowMenuBtn\": {(ShowMenuBtn ? "true" : "false")},\n" +
                    $"  \"SimulateMouseOnTap\": {(SimulateMouseOnTap ? "true" : "false")},\n" +
                    $"  \"MouseControlMode\": \"{MouseControlMode}\",\n" +
                    $"  \"TrackpadSensitivity\": {TrackpadSensitivity.ToString("0.00", CultureInfo.InvariantCulture)},\n" +
                    $"  \"CustomPositionsSet\": {(CustomPositionsSet ? "true" : "false")},\n" +
                    $"  \"JoystickNormX\": {JoystickNormX.ToString("0.000", CultureInfo.InvariantCulture)},\n" +
                    $"  \"JoystickNormY\": {JoystickNormY.ToString("0.000", CultureInfo.InvariantCulture)},\n" +
                    $"  \"ButtonsNormX\": {ButtonsNormX.ToString("0.000", CultureInfo.InvariantCulture)},\n" +
                    $"  \"ButtonsNormY\": {ButtonsNormY.ToString("0.000", CultureInfo.InvariantCulture)}\n" +
                    "}";

                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(path, json);
                EngineLogger.Log("[TouchOverlaySettings] Successfully saved settings.");
            }
            catch (Exception ex)
            {
                EngineLogger.LogWarning($"[TouchOverlaySettings] Error saving config: {ex.Message}");
            }
        }

        public void ResetToDefaults()
        {
            Opacity = 0.6f;
            Scale = 1.0f;
            LeftHanded = false;
            Deadzone = 0.25f;
            ShowKeyboardBtn = true;
            ShowMenuBtn = true;
            SimulateMouseOnTap = true;
            MouseControlMode = MouseMode.PointAndClick;
            TrackpadSensitivity = 1.2f;
            CustomPositionsSet = false;
            JoystickNormX = 0.15f;
            JoystickNormY = 0.82f;
            ButtonsNormX = 0.85f;
            ButtonsNormY = 0.82f;
            Save();
        }
    }
}
