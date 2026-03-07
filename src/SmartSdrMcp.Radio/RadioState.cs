namespace SmartSdrMcp.Radio;

public record RadioState(
    string RadioName,
    string RadioModel,
    bool Connected,
    string? ActiveSlice,
    double FrequencyMHz,
    string Mode,
    bool IsTransmitting,
    string? Serial,
    int CwPitch = 600);
