namespace SpeculativeVerificationCouncil;

/// <summary>
/// Implements the reflection loop: if consensus confidence is below threshold,
/// re-drafts using corrections and re-runs actual council verification.
/// Max 2 iterations to prevent infinite loops.
/// </summary>
public sealed class ReflectionLoop
{
    private const string DraftModel = "gpt-oss:120b-cloud";
    private const double ConfidenceThreshold = 0.6;
    private const int MaxIterations = 2;

    private readonly OllamaClient _client;

    public ReflectionLoop(OllamaClient client) => _client = client;

    /// <summary>
    /// Attempt to improve a draft by incorporating corrections and re-running verification.
    /// Returns the improved draft, iteration count, and final confidence.
    /// </summary>
    public async Task<(string ImprovedDraft, int Iterations, double FinalConfidence)> ExecuteAsync(
        string originalQuery,
        string currentDraft,
        double initialConfidence,
        List<string> initialCorrections,
        Func<string, CancellationToken, Task<(List<string> Facts, List<string> Warnings, double Confidence)>> verifyFunc,
        CancellationToken ct = default)
    {
        if (initialConfidence >= ConfidenceThreshold || initialCorrections.Count == 0)
            return (currentDraft, 0, initialConfidence);

        string draft = currentDraft;
        double confidence = initialConfidence;
        List<string> corrections = initialCorrections;
        int iterations = 0;

        while (confidence < ConfidenceThreshold && iterations < MaxIterations && corrections.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            iterations++;

            string reflectionPrompt = $"""
                Your previous response was reviewed by multiple AI models and received feedback.

                Original question: {originalQuery}

                Your previous draft: {draft}

                Corrections from reviewers:
                {string.Join("\n- ", corrections)}

                Please write an improved response that addresses these corrections.
                Be accurate, concise, and factual. Do not add disclaimers about being AI.
                """;

            try
            {
                var response = await _client.GenerateAsync(
                    DraftModel, reflectionPrompt,
                    temperature: 0.5, maxTokens: 300, isCloud: true, ct: ct);

                if (response?.Response is null || string.IsNullOrWhiteSpace(response.Response))
                    break;

                draft = response.Response.Trim();

                // Re-run actual verification on the improved draft
                var (_, newWarnings, newConfidence) = await verifyFunc(draft, ct);
                confidence = newConfidence;

                // Refresh corrections from latest warnings if still low
                if (confidence < ConfidenceThreshold)
                    corrections = newWarnings
                        .Where(w => w.StartsWith("[DISSENT]") || w.StartsWith("[STRICT]"))
                        .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch { break; }
        }

        return (draft, iterations, confidence);
    }
}
