using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Yita.Desktop;

namespace Yita.Desktop.Tests;

public sealed class PlatformAppearanceTests
{
    [Theory]
    [InlineData("Microsoft YaHei UI", "PingFang SC")]
    [InlineData("SimHei", "PingFang SC")]
    [InlineData("SimSun", "Songti SC")]
    [InlineData("KaiTi", "Kaiti SC")]
    [InlineData("Calibri", "Helvetica Neue")]
    [InlineData("Cambria", "Times")]
    public void MissingWindowsFontsUseAvailableMacFamilies(string savedName, string expected)
    {
        var available = new[] { "PingFang SC", "Songti SC", "Kaiti SC", "Helvetica Neue", "Times" };
        Assert.Equal(expected, DesktopFontResolver.ResolveName(savedName, DesktopFontPlatform.Mac, available.Contains));
        Assert.Equal(savedName, DesktopFontResolver.ResolveName(savedName, DesktopFontPlatform.Windows, _ => false));
    }

    [Fact]
    public void InstalledPreferencesAndBundledFontTakePriorityOverMacFallbacks()
    {
        Assert.Equal("SimHei", DesktopFontResolver.ResolveName("SimHei", DesktopFontPlatform.Mac, _ => true));
        Assert.Equal(DesktopFontResolver.BundledSans, DesktopFontResolver.ResolveName("Source Sans Pro", DesktopFontPlatform.Mac, _ => false));
        Assert.Equal("Heiti SC", DesktopFontResolver.ResolveInterface(DesktopFontPlatform.Mac, name => name == "Heiti SC"));
    }

    [AvaloniaFact]
    public void NoAvailableCandidateUsesAvaloniaDefaultFont()
    {
        Assert.Equal(FontFamily.Default.Name, DesktopFontResolver.ResolveInterface(DesktopFontPlatform.Mac, _ => false));
    }

    [AvaloniaFact]
    public async Task ReductionCancelsRapidRevealsAndRestoresVisibilityAndOriginalTransform()
    {
        ReferenceMotion.Enabled = true;
        ReferenceMotion.SetPreferences(false, true);
        var transform = new TranslateTransform(3, 4);
        var control = new Border { Opacity = .8, RenderTransform = transform, Width = 100, Height = 50 };
        var window = new Window { Content = control };
        try
        {
            window.Show();
            ReferenceMotion.Reveal(control, true);
            ReferenceMotion.Reveal(control, true);
            ReferenceMotion.Reveal(control, true);
            ReferenceMotion.SetReduceMotion(true);
            for (var attempt = 0; attempt < 100 && ReferenceMotion.ActiveCount > 0; attempt++)
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            Assert.Equal(0, ReferenceMotion.ActiveCount);
            Assert.Equal(.8, control.Opacity);
            Assert.Same(transform, control.RenderTransform);
            Assert.False(ReferenceMotion.CanAnimate);
        }
        finally { window.Close(); ReferenceMotion.SetPreferences(false, true); }
    }

    [AvaloniaFact]
    public async Task DetachingARevealedControlReleasesItsAnimation()
    {
        ReferenceMotion.Enabled = true;
        ReferenceMotion.SetPreferences(false, true);
        var control = new Border();
        var window = new Window { Content = control };
        try
        {
            window.Show();
            ReferenceMotion.Reveal(control, true);
            window.Content = null;
            for (var attempt = 0; attempt < 100 && ReferenceMotion.ActiveCount > 0; attempt++)
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            Assert.Equal(0, ReferenceMotion.ActiveCount);
            Assert.Equal(1, control.Opacity);
            Assert.Null(control.RenderTransform);
        }
        finally { window.Close(); ReferenceMotion.SetPreferences(false, true); }
    }
}
