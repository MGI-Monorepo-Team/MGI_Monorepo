using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// Reads complete weekly results produced by the standalone simulator.
/// This class is intentionally read-only: simulator/exporter ownership remains outside Unity.
/// </summary>
public class GameOutcomeReader
{
    public const string DefaultRelativePath = "Coaches/Database/game.json";

    private readonly string _filePath;

    public GameOutcomeReader(string filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(Application.streamingAssetsPath, DefaultRelativePath)
            : filePath;
    }

    public bool TryGetWeek(string seasonId, int week, IReadOnlyCollection<string> seasonTeamIds,
        out List<GameOutcomeRecord> outcomes, out string error)
    {
        outcomes = new List<GameOutcomeRecord>();
        error = null;

        if (!File.Exists(_filePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var file = JsonUtility.FromJson<GameOutcomeFile>(json);
            if (file?.outcomes == null || file.outcomes.Length == 0)
            {
                return false;
            }

            outcomes = file.outcomes
                .Where(outcome => outcome != null && outcome.week == week &&
                    (string.IsNullOrWhiteSpace(outcome.season_id) || outcome.season_id == seasonId))
                .ToList();

            if (outcomes.Count == 0)
            {
                return false;
            }

            error = ValidateCompleteWeek(outcomes, seasonTeamIds);
            if (error == null)
            {
                return true;
            }

            outcomes.Clear();
            return false;
        }
        catch (Exception exception)
        {
            error = $"Could not read game outcomes at '{_filePath}': {exception.Message}";
            outcomes.Clear();
            return false;
        }
    }

    private static string ValidateCompleteWeek(IEnumerable<GameOutcomeRecord> outcomes, IReadOnlyCollection<string> seasonTeamIds)
    {
        if (seasonTeamIds == null || seasonTeamIds.Count == 0)
        {
            return "Season has no teams to match against simulator outcomes.";
        }

        var expected = new HashSet<string>(seasonTeamIds.Where(id => !string.IsNullOrWhiteSpace(id)));
        var encountered = new HashSet<string>();

        foreach (var outcome in outcomes)
        {
            if (string.IsNullOrWhiteSpace(outcome.event_id) ||
                string.IsNullOrWhiteSpace(outcome.home_team_id) ||
                string.IsNullOrWhiteSpace(outcome.away_team_id) ||
                string.IsNullOrWhiteSpace(outcome.winner_team_id))
            {
                return "A simulator outcome is missing event_id or team identifiers.";
            }

            if (!expected.Contains(outcome.home_team_id) || !expected.Contains(outcome.away_team_id))
            {
                return "A simulator outcome references a team that is not in this season.";
            }

            if (outcome.winner_team_id != outcome.home_team_id && outcome.winner_team_id != outcome.away_team_id)
            {
                return "A simulator outcome winner must be either the home or away team.";
            }

            if (!encountered.Add(outcome.home_team_id) || !encountered.Add(outcome.away_team_id))
            {
                return "A simulator outcome week contains a team more than once.";
            }
        }

        return encountered.SetEquals(expected)
            ? null
            : "Simulator outcomes must include exactly one result for every team in the season.";
    }
}
