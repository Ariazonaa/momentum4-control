// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Momentum4.App.Localization;
using Momentum4.App.Settings;
using Momentum4.Bluetooth;
using Momentum4.Bluetooth.Discovery;
using Momentum4.Bluetooth.Rfcomm;
using Momentum4.Core.Control;
using Momentum4.Core.Diagnostics;
using Momentum4.Core.Presets;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.Transport;

namespace Momentum4.App;

/// <summary>
/// Hält Headset-Verbindung und Service für die App (docs/architecture.md §7): sucht das gekoppelte MOMENTUM 4, startet
/// den Service mit der Production-Policy (nur auf Hardware verifizierte Commands) und meldet dem Supervisor, wenn
/// Windows das Headset oder Bluetooth wieder sieht. Solange GAIA nicht verbunden ist, liest er den Akku alle 30 s über
/// Windows (<see cref="WindowsBatteryReader"/>).
/// </summary>
public sealed class HeadsetHost : IAsyncDisposable
{
    private readonly FileLoggerProvider _fileLogger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private const string ProtocolCategory = "Momentum4.Core.Queue.GaiaCommandQueue";

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private volatile bool _protocolLogging;
    private RfcommTransport? _transport;
    private BluetoothPresenceMonitor? _presence;
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource _batteryPoke = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _batteryLoop;
    private readonly Task _controlLoop;

