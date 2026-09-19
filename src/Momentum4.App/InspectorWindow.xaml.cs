// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.UI.Xaml;
using Momentum4.App.ViewModels;

namespace Momentum4.App;

public sealed partial class InspectorWindow : Window
{
    public InspectorWindow(InspectorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1200, 700));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        AmoledTitleBar.Apply(AppWindow);
        Closed += (_, _) => ViewModel.Dispose();
    }

    public InspectorViewModel ViewModel { get; }
}
