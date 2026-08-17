namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Reads combined resident-set memory for every descendant of a process, via
/// the Linux /proc filesystem. Playwright launches Chromium as a descendant
/// process tree (a Node.js driver, which launches the browser, which spawns
/// renderer/GPU child processes) that
/// <see cref="System.Diagnostics.Process.WorkingSet64"/> cannot see — that
/// call only reports the .NET process itself. This is the other half of the
/// #855 memory question: how much of a container's OOM is Chromium versus
/// the .NET process the existing probes already measure.
/// </summary>
/// <remarks>
/// <para>
/// Linux-only: /proc has no equivalent on Windows or macOS, and every ACA job
/// this measures runs the Linux container image built by
/// <c>PinballWizard.Cli/Dockerfile</c> (Debian-based <c>aspnet</c> base).
/// </para>
/// <para>
/// Field layout verified against the canonical Linux kernel documentation
/// (man7.org proc_pid_stat(5) / proc_pid_status(5), consulted 2026-08-17) —
/// not assumed from general familiarity with the format:
/// <list type="bullet">
///   <item><c>/proc/[pid]/stat</c> — the <c>comm</c> field (2nd, whitespace-
///   and-parenthesis-delimited) can itself contain spaces or parentheses, so
///   the only safe way to find the fields after it is to locate the LAST
///   <c>')'</c> on the line. The next two whitespace-separated tokens are
///   state (field 3) and ppid (field 4).</item>
///   <item><c>/proc/[pid]/status</c> — the line <c>"VmRSS:  N kB"</c> gives
///   resident-set size in kilobytes.</item>
/// </list>
/// </para>
/// <para>
/// Never throws. A diagnostic probe that can fail the scrape it is measuring
/// would be a worse defect than the one it exists to find — matches the
/// contract <see cref="PolitePlaywrightScraperBase"/>'s existing memory probe
/// already holds itself to.
/// </para>
/// </remarks>
internal static class ProcTreeMemoryReader
{
    /// <summary>
    /// Sums VmRSS across every live descendant of <paramref name="rootProcessId"/>
    /// (children, grandchildren, ...) — NOT including the root itself, since
    /// callers already measure that via <see cref="System.Diagnostics.Process.WorkingSet64"/>.
    /// </summary>
    /// <returns>
    /// The summed resident-set bytes, or null when unavailable (non-Linux,
    /// /proc unreadable, or the root process has no live descendants right
    /// now). Null is never reported as zero — a probe that cannot measure
    /// must say so, not fabricate an empty reading (invariant #17).
    /// </returns>
    public static long? GetDescendantResidentSetBytes(int rootProcessId)
    {
        if (!OperatingSystem.IsLinux()) return null;

        Dictionary<int, List<int>> childrenByParent;
        try
        {
            childrenByParent = BuildChildrenIndex();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (childrenByParent.Count == 0) return null;

        var descendants = CollectDescendants(rootProcessId, childrenByParent);
        if (descendants.Count == 0) return null;

        long total = 0;
        var measuredAny = false;
        foreach (var pid in descendants)
        {
            var rss = TryReadVmRssBytes(pid);
            if (rss is null) continue;
            total += rss.Value;
            measuredAny = true;
        }

        return measuredAny ? total : null;
    }

    private static Dictionary<int, List<int>> BuildChildrenIndex()
    {
        var byParent = new Dictionary<int, List<int>>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid)) continue;

            var ppid = TryReadParentPid(pid);
            if (ppid is null) continue;

            if (!byParent.TryGetValue(ppid.Value, out var siblings))
            {
                siblings = [];
                byParent[ppid.Value] = siblings;
            }
            siblings.Add(pid);
        }
        return byParent;
    }

    private static int? TryReadParentPid(int pid)
    {
        string stat;
        try
        {
            stat = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The process exited between the directory listing and this read
            // — routine under a live, constantly-changing process tree, not
            // a failure worth reporting.
            return null;
        }

        // comm (field 2) is parenthesized and may itself contain ')' — find
        // the LAST ')' on the line, per proc_pid_stat(5), then split what
        // follows: token 0 is state (field 3), token 1 is ppid (field 4).
        var closeParen = stat.LastIndexOf(')');
        if (closeParen < 0 || closeParen + 2 >= stat.Length) return null;

        var rest = stat[(closeParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return rest.Length >= 2 && int.TryParse(rest[1], out var ppid) ? ppid : null;
    }

    private static List<int> CollectDescendants(int rootPid, Dictionary<int, List<int>> childrenByParent)
    {
        var result = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            if (!childrenByParent.TryGetValue(queue.Dequeue(), out var children)) continue;

            foreach (var child in children)
            {
                result.Add(child);
                queue.Enqueue(child);
            }
        }

        return result;
    }

    private static long? TryReadVmRssBytes(int pid)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines($"/proc/{pid}/status");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var line in lines)
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;

            // Format per proc_pid_status(5): "VmRSS:\t   12345 kB".
            var value = line["VmRSS:".Length..].Trim();
            var kbText = value.EndsWith("kB", StringComparison.Ordinal)
                ? value[..^2].Trim()
                : value;

            return long.TryParse(kbText, out var kb) ? kb * 1024 : null;
        }

        // A zombie process has no memory maps and legitimately has no VmRSS
        // line — that is real zero residency, not a read failure.
        return 0;
    }
}
