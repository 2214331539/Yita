using Avalonia.Interactivity;
using Yita.Core;
using Yita.Core.Selection;
using Yita.Native.Mac;

namespace Yita.Desktop;

public sealed partial class MainWindow
{
    private readonly LatestRequestController _permissionRequests = new();
    private PlatformPermissionStatus? _permissionStatus;
    internal PlatformPermissionStatus? PermissionStatus => _permissionStatus;
    internal enum PermissionAction { Check, RequestAccessibility, OpenSettings, RequestInputMonitoring, OpenInputSettings }

    private async void CheckPermissions_Click(object? sender, RoutedEventArgs e) => await RefreshPlatformPermissionsAsync();
    private async void RequestAccessibility_Click(object? sender, RoutedEventArgs e) =>
        await RefreshPlatformPermissionsAsync(PermissionAction.RequestAccessibility);
    private async void OpenPermissionSettings_Click(object? sender, RoutedEventArgs e) =>
        await RefreshPlatformPermissionsAsync(PermissionAction.OpenSettings);
    private async void RequestInputMonitoring_Click(object? sender, RoutedEventArgs e) =>
        await RefreshPlatformPermissionsAsync(PermissionAction.RequestInputMonitoring);
    private async void OpenInputSettings_Click(object? sender, RoutedEventArgs e) =>
        await RefreshPlatformPermissionsAsync(PermissionAction.OpenInputSettings);

    private void OnNativeInputStatusChanged(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_shuttingDown) UpdateInputAvailability(); });

    private void UpdateInputAvailability()
    {
        PlatformStatusText.IsVisible = _selectionRuntime?.IsRunning != true || NeedsSelectionPermissions;
        PlatformStatusText.Text = _selectionRuntime is null
            ? Localize("Automatic selection and global shortcuts are unavailable in this preview. Clipboard translation remains available from the menu.", "此预览版尚未提供自动划词和全局快捷键，可通过菜单手动翻译剪贴板内容。")
            : NeedsSelectionPermissions
                ? Localize("Selection translation needs Accessibility and Input Monitoring access. Grant the missing permissions in General, then check permissions. Clipboard translation remains available from the menu.", "划词翻译需要辅助功能和输入监控权限。请在“常规”中授予缺少的权限，再检查权限；仍可通过菜单翻译剪贴板。")
                : Localize("Input capture is not running. Check permissions or repair input capture from the menu.", "输入捕获尚未运行，请检查权限或通过菜单修复输入捕获。");
    }

    private bool NeedsSelectionPermissions => _permissionStatus is { Service: NativeServiceState.Available }
        && (!_permissionStatus.Permissions.Accessibility || (_permissionStatus.GlobalInputSupported && !_permissionStatus.Permissions.InputMonitoring));

    internal void UpdateMacInstallationStatus(MacApplicationLocation location)
    {
        MacInstallationStatusText.IsVisible = location is MacApplicationLocation.DiskImage
            or MacApplicationLocation.Translocated or MacApplicationLocation.OtherDirectory;
        MacInstallationStatusText.Text = location == MacApplicationLocation.DiskImage
            ? Localize("Yita is running from the disk image. Drag Yita.app to Applications, quit this copy, eject the disk image, and open Yita from Applications before granting permissions.", "Yita 正在磁盘镜像中运行。请将 Yita.app 拖入“应用程序”，退出当前副本、弹出磁盘镜像，再从“应用程序”启动并授权。")
            : Localize("Move Yita.app to Applications, quit this copy, and reopen the installed application before granting permissions.", "请将 Yita.app 移入“应用程序”，退出当前副本，再从“应用程序”启动并授权。");
    }

    internal async Task RefreshPlatformPermissionsAsync(PermissionAction action = PermissionAction.Check)
    {
        if (_shuttingDown || _permissionService is null) return;
        using var pending = _permissionRequests.Begin();
        PermissionActionsPanel.IsEnabled = false;
        try
        {
            if (action == PermissionAction.RequestAccessibility)
                await _permissionService.RequestAccessibilityPermissionAsync(pending.Token);
            else if (action == PermissionAction.OpenSettings)
                await _permissionService.OpenAccessibilitySettingsAsync(pending.Token);
            else if (action == PermissionAction.RequestInputMonitoring)
                await _permissionService.RequestInputMonitoringPermissionAsync(pending.Token);
            else if (action == PermissionAction.OpenInputSettings)
                await _permissionService.OpenInputMonitoringSettingsAsync(pending.Token);
            var status = await _permissionService.GetStatusAsync(pending.Token);
            if (!_shuttingDown && pending.IsCurrent)
            {
                var previouslyBlocked = NeedsSelectionPermissions;
                _permissionStatus = status;
                if (status.Service == NativeServiceState.Available && !NeedsSelectionPermissions
                    && (previouslyBlocked || _selectionRuntime?.IsRunning == false))
                    _selectionRuntime?.RepairInputCapture();
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!_shuttingDown && pending.IsCurrent)
                _permissionStatus = new(NativeServiceState.Unavailable, default, DiagnosticCode: "permission-check-failed");
        }
        finally
        {
            if (!_shuttingDown && pending.IsCurrent)
            {
                PermissionActionsPanel.IsEnabled = true;
                UpdatePermissionControls();
            }
        }
    }

    private void UpdatePermissionControls()
    {
        PermissionActionsPanel.IsVisible = NativePermissionStatusText.IsVisible = _permissionService is not null;
        NativePermissionStatusText.Text = _permissionStatus?.Service switch
        {
            NativeServiceState.Available => Localize("Accessibility: ", "辅助功能：")
                + Granted(_permissionStatus.Permissions.Accessibility) + Localize("; input monitoring: ", "；输入监控：")
                + Granted(_permissionStatus.Permissions.InputMonitoring),
            NativeServiceState.Missing => Localize("The permission component is missing. Rebuild or reinstall this preview.", "未找到权限组件，请重新构建或安装此预览版。"),
            NativeServiceState.Timeout => Localize("Permission check timed out. Please try again.", "权限检查超时，请重试。"),
            NativeServiceState.ProtocolMismatch => Localize("The permission component version does not match Yita.", "权限组件与 Yita 版本不匹配。"),
            NativeServiceState.RestartBackoff => Localize("The permission component repeatedly stopped. Wait briefly before retrying.", "权限组件多次停止，请稍后重试。"),
            NativeServiceState.NotSupported => Localize("Permission management is not supported on this platform.", "此平台不支持此权限管理。"),
            NativeServiceState.Unavailable => Localize("Could not check permissions. Please try again.", "无法检查权限，请重试。"),
            _ => Localize("Permissions have not been checked.", "尚未检查权限。"),
        };
        RequestAccessibilityButton.IsEnabled = _permissionStatus is { Service: NativeServiceState.Available, Permissions.Accessibility: false };
        OpenPermissionSettingsButton.IsEnabled = _permissionStatus?.Service == NativeServiceState.Available;
        RequestInputMonitoringButton.IsEnabled = _permissionStatus is { Service: NativeServiceState.Available, GlobalInputSupported: true, Permissions.InputMonitoring: false };
        OpenInputSettingsButton.IsEnabled = _permissionStatus is { Service: NativeServiceState.Available, GlobalInputSupported: true };
        UpdateInputAvailability();
    }

    private string Granted(bool value) => value ? Localize("granted", "已授权") : Localize("not granted", "未授权");
}
