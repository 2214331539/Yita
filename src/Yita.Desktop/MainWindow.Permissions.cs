using Avalonia.Interactivity;
using Yita.Core;
using Yita.Core.Selection;

namespace Yita.Desktop;

public sealed partial class MainWindow
{
    private readonly LatestRequestController _permissionRequests = new();
    private PlatformPermissionStatus? _permissionStatus;
    internal PlatformPermissionStatus? PermissionStatus => _permissionStatus;
    internal enum PermissionAction { Check, RequestAccessibility, OpenSettings }

    private async void CheckPermissions_Click(object? sender, RoutedEventArgs e) => await RefreshPlatformPermissionsAsync();
    private async void RequestAccessibility_Click(object? sender, RoutedEventArgs e) =>
        await RefreshPlatformPermissionsAsync(PermissionAction.RequestAccessibility);
    private async void OpenPermissionSettings_Click(object? sender, RoutedEventArgs e) =>
        await RefreshPlatformPermissionsAsync(PermissionAction.OpenSettings);

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
            var status = await _permissionService.GetStatusAsync(pending.Token);
            if (!_shuttingDown && pending.IsCurrent) _permissionStatus = status;
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
    }

    private string Granted(bool value) => value ? Localize("granted", "已授权") : Localize("not granted", "未授权");
}
