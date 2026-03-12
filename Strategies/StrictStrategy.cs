namespace SpeculativeVerificationCouncil.Strategies;

/// <summary>
/// Strict consensus: only facts present in ALL model responses survive.
/// Highest accuracy, lowest recall. Best for math/research.
/// </summary>
public sealed class StrictStrategy : IConsensusStrategy
{
    public ConsensusStrategy Name => ConsensusStrategy.Strict;

    public Task<(List<string> VerifiedFacts, List<string> DissentWarnings, double Confidence)> ResolveAsync(
        VoteAggregator votes,
        OllamaClient client,
        CancellationToken ct = default)
    {
        var facts = votes.IntersectionFacts(similarityThreshold: 0.70);
        var warnings = votes.DissentWarnings();
        double confidence = votes.OverallConfidence;

        // Strict strategy penalizes confidence when models disagree
        if (votes.HasContradictions)
        {
            confidence *= 0.7;
            warnings.Add("[STRICT] Contradictions detected — confidence penalized");
        }

        if (facts.Count == 0 && votes.ValidResponseCount > 0)
        {
            warnings.Add("[STRICT] No facts survived intersection — models diverged entirely");
            // Fall back to highest-confidence model's facts
            var bestVote = votes.Votes
                .Where(v => !v.TimedOut && v.Error is null)
                .OrderByDescending(v => v.Weight * v.Result.Confidence)
                .FirstOrDefault();

            if (bestVote?.Result.Facts is { Count: > 0 })
            {
                facts = bestVote.Result.Facts.ToList();
                warnings.Add($"[STRICT] Fell back to {bestVote.ModelName} facts (highest weighted confidence)");
                confidence *= 0.5;
            }
        }

        return Task.FromResult((facts, warnings, confidence));
    }
}
