using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpeculativeVerificationCouncil;

/// <summary>
/// n8n Developer Agent integration for natural language workflow generation.
/// Implements validation pipeline with retry logic and tool calling support.
/// </summary>
public class N8nWorkflowGenerator
{
    private readonly OllamaClient _ollamaClient;
    private readonly ToonEnabled _toonEnabled;
    private readonly int _maxRetryAttempts = 3;

    public N8nWorkflowGenerator(OllamaClient ollamaClient, bool toonEnabled = true)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _toonEnabled = toonEnabled ? ToonEnabled.Yes : ToonEnabled.No;
    }

    /// <summary>
    /// Generates an n8n workflow from natural language description.
    /// Uses TOON format for 30-40% token reduction when enabled.
    /// </summary>
    public async Task<WorkflowGenerationResult> GenerateWorkflowAsync(
        string prompt,
        CancellationToken ct = default)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(prompt))
            return WorkflowGenerationResult.Failure("Prompt cannot be empty");

        if (prompt.Length > 5000)
            return WorkflowGenerationResult.Failure("Prompt exceeds maximum length of 5000 characters");

        var systemMessage = @"You are an n8n workflow generator. Output ONLY valid JSON representing a complete n8n workflow.
Include nodes array with types and parameters, connections object, and sticky notes for manual credential setup.
If you need to search for node documentation, use the web_search tool.

