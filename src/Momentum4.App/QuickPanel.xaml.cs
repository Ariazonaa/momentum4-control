// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Momentum4.App.ViewModels;
using Windows.Foundation;
using Windows.Graphics;

namespace Momentum4.App;

/// <summary>
/// Kleines Fenster über dem Infobereich (wie die Lautstärke-Flyouts von Windows): rahmenlos bis auf die Kante, nicht in
/// Taskleiste und Alt+Tab, immer oben. Die Höhe folgt dem Inhalt (der Regler erscheint nur im Modus ANC). Unterkante über
/// dem Tray-Symbol – auch bei automatisch ausgeblendeter Taskleiste, deren Arbeitsbereich bis zum Bildschirmrand reicht.
/// </summary>
public sealed partial class QuickPanel : Window
{
    private const double WidthDip = 340, MarginDip = 12;
    private readonly Action _openApp;
    private readonly (int Left, int Top, int Right, int Bottom)? _anchor;
    private readonly nint _hwnd;

    public QuickPanel(MainViewModel viewModel, (int Left, int Top, int Right, int Bottom)? anchor, Action openApp)
    {
        ViewModel = viewModel;
        _anchor = anchor;
        _openApp = openApp;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);

        Place(260); // Schätzung, damit das Fenster nicht erst in der Standardgröße aufblitzt
        Root.Loaded += (_, _) => PlaceToContent();
        viewModel.NoiseControl.PropertyChanged += OnLayoutRelevantChange;
        viewModel.PropertyChanged += OnLayoutRelevantChange;
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            viewModel.NoiseControl.PropertyChanged -= OnLayoutRelevantChange;
            viewModel.PropertyChanged -= OnLayoutRelevantChange;
        };
    }

    public MainViewModel ViewModel { get; }

    public void Show()
    {
        Activate();
        SetForegroundWindow(_hwnd); // Klick kam über das Tray-Symbol; ohne das bliebe der Fokus bei der Taskleiste
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Close();
        }
    }

    private void OnLayoutRelevantChange(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModels.NoiseControlViewModel.ShowStrength) or nameof(MainViewModel.ShowActionError))
        {
            DispatcherQueue.TryEnqueue(PlaceToContent); // erst nach dem Binding-Update messen
        }
    }

    private void PlaceToContent()
    {
        Root.Measure(new Size(WidthDip, double.PositiveInfinity));
        Place(Root.DesiredSize.Height);
    }

    /// <summary>
    /// Rechtsbündig auf dem Bildschirm des Symbols, Unterkante über dem Symbol (bzw. über dem Arbeitsbereich, falls das
    /// höher liegt). Ohne Symbol-Rechteck: unten rechts auf dem Bildschirm mit der Maus.
    /// </summary>
    private void Place(double heightDip)
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var width = (int)Math.Ceiling(WidthDip * scale);
        var height = (int)Math.Ceiling(heightDip * scale);
        var margin = (int)Math.Round(MarginDip * scale);
        GetCursorPos(out var cursor);
        var point = _anchor is { } a ? new PointInt32((a.Left + a.Right) / 2, Math.Min(a.Top, cursor.Y)) : new PointInt32(cursor.X, cursor.Y);
        var display = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest);
        var area = display.WorkArea;
        var bottom = area.Y + area.Height;
        if (_anchor is { } icon)
        {
            bottom = Math.Min(bottom, Math.Min(icon.Top, display.OuterBounds.Y + display.OuterBounds.Height));
        }

        var y = Math.Max(area.Y + margin, bottom - height - margin);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - width - margin, y, width, height));
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        Close();
        _openApp();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
