# syntax=docker/dockerfile:1

# --- Build -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first for better layer caching.
COPY HoloFanStudio.sln ./
COPY src/HoloFan.Core/HoloFan.Core.csproj src/HoloFan.Core/
COPY src/HoloFan.Device/HoloFan.Device.csproj src/HoloFan.Device/
COPY src/HoloFan.Web/HoloFan.Web.csproj src/HoloFan.Web/
COPY tests/HoloFan.Tests/HoloFan.Tests.csproj tests/HoloFan.Tests/
RUN dotnet restore src/HoloFan.Web/HoloFan.Web.csproj

COPY . .
RUN dotnet publish src/HoloFan.Web/HoloFan.Web.csproj -c Release -o /app --no-restore

# --- Runtime -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# ffmpeg + ffprobe are the actual video engine.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# Build identity (passed by CI) so /api/version can report exactly which commit is running.
ARG GIT_COMMIT=dev
ARG BUILD_DATE=""
ENV HOLOFAN_COMMIT=$GIT_COMMIT \
    HOLOFAN_BUILT_AT=$BUILD_DATE

# Rendered files live on a volume so they survive container restarts.
ENV HOLOFAN_DATA=/data \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
RUN mkdir -p /data && useradd -m holofan && chown -R holofan /data /app
USER holofan
VOLUME ["/data"]

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
    CMD ["sh", "-c", "curl -fsS http://localhost:8080/api/health || exit 1"]

ENTRYPOINT ["dotnet", "HoloFan.Web.dll"]
