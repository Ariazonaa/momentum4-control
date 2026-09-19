// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;

namespace Momentum4.Core.Diagnostics;

/// <summary>
/// Anonymisiert Texte für den Diagnose-Export (Spezifikation §21, docs/architecture.md §11): Bluetooth-Adressen behalten
/// nur den Herstellerteil, Gerätenamen und andere persönliche Wörter werden ersetzt – auch dort, wo sie nur als
/// Hexdump stehen (Geräteeinträge <c>0x1501</c>).
/// </summary>
public static partial class DiagnosticsAnonymizer
{
    /// <summary>
    /// <paramref name="sensitiveWords"/>: Gerätenamen, Rechnername, Benutzername … – werden durch „Gerät 1“ usw. ersetzt
    /// (Groß-/Kleinschreibung egal, längere Wörter zuerst).
    /// </summary>
    public static string Anonymize(string text, IEnumerable<string> sensitiveWords)
    {
        text = MacAddress().Replace(text, m =>
        {
            var separator = m.Groups["sep"].Value;
            return string.Join(separator, m.Groups["oui"].Value.Split(separator)) + $"{separator}xx{separator}xx{separator}xx";
        });

        // Geräteeintrag im Protokoll-Log: RX 0495:1501 GetPairedDeviceInfo [idx prio status NAME… 00] → Name entfernt
        text = DeviceEntryLogged().Replace(text, m => $"{m.Groups["head"].Value}{m.Groups["keep"].Value}(Name entfernt)]");

        // Geräteeintrag als Rohframe: … 04 95 15 01 idx prio status NAME… → Name entfernt
        text = DeviceEntryFrame().Replace(text, m => $"{m.Groups["keep"].Value}(Name entfernt)");

        var index = 0;
        foreach (var word in sensitiveWords.Where(w => w.Trim().Length >= 2).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(w => w.Length))
        {
            index++;
            text = text.Replace(word, $"<Gerät {index}>", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    // Keine \b-Grenzen: in „Bluetooth48:45:…-80:c3:…“ steht die Adresse direkt hinter Buchstaben bzw. einem Bindestrich.
    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?<oui>[0-9A-Fa-f]{2}(?<sep>[:-])[0-9A-Fa-f]{2}\k<sep>[0-9A-Fa-f]{2})\k<sep>[0-9A-Fa-f]{2}\k<sep>[0-9A-Fa-f]{2}\k<sep>[0-9A-Fa-f]{2}(?![0-9A-Fa-f])")]
    private static partial Regex MacAddress();

    [GeneratedRegex(@"(?<head>0495:1501\b[^\[\r\n]*\[)(?<keep>(?:[0-9A-Fa-f]{2} ){3})[0-9A-Fa-f ]*\]")]
    private static partial Regex DeviceEntryLogged();

    [GeneratedRegex(@"(?<keep>04 95 15 01 (?:[0-9A-Fa-f]{2} ){3})(?:[0-9A-Fa-f]{2}(?: |$))+", RegexOptions.Multiline)]
    private static partial Regex DeviceEntryFrame();
}
