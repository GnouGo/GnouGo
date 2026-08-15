# GnOuGo.AI.Local

Embedded, publishable local LLM runtime for GnOuGo. It uses LLamaSharp/llama.cpp in-process and never requires an HTTP model server, API key, Python installation, Docker container, or child process.

The initial catalog contains `qwen3:0.6b`, the Apache-2.0 Qwen3 0.6B Q4_0 GGUF. The 428,970,080-byte model is downloaded separately, from an immutable revision, and accepted only after SHA-256 verification.

- Revision: `a41486f827d17edd055fe6b3b0ba3f8d427c0519`
- SHA-256: `da2572f16c06133561ce56accaa822216f2391ef4d37fba427801cd6736417d4`
- Source: `https://huggingface.co/ggml-org/Qwen3-0.6B-GGUF/resolve/a41486f827d17edd055fe6b3b0ba3f8d427c0519/Qwen3-0.6B-Q4_0.gguf`

`LocalModelManager` downloads only catalog URLs, resumes `.partial` files, streams
progress, supports cancellation, checks path containment, verifies exact size and
SHA-256, and atomically promotes a completed file. Removal unloads the runtime
before deleting the model. Models are shared host assets under the workspace
`.GnOuGo/models/` directory; tenant-specific defaults remain in Agent MCP storage.

## Build and test

```powershell
dotnet build src/GnOuGo.AI.Local/GnOuGo.AI.Local.csproj
dotnet test tests/GnOuGo.AI.Local.Tests/GnOuGo.AI.Local.Tests.csproj
```

The standard tests are offline. Set `GNOUOGO_LOCAL_MODEL_SMOKE=1` and `GNOUOGO_LOCAL_MODEL_PATH` to run the real-model inference smoke test.

## Runtime defaults

- Context: 8192 tokens
- CPU threads: automatic
- Windows/Linux: CPU
- macOS ARM64: Metal through the LLamaSharp CPU backend
- Default output limit: 1024 tokens

The embedded GGUF chat template is used for prompting. JSON responses and
Qwen/Hermes tool-call envelopes are mapped into the common `Json` and `ToolCalls`
response fields. Thinking is disabled by default; when requested, reasoning content
is stripped and is never logged or returned.

CUDA and Vulkan acceleration are intentionally deferred.
