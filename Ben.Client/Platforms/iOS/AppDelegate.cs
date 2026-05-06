using Foundation;
using UIKit;

namespace Ben;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIApplication app, NSDictionary options)
	{
		var result = base.FinishedLaunching(app, options);

		if (UIDevice.CurrentDevice.CheckSystemVersion(13, 0))
		{
			Window!.OverrideUserInterfaceStyle = UIUserInterfaceStyle.Light;
		}

		return result;
	}
}
