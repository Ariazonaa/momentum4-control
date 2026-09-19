// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

namespace Momentum4.Core;

/// <summary>
/// Legt fest, wo Einstellungen, Logs und Presets liegen. Normal ist das <c>%LOCALAPPDATA%\Momentum4Control</c>.
/// Liegt neben der ausführbaren Datei die Marker-Datei <see cref="PortableMarker"/> (portable Variante), wandern die
/// Daten stattdessen in einen Ordner <c>Data</c> neben die Exe – so bleibt die portable Kopie in sich geschlossen und
/// lässt auf dem Rechner keine Spuren. Siehe docs/architecture.md §7.
/// </summary>
public static class AppDataLocation
{
    /// <summary>Marker-Datei der portable Variante (leere Datei neben der Exe).</summary>
    public const string PortableMarker = "Momentum4Control.portable";

    /// <summary>Ist neben der Exe der Portable-Marker vorhanden?</summary>
    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

    /// <summary>Verzeichnis für Einstellungen, Logs und Presets – portable neben der Exe, sonst unter LOCALAPPDATA.</summary>
    public static string DataDirectory => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Momentum4Control");
}
