// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Win32;

namespace Momentum4.App;

/// <summary>
/// Autostart über <c>HKCU\…\Run</c> (docs/architecture.md §7, Spezifikation §18): startet minimiert ins Tray,
/// standardmäßig aus. Hat der User den Eintrag im Task-Manager deaktiviert, steht das in <c>…\StartupApproved\Run</c>.
/// </summary>
internal static class Autostart
{
    public const string Argument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Momentum4Control";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>
    /// <c>false</c>, wenn der Eintrag im Task-Manager deaktiviert wurde. Erstes Byte gerade (02, 06) = aktiv,
    /// ungerade (03, 07) = deaktiviert; fehlt der Wert, gilt der Eintrag als aktiv.
    /// </summary>
    public static bool IsApprovedByUser
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(ValueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
    }

    /// <summary>Zeigt der Eintrag auf eine andere Exe (z. B. nach dem Verschieben der App)?</summary>
    public static bool PointsElsewhere
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && !command.Contains(Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {Argument}");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
