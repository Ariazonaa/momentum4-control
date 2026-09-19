// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Momentum4.Bluetooth;
using Momentum4.Bluetooth.Discovery;
using Momentum4.Bluetooth.Rfcomm;
using Momentum4.Core.Device;
using Momentum4.Core.Diagnostics;
using Momentum4.Core.Presets;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Features;
using Windows.Devices.Bluetooth;

// Proof-of-Concept für die Hardware-Stufen. Dieses Tool ruft ITransport.WriteAsync nie direkt auf:
// Gesendet wird nur über die GaiaCommandQueue. read/state geben per Policy ausschließlich die Lese-Commands ihrer
// Stufe aus protocol.md §12 frei; monitor nutzt die Production-Policy (nur verifizierte Commands, alle lesend).
// stage6 erlaubt per CommandPolicy.Only genau seine Getter und Setter (0x1A00, 0x1A02, 0x1804); set und preset erlauben
// die Noise-Control- und EQ-Setter (0x1A00, 0x1A02, 0x1A04, 0x1804, 0x1001, 0x1008, 0x0803), preset apply schreibt nur
// 0x1001; peer erlaubt nur 0x1402/0x1404 – alles nur auf ausdrücklichen Aufruf. Gesperrte Commands (protocol.md §9,
// darunter 0x1403) sind nie erreichbar.

var options = PocOptions.Parse(args);
if (options is null)
{
    PocOptions.PrintUsage();
    return 2;
}

var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momentum4Control", "logs");
using var fileLogger = new FileLoggerProvider(logDirectory, "poc", LogLevel.Debug);
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss.fff ";
    });
    builder.AddFilter<ConsoleLoggerProvider>(null, options.Verbose ? LogLevel.Debug : LogLevel.Information);
    builder.AddProvider(fileLogger);
});
var log = loggerFactory.CreateLogger("m4poc");
log.LogInformation("m4poc {Command} – Log-Datei: {Path}", options.Command, fileLogger.CurrentFilePath);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return options.Command switch
    {
        "list" => await ListAsync(),
        "features" => await FeaturesAsync(),
        "probe20" => await Probe20Async(),
        "setname" => await SetNameAsync(),
        "gesture" => await GestureAsync(),
        "autopause" => await AutoPauseAsync(),
        "reasons" => await ReasonsAsync(),
        "winbattery" => await WinBatteryAsync(),
        "sdp" => await SdpAsync(),
        "listen" => await ListenAsync(),
        "read" => await ReadAsync(),
        "state" => await StateAsync(),
        "monitor" => await MonitorAsync(),
        "stage6" => await Stage6Async(),
        "set" => await SetAsync(),
        "preset" => await PresetAsync(),
        "peer" => await PeerAsync(),
        _ => 2,
    };
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    log.LogWarning("Abgebrochen.");
    return 130;
}
catch (Exception ex)
{
    log.LogError("Fehlgeschlagen: {Reason}", BluetoothErrors.Describe(ex));
    log.LogDebug(ex, "Details");
    return 1;
}

async Task<int> ListAsync()
{
    var candidates = await new HeadsetLocator(loggerFactory.CreateLogger<HeadsetLocator>()).FindAsync(cancellation.Token);
    if (candidates.Count == 0)
    {
        log.LogWarning("Kein gekoppeltes Gerät mit '{Marker}' im Namen gefunden.", ProtocolConstants.DeviceNameMarker);
        return 1;
    }

    foreach (var c in candidates)
    {
        Console.WriteLine($"{c.Address}  {c.Name,-24} verbunden: {(c.IsConnected ? "ja" : "nein"),-4} GAIA-Dienst: {(c.HasGaiaService ? "ja" : "nein")}");
    }

    return 0;
}

async Task<int> SdpAsync()
{
    var address = await ResolveAddressAsync();
    using var device = await BluetoothDevice.FromBluetoothAddressAsync(address.Value).AsTask(cancellation.Token)
        ?? throw new HeadsetUnavailableException($"Kein Gerät mit der Adresse {address}.");

    Console.WriteLine($"Gerät: {device.Name} {address}, Windows-Verbindung: {device.ConnectionStatus}");
    var found = 0;
    foreach (var mode in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
    {
        var infos = await GaiaServiceResolver.InspectAllAsync(device, mode, log, cancellation.Token);
        found += infos.Count;
        Console.WriteLine();
        Console.WriteLine($"== {mode}: {infos.Count} Eintrag/Einträge für {ProtocolConstants.GaiaServiceUuid} ==");
        for (var i = 0; i < infos.Count; i++)
        {
            var info = infos[i];
            Console.WriteLine($"[{i}] Host:         {info.HostName}");
            Console.WriteLine($"    Dienst:       {info.ServiceName}");
            Console.WriteLine($"    SDP-Name:     {info.Sdp?.ServiceName ?? "?"}");
            Console.WriteLine($"    RFCOMM-Kanal: {info.Sdp?.RfcommChannel?.ToString(CultureInfo.InvariantCulture) ?? "unbekannt"}");
            if (info.Sdp is null)
            {
                continue;
            }

            foreach (var (id, raw) in info.Sdp.RawAttributes.OrderBy(kv => kv.Key))
            {
                Console.WriteLine($"    0x{id:X4}  {Hex.Format(raw)}");
                var decoded = info.Sdp.Describe(id);
                if (decoded is not null)
                {
                    Console.WriteLine($"            = {decoded}");
                }
            }
        }
    }

    return found > 0 ? 0 : 1;
}

async Task<int> ListenAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);

    var received = 0;
    var chunks = 0;
    var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.DataReceived += (_, data) =>
    {
        Interlocked.Add(ref received, data.Length);
        Interlocked.Increment(ref chunks);
        log.LogInformation("RX {Count} Byte: {Hex}", data.Length, Hex.Format(data.Span));
    };
    transport.StateChanged += (_, state) =>
    {
        if (state == TransportState.Lost)
        {
            lost.TrySetResult();
        }
    };

    var stopwatch = Stopwatch.StartNew();
    await transport.ConnectAsync(cancellation.Token);
    log.LogInformation("Verbunden nach {Ms} ms. Lausche {Seconds} s passiv (es wird nichts gesendet) …", stopwatch.ElapsedMilliseconds, options.Seconds);

    var finished = await Task.WhenAny(lost.Task, Task.Delay(TimeSpan.FromSeconds(options.Seconds), cancellation.Token));
    if (finished == lost.Task)
    {
        log.LogWarning("Verbindung nach {Seconds:0.0} s verloren.", stopwatch.Elapsed.TotalSeconds);
    }
    else
    {
        await finished; // Abbruch weiterreichen
    }

    log.LogInformation(
        "Ergebnis: {Chunks} Lesevorgänge, {Bytes} Byte empfangen, Zustand {State}, RFCOMM-Kanal {Channel}.",
        chunks, received, transport.State, transport.ServiceInfo?.Sdp?.RfcommChannel?.ToString(CultureInfo.InvariantCulture) ?? "unbekannt");

    await transport.DisconnectAsync();
    return finished == lost.Task ? 1 : 0;
}

