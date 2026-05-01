# ── Build stage ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PinballWizard.Scraper/PinballWizard.Scraper.csproj src/PinballWizard.Scraper/
RUN dotnet restore src/PinballWizard.Scraper/PinballWizard.Scraper.csproj
COPY src/ src/
RUN dotnet publish src/PinballWizard.Scraper -c Release -o /app --no-restore

# ── Runtime stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

# Install Playwright Chromium dependencies + cron
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        cron \
        libnss3 libnspr4 libdbus-1-3 libatk1.0-0 libatk-bridge2.0-0 \
        libcups2 libdrm2 libxkbcommon0 libatspi2.0-0 libxcomposite1 \
        libxdamage1 libxfixes3 libxrandr2 libgbm1 libpango-1.0-0 \
        libcairo2 libasound2 && \
    rm -rf /var/lib/apt/lists/*

# Install Playwright browsers
RUN dotnet PinballWizard.Scraper.dll --install-playwright

# Set up cron schedule
COPY crontab /etc/cron.d/pinball-wizard
RUN chmod 0644 /etc/cron.d/pinball-wizard && crontab /etc/cron.d/pinball-wizard

VOLUME /data
ENV DATA_PATH=/data

# Default: run cron in foreground
CMD ["cron", "-f"]
