namespace SmartSdrMcp.Web;

public class DashboardSnapshot
{
    public AgentSnapshot Agent { get; set; } = new();
    public RadioSnapshot Radio { get; set; } = new();
    public CwSnapshot Cw { get; set; } = new();
    public SsbSnapshot Ssb { get; set; } = new();
    public AudioSnapshot Audio { get; set; } = new();
    public List<QsoEntry> Qsos { get; set; } = [];
    public AgentsRunningSnapshot Agents { get; set; } = new();
    public bool TxGuardArmed { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public class AgentsRunningSnapshot
{
    public bool DxHunter { get; set; }
    public bool DxCluster { get; set; }
    public bool BandScout { get; set; }
    public bool Contest { get; set; }
}

public class AgentSnapshot
{
    public string Stage { get; set; } = "Idle";
    public bool IsRunning { get; set; }
    public string MyCallsign { get; set; } = "";
    public int QsosCompleted { get; set; }
    public int CqsSent { get; set; }
    public string? CurrentCaller { get; set; }
    public string? PartialCallsign { get; set; }
    public int PileupAttempt { get; set; }
    public string? LastDecodedText { get; set; }
    public string? LastSentText { get; set; }
    public string? LastError { get; set; }
    public string Mode { get; set; } = "Voice";
    public string? LicenseClass { get; set; }
    public List<string> StatusLog { get; set; } = [];
}

public class RadioSnapshot
{
    public string RadioName { get; set; } = "N/A";
    public string RadioModel { get; set; } = "N/A";
    public bool Connected { get; set; }
    public double FrequencyMHz { get; set; }
    public string Mode { get; set; } = "N/A";
    public bool IsTransmitting { get; set; }
    public int CwPitch { get; set; } = 600;
    public Dictionary<string, double> Meters { get; set; } = new();
    public int RfPower { get; set; }
}

public class CwSnapshot
{
    public bool Running { get; set; }
    public string LiveText { get; set; } = "";
    public string? AiText { get; set; }
    public bool AiRunning { get; set; }
    public string? NeuralText { get; set; }
    public bool NeuralRunning { get; set; }
    public string[]? NeuralCallsigns { get; set; }
    public string? NeuralMessageType { get; set; }
    public double Wpm { get; set; }
    public double SignalRms { get; set; }
    public double ToneMagnitude { get; set; }
    public double NoiseFloor { get; set; }
    public bool TonePresent { get; set; }
    public bool GateOpen { get; set; }
    public List<CwCharEntry> RecentCharacters { get; set; } = [];
}

public class CwCharEntry
{
    public string Character { get; set; } = "";
    public double Confidence { get; set; }
    public List<string> Alternatives { get; set; } = [];
}

public class SsbSnapshot
{
    public bool Running { get; set; }
    public string LiveText { get; set; } = "";
    public List<SsbSegmentEntry> RecentSegments { get; set; } = [];
}

public class SsbSegmentEntry
{
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = "";
    public string Speaker { get; set; } = "";
}

public class AudioSnapshot
{
    public bool Running { get; set; }
    public double LevelRms { get; set; }
    public int? DaxChannel { get; set; }
}

public class QsoEntry
{
    public string TheirCallsign { get; set; } = "";
    public string OurRst { get; set; } = "";
    public string TheirRst { get; set; } = "";
    public string? TheirName { get; set; }
    public string? TheirQth { get; set; }
    public double FrequencyMHz { get; set; }
    public string Band { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
}
