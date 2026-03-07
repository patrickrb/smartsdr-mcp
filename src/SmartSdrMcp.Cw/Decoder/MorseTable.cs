namespace SmartSdrMcp.Cw.Decoder;

public static class MorseTable
{
    private static readonly Dictionary<string, string> _patternToChar = new()
    {
        // Letters
        [".-"] = "A", ["-..."] = "B", ["-.-."] = "C", ["-.."] = "D",
        ["."] = "E", ["..-."] = "F", ["--."] = "G", ["...."] = "H",
        [".."] = "I", [".---"] = "J", ["-.-"] = "K", [".-.."] = "L",
        ["--"] = "M", ["-."] = "N", ["---"] = "O", [".--."] = "P",
        ["--.-"] = "Q", [".-."] = "R", ["..."] = "S", ["-"] = "T",
        ["..-"] = "U", ["...-"] = "V", [".--"] = "W", ["-..-"] = "X",
        ["-.--"] = "Y", ["--.."] = "Z",

        // Numbers
        [".----"] = "1", ["..---"] = "2", ["...--"] = "3", ["....-"] = "4",
        ["....."] = "5", ["-...."] = "6", ["--..."] = "7", ["---.."] = "8",
        ["----."] = "9", ["-----"] = "0",

        // Punctuation
        [".-.-.-"] = ".", ["--..--"] = ",", ["..--.."] = "?",
        [".----."] = "'", ["-.-.--"] = "!", ["-..-."] = "/",
        ["-.--."] = "(", ["-.--.-"] = ")", [".-..."] = "&",
        ["---..."] = ":", ["-.-.-."] = ";", ["-...-"] = "=",
        [".-.-."] = "+", ["-....-"] = "-", ["..--.-"] = "_",
        [".-..-."] = "\"", ["...-..-"] = "$", [".--.-."] = "@",

        // Prosigns
        ["-...-"] = "<BT>",   // Break (=)
        [".-.-"] = "<AA>",
        ["...-.-"] = "<SK>",  // End of contact
        [".-.-."] = "<AR>",   // End of message (+)
        ["-.--."] = "<KN>",   // Go ahead, named station only
        ["...-."] = "<SN>",   // Understood (also VE)
        ["........"] = "<HH>", // Error
    };

    private static readonly Dictionary<string, string> _charToPattern;

    static MorseTable()
    {
        _charToPattern = _patternToChar.ToDictionary(kv => kv.Value, kv => kv.Key);
    }

    public static string? Lookup(string ditDahPattern)
    {
        return _patternToChar.GetValueOrDefault(ditDahPattern);
    }

    public static string? ToPattern(string character)
    {
        return _charToPattern.GetValueOrDefault(character.ToUpper());
    }

    public static bool IsValidPattern(string pattern)
    {
        return _patternToChar.ContainsKey(pattern);
    }
}
