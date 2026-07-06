using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RocketTool.Core;

namespace RocketTool.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateVersionSelector(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static VersionSelectionWindow CreateVersionSelector(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var profiles = GameProfileCatalog.Load(FindProfilesDirectory(), typeof(App).Assembly);
        return new VersionSelectionWindow(profiles, profile => OpenMainWindow(desktop, profile));
    }

    private static void OpenMainWindow(IClassicDesktopStyleApplicationLifetime desktop, GameProfile profile)
    {
        var main = new MainWindow(profile);
        main.VersionSwitchRequested += (_, _) => SwitchVersion(desktop, main);
        desktop.MainWindow = main;
        main.Show();
    }

    private static void SwitchVersion(IClassicDesktopStyleApplicationLifetime desktop, Window current)
    {
        var selector = CreateVersionSelector(desktop);
        desktop.MainWindow = selector;
        selector.Show();
        current.Close();
    }

    private static string FindProfilesDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.GetFullPath(Path.Combine(dir, string.Concat(Enumerable.Repeat("../", i))));
            var profiles = Path.Combine(candidate, "profiles");
            if (Directory.Exists(profiles)) return profiles;
        }
        return Path.Combine(AppContext.BaseDirectory, "profiles");
    }
}
