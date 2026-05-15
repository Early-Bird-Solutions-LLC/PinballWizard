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

ARG DOTNET_VERSION=10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}

FROM ${SDK_IMAGE} AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
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

FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app

RUN groupadd --system pinwiz \
    && useradd --system --no-create-home --gid pinwiz --shell /usr/sbin/nologin pinwiz
USER pinwiz

COPY --from=build --chown=pinwiz:pinwiz /app/publish .

# Internal ACA ingress: the Web app calls this service over plain HTTP within
# the ACA environment. ACA handles any TLS termination at the environment boundary.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_USE_POLLING_FILE_WATCHER=false

EXPOSE 8080

ENTRYPOINT ["dotnet", "PinballWizard.Api.dll"]
