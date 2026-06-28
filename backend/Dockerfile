# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN apt-get update && \
    apt-get install -y --no-install-recommends python3 python3-pip ffmpeg && \
    pip install --break-system-packages --no-cache-dir "yt-dlp[default,curl-cffi]" && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV DB_PATH=/app/data/history.db
ENV LOG_PATH=/app/logs/issamstube-.log
EXPOSE 8080

RUN mkdir -p /app/data /app/logs

ENTRYPOINT ["dotnet", "IssamsTube.Api.dll"]