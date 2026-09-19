// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;

namespace Momentum4.App;

/// <summary>Schwarze Titelleiste passend zum AMOLED-Design (auch im inaktiven Zustand und hinter den Fensterknöpfen).</summary>
internal static class AmoledTitleBar
{
    public static void Apply(AppWindow window)
    {
        var bar = window.TitleBar;
        var black = Colors.Black;
        var hover = Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        var pressed = Color.FromArgb(0xFF, 0x26, 0x26, 0x26);
        var dim = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);

        bar.BackgroundColor = black;
        bar.InactiveBackgroundColor = black;
        bar.ForegroundColor = Colors.White;
        bar.InactiveForegroundColor = dim;
        bar.ButtonBackgroundColor = black;
        bar.ButtonInactiveBackgroundColor = black;
        bar.ButtonForegroundColor = Colors.White;
        bar.ButtonInactiveForegroundColor = dim;
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonHoverForegroundColor = Colors.White;
        bar.ButtonPressedBackgroundColor = pressed;
        bar.ButtonPressedForegroundColor = Colors.White;
    }
}
