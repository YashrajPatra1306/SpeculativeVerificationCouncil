using System.Diagnostics;

namespace SpeculativeVerificationCouncil;

/// <summary>
/// Auto-classifies queries using local TinyLlama (1B) to select the optimal
/// consensus strategy. Falls back to heuristic keyword matching if the model
/// is unavailable.
/// </summary>
public sealed class QueryClassifier
{
    private const string ClassifierModel = "gpt-oss:120b-cloud";

    private readonly OllamaClient _client;

    public QueryClassifier(OllamaClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Classify a query to determine intent, urgency, and recommended strategy.
    /// </summary>
    public async Task<(QueryIntent Intent, ConsensusStrategy Strategy, string Reasoning)> ClassifyAsync(
        string query, CancellationToken ct = default)
    {
        // Fast heuristic pre-check for urgency and obvious patterns
        var (heuristicIntent, heuristicStrategy) = HeuristicClassify(query);

        // Try LLM classification for more nuanced detection
        try
        {
            string prompt = $$"""
                Classify this query into exactly one category and assess urgency.
                
                Query: "{{query}}"
                
                Respond ONLY with valid JSON:
                {
                  "intent": "math" | "research" | "code" | "creative" | "general",
                  "urgency": 1-10 (10 = extremely urgent),
                  "reasoning": "one sentence why"
                }
                
                Categories:
                - math: calculations, equations, proofs, statistics
                - research: factual questions, history, science, analysis
                - code: programming, debugging, algorithms, technical implementation
                - creative: writing, brainstorming, storytelling, design ideas
                - general: everything else
                """;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await _client.GenerateAsync(
                ClassifierModel,
                prompt,
                temperature: 0.1,
                maxTokens: 100,
                isCloud: true,
                format: "json",
                ct: timeoutCts.Token);

            if (response?.Response is not null)
            {
                var result = OllamaClient.TryParseJson<ClassificationResult>(response.Response);
                if (result is not null)
                {
                    var intent = ParseIntent(result.Intent);
                    var strategy = MapIntentToStrategy(intent, result.Urgency, query);
                    string reasoning = result.Reasoning ?? "LLM classification";
                    return (intent, strategy, reasoning);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Classifier timed out — use heuristic
        }
        catch (Exception)
        {
            // Model unavailable — use heuristic
        }

        return (heuristicIntent, heuristicStrategy, "Heuristic fallback (local model unavailable)");
    }

    /// <summary>
    /// Keyword-based heuristic classifier for when the LLM is unavailable.
    /// </summary>
    private static (QueryIntent Intent, ConsensusStrategy Strategy) HeuristicClassify(string query)
    {
        var lower = query.ToLowerInvariant();
        int wordCount = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // Urgency: very short queries or exclamation marks
        if (wordCount <= 3 || lower.Contains("asap") || lower.Contains("quick") || lower.Contains("urgent"))
            return (QueryIntent.Urgent, ConsensusStrategy.Fast);

        // Math indicators
        string[] mathKeywords = ["calculate", "compute", "solve", "equation", "integral",
            "derivative", "proof", "theorem", "sum of", "product of", "probability",
            "statistics", "factorial", "logarithm", "sqrt", "matrix"];
        if (mathKeywords.Any(k => lower.Contains(k)) || HasMathSymbols(lower))
            return (QueryIntent.Math, ConsensusStrategy.Strict);

        // Research indicators
        string[] researchKeywords = ["explain", "what is", "who is", "when did", "history of",
            "research", "study", "evidence", "according to", "define", "compare",
            "difference between", "how does", "why does", "analysis"];
        if (researchKeywords.Any(k => lower.Contains(k)))
            return (QueryIntent.Research, ConsensusStrategy.Strict);

        // Code indicators
        string[] codeKeywords = ["code", "function", "implement", "algorithm", "debug",
            "error", "compile", "runtime", "syntax", "api", "class", "method",
            "variable", "loop", "array", "database", "sql", "python", "javascript",
            "c#", "rust", "java", "typescript", "html", "css", "regex", "git"];
        if (codeKeywords.Any(k => lower.Contains(k)))
            return (QueryIntent.Code, ConsensusStrategy.Weighted);

        // Creative indicators
        string[] creativeKeywords = ["write", "story", "poem", "creative", "imagine",
            "brainstorm", "design", "idea", "suggest", "invent", "compose",
            "describe a", "what if", "fiction", "character", "narrative"];
        if (creativeKeywords.Any(k => lower.Contains(k)))
            return (QueryIntent.Creative, ConsensusStrategy.Adversarial);

        return (QueryIntent.General, ConsensusStrategy.Weighted);
    }

    private static bool HasMathSymbols(string text) =>
        text.Any(c => c is '+' or '=' or '×' or '÷' or '∫' or '∑' or '^' or '√');

    private static QueryIntent ParseIntent(string? intent) =>
        intent?.ToLowerInvariant() switch
        {
            "math" => QueryIntent.Math,
            "research" => QueryIntent.Research,
            "code" => QueryIntent.Code,
            "creative" => QueryIntent.Creative,
            _ => QueryIntent.General
        };

    private static ConsensusStrategy MapIntentToStrategy(QueryIntent intent, int urgency, string query)
    {
        // High urgency or very short query → Fast
        int wordCount = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (urgency > 8 || wordCount <= 3)
            return ConsensusStrategy.Fast;

        return intent switch
        {
            QueryIntent.Math => ConsensusStrategy.Strict,
            QueryIntent.Research => ConsensusStrategy.Strict,
            QueryIntent.Code => ConsensusStrategy.Weighted,
            QueryIntent.Creative => ConsensusStrategy.Adversarial,
            _ => ConsensusStrategy.Weighted
        };
    }
}
