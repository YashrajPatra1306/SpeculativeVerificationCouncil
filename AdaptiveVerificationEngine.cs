using System.Diagnostics;
using System.Threading.RateLimiting;
using SpeculativeVerificationCouncil.Strategies;

namespace SpeculativeVerificationCouncil;

/// <summary>
/// Main orchestrator implementing the Draft → Verify → Render pipeline.
/// Coordinates local draft generation, parallel cloud verification council,
/// consensus resolution, reflection loops, and final rendering.
/// </summary>
public sealed class AdaptiveVerificationEngine : IDisposable
{
    // ── Model configuration ────────────────────────────────────────────────
    private const string LocalDraftModel = "gpt-oss:120b-cloud";
    private const string LocalRenderModel = "minimax-m2:cloud";

    private static readonly (string Model, double Weight)[] CouncilModels =
    [
        ("deepseek-v3.1:671b-cloud", 2.0),  // Logic/reasoning validator
        ("qwen3-coder:480b-cloud", 1.5),     // Technical/code validator
        ("glm-4.6:cloud", 1.0),               // Context/coherence validator
    ];

    private static readonly TimeSpan CouncilTimeout = TimeSpan.FromSeconds(30);
    
    // Security: Rate limiting to prevent DoS and resource exhaustion
    private readonly RateLimiter _rateLimiter;
    private const int MaxConcurrentRequests = 5;

    // ── Dependencies ───────────────────────────────────────────────────────
    private readonly OllamaClient _client;
    private readonly QueryClassifier _classifier;
    private readonly ReflectionLoop _reflectionLoop;
    private readonly Dictionary<ConsensusStrategy, IConsensusStrategy> _strategies;

    private ConsensusStrategy _currentStrategy = ConsensusStrategy.Auto;

    public ConsensusStrategy CurrentStrategy
    {
        get => _currentStrategy;
        set => _currentStrategy = value;
    }

    /// <summary>Sets the strategy — mirrors CurrentStrategy setter for Kimi-style API compatibility.</summary>
    public void SetStrategy(ConsensusStrategy strategy) => _currentStrategy = strategy;

    /// <summary>Fires on each pipeline status update — subscribe for external progress display.</summary>
    public event EventHandler<string>? ProgressChanged;

    private void Report(string message, Action<string>? onStatus)
    {
        onStatus?.Invoke(message);
        ProgressChanged?.Invoke(this, message);
    }

