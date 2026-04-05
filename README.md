# Financial News Scraper

Aggregatore di notizie finanziarie in tempo reale con dashboard web auto-aggiornante. Raccoglie notizie da **30+ fonti** italiane e internazionali usando tre metodi di scraping complementari.

## Screenshot

Dashboard web dark-theme con card per ogni fonte, badge colorati per tipo di scraping e countdown per il prossimo aggiornamento.

## Fonti

### Browser (Playwright - 20 fonti)

Scraping headless con Chromium per siti JS-heavy, con auto-scroll per contenuto lazy-loaded.

| Fonte | Tipo |
|-------|------|
| Milano Finanza | IT |
| Borsa Italiana | IT |
| Il Sole 24 Ore | IT |
| Corriere Economia | IT |
| Repubblica Economia | IT |
| Teleborsa | IT |
| Sky TG24 Economia | IT |
| Yahoo Finance IT | Aggregatore |
| Bing News Finance (IT) | Aggregatore |
| Bing News Markets (EN) | Aggregatore |
| CNBC Markets | EN |
| CNN Business | EN |
| BBC Business | EN |
| Barron's | EN |
| TradingView | EN |
| Business Insider | EN |
| The Guardian Business | EN |
| Kitco | Commodities |
| CoinDesk | Crypto |
| DW Business | EN |

### HTML (HtmlAgilityPack - 1 fonte)

| Fonte | Tipo |
|-------|------|
| Finviz | EN |

### RSS (6 feed)

Feed usati come backup, inclusi proxy via Google News per siti che bloccano l'accesso diretto.

| Fonte | Note |
|-------|------|
| MarketWatch | Diretto |
| Investing.com | Diretto |
| Il Sole 24 Ore | Diretto |
| Bloomberg | via Google News RSS |
| Financial Times | via Google News RSS |
| Wall Street Journal | via Google News RSS |

## Funzionalità

- **Dashboard web** auto-aggiornante ogni 30 minuti su porta configurabile
- **Scraping ciclico** ogni 30 minuti con 3 tecniche: browser headless, HTML parsing, RSS
- **Filtro keyword** con ~70 termini finanziari in inglese e italiano (regex compilata)
- **Deduplicazione cross-source** tramite normalizzazione titoli
- **Auto-scroll** nelle pagine browser per caricare contenuto lazy-loaded
- **Risoluzione URL relativi** per link da scraping browser
- **Badge colorati** per tipo fonte: rosso (RSS), viola (HTML), verde (BROWSER)
- **Design dark-theme** responsive con card grid
- **Proxy Google News** per Bloomberg, FT, WSJ (siti che bloccano scraping diretto)
- **225+ notizie** tipiche per ciclo di aggiornamento

## Tech Stack

- **.NET 8.0** / C# con `Microsoft.NET.Sdk.Web`
- **ASP.NET Minimal API** per il web server
- **Microsoft.Playwright** per scraping headless con Chromium
- **HtmlAgilityPack** per HTML parsing (Finviz)
- **System.Xml.Linq** per parsing RSS/XML
- **Ollama** con modello **phi3** per analisi AI (sentiment, riassunti, briefing)
- **SQLite** via Entity Framework Core per persistenza dati
- **Docker** — unico container all-in-one (app + Ollama + Playwright)

## Requisiti

- .NET 8.0 SDK
- Playwright browsers: `pwsh bin/Debug/net8.0/playwright.ps1 install chromium`
- (Opzionale) Ollama installato localmente per AI: `curl -fsSL https://ollama.com/install.sh | bash && ollama pull phi3`

## Esecuzione locale

```bash
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install chromium
dotnet run --project FinancialNewsScraper
```

La dashboard sarà disponibile su `http://localhost:5050`.

## Docker (locale)

```bash
docker build -t financial-news-scraper .
docker run -p 8080:8080 financial-news-scraper
```

Il container include Ollama + phi3 — tutto in uno. La dashboard sarà disponibile su `http://localhost:8080`.

## Deploy su Railway

Container unico all-in-one: .NET app + Playwright + Ollama + phi3 — nessun servizio esterno richiesto.

### Setup

1. Push del codice su GitHub
2. Vai su [railway.app](https://railway.app) → **New Project** → **Deploy from GitHub Repo**
3. Railway rileva il `Dockerfile` e il `railway.toml`, fa build + deploy automatico
4. In **Settings → Networking → Generate Domain** ottieni l'URL pubblico

### Volume persistente (consigliato)

Aggiungi un volume dal dashboard Railway per evitare di riscaricare il modello AI ad ogni deploy:

- **Mount path**: `/root/.ollama`
- **Size**: 5GB (sufficiente per phi3)

### Piano consigliato

| Piano | RAM | Compatibilità | Note |
|-------|-----|---------------|------|
| Hobby ($5/mese) | 8GB | ✅ Funziona | Ottimizzazioni memoria attive |
| Pro | 32GB | ✅ Ideale | Nessun limite pratico |

Il piano Hobby funziona grazie alle ottimizzazioni memoria nel Dockerfile (`OLLAMA_NUM_PARALLEL=1`, `OLLAMA_KEEP_ALIVE=60`, `DOTNET_GCHeapHardLimit=256MB`).

### Primo deploy

Al primo avvio il container scarica il modello phi3 (~2.3GB). Questo richiede qualche minuto in più. L'healthcheck ha un timeout di 300s per gestire questa fase. I deploy successivi saranno veloci se il volume è configurato.

## Variabili d'ambiente

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `PORT` | `8080` | Porta del web server (Railway la sovrascrive automaticamente) |
| `AI_PROVIDER` | `ollama` | Provider AI: `ollama` o `openai` |
| `AI_MODEL` | `phi3` | Modello AI da usare |
| `OLLAMA_URL` | `http://localhost:11434` | URL di Ollama (interno al container) |
| `OPENAI_API_KEY` | — | Chiave API OpenAI (solo se `AI_PROVIDER=openai`) |

### Modelli AI alternativi

Se la RAM è un problema, puoi cambiare modello via la variabile `AI_MODEL`:

| Modello | Parametri | RAM | Qualità |
|---------|-----------|-----|---------|
| `phi3` | 3.8B | ~3.5GB | ⭐⭐⭐ Buona (default) |
| `gemma:2b` | 2B | ~2GB | ⭐⭐ Discreta |
| `qwen2:0.5b` | 0.5B | ~500MB | ⭐ Base |

## Struttura progetto

```
FinancialNewsScraper/
├── Dockerfile                      # Build multi-stage, all-in-one con Ollama
├── entrypoint.sh                   # Avvia Ollama + pull modello + app .NET
├── railway.toml                    # Config deploy Railway
├── FinancialNewsScraper.sln
└── FinancialNewsScraper/
    ├── FinancialNewsScraper.csproj
    ├── Program.cs                  # Scraping + web server + dashboard
    ├── Ai/
    │   └── AiService.cs            # Integrazione Ollama/OpenAI
    └── Data/
        ├── NewsDbContext.cs         # Entity Framework + modelli
        └── NewsRepository.cs       # Query e analytics
```

## Licenza

Uso personale.
