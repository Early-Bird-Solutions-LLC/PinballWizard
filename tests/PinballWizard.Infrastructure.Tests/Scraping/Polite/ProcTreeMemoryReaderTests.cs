using System.Diagnostics;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

// These tests exercise the REAL /proc-walking mechanism — no fake filesystem, no mock.
// On Linux (the CI runner and every deployed ACA job) that means spawning a real child
// process and confirming its actual resident memory shows up in the sum. On a non-Linux
// dev machine, the platform guard is itself the behavior under test: there is no /proc
// to walk, and the correct, asserted result is null, not a silent skip.
public sealed class ProcTreeMemoryReaderTests
{
    [Fact]
    public void GetDescendantResidentSetBytes_OnLinux_SumsRssOfARealChildProcess()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(ProcTreeMemoryReader.GetDescendantResidentSetBytes(Environment.ProcessId));
            return;
        }

        using var child = Process.Start(new ProcessStartInfo("sleep", "5")
        {
            UseShellExecute = false,
        });
        Assert.NotNull(child);

        try
        {
            // /proc/<pid>/status is populated by the kernel as part of process creation,
            // but reading it immediately after Process.Start can race that population —
            // poll briefly rather than asserting on the very first sample.
            long? rss = null;
            for (var attempt = 0; attempt < 20 && (rss is null or 0); attempt++)
            {
                rss = ProcTreeMemoryReader.GetDescendantResidentSetBytes(Environment.ProcessId);
                if (rss is null or 0) Thread.Sleep(50);
            }

            Assert.NotNull(rss);
            Assert.True(rss > 0, "a live child process must report positive resident memory, not zero or null");
        }
        finally
        {
            child.Kill();
            child.WaitForExit(2000);
        }
    }

    [Fact]
    public void GetDescendantResidentSetBytes_OnNonLinux_ReturnsNullRatherThanZero()
    {
        // Redundant with the platform branch above on a Linux runner (both assert the
        // Linux path there), but this is the one test that runs meaningfully on the
        // Windows dev machine this was written on — confirming the guard fires rather
        // than throwing or fabricating a zero.
        if (OperatingSystem.IsLinux()) return;

        Assert.Null(ProcTreeMemoryReader.GetDescendantResidentSetBytes(Environment.ProcessId));
    }
}
