using SpeculativeVerificationCouncil;

// ════════════════════════════════════════════════════════════════════════════
//  VERIFICATION COUNCIL - Interactive CLI Entry Point
// ════════════════════════════════════════════════════════════════════════════
// NativeAOT-safe entry point for the multi-model consensus engine.
// Pipeline: Draft → Verify → Render with configurable strategies.
// 
// Commands: !strict !weighted !adversarial !fast !auto !details !status exit
// ════════════════════════════════════════════════════════════════════════════

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); WriteColored("\n[Ctrl+C] Cancelling...", ConsoleColor.Yellow); };

PrintBanner();

// ────────────────────────────────────────────────────────────────────────────
//  CONFIGURATION - Load from environment variables
// ────────────────────────────────────────────────────────────────────────────
string localUrl = Environment.GetEnvironmentVariable("OLLAMA_LOCAL_URL") ?? "http://localhost:11434";
string cloudUrl = Environment.GetEnvironmentVariable("OLLAMA_CLOUD_URL") ?? "https://api.ollama.com";
string? apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");

if (string.IsNullOrEmpty(apiKey))
    WriteColored("⚠ OLLAMA_API_KEY not set — cloud models will fail.", ConsoleColor.Yellow);

using var client = new OllamaClient(localUrl, cloudUrl, apiKey);
using var engine = new AdaptiveVerificationEngine(client);

// UX: Track detailed view preference
bool _showDetails = false;

WriteColored($"Local: {localUrl} | Cloud: {cloudUrl}", ConsoleColor.DarkGray);
WriteColored("Type a query or use: !strict !weighted !adversarial !fast !auto !details !status exit\n", ConsoleColor.DarkGray);

// ────────────────────────────────────────────────────────────────────────────
//  INTERACTIVE REPL - Main command loop
// ────────────────────────────────────────────────────────────────────────────
while (!cts.IsCancellationRequested)
{
    WriteColored("┌─[council]", ConsoleColor.Cyan);
    Console.Write("└─▸ ");
    Console.ForegroundColor = ConsoleColor.White;

    string? input;
    try { input = Console.ReadLine(); }
    catch (OperationCanceledException) { break; }

    Console.ResetColor();

    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        WriteColored("Goodbye.", ConsoleColor.DarkGray);
        break;
    }

    var trimmed = input.Trim();
    if (string.IsNullOrEmpty(trimmed)) continue;

    // ────────────────────────────────────────────────────────────────────────
    //  COMMAND HANDLING - Process ! commands
    // ────────────────────────────────────────────────────────────────────────
    if (trimmed.StartsWith('!'))
    {
        HandleCommand(trimmed, engine);
        continue;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  PROCESS QUERY - Run through verification pipeline
    // ────────────────────────────────────────────────────────────────────────
    try
    {
        Console.WriteLine();
        var report = await engine.ProcessQueryAsync(trimmed,
            onStatus: status => WriteColored($"  ◦ {status}", ConsoleColor.DarkGray),
            ct: cts.Token);
        RenderReport(report, _showDetails);
    }
    catch (OperationCanceledException) { WriteColored("\n[Cancelled]", ConsoleColor.Yellow); }
    catch (Exception ex) { WriteColored($"\n[Error] {ex.Message}", ConsoleColor.Red); }
    Console.WriteLine();
}

return;

// ════════════════════════════════════════════════════════════════════════════
//  CLI HELPERS - Command handling and report rendering
// ════════════════════════════════════════════════════════════════════════════

// ────────────────────────────────────────────────────────────────────────────
//  HANDLE COMMAND - Process strategy selection and status commands
//  UX: Added !details command to toggle verbose output
// ────────────────────────────────────────────────────────────────────────────
static void HandleCommand(string command, AdaptiveVerificationEngine engine)
{
    switch (command.ToLowerInvariant())
    {
        case "!strict":
            engine.CurrentStrategy = ConsensusStrategy.Strict;
            WriteColored("Strategy → Strict (all models must agree)", ConsoleColor.Green);
            break;
        case "!weighted":
            engine.CurrentStrategy = ConsensusStrategy.Weighted;
            WriteColored("Strategy → Weighted (>50% weighted score)", ConsoleColor.Green);
            break;
        case "!adversarial":
            engine.CurrentStrategy = ConsensusStrategy.Adversarial;
            WriteColored("Strategy → Adversarial (contradiction detection)", ConsoleColor.Green);
            break;
        case "!fast":
            engine.CurrentStrategy = ConsensusStrategy.Fast;
            WriteColored("Strategy → Fast (single fastest response)", ConsoleColor.Green);
            break;
        case "!auto":
            engine.CurrentStrategy = ConsensusStrategy.Auto;
            WriteColored("Strategy → Auto (classifier selects per-query)", ConsoleColor.Green);
            break;
        case "!details":
            _showDetails = !_showDetails;
            WriteColored($"Detailed view → {(_showDetails ? "ON" : "OFF")}", ConsoleColor.Green);
            break;
        case "!status":
            Console.WriteLine(engine.GetStatus());
            break;
        default:
            WriteColored($"Unknown command: {command}", ConsoleColor.Red);
            WriteColored("Available: !strict !weighted !adversarial !fast !auto !details !status exit", ConsoleColor.DarkGray);
            break;
    }
}

