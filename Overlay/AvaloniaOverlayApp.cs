using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace Nova;

internal sealed class AvaloniaOverlayApp : Application
{
    public static NovaAssistant? PendingAssistant;
    public static string? PendingRepoRoot;
    public static Action? OnReady;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new AvaloniaOverlayWindow(PendingAssistant!, PendingRepoRoot!);
            desktop.MainWindow = window;
            window.Show();
        }

        base.OnFrameworkInitializationCompleted();
        OnReady?.Invoke();
    }
}
