using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yita.Core.Settings;
using Yita.Desktop;
using Yita.Core.Parity;
using Yita.Services;
using Yita.Translation;
using Avalonia.Controls.Documents;
using System.Net;
using System.Net.Http;

namespace Yita.Desktop.Tests;

public sealed class ReferenceLayoutTests
{
    [AvaloniaFact]
    public async Task ConversationRestoresTheSixPresetsAndFontSliderWithStreamingContent()
    {
        ReferenceMotion.Enabled = false;
        var settings = (YitaSettings.Default with { UiLanguage = "zh-CN" }).ToOriginal("test-key");
        using var client = new HttpClient(new ReferenceAnswerHandler());
        var conversation = new QuestionAnswerWindow(settings, "source", "translation", "", QuestionContextKind.Translation,
            new ReferenceTranslationRuntime(new InjectedTranslationProviderFactory(client)), new AiHistoryStore(),
            new AiHistoryContext(Guid.NewGuid(), DateTimeOffset.Now, "source", "translation", "英语", "简体中文"));
        try
        {
            conversation.Show();
            await conversation.AskAsync("这句话是什么意思？");
            conversation.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var presets = conversation.FindControl<WindowSizePresetBar>("SizePresetBar")!;
            Assert.Equal(6, presets.GetVisualDescendants().OfType<Button>().Count(button => button.Classes.Contains("size-preset")));
            Assert.All(presets.GetVisualDescendants().OfType<RepeatButton>(), button => Assert.True(button.Bounds.Width > 0));
            presets.TextSize = 24;
            Assert.Equal(24, conversation.FindControl<SelectableTextBlock>("TranscriptText")!.FontSize);
            presets.TextSize = 15.5;
            var transcript = conversation.FindControl<SelectableTextBlock>("TranscriptText")!;
            Assert.Equal(Avalonia.Media.FontWeight.Bold, transcript.Inlines!.OfType<Run>().First().FontWeight);
            conversation.UpdateLayout();
            var transcriptHeight = transcript.TextLayout.Height;
            transcript.SelectAll();
            Assert.Contains("它表示你可以在工作时随时阅读英文", transcript.SelectedText);
            conversation.UpdateLayout();
            Assert.Equal(transcriptHeight, transcript.TextLayout.Height);
            transcript.ClearSelection();
            conversation.ApplyUiLanguage("en");
            Assert.Equal("Ask AI", conversation.FindControl<TextBlock>("TitleText")!.Text);
            Assert.Equal("Copy", conversation.FindControl<Button>("CopyButton")!.Content);
            Assert.Contains("You", ReferenceTypography.GetText(transcript));
            Assert.Contains("它表示你可以在工作时随时阅读英文", ReferenceTypography.GetText(transcript));
            conversation.ApplyUiLanguage("zh-CN");
            Assert.Equal("AI 问答", conversation.FindControl<TextBlock>("TitleText")!.Text);
            Capture(conversation, "avalonia-question-answer");
        }
        finally { conversation.Close(); ReferenceMotion.Enabled = true; }
    }

