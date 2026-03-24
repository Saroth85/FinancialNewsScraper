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
- **Docker** con immagine `mcr.microsoft.com/playwright/dotnet`

## Requisiti

- .NET 8.0 SDK
- Playwright browsers: `pwsh bin/Debug/net8.0/playwright.ps1 install chromium`

## Esecuzione locale

```bash
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install chromium
dotnet run --project FinancialNewsScraper
```

La dashboard sarà disponibile su `http://localhost:5050`.

## Docker

```bash
docker build -t financial-news-scraper .
docker run -p 8080:8080 financial-news-scraper
```

La dashboard sarà disponibile su `http://localhost:8080`.

## Deploy su Railway

1. Push del codice su GitHub
2. Vai su [railway.app](https://railway.app) → **New Project** → **Deploy from GitHub Repo**
3. Railway rileva il Dockerfile e fa build + deploy automatico
4. In **Settings → Networking → Generate Domain** ottieni l'URL pubblico

Il deploy automatico viene triggerato ad ogni push su `main` tramite GitHub Actions (`.github/workflows/deploy.yml`).

## Variabili d'ambiente

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `PORT` | `5050` | Porta del web server |

## Struttura progetto

```
FinancialNewsScraper/
├── .github/workflows/deploy.yml   # CI/CD per Railway
├── .dockerignore
├── .gitignore
├── Dockerfile
├── FinancialNewsScraper.sln
└── FinancialNewsScraper/
    ├── FinancialNewsScraper.csproj
    └── Program.cs                  # Tutto il codice (scraping + web server)
```

## Licenza

Uso personale.
