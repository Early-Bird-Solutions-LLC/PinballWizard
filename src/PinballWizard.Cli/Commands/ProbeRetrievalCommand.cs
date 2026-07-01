using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Evaluation;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;
using System.Text.Json;

namespace PinballWizard.Cli.Commands;

// --probe-retrieval <input.jsonl>
//
// Classifies every EvalQuestion in the input JSONL by first-stage retrieval
// rank and writes the results to <input>.classified.jsonl. Requires AI Search
// to be configured (IRetrievalRankProbe is only registered when
// AddAzureAiSearchIntegration is wired, i.e. AiSearch:Endpoint is set).
//
// IMPORTANT: this verb measures FIRST-STAGE rank — the raw AI Search
// hybrid+semantic order BEFORE Cohere cross-encoder reranking. If
// Rag:CrossEncoder:Enabled=true the measurement is corrupted because the
// retriever returns post-rerank order. The command refuses to run when the
// reranker is enabled (exit code 2) so the output is always trustworthy.
internal static class ProbeRetrievalCommand
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    internal static async Task RunAsync(
        string inputPath,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var probe = services.GetService<IRetrievalRankProbe>();
        if (probe is null)
        {
            Console.Error.WriteLine(
                "--probe-retrieval requires Azure AI Search to be configured. Set " +
                $"{AiSearchOptions.EndpointKey} (the deployed search service endpoint URL, e.g. " +
                "https://pinwiz-search-dev-XXXX.search.windows.net).");
            Environment.ExitCode = 2;
            return;
        }

        // Guard: if the reranker is ON, first-stage measurement is corrupted.
        // Resolve CrossEncoderOptions directly from IOptions — it is always
        // registered by AddAzureAiSearchIntegration even when Enabled=false.
        var crossEncoderOptions = services.GetService<IOptions<CrossEncoderOptions>>();
        if (crossEncoderOptions?.Value.Enabled == true)
        {
            Console.Error.WriteLine(
                "--probe-retrieval measures FIRST-STAGE retrieval rank (before Cohere reranking). " +
                "Rag:CrossEncoder:Enabled is currently true, which corrupts the measurement by " +
                "returning post-rerank order. Set Rag:CrossEncoder:Enabled=false and re-run.");
            Environment.ExitCode = 2;
            return;
        }

        var topN = crossEncoderOptions?.Value.TopN ?? CrossEncoderOptions_DefaultTopN;

        IReadOnlyList<EvalQuestion> questions;
        try
        {
            questions = EvalQuestionParser.ParseFile(inputPath);
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"--probe-retrieval: {ex.Message}");
            Environment.ExitCode = 2;
            return;
        }

        var outputPath = BuildOutputPath(inputPath);
        var rows = new List<EvalQuestion>(questions.Count);

        foreach (var question in questions)
        {
            var result = await probe.ProbeAsync(question, topN, cancellationToken).ConfigureAwait(false);
            rows.Add(question with
            {
                Slice = result.Slice,
                FirstStageRank = result.GoldRank,
            });
        }

        await File.WriteAllLinesAsync(
            outputPath,
            rows.Select(r => JsonSerializer.Serialize(r, WriteOptions)),
            cancellationToken).ConfigureAwait(false);

        // Slice-distribution summary line (greppable).
        var easy = rows.Count(r => r.Slice == "easy");
        var rerankerSensitive = rows.Count(r => r.Slice == "reranker-sensitive");
        var retrievalMiss = rows.Count(r => r.Slice == "retrieval-miss");
        Console.WriteLine($"easy={easy} reranker-sensitive={rerankerSensitive} retrieval-miss={retrievalMiss}");
        Console.WriteLine($"Classified {rows.Count} question(s) — written to {outputPath}");
    }

    internal static string BuildOutputPath(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(dir, stem + ".classified.jsonl");
    }

    // Fallback when CrossEncoderOptions is not available from DI (should not
    // happen when AddAzureAiSearchIntegration is wired, but guarded defensively).
    private const int CrossEncoderOptions_DefaultTopN = 5;
}
