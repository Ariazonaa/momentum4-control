// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Momentum4.Core.Diagnostics;

namespace Momentum4.Core.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "m4-logtest-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Writes_timestamped_lines_and_respects_min_level()
    {
        string path;
        using (var provider = new FileLoggerProvider(_directory, "test", LogLevel.Information))
        {
            var logger = provider.CreateLogger("Momentum4.Some.Category");
            logger.LogDebug("unsichtbar");
            logger.LogInformation("Hallo {Wert}", 42);
            logger.LogWarning("Achtung");
            path = provider.CurrentFilePath;
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INF Category: Hallo 42$", lines[0]);
        Assert.EndsWith("WRN Category: Achtung", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Deletes_day_files_older_than_the_retention_but_keeps_others()
    {
        Directory.CreateDirectory(_directory);
        var today = DateTime.Now;
        var old = Path.Combine(_directory, $"app-{today.AddDays(-15):yyyyMMdd}.log");
        var recent = Path.Combine(_directory, $"app-{today.AddDays(-3):yyyyMMdd}.log");
        var otherPrefix = Path.Combine(_directory, $"poc-{today.AddDays(-30):yyyyMMdd}.log");
        var capture = Path.Combine(_directory, "capture-20200101-000000.json");
        foreach (var file in new[] { old, recent, otherPrefix, capture })
        {
            File.WriteAllText(file, "x");
        }

        using (var provider = new FileLoggerProvider(_directory, "app", LogLevel.Information, retainDays: 14))
        {
            provider.CreateLogger("Test").LogInformation("erste Zeile öffnet die Tagesdatei");
        }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(otherPrefix)); // anderes Präfix bleibt
        Assert.True(File.Exists(capture));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