// Hardware-Stufe 2 + 3 (protocol.md §12): nur lesende, freigegebene Commands, einer nach dem anderen.
async Task<int> ReadAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = CommandPolicy.ForHardwareTest(
        CommandCatalog.GetModelId,
        CommandCatalog.GetFirmwareVersion,
        CommandCatalog.QcGetApiVersion,
        CommandCatalog.GetBatteryLevel);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());
    var client = new Momentum4Client(queue);

    var started = Stopwatch.StartNew();
    var exchange = new List<object>();
    queue.Traffic += (_, t) =>
    {
        lock (exchange)
        {
            exchange.Add(new
            {
                atMs = started.ElapsedMilliseconds,
                dir = t.Direction == TrafficDirection.Tx ? "TX" : "RX",
                hex = Hex.Format(t.Raw),
                command = t.CommandName,
                durationMs = t.Duration is { } d ? (int?)d.TotalMilliseconds : null,
            });
        }
    };
    var notifications = 0;
    queue.NotificationReceived += (_, _) => Interlocked.Increment(ref notifications);

    await transport.ConnectAsync(cancellation.Token);
    var channel = transport.ServiceInfo?.Sdp?.RfcommChannel;
    log.LogInformation("Verbunden (RFCOMM-Kanal {Channel}). Policy: {Policy}", channel?.ToString(CultureInfo.InvariantCulture) ?? "?", policy.Name);

    var results = new Dictionary<string, string>();
    async Task Step<T>(string name, Func<CancellationToken, Task<T>> action)
    {
        try
        {
            var value = await action(cancellation.Token);
            results[name] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            log.LogInformation("==> {Name}: {Value}", name, results[name]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            results[name] = "FEHLER: " + BluetoothErrors.Describe(ex);
            log.LogWarning("==> {Name}: {Error}", name, results[name]);
        }
    }

    await Step("modelId", client.GetModelIdAsync);
    await Step("firmwareVersion", client.GetFirmwareVersionAsync);
    await Step("gaiaApiVersion", client.GetGaiaApiVersionAsync);
    await Step("batteryPercent", client.GetBatteryLevelAsync);

    // Kurz weiter lauschen: kommt danach noch etwas Unaufgefordertes?
    await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
    await transport.DisconnectAsync();

    var capture = new
    {
        tool = "m4poc read",
        date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        headset = address.ToAnonymizedString(),
        serviceIndex = options.ServiceIndex,
        rfcommChannel = channel,
        results,
        unsolicitedNotifications = notifications,
        exchange,
    };
    var capturePath = Path.Combine(logDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}-idx{options.ServiceIndex}.json");
    await File.WriteAllTextAsync(capturePath, JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
    log.LogInformation("Mitschnitt: {Path}", capturePath);

    return results.Values.Any(v => v.StartsWith("FEHLER", StringComparison.Ordinal)) ? 1 : 0;
}

// Error-Reason-Codes sammeln: erlaubte Befehle mit ungueltigen Parametern (keine Zustandsaenderung).
async Task<int> ReasonsAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = CommandPolicy.Only(
        CommandCatalog.GetPairedDeviceInfo, CommandCatalog.GetEqBand, CommandCatalog.GetTimer,
        CommandCatalog.RegisterNotification, CommandCatalog.GetMmiConfig, CommandCatalog.GetEqBandFrequency,
        CommandCatalog.GetPairedDeviceStatus, CommandCatalog.GetAncLevel);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());
    await transport.ConnectAsync(cancellation.Token);

    async Task Probe(string label, CommandDescriptor cmd, byte[] payload)
    {
        try
        {
            var rsp = await queue.SendAsync(cmd, payload, cancellation.Token);
            Console.WriteLine($"  {label,-42} -> OK [{Hex.Format(rsp.Payload)}] (kein Fehler)");
        }
        catch (GaiaErrorException ex)
        {
            Console.WriteLine($"  {label,-42} -> Reason 0x{ex.Reason:X2}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  {label,-42} -> {BluetoothErrors.Describe(ex)}");
        }
    }

    Console.WriteLine("Error-Reason-Codes (ungueltige Parameter, keine Zustandsaenderung):");
    await Probe("GetPairedDeviceInfo [FF] (Index 255)", CommandCatalog.GetPairedDeviceInfo, new byte[] { 0xFF });
    await Probe("GetPairedDeviceStatus [FF]", CommandCatalog.GetPairedDeviceStatus, new byte[] { 0xFF });
    await Probe("GetEqBand [FF] (Band 255)", CommandCatalog.GetEqBand, new byte[] { 0xFF });
    await Probe("GetEqBandFrequency [FF]", CommandCatalog.GetEqBandFrequency, new byte[] { 0xFF });
    await Probe("GetTimer [FF] (Timer 255)", CommandCatalog.GetTimer, new byte[] { 0xFF });
    await Probe("GetMmiConfig [00 00] (unbelegtes Muster)", CommandCatalog.GetMmiConfig, new byte[] { 0x00, 0x00 });
    await Probe("RegisterNotification [FE] (Feature 254)", CommandCatalog.RegisterNotification, new byte[] { 0xFE });
    await Probe("RegisterNotification [12] (Feature 18, hat M4 nicht)", CommandCatalog.RegisterNotification, new byte[] { 0x12 });

    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
    await transport.DisconnectAsync();
    return 0;
}

// Automatische Pause bei Transparenz (0x1800 setzen, 0x1801 zuruecklesen). Write -> Verify.
async Task<int> AutoPauseAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = CommandPolicy.Only(CommandCatalog.SetTransparentHearingMode, CommandCatalog.GetTransparentHearingMode);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());
    await transport.ConnectAsync(cancellation.Token);
    var on = options.SetValue == "on";
    Console.WriteLine($"Vorher:  0x1801 = {(await queue.SendAsync(CommandCatalog.GetTransparentHearingMode, cancellation.Token)).Payload[0]}");
    await queue.SendAsync(CommandCatalog.SetTransparentHearingMode, new byte[] { (byte)(on ? 1 : 0) }, cancellation.Token);
    Console.WriteLine($"Nachher: 0x1801 = {(await queue.SendAsync(CommandCatalog.GetTransparentHearingMode, cancellation.Token)).Payload[0]}");
    await transport.DisconnectAsync();
    return 0;
}

// Gestenbelegung (Feature 11): die zwei M4-Slots (Taste 0, Muster 9 und 10) lesen oder eine Geste setzen.
// set schreibt 0x1600 [00, pattern, function] und liest zurueck. Nur fuer den Hardware-Test.
async Task<int> GestureAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = CommandPolicy.ForHardwareTest(CommandCatalog.GetMmiConfig, CommandCatalog.SetMmiConfig);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());
    await transport.ConnectAsync(cancellation.Token);

    async Task Show(byte pattern)
    {
        var g = MmiCodec.DecodeGesture((await queue.SendAsync(CommandCatalog.GetMmiConfig, MmiCodec.EncodeQuery(0, pattern), cancellation.Token)).Payload);
        Console.WriteLine($"  Taste {g.Button}, Muster {g.Pattern}: {g.Function}");
    }

    if (options.SetWhat == "set")
    {
        var pattern = byte.Parse(options.SetValue, System.Globalization.CultureInfo.InvariantCulture);
        var function = (TouchFunction)byte.Parse(options.PeerName, System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine("Vorher:");
        await Show(pattern);
        await queue.SendAsync(CommandCatalog.SetMmiConfig, MmiCodec.EncodeGesture(new TouchGesture(0, pattern, function)), cancellation.Token);
        Console.WriteLine($"Gesetzt: Muster {pattern} = {function}");
        Console.WriteLine("Nachher:");
        await Show(pattern);
    }
    else
    {
        Console.WriteLine("Gestenbelegung (M4-Slots):");
        await Show(9);
        await Show(10);
    }

    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
    await transport.DisconnectAsync();
    return 0;
}

// localName (Feature 20) Namen setzen und pruefen (Write -> Verify), danach kann der Aufrufer zuruecksetzen.
// Achtung: Umbenennen loest laut offizieller App evtl. einen Neustart des Headsets aus.
async Task<int> SetNameAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = CommandPolicy.Only(CommandCatalog.SetLocalName, CommandCatalog.GetLocalName);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());
    var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    transport.StateChanged += (_, st) => { if (st == TransportState.Lost) lost.TrySetResult(); };
    await transport.ConnectAsync(cancellation.Token);

    var name = options.SetValue;
    var payload = System.Text.Encoding.UTF8.GetBytes(name);
    Console.WriteLine($"Vorher:  \"{System.Text.Encoding.UTF8.GetString((await queue.SendAsync(CommandCatalog.GetLocalName, cancellation.Token)).Payload)}\"");
    await queue.SendAsync(CommandCatalog.SetLocalName, payload, cancellation.Token);
    Console.WriteLine($"Gesetzt: 0x2801 [{Hex.Format(payload)}] = \"{name}\"");

    // Kurz auf einen moeglichen Neustart/Verbindungsverlust warten
    var raced = await Task.WhenAny(lost.Task, Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token));
    if (raced == lost.Task)
    {
        Console.WriteLine("HINWEIS: Verbindung verloren (vermutlich Neustart des Headsets).");
        return 2;
    }

    Console.WriteLine($"Nachher: \"{System.Text.Encoding.UTF8.GetString((await queue.SendAsync(CommandCatalog.GetLocalName, cancellation.Token)).Payload)}\"");
    await transport.DisconnectAsync();
    return 0;
}

