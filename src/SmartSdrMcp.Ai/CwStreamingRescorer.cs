using System.Text;
using SmartSdrMcp.Cw;
using SmartSdrMcp.Cw.Decoder;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Ai;

/// <summary>
/// Streaming AI CW rescorer that automatically corrects decoded text after each word gap.
/// Uses QSO context, DX cluster spots, and N-best alternatives for context-aware correction.
/// </summary>
public class CwStreamingRescorer : IDisposable
{
    private readonly CwAiRescorer _aiRescorer;
    private readonly CwPipeline _cwPipeline;
    private readonly QsoTracker _qsoTracker;
    private readonly RadioManager _radioManager;
    private readonly Func<double, List<(string Callsign, double FreqKhz)>>? _spotLookup;

    private Timer? _debounceTimer;
    private readonly SemaphoreSlim _rescoreLock = new(1, 1);
    private int _lastRescoredCharIndex;
    private readonly StringBuilder _rescoredBuffer = new();
    private readonly object _bufferLock = new();
    private volatile bool _enabled;
    private DateTime _lastApiCall = DateTime.MinValue;
    private const int DebounceMs = 200;
    private const int MinApiIntervalMs = 500;
    private const int MinCharsToRescore = 2;
    private const int ContextWindowWords = 5;

    public bool IsRunning => _enabled;

    /// <summary>Fires when the AI-corrected text is updated.</summary>
    public event Action<string>? RescoredTextUpdated;

    public CwStreamingRescorer(
        CwAiRescorer aiRescorer,
        CwPipeline cwPipeline,
        QsoTracker qsoTracker,
        RadioManager radioManager,
        Func<double, List<(string Callsign, double FreqKhz)>>? spotLookup = null)
    {
        _aiRescorer = aiRescorer;
        _cwPipeline = cwPipeline;
        _qsoTracker = qsoTracker;
        _radioManager = radioManager;
        _spotLookup = spotLookup;
    }

    public string Start()
    {
        if (_enabled) return "Streaming AI rescorer is already running.";

        _enabled = true;
        _lastRescoredCharIndex = 0;
        lock (_bufferLock) _rescoredBuffer.Clear();

        _cwPipeline.WordGapReceived += OnWordGap;
        return "Streaming AI CW rescorer started.";
    }

    public string Stop()
    {
        if (!_enabled) return "Streaming AI rescorer is not running.";

        _enabled = false;
        _cwPipeline.WordGapReceived -= OnWordGap;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        return "Streaming AI CW rescorer stopped.";
    }

    public string GetRescoredText()
    {
        lock (_bufferLock)
        {
            return _rescoredBuffer.ToString();
        }
    }

    public void Reset()
    {
        _lastRescoredCharIndex = 0;
        lock (_bufferLock) _rescoredBuffer.Clear();
    }

