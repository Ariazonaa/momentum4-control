// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.Queue;

public enum TrafficDirection
{
    Tx,
    Rx,
}

/// <summary>Ein gesendeter oder empfangener Frame – für Protocol Logging, Inspector und Mitschnitte.</summary>
/// <param name="Timestamp">Zeitpunkt (Uhr der Queue).</param>
/// <param name="Direction">Richtung.</param>
/// <param name="Raw">Alle Bytes des Frames.</param>
/// <param name="CommandName">Name laut Katalog, falls bekannt.</param>
/// <param name="Duration">Bei Antworten: Zeit seit dem zugehörigen Request.</param>
public sealed record GaiaTraffic(DateTimeOffset Timestamp, TrafficDirection Direction, byte[] Raw, string? CommandName, TimeSpan? Duration);
