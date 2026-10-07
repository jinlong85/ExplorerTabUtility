using System.Windows;
using System.Threading;
using System.Windows.Controls;
using ExplorerTabUtility.UI.Views;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Managers;
using ExplorerTabUtility.Localization;

namespace ExplorerTabUtility;

// ReSharper disable once RedundantExtendsListEntry
public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        Loc.Initialize(this, SettingsManager.Language);

        _mutex = new Mutex(true, Constants.MutexId, out var createdNew);

        if (createdNew)
        {
            base.OnStartup(e);
            SetupTooltipBehavior();

            _ = new MainWindow();
            return;
        }

        CustomMessageBox.Show(Loc.Get("App_AnotherInstance"), Loc.Get("App_Title"), icon: MessageBoxImage.Information);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void SetupTooltipBehavior()
    {
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(3500));
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(1700));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(150));
        ToolTipService.ShowsToolTipOnKeyboardFocusProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(false));
    }
}