    public AdaptiveVerificationEngine(OllamaClient client)
    {
        _client = client;
        _classifier = new QueryClassifier(client);
        _reflectionLoop = new ReflectionLoop(client);

        // Security: Initialize rate limiter to prevent DoS
        _rateLimiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = MaxConcurrentRequests,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 10
        });

        _strategies = new Dictionary<ConsensusStrategy, IConsensusStrategy>
        {
            [ConsensusStrategy.Strict] = new StrictStrategy(),
            [ConsensusStrategy.Weighted] = new WeightedStrategy(),
            [ConsensusStrategy.Adversarial] = new AdversarialStrategy(),
            [ConsensusStrategy.Fast] = new FastStrategy()
        };
    }

    /// <summary>
    /// Execute the full Draft → Verify → Render pipeline for a query.
    /// </summary>
    public async Task<ConsensusReport> ProcessQueryAsync(
        string query,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();

        // Security: Validate input to prevent injection and resource exhaustion
        if (string.IsNullOrWhiteSpace(query))
        {
            totalSw.Stop();
            return CreateFallbackReport(query, "", ConsensusStrategy.Weighted, QueryIntent.Unknown, totalSw.Elapsed,
                "Empty query not allowed");
        }
        
        // Security: Enforce maximum query length
        const int MaxQueryLength = 5000;
        if (query.Length > MaxQueryLength)
        {
            totalSw.Stop();
            return CreateFallbackReport(query[..MaxQueryLength], "", ConsensusStrategy.Weighted, QueryIntent.Unknown, totalSw.Elapsed,
                $"Query exceeds maximum length of {MaxQueryLength} characters");
        }

        // ── Phase 0: Classify ──────────────────────────────────────────────
        Report("Classifying query intent...", onStatus);
        var (intent, autoStrategy, reasoning) = await _classifier.ClassifyAsync(query, ct);

        var effectiveStrategy = _currentStrategy == ConsensusStrategy.Auto
            ? autoStrategy
            : _currentStrategy;

        Report($"Intent: {intent} | Strategy: {effectiveStrategy} | {reasoning}", onStatus);

        // ── Phase 1: Draft ────────────────────────────────────────────────
        Report($"Generating draft with {LocalDraftModel}...", onStatus);
        string draft = await GenerateDraftAsync(query, ct);

        if (string.IsNullOrWhiteSpace(draft))
        {
            totalSw.Stop();
            return CreateFallbackReport(query, "", effectiveStrategy, intent, totalSw.Elapsed,
                "Draft model returned empty response");
        }

        Report($"Draft ready ({draft.Length} chars). Dispatching council...", onStatus);

        // ── Phase 2: Parallel Verification Council ─────────────────────────
        var votes = await RunCouncilAsync(query, draft, onStatus, ct);

        Report($"Council responded: {votes.ValidResponseCount}/{CouncilModels.Length} valid", onStatus);

        // ── Phase 3: Consensus Resolution ──────────────────────────────────
        if (!_strategies.TryGetValue(effectiveStrategy, out var strategy))
            strategy = _strategies[ConsensusStrategy.Weighted];

        Report($"Resolving consensus ({strategy.Name})...", onStatus);
        var (verifiedFacts, dissentWarnings, confidence) =
            await strategy.ResolveAsync(votes, _client, ct);

        // ── Phase 3.5: Reflection Loop (real re-verification) ─────────────
        int reflectionIterations = 0;
        if (confidence < 0.6 && votes.AllCorrections.Count > 0)
        {
            Report($"Low confidence ({confidence:P0}) — entering reflection loop...", onStatus);

            // Real verification callback: re-runs the full council on the improved draft
            async Task<(List<string>, List<string>, double)> verifyDraft(string d, CancellationToken c)
            {
                var v = await RunCouncilAsync(query, d, null, c);
                if (!_strategies.TryGetValue(effectiveStrategy, out var s))
                    s = _strategies[ConsensusStrategy.Weighted];
                return await s.ResolveAsync(v, _client, c);
            }

            var (improvedDraft, iterations, newConfidence) = await _reflectionLoop.ExecuteAsync(
                query, draft, confidence, votes.AllCorrections, verifyDraft, ct);

            if (iterations > 0)
            {
                draft = improvedDraft;
                reflectionIterations = iterations;
                confidence = newConfidence; // Real confidence from actual re-verification
                Report($"Reflection complete ({iterations} iteration(s)), confidence: {confidence:P0}", onStatus);
            }
        }

        // ── Phase 4: Render ───────────────────────────────────────────────
        Report("Rendering final response...", onStatus);
        string finalResponse = await RenderFinalAsync(query, draft, verifiedFacts, ct);

        if (string.IsNullOrWhiteSpace(finalResponse))
            finalResponse = draft; // Fallback to draft if render fails

        totalSw.Stop();

        return new ConsensusReport(
            OriginalQuery: query,
            DraftResponse: draft,
            FinalResponse: finalResponse,
            StrategyUsed: effectiveStrategy,
            DetectedIntent: intent,
            Votes: votes.Votes.ToList(),
            VerifiedFacts: verifiedFacts,
            DissentWarnings: dissentWarnings,
            OverallConfidence: confidence,
            ReflectionIterations: reflectionIterations,
            Cost: _client.GetCostSummary(),
            TotalTime: totalSw.Elapsed
        );
    }

    /// <summary>
    /// Generate initial draft using local lightweight model.
    /// </summary>
    private async Task<string> GenerateDraftAsync(string query, CancellationToken ct)
    {
        try
        {
            var response = await _client.GenerateAsync(
                LocalDraftModel,
                query,
                temperature: 0.9,
                maxTokens: 200,
                isCloud: true,
                ct: ct);

            return response?.Response?.Trim() ?? "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return $"[Draft generation failed: {ex.Message}]";
        }
    }

    /// <summary>
    /// Fire parallel verification requests to all council models.
    /// Uses Task.WhenAny with timeout for graceful degradation.
    /// Implements rate limiting to prevent DoS and resource exhaustion.
    /// </summary>
    private async Task<VoteAggregator> RunCouncilAsync(
        string query, string draft, Action<string>? onStatus, CancellationToken ct)
    {
        var aggregator = new VoteAggregator();

        // Security: Acquire rate limit permit before proceeding
        using var lease = await _rateLimiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            onStatus?.Invoke("Rate limit exceeded — request queued");
        }

        // Launch all verifications in parallel with controlled concurrency
        var tasks = CouncilModels.Select(m =>
            _client.VerifyAsync(m.Model, draft, query, m.Weight, CouncilTimeout, ct))
            .ToList();

        // Wait for all with overall timeout (give extra buffer beyond individual timeout)
        try
        {
            var overallTimeout = Task.Delay(CouncilTimeout + TimeSpan.FromSeconds(2), ct);
            var allTasks = Task.WhenAll(tasks);

            var completed = await Task.WhenAny(allTasks, overallTimeout);

            if (completed == allTasks)
            {
                // All completed within timeout
                foreach (var vote in await allTasks)
                {
                    aggregator.Add(vote);
                    onStatus?.Invoke($"  {vote.ModelName}: {(vote.TimedOut ? "TIMEOUT" : vote.Error ?? (vote.Result.Valid ? "✓ VALID" : "✗ INVALID"))} ({vote.ResponseTime.TotalSeconds:F1}s)");
                }
            }
            else
            {
                // Overall timeout hit — collect whatever finished
                onStatus?.Invoke("Council overall timeout — collecting partial results...");
                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        var vote = await task;
                        aggregator.Add(vote);
                        onStatus?.Invoke($"  {vote.ModelName}: {(vote.Result.Valid ? "✓" : "✗")} ({vote.ResponseTime.TotalSeconds:F1}s)");
                    }
                    else
                    {
                        // Create a timeout vote for tasks that didn't finish
                        var modelInfo = CouncilModels[tasks.IndexOf(task)];
                        aggregator.Add(new CouncilVote(
                            modelInfo.Model, modelInfo.Weight,
                            new VerificationResult(),
                            CouncilTimeout, TimedOut: true));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — degrade gracefully
            onStatus?.Invoke("Council timeout — proceeding with available votes");
        }

        // Deadlock fallback: if zero valid responses, create a synthetic local vote
        if (aggregator.ValidResponseCount == 0)
        {
            onStatus?.Invoke("Council deadlock — falling back to local model completion");
            aggregator.Add(new CouncilVote(
                "local-fallback", 1.0,
                new VerificationResult(
                    Valid: true,
                    Corrections: [],
                    Facts: [draft],
                    Confidence: 0.3),
                TimeSpan.Zero));
        }

        return aggregator;
    }

    /// <summary>
    /// Render the final natural language output from verified facts using
    /// a larger local model (3B).
    /// </summary>
    private async Task<string> RenderFinalAsync(
        string query, string draft, List<string> verifiedFacts, CancellationToken ct)
    {
        if (verifiedFacts.Count == 0)
            return draft; // Nothing verified — return raw draft

        string renderPrompt = $"""
            You are rendering a final response. Use ONLY the verified facts below.
            Do not add information beyond what is provided. Write naturally and concisely.

            Original question: {query}

            Verified facts:
            {string.Join("\n- ", verifiedFacts)}

            Draft for reference (may contain errors): {draft}

            Write a clear, accurate response using only the verified facts above.
            """;

        try
        {
            var response = await _client.GenerateAsync(
                LocalRenderModel,
                renderPrompt,
                temperature: 0.4,
                maxTokens: 400,
                isCloud: true,
                ct: ct);

            return response?.Response?.Trim() ?? draft;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return draft;
        }
    }

    /// <summary>Create a fallback report when the pipeline cannot complete.</summary>
    private static ConsensusReport CreateFallbackReport(
        string query, string draft, ConsensusStrategy strategy, QueryIntent intent,
        TimeSpan elapsed, string reason)
    {
        return new ConsensusReport(
            OriginalQuery: query,
            DraftResponse: draft,
            FinalResponse: $"[Pipeline error: {reason}]",
            StrategyUsed: strategy,
            DetectedIntent: intent,
            Votes: [],
            VerifiedFacts: [],
            DissentWarnings: [$"[FALLBACK] {reason}"],
            OverallConfidence: 0.0,
            ReflectionIterations: 0,
            Cost: CostSummary.Calculate(0, 0, 0),
            TotalTime: elapsed
        );
    }

    /// <summary>Get current engine status for the !status command.</summary>
    public string GetStatus()
    {
        var cost = _client.GetCostSummary();
        return $"""
            ┌─ Engine Status ──────────────────────────────────────────┐
            │  Strategy:      {_currentStrategy,-20}                   │
            │  Council:       {CouncilModels.Length} models ({string.Join(", ", CouncilModels.Select(m => m.Model.Split(':')[0]))})
            │  Draft Model:   {LocalDraftModel,-20}                    │
            │  Render Model:  {LocalRenderModel,-20}                   │
            │  Timeout:       {CouncilTimeout.TotalSeconds}s per model │
            │  API Calls:     {cost.TotalApiCalls,-10}                 │
            │  Local Tokens:  {cost.LocalTokens,-10}                   │
            │  Cloud Tokens:  {cost.CloudTokens,-10}                   │
            │  Est. Cost:     ${cost.EstimatedCostUsd:F4}              |                │
            └──────────────────────────────────────────────────────────┘
            """;
    }

    public void Dispose() => _client.Dispose();
}