// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;
using Momentum4.Core.Transport;

namespace Momentum4.Bluetooth;

/// <summary>
/// Lesbare Beschreibung für Fehler aus den WinRT-Bluetooth-/Socket-APIs, die oft nur einen HRESULT ohne Text
/// liefern. Beobachtete Fälle stehen in docs/bluetooth-debugging.md.
/// </summary>
public static class BluetoothErrors
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is HeadsetUnavailableException)
        {
            return exception.Message; // schon beschrieben (siehe RfcommTransport.ConnectAsync)
        }

        var hresult = unchecked((uint)exception.HResult);
        var known = hresult switch
        {
            0x80070490 => "Element nicht gefunden – Dienst/Kanal nicht verfügbar oder bereits belegt",
            0x80070005 => "Zugriff verweigert",
            0x8007048F => "Gerät nicht verbunden",
            0x8007274C => "Zeitüberschreitung – Headset aus oder außer Reichweite?",
            0x8007274D => "Verbindung abgelehnt",
            0x80072745 => "Verbindung abgebrochen",
            0x80072746 => "Verbindung von der Gegenstelle zurückgesetzt",
            0x8007277C => "Dienst nicht gefunden – Headset aus oder außer Reichweite?", // WSASERVICE_NOT_FOUND, beim Aus- und Einschalten beobachtet
            0x800710DF => "Gerät nicht bereit – Bluetooth am PC aus?", // ERROR_DEVICE_NOT_AVAILABLE, bei ausgeschaltetem Funkmodul beobachtet
            _ => null,
        };

        var message = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message.Trim();
        var code = "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);
        return known is null ? $"{message} ({code})" : $"{known} ({code})";
    }
}
