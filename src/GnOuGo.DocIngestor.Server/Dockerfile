# ═══════════════════════════════════════════════════════════════
# GnOuGo.DocIngestor.Server — Multi-stage Dockerfile
#
#   Stage 1 (node)   : Build ClientApp (React + Vite → wwwroot)
#   Stage 2 (sdk)    : Restore + publish .NET app
#   Stage 3 (runtime): Final GnOuGo image
#
# Build (from repo root):
#   docker build -t gnougo-doc-ingestor -f src/GnOuGo.DocIngestor.Server/Dockerfile .
#
# Run:
#   docker run -p 5200:5200 \
#     -e OpenAi__ApiKey=sk-... \
#     -v docingestor-data:/app/data \
#     gnougo-doc-ingestor
# ═══════════════════════════════════════════════════════════════

# ──────────────────────────────────────────────
# Stage 1: Build the React ClientApp
# ──────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM node:24-alpine AS clientapp

ENV PNPM_HOME="/pnpm"
ENV PATH="$PNPM_HOME:$PATH"
RUN corepack enable

WORKDIR /workspace
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY src/GnOuGo.DocIngestor.Server/ClientApp/package.json src/GnOuGo.DocIngestor.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.DocIngestor.Server/ClientApp install --frozen-lockfile

COPY src/GnOuGo.DocIngestor.Server/ClientApp/ src/GnOuGo.DocIngestor.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.DocIngestor.Server/ClientApp build
# vite outDir: '../wwwroot' → output lands in /workspace/src/GnOuGo.DocIngestor.Server/wwwroot

# ──────────────────────────────────────────────
# Stage 2: Build & publish the .NET application
# ──────────────────────────────────────────────
# Use --platform=$BUILDPLATFORM to run the SDK natively on the build host.
# The publish output is framework-dependent (pure IL) — no RID needed.
# This avoids QEMU emulation which can crash during dotnet build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# bash + python3 are needed by the GeneratePricingCatalog target in GnOuGo.AI.Core
RUN apt-get update && apt-get install -y --no-install-recommends bash python3 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy global.json for SDK version pinning
COPY global.json ./

# Copy scripts needed by the Core pre-build target (model pricing generation)
COPY scripts/ scripts/

# Copy csproj files for layer caching (server + core dependency)
COPY src/GnOuGo.DocIngestor.Server/GnOuGo.DocIngestor.Server.csproj src/GnOuGo.DocIngestor.Server/
COPY src/GnOuGo.DocIngestor.Core/GnOuGo.DocIngestor.Core.csproj     src/GnOuGo.DocIngestor.Core/
COPY src/GnOuGo.AI.Core/GnOuGo.AI.Core.csproj                       src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/GnOuGo.Auth.Core.csproj                   src/GnOuGo.Auth.Core/

RUN dotnet restore src/GnOuGo.DocIngestor.Server/GnOuGo.DocIngestor.Server.csproj

# Copy full source
COPY src/GnOuGo.DocIngestor.Server/ src/GnOuGo.DocIngestor.Server/
COPY src/GnOuGo.DocIngestor.Core/   src/GnOuGo.DocIngestor.Core/
COPY src/GnOuGo.AI.Core/            src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/          src/GnOuGo.Auth.Core/

# Copy pre-built wwwroot from the Node stage
COPY --from=clientapp /workspace/src/GnOuGo.DocIngestor.Server/wwwroot src/GnOuGo.DocIngestor.Server/wwwroot/

# Publish (skip ClientApp build — already done in stage 1)
RUN dotnet publish src/GnOuGo.DocIngestor.Server/GnOuGo.DocIngestor.Server.csproj \
    -c Release \
    -o /app/publish \
    -p:SkipClientBuild=true \
    -p:SkipModelMetadataGeneration=true \
    --no-restore

# ──────────────────────────────────────────────
# Stage 3: Final runtime image
# ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# SkiaSharp (PDF rendering) needs native libs: fontconfig + libfreetype
# ICU is needed for non-invariant globalization
RUN apt-get update && apt-get install -y --no-install-recommends \
    libfontconfig1 \
    libfreetype6 \
    libicu-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Create data directory for SQLite store
RUN mkdir -p /app/data/store

# Copy published output
COPY --from=build /app/publish ./

EXPOSE 5200

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "--list-runtimes", "||", "exit", "1"]

# Volume for persistent data (SQLite vector store, ingested docs)
VOLUME ["/app/data"]

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5200 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

ENTRYPOINT ["dotnet", "GnOuGo.DocIngestor.Server.dll"]
