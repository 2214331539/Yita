using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-parity");
        Directory.CreateDirectory(output);
        var assembly = typeof(Yita.App).Assembly;
        var application = new Yita.App();
        application.InitializeComponent();
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var type = assembly.GetType("Yita.Settings.AppSettings", true)!;
        var settings = Activator.CreateInstance(type)!;
        type.GetProperty("UiLanguage")!.SetValue(settings, "zh-CN");
        type.GetProperty("DeepSeekApiKey")!.SetValue(settings, "test-key");
        assembly.GetType("Yita.Settings.ThemeManager", true)!.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [settings]);
        var windowType = assembly.GetType("Yita.Windows.SettingsWindow", true)!;
        var constructor = windowType.GetConstructors(Members).Single();
        var parameters = constructor.GetParameters().Select(parameter => parameter.Position == 0 ? settings : parameter.DefaultValue).ToArray();
        var window = (Window)constructor.Invoke(parameters);
        var content = (FrameworkElement)window.Content;
        var logo = FindImages(content).Single();
        logo.Source = new BitmapImage(new Uri("pack://application:,,,/Yita;component/Assets/AppLogo.png"));
        var navigation = (ListBox)windowType.GetField("_settingsNavigation", Members)!.GetValue(window)!;
        var pages = new[] { "general", "ai-history", "appearance", "model" };
        for (var index = 0; index < pages.Length; index++)
        {
            navigation.SelectedIndex = index;
            windowType.GetMethod("ApplyUiLanguage", Members)!.Invoke(window, ["zh-CN"]);
            Capture(content, 964, 721, Path.Combine(output, "wpf-" + pages[index] + ".png"));
            var metrics = Descendants(content).OfType<FrameworkElement>()
                .Where(element => element.Visibility == Visibility.Visible && (!string.IsNullOrEmpty(element.Name) || element.Tag is string))
                .Select(element =>
                {
                    var point = element.TransformToAncestor(content).Transform(new Point());
                    return new { Id = string.IsNullOrEmpty(element.Name) ? element.Tag!.ToString() : element.Name,
                        Type = element.GetType().Name, X = point.X, Y = point.Y, Width = element.ActualWidth, Height = element.ActualHeight };
                });
            File.WriteAllText(Path.Combine(output, "wpf-" + pages[index] + ".json"), System.Text.Json.JsonSerializer.Serialize(metrics));
        }
        var popupType = assembly.GetType("Yita.Windows.PopupWindow", true)!;
        var popupConstructor = popupType.GetConstructors(Members).Single();
        var popupParameters = popupConstructor.GetParameters().Select(parameter => parameter.Position == 0 ? (object)1L :
            parameter.Name == "uiLanguage" ? "zh-CN" : parameter.DefaultValue).ToArray();
        var popup = (Window)popupConstructor.Invoke(popupParameters);
        try
        {
            var point = Activator.CreateInstance(assembly.GetType("Yita.Models.ScreenPoint", true)!, [300, 300]);
            var source = "Yita helps you read English wherever you work.";
            var translated = "译獭帮助你在日常工作中随时阅读英文。\n划词后，译文会显示在选区附近。你可以固定窗口、移动位置，或继续提问。";
            popupType.GetMethod("ShowTranslation", Members)!.Invoke(popup, [source, translated, "简体中文", point, "英语"]);
            popupType.GetMethod("StopTranslationReveal", Members)!.Invoke(popup, [false]);
            popupType.GetMethod("SetTranslationText", Members)!.Invoke(popup, [translated]);
            popupType.GetMethod("MarkTranslationComplete", Members)!.Invoke(popup, null);
            popup.UpdateLayout();
            Capture((FrameworkElement)popup.Content, popup.ActualWidth, popup.ActualHeight, Path.Combine(output, "wpf-popup.png"));
            File.WriteAllText(Path.Combine(output, "wpf-popup-size.json"), System.Text.Json.JsonSerializer.Serialize(new { popup.ActualWidth, popup.ActualHeight }));
        }
        finally { popup.Close(); }
        var qaType = assembly.GetType("Yita.Windows.QuestionAnswerWindow", true)!;
        var qaConstructor = qaType.GetConstructors(Members).Single();
        var qaArguments = new object[] { Guid.NewGuid(), "zh-CN", "Segoe UI", "Microsoft YaHei UI", qaConstructor.GetParameters()[4].DefaultValue! };
        var qa = (Window)qaConstructor.Invoke(qaArguments);
        qaType.GetMethod("BeginQuestion", Members)!.Invoke(qa, ["这句话是什么意思？", false]);
        qaType.GetMethod("CompleteAnswer", Members)!.Invoke(qa, ["它表示你可以在工作时随时阅读英文，并在选区附近查看译文。"]);
        Capture((FrameworkElement)qa.Content, 520, 360, Path.Combine(output, "wpf-question-answer.png"));
        Console.WriteLine("Captured four WPF settings pages, reading popup and Q&A from immutable reference code.");
        // Do not run App.OnStartup: no hooks, tray, settings writes, or API calls.
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<Image> FindImages(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Image image) yield return image;
            foreach (var nested in FindImages(child)) yield return nested;
        }
    }

    private static void Capture(FrameworkElement content, double width, double height, string path)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
