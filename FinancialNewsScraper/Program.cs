using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    private static DateTime _lastUpdate = DateTime.MinValue;
    private static bool _isUpdating;

    static Program()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
    }

    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Financial News Scraper ===\n");

        // == Web Server ==
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(BuildHtmlPage(), "text/html; charset=utf-8"));

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

        while (!cts.Token.IsCancellationRequested)
        {
            _isUpdating = true;
            SeenTitles.Clear();

            // Scrape in un buffer temporaneo, così la pagina mostra sempre le ultime notizie
            var tempNews = new ConcurrentDictionary<string, List<NewsItem>>();
            var originalNews = LatestNews;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Aggiornamento in corso...");
            await ScrapeAllAsync(browser, cts.Token, tempNews);

            // Aggiorna le notizie solo a scraping completato
            LatestNews.Clear();
            foreach (var kv in tempNews)
                LatestNews[kv.Key] = kv.Value;

            _lastUpdate = DateTime.Now;
            _isUpdating = false;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Completato - {LatestNews.Values.Sum(l => l.Count)} notizie totali");
            Console.WriteLine($"Pagina: http://localhost:{port}\n");

            try { await Task.Delay(TimeSpan.FromMinutes(10), cts.Token); }
            catch (TaskCanceledException) { break; }
        }

        Console.WriteLine("\nUscita...");
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

        foreach (var (name, url, selector) in browserScrapers)
        {
            try
            {
                var news = await ScrapeBrowserAsync(browser, name, url, selector);
                target[$"[BROWSER] {name}"] = news;
                PrintSection($"[BROWSER] {name}", news);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  [BROWSER {name}] Errore: {ex.Message}");
            }
        }

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

            items.Add(new NewsItem(source, title, link, DateTime.Now));
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
            items.Add(new NewsItem("Finviz", title, href, DateTime.Now));
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
                items.Add(new NewsItem(source, title, href, DateTime.Now));
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
        sb.Append("""
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
                position: sticky; top: 0; z-index: 100;
              }
              header h1 { font-size: 1.4em; color: #58a6ff; }
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
              <h1>&#128200; Financial News Scraper</h1>
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
}
