using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Klemmbrett.Services;
using Klemmbrett.ViewModels;
using NLog;

namespace Klemmbrett.Views;

public partial class AboutWindow : ChromeWindow
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const string GithubUrl = "https://github.com/LHP542/Klemmbrett-Scaffold";
    private const string BmcUrl = "https://buymeacoffee.com/kroste";

    private readonly UpdateService? _updateService;
    private readonly MainWindowViewModel? _mainViewModel;

    // Parameterloser Ctor für den XAML-Designer
    public AboutWindow() : this(null) { }

    public AboutWindow(UpdateService? updateService, MainWindowViewModel? mainViewModel = null)
    {
        AvaloniaXamlLoader.Load(this);
        _updateService = updateService;
        _mainViewModel = mainViewModel;

        var v = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0";
        this.FindControl<TextBlock>("VersionText")!.Text = $"Version {v}";
    }

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        var status = this.FindControl<TextBlock>("UpdateStatus")!;
        var button = this.FindControl<Button>("CheckUpdateButton")!;
        if (_updateService is null && _mainViewModel is null)
            return;

        button.IsEnabled = false;
        status.IsVisible = true;
        status.Text = "Suche nach Updates…";
        try
        {
            // Manueller Check erzwingt eine frische Abfrage (Cache umgehen). Über das
            // MainWindowViewModel, damit auch die Update-Leiste im Hauptfenster erscheint.
            var result = _mainViewModel is not null
                ? await _mainViewModel.RefreshUpdateAsync()
                : await _updateService!.CheckForUpdateAsync(forceRefresh: true);
            status.Text = result is null
                ? "Update-Prüfung fehlgeschlagen (offline?)."
                : result.UpdateAvailable
                    ? $"Version {result.Latest} verfügbar — im Hauptfenster aktualisieren."
                    : "Du hast die aktuelle Version.";
            Log.Info("Manueller Update-Check: {Msg}", status.Text);
        }
        catch (Exception ex)
        {
            status.Text = "Update-Prüfung fehlgeschlagen (offline?).";
            Log.Warn(ex, "Manueller Update-Check fehlgeschlagen");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnGithubClick(object? sender, RoutedEventArgs e) => Launch(GithubUrl);
    private void OnBmcClick(object? sender, RoutedEventArgs e) => Launch(BmcUrl);
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void Launch(string url)
    {
        try { TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url)); }
        catch (Exception ex) { Log.Warn(ex, "Link konnte nicht geöffnet werden: {Url}", url); }
    }
}
