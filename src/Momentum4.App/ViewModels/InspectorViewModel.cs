// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momentum4.App.Localization;
using Momentum4.Core.Queue;
using Momentum4.Protocol;
using Momentum4.Protocol.Catalog;
using Momentum4.Protocol.Gaia;

namespace Momentum4.App.ViewModels;

/// <summary>Eine Zeile im Protokoll-Inspektor.</summary>
public sealed record InspectorEntry(string Time, string Direction, string Id, string Name, string Payload, string Decoded, string Duration);

/// <summary>
/// Protokoll-Inspektor (Spezifikation §22), nur im Debug-Build erreichbar: zeigt jeden Frame mit Deutung. Er sendet
/// nichts – eigene Befehle gibt es nur über <c>m4poc</c> mit Policy-Prüfung. Hängt nur am Verkehr, solange er offen ist.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject, IDisposable
{
    private const int MaxEntries = 500;

    private readonly GaiaCommandQueue? _queue;
    private readonly DispatcherQueue _dispatcher;

    public InspectorViewModel(GaiaCommandQueue? queue, DispatcherQueue dispatcher)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        if (_queue is not null)
        {
            _queue.Traffic += OnTraffic;
        }

        Status = _queue is null ? L.Get("InspNoService") : L.Get("InspWaiting");
    }

    public ObservableCollection<InspectorEntry> Entries { get; } = [];

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    public void Dispose()
    {
        if (_queue is not null)
        {
            _queue.Traffic -= OnTraffic;
        }
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    private void OnTraffic(object? sender, GaiaTraffic traffic)
    {
        if (IsPaused)
        {
            return;
        }

        var entry = ToEntry(traffic);
        _dispatcher.TryEnqueue(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }

            Status = L.Get("InspFrames", Entries.Count, MaxEntries);
        });
    }

    private static InspectorEntry ToEntry(GaiaTraffic traffic)
    {
        var frame = new GaiaFrameReader().Push(traffic.Raw).Frames.FirstOrDefault();
        var direction = traffic.Direction == TrafficDirection.Tx ? "TX" : frame?.Type == PacketType.Notification ? "RX ⚑" : "RX";
        return new InspectorEntry(
            traffic.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            direction,
            frame is null ? "?" : $"{frame.Vendor:X4}:{frame.Command.Value:X4}",
            traffic.CommandName ?? "?",
            frame is null ? Hex.Format(traffic.Raw) : Hex.Format(frame.Payload),
            frame is null ? L.Get("InspUnreadable") : FrameDescriber.Describe(frame) ?? string.Empty,
            traffic.Duration is { } duration ? $"{duration.TotalMilliseconds:0} ms" : string.Empty);
    }
}
