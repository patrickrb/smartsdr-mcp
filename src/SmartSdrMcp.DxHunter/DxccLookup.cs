namespace SmartSdrMcp.DxHunter;

/// <summary>
/// Maps callsign prefixes to DXCC entity names.
/// Uses ITU callsign allocation prefixes for the most common ~200 entities.
/// </summary>
public static class DxccLookup
{
    /// <summary>
    /// Look up the DXCC entity name for a callsign.
    /// Tries longest prefix match first for special allocations,
    /// then falls back to standard ITU prefix blocks.
    /// </summary>
    public static string GetEntity(string callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign) || callsign.Length < 2)
            return "Unknown";

        var call = callsign.ToUpperInvariant().Trim();

        // Try exact special prefixes first (longest match wins)
        for (int len = Math.Min(4, call.Length); len >= 2; len--)
        {
            var prefix = call[..len];
            if (SpecialPrefixes.TryGetValue(prefix, out var entity))
                return entity;
        }

        // Standard ITU allocation by first character(s)
        var c1 = call[0];
        var c2 = call.Length > 1 ? call[1] : ' ';

        return (c1, c2) switch
        {
            ('A', >= '2' and <= '3') => "Botswana",
            ('A', >= '4' and <= '4') => "Oman",
            ('A', >= '5' and <= '5') => "Bhutan",
            ('A', >= '6' and <= '6') => "UAE",
            ('A', >= '7' and <= '7') => "Qatar",
            ('A', >= '9' and <= '9') => "Bahrain",
            ('A', 'P') => "Pakistan",
            ('B', _) => "China",
            ('C', >= '2' and <= '3') => "Nauru",
            ('C', >= '5' and <= '5') => "Gambia",
            ('C', >= '6' and <= '6') => "Bahamas",
            ('C', >= '8' and <= '9') => "Mozambique",
            ('C', 'E' or 'D') => "Chile",
            ('C', 'N') => "Morocco",
            ('C', 'O') => "Cuba",
            ('C', 'P') => "Bolivia",
            ('C', 'T') => "Portugal",
            ('C', 'U') => "Azores",
            ('C', 'V') => "Uruguay",
            ('C', 'X') => "Uruguay",
            ('C', 'Y') => "Canada",
            ('D', >= '2' and <= '3') => "Angola",
            ('D', >= '4' and <= '4') => "Cape Verde",
            ('D', >= '6' and <= '6') => "Comoros",
            ('D', 'U') => "Philippines",
            ('E', >= '3' and <= '3') => "Eritrea",
            ('E', >= '4' and <= '4') => "Palestine",
            ('E', >= '5' and <= '5') => "N. Cook Is.",
            ('E', >= '6' and <= '6') => "S. Cook Is.",
            ('E', >= '7' and <= '7') => "Bosnia-Herzegovina",
            ('E', 'A') => "Spain",
            ('E', 'I') => "Ireland",
            ('E', 'K') => "Armenia",
            ('E', 'L') => "Liberia",
            ('E', 'P') => "Iran",
            ('E', 'R') => "Moldova",
            ('E', 'S') => "Estonia",
            ('E', 'T') => "Ethiopia",
            ('E', 'U') => "Belarus",
            ('E', 'X') => "Kyrgyzstan",
            ('F', _) => "France",
            ('G', _) => "England",
            ('H', >= '4' and <= '4') => "Solomon Is.",
            ('H', 'A' or 'G') => "Hungary",
            ('H', 'B') => "Switzerland",
            ('H', 'C') => "Ecuador",
            ('H', 'H') => "Haiti",
            ('H', 'I') => "Dominican Rep.",
            ('H', 'K') => "Colombia",
            ('H', 'L') => "South Korea",
            ('H', 'P') => "Panama",
            ('H', 'R') => "Honduras",
            ('H', 'S') => "Thailand",
            ('H', 'V') => "Vatican",
            ('H', 'Z') => "Saudi Arabia",
            ('I', _) => "Italy",
            ('J', >= '2' and <= '2') => "Djibouti",
            ('J', >= '3' and <= '3') => "Grenada",
            ('J', >= '5' and <= '5') => "Guinea-Bissau",
            ('J', >= '6' and <= '6') => "St. Lucia",
            ('J', >= '7' and <= '7') => "Dominica",
            ('J', >= '8' and <= '8') => "St. Vincent",
            ('J', 'A' or 'E' or 'F' or 'G' or 'H' or 'I' or 'J' or 'K' or 'L' or 'M' or 'N' or 'O' or 'P' or 'Q' or 'R' or 'S') => "Japan",
            ('J', 'D') => "Japan",
            ('J', 'T') => "Mongolia",
            ('J', 'W') => "Svalbard",
            ('J', 'X') => "Jan Mayen",
            ('J', 'Y') => "Jordan",
            ('K', _) => "United States",
            ('L', 'A' or 'B' or 'C' or 'D' or 'E' or 'F' or 'G' or 'H' or 'I' or 'J' or 'K' or 'L' or 'M' or 'N') => "Norway",
            ('L', 'U') => "Argentina",
            ('L', 'X') => "Luxembourg",
            ('L', 'Y') => "Lithuania",
            ('L', 'Z') => "Bulgaria",
            ('M', _) => "England",
            ('N', _) => "United States",
            ('O', 'A') => "Peru",
            ('O', 'D') => "Lebanon",
            ('O', 'E') => "Austria",
            ('O', 'H') => "Finland",
            ('O', 'K') => "Czech Republic",
            ('O', 'M') => "Slovakia",
            ('O', 'N') => "Belgium",
            ('O', 'X') => "Greenland",
            ('O', 'Y') => "Faroe Is.",
            ('O', 'Z') => "Denmark",
            ('P', >= '2' and <= '2') => "Guyana",
            ('P', >= '4' and <= '4') => "Aruba",
            ('P', >= '5' and <= '5') => "North Korea",
            ('P', 'A' or 'B' or 'C' or 'D' or 'E' or 'F' or 'G' or 'H' or 'I') => "Netherlands",
            ('P', 'J') => "Netherlands Antilles",
            ('P', 'Y') => "Brazil",
            ('P', 'Z') => "Suriname",
            ('R', _) => "Russia",
            ('S', >= '2' and <= '3') => "Bangladesh",
            ('S', >= '5' and <= '5') => "South Africa",
            ('S', >= '7' and <= '7') => "Seychelles",
            ('S', >= '9' and <= '9') => "Sao Tome",
            ('S', 'M') => "Sweden",
            ('S', 'N' or 'O' or 'P' or 'Q') => "Poland",
            ('S', 'T') => "Sudan",
            ('S', 'U') => "Egypt",
            ('S', 'V') => "Greece",
            ('T', >= '2' and <= '2') => "Tuvalu",
            ('T', >= '3' and <= '3') => "Kiribati",
            ('T', >= '5' and <= '5') => "Somalia",
            ('T', >= '7' and <= '7') => "San Marino",
            ('T', >= '8' and <= '8') => "Palau",
            ('T', >= '9' and <= '9') => "Bosnia-Herzegovina",
            ('T', 'A') => "Turkey",
            ('T', 'C') => "Turkey",
            ('T', 'F') => "Iceland",
            ('T', 'G') => "Guatemala",
            ('T', 'I') => "Costa Rica",
            ('T', 'J') => "Cameroon",
            ('T', 'K') => "Corsica",
            ('T', 'L') => "Central African Rep.",
            ('T', 'N') => "Congo",
            ('T', 'R') => "Gabon",
            ('T', 'T') => "Chad",
            ('T', 'U') => "Ivory Coast",
            ('T', 'Y') => "Benin",
            ('T', 'Z') => "Mali",
            ('U', 'A' or 'B' or 'C' or 'D' or 'E' or 'F' or 'G' or 'H' or 'I') => "Russia",
            ('U', 'J' or 'K' or 'L' or 'M') => "Uzbekistan",
            ('U', 'N') => "Kazakhstan",
            ('V', >= '2' and <= '2') => "Antigua & Barbuda",
            ('V', >= '3' and <= '3') => "Belize",
            ('V', >= '4' and <= '4') => "St. Kitts & Nevis",
            ('V', >= '5' and <= '5') => "Namibia",
            ('V', >= '6' and <= '6') => "Micronesia",
            ('V', >= '7' and <= '7') => "Marshall Is.",
            ('V', >= '8' and <= '8') => "Brunei",
            ('V', 'E' or 'A' or 'B' or 'C' or 'O') => "Canada",
            ('V', 'K') => "Australia",
            ('V', 'P') => "British OTs",
            ('V', 'Q') => "British OTs",
            ('V', 'R') => "Hong Kong",
            ('V', 'U') => "India",
            ('W', _) => "United States",
            ('X', 'E' or 'F') => "Mexico",
            ('X', 'T') => "Burkina Faso",
            ('X', 'U') => "Cambodia",
            ('X', 'W') => "Laos",
            ('X', 'Z') => "Myanmar",
            ('Y', 'A') => "Afghanistan",
            ('Y', 'B' or 'C' or 'D' or 'E' or 'F' or 'G' or 'H') => "Indonesia",
            ('Y', 'I') => "Iraq",
            ('Y', 'J') => "Vanuatu",
            ('Y', 'K') => "Syria",
            ('Y', 'L') => "Latvia",
            ('Y', 'N') => "Nicaragua",
            ('Y', 'O') => "Romania",
            ('Y', 'S') => "El Salvador",
            ('Y', 'U') => "Serbia",
            ('Y', 'V') => "Venezuela",
            ('Z', >= '2' and <= '2') => "Zimbabwe",
            ('Z', >= '3' and <= '3') => "North Macedonia",
            ('Z', >= '6' and <= '6') => "Kosovo",
            ('Z', >= '8' and <= '8') => "South Sudan",
            ('Z', 'A') => "Albania",
            ('Z', 'B') => "Gibraltar",
            ('Z', 'C') => "British OTs",
            ('Z', 'D') => "St. Helena",
            ('Z', 'F') => "Cayman Is.",
            ('Z', 'K') => "New Zealand",
            ('Z', 'L') => "New Zealand",
            ('Z', 'P') => "Paraguay",
            ('Z', 'R' or 'S') => "South Africa",
            ('3', 'A') => "Monaco",
            ('3', 'B') => "Mauritius",
            ('3', 'D') => "Eswatini",
            ('3', 'V') => "Tunisia",
            ('3', 'X') => "Guinea",
            ('4', 'J') => "Azerbaijan",
            ('4', 'L') => "Georgia",
            ('4', 'O') => "Montenegro",
            ('4', 'S') => "Sri Lanka",
            ('4', 'U') => "United Nations",
            ('4', 'X') => "Israel",
            ('4', 'Z') => "Israel",
            ('5', 'A') => "Libya",
            ('5', 'B') => "Cyprus",
            ('5', 'H') => "Tanzania",
            ('5', 'N') => "Nigeria",
            ('5', 'R') => "Madagascar",
            ('5', 'T') => "Mauritania",
            ('5', 'U') => "Niger",
            ('5', 'V') => "Togo",
            ('5', 'W') => "Samoa",
            ('5', 'X') => "Uganda",
            ('5', 'Z') => "Kenya",
            ('6', 'O') => "Somalia",
            ('6', 'V' or 'W') => "Senegal",
            ('6', 'Y') => "Jamaica",
            ('7', 'O') => "Yemen",
            ('7', 'P') => "Lesotho",
            ('7', 'Q') => "Malawi",
            ('7', 'X') => "Algeria",
            ('8', 'P') => "Barbados",
            ('8', 'Q') => "Maldives",
            ('8', 'R') => "Guyana",
            ('9', 'A') => "Kuwait",
            ('9', 'G') => "Ghana",
            ('9', 'H') => "Malta",
            ('9', 'J') => "Zambia",
            ('9', 'K') => "Kuwait",
            ('9', 'L') => "Sierra Leone",
            ('9', 'M') => "Malaysia",
            ('9', 'N') => "Nepal",
            ('9', 'V') => "Singapore",
            ('9', 'X') => "Rwanda",
            ('9', 'Y') => "Trinidad & Tobago",
            _ => "Unknown"
        };
    }

    // Special/non-standard prefixes that override the ITU blocks
    private static readonly Dictionary<string, string> SpecialPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // US territories
        ["AH6"] = "Hawaii", ["AH7"] = "Hawaii", ["KH6"] = "Hawaii", ["WH6"] = "Hawaii", ["NH6"] = "Hawaii",
        ["KH2"] = "Guam", ["AH2"] = "Guam", ["NH2"] = "Guam", ["WH2"] = "Guam",
        ["KP2"] = "US Virgin Is.", ["NP2"] = "US Virgin Is.", ["WP2"] = "US Virgin Is.",
        ["KP3"] = "Puerto Rico", ["NP3"] = "Puerto Rico", ["WP3"] = "Puerto Rico", ["NP4"] = "Puerto Rico", ["KP4"] = "Puerto Rico", ["WP4"] = "Puerto Rico",
        ["KL7"] = "Alaska", ["AL7"] = "Alaska", ["NL7"] = "Alaska", ["WL7"] = "Alaska",

        // UK constituent countries
        ["GW"] = "Wales", ["GM"] = "Scotland", ["GI"] = "Northern Ireland", ["GD"] = "Isle of Man",
        ["GJ"] = "Jersey", ["GU"] = "Guernsey",

        // Canadian prefixes
        ["VE1"] = "Canada", ["VE2"] = "Canada", ["VE3"] = "Canada", ["VE4"] = "Canada",
        ["VE5"] = "Canada", ["VE6"] = "Canada", ["VE7"] = "Canada", ["VE8"] = "Canada", ["VE9"] = "Canada",
        ["VO1"] = "Canada", ["VO2"] = "Canada", ["VY1"] = "Canada", ["VY2"] = "Canada",
        ["CY0"] = "Sable Is.", ["CY9"] = "St. Paul Is.",

        // France OTs
        ["FG"] = "Guadeloupe", ["FM"] = "Martinique", ["FO"] = "Fr. Polynesia",
        ["FK"] = "New Caledonia", ["FR"] = "Reunion", ["FY"] = "Fr. Guiana",
        ["FH"] = "Mayotte", ["FP"] = "St. Pierre & Miquelon",

        // Netherlands Caribbean
        ["PJ2"] = "Curacao", ["PJ4"] = "Bonaire", ["PJ5"] = "St. Eustatius",
        ["PJ6"] = "Saba", ["PJ7"] = "St. Maarten",

        // Portuguese OTs
        ["CU"] = "Azores", ["CT3"] = "Madeira",

        // Spanish OTs
        ["EA6"] = "Balearic Is.", ["EA8"] = "Canary Is.", ["EA9"] = "Ceuta & Melilla",

        // Russian special
        ["UA9"] = "Asiatic Russia", ["UA0"] = "Asiatic Russia",

        // Antarctic
        ["VP8"] = "Falkland Is.",

        // Others
        ["ZL7"] = "Chatham Is.", ["ZL8"] = "Kermadec Is.", ["ZL9"] = "Auckland & Campbell",
        ["VK9N"] = "Norfolk Is.", ["VK9C"] = "Cocos-Keeling", ["VK9X"] = "Christmas Is.",
        ["VK0M"] = "Macquarie Is.", ["VK0H"] = "Heard Is.",
        ["P40"] = "Aruba", ["P43"] = "Aruba", ["P49"] = "Aruba",
        ["J6"] = "St. Lucia", ["J7"] = "Dominica", ["J8"] = "St. Vincent",

        // DXpedition favorites
        ["3B8"] = "Mauritius", ["3B9"] = "Rodriguez Is.",
        ["FT5W"] = "Crozet", ["FT5X"] = "Kerguelen", ["FT5Z"] = "Amsterdam & St. Paul",
        ["VP6"] = "Pitcairn Is.",
        ["ZS8"] = "Marion Is.",
    };
}
