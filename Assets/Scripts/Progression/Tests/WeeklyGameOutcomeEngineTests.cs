#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Contract tests for the C# A4 weekly engine. The baseline values match S1 in
/// Simulator/tests/scenarios/coach_scenarios.json, the shared Python/C# scenario set.
/// </summary>
public static class WeeklyGameOutcomeEngineTests
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new List<string>();

    public static void RunAll()
    {
        bool passed = RunSuite().Passed;
        if (Application.isBatchMode)
            EditorApplication.Exit(passed ? 0 : 1);
    }

    public static TestSuiteRunResult RunSuite()
    {
        _passed = 0;
        _failed = 0;
        Failures.Clear();
        Debug.Log("===== WeeklyGameOutcomeEngineTests: starting =====");

        try
        {
            Test_S1BaselineStrengthAndProbability();
            Test_WeeklyOutputHasOneRowPerTeamAndIsSeeded();
            Test_RosterRejectsWrongPositionAndRepeatedCard();
        }
        catch (Exception exception)
        {
            Fail("Unhandled test exception: " + exception);
        }

        Debug.Log($"===== WeeklyGameOutcomeEngineTests: {_passed} passed, {_failed} failed =====");
        if (_failed > 0) Debug.LogError("FAILURES:\n - " + string.Join("\n - ", Failures));
        return new TestSuiteRunResult(_passed, _failed, Failures);
    }

    private static void Test_S1BaselineStrengthAndProbability()
    {
        var player = StarterTeam("player", true, 2, new[] { 1f, 1f, 1f, 1f });
        TeamStrengthSummary strength = WeeklyGameOutcomeEngine.CalculateStrength(player);

        AssertNear(37.1429f, strength.offence_rating, 0.0002f, "S1: offence rating matches shared scenario");
        AssertNear(40f, strength.defence_rating, 0.0002f, "S1: defence rating matches shared scenario");
        AssertNear(38.5714f, strength.special_teams_rating, 0.0002f, "S1: special-teams rating matches shared scenario");
        AssertNear(40.425f, strength.strength, 0.0002f, "S1: strength matches shared scenario");
        AssertNear(0.2820f, WeeklyGameOutcomeEngine.CalculateHomeWinProbability(strength.strength, 58f), 0.0005f,
            "S1: home probability matches shared scenario");
    }

    private static void Test_WeeklyOutputHasOneRowPerTeamAndIsSeeded()
    {
        var home = StarterTeam("home", true, 2, new[] { 1f, 1f, 1f, 1f });
        var away = StarterTeam("away", false, 1, null);
        var first = WeeklyGameOutcomeEngine.SimulateMatch(home, away, "season-1", 1, 2, 1234);
        var repeated = WeeklyGameOutcomeEngine.SimulateMatch(home, away, "season-1", 1, 2, 1234);

        AssertEqual(2, first.Count, "Weekly output: emits two team rows");
        Assert(first.Select(record => record.event_id).Distinct().Count() == 2,
            "Weekly output: each team row has a unique idempotency key");
        Assert(first.All(record => record.match_id == "season-1:W01:G02"),
            "Weekly output: rows share one stable match ID");
        Assert(first.Count(record => record.result == "W") == 1 && first.Count(record => record.result == "L") == 1,
            "Weekly output: exactly one team wins");
        Assert(first[0].result == repeated[0].result && first[1].result == repeated[1].result,
            "Weekly output: a fixed seed is reproducible");
        Assert(string.IsNullOrEmpty(typeof(WeeklyGameOutcomeRecord).GetField("coin_reward")?.Name),
            "Weekly output: Economy reward amount is not owned by the simulator");
    }

    private static void Test_RosterRejectsWrongPositionAndRepeatedCard()
    {
        var invalidPosition = StarterTeam("invalid-position", true, 2, null);
        invalidPosition.active_roster[0].position = "RB";
        AssertThrows(() => WeeklyGameOutcomeEngine.CalculateStrength(invalidPosition),
            "Roster validation: rejects a card in the wrong position slot");

        var repeatedCard = StarterTeam("repeated-card", true, 2, null);
        repeatedCard.active_roster[1].card_id = repeatedCard.active_roster[0].card_id;
        AssertThrows(() => WeeklyGameOutcomeEngine.CalculateStrength(repeatedCard),
            "Roster validation: rejects using the same owned card twice");
    }

    private static WeeklyTeamInput StarterTeam(string teamId, bool isPlayer, int nonQuarterbackTier, float[] coachStats)
    {
        string[] slots = { "qb", "rb", "ol1", "ol2", "ol3", "r1", "r2", "dl1", "dl2", "dl3", "db1", "db2", "s1", "s2" };
        var positions = new Dictionary<string, string>
        {
            { "qb", "QB" }, { "rb", "RB" }, { "ol1", "OL" }, { "ol2", "OL" }, { "ol3", "OL" },
            { "r1", "R" }, { "r2", "R" }, { "dl1", "DL" }, { "dl2", "DL" }, { "dl3", "DL" },
            { "db1", "DB" }, { "db2", "DB" }, { "s1", "S" }, { "s2", "S" }
        };

        return new WeeklyTeamInput
        {
            team_id = teamId,
            is_player_team = isPlayer,
            active_roster = slots.Select(slot => new ActiveRosterCard
            {
                slot_id = slot,
                card_id = teamId + "-" + slot,
                position = positions[slot],
                card_tier = slot == "qb" ? 1 : nonQuarterbackTier
            }).ToArray(),
            coaches = coachStats == null ? Array.Empty<ActiveCoach>() : new[]
            {
                new ActiveCoach { role = "O", coach_id = teamId + "-o", stats = coachStats },
                new ActiveCoach { role = "D", coach_id = teamId + "-d", stats = coachStats },
                new ActiveCoach { role = "ST", coach_id = teamId + "-st", stats = coachStats }
            }
        };
    }

    private static void AssertThrows(Action action, string name)
    {
        try { action(); Fail(name + " (no exception)"); }
        catch (ArgumentException) { Pass(name); }
    }

    private static void AssertNear(float expected, float actual, float tolerance, string name)
    {
        Assert(Math.Abs(expected - actual) <= tolerance, $"{name} (expected {expected}, got {actual})");
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"{name} (expected {expected}, got {actual})");
    }

    private static void Assert(bool condition, string name)
    {
        if (condition) Pass(name); else Fail(name);
    }

    private static void Pass(string name)
    {
        _passed++;
        Debug.Log("[WeeklyGameOutcomeEngineTests] PASS: " + name);
    }

    private static void Fail(string name)
    {
        _failed++;
        Failures.Add(name);
        Debug.LogError("[WeeklyGameOutcomeEngineTests] FAIL: " + name);
    }
}
#endif
