using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartSdrMcp.Cw.Decoder;

namespace SmartSdrMcp.Ai;

/// <summary>
/// AI-powered CW text rescorer that uses Claude to correct decode errors
/// by considering N-best character alternatives and ham radio context.
/// </summary>
public class CwAiRescorer
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public CwAiRescorer(HttpClient httpClient, string model = "claude-haiku-4-5-20251001")
    {
        _httpClient = httpClient;
        _model = model;
    }

    /// <summary>
    /// Rescore decoded CW text using AI, considering alternative character candidates.
    /// Returns the corrected text, or the original if the API call fails.
    /// </summary>
    public async Task<AiRescoreResult> RescoreAsync(string rawText, List<DecodedCharacter> characters)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new AiRescoreResult(rawText, rawText, false, "Empty input");

        var prompt = BuildPrompt(rawText, characters);

        try
        {
            var request = new
            {
                model = _model,
                max_tokens = 1024,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync("https://api.anthropic.com/v1/messages", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return new AiRescoreResult(rawText, rawText, false, $"API error {response.StatusCode}: {errorBody}");
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonSerializer.Deserialize<AnthropicResponse>(responseJson);
            var corrected = apiResponse?.Content?.FirstOrDefault()?.Text?.Trim();

            if (string.IsNullOrWhiteSpace(corrected))
                return new AiRescoreResult(rawText, rawText, false, "Empty API response");

            return new AiRescoreResult(rawText, corrected, true, null);
        }
        catch (Exception ex)
        {
            return new AiRescoreResult(rawText, rawText, false, ex.Message);
        }
    }

    private static string BuildPrompt(string rawText, List<DecodedCharacter> characters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert CW (Morse code) decoder post-processor for ham radio.");
        sb.AppendLine("The raw decoder output below is HEAVILY corrupted by dit/dah timing errors.");
        sb.AppendLine("Your job is to AGGRESSIVELY reconstruct the most likely original message.");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Do NOT treat the raw text as mostly correct. It is often very wrong.");
        sb.AppendLine("You must reconstruct whole words and phrases, not just fix individual characters.");
        sb.AppendLine("Use the character-level alternatives AND your knowledge of ham radio QSO structure.");
        sb.AppendLine();
        sb.AppendLine("## Ham radio QSO structure (typical exchange):");
        sb.AppendLine("  CQ CQ CQ DE <callsign> <callsign> K");
        sb.AppendLine("  <their-call> DE <my-call> GM/GA/GE UR RST 599 599 QTH <location> NAME <name> HW CPY? BK");
        sb.AppendLine("  R R TU FB OM <name> UR RST 599 HR QTH <location> RIG <rig> ANT <antenna> WX <weather> 73 TU SK");
        sb.AppendLine();
        sb.AppendLine("## Common QSO vocabulary:");
        sb.AppendLine("  CQ, DE, K, BK, SK, AR, KN, R, TU, 73, 88, 599, 5NN, RST, UR, QTH, QSL, QRZ");
        sb.AppendLine("  NAME, RIG, ANT, WX, HR, HW, CPY, GE, GM, GA, GN, FB, OM, ES, DR, AGN, PSE");
        sb.AppendLine("  SOLID, GOOD, FINE, NICE, HERE, FIRST, TEST, CONTEST");
        sb.AppendLine();
        sb.AppendLine("## Callsign format:");
        sb.AppendLine("  1-2 letter prefix + 1 digit + 1-3 letter suffix (e.g., K1AF, W3ABC, VE7XYZ, N7SME, UA3DX)");
        sb.AppendLine("  Callsigns are VERY common — look for letter-digit-letter patterns in the garbled text.");
        sb.AppendLine();
        sb.AppendLine("## Known decoder confusions (dit↔dah errors cause these swaps):");
        sb.AppendLine("  Single element: T(-)↔E(.)");
        sb.AppendLine("  Two elements: I(..)↔A(.-)↔N(-.)↔M(--)");
        sb.AppendLine("  Three elements: S(...)↔U(..)↔R(.-.)↔D(-..)↔K(-.-)↔G(--.)↔W(.--)↔O(---)");
        sb.AppendLine("  Four elements: H(....)↔V(..-)↔F(..-.)↔B(-...)↔L(.-..)↔C(-..-.)↔P(.--..)↔Z(--..)");
        sb.AppendLine("  The decoder often outputs runs of T,E,I,N,H,S — these are usually longer characters with timing errors.");
        sb.AppendLine();
        sb.AppendLine("## Raw decoded text:");
        sb.AppendLine(rawText);
        sb.AppendLine();

        // Build per-character detail with alternatives
        sb.AppendLine("## Character-by-character decode (position: char [pattern] confidence, alternatives):");
        for (int i = 0; i < characters.Count; i++)
        {
            var ch = characters[i];
            var line = $"  {i}: '{ch.Character}' [{ch.DitDahPattern}] {ch.Confidence:F1}";
            if (ch.Alternatives is { Count: > 0 })
            {
                var alts = string.Join(" ", ch.Alternatives.Select(a => $"{a.Character}({a.Score:F2})"));
                line += $"  alts: {alts}";
            }
            sb.AppendLine(line);
        }
        sb.AppendLine();

        // Add word boundaries from spaces
        var words = rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            sb.AppendLine("## Word-level segments:");
            foreach (var w in words)
                sb.AppendLine($"  [{w}]");
            sb.AppendLine();
        }

        sb.AppendLine("## Instructions:");
        sb.AppendLine("1. For each segment of garbled text, consider ALL possible character substitutions from the confusion groups above.");
        sb.AppendLine("2. Try to form recognizable ham radio words, callsigns, RST reports, or common abbreviations.");
        sb.AppendLine("3. If a segment could be a callsign (has a digit surrounded by letters), try to reconstruct it.");
        sb.AppendLine("4. Unknown patterns ?(xxx) usually represent characters the decoder couldn't match — try to figure out what they are.");
        sb.AppendLine("5. Prefer interpretations that form a coherent QSO exchange.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY the reconstructed text. No explanation, no commentary. Just the corrected ham radio message.");

        return sb.ToString();
    }

    /// <summary>
    /// Configure the HTTP client with the Anthropic API key.
    /// </summary>
    public static void ConfigureHttpClient(HttpClient client, string apiKey)
    {
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // Response models for Anthropic API
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

public record AiRescoreResult(
    string OriginalText,
    string CorrectedText,
    bool AiApplied,
    string? Error);
