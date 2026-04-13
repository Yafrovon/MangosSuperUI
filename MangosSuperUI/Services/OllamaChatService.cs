using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangosSuperUI.Services;

/// <summary>
/// Lightweight Ollama integration for AiBot chat responses.
/// Calls the local Ollama instance to generate personality-driven replies
/// when a player whispers or says something to a bot.
///
/// Registered as singleton in DI. Injected into BotBrainService.
/// The domain layer stays stateless — this lives at the orchestrator level.
/// </summary>
public class OllamaChatService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaChatService> _logger;

    // Ollama endpoint — home AI server
    private const string OllamaUrl = "http://192.168.0.201:11434/api/generate";
    private const string Model = "qwen3:4b-instruct-2507-q4_K_M";

    // Safety: cap response length so bots don't monologue
    private const int MaxResponseLength = 200;

    // Timeout — don't block the brain loop forever
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public OllamaChatService(ILogger<OllamaChatService> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = RequestTimeout };
    }

    /// <summary>
    /// Generate a chat response from Ollama, flavored by the bot's personality.
    /// Returns the response text, or null if Ollama is unreachable / errors out.
    /// </summary>
    public async Task<string?> GetChatResponseAsync(
        string botName,
        string botClass,
        int botLevel,
        string temperament,
        string chatStyle,
        string senderName,
        string message)
    {
        try
        {
            var systemPrompt = BuildSystemPrompt(botName, botClass, botLevel, temperament, chatStyle);
            var userPrompt = $"{senderName} says: {message}";

            var requestBody = new OllamaRequest
            {
                Model = Model,
                Prompt = userPrompt,
                System = systemPrompt,
                Stream = false,
                Options = new OllamaOptions
                {
                    Temperature = 0.8f,
                    TopP = 0.9f,
                    NumPredict = 80  // ~1-2 sentences max
                }
            };

            var json = JsonSerializer.Serialize(requestBody, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _logger.LogInformation("OllamaChat: {BotName} generating reply to {Sender}: \"{Message}\"",
                botName, senderName, message);

            var response = await _http.PostAsync(OllamaUrl, content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OllamaResponse>(responseJson, JsonOpts);

            if (result?.Response == null)
            {
                _logger.LogWarning("OllamaChat: null response for {BotName}", botName);
                return null;
            }

            // Clean up: trim, cap length, strip any /think tags if model outputs them
            var reply = CleanResponse(result.Response);

            _logger.LogInformation("OllamaChat: {BotName} → \"{Reply}\"", botName, reply);
            return reply;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("OllamaChat: timeout for {BotName} replying to {Sender}", botName, senderName);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("OllamaChat: connection error for {BotName}: {Error}", botName, ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OllamaChat: unexpected error for {BotName}", botName);
            return null;
        }
    }

    private static string BuildSystemPrompt(string botName, string botClass, int botLevel, string temperament, string chatStyle)
    {
        // The key insight: keep it SHORT. Small models do best with concise instructions.
        // The bot should feel like a real WoW player, not an AI assistant.
        return $"""
            You are {botName}, a level {botLevel} {botClass} playing World of Warcraft (vanilla, 2005 era).
            You are a real player, NOT an NPC or AI assistant. Never break character.
            Personality: {temperament}. Chat style: {chatStyle}.
            Keep responses to 1-2 short sentences. Use casual MMO language.
            Never use emojis. Never say you're an AI. Never offer help like a customer service bot.
            If someone asks something you don't know, just say so like a normal player would.
            /no_think
            """;
    }

    private static string CleanResponse(string raw)
    {
        var cleaned = raw.Trim();

        // Strip <think>...</think> blocks if the model outputs them despite /no_think
        var thinkStart = cleaned.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (thinkStart >= 0)
        {
            var thinkEnd = cleaned.IndexOf("</think>", thinkStart, StringComparison.OrdinalIgnoreCase);
            if (thinkEnd >= 0)
                cleaned = cleaned[(thinkEnd + 8)..].Trim();
            else
                cleaned = cleaned[..thinkStart].Trim();
        }

        // Cap length — don't let the bot write essays
        if (cleaned.Length > MaxResponseLength)
        {
            // Cut at last sentence boundary before limit
            var cutPoint = cleaned.LastIndexOf('.', MaxResponseLength);
            if (cutPoint < 20) cutPoint = cleaned.LastIndexOf(' ', MaxResponseLength);
            if (cutPoint < 20) cutPoint = MaxResponseLength;
            cleaned = cleaned[..cutPoint].TrimEnd();
        }

        // Strip leading/trailing quotes if model wraps response
        if (cleaned.StartsWith('"') && cleaned.EndsWith('"'))
            cleaned = cleaned[1..^1].Trim();


        // Replace common unicode escapes that break in-game display
        cleaned = cleaned.Replace("\u2014", "-");   // em dash
        cleaned = cleaned.Replace("\u2013", "-");   // en dash  
        cleaned = cleaned.Replace("\u2018", "'");   // left single quote
        cleaned = cleaned.Replace("\u2019", "'");   // right single quote
        cleaned = cleaned.Replace("\u201C", "\"");  // left double quote
        cleaned = cleaned.Replace("\u201D", "\"");  // right double quote
        cleaned = cleaned.Replace("\u2026", "...");  // ellipsis
        cleaned = cleaned.Replace("\n", " ");        // newline to space

        return cleaned;
    }

    // ==================== DTOs ====================

    private class OllamaRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";

        [JsonPropertyName("system")]
        public string System { get; set; } = "";

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("options")]
        public OllamaOptions Options { get; set; } = new();
    }

    private class OllamaOptions
    {
        [JsonPropertyName("temperature")]
        public float Temperature { get; set; }

        [JsonPropertyName("top_p")]
        public float TopP { get; set; }

        [JsonPropertyName("num_predict")]
        public int NumPredict { get; set; }
    }

    private class OllamaResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
