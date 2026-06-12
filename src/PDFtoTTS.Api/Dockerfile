# API orchestrator. Build context = repo root (needs proto/ + src/):
#   docker build -f src/PDFtoTTS.Api/Dockerfile -t pdftotts-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# IPv6 egress is broken on some hosts and .NET prefers it; force IPv4 for restore.
ENV DOTNET_SYSTEM_NET_DISABLEIPV6=1

# Contracts (consumed by the gRPC project via ../../proto) + sources.
COPY proto/ proto/
COPY src/ src/

RUN dotnet restore src/PDFtoTTS.Api/PDFtoTTS.Api.csproj
RUN dotnet publish src/PDFtoTTS.Api/PDFtoTTS.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DATA_DIR=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PDFtoTTS.Api.dll"]
