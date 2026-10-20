#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Verifies imported game results can never award a wallet reward twice.</summary>
public static class GameOutcomeRewardTests
{
    private const string TestPlayerId = "__game_outcome_reward_test__";
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
        Debug.Log("===== GameOutcomeRewardTests: starting =====");

        var economy = new EconomyService { PublishWalletUpdatedEvents = false };
        economy.ResetWallet(TestPlayerId);
        try
        {
            Test_ReplayingSameOutcomePaysOnce(economy);
            Test_EmptyEventIdIsRejected(economy);
        }
        catch (Exception exception)
        {
            Fail("Unhandled test exception: " + exception);
        }
        finally
        {
            economy.ResetWallet(TestPlayerId);
        }

        Debug.Log($"===== GameOutcomeRewardTests: {_passed} passed, {_failed} failed =====");
        if (_failed > 0)
            Debug.Log("FAILURES:\n - " + string.Join("\n - ", Failures));
        return new TestSuiteRunResult(_passed, _failed, Failures);
    }

    private static void Test_ReplayingSameOutcomePaysOnce(EconomyService economy)
    {
        var outcome = new GameOutcomeRecord
        {
            event_id = "season-1-week-1-player-game",
            coin_reward = 250
        };

        bool firstAccepted = GameOutcomeRewardProcessor.TryApplyCoinReward(economy, outcome, TestPlayerId, out var firstApplied);
        bool secondAccepted = GameOutcomeRewardProcessor.TryApplyCoinReward(economy, outcome, TestPlayerId, out var secondApplied);
        var wallet = economy.GetWallet(TestPlayerId, false);
        var transactions = economy.GetRecentTransactions(TestPlayerId, 20).ToList();

        Assert(firstAccepted && firstApplied, "First import: applies the outcome reward");
        Assert(secondAccepted && !secondApplied, "Replay: succeeds as a no-op instead of paying again");
        AssertEqual(250, wallet.coins, "Replay: wallet contains one reward only");
        AssertEqual(1, transactions.Count(transaction => transaction.event_id == outcome.event_id),
            "Replay: transaction ledger contains one event entry");
    }

    private static void Test_EmptyEventIdIsRejected(EconomyService economy)
    {
        var outcome = new GameOutcomeRecord { event_id = "", coin_reward = 20 };
        bool accepted = GameOutcomeRewardProcessor.TryApplyCoinReward(economy, outcome, TestPlayerId, out var applied);

        Assert(!accepted && !applied, "Invalid event: empty event id cannot add coins");
    }

    private static void Assert(bool condition, string message)
    {
        if (condition)
        {
            _passed++;
            Debug.Log("[GameOutcomeRewardTests] PASS: " + message);
            return;
        }

        _failed++;
        Failures.Add(message);
        Debug.LogError("[GameOutcomeRewardTests] FAIL: " + message);
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
        Debug.LogError("[GameOutcomeRewardTests] FAIL: " + message);
    }
}
#endif
