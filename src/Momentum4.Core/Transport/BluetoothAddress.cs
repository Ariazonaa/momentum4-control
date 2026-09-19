// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Momentum4.Core.Transport;

/// <summary>48-Bit-Bluetooth-Adresse, dargestellt als <c>80:C3:BA:12:34:56</c>.</summary>
public readonly record struct BluetoothAddress(ulong Value)
{
    public override string ToString() => Format(Value, anonymize: false);

    /// <summary>Nur der Herstellerteil (OUI) bleibt sichtbar: <c>80:C3:BA:xx:xx:xx</c>.</summary>
    public string ToAnonymizedString() => Format(Value, anonymize: true);

    public static BluetoothAddress Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var hex = text.Replace(":", string.Empty, StringComparison.Ordinal)
                      .Replace("-", string.Empty, StringComparison.Ordinal);
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"Keine gültige Bluetooth-Adresse: '{text}'.");
        }

        return new BluetoothAddress(value);
    }

    private static string Format(ulong value, bool anonymize)
    {
        var parts = new string[6];
        for (var i = 0; i < 6; i++)
        {
            var b = (byte)(value >> (8 * (5 - i)));
            parts[i] = anonymize && i >= 3 ? "xx" : b.ToString("X2", CultureInfo.InvariantCulture);
        }

        return string.Join(':', parts);
    }
}
