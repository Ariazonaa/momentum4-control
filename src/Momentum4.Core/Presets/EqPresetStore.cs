// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Momentum4.Core.Presets;

/// <summary>
/// Ein EQ-Preset: Name und Gains je Band in dB. Presets liegen nur in der App, nicht auf dem Headset – aktiv ist ein
/// Preset, wenn die Kurve auf dem Headset mit ihm übereinstimmt (docs/protocol.md §6.4).
/// </summary>
public sealed record EqPreset(string Name, IReadOnlyList<decimal> GainsDb, bool BuiltIn)
{
    public bool Matches(IReadOnlyList<decimal> gainsDb) => GainsDb.SequenceEqual(gainsDb);

    public bool Equals(EqPreset? other) =>
        other is not null && Name == other.Name && BuiltIn == other.BuiltIn && GainsDb.SequenceEqual(other.GainsDb);

    public override int GetHashCode() => HashCode.Combine(Name, BuiltIn, GainsDb.Count);
}

/// <summary>
/// Mitgelieferte Presets und die eigenen aus <c>presets.json</c> (docs/architecture.md §7). Die Werte der
/// mitgelieferten Presets sind eine eigene Festlegung dieses Projekts – keine Sennheiser-Werte, die Referenzen
/// widersprechen sich dort ohnehin (docs/research.md §6). Eine unlesbare Datei wird nicht überschrieben, sondern
/// beiseitegelegt.
/// </summary>
public sealed class EqPresetStore
{
    public const int MaxNameLength = 40;

    private const int FileVersion = 1;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private List<EqPreset> _own = [];

    public EqPresetStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    /// <summary>5 Bänder (90 / 325 / 1500 / 6500 / 6500 Hz am M4), alle innerhalb ±6 dB aus <c>0x1000</c>.</summary>
    public static IReadOnlyList<EqPreset> BuiltInPresets { get; } =
    [
        new("Neutral", [0.0m, 0.0m, 0.0m, 0.0m, 0.0m], BuiltIn: true),
        new("Music", [3.0m, 1.0m, 0.0m, 1.0m, 2.0m], BuiltIn: true),
        new("Movie", [4.0m, 1.5m, 0.0m, 1.0m, 0.0m], BuiltIn: true),
        new("Voice", [-3.0m, -1.0m, 2.5m, 2.0m, 0.0m], BuiltIn: true),
        new("Gaming", [2.0m, -1.0m, 1.0m, 3.0m, 1.0m], BuiltIn: true),
    ];

    public string FilePath => _path;

    /// <summary>Erst die mitgelieferten, dann die eigenen Presets (in der Reihenfolge der Datei).</summary>
    public IReadOnlyList<EqPreset> All
    {
        get
        {
            lock (_gate)
            {
                return [.. BuiltInPresets, .. _own];
            }
        }
    }

    public EqPreset? Find(string name) => All.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Das Preset, dessen Kurve der aktuellen entspricht – eigene vor mitgelieferten; <c>null</c> = „Eigene Kurve“.</summary>
    public EqPreset? FindMatching(IReadOnlyList<decimal> gainsDb)
    {
        var all = All;
        return all.FirstOrDefault(p => !p.BuiltIn && p.Matches(gainsDb)) ?? all.FirstOrDefault(p => p.BuiltIn && p.Matches(gainsDb));
    }

    /// <summary>Legt ein eigenes Preset an oder ersetzt ein eigenes gleichen Namens. Mitgelieferte Namen sind reserviert.</summary>
    public EqPreset Save(string name, IReadOnlyList<decimal> gainsDb)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new ArgumentException($"Name muss 1 bis {MaxNameLength} Zeichen lang sein.", nameof(name));
        }

        if (BuiltInPresets.Any(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"„{trimmed}“ ist ein mitgeliefertes Preset und kann nicht überschrieben werden.", nameof(name));
        }

        if (Invalid(gainsDb) is { } problem)
        {
            throw new ArgumentException(problem, nameof(gainsDb));
        }

        var preset = new EqPreset(trimmed, [.. gainsDb], BuiltIn: false);
        lock (_gate)
        {
            var own = _own.Where(p => !string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)).Append(preset).ToList();
            Write(own);
            _own = own;
        }

        return preset;
    }

    /// <summary>Löscht ein eigenes Preset; mitgelieferte lassen sich nicht löschen.</summary>
    public bool Delete(string name)
    {
        lock (_gate)
        {
            var own = _own.Where(p => !string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (own.Count == _own.Count)
            {
                return false;
            }

            Write(own);
            _own = own;
            return true;
        }
    }

    private static string? Invalid(IReadOnlyList<decimal> gainsDb)
    {
        if (gainsDb.Count is 0 or > 10)
        {
            return "1 bis 10 Gains erwartet.";
        }

        foreach (var gain in gainsDb)
        {
            if (gain < -12.8m || gain > 12.7m || gain * 10m != decimal.Truncate(gain * 10m))
            {
                return $"Gain {gain} dB ist nicht in 0,1-dB-Schritten zwischen −12,8 und +12,7 dB.";
            }
        }

        return null;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        PresetFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(_path), PresetJsonContext.Default.PresetFile);
            if (file is not { Version: FileVersion, Presets: not null })
            {
                throw new JsonException($"Version {file?.Version} statt {FileVersion} oder keine Presets.");
            }
        }
        catch (JsonException ex)
        {
            var aside = Path.Combine(Path.GetDirectoryName(_path) ?? ".", $"presets.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(_path, aside);
            _logger.LogWarning("presets.json ist unlesbar ({Reason}) – beiseitegelegt als {Aside}; es gelten nur die mitgelieferten Presets.", ex.Message, aside);
            return;
        }

        foreach (var entry in file.Presets)
        {
            var name = entry.Name?.Trim() ?? string.Empty;
            var problem = name.Length is 0 or > MaxNameLength ? "Name ungültig"
                : BuiltInPresets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ? "Name reserviert"
                : _own.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ? "Name doppelt"
                : entry.GainsDb is null ? "keine Gains"
                : Invalid(entry.GainsDb);
            if (problem is not null)
            {
                _logger.LogWarning("presets.json: Preset „{Name}“ übersprungen ({Problem}).", name, problem);
                continue;
            }

            _own.Add(new EqPreset(name, [.. entry.GainsDb!], BuiltIn: false));
        }
    }

    /// <summary>Erst in eine temporäre Datei, dann ersetzen – ein Absturz hinterlässt nie eine halbe Datei.</summary>
    private void Write(List<EqPreset> own)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var file = new PresetFile(FileVersion, [.. own.Select(p => new PresetEntry(p.Name, [.. p.GainsDb]))]);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, PresetJsonContext.Default.PresetFile));
        File.Move(temp, _path, overwrite: true);
    }
}

internal sealed record PresetFile(int Version, List<PresetEntry>? Presets);

internal sealed record PresetEntry(string? Name, List<decimal>? GainsDb);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PresetFile))]
internal sealed partial class PresetJsonContext : JsonSerializerContext;