    private void OnWordGap()
    {
        if (!_enabled) return;

        // Debounce: reset timer on each word gap
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ => _ = RescoreLatestAsync(), null, DebounceMs, Timeout.Infinite);
    }

    private async Task RescoreLatestAsync()
    {
        if (!_enabled) return;

        // Rate limit API calls
        var elapsed = (DateTime.UtcNow - _lastApiCall).TotalMilliseconds;
        if (elapsed < MinApiIntervalMs) return;

        if (!_rescoreLock.Wait(0)) return; // skip if already rescoring
        try
        {
            var allChars = _cwPipeline.GetRecentCharacters(500);
            if (allChars.Count <= _lastRescoredCharIndex) return;

            // Get new characters since last rescore
            var newChars = allChars.Skip(_lastRescoredCharIndex).ToList();
            if (newChars.Count < MinCharsToRescore) return;

            // Build the raw text for new characters
            var rawText = string.Concat(newChars.Select(c => c.Character));
            if (string.IsNullOrWhiteSpace(rawText)) return;

            // Build context prompt
            var contextPrompt = BuildContextPrompt(allChars, _lastRescoredCharIndex);

            _lastApiCall = DateTime.UtcNow;
            var result = await _aiRescorer.RescoreWithContextAsync(rawText, newChars, contextPrompt);

            _lastRescoredCharIndex = allChars.Count;

            var corrected = result.AiApplied ? result.CorrectedText : rawText;

            lock (_bufferLock)
            {
                if (_rescoredBuffer.Length > 0 && !_rescoredBuffer.ToString().EndsWith(' '))
                    _rescoredBuffer.Append(' ');
                _rescoredBuffer.Append(corrected);
            }

            RescoredTextUpdated?.Invoke(GetRescoredText());
        }
        catch (Exception ex)
        {
            // Log failures silently — streaming rescorer is best-effort
        }
        finally
        {
            _rescoreLock.Release();
        }
    }

    private string BuildContextPrompt(List<DecodedCharacter> allChars, int newStartIndex)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert CW (Morse code) decoder corrector for ham radio.");
        sb.AppendLine("Correct the raw decoded text below using the context provided.");
        sb.AppendLine("Fix dit/dah timing errors and reconstruct likely ham radio words/callsigns.");
        sb.AppendLine();

        // QSO state context
        var qso = _qsoTracker.CurrentState;
        sb.AppendLine("## QSO Context:");
        sb.AppendLine($"  Stage: {qso.Stage}");
        if (qso.TheirCallsign != null)
            sb.AppendLine($"  Their callsign: {qso.TheirCallsign}");
        if (qso.TheirName != null)
            sb.AppendLine($"  Their name: {qso.TheirName}");
        if (qso.TheirQth != null)
            sb.AppendLine($"  Their QTH: {qso.TheirQth}");
        if (qso.TheirRst != null)
            sb.AppendLine($"  Their RST: {qso.TheirRst}");

        // What to expect based on stage
        sb.Append("  Expecting: ");
        sb.AppendLine(qso.Stage switch
        {
            QsoStage.Idle => "CQ call with callsign",
            QsoStage.CqDetected => "Response with callsign and RST",
            QsoStage.Replied => "RST report, callsign confirmation",
            QsoStage.ExchangingReports => "NAME, QTH, RIG, ANT info",
            QsoStage.Conversation => "General info, 73, or SK",
            QsoStage.Closing => "73, TU, SK, or new CQ",
            _ => "Any ham radio content"
        });
        sb.AppendLine();

        // DX cluster spots near current frequency
        if (_spotLookup != null && _radioManager.IsConnected)
        {
            try
            {
                var freq = _radioManager.GetState().FrequencyMHz;
                var spots = _spotLookup(freq);
                if (spots.Count > 0)
                {
                    sb.AppendLine("## Stations spotted near this frequency:");
                    foreach (var (call, _) in spots.Take(10))
                        sb.Append($"  {call}");
                    sb.AppendLine();
                    sb.AppendLine("  (Bias callsign reconstruction toward these if plausible)");
                    sb.AppendLine();
                }
            }
            catch { /* ignore spot lookup failures */ }
        }

        // Recent rescored text for continuity
        var recentRescored = GetRescoredText();
        if (!string.IsNullOrWhiteSpace(recentRescored))
        {
            var words = recentRescored.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var contextWords = words.TakeLast(ContextWindowWords);
            sb.AppendLine("## Recently decoded (for continuity):");
            sb.AppendLine($"  ...{string.Join(" ", contextWords)}");
            sb.AppendLine();
        }

        // Common confusions (compact)
        sb.AppendLine("## Common dit/dah confusions:");
        sb.AppendLine("  T↔E, I↔A↔N↔M, S↔U↔R↔D↔K↔G↔W↔O, H↔V↔F↔B↔L↔C↔P↔Z");
        sb.AppendLine("  Common words: CQ DE K BK SK 73 RST 599 5NN QTH NAME UR TU FB OM ES HR HW CPY GE GM GA");
        sb.AppendLine("  Callsign format: 1-2 letters + digit + 1-3 letters (e.g., K1AF, W3ABC, VE7XYZ)");
        sb.AppendLine();

        return sb.ToString();
    }

    public void Dispose()
    {
        Stop();
        _rescoreLock.Dispose();
    }
}
