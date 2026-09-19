// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Catalog;

/// <summary>Was ein Command am Gerät bewirkt (docs/architecture.md §4, §10).</summary>
public enum SafetyClass
{
    /// <summary>Reiner Getter.</summary>
    Read,

    /// <summary>Idempotente Einstellung.</summary>
    Setting,

    /// <summary>Zustandswechsel wie Verbinden eines Geräts – wird nie automatisch wiederholt.</summary>
    Action,

    /// <summary>Wirkung unbekannt – darf nicht gesendet werden.</summary>
    Unknown,

    /// <summary>Gefährlich oder widersprüchlich (docs/protocol.md §9) – wird nie gesendet.</summary>
    Blocked,
}

/// <summary>Teststatus wie in docs/protocol.md §0.</summary>
public enum ProtocolStatus
{
    Verified,
    NeedsHardwareTest,
    Unverified,
    Unknown,
}

/// <summary>Ein Eintrag der Allowlist. Nur was hier steht, kann überhaupt gesendet werden.</summary>
/// <param name="Name">Technischer Name für Logs, z. B. <c>GetBatteryLevel</c>.</param>
/// <param name="Vendor">Vendor-ID – gehört immer zum Schlüssel.</param>
/// <param name="Command">Command-Word vom Typ <see cref="PacketType.Command"/>.</param>
/// <param name="Safety">Sicherheitsklasse.</param>
/// <param name="Status">Teststatus laut docs/protocol.md.</param>
/// <param name="RequestLength">Erwartete Request-Payload-Länge; <c>null</c> = variabel/ungeklärt.</param>
/// <param name="Timeout">Eigenes Timeout; <c>null</c> = Standard der Queue.</param>
public sealed record CommandDescriptor(
    string Name,
    ushort Vendor,
    CommandWord Command,
    SafetyClass Safety,
    ProtocolStatus Status,
    int? RequestLength = null,
    TimeSpan? Timeout = null)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name} ({Vendor:X4}/{Command.Value:X4})");
}
