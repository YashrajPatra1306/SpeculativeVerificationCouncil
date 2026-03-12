namespace SpeculativeVerificationCouncil.Strategies;

/// <summary>
/// Adversarial consensus: detect contradictions and dispatch an arbiter
/// (llama3.3:70b-cloud) to resolve disputes. Best for creative tasks
/// where disagreement is expected and valuable.
/// </summary>
public sealed class AdversarialStrategy : IConsensusStrategy
{
    private const string ArbiterModel = "llama3.3:70b-cloud";

    public ConsensusStrategy Name => ConsensusStrategy.Adversarial;

    public async Task<(List<string> VerifiedFacts, List<string> DissentWarnings, double Confidence)> ResolveAsync(
        VoteAggregator votes,
        OllamaClient client,
        CancellationToken ct = default)
    {
        var warnings = votes.DissentWarnings();
        double confidence = votes.OverallConfidence;

        var contradictions = votes.FindContradictions();

        if (contradictions.Count == 0)
        {
            // No contradictions — use weighted union as fallback
            var facts = votes.WeightedFacts(thresholdRatio: 0.40);
            warnings.Add("[ADVERSARIAL] No contradictions found — used weighted union");
            return (facts, warnings, confidence);
        }

        // Build arbiter prompt with typed Contradiction records
        var disputeLines = contradictions.Select((c, i) =>
            $"Dispute {i + 1}:\n  Model A ({c.ModelA}): \"{c.FactA}\"\n  Model B ({c.ModelB}): \"{c.FactB}\"");

        string arbiterPrompt = $$"""
            You are an impartial arbiter resolving factual disputes between AI models.
            
            The following disputes were detected:
            {{string.Join("\n\n", disputeLines)}}
            
            For each dispute, determine which position is more likely correct.
            Respond in JSON format:
            {"resolved_facts": ["fact1", "fact2", ...], "reasoning": "brief explanation"}
            
            Only include facts you are confident about. If neither side is clearly correct,
            omit that fact entirely. Be conservative.
            """;

        warnings.Add($"[ADVERSARIAL] {contradictions.Count} contradiction(s) detected — invoking arbiter ({ArbiterModel})");

        try
        {
            var arbiterResult = await client.GenerateAsync(
                ArbiterModel,
                arbiterPrompt,
                temperature: 0.3,
                maxTokens: 400,
                isCloud: true,
                ct: ct);

            if (arbiterResult is not null && !string.IsNullOrWhiteSpace(arbiterResult.Response))
            {
                var parsed = OllamaClient.TryParseJson<ArbiterResponse>(arbiterResult.Response);
                if (parsed?.ResolvedFacts is { Count: > 0 })
                {
                    // Merge arbiter facts with non-contradicted facts from weighted union
                    var baseFacts = votes.WeightedFacts(thresholdRatio: 0.40);
                    var mergedFacts = MergeFacts(baseFacts, parsed.ResolvedFacts);

                    warnings.Add($"[ADVERSARIAL] Arbiter resolved {parsed.ResolvedFacts.Count} fact(s)");
                    if (parsed.Reasoning is not null)
                        warnings.Add($"[ADVERSARIAL] Arbiter reasoning: {parsed.Reasoning}");

                    confidence = Math.Min(confidence * 1.1, 1.0); // Slight boost for resolved disputes
                    return (mergedFacts, warnings, confidence);
                }
            }

            warnings.Add("[ADVERSARIAL] Arbiter response could not be parsed — falling back to weighted union");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"[ADVERSARIAL] Arbiter call failed: {ex.Message} — falling back to weighted union");
        }

        // Fallback
        var fallbackFacts = votes.WeightedFacts(thresholdRatio: 0.40);
        confidence *= 0.6;
        return (fallbackFacts, warnings, confidence);
    }

    private static List<string> MergeFacts(List<string> baseFacts, List<string> arbiterFacts)
    {
        var merged = new List<string>(baseFacts);
        foreach (var fact in arbiterFacts)
        {
            if (!merged.Any(f => SemanticSimilarity.IsSimilar(f, fact, 0.75)))
                merged.Add(fact);
        }
        return merged;
    }

}
