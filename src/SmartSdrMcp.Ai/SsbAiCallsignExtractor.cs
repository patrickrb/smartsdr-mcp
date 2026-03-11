using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartSdrMcp.Ai;

/// <summary>
/// AI-powered callsign extraction from garbled SSB transcriptions.
/// Used as a last-resort fallback when regex and phonetic conversion fail.
/// Also supports general conversational response generation.
/// </summary>
public class SsbAiCallsignExtractor
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public SsbAiCallsignExtractor(HttpClient httpClient, string model = "claude-haiku-4-5-20251001")
    {
        _httpClient = httpClient;
        _model = model;
    }

    /// <summary>
    /// Attempt to extract a callsign from garbled transcription text using AI.
    /// Returns the callsign string or null if none could be identified.
    /// </summary>
    public async Task<string?> ExtractCallsignAsync(string transcribedText)
    {
        if (string.IsNullOrWhiteSpace(transcribedText)) return null;

        var systemPrompt =
            "You are an expert ham radio operator. Extract the amateur radio callsign from the following " +
            "speech-to-text transcription. The transcription may contain NATO phonetic alphabet words " +
            "(Alpha, Bravo, etc.), number words, or garbled/misheard versions of these. " +
            "Callsign format: 1-2 letter prefix + 1 digit + 1-4 letter suffix (e.g., K1AF, W3ABC, VE7XYZ). " +
            "Output ONLY the callsign in uppercase, nothing else. If you cannot identify a callsign, output NONE.";

        try
        {
            var request = new
            {
                model = _model,
                max_tokens = 32,
                system = systemPrompt,
                messages = new[]
                {
                    new { role = "user", content = transcribedText }
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _httpClient.PostAsync(
                "https://api.anthropic.com/v1/messages", content, cts.Token);

            if (!response.IsSuccessStatusCode) return null;

            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
            var apiResponse = JsonSerializer.Deserialize<AnthropicResponse>(responseJson);
            var result = apiResponse?.Content?.FirstOrDefault()?.Text?.Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(result) || result == "NONE") return null;

            // Validate it looks like a callsign
            if (result.Length >= 3 && result.Length <= 8 &&
                result.Any(char.IsDigit) && result.Any(char.IsLetter))
                return result;

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Generate a conversational AI response for casual QSO chat.
    /// The response is FCC Part 97 compliant and personality-driven.
    /// </summary>
    public async Task<string?> GenerateConversationalResponseAsync(
        string transcribedText, string myCallsign, string myName, string myQth,
        string? theirCallsign = null)
    {
        if (string.IsNullOrWhiteSpace(transcribedText)) return null;

        var callerRef = theirCallsign ?? "OM";
        var systemPrompt =
            $"You are an AI ham radio operator. Your callsign is {myCallsign}, your name is {myName}, " +
            $"your QTH is {myQth}. You are having an on-air SSB QSO with {callerRef}. " +
            "Be friendly, witty, and a little funny. Keep responses under 3 sentences. " +
            "You MUST comply with all FCC Part 97 rules — no obscenity, no music, no broadcasting, " +
            "always identify with your callsign. Never say anything that would violate FCC regulations. " +
            "Do not use any abbreviations that would be hard to understand when spoken aloud. " +
            "End with your callsign.";

        try
        {
            var request = new
            {
                model = _model,
                max_tokens = 256,
                system = systemPrompt,
                messages = new[]
                {
                    new { role = "user", content = $"The other station said: \"{transcribedText}\"\nRespond naturally:" }
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await _httpClient.PostAsync(
                "https://api.anthropic.com/v1/messages", content, cts.Token);

            if (!response.IsSuccessStatusCode) return null;

            var responseJson = await response.Content.ReadAsStringAsync(cts.Token);
            var apiResponse = JsonSerializer.Deserialize<AnthropicResponse>(responseJson);
            return apiResponse?.Content?.FirstOrDefault()?.Text?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public List<ContentBlock>? Content { get; set; }
    }

    private class ContentBlock
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
