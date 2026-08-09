# PinballWizard RAG Ingestion Worker — multi-stage Dockerfile.
#
# Targets the .NET 10 / C# 14 toolchain pinned in Directory.Build.props.
# Built from the repo root (NOT this folder) so the build context can
# see Directory.Packages.props + every project the worker references.
# Build command (run from repo root):
#   docker build -t pinwiz-rag-indexer:<sha> -f src/PinballWizard.RagIngestionWorker/Dockerfile .
#
# Per memory/feedback_compute_on_container_apps.md the deploy target is
# Azure Container Apps with the KEDA Cosmos scaler — NOT a standalone
# Function App. The Bicep already wires the Container App resource
# with a placeholder image; an operator runs:
#   az acr build --registry <acr> --image pinwiz-rag-indexer:<sha> .
#   az containerapp update -n <ca-name> -g <rg> --image <acr>/pinwiz-rag-indexer:<sha>
# to swap this image in.

# Runtime base is `aspnet`, NOT `runtime`: the worker references
# PinballWizard.ServiceDefaults, which takes `<FrameworkReference
# Include="Microsoft.AspNetCore.App" />` (shared OTel + health + service
# discovery). That framework reference flows transitively into the worker's
# runtimeconfig, so the published app demands the ASP.NET shared framework at
# launch even though the worker opens no HTTP socket (KEDA/Change-Feed driven,
# ingress disabled). On the bare `dotnet/runtime` image it fails immediately
# with "No frameworks were found" (verified locally) — a latent bug that never
# surfaced while #396 was inert and the image never actually ran.
ARG DOTNET_VERSION=10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}

FROM ${SDK_IMAGE} AS build
WORKDIR /src

# Restore stage uses copy-then-restore to maximize layer cache hits on
# package changes. Copying every csproj + props file first lets `restore`
# warm the package cache; the source copy (and the slow build) only
# reruns when actual source changes.
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/PinballWizard.Core/PinballWizard.Core.csproj src/PinballWizard.Core/
COPY src/PinballWizard.Application/PinballWizard.Application.csproj src/PinballWizard.Application/
COPY src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj src/PinballWizard.Infrastructure/
COPY src/PinballWizard.ServiceDefaults/PinballWizard.ServiceDefaults.csproj src/PinballWizard.ServiceDefaults/
COPY src/PinballWizard.RagIngestionWorker/PinballWizard.RagIngestionWorker.csproj src/PinballWizard.RagIngestionWorker/

RUN dotnet restore src/PinballWizard.RagIngestionWorker/PinballWizard.RagIngestionWorker.csproj

# Source copy + publish.
COPY src/ src/
RUN dotnet publish src/PinballWizard.RagIngestionWorker/PinballWizard.RagIngestionWorker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Runtime stage. The worker is a console host with no HTTP ingress
# (KEDA-Cosmos-driven; the ACA resource has `ingress` omitted = disabled), but
# it still needs the ASP.NET shared framework — see the RUNTIME_IMAGE comment
# at the top of the file.
FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app

# Run as a non-root user. ACA's default seccomp profile + the Container
# App ingress disabled mean network exposure is minimal, but defense-in-
# depth costs nothing here.
# Let the system assign the UID/GID — the dotnet/runtime base image
# already occupies GID 1000, so pinning --gid 1000 fails the build
# ("GID '1000' already exists"). Matches the working api/web Dockerfiles,
# which create the system user without explicit id pins.
RUN groupadd --system pinwiz \
    && useradd --system --no-create-home --gid pinwiz --shell /usr/sbin/nologin pinwiz
USER pinwiz

COPY --from=build --chown=pinwiz:pinwiz /app/publish .

# Declared but not promoted to ENV — silences Docker's "unused build-arg" warning
# when deploy.yml passes BUILD_SHA / BUILD_TIME to all matrix legs.
ARG BUILD_SHA=""
ARG BUILD_TIME=""

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

ENTRYPOINT ["dotnet", "PinballWizard.RagIngestionWorker.dll"]