    public HeadsetHost()
    {
        DataDirectory = Momentum4.Core.AppDataLocation.DataDirectory;
        Settings = AppSettings.Load(SettingsPath);
        _protocolLogging = Settings.ProtocolLogging;
        _fileLogger = new FileLoggerProvider(Path.Combine(DataDirectory, "logs"), "app", LogLevel.Information, retainDays: 14);
        _loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            // Protokoll-Zeilen (jeder TX/RX) nur bei eingeschaltetem Protokoll-Logging; Warnungen immer.
            builder.AddFilter((_, category, level) => level >= LogLevel.Warning || category != ProtocolCategory || _protocolLogging);
            builder.AddProvider(_fileLogger);
        });
        _logger = _loggerFactory.CreateLogger<HeadsetHost>();
        Presets = new EqPresetStore(Path.Combine(DataDirectory, "presets.json"), _loggerFactory.CreateLogger<EqPresetStore>());

        // m4ctl-Befehle über die Named Pipe: Solange die App läuft, spricht nur sie mit dem Headset.
        _controlLoop = Task.Run(() => ControlPipe.ServeAsync(ExecuteControlAsync, _loggerFactory.CreateLogger("Momentum4.App.ControlPipe"), _stopping.Token));
    }

    private Task<ControlResult> ExecuteControlAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        Service is { } service
            ? ControlCommands.ExecuteAsync(args, service, Presets, cancellationToken)
            : Task.FromResult(ControlResult.Error(StartupProblem ?? "Die App sucht das Headset noch."));

    /// <summary>Service gestartet oder Startproblem – die Oberfläche hängt sich dann an den Zustand.</summary>
    public event EventHandler? Changed;

    public string DataDirectory { get; }

    public ILoggerFactory LoggerFactory => _loggerFactory;

    public EqPresetStore Presets { get; }

    public AppSettings Settings { get; private set; }

    public bool ProtocolLogging
    {
        get => _protocolLogging;
        set
        {
            _protocolLogging = value;
            SaveSettings(Settings with { ProtocolLogging = value });
            _logger.LogInformation("Protokoll-Logging {State}.", value ? "an" : "aus");
        }
    }

    /// <summary>Adresse des verwendeten Headsets, sobald es gefunden wurde.</summary>
    public BluetoothAddress? Address { get; private set; }

    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public Momentum4Service? Service { get; private set; }

    /// <summary>Warum der Service nicht läuft (kein Headset gekoppelt …); <c>null</c>, solange alles gut ist.</summary>
    public string? StartupProblem { get; private set; }

    /// <summary>Sucht das Headset und startet den Service. Mehrfach aufrufbar („Erneut suchen“); läuft der Service schon, passiert nichts.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (Service is not null)
            {
                return;
            }

            StartupProblem = null;
            var candidates = await new HeadsetLocator(_loggerFactory.CreateLogger<HeadsetLocator>()).FindAsync(cancellationToken);
            var usable = candidates.Where(c => c.HasGaiaService).ToList();
            if (usable.Count != 1)
            {
                StartupProblem = usable.Count == 0
                    ? L.Get("NoHeadsetProblem")
                    : L.Get("MultipleHeadsets", usable.Count);
                _logger.LogWarning("Start: {Problem}", StartupProblem);
                return;
            }

            var address = usable[0].Address;
            Address = address;
            _transport = new RfcommTransport(address, _loggerFactory.CreateLogger<RfcommTransport>());
            var options = new Momentum4ServiceOptions
            {
                PollInterval = TimeSpan.FromSeconds(Settings.RefreshSeconds),
                RestoreTransparencyAfterRestart = Settings.RestoreTransparency,
            };
            var service = new Momentum4Service(_transport, CommandPolicy.Production, options, _loggerFactory);
            _presence = new BluetoothPresenceMonitor(_loggerFactory.CreateLogger<BluetoothPresenceMonitor>());
            _presence.Changed += (_, change) =>
            {
                if (change.HeadsetConnected == true || change.RadioOn == true)
                {
                    service.NotifyHeadsetAvailable(change.HeadsetConnected == true ? "Headset verbunden" : "Bluetooth an");
                }

                _batteryPoke.TrySetResult();
            };
            service.Store.StateChanged += (_, e) =>
            {
                if (e.Previous.Connection != e.Current.Connection)
                {
                    _batteryPoke.TrySetResult();
                }
            };

            await _presence.StartAsync(address, cancellationToken);
            service.Start();
            Service = service;
            _batteryLoop = RunBatteryFallbackAsync(service, new WindowsBatteryReader(address, _loggerFactory.CreateLogger<WindowsBatteryReader>()), _stopping.Token);
            _logger.LogInformation("Service gestartet für {Address}.", address);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StartupProblem = BluetoothErrors.Describe(ex);
            _logger.LogError(ex, "Start fehlgeschlagen.");
        }
        finally
        {
            _startGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Akku über Windows, solange GAIA nicht verbunden ist (Handy-App belegt den Kanal, Verbindungsversuch scheitert …).
    /// Bei GAIA-Verbindung wird der Wert gelöscht, damit später kein alter Stand angezeigt wird.
    /// </summary>
    private async Task RunBatteryFallbackAsync(Momentum4Service service, WindowsBatteryReader reader, CancellationToken stopping)
    {
        int? last = null;
        while (!stopping.IsCancellationRequested)
        {
            var poke = Volatile.Read(ref _batteryPoke).Task;
            try
            {
                int? percent = service.Store.Current.Connection == Core.State.ConnectionState.Connected ? null : await reader.ReadAsync(stopping);
                if (percent != last)
                {
                    if (percent is not null)
                    {
                        _logger.LogInformation("Akku über Windows: {Percent} % (GAIA nicht verbunden).", percent);
                    }

                    last = percent;
                    service.Store.Apply(new Core.State.WindowsBatteryChanged(percent));
                }

                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(30), stopping), poke);
                if (poke.IsCompleted)
                {
                    Interlocked.Exchange(ref _batteryPoke, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    await Task.Delay(TimeSpan.FromSeconds(2), stopping); // Windows braucht nach dem Verbinden einen Moment für HFP
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public void LogUnhandled(Exception exception) => _logger.LogError(exception, "Unbehandelte Ausnahme.");

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        if (Service is { } service)
        {
            service.RestoreTransparencyAfterRestart = settings.RestoreTransparency;
        }

        try
        {
            settings.Save(SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "settings.json konnte nicht gespeichert werden.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_batteryLoop is not null)
        {
            await _batteryLoop;
        }

        await _controlLoop;

        _presence?.Dispose();
        if (Service is not null)
        {
            await Service.DisposeAsync();
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }

        _loggerFactory.Dispose();
        _fileLogger.Dispose();
        _startGate.Dispose();
        _stopping.Dispose();
    }
}
