using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FinancialNewsScraper.Ai;
using FinancialNewsScraper.Data;
using HtmlAgilityPack;
using Microsoft.Playwright;

namespace FinancialNewsScraper;

public record NewsItem(string Source, string Title, string Url, DateTime ScrapedAt);

public static class Program
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly Regex FinanceKeywords = new(
        @"\b(stock|stocks|market|markets|shares|equit|index|indices|s&p|nasdaq|dow\s?jones|" +
        @"ftse|dax|nikkei|wall\s?street|bull|bear|rally|sell.?off|trading|trader|" +
        @"bond|bonds|yield|treasury|treasuries|fed|ecb|interest\s?rate|rate\s?(hike|cut)|" +
        @"inflation|cpi|gdp|recession|growth|earnings|revenue|profit|eps|" +
        @"oil|crude|gold|commodit|bitcoin|crypto|forex|currency|dollar|euro|yen|" +
        @"etf|fund|investor|portfolio|dividend|ipo|merger|acquisition|" +
        @"bank|banking|fintech|hedge|derivatives|futures|options|" +
        @"borsa|mercat[oi]|azioni|titol[oi]|indic[ei]|piazza\s?affari|" +
        @"rendiment[oi]|obbligazion|spread|btp|bund|tassi|tasso|" +
        @"inflazione|pil|utili|ricavi|petrolio|oro|valut[ea])\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SeenTitles = new(StringComparer.OrdinalIgnoreCase);

    // Dati condivisi tra scraper e web server
    private static readonly ConcurrentDictionary<string, List<NewsItem>> LatestNews = new();
    private static readonly SemaphoreSlim ScrapeSemaphore = new(3); // max 3 scraper paralleli
    private static DateTime _lastUpdate = DateTime.MinValue;
    private const int MaxAiPerDay = 200;
    private static bool _isUpdating;
    private static NewsRepository _repository = null!;
    private static AiService _aiService = null!;
    private static readonly TimeZoneInfo ItalyTz = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Rome");
    private static DateTime ItalyNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ItalyTz);

    static Program()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Financial News Scraper ===\n");

        // == Database (su volume persistente in produzione, locale in dev) ==
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "news.db");
        _repository = new NewsRepository(dbPath);
        Console.WriteLine($"Database: {dbPath}\n");

        // == AI Service ==
        Console.WriteLine("Verifica connessione AI...");
        _aiService = new AiService();
        await _aiService.CheckConnectionAsync();

        // == Web Server ==
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(BuildHtmlPage(), "text/html; charset=utf-8"));
        app.MapGet("/archive", () => Results.Content(BuildArchivePage(), "text/html; charset=utf-8"));
        app.MapGet("/analytics", () => Results.Content(BuildAnalyticsPage(), "text/html; charset=utf-8"));
        app.MapGet("/ai", () => Results.Content(BuildAiInsightsPage(), "text/html; charset=utf-8"));

        // == API Endpoints ==
        app.MapGet("/api/search", async (string? q, string? source, string? from, string? to, int? limit, int? offset) =>
        {
            DateTime? fromDt = string.IsNullOrEmpty(from) ? null : DateTime.Parse(from);
            DateTime? toDt = string.IsNullOrEmpty(to) ? null : DateTime.Parse(to);
            var results = await _repository.SearchAsync(q, source, fromDt, toDt, limit ?? 100, offset ?? 0);
            var count = await _repository.CountAsync(q, source, fromDt, toDt);
            return Results.Json(new { total = count, items = results });
        });

        app.MapGet("/api/trend", async (string? keyword, int? days) =>
        {
            var trend = await _repository.GetDailyTrendAsync(keyword, days ?? 30);
            return Results.Json(trend);
        });

        app.MapGet("/api/top-keywords", async (int? days, int? top) =>
        {
            var keywords = await _repository.GetTopKeywordsAsync(days ?? 30, top ?? 20);
            return Results.Json(keywords.Select(k => new { keyword = k.Keyword, count = k.Count }));
        });

        app.MapGet("/api/sources", async () =>
        {
            var sources = await _repository.GetAllSourcesAsync();
            return Results.Json(sources);
        });

        app.MapGet("/api/stats", async () =>
        {
            var stats = await _repository.GetStatsAsync();
            var catBreakdown = await _repository.GetCategoryBreakdownAsync();
            var srcBreakdown = await _repository.GetSourceBreakdownAsync();
            return Results.Json(new { stats.Total, stats.FirstDate, stats.LastDate, categories = catBreakdown, sources = srcBreakdown });
        });

        app.MapGet("/api/sentiment", async (int? days) =>
        {
            var sentiment = await _repository.GetSentimentAsync(days ?? 30);
            return Results.Json(sentiment);
        });

        app.MapGet("/api/sentiment-trend", async (int? days) =>
        {
            var trend = await _repository.GetSentimentTrendAsync(days ?? 30);
            return Results.Json(trend);
        });

        app.MapGet("/api/hourly", async (int? days) =>
        {
            var data = await _repository.GetHourlyDistributionAsync(days ?? 30);
            return Results.Json(data);
        });

        app.MapGet("/api/weekday", async (int? days) =>
        {
            var data = await _repository.GetWeekdayDistributionAsync(days ?? 30);
            return Results.Json(data);
        });

        app.MapGet("/api/velocity", async () =>
        {
            var data = await _repository.GetKeywordVelocityAsync();
            return Results.Json(data);
        });

        app.MapGet("/api/cooccurrence", async (int? days, int? top) =>
        {
            var data = await _repository.GetCoOccurrencesAsync(days ?? 30, top ?? 20);
            return Results.Json(data);
        });

        app.MapGet("/api/multi-trend", async (string keywords, int? days) =>
        {
            var kws = keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var data = await _repository.GetMultiKeywordTrendAsync(kws, days ?? 30);
            return Results.Json(data);
        });

        app.MapGet("/api/export", async (string? q, string? source, string? from, string? to) =>
        {
            DateTime? fromDt = string.IsNullOrEmpty(from) ? null : DateTime.Parse(from);
            DateTime? toDt = string.IsNullOrEmpty(to) ? null : DateTime.Parse(to);
            var csv = await _repository.ExportCsvAsync(q, source, fromDt, toDt);
            return Results.Text(csv, "text/csv; charset=utf-8");
        });

        // == AI API Endpoints ==
        app.MapGet("/api/ai/status", () =>
        {
            return Results.Json(new { available = _aiService.IsAvailable });
        });

        app.MapGet("/api/ai/analyze", async (int? count) =>
        {
            if (!_aiService.IsAvailable)
                return Results.Json(new { error = "AI non disponibile. Configura Ollama o OpenAI." });

            var unanalyzed = await _repository.GetUnanalyzedNewsAsync(count ?? 10);
            var analyzed = 0;
            foreach (var news in unanalyzed)
            {
                var result = await _aiService.AnalyzeNewsAsync(news.Title, news.Source);
                if (result != null)
                {
                    await _repository.SaveAiAnalysisAsync(news.Id, result);
                    analyzed++;
                }
            }
            return Results.Json(new { analyzed, pending = unanalyzed.Count - analyzed });
        });

        app.MapGet("/api/ai/briefing", async () =>
        {
            if (!_aiService.IsAvailable)
                return Results.Json(new { error = "AI non disponibile." });

            // Controlla se esiste già un briefing di oggi
            var existing = await _repository.GetLatestBriefingAsync();
            if (existing != null && existing.Date == DateTime.UtcNow.ToString("yyyy-MM-dd"))
            {
                return Results.Json(new
                {
                    date = existing.Date,
                    summary = existing.Summary,
                    keyThemes = JsonSerializer.Deserialize<string[]>(existing.KeyThemes),
                    marketOutlook = existing.MarketOutlook,
                    topMovers = JsonSerializer.Deserialize<string[]>(existing.TopMovers),
                    riskFactors = JsonSerializer.Deserialize<string[]>(existing.RiskFactors),
                    cached = true
                });
            }

            // Genera un nuovo briefing
            var today = DateTime.UtcNow.Date;
            var recentNews = await _repository.SearchAsync(from: today, limit: 100);
            if (recentNews.Count == 0)
                recentNews = await _repository.SearchAsync(limit: 50);

            var briefing = await _aiService.GenerateDailyBriefingAsync(recentNews.Select(n => n.Title));
            if (briefing == null)
                return Results.Json(new { error = "Generazione briefing fallita." });

            await _repository.SaveDailyBriefingAsync(briefing);
            return Results.Json(new
            {
                date = briefing.Date,
                summary = briefing.Summary,
                keyThemes = briefing.KeyThemes,
                marketOutlook = briefing.MarketOutlook,
                topMovers = briefing.TopMovers,
                riskFactors = briefing.RiskFactors,
                cached = false
            });
        });

        app.MapGet("/api/ai/sentiment-stats", async (int? days) =>
        {
            var stats = await _repository.GetAiSentimentStatsAsync(days ?? 30);
            return Results.Json(new
            {
                positive = stats.Positive, negative = stats.Negative, neutral = stats.Neutral,
                bullish = stats.Bullish, bearish = stats.Bearish
            });
        });

        app.MapGet("/api/ai/sentiment-trend", async (int? days) =>
        {
            var trend = await _repository.GetAiSentimentTrendAsync(days ?? 30);
            return Results.Json(trend);
        });

        app.MapGet("/api/ai/entities", async (int? days, int? top) =>
        {
            var entities = await _repository.GetAiTopEntitiesAsync(days ?? 30, top ?? 20);
            return Results.Json(entities.Select(e => new { entity = e.Entity, count = e.Count }));
        });

        app.MapGet("/api/ai/topics", async (int? days) =>
        {
            var topics = await _repository.GetAiTopTopicsAsync(days ?? 30);
            return Results.Json(topics.Select(t => new { topic = t.Topic, count = t.Count }));
        });

        app.MapGet("/api/ai/coverage", async () =>
        {
            var coverage = await _repository.GetAiCoverageAsync();
            return Results.Json(new { analyzed = coverage.Analyzed, total = coverage.Total,
                percentage = coverage.Total > 0 ? Math.Round((double)coverage.Analyzed / coverage.Total * 100, 1) : 0 });
        });

        app.MapGet("/api/ai/recent", async (int? limit) =>
        {
            var items = await _repository.GetRecentAiAnalysesAsync(limit ?? 20);
            return Results.Json(items.Select(i => new
            {
                title = i.News.Title, source = i.News.Source, url = i.News.Url,
                scrapedAt = i.News.ScrapedAtUtc,
                sentiment = i.Analysis.Sentiment, sentimentScore = i.Analysis.SentimentScore,
                summary = i.Analysis.Summary, marketImpact = i.Analysis.MarketImpact,
                entities = JsonSerializer.Deserialize<string[]>(i.Analysis.Entities),
                topics = JsonSerializer.Deserialize<string[]>(i.Analysis.Topics)
            }));
        });

        var port = Environment.GetEnvironmentVariable("PORT") ?? "5050";
        var listenUrl = $"http://0.0.0.0:{port}";
        _ = app.RunAsync(listenUrl);
        Console.WriteLine($"Web server avviato: {listenUrl}\n");

        // == Playwright ==
        Console.WriteLine("Inizializzazione browser headless...");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        Console.WriteLine("Browser pronto. Premi Ctrl+C per uscire.\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // == AI Background Task: processa max 200 news/giorno, spalmate lentamente ==
        if (_aiService.IsAvailable)
        {
            _ = Task.Run(async () => await RunAiBackgroundLoopAsync(cts.Token));
            Console.WriteLine($"  [AI] Background loop avviato (max {MaxAiPerDay} news/giorno, ~1 ogni {24 * 60 / MaxAiPerDay} min)\n");
        }

        while (!cts.Token.IsCancellationRequested)
        {
            _isUpdating = true;
            SeenTitles.Clear();

            // Scrape in un buffer temporaneo, così la pagina mostra sempre le ultime notizie
            var tempNews = new ConcurrentDictionary<string, List<NewsItem>>();
            var originalNews = LatestNews;

            Console.WriteLine($"[{ItalyNow:HH:mm:ss}] Aggiornamento in corso...");
            await ScrapeAllAsync(browser, cts.Token, tempNews);

            // Aggiorna le notizie solo a scraping completato
            LatestNews.Clear();
            foreach (var kv in tempNews)
                LatestNews[kv.Key] = kv.Value;

            // Salva nel database
            var allItems = tempNews.SelectMany(kv => kv.Value.Select(item => (kv.Key, item)));
            var saved = await _repository.SaveNewsAsync(allItems);

            _lastUpdate = ItalyNow;
            _isUpdating = false;

            Console.WriteLine($"[{ItalyNow:HH:mm:ss}] Completato - {LatestNews.Values.Sum(l => l.Count)} notizie totali, {saved} nuove salvate nel DB");

            // == Pulizia news vecchie (> 365 giorni / 1 anno) ==
            var deleted = await _repository.DeleteOldNewsAsync(365);
            if (deleted > 0)
                Console.WriteLine($"  [CLEANUP] Eliminate {deleted} news più vecchie di 1 anno");

            Console.WriteLine($"Pagina: http://localhost:{port}\n");

            try { await Task.Delay(TimeSpan.FromHours(4), cts.Token); }
            catch (TaskCanceledException) { break; }
        }

        Console.WriteLine("\nUscita...");
    }

    // ================================================================
    //  AI Background Loop — max 200 news/giorno spalmate lentamente
    // ================================================================

    private static async Task RunAiBackgroundLoopAsync(CancellationToken ct)
    {
        // Attende 30 secondi all'avvio per dare tempo al primo scraping di popolare il DB
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        // Pre-carica il modello in RAM (fatto qui e non al startup per non bloccare il web server)
        var warmupOk = await _aiService.WarmUpModelAsync();
        if (!warmupOk)
        {
            Console.WriteLine("  [AI] Warm-up fallito, riprovo tra 60s...");
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            await _aiService.WarmUpModelAsync();
        }

        var analyzedToday = await _repository.GetTodayAnalysisCountAsync();
        Console.WriteLine($"  [AI] Avvio — dal DB: {analyzedToday}/{MaxAiPerDay} analisi già completate oggi");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var todayDate = ItalyNow.Date;

                // Finestra operativa: 8:00 - 16:00 (ora italiana)
                var hour = ItalyNow.Hour;
                if (hour < 8 || hour >= 16)
                {
                    // Fuori finestra: calcola attesa fino alle 8:00
                    var next8am = hour >= 16
                        ? todayDate.AddDays(1).AddHours(8)
                        : todayDate.AddHours(8);
                    var waitTime = next8am - ItalyNow;
                    Console.WriteLine($"  [AI] Fuori finestra operativa (8:00-16:00). Prossimo avvio tra {waitTime.Hours}h {waitTime.Minutes}m");
                    await Task.Delay(waitTime, ct);
                    continue;
                }

                // Legge sempre il conteggio reale dal DB (unica fonte di verità)
                analyzedToday = await _repository.GetTodayAnalysisCountAsync();

                var remaining = MaxAiPerDay - analyzedToday;
                if (remaining <= 0)
                {
                    // Budget esaurito: attendi fino a domani alle 8:00
                    var next8am = todayDate.AddDays(1).AddHours(8);
                    var waitTime = next8am - ItalyNow;
                    Console.WriteLine($"  [AI] Budget giornaliero esaurito ({analyzedToday}/{MaxAiPerDay}). Prossimo reset tra {waitTime.Hours}h {waitTime.Minutes}m");
                    await Task.Delay(waitTime, ct);
                    continue;
                }

                // 200 analisi in 8h (8:00-16:00) = 1 ogni 144 secondi
                var delayBetweenAnalyses = TimeSpan.FromSeconds(144);

                // Pesca 1 news con fair sampling da tutte le fonti
                var batch = await _repository.GetUnanalyzedNewsFairSampledAsync(1);
                if (batch.Count == 0)
                {
                    // Nessuna news da analizzare: attendi 10 minuti e riprova
                    await Task.Delay(TimeSpan.FromMinutes(10), ct);
                    continue;
                }

                var news = batch[0];
                if (analyzedToday == 0)
                    Console.WriteLine($"  [AI] Prima analisi in corso: \"{news.Title[..Math.Min(60, news.Title.Length)]}...\" (fonte: {news.Source})");
                var result = await _aiService.AnalyzeNewsAsync(news.Title, news.Source);
                if (result != null)
                {
                    await _repository.SaveAiAnalysisAsync(news.Id, result);
                    analyzedToday++;

                    if (analyzedToday <= 3 || analyzedToday % 20 == 0)
                        Console.WriteLine($"  [AI] Analisi #{analyzedToday}/{MaxAiPerDay}: {result.Sentiment} ({result.SentimentScore:+0.0;-0.0}) — \"{news.Title[..Math.Min(50, news.Title.Length)]}...\" (ritmo: ~{(int)delayBetweenAnalyses.TotalSeconds}s)");
                }
                else if (analyzedToday == 0)
                {
                    Console.WriteLine($"  [AI] Analisi fallita per: \"{news.Title[..Math.Min(60, news.Title.Length)]}...\" — Ollama non ha risposto");
                }

                // Genera briefing giornaliero dopo aver analizzato almeno 30 news
                if (analyzedToday >= 30)
                {
                    var existingBriefing = await _repository.GetLatestBriefingAsync();
                    if (existingBriefing == null || existingBriefing.Date != DateTime.UtcNow.ToString("yyyy-MM-dd"))
                    {
                        var todayUtc = DateTime.UtcNow.Date;
                        var recentNews = await _repository.SearchAsync(from: todayUtc, limit: 100);
                        if (recentNews.Count == 0)
                            recentNews = await _repository.SearchAsync(limit: 50);

                        var briefing = await _aiService.GenerateDailyBriefingAsync(recentNews.Select(n => n.Title));
                        if (briefing != null)
                        {
                            await _repository.SaveDailyBriefingAsync(briefing);
                            Console.WriteLine($"  [AI] Briefing giornaliero generato ({analyzedToday} news analizzate finora)");
                        }
                    }
                }

                // Attendi il tempo calcolato prima della prossima analisi
                await Task.Delay(delayBetweenAnalyses, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Microsoft.Data.Sqlite.SqliteException dbEx)
            {
                Console.WriteLine($"  [AI] Errore DB: {dbEx.Message} (tabella mancante o schema incompatibile?)");
                Console.WriteLine($"  [AI] Stack: {dbEx.StackTrace?.Split('\n').FirstOrDefault()}");
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [AI] Errore: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"  [AI] Inner: {ex.InnerException.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        }
    }

    // ================================================================
    //  Scraping
    // ================================================================

    private static async Task ScrapeAllAsync(IBrowser browser, CancellationToken ct, ConcurrentDictionary<string, List<NewsItem>> target)
    {
        // == BROWSER SCRAPERS (priorità) ==
        var browserScrapers = new (string Name, string Url, string CssSelector)[]
        {
            // -- Priorità: Italiani --
            ("Milano Finanza",    "https://www.milanofinanza.it/",                      "h2 a, h3 a, h4 a, article a, a[href*='/news/'], a[href*='/mercati/'], .title a"),
            ("Borsa Italiana",    "https://www.borsaitaliana.it/",                      "h3 a, h2 a, h4 a, article a, a[href*='/notizie/'], a[href*='/borsa/'], .link-news a, td a"),
            ("Il Sole 24 Ore",    "https://www.ilsole24ore.com/finanza",                "article a, h2 a, h3 a, h4 a, .aheadpost a, a[href*='/art/'], a[href*='/finanza/']"),
            ("Corriere Economia", "https://www.corriere.it/economia/",                  "h2 a, h3 a, h4 a, article a, a[href*='/economia/'], a[href*='/finanza/']"),
            ("Repubblica Econ.",  "https://www.repubblica.it/economia/",                "h2 a, h3 a, h4 a, article a, a[href*='/economia/'], a[href*='/affari/']"),
            ("Teleborsa",         "https://www.teleborsa.it/",                          "h3 a, h2 a, h4 a, article a, a[href*='/News/'], a[href*='/Finanza']"),
            ("Sky TG24 Economia", "https://tg24.sky.it/economia",                       "h3 a, h2 a, h4 a, article a, a[href*='/economia/']"),
            // -- Priorità: Motori di ricerca / Aggregatori --
            ("Yahoo Finance IT",  "https://it.finance.yahoo.com/",                      "h3 a, h2 a, li[class*='stream'] a, a[href*='/news/'], a[class*='title']"),
            ("Bing News Finance", "https://www.bing.com/news/search?q=borsa+mercati+finanza&qft=sortbydate%3d%221%22", "a.title, h4 a, a[href*='/news/'], .news-card a, a[class*='title']"),
            ("Bing News Markets", "https://www.bing.com/news/search?q=stock+market+wall+street&qft=sortbydate%3d%221%22", "a.title, h4 a, a[href*='/news/'], .news-card a, a[class*='title']"),
            // -- Internazionali EN --
            ("CNBC Markets",      "https://www.cnbc.com/markets/",                      "a.Card-title, div.Card a, h3 a"),
            ("CNN Business",      "https://edition.cnn.com/business",                   "a.container__link--type-article span.container__headline-text, h3 a, a[data-link-type='article']"),
            ("BBC Business",      "https://www.bbc.com/business",                       "h3 a, a[data-testid='internal-link'] h2, a[data-testid='internal-link'] span"),
            ("Barron's",          "https://www.barrons.com/market-data",                "h3 a, .article__headline a, a[href*='/articles/']"),
            ("TradingView",       "https://www.tradingview.com/news/",                  "a[class*='title'], article a, h3 a, a[href*='/news/']"),
            ("Business Insider",  "https://www.businessinsider.com/markets",            "h2 a, h3 a, a[href*='/news/'], a[data-analytics-post-type]"),
            ("The Guardian",      "https://www.theguardian.com/business",               "h3 a, a[data-link-name='article'] span, .fc-item__title a"),
            ("Kitco",             "https://www.kitco.com/news/",                        "h3 a, h4 a, a[href*='/news/article/'], .article-title a"),
            ("CoinDesk",          "https://www.coindesk.com/markets/",                  "h4 a, h5 a, h6 a, a[class*='card-title'], article a[href*='/20']"),
            ("DW Business",       "https://www.dw.com/en/business/s-1431",              "h3 a, a[href*='/a-'], span[class*='headline']"),
        };

        // Scraping parallelo con concorrenza limitata (max 3 pagine contemporanee)
        var browserTasks = browserScrapers.Select(s => Task.Run(async () =>
        {
            await ScrapeSemaphore.WaitAsync(ct);
            try
            {
                var news = await ScrapeBrowserAsync(browser, s.Name, s.Url, s.CssSelector);
                target[$"[BROWSER] {s.Name}"] = news;
                PrintSection($"[BROWSER] {s.Name}", news);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  [BROWSER {s.Name}] Errore: {ex.Message}");
            }
            finally
            {
                ScrapeSemaphore.Release();
            }
        })).ToArray();
        await Task.WhenAll(browserTasks);

        // == HTML Scraper ==
        try
        {
            var news = await ScrapeFinvizAsync(ct);
            target["[HTML] Finviz"] = news;
            PrintSection("[HTML] Finviz", news);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  [HTML Finviz] Errore: {ex.Message}");
        }

        // == RSS Feeds (backup) ==
        var feeds = new (string Name, string Url)[]
        {
            ("MarketWatch",        "https://feeds.marketwatch.com/marketwatch/topstories"),
            ("Investing.com",      "https://www.investing.com/rss/news.rss"),
            ("Il Sole 24 Ore",     "https://www.ilsole24ore.com/rss/finanza.xml"),
            ("Bloomberg (via GN)", "https://news.google.com/rss/search?q=site:bloomberg.com+when:1d&hl=en&gl=US&ceid=US:en"),
            ("FT (via GN)",        "https://news.google.com/rss/search?q=site:ft.com+markets+when:1d&hl=en&gl=US&ceid=US:en"),
            ("WSJ (via GN)",       "https://news.google.com/rss/search?q=site:wsj.com+markets+when:1d&hl=en&gl=US&ceid=US:en"),
        };

        foreach (var (name, url) in feeds)
        {
            try
            {
                var news = await FetchRssFeedAsync(name, url, ct);
                target[$"[RSS] {name}"] = news;
                PrintSection($"[RSS] {name}", news);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  [RSS {name}] Errore: {ex.Message}");
            }
        }
    }

    // ================================================================
    //  RSS
    // ================================================================

    private static async Task<List<NewsItem>> FetchRssFeedAsync(string source, string url, CancellationToken ct)
    {
        var xml = await Http.GetStringAsync(url, ct);
        var doc = XDocument.Parse(xml);
        var items = new List<NewsItem>();

        foreach (var item in doc.Descendants("item"))
        {
            var title = WebUtility.HtmlDecode(item.Element("title")?.Value?.Trim() ?? "");
            var description = WebUtility.HtmlDecode(item.Element("description")?.Value?.Trim() ?? "");
            var link = item.Element("link")?.Value?.Trim() ?? "";

            if (!IsValidFinanceTitle(title, description)) continue;
            if (!SeenTitles.Add(NormalizeForDedup(title))) continue;

            items.Add(new NewsItem(source, title, link, ItalyNow));
            if (items.Count >= 10) break;
        }
        return items;
    }

    // ================================================================
    //  HTML Scrapers
    // ================================================================

    private static async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9,it;q=0.8");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "none");

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task<List<NewsItem>> ScrapeFinvizAsync(CancellationToken ct)
    {
        var html = await GetHtmlAsync("https://finviz.com/news.ashx", ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var items = new List<NewsItem>();

        var nodes = doc.DocumentNode.SelectNodes(
            "//table[@id='news']//a[contains(@class,'nn-tab-link')] | //div[@id='news']//a[@class='nn-tab-link']");
        if (nodes == null) return items;

        foreach (var node in nodes)
        {
            var title = CleanText(WebUtility.HtmlDecode(node.InnerText));
            var href = node.GetAttributeValue("href", "");
            if (!IsValidFinanceTitle(title, "")) continue;
            if (!SeenTitles.Add(NormalizeForDedup(title))) continue;
            items.Add(new NewsItem("Finviz", title, href, ItalyNow));
            if (items.Count >= 10) break;
        }
        return items;
    }

    // ================================================================
    //  Browser Scrapers (Playwright)
    // ================================================================

    private static async Task<List<NewsItem>> ScrapeBrowserAsync(
        IBrowser browser, string source, string url, string cssSelector)
    {
        var page = await browser.NewPageAsync();
        try
        {
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 25_000 });
            // Aspetta caricamento JS + scroll per contenuto lazy-loaded
            await page.WaitForTimeoutAsync(2000);
            await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight / 2)");
            await page.WaitForTimeoutAsync(1500);
            await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
            await page.WaitForTimeoutAsync(1500);

            var items = new List<NewsItem>();
            var elements = await page.QuerySelectorAllAsync(cssSelector);

            foreach (var el in elements)
            {
                var title = CleanText(await el.InnerTextAsync() ?? "");
                var href = await el.GetAttributeAsync("href") ?? "";
                if (string.IsNullOrEmpty(href))
                {
                    var parent = await el.EvaluateAsync<string?>("e => e.closest('a')?.href");
                    href = parent ?? "";
                }
                // Risolvi URL relativi
                if (!string.IsNullOrEmpty(href) && href.StartsWith("/"))
                {
                    var uri = new Uri(url);
                    href = $"{uri.Scheme}://{uri.Host}{href}";
                }
                if (!IsValidFinanceTitle(title, "")) continue;
                if (!SeenTitles.Add(NormalizeForDedup(title))) continue;
                items.Add(new NewsItem(source, title, href, ItalyNow));
                if (items.Count >= 10) break;
            }
            return items;
        }
        finally { await page.CloseAsync(); }
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static bool IsValidFinanceTitle(string title, string description)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length < 10) return false;
        return FinanceKeywords.IsMatch(title) || FinanceKeywords.IsMatch(description);
    }

    private static string NormalizeForDedup(string title) =>
        Regex.Replace(title.ToLowerInvariant(), @"[^\w]", "");

    private static string CleanText(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();

    private static void PrintSection(string source, List<NewsItem> items)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  {source}: {items.Count} notizie");
        Console.ResetColor();
    }

    // ================================================================
    //  HTML Page
    // ================================================================

    private static string BuildHtmlPage()
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            <!DOCTYPE html>
            <html lang="it">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Financial News Scraper</title>
            <style>
              * { margin: 0; padding: 0; box-sizing: border-box; }
              body {
                font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                background: #0d1117; color: #c9d1d9; line-height: 1.6;
              }
              header {
                background: linear-gradient(135deg, #161b22, #1a2332);
                border-bottom: 1px solid #30363d; padding: 20px 30px;
                display: flex; justify-content: space-between; align-items: center;
                position: sticky; top: 0; z-index: 100; flex-wrap: wrap; gap: 10px;
              }
              header h1 { font-size: 1.4em; color: #58a6ff; }
              nav { display: flex; gap: 12px; }
              nav a { color: #8b949e; text-decoration: none; font-size: 0.9em; padding: 4px 12px;
                      border-radius: 6px; transition: all 0.2s; }
              nav a:hover, nav a.active { color: #58a6ff; background: #1f6feb22; }
              .status { font-size: 0.85em; color: #8b949e; text-align: right; }
              .status .live { color: #3fb950; font-weight: bold; }
              .container { max-width: 1400px; margin: 0 auto; padding: 20px; }
              .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(400px, 1fr)); gap: 20px; }
              .card {
                background: #161b22; border: 1px solid #30363d; border-radius: 8px;
                overflow: hidden; transition: border-color 0.2s;
              }
              .card:hover { border-color: #58a6ff; }
              .card-header {
                padding: 12px 16px; border-bottom: 1px solid #30363d;
                display: flex; justify-content: space-between; align-items: center;
              }
              .card-header h2 { font-size: 0.95em; color: #58a6ff; }
              .card-header .badge {
                background: #1f6feb; color: #fff; padding: 2px 8px;
                border-radius: 10px; font-size: 0.75em; white-space: nowrap;
              }
              .card-header .badge.rss { background: #da3633; }
              .card-header .badge.html { background: #a371f7; }
              .card-header .badge.browser { background: #3fb950; }
              .news-list { list-style: none; padding: 0; }
              .news-item {
                padding: 10px 16px; border-bottom: 1px solid #21262d;
                transition: background 0.15s;
              }
              .news-item:last-child { border-bottom: none; }
              .news-item:hover { background: #1c2333; }
              .news-item a {
                color: #c9d1d9; text-decoration: none; font-size: 0.9em;
                display: block;
              }
              .news-item a:hover { color: #58a6ff; }
              .news-item .time { color: #484f58; font-size: 0.75em; margin-top: 3px; }
              .empty { padding: 20px 16px; color: #484f58; font-style: italic; text-align: center; }
              @media (max-width: 500px) { .grid { grid-template-columns: 1fr; } }
            </style>
            </head>
            <body>
            <header>
              <div>
                <h1>&#128200; Financial News Scraper</h1>
                <nav>
                  <a href="/" class="active">&#127968; Live</a>
                  <a href="/archive">&#128269; Archivio</a>
                  <a href="/analytics">&#128202; Analytics</a>
                  <a href="/ai">&#129302; AI Insights</a>
                </nav>
              </div>
              <div class="status">
            """);

        if (_isUpdating)
            sb.Append("<span class='live'>&#9679; Aggiornamento in corso...</span>");
        else if (_lastUpdate > DateTime.MinValue)
            sb.Append($"Ultimo aggiornamento: <strong>{WebUtility.HtmlEncode(_lastUpdate.ToString("HH:mm:ss"))}</strong> " +
                       "<span class='live'>&#9679; LIVE</span>");
        else
            sb.Append("In attesa del primo aggiornamento...");

        sb.Append("""

                <br><span id="cd"></span>
              </div>
            </header>
            <div class="container">
            <div class="grid">
            """);

        foreach (var (section, items) in LatestNews.OrderByDescending(kv => kv.Value.Count))
        {
            var badge = "rss";
            if (section.Contains("[HTML]")) badge = "html";
            else if (section.Contains("[BROWSER]")) badge = "browser";

            var sectionName = WebUtility.HtmlEncode(section);

            sb.Append($"""
                <div class="card">
                  <div class="card-header">
                    <h2>{sectionName}</h2>
                    <span class="badge {badge}">{badge.ToUpper()}</span>
                  </div>
                  <ul class="news-list">
                """);

            if (items.Count == 0)
            {
                sb.Append("<li class='empty'>Nessuna notizia finanziaria trovata.</li>");
            }
            else
            {
                foreach (var item in items.Take(10))
                {
                    var title = WebUtility.HtmlEncode(item.Title);
                    var url = WebUtility.HtmlEncode(item.Url);
                    var time = item.ScrapedAt.ToString("HH:mm");

                    if (!string.IsNullOrWhiteSpace(item.Url))
                        sb.Append($"""
                            <li class="news-item">
                              <a href="{url}" target="_blank" rel="noopener">{title}</a>
                              <div class="time">{time}</div>
                            </li>
                            """);
                    else
                        sb.Append($"""
                            <li class="news-item">
                              <span style="font-size:0.9em">{title}</span>
                              <div class="time">{time}</div>
                            </li>
                            """);
                }
            }

            sb.Append("  </ul>\n</div>\n");
        }

        sb.Append("""
            </div>
            </div>
            <script>
              let sec = 60;
              const cd = document.getElementById('cd');
              setInterval(() => {
                sec--;
                if (sec <= 0) { location.reload(); return; }
                cd.textContent = 'Refresh tra ' + sec + 's';
              }, 1000);
            </script>
            </body>
            </html>
            """);

        return sb.ToString();
    }

    // ================================================================
    //  Archive Page (Ricerca news passate)
    // ================================================================

    private static string BuildArchivePage()
    {
        return """
            <!DOCTYPE html>
            <html lang="it">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Archivio News - Financial News Scraper</title>
            <style>
              * { margin: 0; padding: 0; box-sizing: border-box; }
              body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                     background: #0d1117; color: #c9d1d9; line-height: 1.6; }
              header { background: linear-gradient(135deg, #161b22, #1a2332);
                       border-bottom: 1px solid #30363d; padding: 20px 30px;
                       display: flex; justify-content: space-between; align-items: center;
                       position: sticky; top: 0; z-index: 100; flex-wrap: wrap; gap: 10px; }
              header h1 { font-size: 1.4em; color: #58a6ff; }
              nav { display: flex; gap: 12px; }
              nav a { color: #8b949e; text-decoration: none; font-size: 0.9em; padding: 4px 12px;
                      border-radius: 6px; transition: all 0.2s; }
              nav a:hover, nav a.active { color: #58a6ff; background: #1f6feb22; }
              .container { max-width: 1200px; margin: 0 auto; padding: 20px; }
              .search-box { background: #161b22; border: 1px solid #30363d; border-radius: 8px;
                            padding: 20px; margin-bottom: 20px; }
              .search-box h2 { color: #58a6ff; margin-bottom: 15px; font-size: 1.1em; }
              .search-row { display: flex; gap: 12px; flex-wrap: wrap; align-items: end; }
              .field { display: flex; flex-direction: column; gap: 4px; }
              .field label { font-size: 0.8em; color: #8b949e; }
              .field input, .field select { background: #0d1117; border: 1px solid #30363d;
                color: #c9d1d9; padding: 8px 12px; border-radius: 6px; font-size: 0.9em; }
              .field input:focus, .field select:focus { border-color: #58a6ff; outline: none; }
              .btn { background: #1f6feb; color: #fff; border: none; padding: 8px 20px;
                     border-radius: 6px; cursor: pointer; font-size: 0.9em; transition: background 0.2s; }
              .btn:hover { background: #388bfd; }
              .results-info { color: #8b949e; font-size: 0.85em; margin-bottom: 12px; }
              .results-table { width: 100%; border-collapse: collapse; }
              .results-table th { text-align: left; padding: 10px 12px; border-bottom: 2px solid #30363d;
                                  color: #58a6ff; font-size: 0.85em; }
              .results-table td { padding: 10px 12px; border-bottom: 1px solid #21262d; font-size: 0.9em; }
              .results-table tr:hover { background: #1c2333; }
              .results-table a { color: #c9d1d9; text-decoration: none; }
              .results-table a:hover { color: #58a6ff; }
              .source-badge { background: #30363d; padding: 2px 8px; border-radius: 10px;
                              font-size: 0.75em; white-space: nowrap; }
              .pagination { display: flex; gap: 8px; margin-top: 16px; justify-content: center; }
              .pagination button { background: #161b22; border: 1px solid #30363d; color: #c9d1d9;
                                   padding: 6px 14px; border-radius: 6px; cursor: pointer; }
              .pagination button:hover { border-color: #58a6ff; }
              .pagination button:disabled { opacity: 0.4; cursor: not-allowed; }
              #loading { color: #8b949e; text-align: center; padding: 40px; display: none; }
            </style>
            </head>
            <body>
            <header>
              <div>
                <h1>&#128200; Financial News Scraper</h1>
                <nav>
                  <a href="/">&#127968; Live</a>
                  <a href="/archive" class="active">&#128269; Archivio</a>
                  <a href="/analytics">&#128202; Analytics</a>
                  <a href="/ai">&#129302; AI Insights</a>
                </nav>
              </div>
            </header>
            <div class="container">
              <div class="search-box">
                <h2>&#128269; Ricerca News Archiviate</h2>
                <div class="search-row">
                  <div class="field" style="flex:2">
                    <label>Testo</label>
                    <input type="text" id="q" placeholder="Cerca per parola chiave...">
                  </div>
                  <div class="field">
                    <label>Sorgente</label>
                    <select id="source"><option value="">Tutte</option></select>
                  </div>
                  <div class="field">
                    <label>Da</label>
                    <input type="date" id="from">
                  </div>
                  <div class="field">
                    <label>A</label>
                    <input type="date" id="to">
                  </div>
                  <button class="btn" onclick="search(0)">Cerca</button>
                </div>
              </div>
              <div class="results-info" id="info"></div>
              <div id="loading">Caricamento...</div>
              <table class="results-table" id="table" style="display:none">
                <thead><tr><th>Data</th><th>Sorgente</th><th>Titolo</th></tr></thead>
                <tbody id="tbody"></tbody>
              </table>
              <div class="pagination" id="pagination"></div>
            </div>
            <script>
              const PAGE_SIZE = 50;
              let currentOffset = 0;
              let totalResults = 0;

              // Carica sorgenti per il filtro
              fetch('/api/sources').then(r=>r.json()).then(sources => {
                const sel = document.getElementById('source');
                sources.forEach(s => { const o = document.createElement('option'); o.value = s; o.textContent = s; sel.appendChild(o); });
              });

              document.getElementById('q').addEventListener('keydown', e => { if(e.key==='Enter') search(0); });

              function search(offset) {
                currentOffset = offset;
                const params = new URLSearchParams();
                const q = document.getElementById('q').value;
                const source = document.getElementById('source').value;
                const from = document.getElementById('from').value;
                const to = document.getElementById('to').value;
                if(q) params.set('q', q);
                if(source) params.set('source', source);
                if(from) params.set('from', from);
                if(to) params.set('to', to);
                params.set('limit', PAGE_SIZE);
                params.set('offset', offset);

                document.getElementById('loading').style.display = 'block';
                document.getElementById('table').style.display = 'none';

                fetch('/api/search?' + params).then(r=>r.json()).then(data => {
                  totalResults = data.total;
                  document.getElementById('info').textContent =
                    `${data.total} risultati trovati` + (data.total > PAGE_SIZE ? ` (pagina ${Math.floor(offset/PAGE_SIZE)+1} di ${Math.ceil(data.total/PAGE_SIZE)})` : '');
                  const tbody = document.getElementById('tbody');
                  tbody.innerHTML = '';
                  data.items.forEach(item => {
                    const tr = document.createElement('tr');
                    const date = new Date(item.scrapedAtUtc).toLocaleString('it-IT', {day:'2-digit',month:'2-digit',year:'numeric',hour:'2-digit',minute:'2-digit'});
                    tr.innerHTML = `<td style="white-space:nowrap">${date}</td>
                      <td><span class="source-badge">${esc(item.source)}</span></td>
                      <td>${item.url ? `<a href="${esc(item.url)}" target="_blank" rel="noopener">${esc(item.title)}</a>` : esc(item.title)}</td>`;
                    tbody.appendChild(tr);
                  });
                  document.getElementById('loading').style.display = 'none';
                  document.getElementById('table').style.display = data.items.length ? 'table' : 'none';
                  renderPagination();
                });
              }

              function renderPagination() {
                const div = document.getElementById('pagination');
                div.innerHTML = '';
                if(totalResults <= PAGE_SIZE) return;
                const pages = Math.ceil(totalResults / PAGE_SIZE);
                const current = Math.floor(currentOffset / PAGE_SIZE);
                const prev = document.createElement('button');
                prev.textContent = '← Prec';
                prev.disabled = current === 0;
                prev.onclick = () => search((current-1)*PAGE_SIZE);
                div.appendChild(prev);
                const span = document.createElement('span');
                span.style.color = '#8b949e';
                span.style.padding = '6px';
                span.textContent = `${current+1} / ${pages}`;
                div.appendChild(span);
                const next = document.createElement('button');
                next.textContent = 'Succ →';
                next.disabled = current >= pages-1;
                next.onclick = () => search((current+1)*PAGE_SIZE);
                div.appendChild(next);
              }

              function esc(s) { const d=document.createElement('div'); d.textContent=s; return d.innerHTML; }

              // Auto-search on load
              search(0);
            </script>
            </body>
            </html>
            """;
    }

    // ================================================================
    //  Analytics Page (Trend e analisi)
    // ================================================================

    private static string BuildAnalyticsPage()
    {
        return """
            <!DOCTYPE html>
            <html lang="it">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Analytics - Financial News Scraper</title>
            <script src="https://cdn.jsdelivr.net/npm/chart.js@4"></script>
            <style>
              * { margin: 0; padding: 0; box-sizing: border-box; }
              body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                     background: #0d1117; color: #c9d1d9; line-height: 1.6; }
              header { background: linear-gradient(135deg, #161b22, #1a2332);
                       border-bottom: 1px solid #30363d; padding: 20px 30px;
                       display: flex; justify-content: space-between; align-items: center;
                       position: sticky; top: 0; z-index: 100; flex-wrap: wrap; gap: 10px; }
              header h1 { font-size: 1.4em; color: #58a6ff; }
              nav { display: flex; gap: 12px; }
              nav a { color: #8b949e; text-decoration: none; font-size: 0.9em; padding: 4px 12px;
                      border-radius: 6px; transition: all 0.2s; }
              nav a:hover, nav a.active { color: #58a6ff; background: #1f6feb22; }
              .container { max-width: 1400px; margin: 0 auto; padding: 20px; }
              .stats-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
                            gap: 16px; margin-bottom: 24px; }
              .stat-card { background: #161b22; border: 1px solid #30363d; border-radius: 8px;
                           padding: 16px; text-align: center; }
              .stat-card .value { font-size: 1.8em; color: #58a6ff; font-weight: bold; }
              .stat-card .value.green { color: #3fb950; }
              .stat-card .value.red { color: #da3633; }
              .stat-card .label { font-size: 0.8em; color: #8b949e; margin-top: 4px; }
              .charts-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 20px; margin-bottom: 24px; }
              .chart-card { background: #161b22; border: 1px solid #30363d; border-radius: 8px; padding: 20px; }
              .chart-card h3 { color: #58a6ff; margin-bottom: 12px; font-size: 1em; }
              .controls { background: #161b22; border: 1px solid #30363d; border-radius: 8px;
                          padding: 16px; margin-bottom: 20px; display: flex; gap: 12px;
                          align-items: end; flex-wrap: wrap; }
              .field { display: flex; flex-direction: column; gap: 4px; }
              .field label { font-size: 0.8em; color: #8b949e; }
              .field input, .field select { background: #0d1117; border: 1px solid #30363d;
                color: #c9d1d9; padding: 8px 12px; border-radius: 6px; font-size: 0.9em; }
              .field input:focus, .field select:focus { border-color: #58a6ff; outline: none; }
              .btn { background: #1f6feb; color: #fff; border: none; padding: 8px 20px;
                     border-radius: 6px; cursor: pointer; font-size: 0.9em; transition: background 0.2s; }
              .btn:hover { background: #388bfd; }
              .btn.secondary { background: #21262d; border: 1px solid #30363d; }
              .btn.secondary:hover { border-color: #58a6ff; }
              .full-width { grid-column: 1 / -1; }
              .section-title { color: #58a6ff; font-size: 1.1em; margin: 24px 0 12px; padding-bottom: 8px;
                               border-bottom: 1px solid #21262d; }
              .velocity-table { width: 100%; border-collapse: collapse; margin-top: 8px; }
              .velocity-table th { text-align: left; padding: 8px; border-bottom: 2px solid #30363d;
                                   color: #58a6ff; font-size: 0.8em; }
              .velocity-table td { padding: 8px; border-bottom: 1px solid #21262d; font-size: 0.85em; }
              .velocity-table tr:hover { background: #1c2333; }
              .up { color: #3fb950; } .down { color: #da3633; } .flat { color: #8b949e; }
              .cooc-list { list-style: none; padding: 0; }
              .cooc-item { display: flex; justify-content: space-between; padding: 6px 0;
                           border-bottom: 1px solid #21262d; font-size: 0.85em; }
              .cooc-pair { color: #c9d1d9; }
              .cooc-count { color: #58a6ff; font-weight: bold; }
              .cooc-bar { background: #1f6feb33; height: 4px; border-radius: 2px; margin-top: 4px; }
              .export-bar { display: flex; gap: 12px; justify-content: flex-end; margin-bottom: 16px; }
              @media (max-width: 800px) { .charts-grid { grid-template-columns: 1fr; } }
            </style>
            </head>
            <body>
            <header>
              <div>
                <h1>&#128200; Financial News Scraper</h1>
                <nav>
                  <a href="/">&#127968; Live</a>
                  <a href="/archive">&#128269; Archivio</a>
                  <a href="/analytics" class="active">&#128202; Analytics</a>
                  <a href="/ai">&#129302; AI Insights</a>
                </nav>
              </div>
            </header>
            <div class="container">

              <!-- Export bar -->
              <div class="export-bar">
                <div style="display:flex; align-items:center; gap:8px; margin-right:auto;">
                  <label style="font-size:0.85em; color:#8b949e; font-weight:600;">&#128197; Periodo globale:</label>
                  <select id="global-days" onchange="reloadAll()" style="background:#0d1117; border:1px solid #30363d; color:#c9d1d9; padding:6px 10px; border-radius:6px; font-size:0.85em;">
                    <option value="7">7 giorni</option>
                    <option value="14">14 giorni</option>
                    <option value="30" selected>30 giorni</option>
                    <option value="60">60 giorni</option>
                    <option value="90">90 giorni</option>
                    <option value="180">6 mesi</option>
                    <option value="365">1 anno</option>
                  </select>
                </div>
                <button class="btn secondary" onclick="window.open('/api/export','_blank')">&#128229; Esporta CSV (tutte le news)</button>
              </div>

              <!-- Stats -->
              <div class="stats-grid" id="stats-grid"></div>

              <!-- ========== TREND SECTION ========== -->
              <h2 class="section-title">&#128200; Trend &amp; Confronto Keywords</h2>

              <!-- Single keyword trend -->
              <div class="controls">
                <div class="field" style="flex:1">
                  <label>Keyword singola</label>
                  <input type="text" id="trend-kw" placeholder="es. bitcoin, oil, borsa...">
                </div>
                <div class="field">
                  <label>Periodo</label>
                  <select id="trend-days">
                    <option value="7">7 giorni</option>
                    <option value="14">14 giorni</option>
                    <option value="30" selected>30 giorni</option>
                    <option value="60">60 giorni</option>
                    <option value="90">90 giorni</option>
                    <option value="180">6 mesi</option>
                    <option value="365">1 anno</option>
                  </select>
                </div>
                <button class="btn" onclick="loadTrend()">Aggiorna</button>
              </div>

              <div class="charts-grid">
                <div class="chart-card full-width">
                  <h3>&#128200; Trend Giornaliero</h3>
                  <canvas id="trendChart" height="70"></canvas>
                </div>
              </div>

              <!-- Multi-keyword comparison -->
              <div class="controls">
                <div class="field" style="flex:2">
                  <label>Confronta keywords (separate da virgola)</label>
                  <input type="text" id="multi-kw" placeholder="es. bitcoin, gold, oil, euro">
                </div>
                <div class="field">
                  <label>Periodo</label>
                  <select id="multi-days">
                    <option value="7">7 giorni</option>
                    <option value="14">14 giorni</option>
                    <option value="30" selected>30 giorni</option>
                    <option value="60">60 giorni</option>
                    <option value="90">90 giorni</option>
                    <option value="180">6 mesi</option>
                    <option value="365">1 anno</option>
                  </select>
                </div>
                <button class="btn" onclick="loadMultiTrend()">Confronta</button>
              </div>

              <div class="charts-grid">
                <div class="chart-card full-width">
                  <h3>&#128202; Confronto Multi-Keyword</h3>
                  <canvas id="multiTrendChart" height="70"></canvas>
                </div>
              </div>

              <!-- ========== SENTIMENT SECTION ========== -->
              <h2 class="section-title">&#128161; Analisi Sentiment</h2>
              <div class="charts-grid">
                <div class="chart-card">
                  <h3 id="sentTitle">&#127919; Sentiment Attuale (30 giorni)</h3>
                  <canvas id="sentimentChart" height="200"></canvas>
                </div>
                <div class="chart-card">
                  <h3 id="sentTrendTitle">&#128200; Sentiment Trend (30 giorni)</h3>
                  <canvas id="sentimentTrendChart" height="200"></canvas>
                </div>
              </div>

              <!-- ========== DISTRIBUTION SECTION ========== -->
              <h2 class="section-title">&#128337; Distribuzione Temporale</h2>
              <div class="charts-grid">
                <div class="chart-card">
                  <h3 id="hourlyTitle">&#9200; Distribuzione per Ora (30 giorni)</h3>
                  <canvas id="hourlyChart" height="200"></canvas>
                </div>
                <div class="chart-card">
                  <h3 id="weekdayTitle">&#128197; Distribuzione per Giorno (30 giorni)</h3>
                  <canvas id="weekdayChart" height="200"></canvas>
                </div>
              </div>

              <!-- ========== KEYWORDS SECTION ========== -->
              <h2 class="section-title">&#127991; Keywords &amp; Topics</h2>
              <div class="charts-grid">
                <div class="chart-card">
                  <h3 id="kwTitle">&#128293; Top Keywords (30 giorni)</h3>
                  <canvas id="keywordsChart" height="250"></canvas>
                </div>
                <div class="chart-card">
                  <h3>&#128203; Distribuzione per Categoria</h3>
                  <canvas id="categoryChart" height="250"></canvas>
                </div>
              </div>

              <!-- ========== VELOCITY & CO-OCCURRENCE ========== -->
              <h2 class="section-title">&#128640; Keyword Velocity &amp; Correlazioni</h2>
              <div class="charts-grid">
                <div class="chart-card">
                  <h3>&#128640; Keyword Velocity (settimana vs precedente)</h3>
                  <div style="max-height:400px;overflow-y:auto">
                    <table class="velocity-table" id="velocity-table">
                      <thead><tr><th>Keyword</th><th>Questa sett.</th><th>Sett. prec.</th><th>Variazione</th></tr></thead>
                      <tbody></tbody>
                    </table>
                  </div>
                </div>
                <div class="chart-card">
                  <h3 id="coocTitle">&#128279; Co-occorrenze Keywords (30 giorni)</h3>
                  <div style="max-height:400px;overflow-y:auto">
                    <ul class="cooc-list" id="cooc-list"></ul>
                  </div>
                </div>
              </div>

              <!-- ========== SOURCES SECTION ========== -->
              <h2 class="section-title">&#128218; Sorgenti</h2>
              <div class="charts-grid">
                <div class="chart-card full-width">
                  <h3 id="sourcesTitle">&#128218; Top Sorgenti (30 giorni)</h3>
                  <canvas id="sourcesChart" height="60"></canvas>
                </div>
              </div>

            </div>
            <script>
              Chart.defaults.color = '#8b949e';
              Chart.defaults.borderColor = '#21262d';
              const COLORS = ['#58a6ff','#3fb950','#da3633','#a371f7','#f0883e','#56d4dd','#db61a2','#e3b341','#79c0ff','#7ee787'];

              let trendChart, multiTrendChart, sentimentChart, sentimentTrendChart,
                  hourlyChart, weekdayChart, keywordsChart, categoryChart, sourcesChart;

              function gDays() { return document.getElementById('global-days').value; }

              // Update all section titles with the selected period
              function updateTitles(d) {
                document.getElementById('sentTitle').innerHTML = `&#127919; Sentiment Attuale (${d} giorni)`;
                document.getElementById('sentTrendTitle').innerHTML = `&#128200; Sentiment Trend (${d} giorni)`;
                document.getElementById('hourlyTitle').innerHTML = `&#9200; Distribuzione per Ora (${d} giorni)`;
                document.getElementById('weekdayTitle').innerHTML = `&#128197; Distribuzione per Giorno (${d} giorni)`;
                document.getElementById('kwTitle').innerHTML = `&#128293; Top Keywords (${d} giorni)`;
                document.getElementById('coocTitle').innerHTML = `&#128279; Co-occorrenze Keywords (${d} giorni)`;
                document.getElementById('sourcesTitle').innerHTML = `&#128218; Top Sorgenti (${d} giorni)`;
              }

              // ===== STATS =====
              function loadStats() {
                const days = gDays();
                Promise.all([fetch('/api/stats').then(r=>r.json()), fetch('/api/sentiment?days=' + days).then(r=>r.json())])
                .then(([stats, sent]) => {
                const grid = document.getElementById('stats-grid');
                const total = sent.positive + sent.negative + sent.neutral;
                const sentPct = total > 0 ? Math.round((sent.positive / total) * 100) : 0;
                const cards = [
                  { value: stats.total.toLocaleString(), label: 'News totali nel DB', cls: '' },
                  { value: stats.firstDate ? new Date(stats.firstDate).toLocaleDateString('it-IT') : '-', label: 'Prima news', cls: '' },
                  { value: stats.lastDate ? new Date(stats.lastDate).toLocaleDateString('it-IT') : '-', label: 'Ultima news', cls: '' },
                  { value: stats.sources?.length || 0, label: 'Sorgenti attive', cls: '' },
                  { value: sent.positive, label: `Positive (${days}gg)`, cls: 'green' },
                  { value: sent.negative, label: `Negative (${days}gg)`, cls: 'red' },
                  { value: sentPct + '%', label: `Sentiment Index (${days}gg)`, cls: sentPct >= 50 ? 'green' : 'red' },
                ];
                cards.forEach(c => {
                  grid.innerHTML += `<div class="stat-card"><div class="value ${c.cls}">${c.value}</div><div class="label">${c.label}</div></div>`;
                });
              });
              }

              // ===== SINGLE TREND =====
              function loadTrend() {
                const kw = document.getElementById('trend-kw').value;
                const days = document.getElementById('trend-days').value;
                const params = new URLSearchParams();
                if(kw) params.set('keyword', kw);
                params.set('days', days);
                fetch('/api/trend?' + params).then(r=>r.json()).then(data => {
                  const labels = data.map(d => new Date(d.date).toLocaleDateString('it-IT', {day:'2-digit',month:'2-digit'}));
                  const values = data.map(d => d.count);
                  if(trendChart) trendChart.destroy();
                  trendChart = new Chart(document.getElementById('trendChart'), {
                    type: 'line',
                    data: { labels, datasets: [{ label: kw || 'Tutte le news', data: values,
                      borderColor: '#58a6ff', backgroundColor: '#58a6ff22', fill: true, tension: 0.3, pointRadius: 3 }] },
                    options: { responsive: true, plugins: { legend: { display: true } },
                      scales: { y: { beginAtZero: true } } }
                  });
                });
              }

              // ===== MULTI-KEYWORD TREND =====
              function loadMultiTrend() {
                const kws = document.getElementById('multi-kw').value;
                const days = document.getElementById('multi-days').value;
                if(!kws.trim()) return;
                fetch(`/api/multi-trend?keywords=${encodeURIComponent(kws)}&days=${days}`).then(r=>r.json()).then(data => {
                  // Collect all unique dates
                  const allDates = new Set();
                  Object.values(data).forEach(points => points.forEach(p => allDates.add(p.date.split('T')[0])));
                  const labels = [...allDates].sort().map(d => {
                    const dt = new Date(d); return dt.toLocaleDateString('it-IT', {day:'2-digit',month:'2-digit'});
                  });
                  const sortedDates = [...allDates].sort();
                  const datasets = Object.entries(data).map(([kw, points], i) => {
                    const map = {};
                    points.forEach(p => map[p.date.split('T')[0]] = p.count);
                    return {
                      label: kw, data: sortedDates.map(d => map[d] || 0),
                      borderColor: COLORS[i % COLORS.length], backgroundColor: COLORS[i % COLORS.length] + '22',
                      tension: 0.3, pointRadius: 3, fill: false
                    };
                  });
                  if(multiTrendChart) multiTrendChart.destroy();
                  multiTrendChart = new Chart(document.getElementById('multiTrendChart'), {
                    type: 'line', data: { labels, datasets },
                    options: { responsive: true, interaction: { mode: 'index', intersect: false },
                      plugins: { legend: { display: true, position: 'top' } },
                      scales: { y: { beginAtZero: true } } }
                  });
                });
              }

              // ===== SENTIMENT =====
              function loadSentiment() {
              const days = gDays();
              fetch('/api/sentiment?days=' + days).then(r=>r.json()).then(data => {
                if(sentimentChart) sentimentChart.destroy();
                sentimentChart = new Chart(document.getElementById('sentimentChart'), {
                  type: 'doughnut',
                  data: { labels: ['Positive', 'Negative', 'Neutral'],
                    datasets: [{ data: [data.positive, data.negative, data.neutral],
                      backgroundColor: ['#3fb950', '#da3633', '#8b949e'] }] },
                  options: { responsive: true, plugins: { legend: { position: 'bottom' } } }
                });
              });
              }

              function loadSentimentTrendAnalytics() {
              const days = gDays();
              fetch('/api/sentiment-trend?days=' + days).then(r=>r.json()).then(data => {
                const labels = data.map(d => new Date(d.date).toLocaleDateString('it-IT', {day:'2-digit',month:'2-digit'}));
                if(sentimentTrendChart) sentimentTrendChart.destroy();
                sentimentTrendChart = new Chart(document.getElementById('sentimentTrendChart'), {
                  type: 'bar',
                  data: { labels, datasets: [
                    { label: 'Positive', data: data.map(d=>d.positive), backgroundColor: '#3fb95088', stack: 'a' },
                    { label: 'Negative', data: data.map(d=>d.negative), backgroundColor: '#da363388', stack: 'a' },
                    { label: 'Neutral', data: data.map(d=>d.neutral), backgroundColor: '#8b949e44', stack: 'a' }
                  ]},
                  options: { responsive: true, plugins: { legend: { position: 'top' } },
                    scales: { x: { stacked: true }, y: { stacked: true, beginAtZero: true } } }
                });
              });
              }

              // ===== HOURLY / WEEKDAY =====
              function loadHourly() {
              const days = gDays();
              fetch('/api/hourly?days=' + days).then(r=>r.json()).then(data => {
                if(hourlyChart) hourlyChart.destroy();
                hourlyChart = new Chart(document.getElementById('hourlyChart'), {
                  type: 'bar',
                  data: { labels: data.map(d => d.hour + ':00'),
                    datasets: [{ label: 'News', data: data.map(d=>d.count),
                      backgroundColor: data.map(d => d.count > 0 ? '#58a6ff88' : '#21262d'),
                      borderColor: '#58a6ff', borderWidth: 1 }] },
                  options: { responsive: true, plugins: { legend: { display: false } },
                    scales: { y: { beginAtZero: true } } }
                });
              });
              }

              function loadWeekday() {
              const days = gDays();
              fetch('/api/weekday?days=' + days).then(r=>r.json()).then(data => {
                const dayColors = ['#da3633','#58a6ff','#58a6ff','#58a6ff','#58a6ff','#58a6ff','#f0883e'];
                if(weekdayChart) weekdayChart.destroy();
                weekdayChart = new Chart(document.getElementById('weekdayChart'), {
                  type: 'bar',
                  data: { labels: data.map(d => d.dayName),
                    datasets: [{ label: 'News', data: data.map(d=>d.count),
                      backgroundColor: data.map((_,i) => dayColors[i] + '88'),
                      borderColor: data.map((_,i) => dayColors[i]), borderWidth: 1 }] },
                  options: { responsive: true, plugins: { legend: { display: false } },
                    scales: { y: { beginAtZero: true } } }
                });
              });
              }

              // ===== KEYWORDS =====
              function loadKeywords() {
              const days = gDays();
              fetch('/api/top-keywords?days=' + days + '&top=15').then(r=>r.json()).then(data => {
                if(keywordsChart) keywordsChart.destroy();
                keywordsChart = new Chart(document.getElementById('keywordsChart'), {
                  type: 'bar',
                  data: { labels: data.map(d => d.keyword),
                    datasets: [{ label: 'Frequenza', data: data.map(d=>d.count),
                      backgroundColor: '#1f6feb', borderColor: '#388bfd', borderWidth: 1 }] },
                  options: { indexAxis: 'y', responsive: true,
                    plugins: { legend: { display: false } },
                    scales: { x: { beginAtZero: true } } }
                });
              });
              }

              // ===== CATEGORY =====
              function loadCategory() {
              fetch('/api/stats').then(r=>r.json()).then(data => {
                if(!data.categories?.length) return;
                const colors = { BROWSER: '#3fb950', HTML: '#a371f7', RSS: '#da3633' };
                if(categoryChart) categoryChart.destroy();
                categoryChart = new Chart(document.getElementById('categoryChart'), {
                  type: 'doughnut',
                  data: { labels: data.categories.map(c => c.category),
                    datasets: [{ data: data.categories.map(c => c.count),
                      backgroundColor: data.categories.map(c => colors[c.category] || '#1f6feb') }] },
                  options: { responsive: true, plugins: { legend: { position: 'bottom' } } }
                });
              });
              }

              // ===== VELOCITY =====
              function loadVelocity() {
              fetch('/api/velocity').then(r=>r.json()).then(data => {
                const tbody = document.querySelector('#velocity-table tbody');
                data.forEach(v => {
                  const cls = v.changePercent > 0 ? 'up' : v.changePercent < 0 ? 'down' : 'flat';
                  const arrow = v.changePercent > 0 ? '&#9650;' : v.changePercent < 0 ? '&#9660;' : '&#9644;';
                  const tr = document.createElement('tr');
                  tr.innerHTML = `<td><strong>${esc(v.keyword)}</strong></td>
                    <td>${v.currentWeek}</td><td>${v.previousWeek}</td>
                    <td class="${cls}">${arrow} ${v.changePercent > 0 ? '+' : ''}${v.changePercent}%</td>`;
                  tbody.appendChild(tr);
                });
              });
              }

              // ===== CO-OCCURRENCE =====
              function loadCoOccurrence() {
              const days = gDays();
              fetch('/api/cooccurrence?days=' + days + '&top=15').then(r=>r.json()).then(data => {
                const list = document.getElementById('cooc-list');
                const maxCount = data.length > 0 ? data[0].count : 1;
                data.forEach(co => {
                  const pct = Math.round((co.count / maxCount) * 100);
                  const li = document.createElement('li');
                  li.className = 'cooc-item';
                  li.innerHTML = `<div style="flex:1">
                    <span class="cooc-pair">${esc(co.keyword1)} + ${esc(co.keyword2)}</span>
                    <div class="cooc-bar" style="width:${pct}%"></div>
                  </div><span class="cooc-count">${co.count}</span>`;
                  list.appendChild(li);
                });
              });
              }

              // ===== SOURCES =====
              function loadSources() {
              fetch('/api/stats').then(r=>r.json()).then(data => {
                if(!data.sources?.length) return;
                const top = data.sources.slice(0, 20);
                if(sourcesChart) sourcesChart.destroy();
                sourcesChart = new Chart(document.getElementById('sourcesChart'), {
                  type: 'bar',
                  data: { labels: top.map(s => s.source),
                    datasets: [{ label: 'News', data: top.map(s => s.count),
                      backgroundColor: '#58a6ff88', borderColor: '#58a6ff', borderWidth: 1 }] },
                  options: { responsive: true, plugins: { legend: { display: false } },
                    scales: { y: { beginAtZero: true } } }
                });
              });
              }

              function esc(s) { const d=document.createElement('div'); d.textContent=s; return d.innerHTML; }

              // Reload everything when period changes
              function reloadAll() {
                const d = gDays();
                updateTitles(d);
                // Clear dynamic elements
                document.getElementById('stats-grid').innerHTML = '';
                document.querySelector('#velocity-table tbody').innerHTML = '';
                document.getElementById('cooc-list').innerHTML = '';
                // Destroy existing charts
                [sentimentChart, sentimentTrendChart, hourlyChart, weekdayChart, keywordsChart, categoryChart, sourcesChart].forEach(c => { if(c) c.destroy(); });
                sentimentChart = sentimentTrendChart = hourlyChart = weekdayChart = keywordsChart = categoryChart = sourcesChart = null;
                // Reload all sections
                loadStats();
                loadSentiment();
                loadSentimentTrendAnalytics();
                loadHourly();
                loadWeekday();
                loadKeywords();
                loadCategory();
                loadVelocity();
                loadCoOccurrence();
                loadSources();
              }

              // Auto load
              reloadAll();
              loadTrend();
            </script>
            </body>
            </html>
            """;
    }

    // ================================================================
    //  AI Insights Page
    // ================================================================

    private static string BuildAiInsightsPage()
    {
        return """
            <!DOCTYPE html>
            <html lang="it">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>AI Insights - Financial News Scraper</title>
            <script src="https://cdn.jsdelivr.net/npm/chart.js@4"></script>
            <style>
              * { margin: 0; padding: 0; box-sizing: border-box; }
              body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
                     background: #0d1117; color: #c9d1d9; line-height: 1.6; }
              header { background: linear-gradient(135deg, #161b22, #1a2332);
                       border-bottom: 1px solid #30363d; padding: 20px 30px;
                       display: flex; justify-content: space-between; align-items: center;
                       position: sticky; top: 0; z-index: 100; flex-wrap: wrap; gap: 10px; }
              header h1 { font-size: 1.4rem; color: #58a6ff; }
              nav { display: flex; gap: 8px; margin-top: 8px; }
              nav a { color: #8b949e; text-decoration: none; padding: 6px 14px; border-radius: 6px;
                      font-size: 0.85rem; transition: all 0.2s ease; }
              nav a:hover { background: #21262d; color: #c9d1d9; }
              nav a.active { background: #58a6ff22; color: #58a6ff; font-weight: 600; }
              .container { max-width: 1400px; margin: 0 auto; padding: 20px; }
              .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 20px; margin-bottom: 20px; }
              .card { background: #161b22; border: 1px solid #30363d; border-radius: 12px; padding: 20px; }
              .card h3 { color: #58a6ff; margin-bottom: 12px; font-size: 1rem; }
              .card-full { grid-column: 1 / -1; }

              /* AI Status banner */
              .ai-status { padding: 16px 20px; border-radius: 10px; margin-bottom: 20px;
                           display: flex; align-items: center; gap: 12px; font-size: 0.95rem; }
              .ai-status.online { background: #0d331f; border: 1px solid #238636; }
              .ai-status.offline { background: #3d1419; border: 1px solid #da3633; }
              .ai-status .dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
              .ai-status.online .dot { background: #3fb950; box-shadow: 0 0 8px #3fb95088; }
              .ai-status.offline .dot { background: #da3633; box-shadow: 0 0 8px #da363388; }

              /* Stats cards */
              .stat-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; margin-bottom: 20px; }
              .stat-card { background: #161b22; border: 1px solid #30363d; border-radius: 10px;
                           padding: 16px; text-align: center; }
              .stat-card .value { font-size: 1.8rem; font-weight: 700; }
              .stat-card .label { font-size: 0.75rem; color: #8b949e; margin-top: 4px; }
              .positive { color: #3fb950; }
              .negative { color: #da3633; }
              .neutral { color: #8b949e; }
              .bullish { color: #3fb950; }
              .bearish { color: #f85149; }

              /* Briefing */
              .briefing { line-height: 1.8; }
              .briefing .section { margin-bottom: 16px; }
              .briefing .section-title { color: #58a6ff; font-weight: 600; font-size: 0.9rem; margin-bottom: 6px; }
              .briefing ul { list-style: none; padding-left: 0; }
              .briefing li { padding: 3px 0; }
              .briefing li::before { content: "▸ "; color: #58a6ff; }

              /* News list */
              .ai-news { max-height: 500px; overflow-y: auto; }
              .ai-news-item { border-bottom: 1px solid #21262d; padding: 12px 0; }
              .ai-news-item:last-child { border-bottom: none; }
              .ai-news-title { font-weight: 600; margin-bottom: 4px; }
              .ai-news-title a { color: #c9d1d9; text-decoration: none; }
              .ai-news-title a:hover { color: #58a6ff; }
              .ai-news-meta { font-size: 0.8rem; color: #8b949e; display: flex; gap: 10px; flex-wrap: wrap; }
              .tag { display: inline-block; padding: 2px 8px; border-radius: 12px; font-size: 0.7rem; font-weight: 600; }
              .tag-positive { background: #238636; color: #fff; }
              .tag-negative { background: #da3633; color: #fff; }
              .tag-neutral { background: #30363d; color: #8b949e; }
              .tag-bullish { background: #0d331f; color: #3fb950; border: 1px solid #238636; }
              .tag-bearish { background: #3d1419; color: #f85149; border: 1px solid #da3633; }
              .tag-entity { background: #1f2937; color: #a5b4fc; border: 1px solid #4f46e5; }
              .tag-topic { background: #1a252f; color: #7dd3fc; border: 1px solid #0284c7; }

              .btn { padding: 10px 20px; border: none; border-radius: 8px; cursor: pointer;
                     font-size: 0.9rem; font-weight: 600; transition: all 0.2s; }
              .btn-primary { background: #238636; color: #fff; }
              .btn-primary:hover { background: #2ea043; }
              .btn-primary:disabled { opacity: 0.5; cursor: not-allowed; }
              .btn-secondary { background: #21262d; color: #c9d1d9; border: 1px solid #30363d; }
              .btn-secondary:hover { background: #30363d; }
              .actions { display: flex; gap: 10px; margin-bottom: 20px; flex-wrap: wrap; }

              .loading { text-align: center; color: #8b949e; padding: 30px; }
              .spinner { display: inline-block; width: 24px; height: 24px; border: 3px solid #30363d;
                         border-top-color: #58a6ff; border-radius: 50%; animation: spin 0.8s linear infinite; }
              @keyframes spin { to { transform: rotate(360deg); } }
              .progress-bar { height: 6px; background: #21262d; border-radius: 3px; margin-top: 8px; overflow: hidden; }
              .progress-fill { height: 100%; background: linear-gradient(90deg, #238636, #3fb950); border-radius: 3px; transition: width 0.3s; }

              canvas { max-height: 280px; }
            </style>
            </head>
            <body>
            <header>
              <div>
                <h1>&#128200; Financial News Scraper</h1>
                <nav>
                  <a href="/">&#127968; Live</a>
                  <a href="/archive">&#128269; Archivio</a>
                  <a href="/analytics">&#128202; Analytics</a>
                  <a href="/ai" class="active">&#129302; AI Insights</a>
                </nav>
              </div>
            </header>
            <div class="container">

              <!-- AI Status -->
              <div id="aiStatus" class="ai-status offline">
                <span class="dot"></span>
                <span id="aiStatusText">Verifica connessione AI...</span>
              </div>

              <!-- Actions -->
              <div class="actions">
                <button class="btn btn-primary" id="btnAnalyze" onclick="analyzeNews()" disabled>
                  &#129302; Analizza News (AI)
                </button>
                <button class="btn btn-primary" id="btnBriefing" onclick="generateBriefing()" disabled>
                  &#128220; Genera Briefing Giornaliero
                </button>
                <button class="btn btn-secondary" onclick="loadAll()">
                  &#128260; Aggiorna Dashboard
                </button>
                <div style="margin-left:auto; display:flex; align-items:center; gap:8px;">
                  <label style="font-size:0.8rem; color:#8b949e;">Periodo:</label>
                  <select id="ai-days" onchange="loadAll()" style="background:#0d1117; border:1px solid #30363d; color:#c9d1d9; padding:6px 10px; border-radius:6px; font-size:0.85rem;">
                    <option value="7">7 giorni</option>
                    <option value="14">14 giorni</option>
                    <option value="30" selected>30 giorni</option>
                    <option value="60">60 giorni</option>
                    <option value="90">90 giorni</option>
                    <option value="180">6 mesi</option>
                    <option value="365">1 anno</option>
                  </select>
                </div>
              </div>

              <!-- Coverage -->
              <div id="coverageCard" class="card" style="margin-bottom:20px;">
                <h3>&#127919; Copertura AI</h3>
                <div style="display:flex; align-items:center; gap:16px;">
                  <span id="coverageText" style="font-size:0.9rem; color:#8b949e;">Caricamento...</span>
                </div>
                <div class="progress-bar"><div id="coverageFill" class="progress-fill" style="width:0%"></div></div>
              </div>

              <!-- Stats Row -->
              <div class="stat-grid" id="statsRow">
                <div class="stat-card"><div class="value positive" id="statPos">-</div><div class="label" id="lblPos">Positive (30gg)</div></div>
                <div class="stat-card"><div class="value negative" id="statNeg">-</div><div class="label" id="lblNeg">Negative (30gg)</div></div>
                <div class="stat-card"><div class="value neutral" id="statNeu">-</div><div class="label" id="lblNeu">Neutral (30gg)</div></div>
                <div class="stat-card"><div class="value bullish" id="statBull">-</div><div class="label" id="lblBull">Bullish (30gg)</div></div>
                <div class="stat-card"><div class="value bearish" id="statBear">-</div><div class="label" id="lblBear">Bearish (30gg)</div></div>
              </div>

              <div class="grid">
                <!-- Daily Briefing -->
                <div class="card card-full">
                  <h3>&#128220; Briefing Giornaliero AI</h3>
                  <div id="briefingContent" class="briefing">
                    <div class="loading">Clicca "Genera Briefing Giornaliero" per creare il briefing di oggi</div>
                  </div>
                </div>

                <!-- AI Sentiment Donut -->
                <div class="card">
                  <h3>&#127919; AI Sentiment</h3>
                  <canvas id="aiSentimentChart"></canvas>
                </div>

                <!-- Market Impact -->
                <div class="card">
                  <h3>&#128200; Market Impact AI</h3>
                  <canvas id="marketImpactChart"></canvas>
                </div>

                <!-- AI Sentiment Trend -->
                <div class="card card-full">
                  <h3>&#128202; AI Sentiment Trend (30gg)</h3>
                  <canvas id="aiSentimentTrendChart"></canvas>
                </div>

                <!-- Top Entities -->
                <div class="card">
                  <h3>&#127981; Top Entit&agrave; (AI)</h3>
                  <canvas id="entitiesChart"></canvas>
                </div>

                <!-- Top Topics -->
                <div class="card">
                  <h3>&#128278; Top Topics (AI)</h3>
                  <canvas id="topicsChart"></canvas>
                </div>

                <!-- Recent AI Analyses -->
                <div class="card card-full">
                  <h3>&#128196; Ultime Analisi AI</h3>
                  <div id="recentAnalyses" class="ai-news">
                    <div class="loading">Caricamento...</div>
                  </div>
                </div>
              </div>
            </div>

            <script>
              let aiSentimentChart, marketImpactChart, aiSentimentTrendChart, entitiesChart, topicsChart;

              function esc(s) { const d=document.createElement('div'); d.textContent=s; return d.innerHTML; }
              function getAiDays() { return document.getElementById('ai-days').value; }

              // Check AI status (con retry automatico)
              let _statusRetries = 0;
              async function checkStatus() {
                try {
                  const r = await fetch('/api/ai/status');
                  const data = await r.json();
                  const el = document.getElementById('aiStatus');
                  const txt = document.getElementById('aiStatusText');
                  _statusRetries = 0;
                  if (data.available) {
                    el.className = 'ai-status online';
                    txt.textContent = 'AI Engine connesso e operativo';
                    document.getElementById('btnAnalyze').disabled = false;
                    document.getElementById('btnBriefing').disabled = false;
                  } else {
                    el.className = 'ai-status offline';
                    txt.innerHTML = 'AI non disponibile. Configura <b>Ollama</b> (locale) o <b>OpenAI API</b> per abilitare le funzionalit\u00E0 AI.';
                  }
                } catch {
                  _statusRetries++;
                  if (_statusRetries <= 5) {
                    setTimeout(checkStatus, 3000);
                  } else {
                    const el = document.getElementById('aiStatus');
                    const txt = document.getElementById('aiStatusText');
                    el.className = 'ai-status offline';
                    txt.textContent = 'Impossibile verificare lo stato AI. Ricarica la pagina.';
                  }
                }
              }

              // Analyze news
              async function analyzeNews() {
                const btn = document.getElementById('btnAnalyze');
                btn.disabled = true;
                btn.innerHTML = '<span class="spinner"></span> Analisi in corso...';
                try {
                  const r = await fetch('/api/ai/analyze?count=20');
                  const data = await r.json();
                  if (data.error) { alert(data.error); return; }
                  btn.innerHTML = `&#9989; Analizzate ${data.analyzed} news`;
                  setTimeout(() => { btn.innerHTML = '&#129302; Analizza News (AI)'; btn.disabled = false; }, 3000);
                  loadAll();
                } catch(e) {
                  alert('Errore: ' + e.message);
                  btn.innerHTML = '&#129302; Analizza News (AI)';
                  btn.disabled = false;
                }
              }

              // Generate briefing
              async function generateBriefing() {
                const btn = document.getElementById('btnBriefing');
                btn.disabled = true;
                btn.innerHTML = '<span class="spinner"></span> Generazione...';
                try {
                  const r = await fetch('/api/ai/briefing');
                  const data = await r.json();
                  if (data.error) { alert(data.error); return; }
                  renderBriefing(data);
                  btn.innerHTML = '&#128220; Genera Briefing Giornaliero';
                  btn.disabled = false;
                } catch(e) {
                  alert('Errore: ' + e.message);
                  btn.innerHTML = '&#128220; Genera Briefing Giornaliero';
                  btn.disabled = false;
                }
              }

              function renderBriefing(data) {
                const el = document.getElementById('briefingContent');
                let html = '';
                if (data.cached) html += '<div style="color:#8b949e;font-size:0.8rem;margin-bottom:10px;">&#128337; Briefing gi\u00E0 generato oggi (cache)</div>';
                html += `<div class="section"><div class="section-title">&#128200; Riepilogo</div><p>${esc(data.summary)}</p></div>`;
                if (data.keyThemes?.length) {
                  html += '<div class="section"><div class="section-title">&#128278; Temi Principali</div><ul>';
                  data.keyThemes.forEach(t => html += `<li>${esc(t)}</li>`);
                  html += '</ul></div>';
                }
                html += `<div class="section"><div class="section-title">&#128202; Outlook Mercato</div><p>${esc(data.marketOutlook)}</p></div>`;
                if (data.topMovers?.length) {
                  html += '<div class="section"><div class="section-title">&#128640; Top Movers</div><ul>';
                  data.topMovers.forEach(m => html += `<li>${esc(m)}</li>`);
                  html += '</ul></div>';
                }
                if (data.riskFactors?.length) {
                  html += '<div class="section"><div class="section-title">&#9888;&#65039; Fattori di Rischio</div><ul>';
                  data.riskFactors.forEach(r => html += `<li>${esc(r)}</li>`);
                  html += '</ul></div>';
                }
                el.innerHTML = html;
              }

              // Load coverage
              async function loadCoverage() {
                try {
                  const r = await fetch('/api/ai/coverage');
                  const data = await r.json();
                  document.getElementById('coverageText').textContent =
                    `${data.analyzed} / ${data.total} news analizzate (${data.percentage}%)`;
                  document.getElementById('coverageFill').style.width = data.percentage + '%';
                } catch { }
              }

              // Load sentiment stats
              async function loadSentimentStats() {
                try {
                  const days = getAiDays();
                  const r = await fetch('/api/ai/sentiment-stats?days=' + days);
                  const data = await r.json();
                  document.getElementById('statPos').textContent = data.positive;
                  document.getElementById('statNeg').textContent = data.negative;
                  document.getElementById('statNeu').textContent = data.neutral;
                  document.getElementById('statBull').textContent = data.bullish;
                  document.getElementById('statBear').textContent = data.bearish;

                  // Update labels with selected period
                  document.getElementById('lblPos').textContent = `Positive (${days}gg)`;
                  document.getElementById('lblNeg').textContent = `Negative (${days}gg)`;
                  document.getElementById('lblNeu').textContent = `Neutral (${days}gg)`;
                  document.getElementById('lblBull').textContent = `Bullish (${days}gg)`;
                  document.getElementById('lblBear').textContent = `Bearish (${days}gg)`;

                  // Sentiment donut
                  if (aiSentimentChart) aiSentimentChart.destroy();
                  if (data.positive + data.negative + data.neutral > 0) {
                    aiSentimentChart = new Chart(document.getElementById('aiSentimentChart'), {
                      type: 'doughnut',
                      data: { labels: ['Positive', 'Negative', 'Neutral'],
                        datasets: [{ data: [data.positive, data.negative, data.neutral],
                          backgroundColor: ['#3fb950', '#da3633', '#484f58'] }] },
                      options: { responsive: true, plugins: { legend: { position: 'bottom', labels: { color: '#8b949e' } } } }
                    });
                  }

                  // Market impact donut
                  if (marketImpactChart) marketImpactChart.destroy();
                  if (data.bullish + data.bearish > 0) {
                    const otherMarket = (data.positive + data.negative + data.neutral) - data.bullish - data.bearish;
                    marketImpactChart = new Chart(document.getElementById('marketImpactChart'), {
                      type: 'doughnut',
                      data: { labels: ['Bullish', 'Bearish', 'Neutral/Mixed'],
                        datasets: [{ data: [data.bullish, data.bearish, Math.max(0, otherMarket)],
                          backgroundColor: ['#238636', '#da3633', '#484f58'] }] },
                      options: { responsive: true, plugins: { legend: { position: 'bottom', labels: { color: '#8b949e' } } } }
                    });
                  }
                } catch { }
              }

              // Load sentiment trend
              async function loadSentimentTrend() {
                try {
                  const days = getAiDays();
                  const r = await fetch('/api/ai/sentiment-trend?days=' + days);
                  const data = await r.json();
                  if (!data.length) return;
                  const labels = data.map(d => new Date(d.date).toLocaleDateString('it-IT', {day:'2-digit',month:'2-digit'}));
                  if (aiSentimentTrendChart) aiSentimentTrendChart.destroy();
                  aiSentimentTrendChart = new Chart(document.getElementById('aiSentimentTrendChart'), {
                    type: 'bar',
                    data: { labels, datasets: [
                      { label: 'Positive', data: data.map(d => d.positive), backgroundColor: '#3fb95088', stack: 'a' },
                      { label: 'Negative', data: data.map(d => d.negative), backgroundColor: '#da363388', stack: 'a' },
                      { label: 'Neutral', data: data.map(d => d.neutral), backgroundColor: '#484f5888', stack: 'a' }
                    ] },
                    options: { responsive: true, plugins: { legend: { position: 'top', labels: { color: '#8b949e' } } },
                      scales: { x: { stacked: true, ticks: { color: '#8b949e' } }, y: { stacked: true, ticks: { color: '#8b949e' }, beginAtZero: true } } }
                  });
                } catch { }
              }

              // Load entities
              async function loadEntities() {
                try {
                  const days = getAiDays();
                  const r = await fetch('/api/ai/entities?days=' + days + '&top=15');
                  const data = await r.json();
                  if (!data.length) return;
                  if (entitiesChart) entitiesChart.destroy();
                  entitiesChart = new Chart(document.getElementById('entitiesChart'), {
                    type: 'bar',
                    data: { labels: data.map(e => e.entity),
                      datasets: [{ label: 'Menzioni', data: data.map(e => e.count),
                        backgroundColor: '#a78bfa88', borderColor: '#a78bfa', borderWidth: 1 }] },
                    options: { indexAxis: 'y', responsive: true, plugins: { legend: { display: false } },
                      scales: { x: { beginAtZero: true, ticks: { color: '#8b949e' } }, y: { ticks: { color: '#8b949e', font: { size: 11 } } } } }
                  });
                } catch { }
              }

              // Load topics
              async function loadTopics() {
                try {
                  const days = getAiDays();
                  const r = await fetch('/api/ai/topics?days=' + days);
                  const data = await r.json();
                  if (!data.length) return;
                  if (topicsChart) topicsChart.destroy();
                  const colors = ['#58a6ff','#3fb950','#d29922','#da3633','#a78bfa','#f778ba','#79c0ff','#56d364','#e3b341','#ff7b72','#bc8cff','#ff9bce','#39d353','#6cb6ff','#db6d28'];
                  topicsChart = new Chart(document.getElementById('topicsChart'), {
                    type: 'doughnut',
                    data: { labels: data.map(t => t.topic),
                      datasets: [{ data: data.map(t => t.count),
                        backgroundColor: data.map((_, i) => colors[i % colors.length]) }] },
                    options: { responsive: true, plugins: { legend: { position: 'bottom', labels: { color: '#8b949e', font: { size: 11 } } } } }
                  });
                } catch { }
              }

              // Load recent analyses
              async function loadRecent() {
                try {
                  const r = await fetch('/api/ai/recent?limit=30');
                  const data = await r.json();
                  const el = document.getElementById('recentAnalyses');
                  if (!data.length) { el.innerHTML = '<div class="loading">Nessuna analisi AI disponibile. Clicca "Analizza News" per iniziare.</div>'; return; }
                  el.innerHTML = data.map(item => `
                    <div class="ai-news-item">
                      <div class="ai-news-title"><a href="${esc(item.url)}" target="_blank">${esc(item.title)}</a></div>
                      <div class="ai-news-meta">
                        <span>${esc(item.source)}</span>
                        <span class="tag tag-${item.sentiment}">${item.sentiment} (${item.sentimentScore?.toFixed(2) ?? '?'})</span>
                        <span class="tag tag-${item.marketImpact}">${item.marketImpact}</span>
                        ${(item.entities||[]).slice(0,3).map(e => `<span class="tag tag-entity">${esc(e)}</span>`).join('')}
                        ${(item.topics||[]).map(t => `<span class="tag tag-topic">${esc(t)}</span>`).join('')}
                      </div>
                      ${item.summary ? `<div style="font-size:0.85rem; color:#8b949e; margin-top:6px;">${esc(item.summary)}</div>` : ''}
                    </div>
                  `).join('');
                } catch { }
              }

              // Try loading existing briefing
              async function loadExistingBriefing() {
                try {
                  const r = await fetch('/api/ai/briefing');
                  const data = await r.json();
                  if (!data.error && data.summary) renderBriefing(data);
                } catch { }
              }

              function loadAll() {
                loadCoverage();
                loadSentimentStats();
                loadSentimentTrend();
                loadEntities();
                loadTopics();
                loadRecent();
              }

              checkStatus();
              loadAll();
            </script>
            </body>
            </html>
            """;
    }
}
