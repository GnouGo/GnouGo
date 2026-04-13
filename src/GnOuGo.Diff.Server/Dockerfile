# ═══════════════════════════════════════════════════════════════
# GnOuGo.Diff.Server — Multi-stage Dockerfile
#
#   Stage 1 (node)   : Build ClientApp (React + Vite → wwwroot)
#   Stage 2 (sdk)    : Restore + publish .NET app
#   Stage 3 (runtime): Final GnOuGo image
#
# Build (from repo root):
#   docker build -t gnougo-diff -f src/GnOuGo.Diff.Server/Dockerfile .
#
# Run:
#   docker run -p 5100:5100 -v gnougo-diff-data:/app/data gnougo-diff
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
COPY src/GnOuGo.Diff.Server/ClientApp/package.json src/GnOuGo.Diff.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.Diff.Server/ClientApp install --frozen-lockfile

COPY src/GnOuGo.Diff.Server/ClientApp/ src/GnOuGo.Diff.Server/ClientApp/
RUN pnpm --dir src/GnOuGo.Diff.Server/ClientApp build
# vite outDir: '../wwwroot' → output lands in /workspace/src/GnOuGo.Diff.Server/wwwroot

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

# Copy csproj files for layer caching (server + core dependency)
COPY src/GnOuGo.Diff.Server/GnOuGo.Diff.Server.csproj src/GnOuGo.Diff.Server/
COPY src/GnOuGo.Diff.Core/GnOuGo.Diff.Core.csproj     src/GnOuGo.Diff.Core/

RUN dotnet restore src/GnOuGo.Diff.Server/GnOuGo.Diff.Server.csproj

# Copy full source
COPY src/GnOuGo.Diff.Server/ src/GnOuGo.Diff.Server/
COPY src/GnOuGo.Diff.Core/   src/GnOuGo.Diff.Core/

# Copy pre-built wwwroot from the Node stage
COPY --from=clientapp /workspace/src/GnOuGo.Diff.Server/wwwroot src/GnOuGo.Diff.Server/wwwroot/

# Publish (skip ClientApp build — already done in stage 1)
RUN dotnet publish src/GnOuGo.Diff.Server/GnOuGo.Diff.Server.csproj \
    -c Release \
    -o /app/publish \
    -p:SkipClientBuild=true \
    --no-restore

# ──────────────────────────────────────────────
# Stage 3: Final runtime image
# ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

# Create data directory for SQLite
RUN mkdir -p /app/data

# Copy published output
COPY --from=build /app/publish ./

EXPOSE 5100

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "--list-runtimes", "||", "exit", "1"]

# Volume for persistent SQLite data
VOLUME ["/app/data"]

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5100

ENTRYPOINT ["dotnet", "GnOuGo.Diff.Server.dll"]
