using System.Text.RegularExpressions;

namespace SmartSdrMcp.Qso;

/// <summary>
/// State machine tracking QSO progression.
/// </summary>
public partial class QsoTracker
{
    private readonly string _myCallsign;
    private readonly List<CwMessage> _recentMessages = new();
    private readonly object _lock = new();

    public QsoState CurrentState { get; private set; } = QsoState.Empty;

    public event Action<QsoState>? StateChanged;
    public event Action<CwMessage>? MessageReceived;

    public QsoTracker(string myCallsign)
    {
        _myCallsign = myCallsign.ToUpper();
    }

    public void ProcessMessage(CwMessage message)
    {
        lock (_lock)
        {
            _recentMessages.Add(message);
            if (_recentMessages.Count > 100)
                _recentMessages.RemoveRange(0, 50);
        }

        MessageReceived?.Invoke(message);
        UpdateState(message);
    }

    public void NotifySent(string text)
    {
        CurrentState = CurrentState with { LastSent = text };

        if (CurrentState.Stage == QsoStage.CqDetected)
        {
            CurrentState = CurrentState with { Stage = QsoStage.Replied };
        }

        StateChanged?.Invoke(CurrentState);
    }

    public List<CwMessage> GetRecentMessages(int count = 20)
    {
        lock (_lock)
        {
            return _recentMessages.TakeLast(count).ToList();
        }
    }

    public void ResetQso()
    {
        CurrentState = QsoState.Empty;
        StateChanged?.Invoke(CurrentState);
    }

    private void UpdateState(CwMessage message)
    {
        var text = message.DecodedText.ToUpper();

        switch (CurrentState.Stage)
        {
            case QsoStage.Idle:
                if (message.IsCq || text.Contains(_myCallsign))
                {
                    CurrentState = new QsoState(
                        TheirCallsign: message.DetectedCallsign,
                        Stage: QsoStage.CqDetected,
                        LastReceived: message,
                        LastSent: null,
                        TheirName: null,
                        TheirQth: null,
                        TheirRst: null,
                        StartTime: DateTime.UtcNow);
                }
                break;

            case QsoStage.CqDetected:
            case QsoStage.Replied:
                // Look for signal report
                if (ContainsSignalReport(text))
                {
                    CurrentState = CurrentState with
                    {
                        Stage = QsoStage.ExchangingReports,
                        LastReceived = message,
                        TheirRst = ExtractRst(text),
                        TheirCallsign = message.DetectedCallsign ?? CurrentState.TheirCallsign
                    };
                }
                else if (message.DetectedCallsign != null)
                {
                    CurrentState = CurrentState with
                    {
                        TheirCallsign = message.DetectedCallsign,
                        LastReceived = message
                    };
                }
                break;

            case QsoStage.ExchangingReports:
                var name = ExtractName(text);
                var qth = ExtractQth(text);
                CurrentState = CurrentState with
                {
                    Stage = QsoStage.Conversation,
                    LastReceived = message,
                    TheirName = name ?? CurrentState.TheirName,
                    TheirQth = qth ?? CurrentState.TheirQth
                };
                break;

            case QsoStage.Conversation:
                if (text.Contains("73") || text.Contains("<SK>") || text.Contains("TU"))
                {
                    CurrentState = CurrentState with
                    {
                        Stage = QsoStage.Closing,
                        LastReceived = message
                    };
                }
                else
                {
                    var n = ExtractName(text);
                    var q = ExtractQth(text);
                    CurrentState = CurrentState with
                    {
                        LastReceived = message,
                        TheirName = n ?? CurrentState.TheirName,
                        TheirQth = q ?? CurrentState.TheirQth
                    };
                }
                break;

            case QsoStage.Closing:
                CurrentState = CurrentState with
                {
                    Stage = QsoStage.Complete,
                    LastReceived = message
                };
                break;
        }

        StateChanged?.Invoke(CurrentState);
    }

    private static bool ContainsSignalReport(string text)
    {
        return RstRegex().IsMatch(text) || text.Contains("5NN") || text.Contains("599");
    }

    private static string? ExtractRst(string text)
    {
        var match = RstRegex().Match(text);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\b[1-5][1-9][1-9]\b")]
    private static partial Regex RstRegex();

    private static string? ExtractName(string text)
    {
        // Look for "NAME <word>" or "NAME IS <word>"
        var match = Regex.Match(text, @"NAME\s+(?:IS\s+)?([A-Z]{2,})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractQth(string text)
    {
        var match = Regex.Match(text, @"QTH\s+(?:IS\s+)?([A-Z]{2,})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
