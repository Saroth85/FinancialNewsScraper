# Financial News Scraper

Aggregatore di notizie finanziarie in tempo reale con **analisi AI integrata**, dashboard web dark-theme e deploy su Railway in un unico container.  
Raccoglie notizie da **27+ fonti** italiane e internazionali, le salva in SQLite, e le analizza con **Ollama + phi3** per generare sentiment, riassunti e briefing giornalieri — tutto offline, senza API esterne a pagamento.

---

## Come funziona

### Ciclo di scraping (ogni 10 minuti)

```
┌─────────────────────────────────────────────────────────┐
│  1. SCRAPING  (3 tecniche in parallelo)                 │
│     ├── Playwright (Chromium headless) → 20 fonti       │
│     ├── HtmlAgilityPack (HTML parsing) → 1 fonte        │
│     └── RSS/XML feed → 6 fonti                          │
│                                                         │
│  2. FILTRAGGIO                                          │
│     ├── Regex con ~70 keyword finanziarie (IT + EN)     │
│     └── Dedup cross-source (normalizzazione titoli)     │
│                                                         │
│  3. PERSISTENZA → SQLite (news.db)                      │
│                                                         │
│  4. ANALISI AI (background, non blocca il ciclo)        │
│     ├── Ollama + phi3 analizza ogni news                │
│     └── Sentiment, entità, topic, market impact         │
└─────────────────────────────────────────────────────────┘
```

### Scraping dettagliato

**Browser (Playwright)** — Per i siti JS-heavy che richiedono rendering. Il browser headless Chromium naviga alla pagina, attende il caricamento JS (2s), esegue auto-scroll fino a metà pagina e poi fino in fondo per triggerare il lazy-loading, poi estrae i link con selettori CSS specifici per ogni fonte. Max 10 notizie per fonte.

**HTML (HtmlAgilityPack)** — Parsing diretto dell'HTML per siti leggeri (Finviz). Scarica l'HTML con HttpClient e usa XPath per estrarre i link alle news.

**RSS** — Feed XML diretti (MarketWatch, Investing.com, Il Sole 24 Ore) + proxy Google News per Bloomberg, Financial Times, WSJ (siti che bloccano lo scraping diretto).

### Filtro keyword

Una regex compilata con ~70 termini finanziari in inglese e italiano filtra le notizie rilevanti:

```
stock, market, nasdaq, s&p, dow jones, bond, yield, fed, ecb, inflation,
gdp, oil, gold, bitcoin, crypto, forex, etf, ipo, merger, bank...
borsa, mercati, azioni, spread, btp, bund, tassi, inflazione, pil, petrolio...
```

Solo i titoli che contengono almeno una di queste keyword vengono salvati.

### Deduplicazione

I titoli vengono normalizzati (lowercase, rimozione caratteri non-alfanumerici) e confrontati con un `HashSet<string>`. La stessa notizia da fonti diverse viene salvata una sola volta per giornata.

---

## AI con Ollama + phi3

### Cos'è Ollama

