// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Momentum4.Bluetooth.Discovery;
using Momentum4.Bluetooth.Rfcomm;
using Momentum4.Core.Control;
using Momentum4.Core.Diagnostics;
using Momentum4.Core.Presets;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;

// m4ctl (docs/architecture.md §6.6): Läuft die App, führt sie den Befehl über die Named Pipe aus. Sonst verbindet
// m4ctl selbst – aber nie neben der App, denn zwei Prozesse auf demselben RFCOMM-Kanal teilen sich die Antworten.
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
{
    Console.WriteLine(ControlCommands.Help);
    return 2;
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (await ControlPipe.TryExecuteInAppAsync(args, TimeSpan.FromMilliseconds(500), cancellation.Token) is { } viaApp)
    {
        return Print(viaApp);
    }

    if (Process.GetProcessesByName("Momentum4Control").Length > 0)
    {
        return Print(ControlResult.Error("Die App läuft, nimmt aber keine m4ctl-Befehle an (ältere Version?). Bitte die App neu starten oder beenden."));
    }

    return Print(await RunDirectAsync(args, cancellation.Token));
}
catch (OperationCanceledException)
{
    return Print(ControlResult.Error("Abgebrochen (Zeitlimit 60 s oder Strg+C)."));
}

static async Task<ControlResult> RunDirectAsync(string[] args, CancellationToken cancellationToken)
{
    var dataDirectory = Momentum4.Core.AppDataLocation.DataDirectory;
    using var fileLogger = new FileLoggerProvider(Path.Combine(dataDirectory, "logs"), "m4ctl", LogLevel.Information, retainDays: 14);
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddFilter("Momentum4.Core.Queue.GaiaCommandQueue", LogLevel.Warning); // kein TX/RX pro Aufruf, wie in der App
        builder.AddProvider(fileLogger);
    });

    var usable = (await new HeadsetLocator(loggerFactory.CreateLogger<HeadsetLocator>()).FindAsync(cancellationToken)).Where(c => c.HasGaiaService).ToList();
    if (usable.Count != 1)
    {
        return ControlResult.Error(usable.Count == 0
            ? "Kein gekoppeltes MOMENTUM 4 gefunden."
            : $"{usable.Count} MOMENTUM 4 gekoppelt – mehrere Headsets werden noch nicht unterstützt.");
    }

    await using var transport = new RfcommTransport(usable[0].Address, loggerFactory.CreateLogger<RfcommTransport>());
    var options = new Momentum4ServiceOptions { PollInterval = TimeSpan.FromMinutes(10), RegisterNotifications = false };
    await using var service = new Momentum4Service(transport, CommandPolicy.Production, options, loggerFactory);
    service.Start();

    var started = Stopwatch.StartNew();
    while (service.Store.Current.Connection != ConnectionState.Connected && started.Elapsed < TimeSpan.FromSeconds(15))
    {
        await Task.Delay(50, cancellationToken);
    }

    if (service.Store.Current.Connection != ConnectionState.Connected)
    {
        var reason = service.Store.Current.LastError is { } error ? $" ({error})" : string.Empty;
        await service.StopAsync();
        return ControlResult.Error($"Keine Verbindung zum Headset{reason}. Ist es an und mit diesem PC verbunden?");
    }

    var presets = new EqPresetStore(Path.Combine(dataDirectory, "presets.json"), loggerFactory.CreateLogger<EqPresetStore>());
    var result = await ControlCommands.ExecuteAsync(args, service, presets, cancellationToken);
    await service.StopAsync();
    return result;
}

static int Print(ControlResult result)
{
    (result.ExitCode == 0 ? Console.Out : Console.Error).WriteLine(result.Output);
    return result.ExitCode;
}
