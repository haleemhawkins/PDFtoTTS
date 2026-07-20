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

# ocrmypdf + Tesseract so scanned/image-only PDFs get a searchable text layer added
# server-side before extraction. Pulls ghostscript/qpdf/unpaper as deps. osd is the
# orientation/script model required by --rotate-pages. We then overwrite the default
# (fast) English model with the slower, more accurate tessdata_best LSTM model — a
# direct base-accuracy win on scans. curl/ca-certificates are only needed for that
# download and are removed afterwards to keep the image lean.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ocrmypdf tesseract-ocr-eng tesseract-ocr-osd curl ca-certificates \
        ffmpeg \
    && ENG="$(dpkg -L tesseract-ocr-eng | grep '/eng.traineddata$')" \
    && curl -fsSL -o "$ENG" \
        https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata \
    && apt-get purge -y --auto-remove curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DATA_DIR=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PDFtoTTS.Api.dll"]