// ────────────────────────────────────────────────────────────────────────────
//  RENDER REPORT - Display verification results to console
//  UX: Shows only essential info by default. Use !details for full report.
// ────────────────────────────────────────────────────────────────────────────
static void RenderReport(ConsensusReport report, bool showDetails = false)
{
    Console.WriteLine();
    
    // SECTION: MINIMAL OUTPUT (Default)
    // Show only the final answer and confidence score for clean UX
    WriteColored("┌────────────────────────────────────────────────────────────┐", ConsoleColor.Cyan);
    WriteColored("│  ANSWER                                                    │", ConsoleColor.Cyan);
    WriteColored("└────────────────────────────────────────────────────────────┘", ConsoleColor.Cyan);
    
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"\n{report.FinalResponse}\n");
    Console.ResetColor();
    
    // Confidence indicator with color coding
    var confColor = report.OverallConfidence >= 0.7 ? ConsoleColor.Green :
                    report.OverallConfidence >= 0.4 ? ConsoleColor.Yellow : ConsoleColor.Red;
    WriteColored($"Confidence: {report.OverallConfidence:P0}", confColor);
    
    // SECTION: DETAILED OUTPUT (Only when requested via !details)
    // Shows metrics, votes, costs, and technical details
    if (showDetails)
    {
        Console.WriteLine();
        WriteColored("┌────────────────────────────────────────────────────────────┐", ConsoleColor.DarkCyan);
        WriteColored("│  DETAILED METRICS                                          │", ConsoleColor.DarkCyan);
        WriteColored("└────────────────────────────────────────────────────────────┘", ConsoleColor.DarkCyan);
        
        // Performance metrics
        Console.WriteLine();
        WriteLabel("Strategy", report.StrategyUsed.ToString());
        WriteLabel("Time", $"{report.TotalTime.TotalSeconds:F2}s");
        if (report.ReflectionIterations > 0)
            WriteLabel("Reflections", $"{report.ReflectionIterations} iteration(s)", ConsoleColor.Magenta);
        
        // Vote breakdown
        Console.WriteLine();
        WriteColored("── Council Votes ────────────────────────────────────────────", ConsoleColor.DarkCyan);
        foreach (var vote in report.Votes)
        {
            string status; ConsoleColor color;
            if (vote.TimedOut) { status = "TIMEOUT"; color = ConsoleColor.DarkYellow; }
            else if (vote.Error is not null) { status = "ERROR"; color = ConsoleColor.Red; }
            else if (vote.Result.Valid) { status = $"✓ VALID  conf:{vote.Result.Confidence:P0}"; color = ConsoleColor.Green; }
            else { status = $"✗ REJECT conf:{vote.Result.Confidence:P0}"; color = ConsoleColor.Red; }

            string modelType = GetModelType(vote.ModelName);
            Console.Write($"  [{vote.Weight:F1}x] ");
            WriteColored($"{modelType,-20}", ConsoleColor.White);
            Console.Write($" {vote.ResponseTime.TotalSeconds,5:F1}s  ");
            WriteColored(status, color);
        }
        
        // Dissent warnings
        if (report.DissentWarnings.Count > 0)
        {
            Console.WriteLine();
            WriteColored("── Warnings ─────────────────────────────────────────────", ConsoleColor.Yellow);
            foreach (var warning in report.DissentWarnings)
                WriteColored($"  ⚠ {warning}", ConsoleColor.Yellow);
        }
        
        // Cost info
        Console.WriteLine();
        var cost = report.Cost;
        WriteColored($"API calls: {cost.TotalApiCalls} | Est. cost: ${cost.EstimatedCostUsd:F4}", ConsoleColor.DarkGray);
    }
    else
    {
        // Hint user about detailed view
        WriteColored("\n💡 Type !details to see metrics, votes, and costs", ConsoleColor.DarkGray);
    }
}

// ────────────────────────────────────────────────────────────────────────────
//  GET MODEL TYPE - Map model names to generic types (security: reduce info disclosure)
// ────────────────────────────────────────────────────────────────────────────
static string GetModelType(string modelName)
{
    if (string.IsNullOrEmpty(modelName)) return "unknown";
    return modelName.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ? "deepseek" :
           modelName.Contains("qwen", StringComparison.OrdinalIgnoreCase) ? "qwen" :
           modelName.Contains("glm", StringComparison.OrdinalIgnoreCase) ? "glm" :
           modelName.Contains("cloud", StringComparison.OrdinalIgnoreCase) ? "cloud-model" :
           modelName.Split(':')[0];
}

// ────────────────────────────────────────────────────────────────────────────
//  CONSOLE HELPERS - Colored output utilities
// ────────────────────────────────────────────────────────────────────────────
static void WriteLabel(string label, string value, ConsoleColor valueColor = ConsoleColor.White)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"  {label}: ");
    Console.ForegroundColor = valueColor;
    Console.WriteLine(value);
    Console.ResetColor();
}

static void WriteColored(string text, ConsoleColor color)
{
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ResetColor();
}

// ────────────────────────────────────────────────────────────────────────────
//  PRINT BANNER - Display application header
// ────────────────────────────────────────────────────────────────────────────
static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""

    ╔═══════════════════════════════════════════════════════════════════╗
    ║   ___                 _      _   _           ___                ║
    ║  / __|_ __  ___ __ __| |__ _| |_(_)_ _____  | \\ \\/ /           ║
    ║  \\__ \\ '_ \\/ -_) _/ _| ' \\| |  _| \\ V / -_) |  >  <           ║
    ║  |___/ .__/\\___\\__\\__|_||_|_|\\__|_|\\_/\\___| |_/_/\\_\\           ║
    ║      |_|                                                        ║
    ║         Verification Council  v1.0  [NativeAOT]                 ║
    ║   Draft → Verify → Render  |  Multi-model consensus engine      ║
    ╚═══════════════════════════════════════════════════════════════════╝

    """);
    Console.ResetColor();
}
