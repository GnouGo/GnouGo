# ===================================================================
# GnOuGo.Agent.Server - Multi-stage Dockerfile
#
#   Stage 1 (node)   : Build ClientApp (Vite + TS -> wwwroot/ui)
#   Stage 2 (sdk)    : Restore + publish .NET app
#   Stage 3 (runtime): Final GnOuGo image
#
# Build (from repo root):
#   docker build -t gnougo-agent -f src/GnOuGo.Agent.Server/Dockerfile .
#
# Run:
#   docker run -p 5000:5000 -e OpenAI__ApiKey=sk-... gnougo-agent
# ===================================================================
# ----------------------------------------------
# Stage 1: Build the ClientApp (Vite + TS)
# ----------------------------------------------
FROM --platform=$BUILDPLATFORM node:24-alpine AS clientapp
ENV PNPM_HOME="/pnpm"
ENV PATH="$PNPM_HOME:$PATH"
RUN corepack enable
WORKDIR /workspace
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY src/GnOuGo.Agent.Server/ClientApp/package.json src/GnOuGo.Agent.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.Agent.Server/ClientApp install --frozen-lockfile
COPY src/GnOuGo.Agent.Server/ClientApp/ src/GnOuGo.Agent.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.Agent.Server/ClientApp build
# vite outDir: '../wwwroot/ui' -> output lands in /workspace/src/GnOuGo.Agent.Server/wwwroot/ui
# ----------------------------------------------
# Stage 2: Build & publish the .NET application
# ----------------------------------------------
# Use --platform=$BUILDPLATFORM to run the SDK natively on the build host.
# The publish output is framework-dependent (pure IL) — no RID needed.
# This avoids QEMU emulation which can crash during dotnet build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Node.js is needed when publishing the bundled GnOuGo.Browser.Mcp Playwright tool.
RUN apt-get update -o Acquire::Retries=5 -o Acquire::http::Timeout=30 \
    && apt-get install -y --no-install-recommends -o Acquire::Retries=5 nodejs \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
# Copy global.json for SDK version pinning
COPY global.json ./

# Copy scripts used by local metadata generation; Docker publish skips this target.
COPY scripts/ scripts/

# Copy csproj files for layer caching (server + shared/project dependencies)
COPY src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj src/GnOuGo.Agent.Server/
COPY src/GnOuGo.Agent.Shared/GnOuGo.Agent.Shared.csproj src/GnOuGo.Agent.Shared/
COPY src/GnOuGo.Agent.Mcp/GnOuGo.Agent.Mcp.csproj src/GnOuGo.Agent.Mcp/
COPY src/GnOuGo.AI.Core/GnOuGo.AI.Core.csproj src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/GnOuGo.Auth.Core.csproj src/GnOuGo.Auth.Core/
COPY src/GnOuGo.Browser.Mcp/GnOuGo.Browser.Mcp.csproj src/GnOuGo.Browser.Mcp/
COPY ["src/GnOuGo.Cmd.Mcp/GnOuGo.Cmd.Mcp.csproj", "src/GnOuGo.Cmd.Mcp/"]
COPY src/GnOuGo.Diff.Core/GnOuGo.Diff.Core.csproj src/GnOuGo.Diff.Core/
COPY src/GnOuGo.Document.Mcp/GnOuGo.Document.Mcp.csproj src/GnOuGo.Document.Mcp/
COPY src/GnOuGo.DocIngestor.Core/GnOuGo.DocIngestor.Core.csproj src/GnOuGo.DocIngestor.Core/
COPY src/GnOuGo.DocIngestor.Mcp/GnOuGo.DocIngestor.Mcp.csproj src/GnOuGo.DocIngestor.Mcp/
COPY src/GnOuGo.Files.Server/GnOuGo.Files.Server.csproj src/GnOuGo.Files.Server/
COPY src/GnOuGo.Flow.Core/GnOuGo.Flow.Core.csproj src/GnOuGo.Flow.Core/
COPY src/GnOuGo.KeyVault.Core/GnOuGo.KeyVault.Core.csproj src/GnOuGo.KeyVault.Core/
COPY src/GnOuGo.KeyVault.Mcp/GnOuGo.KeyVault.Mcp.csproj src/GnOuGo.KeyVault.Mcp/
COPY src/GnOuGo.Observability.Core/GnOuGo.Observability.Core.csproj src/GnOuGo.Observability.Core/
COPY src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj src/GnOuGo.OtlpCollector.Server/
RUN dotnet restore src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj
# Copy full source
COPY src/ src/
# Overlay pre-built ClientApp output (wwwroot/ui) from stage 1.
# Other static assets (favicon.svg, assets/, js/) come from source.
COPY --from=clientapp /workspace/src/GnOuGo.Agent.Server/wwwroot/ui src/GnOuGo.Agent.Server/wwwroot/ui/
# Publish in Release (without AOT - container uses the managed runtime).
# SkipClientBuild=true avoids re-running pnpm inside dotnet publish.
# PublishAot=false overrides the csproj Release default for container builds.
# Do not use --no-restore here: the embedded OTLP collector downloads proto files
# during build, and Grpc.Tools must be allowed to refresh generated inputs in a
# clean Docker layer. The earlier restore is still useful for cache warming.
RUN dotnet publish src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj \
    -c Release \
    -o /app/publish \
    -p:SkipClientBuild=true \
    -p:PublishAot=false \
    -p:PublishTrimmed=false \
    -p:SkipModelMetadataGeneration=true
# ----------------------------------------------
# Stage 3: Final runtime image
# ----------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Copy published output
COPY --from=build /app/publish ./
EXPOSE 5000
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "--list-runtimes", "||", "exit", "1"]
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "GnOuGo.Agent.Server.dll"]