Required structure:
{
  ""nodes"": [
    { ""id"": ""1"", ""name"": ""Start"", ""type"": ""n8n-nodes-base.manualTrigger"", ""parameters"": {} }
  ],
  ""connections"": {
    ""Start"": { ""main"": [ [ { ""node"": ""NextNode"" } ] ] }
  }
}";

        var attempts = 0;
        WorkflowGenerationResult? lastError = null;

        while (attempts < _maxRetryAttempts)
        {
            attempts++;

            try
            {
                // Generate workflow using LLM with tool calling
                var response = await _ollamaClient.ChatAsync(
                    model: "glm-4.7:cloud",
                    messages: new[]
                    {
                        new ChatMessage("system", systemMessage),
                        new ChatMessage("user", prompt)
                    },
                    temperature: 0.7f,
                    tools: new[] { "web_search", "web_fetch" },
                    enableThinking: true,
                    cancellationToken: ct);

                // Extract and validate workflow
                var workflowJson = _toonEnabled == ToonEnabled.Yes
                    ? ToonConverter.ToJson(response.Content)
                    : ExtractJsonFromResponse(response.Content);

                var validationResult = ValidateWorkflow(workflowJson);

                if (!validationResult.IsValid)
                {
                    lastError = WorkflowGenerationResult.Failure(
                        $"Validation failed: {validationResult.Error}. Attempt {attempts}/{_maxRetryAttempts}");
                    
                    // Auto-retry with validation feedback
                    if (attempts < _maxRetryAttempts)
                    {
                        prompt = $@"Previous attempt failed: {validationResult.Error}
Please fix the issues and generate a valid workflow. Ensure all node types exist and connections reference valid nodes.";
                        continue;
                    }
                }

                return WorkflowGenerationResult.Success(workflowJson, response.TokenUsage);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = WorkflowGenerationResult.Failure(
                    $"Generation error: {ex.Message}. Attempt {attempts}/{_maxRetryAttempts}");
                
                if (attempts >= _maxRetryAttempts)
                    break;
            }
        }

        return lastError ?? WorkflowGenerationResult.Failure("Workflow generation failed after all retry attempts");
    }

    /// <summary>
    /// Validates an n8n workflow JSON/TOON structure.
    /// Implements Mercury-style guardrail validation.
    /// </summary>
    private ValidationResult ValidateWorkflow(string workflowJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(workflowJson);
            var root = doc.RootElement;

            // 1. Validate required fields
            if (!root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                return ValidationResult.Invalid("Missing 'nodes' array");

            if (!root.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Object)
                return ValidationResult.Invalid("Missing 'connections' object");

            // 2. Validate node structure
            var nodeIds = new HashSet<string>();
            foreach (var node in nodes.EnumerateArray())
            {
                if (!node.TryGetProperty("id", out var id))
                    return ValidationResult.Invalid("Node missing 'id' field");

                if (!node.TryGetProperty("type", out var type))
                    return ValidationResult.Invalid("Node missing 'type' field");

                nodeIds.Add(id.GetString() ?? string.Empty);

                // 3. Check if node type exists (basic validation)
                var nodeType = type.GetString() ?? string.Empty;
                if (!IsValidNodeType(nodeType))
                {
                    // Could use web_search here to verify unknown node types
                    return ValidationResult.Invalid($"Unknown node type: {nodeType}");
                }
            }

            // 4. Validate connections reference real nodes
            foreach (var connProp in connections.EnumerateObject())
            {
                if (!nodeIds.Contains(connProp.Name))
                    return ValidationResult.Invalid(
                        $"Connection references non-existent node: {connProp.Name}");

                // Validate connection targets
                foreach (var connection in connProp.Value.EnumerateObject())
                {
                    if (connection.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var connArray in connection.Value.EnumerateArray())
                        {
                            if (connArray.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var target in connArray.EnumerateArray())
                                {
                                    if (target.TryGetProperty("node", out var targetNode))
                                    {
                                        var targetNodeId = targetNode.GetString();
                                        if (!string.IsNullOrEmpty(targetNodeId) && !nodeIds.Contains(targetNodeId))
                                            return ValidationResult.Invalid(
                                                $"Connection targets non-existent node: {targetNodeId}");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return ValidationResult.Valid();
        }
        catch (JsonException ex)
        {
            return ValidationResult.Invalid($"Invalid JSON format: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if a node type is valid (basic allowlist).
    /// In production, this would query n8n's API or use web_search.
    /// </summary>
    private static bool IsValidNodeType(string nodeType)
    {
        // Common n8n node types - expand as needed
        var knownPrefixes = new[]
        {
            "n8n-nodes-base.",
            "@n8n/",
            "n8n-nodes-"
        };

        return knownPrefixes.Any(prefix => nodeType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
               nodeType.Contains("trigger") ||
               nodeType.Contains("action") ||
               nodeType.Contains("webhook");
    }

    /// <summary>
    /// Extracts JSON from LLM response that may contain explanations.
    /// </summary>
    private static string ExtractJsonFromResponse(string response)
    {
        // Try to find JSON blocks
        var patterns = new[]
        {
            @"```json\s*(.*?)\s*```",
            @"```\s*(.*?)\s*```",
            @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}"
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                response, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            
            if (match.Success)
                return match.Groups[1].Success ? match.Groups[1].Value.Trim() : match.Value.Trim();
        }

        return response.Trim();
    }
}

/// <summary>
/// Result of workflow generation.
/// </summary>
public record WorkflowGenerationResult(
    bool IsSuccess,
    string? WorkflowJson,
    TokenUsage? TokenUsage,
    string? Error
)
{
    public static WorkflowGenerationResult Success(string workflowJson, TokenUsage tokenUsage) =>
        new(true, workflowJson, tokenUsage, null);

    public static WorkflowGenerationResult Failure(string error) =>
        new(false, null, null, error);
}

/// <summary>
/// Validation result for workflow checks.
/// </summary>
public record ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Valid() => new(true, null);
    public static ValidationResult Invalid(string error) => new(false, error);
}

/// <summary>
/// Token usage tracking.
/// </summary>
public record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

/// <summary>
/// Chat message for LLM interactions.
/// </summary>
public record ChatMessage(string Role, string Content);

/// <summary>
/// TOON enabled/disabled flag.
/// </summary>
public enum ToonEnabled { Yes, No }
