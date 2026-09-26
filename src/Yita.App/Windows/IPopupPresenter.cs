using Yita.Models;
using Yita.Translation;

namespace Yita.Windows;

internal sealed record PopupRetranslateRequest(
    long RequestId,
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    ScreenPoint AnchorPoint);

internal sealed record PopupTranslationCorrection(
    long RequestId,
    string SourceText,
    string CorrectedTranslation,
    string SourceLanguage,
    string TargetLanguage);

internal sealed record PopupExplanationRequest(
    long RequestId,
    Guid HistorySessionId,
    DateTimeOffset HistoryCreatedAt,
    string SubjectText,
    string SourceText,
    string TranslationText,
    string SourceLanguage,
    string TargetLanguage,
    ExplanationScope Scope);

internal sealed record PopupQuestionAnswerRequest(
    Guid SessionId,
    long ParentRequestId,
    DateTimeOffset HistoryCreatedAt,
    string Question,
    string SourceText,
    string TranslationText,
    string? ExplanationText,
    string SourceLanguage,
    string TargetLanguage,
    string UiLanguage,
    QuestionContextKind ContextKind,
    IReadOnlyList<ConversationTurn> History);

internal interface IPopupPresenter
{
    event Action<PopupRetranslateRequest>? RetranslateRequested;

    event Action<long>? PopupClosed;

    event Action<long, bool>? PinStateChanged;

    event Func<PopupTranslationCorrection, bool>? TranslationCorrectionRequested;

    event Action<PopupExplanationRequest>? ExplanationRequested;

    event Action<long>? ExplanationDismissed;

    event Action<PopupQuestionAnswerRequest>? QuestionAnswerRequested;

    event Action<Guid>? QuestionAnswerCancelled;

    void ShowLoading(long requestId, ScreenPoint anchorPoint);

    void ShowTranslation(
        long requestId,
        string sourceText,
        string translatedText,
        string targetLanguage,
        ScreenPoint anchorPoint,
        string sourceLanguage = "自动检测");

    void FailRequest(long requestId, string? message = null);

    void CompleteRequest(long requestId);

    void ShowExplanationLoading(long requestId);

    void ShowExplanation(long requestId, string explanation);

    void CompleteExplanation(long requestId);

    void FailExplanation(long requestId, string message);

    void HideExplanation(long requestId);

    void ShowQuestionAnswer(Guid sessionId, string answer);

    void CompleteQuestionAnswer(Guid sessionId, string answer);

    void FailQuestionAnswer(Guid sessionId, string message);

    bool IsPointOverPopup(ScreenPoint point);

    void HideTransientPopup();

    void HideTransientPopupIfOutside(ScreenPoint point);

}
