using System;

namespace Steamworks
{
    public static class SteamAPI
    {
        public static bool Init() => false;
        public static void Shutdown() { }
        public static bool RestartAppIfNecessary(AppId_t unOwnAppID) => false;
        public static void RunCallbacks() { }
        public static bool IsSteamRunning() => false;
    }

    public struct AppId_t
    {
        public uint m_AppId;
        public AppId_t(uint value) => m_AppId = value;
        public static explicit operator AppId_t(uint value) => new AppId_t(value);
    }

    public struct CSteamID
    {
        public ulong m_SteamID;
        public CSteamID(ulong value) => m_SteamID = value;
        public bool IsValid() => false;
    }

    public static class SteamApps
    {
        public static bool BIsAppInstalled(AppId_t appID) => true;
        public static string GetCurrentGameLanguage() => "english";
        public static bool BIsDlcInstalled(AppId_t appID) => false;
    }

    public static class SteamUserStats
    {
        public static bool RequestCurrentStats() => false;
        public static bool SetAchievement(string pchName) => false;
        public static bool StoreStats() => false;
    }

    public static class SteamFriends
    {
        public static string GetPersonaName() => "Farmer";
    }

    public static class SteamUtils
    {
        public static string GetIPCountry() => "US";
    }
}
