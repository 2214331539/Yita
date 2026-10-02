using Yita.Core.Selection;

namespace Yita.Desktop;

internal static class SelectionFailureText
{
    internal static string Message(SelectionIssue issue, bool chinese)
    {
        var text = issue switch
        {
            SelectionIssue.PermissionDenied => ("Text access was denied. Check system permissions; you can also copy text and translate the clipboard.", "文字读取被拒绝，请检查系统权限；也可先复制文字，再翻译剪贴板。"),
            SelectionIssue.ComponentMissing => ("The text capture component is missing. Rebuild or reinstall Yita.", "缺少取词组件，请重新构建或安装 Yita。"),
            SelectionIssue.VersionMismatch => ("The text capture component version does not match. Rebuild or reinstall Yita.", "取词组件版本不匹配，请重新构建或安装 Yita。"),
            SelectionIssue.RestartBackoff => ("The text capture component repeatedly stopped. Wait briefly, then repair input capture from the menu.", "取词组件多次停止，请稍后通过菜单修复输入捕获。"),
            SelectionIssue.Timeout => ("Text capture timed out. Retry, or copy text and translate the clipboard.", "取词超时，请重试，或先复制文字再翻译剪贴板。"),
            SelectionIssue.ProtectedContent => ("This selection is protected and cannot be read automatically.", "当前选区属于受保护内容，无法自动读取。"),
            SelectionIssue.UnsafeCopy => ("Automatic copying was stopped to protect your clipboard or input. Copy text yourself and translate the clipboard.", "为保护剪贴板或输入操作，已停止自动复制。请手动复制文字后翻译剪贴板。"),
            SelectionIssue.ClipboardChanged => ("The clipboard changed during capture. Copy the intended text and try again.", "取词期间剪贴板发生变化，请重新复制需要翻译的文字后重试。"),
            SelectionIssue.ClipboardUnavailable => ("The clipboard could not be read or restored. Try copying text again.", "无法读取或恢复剪贴板，请重新复制文字后重试。"),
            SelectionIssue.TextLimit => ("The selection exceeds the character limit. Select a shorter passage.", "所选文本超过字符上限，请缩小选区。"),
            SelectionIssue.UnsupportedApplication => ("This application did not provide readable selected text. Copy text and translate the clipboard.", "当前应用未提供可读取的选中文字，请先复制文字再翻译剪贴板。"),
            SelectionIssue.Unknown => ("Text capture failed. Retry or copy text and translate the clipboard; diagnostics are available from the menu.", "取词失败，请重试或先复制文字再翻译剪贴板；可通过菜单复制诊断。"),
            _ => ("No text captured. Copy text and try again.", "未读取到文字，请复制文字后重试。"),
        };
        return chinese ? text.Item2 : text.Item1;
    }
}
