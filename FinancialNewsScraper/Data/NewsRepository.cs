using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace FinancialNewsScraper.Data;

public record TrendPoint(DateTime Date, int Count);
public record CategoryBreakdown(string Category, int Count);
public record SourceBreakdown(string Source, int Count);
public record SentimentResult(int Positive, int Negative, int Neutral);
public record SentimentDaily(DateTime Date, int Positive, int Negative, int Neutral);
public record HourlyActivity(int Hour, int Count);
public record WeekdayActivity(int DayOfWeek, string DayName, int Count);
public record KeywordVelocity(string Keyword, int CurrentWeek, int PreviousWeek, double ChangePercent);
public record CoOccurrence(string Keyword1, string Keyword2, int Count);

public class NewsRepository
{
    private readonly string _dbPath;

    private static readonly string[] TrackedKeywords =
    {
        "stock", "market", "nasdaq", "s&p", "dow jones", "ftse",
        "bond", "yield", "treasury", "fed", "ecb", "interest rate",
        "inflation", "cpi", "gdp", "recession", "earnings",
        "oil", "crude", "gold", "bitcoin", "crypto", "forex", "dollar", "euro",
        "etf", "ipo", "merger", "acquisition", "bank", "fintech",
        "borsa", "mercati", "azioni", "spread", "btp", "bund", "tassi",
        "inflazione", "pil", "petrolio", "oro", "valuta"
    };

    private static readonly string[] PositiveWords =
    {
        "rally", "surge", "gain", "rise", "jump", "soar", "boom", "bull", "record high",
        "recovery", "growth", "profit", "upgrade", "beat", "outperform", "optimism",
        "rialzo", "crescita", "ripresa", "guadagn", "record", "positiv", "ottimism"
    };

    private static readonly string[] NegativeWords =
    {
        "crash", "plunge", "drop", "fall", "decline", "slump", "bear", "sell-off", "selloff",
        "loss", "recession", "downgrade", "miss", "fear", "crisis", "risk", "warning",
        "ribasso", "crollo", "calo", "perdita", "crisi", "rischio", "negativ", "timor"
    };

