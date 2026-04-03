using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpeculativeVerificationCouncil;

/// <summary>
/// Supabase PostgreSQL client for workflow storage and execution logs.
/// Free tier: 500MB storage, unlimited API requests.
/// </summary>
public class SupabaseClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _supabaseUrl;
    private readonly string _supabaseKey;
    private bool _disposed;

    public SupabaseClient(string supabaseUrl, string supabaseKey)
    {
        _supabaseUrl = supabaseUrl ?? throw new ArgumentNullException(nameof(supabaseUrl));
        _supabaseKey = supabaseKey ?? throw new ArgumentNullException(nameof(supabaseKey));

        // Validate URL format (SSRF prevention)
        if (!Uri.TryCreate(_supabaseUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid Supabase URL. Must be a valid .supabase.co domain.", nameof(supabaseUrl));
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_supabaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _supabaseKey);
        _httpClient.DefaultRequestHeaders.Add("apikey", _supabaseKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Stores a workflow definition in the database.
    /// </summary>
    public async Task<string> StoreWorkflowAsync(
        string workflowName, 
        string workflowData, 
        string? userId = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            name = workflowName,
            data = workflowData,
            user_id = userId,
            created_at = DateTime.UtcNow.ToString("O"),
            updated_at = DateTime.UtcNow.ToString("O")
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync("/rest/v1/workflow_entity", content, ct);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new SupabaseException($"Failed to store workflow: {response.StatusCode} - {error}");
        }

        var result = await JsonSerializer.DeserializeAsync<JsonElement>(
            await response.Content.ReadAsStreamAsync(ct), 
            cancellationToken: ct);

        return result.GetProperty("id").GetString() ?? 
               throw new SupabaseException("No ID returned from workflow storage");
    }

    /// <summary>
    /// Retrieves a workflow by ID.
    /// </summary>
    public async Task<WorkflowRecord?> GetWorkflowAsync(string workflowId, CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(
            $"/rest/v1/workflow_entity?id=eq.{workflowId}", ct);

        if (!response.IsSuccessStatusCode)
            return null;

        var results = await JsonSerializer.DeserializeAsync<WorkflowRecord[]>(
            await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);

        return results?.Length > 0 ? results[0] : null;
    }

    /// <summary>
    /// Logs an execution event to the database.
    /// </summary>
    public async Task LogExecutionAsync(
        string workflowId,
        string status,
        string? errorMessage = null,
        int? tokenCount = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            workflow_id = workflowId,
            status = status,
            error_message = errorMessage,
            token_count = tokenCount,
            executed_at = DateTime.UtcNow.ToString("O")
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        await _httpClient.PostAsync("/rest/v1/execution_entity", content, ct);
    }

    /// <summary>
    /// Retrieves execution logs for a workflow.
    /// </summary>
    public async Task<ExecutionLog[]> GetExecutionLogsAsync(
        string workflowId, 
        int limit = 50,
        CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(
            $"/rest/v1/execution_entity?workflow_id=eq.{workflowId}&order=executed_at.desc&limit={limit}", 
            ct);

        if (!response.IsSuccessStatusCode)
            return Array.Empty<ExecutionLog>();

        return await JsonSerializer.DeserializeAsync<ExecutionLog[]>(
            await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct) ?? Array.Empty<ExecutionLog>();
    }

    /// <summary>
    /// Tests the connection to Supabase.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/rest/v1/", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _httpClient.Dispose();
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Workflow record from Supabase.
/// </summary>
public record WorkflowRecord(
    string Id,
    string Name,
    string Data,
    string? UserId,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>
/// Execution log entry from Supabase.
/// </summary>
public record ExecutionLog(
    string Id,
    string WorkflowId,
    string Status,
    string? ErrorMessage,
    int? TokenCount,
    DateTime ExecutedAt
);

/// <summary>
/// Exception thrown when Supabase operations fail.
/// </summary>
public class SupabaseException : Exception
{
    public SupabaseException(string message) : base(message) { }
    public SupabaseException(string message, Exception inner) : base(message, inner) { }
}
