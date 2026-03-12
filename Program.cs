using SpeculativeVerificationCouncil;

// ── NativeAOT-safe entry point ─────────────────────────────────────────────

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    WriteColored("\n[Ctrl+C] Cancelling...", ConsoleColor.Yellow);
};

PrintBanner();

// ── Configuration ──────────────────────────────────────────────────────────

string localUrl = Environment.GetEnvironmentVariable("OLLAMA_LOCAL_URL") ?? "http://localhost:11434";
string cloudUrl = Environment.GetEnvironmentVariable("OLLAMA_CLOUD_URL") ?? "https://api.ollama.com";
string? apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");

if (string.IsNullOrEmpty(apiKey))
{
    WriteColored("⚠ OLLAMA_API_KEY not set — cloud models will fail. Set it for full council support.", ConsoleColor.Yellow);
    Console.WriteLine();
}

using var client = new OllamaClient(localUrl, cloudUrl, apiKey);
using var engine = new AdaptiveVerificationEngine(client);

WriteColored($"Local: {localUrl} | Cloud: {cloudUrl}", ConsoleColor.DarkGray);
WriteColored("Type a query, or use !strict !weighted !adversarial !fast !auto !status exit\n", ConsoleColor.DarkGray);

// ── Interactive REPL ───────────────────────────────────────────────────────

while (!cts.IsCancellationRequested)
{
    WriteColored("┌─[council]", ConsoleColor.Cyan);
    Console.Write("└─▸ ");
    Console.ForegroundColor = ConsoleColor.White;

    string? input;
    try
    {
        input = Console.ReadLine();
    }
    catch (OperationCanceledException)
    {
        break;
    }

    Console.ResetColor();

    if (input is null || string.Equals(input.Trim(), "exit", StringComparison.OrdinalIgnoreCase))
    {
        WriteColored("Goodbye.", ConsoleColor.DarkGray);
        break;
    }

    var trimmed = input.Trim();
    if (string.IsNullOrEmpty(trimmed)) continue;

    // ── Command handling ───────────────────────────────────────────────
    if (trimmed.StartsWith('!'))
    {
        HandleCommand(trimmed, engine);
        continue;
    }

    // ── Process query ──────────────────────────────────────────────────
    try
    {
        Console.WriteLine();
        var report = await engine.ProcessQueryAsync(
            trimmed,
            onStatus: status =>
            {
                WriteColored($"  ◦ {status}", ConsoleColor.DarkGray);
            },
            ct: cts.Token);

        RenderReport(report);
    }
    catch (OperationCanceledException)
    {
        WriteColored("\n[Cancelled]", ConsoleColor.Yellow);
    }
    catch (Exception ex)
    {
        WriteColored($"\n[Error] {ex.Message}", ConsoleColor.Red);
    }

    Console.WriteLine();
}

return;

// ════════════════════════════════════════════════════════════════════════════
//  CLI helpers
// ════════════════════════════════════════════════════════════════════════════

static void HandleCommand(string command, AdaptiveVerificationEngine engine)
{
    switch (command.ToLowerInvariant())
    {
        case "!strict":
            engine.CurrentStrategy = ConsensusStrategy.Strict;
            WriteColored("Strategy → Strict (intersection, all models must agree)", ConsoleColor.Green);
            break;
        case "!weighted":
            engine.CurrentStrategy = ConsensusStrategy.Weighted;
            WriteColored("Strategy → Weighted (union with >50% weighted score threshold)", ConsoleColor.Green);
            break;
        case "!adversarial":
            engine.CurrentStrategy = ConsensusStrategy.Adversarial;
            WriteColored("Strategy → Adversarial (contradiction detection + arbiter)", ConsoleColor.Green);
            break;
        case "!fast":
            engine.CurrentStrategy = ConsensusStrategy.Fast;
            WriteColored("Strategy → Fast (single fastest response, failover mode)", ConsoleColor.Green);
            break;
        case "!auto":
            engine.CurrentStrategy = ConsensusStrategy.Auto;
            WriteColored("Strategy → Auto (TinyLlama classifier selects per-query)", ConsoleColor.Green);
            break;
        case "!status":
            Console.WriteLine(engine.GetStatus());
            break;
        default:
            WriteColored($"Unknown command: {command}", ConsoleColor.Red);
            WriteColored("Available: !strict !weighted !adversarial !fast !auto !status exit", ConsoleColor.DarkGray);
            break;
    }
}

