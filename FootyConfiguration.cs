using System;
using System.Collections.Generic;

namespace FootyScores;

public static class FootyConfiguration
{
    // This will be set once at startup from configuration
    public static string ApiUrl { get; set; } = string.Empty;
    public static int ClientCacheSeconds { get; set; } = 0;
    public static int ServerCacheSeconds { get; set; } = 0;

    // Always show Melb time
    public static readonly TimeZoneInfo MelbourneTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Melbourne");
    public static DateTimeOffset MelbourneNow => TimeZoneInfo.ConvertTime(DateTimeOffset.Now, MelbourneTimeZone);

    public static readonly IReadOnlyDictionary<int, string> Teams = new Dictionary<int, string>
    {
        { 10, "Crows" }, { 20, "Lions" }, { 30, "Blues" },
        { 40, "Magpies" }, { 50, "Bombers" }, { 60, "Dockers" },
        { 70, "Cats" }, { 80, "Hawks" }, { 90, "Demons" },
        { 100, "Roos" }, { 110, "Power" }, { 120, "Tigers" },
        { 130, "Saints" }, { 140, "Dogs" }, { 150, "Eagles" },
        { 160, "Swans" }, { 1000, "Suns" }, { 1010, "Giants" }
    };

    public static readonly IReadOnlyDictionary<int, string> Venues = new Dictionary<int, string>
    {
        { 2, "Bellerive Oval" }, { 6, "Adelaide Oval" }, { 20, "Gabba" },
        { 30, "Kardinia Park" }, { 40, "MCG" }, { 43, "Showgrounds" },
        { 60, "SCG" }, { 81, "Carrara" }, { 150, "Manuka Oval" },
        { 160, "Darwin" }, { 190, "Docklands" }, { 200, "York Park" },
        { 313, "Ballarat" }, { 374, "Norwood Oval" }, { 386, "Alice Springs" },
        { 2125, "Hands Oval" }, { 2925, "Perth Stadium" }, { 4105, "Barossa Park" }
    };
}