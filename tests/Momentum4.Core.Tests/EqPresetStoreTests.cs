// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging.Abstractions;
using Momentum4.Core.Presets;

namespace Momentum4.Core.Tests;

public sealed class EqPresetStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "m4-presets-" + Guid.NewGuid().ToString("N"));

    private string PresetPath => Path.Combine(_directory, "presets.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Built_in_presets_fit_the_m4_range_and_have_unique_names()
    {
        Assert.All(EqPresetStore.BuiltInPresets, p =>
        {
            Assert.True(p.BuiltIn);
            Assert.Equal(5, p.GainsDb.Count);
            Assert.All(p.GainsDb, g => Assert.InRange(g, -6.0m, 6.0m));
        });
        Assert.Equal(EqPresetStore.BuiltInPresets.Count, EqPresetStore.BuiltInPresets.Select(p => p.Name.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void Missing_file_means_only_built_in_presets_and_nothing_is_written()
    {
        var store = NewStore();

        Assert.Equal(EqPresetStore.BuiltInPresets, store.All);
        Assert.False(File.Exists(PresetPath));
    }

    [Fact]
    public void Saved_preset_survives_a_reload()
    {
        NewStore().Save("Eigene", [6.0m, 6.0m, 2.2m, 2.2m, 0.0m]);

        var reloaded = NewStore();

        Assert.Equal(new EqPreset("Eigene", [6.0m, 6.0m, 2.2m, 2.2m, 0.0m], BuiltIn: false), reloaded.Find("eigene"));
        Assert.False(File.Exists(PresetPath + ".tmp"));
    }

    [Fact]
    public void Saving_an_existing_name_replaces_it()
    {
        var store = NewStore();
        store.Save("Eigene", [1.0m, 0.0m, 0.0m, 0.0m, 0.0m]);

        store.Save("EIGENE", [2.0m, 0.0m, 0.0m, 0.0m, 0.0m]);

        var own = Assert.Single(NewStore().All, p => !p.BuiltIn);
        Assert.Equal(("EIGENE", 2.0m), (own.Name, own.GainsDb[0]));
    }

    [Theory]
    [InlineData("Neutral")]
    [InlineData(" voice ")]
    [InlineData("")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void Reserved_or_invalid_names_are_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => NewStore().Save(name, [0.0m, 0.0m, 0.0m, 0.0m, 0.0m]));
        Assert.False(File.Exists(PresetPath));
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(12.8)]
    [InlineData(-12.9)]
    public void Unencodable_gains_are_refused(double gain)
    {
        Assert.Throws<ArgumentException>(() => NewStore().Save("X", [(decimal)gain, 0.0m, 0.0m, 0.0m, 0.0m]));
    }

    [Fact]
    public void Delete_removes_only_own_presets()
    {
        var store = NewStore();
        store.Save("Eigene", [0.5m, 0.0m, 0.0m, 0.0m, 0.0m]);

        Assert.False(store.Delete("Neutral"));
        Assert.True(store.Delete("eigene"));
        Assert.False(store.Delete("eigene"));
        Assert.Equal(EqPresetStore.BuiltInPresets, NewStore().All);
    }

    [Fact]
    public void Matching_prefers_own_presets_and_returns_null_for_a_custom_curve()
    {
        var store = NewStore();
        Assert.Equal("Neutral", store.FindMatching([0.0m, 0.0m, 0.0m, 0.0m, 0.0m])?.Name);

        store.Save("Flach", [0.0m, 0.0m, 0.0m, 0.0m, 0.0m]);

        Assert.Equal("Flach", store.FindMatching([0.0m, 0.0m, 0.0m, 0.0m, 0.0m])?.Name);
        Assert.Null(store.FindMatching([0.1m, 0.0m, 0.0m, 0.0m, 0.0m]));
    }

    [Fact]
    public void Broken_file_is_moved_aside_instead_of_being_overwritten()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PresetPath, "{ kaputt");

        var store = NewStore();

        Assert.Equal(EqPresetStore.BuiltInPresets, store.All);
        Assert.False(File.Exists(PresetPath));
        var aside = Assert.Single(Directory.GetFiles(_directory, "presets.broken-*.json"));
        Assert.Equal("{ kaputt", File.ReadAllText(aside));
    }

    [Fact]
    public void Invalid_entries_are_skipped_and_valid_ones_kept()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PresetPath, """
            {
              "version": 1,
              "presets": [
                { "name": "Gut", "gainsDb": [1.0, 0.0, 0.0, 0.0, 0.0] },
                { "name": "Neutral", "gainsDb": [1.0, 0.0, 0.0, 0.0, 0.0] },
                { "name": "Krumm", "gainsDb": [0.05, 0.0, 0.0, 0.0, 0.0] },
                { "name": "gut", "gainsDb": [2.0, 0.0, 0.0, 0.0, 0.0] },
                { "name": "Leer" }
              ]
            }
            """);

        var own = NewStore().All.Where(p => !p.BuiltIn).ToList();

        Assert.Equal(["Gut"], own.Select(p => p.Name));
        Assert.True(File.Exists(PresetPath)); // nichts beiseitegelegt
    }

    [Fact]
    public void Unknown_file_version_is_moved_aside()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PresetPath, """{ "version": 2, "presets": [] }""");

        NewStore();

        Assert.Single(Directory.GetFiles(_directory, "presets.broken-*.json"));
    }

    private EqPresetStore NewStore() => new(PresetPath, NullLogger.Instance);
}
