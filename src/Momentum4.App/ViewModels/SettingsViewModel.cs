// SPDX-FileCopyrightText: 2026 Ariazonaa
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Momentum4.App.Localization;
using Momentum4.App.Settings;
using Momentum4.Core.State;

namespace Momentum4.App.ViewModels;

/// <summary>Einstellungen und Diagnose (Spezifikation §20/§21); Export der Diagnose folgt in Phase J.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly HeadsetHost _host;
    private readonly Action _settingsChanged;
    private bool _loading;

    public SettingsViewModel(HeadsetHost host, Action settingsChanged)
    {
        _host = host;
        _settingsChanged = settingsChanged;
        _loading = true;
        RefreshIntervalIndex = Array.IndexOf(AppSettings.RefreshIntervals, host.Settings.RefreshSeconds);
        LanguageIndex = (int)host.Settings.Language;
        StartWithWindows = Autostart.IsEnabled;
        StartMinimized = host.Settings.StartMinimized;
        BatteryInTooltip = host.Settings.BatteryInTooltip;
        RestoreTransparency = host.Settings.RestoreTransparency;
        ProtocolLogging = host.ProtocolLogging;
        CheckForUpdates = host.Settings.CheckForUpdates;
        UpdateAutostartHint();
        _loading = false;
    }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool BatteryInTooltip { get; set; }

    [ObservableProperty]
    public partial string AutostartHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RestoreTransparency { get; set; }

    [ObservableProperty]
    public partial bool ProtocolLogging { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasUpdateLink { get; set; }

    private string _updateUrl = string.Empty;

    [ObservableProperty]
    public partial string CurrentDeviceName { get; set; } = "–";

    [ObservableProperty]
    public partial string NewDeviceName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceNameHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SerialNumber { get; set; } = "–";

    [ObservableProperty]
    public partial string PromptLanguage { get; set; } = "–";

    [ObservableProperty]
    public partial bool AnonymizeExport { get; set; } = true;

    [ObservableProperty]
    public partial string ExportHint { get; set; } = string.Empty;

    /// <summary>Fensterhandle für den Speichern-Dialog (entpackte App: <c>InitializeWithWindow</c>).</summary>
    public Func<nint>? WindowHandle { get; set; }

    /// <summary>Öffnet den Protokoll-Inspektor (nur Debug-Build, gesetzt vom Hauptfenster).</summary>
    public Action? OpenInspector { get; set; }

    public bool IsDevelopmentBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    public List<string> RefreshIntervalOptions { get; } = [.. AppSettings.RefreshIntervals.Select(s => $"{s} s")];

    [ObservableProperty]
    public partial int RefreshIntervalIndex { get; set; }

    public List<string> LanguageOptions { get; } = [L.Get("LanguageAuto"), L.Get("LanguageGerman"), L.Get("LanguageEnglish")];

    [ObservableProperty]
    public partial int LanguageIndex { get; set; }

    [ObservableProperty]
    public partial string SavedHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Headset { get; set; } = "–";

    [ObservableProperty]
    public partial string Firmware { get; set; } = "–";

    [ObservableProperty]
    public partial string Bluetooth { get; set; } = "–";

    [ObservableProperty]
    public partial string LastRefresh { get; set; } = "–";

    public string Protocol => L.Get("ProtocolLine");

    public string LogDirectory => Path.Combine(_host.DataDirectory, "logs");

    public string Version { get; } = "Version " + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?") + L.Get("UnofficialSuffix");

    public void Apply(Momentum4State state)
    {
        Headset = state.Device?.ModelId ?? "–";
        Firmware = state.Device?.Firmware.ToString() ?? "–";
        Bluetooth = (_host.Address is { } address ? address.ToAnonymizedString() + " · " : string.Empty) + state.Connection switch
        {
            ConnectionState.Connected => L.Get("ConnLowerConnected"),
            ConnectionState.Connecting or ConnectionState.Initializing => L.Get("ConnLowerConnecting"),
            ConnectionState.WaitingRetry => L.Get("ConnLowerRetry"),
            _ => L.Get("ConnLowerDisconnected"),
        };
        LastRefresh = state.LastFullRefresh?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "–";
        SerialNumber = state.SerialNumber ?? "–";
        PromptLanguage = state.PromptLanguage is { } lang ? Momentum4.Protocol.Features.GenericAudioCodec.LanguageName(lang) : "–";
        if (state.DeviceName is { } name)
        {
            CurrentDeviceName = name;
            if (NewDeviceName.Length == 0)
            {
                NewDeviceName = name;
            }
        }
    }

    /// <summary>Headset umbenennen (localName). Der Name wird geschrieben und zurückgelesen (protocol.md §6.11).</summary>
    [RelayCommand]
    private async Task RenameAsync()
    {
        var name = NewDeviceName.Trim();
        if (_host.Service is not { } service || name.Length == 0 || name == CurrentDeviceName)
        {
            return;
        }

        DeviceNameHint = L.Get("Setting");
        try
        {
            CurrentDeviceName = await service.SetDeviceNameAsync(name, CancellationToken.None);
            DeviceNameHint = L.Get("DeviceNameSetHint", CurrentDeviceName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeviceNameHint = L.Get("NotSet", ActionErrors.Describe(ex));
        }
    }

    partial void OnRefreshIntervalIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= AppSettings.RefreshIntervals.Length)
        {
            return;
        }

        _host.SaveSettings(_host.Settings with { RefreshSeconds = AppSettings.RefreshIntervals[value] });
        SavedHint = L.Get("SavedRestart");
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (_loading || value < 0)
        {
            return;
        }

        _host.SaveSettings(_host.Settings with { Language = (AppLanguage)value });
        SavedHint = L.Get("LanguageRestartHint");
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            if (value)
            {
                Autostart.Enable();
            }
            else
            {
                Autostart.Disable();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AutostartHint = L.Get("AutostartFailed", ex.Message);
            return;
        }

        UpdateAutostartHint();
    }

    [RelayCommand]
    private void ShowInspector() => OpenInspector?.Invoke();

    partial void OnProtocolLoggingChanged(bool value)
    {
        if (!_loading)
        {
            _host.ProtocolLogging = value;
        }
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _host.SaveSettings(_host.Settings with { CheckForUpdates = value });
        if (value)
        {
            _ = RunUpdateCheckAsync();
        }
    }

    [RelayCommand]
    private async Task CheckUpdateAsync() => await RunUpdateCheckAsync();

    [RelayCommand]
    private void OpenRelease()
    {
        if (_updateUrl.Length > 0)
        {
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
    }

    private async Task RunUpdateCheckAsync()
    {
        HasUpdateLink = false;
        UpdateStatus = Localization.L.Get("UpdateChecking");
        var info = await UpdateChecker.CheckAsync(CancellationToken.None);
        if (info is null)
        {
            UpdateStatus = Localization.L.Get("UpdateFailed");
            return;
        }

        if (info.Available)
        {
            _updateUrl = info.Url;
            HasUpdateLink = info.Url.Length > 0;
            UpdateStatus = Localization.L.Get("UpdateAvailable", info.LatestVersion);
        }
        else
        {
            UpdateStatus = Localization.L.Get("UpToDate", UpdateChecker.CurrentVersion.ToString(3));
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            SuggestedFileName = $"Momentum4Control-Diagnose-{DateTime.Now:yyyyMMdd-HHmm}",
        };
        picker.FileTypeChoices.Add(L.Get("ZipArchive"), [".zip"]);
        if (WindowHandle?.Invoke() is { } hwnd and not 0)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var anonymize = AnonymizeExport;
        try
        {
            ExportHint = L.Get("Exporting");
            await Task.Run(() => DiagnosticsExporter.Export(_host, file.Path, anonymize));
            ExportHint = L.Get("Exported", file.Path, anonymize ? L.Get("AnonymizedSuffix") : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ExportHint = L.Get("ExportFailed", ex.Message);
        }
    }

    partial void OnRestoreTransparencyChanged(bool value)
    {
        if (!_loading)
        {
            _host.SaveSettings(_host.Settings with { RestoreTransparency = value });
        }
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_loading)
        {
            _host.SaveSettings(_host.Settings with { StartMinimized = value });
        }
    }

    partial void OnBatteryInTooltipChanged(bool value)
    {
        if (!_loading)
        {
            _host.SaveSettings(_host.Settings with { BatteryInTooltip = value });
            _settingsChanged();
        }
    }

    private void UpdateAutostartHint() => AutostartHint =
        !StartWithWindows ? L.Get("AutostartOn2")
        : !Autostart.IsApprovedByUser ? L.Get("AutostartDisabled")
        : Autostart.PointsElsewhere ? L.Get("AutostartPointsElsewhere")
        : L.Get("AutostartActive");

    [RelayCommand]
    private void OpenLogDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogDirectory}\"") { UseShellExecute = true });
    }
}
