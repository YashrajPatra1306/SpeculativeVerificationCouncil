# SpeculativeVerificationCouncil

A fully cloud-powered LLM agent architecture implementing a **Draft → Verify → Render** pipeline with parallel model council verification. All models run via Ollama cloud — no local model downloads required.

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│  User Query                                                          │
│     ↓                                                                │
│  [QueryClassifier] ── gpt-oss:120b-cloud ── auto-select strategy    │
│     ↓                                                                │
│  [Cloud Draft] ── gpt-oss:120b-cloud (temp=0.9, 200 tokens)        │
│     ↓                                                                │
│  [Verification Council] ── 3 parallel cloud requests (8s timeout)   │
│     ├── deepseek-v3.1:671b  (weight 2.0x, logic/reasoning)         │
│     ├── qwen3-coder:480b    (weight 1.5x, technical/code)          │
│     └── glm-4.6             (weight 1.0x, context/coherence)       │
│     ↓                                                                │
│  [Consensus Engine] ── Strategy Pattern (4 strategies)              │
│     ├── Strict:      Intersection (all must agree)                  │
│     ├── Weighted:    Union with >50% weighted threshold             │
│     ├── Adversarial: Contradiction detection + cloud arbiter        │
│     └── Fast:        Single fastest response (failover)             │
│     ↓                                                                │
│  [Reflection Loop] ── if confidence < 0.6, re-draft (max 2 iters)  │
│     ↓                                                                │
│  [Cloud Render] ── minimax-m2:cloud (structured fact injection)     │
│     ↓                                                                │
│  Final Verified Response                                             │
└──────────────────────────────────────────────────────────────────────┘
```

## Requirements

- **.NET 10 SDK** (preview) — or .NET 9 SDK (change `net10.0` to `net9.0` in `.csproj`)
- **Ollama** running with the following cloud models pulled:
  - `gpt-oss:120b-cloud` (draft, classification, reflection)
  - `deepseek-v3.1:671b-cloud` (council — logic/reasoning)
  - `qwen3-coder:480b-cloud` (council — technical/code)
  - `glm-4.6:cloud` (council — context/coherence)
  - `minimax-m2:cloud` (final rendering)
- **OLLAMA_API_KEY** environment variable required for all cloud models

## Build

### Debug (JIT)
```bash
dotnet build -c Debug
dotnet run
```

### Release (NativeAOT — single executable)
```bash
# Windows x64
dotnet publish -c Release -r win-x64 -p:PublishAot=true

# Linux x64
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

Output: `bin/Release/net10.0/<rid>/publish/SpeculativeVerificationCouncil` (single .exe, <20MB)

## Configuration

| Variable            | Default                      | Description                    |
|---------------------|------------------------------|--------------------------------|
| `OLLAMA_API_KEY`    | (none)                       | API key for Ollama cloud       |
| `OLLAMA_LOCAL_URL`  | `http://localhost:11434`     | Local Ollama endpoint          |
| `OLLAMA_CLOUD_URL`  | `https://api.ollama.com`     | Cloud Ollama endpoint          |

## CLI Commands

| Command         | Effect                                                      |
|-----------------|-------------------------------------------------------------|
| `!strict`       | Switch to Strict strategy (intersection consensus)          |
| `!weighted`     | Switch to Weighted strategy (union with threshold)          |
| `!adversarial`  | Switch to Adversarial strategy (contradiction + arbiter)    |
| `!fast`         | Switch to Fast strategy (single fastest response)           |
| `!auto`         | Auto-select strategy per query via gpt-oss:120b classifier  |
| `!status`       | Show engine status, token counts, and estimated cost        |
| `exit`          | Quit the application                                        |

## File Structure

```
SpeculativeVerificationCouncil/
├── Program.cs                          # CLI entry, REPL loop, rich display
├── AdaptiveVerificationEngine.cs       # Main Draft→Verify→Render orchestrator
├── IConsensusStrategy.cs               # Strategy pattern interface
├── Strategies/
│   ├── StrictStrategy.cs               # Intersection consensus
│   ├── WeightedStrategy.cs             # Weighted union consensus
│   ├── AdversarialStrategy.cs          # Contradiction detection + arbiter
│   └── FastStrategy.cs                 # Single fastest response
├── QueryClassifier.cs                  # Cloud intent detection (gpt-oss:120b)
├── OllamaClient.cs                     # Typed HTTP client (local + cloud)
├── ConsensusReport.cs                  # Data models + JSON source generators
├── VerificationVote.cs                 # Vote aggregation + Levenshtein dedup
├── ReflectionLoop.cs                   # Iterative improvement handler
└── SpeculativeVerificationCouncil.csproj
```

## Key Design Decisions

- **Zero external dependencies** — only .NET SDK base libraries
- **NativeAOT-safe JSON** — `System.Text.Json` source generators (no reflection)
- **ArrayPool buffer reuse** — minimizes GC pressure on the HTTP hot path
- **Levenshtein deduplication** — fuzzy-matches near-duplicate facts across models
- **Graceful degradation** — timeouts, parse failures, and network errors all have fallback paths
- **CancellationToken everywhere** — responsive Ctrl+C handling at every async boundary
