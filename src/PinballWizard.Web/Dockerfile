# PinballWizard Web — multi-stage Dockerfile (Blazor Web App, auto-render mode).
#
# Build from the repo root so the build context includes Directory.Packages.props
# and every referenced project:
#   az acr build --registry <acr> --image pinwiz-web:<sha> \
#                --file src/PinballWizard.Web/Dockerfile .
#
# The CI/CD deploy workflow (deploy.yml) always uses the explicit :{sha} tag.
# :latest is pushed as a convenience alias but is NEVER referenced in ACA
# container configuration (locked invariant #16 / CLAUDE.md).
#
# AzureAd note: AddMicrosoftIdentityWebApp validates Instance + TenantId +
# ClientId at startup even on anonymous routes. The three ENV vars below
# contain placeholder GUIDs that satisfy the validator without enabling real
# OIDC — no token exchange occurs on anonymous public routes. Real tenant
# values arrive as ACA environment variables from the Bicep / Key Vault at
# deploy time and override these defaults.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore stage: copy only csproj + props files first to maximise Docker
# layer cache hits. The slow restore only re-runs when package dependencies
# change, not on every source edit.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/PinballWizard.Core/PinballWizard.Core.csproj                         src/PinballWizard.Core/
COPY src/PinballWizard.Application/PinballWizard.Application.csproj           src/PinballWizard.Application/
COPY src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj     src/PinballWizard.Infrastructure/
COPY src/PinballWizard.ServiceDefaults/PinballWizard.ServiceDefaults.csproj   src/PinballWizard.ServiceDefaults/
COPY src/PinballWizard.Web.Client/PinballWizard.Web.Client.csproj             src/PinballWizard.Web.Client/
COPY src/PinballWizard.Web/PinballWizard.Web.csproj                           src/PinballWizard.Web/

RUN dotnet restore src/PinballWizard.Web/PinballWizard.Web.csproj

# Source copy + publish. dotnet publish of the Web project automatically
# pulls in the WASM bootstrap artifacts from Web.Client (via
# StaticWebAssetProjectMode) so the runtime image can serve WASM without
# a separate build step.
COPY src/ src/
# PinballWizard.Web embeds engineering docs as resources (Web.csproj: the
# manifest, five named docs, and docs/adr/*.md). .dockerignore re-includes
# exactly that set under docs/, so copying the whole (already-filtered) docs/
# tree brings in precisely what `dotnet publish` must embed — and stays correct
# as ADRs are added. Without this COPY: CS1566 at publish (2026-07-07 break).
COPY docs/ docs/
RUN dotnet publish src/PinballWizard.Web/PinballWizard.Web.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Runtime stage — aspnet (not dotnet/runtime) because Kestrel is the HTTP server.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN groupadd --system pinwiz \
    && useradd --system --no-create-home --gid pinwiz --shell /usr/sbin/nologin pinwiz
USER pinwiz

COPY --from=build --chown=pinwiz:pinwiz /app/publish .

# Build metadata injected by the CI/CD pipeline (deploy.yml build-args).
# Defaults are empty so local builds succeed without these args being supplied.
# Declared here (final runtime stage only) so the web image is the single owner;
# the other Dockerfiles declare the same ARGs (no ENV) to silence Docker's
# "unused build-arg" warning when deploy.yml passes them to every matrix leg.
ARG BUILD_SHA=""
ARG BUILD_TIME=""

# ACA terminates TLS and forwards plain HTTP to port 8080.
# ASPNETCORE_FORWARDEDHEADERS_ENABLED lets Kestrel trust the X-Forwarded-*
# headers that ACA injects so redirect URIs and scheme detection work correctly.
# UseHttpsRedirection + UseHsts are disabled in Program.cs (ACA handles TLS).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false \
    AzureAd__Instance=https://login.microsoftonline.com/ \
    AzureAd__TenantId=00000000-0000-0000-0000-000000000000 \
    AzureAd__ClientId=00000000-0000-0000-0000-000000000000 \
    PINWIZ_BUILD_SHA=$BUILD_SHA \
    PINWIZ_BUILD_TIME=$BUILD_TIME

EXPOSE 8080

ENTRYPOINT ["dotnet", "PinballWizard.Web.dll"]
