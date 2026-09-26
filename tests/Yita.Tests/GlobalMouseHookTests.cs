using Yita.Hooks;

namespace Yita.Tests;

public sealed class GlobalMouseHookTests
{
    [Fact]
    public void CreateStatusReport_BeforeStart_IsPrivacySafeAndLocalized()
    {
        using var hook = new GlobalMouseHook();

        var english = hook.CreateStatusReport(useChinese: false);
        var chinese = hook.CreateStatusReport(useChinese: true);

        Assert.False(hook.IsRunning);
        Assert.Null(hook.LastMouseInputAtUtc);
        Assert.Contains("Input capture status", english);
        Assert.Contains("not received yet", english);
        Assert.Contains("no selected text, translations, or credentials", english);
        Assert.Contains("输入捕获状态", chinese);
        Assert.Contains("尚未收到", chinese);
        Assert.Contains("不包含选中文本、译文或凭据", chinese);
    }

    [Fact]
    public void NativeHook_CanBeReboundRepeatedlyWithoutLosingRunningState()
    {
        using var hook = new GlobalMouseHook();
        hook.Start();

        Parallel.For(0, 8, _ => hook.Restart());

        Assert.True(hook.IsRunning);
        hook.Dispose();
        Assert.False(hook.IsRunning);
        Assert.Throws<ObjectDisposedException>(hook.Restart);
    }
}
