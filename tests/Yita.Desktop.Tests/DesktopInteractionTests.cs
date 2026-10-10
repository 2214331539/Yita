using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yita.Core.Settings;
using Yita.Core.Selection;
using Yita.Core.Translation;
using Yita.Desktop;

[assembly: AvaloniaTestApplication(typeof(Yita.Desktop.Tests.TestAppBuilder))]

namespace Yita.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class DesktopInteractionTests
{
    [AvaloniaFact]
    public async Task MotionPreviewCancelAndSaveRespectTheSystemPreference()
    {
        ReferenceMotion.Enabled = true;
        ReferenceMotion.SetPreferences(false, true);
        try
        {
            using var fixture = await Fixture.CreateAsync(new DelayedHandler(delayFirst: false));
            await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
            var popup = Assert.Single(fixture.Window.TranslationPopups);
            var toggle = fixture.Window.FindControl<ToggleSwitch>("ReduceMotionSwitch")!;
            toggle.IsChecked = true;
            Assert.False(ReferenceMotion.CanAnimate);
            Assert.Equal(TimeSpan.Zero, popup.Resources["SwitchMotionDuration"]);
            fixture.Window.FindControl<ListBox>("SettingsNavigation")!.SelectedIndex = 2;
            fixture.Window.UpdateLayout();
            var scroll = fixture.Window.FindControl<ScrollViewer>("SettingsScrollViewer")!;
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            ReferenceLayoutTests.Capture(fixture.Window, "avalonia-motion-preference");
            Click(fixture.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Tag?.ToString() == "loc:Cancel"));
            Assert.False(toggle.IsChecked);
            Assert.False(fixture.Window.SavedSettings.ReduceMotion);
            Assert.True(ReferenceMotion.CanAnimate);
            ReferenceMotion.SetSystemAnimations(false);
            fixture.Window.RefreshMotionResources();
            Assert.False(ReferenceMotion.CanAnimate);
            Assert.Equal(TimeSpan.Zero, popup.Resources["PressMotionDuration"]);
            ReferenceMotion.SetSystemAnimations(true);
            fixture.Window.Show();
            toggle.IsChecked = true;
            Click(fixture.Window.FindControl<Button>("SaveSettingsButton")!);
            for (var attempt = 0; attempt < 100 && fixture.Window.IsVisible; attempt++) await Task.Delay(10);
            Assert.False(fixture.Window.IsVisible);
            Assert.True(fixture.Window.SavedSettings.ReduceMotion);
            Assert.True((await fixture.Store.LoadAsync()).ReduceMotion);
        }
        finally { ReferenceMotion.SetPreferences(false, true); }
    }

    [AvaloniaFact]
    public async Task FailedAutomaticReadsOnlyUpdateSanitizedStatusAndNeverCallTheProvider()
    {
        var handler = new DelayedHandler(delayFirst: false);
        using var fixture = await Fixture.CreateAsync(handler);
        var automatic = new SelectionRequest(SelectionTrigger.MouseGesture, new(100, 150), "private-app",
            ForegroundProcessId: 987654);
        await fixture.Window.ShowSelectionTranslationAsync(automatic,
            SelectionResult.Failed(SelectionFailureKind.PermissionDenied, "private diagnostic"), TimeSpan.FromMilliseconds(25));
        Assert.Empty(fixture.Window.TranslationPopups);
        Assert.Empty(handler.AuthorizationValues);
        var status = fixture.Window.FindControl<TextBlock>("SelectionStatusText")!;
        Assert.True(status.IsVisible);
        Assert.Contains("denied", status.Text);
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Assert.Contains("读取被拒绝", status.Text);
        ReferenceMotion.SetReduceMotion(true);
        try
        {
            for (var attempt = 0; attempt < 100 && ReferenceMotion.ActiveCount > 0; attempt++)
            { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            Assert.Equal(1, fixture.Window.FindControl<Grid>("SettingsRoot")!.Opacity);
            fixture.Window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            ReferenceLayoutTests.Capture(fixture.Window, "avalonia-selection-failure");
        }
        finally { ReferenceMotion.SetReduceMotion(false); }
        var report = fixture.Window.SelectionDiagnostics.CreateReport(false);
        Assert.DoesNotContain("private", report);
        Assert.DoesNotContain("987654", report);
        await fixture.Window.ShowSelectionTranslationAsync(automatic, SelectionResult.Failed(SelectionFailureKind.Empty));
        Assert.True(status.IsVisible);
        await fixture.Window.ShowSelectionTranslationAsync(automatic, Result("next"), TimeSpan.FromMilliseconds(15));
        Assert.False(status.IsVisible);
        Assert.Single(handler.AuthorizationValues);
    }

    [AvaloniaFact]
    public async Task ManualFailureShowsLocalizedReasonAndCancelledReadStaysQuiet()
    {
        var handler = new DelayedHandler(delayFirst: false);
        using var fixture = await Fixture.CreateAsync(handler);
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), SelectionResult.Failed(SelectionFailureKind.Cancelled));
        Assert.Empty(fixture.Window.TranslationPopups);
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), SelectionResult.Failed(SelectionFailureKind.Timeout));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        Assert.Contains("取词超时", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Assert.Contains("timed out", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
        Assert.Empty(handler.AuthorizationValues);
    }

    [AvaloniaFact]
    public void TrayMenuMeasuresItsCommandsAndRefreshesLanguageWhileOpen()
    {
        using var tray = new YitaTrayController(() => { }, () => { }, () => { }, true, createIcon: false);
        tray.ApplyUiLanguage("en");
        tray.ShowMenu(new ScreenPoint(900, 700));
        var menu = tray.MenuWindow!;
        menu.UpdateLayout();
        Assert.True(menu.IsVisible);
        Assert.True(menu.Width >= 260);
        Assert.True(menu.Height > 200);
        Assert.Contains("Settings…", Labels());
        tray.ApplyUiLanguage("zh-CN");
        menu.UpdateLayout();
        Assert.Same(menu, tray.MenuWindow);
        Assert.Contains("设置…", Labels());
        Assert.DoesNotContain("Settings…", Labels());
        tray.SetEnabled(false);
        Assert.Contains("Yita · 已暂停", Labels());
        string[] Labels() => menu.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
    }
    [AvaloniaFact]
    public async Task LanguagePreviewSaveAndCancelSynchronizeExistingAndFuturePopups()
    {
        using var fixture = await Fixture.CreateAsync(new DelayedHandler(delayFirst: false));
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        popup.FindControl<ToggleButton>("PinButton")!.IsChecked = true;
        var text = ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!);
        var languages = new List<string>();
        fixture.Window.UiLanguageChanged += (_, language) => languages.Add(language);
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Assert.Equal("zh-CN", Assert.Single(languages));
        Assert.Equal("原文", popup.FindControl<Button>("OriginalButton")!.Content);
        Assert.Equal(text, ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second"));
        var nextPopup = fixture.Window.TranslationPopups.Single(window => !ReferenceEquals(window, popup));
        Assert.Equal("译文", nextPopup.FindControl<Button>("TranslatedButton")!.Content);
        Click(fixture.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Tag?.ToString() == "loc:Cancel"));
        Assert.Equal("en", languages.Last());
        Assert.Equal("Source", popup.FindControl<Button>("OriginalButton")!.Content);
        Assert.Equal("Translation", nextPopup.FindControl<Button>("TranslatedButton")!.Content);
        fixture.Window.Show();
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Click(fixture.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Tag?.ToString() == "loc:SaveSettings"));
        for (var attempt = 0; attempt < 100 && fixture.Window.IsVisible; attempt++) await Task.Delay(10);
        Assert.False(fixture.Window.IsVisible);
        Assert.Equal("zh-CN", fixture.Window.SavedSettings.UiLanguage);
        Assert.Equal("原文", popup.FindControl<Button>("OriginalButton")!.Content);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void TrayMenuFitsAboveTheBottomRightAnchorAtDifferentDpi(double scale)
    {
        var area = new PixelRect(-1920, 0, 1920, 1040);
        var anchor = new ScreenPoint(-40, 1070);
        var size = new Size(280, 320);
        var position = YitaTrayController.ResolveMenuPosition(anchor, size, area, scale);
        Assert.InRange(position.X, area.X, area.Right - (int)Math.Ceiling(size.Width * scale));
        Assert.InRange(position.Y, area.Y, area.Bottom - (int)Math.Ceiling(size.Height * scale));
        Assert.True(position.Y + size.Height * scale < anchor.Y);
        var topPosition = YitaTrayController.ResolveMenuPosition(new ScreenPoint(-1880, 0), size, area, scale);
        Assert.True(topPosition.Y > 0);
    }
    [AvaloniaFact]
    public async Task ConnectionTestAlwaysReachesTheProviderAndUsesTheCurrentUnsavedKey()
    {
        var handler = new DelayedHandler(delayFirst: false);
        using var fixture = await Fixture.CreateAsync(handler);
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("hello"));
        var test = fixture.Window.FindControl<Button>("TestConnectionButton")!;
        Click(test);
        for (var attempt = 0; attempt < 100 && !test.IsEnabled; attempt++) await Task.Delay(10);
        fixture.Window.FindControl<TextBox>("ApiKeyPasswordBox")!.Text = "new-key";
        Click(test);
        for (var attempt = 0; attempt < 100 && !test.IsEnabled; attempt++) await Task.Delay(10);
        Assert.Equal(3, handler.AuthorizationValues.Count);
        Assert.Equal("new-key", handler.AuthorizationValues[^1]);
    }

    [AvaloniaFact]
    public async Task ExternalClicksDismissOnlyUnpinnedWindows()
    {
        using var fixture = await Fixture.CreateAsync(new DelayedHandler(delayFirst: false));
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        var pinned = Assert.Single(fixture.Window.TranslationPopups);
        pinned.FindControl<ToggleButton>("PinButton")!.IsChecked = true;
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second"));
        fixture.Window.DismissUnpinnedWindows();
        Assert.True(pinned.IsVisible);
        Assert.Single(fixture.Window.TranslationPopups, window => window.IsVisible);
    }

    [AvaloniaFact]
    public void OriginalAndTranslationRemainSelectableAndErrorColorIsReset()
    {
        var popup = new TranslationPopupWindow();
        try
        {
            popup.BeginTranslation("The original sentence.");
            popup.SetError("Connection failed");
            popup.SetText("正常译文");
            var text = popup.FindControl<SelectableTextBlock>("TranslationText")!;
            Assert.Equal("正常译文", ReferenceTypography.GetText(text));
            Assert.Equal(Color.Parse("#302D29"), ((SolidColorBrush)text.Foreground!).Color);
            Click(popup.FindControl<Button>("OriginalButton")!);
            Assert.Equal("The original sentence.", ReferenceTypography.GetText(text));
            popup.SetText("流式译文仍在更新");
            Assert.Equal("The original sentence.", ReferenceTypography.GetText(text));
            Click(popup.FindControl<Button>("TranslatedButton")!);
            Assert.Equal("流式译文仍在更新", ReferenceTypography.GetText(text));
        }
        finally { popup.Close(); }
    }

    [AvaloniaFact]
    public void LongTranslationStaysInsideScrollableReadingAreaAndWindowCanBeReopened()
    {
        var popup = new TranslationPopupWindow();
        try
        {
            popup.Show();
            popup.BeginTranslation("source");
            popup.SetText(string.Concat(Enumerable.Repeat("这是一段较长的译文，用于检查窗口正文是否完整可见。\n", 60)));
            popup.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(popup.Height, popup.MinHeight, popup.MaxHeight);
            var scroll = popup.FindControl<ScrollViewer>("ReadingScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var close = popup.FindControl<Button>("CloseButton")!;
            Click(close);
            Assert.False(popup.IsVisible);
            popup.Show();
            popup.BeginTranslation("next");
            popup.SetText("短译文");
            popup.UpdateLayout();
            Assert.True(popup.Height < popup.MaxHeight);
        }
        finally { popup.Close(); }
    }

    [AvaloniaFact]
    public async Task PopupLanguageSwitchRetranslatesMixedTextAndPersistsOnlyTheDefaultLanguage()
    {
        var handler = new DelayedHandler(delayFirst: false);
        using var fixture = await Fixture.CreateAsync(handler);
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("请 review this sentence"));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        var languages = popup.FindControl<ComboBox>("PopupTargetLanguageComboBox")!;
        Assert.Equal("自动判断", ((ComboBoxItem)languages.SelectedItem!).Tag);
        Assert.Contains("to 简体中文", Prompt(0));
        fixture.Window.FindControl<TextBox>("ApiKeyPasswordBox")!.Text = "unsaved-key";
        fixture.Window.FindControl<TextBox>("EndpointTextBox")!.Text = "unsaved-endpoint";
        Select(languages, "英语");
        await UntilAsync(() => handler.RequestBodies.Count == 2 && popup.FindControl<Grid>("QuestionRow")!.IsVisible);
        Assert.Contains("to 英语", Prompt(1));
        var saved = await fixture.Store.LoadAsync();
        Assert.Equal("fixed", saved.TargetLanguageMode);
        Assert.Equal("英语", saved.TargetLanguage);
        Assert.Equal("https://api.deepseek.com", saved.DeepSeekEndpoint);
        Assert.All(handler.AuthorizationValues, key => Assert.Equal("test-key", key));
        Assert.Equal("英语", ((ComboBoxItem)fixture.Window.FindControl<ComboBox>("TargetLanguageComboBox")!.SelectedItem!).Tag);
        await fixture.Window.ShowSelectionTranslationAsync(Request(200), Result("another sentence"));
        Assert.Contains("to 英语", Prompt(2));
        // A newly created window uses the saved choice too.
        popup.FindControl<ToggleButton>("PinButton")!.IsChecked = true;
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("下一句 mixed English"));
        var next = fixture.Window.TranslationPopups.Single(window => window != popup);
        Assert.Equal("英语", next.TargetLanguageChoice);
        foreach (var choice in new[] { "日语", "简体中文", "自动判断" })
        {
            await fixture.Window.ChangePopupTargetLanguageAsync(next, choice);
            saved = await fixture.Store.LoadAsync();
            Assert.Equal(choice == "自动判断" ? "auto" : "fixed", saved.TargetLanguageMode);
            Assert.Equal(choice == "自动判断" ? "简体中文" : choice, saved.TargetLanguage);
            Assert.Equal(choice, next.TargetLanguageChoice);
        }
        Assert.Equal("英语", popup.TargetLanguageChoice);
        Assert.Equal("unsaved-endpoint", fixture.Window.FindControl<TextBox>("EndpointTextBox")!.Text);
        string Prompt(int index) => System.Text.Json.JsonDocument.Parse(handler.RequestBodies[index])
            .RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
    }

    [AvaloniaFact]
    public async Task SwitchingLanguageDuringAStreamPreventsAnOldAnswerFromOverwritingTheNewOne()
    {
        var handler = new DelayedHandler();
        using var fixture = await Fixture.CreateAsync(handler);
        var original = fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("请 review this sentence"));
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        popup.FindControl<ToggleButton>("PinButton")!.IsChecked = true;
        var anchor = popup.CurrentAnchor;
        await fixture.Window.ChangePopupTargetLanguageAsync(popup, "英语");
        handler.ReleaseFirst.TrySetResult();
        await original;
        Assert.Equal("new translation", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
        Assert.Equal("英语", popup.TargetLanguageChoice);
        Assert.Equal(anchor, popup.CurrentAnchor);
        Assert.True(popup.IsPinned);
        Assert.Single(fixture.Window.TranslationPopups);
    }

    [AvaloniaFact]
    public async Task LocalizingAndApplyingSettingsDoNotRetranslateOrRelabelPinnedAnswers()
    {
        var handler = new DelayedHandler(delayFirst: false);
        using var fixture = await Fixture.CreateAsync(handler);
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("mixed 内容"));
        var first = Assert.Single(fixture.Window.TranslationPopups);
        first.FindControl<ToggleButton>("PinButton")!.IsChecked = true;
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second 内容"));
        var second = fixture.Window.TranslationPopups.Single(popup => popup != first);
        await fixture.Window.ChangePopupTargetLanguageAsync(second, "英语");
        var calls = handler.RequestBodies.Count;
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Assert.Equal("自动判断", SelectedLabel(first));
        Assert.Equal("英语", SelectedLabel(second));
        Click(fixture.Window.FindControl<Button>("UiLanguageButton")!);
        Assert.Equal("Auto", SelectedLabel(first));
        Assert.Equal("English", SelectedLabel(second));
        first.ApplySettings(fixture.Window.SavedSettings.ToOriginal());
        Assert.Equal("自动判断", first.TargetLanguageChoice);
        Assert.Equal(calls, handler.RequestBodies.Count);
        Assert.Equal("英语", (await fixture.Store.LoadAsync()).TargetLanguage);
        static object? SelectedLabel(TranslationPopupWindow popup) =>
            ((ComboBoxItem)popup.FindControl<ComboBox>("PopupTargetLanguageComboBox")!.SelectedItem!).Content;
    }

    [AvaloniaFact]
    public async Task RapidLanguageSwitchesKeepTheLatestDefaultAndPreserveTheOriginalText()
    {
        using var fixture = await Fixture.CreateAsync(new DelayedHandler(delayFirst: false));
        const string source = "  请 review\r\nthis sentence  ";
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result(source));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        var english = fixture.Window.ChangePopupTargetLanguageAsync(popup, "英语");
        var japanese = fixture.Window.ChangePopupTargetLanguageAsync(popup, "日语");
        var automatic = fixture.Window.ChangePopupTargetLanguageAsync(popup, "自动判断");
        await Task.WhenAll(english, japanese, automatic);
        Assert.Equal("自动判断", popup.TargetLanguageChoice);
        Assert.Equal("auto", (await fixture.Store.LoadAsync()).TargetLanguageMode);
        Assert.Equal("old translation", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
        Click(popup.FindControl<Button>("OriginalButton")!);
        Assert.Equal(source, ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
    }

    [AvaloniaFact]
    public async Task AFailedDefaultSaveKeepsTheRetranslationAndShowsANotice()
    {
        var handler = new DelayedHandler(delayFirst: false);
        var store = new UnwritableSettings();
        using var fixture = await Fixture.CreateAsync(handler, store);
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("mixed 内容"));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        await fixture.Window.ChangePopupTargetLanguageAsync(popup, "英语");
        Assert.Equal("new translation", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
        Assert.Equal("英语", popup.TargetLanguageChoice);
        Assert.Contains("could not save", popup.FindControl<TextBlock>("NoticeText")!.Text);
        Assert.Equal(1, store.SaveAttempts);
    }

    [AvaloniaFact]
    public async Task AReadOnlySettingsFileCannotBeOverwrittenFromThePopup()
    {
        var handler = new DelayedHandler(delayFirst: false);
        var store = new UnwritableSettings(readOnly: true);
        using var fixture = await Fixture.CreateAsync(handler, store);
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("mixed 内容"));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        var combo = popup.FindControl<ComboBox>("PopupTargetLanguageComboBox")!;
        Select(combo, "英语");
        Assert.Equal("自动判断", popup.TargetLanguageChoice);
        Assert.Equal("自动判断", ((ComboBoxItem)combo.SelectedItem!).Tag);
        Assert.Contains("read-only", popup.FindControl<TextBlock>("NoticeText")!.Text);
        Assert.Equal(0, store.SaveAttempts);
        Assert.Single(handler.RequestBodies);
    }

    [AvaloniaFact]
    public async Task ASlowPreviousSelectionCannotOverwriteTheLatestPopup()
    {
        var handler = new DelayedHandler();
        using var fixture = await Fixture.CreateAsync(handler);
        var oldRequest = fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second"));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        Assert.Equal("new translation", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
        handler.ReleaseFirst.TrySetResult();
        await oldRequest;
        Assert.Equal("new translation", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
    }

    [AvaloniaFact]
    public async Task PinningPreservesAnAnswerWhileNewSelectionsOpenAnotherWindow()
    {
        using var fixture = await Fixture.CreateAsync(new DelayedHandler(delayFirst: false));
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        var first = Assert.Single(fixture.Window.TranslationPopups);
        first.FindControl<ToggleButton>("PinButton")!.IsChecked = true;
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second"));
        Assert.Equal(2, fixture.Window.TranslationPopups.Count);
        Assert.Equal("old translation", ReferenceTypography.GetText(first.FindControl<SelectableTextBlock>("TranslationText")!));
    }

    [AvaloniaFact]
    public async Task ClosingAPopupCancelsItsAnswerAndNextSelectionReopensIt()
    {
        var handler = new DelayedHandler();
        using var fixture = await Fixture.CreateAsync(handler);
        var oldRequest = fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        Click(popup.FindControl<Button>("CloseButton")!);
        Assert.False(popup.IsVisible);
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second"));
        Assert.Same(popup, Assert.Single(fixture.Window.TranslationPopups));
        Assert.True(popup.IsVisible);
        handler.ReleaseFirst.TrySetResult();
        await oldRequest;
        Assert.Equal("new translation", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
    }

    [AvaloniaFact]
    public async Task ClosingTheNativeWindowReleasesItAndNextSelectionCreatesANewPopup()
    {
        using var fixture = await Fixture.CreateAsync(new DelayedHandler(delayFirst: false));
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        var closedPopup = Assert.Single(fixture.Window.TranslationPopups);
        closedPopup.Close();
        Assert.Empty(fixture.Window.TranslationPopups);
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second"));
        var nextPopup = Assert.Single(fixture.Window.TranslationPopups);
        Assert.NotSame(closedPopup, nextPopup);
        Assert.Equal("new translation", ReferenceTypography.GetText(nextPopup.FindControl<SelectableTextBlock>("TranslationText")!));
    }

    [AvaloniaFact]
    public async Task ShutdownCancelsActiveTranslationAndIgnoresQueuedSelections()
    {
        var handler = new DelayedHandler();
        using var fixture = await Fixture.CreateAsync(handler);
        var active = fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Window.ShutdownServices();
        handler.ReleaseFirst.TrySetResult();
        await active;
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second"));
        Assert.Empty(fixture.Window.TranslationPopups);
        Assert.Single(handler.AuthorizationValues);
    }

    [AvaloniaFact]
    public async Task UnsavedModelFieldsDoNotChangeNativeTranslationCredentials()
    {
        var handler = new DelayedHandler(delayFirst: false);
        using var fixture = await Fixture.CreateAsync(handler);
        fixture.Window.FindControl<TextBox>("ApiKeyPasswordBox")!.Text = "unsaved-test-key";
        fixture.Window.FindControl<TextBox>("EndpointTextBox")!.Text = "not-a-url";
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        Assert.Equal("test-key", Assert.Single(handler.AuthorizationValues));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        Assert.Equal("old translation", ReferenceTypography.GetText(popup.FindControl<SelectableTextBlock>("TranslationText")!));
    }

    [AvaloniaFact]
    public async Task SettingsNavigationAndCancelPreserveSavedPreferences()
    {
        using var fixture = await Fixture.CreateAsync(new DelayedHandler(delayFirst: false));
        var navigation = fixture.Window.FindControl<ListBox>("SettingsNavigation")!;
        var pages = new[] { "GeneralPage", "HistoryPage", "AppearancePage", "ModelPage" };
        for (var index = 0; index < pages.Length; index++)
        {
            navigation.SelectedIndex = index;
            for (var page = 0; page < pages.Length; page++)
                Assert.Equal(page == index, fixture.Window.FindControl<StackPanel>(pages[page])!.IsVisible);
        }
        fixture.Window.FindControl<TextBox>("EndpointTextBox")!.Text = "unsaved";
        Click(fixture.Window.GetLogicalDescendants().OfType<Button>().Single(button => button.Tag?.ToString() == "loc:Cancel"));
        Assert.Equal("https://api.deepseek.com", fixture.Window.FindControl<TextBox>("EndpointTextBox")!.Text);
        Assert.False(fixture.Window.IsVisible);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Select(ComboBox combo, string value) => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == value);
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(condition());
    }
    private static SelectionRequest Request(int x) => new(SelectionTrigger.TranslateShortcut, new ScreenPoint(x, 150));
    private static SelectionResult Result(string text) => new(text, SelectionSource.Accessibility);
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        public MainWindow Window { get; }
        public JsonlTranslationHistoryStore History { get; }
        public JsonSettingsStore Store { get; }
        private Fixture(MainWindow window, JsonlTranslationHistoryStore history, string directory, JsonSettingsStore store) =>
            (Window, History, _directory, Store) = (window, history, directory, store);

        public static async Task<Fixture> CreateAsync(HttpMessageHandler handler, ISettingsStore? injectedStore = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "yita-ui-tests-" + Guid.NewGuid().ToString("N"));
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            var secrets = new MemorySecretStore();
            await secrets.SaveApiKeyAsync("test-key");
            var history = new JsonlTranslationHistoryStore(Path.Combine(directory, "history.jsonl"));
            var window = new MainWindow(null, injectedStore ?? store, secrets, history, new HttpClient(handler));
            window.Show();
            await window.Initialization;
            return new Fixture(window, history, directory, store);
        }

        public void Dispose()
        {
            Window.Close();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class UnwritableSettings(bool readOnly = false) : ISettingsStore
    {
        public int SaveAttempts { get; private set; }
        public Task<YitaSettings> LoadAsync(CancellationToken cancellationToken = default) => readOnly
            ? Task.FromException<YitaSettings>(new SettingsStoreException(SettingsFailureKind.NewerVersion))
            : Task.FromResult(YitaSettings.Default);
        public Task SaveAsync(YitaSettings settings, CancellationToken cancellationToken = default)
        { SaveAttempts++; return Task.FromException(new IOException("Test storage unavailable.")); }
    }

    private sealed class DelayedHandler(bool delayFirst = true) : HttpMessageHandler
    {
        private int _calls;
        public List<string?> AuthorizationValues { get; } = new();
        public List<string> RequestBodies { get; } = new();
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationValues.Add(request.Headers.Authorization?.Parameter);
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var first = Interlocked.Increment(ref _calls) == 1;
            if (first && delayFirst) { FirstStarted.TrySetResult(); await ReleaseFirst.Task; }
            var text = first ? "old translation" : "new translation";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"" + text + "\"}}]}\n\ndata: [DONE]\n\n",
                    System.Text.Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
