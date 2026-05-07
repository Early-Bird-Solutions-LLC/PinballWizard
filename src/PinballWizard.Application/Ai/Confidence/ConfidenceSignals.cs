namespace PinballWizard.Application.Ai.Confidence;

// The three signals that feed the geometric-mean confidence score per
// ADR-0017. All values clip to [0,1]. Geometric mean composition is
// load-bearing here — a near-zero in any one signal pulls the composite
// to near-zero, which is exactly the property we want for safety
// (a plausible-sounding answer with zero citations should not pass).
public sealed record ConfidenceSignals(
    double RetrievalSimilarity,
    double ModelSelfReported,
    double CitationCoverage)
{
    // Composite confidence. Geometric mean of the three signals; per
    // ADR-0017 we apply a +epsilon floor on CitationCoverage so a
    // perfectly-grounded answer with one accidentally-missing citation
    // downweights but doesn't zero-out.
    public double Composite()
    {
        var r = Clip(RetrievalSimilarity);
        var m = Clip(ModelSelfReported);
        var c = Math.Max(Clip(CitationCoverage), 0.05);
        return Math.Pow(r * m * c, 1.0 / 3.0);
    }

    private static double Clip(double value)
    {
        if (double.IsNaN(value) || value < 0.0)
        {
            return 0.0;
        }
        return value > 1.0 ? 1.0 : value;
    }
}
