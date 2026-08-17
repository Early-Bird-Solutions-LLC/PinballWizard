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
/// <para>
/// <b>This is an upper bound on Chromium's footprint, not a page-for-page
/// match to the container-level figure it's compared against.</b> VmRSS is
/// measured per-process; Chromium deliberately shares memory across its own
/// processes (mmap'd IPC buffers, the V8 snapshot, shared libraries), and
/// each sharer's VmRSS independently counts a shared page. Summing across
/// the tree therefore double-counts every shared page once per additional
/// sharer. A cgroup-level figure like ACA's <c>UsageBytes</c> does not have
/// this problem — a physical page is charged to the cgroup once regardless
/// of how many of its processes map it — so this sum can plausibly exceed
/// (never merely equal) the true Chromium share of that number. Proportional
/// Set Size (<c>/proc/[pid]/smaps_rollup</c>'s <c>Pss</c> field, which divides
/// a shared page's cost across its sharers) would close this gap; that file
/// is more expensive to read and its availability under ACA's container
/// security context is unverified, so it's deliberately not what this reads.
/// Read this instrument as "Chromium uses at least this much," not "exactly
/// this much."
/// </para>
/// </remarks>
internal static class ProcTreeMemoryReader
{
    /// <summary>
    /// Sums VmRSS across every live descendant of <paramref name="rootProcessId"/>
    /// (children, grandchildren, ...) — NOT including the root itself, since
    /// callers already measure that via <see cref="System.Diagnostics.Process.WorkingSet64"/>.
    /// See the type-level remarks for why this is an upper bound, not an exact figure.
    /// </summary>
    /// <returns>
    /// The summed resident-set bytes, or null when unavailable: non-Linux, /proc
    /// unreadable, or zero descendants found. That last case is treated as
    /// "unmeasurable" rather than "measured zero" deliberately — every real call
    /// site (<see cref="PolitePlaywrightScraperBase"/>'s memory probe) only calls
    /// this after the browser context already exists, so zero descendants there
    /// means the walk failed to see a process that is actually running, not that
    /// Chromium genuinely hasn't started yet. Null is never reported as zero — a
    /// probe that cannot measure must say so, not fabricate an empty reading
    /// (invariant #17).
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
        var seen = new HashSet<int> { rootPid };
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            if (!childrenByParent.TryGetValue(queue.Dequeue(), out var children)) continue;

            foreach (var child in children)
            {
                // A PID recycled mid-scan (process A exits, its PID is reassigned as a
                // child of process B, while B's own PID is simultaneously recycled
                // elsewhere) could otherwise form a cycle in childrenByParent. This walk
                // runs inside SampleMemory, which SampleMemory's caller holds
                // _contextInitLock for — an infinite loop here would deadlock every
                // future page open, not just this one sample. `seen` makes that
                // structurally impossible regardless of how implausible the race is.
                if (!seen.Add(child)) continue;

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
