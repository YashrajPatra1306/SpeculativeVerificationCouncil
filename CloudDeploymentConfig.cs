using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpeculativeVerificationCouncil;

/// <summary>
/// Hugging Face Spaces deployment configuration and management.
/// Free tier: 2 vCPU, 16GB RAM, 50GB disk.
/// Space sleeps after 48 hours of inactivity.
/// </summary>
public class CloudDeploymentConfig
{
    private readonly HttpClient _httpClient;
    private readonly string _spaceUrl;
    private readonly string? _apiKey;

    public CloudDeploymentConfig(string spaceUrl, string? apiKey = null)
    {
        _spaceUrl = spaceUrl ?? throw new ArgumentNullException(nameof(spaceUrl));
        _apiKey = apiKey;

        // Validate URL format (SSRF prevention)
        if (!Uri.TryCreate(_spaceUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(".hf.space", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Contains("huggingface.co"))
        {
            throw new ArgumentException(
                "Invalid Hugging Face Space URL. Must be a valid .hf.space or huggingface.co/spaces domain.", 
                nameof(spaceUrl));
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_spaceUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    /// <summary>
    /// Checks if the Hugging Face Space is currently active or sleeping.
    /// </summary>
    public async Task<SpaceStatus> GetSpaceStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/health", ct);
            
            if (response.IsSuccessStatusCode)
                return new SpaceStatus(true, "Active", await GetSpaceMetricsAsync(ct));

            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                return new SpaceStatus(false, "Sleeping", null);

            return new SpaceStatus(false, $"Error: {response.StatusCode}", null);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("Connection refused"))
        {
            return new SpaceStatus(false, "Sleeping", null);
        }
        catch (Exception ex)
        {
            return new SpaceStatus(false, $"Error: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Wakes up a sleeping Hugging Face Space by sending a webhook request.
    /// The space will stay active for 48 hours after wake-up.
    /// </summary>
    public async Task<bool> WakeUpSpaceAsync(CancellationToken ct = default)
    {
        try
        {
            // Send a simple GET request to wake the space
            var response = await _httpClient.GetAsync("/", ct);
            return response.IsSuccessStatusCode || 
                   response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets current resource metrics from the space.
    /// </summary>
    private async Task<SpaceMetrics?> GetSpaceMetricsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/metrics", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return await JsonSerializer.DeserializeAsync<SpaceMetrics>(
                await response.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deploys an n8n workflow to the space.
    /// </summary>
    public async Task<bool> DeployWorkflowAsync(
        string workflowName,
        string workflowData,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                name = workflowName,
                data = workflowData,
                timestamp = DateTime.UtcNow.ToString("O")
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("/api/workflows", content, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tests the connection to the Hugging Face Space.
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var status = await GetSpaceStatusAsync(ct);
            return status.IsActive || status.Status == "Sleeping";
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Status of a Hugging Face Space.
/// </summary>
public record SpaceStatus(bool IsActive, string Status, SpaceMetrics? Metrics);

/// <summary>
/// Resource metrics from a Hugging Face Space.
/// </summary>
public record SpaceMetrics(
    double? CpuUsage,
    double? MemoryUsage,
    double? DiskUsage,
    int? RequestCount,
    DateTime? LastActive
);

/// <summary>
/// Configuration for n8n integration with Hugging Face Spaces.
/// </summary>
public class N8nIntegrationConfig
{
    public string WebhookUrl { get; set; } = string.Empty;
    public string EncryptionKey { get; set; } = string.Empty;
    public string DatabaseUrl { get; set; } = string.Empty;
    public bool AutoWakeEnabled { get; set; } = true;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validates the n8n configuration.
    /// </summary>
    public ValidationResult Validate()
    {
        var errors = new System.Collections.Generic.List<string>();

        if (string.IsNullOrWhiteSpace(WebhookUrl))
            errors.Add("WebhookUrl is required");

        if (string.IsNullOrWhiteSpace(EncryptionKey) || EncryptionKey.Length < 16)
            errors.Add("EncryptionKey must be at least 16 characters");

        if (string.IsNullOrWhiteSpace(DatabaseUrl) || !DatabaseUrl.StartsWith("postgresql://"))
            errors.Add("DatabaseUrl must be a valid PostgreSQL connection string");

        return errors.Count == 0 
            ? ValidationResult.Success 
            : ValidationResult.Failure(errors.ToArray());
    }
}

/// <summary>
/// Validation result for configuration checks.
/// </summary>
public record ValidationResult(bool IsSuccess, string[] Errors)
{
    public static ValidationResult Success { get; } = new(true, Array.Empty<string>());
    
    public static ValidationResult Failure(params string[] errors) => 
        new(false, errors);
}
