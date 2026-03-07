namespace SmartSdrMcp.Cw.Rescorer;

public static class CwPatterns
{
    public static readonly Dictionary<string, string> CommonPatterns = new()
    {
        ["CQ"] = "Calling any station",
        ["DE"] = "From (this is)",
        ["K"] = "Go ahead",
        ["KN"] = "Go ahead, named station only",
        ["BK"] = "Break",
        ["73"] = "Best regards",
        ["88"] = "Love and kisses",
        ["599"] = "Perfect signal report",
        ["5NN"] = "599 abbreviation",
        ["RST"] = "Readability/Signal/Tone report",
        ["UR"] = "Your",
        ["NAME"] = "Name",
        ["QTH"] = "Location",
        ["HR"] = "Here",
        ["HW"] = "How",
        ["TU"] = "Thank you",
        ["R"] = "Roger/Received",
        ["GM"] = "Good morning",
        ["GA"] = "Good afternoon",
        ["GE"] = "Good evening",
        ["OM"] = "Old man",
        ["YL"] = "Young lady",
        ["XYL"] = "Wife",
        ["WX"] = "Weather",
        ["ANT"] = "Antenna",
        ["RIG"] = "Radio equipment",
        ["PWR"] = "Power",
        ["PSE"] = "Please",
        ["AGN"] = "Again",
        ["FB"] = "Fine business (great)",
        ["ES"] = "And",
        ["FER"] = "For",
        ["SRI"] = "Sorry",
        ["HI"] = "Laughter",
        ["QSL"] = "Confirmation",
        ["QRZ"] = "Who is calling?",
        ["QRM"] = "Interference",
        ["QRN"] = "Static noise",
        ["QSB"] = "Fading",
        ["QRP"] = "Low power",
        ["QRO"] = "High power",
        ["SK"] = "End of contact",
        ["CL"] = "Closing station",
        ["DX"] = "Long distance",
        ["TEST"] = "Contest",
    };

    /// <summary>
    /// Common confusions in CW decode that should be rescored.
    /// Key = wrong decode, Value = likely correct decode.
    /// </summary>
    public static readonly Dictionary<string, string> CommonConfusions = new()
    {
        ["CO"] = "CQ",   // C and Q are close in Morse
        ["CE"] = "DE",   // Common mishear
        ["5T9"] = "599", // T is often sent for 9 in contest
        ["5NN"] = "599", // N is abbreviation for 9
    };
}
