using System;
using Foundation;
using UIKit;
using SDViOS.Diagnostics;
using SDViOS.Loader;

namespace SDViOS
{
    [Register("AppDelegate")]
    public class AppDelegate : UIApplicationDelegate
    {
        public override UIWindow? Window { get; set; }

        public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
        {
            try
            {
                GameHost.InitializeFileSystem();
                EngineLogger.Log("[AppDelegate] FinishedLaunching: Starting GameHost");
                GameHost.Launch(Array.Empty<string>());
            }
            catch (Exception ex)
            {
                EngineLogger.LogFatal("AppDelegate FinishedLaunching", ex);
            }

            return true;
        }

        public override void DidEnterBackground(UIApplication application)
        {
            EngineLogger.Log("[AppDelegate] DidEnterBackground");
        }

        public override void WillEnterForeground(UIApplication application)
        {
            EngineLogger.Log("[AppDelegate] WillEnterForeground");
        }

        public override void WillTerminate(UIApplication application)
        {
            EngineLogger.Log("[AppDelegate] WillTerminate");
        }
    }
}
