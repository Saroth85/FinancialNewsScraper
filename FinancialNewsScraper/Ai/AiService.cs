using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinancialNewsScraper.Ai;

/// <summary>
/// Risultato dell'analisi AI di una news.
/// </summary>
public record AiNewsAnalysis(
    string Sentiment,       // "positive", "negative", "neutral"
    double SentimentScore,  // -1.0 a +1.0
    string Summary,         // riassunto breve
    string[] Entities,      // entità estratte (aziende, indici, persone)
    string[] Topics,        // topic/categorie rilevate
    string MarketImpact     // "bullish", "bearish", "neutral", "mixed"
);

/// <summary>
/// Briefing giornaliero generato dall'AI.
/// </summary>
public record AiDailyBriefing(
    string Date,
    string Summary,
    string[] KeyThemes,
    string MarketOutlook,
    string[] TopMovers,
    string[] RiskFactors
);

/// <summary>
/// Servizio AI che supporta Ollama (locale, gratis) e OpenAI API.
/// Configurazione via variabili d'ambiente:
///   AI_PROVIDER = "ollama" | "openai" (default: "ollama")
///   AI_MODEL = nome modello (default: "llama3" per ollama, "gpt-4o-mini" per openai)
///   OLLAMA_URL = URL base Ollama (default: "http://localhost:11434")
///   OPENAI_API_KEY = chiave API OpenAI (richiesta se provider=openai)
/// </summary>
public class AiService
{
    private readonly HttpClient _http;
    private readonly string _provider;
    private readonly string _model;
    private readonly string _ollamaUrl;
    private readonly string? _openAiKey;

    public bool IsAvailable { get; private set; }

    public AiService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _provider = Environment.GetEnvironmentVariable("AI_PROVIDER")?.ToLowerInvariant() ?? "ollama";
        _ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
        _openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        _model = Environment.GetEnvironmentVariable("AI_MODEL")
            ?? (_provider == "openai" ? "gpt-4o-mini" : "phi3");

