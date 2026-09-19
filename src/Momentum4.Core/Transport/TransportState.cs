// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.Transport;

public enum TransportState
{
    /// <summary>Nicht verbunden (Ausgangszustand oder nach bewusstem Trennen).</summary>
    Disconnected,

    /// <summary>Verbindungsaufbau läuft.</summary>
    Connecting,

    /// <summary>Kanal offen, Bytes können gelesen und geschrieben werden.</summary>
    Connected,

    /// <summary>Verbindung ist ungeplant abgerissen (Headset aus, außer Reichweite, Lesefehler).</summary>
    Lost,
}
