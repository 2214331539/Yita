using System.IO;
using Yita.Services;

namespace Yita.Tests;

public sealed class RuntimeHealthJournalTests
{
    [Fact]
    public void InnerExceptionTypesAreRecordedWithoutPrivateMessagesOrPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Yita-health-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "health.log");
        try
        {
            new RuntimeHealthJournal(path).Record(RuntimeHealthEvent.DispatcherUnhandledException,
                new InvalidOperationException("private source text", new IOException("C:\\private\\document.pdf")));
            var content = File.ReadAllText(path);
            Assert.Contains("inner=System.IO.IOException", content);
            Assert.DoesNotContain("private", content);
            Assert.DoesNotContain("document.pdf", content);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RecordNeverPersistsExceptionMessagesOrUserText()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"Yita-Health-{Guid.NewGuid():N}");
        var journalPath = Path.Combine(directory, "runtime-health.log");
        var journal = new RuntimeHealthJournal(journalPath);

        try
        {
            journal.Record(
                RuntimeHealthEvent.MouseHookRecoveryFailed,
                new InvalidOperationException("sensitive selected text"),
                numericCode: 17);

            var content = File.ReadAllText(journalPath);
            Assert.Contains("event=MouseHookRecoveryFailed", content);
            Assert.Contains("exception=System.InvalidOperationException", content);
            Assert.Contains("code=17", content);
            Assert.DoesNotContain("sensitive selected text", content);
            Assert.DoesNotContain("Endpoint", content);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void OversizedJournalRotatesBeforeAppendingNewEvent()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"Yita-Health-{Guid.NewGuid():N}");
        var journalPath = Path.Combine(directory, "runtime-health.log");
        Directory.CreateDirectory(directory);
        File.WriteAllText(journalPath, new string('x', (int)RuntimeHealthJournal.MaximumFileBytes));
        var journal = new RuntimeHealthJournal(journalPath);

        try
        {
            journal.Record(RuntimeHealthEvent.AppStarted);

            Assert.True(File.Exists(journalPath + ".previous"));
            Assert.Contains("event=AppStarted", File.ReadAllText(journalPath));
            Assert.True(new FileInfo(journalPath).Length < RuntimeHealthJournal.MaximumFileBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
