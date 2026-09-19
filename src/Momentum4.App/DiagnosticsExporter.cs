// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Momentum4.Core.Diagnostics;
using Momentum4.Core.State;

namespace Momentum4.App;

/// <summary>
/// Diagnose-Export als ZIP (Spezifikation §21, docs/architecture.md §11): Systeminfo, aktueller Zustand, die letzten
/// Logs sowie <c>settings.json</c> und <c>presets.json</c>. Anonymisiert werden Bluetooth-Adressen, Gerätenamen (auch
/// in <c>0x1501</c>-Hexdumps), Rechner- und Benutzername.
/// </summary>
internal static class DiagnosticsExporter
{
    private const int LogDays = 3;

    public static void Export(HeadsetHost host, string zipPath, bool anonymize)
    {
        var state = host.Service?.Store.Current;
        string[] words = anonymize
            ? [.. state?.Multipoint?.Devices.Select(d => d.Name) ?? [], Environment.MachineName, Environment.UserName]
            : [];
        string Clean(string text) => anonymize ? DiagnosticsAnonymizer.Anonymize(text, words) : text;

        var temp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Add(zip, "info.txt", Clean(Info(host, state, anonymize)));
            Add(zip, "state.txt", Clean(StateText(state)));

            var logs = Path.Combine(host.DataDirectory, "logs");
            if (Directory.Exists(logs))
            {
                foreach (var file in Directory.EnumerateFiles(logs, "app-????????.log").Order().TakeLast(LogDays))
                {
                    Add(zip, "logs/" + Path.GetFileName(file), Clean(ReadShared(file)));
                }
            }

            foreach (var name in new[] { "settings.json", "presets.json" })
            {
                var file = Path.Combine(host.DataDirectory, name);
                if (File.Exists(file))
                {
                    Add(zip, name, Clean(ReadShared(file)));
                }
            }
        }

        File.Move(temp, zipPath, overwrite: true);
    }

    private static string Info(HeadsetHost host, Momentum4State? state, bool anonymized)
    {
        using var process = Process.GetCurrentProcess();
        var text = new StringBuilder()
            .AppendLine("MOMENTUM 4 Control – Diagnose (inoffiziell, nicht von Sennheiser)")
            .AppendLine($"Erstellt: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"Anonymisiert: {(anonymized ? "ja" : "nein")}")
            .AppendLine($"App: {typeof(DiagnosticsExporter).Assembly.GetName().Version}, {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "Native AOT")}")
            .AppendLine($"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})")
            .AppendLine($".NET: {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"Prozess: Working Set {process.WorkingSet64 / 1048576.0:0.0} MB, Commit {process.PrivateMemorySize64 / 1048576.0:0.0} MB, Laufzeit {DateTime.Now - process.StartTime:hh\\:mm\\:ss}")
            .AppendLine($"Headset-Adresse: {host.Address?.ToString() ?? "–"}")
            .AppendLine($"Startproblem: {host.StartupProblem ?? "–"}")
            .AppendLine($"Protokoll-Logging: {(host.ProtocolLogging ? "an" : "aus")}");
        if (state is not null)
        {
            text.AppendLine($"Verbindung: {state.Connection}{(state.LastError is { } error ? $" ({error})" : string.Empty)}")
                .AppendLine($"Modell: {state.Device?.ModelId ?? "–"}, Firmware {state.Device?.Firmware.ToString() ?? "–"}")
                .AppendLine($"Zuletzt vollständig gelesen: {state.LastFullRefresh?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "–"}");
        }

        return text.ToString();
    }

    private static string StateText(Momentum4State? state)
    {
        if (state is null)
        {
            return "Kein Zustand (Service nicht gestartet).";
        }

        var text = new StringBuilder()
            .AppendLine($"Akku: {state.BatteryPercent?.ToString() ?? "–"} %, lädt: {state.Charging?.ToString() ?? "–"}")
            .AppendLine($"Noise Control: {state.NoiseControl?.ToString() ?? "–"}")
            .AppendLine($"Equalizer: {state.Equalizer?.ToString() ?? "–"}");
        if (state.Multipoint is { } mp)
        {
            text.AppendLine($"Geräte (max. {mp.MaxConnections}, eigener Platz {mp.OwnIndex}):");
            foreach (var device in mp.Devices)
            {
                text.AppendLine($"  #{device.Index} {device.Name}{(device.IsThisComputer ? " (dieser PC)" : string.Empty)}{(device.Connected ? " – verbunden" : string.Empty)}");
            }
        }

        return text.ToString();
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