    [AvaloniaFact]
    public async Task ManualConversationRecordingPreventsDuplicateWritesAndAllowsNewTurns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yita-conversation-" + Guid.NewGuid().ToString("N"));
        var settings = (YitaSettings.Default with { ProviderId = "mock", AiHistoryDirectory = directory }).ToOriginal();
        using var factory = new TranslationProviderFactory();
        var conversation = new QuestionAnswerWindow(settings, "source", "translation", "", QuestionContextKind.Translation,
            new ReferenceTranslationRuntime(factory), new AiHistoryStore(),
            new AiHistoryContext(Guid.NewGuid(), DateTimeOffset.Now, "source", "translation", "英语", "简体中文"));
        try
        {
            conversation.Show();
            await conversation.AskAsync("first question");
            var record = conversation.FindControl<Button>("RecordButton")!;
            record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; attempt < 100 && record.Content?.ToString() != "Saved"; attempt++) await Task.Delay(10);
            Assert.Equal("Saved", record.Content);
            Assert.False(record.IsEnabled);
            var files = Directory.GetFiles(directory, "*.md", SearchOption.AllDirectories);
            var snapshot = string.Join("", files.Select(File.ReadAllText));
            record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(snapshot, string.Join("", files.Select(File.ReadAllText)));
            await conversation.AskAsync("second question");
            Assert.True(record.IsEnabled);
            Assert.Contains("second question", ReferenceTypography.GetText(conversation.FindControl<SelectableTextBlock>("TranscriptText")!));
        }
        finally
        {
            conversation.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public void MixedScriptUsesSeparateConfiguredFontsAndKeepsSelectedTextCopyable()
    {
        var popup = new TranslationPopupWindow();
        try
        {
            popup.ApplySettings((YitaSettings.Default with { EnglishTranslationFontFamily = "Georgia", ChineseTranslationFontFamily = "SimHei" }).ToOriginal());
            popup.SetText("English 中文");
            var text = popup.FindControl<SelectableTextBlock>("TranslationText")!;
            Assert.Equal("Georgia", ((Run)text.Inlines![0]).FontFamily!.Name);
            Assert.Equal("SimHei", ((Run)text.Inlines[1]).FontFamily!.Name);
            text.SelectAll();
            Assert.Equal("English 中文", text.SelectedText);
        }
        finally { popup.Close(); }
    }
    [AvaloniaFact]
    public void FollowUpUsesExplanationContextOnlyAfterAnExplanationHasCompleted()
    {
        var popup = new TranslationPopupWindow();
        QuestionAction? submitted = null;
        popup.QuestionRequested += (_, action) => submitted = action;
        try
        {
            popup.BeginTranslation("source"); popup.SetText("translation"); popup.CompleteTranslation();
            popup.BeginExplanation();
            Submit();
            Assert.Equal(QuestionContextKind.Translation, submitted!.ContextKind);
            popup.SetExplanation("explanation"); popup.CompleteExplanation();
            Submit();
            Assert.Equal(QuestionContextKind.Explanation, submitted!.ContextKind);
            popup.FindControl<Button>("ExplanationBackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Submit();
            Assert.Equal(QuestionContextKind.Translation, submitted!.ContextKind);
        }
        finally { popup.Close(); }
        void Submit()
        {
            popup.FindControl<TextBox>("InlineQuestionTextBox")!.Text = "follow-up";
            popup.FindControl<Button>("AskButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
    [AvaloniaFact]
    public void ExplanationRecordingIsBoundToItsGenerationAndFailedSavesRemainRetryable()
    {
        var popup = new TranslationPopupWindow();
        try
        {
            popup.BeginTranslation("source");
            popup.BeginExplanation(); popup.SetExplanation("first"); popup.CompleteExplanation();
            var version = popup.BeginExplanationRecord();
            Assert.NotEqual(0, version);
            Assert.Equal(0, popup.BeginExplanationRecord());
            popup.CompleteExplanationRecord(version, false);
            Assert.Equal(version, popup.BeginExplanationRecord());
            popup.BeginExplanation(); popup.SetExplanation("second"); popup.CompleteExplanation();
            popup.CompleteExplanationRecord(version, true);
            var second = popup.BeginExplanationRecord();
            Assert.NotEqual(version, second);
            popup.CompleteExplanationRecord(second, true);
            Assert.Equal(0, popup.BeginExplanationRecord());
        }
        finally { popup.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("yita", "minimal", 2d)]
    [InlineData("ocean", "minimal", 1.5d)]
    [InlineData("yita", "bubble-v3", 1.5d)]
    [InlineData("ocean", "bubble-v3-color", 1.5d)]
    public void ThemedLongTextKeepsTheReadingSurfaceScrollableAtScaledRendering(string theme, string style, double scale)
    {
        ReferenceMotion.Enabled = false;
        var popup = new TranslationPopupWindow();
        try
        {
            popup.ApplySettings((YitaSettings.Default with { ColorTheme = theme, PopupVisualStyle = style, UiLanguage = "zh-CN" }).ToOriginal());
            popup.Show();
            popup.BeginTranslation("source");
            popup.SetText(string.Concat(Enumerable.Repeat("译獭让英文阅读更加顺畅。This is a longer paragraph for the reading window.\n", 35)));
            popup.CompleteTranslation(); popup.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var scroll = popup.FindControl<ScrollViewer>("ReadingScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(scroll.Viewport.Height > 100);
            Assert.InRange(popup.Height, popup.MinHeight, popup.MaxHeight);
            if (Environment.GetEnvironmentVariable("YITA_PARITY_CAPTURE_DIRECTORY") is { Length: > 0 } directory)
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(popup.Width * scale), (int)Math.Ceiling(popup.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(popup);
                bitmap.Save(Path.Combine(directory, $"avalonia-{theme}-{style}-scale-{scale:0.0}.png"));
            }
        }
        finally { popup.Close(); ReferenceMotion.Enabled = true; }
    }
    [AvaloniaFact]
    public async Task FourSettingsPagesRenderWithTheReferenceDimensionsAndBrandAsset()
    {
        ReferenceMotion.Enabled = false;
        var store = new InMemorySettings { Value = YitaSettings.Default with { UiLanguage = "zh-CN" } };
        var secrets = new MemorySecretStore();
        await secrets.SaveApiKeyAsync("test-key");
        var window = new MainWindow(null, store, secrets) { Width = 964, Height = 721 };
        try
        {
            window.Show();
            await window.Initialization;
            Assert.Equal("Microsoft YaHei", window.FontFamily.Name);
            Assert.NotNull(window.GetVisualDescendants().OfType<Image>().Single().Source);
            Assert.Equal("今天", window.FindControl<ComboBox>("AiSummaryRangeComboBox")!.SelectionBoxItem);
            Assert.Equal("自动检测", window.FindControl<ComboBox>("SourceLanguageComboBox")!.SelectionBoxItem);
            var navigation = window.FindControl<ListBox>("SettingsNavigation")!;
            var names = new[] { "general", "ai-history", "appearance", "model" };
            for (var index = 0; index < names.Length; index++)
            {
                navigation.SelectedIndex = index;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(964, window.ClientSize.Width);
                foreach (var field in window.GetVisualDescendants().OfType<TextBox>().Where(field => field.IsEffectivelyVisible))
                    Assert.True(field.Bounds.Width > 40, $"Clipped field: {field.Name}");
                Capture(window, "avalonia-" + names[index]);
            }
        }
        finally { window.Close(); ReferenceMotion.Enabled = true; }
    }

    [AvaloniaFact]
    public async Task SavingRestoresAllOriginalPreferenceFieldsAndKeepsSecretOutOfSettings()
    {
        var store = new InMemorySettings();
        var secrets = new MemorySecretStore();
        await secrets.SaveApiKeyAsync("test-key");
        var window = new MainWindow(null, store, secrets);
        try
        {
            window.Show(); await window.Initialization;
            window.FindControl<ToggleSwitch>("WpsPdfCompatibilityCheckBox")!.IsChecked = false;
            window.FindControl<ToggleSwitch>("UseSelectionContextCheckBox")!.IsChecked = true;
            window.FindControl<TextBox>("PersonalGlossaryTextBox")!.Text = "API => 接口";
            window.FindControl<Slider>("DefaultFontSizeSlider")!.Value = 21;
            var provider = window.FindControl<ComboBox>("ProviderComboBox")!;
            provider.SelectedIndex = 1;
            window.FindControl<Button>("UiLanguageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.GetVisualDescendants().OfType<Button>().Single(button => button.Tag?.ToString() == "loc:SaveSettings")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var attempt = 0; attempt < 100 && window.IsVisible; attempt++) await Task.Delay(10);
            Assert.False(window.IsVisible);
            Assert.False(store.Value.UseWpsPdfCompatibility);
            Assert.True(store.Value.UseSelectionContext);
            Assert.Equal("API => 接口", store.Value.PersonalGlossary);
            Assert.Equal(21, store.Value.DefaultTranslationFontSize);
            Assert.Equal("mock", store.Value.ProviderId);
            Assert.Equal("zh-CN", store.Value.UiLanguage);
            Assert.DoesNotContain("test-key", System.Text.Json.JsonSerializer.Serialize(store.Value));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PopupReadingSurfaceRendersWithoutClippingTheLastLine()
    {
        ReferenceMotion.Enabled = false;
        var popup = new TranslationPopupWindow();
        try
        {
            popup.ApplySettings((YitaSettings.Default with { UiLanguage = "zh-CN" }).ToOriginal());
            popup.Show();
            popup.BeginTranslation("Yita helps you read English wherever you work.");
            popup.SetText("译獭帮助你在日常工作中随时阅读英文。\n划词后，译文会显示在选区附近。你可以固定窗口、移动位置，或继续提问。");
            popup.CompleteTranslation(); popup.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var text = popup.FindControl<SelectableTextBlock>("TranslationText")!;
            var scroll = popup.FindControl<ScrollViewer>("ReadingScroll")!;
            Assert.True(text.Bounds.Height <= scroll.Viewport.Height);
            Capture(popup, "avalonia-popup");
        }
        finally { popup.Close(); ReferenceMotion.Enabled = true; }
    }

    internal static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("YITA_PARITY_CAPTURE_DIRECTORY") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(directory, name + ".png"));
        var metrics = window.GetVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible && (!string.IsNullOrEmpty(control.Name) || control.Tag is string))
            .Select(control =>
            {
                var point = control.TranslatePoint(default, window) ?? default;
                return new { Id = string.IsNullOrEmpty(control.Name) ? control.Tag!.ToString() : control.Name,
                    Type = control.GetType().Name, X = point.X, Y = point.Y, Width = control.Bounds.Width, Height = control.Bounds.Height };
            });
        File.WriteAllText(Path.Combine(directory, name + ".json"), System.Text.Json.JsonSerializer.Serialize(metrics));
    }

    private sealed class InMemorySettings : ISettingsStore
    {
        internal YitaSettings Value { get; set; } = YitaSettings.Default;
        public Task<YitaSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(YitaSettings settings, CancellationToken cancellationToken = default) { Value = settings; return Task.CompletedTask; }
    }
    private sealed class ReferenceAnswerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"它表示你可以在工作时随时阅读英文，并在选区附近查看译文。\"}}]}\n\ndata: [DONE]\n\n",
                    System.Text.Encoding.UTF8, "text/event-stream"),
            });
    }
}
