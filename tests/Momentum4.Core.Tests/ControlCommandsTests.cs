// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Momentum4.Core.Control;
using Momentum4.Core.Presets;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.State;
using Momentum4.Core.Transport;

namespace Momentum4.Core.Tests;

/// <summary>m4ctl-Befehle gegen das simulierte Headset und der Weg über die Named Pipe.</summary>
public sealed class ControlCommandsTests : IDisposable
{
    private const int TestTimeoutMs = 15_000;

    private readonly SimulatedHeadset _headset = new();
    private readonly FakeTransport _transport;
    private readonly FakeTimeProvider _time = new();
    private readonly string _presetDirectory = Path.Combine(Path.GetTempPath(), "m4ctl-tests-" + Guid.NewGuid().ToString("N"));

    public ControlCommandsTests()
    {
        _transport = new FakeTransport { Responder = _headset.Respond };
        _transport.SetState(TransportState.Disconnected);
    }

    public void Dispose()
    {
        if (Directory.Exists(_presetDirectory))
        {
            Directory.Delete(_presetDirectory, recursive: true);
        }
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Status_lists_the_confirmed_state()
    {
        await using var service = await StartAsync(TestContext.Current.CancellationToken);

        var result = await ControlCommands.ExecuteAsync(["status"], service, Presets(), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Noise Control: Transparenz", result.Output, StringComparison.Ordinal);
        Assert.Contains("Tragezustand:  aufgesetzt", result.Output, StringComparison.Ordinal);
        Assert.Contains("Touch an", result.Output, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Mode_toggle_switches_between_transparency_and_anc()
    {
        await using var service = await StartAsync(TestContext.Current.CancellationToken);

        var first = await ControlCommands.ExecuteAsync(["mode", "toggle"], service, null, TestContext.Current.CancellationToken);
        Assert.Equal(NoiseControlMode.Custom, service.Store.Current.NoiseControl!.Mode);
        var second = await ControlCommands.ExecuteAsync(["mode", "toggle"], service, null, TestContext.Current.CancellationToken);

        Assert.Equal((0, 0), (first.ExitCode, second.ExitCode));
        Assert.Equal(NoiseControlMode.Transparency, service.Store.Current.NoiseControl!.Mode);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Strength_is_the_inverted_level()
    {
        await using var service = await StartAsync(TestContext.Current.CancellationToken);

        var result = await ControlCommands.ExecuteAsync(["strength", "70"], service, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(30, _headset.AncLevel);
        Assert.Contains("ANC-Stärke 70", result.Output, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Autooff_takes_minutes_and_status_shows_it()
    {
        await using var service = await StartAsync(TestContext.Current.CancellationToken);

        var set = await ControlCommands.ExecuteAsync(["autooff", "30"], service, null, TestContext.Current.CancellationToken);
        var status = await ControlCommands.ExecuteAsync(["status"], service, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, set.ExitCode);
        Assert.Equal(1800, _headset.AutoPowerOffSeconds);
        Assert.Contains("Auto-Aus:      nach 30 min", status.Output, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Prompts_switch_and_show_up_in_status()
    {
        await using var service = await StartAsync(TestContext.Current.CancellationToken);

        var set = await ControlCommands.ExecuteAsync(["prompts", "tones"], service, null, TestContext.Current.CancellationToken);
        var status = await ControlCommands.ExecuteAsync(["status"], service, null, TestContext.Current.CancellationToken);

        Assert.Equal((0, "Töne & Sprachansagen: nur Töne"), (set.ExitCode, set.Output));
        Assert.Equal(1, _headset.PromptMode);
        Assert.Contains("Ansagen:       nur Töne", status.Output, StringComparison.Ordinal);
    }

    [Theory(Timeout = TestTimeoutMs)]
    [InlineData("prompts", "laut")]
    [InlineData("autooff", "45")]
    [InlineData("mode", "quatsch")]
    [InlineData("strength", "101")]
    [InlineData("bass", "vielleicht")]
    [InlineData("unbekannt")]
    public async Task Wrong_arguments_send_nothing_and_exit_with_2(params string[] args)
    {
        await using var service = await StartAsync(TestContext.Current.CancellationToken);
        var before = _headset.Requests.Count;

        var result = await ControlCommands.ExecuteAsync(args, service, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(before, _headset.Requests.Count);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Preset_by_name_is_applied_and_unknown_names_are_listed()
    {
        await using var service = await StartAsync(TestContext.Current.CancellationToken);
        var presets = Presets();

        var unknown = await ControlCommands.ExecuteAsync(["preset", "Gibtsnicht"], service, presets, TestContext.Current.CancellationToken);
        var known = await ControlCommands.ExecuteAsync(["preset", presets.All[0].Name], service, presets, TestContext.Current.CancellationToken);

        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains(presets.All[0].Name, unknown.Output, StringComparison.Ordinal);
        Assert.Equal(0, known.ExitCode);
        Assert.Equal(presets.All[0].GainsDb, service.Store.Current.Equalizer!.Bands.Select(b => b.GainDb));
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Actions_need_a_connection_but_status_does_not()
    {
        await using var service = new Momentum4Service(_transport, CommandPolicy.Production, new Momentum4ServiceOptions(), NullLoggerFactory.Instance, _time);

        var status = await ControlCommands.ExecuteAsync(["status"], service, null, TestContext.Current.CancellationToken);
        var action = await ControlCommands.ExecuteAsync(["bass", "off"], service, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, status.ExitCode);
        Assert.Contains("getrennt", status.Output, StringComparison.Ordinal);
        Assert.Equal(1, action.ExitCode);
        Assert.Empty(_headset.Requests);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Pipe_round_trip_carries_arguments_exit_code_and_output()
    {
        var name = "Momentum4Control.Test." + Guid.NewGuid().ToString("N");
        using var stopping = new CancellationTokenSource();
        IReadOnlyList<string>? received = null;
        var server = ControlPipe.ServeAsync(
            (args, _) =>
            {
                received = args;
                return Task.FromResult(new ControlResult(1, "Zeile 1\nZeile 2 – ä"));
            },
            NullLogger.Instance,
            stopping.Token,
            name);

        var result = await ControlPipe.TryExecuteInAppAsync(["preset", "Mein Preset"], TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken, name);
        await stopping.CancelAsync();
        await server;

        Assert.Equal(["preset", "Mein Preset"], received);
        Assert.Equal(new ControlResult(1, "Zeile 1\nZeile 2 – ä"), result);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Without_a_server_the_client_reports_no_app()
    {
        var result = await ControlPipe.TryExecuteInAppAsync(["status"], TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken, "Momentum4Control.Test." + Guid.NewGuid().ToString("N"));

        Assert.Null(result);
    }

    private EqPresetStore Presets() => new(Path.Combine(_presetDirectory, "presets.json"), NullLogger.Instance);

    private async Task<Momentum4Service> StartAsync(CancellationToken cancellationToken)
    {
        var service = new Momentum4Service(_transport, CommandPolicy.Production, new Momentum4ServiceOptions { RegisterNotifications = false }, NullLoggerFactory.Instance, _time);
        service.Start();
        for (var i = 0; i < 1000 && service.Store.Current.Connection != ConnectionState.Connected; i++)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.Equal(ConnectionState.Connected, service.Store.Current.Connection);
        return service;
    }
}
