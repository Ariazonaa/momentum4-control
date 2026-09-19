// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Queue;

/// <summary>Das Headset hat den Command mit einem Error-Frame (<c>cmd + 0x0180</c>) abgelehnt.</summary>
public sealed class GaiaErrorException : Exception
{
    public GaiaErrorException(CommandDescriptor command, GaiaFrame frame)
        : base(frame.Payload.Length > 0
            ? $"{command} abgelehnt, Reason 0x{frame.Payload[0]:X2}"
            : $"{command} abgelehnt (ohne Reason)")
    {
        Command = command;
        Frame = frame;
    }

    public CommandDescriptor Command { get; }

    public GaiaFrame Frame { get; }

    /// <summary>Erstes Payload-Byte des Error-Frames (docs/protocol.md §3.3), falls vorhanden.</summary>
    public byte? Reason => Frame.Payload.Length > 0 ? Frame.Payload[0] : null;
}

/// <summary>Der Command darf nach Katalog/Policy nicht gesendet werden. Es wurde nichts gesendet.</summary>
public sealed class CommandNotAllowedException(CommandDescriptor command, string reason)
    : Exception($"{command} darf nicht gesendet werden: {reason}")
{
    public CommandDescriptor Command { get; } = command;
}