// localName (Feature 20) nur lesend prüfen: ungerade Ops 0x2801/03/05/07 mit leerer Payload. Ein Getter antwortet mit
// Daten (mutmaßlich der Name als String), ein Setter lehnt leere Payload mit Reason 0x05 ab. Kein Schreiben.
async Task<int> Probe20Async()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var candidates = new[] { CommandCatalog.GetLocalName };
    var policy = CommandPolicy.ForHardwareTest(candidates);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());
    await transport.ConnectAsync(cancellation.Token);
    foreach (var cmd in candidates)
    {
        try
        {
            var payload = (await queue.SendAsync(cmd, cancellation.Token)).Payload;
            var text = System.Text.Encoding.UTF8.GetString(payload).TrimEnd(' ');
            Console.WriteLine($"  0495/{cmd.Command.Value:X4} -> [{Hex.Format(payload)}]  \"{text}\"");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  0495/{cmd.Command.Value:X4} -> {BluetoothErrors.Describe(ex)}");
        }
    }

    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
    await transport.DisconnectAsync();
    return 0;
}

// Unterstützte Features und Tastenbelegung – nur lesend (research.md §4.13, protocol.md §5, §6.7).
async Task<int> FeaturesAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = CommandPolicy.ForHardwareTest(
        CommandCatalog.QcGetSupportedFeatures, CommandCatalog.QcGetSupportedFeaturesNext,
        CommandCatalog.GetSupportedFeatures, CommandCatalog.GetSupportedFeaturesNext,
        CommandCatalog.IsDefaultMmiConfig, CommandCatalog.GetMmiConfig, CommandCatalog.GetCodec,
        CommandCatalog.QcGetSerialNumber, CommandCatalog.GetPromptLanguage, CommandCatalog.GetSidetone,
        CommandCatalog.GetTransparentHearingTw);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());
    await transport.ConnectAsync(cancellation.Token);

    async Task<byte[]?> Read(CommandDescriptor command, byte[] payload)
    {
        try
        {
            var response = (await queue.SendAsync(command, payload, cancellation.Token)).Payload;
            Console.WriteLine($"  {command.Vendor:X4}/{command.Command.Value:X4} [{Hex.Format(payload)}] → [{Hex.Format(response)}]");
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  {command.Vendor:X4}/{command.Command.Value:X4} [{Hex.Format(payload)}] → FEHLER {BluetoothErrors.Describe(ex)}");
            return null;
        }
    }

    async Task Features(string label, CommandDescriptor first, CommandDescriptor next)
    {
        Console.WriteLine(label);
        var list = new List<string>();
        var response = await Read(first, []);
        for (var round = 0; response is { Length: >= 1 } && round < 8; round++)
        {
            for (var i = 1; i + 1 < response.Length; i += 2)
            {
                list.Add($"{response[i]} (v{response[i + 1]})");
            }

            if (response[0] != 1)
            {
                break;
            }

            response = await Read(next, []);
        }

        Console.WriteLine($"  → {list.Count} Features: {string.Join(", ", list)}");
    }

    await Features("Qualcomm (001D):", CommandCatalog.QcGetSupportedFeatures, CommandCatalog.QcGetSupportedFeaturesNext);
    await Features("Sennheiser (0495):", CommandCatalog.GetSupportedFeatures, CommandCatalog.GetSupportedFeaturesNext);

    Console.WriteLine("Codec / Seriennummer / Sprache:");
    await Read(CommandCatalog.GetCodec, []);
    await Read(CommandCatalog.QcGetSerialNumber, []);
    await Read(CommandCatalog.GetPromptLanguage, []);
    await Read(CommandCatalog.GetSidetone, []);
    await Read(CommandCatalog.GetTransparentHearingTw, []);
    Console.WriteLine("Tastenbelegung (m4.json: button 0/1, pattern 0…9):");
    await Read(CommandCatalog.IsDefaultMmiConfig, []);
    for (byte button = 0; button <= 2; button++)
    {
        for (byte pattern = 0; pattern <= 20; pattern++)
        {
            await Read(CommandCatalog.GetMmiConfig, [button, pattern]);
        }
    }

    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
    await transport.DisconnectAsync();
    return 0;
}

// Akku über Windows (WindowsBatteryReader), sendet nichts ans Headset.
async Task<int> WinBatteryAsync()
{
    var address = await ResolveAddressAsync();
    var reader = new WindowsBatteryReader(address, loggerFactory.CreateLogger<WindowsBatteryReader>());
    var percent = await reader.ReadAsync(cancellation.Token);
    Console.WriteLine(percent is { } p ? $"Akku laut Windows: {p} %" : "Kein Wert von Windows (Headset nicht verbunden oder Eigenschaft fehlt).");
    return percent is null ? 1 : 0;
}

