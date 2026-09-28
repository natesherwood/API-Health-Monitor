# syntax=docker/dockerfile:1
# One Dockerfile, two runtime targets: `api` and `worker` (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore in its own layer so source edits don't invalidate the package cache.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/RateMyApi.Core/RateMyApi.Core.csproj     src/RateMyApi.Core/
COPY src/RateMyApi.Data/RateMyApi.Data.csproj     src/RateMyApi.Data/
COPY src/RateMyApi.Worker/RateMyApi.Worker.csproj src/RateMyApi.Worker/
COPY src/RateMyApi.Api/RateMyApi.Api.csproj       src/RateMyApi.Api/
RUN dotnet restore src/RateMyApi.Api/RateMyApi.Api.csproj \
 && dotnet restore src/RateMyApi.Worker/RateMyApi.Worker.csproj

COPY migrations/ migrations/
COPY web/ web/
COPY src/ src/
RUN dotnet publish src/RateMyApi.Api/RateMyApi.Api.csproj       -c Release -o /out/api    --no-restore -p:UseAppHost=false \
 && dotnet publish src/RateMyApi.Worker/RateMyApi.Worker.csproj -c Release -o /out/worker --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS api
WORKDIR /app
COPY --from=build /out/api .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "RateMyApi.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
USER $APP_UID
ENTRYPOINT ["dotnet", "RateMyApi.Worker.dll"]
