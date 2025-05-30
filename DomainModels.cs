using System;
using System.Collections.Generic;

namespace FootyScores;

public record Score(int Goals, int Behinds, int Total);
public record Team(int Id, Score Score);
public record Clock(string Period, int Seconds);
public record Match(int VenueId, string Status, DateTime Date, Team Home, Team Away, Clock? Clock);
public record Round(int Id, string Name, string Status, DateTimeOffset Now, IReadOnlyList<Match> Matches, IReadOnlyList<int>? ByeTeamIds = null, int? PreviousRoundId = null, int? NextRoundId = null);