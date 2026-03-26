# ═══════════════════════════════════════════════════════════════
# GnOuGo.KeyVault.Server — Multi-stage Dockerfile
#
#   Stage 1 (node)   : Build ClientApp (React + Vite → wwwroot)
#   Stage 2 (sdk)    : Restore + publish .NET app
#   Stage 3 (runtime): Final GnOuGo image
#
# Build (from repo root):
#   docker build -t gnougo-keyvault -f src/GnOuGo.KeyVault.Server/Dockerfile .
#
# Run:
#   docker run -p 5300:5300 -v keyvault-data:/app/data gnougo-keyvault
# ═══════════════════════════════════════════════════════════════

# ──────────────────────────────────────────────
# Stage 1: Build the React ClientApp
# ──────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM node:24-alpine AS clientapp

WORKDIR /build/ClientApp
COPY src/GnOuGo.KeyVault.Server/ClientApp/package.json \
     src/GnOuGo.KeyVault.Server/ClientApp/package-lock.json* \
     ./
RUN npm ci --prefer-offline

COPY src/GnOuGo.KeyVault.Server/ClientApp/ ./
RUN npm run build
# vite outDir: '../wwwroot' → output lands in /build/wwwroot

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
COPY src/GnOuGo.KeyVault.Server/GnOuGo.KeyVault.Server.csproj src/GnOuGo.KeyVault.Server/
COPY src/GnOuGo.KeyVault.Core/GnOuGo.KeyVault.Core.csproj     src/GnOuGo.KeyVault.Core/

RUN dotnet restore src/GnOuGo.KeyVault.Server/GnOuGo.KeyVault.Server.csproj

# Copy source code
COPY src/GnOuGo.KeyVault.Server/ src/GnOuGo.KeyVault.Server/
COPY src/GnOuGo.KeyVault.Core/   src/GnOuGo.KeyVault.Core/

# Copy pre-built wwwroot from the Node stage
COPY --from=clientapp /build/wwwroot src/GnOuGo.KeyVault.Server/wwwroot/

# Publish (skip ClientApp build — already done in stage 1)
RUN dotnet publish src/GnOuGo.KeyVault.Server/GnOuGo.KeyVault.Server.csproj \
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

EXPOSE 5300

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["dotnet", "--list-runtimes", "||", "exit", "1"]

# Volume for persistent SQLite data
VOLUME ["/app/data"]

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5300

ENTRYPOINT ["dotnet", "GnOuGo.KeyVault.Server.dll"]