// Hardware-Stufe 4 (protocol.md §12): alle Getter aus dem Testplan, nur lesend.
async Task<int> StateAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = CommandPolicy.ForHardwareTest(
        CommandCatalog.GetAncEnabled, CommandCatalog.GetAncModes, CommandCatalog.GetAncLevel,
        CommandCatalog.GetTransparentHearingStatus, CommandCatalog.GetSoundMode, CommandCatalog.GetBtCompatibilityMode,
        CommandCatalog.GetPersonalizationState, CommandCatalog.GetEqConfig, CommandCatalog.GetEqBand,
        CommandCatalog.GetEqAllGains, CommandCatalog.GetEqBandFrequency, CommandCatalog.GetBassBoost,
        CommandCatalog.GetPairedDeviceCount, CommandCatalog.GetPairedDeviceInfo, CommandCatalog.GetOwnDeviceIndex,
        CommandCatalog.GetMaxConnections, CommandCatalog.GetOnHeadDetection, CommandCatalog.GetAutoAnswer,
        CommandCatalog.GetSmartPause, CommandCatalog.GetComfortCall, CommandCatalog.GetTouchLock, CommandCatalog.GetTimer,
        CommandCatalog.GetPhysicalState, CommandCatalog.GetAudioPromptMode, CommandCatalog.GetPromptLanguage,
        CommandCatalog.GetAvailablePromptLanguages, CommandCatalog.GetPromptLanguageVersions,
        CommandCatalog.GetChargingState, CommandCatalog.GetTransparentHearingMode, CommandCatalog.GetTransparentHearingTw);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());

    var steps = new List<object>();
    var notifications = 0;
    queue.NotificationReceived += (_, _) => Interlocked.Increment(ref notifications);

    // Führt einen Getter aus; liefert die Antwort-Payload oder null. Das Ergebnis wird angezeigt und protokolliert.
    async Task<byte[]?> Step(CommandDescriptor command, byte[] payload, Func<byte[], string>? decode = null)
    {
        var label = payload.Length > 0 ? $"{command.Name} [{Hex.Format(payload)}]" : command.Name;
        string decoded;
        byte[]? response = null;
        try
        {
            response = (await queue.SendAsync(command, payload, cancellation.Token)).Payload;
            try
            {
                decoded = decode?.Invoke(response) ?? "(roh)";
            }
            catch (ProtocolFormatException ex)
            {
                decoded = "FORMAT ABWEICHEND: " + ex.Message;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            decoded = "FEHLER: " + BluetoothErrors.Describe(ex);
        }

        Console.WriteLine($"  {label,-34} [{(response is null ? "–" : Hex.Format(response)),-40}] {decoded}");
        steps.Add(new
        {
            command = command.Name,
            id = $"{command.Vendor:X4}/{command.Command.Value:X4}",
            request = Hex.Format(payload),
            response = response is null ? null : Hex.Format(response),
            decoded,
        });
        return response;
    }

    await transport.ConnectAsync(cancellation.Token);
    Console.WriteLine($"Verbunden (RFCOMM-Kanal {transport.ServiceInfo?.Sdp?.RfcommChannel}). PC-Name: {Environment.MachineName}");

    Console.WriteLine("ANC / Transparent Hearing / Sound:");
    await Step(CommandCatalog.GetAncEnabled, [], p => AncCodec.DecodeEnabled(p) ? "ANC an" : "ANC aus");
    await Step(CommandCatalog.GetAncModes, [], p =>
    {
        var t = AncCodec.DecodeModes(p);
        return $"Anti-Wind {t.AntiWind}, Comfort {t.Comfort}, Adaptive {t.Adaptive}";
    });
    await Step(CommandCatalog.GetAncLevel, [], p => $"Pegel {AncCodec.DecodeLevel(p)}");
    await Step(CommandCatalog.GetTransparentHearingStatus, [], p => SwitchCodec.Decode(p, "TH") ? "an" : "aus");
    await Step(CommandCatalog.GetSoundMode, [], p => GenericAudioCodec.DecodeSoundMode(p).ToString());
    await Step(CommandCatalog.GetBtCompatibilityMode, [], p => DeviceCodec.DecodeBtCompatibilityMode(p).ToString());
    await Step(CommandCatalog.GetPersonalizationState, [], p => DeviceCodec.DecodePersonalizationState(p).ToString());

    Console.WriteLine("Equalizer:");
    EqConfig? eq = null;
    await Step(CommandCatalog.GetEqConfig, [], p =>
    {
        eq = UserEqCodec.DecodeConfig(p);
        return $"{eq.BandCount} Bänder, {eq.MinGainDb} … {eq.MaxGainDb} dB";
    });
    var bands = Math.Min(eq?.BandCount ?? 0, 10);
    for (var band = 0; band < bands; band++)
    {
        var b = band;
        var r = await Step(CommandCatalog.GetEqBand, [(byte)b], p => $"{UserEqCodec.DecodeBandGain(p, b)} dB");
        if (r is { Length: 0 })
        {
            Console.WriteLine("  Leere Antwort auf indizierten Getter – Abbruch (Regel aus protocol.md §9).");
            break;
        }
    }

    await Step(CommandCatalog.GetEqAllGains, [0x00]);
    for (var band = 0; band < bands; band++)
    {
        var r = await Step(CommandCatalog.GetEqBandFrequency, [(byte)band]);
        if (r is { Length: 0 })
        {
            Console.WriteLine("  Leere Antwort auf indizierten Getter – Abbruch (Regel aus protocol.md §9).");
            break;
        }
    }

    await Step(CommandCatalog.GetBassBoost, [], p => UserEqCodec.DecodeBassBoost(p) ? "an" : "aus");

    Console.WriteLine("Multipoint:");
    var count = 0;
    await Step(CommandCatalog.GetPairedDeviceCount, [], p =>
    {
        count = DeviceManagementCodec.DecodeCount(p);
        return $"{count} gekoppelte Geräte";
    });
    for (var index = 0; index < Math.Min(count, 16); index++)
    {
        var i = index;
        var r = await Step(CommandCatalog.GetPairedDeviceInfo, [(byte)i], p =>
        {
            var e = DeviceManagementCodec.DecodeEntry(p, i);
            return $"#{e.Index} '{e.Name}' Priorität {e.Priority}, Status {e.ConnectionStatus}";
        });
        if (r is { Length: 0 })
        {
            Console.WriteLine("  Leere Antwort auf indizierten Getter – Abbruch (Regel aus protocol.md §9).");
            break;
        }
    }

    await Step(CommandCatalog.GetOwnDeviceIndex, [], p => $"Index {DeviceManagementCodec.DecodeOwnIndex(p)}");
    await Step(CommandCatalog.GetMaxConnections, [], p => $"max. {DeviceManagementCodec.DecodeMaxConnections(p)}");

    Console.WriteLine("Geräteeinstellungen:");
    await Step(CommandCatalog.GetOnHeadDetection, [], p => SwitchCodec.Decode(p, "On-Head") ? "an" : "aus");
    await Step(CommandCatalog.GetAutoAnswer, [], p => SwitchCodec.Decode(p, "Auto-Answer") ? "an" : "aus");
    await Step(CommandCatalog.GetSmartPause, [], p => SwitchCodec.Decode(p, "Smart Pause") ? "an" : "aus");
    await Step(CommandCatalog.GetComfortCall, [], p => SwitchCodec.Decode(p, "Comfort Call") ? "an" : "aus");
    await Step(CommandCatalog.GetTouchLock, [], p => SwitchCodec.Decode(p, "Touch-Sperre") ? "gesperrt (1)" : "nicht gesperrt (0)");
    await Step(CommandCatalog.GetPhysicalState, [], p => $"Tragezustand {DeviceCodec.DecodeWearState(p)}");

    // Töne & Sprachansagen (m4.json; protocol.md §6.3) – nur lesend, Bedeutung noch nicht geprüft
    Console.WriteLine("Töne & Sprachansagen (nur lesend):");
    string[] languages = ["EN", "DE", "FR", "ES", "ZH", "JA", "RU", "KO"];
    string Language(byte code) => code < languages.Length ? languages[code] : $"?{code}";
    await Step(CommandCatalog.GetAudioPromptMode, [], p => p is [var mode] ? mode switch { 0 => "0 = Töne und Stimme aus", 1 => "1 = nur Töne", 2 => "2 = Töne und Stimme", _ => $"{mode} = unbekannt" } : "Länge unerwartet");
    await Step(CommandCatalog.GetPromptLanguage, [], p => p is [var code] ? $"Sprache {Language(code)}" : "Länge unerwartet");
    await Step(CommandCatalog.GetAvailablePromptLanguages, [], p => $"{p.Length} Byte: {string.Join(", ", p.Select(Language))}");
    await Step(CommandCatalog.GetPromptLanguageVersions, [0x00], p => p.Length >= 1 ? $"aktiv {p[0]}, {(p.Length - 1) / 4} Einträge à 4 Byte, Rest {(p.Length - 1) % 4}" : "leer");

    // Weitere Getter aus m4.json (protocol.md §6.2, §6.8) – nur lesend
    Console.WriteLine("Weitere Getter (nur lesend):");
    await Step(CommandCatalog.GetChargingState, [], p => p.Length >= 1 ? p[0] switch { 0 => "0 = nicht angeschlossen", 1 => "1 = lädt", 2 => "2 = voll", var v => $"{v} = unbekannt" } + (p.Length > 1 ? $" (+{p.Length - 1} Byte)" : string.Empty) : "leer");
    await Step(CommandCatalog.GetTransparentHearingMode, [], p => p is [var mode] ? mode switch { 0 => "0 (m4.json: Musik läuft weiter / aus)", 1 => "1 (m4.json: Musik stoppt / an)", _ => $"{mode} = unbekannt" } : "Länge unerwartet");
    await Step(CommandCatalog.GetTransparentHearingTw, [], p => p is [var status] ? $"Status {status}" : "Länge unerwartet");
    await Step(CommandCatalog.GetTimer, [BatteryCodec.TimerAutoPowerOff], p =>
    {
        var s = BatteryCodec.DecodeTimerSeconds(p, BatteryCodec.TimerAutoPowerOff);
        return s == 0 ? "Auto-Off: nie" : $"Auto-Off: {s} s ({s / 60} min)";
    });

    await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
    await transport.DisconnectAsync();

    var capturePath = Path.Combine(logDirectory, $"state-{DateTime.Now:yyyyMMdd-HHmmss}-idx{options.ServiceIndex}.json");
    await File.WriteAllTextAsync(
        capturePath,
        JsonSerializer.Serialize(
            new
            {
                tool = "m4poc state",
                date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                headset = address.ToAnonymizedString(),
                machineName = Environment.MachineName,
                unsolicitedNotifications = notifications,
                steps,
            },
            new JsonSerializerOptions { WriteIndented = true }),
        cancellation.Token);
    Console.WriteLine($"Unaufgeforderte Notifications: {notifications}. Mitschnitt: {capturePath}");
    return 0;
}

