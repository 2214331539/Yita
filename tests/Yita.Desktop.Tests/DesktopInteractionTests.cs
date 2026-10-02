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
    private static SelectionRequest Request(int x) => new(SelectionTrigger.TranslateShortcut, new ScreenPoint(x, 150));
    private static SelectionResult Result(string text) => new(text, SelectionSource.Accessibility);
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        public MainWindow Window { get; }
        public JsonlTranslationHistoryStore History { get; }
        private Fixture(MainWindow window, JsonlTranslationHistoryStore history, string directory) =>
            (Window, History, _directory) = (window, history, directory);

        public static async Task<Fixture> CreateAsync(HttpMessageHandler handler)
        {
            var directory = Path.Combine(Path.GetTempPath(), "yita-ui-tests-" + Guid.NewGuid().ToString("N"));
            var store = new JsonSettingsStore(Path.Combine(directory, "settings.json"));
            var secrets = new MemorySecretStore();
            await secrets.SaveApiKeyAsync("test-key");
            var history = new JsonlTranslationHistoryStore(Path.Combine(directory, "history.jsonl"));
            var window = new MainWindow(null, store, secrets, history, new HttpClient(handler));
            window.Show();
            await window.Initialization;
            return new Fixture(window, history, directory);
        }

        public void Dispose()
        {
            Window.Close();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private sealed class DelayedHandler(bool delayFirst = true) : HttpMessageHandler
    {
        private int _calls;
        public List<string?> AuthorizationValues { get; } = new();
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationValues.Add(request.Headers.Authorization?.Parameter);
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
