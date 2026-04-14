# ═══════════════════════════════════════════════════════════════
# GnOuGo.OtlpCollector.Server — Multi-stage Dockerfile
#
#   Stage 1 (node)   : Build ClientApp (React + Vite → wwwroot)
#   Stage 2 (sdk)    : Restore + publish .NET app
#   Stage 3 (runtime): Final GnOuGo image
#
# Ports exposed:
#   4317 — gRPC  (OTLP/gRPC, HTTP/2)
#   4318 — HTTP  (OTLP/HTTP protobuf, REST API, Web UI)
#
# Build:
#   docker build -t gnougo-otlp-collector -f src/GnOuGo.OtlpCollector.Server/Dockerfile .
#
# Run:
#   docker run -p 4317:4317 -p 4318:4318 -v otlp-data:/app/data gnougo-otlp-collector
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
COPY src/GnOuGo.OtlpCollector.Server/ClientApp/package.json src/GnOuGo.OtlpCollector.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.OtlpCollector.Server/ClientApp install --frozen-lockfile

COPY src/GnOuGo.OtlpCollector.Server/ClientApp/ src/GnOuGo.OtlpCollector.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.OtlpCollector.Server/ClientApp build
# vite outDir: '../wwwroot' → output lands in /workspace/src/GnOuGo.OtlpCollector.Server/wwwroot

# ──────────────────────────────────────────────
# Stage 2: Build & publish the .NET application
# ──────────────────────────────────────────────
# Use --platform=$BUILDPLATFORM to run the SDK natively on the build host.
# The publish output is framework-dependent (pure IL) — no RID needed.
# This avoids QEMU emulation for protoc/grpc_csharp_plugin which segfault under QEMU.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Install system protoc (runs natively — no QEMU)
RUN apt-get update && apt-get install -y --no-install-recommends protobuf-compiler && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy global.json for SDK version pinning
COPY global.json ./

# Copy scripts needed by the AI.Core pre-build target (model pricing generation)
COPY scripts/ scripts/

# Copy csproj files for layer caching (server + dependency projects)
COPY src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj \
     src/GnOuGo.OtlpCollector.Server/
COPY src/GnOuGo.AI.Core/GnOuGo.AI.Core.csproj   src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/GnOuGo.Auth.Core.csproj src/GnOuGo.Auth.Core/

# Restore NuGet packages (proto files not present yet — that's fine,
# they are downloaded by the EnsureOtlpProtos target during build)
RUN dotnet restore src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj

# Copy the rest of the source (excluding ClientApp node_modules via .dockerignore)
COPY src/GnOuGo.OtlpCollector.Server/ src/GnOuGo.OtlpCollector.Server/
COPY src/GnOuGo.AI.Core/   src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/ src/GnOuGo.Auth.Core/

# Copy pre-built wwwroot from the Node stage
COPY --from=clientapp /workspace/src/GnOuGo.OtlpCollector.Server/wwwroot src/GnOuGo.OtlpCollector.Server/wwwroot/

# Publish (skip ClientApp build — already done in stage 1).
# NOTE: we intentionally omit --no-restore so that Grpc.Tools can properly
# discover proto files downloaded by the EnsureOtlpProtos target during build.
RUN dotnet publish src/GnOuGo.OtlpCollector.Server/GnOuGo.OtlpCollector.Server.csproj \
    -c Release \
    -o /app/publish \
    -p:SkipClientBuild=true \
    -p:SkipPricingGeneration=true \
    -p:Protobuf_ProtocPath=/usr/bin/protoc

# ──────────────────────────────────────────────
# Stage 3: Final runtime image
# ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install curl for healthcheck + ICU for globalization (non-invariant)
RUN apt-get update && apt-get install -y --no-install-recommends curl libicu-dev && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Create data directory for SQLite
RUN mkdir -p /app/data

# Copy published output
COPY --from=build /app/publish ./

# Expose OTLP ports
#   4317 = gRPC (HTTP/2)
#   4318 = HTTP (HTTP/1.1 + HTTP/2) — API, OTLP/HTTP, Web UI
EXPOSE 4317
EXPOSE 4318

# Health check — Web UI / HTTP endpoint
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:4318/admin/config || exit 1

# Volume for persistent SQLite data
VOLUME ["/app/data"]

# Environment defaults
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

ENTRYPOINT ["dotnet", "GnOuGo.OtlpCollector.Server.dll"]
