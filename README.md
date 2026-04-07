# Financial News Scraper

Aggregatore di notizie finanziarie con **analisi AI integrata**, dashboard web dark-theme e deploy su Railway.  
Raccoglie notizie da **27+ fonti** italiane e internazionali ogni 4 ore, le salva in SQLite, e le analizza con **Ollama + phi3** distribuendo max **200 analisi AI al giorno** su tutti i cicli, pescando in modo bilanciato da tutte le fonti — tutto offline, senza API esterne a pagamento.

---

## Indice

- [Architettura](#architettura)
- [Ciclo di funzionamento](#ciclo-di-funzionamento)
- [Scraping: come raccoglie le news](#scraping-come-raccoglie-le-news)
- [Filtro keyword e deduplicazione](#filtro-keyword-e-deduplicazione)
- [Dashboard — Tab Live](#tab-live--homepage)
- [Dashboard — Tab Archivio](#tab-archivio--archive)
- [Dashboard — Tab Analytics](#tab-analytics--analytics)
- [Dashboard — Tab AI Insights](#tab-ai-insights--ai)
- [Motore AI: come funziona](#motore-ai-come-funziona)
- [API REST complete](#api-rest-complete)
- [Database SQLite](#database-sqlite)
- [Gestione risorse e ottimizzazioni](#gestione-risorse-e-ottimizzazioni)
- [Fonti](#fonti)
- [Tech Stack](#tech-stack)
- [Esecuzione locale](#esecuzione-locale)
- [Deploy su Railway](#deploy-su-railway)
- [Variabili d'ambiente](#variabili-dambiente)
- [Alternative hosting](#alternative-hosting)
- [Struttura progetto](#struttura-progetto)

---

## Architettura

```
┌──────────────────────────────────────────────────────────────────────────┐
│                        FINANCIAL NEWS SCRAPER                           │
│                                                                        │
│  ┌─────────────────┐   ┌──────────────┐   ┌────────────────────────┐   │
│  │  SCRAPING ENGINE │   │  WEB SERVER  │   │     AI ENGINE          │   │
│  │  (ogni 4 ore)   │   │  (ASP.NET)   │   │  (loop continuo 24h)  │   │
│  │                  │   │              │   │                        │   │
│  │  Playwright ×20  │   │  /           │   │  Ollama + phi3         │   │
│  │  HTML ×1         │──▶│  /archive    │   │  Sentiment analysis    │   │
│  │  RSS ×6          │   │  /analytics  │   │  Entity extraction     │   │
│  │                  │   │  /ai         │   │  Daily briefing        │   │
│  └────────┬─────────┘   │  /api/*      │   └──────────┬─────────────┘  │
│           │              └──────┬───────┘              │                │
│           │                     │                      │                │
│           ▼                     ▼                      ▼                │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │                    SQLite (news.db)                             │    │
│  │  News │ AiAnalyses │ AiDailyBriefings                         │    │
│  └─────────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────────┘
```

L'app è un **singolo processo .NET 8** che fa girare contemporaneamente:
1. **Web server** (ASP.NET Minimal API) — serve le 4 pagine HTML e le API REST
2. **Scraping engine** — raccoglie news ogni 4 ore da 27 fonti con 3 tecniche diverse
3. **AI engine** — loop background continuo, analizza max 200 news/giorno spalmate lentamente (~1 ogni 7 min), con sampling bilanciato da tutte le fonti

---

## Ciclo di funzionamento

Ad ogni ciclo (ogni **4 ore**):

```
┌──────────────────────────────────────────────────────────────────┐
│  1. SCRAPING  (3 tecniche, parallelo con SemaphoreSlim)         │
│     ├── Playwright (Chromium headless) → 20 fonti               │
│     │   └── Max 3 pagine contemporanee (SemaphoreSlim)          │
│     ├── HtmlAgilityPack (HTML parsing) → 1 fonte                │
│     └── RSS/XML feed → 6 fonti                                  │
│                                                                  │
│  2. FILTRAGGIO                                                   │
│     ├── Regex con ~70 keyword finanziarie (IT + EN)              │
│     └── Dedup cross-source (normalizzazione titoli)              │
│                                                                  │
│  3. PERSISTENZA → SQLite (news.db)                               │
│                                                                  │
│  4. PULIZIA DB → Elimina news > 1 anno (365 giorni)              │
│                                                                  │
│  5. ANALISI AI (background continuo, indipendente dallo scraping) │
│     ├── Max 200 news/giorno, spalmate lentamente (~1 ogni 7 min)  │
│     ├── Round-robin random da TUTTE le fonti (copertura equa)     │
│     ├── Ritmo auto-calcolato: secondi_rimasti / news_rimaste      │
│     └── Auto-genera briefing giornaliero dopo ≥30 analisi         │
│                                                                  │
│  6. ATTESA → 4 ore prima del prossimo ciclo                      │
└──────────────────────────────────────────────────────────────────┘
```

---

## Scraping: come raccoglie le news

### Browser (Playwright) — 20 fonti

Per i siti JS-heavy che richiedono rendering completo. I 20 browser scraper girano in **parallelo con concorrenza limitata** (`SemaphoreSlim(3)` — max 3 pagine contemporanee) per bilanciare velocità e consumo di RAM.

Per ogni fonte il flusso è:
1. Apre una nuova pagina Chromium headless
2. Naviga all'URL e attende `DOMContentLoaded` (timeout 25s)
3. Attende 2 secondi per il caricamento JS iniziale
4. Esegue scroll a metà pagina (per triggerare lazy-loading)
5. Attende 1.5 secondi
6. Esegue scroll fino in fondo alla pagina
7. Attende 1.5 secondi
8. Estrae tutti i link tramite CSS selectors specifici per ogni fonte
9. Risolve URL relativi in assoluti
10. Filtra per keyword finanziarie e deduplica
11. Salva max **10 notizie** per fonte
12. Chiude la pagina

### HTML (HtmlAgilityPack) — 1 fonte

Parsing diretto dell'HTML per siti leggeri (Finviz). Scarica l'HTML con `HttpClient` e usa selettori XPath per estrarre i link alle news. Più veloce e leggero del browser.

### RSS — 6 feed

Feed XML diretti tramite `System.Xml.Linq`. Per siti che bloccano lo scraping diretto (Bloomberg, FT, WSJ) usa come proxy i feed RSS di Google News filtrati per dominio.

---

## Filtro keyword e deduplicazione

### Filtro keyword

Una regex compilata con **~70 termini finanziari** in inglese e italiano filtra le notizie rilevanti:

```
stock, market, nasdaq, s&p, dow jones, ftse, dax, nikkei, wall street,
bull, bear, rally, sell-off, trading, bond, yield, treasury, fed, ecb,
interest rate, inflation, cpi, gdp, recession, earnings, revenue, profit,
oil, crude, gold, bitcoin, crypto, forex, dollar, euro, yen, etf, ipo,
merger, acquisition, bank, fintech, hedge, derivatives, futures, options,
borsa, mercati, azioni, titoli, piazza affari, rendimento, obbligazioni,
spread, btp, bund, tassi, inflazione, pil, utili, petrolio, oro, valuta
```

Solo i titoli che contengono almeno una keyword vengono salvati. Il controllo avviene sia sul titolo che sulla descrizione (per RSS).

### Deduplicazione

I titoli vengono **normalizzati** (lowercase + rimozione di tutti i caratteri non-alfanumerici) e confrontati con un `HashSet<string>` in memoria. La stessa notizia da fonti diverse viene salvata una sola volta. Nel database, un ulteriore controllo impedisce duplicati nella stessa giornata.

---

## Tab Live (`/` — Homepage)

La pagina principale mostra tutte le notizie raccolte nell'ultimo ciclo di scraping in tempo reale.

### Layout
- **Header sticky** con logo, navigazione tra le 4 tab, stato di aggiornamento e countdown
- **Grid di card responsive** (`auto-fill, minmax(400px, 1fr)`) — si adatta a qualsiasi schermo

### Per ogni fonte (card):
- **Nome della fonte** (es. "Milano Finanza", "CNBC Markets")
- **Badge colorato** che indica il tipo di scraping:
  - 🟢 `BROWSER` — verde — scraping via Playwright
  - 🟣 `HTML` — viola — parsing HTML diretto
  - 🔴 `RSS` — rosso — feed XML
- **Lista delle notizie** (max 10 per fonte):
  - Titolo cliccabile che apre l'articolo originale in una nuova tab
  - Orario di raccolta (es. "14:30")
- Le card sono ordinate per **numero di notizie** (decrescente)

### Comportamento dinamico
- Durante lo scraping mostra **"Aggiornamento in corso..."** in verde
- Completato, mostra l'orario dell'ultimo aggiornamento + **"LIVE"**
- **Auto-refresh** ogni 60 secondi con countdown visibile ("Refresh tra 45s")
- Se una fonte non ha trovato notizie finanziarie, mostra "Nessuna notizia finanziaria trovata."

---

## Tab Archivio (`/archive`)

Motore di ricerca completo per tutte le news archiviate nel database SQLite.

### Barra di ricerca
Quattro filtri combinabili:
- **Testo libero** — ricerca full-text nel titolo (case-insensitive)
- **Sorgente** — dropdown con tutte le fonti presenti nel DB (caricato via API `/api/sources`)
- **Data Da / Data A** — filtro per intervallo temporale con date picker

### Tabella risultati
Per ogni news trovata:
- **Data e ora** — formato italiano (dd/mm/yyyy HH:mm)
- **Sorgente** — badge grigio con il nome della fonte
- **Titolo** — link cliccabile all'articolo originale

### Paginazione
- **50 risultati per pagina**
- Pulsanti "← Prec" e "Succ →" con indicazione pagina corrente
- Conteggio totale dei risultati

### Comportamento
- La ricerca si avvia premendo il pulsante "Cerca" oppure premendo **Invio** nel campo testo
- Al caricamento della pagina, mostra automaticamente le ultime 50 news
- Loading spinner durante il caricamento dei risultati

---

## Tab Analytics (`/analytics`)

Dashboard analitica completa con grafici interattivi (Chart.js 4) per visualizzare trend, sentiment, distribuzione e correlazioni.

### 1. Barra Export
- Pulsante **"Esporta CSV"** — scarica tutte le news in formato CSV

### 2. Stat Cards (riga in alto)
7 card con metriche chiave:
| Card | Dato |
|------|------|
| News totali nel DB | Conteggio totale di tutte le news archiviate |
| Prima news | Data della news più vecchia nel database |
| Ultima news | Data della news più recente |
| Sorgenti attive | Numero di fonti uniche nel database |
| Positive (7gg) | News con sentiment positivo negli ultimi 7 giorni |
| Negative (7gg) | News con sentiment negativo negli ultimi 7 giorni |
| Sentiment Index | Percentuale di news positive sul totale (verde se ≥50%, rosso altrimenti) |

### 3. Trend & Confronto Keywords

**Trend singola keyword:**
- Campo testo per inserire una keyword (es. "bitcoin", "oil", "borsa")
- Selettore periodo: 7, 14, 30, 60, 90 giorni
- **Grafico a linee** con area fill — mostra quante news al giorno contengono quella keyword
- Senza keyword, mostra il volume totale di news per giorno

**Confronto multi-keyword:**
- Campo testo per più keyword separate da virgola (es. "bitcoin, gold, oil, euro")
- Selettore periodo
- **Grafico a linee multiple** con colori diversi — confronto visivo dell'andamento di ogni keyword nel tempo
- Interazione: hover su un punto mostra tutti i valori per quella data (mode: index)

### 4. Analisi Sentiment (keyword-based)

**Sentiment Attuale (7 giorni):**
- **Grafico a ciambella** con 3 segmenti: Positive (verde), Negative (rosso), Neutral (grigio)
- Il sentiment è calcolato contando parole positive/negative nel titolo:
  - Positive: rally, surge, gain, rise, jump, soar, boom, bull, recovery, growth, profit...
  - Negative: crash, plunge, drop, fall, decline, slump, bear, sell-off, loss, recession, crisis...

**Sentiment Trend (30 giorni):**
- **Grafico a barre impilate** — per ogni giorno, barre colorate per positive/negative/neutral
- Permette di vedere l'evoluzione del sentiment nel tempo

### 5. Distribuzione Temporale

**Per ora del giorno (30 giorni):**
- **Grafico a barre** con 24 barre (00:00 → 23:00) — mostra quando vengono pubblicate più news

**Per giorno della settimana (30 giorni):**
- **Grafico a barre** con 7 barre (Dom → Sab)
- Colori differenziati: blu per giorni lavorativi, rosso per domenica, arancione per sabato

### 6. Keywords & Topics

**Top Keywords (7 giorni):**
- **Grafico a barre orizzontali** — le 15 keyword più frequenti nei titoli
- Le keyword tracciate sono ~40 termini finanziari predefiniti (IT + EN)

**Distribuzione per Categoria:**
- **Grafico a ciambella** — distribuzione delle news per metodo di raccolta (BROWSER/HTML/RSS)
- Colori: verde per BROWSER, viola per HTML, rosso per RSS

### 7. Keyword Velocity & Correlazioni

**Keyword Velocity:**
- **Tabella** con tutte le keyword tracciate
- Per ogni keyword: frequenza questa settimana, settimana precedente, variazione percentuale
- Frecce colorate: 🔼 verde (crescita), 🔽 rosso (declino), — grigio (stabile)
- Ordinata per variazione assoluta più alta

**Co-occorrenze Keywords (7 giorni):**
- **Lista** delle 15 coppie di keyword che compaiono più spesso nello stesso titolo
- Barra di progresso proporzionale al conteggio
- Es. "market + stock: 42", "oil + gold: 18"

### 8. Top Sorgenti (30 giorni)
- **Grafico a barre** con le 20 fonti che producono più notizie

---

## Tab AI Insights (`/ai`)

Dashboard dedicata all'intelligenza artificiale. Mostra i risultati dell'analisi di Ollama/phi3 su ogni singola news.

### 1. AI Status Banner
- **Indicatore verde** con pallino luminoso se Ollama è connesso e operativo
- **Indicatore rosso** se l'AI non è disponibile (con messaggio di aiuto per configurare)

### 2. Pulsanti Azione
| Pulsante | Funzione |
|----------|----------|
| **Analizza News (AI)** | Invia le ultime 20 news non analizzate a Ollama per l'analisi. Mostra spinner durante l'elaborazione e conferma "✅ Analizzate N news" |
| **Genera Briefing Giornaliero** | Genera un riassunto AI della giornata basato sugli ultimi 50-100 titoli. Se già generato oggi, mostra la versione cache |
| **Aggiorna Dashboard** | Ricarica tutti i dati e grafici della pagina |

### 3. Copertura AI
- **Barra di progresso** che mostra quante news sono state analizzate dall'AI vs il totale
- Es. "142 / 350 news analizzate (40.6%)"

### 4. Stat Cards AI (5 card)
| Card | Colore | Dato |
|------|--------|------|
| Positive | 🟢 Verde | News con sentiment AI positivo (ultimi 7 giorni) |
| Negative | 🔴 Rosso | News con sentiment AI negativo |
| Neutral | ⚪ Grigio | News con sentiment AI neutro |
| Bullish | 🟢 Verde | News con market impact bullish |
| Bearish | 🔴 Rosso | News con market impact bearish |

### 5. Briefing Giornaliero AI
Card a larghezza piena con il briefing generato dall'AI:
- **Riepilogo** — Overview di 2-3 frasi in italiano dei temi principali della giornata
- **Temi Principali** — Lista di 3-5 temi emergenti (con bullet point ▸)
- **Outlook Mercato** — Prospettiva bullish/bearish/mixed con motivazione
- **Top Movers** — Titoli, indici e commodity più impattati
- **Fattori di Rischio** — 2-3 fattori di rischio emergenti
- Se già generato oggi, mostra "(cache)" con l'icona orologio

### 6. Grafici AI

**AI Sentiment (ciambella):**
- Distribuzione Positive/Negative/Neutral basata sull'analisi semantica AI (non keyword)

**Market Impact AI (ciambella):**
- Distribuzione Bullish/Bearish/Neutral-Mixed

**AI Sentiment Trend (30 giorni):**
- **Barre impilate** con l'evoluzione giornaliera del sentiment AI

### 7. Top Entità (AI)
- **Grafico a barre orizzontali** — le 15 entità più citate, estratte dall'AI
- Entità = aziende (Tesla, Apple), indici (S&P 500, FTSE), persone (Powell), strumenti finanziari
- Colore viola

### 8. Top Topics (AI)
- **Grafico a ciambella** — distribuzione dei topic rilevati dall'AI
- Topic possibili: stocks, bonds, crypto, commodities, forex, central_banks, earnings, macro, geopolitics, tech

### 9. Ultime Analisi AI
Lista scrollabile delle ultime 30 news analizzate dall'AI, per ognuna:
- **Titolo** cliccabile che apre l'articolo
- **Fonte** dell'articolo
- **Tag sentiment** — badge colorato (positive verde / negative rosso / neutral grigio) con score numerico (-1.0 → +1.0)
- **Tag market impact** — badge (bullish verde / bearish rosso)
- **Tag entità** — badge viola per ogni entità estratta (max 3 mostrate)
- **Tag topic** — badge azzurro per ogni topic rilevato
- **Riassunto** — frase di sintesi generata dall'AI nella lingua del titolo

---

## Motore AI: come funziona

### Provider supportati

| Provider | Modello default | Costo | Setup |
|----------|----------------|-------|-------|
| **Ollama** (locale) | phi3 (3.8B, ~2.3GB) | Gratis | Installare Ollama + `ollama pull phi3` |
| **OpenAI** (cloud) | gpt-4o-mini | A pagamento | Configurare `OPENAI_API_KEY` |

### Flusso di comunicazione

```
┌──────────────┐     prompt JSON      ┌───────────┐     inference     ┌───────┐
│  AiService   │ ───────────────────→ │  Ollama   │ ───────────────→ │ phi3  │
│  (.NET)      │ ←─────────────────── │  server   │ ←─────────────── │ model │
└──────────────┘     risposta JSON     └───────────┘                  └───────┘
```

1. `AiService` manda un prompt strutturato a Ollama (`POST /api/generate`) o OpenAI
2. Il prompt chiede di rispondere **solo in JSON** con campi specifici
3. Il modello genera la risposta, `AiService` estrae il JSON (trova `{` ... `}`) e lo parsa
4. Temperature bassa (0.3) per risposte deterministiche
5. Timeout HTTP di 300 secondi per modelli lenti, keep-alive 10 minuti
6. Pre-caricamento modello in RAM (warm-up) nel background loop, senza bloccare il web server

### Analisi per singola news

Per ogni notizia, l'AI genera:

| Campo | Tipo | Descrizione |
|-------|------|-------------|
| `sentiment` | `positive/negative/neutral` | Sentiment semantico del titolo |
| `sentimentScore` | `-1.0` a `+1.0` | Score numerico del sentiment |
| `summary` | stringa | Riassunto in una frase (nella lingua del titolo) |
| `entities` | array | Entità: aziende, indici, persone, strumenti finanziari |
| `topics` | array | 1-3 topic: stocks, bonds, crypto, commodities, forex, central_banks, earnings, macro, geopolitics, tech |
| `marketImpact` | `bullish/bearish/neutral/mixed` | Impatto previsto sul mercato |

### Schedulazione AI

L'AI gira come **loop background continuo**, indipendente dai cicli di scraping:
- **Max 200 news/giorno**, spalmate lentamente nell'arco delle 24 ore
- Ritmo auto-calcolato: `secondi_rimasti_oggi / news_rimaste` (~7.2 min con budget pieno)
- **Sampling bilanciato**: round-robin random da tutte le fonti, garantendo copertura equa
- **Auto-briefing**: genera il briefing giornaliero automaticamente dopo ≥30 news analizzate
- A mezzanotte il budget si resetta
- Se non ci sono news da analizzare, attende 10 min e riprova
- Se Ollama è offline o genera errori, riprova dopo 5 min

### Doppio sistema di sentiment

L'app ha **due sistemi di sentiment** complementari:
- **Keyword-based** (veloce, nel Repository): conta parole positive/negative nel titolo → disponibile sulla tab Analytics
- **AI-based** (accurato, via Ollama): analisi semantica completa → disponibile sulla tab AI Insights

---

## API REST complete

### Dati e ricerca

| Endpoint | Parametri | Risposta |
|----------|-----------|----------|
| `GET /api/search` | `q, source, from, to, limit, offset` | `{ total, items: [...] }` — ricerca paginata |
| `GET /api/trend` | `keyword, days` | `[{ date, count }]` — volume giornaliero |
| `GET /api/top-keywords` | `days, top` | `[{ keyword, count }]` — keyword più frequenti |
| `GET /api/sources` | — | `["fonte1", "fonte2", ...]` — fonti uniche |
| `GET /api/stats` | — | `{ total, firstDate, lastDate, categories, sources }` — statistiche generali |
| `GET /api/sentiment` | `days` | `{ positive, negative, neutral }` — sentiment keyword-based |
| `GET /api/sentiment-trend` | `days` | `[{ date, positive, negative, neutral }]` — trend giornaliero |
| `GET /api/hourly` | `days` | `[{ hour, count }]` — distribuzione oraria |
| `GET /api/weekday` | `days` | `[{ dayOfWeek, dayName, count }]` — distribuzione settimanale |
| `GET /api/velocity` | — | `[{ keyword, currentWeek, previousWeek, changePercent }]` — crescita keyword |
| `GET /api/cooccurrence` | `days, top` | `[{ keyword1, keyword2, count }]` — co-occorrenze |
| `GET /api/multi-trend` | `keywords, days` | `{ "kw1": [{date,count}], "kw2": [...] }` — trend comparato |
| `GET /api/export` | `q, source, from, to` | CSV completo delle news filtrate |

### API AI

| Endpoint | Descrizione |
|----------|-------------|
| `GET /api/ai/status` | `{ available: true/false }` — stato Ollama |
| `GET /api/ai/analyze?count=N` | Analizza N news non processate → `{ analyzed, pending }` |
| `GET /api/ai/briefing` | Briefing giornaliero (genera o restituisce cache) |
| `GET /api/ai/sentiment-stats?days=N` | `{ positive, negative, neutral, bullish, bearish }` |
| `GET /api/ai/sentiment-trend?days=N` | Trend giornaliero sentiment AI |
| `GET /api/ai/entities?days=N&top=N` | Top entità estratte dall'AI |
| `GET /api/ai/topics?days=N` | Top topic rilevati dall'AI |
| `GET /api/ai/coverage` | `{ analyzed, total, percentage }` — copertura AI |
| `GET /api/ai/recent?limit=N` | Ultime N analisi AI con tutti i dettagli |

---

## Database (SQLite)

File: `news.db` nella directory dell'applicazione.

### Tabella `News`
```
Id              INTEGER PK AUTOINCREMENT
Source          TEXT         -- "Milano Finanza", "CNBC Markets", ...
Category        TEXT         -- "RSS", "HTML", "BROWSER"
Title           TEXT         -- Titolo della notizia
Url             TEXT         -- Link all'articolo originale
ScrapedAtUtc    DATETIME     -- Data/ora di raccolta (UTC)
TitleNormalized TEXT         -- Titolo normalizzato per dedup
```
Indici: `TitleNormalized`, `ScrapedAtUtc`, `Source`, `Category`

### Tabella `AiAnalyses`
```
Id              INTEGER PK AUTOINCREMENT
NewsItemId      INTEGER FK → News.Id (UNIQUE)
Sentiment       TEXT         -- "positive", "negative", "neutral"
SentimentScore  REAL         -- -1.0 a +1.0
Summary         TEXT         -- Riassunto AI della news
Entities        TEXT         -- JSON array ["Tesla", "S&P 500", ...]
Topics          TEXT         -- JSON array ["stocks", "crypto", ...]
MarketImpact    TEXT         -- "bullish", "bearish", "neutral", "mixed"
AnalyzedAtUtc   DATETIME     -- Data/ora analisi
```
Indici: `NewsItemId` (unique), `Sentiment`, `MarketImpact`

### Tabella `AiDailyBriefings`
```
Id              INTEGER PK AUTOINCREMENT
Date            TEXT UNIQUE  -- "2026-04-07" (uno per giorno)
Summary         TEXT         -- Riepilogo giornaliero in italiano
KeyThemes       TEXT         -- JSON array dei temi principali
MarketOutlook   TEXT         -- Prospettiva di mercato
TopMovers       TEXT         -- JSON array dei titoli più impattati
RiskFactors     TEXT         -- JSON array dei fattori di rischio
GeneratedAtUtc  DATETIME     -- Data/ora generazione
```

### Pulizia automatica
Ad ogni ciclo di scraping vengono eliminate automaticamente le news più vecchie di **1 anno** (365 giorni), insieme alle relative analisi AI.

---

## Gestione risorse e ottimizzazioni

| Strategia | Dettaglio |
|-----------|----------|
| **Refresh ogni 4 ore** | Riduce il carico di scraping da 24 a 6 cicli/giorno |
| **Scraping parallelo limitato** | `SemaphoreSlim(3)` — max 3 pagine Playwright contemporanee, bilancia velocità e RAM |
| **AI max 200/giorno, loop continuo** | Background indipendente, ~1 news ogni 7 min, spalmate su 24h |
| **Throttling AI** | Ritmo auto-adattivo: secondi_rimasti / news_rimaste (min 30s) |
| **Pulizia DB automatica** | Elimina news e analisi AI più vecchie di 1 anno ad ogni ciclo |
| **Ollama ottimizzato** | 1 modello in RAM, 1 richiesta parallela, keep-alive 10min, warm-up in background |
| **GC .NET limitato** | `DOTNET_GCHeapHardLimit=256MB` per contenere l'uso di memoria |

---

## Fonti

### Browser (Playwright — 20 fonti)

| # | Fonte | Tipo | Note |
|---|-------|------|------|
| 1 | Milano Finanza | 🇮🇹 IT | Principale fonte italiana |
| 2 | Borsa Italiana | 🇮🇹 IT | Dati di borsa ufficiali |
| 3 | Il Sole 24 Ore | 🇮🇹 IT | Sezione finanza |
| 4 | Corriere Economia | 🇮🇹 IT | Sezione economia |
| 5 | Repubblica Economia | 🇮🇹 IT | Sezione economia |
| 6 | Teleborsa | 🇮🇹 IT | Notizie finanziarie |
| 7 | Sky TG24 Economia | 🇮🇹 IT | Sezione economia |
| 8 | Yahoo Finance IT | 🇮🇹 Aggregatore | |
| 9 | Bing News Finance (IT) | 🇮🇹 Aggregatore | Sorted by date |
| 10 | Bing News Markets (EN) | 🇬🇧 Aggregatore | Sorted by date |
| 11 | CNBC Markets | 🇬🇧 EN | |
| 12 | CNN Business | 🇬🇧 EN | |
| 13 | BBC Business | 🇬🇧 EN | |
| 14 | Barron's | 🇬🇧 EN | Market data |
| 15 | TradingView | 🇬🇧 EN | News feed |
| 16 | Business Insider | 🇬🇧 EN | Markets section |
| 17 | The Guardian Business | 🇬🇧 EN | |
| 18 | Kitco | 🌍 Commodities | Oro, metalli |
| 19 | CoinDesk | 🌍 Crypto | Markets section |
| 20 | DW Business | 🇬🇧 EN | Deutsche Welle |

### HTML (HtmlAgilityPack — 1 fonte)

| Fonte | Tipo |
|-------|------|
| Finviz | 🇬🇧 EN — stock screener |

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

## Tech Stack

| Componente | Tecnologia |
|------------|-----------|
| Runtime | .NET 8.0 / C# |
| Web server | ASP.NET Minimal API |
| Browser headless | Microsoft.Playwright 1.49.0 (Chromium) |
| HTML parser | HtmlAgilityPack 1.12.4 |
| RSS parser | System.Xml.Linq |
| Database | SQLite via Entity Framework Core 8.0 |
| AI locale | Ollama + phi3 (3.8B parametri, ~2.3GB) |
| AI cloud | OpenAI API (gpt-4o-mini) — opzionale |
| Grafici | Chart.js 4 (CDN, lato client) |
| Container | Docker multi-stage (SDK → Ollama → Playwright runtime) |
| Deploy | Railway (single container) |

---

## Esecuzione locale

```bash
# Build
dotnet build

# Installa browser Chromium per Playwright
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

Senza volume, il database SQLite e il modello phi3 (~2.3GB) vengono persi ad ogni deploy.

1. Nel dashboard Railway → **Service → Volumes → Add Volume**
2. **Mount path**: `/data`
3. **Size**: 5GB

Il volume mantiene tra i deploy:
- `news.db` — tutto lo storico delle news e analisi AI
- `ollama/models/` — il modello phi3 (non viene riscaricato)

### Cosa succede ad ogni deploy

```
1. Railway rebuilda il container (build stage)
2. Il container si riavvia
3. entrypoint.sh:
   ├── Crea /data/ (DB + modelli)
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

---

## Variabili d'ambiente

| Variabile | Default | Descrizione |
|-----------|---------|-------------|
| `PORT` | `5050` (locale) / `8080` (Railway) | Porta web server |
| `DB_PATH` | `/data/news.db` (Railway) / `./news.db` (locale) | Percorso database SQLite |
| `AI_PROVIDER` | `ollama` | Provider: `ollama` o `openai` |
| `AI_MODEL` | `phi3` | Modello AI |
| `OLLAMA_URL` | `http://localhost:11434` | URL Ollama |
| `OLLAMA_MODELS` | `/data/ollama/models` | Path storage modelli |
| `OPENAI_API_KEY` | — | Solo se `AI_PROVIDER=openai` |
| `OLLAMA_NUM_PARALLEL` | `1` | Richieste parallele Ollama |
| `OLLAMA_MAX_LOADED_MODELS` | `1` | Modelli in RAM simultanei |
| `OLLAMA_KEEP_ALIVE` | `60` | Secondi prima di scaricare modello dalla RAM |
| `DOTNET_GCHeapHardLimit` | `0x10000000` | Limite heap GC .NET (256MB) |

### Modelli AI alternativi

| Modello | Parametri | RAM | Qualità |
|---------|-----------|-----|---------|
| `phi3` | 3.8B | ~3.5GB | ⭐⭐⭐ Default — buon compromesso |
| `gemma:2b` | 2B | ~2GB | ⭐⭐ Più leggero |
| `qwen2:0.5b` | 0.5B | ~500MB | ⭐ Ultra-leggero |
| `llama3` | 8B | ~5GB | ⭐⭐⭐⭐ Più preciso, serve più RAM |

---

## Alternative hosting

| Piattaforma | Prezzo | RAM | Note |
|-------------|--------|-----|------|
| **Railway** (attuale) | ~$5/mese + consumo | 8GB condivisi | Setup zero, deploy da GitHub |
| **Hetzner CAX21 (ARM)** | ~€5.90/mese | 8GB | Ottimo rapporto prezzo/prestazioni, .NET 8 supporta ARM |
| **Hetzner CX32** | ~€7.50/mese | 8GB | 4 vCPU x86, perfetto per Playwright + Ollama |
| **Hetzner CX42** | ~€14.90/mese | 16GB | Modelli AI più grandi (llama3 8B) |
| **Oracle Cloud Free** | Gratis per sempre | 24GB ARM | 4 core ARM, ideale per Ollama |
| **Render** | Free tier | Limitata | Free tier dorme dopo 15min inattività |
| **Fly.io** | Free tier | 256MB | Troppo poco per Playwright + Ollama |

**Consiglio con budget $20/mese**: Hetzner CAX21 (ARM, €5.90) — 8GB RAM bastano per tutto, avanzano $14 per upgrade futuri.

---

## Struttura progetto

```
FinancialNewsScraper/
├── Dockerfile                      # Multi-stage: SDK → Ollama → Playwright runtime
├── entrypoint.sh                   # Avvia Ollama + pull modello + app .NET
├── railway.toml                    # Config deploy Railway
├── FinancialNewsScraper.sln
└── FinancialNewsScraper/
    ├── FinancialNewsScraper.csproj  # .NET 8, Playwright, EF Core, HtmlAgilityPack
    ├── Program.cs                  # Scraping engine + web server + 4 pagine HTML + API
    ├── Ai/
    │   └── AiService.cs            # Client Ollama/OpenAI, prompt engineering, parsing JSON
    └── Data/
        ├── NewsDbContext.cs         # EF Core context + modelli (News, AiAnalysis, Briefing)
        └── NewsRepository.cs       # Query, analytics, sentiment, velocity, co-occurrence, cleanup
```

## Licenza

Uso personale.
