using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Playwright;
using OpenAI.Chat;

namespace PinballWizard.Web.Tests.BugFinder;

// AI-powered visual UI review using Azure OpenAI GPT-4o vision.
// Takes desktop + mobile screenshots and sends them to the model with a
// senior UI/UX expert prompt, returning structured findings.
//
// Requires AiFoundry__AccountEndpoint env var (same as the Api project).
// If the env var is absent the review pass is skipped gracefully — functional
// checks still run.
public sealed class UiReviewPass : IDisposable
{
    // Viewport sizes
    private static readonly ViewportSize Desktop = new() { Width = 1440, Height = 900 };
    private static readonly ViewportSize Mobile = new() { Width = 390, Height = 844 };

    private const string DeploymentName = "gpt-4o";
    private const string EnvAccountEndpoint = "AiFoundry__AccountEndpoint";
    private const string EnvProjectEndpoint = "AiFoundry__ProjectEndpoint";

    private readonly AzureOpenAIClient? _client;
    private readonly bool _enabled;

    public UiReviewPass()
    {
        // Prefer account-level endpoint; fall back to project endpoint
        var endpoint = Environment.GetEnvironmentVariable(EnvAccountEndpoint)
                    ?? DeriveAccountEndpoint(
                           Environment.GetEnvironmentVariable(EnvProjectEndpoint));

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _enabled = false;
            return;
        }

        _client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
        _enabled = true;
    }

    public bool IsEnabled => _enabled;

    public async Task<List<BugFinding>> ReviewPageAsync(IPage page, string url)
    {
        if (!_enabled || _client is null)
            return [];

        var findings = new List<BugFinding>();

        try
        {
            // Desktop screenshot
            var desktopBytes = await CaptureAsync(page, url, Desktop);
            var desktopFindings = await AnalyzeAsync(url, "desktop (1440×900)", desktopBytes);
            findings.AddRange(desktopFindings);

            // Mobile screenshot
            var mobileBytes = await CaptureAsync(page, url, Mobile);
            var mobileFindings = await AnalyzeAsync(url, "mobile (390×844)", mobileBytes);
            findings.AddRange(mobileFindings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Vision call failed — record as a low-severity note, don't crash the crawl
            findings.Add(new BugFinding(url, BugSeverity.Low, BugSource.Ui,
                "UI review pass failed (API error)",
                ex.Message));
        }

        return findings;
    }

    private static async Task<byte[]> CaptureAsync(IPage page, string url, ViewportSize viewport)
    {
        // Clone the page context at the target viewport
        var context = page.Context;
        var clone = await context.Browser!.NewContextAsync(new() { ViewportSize = viewport });
        var clonePage = await clone.NewPageAsync();
        try
        {
            await clonePage.GotoAsync(url, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30_000 });
            await clonePage.WaitForTimeoutAsync(1_500); // let animations settle
            return await clonePage.ScreenshotAsync(new() { FullPage = false });
        }
        finally
        {
            await clone.CloseAsync();
        }
    }

    private async Task<List<BugFinding>> AnalyzeAsync(string url, string viewportLabel, byte[] screenshotBytes)
    {
        var chatClient = _client!.GetChatClient(DeploymentName);

        var prompt = BuildPrompt(url, viewportLabel);
        var imageContent = ChatMessageContentPart.CreateImagePart(
            BinaryData.FromBytes(screenshotBytes), "image/png");
        var textContent = ChatMessageContentPart.CreateTextPart(prompt);

        var messages = new List<ChatMessage>
        {
            new UserChatMessage(textContent, imageContent)
        };

        var completion = await chatClient.CompleteChatAsync(messages, new ChatCompletionOptions
        {
            MaxOutputTokenCount = 2000,
            Temperature = 0.2f,
        });

        var responseText = completion.Value.Content.FirstOrDefault()?.Text ?? "{}";
        return ParseResponse(url, viewportLabel, responseText);
    }

    private static string BuildPrompt(string url, string viewportLabel) => $"""
        You are a senior UI/UX engineer performing a professional quality audit of a pinball 
        machine reference web application called PinWiz.

        You are reviewing a screenshot of the page at URL: {url}
        Viewport: {viewportLabel}

        Evaluate the screenshot and identify any of the following issues if present:
        1. Layout problems: overflow, clipping, elements overlapping, misalignment
        2. Typography: inconsistent sizes/weights, truncated text, unreadable fonts, line-height issues
        3. Color & contrast: text hard to read against background, likely WCAG AA violations
        4. Spacing: excessive or missing padding/margin, elements too cramped or too spread
        5. Responsiveness: content clearly designed for wrong viewport, horizontal scroll, broken grid
        6. Visual hierarchy: CTAs not prominent, important info buried, unclear focus path
        7. Component inconsistencies: buttons/cards styled differently from siblings on the same page
        8. Broken/placeholder UI: stuck loading spinners, empty containers without fallback copy
        9. Accessibility red flags: missing visible labels, low-contrast interactive elements
        10. Anything that would look unprofessional or embarrassing in a shipped product

        For each issue found, provide:
        - A concise summary (one sentence)
        - Severity: Critical (broken/unusable), High (significant defect), Medium (noticeable flaw), Low (polish item)
        - The affected element or area (e.g. "navigation bar", "document card grid", "hero heading")

        If the page looks polished and professional with no notable issues, say so briefly.

        Respond ONLY with valid JSON in this exact format (no markdown fences):
        {{
          "pageVerdict": "pass" | "fail" | "warn",
          "viewportNotes": "brief overall impression",
          "issues": [
            {{
              "severity": "Critical" | "High" | "Medium" | "Low",
              "element": "element or area name",
              "summary": "one-sentence description of the issue"
            }}
          ]
        }}
        """;

    private static List<BugFinding> ParseResponse(string url, string viewportLabel, string json)
    {
        var findings = new List<BugFinding>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(json));
            var root = doc.RootElement;

            if (!root.TryGetProperty("issues", out var issues))
                return findings;

            foreach (var issue in issues.EnumerateArray())
            {
                var severityStr = issue.TryGetProperty("severity", out var sev) ? sev.GetString() : "Low";
                var severity = severityStr switch
                {
                    "Critical" => BugSeverity.Critical,
                    "High" => BugSeverity.High,
                    "Medium" => BugSeverity.Medium,
                    _ => BugSeverity.Low
                };

                var element = issue.TryGetProperty("element", out var el) ? el.GetString() ?? "" : "";
                var summary = issue.TryGetProperty("summary", out var sum) ? sum.GetString() ?? "" : "";

                findings.Add(new BugFinding(url, severity, BugSource.Ui,
                    $"[{viewportLabel}] {summary}",
                    $"Element: {element}"));
            }
        }
        catch (JsonException)
        {
            // Model returned malformed JSON — skip rather than crashing
        }
        return findings;
    }

    // Strip any accidental markdown fences the model adds despite the prompt
    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : "{}";
    }

    // Derive account-level endpoint from a project endpoint
    // e.g. https://xyz.services.ai.azure.com/api/projects/myproj → https://xyz.services.ai.azure.com/
    private static string? DeriveAccountEndpoint(string? projectEndpoint)
    {
        if (string.IsNullOrWhiteSpace(projectEndpoint)) return null;
        if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out var uri)) return null;
        return $"{uri.Scheme}://{uri.Host}/";
    }

    public void Dispose() { }
}
