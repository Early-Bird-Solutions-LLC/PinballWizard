# PinballWizard CLI — multi-stage Dockerfile (command-line entrypoint).
#
# Unlike Web/Api (long-running ASP.NET hosts) the CLI is a one-shot console
# tool: it runs a verb (e.g. `--link-documents`, `--source opdb`) to completion
# and exits. Its deploy target is therefore ACA *Jobs*, not ACA *Apps* — the
# nightly document-linker job and the weekly OPDB sync job both run this image.
# Each ACA Job supplies the full `command`/`args` (see deploy/linker-job and
# deploy/opdb-sync-job), so the ENTRYPOINT below is only the bare `dotnet
# PinballWizard.Cli.dll` invocation; the job appends the verb.
#
# Built from the repo root (NOT this folder) so the build context can see
# Directory.Packages.props + every project the CLI references.
# Build command (run from repo root):
#   docker build -t pinwiz-cli:<sha> -f src/PinballWizard.Cli/Dockerfile .
#
# The CI/CD deploy workflow (deploy.yml) builds + pushes this image as
# `pinwiz-cli:<sha>` (build-only matrix leg — no `az containerapp update`,
# because Jobs pick up a new image on the next Deployment Stack run via
# Deploy-SharedResources.ps1, not via an app revision swap). :latest is pushed
# as a convenience alias but ACA Job configuration ALWAYS uses :{sha}
# (locked invariant #16 / CLAUDE.md).

# Runtime base is `aspnet`, NOT `runtime`: the CLI references
# PinballWizard.ServiceDefaults, which takes `<FrameworkReference
# Include="Microsoft.AspNetCore.App" />` (shared OTel + health + service
# discovery). That framework reference flows transitively into the CLI's
# runtimeconfig, so the published app demands the ASP.NET shared framework at
# launch even though the CLI itself opens no HTTP socket. On the bare
# `dotnet/runtime` image it fails immediately with "No frameworks were found"
# (verified locally). `aspnet` ships both the .NET and ASP.NET shared
# frameworks; it is the correct minimal base for any app referencing
# ServiceDefaults.
ARG DOTNET_VERSION=10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}

FROM ${SDK_IMAGE} AS build
WORKDIR /src

# Restore stage uses copy-then-restore to maximize layer cache hits on
# package changes. Copying every csproj + props file first lets `restore`
# warm the package cache; the source copy (and the slow build) only
# reruns when actual source changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/PinballWizard.Core/PinballWizard.Core.csproj src/PinballWizard.Core/
COPY src/PinballWizard.Application/PinballWizard.Application.csproj src/PinballWizard.Application/
COPY src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj src/PinballWizard.Infrastructure/
COPY src/PinballWizard.ServiceDefaults/PinballWizard.ServiceDefaults.csproj src/PinballWizard.ServiceDefaults/
COPY src/PinballWizard.Cli/PinballWizard.Cli.csproj src/PinballWizard.Cli/

RUN dotnet restore src/PinballWizard.Cli/PinballWizard.Cli.csproj

# Source copy + publish.
COPY src/ src/
RUN dotnet publish src/PinballWizard.Cli/PinballWizard.Cli.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Runtime stage. The CLI is a console tool with no HTTP ingress (the ACA Jobs
# that run this image omit ingress entirely), but it still needs the ASP.NET
# shared framework — see the RUNTIME_IMAGE comment at the top of the file.
FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app

# Run as a non-root user (defense-in-depth). Let the system assign the
# UID/GID — the dotnet/runtime base image already occupies GID 1000, so
# pinning --gid 1000 fails the build ("GID '1000' already exists"). Matches
# the working api/web/worker Dockerfiles, which create the system user
# without explicit id pins.
RUN groupadd --system pinwiz \
    && useradd --system --no-create-home --gid pinwiz --shell /usr/sbin/nologin pinwiz
USER pinwiz

COPY --from=build --chown=pinwiz:pinwiz /app/publish .

# Declared but not promoted to ENV — silences Docker's "unused build-arg" warning
# when deploy.yml passes BUILD_SHA / BUILD_TIME to all matrix legs.
ARG BUILD_SHA=""
ARG BUILD_TIME=""

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

ENTRYPOINT ["dotnet", "PinballWizard.Cli.dll"]
