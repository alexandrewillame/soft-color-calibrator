namespace SoftCal.Phone.iOS;

/// <summary>The app: one window scene ("Default Configuration" in Info.plist), run by <see cref="SceneDelegate"/>.</summary>
[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

    public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
        new("Default Configuration", connectingSceneSession.Role);
}

/// <summary>
/// The app's window, and its comings and goings: the screen keeps the camera, the link and the announcement while the
/// app is in front, and lets go of them when it leaves (iOS stops the camera and, soon after, the app).
/// </summary>
[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    private MainViewController? _screen;

    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
        {
            return;
        }

        _screen = new MainViewController();
        Window = new UIWindow(windowScene) { RootViewController = _screen };
        Window.MakeKeyAndVisible();
    }

    [Export("sceneDidBecomeActive:")]
    public void DidBecomeActive(UIScene scene) => _screen?.Activated();

    [Export("sceneWillResignActive:")]
    public void WillResignActive(UIScene scene) => _screen?.Deactivated();

    [Export("sceneWillEnterForeground:")]
    public void WillEnterForeground(UIScene scene) => _screen?.EnteringForeground();

    [Export("sceneDidEnterBackground:")]
    public void DidEnterBackground(UIScene scene) => _screen?.EnteredBackground();
}
