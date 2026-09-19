// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Momentum4.App.ViewModels;

namespace Momentum4.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        viewModel.Settings.WindowHandle = () => WinRT.Interop.WindowNative.GetWindowHandle(this);
        viewModel.Settings.OpenInspector = () =>
        {
            _inspector ??= CreateInspector();
            _inspector.Activate();
        };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 780));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        AmoledTitleBar.Apply(AppWindow);
    }

    private InspectorWindow? _inspector;

    public MainViewModel ViewModel { get; }

    private InspectorWindow CreateInspector()
    {
        var window = new InspectorWindow(new InspectorViewModel(ViewModel.Queue, DispatcherQueue));
        window.Closed += (_, _) => _inspector = null;
        Closed += (_, _) => window.Close(); // Inspektor geht mit dem Hauptfenster
        return window;
    }

    /// <summary>Fenster zeigen, aus der Minimierung holen und nach vorn bringen (zweiter Start, später Tray).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
