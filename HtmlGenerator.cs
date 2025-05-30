using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web;

namespace FootyScores;

public class HtmlGenerator()
{
    public static string GenerateCompletePage(Round? round)
    {
        var scoresHtml = round != null
            ? GenerateRoundHtml(round)
            : "<table class=\"round-table\"><thead><tr><th class=\"round-header\">No round found!</th></tr></thead></table>";

        var html = $@"<!DOCTYPE html>
<html class=""no-js"" lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""robots"" content=""noindex,nofollow"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Footy Scores</title>
    <link rel=""stylesheet"" href=""style.css"">
    <meta name=""description"" content=""Local footy scores for local people"">
    <meta property=""og:image"" content=""/icon512.png"">

    <link rel=""icon"" type=""image/png"" href=""/favicon96.png"" sizes=""96x96"" />
    <link rel=""icon"" type=""image/svg+xml"" href=""/favicon.svg"" />
    <link rel=""shortcut icon"" href=""/favicon.ico"" />
    <link rel=""apple-touch-icon"" sizes=""180x180"" href=""/apple-touch-icon.png"" />
    <meta name=""apple-mobile-web-app-title"" content=""Footy"" />
    <link rel=""manifest"" href=""/site.webmanifest"" />
</head>
<body>
<h1 class=""sr-only"">Footy Scores</h1>
<main><section id=""scores""><div id=""footyScores"">
{scoresHtml}

</div></section></main>
</body>
</html>";

        return html;
    }

    private static string GenerateRoundHtml(Round round)
    {
        var matchesHtml = new StringBuilder();
        foreach (var match in round.Matches)
        {
            matchesHtml.Append(GenerateMatchHtml(match));
        }

        string roundHeader = FormatRoundHeaderWithDateRange(round);

        string prevLink = round.PreviousRoundId.HasValue
            ? $"<a href=\"?round={round.PreviousRoundId}\" class=\"nav-link prev-link\">« Round {round.PreviousRoundId}</a>"
            : "<span class=\"nav-link disabled\"></span>";

        string nextLink = round.NextRoundId.HasValue
            ? $"<a href=\"?round={round.NextRoundId}\" class=\"nav-link next-link\">Round {round.NextRoundId} »</a>"
            : "<span class=\"nav-link disabled\"></span>";

        // Make the timestamp a refresh link
        string currentUrl = round.Status != "active" && round.Id > 0 ? $"?round={round.Id}" : "/";
        string dateStr = $"<a href=\"{currentUrl}\" class=\"date-time\" title=\"Click to refresh\">{round.Now:yyyy-MM-dd h:mm:ss tt}</a>";

        string byeTeamsHtml = CreateByeTeamsHtml(round.ByeTeamIds);

        return $@"
<table class=""round-table"">
    <thead>
        <tr>
            <th colspan=""8"" class=""round-header"">{roundHeader}</th>
        </tr>
    </thead>
    <tbody>
        {matchesHtml}
    </tbody>
</table>

{byeTeamsHtml}

<div class=""navigation-links"">
    {prevLink}
    {nextLink}
</div>

{dateStr}";
    }

    private static string GenerateMatchHtml(Match match)
    {
        string venueNickname = FootyConfiguration.Venues.TryGetValue(match.VenueId, out var venue)
            ? venue
            : $"Venue {match.VenueId}";

        string homeTeamNickname = FootyConfiguration.Teams.TryGetValue(match.Home.Id, out var homeNick)
            ? homeNick
            : $"Team {match.Home.Id}";

        string awayTeamNickname = FootyConfiguration.Teams.TryGetValue(match.Away.Id, out var awayNick)
            ? awayNick
            : $"Team {match.Away.Id}";

        string matchHeader = FormatMatchHeader(match, venueNickname);
        string homeScoreDetail = $"{match.Home.Score.Goals}.{match.Home.Score.Behinds}";
        string awayScoreDetail = $"{match.Away.Score.Goals}.{match.Away.Score.Behinds}";

        string winningClass = match.Home.Score.Total > match.Away.Score.Total ? "home" :
                             match.Away.Score.Total > match.Home.Score.Total ? "away" : "level";

        return $@"
        <tr>
            <td colspan=""8"" class=""match-header {match.Status}"">{matchHeader}</td>
        </tr>
        <tr class=""match-row {match.Status} {winningClass}"">
            <td class=""team-icon home""><img src=""/icons/{homeTeamNickname.ToLower()}.svg"" alt=""""></td>
            <td class=""team-name home"">{homeTeamNickname}</td>
            <td class=""score detail home"">{homeScoreDetail}</td>
            <td class=""score total home"">{match.Home.Score.Total}</td>
            <td class=""score total away"">{match.Away.Score.Total}</td>
            <td class=""score detail away"">{awayScoreDetail}</td>
            <td class=""team-name away"">{awayTeamNickname}</td>
            <td class=""team-icon away""><img src=""/icons/{awayTeamNickname.ToLower()}.svg"" alt=""""></td>
        </tr>";
    }

