namespace SpeculativeVerificationCouncil.Strategies;

/// <summary>
/// Weighted consensus: union of facts scoring above 50% of max weighted score.
/// Balanced accuracy/recall. Best for code verification.
/// </summary>
public sealed class WeightedStrategy : IConsensusStrategy
{
    public ConsensusStrategy Name => ConsensusStrategy.Weighted;

    public Task<(List<string> VerifiedFacts, List<string> DissentWarnings, double Confidence)> ResolveAsync(
        VoteAggregator votes,
        OllamaClient client,
        CancellationToken ct = default)
    {
        var facts = votes.WeightedFacts(thresholdRatio: 0.50);
        var warnings = votes.DissentWarnings();
        double confidence = votes.OverallConfidence;

        if (votes.HasContradictions)
        {
            warnings.Add("[WEIGHTED] Contradictions detected — low-scoring facts filtered out");
        }

        if (facts.Count == 0 && votes.AllFacts.Count > 0)
        {
            // Relax threshold
            facts = votes.WeightedFacts(thresholdRatio: 0.25);
            warnings.Add("[WEIGHTED] Relaxed threshold to 25% — original threshold yielded no facts");
            confidence *= 0.7;
        }

        return Task.FromResult((facts, warnings, confidence));
    }
}
