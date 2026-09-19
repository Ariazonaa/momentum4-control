// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Momentum4.Protocol;
using Momentum4.Protocol.Gaia;

namespace Momentum4.Core.Tests;

/// <summary>
/// Antwortet auf Requests mit den echten Mitschnitten aus tests/CapturedPackets (FW 3.37.3) – der Service wird so
/// gegen reales Geräteverhalten getestet. Noise Control und EQ sind zusätzlich zustandsbehaftet, damit Setter getestet
/// werden können (Startwerte wie im Mitschnitt: ANC an, Anti-Wind Maximum, Custom, Pegel 100, Transparent Hearing an).
/// Nachgebildete Seiteneffekte (Hardware-Befunde Phase E/H): <c>0x1804 [01]</c> merkt sich den Pegel und setzt ihn auf 100;
/// <c>0x1804 [00]</c>, <c>0x1A00</c> und ANC einschalten (<c>0x1A04 [01]</c> aus dem Aus-Zustand) beenden Transparent
/// Hearing und stellen den gemerkten Pegel wieder her; ANC aus lässt Transparent Hearing formal an.
/// </summary>
internal sealed class SimulatedHeadset
{
    private readonly Dictionary<string, string> _answers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public SimulatedHeadset()
    {
        // Seriennummer (001D/0003) – Vendor Qualcomm, daher als feste Antwort (HandleStateful behandelt nur Sennheiser).
        _answers["FF 03 00 00 00 1D 00 03"] = "FF 03 00 0C 00 1D 01 03 4D 34 54 45 53 54 30 30 30 30 30 30";
        var directory = Path.Combine(AppContext.BaseDirectory, "CapturedPackets");
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var root = json.RootElement;
            if (root.TryGetProperty("steps", out var steps))
            {
                foreach (var step in steps.EnumerateArray())
                {
                    _answers[step.GetProperty("tx").GetString()!] = step.GetProperty("rx").GetString()!;
                }
            }
            else
            {
                var exchange = root.GetProperty("exchange").EnumerateArray().ToArray();
                _answers[exchange.Single(e => e.GetProperty("dir").GetString() == "TX").GetProperty("hex").GetString()!] =
                    exchange.Single(e => e.GetProperty("dir").GetString() == "RX").GetProperty("hex").GetString()!;
            }
        }
    }

    // ---- zustandsbehaftete Noise Control ----
    public bool AncEnabled { get; set; } = true;

    public byte[] AncTable { get; set; } = [0x01, 0x01, 0x02, 0x00, 0x03, 0x00];

    public byte AncLevel { get; set; } = 100;

    public bool TransparentHearing { get; set; } = true;

    /// <summary>Pegel von vor dem Einschalten von Transparent Hearing (am 2026-09-19 anfangs 0, wie die Mitschnitte zeigen).</summary>
    public byte LevelBeforeTransparentHearing { get; set; }

    // ---- zustandsbehafteter EQ (Startwerte wie im Mitschnitt: +6,0 / +6,0 / +2,2 / +2,2 / 0 dB, Bass Boost an) ----
    public byte[] EqGains { get; set; } = [0x3C, 0x3C, 0x16, 0x16, 0x00];

    public bool BassBoost { get; set; } = true;

    public byte SoundMode { get; set; } = 1; // Equalizer

    /// <summary>0x1001 für dieses Band mit Error Reason 0x83 ablehnen (Rollback-Tests).</summary>
    public int? RejectEqBand { get; set; }

    // ---- zustandsbehaftete Trage- und Anrufeinstellungen (Startwerte wie im Mitschnitt von Stufe 4) ----
    public bool OnHeadDetection { get; set; } = true;

    public bool SmartPause { get; set; }

    public bool AutoAnswer { get; set; }

    public bool ComfortCall { get; set; } = true;

    public bool TouchLocked { get; set; }

    /// <summary>Auto Power Off in Sekunden (Timer 0), wie im Mitschnitt: 900 s.</summary>
    public int AutoPowerOffSeconds { get; set; } = 900;

    /// <summary>Töne und Sprachansagen (<c>0x0802</c>), wie am eigenen Gerät: 2 = Töne und Stimme.</summary>
    public byte PromptMode { get; set; } = 2;

    /// <summary>Gerätename (localName, <c>0x2802</c>).</summary>
    public string DeviceName { get; set; } = "MOMENTUM 4";

    /// <summary>Codec (<c>0x0800</c>), wie am eigenen Gerät: 5 = aptX HD.</summary>
    public byte Codec { get; set; } = 5;

    /// <summary>Sprache der Ansagen (<c>0x0807</c>): 1 = DE.</summary>
    public byte PromptLanguage { get; set; } = 1;

    /// <summary>Automatische Pause bei Transparenz (<c>0x1801</c>).</summary>
    public bool AutoPause { get; set; }

    /// <summary>Tragezustand für <c>0x0402</c> (3 = aufgesetzt).</summary>
    public byte WearState { get; set; } = 3;

    // ---- zustandsbehaftetes Multipoint (Liste wie im Mitschnitt, anonymisiert; Platz 0 = dieser PC, verbunden) ----
    public List<string> PeerNames { get; } = ["PC-A", "Laptop-B", "Phone-C", "PC-D"];

    public List<bool> PeerConnected { get; } = [true, false, false, false];

    /// <summary>0x1402 bestätigen, das Gerät verbindet sich aber nie (Handy aus, außer Reichweite …).</summary>
    public bool PeersNeverConnect { get; set; }

    /// <summary>0x1402 mit Error-Frame 0x1582 und diesem Status ablehnen.</summary>
    public byte? RejectConnectStatus { get; set; }

    /// <summary>0x1A00 mit 2 Byte (Einzelpaar) mit Error Reason 0x05 ablehnen.</summary>
    public bool RejectSinglePair { get; set; }

    /// <summary>0x1A00 mit kompletter Tabelle mit Error Reason 0x05 ablehnen.</summary>
    public bool RejectFullTable { get; set; }

    /// <summary>Schreibbefehle bestätigen, aber nicht übernehmen (Blind-Ack wie in docs/protocol.md §3.2).</summary>
    public bool IgnoreWrites { get; set; }

    /// <summary>Headset hängt: Die Verbindung steht, aber es kommt keine Antwort mehr (Spezifikation §31 „Timeout“).</summary>
    public bool Unresponsive { get; set; }

    /// <summary>Requests, auf die es keinen Mitschnitt gab (sollten im Test leer bleiben).</summary>
    public List<string> Unanswered { get; } = [];

    public List<string> Requests { get; } = [];

    public void Set(string txHex, string rxHex)
    {
        lock (_gate)
        {
            _answers[txHex] = rxHex;
        }
    }

    public IEnumerable<byte[]> Respond(byte[] request)
    {
        var tx = Hex.Format(request);
        lock (_gate)
        {
            Requests.Add(tx);
            if (Unresponsive)
            {
                return [];
            }

            if (HandleStateful(request) is { } answer)
            {
                return [answer];
            }

            if (_answers.TryGetValue(tx, out var rx))
            {
                return [Hex.Parse(rx)];
            }

            Unanswered.Add(tx);
            return [];
        }
    }

    public int CountRequests(string txHex)
    {
        lock (_gate)
        {
            return Requests.Count(r => r == txHex);
        }
    }

    public List<string> RequestsStartingWith(string prefix)
    {
        lock (_gate)
        {
            return Requests.Where(r => r.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        }
    }

    private byte[]? HandleStateful(byte[] request)
    {
        var frame = new GaiaFrameReader().Push(request).Frames.Single();
        if (frame.Vendor != ProtocolConstants.VendorSennheiser)
        {
            return null;
        }

        var p = frame.Payload;
        switch (frame.Command.Value)
        {
            case 0x1003: return Response(frame, EqGains);
            case 0x1002 when p.Length == 1 && p[0] < EqGains.Length: return Response(frame, EqGains[p[0]]); // ohne Band-Echo wie am M4
            case 0x1009: return Response(frame, BassBoost ? (byte)1 : (byte)0);
            case 0x0804: return Response(frame, 0x00, SoundMode);
            case 0x1400: return Response(frame, 0x00, (byte)PeerNames.Count);
            case 0x1401 when p.Length == 1 && p[0] < PeerNames.Count:
                return Response(frame, [p[0], p[0], PeerConnected[p[0]] ? (byte)1 : (byte)0, .. System.Text.Encoding.UTF8.GetBytes(PeerNames[p[0]]), 0x00]);
            case 0x1404 when p.Length == 1 && p[0] < PeerNames.Count: return Response(frame, p[0], PeerConnected[p[0]] ? (byte)1 : (byte)0);
            case 0x1402:
                if (RejectConnectStatus is { } status)
                {
                    return Error(frame, status);
                }

                if (p.Length == 1 && p[0] < PeerNames.Count && !PeersNeverConnect && !IgnoreWrites)
                {
                    PeerConnected[p[0]] = true;
                }

                return Response(frame);
            case 0x0803:
                if (p.Length != 2 || p[0] != 0 || p[1] > 3)
                {
                    return Error(frame, 0x05);
                }

                if (!IgnoreWrites)
                {
                    SoundMode = p[1];
                }

                return Response(frame);
            case 0x1001:
                if (p.Length != 2 || p[0] >= EqGains.Length || p[0] == RejectEqBand)
                {
                    return Error(frame, 0x83); // Index außerhalb des Bereichs ([DS])
                }

                if (!IgnoreWrites)
                {
                    EqGains[p[0]] = p[1];
                }

                return Response(frame);
            case 0x1008:
                if (!IgnoreWrites)
                {
                    BassBoost = p[0] == 1;
                }

                return Response(frame);

            case 0x0401: return Response(frame, OnHeadDetection ? (byte)1 : (byte)0);
            case 0x080D: return Response(frame, SmartPause ? (byte)1 : (byte)0);
            case 0x080B: return Response(frame, AutoAnswer ? (byte)1 : (byte)0);
            case 0x0815: return Response(frame, ComfortCall ? (byte)1 : (byte)0);
            case 0x1607: return Response(frame, TouchLocked ? (byte)1 : (byte)0);
            case 0x0402: return Response(frame, WearState);
            case 0x0802: return Response(frame, PromptMode);
            case 0x0800: return Response(frame, Codec);
            case 0x0807: return Response(frame, PromptLanguage);
            case 0x1801: return Response(frame, AutoPause ? (byte)1 : (byte)0);
            case 0x1800:
                if (!IgnoreWrites && p.Length == 1 && p[0] <= 1)
                {
                    AutoPause = p[0] == 1;
                }

                return Response(frame);
            case 0x2802: return Response(frame, System.Text.Encoding.UTF8.GetBytes(DeviceName));
            case 0x2801:
                if (p.Length == 0)
                {
                    return Error(frame, 0x03);
                }

                if (!IgnoreWrites)
                {
                    DeviceName = System.Text.Encoding.UTF8.GetString(p);
                }

                return Response(frame);
            case 0x0801 when p.Length == 1 && p[0] <= 2:
                if (!IgnoreWrites)
                {
                    PromptMode = p[0];
                }

                return Response(frame);
            case 0x0601 when p.Length == 1 && p[0] == 0: return Response(frame, 0x00, (byte)(AutoPowerOffSeconds >> 8), (byte)AutoPowerOffSeconds);
            case 0x0600 when p.Length == 3 && p[0] == 0:
                if (!IgnoreWrites)
                {
                    AutoPowerOffSeconds = (p[1] << 8) | p[2];
                }

                return Response(frame);
            case 0x0400 or 0x080C or 0x080A or 0x0814 or 0x1606:
                if (!IgnoreWrites)
                {
                    var on = p[0] == 1;
                    _ = frame.Command.Value switch
                    {
                        0x0400 => OnHeadDetection = on,
                        0x080C => SmartPause = on,
                        0x080A => AutoAnswer = on,
                        0x1606 => TouchLocked = on,
                        _ => ComfortCall = on,
                    };
                }

                return Response(frame);

            case 0x1A05: return Response(frame, AncEnabled ? (byte)1 : (byte)0);
            case 0x1A01: return Response(frame, AncTable);
            case 0x1A03: return Response(frame, AncLevel);
            case 0x1805: return Response(frame, TransparentHearing ? (byte)1 : (byte)0);

            case 0x1A04:
                if (!IgnoreWrites)
                {
                    var switchingOn = !AncEnabled && p[0] == 1;
                    AncEnabled = p[0] == 1;
                    if (switchingOn)
                    {
                        LeaveTransparentHearing();
                    }
                }

                return Response(frame);
            case 0x1A02:
                if (!IgnoreWrites)
                {
                    AncLevel = p[0];
                }

                return Response(frame);
            case 0x1804:
                if (!IgnoreWrites)
                {
                    if (p[0] == 1)
                    {
                        if (!TransparentHearing)
                        {
                            LevelBeforeTransparentHearing = AncLevel;
                        }

                        TransparentHearing = true;
                        AncLevel = 100;
                    }
                    else
                    {
                        LeaveTransparentHearing();
                    }
                }

                return Response(frame);
            case 0x1A00 when p.Length == 2:
                if (RejectSinglePair)
                {
                    return Error(frame, 0x05);
                }

                if (!IgnoreWrites)
                {
                    for (var i = 0; i < AncTable.Length; i += 2)
                    {
                        if (AncTable[i] == p[0])
                        {
                            AncTable[i + 1] = p[1];
                        }
                    }

                    LeaveTransparentHearing();
                }

                return Response(frame);
            case 0x1A00:
                if (RejectFullTable)
                {
                    return Error(frame, 0x05);
                }

                if (!IgnoreWrites)
                {
                    AncTable = p.ToArray();
                    LeaveTransparentHearing();
                }

                return Response(frame);
            default:
                return null;
        }
    }

    private void LeaveTransparentHearing()
    {
        if (TransparentHearing)
        {
            TransparentHearing = false;
            AncLevel = LevelBeforeTransparentHearing;
        }
    }

    private static byte[] Response(GaiaFrame request, params byte[] payload) =>
        GaiaFrameCodec.Encode(request.Vendor, request.Command.AsResponse(), payload);

    private static byte[] Error(GaiaFrame request, byte reason) =>
        GaiaFrameCodec.Encode(request.Vendor, request.Command.AsError(), [reason]);
}
