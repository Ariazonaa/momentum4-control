// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Momentum4.App.Tray;

/// <summary>Eintrag im nativen Kontextmenü; <see cref="Id"/> 0 = nicht auswählbar.</summary>
internal sealed record TrayMenuItem(string Text, uint Id = 0, bool Enabled = true, bool Checked = false, IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static readonly TrayMenuItem Separator = new("-");
}

/// <summary>
/// Tray-Symbol über <c>Shell_NotifyIconW</c> mit nativem Win32-Kontextmenü (docs/architecture.md §7, §14). Braucht kein
/// XAML-Fenster – so kann das WinUI-Fenster im Tray-Betrieb komplett freigegeben werden (RAM-Budget). Muss auf dem
/// UI-Thread erzeugt werden; dessen Nachrichtenschleife bedient das versteckte Fenster.
/// </summary>
internal sealed unsafe partial class TrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 1;
    private const uint WmSettingChange = 0x001A;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifInfo = 0x01, NiifWarning = 0x02;
    private const uint NotifyIconVersion4 = 4;

    private const string ClassName = "Momentum4Control.Tray";

    private static TrayIcon? s_instance;

    private readonly nint _hwnd;
    private readonly uint _taskbarCreated;
    private readonly string _iconDirectory;
    private nint _icon;
    private string _tooltip;

    public TrayIcon(string tooltip, string iconDirectory)
    {
        if (s_instance is not null)
        {
            throw new InvalidOperationException("Es gibt schon ein Tray-Symbol.");
        }

        s_instance = this;
        _tooltip = tooltip;
        _iconDirectory = iconDirectory;
        var instance = GetModuleHandleW(null);
        fixed (char* className = ClassName)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = &WndProc,
                Instance = instance,
                ClassName = className,
            };
            RegisterClassExW(&windowClass);

            // Unsichtbares Top-Level-Fenster (kein Message-only-Fenster): nur so kommt „TaskbarCreated“ an.
            _hwnd = CreateWindowExW(0, className, className, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, instance, 0);
        }

        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadThemeIcon();
        Add();
    }

    /// <summary>Linksklick oder Enter auf dem Symbol: Schnellzugriff.</summary>
    public event EventHandler? SelectRequested;

    /// <summary>Doppelklick auf das Symbol: Hauptfenster.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Rechtsklick: der Besitzer baut das Menü und ruft <see cref="ShowMenu"/>.</summary>
    public event EventHandler? MenuRequested;

    public void SetTooltip(string text)
    {
        _tooltip = text;
        var data = NewData(NifTip | NifShowTip);
        Shell_NotifyIconW(NimModify, &data);
    }

    /// <summary>Hinweis als Benachrichtigung (unter Windows 11 als Toast).</summary>
    public void ShowBalloon(string title, string text, bool warning)
    {
        var data = NewData(NifInfo);
        Copy(title, data.InfoTitle, 64);
        Copy(text, data.Info, 256);
        data.InfoFlags = warning ? NiifWarning : NiifInfo;
        Shell_NotifyIconW(NimModify, &data);
    }

    /// <summary>
    /// Bildschirm-Rechteck des Symbols (physische Pixel) oder <c>null</c>. Bei automatisch ausgeblendeter Taskleiste
    /// liegt es außerhalb des Bildschirms, solange die Leiste versteckt ist.
    /// </summary>
    public (int Left, int Top, int Right, int Bottom)? GetIconRect()
    {
        var id = new NotifyIconIdentifier { Size = (uint)sizeof(NotifyIconIdentifier), Hwnd = _hwnd, Id = 1 };
        return Shell_NotifyIconGetRect(&id, out var rect) == 0 ? (rect.Left, rect.Top, rect.Right, rect.Bottom) : null;
    }

    /// <summary>Zeigt das Menü an der Mausposition und liefert die gewählte Id (0 = nichts).</summary>
    public uint ShowMenu(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = Build(items);
        try
        {
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd); // sonst schließt das Menü nicht beim Klick daneben
            var id = (uint)TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0080 /* TPM_NONOTIFY */ | 0x0002 /* TPM_RIGHTBUTTON */, point.X, point.Y, _hwnd, 0);
            PostMessageW(_hwnd, 0 /* WM_NULL */, 0, 0);
            return id;
        }
        finally
        {
            DestroyMenu(menu); // zerstört Untermenüs mit
        }
    }

    public void Dispose()
    {
        var data = NewData(0);
        Shell_NotifyIconW(NimDelete, &data);
        if (_icon != 0)
        {
            DestroyIcon(_icon);
        }

        DestroyWindow(_hwnd);
        s_instance = null;
    }

    private static nint Build(IReadOnlyList<TrayMenuItem> items)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (ReferenceEquals(item, TrayMenuItem.Separator))
            {
                AppendMenuW(menu, 0x0800 /* MF_SEPARATOR */, 0, null);
                continue;
            }

            var flags = (item.Enabled ? 0u : 0x0001u /* MF_GRAYED */) | (item.Checked ? 0x0008u /* MF_CHECKED */ : 0u);
            if (item.Children is { Count: > 0 } children)
            {
                AppendMenuW(menu, flags | 0x0010 /* MF_POPUP */, (nuint)Build(children), item.Text);
            }
            else
            {
                AppendMenuW(menu, flags, item.Id, item.Text);
            }
        }

        return menu;
    }

    private void Add()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        Shell_NotifyIconW(NimAdd, &data);
        data.TimeoutOrVersion = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, &data);
    }

    private NotifyIconData NewData(uint flags)
    {
        var data = new NotifyIconData
        {
            Size = (uint)sizeof(NotifyIconData),
            Window = _hwnd,
            Id = 1,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        Copy(_tooltip, data.Tip, 128);
        return data;
    }

    /// <summary>Weißes Symbol für dunkle, schwarzes für helle Taskleisten (Windows-Einstellung „Windows-Modus“).</summary>
    private nint LoadThemeIcon()
    {
        var light = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        return LoadImageW(0, Path.Combine(_iconDirectory, light ? "TrayLight.ico" : "TrayDark.ico"), 1 /* IMAGE_ICON */, size, size, 0x0010 /* LR_LOADFROMFILE */);
    }

    private nint Handle(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case WmLButtonUp or NinSelect or NinKeySelect:
                    SelectRequested?.Invoke(this, EventArgs.Empty);
                    return 0;
                case WmLButtonDblClk:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    return 0;
                case WmContextMenu:
                    MenuRequested?.Invoke(this, EventArgs.Empty);
                    return 0;
            }

            return 0;
        }

        if (message == _taskbarCreated)
        {
            Add(); // Explorer wurde neu gestartet
            return 0;
        }

        if (message == WmSettingChange)
        {
            var old = _icon;
            _icon = LoadThemeIcon();
            var data = NewData(NifIcon);
            Shell_NotifyIconW(NimModify, &data);
            if (old != 0)
            {
                DestroyIcon(old);
            }
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            return s_instance is { } tray && hwnd == tray._hwnd
                ? tray.Handle(hwnd, message, wParam, lParam)
                : DefWindowProcW(hwnd, message, wParam, lParam);
        }
        catch
        {
            // Eine Ausnahme darf die native Nachrichtenschleife nie erreichen.
            return 0;
        }
    }

    private static void Copy(string text, char* target, int capacity)
    {
        var length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(target, capacity));
        target[length] = '\0';
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint Hwnd;
        public uint Id;
        public Guid Item;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, NotifyIconData* data);

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(NotifyIconIdentifier* identifier, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WndClassEx* windowClass);

    [LibraryImport("user32.dll")]
    private static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