// Live-Monitor: kompletter Service (Verbindung, Wiederverbinden, Polling, optional Notifications).
// Liest nur; mit --notifications wird zusätzlich 0495/0007 (Notification-Anmeldung) freigegeben (Hardware-Stufe 5).
async Task<int> MonitorAsync()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = options.Notifications ? CommandPolicy.ForHardwareTest(CommandCatalog.RegisterNotification) : CommandPolicy.Production;
    var serviceOptions = new Momentum4ServiceOptions
    {
        PollInterval = TimeSpan.FromSeconds(options.PollSeconds),
        RegisterNotifications = options.Notifications,
        NotificationFeatures = options.Features ?? new Momentum4ServiceOptions().NotificationFeatures,
    };
    await using var service = new Momentum4Service(transport, policy, serviceOptions, loggerFactory);
    using var presence = new BluetoothPresenceMonitor(loggerFactory.CreateLogger<BluetoothPresenceMonitor>());

    var notifications = new List<object>();
    var started = Stopwatch.StartNew();
    service.Queue.NotificationReceived += (_, frame) =>
    {
        lock (notifications)
        {
            notifications.Add(new { atMs = started.ElapsedMilliseconds, hex = Hex.Format(frame.Raw), id = $"{frame.Vendor:X4}/{frame.Command.Value:X4}" });
        }

        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  NOTIFICATION {frame.Vendor:X4}:{frame.Command.Value:X4} [{Hex.Format(frame.Payload)}]");
    };
    service.Store.StateChanged += (_, e) => PrintChange(e);
    presence.Changed += (_, change) =>
    {
        if (change.HeadsetConnected == true || change.RadioOn == true)
        {
            service.NotifyHeadsetAvailable(change.HeadsetConnected == true ? "Headset verbunden" : "Bluetooth an");
        }
    };

    await presence.StartAsync(address, cancellation.Token);
    service.Start();
    Console.WriteLine($"Monitor läuft ({policy.Name}, Poll {options.PollSeconds} s){(options.Seconds > 0 ? $" für {options.Seconds} s" : " bis Strg+C")} …");

    try
    {
        await Task.Delay(options.Seconds > 0 ? TimeSpan.FromSeconds(options.Seconds) : Timeout.InfiniteTimeSpan, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Beende …");
    }

    await service.StopAsync();

    using var process = Process.GetCurrentProcess();
    process.Refresh();
    Console.WriteLine($"Laufzeit {started.Elapsed:hh\\:mm\\:ss}, {notifications.Count} Notifications, Arbeitssatz {process.WorkingSet64 / 1024 / 1024} MB, privat {process.PrivateMemorySize64 / 1024 / 1024} MB.");

    var path = Path.Combine(logDirectory, $"monitor-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { tool = "m4poc monitor", notificationsRegistered = options.Notifications, notifications }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
    Console.WriteLine($"Notifications gespeichert: {path}");
    return 0;

    static void PrintChange(StateChangedEventArgs e)
    {
        var (p, c) = (e.Previous, e.Current);
        var t = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        if (p.Connection != c.Connection)
        {
            Console.WriteLine($"{t}  Verbindung: {c.Connection}{(c.LastError is { } err && c.Connection != Momentum4.Core.State.ConnectionState.Connected ? $" ({err})" : string.Empty)}");
        }

        if (p.Device != c.Device && c.Device is { } d)
        {
            Console.WriteLine($"{t}  Gerät: {d.ModelId}, Firmware {d.Firmware}");
        }

        if (p.BatteryPercent != c.BatteryPercent)
        {
            Console.WriteLine($"{t}  Akku: {c.BatteryPercent} %  (Auslöser: {e.Cause.GetType().Name})");
        }

        if (p.NoiseControl != c.NoiseControl && c.NoiseControl is { } nc)
        {
            Console.WriteLine($"{t}  Noise Control: {nc.Mode}, Pegel {nc.Level}, Anti-Wind {nc.AntiWind}, Transparent Hearing {(nc.TransparentHearing == true ? "an" : "aus")}  (Auslöser: {e.Cause.GetType().Name})");
        }

        if (p.Equalizer != c.Equalizer && c.Equalizer is { } eq)
        {
            var gains = string.Join(" / ", eq.Bands.Select(b => $"{b.GainDb:+0.0;-0.0;0.0}"));
            var freqs = string.Join("/", eq.Bands.Select(b => b.FrequencyHz?.ToString(CultureInfo.InvariantCulture) ?? "?"));
            Console.WriteLine($"{t}  EQ: {gains} dB @ {freqs} Hz, Bass Boost {(eq.BassBoost == true ? "an" : "aus")}, Mode {eq.SoundMode}  (Auslöser: {e.Cause.GetType().Name})");
        }

        if (p.Multipoint != c.Multipoint && c.Multipoint is { } mp)
        {
            var devices = string.Join(", ", mp.Devices.Select(x => $"{x.Name}{(x.IsThisComputer ? " (dieser PC)" : string.Empty)}{(x.Connected ? " ✓" : string.Empty)}"));
            Console.WriteLine($"{t}  Geräte (max. {mp.MaxConnections}): {devices}  (Auslöser: {e.Cause.GetType().Name})");
        }
    }
}

// Hardware-Stufe 6–8 (protocol.md §12), überarbeitet nach Phase E: Anti-Wind nacheinander mit beiden Formaten von
// 0x1A00 ändern (komplette Tabelle, Einzelpaar) und nach jedem Versuch den GESAMTEN Noise-Control-Zustand
// wiederherstellen und mit dem Ausgangszustand vergleichen – jedes Schreiben von 0x1A00 beendet Transparent Hearing.
// Läuft nur bei ANC an (0x1A00 bei ANC aus ist ungeprüft) und bricht ab, sobald eine Wiederherstellung misslingt.
async Task<int> Stage6Async()
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var policy = CommandPolicy.Only(
        CommandCatalog.GetAncEnabled, CommandCatalog.GetAncModes, CommandCatalog.GetAncLevel, CommandCatalog.GetTransparentHearingStatus,
        CommandCatalog.RegisterNotification, CommandCatalog.SetAncMode, CommandCatalog.SetAncLevel, CommandCatalog.SetTransparentHearingStatus);
    using var queue = new GaiaCommandQueue(transport, policy, loggerFactory.CreateLogger<GaiaCommandQueue>());
    var client = new Momentum4Client(queue);
    var ct = cancellation.Token;

    var started = Stopwatch.StartNew();
    var exchange = new List<object>();
    queue.Traffic += (_, t) =>
    {
        lock (exchange)
        {
            exchange.Add(new { atMs = started.ElapsedMilliseconds, dir = t.Direction == TrafficDirection.Tx ? "TX" : "RX", hex = Hex.Format(t.Raw), command = t.CommandName });
        }
    };
    queue.NotificationReceived += (_, f) => Console.WriteLine($"  NOTIFICATION {f.Vendor:X4}:{f.Command.Value:X4} [{Hex.Format(f.Payload)}]");

    static string Describe(NoiseControlState s, AncModeTable t) =>
        $"{s}, Tabelle [{string.Join(" ", t.Pairs.Select(p => $"{p.Id:X2} {p.Value:X2}"))}]";

    static bool SameState(NoiseControlState a, AncModeTable at, NoiseControlState b, AncModeTable bt) =>
        a == b && at.Pairs.SequenceEqual(bt.Pairs);

    await transport.ConnectAsync(ct);
    await client.RegisterNotificationsAsync(13, ct);
    await Task.Delay(500, ct); // Dump abwarten

    var (start, startTable) = await client.ReadNoiseControlWithTableAsync(ct);
    Console.WriteLine($"Ausgangszustand: {Describe(start, startTable)}");
    if (!start.AncEnabled || start.TransparentHearing is null || start.AntiWind is not (AntiWindMode.Off or AntiWindMode.Maximum or AntiWindMode.Automatic))
    {
        Console.WriteLine("Abbruch: stage6 läuft nur bei eingeschaltetem ANC und vollständig bekanntem Zustand.");
        await transport.DisconnectAsync();
        return 1;
    }

    // Stellt den Zielzustand her, in der Reihenfolge, die die Seiteneffekte verlangen: erst die Tabelle (beendet
    // Transparent Hearing, Pegel → Wert davor), dann Transparent Hearing (setzt den Pegel auf 100) bzw. den Pegel. Den Pegel
    // schreibt es nur bei Transparent Hearing aus – 0x1A02 bei Transparent Hearing an ist ungeprüft.
    async Task<(NoiseControlState State, AncModeTable Table)> RestoreAsync()
    {
        var (now, nowTable) = await client.ReadNoiseControlWithTableAsync(ct);
        if (!nowTable.Pairs.SequenceEqual(startTable.Pairs))
        {
            await client.SetAncModeAsync(startTable, AncCodec.ModeAntiWind, (byte)start.AntiWind, AncModeWriteFormat.FullTable, ct);
            (now, _) = await client.ReadNoiseControlWithTableAsync(ct);
        }

        if (now.TransparentHearing != start.TransparentHearing)
        {
            await client.SetTransparentHearingStatusAsync(start.TransparentHearing == true, ct);
            (now, _) = await client.ReadNoiseControlWithTableAsync(ct);
        }

        if (start.TransparentHearing == false && now.Level != start.Level)
        {
            await client.SetAncLevelAsync(start.Level, ct);
        }

        await Task.Delay(300, ct);
        return await client.ReadNoiseControlWithTableAsync(ct);
    }

    var target = start.AntiWind == AntiWindMode.Automatic ? AntiWindMode.Off : AntiWindMode.Automatic;
    var runs = new List<object>();
    var appliedFormats = new List<AncModeWriteFormat>();
    var allRestored = true;
    foreach (var format in new[] { AncModeWriteFormat.FullTable, AncModeWriteFormat.SinglePair })
    {
        string write;
        try
        {
            await client.SetAncModeAsync(startTable, AncCodec.ModeAntiWind, (byte)target, format, ct);
            write = "OK";
        }
        catch (GaiaErrorException ex)
        {
            write = $"abgelehnt (Reason 0x{ex.Reason:X2})";
        }

        await Task.Delay(500, ct);
        var (changed, changedTable) = await client.ReadNoiseControlWithTableAsync(ct);
        var applied = changed.AntiWind == target;
        if (applied)
        {
            appliedFormats.Add(format);
        }

        Console.WriteLine($"Anti-Wind → {target} mit {format}: {write}; {(applied ? "übernommen" : "NICHT übernommen")}");
        Console.WriteLine($"  danach: {Describe(changed, changedTable)}");

        var (restored, restoredTable) = await RestoreAsync();
        var equal = SameState(restored, restoredTable, start, startTable);
        Console.WriteLine(equal
            ? "  Wiederhergestellt, gesamter Zustand wie am Anfang."
            : $"  ACHTUNG: Zustand weicht vom Anfang ab: {Describe(restored, restoredTable)}");
        runs.Add(new { format = format.ToString(), write, applied, after = Describe(changed, changedTable), restored = Describe(restored, restoredTable), equal });
        if (!equal)
        {
            allRestored = false;
            break;
        }
    }

    Console.WriteLine($"Ergebnis: wirksame Formate = {(appliedFormats.Count == 0 ? "keins" : string.Join(", ", appliedFormats))}; " +
        (allRestored ? "Ausgangszustand vollständig wiederhergestellt." : "Ausgangszustand NICHT vollständig wiederhergestellt!"));

    await Task.Delay(1000, ct);
    await transport.DisconnectAsync();

    var path = Path.Combine(logDirectory, $"stage6-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { tool = "m4poc stage6", start = Describe(start, startTable), target = target.ToString(), runs, allRestored, exchange }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
    Console.WriteLine($"Mitschnitt: {path}");
    return allRestored && appliedFormats.Contains(AncModeWriteFormat.FullTable) ? 0 : 1;
}

// Einzelne Noise-Control- oder EQ-Einstellung über den Service (Write → Verify), z. B. für Hörtests mit dem User.
CommandPolicy SetterPolicy() => CommandPolicy.ForHardwareTest(
    CommandCatalog.SetAncMode, CommandCatalog.SetAncLevel, CommandCatalog.SetAncEnabled, CommandCatalog.SetTransparentHearingStatus,
    CommandCatalog.SetEqBand, CommandCatalog.SetBassBoost, CommandCatalog.SetSoundMode,
    CommandCatalog.SetOnHeadDetection, CommandCatalog.SetSmartPause, CommandCatalog.SetAutoAnswer, CommandCatalog.SetComfortCall,
    CommandCatalog.SetTouchLock, CommandCatalog.GetPhysicalState, CommandCatalog.SetTimer, CommandCatalog.SetAudioPromptMode,
    CommandCatalog.SetBtCompatibilityMode);

async Task<int> SetAsync() => await WithServiceAsync(SetterPolicy(), async service =>
{
    var (what, value) = (options.SetWhat, options.SetValue);
    var equalizer = what is "eq" or "bass" or "soundmode";
    BehaviorSetting? behavior = what switch
    {
        "onhead" => BehaviorSetting.OnHeadDetection,
        "smartpause" => BehaviorSetting.SmartPause,
        "autoanswer" => BehaviorSetting.AutoAnswer,
        "comfortcall" => BehaviorSetting.ComfortCall,
        "touch" => BehaviorSetting.TouchControl,
        _ => null,
    };
    if (what == "comfort")
    {
        var (state, comfort) = await service.SetAncComfortAsync(value switch { "on" => true, "off" => false, _ => throw new ArgumentException($"comfort '{value}'?") }, cancellation.Token);
        Console.WriteLine($"Nachher: {state}, Comfort {comfort}");
        return 0;
    }

    if (what == "hires")
    {
        // hires on = besserer Klang (0x0405 [00]), off = bessere Kompatibilität ([01]) – Deutung noch ungeprüft
        var mode = value switch { "on" => BtCompatibilityMode.BetterAudio, "off" => BtCompatibilityMode.BetterCompatibility, _ => throw new ArgumentException($"hires '{value}'?") };
        Console.WriteLine($"Nachher: {await service.SetBtCompatibilityModeAsync(mode, cancellation.Token)}");
        return 0;
    }

    if (what == "prompts")
    {
        Console.WriteLine($"Vorher:  {service.Store.Current.PromptMode}");
        var mode = value switch { "off" => AudioPromptMode.Off, "tones" => AudioPromptMode.TonesOnly, "voice" => AudioPromptMode.TonesAndVoice, _ => throw new ArgumentException($"prompts '{value}'?") };
        Console.WriteLine($"Nachher: {await service.SetAudioPromptModeAsync(mode, cancellation.Token)}");
        return 0;
    }

    if (what == "autooff")
    {
        Console.WriteLine($"Vorher:  {service.Store.Current.AutoPowerOffSeconds} s");
        var seconds = value is "never" or "0" ? 0 : int.Parse(value, CultureInfo.InvariantCulture) * 60;
        Console.WriteLine($"Nachher: {await service.SetAutoPowerOffAsync(seconds, cancellation.Token)} s");
        return 0;
    }

    if (behavior is { } setting)
    {
        Console.WriteLine($"Vorher:  {service.Store.Current.Behavior}");
        var on = value switch { "on" => true, "off" => false, _ => throw new ArgumentException($"{what} '{value}'?") };
        Console.WriteLine($"Nachher: {await service.SetBehaviorAsync(setting, on, cancellation.Token)}");
        return 0;
    }

    Console.WriteLine($"Vorher:  {(equalizer ? service.Store.Current.Equalizer : service.Store.Current.NoiseControl)}");
    object result = what switch
    {
        "mode" => await service.SetNoiseControlModeAsync(Enum.Parse<NoiseControlMode>(value, ignoreCase: true), cancellation.Token),
        "level" => await service.SetNoiseControlLevelAsync(int.Parse(value, CultureInfo.InvariantCulture), cancellation.Token),
        "antiwind" => await service.SetAntiWindAsync(value switch { "off" => AntiWindMode.Off, "max" => AntiWindMode.Maximum, "auto" => AntiWindMode.Automatic, _ => throw new ArgumentException($"Anti-Wind '{value}'?") }, cancellation.Token),
        "th" => await service.SetTransparentHearingAsync(value switch { "on" => true, "off" => false, _ => throw new ArgumentException($"Transparent Hearing '{value}'?") }, cancellation.Token),
        "anc" => await service.SetAncEnabledAsync(value switch { "on" => true, "off" => false, _ => throw new ArgumentException($"ANC '{value}'?") }, cancellation.Token),
        "eq" when value.Split(':') is [var band, var gain] =>
            await service.SetEqBandAsync(int.Parse(band, CultureInfo.InvariantCulture), decimal.Parse(gain, NumberStyles.Float, CultureInfo.InvariantCulture), cancellation.Token),
        "eq" => await service.SetEqGainsAsync([.. value.Split(',').Select(g => decimal.Parse(g, NumberStyles.Float, CultureInfo.InvariantCulture))], cancellation.Token),
        "bass" => await service.SetBassBoostAsync(value switch { "on" => true, "off" => false, _ => throw new ArgumentException($"Bass Boost '{value}'?") }, cancellation.Token),
        "soundmode" => await service.SetSoundModeAsync(value switch { "off" => SoundMode.Off, "eq" => SoundMode.Equalizer, "podcast" => SoundMode.Podcast, _ => throw new ArgumentException($"Sound-Mode '{value}'?") }, cancellation.Token),
        _ => throw new ArgumentException($"Unbekannte Einstellung '{what}'."),
    };
    Console.WriteLine(equalizer
        ? $"Nachher: {result}"
        : $"Nachher: {result}  (0x1A00-Format: {service.AncWriteFormat?.ToString() ?? "nicht benutzt"})");
    return 0;
});

// Eigene EQ-Presets in presets.json (docs/architecture.md §7): list und delete ohne Headset, save liest die aktuelle
// Kurve vom Headset, apply schreibt ein Preset Band für Band mit Rollback.
async Task<int> PresetAsync()
{
    var store = new EqPresetStore(Path.Combine(Path.GetDirectoryName(logDirectory)!, "presets.json"), loggerFactory.CreateLogger<EqPresetStore>());
    var (sub, name) = (options.SetWhat, options.SetValue);
    static string Curve(IEnumerable<decimal> gains) => string.Join(" ", gains.Select(g => g.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)));

    switch (sub)
    {
        case "list":
            foreach (var p in store.All)
            {
                Console.WriteLine($"  {(p.BuiltIn ? "mitgeliefert" : "eigenes"),-12}  {p.Name,-20} [{Curve(p.GainsDb)}] dB");
            }

            Console.WriteLine($"Datei: {store.FilePath}");
            return 0;
        case "delete":
            Console.WriteLine(store.Delete(name) ? $"Preset „{name}“ gelöscht." : $"Kein eigenes Preset „{name}“.");
            return 0;
        case "apply" when store.Find(name) is null:
            log.LogError("Preset „{Name}“ gibt es nicht (m4poc preset list).", name);
            return 1;
    }

    return await WithServiceAsync(SetterPolicy(), async service =>
    {
        var current = service.Store.Current.Equalizer ?? throw new InvalidOperationException("EQ nicht gelesen.");
        var gains = current.Bands.Select(b => b.GainDb).ToList();
        Console.WriteLine($"Vorher:  {current}  (Preset: {store.FindMatching(gains)?.Name ?? "keins"})");
        if (sub == "save")
        {
            var saved = store.Save(name, gains);
            Console.WriteLine($"Gespeichert: „{saved.Name}“ [{Curve(saved.GainsDb)}] dB in {store.FilePath}");
            return 0;
        }

        var preset = store.Find(name)!;
        var result = await service.SetEqGainsAsync(preset.GainsDb, cancellation.Token);
        Console.WriteLine($"Nachher: {result}  (Preset: {store.FindMatching(result.Bands.Select(b => b.GainDb).ToList())?.Name ?? "keins"})");
        return 0;
    });
}

