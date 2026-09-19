// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Momentum4.App.Settings;

/// <summary>
/// Einstellungen der App in <c>settings.json</c> (docs/architecture.md §7, Spezifikation §20). Der Autostart selbst steht
/// in der Registry (<see cref="Autostart"/>), nicht hier.
/// </summary>
public sealed record AppSettings
{
    public static readonly int[] RefreshIntervals = [10, 30, 60];

    /// <summary>Fallback-Abfrage in Sekunden (Notifications liefern die meisten Änderungen sofort).</summary>
    public int RefreshSeconds { get; init; } = 10;

    /// <summary>Beim Start nur das Tray-Symbol zeigen (der Autostart startet immer so).</summary>
    public bool StartMinimized { get; init; }

    public bool BatteryInTooltip { get; init; } = true;

    /// <summary>Sprache der Oberfläche: automatisch (nach Windows), Deutsch oder Englisch. Greift nach einem Neustart.</summary>
    public Localization.AppLanguage Language { get; init; } = Localization.AppLanguage.Auto;

    /// <summary>Transparent Hearing nach einem Neustart des Headsets wieder einschalten (war vorher Transparenz aktiv).</summary>
    public bool RestoreTransparency { get; init; }

    /// <summary>Jeden Befehl mit Zeit und Dauer ins Log schreiben (Spezifikation §21 „Protocol Logging“).</summary>
    public bool ProtocolLogging { get; init; }

    /// <summary>Der Hinweis „läuft im Tray weiter“ wurde schon einmal gezeigt.</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>Beim Start optional bei GitHub nachsehen, ob eine neuere Version vorliegt (Standard: aus, keine Cloud).</summary>
    public bool CheckForUpdates { get; init; }

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), AppSettingsJsonContext.Default.AppSettings) is { } settings)
            {
                return settings.Normalized();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unlesbar: mit Standardwerten weiter, die Datei wird beim nächsten Speichern ersetzt.
        }

        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
        File.Move(temp, path, overwrite: true);
    }

    private AppSettings Normalized() => RefreshIntervals.Contains(RefreshSeconds) ? this : this with { RefreshSeconds = 10 };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
