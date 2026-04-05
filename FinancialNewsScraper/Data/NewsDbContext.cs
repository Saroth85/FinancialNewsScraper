using Microsoft.EntityFrameworkCore;

namespace FinancialNewsScraper.Data;

public class StoredNewsItem
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public string Category { get; set; } = "";  // RSS, HTML, BROWSER
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime ScrapedAtUtc { get; set; }
    public string TitleNormalized { get; set; } = ""; // per dedup
}

/// <summary>
/// Risultato analisi AI per una news (1-a-1 con StoredNewsItem).
/// </summary>
public class AiAnalysisResult
{
    public int Id { get; set; }
    public int NewsItemId { get; set; }
    public StoredNewsItem NewsItem { get; set; } = null!;
    public string Sentiment { get; set; } = "";          // positive, negative, neutral
    public double SentimentScore { get; set; }            // -1.0 ~ +1.0
    public string Summary { get; set; } = "";
    public string Entities { get; set; } = "";            // JSON array stringified
    public string Topics { get; set; } = "";              // JSON array stringified
    public string MarketImpact { get; set; } = "";        // bullish, bearish, neutral, mixed
    public DateTime AnalyzedAtUtc { get; set; }
}

/// <summary>
/// Briefing giornaliero generato dall'AI.
/// </summary>
public class AiDailyBriefingRecord
{
    public int Id { get; set; }
    public string Date { get; set; } = "";                // yyyy-MM-dd
    public string Summary { get; set; } = "";
    public string KeyThemes { get; set; } = "";           // JSON array
    public string MarketOutlook { get; set; } = "";
    public string TopMovers { get; set; } = "";           // JSON array
    public string RiskFactors { get; set; } = "";         // JSON array
    public DateTime GeneratedAtUtc { get; set; }
}

public class NewsDbContext : DbContext
{
    public DbSet<StoredNewsItem> News => Set<StoredNewsItem>();
    public DbSet<AiAnalysisResult> AiAnalyses => Set<AiAnalysisResult>();
    public DbSet<AiDailyBriefingRecord> AiDailyBriefings => Set<AiDailyBriefingRecord>();

    private readonly string _dbPath;

    public NewsDbContext()
    {
        var folder = AppContext.BaseDirectory;
        _dbPath = Path.Combine(folder, "news.db");
    }

    public NewsDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={_dbPath}");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<StoredNewsItem>();
        entity.HasIndex(n => n.TitleNormalized);
        entity.HasIndex(n => n.ScrapedAtUtc);
        entity.HasIndex(n => n.Source);
        entity.HasIndex(n => n.Category);

        var ai = modelBuilder.Entity<AiAnalysisResult>();
        ai.HasIndex(a => a.NewsItemId).IsUnique();
        ai.HasIndex(a => a.Sentiment);
        ai.HasIndex(a => a.MarketImpact);
        ai.HasOne(a => a.NewsItem).WithMany().HasForeignKey(a => a.NewsItemId);

        var briefing = modelBuilder.Entity<AiDailyBriefingRecord>();
        briefing.HasIndex(b => b.Date).IsUnique();
    }
}
