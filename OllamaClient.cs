using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SpeculativeVerificationCouncil;

/// <summary>
/// Typed HTTP client for Ollama API supporting both local and cloud endpoints.
/// Uses System.Text.Json source generators for NativeAOT-safe zero-allocation parsing.
/// Employs ArrayPool for buffer reuse on the hot path.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _localClient;
    private readonly HttpClient _cloudClient;
    private readonly string? _apiKey;
    private const int MaxRetries = 2;
    private int _totalLocalTokens;
    private int _totalCloudTokens;
    private int _totalApiCalls;

    public int TotalLocalTokens => _totalLocalTokens;
    public int TotalCloudTokens => _totalCloudTokens;
    public int TotalApiCalls => _totalApiCalls;

    public OllamaClient(string localBaseUrl = "http://localhost:11434",
                         string cloudBaseUrl = "https://api.ollama.com",
                         string? apiKey = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OLLAMA_API_KEY");

        _localClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 10
        })
        {
            BaseAddress = new Uri(localBaseUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };

        _cloudClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 10
        })
        {
            BaseAddress = new Uri(cloudBaseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _cloudClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    /// <summary>
    /// Send a generation request to Ollama (local or cloud).
    /// </summary>
    public async Task<OllamaResponse?> GenerateAsync(
        string model,
        string prompt,
        double temperature = 0.7,
        int maxTokens = 200,
        bool isCloud = false,
        string? format = null,
        CancellationToken ct = default)
    {
        var request = new OllamaRequest(
            Model: model,
            Prompt: prompt,
            Stream: false,
            Options: new OllamaOptions(Temperature: temperature, NumPredict: maxTokens),
            Format: format
        );

        var client = isCloud ? _cloudClient : _localClient;

        for (int attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            byte[]? rentedBuffer = null;
            try
            {
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
                    request, AppJsonContext.Default.OllamaRequest);

                using var content = new ByteArrayContent(jsonBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                using var response = await client.PostAsync("/api/generate", content, ct);
                response.EnsureSuccessStatusCode();

                var stream = await response.Content.ReadAsStreamAsync(ct);
                rentedBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);

                using var ms = new MemoryStream();
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(rentedBuffer.AsMemory(), ct)) > 0)
                    ms.Write(rentedBuffer, 0, bytesRead);

                var result = JsonSerializer.Deserialize(
                    ms.ToArray().AsSpan(),
                    AppJsonContext.Default.OllamaResponse);

                if (result is not null)
                {
                    int tokens = result.EvalCount + result.PromptEvalCount;
                    if (isCloud)
                        Interlocked.Add(ref _totalCloudTokens, tokens);
                    else
                        Interlocked.Add(ref _totalLocalTokens, tokens);

                    Interlocked.Increment(ref _totalApiCalls);
                }

                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (attempt <= MaxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
            }
            catch
            {
                return null; // Retries exhausted
            }
            finally
            {
                if (rentedBuffer is not null)
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }

        return null;
    }

    /// <summary>
    /// Send a verification request and parse the structured JSON response.
    /// Falls back gracefully if the model returns malformed JSON.
    /// </summary>
    public async Task<CouncilVote> VerifyAsync(
        string model,
        string draft,
        string originalQuery,
        double weight,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        bool isCloud = model.Contains("cloud", StringComparison.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();

        string prompt = $$"""
            You are a verification model. Analyze the following draft response for accuracy.

            Original question: {{originalQuery}}
            Draft response: {{draft}}

            Evaluate the draft and respond ONLY with valid JSON (no markdown, no explanation):
            {
              "valid": true or false,
              "corrections": ["list of specific corrections needed, or empty if valid"],
              "facts": ["list of verified factual claims extracted from the draft"],
              "confidence": 0.0 to 1.0
            }

            Be precise. Extract only claims that can be verified. Set confidence based on your certainty.
            """;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var response = await GenerateAsync(
                model, prompt,
                temperature: 0.2,
                maxTokens: 500,
                isCloud: isCloud,
                format: "json",
                ct: timeoutCts.Token);

            sw.Stop();

            if (response is null || string.IsNullOrWhiteSpace(response.Response))
            {
                return new CouncilVote(model, weight,
                    new VerificationResult(), sw.Elapsed, Error: "Empty response");
            }

            var parsed = TryParseJson<VerificationResult>(response.Response);
            if (parsed is null)
            {
                return new CouncilVote(model, weight,
                    new VerificationResult(
                        Valid: true,
                        Confidence: 0.3,
                        Facts: [response.Response.Trim()]),
                    sw.Elapsed,
                    Error: "JSON parse fallback — raw text used as single fact");
            }

            return new CouncilVote(model, weight, parsed, sw.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            sw.Stop();
            return new CouncilVote(model, weight,
                new VerificationResult(), sw.Elapsed, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            throw; // User-initiated cancellation — propagate
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new CouncilVote(model, weight,
                new VerificationResult(), sw.Elapsed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Try to parse JSON from an LLM response, handling markdown code fences
    /// and other common issues. NativeAOT-safe via source-generated context.
    /// </summary>
    public static T? TryParseJson<T>(string? raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = StripMarkdownFences(raw.Trim());

        // Get the appropriate JsonTypeInfo from our source-generated context
        var typeInfo = GetTypeInfo<T>();
        if (typeInfo is null) return null;

        // Try direct parse first
        if (TryDeserialize(text, typeInfo, out var result))
            return result;

        // Try to extract JSON object from surrounding text
        int braceStart = text.IndexOf('{');
        int braceEnd = text.LastIndexOf('}');
        if (braceStart >= 0 && braceEnd > braceStart)
        {
            var jsonSubstring = text[braceStart..(braceEnd + 1)];
            if (TryDeserialize(jsonSubstring, typeInfo, out result))
                return result;
        }

        return null;
    }

    private static string StripMarkdownFences(string text)
    {
        if (text.StartsWith("```"))
        {
            int firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3];
            text = text.Trim();
        }
        return text;
    }

    private static bool TryDeserialize<T>(string json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, out T? result)
    {
        result = default;
        try
        {
            result = JsonSerializer.Deserialize(json, typeInfo);
            return result is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the JsonTypeInfo for supported types from our AOT-safe source-generated context.
    /// </summary>
    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>? GetTypeInfo<T>()
    {
        // NativeAOT-compatible: resolve from source-generated context
        if (typeof(T) == typeof(VerificationResult))
            return (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(object)
                LenientJsonContext.Default.VerificationResult;
        if (typeof(T) == typeof(ClassificationResult))
            return (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(object)
                LenientJsonContext.Default.ClassificationResult;
        if (typeof(T) == typeof(ArbiterResponse))
            return (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(object)
                LenientJsonContext.Default.ArbiterResponse;

        return null;
    }

    public CostSummary GetCostSummary() =>
        CostSummary.Calculate(_totalLocalTokens, _totalCloudTokens, _totalApiCalls);

    public void Dispose()
    {
        _localClient.Dispose();
        _cloudClient.Dispose();
    }
}
