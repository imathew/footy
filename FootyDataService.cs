using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FootyScores;

// Internal DTOs for JSON deserialization
internal record GameDto(
    [property: JsonPropertyName("venueId")] int VenueId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("homeId")] int HomeId,
    [property: JsonPropertyName("homeGoals")] int? HomeGoals,
    [property: JsonPropertyName("homeBehinds")] int? HomeBehinds,
    [property: JsonPropertyName("homeScore")] int? HomeScore,
    [property: JsonPropertyName("awayId")] int AwayId,
    [property: JsonPropertyName("awayGoals")] int? AwayGoals,
    [property: JsonPropertyName("awayBehinds")] int? AwayBehinds,
    [property: JsonPropertyName("awayScore")] int? AwayScore,
    [property: JsonPropertyName("period")] string? Period,
    [property: JsonPropertyName("periodSeconds")] int? PeriodSeconds);

internal record RoundDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("roundNumber")] int RoundNumber,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startDate")] string StartDate,
    [property: JsonPropertyName("endDate")] string EndDate,
    [property: JsonPropertyName("games")] List<GameDto> Games,
    [property: JsonPropertyName("byeSquads")] List<int>? ByeSquads);

internal interface IFootyDataService
{
    Task<List<RoundDto>> FetchDataAsync();
    Round? FindAndParseRound(List<RoundDto> rounds, int? requestedRoundId, DateTimeOffset now);
}

internal class FootyDataService : IFootyDataService
{
    private readonly HttpClient _httpClient;

    public FootyDataService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("footy");
    }

    public async Task<List<RoundDto>> FetchDataAsync()
    {
        using var response = await _httpClient.GetAsync(FootyConfiguration.ApiUrl);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RoundDto>>() ?? [];
    }

    public Round? FindAndParseRound(List<RoundDto> rounds, int? requestedRoundId, DateTimeOffset now)
    {
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
            foundRoundIndex = rounds.FindIndex(r => r.Status == "playing");
            if (foundRoundIndex >= 0)
                foundRound = rounds[foundRoundIndex];

            if (foundRound == null)
            {
                // Find the last round that has started (startDate <= now)
                int lastStartedIndex = -1;
                for (int i = rounds.Count - 1; i >= 0; i--)
                {
                    if (DateTimeOffset.TryParse(rounds[i].StartDate, out var startOffset) && startOffset <= now)
                    {
                        lastStartedIndex = i;
                        break;
                    }
                }

                if (lastStartedIndex < 0)
                {
                    // Season hasn't started yet, show the first round
                    foundRound = rounds[0];
                    foundRoundIndex = 0;
                }
                else
                {
                    var lastStartedRound = rounds[lastStartedIndex];

                    // A round is unfinished if any game has passed its scheduled time but isn't complete
                    bool isUnfinished = lastStartedRound.Games.Any(g =>
                        g.Status != "completed" && g.Status != "complete" &&
                        DateTimeOffset.TryParse(g.Date, out var gameDate) && gameDate <= now);

                    bool withinCooldown = DateTimeOffset.TryParse(lastStartedRound.EndDate, out var endOffset) &&
                        now < endOffset.AddHours(24);

                    if (!isUnfinished && !withinCooldown && lastStartedIndex + 1 < rounds.Count)
                    {
                        foundRoundIndex = lastStartedIndex + 1;
                        foundRound = rounds[foundRoundIndex];
                    }
                    else
                    {
                        foundRound = lastStartedRound;
                        foundRoundIndex = lastStartedIndex;
                    }
                }
            }
        }

        if (foundRound == null)
            return null;

        int? prevRoundId = foundRoundIndex > 0 ? rounds[foundRoundIndex - 1].Id : null;
        int? nextRoundId = foundRoundIndex < rounds.Count - 1 ? rounds[foundRoundIndex + 1].Id : null;
        string? prevRoundName = foundRoundIndex > 0 ? rounds[foundRoundIndex - 1].Name : null;
        string? nextRoundName = foundRoundIndex < rounds.Count - 1 ? rounds[foundRoundIndex + 1].Name : null;

        return ConvertDtoToRound(foundRound, now, prevRoundId, nextRoundId, prevRoundName, nextRoundName);
    }

    private static Round ConvertDtoToRound(RoundDto roundDto, DateTimeOffset now, int? prevRoundId = null, int? nextRoundId = null, string? prevRoundName = null, string? nextRoundName = null)
    {
        var matches = roundDto.Games
            .Select(ConvertDtoToMatch)
            .OfType<Match>()
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
            nextRoundId,
            prevRoundName,
            nextRoundName
        );
    }

    private static Match? ConvertDtoToMatch(GameDto gameDto)
    {
        // Use DateTimeOffset to properly parse timezone-aware dates
        if (!DateTimeOffset.TryParse(gameDto.Date, out var dateOffset))
            return null;

        // Convert to UTC DateTime for consistent storage
        var date = dateOffset.UtcDateTime;

        // Build clock from period + periodSeconds (period is null when not started)
        Clock? clock = !string.IsNullOrEmpty(gameDto.Period)
            ? new Clock(gameDto.Period, gameDto.PeriodSeconds ?? 0)
            : null;

        return new Match(
            gameDto.VenueId,
            gameDto.Status,
            date,
            new Team(gameDto.HomeId, new Score(
                gameDto.HomeGoals.GetValueOrDefault(),
                gameDto.HomeBehinds.GetValueOrDefault(),
                gameDto.HomeScore.GetValueOrDefault())),
            new Team(gameDto.AwayId, new Score(
                gameDto.AwayGoals.GetValueOrDefault(),
                gameDto.AwayBehinds.GetValueOrDefault(),
                gameDto.AwayScore.GetValueOrDefault())),
            clock
        );
    }
}