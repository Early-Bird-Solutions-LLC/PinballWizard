# PinballWizard Api — multi-stage Dockerfile (ASP.NET Core JSON/SSE host).
#
# Hosts POST /api/wizard/ask:stream (SSE) and GET /api/wizard/landing.
# Called internally by PinballWizard.Web via Aspire service discovery
# (services__pinwiz-api__http__0 = 'http://pinwiz-ca-api-{env}' within
# the ACA environment). ACA ingress is set to internal-only (external=false).
#
# Build from the repo root:
#   az acr build --registry <acr> --image pinwiz-api:<sha> \
#                --file src/PinballWizard.Api/Dockerfile .
#
# The CI/CD deploy workflow (deploy.yml) always uses the explicit :{sha} tag.
# :latest is pushed as a convenience alias but is NEVER referenced in ACA
# container configuration (locked invariant #16 / CLAUDE.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/PinballWizard.Core/PinballWizard.Core.csproj                         src/PinballWizard.Core/
COPY src/PinballWizard.Application/PinballWizard.Application.csproj           src/PinballWizard.Application/
COPY src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj     src/PinballWizard.Infrastructure/
COPY src/PinballWizard.ServiceDefaults/PinballWizard.ServiceDefaults.csproj   src/PinballWizard.ServiceDefaults/
COPY src/PinballWizard.Api/PinballWizard.Api.csproj                           src/PinballWizard.Api/

RUN dotnet restore src/PinballWizard.Api/PinballWizard.Api.csproj

COPY src/ src/
RUN dotnet publish src/PinballWizard.Api/PinballWizard.Api.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN groupadd --system pinwiz \
    && useradd --system --no-create-home --gid pinwiz --shell /usr/sbin/nologin pinwiz
USER pinwiz

COPY --from=build --chown=pinwiz:pinwiz /app/publish .

# Runtime seed data: the landing endpoint reads
# data/seeds/wizard_seed_questions.v1.json and the refusal-recovery path
# reads community_resources.v1.json / featured_machines.v1.json, all
# resolved relative to the working directory (/app). Without this COPY
# the landing endpoint 500s on every call and the Web app silently
# serves its compiled-in fallback (observed live 2026-06-10).
COPY --chown=pinwiz:pinwiz data/seeds/ data/seeds/

# Declared but not promoted to ENV — silences Docker's "unused build-arg" warning
# when deploy.yml passes BUILD_SHA / BUILD_TIME to all matrix legs (only the web
# image uses them). See src/PinballWizard.Web/Dockerfile for the web-only ENV promotion.
ARG BUILD_SHA=""
ARG BUILD_TIME=""

# Internal ACA ingress: the Web app calls this service over plain HTTP within
# the ACA environment. ACA handles any TLS termination at the environment boundary.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

EXPOSE 8080

ENTRYPOINT ["dotnet", "PinballWizard.Api.dll"]