    private static string CreateByeTeamsHtml(IReadOnlyList<int>? byeTeamIds)
    {
        if (byeTeamIds == null || byeTeamIds.Count == 0)
            return string.Empty;

        var html = new StringBuilder("<div class=\"bye-teams\">");

        foreach (var teamId in byeTeamIds)
        {
            if (FootyConfiguration.Teams.TryGetValue(teamId, out var teamName))
            {
                html.Append($"<img src=\"/icons/{teamName.ToLower()}.svg\" alt=\"{teamName}\" class=\"bye-team-icon\"> ");
            }
        }

        html.Append("</div>");
        return html.ToString();
    }


    private static string FormatRoundHeaderWithDateRange(Round round)
    {
        if (round.Matches.Count == 0)
            return $"Round {round.Id}";

        DateTime firstDate = round.Matches.Min(m => m.Date);
        DateTime lastDate = round.Matches.Max(m => m.Date);

        string dateRangeStr;
        if (firstDate.Month == lastDate.Month)
        {
            dateRangeStr = $"Round {round.Id} <span class=\"round-date\">({firstDate:MMM} {firstDate.Day} - {lastDate.Day})</span>";
        }
        else
        {
            dateRangeStr = $"Round {round.Id} <span class=\"round-date\">({firstDate:MMM} {firstDate.Day} - {lastDate:MMM} {lastDate.Day})</span>";
        }

        return dateRangeStr;
    }

    private static string FormatMatchHeader(Match match, string venueNickname)
    {
        string statusText = match.Status.ToLower() switch
        {
            "complete" => "FT",
            "playing" when match.Clock != null => FormatClockText(match.Clock),
            "playing" => "LIVE",
            "scheduled" => match.Date.ToString("ddd h:mmtt"),
            _ => match.Status
        };

        return $"{statusText} @ {venueNickname}";
    }

    private static string FormatClockText(Clock? clock)
    {
        return clock switch
        {
            null => string.Empty,
            { Seconds: -1, Period: "Q1" } => "QT",
            { Seconds: -1, Period: "Q2" } => "HT",
            { Seconds: -1, Period: "Q3" } => "3QT",
            { Seconds: -1, Period: "Q4" } => "FT",
            { Seconds: -1 } => clock.Period,
            _ => $"{clock.Period} {clock.Seconds / 60}:{clock.Seconds % 60:D2}"
        };
    }

    public static string GenerateErrorPage(string errorMessage)
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""robots"" content=""noindex,nofollow"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Footy Scores - Error</title>
    <style>
        body {{ font-family: sans-serif; max-width: 30em; margin: 0 auto; padding: 1em; }}
        .error-message {{ color: #d00; padding: 1em; }}
    </style>
</head>
<body>
    <main>
        <section id=""scores"">
            <div id=""footyScores"">
                <table class=""round-table"">
                    <thead>
                        <tr>
                            <th class=""round-header"">Error</th>
                        </tr>
                    </thead>
                    <tbody>
                        <tr>
                            <td colspan=""8"" class=""error-message"">{HttpUtility.HtmlEncode(errorMessage)}</td>
                        </tr>
                    </tbody>
                </table>
            </div>
        </section>
    </main>
</body>
</html>";
    }

}
