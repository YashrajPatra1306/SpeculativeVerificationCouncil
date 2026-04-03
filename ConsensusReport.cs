using System.Text.Json.Serialization;

namespace SpeculativeVerificationCouncil;

// ── Enums ──────────────────────────────────────────────────────────────────

public enum ConsensusStrategy
{
    Auto,
    Strict,
    Weighted,
    Adversarial,
    Fast
}

public enum QueryIntent
{
    Math,
    Research,
    Code,
    Creative,
    General,
    Urgent,
    // Fix (Bug 1): Added Unknown so CreateFallbackReport can reference it
    // without a compile error.
    Unknown
}

// ── Ollama API contracts ───────────────────────────────────────────────────

public sealed record OllamaRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("options")] OllamaOptions? Options = null,
    [property: JsonPropertyName("format")] string? Format = null
);

public sealed record OllamaOptions(
    [property: JsonPropertyName("temperature")] double Temperature = 0.7,
    [property: JsonPropertyName("num_predict")] int NumPredict = 200
);

public sealed record OllamaResponse(
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("response")] string? Response = null,
    [property: JsonPropertyName("done")] bool Done = false,
    [property: JsonPropertyName("total_duration")] long TotalDuration = 0,
    [property: JsonPropertyName("eval_count")] int EvalCount = 0,
    [property: JsonPropertyName("prompt_eval_count")] int PromptEvalCount = 0
);

// ── Verification model response ────────────────────────────────────────────

public sealed record VerificationResult(
    [property: JsonPropertyName("valid")] bool Valid = false,
    [property: JsonPropertyName("corrections")] List<string>? Corrections = null,
    [property: JsonPropertyName("facts")] List<string>? Facts = null,
    [property: JsonPropertyName("confidence")] double Confidence = 0.0
);

// ── Classification result ──────────────────────────────────────────────────

public sealed record ClassificationResult(
    [property: JsonPropertyName("intent")] string? Intent = null,
    [property: JsonPropertyName("urgency")] int Urgency = 5,
    [property: JsonPropertyName("reasoning")] string? Reasoning = null
);

// ── Council vote with metadata ─────────────────────────────────────────────

public sealed record CouncilVote(
    string ModelName,
    double Weight,
    VerificationResult Result,
    TimeSpan ResponseTime,
    bool TimedOut = false,
    string? Error = null
);

// ── Final consensus report ─────────────────────────────────────────────────

public sealed record ConsensusReport(
    string OriginalQuery,
    string DraftResponse,
    string FinalResponse,
    ConsensusStrategy StrategyUsed,
    QueryIntent DetectedIntent,
    List<CouncilVote> Votes,
    List<string> VerifiedFacts,
    List<string> DissentWarnings,
    double OverallConfidence,
    int ReflectionIterations,
    CostSummary Cost,
    TimeSpan TotalTime
);

// ── Contradiction record ─────────────────────────────────────────────────────

/// <summary>A factual contradiction detected between two council members.</summary>
public sealed record Contradiction(
    string FactA,
    string FactB,
    string ModelA,
    string ModelB,
    string? Resolution = null
);

// ── Cost tracking ──────────────────────────────────────────────────────────

public sealed record CostSummary(
    int LocalTokens,
    int CloudTokens,
    int TotalApiCalls,
    double EstimatedCostUsd
)
{
    // Rough pricing: cloud calls ~$0.001 per 1K tokens (blended estimate)
    private const double CostPer1KCloudTokens = 0.001;

    public static CostSummary Calculate(int localTokens, int cloudTokens, int apiCalls) =>
        new(localTokens, cloudTokens, apiCalls,
            EstimatedCostUsd: cloudTokens / 1000.0 * CostPer1KCloudTokens);
}

// ── JSON source generator context (NativeAOT-safe) — for serialization ─────

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OllamaRequest))]
[JsonSerializable(typeof(OllamaOptions))]
[JsonSerializable(typeof(OllamaResponse))]
[JsonSerializable(typeof(VerificationResult))]
[JsonSerializable(typeof(ClassificationResult))]
[JsonSerializable(typeof(CouncilVote))]
[JsonSerializable(typeof(ConsensusReport))]
[JsonSerializable(typeof(Contradiction))]
[JsonSerializable(typeof(CostSummary))]
[JsonSerializable(typeof(List<string>))]
public partial class AppJsonContext : JsonSerializerContext;

// ── Lenient JSON context for parsing LLM outputs (case-insensitive) ────────

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(VerificationResult))]
[JsonSerializable(typeof(ClassificationResult))]
[JsonSerializable(typeof(ArbiterResponse))]
[JsonSerializable(typeof(List<string>))]
public partial class LenientJsonContext : JsonSerializerContext;

// ── Arbiter response model (used by AdversarialStrategy) ───────────────────

public sealed record ArbiterResponse(
    [property: JsonPropertyName("resolved_facts")] List<string>? ResolvedFacts = null,
    [property: JsonPropertyName("reasoning")] string? Reasoning = null
);