// Multipoint (Phase G): Liste anzeigen oder ein gekoppeltes Gerät verbinden – Platz UND Name müssen passen. Getrennt wird
// nie (0x1403 ist gesperrt); das macht der User am Gerät selbst.
async Task<int> PeerAsync() => await WithServiceAsync(
    CommandPolicy.ForHardwareTest(CommandCatalog.ConnectPairedDevice, CommandCatalog.GetPairedDeviceStatus),
    async service =>
    {
        static void Print(string label, MultipointState s)
        {
            Console.WriteLine($"{label} (max. {s.MaxConnections} gleichzeitig):");
            foreach (var d in s.Devices)
            {
                Console.WriteLine($"  #{d.Index}  {d.Name,-24}{(d.IsThisComputer ? " (dieser PC)" : string.Empty),-13}{(d.Connected ? "verbunden" : "–")}");
            }
        }

        Print("Gekoppelte Geräte", service.Store.Current.Multipoint ?? throw new InvalidOperationException("Liste nicht gelesen."));
        if (options.SetWhat == "list")
        {
            return 0;
        }

        var started = Stopwatch.StartNew();
        var result = await service.ConnectPeerAsync(int.Parse(options.SetValue, CultureInfo.InvariantCulture), options.PeerName, cancellation.Token);
        Print($"Nachher ({started.ElapsedMilliseconds} ms)", result);
        return 0;
    });

