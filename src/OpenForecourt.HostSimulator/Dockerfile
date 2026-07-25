# Build context is the repository root (see deploy/docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/OpenForecourt.HostSimulator/OpenForecourt.HostSimulator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 9583
ENTRYPOINT ["dotnet", "OpenForecourt.HostSimulator.dll", "serve", "--port", "9583"]
