namespace SpeculativeVerificationCouncil.Strategies;

/// <summary>
/// Fast consensus: use the single fastest response only.
/// Lowest latency, no cross-validation. Failover mode for urgent queries
/// or when cloud models are degraded.
/// </summary>
public sealed class FastStrategy : IConsensusStrategy
{
    public ConsensusStrategy Name => ConsensusStrategy.Fast;

    public Task<(List<string> VerifiedFacts, List<string> DissentWarnings, double Confidence)> ResolveAsync(
        VoteAggregator votes,
        OllamaClient client,
        CancellationToken ct = default)
    {
        var warnings = votes.DissentWarnings();
        var fastest = votes.FastestResponse;

        if (fastest is null)
        {
            warnings.Add("[FAST] No valid responses received — all models timed out or errored");
            return Task.FromResult((new List<string>(), warnings, 0.0));
        }

        var facts = fastest.Result.Facts?.ToList() ?? [];
        double confidence = fastest.Result.Confidence;

        warnings.Add($"[FAST] Using single response from {fastest.ModelName} ({fastest.ResponseTime.TotalSeconds:F1}s)");

        // Warn that this is unverified
        if (votes.ValidResponseCount > 1)
        {
            warnings.Add($"[FAST] {votes.ValidResponseCount - 1} additional response(s) ignored for speed");
        }

        return Task.FromResult((facts, warnings, confidence));
    }
}