// Verbindet den Service mit der angegebenen Policy, führt body aus und wartet danach 3 s auf Notifications zur Änderung.
async Task<int> WithServiceAsync(CommandPolicy policy, Func<Momentum4Service, Task<int>> body)
{
    var address = await ResolveAddressAsync();
    await using var transport = new RfcommTransport(address, loggerFactory.CreateLogger<RfcommTransport>(), serviceIndex: options.ServiceIndex);
    var serviceOptions = new Momentum4ServiceOptions { PollInterval = TimeSpan.FromMinutes(10) };
    if (options.Features is { } features)
    {
        serviceOptions = serviceOptions with { NotificationFeatures = features };
    }

    await using var service = new Momentum4Service(transport, policy, serviceOptions, loggerFactory);
    service.Queue.NotificationReceived += (_, f) => Console.WriteLine($"  NOTIFICATION {f.Vendor:X4}:{f.Command.Value:X4} [{Hex.Format(f.Payload)}]");

    service.Start();
    var deadline = Stopwatch.StartNew();
    while (service.Store.Current.Connection != Momentum4.Core.State.ConnectionState.Connected && deadline.Elapsed < TimeSpan.FromSeconds(20))
    {
        await Task.Delay(50, cancellation.Token);
    }

    if (service.Store.Current.Connection != Momentum4.Core.State.ConnectionState.Connected)
    {
        log.LogError("Keine Verbindung.");
        return 1;
    }

    var exitCode = await body(service);
    await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token); // Notifications zur Änderung abwarten
    await service.StopAsync();
    return exitCode;
}

async Task<BluetoothAddress> ResolveAddressAsync()
{
    if (options.Address is { } explicitAddress)
    {
        return explicitAddress;
    }

    var candidates = await new HeadsetLocator(loggerFactory.CreateLogger<HeadsetLocator>()).FindAsync(cancellation.Token);
    var usable = candidates.Where(c => c.HasGaiaService).ToList();
    return usable.Count switch
    {
        1 => usable[0].Address,
        0 => throw new HeadsetUnavailableException("Kein gekoppeltes MOMENTUM 4 mit GAIA-Dienst gefunden."),
        _ => throw new InvalidOperationException($"Mehrere Headsets gefunden ({string.Join(", ", usable.Select(c => c.Address))}) – bitte --address angeben."),
    };
}

