using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Yita.Core.Platform;
using Yita.Core.Selection;
using Yita.Core.Settings;
using Yita.Native.Mac;

namespace Yita.Desktop.Tests;

public sealed class PlatformHostTests
{
    [AvaloniaFact]
    public async Task NativeRuntimeReusesPermissionServiceAndMonitoringRequiresAnExplicitAction()
    {
        using var runtime = new TestPermissionRuntime();
        using var fixture = await Fixture.CreateAsync(runtime);
        Assert.Equal(1, runtime.Permissions.Checks);
        Assert.Equal(0, runtime.Permissions.InputRequests);
        Assert.True(fixture.Window.FindControl<Button>("RequestInputMonitoringButton")!.IsEnabled);
        Assert.True(fixture.Window.FindControl<ToggleSwitch>("EnabledCheckBox")!.IsEnabled);
        Assert.True(fixture.Window.FindControl<TextBlock>("PlatformStatusText")!.IsVisible);
        await fixture.Window.RefreshPlatformPermissionsAsync(MainWindow.PermissionAction.RequestInputMonitoring);
        Assert.Equal(1, runtime.Permissions.InputRequests);
        Assert.False(fixture.Window.FindControl<Button>("RequestInputMonitoringButton")!.IsEnabled);
        Assert.True(fixture.Window.FindControl<TextBlock>("PlatformStatusText")!.IsVisible);
        await fixture.Window.RefreshPlatformPermissionsAsync(MainWindow.PermissionAction.RequestAccessibility);
        Assert.False(fixture.Window.FindControl<TextBlock>("PlatformStatusText")!.IsVisible);
        await fixture.Window.RefreshPlatformPermissionsAsync(MainWindow.PermissionAction.OpenInputSettings);
        Assert.Equal(1, runtime.Permissions.InputSettingsOpened);
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Assert.Equal("允许输入监控", fixture.Window.FindControl<Button>("RequestInputMonitoringButton")!.Content);
        fixture.Window.ShutdownServices();
        Assert.True(runtime.Permissions.Disposed);
    }

    [AvaloniaFact]
    public async Task SavingModelConfigurationWithMissingNativePermissionsKeepsThePermissionsPageOpen()
    {
        using var runtime = new TestPermissionRuntime();
        using var fixture = await Fixture.CreateAsync(runtime);
        fixture.Window.FindControl<ListBox>("SettingsNavigation")!.SelectedIndex = 3;
        Click(fixture.Window.FindControl<Button>("SaveSettingsButton")!);
        await UntilAsync(() => fixture.Window.FindControl<ListBox>("SettingsNavigation")!.SelectedIndex == 0);
        Assert.True(fixture.Window.IsVisible);
        Assert.True(fixture.Window.FindControl<TextBlock>("PlatformStatusText")!.IsVisible);
        Assert.Contains("Accessibility", fixture.Window.FindControl<TextBlock>("PlatformStatusText")!.Text);
        Assert.True(fixture.Window.SavedSettings.IsEnabled);
    }

    [AvaloniaFact]
    public async Task ReturningFromSystemSettingsRepairsInputAndRefreshesPermissionState()
    {
        using var runtime = new TestPermissionRuntime();
        using var fixture = await Fixture.CreateAsync(runtime);
        await runtime.Permissions.RequestAccessibilityPermissionAsync();
        await runtime.Permissions.RequestInputMonitoringPermissionAsync();
        await fixture.Window.RefreshPlatformPermissionsAsync();
        Assert.False(fixture.Window.FindControl<TextBlock>("PlatformStatusText")!.IsVisible);
        Assert.Equal(1, runtime.Repairs);
    }

