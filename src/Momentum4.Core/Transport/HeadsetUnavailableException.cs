// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core.Transport;

/// <summary>Das Headset oder sein GAIA-Dienst ist nicht erreichbar (aus, außer Reichweite, nicht gekoppelt).</summary>
public sealed class HeadsetUnavailableException : Exception
{
    public HeadsetUnavailableException(string message)
        : base(message)
    {
    }

    public HeadsetUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
