using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace V2RayTui.Engine;

/// <summary>Exit IP and its country.</summary>
public sealed record GeoInfo(string Ip, string Country)
{
    public string CountryName => GeoIp.CountryName(Country);
}

/// <summary>
/// Exit IP / country lookup through a proxy, and the country a server claims in its name
/// (flag emoji, ISO code or country name in English / Russian).
/// </summary>
public static partial class GeoIp
{
    // Tried in order after v2rayN's own IPAPIUrl (if set). All answer JSON with an IP and a country.
    private static readonly string[] Services =
    [
        "https://ipinfo.io/json",
        "https://api.ipapi.is",
        "https://api.ip.sb/geoip",
        "http://ip-api.com/json",
    ];

    public static async Task<GeoInfo?> LookupAsync(IWebProxy? proxy, CancellationToken ct = default)
    {
        var urls = new List<string>();
        if (AppHost.Config?.SpeedTestItem?.IPAPIUrl is { Length: > 0 } custom)
        {
            urls.Add(custom);
        }
        urls.AddRange(Services.Where(s => !urls.Contains(s)));

        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = proxy != null,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curl/8.5.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await client.GetStringAsync(url, ct);
                if (Parse(json) is { } info)
                {
                    return info;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // next service
            }
        }
        return null;
    }

    /// <summary>Understands ipinfo.io, ipapi.is, ip.sb, ip-api.com and similar answers.</summary>
    public static GeoInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Str(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var ip = Str(root, "ip") ?? Str(root, "query") ?? Str(root, "clientIp") ?? Str(root, "ip_addr");
            var code = Str(root, "country_code") ?? Str(root, "countryCode");
            if (code.IsNullOrEmpty() && root.TryGetProperty("location", out var loc))
            {
                code = Str(loc, "country_code");
            }
            if (code.IsNullOrEmpty() && Str(root, "country") is { } country)
            {
                code = country.Length == 2 ? country : CodeFromName(country);
            }
            return ip.IsNullOrEmpty() || code.IsNullOrEmpty() ? null : new GeoInfo(ip, code.ToUpperInvariant());
        }
        catch
        {
            return null;
        }
    }

    public static string CountryName(string code)
    {
        if (Loc.IsRu)
        {
            if (code is "US" or "AE")
            {
                return code == "US" ? "США" : "ОАЭ";
            }
            // The longest Russian name is the official one ("Нидерланды", not "Голландия").
            var ru = _aliases.Where(kv => kv.Value == code && kv.Key.Any(ch => ch is >= 'А' and <= 'я'))
                .Select(kv => kv.Key)
                .MaxBy(n => n.Length);
            if (ru != null)
            {
                return ru;
            }
        }
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch
        {
            return code;
        }
    }

    /// <summary>"NL 1.2.3.4" or "NL 1.2.3.4 ≠DE" when the server name promises another country.</summary>
    public static string Format(GeoInfo geo, string? expected) =>
        expected != null && expected != geo.Country ? $"{geo.Country} {geo.Ip} ≠{expected}" : $"{geo.Country} {geo.Ip}";

    #region country claimed in the server name

    private static readonly Lazy<(Dictionary<string, string> Names, HashSet<string> Codes)> _regions = new(BuildRegions);

    // Common informal names that RegionInfo does not produce.
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USA"] = "US", ["America"] = "US", ["США"] = "US", ["Америка"] = "US",
        ["UK"] = "GB", ["England"] = "GB", ["Britain"] = "GB", ["Англия"] = "GB", ["Британия"] = "GB",
        ["Holland"] = "NL", ["Голландия"] = "NL",
        ["Korea"] = "KR", ["Корея"] = "KR",
        ["UAE"] = "AE", ["Emirates"] = "AE", ["ОАЭ"] = "AE", ["Эмираты"] = "AE",
        ["Czechia"] = "CZ", ["Чехия"] = "CZ",
        ["Turkey"] = "TR", ["Türkiye"] = "TR", ["Турция"] = "TR",
        ["Hongkong"] = "HK", ["Гонконг"] = "HK",
        ["Russia"] = "RU", ["Россия"] = "RU", ["РФ"] = "RU",
        // .NET does not localize RegionInfo names, so Russian names are listed explicitly.
        ["Германия"] = "DE", ["Нидерланды"] = "NL", ["Франция"] = "FR", ["Финляндия"] = "FI", ["Швеция"] = "SE",
        ["Норвегия"] = "NO", ["Дания"] = "DK", ["Польша"] = "PL", ["Эстония"] = "EE", ["Латвия"] = "LV",
        ["Литва"] = "LT", ["Великобритания"] = "GB", ["Ирландия"] = "IE", ["Испания"] = "ES", ["Португалия"] = "PT",
        ["Италия"] = "IT", ["Швейцария"] = "CH", ["Австрия"] = "AT", ["Бельгия"] = "BE", ["Люксембург"] = "LU",
        ["Словакия"] = "SK", ["Венгрия"] = "HU", ["Румыния"] = "RO", ["Болгария"] = "BG", ["Сербия"] = "RS",
        ["Хорватия"] = "HR", ["Словения"] = "SI", ["Греция"] = "GR", ["Кипр"] = "CY", ["Молдова"] = "MD",
        ["Украина"] = "UA", ["Беларусь"] = "BY", ["Грузия"] = "GE", ["Армения"] = "AM", ["Азербайджан"] = "AZ",
        ["Казахстан"] = "KZ", ["Узбекистан"] = "UZ", ["Кыргызстан"] = "KG", ["Киргизия"] = "KG", ["Таджикистан"] = "TJ",
        ["Израиль"] = "IL", ["Индия"] = "IN", ["Япония"] = "JP", ["Китай"] = "CN", ["Сингапур"] = "SG",
        ["Тайвань"] = "TW", ["Вьетнам"] = "VN", ["Таиланд"] = "TH", ["Малайзия"] = "MY", ["Индонезия"] = "ID",
        ["Филиппины"] = "PH", ["Австралия"] = "AU", ["Новая Зеландия"] = "NZ", ["Канада"] = "CA", ["Мексика"] = "MX",
        ["Бразилия"] = "BR", ["Аргентина"] = "AR", ["Чили"] = "CL", ["ЮАР"] = "ZA", ["Египет"] = "EG",
        ["Исландия"] = "IS", ["Монголия"] = "MN", ["Саудовская Аравия"] = "SA", ["Катар"] = "QA", ["Иран"] = "IR",
        ["Южная Корея"] = "KR", ["Черногория"] = "ME", ["Албания"] = "AL", ["Северная Македония"] = "MK", ["Мальта"] = "MT",
    };

    private static (Dictionary<string, string>, HashSet<string>) BuildRegions()
    {
        var names = new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var r = new RegionInfo(c.Name);
                if (r.TwoLetterISORegionName.Length != 2 || !r.TwoLetterISORegionName.All(char.IsAsciiLetterUpper))
                {
                    continue;
                }
                codes.Add(r.TwoLetterISORegionName);
                foreach (var n in new[] { r.EnglishName, r.NativeName })
                {
                    if (n.Length > 3)
                    {
                        names.TryAdd(n, r.TwoLetterISORegionName);
                    }
                }
            }
            catch
            {
                // not a region
            }
        }
        return (names, codes);
    }

    private static string? CodeFromName(string name) =>
        _regions.Value.Names.TryGetValue(name.Trim(), out var code) ? code : null;

    [GeneratedRegex(@"(?<![\p{L}])([A-Z]{2})(?![\p{L}])")]
    private static partial Regex CodeToken();

    /// <summary>Country the server name claims: flag emoji, then a country name, then an ISO code token.</summary>
    public static string? ExpectedCountry(string? remarks)
    {
        if (remarks.IsNullOrEmpty())
        {
            return null;
        }

        // 1. Flag emoji = two regional indicator symbols.
        var runes = remarks.EnumerateRunes().ToList();
        for (var i = 0; i + 1 < runes.Count; i++)
        {
            if (IsIndicator(runes[i]) && IsIndicator(runes[i + 1]))
            {
                var code = $"{(char)('A' + runes[i].Value - 0x1F1E6)}{(char)('A' + runes[i + 1].Value - 0x1F1E6)}";
                return code == "UK" ? "GB" : code;
            }
        }

        // 2. Country name (longest match wins: "South Korea" over "Korea").
        var (names, codes) = _regions.Value;
        string? best = null;
        var bestLen = 0;
        foreach (var (name, code) in names)
        {
            if (name.Length <= bestLen)
            {
                continue;
            }
            var idx = remarks.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && IsWordBoundary(remarks, idx - 1) && IsWordBoundary(remarks, idx + name.Length))
            {
                best = code;
                bestLen = name.Length;
            }
        }
        if (best != null)
        {
            return best;
        }

        // 3. Upper-case ISO code as a separate token: "DE-01", "[NL] fast", "US|2".
        foreach (Match m in CodeToken().Matches(remarks))
        {
            var code = m.Groups[1].Value == "UK" ? "GB" : m.Groups[1].Value;
            if (codes.Contains(code))
            {
                return code;
            }
        }
        return null;
    }

    private static bool IsIndicator(Rune r) => r.Value is >= 0x1F1E6 and <= 0x1F1FF;

    private static bool IsWordBoundary(string s, int i) => i < 0 || i >= s.Length || !char.IsLetter(s[i]);

    #endregion
}
