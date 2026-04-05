# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FinancialNewsScraper/FinancialNewsScraper.csproj FinancialNewsScraper/
RUN dotnet restore FinancialNewsScraper/FinancialNewsScraper.csproj
COPY . .
RUN dotnet publish FinancialNewsScraper/FinancialNewsScraper.csproj -c Release -o /app

# Runtime stage — unico container con .NET + Playwright + Ollama
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-jammy AS runtime

# Installa Ollama (binario diretto, più affidabile in Docker)
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates && \
    curl -fsSL -o /usr/local/bin/ollama https://ollama.com/download/ollama-linux-amd64 && \
    chmod +x /usr/local/bin/ollama && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Configurazione default (Railway sovrascrive PORT automaticamente)
ENV AI_PROVIDER=ollama
ENV AI_MODEL=phi3
ENV OLLAMA_URL=http://localhost:11434
ENV RAILWAY_ENVIRONMENT=true

# Ottimizzazioni memoria per Hobby plan (8GB condivisi)
ENV OLLAMA_NUM_PARALLEL=1
ENV OLLAMA_MAX_LOADED_MODELS=1
ENV OLLAMA_KEEP_ALIVE=60
ENV DOTNET_GCHeapHardLimit=0x10000000

ENTRYPOINT ["/app/entrypoint.sh"]
