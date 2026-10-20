#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>File-contract checks for simulator output consumed by LocalSeasonBackend.</summary>
public static class GameOutcomeReaderTests
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
        Debug.Log("===== GameOutcomeReaderTests: starting =====");

        string path = Path.Combine(Path.GetTempPath(), $"mgi-game-outcomes-{Guid.NewGuid()}.json");
        try
        {
            Test_CompleteWeekLoads(path);
            Test_IncompleteWeekIsRejected(path);
            Test_NullFileUsesLocalFallback(path);
        }
        catch (Exception exception)
        {
            Fail("Unhandled test exception: " + exception);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        Debug.Log($"===== GameOutcomeReaderTests: {_passed} passed, {_failed} failed =====");
        if (_failed > 0)
            Debug.Log("FAILURES:\n - " + string.Join("\n - ", Failures));
        return new TestSuiteRunResult(_passed, _failed, Failures);
    }

    private static void Test_CompleteWeekLoads(string path)
    {
        WriteFile(path, new GameOutcomeFile
        {
            outcomes = new[]
            {
                Outcome("week-1-a", "season-1", 1, "team-a", "team-b", "team-a"),
                Outcome("week-1-b", "season-1", 1, "team-c", "team-d", "team-d")
            }
        });

        bool found = new GameOutcomeReader(path).TryGetWeek("season-1", 1,
            new[] { "team-a", "team-b", "team-c", "team-d" }, out var outcomes, out var error);

        Assert(found, "Complete week: loads valid simulator outcomes");
        AssertEqual(2, outcomes.Count, "Complete week: returns every match");
        Assert(string.IsNullOrEmpty(error), "Complete week: has no validation error");
    }

    private static void Test_IncompleteWeekIsRejected(string path)
    {
        WriteFile(path, new GameOutcomeFile
        {
            outcomes = new[] { Outcome("week-1-a", "season-1", 1, "team-a", "team-b", "team-a") }
        });

        bool found = new GameOutcomeReader(path).TryGetWeek("season-1", 1,
            new[] { "team-a", "team-b", "team-c", "team-d" }, out var outcomes, out var error);

        Assert(!found, "Incomplete week: does not apply partial results");
        AssertEqual(0, outcomes.Count, "Incomplete week: clears untrusted partial results");
        Assert(!string.IsNullOrEmpty(error), "Incomplete week: returns validation reason");
    }

    private static void Test_NullFileUsesLocalFallback(string path)
    {
        File.WriteAllText(path, "null");

        bool found = new GameOutcomeReader(path).TryGetWeek("season-1", 1,
            new[] { "team-a", "team-b" }, out var outcomes, out var error);

        Assert(!found, "Null file: signals no simulator output is available");
        AssertEqual(0, outcomes.Count, "Null file: returns no outcomes");
        Assert(string.IsNullOrEmpty(error), "Null file: allows normal local fallback without an error");
    }

    private static GameOutcomeRecord Outcome(string eventId, string seasonId, int week, string home, string away, string winner)
    {
        return new GameOutcomeRecord
        {
            event_id = eventId,
            season_id = seasonId,
            week = week,
            home_team_id = home,
            away_team_id = away,
            winner_team_id = winner
        };
    }

    private static void WriteFile(string path, GameOutcomeFile file)
    {
        File.WriteAllText(path, JsonUtility.ToJson(file));
    }

    private static void Assert(bool condition, string message)
    {
        if (condition)
        {
            _passed++;
            Debug.Log("[GameOutcomeReaderTests] PASS: " + message);
            return;
        }

        Fail(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        Assert(EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message} (expected: {expected}; actual: {actual})");
    }

    private static void Fail(string message)
    {
        _failed++;
        Failures.Add(message);
        Debug.LogError("[GameOutcomeReaderTests] FAIL: " + message);
    }
}
#endif
