# Build context is the repository root (see deploy/docker-compose.yml).
#
# Three build stages, because the site controller now ships three things that are built with
# three different toolchains: the .NET application, the Angular console it serves, and the pump
# firmware host build it launches one process of per dispenser. Only their outputs reach the
# runtime image.

# --- the operator console -----------------------------------------------------------------
FROM node:24-bookworm-slim AS web
WORKDIR /web
COPY web/forecourt-dashboard/package*.json ./
RUN npm ci
COPY web/forecourt-dashboard/ ./
RUN npm run build

# --- the pump firmware (the identical portable C core Phase 4 cross-compiles for a Cortex-M4) -
FROM debian:bookworm-slim AS firmware
RUN apt-get update \
 && apt-get install -y --no-install-recommends cmake gcc make libc6-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /fw
COPY firmware/pump/ ./
RUN cmake -S . -B build-host -DHAL=host && cmake --build build-host

# --- the site controller ---------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/OpenForecourt.SiteController/OpenForecourt.SiteController.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=web /web/dist/forecourt-dashboard/browser ./wwwroot
COPY --from=firmware /fw/build-host/pump_host ./firmware/pump_host
# Test card profiles and the test BDK. Reserved test PANs and the published X9.24 test key
# only — never real cardholder data or real keys (CLAUDE.md section 7).
COPY tests/testdata/cards ./cards
COPY tests/testdata/keys ./keys
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpenForecourt.SiteController.dll"]
