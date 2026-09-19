// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Momentum4.App.Tray;
using Momentum4.App.ViewModels;

namespace Momentum4.App;

/// <summary>
/// Lebenszyklus (docs/architecture.md §7): Die App lebt im Tray. Das Fenster wird bei Bedarf erzeugt und beim Schließen
/// komplett freigegeben (RAM-Budget, §1 Punkt 8); beendet wird nur über „Beenden“ im Tray-Menü.
/// </summary>
public partial class App : Application
{
    private readonly HeadsetHost _host = new();
    private SingleInstance? _instance;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _trimTimer;
    private TrayController? _tray;
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private QuickPanel? _quick;
    private long _quickClosedAt;
    private ILogger? _log;
    private bool _quitting;

    public App()
    {
        InitializeComponent();

        // Spezifikation §31: kein Absturz – protokollieren und weiterlaufen.
        UnhandledException += (_, e) =>
        {
            _host.LogUnhandled(e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Nur eine Instanz: ein zweiter Start holt das Fenster nach vorn und beendet sich.
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            Exit();
            return;
        }

        // Sprache der Oberfläche festlegen, bevor Tray/Fenster mit ihren Texten entstehen.
        Localization.L.Init(_host.Settings.Language);

        // Das Schließen des letzten Fensters beendet die App nicht – sie läuft im Tray weiter.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _instance.ShowRequested += (_, _) => _dispatcher.TryEnqueue(ShowWindow);

        _trimTimer = _dispatcher.CreateTimer();
        _trimTimer.Interval = TimeSpan.FromSeconds(3);
        _trimTimer.IsRepeating = false;
        _trimTimer.Tick += (_, _) => MemoryTrim.Run();

        _log = _host.LoggerFactory.CreateLogger<App>();
        _tray = new TrayController(_host, _dispatcher, ShowWindow, ShowQuickPanel, QuitAsync);

        var minimized = Environment.GetCommandLineArgs().Contains(Autostart.Argument) || _host.Settings.StartMinimized;
        if (!minimized)
        {
            ShowWindow();
        }

        _ = _host.StartAsync(CancellationToken.None);
        if (_host.Settings.CheckForUpdates)
        {
            _ = CheckForUpdateAsync();
        }

        if (minimized)
        {
            _trimTimer.Start();
        }
    }

    // Optionale Update-Prüfung beim Start (nur wenn eingeschaltet): fragt GitHub und meldet einen neueren Release im Tray.
    private async Task CheckForUpdateAsync()
    {
        var info = await UpdateChecker.CheckAsync(CancellationToken.None);
        if (info is { Available: true })
        {
            _dispatcher?.TryEnqueue(() => _tray?.ShowBalloon(
                Localization.L.Get("UpdateBalloonTitle"),
                Localization.L.Get("UpdateBalloonBody", info.LatestVersion)));
        }
    }

    private void ShowWindow()
    {
        if (_quitting)
        {
            return;
        }

        if (_window is not null)
        {
            _window.BringToFront();
            return;
        }

        _trimTimer?.Stop();
        _viewModel = new MainViewModel(_host, _dispatcher!, () => _tray?.Refresh());
        _window = new MainWindow(_viewModel);
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    /// <summary>
    /// Schnellzugriff am Tray-Symbol. Ein Klick aufs Symbol bei offenem Panel schließt es (Fokusverlust) – das folgende
    /// Select öffnet es dann nicht gleich wieder, wie bei den Flyouts von Windows.
    /// </summary>
    private void ShowQuickPanel((int Left, int Top, int Right, int Bottom)? anchor)
    {
        if (_quitting || Environment.TickCount64 - _quickClosedAt < 500)
        {
            _log?.LogDebug("Schnellzugriff: Select direkt nach dem Schließen – bleibt zu.");
            return;
        }

        if (_quick is not null)
        {
            _quick.Show();
            return;
        }

        _trimTimer?.Stop();
        var viewModel = new MainViewModel(_host, _dispatcher!, () => _tray?.Refresh());
        _quick = new QuickPanel(viewModel, anchor, ShowWindow);
        _quick.Activated += (_, e) => _log?.LogDebug("Schnellzugriff: {State}.", e.WindowActivationState);
        _quick.Closed += (_, _) =>
        {
            _log?.LogDebug("Schnellzugriff geschlossen.");
            viewModel.Dispose();
            _quick = null;
            _quickClosedAt = Environment.TickCount64;
            if (!_quitting && _window is null)
            {
                _trimTimer?.Start();
            }
        };
        _quick.Show();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _viewModel?.Dispose();
        _viewModel = null;
        _window = null;
        if (_quitting)
        {
            return;
        }

        if (!_host.Settings.TrayHintShown)
        {
            _tray?.ShowBalloon(Localization.L.Get("TrayHintTitle"), Localization.L.Get("TrayHintBody"));
            _host.SaveSettings(_host.Settings with { TrayHintShown = true });
        }

        _trimTimer?.Start(); // erst freigeben lassen, dann Speicher einsammeln und Arbeitssatz verkleinern
    }

    private async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _trimTimer?.Stop();
        _quick?.Close();
        _window?.Close();
        _tray?.Dispose();
        await _host.DisposeAsync();
        _instance?.Dispose();
        Exit();
    }
}
