# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FinancialNewsScraper/FinancialNewsScraper.csproj FinancialNewsScraper/
RUN dotnet restore FinancialNewsScraper/FinancialNewsScraper.csproj
COPY . .
RUN dotnet publish FinancialNewsScraper/FinancialNewsScraper.csproj -c Release -o /app

# Ollama stage — prende il binario dall'immagine ufficiale
FROM ollama/ollama:latest AS ollama

# Runtime stage — unico container con .NET + Playwright + Ollama
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-jammy AS runtime

# Copia il binario Ollama dall'immagine ufficiale (niente curl/download)
COPY --from=ollama /bin/ollama /usr/local/bin/ollama

WORKDIR /app
COPY --from=build /app .
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Configurazione default (Railway sovrascrive PORT automaticamente)
ENV AI_PROVIDER=ollama
ENV AI_MODEL=phi3
ENV OLLAMA_URL=http://localhost:11434
ENV RAILWAY_ENVIRONMENT=true

# Percorso modelli Ollama — montare un volume Railway su /data/ollama
# così i modelli persistono tra i deploy e non vengono riscaricati
ENV OLLAMA_MODELS=/data/ollama/models

# Database SQLite su volume persistente — sopravvive ai redeploy
ENV DB_PATH=/data/news.db

# Ottimizzazioni memoria per Hobby plan (8GB condivisi)
ENV OLLAMA_NUM_PARALLEL=1
ENV OLLAMA_MAX_LOADED_MODELS=1
ENV OLLAMA_KEEP_ALIVE=60
ENV DOTNET_GCHeapHardLimit=0x10000000

ENTRYPOINT ["/app/entrypoint.sh"]
