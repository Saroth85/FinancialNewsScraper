#!/bin/bash
set -e

# Modello AI (default: phi3 — leggero, ~2.3GB, buono per analisi news)
AI_MODEL="${AI_MODEL:-phi3}"

# Porta: Railway la passa come $PORT, fallback a 8080
APP_PORT="${PORT:-8080}"
export ASPNETCORE_URLS="http://+:${APP_PORT}"

# Crea directory per modelli persistenti (volume Railway)
mkdir -p "${OLLAMA_MODELS:-/data/ollama/models}"

echo "=== Avvio Ollama server ==="
ollama serve &
OLLAMA_PID=$!

# Attendi che Ollama sia pronto (fast check, 0.5s interval)
echo "Attendo Ollama..."
for i in $(seq 1 60); do
    if curl -sf http://localhost:11434/api/tags > /dev/null 2>&1; then
        echo "Ollama pronto (${i}s)."
        break
    fi
    if [ "$i" -eq 60 ]; then
        echo "ERRORE: Ollama non si è avviato in tempo."
        exit 1
    fi
    sleep 0.5
done

# Scarica il modello solo se non presente nel volume persistente
# Con il volume Railway montato su /data/ollama, il modello resta tra i deploy
if ! ollama list 2>/dev/null | grep -q "$AI_MODEL"; then
    echo "Download modello $AI_MODEL (solo al primo deploy o senza volume)..."
    ollama pull "$AI_MODEL"
    echo "Modello $AI_MODEL scaricato e pronto."
else
    echo "Modello $AI_MODEL già nel volume persistente — skip download."
fi

echo "=== Avvio Financial News Scraper su porta ${APP_PORT} ==="
echo "Modelli Ollama in: ${OLLAMA_MODELS:-/data/ollama/models}"
exec dotnet FinancialNewsScraper.dll