    [AvaloniaFact]
    public async Task DiskImageInstallationNoticeIsVisibleFromModelConfiguration()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Window.FindControl<ListBox>("SettingsNavigation")!.SelectedIndex = 3;
        fixture.Window.UpdateMacInstallationStatus(MacApplicationLocation.DiskImage);
        var notice = fixture.Window.FindControl<TextBlock>("MacInstallationStatusText")!;
        Assert.True(notice.IsVisible);
        Assert.Contains("disk image", notice.Text);
        Assert.DoesNotContain(fixture.Window.FindControl<StackPanel>("GeneralPage")!, notice.GetLogicalAncestors());
        fixture.Window.UpdateMacInstallationStatus(MacApplicationLocation.Applications);
        Assert.False(notice.IsVisible);
    }

    [AvaloniaFact]
    public async Task PermissionActionsAreExplicitLocalizedAndDoNotEnableUnimplementedInput()
    {
        var permissions = new TestPermissionService();
        using var fixture = await Fixture.CreateAsync(permissions: permissions);
        Assert.Equal(1, permissions.Checks);
        Assert.Equal(0, permissions.Requests);
        Assert.Equal(0, permissions.SettingsOpened);
        Assert.True(fixture.Window.FindControl<WrapPanel>("PermissionActionsPanel")!.IsVisible);
        Assert.True(fixture.Window.FindControl<Button>("RequestAccessibilityButton")!.IsEnabled);
        await fixture.Window.RefreshPlatformPermissionsAsync(MainWindow.PermissionAction.RequestAccessibility);
        Assert.Equal(1, permissions.Requests);
        Assert.Contains("granted", fixture.Window.FindControl<TextBlock>("NativePermissionStatusText")!.Text);
        Assert.False(fixture.Window.FindControl<ToggleSwitch>("EnabledCheckBox")!.IsEnabled);
        await fixture.Window.RefreshPlatformPermissionsAsync(MainWindow.PermissionAction.OpenSettings);
        Assert.Equal(1, permissions.SettingsOpened);
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Assert.Equal("请求授权", fixture.Window.FindControl<Button>("RequestAccessibilityButton")!.Content);
        Assert.Contains("已授权", fixture.Window.FindControl<TextBlock>("NativePermissionStatusText")!.Text);
        Assert.DoesNotContain("test-key", fixture.Window.CreatePlatformDiagnostics());
        fixture.Window.ShutdownServices();
        Assert.True(permissions.Disposed);
    }

    [AvaloniaFact]
    public async Task StalePermissionResponsesCannotReplaceFreshStateOrUpdateAfterShutdown()
    {
        var permissions = new TestPermissionService();
        using var fixture = await Fixture.CreateAsync(permissions: permissions);
        var delayed = new TaskCompletionSource<PlatformPermissionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        permissions.Next = delayed.Task;
        var stale = fixture.Window.RefreshPlatformPermissionsAsync();
        await fixture.Window.RefreshPlatformPermissionsAsync();
        var current = fixture.Window.PermissionStatus;
        delayed.SetResult(new(NativeServiceState.Missing, default));
        await stale;
        Assert.Equal(current, fixture.Window.PermissionStatus);
        var afterClose = new TaskCompletionSource<PlatformPermissionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        permissions.Next = afterClose.Task;
        var pending = fixture.Window.RefreshPlatformPermissionsAsync();
        fixture.Window.ShutdownServices();
        afterClose.SetResult(new(NativeServiceState.Timeout, default));
        await pending;
        Assert.Equal(current, fixture.Window.PermissionStatus);
    }

    [AvaloniaFact]
    public async Task FutureSettingsDisableSavingWithoutChangingTheFileOrCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yita-future-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        const string contents = "{\"schemaVersion\":99,\"privateFutureData\":\"preserve\"}";
        await File.WriteAllTextAsync(path, contents);
        var secrets = new MemorySecretStore();
        await secrets.SaveApiKeyAsync("existing-private-key");
        var window = new MainWindow(null, new JsonSettingsStore(path), secrets);
        try
        {
            window.Show();
            await window.Initialization;
            Assert.False(window.FindControl<Button>("SaveSettingsButton")!.IsEnabled);
            Assert.True(window.FindControl<TextBlock>("SettingsStatusText")!.IsVisible);
            Assert.False(window.IsSelectionTranslationEnabled);
            window.FindControl<TextBox>("ApiKeyPasswordBox")!.Text = "replacement-private-key";
            Click(window.FindControl<Button>("SaveSettingsButton")!);
            window.ToggleEnabledFromTray();
            await Task.Delay(30);
            Assert.Equal(contents, await File.ReadAllTextAsync(path));
            Assert.Equal("existing-private-key", await secrets.ReadApiKeyAsync());
            Assert.DoesNotContain("private", window.CreatePlatformDiagnostics());
            Assert.DoesNotContain(directory, window.CreatePlatformDiagnostics());
            Click(window.FindControl<Button>("UiLanguageButton")!);
            Assert.Contains("更高版本", window.FindControl<TextBlock>("SettingsStatusText")!.Text);
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task MissingInputServiceIsExplainedAndItsControlsAreDisabled()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True(fixture.Window.FindControl<TextBlock>("PlatformStatusText")!.IsVisible);
        Assert.False(fixture.Window.FindControl<ToggleSwitch>("EnabledCheckBox")!.IsEnabled);
        Assert.False(fixture.Window.FindControl<ToggleSwitch>("WpsPdfCompatibilityCheckBox")!.IsEnabled);
        Assert.Contains("not implemented", fixture.Window.CreatePlatformDiagnostics());
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Assert.Contains("自动划词", fixture.Window.FindControl<TextBlock>("PlatformStatusText")!.Text);
    }

    [AvaloniaFact]
    public async Task FutureSettingsReplacedAfterLoadingAreDetectedBeforeSavingCredentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yita-settings-recheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(YitaSettings.Default);
        var secrets = new MemorySecretStore();
        await secrets.SaveApiKeyAsync("existing-key");
        var window = new MainWindow(null, store, secrets);
        try
        {
            window.Show();
            await window.Initialization;
            const string future = "{\"schemaVersion\":99}";
            await File.WriteAllTextAsync(path, future);
            window.FindControl<TextBox>("ApiKeyPasswordBox")!.Text = "replacement-key";
            Click(window.FindControl<Button>("SaveSettingsButton")!);
            await UntilAsync(() => !window.FindControl<Button>("SaveSettingsButton")!.IsEnabled);
            Assert.Equal("existing-key", await secrets.ReadApiKeyAsync());
            Assert.Equal(future, await File.ReadAllTextAsync(path));
            Assert.True(window.FindControl<TextBlock>("SettingsStatusText")!.IsVisible);
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task PlatformEventsReachTheSharedWindowFromABackgroundThreadAndDetachOnShutdown()
    {
        using var runtime = new TestSelectionRuntime();
        using var fixture = await Fixture.CreateAsync(runtime);
        Assert.Equal(1, runtime.SelectionSubscribers);
        Assert.Equal(1, runtime.PointerSubscribers);
        Assert.True(runtime.Enabled);
        await Task.Run(() => runtime.Capture("selected source"));
        await UntilAsync(() => fixture.Window.TranslationPopups.Any(popup => popup.IsVisible));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        await UntilAsync(() => ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!) == "translated");
        await Task.Run(runtime.PointerPressed);
        await UntilAsync(() => !popup.IsVisible);
        runtime.Capture("queued before shutdown");
        fixture.Window.ShutdownServices();
        Assert.Equal(0, runtime.SelectionSubscribers);
        Assert.Equal(0, runtime.PointerSubscribers);
        runtime.Capture("after shutdown");
        await Task.Delay(30);
        Assert.Empty(fixture.Window.TranslationPopups);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [AvaloniaFact]
    public async Task ManualClipboardTranslationWorksWithoutNativeInputWhileAutomaticSelectionIsPaused()
    {
        using var fixture = await Fixture.CreateAsync(enabled: false);
        await fixture.Window.Clipboard!.SetTextAsync("manual source");
        fixture.Window.TranslateClipboardFromTray();
        await UntilAsync(() => fixture.Window.TranslationPopups.Any());
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        await UntilAsync(() => ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!) == "translated");
        popup.FindControl<Button>("OriginalButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("manual source", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
        Assert.False(fixture.Window.IsSelectionTranslationEnabled);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [AvaloniaFact]
    public async Task UnsupportedStartupIsDisabledAndSavingPreferencesDoesNotEnableIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var startup = fixture.Window.FindControl<ToggleSwitch>("StartWithWindowsCheckBox")!;
        Assert.False(startup.IsEnabled);
        startup.IsChecked = true;
        Click(fixture.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Tag?.ToString() == "loc:SaveSettings"));
        await UntilAsync(() => !fixture.Window.IsVisible);
        Assert.False(fixture.Window.SavedSettings.StartWithSystem);
    }

    [AvaloniaFact]
    public async Task StartupAndTrayCommandsUseInjectedPlatformServices()
    {
        using var runtime = new TestSelectionRuntime();
        var startup = new TestStartupRegistration();
        using var fixture = await Fixture.CreateAsync(runtime, startup);
        fixture.Window.FindControl<ToggleSwitch>("StartWithWindowsCheckBox")!.IsChecked = true;
        Click(fixture.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Tag?.ToString() == "loc:SaveSettings"));
        await UntilAsync(() => !fixture.Window.IsVisible);
        Assert.True(startup.Enabled);
        Assert.True(fixture.Window.SavedSettings.StartWithSystem);
        var icon = new TestStatusIcon();
        var settingsOpened = 0;
        using var tray = new YitaTrayController(() => settingsOpened++, () => { }, () => { }, true, runtime,
            createStatusIcon: _ => icon);
        await Task.Run(icon.Open);
        await UntilAsync(() => settingsOpened == 1);
        await Task.Run(icon.Menu);
        await UntilAsync(() => tray.MenuWindow is { IsVisible: true });
        var command = tray.MenuWindow!.GetLogicalDescendants().OfType<Button>()
            .First(button => button.Content is Grid grid && grid.Children.OfType<TextBlock>().Any(text => text.Text?.StartsWith("Translate clipboard") == true));
        Click(command);
        Assert.Equal(1, runtime.ClipboardRequests);
        tray.Dispose();
        Assert.True(icon.Disposed);
        icon.Open();
        await Task.Delay(30);
        Assert.Equal(1, settingsOpened);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task NativeMenuCommandsLocalizeAndCannotRunAfterDisposal()
    {
        using var runtime = new TestSelectionRuntime();
        var settings = 0;
        var toggles = 0;
        var diagnostics = 0;
        var about = 0;
        var exits = 0;
        using var tray = new YitaTrayController(() => settings++, () => toggles++, () => exits++, true,
            runtime, () => diagnostics++, () => about++, createIcon: false);
        var items = tray.NativeMenu.Items.OfType<NativeMenuItem>().ToArray();
        void Invoke(string header) => ((INativeMenuItemExporterEventsImplBridge)items.Single(item => item.Header == header)).RaiseClicked();
        Invoke("Translate clipboard");
        Invoke("Settings…");
        Invoke("Enable selection translation");
        Invoke("Repair input capture");
        await UntilAsync(() => runtime.Repairs == 1);
        Invoke("Copy performance diagnostics");
        Invoke("About Yita");
        Assert.Equal(1, runtime.ClipboardRequests);
        Assert.Equal((1, 1, 1, 1), (settings, toggles, diagnostics, about));
        tray.SetEnabled(false);
        Assert.False(items.Single(item => item.ToggleType == NativeMenuItemToggleType.CheckBox).IsChecked);
        tray.ApplyUiLanguage("zh-CN");
        Assert.Contains(items, item => item.Header == "修复划词捕获");
        Assert.DoesNotContain(items, item => item.Header == "Settings…");
        Invoke("退出");
        Assert.Equal(1, exits);
        tray.Dispose();
        Invoke("设置…");
        Invoke("退出");
        Assert.Equal(1, settings);
        Assert.Equal(1, exits);
    }

    [AvaloniaFact]
    public async Task StartupFailureDoesNotPersistAnEnabledPreferenceOrReplaceCredentials()
    {
        var startup = new TestStartupRegistration { FailOnEnable = true };
        using var fixture = await Fixture.CreateAsync(startup: startup);
        fixture.Window.FindControl<ToggleSwitch>("StartWithWindowsCheckBox")!.IsChecked = true;
        fixture.Window.FindControl<TextBox>("ApiKeyPasswordBox")!.Text = "replacement-key";
        Click(fixture.Window.FindControl<Button>("SaveSettingsButton")!);
        await UntilAsync(() => fixture.Window.OwnedWindows.Count > 0);
        Assert.False(fixture.Window.SavedSettings.StartWithSystem);
        Assert.Equal("test-key", await fixture.Secrets.ReadApiKeyAsync());
        Assert.Equal(new[] { true }, startup.Applied);
        foreach (var dialog in fixture.Window.OwnedWindows.ToArray()) dialog.Close();
    }

    [AvaloniaFact]
    public async Task FailedCredentialSaveRestoresThePreviousLoginRegistration()
    {
        var startup = new TestStartupRegistration();
        var secrets = new TestSecretStore();
        using var fixture = await Fixture.CreateAsync(startup: startup, secrets: secrets);
        secrets.FailSaving = true;
        fixture.Window.FindControl<ToggleSwitch>("StartWithWindowsCheckBox")!.IsChecked = true;
        Click(fixture.Window.FindControl<Button>("SaveSettingsButton")!);
        await UntilAsync(() => fixture.Window.OwnedWindows.Count > 0);
        Assert.False(fixture.Window.SavedSettings.StartWithSystem);
        Assert.False(startup.Enabled);
        Assert.Equal(new[] { true, false }, startup.Applied);
        foreach (var dialog in fixture.Window.OwnedWindows.ToArray()) dialog.Close();
    }

    [AvaloniaFact]
    public async Task SuspensionHidesPinnedWindowsDismissesOthersAndResumesWithoutReplayingSelection()
    {
        using var runtime = new TestSelectionRuntime();
        using var fixture = await Fixture.CreateAsync(runtime);
        await fixture.Window.ShowSelectionTranslationAsync(new(SelectionTrigger.MouseGesture, new(100, 150)),
            new("pinned selection", SelectionSource.Accessibility));
        var pinned = Assert.Single(fixture.Window.TranslationPopups);
        pinned.FindControl<Avalonia.Controls.Primitives.ToggleButton>("PinButton")!.IsChecked = true;
        await fixture.Window.ShowSelectionTranslationAsync(new(SelectionTrigger.MouseGesture, new(200, 150)),
            new("temporary selection", SelectionSource.Accessibility));
        var temporary = fixture.Window.TranslationPopups.Single(popup => popup != pinned);
        var position = pinned.Position;
        await Task.Run(() => runtime.SetSessionActive(false));
        await UntilAsync(() => !pinned.IsVisible && !temporary.IsVisible);
        runtime.Capture("stale selection");
        await Task.Delay(30);
        Assert.Equal(2, fixture.Handler.Calls);
        await Task.Run(() => runtime.SetSessionActive(true));
        await UntilAsync(() => pinned.IsVisible);
        Assert.True(pinned.IsPinned);
        Assert.False(temporary.IsVisible);
        Assert.Equal(position, pinned.Position);
        Assert.Equal(2, fixture.Handler.Calls);
        fixture.Window.ShutdownServices();
        Assert.Equal(0, runtime.SessionSubscribers);
        runtime.SetSessionActive(true);
        Assert.False(pinned.IsVisible);
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private class TestSelectionRuntime : ISelectionRuntime, IDesktopSessionRuntime
    {
        public event EventHandler<SelectionCapturedEventArgs>? SelectionCaptured;
        public event EventHandler<ScreenPoint>? ExternalPointerPressed;
        public event EventHandler<bool>? SessionActivityChanged;
        public bool IsSessionActive { get; private set; } = true;
        public int SessionSubscribers => SessionActivityChanged?.GetInvocationList().Length ?? 0;
        public void SetSessionActive(bool active) { IsSessionActive = active; SessionActivityChanged?.Invoke(this, active); }
        public int SelectionSubscribers => SelectionCaptured?.GetInvocationList().Length ?? 0;
        public int PointerSubscribers => ExternalPointerPressed?.GetInvocationList().Length ?? 0;
        public bool Enabled { get; private set; }
        public virtual bool IsRunning => true;
        public bool IsHotkeyRunning => true;
        public int ClipboardRequests { get; private set; }
        public int Repairs;
        public void Start() { }
        public void Configure(bool isEnabled, bool useClipboardFallback, int selectionDelayMilliseconds,
            bool useWpsPdfCompatibility = true, bool useSelectionContext = false) => Enabled = isEnabled;
        public void TranslateClipboard() => ClipboardRequests++;
        public void RepairInputCapture() => Interlocked.Increment(ref Repairs);
        public string CreateDiagnostics(bool chinese) => "Test input service";
        public void Capture(string text) => SelectionCaptured?.Invoke(this, new SelectionCapturedEventArgs(
            new SelectionRequest(SelectionTrigger.MouseGesture, new ScreenPoint(100, 150)),
            new SelectionResult(text, SelectionSource.Accessibility)));
        public void PointerPressed() => ExternalPointerPressed?.Invoke(this, new ScreenPoint(400, 500));
        public virtual void Dispose() { }
    }

    private sealed class TestPermissionRuntime : TestSelectionRuntime, IPlatformPermissionService
    {
        public TestPermissionService Permissions { get; } = new() { GlobalInput = true };
        public override bool IsRunning => Permissions.InputRequests > 0;
        public Task<PermissionState> GetStateAsync(CancellationToken cancellationToken = default) => Permissions.GetStateAsync(cancellationToken);
        public Task<PlatformPermissionStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Permissions.GetStatusAsync(cancellationToken);
        public Task RequestAccessibilityPermissionAsync(CancellationToken cancellationToken = default) => Permissions.RequestAccessibilityPermissionAsync(cancellationToken);
        public Task OpenAccessibilitySettingsAsync(CancellationToken cancellationToken = default) => Permissions.OpenAccessibilitySettingsAsync(cancellationToken);
        public Task RequestInputMonitoringPermissionAsync(CancellationToken cancellationToken = default) => Permissions.RequestInputMonitoringPermissionAsync(cancellationToken);
        public Task OpenInputMonitoringSettingsAsync(CancellationToken cancellationToken = default) => Permissions.OpenInputMonitoringSettingsAsync(cancellationToken);
        public override void Dispose() => Permissions.Dispose();
    }

    private sealed class TestStartupRegistration : IStartupRegistration
    {
        public bool FailOnEnable { get; init; }
        public List<bool> Applied { get; } = new();
        public bool IsSupported => true;
        public bool Enabled { get; private set; }
        public void Apply(bool enabled)
        {
            Applied.Add(enabled);
            if (enabled && FailOnEnable) throw new IOException("Startup fixture unavailable.");
            Enabled = enabled;
        }
    }

    private sealed class TestSecretStore : ISecretStore
    {
        private readonly MemorySecretStore _inner = new();
        public bool FailSaving { get; set; }
        public Task<string?> ReadApiKeyAsync(CancellationToken cancellationToken = default) => _inner.ReadApiKeyAsync(cancellationToken);
        public Task SaveApiKeyAsync(string value, CancellationToken cancellationToken = default) =>
            FailSaving ? throw new IOException("Credential fixture unavailable.") : _inner.SaveApiKeyAsync(value, cancellationToken);
    }

    private sealed class TestPermissionService : IPlatformPermissionService, IDisposable
    {
        public int Checks { get; private set; }
        public int Requests { get; private set; }
        public int SettingsOpened { get; private set; }
        public int InputRequests { get; private set; }
        public int InputSettingsOpened { get; private set; }
        public bool GlobalInput { get; init; }
        public bool Disposed { get; private set; }
        public Task<PlatformPermissionStatus>? Next { get; set; }
        public Task<PermissionState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PermissionState(Requests > 0, false, false));
        public Task<PlatformPermissionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            if (Next is { } next) { Next = null; return next; }
            return Task.FromResult(new PlatformPermissionStatus(NativeServiceState.Available, new(Requests > 0, InputRequests > 0, false), GlobalInputSupported: GlobalInput));
        }
        public Task RequestAccessibilityPermissionAsync(CancellationToken cancellationToken = default)
        { Requests++; return Task.CompletedTask; }
        public Task OpenAccessibilitySettingsAsync(CancellationToken cancellationToken = default)
        { SettingsOpened++; return Task.CompletedTask; }
        public Task RequestInputMonitoringPermissionAsync(CancellationToken cancellationToken = default)
        { InputRequests++; return Task.CompletedTask; }
        public Task OpenInputMonitoringSettingsAsync(CancellationToken cancellationToken = default)
        { InputSettingsOpened++; return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }

    private sealed class TestStatusIcon : IStatusIcon
    {
        public event EventHandler<ScreenPoint>? MenuRequested;
        public event EventHandler? OpenRequested;
        public bool IsRunning => !Disposed;
        public bool Disposed { get; private set; }
        public void Open() => OpenRequested?.Invoke(this, EventArgs.Empty);
        public void Menu() => MenuRequested?.Invoke(this, new ScreenPoint(900, 700));
        public void Dispose() => Disposed = true;
    }

    private sealed class Fixture(MainWindow window, ResponseHandler handler, string directory, ISecretStore secrets) : IDisposable
    {
        public MainWindow Window { get; } = window;
        public ResponseHandler Handler { get; } = handler;
        public ISecretStore Secrets { get; } = secrets;
        public static async Task<Fixture> CreateAsync(ISelectionRuntime? runtime = null, IStartupRegistration? startup = null,
            bool enabled = true, IPlatformPermissionService? permissions = null, ISecretStore? secrets = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "yita-host-tests-" + Guid.NewGuid().ToString("N"));
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            await store.SaveAsync(YitaSettings.Default with { IsEnabled = enabled });
            var secret = secrets ?? new MemorySecretStore();
            await secret.SaveApiKeyAsync("test-key");
            var handler = new ResponseHandler();
            var window = new MainWindow(runtime, store, secret, httpClient: new HttpClient(handler),
                startupRegistration: startup ?? new UnsupportedStartupRegistration(), permissionService: permissions);
            window.Show();
            await window.Initialization;
            return new Fixture(window, handler, directory, secret);
        }
        public void Dispose()
        {
            Window.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"translated\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            });
        }
    }
}
