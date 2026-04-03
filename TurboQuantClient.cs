// ============================================================================
// TurboQuant Client - KV Cache Compression for Long-Context LLM Inference
// ============================================================================
// Purpose: HTTP client for TurboQuant+ Python sidecar service
// Benefits: 3.8-6.4x KV cache compression, enabling longer contexts on free tier
// Source: https://github.com/TheTom/turboquant_plus (Apache 2.0)
// ============================================================================

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mercury;

/// <summary>
/// Configuration for TurboQuant compression settings
/// </summary>
public sealed class TurboQuantConfig
{
    // K-cache quantization type (q8_0, turbo2, turbo3, turbo4)
    public string KCacheType { get; set; } = "q8_0";
    
    // V-cache quantization type (q8_0, turbo2, turbo3, turbo4)
    public string VCacheType { get; set; } = "turbo4";
    
    // Block size for compression (32 or 128)
    public int BlockSize { get; set; } = 32;
    
    // Enable boundary layer protection (first 2 + last 2 layers at higher precision)
    public bool EnableBoundaryProtection { get; set; } = true;
    
    // Number of boundary layers to protect on each end
    public int BoundaryLayers { get; set; } = 2;
    
    // Enable Sparse V dequantization for faster decode (+22% at long context)
    public bool EnableSparseV { get; set; } = true;
}

/// <summary>
/// Compression request sent to TurboQuant service
/// </summary>
public sealed class CompressionRequest
{
    [JsonPropertyName("kv_cache")]
    public float[]? KvCache { get; set; }
    
    [JsonPropertyName("config")]
    public TurboQuantConfig? Config { get; set; }
    
    [JsonPropertyName("context_length")]
    public int ContextLength { get; set; }
}

/// <summary>
/// Compression response from TurboQuant service
/// </summary>
public sealed class CompressionResponse
{
    [JsonPropertyName("compressed_data")]
    public byte[]? CompressedData { get; set; }
    
    [JsonPropertyName("original_size")]
    public int OriginalSize { get; set; }
    
    [JsonPropertyName("compressed_size")]
    public int CompressedSize { get; set; }
    
    [JsonPropertyName("compression_ratio")]
    public double CompressionRatio { get; set; }
    
    [JsonPropertyName("quality_estimate")]
    public double QualityEstimate { get; set; }
    
    [JsonPropertyName("processing_time_ms")]
    public double ProcessingTimeMs { get; set; }
}

/// <summary>
/// HTTP client for communicating with TurboQuant+ Python sidecar service
/// </summary>
public sealed class TurboQuantClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly TurboQuantConfig _config;
    private readonly string _baseUrl;
    private bool _disposed;

    /// <summary>
    /// Initialize TurboQuant client with default configuration
    /// </summary>
    /// <param name="baseUrl">Base URL of TurboQuant Python service (default: http://localhost:8080)</param>
    /// <param name="config">Compression configuration options</param>
    public TurboQuantClient(
        string baseUrl = "http://localhost:8080",
        TurboQuantConfig? config = null)
    {
        // Security: Validate base URL to prevent SSRF attacks
        if (!IsValidUrl(baseUrl))
            throw new ArgumentException("Invalid base URL format", nameof(baseUrl));
        
        _baseUrl = baseUrl.TrimEnd('/');
        _config = config ?? new TurboQuantConfig();
        
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    // ========================================================================
    // SECTION: URL Validation (Security - SSRF Prevention)
    // ========================================================================
    
    /// <summary>
    /// Validate URL to prevent SSRF attacks
    /// Only allows http/https schemes and non-private IP addresses
    /// </summary>
    private static bool IsValidUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        
        // Block private IP ranges to prevent internal network access
        if (uri.HostNameType == UriHostNameType.IPv4)
        {
            if (System.Net.IPAddress.TryParse(uri.Host, out var ip))
            {
                var bytes = ip.GetAddressBytes();
                // Block 10.x.x.x, 172.16-31.x.x, 192.168.x.x, 127.x.x.x
                if (bytes[0] == 10 || 
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168) ||
                    (bytes[0] == 127))
                    return false;
            }
        }
        
        // Block localhost variations
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;
        
        return true;
    }

    // ========================================================================
    // SECTION: Core Compression Operations
    // ========================================================================
    
    /// <summary>
    /// Compress KV cache using TurboQuant algorithms
    /// </summary>
    /// <param name="kvCache">Raw KV cache data (float array)</param>
    /// <param name="contextLength">Current context length in tokens</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Compression response with compressed data and metrics</returns>
    public async Task<CompressionResponse?> CompressAsync(
        float[] kvCache,
        int contextLength,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TurboQuantClient));
        if (kvCache == null || kvCache.Length == 0)
            return null;
        
        try
        {
            var request = new CompressionRequest
            {
                KvCache = kvCache,
                Config = _config,
                ContextLength = contextLength
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/compress", content, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
                return null;
            
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<CompressionResponse>(responseJson);
        }
        catch (HttpRequestException)
        {
            // Service unavailable - gracefully degrade without compression
            return null;
        }
        catch (TaskCanceledException)
        {
            // Timeout - return null to indicate compression skipped
            return null;
        }
    }

    // ========================================================================
    // SECTION: Health Check & Service Discovery
    // ========================================================================
    
    /// <summary>
    /// Check if TurboQuant service is available and healthy
    /// </summary>
    public async Task<bool> IsServiceAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;
        
        try
        {
            var response = await _httpClient.GetAsync("/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ========================================================================
    // SECTION: Configuration Management
    // ========================================================================
    
    /// <summary>
    /// Update compression configuration dynamically
    /// </summary>
    public void UpdateConfig(TurboQuantConfig newConfig)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TurboQuantClient));
        _config.KCacheType = newConfig.KCacheType;
        _config.VCacheType = newConfig.VCacheType;
        _config.BlockSize = newConfig.BlockSize;
        _config.EnableBoundaryProtection = newConfig.EnableBoundaryProtection;
        _config.BoundaryLayers = newConfig.BoundaryLayers;
        _config.EnableSparseV = newConfig.EnableSparseV;
    }

    /// <summary>
    /// Get current compression configuration
    /// </summary>
    public TurboQuantConfig GetConfig() => new()
    {
        KCacheType = _config.KCacheType,
        VCacheType = _config.VCacheType,
        BlockSize = _config.BlockSize,
        EnableBoundaryProtection = _config.EnableBoundaryProtection,
        BoundaryLayers = _config.BoundaryLayers,
        EnableSparseV = _config.EnableSparseV
    };

    // ========================================================================
    // SECTION: Resource Cleanup
    // ========================================================================
    
    public void Dispose()
    {
        if (_disposed) return;
        _httpClient.Dispose();
        _disposed = true;
    }
}
