// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Momentum4.Core.Control;

/// <summary>
/// Steuerkanal zwischen <c>m4ctl</c> und der laufenden App (docs/architecture.md §6.6). Nötig, weil zwei Prozesse auf
/// demselben RFCOMM-Kanal sich die eingehenden Daten teilen (docs/bluetooth-debugging.md §2) – solange die App läuft,
/// darf nur sie mit dem Headset sprechen. Named Pipe pro Windows-Benutzer, <see cref="PipeOptions.CurrentUserOnly"/>:
/// Andere Benutzer können sich nicht verbinden.
/// Anfrage: Anzahl der Argumente, dann je ein Argument pro Zeile. Antwort: Exit-Code, dann der Text bis zum Ende.
/// </summary>
public static class ControlPipe
{
    public static string DefaultName => $"Momentum4Control.{Environment.UserName}";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Führt die Argumente in der laufenden App aus; <c>null</c>, wenn keine App auf der Pipe wartet.</summary>
    public static async Task<ControlResult?> TryExecuteInAppAsync(IReadOnlyList<string> args, TimeSpan connectTimeout, CancellationToken cancellationToken, string? name = null)
    {
        await using var pipe = new NamedPipeClientStream(".", name ?? DefaultName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(connectTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }

        var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { NewLine = "\n" };
        await writer.WriteLineAsync(args.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var arg in args)
        {
            await writer.WriteLineAsync(arg.ReplaceLineEndings(" "));
        }

        await writer.FlushAsync(cancellationToken);
        using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
        var code = int.TryParse(await reader.ReadLineAsync(cancellationToken), out var parsed) ? parsed : 1;
        return new ControlResult(code, (await reader.ReadToEndAsync(cancellationToken)).TrimEnd());
    }

    /// <summary>Nimmt Befehle an, bis <paramref name="stopping"/> ausgelöst wird; jeweils eine Verbindung nach der anderen.</summary>
    public static async Task ServeAsync(Func<IReadOnlyList<string>, CancellationToken, Task<ControlResult>> execute, ILogger logger, CancellationToken stopping, string? name = null)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name ?? DefaultName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopping);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                using var reader = new StreamReader(pipe, Utf8, leaveOpen: true);
                var args = new List<string>();
                if (int.TryParse(await reader.ReadLineAsync(timeout.Token), out var count) && count is >= 0 and <= 32)
                {
                    for (var i = 0; i < count && await reader.ReadLineAsync(timeout.Token) is { } arg; i++)
                    {
                        args.Add(arg);
                    }
                }

                logger.LogInformation("m4ctl: {Command}", string.Join(' ', args));
                var result = await execute(args, timeout.Token);
                var writer = new StreamWriter(pipe, Utf8, leaveOpen: true) { NewLine = "\n" };
                await writer.WriteLineAsync(result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await writer.WriteAsync(result.Output);
                await writer.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                logger.LogWarning("m4ctl-Verbindung abgebrochen: {Reason}", ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(200), stopping).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
}