static void RenderReport(ConsensusReport report)
{
    Console.WriteLine();

    // Header bar
    WriteColored("╔══════════════════════════════════════════════════════════════╗", ConsoleColor.Cyan);
    WriteColored("║                  VERIFICATION COUNCIL REPORT                ║", ConsoleColor.Cyan);
    WriteColored("╚══════════════════════════════════════════════════════════════╝", ConsoleColor.Cyan);
    Console.WriteLine();

    // Metadata
    WriteLabel("Intent", report.DetectedIntent.ToString());
    WriteLabel("Strategy", report.StrategyUsed.ToString());
    WriteLabel("Confidence", $"{report.OverallConfidence:P0}",
        report.OverallConfidence >= 0.7 ? ConsoleColor.Green :
        report.OverallConfidence >= 0.4 ? ConsoleColor.Yellow :
        ConsoleColor.Red);
    WriteLabel("Time", $"{report.TotalTime.TotalSeconds:F2}s");

    if (report.ReflectionIterations > 0)
        WriteLabel("Reflections", $"{report.ReflectionIterations} iteration(s)", ConsoleColor.Magenta);

    Console.WriteLine();

    // Vote distribution
    WriteColored("── Council Votes ──────────────────────────────────────────────", ConsoleColor.DarkCyan);
    foreach (var vote in report.Votes)
    {
        string status;
        ConsoleColor color;

        if (vote.TimedOut)
        {
            status = "TIMEOUT";
            color = ConsoleColor.DarkYellow;
        }
        else if (vote.Error is not null)
        {
            status = $"ERROR: {vote.Error}";
            color = ConsoleColor.Red;
        }
        else if (vote.Result.Valid)
        {
            status = $"✓ VALID  conf:{vote.Result.Confidence:P0}  facts:{vote.Result.Facts?.Count ?? 0}";
            color = ConsoleColor.Green;
        }
        else
        {
            status = $"✗ REJECT conf:{vote.Result.Confidence:P0}  corrections:{vote.Result.Corrections?.Count ?? 0}";
            color = ConsoleColor.Red;
        }

        string modelShort = vote.ModelName.Length > 30
            ? vote.ModelName[..30] + "…"
            : vote.ModelName;

        Console.Write($"  [{vote.Weight:F1}x] ");
        WriteColored($"{modelShort,-32}", ConsoleColor.White);
        Console.Write($" {vote.ResponseTime.TotalSeconds,5:F1}s  ");
        WriteColored(status, color);
    }
    Console.WriteLine();

    // Dissent warnings
    if (report.DissentWarnings.Count > 0)
    {
        WriteColored("── Dissent & Warnings ─────────────────────────────────────────", ConsoleColor.Yellow);
        foreach (var warning in report.DissentWarnings)
        {
            WriteColored($"  ⚠ {warning}", ConsoleColor.Yellow);
        }
        Console.WriteLine();
    }

    // Verified facts
    WriteColored($"── Verified Facts ({report.VerifiedFacts.Count}) ──────────────────────────────────", ConsoleColor.DarkCyan);
    if (report.VerifiedFacts.Count == 0)
    {
        WriteColored("  (none — draft used as-is)", ConsoleColor.DarkGray);
    }
    else
    {
        for (int i = 0; i < report.VerifiedFacts.Count; i++)
        {
            string fact = report.VerifiedFacts[i];
            if (fact.Length > 100)
                fact = fact[..97] + "...";
            WriteColored($"  {i + 1}. {fact}", ConsoleColor.White);
        }
    }
    Console.WriteLine();

    // Final response
    WriteColored("── Final Response ─────────────────────────────────────────────", ConsoleColor.Green);
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"  {report.FinalResponse}");
    Console.ResetColor();
    Console.WriteLine();

    // Cost footer
    var cost = report.Cost;
    WriteColored(
        $"  tokens: {cost.LocalTokens} local + {cost.CloudTokens} cloud | " +
        $"calls: {cost.TotalApiCalls} | " +
        $"est. cost: ${cost.EstimatedCostUsd:F4}",
        ConsoleColor.DarkGray);
}

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

static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""

    ╔═══════════════════════════════════════════════════════════════════╗
    ║   ___                 _      _   _           ___                ║
    ║  / __|_ __  ___ __ __| |__ _| |_(_)_ _____  | \ \/ /           ║
    ║  \__ \ '_ \/ -_) _/ _| ' \| |  _| \ V / -_) |  >  <           ║
    ║  |___/ .__/\___\__\__|_||_|_|\__|_|\_/\___| |_/_/\_\           ║
    ║      |_|                                                        ║
    ║         Verification Council  v1.0  [NativeAOT]                 ║
    ║   Draft → Verify → Render  |  Multi-model consensus engine      ║
    ╚═══════════════════════════════════════════════════════════════════╝

    """);
    Console.ResetColor();
}