[Ollama](https://ollama.com) è un runtime per modelli LLM che gira in locale. Non serve nessuna API key, nessun servizio cloud, nessun costo per token. Il modello gira direttamente nella RAM del container.

### Cos'è phi3

**phi3** (Microsoft) è un modello da 3.8 miliardi di parametri (~2.3GB), progettato per essere compatto ma capace. Rispetto a modelli più grandi:
- **Pro**: leggero (sta in 4GB di RAM), veloce, buono per task strutturati come analisi di titoli
- **Contro**: meno preciso di GPT-4 o Llama 70B su task complessi — ma per classificare sentiment e estrarre entità da titoli di news è più che sufficiente

### Come funziona l'integrazione

L'app comunica con Ollama via HTTP locale (`http://localhost:11434`). Il flusso:

```
┌──────────────┐     prompt JSON      ┌───────────┐     inference     ┌───────┐
│  AiService   │ ───────────────────→ │  Ollama   │ ───────────────→ │ phi3  │
│  (.NET)      │ ←─────────────────── │  server   │ ←─────────────── │ model │
└──────────────┘     risposta JSON     └───────────┘                  └───────┘
```

1. `AiService` manda un prompt strutturato a Ollama (`POST /api/generate`)
2. Il prompt chiede di rispondere **solo in JSON** con campi specifici
3. phi3 genera la risposta, `AiService` la parsa e salva nel DB
4. Temperature bassa (0.3) per risposte deterministiche

### Analisi per singola news

Per ogni notizia, l'AI genera:

| Campo | Tipo | Descrizione |
|-------|------|-------------|
| `sentiment` | `positive/negative/neutral` | Sentiment del titolo |
| `sentimentScore` | `-1.0` a `+1.0` | Score numerico del sentiment |
| `summary` | stringa | Riassunto in una frase (nella lingua del titolo) |
| `entities` | array | Entità estratte: aziende, indici, persone, strumenti |
| `topics` | array | Topic: stocks, bonds, crypto, commodities, forex, central_banks, earnings, macro, geopolitics, tech |
| `marketImpact` | `bullish/bearish/neutral/mixed` | Impatto previsto sul mercato |

**Esempio prompt** inviato a phi3:
```
Analyze this financial news headline and respond ONLY with valid JSON:

Headline: "S&P 500 rally a nuovi massimi, Fed conferma taglio tassi a settembre"
Source: Il Sole 24 Ore

JSON format: { "sentiment": "...", "sentimentScore": ..., ... }
```

**Esempio risposta** di phi3:
```json
{
  "sentiment": "positive",
  "sentimentScore": 0.8,
  "summary": "L'S&P 500 raggiunge nuovi massimi storici dopo la conferma del taglio tassi dalla Fed",
  "entities": ["S&P 500", "Fed"],
  "topics": ["stocks", "central_banks"],
  "marketImpact": "bullish"
}
```

### Briefing giornaliero

L'AI genera anche un **briefing quotidiano** aggregando fino a 50 titoli del giorno:

| Campo | Descrizione |
|-------|-------------|
| `summary` | Overview di 2-3 frasi in italiano |
| `keyThemes` | 3-5 temi principali emergenti |
| `marketOutlook` | Prospettiva di mercato con motivazione |
| `topMovers` | Titoli/indici/commodity più menzionati |
| `riskFactors` | 2-3 fattori di rischio |

### Analisi in background

L'analisi AI non blocca lo scraping. Dopo ogni ciclo di raccolta:
1. Un `Task.Run` in background prende le ultime 20 news non ancora analizzate
2. Le analizza una alla volta (phi3 impiega ~2-5s per titolo)
3. Salva i risultati nel DB
4. Se Ollama non è disponibile o va in errore, il sistema continua a funzionare senza AI

### Doppio sistema di sentiment

L'app ha **due sistemi di sentiment** complementari:
- **Keyword-based** (veloce, nel Repository): conta parole positive/negative nel titolo. Parole come "rally", "surge", "growth" → positivo; "crash", "plunge", "crisis" → negativo
- **AI-based** (accurato, via Ollama): analisi semantica completa del titolo con score numerico

---

## Dashboard e API

### Pagine web

| Pagina | URL | Contenuto |
|--------|-----|-----------|
| Live | `/` | Card grid con tutte le fonti, badge per tipo, auto-refresh |
| Archivio | `/archive` | Ricerca full-text, filtri per fonte/data, export CSV |
| Analytics | `/analytics` | Grafici trend, keyword velocity, distribuzione oraria |
| AI Insights | `/ai` | Sentiment AI, briefing, entità, topic, market impact |

### API REST

**Dati e ricerca:**
| Endpoint | Parametri | Descrizione |
|----------|-----------|-------------|
| `GET /api/search` | `q, source, from, to, limit, offset` | Ricerca news |
| `GET /api/trend` | `keyword, days` | Trend giornaliero per keyword |
| `GET /api/top-keywords` | `days, top` | Top keyword per frequenza |
| `GET /api/sources` | — | Lista fonti uniche |
| `GET /api/stats` | — | Statistiche generali + breakdown |
| `GET /api/sentiment` | `days` | Sentiment aggregato (keyword-based) |
| `GET /api/sentiment-trend` | `days` | Sentiment giornaliero |
| `GET /api/hourly` | `days` | Distribuzione oraria news |
| `GET /api/weekday` | `days` | Distribuzione per giorno settimana |
| `GET /api/velocity` | — | Keyword velocity (crescita/declino) |
| `GET /api/cooccurrence` | `days, top` | Co-occorrenza keyword |
| `GET /api/multi-trend` | `keywords, days` | Trend multi-keyword comparato |
| `GET /api/export` | `q, source, from, to` | Export CSV |

**AI:**
| Endpoint | Descrizione |
|----------|-------------|
| `GET /api/ai/status` | Stato di Ollama |
| `GET /api/ai/analyze?count=N` | Analizza N news non processate |
| `GET /api/ai/briefing` | Briefing giornaliero (cached) |
| `GET /api/ai/sentiment-stats?days=N` | Sentiment AI aggregato |
| `GET /api/ai/sentiment-trend?days=N` | Trend sentiment AI |
| `GET /api/ai/entities?days=N&top=N` | Top entità estratte |
| `GET /api/ai/topics?days=N` | Top topic |
| `GET /api/ai/coverage` | % news analizzate vs totali |
| `GET /api/ai/recent?limit=N` | Ultime analisi complete |

---

## Fonti

### Browser (Playwright — 20 fonti)

| Fonte | Tipo | Note |
|-------|------|------|
| Milano Finanza | IT | Principale fonte italiana |
| Borsa Italiana | IT | Dati di borsa |
| Il Sole 24 Ore | IT | Finanza section |
| Corriere Economia | IT | |
| Repubblica Economia | IT | |
| Teleborsa | IT | |
| Sky TG24 Economia | IT | |
| Yahoo Finance IT | Aggregatore | |
| Bing News Finance (IT) | Aggregatore | Sorted by date |
| Bing News Markets (EN) | Aggregatore | Sorted by date |
| CNBC Markets | EN | |
| CNN Business | EN | |
| BBC Business | EN | |
| Barron's | EN | Market data |
| TradingView | EN | News feed |
| Business Insider | EN | Markets section |
| The Guardian Business | EN | |
| Kitco | Commodities | Oro, metalli |
| CoinDesk | Crypto | Markets section |
| DW Business | EN | Deutsche Welle |

### HTML (HtmlAgilityPack — 1 fonte)

| Fonte | Tipo |
|-------|------|
| Finviz | EN — stock screener |

### RSS (6 feed)

| Fonte | Note |
|-------|------|
| MarketWatch | Feed diretto |
| Investing.com | Feed diretto |
| Il Sole 24 Ore | Feed diretto |
| Bloomberg | via Google News RSS (proxy) |
| Financial Times | via Google News RSS (proxy) |
| Wall Street Journal | via Google News RSS (proxy) |

---

## Database (SQLite)

### Tabelle

**`News`** — Tutte le notizie raccolte
```
Id, Source, Category (RSS/HTML/BROWSER), Title, Url, ScrapedAtUtc, TitleNormalized
```
Indici su: `TitleNormalized`, `ScrapedAtUtc`, `Source`, `Category`

**`AiAnalyses`** — Risultati analisi AI (1:1 con News)
```
Id, NewsItemId (FK), Sentiment, SentimentScore, Summary, Entities (JSON), Topics (JSON), MarketImpact, AnalyzedAtUtc
```
Indici su: `NewsItemId` (unique), `Sentiment`, `MarketImpact`

**`AiDailyBriefings`** — Briefing giornalieri
```
Id, Date (unique), Summary, KeyThemes (JSON), MarketOutlook, TopMovers (JSON), RiskFactors (JSON), GeneratedAtUtc
```

---

## Tech Stack

| Componente | Tecnologia |
|------------|-----------|
| Runtime | .NET 8.0 / C# |
| Web server | ASP.NET Minimal API |
| Browser headless | Microsoft.Playwright (Chromium) |
| HTML parser | HtmlAgilityPack |
| RSS parser | System.Xml.Linq |
| Database | SQLite via Entity Framework Core |
| AI locale | Ollama + phi3 (3.8B parametri) |
| Container | Docker multi-stage (SDK → Ollama → Playwright runtime) |
| Deploy | Railway (single container) |

---

## Esecuzione locale

```bash
# Build e installa browser
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install chromium

# (Opzionale) Installa Ollama per AI locale
# Linux/Mac: curl -fsSL https://ollama.com/install.sh | bash
# Windows: scarica da https://ollama.com/download
ollama pull phi3

# Avvia
dotnet run --project FinancialNewsScraper
```

Dashboard su `http://localhost:5050`

---

## Deploy su Railway

Container unico all-in-one: .NET + Playwright + Ollama + phi3.

### Setup

1. Push del codice su GitHub
2. Su [railway.app](https://railway.app) → **New Project** → **Deploy from GitHub Repo**
3. Railway rileva il `Dockerfile` e il `railway.toml`, fa build + deploy automatico
4. **Settings → Networking → Generate Domain** per l'URL pubblico

### Volume persistente (fondamentale)

Senza volume, il modello phi3 (~2.3GB) viene riscaricato ad ogni deploy.  
Con il volume, il modello resta e il riavvio dura pochi secondi.

1. Nel dashboard Railway → **Service → Volumes → Add Volume**
2. **Mount path**: `/data/ollama`
3. **Size**: 5GB

Il Dockerfile configura `OLLAMA_MODELS=/data/ollama/models` — Ollama salva e legge i modelli da lì.

### Cosa succede ad ogni deploy (commit + push)

```
1. Railway rebuilda il container (build stage)
2. Il container si riavvia
3. entrypoint.sh:
   ├── Avvia Ollama server (~2s)
   ├── Controlla se phi3 è nel volume
   │   ├── SÌ → "skip download" → pronto in ~5s
   │   └── NO → "ollama pull phi3" → ~2-3 min (solo primo deploy)
   └── Avvia l'app .NET
```

### Piano consigliato

| Piano | RAM | Compatibilità | Costo |
|-------|-----|---------------|-------|
| Hobby | 8GB | ✅ Funziona | $5/mese + consumo |
| Pro | 32GB | ✅ Ideale | $20/mese + consumo |

Le ottimizzazioni nel Dockerfile (un solo modello in RAM, keep-alive 60s, GC .NET limitato a 256MB) rendono il piano Hobby sufficiente.

---

## Variabili d'ambiente

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `PORT` | `8080` | Porta web server (Railway la sovrascrive) |
| `AI_PROVIDER` | `ollama` | Provider: `ollama` o `openai` |
| `AI_MODEL` | `phi3` | Modello AI |
| `OLLAMA_URL` | `http://localhost:11434` | URL Ollama (interno al container) |
| `OLLAMA_MODELS` | `/data/ollama/models` | Path storage modelli (volume Railway) |
| `OPENAI_API_KEY` | — | Solo se `AI_PROVIDER=openai` |
| `OLLAMA_NUM_PARALLEL` | `1` | Richieste parallele Ollama |
| `OLLAMA_MAX_LOADED_MODELS` | `1` | Modelli in RAM simultanei |
| `OLLAMA_KEEP_ALIVE` | `60` | Secondi prima di scaricare modello dalla RAM |

### Modelli AI alternativi

| Modello | Parametri | RAM | Qualità |
|---------|-----------|-----|---------|
| `phi3` | 3.8B | ~3.5GB | ⭐⭐⭐ Default — buon compromesso |
| `gemma:2b` | 2B | ~2GB | ⭐⭐ Più leggero |
| `qwen2:0.5b` | 0.5B | ~500MB | ⭐ Ultra-leggero |

---

## Struttura progetto

```
FinancialNewsScraper/
├── Dockerfile                      # Multi-stage: SDK → Ollama → Playwright runtime
├── entrypoint.sh                   # Avvia Ollama + pull modello + app .NET
├── railway.toml                    # Config deploy Railway
├── .gitattributes                  # LF per shell scripts
├── FinancialNewsScraper.sln
└── FinancialNewsScraper/
    ├── FinancialNewsScraper.csproj
    ├── Program.cs                  # Scraping + web server + dashboard HTML
    ├── Ai/
    │   └── AiService.cs            # Client Ollama/OpenAI, prompt engineering, parsing JSON
    └── Data/
        ├── NewsDbContext.cs         # EF Core context + modelli (News, AiAnalysis, Briefing)
        └── NewsRepository.cs       # Query, analytics, sentiment, velocity, co-occurrence
```

## Licenza

Uso personale.
