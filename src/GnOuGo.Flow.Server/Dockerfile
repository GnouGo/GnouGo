# ═══════════════════════════════════════════════════════════════
# GnOuGo.Flow.Server — Multi-stage Dockerfile
#
#   Stage 1 (node)   : Build ClientApp (React + Vite → wwwroot)
#   Stage 2 (sdk)    : Restore + publish .NET app
#   Stage 3 (runtime): Final GnOuGo image
#
# Build (from repo root):
#   docker build -t gnougo-flow -f src/GnOuGo.Flow.Server/Dockerfile .
#
# Run:
#   docker run -p 5300:5300 gnougo-flow
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
COPY src/GnOuGo.Flow.Server/ClientApp/package.json src/GnOuGo.Flow.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.Flow.Server/ClientApp install --frozen-lockfile

COPY src/GnOuGo.Flow.Server/ClientApp/ src/GnOuGo.Flow.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.Flow.Server/ClientApp build
# vite outDir: '../wwwroot' → output lands in /workspace/src/GnOuGo.Flow.Server/wwwroot

# ──────────────────────────────────────────────
# Stage 2: Build & publish the .NET application
# ──────────────────────────────────────────────
# Use --platform=$BUILDPLATFORM to run the SDK natively on the build host.
# The publish output is framework-dependent (pure IL) — no RID needed.
# This avoids QEMU emulation which can crash during dotnet build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy global.json for SDK version pinning
COPY global.json ./

# Copy scripts used by local metadata generation; Docker publish skips this target.
COPY scripts/ scripts/

# Copy csproj files for layer caching (server + core dependency)
COPY src/GnOuGo.Flow.Server/GnOuGo.Flow.Server.csproj src/GnOuGo.Flow.Server/
COPY src/GnOuGo.Flow.Core/GnOuGo.Flow.Core.csproj     src/GnOuGo.Flow.Core/
COPY src/GnOuGo.AI.Core/GnOuGo.AI.Core.csproj         src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/GnOuGo.Auth.Core.csproj     src/GnOuGo.Auth.Core/

RUN dotnet restore src/GnOuGo.Flow.Server/GnOuGo.Flow.Server.csproj

# Copy full source
COPY src/GnOuGo.Flow.Server/ src/GnOuGo.Flow.Server/
COPY src/GnOuGo.Flow.Core/   src/GnOuGo.Flow.Core/
COPY src/GnOuGo.AI.Core/     src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/   src/GnOuGo.Auth.Core/

# Copy pre-built wwwroot from the Node stage
COPY --from=clientapp /workspace/src/GnOuGo.Flow.Server/wwwroot src/GnOuGo.Flow.Server/wwwroot/

# Publish (skip ClientApp build — already done in stage 1)
RUN dotnet publish src/GnOuGo.Flow.Server/GnOuGo.Flow.Server.csproj \
    -c Release \
    -o /app/publish \
    -p:SkipClientBuild=true \
    -p:SkipModelMetadataGeneration=true \
    --no-restore

# ──────────────────────────────────────────────
# Stage 3: Final runtime image
# ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

# Copy published output
COPY --from=build /app/publish ./

EXPOSE 5300

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "--list-runtimes", "||", "exit", "1"]

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5300

ENTRYPOINT ["dotnet", "GnOuGo.Flow.Server.dll"]
