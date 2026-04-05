#!/bin/bash
set -e

# Modello AI (default: phi3 — leggero, ~2.3GB, buono per analisi news)
AI_MODEL="${AI_MODEL:-phi3}"

# Porta: Railway la passa come $PORT, fallback a 8080
APP_PORT="${PORT:-8080}"
export ASPNETCORE_URLS="http://+:${APP_PORT}"

echo "=== Avvio Ollama server ==="
ollama serve &
OLLAMA_PID=$!
echo "Ollama PID: $OLLAMA_PID"

# Attendi che Ollama sia pronto
echo "Attendo che Ollama sia raggiungibile..."
for i in $(seq 1 60); do
    if curl -sf http://localhost:11434/api/tags > /dev/null 2>&1; then
        echo "Ollama pronto."
        break
    fi
    if [ "$i" -eq 60 ]; then
        echo "ERRORE: Ollama non si è avviato in tempo."
        exit 1
    fi
    sleep 1
done

# Scarica il modello se non presente
if ! ollama list | grep -q "$AI_MODEL"; then
    echo "Download modello $AI_MODEL (prima esecuzione, potrebbe richiedere tempo)..."
    ollama pull "$AI_MODEL"
    echo "Modello $AI_MODEL pronto."
else
    echo "Modello $AI_MODEL già disponibile."
fi

echo "=== Avvio Financial News Scraper su porta ${APP_PORT} ==="
exec dotnet FinancialNewsScraper.dll
