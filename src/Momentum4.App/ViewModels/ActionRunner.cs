// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.App.Localization;
using Momentum4.Core.Queue;
using Momentum4.Core.Services;
using Momentum4.Core.Transport;

namespace Momentum4.App.ViewModels;

/// <summary>Führt eine Aktion über den Service aus und macht Fehler für die Oberfläche lesbar.</summary>
public interface IActionRunner
{
    /// <summary>Liefert <c>true</c>, wenn die Aktion bestätigt wurde. Fehler landen als Meldung in der Oberfläche.</summary>
    Task<bool> RunAsync(string what, Func<Momentum4Service, CancellationToken, Task> action);
}

internal static class ActionErrors
{
    public static string Describe(Exception exception) => exception switch
    {
        SettingNotAppliedException => L.Get("ErrNotApplied"),
        HeadsetUnavailableException => L.Get("ErrNotConnected"),
        CommandNotAllowedException ex => L.Get("ErrNotAllowed", ex.Message),
        GaiaErrorException ex => L.Get("ErrRejected", ex.Reason is { } reason ? L.Get("ErrCode", reason) : string.Empty),
        TimeoutException => L.Get("ErrNoResponse"),
        PeerActionRefusedException or FeatureNotSupportedException or ArgumentException => exception.Message,
        _ => exception.Message,
    };
}
