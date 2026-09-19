// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using Momentum4.Protocol.Catalog;

namespace Momentum4.Core.Queue;

/// <summary>
/// Entscheidet, welche Katalog-Einträge gesendet werden dürfen (docs/architecture.md §10):
/// <list type="bullet">
/// <item><c>Blocked</c> und <c>Unknown</c> (Sicherheitsklasse) – nie.</item>
/// <item><c>Unverified</c>/<c>Unknown</c> (Status) – nie.</item>
/// <item><c>Verified</c> – immer.</item>
/// <item><c>NeedsHardwareTest</c> – nur wenn für einen Hardware-Test ausdrücklich freigegeben.</item>
/// </list>
/// </summary>
public sealed class CommandPolicy
{
    private readonly FrozenSet<CommandDescriptor> _hardwareTestAllowed;
    private readonly FrozenSet<CommandDescriptor>? _only;

    private CommandPolicy(IEnumerable<CommandDescriptor> hardwareTestAllowed, string name, IEnumerable<CommandDescriptor>? only = null)
    {
        _hardwareTestAllowed = hardwareTestAllowed.ToFrozenSet();
        _only = only?.ToFrozenSet();
        Name = name;
    }

    /// <summary>Normalbetrieb: nur auf Hardware verifizierte Commands.</summary>
    public static CommandPolicy Production { get; } = new([], "Production");

    public string Name { get; }

    /// <summary>
    /// Nur genau die genannten Commands (jeweils <c>Verified</c> oder <c>NeedsHardwareTest</c>) – alles andere nicht,
    /// auch keine verifizierten. Für Tests und eingeschränkte Diagnose-Werkzeuge.
    /// </summary>
    public static CommandPolicy Only(params CommandDescriptor[] allowed)
    {
        Validate(allowed);
        return new CommandPolicy(allowed, "Only[" + string.Join(", ", allowed.Select(d => d.Name)) + "]", allowed);
    }

    /// <summary>
    /// Hardware-Testmodus: zusätzlich genau die genannten <c>NeedsHardwareTest</c>-Einträge. Andere Einträge lassen
    /// sich so nicht freischalten.
    /// </summary>
    public static CommandPolicy ForHardwareTest(params CommandDescriptor[] allowed)
    {
        Validate(allowed);
        return new CommandPolicy(allowed, "HardwareTest[" + string.Join(", ", allowed.Select(d => d.Name)) + "]");
    }

    private static void Validate(CommandDescriptor[] allowed)
    {
        foreach (var descriptor in allowed)
        {
            if (descriptor.Safety is SafetyClass.Blocked or SafetyClass.Unknown
                || descriptor.Status is not (ProtocolStatus.Verified or ProtocolStatus.NeedsHardwareTest))
            {
                throw new ArgumentException($"{descriptor} ({descriptor.Safety}, {descriptor.Status}) kann nicht freigegeben werden.", nameof(allowed));
            }
        }
    }

    public bool IsAllowed(CommandDescriptor descriptor, out string reason)
    {
        if (!ReferenceEquals(CommandCatalog.Find(descriptor.Vendor, descriptor.Command), descriptor))
        {
            reason = "nicht aus dem CommandCatalog";
            return false;
        }

        switch (descriptor.Safety)
        {
            case SafetyClass.Blocked:
                reason = "gesperrt (docs/protocol.md §9)";
                return false;
            case SafetyClass.Unknown:
                reason = "Wirkung unbekannt";
                return false;
        }

        if (_only is not null)
        {
            var allowed = _only.Contains(descriptor);
            reason = allowed ? string.Empty : $"nicht in Policy '{Name}'";
            return allowed;
        }

        switch (descriptor.Status)
        {
            case ProtocolStatus.Verified:
                reason = string.Empty;
                return true;
            case ProtocolStatus.NeedsHardwareTest when _hardwareTestAllowed.Contains(descriptor):
                reason = string.Empty;
                return true;
            case ProtocolStatus.NeedsHardwareTest:
                reason = $"noch nicht auf Hardware verifiziert und in Policy '{Name}' nicht freigegeben";
                return false;
            default:
                reason = $"Status {descriptor.Status}";
                return false;
        }
    }
}
