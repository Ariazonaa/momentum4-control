// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Protocol.Tests;

public sealed partial class CommandCatalogTests
{
    [Fact]
    public void All_non_blocked_entries_are_command_words()
    {
        Assert.All(
            CommandCatalog.All.Where(d => d.Safety != SafetyClass.Blocked),
            d => Assert.Equal(PacketType.Command, d.Command.Type));
    }

    [Fact]
    public void Find_resolves_response_notification_and_error_ids()
    {
        Assert.Same(CommandCatalog.GetBatteryLevel, CommandCatalog.Find(0x0495, new CommandWord(0x0703)));
        Assert.Same(CommandCatalog.GetBatteryLevel, CommandCatalog.Find(0x0495, new CommandWord(0x0683)));
        Assert.Same(CommandCatalog.GetBatteryLevel, CommandCatalog.Find(0x0495, new CommandWord(0x0783)));
        Assert.Null(CommandCatalog.Find(0x0495, new CommandWord(0x7F00)));
    }

    [Fact]
    public void Vendor_is_part_of_the_key()
    {
        // Gleiche ID, völlig andere Bedeutung (docs/protocol.md §4)
        Assert.Same(CommandCatalog.GetSoundMode, CommandCatalog.Find(ProtocolConstants.VendorSennheiser, new CommandWord(0x0804)));
        Assert.Same(CommandCatalog.QcErasePanicLog, CommandCatalog.Find(ProtocolConstants.VendorQualcomm, new CommandWord(0x0804)));
        Assert.Equal(SafetyClass.Blocked, CommandCatalog.QcErasePanicLog.Safety);
    }

    [Fact]
    public void Dangerous_commands_are_blocked()
    {
        Assert.All(
            new[]
            {
                CommandCatalog.FactoryReset, CommandCatalog.UpgradeEnable, CommandCatalog.Unknown0607,
                CommandCatalog.DisconnectPairedDevice, CommandCatalog.DeletePairedDevice, CommandCatalog.DeletePairedDeviceList,
                CommandCatalog.ResetMmiConfig, CommandCatalog.QcErasePanicLog, CommandCatalog.QcUpgradeControl,
            },
            d => Assert.Equal(SafetyClass.Blocked, d.Safety));
    }

