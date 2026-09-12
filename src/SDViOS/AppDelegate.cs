using System;
using System.IO;
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
                EngineLogger.Log("[AppDelegate] FinishedLaunching: Checking game files...");

                if (GameHost.TryFindGameBinary(out string sdvPath, out _))
                {
                    EngineLogger.Log($"Game files located at: {sdvPath}. Launching...");
                    GameHost.Launch(Array.Empty<string>());
                }
                else
                {
                    EngineLogger.LogWarning("Game files missing. Displaying setup interface.");
                    ShowMissingFilesScreen();
                }
            }
            catch (Exception ex)
            {
                EngineLogger.LogFatal("AppDelegate FinishedLaunching", ex);
                ShowErrorScreen(ex);
            }

            return true;
        }

        private void ShowMissingFilesScreen()
        {
            Window = new UIWindow(UIScreen.MainScreen.Bounds);
            var vc = new UIViewController();
            vc.View!.BackgroundColor = UIColor.FromRGB(24, 30, 42);

            nfloat width = Window.Bounds.Width;
            nfloat height = Window.Bounds.Height;

            var labelTitle = new UILabel(new CoreGraphics.CGRect(20, 50, width - 40, 40))
            {
                Text = "Stardew Valley (PC Port)",
                TextColor = UIColor.White,
                Font = UIFont.BoldSystemFontOfSize(24),
                TextAlignment = UITextAlignment.Center
            };

            var labelStatus = new UILabel(new CoreGraphics.CGRect(30, 100, width - 60, 110))
            {
                Text = "Game files not found!\n\nPlease copy your PC Stardew Valley files into the iOS Files app:\nOn My iPhone > Stardew Valley\n\nRequired: Stardew Valley.dll and Content folder.",
                TextColor = UIColor.FromRGB(220, 220, 220),
                Font = UIFont.SystemFontOfSize(15),
                Lines = 0,
                TextAlignment = UITextAlignment.Center
            };

            var btnReload = new UIButton(UIButtonType.System)
            {
                Frame = new CoreGraphics.CGRect((width - 220) / 2, 230, 220, 46),
                BackgroundColor = UIColor.FromRGB(46, 139, 87),
                Layer = { CornerRadius = 10 }
            };
            btnReload.SetTitle("Check Files & Start Game", UIControlState.Normal);
            btnReload.SetTitleColor(UIColor.White, UIControlState.Normal);
            btnReload.TitleLabel.Font = UIFont.BoldSystemFontOfSize(16);
            btnReload.TouchUpInside += (s, e) =>
            {
                if (GameHost.TryFindGameBinary(out _, out _))
                {
                    GameHost.Launch(Array.Empty<string>());
                }
                else
                {
                    var alert = UIAlertController.Create("Still Missing", "Stardew Valley.dll was not found yet.\n\nPath checked:\nFiles app > On My iPhone > Stardew Valley", UIAlertControllerStyle.Alert);
                    alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
                    vc.PresentViewController(alert, true, null);
                }
            };

            vc.View.AddSubviews(labelTitle, labelStatus, btnReload);
            Window.RootViewController = vc;
            Window.MakeKeyAndVisible();
        }

        private void ShowErrorScreen(Exception ex)
        {
            Window = new UIWindow(UIScreen.MainScreen.Bounds);
            var vc = new UIViewController();
            vc.View!.BackgroundColor = UIColor.FromRGB(40, 10, 10);

            var realEx = ex;
            while (realEx.InnerException != null)
            {
                realEx = realEx.InnerException;
            }

            var label = new UILabel(new CoreGraphics.CGRect(20, 60, Window.Bounds.Width - 40, Window.Bounds.Height - 120))
            {
                Text = $"Fatal Error on Launch:\n{realEx.GetType().Name}: {realEx.Message}\n\n{realEx.StackTrace}",
                TextColor = UIColor.White,
                Font = UIFont.SystemFontOfSize(12),
                Lines = 0
            };
            vc.View.AddSubview(label);
            Window.RootViewController = vc;
            Window.MakeKeyAndVisible();
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
