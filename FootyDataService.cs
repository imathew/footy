using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FootyScores;

// Internal DTOs for JSON deserialization
internal record ClockDto(
    [property: JsonPropertyName("p")] string Period,
    [property: JsonPropertyName("s")] int Seconds);

internal record MatchDto(
    [property: JsonPropertyName("venue_id")] int VenueId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("home_squad_id")] int HomeSquadId,
    [property: JsonPropertyName("home_goals")] int? HomeGoals,
    [property: JsonPropertyName("home_behinds")] int? HomeBehinds,
    [property: JsonPropertyName("home_score")] int? HomeScore,
    [property: JsonPropertyName("away_squad_id")] int AwaySquadId,
    [property: JsonPropertyName("away_goals")] int? AwayGoals,
    [property: JsonPropertyName("away_behinds")] int? AwayBehinds,
    [property: JsonPropertyName("away_score")] int? AwayScore,
    [property: JsonPropertyName("clock")] ClockDto? Clock);

internal record RoundDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("start")] string Start,
    [property: JsonPropertyName("end")] string End,
    [property: JsonPropertyName("matches")] List<MatchDto> Matches,
    [property: JsonPropertyName("bye_squads")] List<int>? ByeSquads);

public interface IFootyDataService
{
    Task<string> FetchDataAsync();
    Round? FindAndParseRound(string jsonData, int? requestedRoundId, DateTimeOffset now);
}

public class FootyDataService : IFootyDataService
{
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FootyDataService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
    }

    public async Task<string> FetchDataAsync()
    {
        using var response = await _httpClient.GetAsync(FootyConfiguration.ApiUrl);
        response.EnsureSuccessStatusCode();

        using var contentStream = await response.Content.ReadAsStreamAsync();

        Stream stream = response.Content.Headers.ContentEncoding.Contains("gzip")
            ? new GZipStream(contentStream, CompressionMode.Decompress)
            : contentStream;

        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public Round? FindAndParseRound(string jsonData, int? requestedRoundId, DateTimeOffset now)
    {
        var rounds = JsonSerializer.Deserialize<List<RoundDto>>(jsonData, _jsonOptions);
        if (rounds == null || rounds.Count == 0)
            return null;

        RoundDto? foundRound = null;
        int foundRoundIndex = -1;

        if (requestedRoundId.HasValue)
        {
            foundRoundIndex = rounds.FindIndex(r => r.Id == requestedRoundId.Value);
            if (foundRoundIndex >= 0)
                foundRound = rounds[foundRoundIndex];
        }
        else
        {
            foundRoundIndex = rounds.FindIndex(r => r.Status == "active");
            if (foundRoundIndex >= 0)
                foundRound = rounds[foundRoundIndex];

            if (foundRound == null)
            {
                double? closestDiff = null;

                for (int i = 0; i < rounds.Count; i++)
                {
                    var round = rounds[i];
                    if (string.IsNullOrEmpty(round.Start) || string.IsNullOrEmpty(round.End))
                        continue;

                    if (!DateTimeOffset.TryParse(round.Start, out var startOffset) ||
                        !DateTimeOffset.TryParse(round.End, out var endOffset))
                        continue;

                    var start = startOffset.UtcDateTime;
                    var end = endOffset.UtcDateTime;

                    var startDiff = Math.Abs((start - now).TotalSeconds);
                    var endDiff = Math.Abs((end - now).TotalSeconds);
                    var diff = Math.Min(startDiff, endDiff);

                    if (!closestDiff.HasValue || diff < closestDiff.Value)
                    {
                        closestDiff = diff;
                        foundRound = round;
                        foundRoundIndex = i;
                    }
                }
            }
        }

        if (foundRound == null)
            return null;

        int? prevRoundId = foundRoundIndex > 0 ? rounds[foundRoundIndex - 1].Id : null;
        int? nextRoundId = foundRoundIndex < rounds.Count - 1 ? rounds[foundRoundIndex + 1].Id : null;

        return ConvertDtoToRound(foundRound, now, prevRoundId, nextRoundId);
    }

    private static Round ConvertDtoToRound(RoundDto roundDto, DateTimeOffset now, int? prevRoundId = null, int? nextRoundId = null)
    {
        var matches = roundDto.Matches
            .Select(ConvertDtoToMatch)
            .Where(m => m != null)
            .Cast<Match>()
            .OrderBy(m => m.Date)
            .ToList();

        return new Round(
            roundDto.Id,
            roundDto.Name,
            roundDto.Status,
            now,
            matches,
            roundDto.ByeSquads,
            prevRoundId,
            nextRoundId
        );
    }

    private static Match? ConvertDtoToMatch(MatchDto matchDto)
    {
        // Use DateTimeOffset to properly parse timezone-aware dates
        if (!DateTimeOffset.TryParse(matchDto.Date, out var dateOffset))
            return null;

        // Convert to UTC DateTime for consistent storage
        var date = dateOffset.UtcDateTime;

        Clock? clock = matchDto.Clock != null
            ? new Clock(matchDto.Clock.Period, matchDto.Clock.Seconds)
            : null;

        return new Match(
            matchDto.VenueId,
            matchDto.Status,
            date,  // Now stored as UTC
            new Team(matchDto.HomeSquadId, new Score(
                matchDto.HomeGoals.GetValueOrDefault(),
                matchDto.HomeBehinds.GetValueOrDefault(),
                matchDto.HomeScore.GetValueOrDefault())),
            new Team(matchDto.AwaySquadId, new Score(
                matchDto.AwayGoals.GetValueOrDefault(),
                matchDto.AwayBehinds.GetValueOrDefault(),
                matchDto.AwayScore.GetValueOrDefault())),
            clock
        );
    }
}