        if (_provider == "openai" && string.IsNullOrEmpty(_openAiKey))
        {
            Console.WriteLine("  [AI] OPENAI_API_KEY non configurata, AI disabilitata.");
            IsAvailable = false;
        }
        else
        {
            IsAvailable = true;
        }
    }

    public async Task CheckConnectionAsync()
    {
        if (!IsAvailable) return;
        try
        {
            if (_provider == "ollama")
            {
                var resp = await _http.GetAsync($"{_ollamaUrl}/api/tags");
                IsAvailable = resp.IsSuccessStatusCode;
                if (!IsAvailable)
                    Console.WriteLine("  [AI] Ollama non raggiungibile. AI disabilitata.");
                else
                    Console.WriteLine($"  [AI] Ollama connesso ({_ollamaUrl}), modello: {_model}");
            }
            else
            {
                Console.WriteLine($"  [AI] OpenAI configurato, modello: {_model}");
            }
        }
        catch
        {
            IsAvailable = false;
            Console.WriteLine("  [AI] Servizio AI non raggiungibile. AI disabilitata.");
        }
    }

    /// <summary>
    /// Analizza una singola news con l'AI.
    /// </summary>
    public async Task<AiNewsAnalysis?> AnalyzeNewsAsync(string title, string source)
    {
        if (!IsAvailable) return null;

        var prompt = $$"""
            Analyze this financial news headline and respond ONLY with valid JSON (no markdown, no explanation):
            
            Headline: "{{title}}"
            Source: {{source}}
            
            JSON format:
            {
              "sentiment": "positive" or "negative" or "neutral",
              "sentimentScore": number from -1.0 to 1.0,
              "summary": "one sentence summary in the same language as the headline",
              "entities": ["list of companies, indices, people, financial instruments mentioned"],
              "topics": ["list of 1-3 financial topics like: stocks, bonds, crypto, commodities, forex, central_banks, earnings, macro, geopolitics, tech"],
              "marketImpact": "bullish" or "bearish" or "neutral" or "mixed"
            }
            """;

        var json = await SendPromptAsync(prompt);
        if (json == null) return null;

        try
        {
            // Estrai JSON dalla risposta (potrebbe avere testo extra)
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end < 0) return null;
            json = json[start..(end + 1)];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AiNewsAnalysis(
                root.GetProperty("sentiment").GetString() ?? "neutral",
                root.TryGetProperty("sentimentScore", out var sc) ? sc.GetDouble() : 0,
                root.TryGetProperty("summary", out var su) ? su.GetString() ?? "" : "",
                root.TryGetProperty("entities", out var en)
                    ? en.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>(),
                root.TryGetProperty("topics", out var tp)
                    ? tp.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>(),
                root.TryGetProperty("marketImpact", out var mi) ? mi.GetString() ?? "neutral" : "neutral"
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Genera un briefing giornaliero a partire dai titoli delle news.
    /// </summary>
    public async Task<AiDailyBriefing?> GenerateDailyBriefingAsync(IEnumerable<string> titles)
    {
        if (!IsAvailable) return null;

        var titlesText = string.Join("\n", titles.Take(50).Select((t, i) => $"{i + 1}. {t}"));

        var prompt = $$"""
            You are a financial analyst. Based on these news headlines from today, generate a daily market briefing.
            Respond ONLY with valid JSON (no markdown, no explanation).
            Write the summary and outlook in Italian.
            
            Headlines:
            {{titlesText}}
            
            JSON format:
            {
              "summary": "2-3 sentence overview of today's financial news in Italian",
              "keyThemes": ["list of 3-5 main themes emerging from today's news"],
              "marketOutlook": "brief outlook: bullish/bearish/mixed sentiment and why, in Italian",
              "topMovers": ["list of stocks/indices/commodities most mentioned or impacted"],
              "riskFactors": ["list of 2-3 risk factors or concerns emerging from the news"]
            }
            """;

        var json = await SendPromptAsync(prompt);
        if (json == null) return null;

        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end < 0) return null;
            json = json[start..(end + 1)];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new AiDailyBriefing(
                DateTime.UtcNow.ToString("yyyy-MM-dd"),
                root.GetProperty("summary").GetString() ?? "",
                root.TryGetProperty("keyThemes", out var kt)
                    ? kt.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>(),
                root.TryGetProperty("marketOutlook", out var mo) ? mo.GetString() ?? "" : "",
                root.TryGetProperty("topMovers", out var tm)
                    ? tm.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>(),
                root.TryGetProperty("riskFactors", out var rf)
                    ? rf.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>()
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Analisi batch: analizza più news e restituisce aggregati.
    /// </summary>
    public async Task<List<AiNewsAnalysis>> AnalyzeBatchAsync(
        IEnumerable<(string Title, string Source)> news, int maxItems = 20)
    {
        var results = new List<AiNewsAnalysis>();
        foreach (var (title, source) in news.Take(maxItems))
        {
            var analysis = await AnalyzeNewsAsync(title, source);
            if (analysis != null)
                results.Add(analysis);
        }
        return results;
    }

    private async Task<string?> SendPromptAsync(string prompt)
    {
        try
        {
            if (_provider == "openai")
                return await SendOpenAiAsync(prompt);
            else
                return await SendOllamaAsync(prompt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [AI] Errore: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> SendOllamaAsync(string prompt)
    {
        var request = new
        {
            model = _model,
            prompt = prompt,
            stream = false,
            options = new { temperature = 0.3 }
        };

        var response = await _http.PostAsJsonAsync($"{_ollamaUrl}/api/generate", request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("response").GetString();
    }

    private async Task<string?> SendOpenAiAsync(string prompt)
    {
        var request = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = "You are a financial news analyst. Always respond with valid JSON only." },
                new { role = "user", content = prompt }
            },
            temperature = 0.3,
            max_tokens = 1000
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        httpReq.Headers.Add("Authorization", $"Bearer {_openAiKey}");
        httpReq.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(httpReq);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
    }
}
