using System.Text.Json;
using Yita.Core.Selection;

namespace Yita.Core.Tests;

public sealed class SelectionDiagnosticsTests
{
    [Theory]
    [InlineData(SelectionFailureKind.Unknown, "mac-helper-missing", SelectionIssue.ComponentMissing)]
    [InlineData(SelectionFailureKind.Unknown, "mac-helper-version-mismatch", SelectionIssue.VersionMismatch)]
    [InlineData(SelectionFailureKind.Unknown, "mac-helper-restart-backoff", SelectionIssue.RestartBackoff)]
    [InlineData(SelectionFailureKind.UnsupportedApplication, "uia-worker-missing", SelectionIssue.ComponentMissing)]
    [InlineData(SelectionFailureKind.ClipboardUnavailable, "manual-clipboard-empty", SelectionIssue.Empty)]
    [InlineData(SelectionFailureKind.Cancelled, "clipboard-owner-changed", SelectionIssue.ClipboardChanged)]
    [InlineData(SelectionFailureKind.Cancelled, "mac-helper-clipboard-superseded", SelectionIssue.ClipboardChanged)]
    [InlineData(SelectionFailureKind.Cancelled, "mac-helper-ax-text-limit", SelectionIssue.TextLimit)]
    [InlineData(SelectionFailureKind.ProtectedContent, "mac-helper-clipboard-unsafe-target", SelectionIssue.UnsafeCopy)]
    [InlineData(SelectionFailureKind.Unknown, "mac-helper-ax-permission-denied", SelectionIssue.PermissionDenied)]
    [InlineData(SelectionFailureKind.Timeout, "private arbitrary diagnostic", SelectionIssue.Timeout)]
    [InlineData(SelectionFailureKind.Cancelled, "selection-cancelled", SelectionIssue.Cancelled)]
    public void NativeFailuresMapToTypedIssuesWithoutRetainingCodes(SelectionFailureKind failure, string code, SelectionIssue expected)
    {
        var diagnostics = new SelectionDiagnostics();
        var sample = diagnostics.Record(SelectionTrigger.MouseGesture, SelectionResult.Failed(failure, code));
        Assert.Equal(expected, sample.Issue);
        Assert.Null(sample.Source);
        Assert.DoesNotContain(code, JsonSerializer.Serialize(sample));
    }

    [Fact]
    public void SamplesAreBoundedAndNeverRetainTextContextOrDiagnosticStrings()
    {
        var diagnostics = new SelectionDiagnostics();
        var success = new SelectionResult("private selection", SelectionSource.ClipboardFallback,
            DiagnosticCode: "secret-api-key", Context: "private context");
        for (var index = 0; index < 200; index++) diagnostics.Record(SelectionTrigger.MouseGesture, success, TimeSpan.FromMilliseconds(index));
        Assert.Equal(SelectionDiagnostics.SampleCapacity, diagnostics.Samples.Count);
        Assert.Equal(72d, diagnostics.Samples[0].ReadMilliseconds);
        var report = diagnostics.CreateReport(false);
        Assert.Contains("Completed reads: 200", report);
        Assert.Contains("ClipboardFallback: n=128; p50=135.0ms; p95=193.0ms", report);
        var serialized = JsonSerializer.Serialize(diagnostics.Samples) + report;
        Assert.DoesNotContain("private", serialized);
        Assert.DoesNotContain("secret-api-key", serialized);
        diagnostics.Record(SelectionTrigger.MouseGesture, SelectionResult.Failed(SelectionFailureKind.Unknown, "private exception"));
        Assert.DoesNotContain("private exception", JsonSerializer.Serialize(diagnostics.Samples) + diagnostics.CreateReport(false));
    }

    [Fact]
    public void EmptyAndCancelledAttemptsPreserveActionableStatusAndSuccessClearsIt()
    {
        var diagnostics = new SelectionDiagnostics();
        diagnostics.Record(SelectionTrigger.MouseGesture, SelectionResult.Failed(SelectionFailureKind.PermissionDenied));
        diagnostics.Record(SelectionTrigger.MouseGesture, SelectionResult.Failed(SelectionFailureKind.Empty));
        diagnostics.Record(SelectionTrigger.MouseGesture, SelectionResult.Failed(SelectionFailureKind.Cancelled));
        Assert.Equal(SelectionIssue.PermissionDenied, diagnostics.LastMeaningful!.Issue);
        diagnostics.Record(SelectionTrigger.TranslateShortcut, new("hello", SelectionSource.ManualClipboard));
        Assert.False(SelectionIssueClassifier.IsActionable(diagnostics.LastMeaningful!.Issue));
    }

    [Fact]
    public void MissingAndInvalidDurationsDoNotPolluteLatencyPercentiles()
    {
        var diagnostics = new SelectionDiagnostics();
        var success = new SelectionResult("hello", SelectionSource.Accessibility);
        diagnostics.Record(SelectionTrigger.MouseGesture, success);
        diagnostics.Record(SelectionTrigger.MouseGesture, success, TimeSpan.FromMilliseconds(-1));
        diagnostics.Record(SelectionTrigger.MouseGesture, success, TimeSpan.FromDays(1));
        Assert.All(diagnostics.Samples, sample => Assert.Null(sample.ReadMilliseconds));
        Assert.DoesNotContain("p95", diagnostics.CreateReport(false));
    }
}