    /// <summary>
    /// Katalog und docs/protocol.md müssen übereinstimmen: jede ID mit Status in beiden, BLOCKED ⇔ Blocked.
    /// </summary>
    [Fact]
    public void Catalog_matches_protocol_documentation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "docs", "protocol.md");
        Assert.SkipUnless(File.Exists(path), "docs/protocol.md nicht vorhanden (öffentlicher Build ohne docs/).");
        var doc = ProtocolDoc.Load(path);
        var catalog = CommandCatalog.All.ToDictionary(d => (d.Vendor, d.Command.Value));
        var problems = new List<string>();

        foreach (var (key, status) in doc.Statuses)
        {
            var name = Key(key);
            if (!catalog.TryGetValue(key, out var entry))
            {
                problems.Add($"{name}: steht in protocol.md ({status}), fehlt im Katalog");
                continue;
            }

            var blockedInDoc = status == "BLOCKED" || doc.Blocked.Contains(key);
            if (blockedInDoc != (entry.Safety == SafetyClass.Blocked))
            {
                problems.Add($"{name}: protocol.md {(blockedInDoc ? "BLOCKED" : status)}, Katalog {entry.Safety}");
                continue;
            }

            if (!blockedInDoc && MapStatus(status) != entry.Status)
            {
                problems.Add($"{name}: protocol.md {status}, Katalog {entry.Status}");
            }
        }

        foreach (var key in doc.Blocked.Where(k => !doc.Statuses.ContainsKey(k)))
        {
            if (!catalog.TryGetValue(key, out var entry) || entry.Safety != SafetyClass.Blocked)
            {
                problems.Add($"{Key(key)}: in protocol.md §9 gesperrt, im Katalog {(entry is null ? "nicht vorhanden" : entry.Safety.ToString())}");
            }
        }

        foreach (var key in catalog.Keys.Where(k => !doc.Statuses.ContainsKey(k) && !doc.Blocked.Contains(k)))
        {
            problems.Add($"{Key(key)} ({catalog[key].Name}): im Katalog, aber nicht in protocol.md");
        }

        Assert.True(problems.Count == 0, "Abweichungen:\n" + string.Join('\n', problems));
        Assert.True(doc.Statuses.Count > 80, $"Nur {doc.Statuses.Count} IDs aus protocol.md gelesen – Parser prüfen.");
    }

    private static string Key((ushort Vendor, ushort Command) key) =>
        string.Create(CultureInfo.InvariantCulture, $"{key.Vendor:X4}/{key.Command:X4}");

    private static ProtocolStatus MapStatus(string status) => status switch
    {
        "VERIFIED" => ProtocolStatus.Verified,
        "NEEDS HARDWARE TEST" => ProtocolStatus.NeedsHardwareTest,
        "UNVERIFIED" => ProtocolStatus.Unverified,
        "UNKNOWN" => ProtocolStatus.Unknown,
        _ => throw new InvalidOperationException($"Unbekannter Status '{status}'"),
    };

    /// <summary>Liest die Command-Tabellen aus docs/protocol.md (§5, §6, §6.12, §9).</summary>
    private sealed partial class ProtocolDoc
    {
        public Dictionary<(ushort Vendor, ushort Command), string> Statuses { get; } = [];

        public HashSet<(ushort Vendor, ushort Command)> Blocked { get; } = [];

        public static ProtocolDoc Load(string path)
        {
            var doc = new ProtocolDoc();
            var section = string.Empty;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal))
                {
                    section = line;
                    continue;
                }

                if (!line.StartsWith('|') || line.StartsWith("|---", StringComparison.Ordinal))
                {
                    continue;
                }

                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells[0] is "Command" or "Bereich" or "Vendor")
                {
                    continue; // Kopfzeile
                }

                if (section.StartsWith("### 6.12", StringComparison.Ordinal) && cells.Length == 4)
                {
                    var idText = cells[1].Split("Notification")[0];
                    doc.Add(ProtocolConstants.VendorQualcomm, idText, cells[3]);
                }
                else if (cells.Length == 10 && (section.StartsWith("### 5.", StringComparison.Ordinal) || section.StartsWith("### 6.", StringComparison.Ordinal)))
                {
                    var vendor = section.StartsWith("### 5.1", StringComparison.Ordinal) ? ProtocolConstants.VendorQualcomm : ProtocolConstants.VendorSennheiser;
                    doc.Add(vendor, cells[2], cells[9]);
                }
                else if (section.StartsWith("## 9.", StringComparison.Ordinal) && cells.Length == 5)
                {
                    var vendorMatch = HexId().Match(cells[0]);
                    if (vendorMatch.Success)
                    {
                        var vendor = ushort.Parse(vendorMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        foreach (var id in ExtractIds(cells[1]))
                        {
                            doc.Blocked.Add((vendor, id));
                        }
                    }
                }
            }

            return doc;
        }

        private void Add(ushort vendor, string idCell, string statusCell)
        {
            var status = ParseStatus(statusCell);
            foreach (var id in ExtractIds(idCell))
            {
                Statuses[(vendor, id)] = status;
            }
        }

        private static string ParseStatus(string cell)
        {
            var text = cell.Replace("**", string.Empty, StringComparison.Ordinal).Trim();
            foreach (var status in new[] { "BLOCKED", "NEEDS HARDWARE TEST", "UNVERIFIED", "UNKNOWN", "VERIFIED" })
            {
                if (text.StartsWith(status, StringComparison.Ordinal))
                {
                    return status;
                }
            }

            throw new InvalidOperationException($"Status nicht erkannt: '{cell}'");
        }

        /// <summary>Alle <c>0xNNNN</c>-IDs einer Zelle; Bereiche wie <c>`0x0600`–`0x0602`</c> werden aufgefächert.</summary>
        private static IEnumerable<ushort> ExtractIds(string cell)
        {
            var matches = HexId().Matches(cell);
            for (var i = 0; i < matches.Count; i++)
            {
                var value = ushort.Parse(matches[i].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (i + 1 < matches.Count)
                {
                    var between = cell[(matches[i].Index + matches[i].Length)..matches[i + 1].Index].Replace("`", string.Empty, StringComparison.Ordinal).Trim();
                    if (between == "–")
                    {
                        var end = ushort.Parse(matches[i + 1].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        for (var id = value; id <= end; id++)
                        {
                            yield return id;
                        }

                        i++;
                        continue;
                    }
                }

                yield return value;
            }
        }

        [GeneratedRegex("0x([0-9A-F]{4})")]
        private static partial Regex HexId();
    }
}
