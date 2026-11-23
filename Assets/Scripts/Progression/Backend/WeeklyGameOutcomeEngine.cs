using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Deterministic, in-game calculation for one weekly match.
///
/// The engine deliberately receives an already-selected active roster. CCAS owns
/// card collection data and the future roster UI owns the player's slot choices;
/// this class only validates and consumes that shared contract. Economy likewise
/// owns the W/L reward rule, so no currency value is included in its output.
/// </summary>
public static class WeeklyGameOutcomeEngine
{
    public const string ModelVersion = "0.4";
    public const string SchemaVersion = "2.0";

    public const float TierScale = 20f;
    public const float OffenceWeight = 0.45f;
    public const float DefenceWeight = 0.40f;
    public const float SpecialTeamsWeight = 0.15f;
    public const float LogisticSteepness = 0.06f;
    public const float HomeAdvantage = 2f;

    private static readonly string[] OffenceSlots = { "qb", "rb", "ol1", "ol2", "ol3", "r1", "r2" };
    private static readonly string[] DefenceSlots = { "dl1", "dl2", "dl3", "db1", "db2", "s1", "s2" };
    private static readonly Dictionary<string, string> SlotPositions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "qb", "QB" }, { "rb", "RB" }, { "ol1", "OL" }, { "ol2", "OL" }, { "ol3", "OL" },
        { "r1", "R" }, { "r2", "R" }, { "dl1", "DL" }, { "dl2", "DL" }, { "dl3", "DL" },
        { "db1", "DB" }, { "db2", "DB" }, { "s1", "S" }, { "s2", "S" }
    };

    /// <summary>Calculates unit ratings and total strength from the 14 selected cards and coaches.</summary>
    public static TeamStrengthSummary CalculateStrength(WeeklyTeamInput team)
    {
        ValidateTeam(team);

        var cardsBySlot = team.active_roster.ToDictionary(card => card.slot_id, StringComparer.OrdinalIgnoreCase);
        float offence = UnitRating(cardsBySlot, OffenceSlots) * (1f + CoachBonus(team, "O") / 100f);
        float defence = UnitRating(cardsBySlot, DefenceSlots) * (1f + CoachBonus(team, "D") / 100f);
        float specialTeams = UnitRating(cardsBySlot, OffenceSlots.Concat(DefenceSlots)) *
                             (1f + CoachBonus(team, "ST") / 100f);

        return new TeamStrengthSummary
        {
            offence_rating = offence,
            defence_rating = defence,
            special_teams_rating = specialTeams,
            strength = OffenceWeight * offence + DefenceWeight * defence + SpecialTeamsWeight * specialTeams
        };
    }

    /// <summary>Probability that the home team wins under the shared v0.4 logistic formula.</summary>
    public static float CalculateHomeWinProbability(float homeStrength, float awayStrength)
    {
        return 1f / (1f + (float)Math.Exp(-LogisticSteepness * (homeStrength - awayStrength + HomeAdvantage)));
    }

    /// <summary>
    /// Creates exactly two records for a match: one for each team. A caller can use the
    /// stable event_id (<c>match_id:team_id</c>) to award Economy rewards exactly once.
    /// </summary>
    public static List<WeeklyGameOutcomeRecord> SimulateMatch(
        WeeklyTeamInput home,
        WeeklyTeamInput away,
        string seasonId,
        int week,
        int matchNumber,
        int seed)
    {
        if (string.IsNullOrWhiteSpace(seasonId)) throw new ArgumentException("A season ID is required.", nameof(seasonId));
        if (week < 1) throw new ArgumentOutOfRangeException(nameof(week));
        if (matchNumber < 1) throw new ArgumentOutOfRangeException(nameof(matchNumber));

        var homeSummary = CalculateStrength(home);
        var awaySummary = CalculateStrength(away);
        float homeProbability = CalculateHomeWinProbability(homeSummary.strength, awaySummary.strength);
        bool homeWon = new Random(seed).NextDouble() < homeProbability;
        string matchId = $"{seasonId}:W{week:D2}:G{matchNumber:D2}";

        return new List<WeeklyGameOutcomeRecord>
        {
            RecordFor(home, away, seasonId, week, matchId, true, homeWon, homeSummary.strength, awaySummary.strength, homeProbability),
            RecordFor(away, home, seasonId, week, matchId, false, !homeWon, awaySummary.strength, homeSummary.strength, 1f - homeProbability)
        };
    }

    private static WeeklyGameOutcomeRecord RecordFor(
        WeeklyTeamInput team,
        WeeklyTeamInput opponent,
        string seasonId,
        int week,
        string matchId,
        bool isHome,
        bool won,
        float strength,
        float opponentStrength,
        float winProbability)
    {
        return new WeeklyGameOutcomeRecord
        {
            schema_version = SchemaVersion,
            model_version = ModelVersion,
            season_id = seasonId,
            week = week,
            match_id = matchId,
            event_id = $"{matchId}:{team.team_id}",
            team_id = team.team_id,
            opponent_id = opponent.team_id,
            is_player_team = team.is_player_team,
            home_away = isHome ? "home" : "away",
            result = won ? "W" : "L",
            team_strength = (float)Math.Round(strength, 4),
            opponent_strength = (float)Math.Round(opponentStrength, 4),
            win_probability = (float)Math.Round(winProbability, 4)
        };
    }

    private static float UnitRating(IReadOnlyDictionary<string, ActiveRosterCard> cardsBySlot, IEnumerable<string> slots)
    {
        var selected = slots.Select(slot => cardsBySlot[slot].card_tier).ToList();
        return (float)selected.Average() * TierScale;
    }

    private static int CoachBonus(WeeklyTeamInput team, string role)
    {
        var coach = (team.coaches ?? Array.Empty<ActiveCoach>()).FirstOrDefault(value =>
            string.Equals(NormalizeRole(value.role), role, StringComparison.Ordinal));
        if (coach == null || coach.stats == null || coach.stats.Length != 4) return 0;
        return (int)Math.Round(coach.stats.Average() * 5f, MidpointRounding.ToEven);
    }

    private static void ValidateTeam(WeeklyTeamInput team)
    {
        if (team == null) throw new ArgumentNullException(nameof(team));
        if (string.IsNullOrWhiteSpace(team.team_id)) throw new ArgumentException("A team ID is required.", nameof(team));
        if (team.active_roster == null || team.active_roster.Length != SlotPositions.Count)
            throw new ArgumentException("An active roster must contain one card for each of the 14 team slots.", nameof(team));

        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in team.active_roster)
        {
            if (card == null || string.IsNullOrWhiteSpace(card.slot_id) || string.IsNullOrWhiteSpace(card.card_id))
                throw new ArgumentException("Every active-roster card requires a slot ID and card ID.", nameof(team));
            if (!SlotPositions.TryGetValue(card.slot_id, out var expectedPosition) ||
                !string.Equals(expectedPosition, card.position, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Card '{card.card_id}' does not match roster slot '{card.slot_id}'.", nameof(team));
            if (card.card_tier < 1 || card.card_tier > 5)
                throw new ArgumentException($"Card '{card.card_id}' has an invalid tier.", nameof(team));
            if (!slots.Add(card.slot_id) || !cardIds.Add(card.card_id))
                throw new ArgumentException("A roster slot and an owned card may each be used only once.", nameof(team));
        }
    }

    private static string NormalizeRole(string role)
    {
        return string.Equals(role, "S", StringComparison.OrdinalIgnoreCase) ? "ST" : role?.ToUpperInvariant();
    }
}

[Serializable]
public class ActiveRosterCard
{
    public string slot_id;
    public string card_id;
    public string position;
    public int card_tier;
}

[Serializable]
public class ActiveCoach
{
    public string role; // O, D, or ST
    public string coach_id;
    public float[] stats;
}

[Serializable]
public class WeeklyTeamInput
{
    public string team_id;
    public bool is_player_team;
    public ActiveRosterCard[] active_roster;
    public ActiveCoach[] coaches;
}

[Serializable]
public class TeamStrengthSummary
{
    public float offence_rating;
    public float defence_rating;
    public float special_teams_rating;
    public float strength;
}

/// <summary>One team-facing row in the shared weekly outcome contract.</summary>
[Serializable]
public class WeeklyGameOutcomeRecord
{
    public string schema_version;
    public string model_version;
    public string event_id;
    public string season_id;
    public int week;
    public string match_id;
    public string team_id;
    public string opponent_id;
    public bool is_player_team;
    public string home_away;
    public string result;
    public float team_strength;
    public float opponent_strength;
    public float win_probability;
}