internal sealed record PocOptions(string Command, BluetoothAddress? Address, int Seconds, int ServiceIndex, bool Verbose, bool Notifications, int PollSeconds, string SetWhat = "", string SetValue = "", string PeerName = "", IReadOnlyList<byte>? Features = null)
{
    public static PocOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("list" or "sdp" or "listen" or "read" or "state" or "monitor" or "stage6" or "set" or "preset" or "peer" or "winbattery" or "features" or "probe20" or "setname" or "gesture" or "autopause" or "reasons"))
        {
            return null;
        }

        var setWhat = string.Empty;
        var setValue = string.Empty;
        var peerName = string.Empty;
        var first = 1;
        if (args[0] == "set")
        {
            if (args.Length < 3 || args[1] is not ("mode" or "level" or "antiwind" or "th" or "anc" or "eq" or "bass" or "soundmode"
                or "onhead" or "smartpause" or "autoanswer" or "comfortcall" or "touch" or "autooff" or "prompts" or "comfort" or "hires"))
            {
                return null;
            }

            (setWhat, setValue, first) = (args[1], args[2].ToLowerInvariant(), 3);
        }
        else if (args[0] == "preset")
        {
            switch (args)
            {
                case [_, "list", ..]:
                    (setWhat, first) = ("list", 2);
                    break;
                case [_, "save" or "apply" or "delete", var name, ..]:
                    (setWhat, setValue, first) = (args[1], name, 3); // Name behält Groß-/Kleinschreibung
                    break;
                default:
                    return null;
            }
        }
        else if (args[0] == "autopause")
        {
            if (args.Length < 2 || args[1] is not ("on" or "off"))
            {
                return null;
            }

            (setWhat, setValue, first) = ("autopause", args[1], 2);
        }
        else if (args[0] == "gesture")
        {
            switch (args)
            {
                case [_, "read", ..]:
                    (setWhat, first) = ("read", 2);
                    break;
                case [_, "set", var pat, var fn, ..]:
                    (setWhat, setValue, peerName, first) = ("set", pat, fn, 4);
                    break;
                default:
                    return null;
            }
        }
        else if (args[0] == "setname")
        {
            if (args.Length < 2)
            {
                return null;
            }

            (setWhat, setValue, first) = ("setname", args[1], 2);
        }
        else if (args[0] == "peer")
        {
            switch (args)
            {
                case [_, "list", ..]:
                    (setWhat, first) = ("list", 2);
                    break;
                case [_, "connect", var index, var name, ..] when int.TryParse(index, out _):
                    (setWhat, setValue, peerName, first) = ("connect", index, name, 4);
                    break;
                default:
                    return null;
            }
        }

        BluetoothAddress? address = null;
        var seconds = args[0] == "monitor" ? 0 : 10;
        var serviceIndex = 0;
        var verbose = false;
        var notifications = false;
        IReadOnlyList<byte>? features = null;
        var pollSeconds = 10;
        for (var i = first; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--address" when i + 1 < args.Length:
                    address = BluetoothAddress.Parse(args[++i]);
                    break;
                case "--seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s) && s is > 0 and <= 86400:
                    seconds = s;
                    i++;
                    break;
                case "--service-index" when i + 1 < args.Length && int.TryParse(args[i + 1], out var index) && index is >= 0 and < 8:
                    serviceIndex = index;
                    i++;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--notifications":
                    notifications = true;
                    break;
                case "--features" when i + 1 < args.Length:
                    // nur bekannte Feature-IDs aus protocol.md §4 (0x0007 [feature] meldet Notifications an)
                    var ids = args[++i].Split(',').Select(f => byte.TryParse(f, out var id) ? id : (byte)0xFF).ToList();
                    if (ids.Any(id => id is not (2 or 3 or 4 or 8 or 10 or 11 or 12 or 13 or 18 or 19 or 20 or 21)))
                    {
                        return null;
                    }

                    features = ids;
                    notifications = true;
                    break;
                case "--poll" when i + 1 < args.Length && int.TryParse(args[i + 1], out var poll) && poll is >= 2 and <= 600:
                    pollSeconds = poll;
                    i++;
                    break;
                default:
                    return null;
            }
        }

        return new PocOptions(args[0], address, seconds, serviceIndex, verbose, notifications, pollSeconds, setWhat, setValue, peerName, features);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            m4poc – Proof of Concept für den MOMENTUM 4 (Hardware-Stufen aus docs/protocol.md §12)

              m4poc list                              gekoppelte MOMENTUM-4-Kandidaten anzeigen
              m4poc sdp    [--address XX:XX:..]       GAIA-Dienst und SDP-Attribute anzeigen
              m4poc winbattery                        Akku laut Windows (HFP-Knoten), ohne GAIA
              m4poc features                          nur lesend: unterstützte Features (Qualcomm, Sennheiser),
                                                      Tastenbelegung (0x1605, 0x1601 je Taste und Muster)
              m4poc listen [--address XX:XX:..] [--seconds 10] [--service-index 0]
                                                      RFCOMM öffnen, passiv lauschen, schließen (sendet nichts)
              m4poc read   [--address XX:XX:..] [--service-index 0]
                                                      nur lesend: Modell, Firmware, API-Version, Akku;
                                                      Mitschnitt als JSON unter %LOCALAPPDATA%\Momentum4Control\logs
              m4poc state  [--address XX:XX:..] [--service-index 0]
                                                      nur lesend: ANC, Transparent Hearing, Sound-Mode, EQ, Bass Boost,
                                                      Multipoint, Geräteeinstellungen (Hardware-Stufe 4)
              m4poc monitor [--seconds N] [--poll 10] [--notifications]
                                                      kompletter Service: Wiederverbinden, Polling, Zustandsänderungen live;
                                                      --notifications meldet Notifications an (Hardware-Stufe 5)
                                                      --features 3,13,8,10,2,12  diese Features anmelden (bekannte IDs)
              m4poc stage6                            Hardware-Stufe 6–8: Anti-Wind mit beiden 0x1A00-Formaten ändern, nach
                                                      jedem Versuch den gesamten Zustand wiederherstellen und vergleichen –
                                                      ÄNDERT kurz Einstellungen (nur bei ANC an)
              m4poc set mode transparency|adaptive|custom|off
                                                      Noise-Control-Modus setzen (Write → Verify)
              m4poc set level 0..100                  Pegel setzen (schaltet auf Custom)
              m4poc set antiwind off|max|auto         Anti-Wind setzen
              m4poc set th on|off                     Transparent Hearing (0x1804) setzen
              m4poc set anc on|off                    nur ANC ein/aus (0x1A04), ohne Transparent Hearing anzufassen
              m4poc set eq BAND:DB                    ein EQ-Band setzen, z. B. 4:1.5 (0x1001, Write → Verify)
              m4poc set eq G0,G1,G2,G3,G4             ganze EQ-Kurve in dB, Band für Band mit Rollback
              m4poc set bass on|off                   Bass Boost (0x1008)
              m4poc set soundmode off|eq|podcast      Sound-Mode (0x0803)
              m4poc set onhead|smartpause|autoanswer|comfortcall on|off
                                                      On-Head-Erkennung (0x0400), Smart Pause (0x080C), Anrufe automatisch
                                                      annehmen (0x080A), Comfort Call (0x0814); Write → Verify
              m4poc set touch on|off                  Touch-Steuerung (0x1606, invertiert: off = Sperre an)
              m4poc gesture read | gesture set MUSTER FUNKTION   Touch-Belegung lesen/setzen (Feature 11) – Hardware-Test
              m4poc setname NAME                       Gerätenamen setzen (0x2801) und zurücklesen – kann Neustart auslösen
              m4poc set autooff 0|15|30|60            Auto Power Off in Minuten (0x0600 [00, s u16], 0 = nie)
              m4poc set prompts off|tones|voice       Töne & Sprachansagen (0x0801: aus, nur Töne, Töne und Stimme)
              m4poc set comfort on|off                ANC-Modus Comfort (0x1A00 Modus 2) – Hardware-Test
              m4poc set hires on|off                  BT-Kompatibilitätsmodus 0x0405 (on = [00] besserer Klang) – Hardware-Test
              m4poc preset list                       mitgelieferte und eigene EQ-Presets (presets.json, ohne Headset)
              m4poc preset save NAME                  aktuelle EQ-Kurve des Headsets als eigenes Preset speichern
              m4poc preset apply NAME                 Preset aufs Headset schreiben (Band für Band, Rollback bei Fehler)
              m4poc preset delete NAME                eigenes Preset löschen (ohne Headset)
              m4poc peer list                         gekoppelte Geräte mit Status
              m4poc peer connect PLATZ NAME           gekoppeltes Gerät verbinden (0x1402); Platz und Name müssen passen,
                                                      getrennt wird nie (0x1403 gesperrt)
              --service-index N                       GAIA-Eintrag in Windows-Reihenfolge (0 = Kanal 1, 1 = Kanal 2)
              --verbose                               Debug-Ausgaben auch auf der Konsole
            """);
    }
}
