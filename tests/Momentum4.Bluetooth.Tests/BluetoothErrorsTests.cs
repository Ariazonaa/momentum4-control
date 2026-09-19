// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Momentum4.Bluetooth.Tests;

public sealed class BluetoothErrorsTests
{
    [Fact]
    public void Describes_known_hresult_even_without_message()
    {
        // Beobachtet am 2026-09-18: zweiter GAIA-Kanal, während der erste offen ist.
        var ex = new COMException(string.Empty, unchecked((int)0x80070490));

        Assert.Equal("Element nicht gefunden – Dienst/Kanal nicht verfügbar oder bereits belegt (0x80070490)", BluetoothErrors.Describe(ex));
    }

    [Fact]
    public void Falls_back_to_message_and_code_for_unknown_errors()
    {
        var ex = new InvalidOperationException("kaputt");

        Assert.Equal("kaputt (0x80131509)", BluetoothErrors.Describe(ex));
    }

    [Fact]
    public void Describes_service_not_found_seen_while_the_headset_restarts()
    {
        var ex = new System.Runtime.InteropServices.COMException(string.Empty, unchecked((int)0x8007277C));

        Assert.Equal("Dienst nicht gefunden – Headset aus oder außer Reichweite? (0x8007277C)", BluetoothErrors.Describe(ex));
    }

    [Fact]
    public void Already_described_unavailability_is_passed_through()
    {
        var ex = new Momentum4.Core.Transport.HeadsetUnavailableException("Dienst nicht gefunden (0x8007277C)", new InvalidOperationException());

        Assert.Equal("Dienst nicht gefunden (0x8007277C)", BluetoothErrors.Describe(ex));
    }

    [Fact]
    public void Describes_device_not_ready_seen_while_bluetooth_is_off()
    {
        var ex = new System.Runtime.InteropServices.COMException(string.Empty, unchecked((int)0x800710DF));

        Assert.Equal("Gerät nicht bereit – Bluetooth am PC aus? (0x800710DF)", BluetoothErrors.Describe(ex));
    }
}
