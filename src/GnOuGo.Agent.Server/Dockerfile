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
WORKDIR /build/ClientApp
COPY src/GnOuGo.Agent.Server/ClientApp/package.json \
     src/GnOuGo.Agent.Server/ClientApp/package-lock.json* \
     ./
RUN npm ci --prefer-offline
COPY src/GnOuGo.Agent.Server/ClientApp/ ./
RUN npm run build
# vite outDir: '../wwwroot/ui' -> output lands in /build/wwwroot/ui
# ----------------------------------------------
# Stage 2: Build & publish the .NET application
# ----------------------------------------------
# Use --platform=$BUILDPLATFORM to run the SDK natively on the build host.
# The publish output is framework-dependent (pure IL) — no RID needed.
# This avoids QEMU emulation which can crash during dotnet build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# bash + python3 are needed by the GeneratePricingCatalog target in GnOuGo.AI.Core
RUN apt-get update && apt-get install -y --no-install-recommends bash python3 && rm -rf /var/lib/apt/lists/*

WORKDIR /src
# Copy global.json for SDK version pinning
COPY global.json ./

# Copy scripts needed by the AI.Core pre-build target (model pricing generation)
COPY scripts/ scripts/

# Copy csproj files for layer caching (server + shared/project dependencies)
COPY src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj src/GnOuGo.Agent.Server/
COPY src/GnOuGo.Agent.Shared/GnOuGo.Agent.Shared.csproj src/GnOuGo.Agent.Shared/
COPY src/GnOuGo.AI.Core/GnOuGo.AI.Core.csproj src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/GnOuGo.Auth.Core.csproj src/GnOuGo.Auth.Core/
COPY src/GnOuGo.Flow.Core/GnOuGo.Flow.Core.csproj src/GnOuGo.Flow.Core/
RUN dotnet restore src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj
# Copy full source
COPY src/GnOuGo.Agent.Server/ src/GnOuGo.Agent.Server/
COPY src/GnOuGo.Agent.Shared/ src/GnOuGo.Agent.Shared/
COPY src/GnOuGo.AI.Core/ src/GnOuGo.AI.Core/
COPY src/GnOuGo.Auth.Core/ src/GnOuGo.Auth.Core/
COPY src/GnOuGo.Flow.Core/ src/GnOuGo.Flow.Core/
# Overlay pre-built ClientApp output (wwwroot/ui) from stage 1.
# Other static assets (favicon.svg, assets/, js/) come from source.
COPY --from=clientapp /build/wwwroot/ui src/GnOuGo.Agent.Server/wwwroot/ui/
# Publish in Release (without AOT - container uses the managed runtime).
# SkipClientBuild=true avoids re-running npm inside dotnet publish.
# PublishAot=false overrides the csproj Release default for container builds.
RUN dotnet publish src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj \
    -c Release \
    -o /app/publish \
    -p:SkipClientBuild=true \
    -p:PublishAot=false \
    -p:PublishTrimmed=false \
    -p:SkipPricingGeneration=true \
    --no-restore
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