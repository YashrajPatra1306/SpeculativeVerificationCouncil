namespace SpeculativeVerificationCouncil;

/// <summary>
/// Strategy pattern interface for consensus resolution.
/// Each strategy determines how to merge council votes into a final verified fact set.
/// </summary>
public interface IConsensusStrategy
{
    ConsensusStrategy Name { get; }

    /// <summary>
    /// Resolve council votes into a set of verified facts and dissent warnings.
    /// May optionally invoke an arbiter model for tie-breaking (Adversarial strategy).
    /// </summary>
    Task<(List<string> VerifiedFacts, List<string> DissentWarnings, double Confidence)> ResolveAsync(
        VoteAggregator votes,
        OllamaClient client,
        CancellationToken ct = default);
}
