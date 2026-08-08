using Microsoft.ML.Tokenizers;

namespace PinballWizard.Processor.Chunking;

internal static class TokenHelper
{
    // Use the cl100k_base tokenizer (GPT-4/text-embedding-3-small compatible)
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    public static int CountTokens(string text)
        => Tokenizer.CountTokens(text);

    public static IReadOnlyList<string> SplitIntoTokenChunks(string text, int maxTokens)
    {
        var tokens = Tokenizer.EncodeToTokens(text, out _);
        var chunks = new List<string>();

        for (int i = 0; i < tokens.Count; i += maxTokens)
        {
            var slice = tokens.Skip(i).Take(maxTokens);
            chunks.Add(string.Concat(slice.Select(t => t.Value)));
        }

        return chunks;
    }
}
