using System;

namespace Galaxy.Api
{
    public static class GalaxyInstance
    {
        public static void Init(string clientID, string clientSecret) { }
        public static void Shutdown() { }
        public static object User() => null!;
        public static object Friends() => null!;
        public static object Stats() => null!;
        public static void ProcessData() { }
    }
}
