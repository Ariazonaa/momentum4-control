// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Text;

namespace Momentum4.Protocol;

/// <summary>Hex-Darstellung für Logs und Mitschnitte, z. B. <c>FF 03 00 00 04 95 06 03</c>.</summary>
public static class Hex
{
    public static string Format(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(data.Length * 3 - 1);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(data[i].ToString("X2"));
        }

        return sb.ToString();
    }

    /// <summary>Liest eine Hex-Zeichenkette wie <c>"FF 03 00 00"</c> oder <c>"FF030000"</c>.</summary>
    public static byte[] Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var digits = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c))
            {
                digits.Append(c);
            }
            else if (!char.IsWhiteSpace(c) && c != '-' && c != ':')
            {
                throw new FormatException($"Ungültiges Zeichen '{c}' in Hex-Zeichenkette.");
            }
        }

        return Convert.FromHexString(digits.ToString());
    }
}
