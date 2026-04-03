# Verification Council - Multi-Model Consensus Engine

A high-performance LLM verification system using speculative decoding principles with multiple AI models.

## Architecture

```
┌─────────────┐    ┌──────────────┐    ┌─────────────┐    ┌─────────────┐
│   Classify  │ →  │    Draft     │ →  │   Verify    │ →  │   Render    │
│  (Intent)   │    │  (Local LLM) │    │  (Council)  │    │  (Final)    │
└─────────────┘    └──────────────┘    └─────────────┘    └─────────────┘
                         │                   │
                         │              ┌────┴────┐
                         │              │Reflect? │
                         │              └────┬────┘
                         │                   │
                         └───────────────────┘
```

## Features

- **Multi-Model Verification**: Parallel validation by 3+ LLMs (DeepSeek, Qwen, GLM)
- **Consensus Strategies**: Strict, Weighted, Adversarial, Fast, Auto
- **Reflection Loop**: Auto-correction when confidence is low
- **Security Hardened**: SSRF prevention, prompt injection filtering, rate limiting
- **NativeAOT Ready**: Source-generated JSON parsing, zero-allocation hot paths
- **Cloud Integration**: TOON format (30-40% token reduction), Supabase storage, HF Spaces deployment

## Quick Start

```bash
# Set environment variables
export OLLAMA_API_KEY="your-api-key"
export OLLAMA_CLOUD_URL="https://api.ollama.com"

# Run
dotnet run
```

## Commands

| Command | Description |
|---------|-------------|
| `!strict` | All models must agree |
| `!weighted` | >50% weighted score threshold |
| `!adversarial` | Contradiction detection + arbiter |
| `!fast` | Single fastest response |
| `!auto` | Auto-select strategy per query |
| `!details` | Toggle detailed metrics view (default: minimal) |
| `!status` | Show engine status |
| `exit` | Quit application |

## UX Features

- **Minimal Output by Default**: Shows only answer and confidence score
- **Toggle Detailed View**: Use `!details` to see metrics, votes, costs
- **Clean Interface**: No clutter from tokens, latency, or cost info unless requested
- **Color-Coded Confidence**: Green (>70%), Yellow (>40%), Red (<40%)

## Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `OLLAMA_LOCAL_URL` | `http://localhost:11434` | Local Ollama endpoint |
| `OLLAMA_CLOUD_URL` | `https://api.ollama.com` | Cloud Ollama endpoint |
| `OLLAMA_API_KEY` | (none) | API key for cloud models |
| `SUPABASE_URL` | (none) | Supabase PostgreSQL URL |
| `SUPABASE_KEY` | (none) | Supabase API key |
| `HF_SPACE_URL` | (none) | Hugging Face Space URL |

## Project Structure

```
/workspace/
├── Program.cs                  # CLI entry point
├── OllamaClient.cs             # HTTP client for Ollama API
├── AdaptiveVerificationEngine.cs  # Main orchestrator
├── QueryClassifier.cs          # Intent classification
├── ReflectionLoop.cs           # Auto-correction loop
├── ConsensusReport.cs          # Report data structure
├── VerificationVote.cs         # Vote aggregation
├── IConsensusStrategy.cs       # Strategy interface
├── ToonConverter.cs            # JSON↔TOON conversion
├── SupabaseClient.cs           # Database storage
├── CloudDeploymentConfig.cs    # HF Spaces management
├── N8nWorkflowGenerator.cs     # n8n workflow generation
└── Strategies/
    ├── StrictStrategy.cs
    ├── WeightedStrategy.cs
    ├── AdversarialStrategy.cs
    └── FastStrategy.cs
```

## Security Features

- **SSRF Prevention**: URL allowlisting for endpoints
- **Prompt Injection Filtering**: Input sanitization before LLM calls
- **Rate Limiting**: Max 5 concurrent requests to prevent DoS
- **Input Validation**: Length limits, empty checks
- **Information Disclosure Reduction**: Generic model type mapping

## License

MIT