    public NewsRepository(string dbPath)
    {
        _dbPath = dbPath;
        using var db = CreateContext();
        db.Database.EnsureCreated();

        // EnsureCreated() NON aggiunge tabelle a un DB esistente con schema vecchio.
        // Questi CREATE TABLE IF NOT EXISTS gestiscono l'evoluzione dello schema.
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS AiAnalyses (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                NewsItemId INTEGER NOT NULL UNIQUE,
                Sentiment TEXT NOT NULL DEFAULT '',
                SentimentScore REAL NOT NULL DEFAULT 0,
                Summary TEXT NOT NULL DEFAULT '',
                Entities TEXT NOT NULL DEFAULT '',
                Topics TEXT NOT NULL DEFAULT '',
                MarketImpact TEXT NOT NULL DEFAULT '',
                AnalyzedAtUtc TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (NewsItemId) REFERENCES News(Id)
            );
        ");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_AiAnalyses_NewsItemId ON AiAnalyses(NewsItemId);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_AiAnalyses_Sentiment ON AiAnalyses(Sentiment);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_AiAnalyses_MarketImpact ON AiAnalyses(MarketImpact);");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS AiDailyBriefings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Date TEXT NOT NULL UNIQUE DEFAULT '',
                Summary TEXT NOT NULL DEFAULT '',
                KeyThemes TEXT NOT NULL DEFAULT '',
                MarketOutlook TEXT NOT NULL DEFAULT '',
                TopMovers TEXT NOT NULL DEFAULT '',
                RiskFactors TEXT NOT NULL DEFAULT '',
                GeneratedAtUtc TEXT NOT NULL DEFAULT ''
            );
        ");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_AiDailyBriefings_Date ON AiDailyBriefings(Date);");
    }

    private NewsDbContext CreateContext() => new(_dbPath);

    /// <summary>
    /// Salva una lista di news nel database, evitando duplicati tramite TitleNormalized.
    /// </summary>
    public async Task<int> SaveNewsAsync(IEnumerable<(string Section, NewsItem Item)> items)
    {
        using var db = CreateContext();
        var saved = 0;

        foreach (var (section, item) in items)
        {
            var normalized = NormalizeForDedup(item.Title);
            // Controlla se già esiste nella stessa giornata
            var today = DateTime.UtcNow.Date;
            var exists = await db.News.AnyAsync(n =>
                n.TitleNormalized == normalized && n.ScrapedAtUtc >= today);
            if (exists) continue;

            var category = "RSS";
            if (section.Contains("[HTML]")) category = "HTML";
            else if (section.Contains("[BROWSER]")) category = "BROWSER";

            db.News.Add(new StoredNewsItem
            {
                Source = item.Source,
                Category = category,
                Title = item.Title,
                Url = item.Url,
                ScrapedAtUtc = item.ScrapedAt.ToUniversalTime(),
                TitleNormalized = normalized
            });
            saved++;
        }

        await db.SaveChangesAsync();
        return saved;
    }

    /// <summary>
    /// Ricerca news per testo, sorgente e intervallo di date.
    /// </summary>
    public async Task<List<StoredNewsItem>> SearchAsync(
        string? query = null, string? source = null,
        DateTime? from = null, DateTime? to = null,
        int limit = 100, int offset = 0)
    {
        using var db = CreateContext();
        IQueryable<StoredNewsItem> q = db.News.OrderByDescending(n => n.ScrapedAtUtc);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var lower = query.ToLowerInvariant();
            q = q.Where(n => n.Title.ToLower().Contains(lower));
        }
        if (!string.IsNullOrWhiteSpace(source))
            q = q.Where(n => n.Source == source);
        if (from.HasValue)
            q = q.Where(n => n.ScrapedAtUtc >= from.Value.ToUniversalTime());
        if (to.HasValue)
            q = q.Where(n => n.ScrapedAtUtc <= to.Value.ToUniversalTime());

        return await q.Skip(offset).Take(limit).ToListAsync();
    }

    /// <summary>
    /// Conteggio totale per la ricerca corrente.
    /// </summary>
    public async Task<int> CountAsync(
        string? query = null, string? source = null,
        DateTime? from = null, DateTime? to = null)
    {
        using var db = CreateContext();
        IQueryable<StoredNewsItem> q = db.News;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var lower = query.ToLowerInvariant();
            q = q.Where(n => n.Title.ToLower().Contains(lower));
        }
        if (!string.IsNullOrWhiteSpace(source))
            q = q.Where(n => n.Source == source);
        if (from.HasValue)
            q = q.Where(n => n.ScrapedAtUtc >= from.Value.ToUniversalTime());
        if (to.HasValue)
            q = q.Where(n => n.ScrapedAtUtc <= to.Value.ToUniversalTime());

        return await q.CountAsync();
    }

    /// <summary>
    /// Trend giornaliero: quante news per giorno in un intervallo.
    /// </summary>
    public async Task<List<TrendPoint>> GetDailyTrendAsync(
        string? keyword = null, int days = 30)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        IQueryable<StoredNewsItem> q = db.News.Where(n => n.ScrapedAtUtc >= since);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var lower = keyword.ToLowerInvariant();
            q = q.Where(n => n.Title.ToLower().Contains(lower));
        }

        var data = await q.ToListAsync();
        return data
            .GroupBy(n => n.ScrapedAtUtc.Date)
            .Select(g => new TrendPoint(g.Key, g.Count()))
            .OrderBy(t => t.Date)
            .ToList();
    }

    /// <summary>
    /// Top keyword/tema per frequenza in un periodo.
    /// </summary>
    public async Task<List<(string Keyword, int Count)>> GetTopKeywordsAsync(int days = 7, int top = 20)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var titles = await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .Select(n => n.Title)
            .ToListAsync();

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kw in TrackedKeywords)
            result[kw] = 0;

        foreach (var title in titles)
        {
            var lower = title.ToLowerInvariant();
            foreach (var kw in TrackedKeywords)
            {
                if (lower.Contains(kw))
                    result[kw]++;
            }
        }

        return result
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Breakdown per categoria (RSS/HTML/BROWSER).
    /// </summary>
    public async Task<List<CategoryBreakdown>> GetCategoryBreakdownAsync(int days = 30)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        return await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .GroupBy(n => n.Category)
            .Select(g => new CategoryBreakdown(g.Key, g.Count()))
            .ToListAsync();
    }

    /// <summary>
    /// Breakdown per sorgente.
    /// </summary>
    public async Task<List<SourceBreakdown>> GetSourceBreakdownAsync(int days = 30)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        return await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .GroupBy(n => n.Source)
            .Select(g => new SourceBreakdown(g.Key, g.Count()))
            .OrderByDescending(s => s.Count)
            .ToListAsync();
    }

    /// <summary>
    /// Lista di tutte le sorgenti uniche nel database.
    /// </summary>
    public async Task<List<string>> GetAllSourcesAsync()
    {
        using var db = CreateContext();
        return await db.News.Select(n => n.Source).Distinct().OrderBy(s => s).ToListAsync();
    }

    /// <summary>
    /// Statistiche generali.
    /// </summary>
    public async Task<(int Total, DateTime? FirstDate, DateTime? LastDate)> GetStatsAsync()
    {
        using var db = CreateContext();
        var total = await db.News.CountAsync();
        var first = total > 0 ? await db.News.MinAsync(n => n.ScrapedAtUtc) : (DateTime?)null;
        var last = total > 0 ? await db.News.MaxAsync(n => n.ScrapedAtUtc) : (DateTime?)null;
        return (total, first, last);
    }

    // ================================================================
    //  Sentiment Analysis
    // ================================================================

    private static string ClassifySentiment(string title)
    {
        var lower = title.ToLowerInvariant();
        var pos = PositiveWords.Count(w => lower.Contains(w));
        var neg = NegativeWords.Count(w => lower.Contains(w));
        if (pos > neg) return "positive";
        if (neg > pos) return "negative";
        return "neutral";
    }

    public async Task<SentimentResult> GetSentimentAsync(int days = 7)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var titles = await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .Select(n => n.Title)
            .ToListAsync();

        int pos = 0, neg = 0, neu = 0;
        foreach (var t in titles)
        {
            switch (ClassifySentiment(t))
            {
                case "positive": pos++; break;
                case "negative": neg++; break;
                default: neu++; break;
            }
        }
        return new SentimentResult(pos, neg, neu);
    }

    public async Task<List<SentimentDaily>> GetSentimentTrendAsync(int days = 30)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var news = await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .Select(n => new { n.Title, n.ScrapedAtUtc })
            .ToListAsync();

        return news
            .GroupBy(n => n.ScrapedAtUtc.Date)
            .Select(g =>
            {
                int pos = 0, neg = 0, neu = 0;
                foreach (var n in g)
                {
                    switch (ClassifySentiment(n.Title))
                    {
                        case "positive": pos++; break;
                        case "negative": neg++; break;
                        default: neu++; break;
                    }
                }
                return new SentimentDaily(g.Key, pos, neg, neu);
            })
            .OrderBy(s => s.Date)
            .ToList();
    }

    // ================================================================
    //  Hourly / Weekday Distribution
    // ================================================================

    public async Task<List<HourlyActivity>> GetHourlyDistributionAsync(int days = 30)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var news = await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .Select(n => n.ScrapedAtUtc)
            .ToListAsync();

        return Enumerable.Range(0, 24)
            .Select(h => new HourlyActivity(h, news.Count(n => n.Hour == h)))
            .ToList();
    }

    public async Task<List<WeekdayActivity>> GetWeekdayDistributionAsync(int days = 30)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var news = await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .Select(n => n.ScrapedAtUtc)
            .ToListAsync();

        var dayNames = new[] { "Dom", "Lun", "Mar", "Mer", "Gio", "Ven", "Sab" };
        return Enumerable.Range(0, 7)
            .Select(d => new WeekdayActivity(d, dayNames[d],
                news.Count(n => (int)n.DayOfWeek == d)))
            .ToList();
    }

    // ================================================================
    //  Keyword Velocity (crescita/declino)
    // ================================================================

    public async Task<List<KeywordVelocity>> GetKeywordVelocityAsync()
    {
        using var db = CreateContext();
        var now = DateTime.UtcNow;
        var currentWeekStart = now.AddDays(-7);
        var previousWeekStart = now.AddDays(-14);

        var currentTitles = await db.News
            .Where(n => n.ScrapedAtUtc >= currentWeekStart)
            .Select(n => n.Title)
            .ToListAsync();

        var previousTitles = await db.News
            .Where(n => n.ScrapedAtUtc >= previousWeekStart && n.ScrapedAtUtc < currentWeekStart)
            .Select(n => n.Title)
            .ToListAsync();

        var result = new List<KeywordVelocity>();
        foreach (var kw in TrackedKeywords)
        {
            var curr = currentTitles.Count(t => t.Contains(kw, StringComparison.OrdinalIgnoreCase));
            var prev = previousTitles.Count(t => t.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (curr == 0 && prev == 0) continue;
            var change = prev > 0 ? ((double)(curr - prev) / prev) * 100 : (curr > 0 ? 100 : 0);
            result.Add(new KeywordVelocity(kw, curr, prev, Math.Round(change, 1)));
        }

        return result.OrderByDescending(v => Math.Abs(v.ChangePercent)).ToList();
    }

    // ================================================================
    //  Co-occurrence Analysis
    // ================================================================

    public async Task<List<CoOccurrence>> GetCoOccurrencesAsync(int days = 7, int top = 20)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var titles = await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .Select(n => n.Title)
            .ToListAsync();

        var pairs = new Dictionary<string, int>();
        foreach (var title in titles)
        {
            var lower = title.ToLowerInvariant();
            var found = TrackedKeywords.Where(kw => lower.Contains(kw)).Distinct().OrderBy(k => k).ToList();
            for (int i = 0; i < found.Count; i++)
            {
                for (int j = i + 1; j < found.Count; j++)
                {
                    var key = $"{found[i]}|{found[j]}";
                    pairs.TryGetValue(key, out var count);
                    pairs[key] = count + 1;
                }
            }
        }

        return pairs
            .OrderByDescending(p => p.Value)
            .Take(top)
            .Select(p =>
            {
                var parts = p.Key.Split('|');
                return new CoOccurrence(parts[0], parts[1], p.Value);
            })
            .ToList();
    }

    // ================================================================
    //  Multi-keyword Comparison
    // ================================================================

    public async Task<Dictionary<string, List<TrendPoint>>> GetMultiKeywordTrendAsync(
        string[] keywords, int days = 30)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var news = await db.News
            .Where(n => n.ScrapedAtUtc >= since)
            .Select(n => new { n.Title, n.ScrapedAtUtc })
            .ToListAsync();

        var result = new Dictionary<string, List<TrendPoint>>();
        foreach (var kw in keywords)
        {
            var lower = kw.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(lower)) continue;
            result[kw.Trim()] = news
                .Where(n => n.Title.Contains(lower, StringComparison.OrdinalIgnoreCase))
                .GroupBy(n => n.ScrapedAtUtc.Date)
                .Select(g => new TrendPoint(g.Key, g.Count()))
                .OrderBy(t => t.Date)
                .ToList();
        }
        return result;
    }

    // ================================================================
    //  CSV Export
    // ================================================================

    public async Task<string> ExportCsvAsync(
        string? query = null, string? source = null,
        DateTime? from = null, DateTime? to = null,
        int maxRows = 10000)
    {
        var items = await SearchAsync(query, source, from, to, maxRows, 0);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Id,Source,Category,Title,Url,ScrapedAtUtc");
        foreach (var item in items)
        {
            var title = item.Title.Replace("\"", "\"\"");
            var url = item.Url.Replace("\"", "\"\"");
            sb.AppendLine($"{item.Id},\"{item.Source}\",\"{item.Category}\",\"{title}\",\"{url}\",{item.ScrapedAtUtc:yyyy-MM-dd HH:mm:ss}");
        }
        return sb.ToString();
    }

    // ================================================================
    //  AI Analysis persistence
    // ================================================================

    /// <summary>
    /// Salva i risultati AI per una news.
    /// </summary>
    public async Task SaveAiAnalysisAsync(int newsItemId, Ai.AiNewsAnalysis analysis)
    {
        using var db = CreateContext();
        var exists = await db.AiAnalyses.AnyAsync(a => a.NewsItemId == newsItemId);
        if (exists) return;

        db.AiAnalyses.Add(new AiAnalysisResult
        {
            NewsItemId = newsItemId,
            Sentiment = analysis.Sentiment,
            SentimentScore = analysis.SentimentScore,
            Summary = analysis.Summary,
            Entities = System.Text.Json.JsonSerializer.Serialize(analysis.Entities),
            Topics = System.Text.Json.JsonSerializer.Serialize(analysis.Topics),
            MarketImpact = analysis.MarketImpact,
            AnalyzedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Salva il briefing giornaliero AI.
    /// </summary>
    public async Task SaveDailyBriefingAsync(Ai.AiDailyBriefing briefing)
    {
        using var db = CreateContext();
        var exists = await db.AiDailyBriefings.AnyAsync(b => b.Date == briefing.Date);
        if (exists) return;

        db.AiDailyBriefings.Add(new AiDailyBriefingRecord
        {
            Date = briefing.Date,
            Summary = briefing.Summary,
            KeyThemes = System.Text.Json.JsonSerializer.Serialize(briefing.KeyThemes),
            MarketOutlook = briefing.MarketOutlook,
            TopMovers = System.Text.Json.JsonSerializer.Serialize(briefing.TopMovers),
            RiskFactors = System.Text.Json.JsonSerializer.Serialize(briefing.RiskFactors),
            GeneratedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Ottieni i risultati AI sentiment aggregati.
    /// </summary>
    public async Task<(int Positive, int Negative, int Neutral, int Bullish, int Bearish)> GetAiSentimentStatsAsync(int days = 7)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var analyses = await db.AiAnalyses
            .Where(a => a.AnalyzedAtUtc >= since)
            .ToListAsync();

        return (
            analyses.Count(a => a.Sentiment == "positive"),
            analyses.Count(a => a.Sentiment == "negative"),
            analyses.Count(a => a.Sentiment == "neutral"),
            analyses.Count(a => a.MarketImpact == "bullish"),
            analyses.Count(a => a.MarketImpact == "bearish")
        );
    }

    /// <summary>
    /// Top entità estratte dall'AI.
    /// </summary>
    public async Task<List<(string Entity, int Count)>> GetAiTopEntitiesAsync(int days = 7, int top = 20)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var entitiesJson = await db.AiAnalyses
            .Where(a => a.AnalyzedAtUtc >= since)
            .Select(a => a.Entities)
            .ToListAsync();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in entitiesJson)
        {
            try
            {
                var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
                if (arr == null) continue;
                foreach (var entity in arr)
                {
                    var key = entity.Trim();
                    if (key.Length == 0) continue;
                    counts.TryGetValue(key, out var c);
                    counts[key] = c + 1;
                }
            }
            catch { }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Top topics estratti dall'AI.
    /// </summary>
    public async Task<List<(string Topic, int Count)>> GetAiTopTopicsAsync(int days = 7, int top = 15)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var topicsJson = await db.AiAnalyses
            .Where(a => a.AnalyzedAtUtc >= since)
            .Select(a => a.Topics)
            .ToListAsync();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in topicsJson)
        {
            try
            {
                var arr = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
                if (arr == null) continue;
                foreach (var topic in arr)
                {
                    var key = topic.Trim().ToLowerInvariant();
                    if (key.Length == 0) continue;
                    counts.TryGetValue(key, out var c);
                    counts[key] = c + 1;
                }
            }
            catch { }
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Ottieni l'ultimo briefing giornaliero AI.
    /// </summary>
    public async Task<AiDailyBriefingRecord?> GetLatestBriefingAsync()
    {
        using var db = CreateContext();
        return await db.AiDailyBriefings
            .OrderByDescending(b => b.GeneratedAtUtc)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Ottieni le news non ancora analizzate dall'AI.
    /// </summary>
    public async Task<List<StoredNewsItem>> GetUnanalyzedNewsAsync(int limit = 50)
    {
        using var db = CreateContext();
        var analyzedIds = db.AiAnalyses.Select(a => a.NewsItemId);
        return await db.News
            .Where(n => !analyzedIds.Contains(n.Id))
            .OrderByDescending(n => n.ScrapedAtUtc)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Ottieni news non analizzate con sampling bilanciato da tutte le fonti.
    /// Round-robin casuale: pesca da ogni fonte a turno garantendo copertura equa.
    /// </summary>
    public async Task<List<StoredNewsItem>> GetUnanalyzedNewsFairSampledAsync(int limit)
    {
        using var db = CreateContext();
        var analyzedIds = db.AiAnalyses.Select(a => a.NewsItemId);

        var unanalyzed = await db.News
            .Where(n => !analyzedIds.Contains(n.Id))
            .OrderByDescending(n => n.ScrapedAtUtc)
            .ToListAsync();

        if (unanalyzed.Count == 0) return new List<StoredNewsItem>();

        // Raggruppa per fonte
        var bySource = unanalyzed
            .GroupBy(n => n.Source)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<StoredNewsItem>();
        var rng = new Random();

        // Round-robin casuale: ad ogni giro pesca 1 news random da ogni fonte
        while (result.Count < limit && bySource.Count > 0)
        {
            var emptySources = new List<string>();
            // Ordine casuale delle fonti ad ogni giro
            foreach (var source in bySource.Keys.OrderBy(_ => rng.Next()).ToList())
            {
                if (result.Count >= limit) break;
                var items = bySource[source];
                if (items.Count == 0) { emptySources.Add(source); continue; }

                var idx = rng.Next(items.Count);
                result.Add(items[idx]);
                items.RemoveAt(idx);

                if (items.Count == 0) emptySources.Add(source);
            }
            foreach (var s in emptySources) bySource.Remove(s);
        }

        return result;
    }

    /// <summary>
    /// Sentiment AI trend giornaliero.
    /// </summary>
    public async Task<List<SentimentDaily>> GetAiSentimentTrendAsync(int days = 30)
    {
        using var db = CreateContext();
        var since = DateTime.UtcNow.AddDays(-days);
        var analyses = await db.AiAnalyses
            .Include(a => a.NewsItem)
            .Where(a => a.AnalyzedAtUtc >= since)
            .ToListAsync();

        return analyses
            .GroupBy(a => a.NewsItem.ScrapedAtUtc.Date)
            .Select(g => new SentimentDaily(
                g.Key,
                g.Count(a => a.Sentiment == "positive"),
                g.Count(a => a.Sentiment == "negative"),
                g.Count(a => a.Sentiment == "neutral")))
            .OrderBy(s => s.Date)
            .ToList();
    }

    /// <summary>
    /// Conta le analisi AI effettuate oggi (UTC).
    /// </summary>
    public async Task<int> GetTodayAnalysisCountAsync()
    {
        using var db = CreateContext();
        var todayUtc = DateTime.UtcNow.Date;
        return await db.AiAnalyses.CountAsync(a => a.AnalyzedAtUtc >= todayUtc);
    }

    /// <summary>
    /// Conteggio analisi AI completate.
    /// </summary>
    public async Task<(int Analyzed, int Total)> GetAiCoverageAsync()
    {
        using var db = CreateContext();
        var total = await db.News.CountAsync();
        var analyzed = await db.AiAnalyses.CountAsync();
        return (analyzed, total);
    }

    /// <summary>
    /// Ultime N analisi AI con dettagli.
    /// </summary>
    public async Task<List<(StoredNewsItem News, AiAnalysisResult Analysis)>> GetRecentAiAnalysesAsync(int limit = 20)
    {
        using var db = CreateContext();
        var items = await db.AiAnalyses
            .Include(a => a.NewsItem)
            .OrderByDescending(a => a.AnalyzedAtUtc)
            .Take(limit)
            .ToListAsync();

        return items.Select(a => (a.NewsItem, a)).ToList();
    }

    /// <summary>
    /// Elimina news e relative analisi AI più vecchie di N giorni.
    /// </summary>
    public async Task<int> DeleteOldNewsAsync(int retentionDays = 365)
    {
        using var db = CreateContext();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        var oldNewsIds = await db.News.Where(n => n.ScrapedAtUtc < cutoff).Select(n => n.Id).ToListAsync();
        if (oldNewsIds.Count == 0) return 0;

        // Elimina prima le analisi AI collegate
        var oldAnalyses = db.AiAnalyses.Where(a => oldNewsIds.Contains(a.NewsItemId));
        db.AiAnalyses.RemoveRange(oldAnalyses);

        // Elimina le news vecchie
        var oldNews = db.News.Where(n => n.ScrapedAtUtc < cutoff);
        db.News.RemoveRange(oldNews);

        await db.SaveChangesAsync();
        return oldNewsIds.Count;
    }

    private static string NormalizeForDedup(string title) =>
        Regex.Replace(title.ToLowerInvariant(), @"[^\w]", "");
}
