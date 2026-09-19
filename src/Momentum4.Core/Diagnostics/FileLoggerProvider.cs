// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Momentum4.Core.Diagnostics;

/// <summary>
/// Schlichter Datei-Logger: eine Datei pro Tag (<c>{prefix}-yyyyMMdd.log</c>), Zeilen mit Millisekunden-Zeitstempel.
/// Schreibt synchron und threadsicher; für die Log-Mengen dieser App ausreichend. Mit <c>retainDays</c> &gt; 0 werden
/// ältere Tagesdateien desselben Präfixes beim Tageswechsel gelöscht (rollierende Logs).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly LogLevel _minLevel;
    private readonly int _retainDays;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _currentDay;

    public FileLoggerProvider(string directory, string prefix, LogLevel minLevel = LogLevel.Debug, int retainDays = 0)
    {
        _directory = directory;
        _prefix = prefix;
        _minLevel = minLevel;
        _retainDays = retainDays;
        Directory.CreateDirectory(directory);
    }

    public string CurrentFilePath => Path.Combine(_directory, $"{_prefix}-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelText(level))
            .Append(' ').Append(ShortCategory(category))
            .Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            var day = DateOnly.FromDateTime(now);
            if (_writer is null || day != _currentDay)
            {
                _writer?.Dispose();
                _writer = new StreamWriter(new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                _currentDay = day;
                DeleteOldFiles(day);
            }

            _writer.WriteLine(line.ToString());
        }
    }

    /// <summary>Löscht Tagesdateien dieses Präfixes, die älter als <c>retainDays</c> Tage sind.</summary>
    private void DeleteOldFiles(DateOnly today)
    {
        if (_retainDays <= 0)
        {
            return;
        }

        var oldest = today.AddDays(-_retainDays);
        foreach (var file in Directory.EnumerateFiles(_directory, $"{_prefix}-????????.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[(_prefix.Length + 1)..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Gerade von einem anderen Prozess geöffnet – beim nächsten Tageswechsel erneut.
                }
            }
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
