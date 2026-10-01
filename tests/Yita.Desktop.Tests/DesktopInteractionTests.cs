using System.Net;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
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
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public sealed class DesktopInteractionTests
{
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
            Assert.Equal("正常译文", text.Text);
            Assert.Equal(Color.Parse("#173F43"), ((SolidColorBrush)text.Foreground!).Color);
            Click(popup.FindControl<Button>("OriginalButton")!);
            Assert.Equal("The original sentence.", text.Text);
            popup.SetText("流式译文仍在更新");
            Assert.Equal("The original sentence.", text.Text);
            Click(popup.FindControl<Button>("TranslatedButton")!);
            Assert.Equal("流式译文仍在更新", text.Text);
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
            var close = popup.GetVisualDescendants().OfType<Button>().Single(button => ToolTip.GetTip(button)?.ToString() == "关闭翻译");
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
        Assert.Equal("new translation", popup.FindControl<SelectableTextBlock>("TranslationText")!.Text);
        handler.ReleaseFirst.TrySetResult();
        await oldRequest;
        Assert.Equal("new translation", popup.FindControl<SelectableTextBlock>("TranslationText")!.Text);
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
        Assert.Equal("old translation", first.FindControl<SelectableTextBlock>("TranslationText")!.Text);
    }

    [AvaloniaFact]
    public async Task ClosingAPopupCancelsItsAnswerAndNextSelectionReopensIt()
    {
        var handler = new DelayedHandler();
        using var fixture = await Fixture.CreateAsync(handler);
        var oldRequest = fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        Click(popup.GetVisualDescendants().OfType<Button>()
            .Single(button => ToolTip.GetTip(button)?.ToString() == "关闭翻译"));
        Assert.False(popup.IsVisible);
        await fixture.Window.ShowSelectionTranslationAsync(Request(500), Result("second"));
        Assert.Same(popup, Assert.Single(fixture.Window.TranslationPopups));
        Assert.True(popup.IsVisible);
        handler.ReleaseFirst.TrySetResult();
        await oldRequest;
        Assert.Equal("new translation", popup.FindControl<SelectableTextBlock>("TranslationText")!.Text);
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
        Assert.Equal("new translation", nextPopup.FindControl<SelectableTextBlock>("TranslationText")!.Text);
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
        fixture.Window.FindControl<TextBox>("ApiKeyField")!.Text = "unsaved-test-key";
        fixture.Window.FindControl<TextBox>("EndpointBox")!.Text = "not-a-url";
        await fixture.Window.ShowSelectionTranslationAsync(Request(100), Result("first"));
        Assert.Equal("test-key", Assert.Single(handler.AuthorizationValues));
        var popup = Assert.Single(fixture.Window.TranslationPopups);
        Assert.Equal("old translation", popup.FindControl<SelectableTextBlock>("TranslationText")!.Text);
    }

    [AvaloniaFact]
    public async Task HistoryShowsNewestRecordAndFiltersSourceOrTranslation()
    {
        using var fixture = await Fixture.CreateAsync(new DelayedHandler(delayFirst: false));
        await fixture.History.AppendAsync(new TranslationHistoryEntry(DateTimeOffset.UtcNow.AddMinutes(-1),
            "apple", "苹果", "en", "zh"));
        await fixture.History.AppendAsync(new TranslationHistoryEntry(DateTimeOffset.UtcNow,
            "pear", "梨", "en", "zh"));
        // Use the real refresh event, awaiting its observable result.
        var refresh = fixture.Window.GetVisualDescendants().OfType<Button>().Single(button => button.Content?.ToString() == "刷新");
        Click(refresh);
        for (var index = 0; index < 100 && fixture.Window.FindControl<ListBox>("HistoryList")!.ItemCount == 0; index++)
            await Task.Delay(10);
        var list = fixture.Window.FindControl<ListBox>("HistoryList")!;
        Assert.Equal(2, list.ItemCount);
        Assert.Equal("pear", ((HistoryListItem)list.Items[0]!).Entry.SourceText);
        fixture.Window.FindControl<TextBox>("HistorySearchBox")!.Text = "苹果";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, list.ItemCount);
        list.SelectedIndex = 0;
        Assert.Equal("apple", fixture.Window.FindControl<TextBox>("HistorySourceText")!.Text);
        Assert.Equal("苹果", fixture.Window.FindControl<TextBox>("HistoryTranslatedText")!.Text);
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
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"" + text + "\"}}]}\n\ndata: [DONE]\n\n"),
            };
        }
    }
}
