using Xunit;

namespace PinballWizard.Core.Tests.Domain;

// OBS-01 mechanical check. PR #972 logged and metered an Azure Playwright
// authentication failure, then launched local Chromium and let the job succeed.
// The previous CHECK was qualitative ("would anyone know?"), so a logged switch
// of providers passed. These tests fail when a catch body starts the other
// browser provider.
public sealed class Obs01NoMaskingFallbackTests
{
    [Fact]
    public void CatchThatLaunchesLocalChromiumAfterWorkspaceFailure_IsAProviderSwitch()
    {
        const string source = """
            catch (Exception ex) when (IsWorkspaceAuthenticationFailure(ex))
            {
                launchLocalChromium(playwright);
                return local;
            }
            """;

        var hits = ProviderSwitchScanner.Find(source, "fixture.cs");

        Assert.NotEmpty(hits);
    }

    [Fact]
    public void CatchThatConnectsToWorkspaceAfterLocalChromiumFailure_IsAProviderSwitch()
    {
        const string source = """
            catch (Exception ex)
            {
                return await connectToWorkspace(playwright);
            }
            """;

        var hits = ProviderSwitchScanner.Find(source, "fixture.cs");

        Assert.NotEmpty(hits);
    }

    [Fact]
    public void CatchThatMetersAndRethrows_IsNotAProviderSwitch()
    {
        const string source = """
            if (!configured)
            {
                return await launchLocalChromium(playwright);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (IsWorkspaceAuthenticationFailure(ex))
                    LogError(ex);
                throw;
            }
            """;

        var hits = ProviderSwitchScanner.Find(source, "fixture.cs");

        Assert.Empty(hits);
    }

    [Fact]
    public void ProductionSources_DoNotSwitchBrowserProvidersInsideACatch()
    {
        var root = DocConformanceTests.FindRepoRoot();
        var src = Path.Combine(root, "src");
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file);
            hits.AddRange(ProviderSwitchScanner.Find(File.ReadAllText(file), relative));
        }

        Assert.True(hits.Count == 0,
            "OBS-01: a catch switches browser providers. The configured provider's failure must fail the job.\n  "
            + string.Join("\n  ", hits));
    }

    internal static class ProviderSwitchScanner
    {
        internal static IReadOnlyList<string> Find(string source, string relativePath)
        {
            var hits = new List<string>();
            var mask = MaskCommentsAndStrings(source);
            for (var i = 0; i < source.Length; i++)
            {
                if (mask[i] || !IsKeywordAt(source, i, "catch"))
                    continue;

                var open = IndexOfBodyBrace(source, mask, i);
                if (open < 0)
                    continue;

                var close = MatchingBrace(source, mask, open);
                if (close < 0)
                {
                    hits.Add($"{relativePath}:{LineNumber(source, i)} catch has no matching brace");
                    break;
                }

                var body = source.Substring(open, close - open + 1);
                if (SwitchesProvider(body))
                    hits.Add($"{relativePath}:{LineNumber(source, i)} catch switches browser provider");

                i = close;
            }

            return hits;
        }

        private static bool SwitchesProvider(string body) =>
            body.Contains("launchLocalChromium", StringComparison.Ordinal)
            || body.Contains("Chromium.Launch", StringComparison.Ordinal)
            || body.Contains("connectToWorkspace", StringComparison.Ordinal)
            || body.Contains("Chromium.Connect", StringComparison.Ordinal)
            || body.Contains("local_chromium", StringComparison.Ordinal);

        private static bool IsKeywordAt(string source, int index, string word)
        {
            if (index + word.Length > source.Length)
                return false;
            if (!source.AsSpan(index, word.Length).Equals(word, StringComparison.Ordinal))
                return false;
            if (index > 0 && IsIdentChar(source[index - 1]))
                return false;
            var after = index + word.Length;
            return after >= source.Length || !IsIdentChar(source[after]);
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int IndexOfBodyBrace(string source, bool[] mask, int catchAt)
        {
            var depth = 0;
            for (var i = catchAt; i < source.Length; i++)
            {
                if (mask[i])
                    continue;
                if (source[i] == '(')
                    depth++;
                else if (source[i] == ')')
                    depth = Math.Max(0, depth - 1);
                else if (source[i] == '{' && depth == 0)
                    return i;
            }

            return -1;
        }

        private static int MatchingBrace(string source, bool[] mask, int open)
        {
            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (mask[i])
                    continue;
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static int LineNumber(string source, int index)
        {
            var line = 1;
            for (var i = 0; i < index && i < source.Length; i++)
            {
                if (source[i] == '\n')
                    line++;
            }

            return line;
        }

        // True where a character is inside a comment or string, so a "catch" in
        // prose or a brace inside a message does not move the scanner.
        private static bool[] MaskCommentsAndStrings(string source)
        {
            var mask = new bool[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n')
                    {
                        mask[i] = true;
                        i++;
                    }
                }
                else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    mask[i] = true;
                    i++;
                    mask[i] = true;
                    i++;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                    {
                        mask[i] = true;
                        i++;
                    }

                    if (i < source.Length)
                        mask[i] = true;
                    if (i + 1 < source.Length)
                        mask[i + 1] = true;
                }
                else if (source[i] == '"')
                {
                    mask[i] = true;
                    i++;
                    while (i < source.Length && source[i] != '"')
                    {
                        if (source[i] == '\\')
                        {
                            mask[i] = true;
                            i++;
                        }

                        if (i < source.Length)
                            mask[i] = true;
                        i++;
                    }

                    if (i < source.Length)
                        mask[i] = true;
                }
                else if (source[i] == '\'')
                {
                    mask[i] = true;
                    i++;
                    while (i < source.Length && source[i] != '\'')
                    {
                        if (source[i] == '\\')
                        {
                            mask[i] = true;
                            i++;
                        }

                        if (i < source.Length)
                            mask[i] = true;
                        i++;
                    }

                    if (i < source.Length)
                        mask[i] = true;
                }
            }

            return mask;
        }
    }
}
