#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;
using UnityEditor;
using CCAS.Backend;
using CCAS.Config;

/// <summary>
/// Lightweight, dependency-free unit test harness for Progression's services.
/// Runs against the real ProgressionService/LocalSeasonBackend (no mocking framework
/// available in this project - there are no assembly definitions yet, so a proper
/// Unity Test Framework test assembly can't reference this code). Invoke via:
///   Unity.exe -batchmode -projectPath <path> -executeMethod ProgressionUnitTests.RunAll -quit -logFile <path>
/// Exits the Editor process with a non-zero code if any test fails, for CI/script use.
/// </summary>
public static class ProgressionUnitTests
{
    private const string TestPlayerId = "__unit_test_progression__";

    private static int _passed;
    private static int _failed;
    private static readonly List<string> _failures = new List<string>();

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
        _failures.Clear();

        Log("===== ProgressionUnitTests: starting =====");

        var progression = GetOrCreateProgressionService();
        var backend = GetOrCreateLocalSeasonBackend();

        // Always start from a clean slate for the test player.
        progression.ClearPlayerProgression(TestPlayerId);

        Test_NewPlayer_StartsAtZeroRookie(progression);
        Test_AddXp_IncreasesTotal(progression);
        Test_TierBoundaries_MatchConfig(progression);
        Test_AddXp_NonPositiveIsNoOp(progression);
        Test_AddXp_NullOrEmptyPlayerIdIsSafe(progression);
        Test_AddXp_DuplicateEventIdIsIdempotent(progression);
        Test_XpHistory_RecordsSourceAndAmount(progression);
        Test_GetAllTiers_MatchesConfig(progression);
        Test_ClearPlayerProgression_ResetsState(progression);

        Test_XpFormat_ToDisplay_RoundsToNearestWithoutFloatNoise();
        Test_AddXp_KeepsFractionalXp_AndAccumulatesWithoutDrift(progression);
        Test_FractionalXp_SurvivesReloadFromDisk(progression);
        Test_TierLookup_HandlesFractionalXpAndXpBeyondLastTier(progression);

        // CreateSeason runs first, matching real gameplay order: a season must be
        // created (which warms up LocalSeasonBackend's ProgressionService reference)
        // before any match simulation or season-end rewards can occur.
        Test_CreateSeason_InitializesPlayerProgression(backend, progression);
        Test_AwardSeasonRewards_PlacementXp(backend, progression);
        Test_SimulateWeek_AwardsMatchXpAndAdvancesWeek(backend, progression);
        Test_SimulateWeek_RejectsPastFinalWeek(backend);

        Test_XpRewardRules_AreReadFromConfig(progression);
        Test_SimulateWeek_GrantsOneTrainingEntryPerWeek(backend, progression);
        Test_AwardTrainingXp_IsIdempotentPerWeek(backend, progression);
        Test_FinalWeek_AwardsSeasonRewardOnce(backend, progression);
        Test_MidSeasonWeek_DoesNotAwardSeasonReward(backend, progression);
        Test_LastMatchProperties_IgnoreTrainingEntry(progression);
        Test_XpScreen_GroupsEntriesByWeek();

        // Leave no residue in mgi_state for the test player.
        progression.ClearPlayerProgression(TestPlayerId);

        Log($"===== ProgressionUnitTests: {_passed} passed, {_failed} failed =====");
        if (_failed > 0)
        {
            Log("FAILURES:\n - " + string.Join("\n - ", _failures));
        }

        return new TestSuiteRunResult(_passed, _failed, _failures);
    }

    // ---------------- ProgressionService ----------------

    private static void Test_NewPlayer_StartsAtZeroRookie(ProgressionService progression)
    {
        var state = progression.GetState(TestPlayerId, createIfMissing: true);
        AssertEqual(0, state.current_xp, "NewPlayer_StartsAtZeroRookie: current_xp");
        AssertEqual("rookie", state.current_tier, "NewPlayer_StartsAtZeroRookie: current_tier");
    }

    private static void Test_AddXp_IncreasesTotal(ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);
        progression.AddXp(TestPlayerId, 10, "unit_test", Guid.NewGuid().ToString());
        var state = progression.GetState(TestPlayerId, createIfMissing: false);
        AssertEqual(10, state.current_xp, "AddXp_IncreasesTotal");
    }

    private static void Test_TierBoundaries_MatchConfig(ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);

        // Walk up to exactly 49 -> still rookie
        progression.AddXp(TestPlayerId, 49, "unit_test", Guid.NewGuid().ToString());
        AssertEqual("rookie", progression.GetState(TestPlayerId).current_tier, "TierBoundaries: 49 XP is rookie");

        // Cross into pro at 50
        progression.AddXp(TestPlayerId, 1, "unit_test", Guid.NewGuid().ToString());
        AssertEqual("pro", progression.GetState(TestPlayerId).current_tier, "TierBoundaries: 50 XP is pro");

        // Up to 99 -> still pro
        progression.AddXp(TestPlayerId, 49, "unit_test", Guid.NewGuid().ToString());
        AssertEqual("pro", progression.GetState(TestPlayerId).current_tier, "TierBoundaries: 99 XP is pro");

        // Cross into all_star at 100
        progression.AddXp(TestPlayerId, 1, "unit_test", Guid.NewGuid().ToString());
        AssertEqual("all_star", progression.GetState(TestPlayerId).current_tier, "TierBoundaries: 100 XP is all_star");

        // Up to 149 -> still all_star
        progression.AddXp(TestPlayerId, 49, "unit_test", Guid.NewGuid().ToString());
        AssertEqual("all_star", progression.GetState(TestPlayerId).current_tier, "TierBoundaries: 149 XP is all_star");

        // Cross into legend at 150
        progression.AddXp(TestPlayerId, 1, "unit_test", Guid.NewGuid().ToString());
        AssertEqual("legend", progression.GetState(TestPlayerId).current_tier, "TierBoundaries: 150 XP is legend");
    }

    private static void Test_AddXp_NonPositiveIsNoOp(ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);
        progression.AddXp(TestPlayerId, 20, "unit_test", Guid.NewGuid().ToString());
        float before = progression.GetState(TestPlayerId).current_xp;

        progression.AddXp(TestPlayerId, 0, "unit_test", Guid.NewGuid().ToString());
        progression.AddXp(TestPlayerId, -5, "unit_test", Guid.NewGuid().ToString());

        float after = progression.GetState(TestPlayerId).current_xp;
        AssertEqual(before, after, "AddXp_NonPositiveIsNoOp");
    }

    private static void Test_AddXp_NullOrEmptyPlayerIdIsSafe(ProgressionService progression)
    {
        try
        {
            progression.AddXp(null, 10, "unit_test", Guid.NewGuid().ToString());
            progression.AddXp("", 10, "unit_test", Guid.NewGuid().ToString());
            Assert(true, "AddXp_NullOrEmptyPlayerIdIsSafe: no exception thrown");
        }
        catch (Exception ex)
        {
            Assert(false, $"AddXp_NullOrEmptyPlayerIdIsSafe: threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Test_AddXp_DuplicateEventIdIsIdempotent(ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);
        string eventId = Guid.NewGuid().ToString();

        progression.AddXp(TestPlayerId, 15, "unit_test", eventId);
        progression.AddXp(TestPlayerId, 15, "unit_test", eventId); // duplicate grant, same eventId

        AssertEqual(15, progression.GetState(TestPlayerId).current_xp, "AddXp_DuplicateEventIdIsIdempotent");
    }

    private static void Test_XpHistory_RecordsSourceAndAmount(ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);
        progression.AddXp(TestPlayerId, 7, "match_win", Guid.NewGuid().ToString());

        var state = progression.GetState(TestPlayerId);
        var entry = state.xp_history.LastOrDefault();

        Assert(entry != null, "XpHistory_RecordsSourceAndAmount: entry exists");
        if (entry != null)
        {
            // match_win picks up Facilities' baseline match multiplier, and the fraction is kept.
            float expectedGain = 7f * new FacilitiesService().GetProgressionXpMultiplier(TestPlayerId, "match_win");
            AssertEqual(expectedGain, entry.xp_gained, "XpHistory_RecordsSourceAndAmount: xp_gained", XpTolerance);
            AssertEqual("match_win", entry.source, "XpHistory_RecordsSourceAndAmount: source");
        }
    }

    private static void Test_GetAllTiers_MatchesConfig(ProgressionService progression)
    {
        var tiers = progression.GetAllTiers();
        AssertEqual(4, tiers.Count, "GetAllTiers_MatchesConfig: tier count");

        AssertTierMatches(tiers, "rookie", 0, 49);
        AssertTierMatches(tiers, "pro", 50, 99);
        AssertTierMatches(tiers, "all_star", 100, 149);
        AssertTierMatches(tiers, "legend", 150, 999);
    }

    private static void AssertTierMatches(Dictionary<string, TierData> tiers, string key, int expectedMin, int expectedMax)
    {
        if (!tiers.TryGetValue(key, out var tier))
        {
            Assert(false, $"GetAllTiers_MatchesConfig: '{key}' tier missing");
            return;
        }

        AssertEqual(expectedMin, tier.min_xp, $"GetAllTiers_MatchesConfig: {key} min_xp");
        AssertEqual(expectedMax, tier.max_xp, $"GetAllTiers_MatchesConfig: {key} max_xp");
    }

    private static void Test_ClearPlayerProgression_ResetsState(ProgressionService progression)
    {
        progression.AddXp(TestPlayerId, 30, "unit_test", Guid.NewGuid().ToString());
        progression.ClearPlayerProgression(TestPlayerId);

        var stateAfterClear = progression.GetState(TestPlayerId, createIfMissing: false);
        Assert(stateAfterClear == null, "ClearPlayerProgression_ResetsState: state absent after clear");

        var freshState = progression.GetState(TestPlayerId, createIfMissing: true);
        AssertEqual(0, freshState.current_xp, "ClearPlayerProgression_ResetsState: fresh state is 0 XP");
    }

    // ---------------- Fractional XP ----------------

    private static void Test_XpFormat_ToDisplay_RoundsToNearestWithoutFloatNoise()
    {
        // The spec's worked examples: the UI is the running internal total rounded to the
        // nearest whole number.
        AssertEqual(15, XpFormat.ToDisplay(15.4f), "XpFormat: example 1, match 1 (15.4) shows 15");
        AssertEqual(31, XpFormat.ToDisplay(30.8f), "XpFormat: example 1, match 2 (30.8) shows 31");
        AssertEqual(15, XpFormat.ToDisplay(15.2f), "XpFormat: example 2, match 1 (15.2) shows 15");
        AssertEqual(30, XpFormat.ToDisplay(30.4f), "XpFormat: example 2, match 2 (30.4) shows 30");
        AssertEqual(46, XpFormat.ToDisplay(45.6f), "XpFormat: example 2, match 3 (45.6) shows 46");

        // Rounding the total, not summing rounded gains: two 15.4 gains show 31, not 15 + 15.
        Assert(XpFormat.ToDisplay(15.4f + 15.4f) != XpFormat.ToDisplay(15.4f) * 2,
            "XpFormat: the total is rounded once (30.8 -> 31), not built from rounded gains (15 + 15 = 30)");

        AssertEqual(0, XpFormat.ToDisplay(0f), "XpFormat: zero displays as 0");
        AssertEqual(0, XpFormat.ToDisplay(0.4f), "XpFormat: below .5 rounds down");
        AssertEqual(103, XpFormat.ToDisplay(103f), "XpFormat: a whole number is unchanged");

        // .5 rounds up. Plain Math.Round would round half to even and show 30.5 as 30.
        AssertEqual(1, XpFormat.ToDisplay(0.5f), "XpFormat: 0.5 rounds up to 1");
        AssertEqual(31, XpFormat.ToDisplay(30.5f), "XpFormat: 30.5 rounds up to 31, not to the even 30");
        AssertEqual(32, XpFormat.ToDisplay(31.5f), "XpFormat: 31.5 rounds up to 32");

        // float math leaves noise: 100 * 1.08f is 108.0000076, and a value that is 30.5 up to
        // noise must still land on the .5 side of the boundary.
        AssertEqual(108, XpFormat.ToDisplay(108.0000076f), "XpFormat: float noise above a whole number does not change it");
        AssertEqual(103, XpFormat.ToDisplay(102.9999971f), "XpFormat: float noise below a whole number still reads as that number");
        AssertEqual(31, XpFormat.ToDisplay(30.4999998f), "XpFormat: 30.5 with float noise below it still rounds up");
        AssertEqual(30, XpFormat.ToDisplay(30.4994f), "XpFormat: a real value just under .5 rounds down");

        double stored = XpFormat.ToStorage(15f * 1.03f);
        Assert(Math.Abs(stored - 15.45) < 1e-9, $"XpFormat: ToStorage drops float noise (15 x 1.03 stores as {stored}, expected 15.45)");
    }

    private static void Test_AddXp_KeepsFractionalXp_AndAccumulatesWithoutDrift(ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);

        // match_win carries the level-1 Film Room's baseline multiplier from Facilities,
        // so a 1 XP grant is worth slightly more than 1. Rounding each grant used to
        // discard that every time.
        float multiplier = new FacilitiesService().GetProgressionXpMultiplier(TestPlayerId, "match_win");
        Assert(multiplier > 1f, "FractionalXp: match_win has a Facilities multiplier above 1 (precondition)");

        for (int i = 0; i < 10; i++)
            progression.AddXp(TestPlayerId, 1, "match_win", Guid.NewGuid().ToString());

        var state = progression.GetState(TestPlayerId);
        AssertEqual(10f * multiplier, state.current_xp, "FractionalXp: ten 1-XP grants total 10 x multiplier, not a rounded 10", XpTolerance);
        AssertEqual(multiplier, state.xp_history[0].xp_gained, "FractionalXp: each history entry keeps its fractional amount", XpTolerance);
    }

    private static void Test_FractionalXp_SurvivesReloadFromDisk(ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);
        progression.AddXp(TestPlayerId, 7, "match_win", Guid.NewGuid().ToString());

        var before = progression.GetState(TestPlayerId);
        float totalBefore = before.current_xp;
        float gainedBefore = before.xp_history[0].xp_gained;
        Assert(Mathf.Abs(totalBefore - Mathf.Round(totalBefore)) > 0.05f, $"FractionalXp reload: precondition - {totalBefore} XP is fractional");

        // Drop the in-memory cache so the next GetState has to read the files back.
        var cacheField = typeof(ProgressionService).GetField("_progressionCache", BindingFlags.NonPublic | BindingFlags.Instance);
        ((Dictionary<string, PlayerProgressionState>)cacheField.GetValue(progression)).Clear();

        var reloaded = progression.GetState(TestPlayerId, createIfMissing: false);
        Assert(reloaded != null, "FractionalXp reload: state is read back from disk");
        if (reloaded == null) return;

        AssertEqual(totalBefore, reloaded.current_xp, "FractionalXp reload: total XP keeps its fraction", XpTolerance);
        AssertEqual(gainedBefore, reloaded.xp_history[0].xp_gained, "FractionalXp reload: history entry keeps its fraction", XpTolerance);
    }

    private static void Test_TierLookup_HandlesFractionalXpAndXpBeyondLastTier(ProgressionService progression)
    {
        // The tier config uses whole-number ranges (rookie 0-49, pro 50-99, ...). Fractional
        // XP between one range's max and the next range's min (e.g. 99.5) used to match no
        // tier at all and fall back to rookie; so did any XP above the last range's max.
        AssertEqual("rookie", TierFor(progression, 49.5f), "TierLookup: 49.5 XP is rookie");
        AssertEqual("pro", TierFor(progression, 50f), "TierLookup: 50 XP is pro");
        AssertEqual("pro", TierFor(progression, 99.5f), "TierLookup: 99.5 XP is pro, not rookie");
        AssertEqual("all_star", TierFor(progression, 100f), "TierLookup: 100 XP is all_star");
        AssertEqual("all_star", TierFor(progression, 149.9f), "TierLookup: 149.9 XP is all_star, not rookie");
        AssertEqual("legend", TierFor(progression, 150f), "TierLookup: 150 XP is legend");
        AssertEqual("legend", TierFor(progression, 5000f), "TierLookup: XP above the last range's max stays legend");
    }

    private static string TierFor(ProgressionService progression, float xp)
    {
        var method = typeof(ProgressionService).GetMethod("CalculateTierForXp", BindingFlags.NonPublic | BindingFlags.Instance);
        return (string)method.Invoke(progression, new object[] { xp });
    }

    // ---------------- LocalSeasonBackend ----------------

    private static void Test_AwardSeasonRewards_PlacementXp(LocalSeasonBackend backend, ProgressionService progression)
    {
        AssertPlacementReward(backend, progression, placement: 1, baseXp: 50);
        AssertPlacementReward(backend, progression, placement: 2, baseXp: 30);
        AssertPlacementReward(backend, progression, placement: 3, baseXp: 15);
        AssertPlacementReward(backend, progression, placement: 4, baseXp: 0);
    }

    private static void AssertPlacementReward(LocalSeasonBackend backend, ProgressionService progression, int placement, int baseXp)
    {
        progression.ClearPlayerProgression(TestPlayerId);

        var season = BuildSeasonWithPlayerAtPlacement(placement);
        string result = null;
        string error = null;
        backend.AwardSeasonRewards(season, s => result = s, e => error = e);

        Assert(error == null, $"AwardSeasonRewards_PlacementXp: placement {placement} did not error ({error})");

        // AddXp applies Facilities' "season_reward" multiplier on top of the base reward
        // (this is the intended Facility-affected-XP behavior, not a bug) - compute the
        // expected value the same way production does rather than assuming a 1:1 mapping.
        // XP keeps its fractional part, so nothing is rounded here.
        float expectedXp = baseXp == 0
            ? 0f
            : baseXp * new FacilitiesService().GetProgressionXpMultiplier(TestPlayerId, "season_reward");

        float actualXp = progression.GetState(TestPlayerId, createIfMissing: false)?.current_xp ?? 0f;
        AssertEqual(expectedXp, actualXp, $"AwardSeasonRewards_PlacementXp: placement {placement} awards {expectedXp} XP (base {baseXp})");
    }

    private static SeasonSaveData BuildSeasonWithPlayerAtPlacement(int placement)
    {
        // 4 teams, points descending 300/200/100/0. Player team's points position
        // determines its placement (1st..4th) among all teams.
        var pointsByRank = new[] { 300, 200, 100, 0 };

        var season = new SeasonSaveData
        {
            season_id = Guid.NewGuid().ToString(),
            current_week = 5,
            total_weeks = 10,
            teams = new List<TeamSaveData>()
        };

        for (int rank = 1; rank <= 4; rank++)
        {
            bool isPlayer = rank == placement;
            season.teams.Add(new TeamSaveData
            {
                team_id = Guid.NewGuid().ToString(),
                player_id = isPlayer ? TestPlayerId : Guid.NewGuid().ToString(),
                team_name = isPlayer ? "PlayerTeam" : $"AiTeam{rank}",
                rating = 1000,
                is_player_team = isPlayer,
                stats = new TeamStatsSaveData { points = pointsByRank[rank - 1] }
            });
        }

        return season;
    }

    private static void Test_CreateSeason_InitializesPlayerProgression(LocalSeasonBackend backend, ProgressionService progression)
    {
        SeasonSaveData created = null;
        string error = null;
        backend.CreateSeason(new List<string> { "Jets", "Hawks" }, "MyTeam", s => created = s, e => error = e);

        Assert(error == null, $"CreateSeason_InitializesPlayerProgression: no error ({error})");
        Assert(created != null, "CreateSeason_InitializesPlayerProgression: season returned");
        if (created != null)
        {
            AssertEqual(3, created.teams.Count, "CreateSeason_InitializesPlayerProgression: team count (2 AI + player)");

            var playerTeam = created.teams.FirstOrDefault(t => t.is_player_team);
            Assert(playerTeam != null, "CreateSeason_InitializesPlayerProgression: player team present");
            if (playerTeam != null)
            {
                var state = progression.GetState(playerTeam.player_id, createIfMissing: false);
                Assert(state != null, "CreateSeason_InitializesPlayerProgression: progression state created for player");
                if (state != null)
                {
                    AssertEqual(0, state.current_xp, "CreateSeason_InitializesPlayerProgression: starts at 0 XP");
                }

                // NOTE: CreateSeason hardcodes player_id = "local_player" (LocalSeasonBackend.cs:117),
                // not the canonical device-id convention the DM testing doc calls for. Cleaning up
                // whatever state this created so the test doesn't leave real "local_player" residue.
                progression.ClearPlayerProgression(playerTeam.player_id);
            }
        }
    }

    private static void Test_SimulateWeek_AwardsMatchXpAndAdvancesWeek(LocalSeasonBackend backend, ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);

        var season = new SeasonSaveData
        {
            season_id = Guid.NewGuid().ToString(),
            current_week = 0,
            total_weeks = 10,
            teams = new List<TeamSaveData>
            {
                new TeamSaveData
                {
                    team_id = Guid.NewGuid().ToString(),
                    player_id = TestPlayerId,
                    team_name = "PlayerTeam",
                    is_player_team = true,
                    stats = new TeamStatsSaveData()
                },
                new TeamSaveData
                {
                    team_id = Guid.NewGuid().ToString(),
                    player_id = Guid.NewGuid().ToString(),
                    team_name = "AiTeam",
                    is_player_team = false,
                    stats = new TeamStatsSaveData()
                }
            }
        };

        SeasonSaveData updated = null;
        string error = null;
        backend.SimulateWeek(season, s => updated = s, e => error = e);

        Assert(error == null, $"SimulateWeek_AwardsMatchXpAndAdvancesWeek: no error ({error})");
        Assert(updated != null, "SimulateWeek_AwardsMatchXpAndAdvancesWeek: season returned");
        if (updated != null)
        {
            AssertEqual(1, updated.current_week, "SimulateWeek_AwardsMatchXpAndAdvancesWeek: week advanced to 1");
        }

        float xpGained = progression.GetState(TestPlayerId, createIfMissing: false)?.current_xp ?? 0f;
        // Match played (5) + win (10) = 15, or match played (5) + loss (2) = 7 - outcome is
        // randomized. Facilities' match multiplier applies on top, and so does the weekly
        // training grant (2 XP x the Weight Room multiplier); the fraction is kept.
        var facilities = new FacilitiesService();
        float trainingXp = 2f * facilities.GetProgressionXpMultiplier(TestPlayerId, "training");
        float winXp = 15f * facilities.GetProgressionXpMultiplier(TestPlayerId, "match_win") + trainingXp;
        float lossXp = 7f * facilities.GetProgressionXpMultiplier(TestPlayerId, "match_loss") + trainingXp;
        Assert(Mathf.Abs(xpGained - winXp) < 0.001f || Mathf.Abs(xpGained - lossXp) < 0.001f,
            $"SimulateWeek_AwardsMatchXpAndAdvancesWeek: XP is {winXp} (win + training) or {lossXp} (loss + training), got {xpGained}");
    }

    // ---------------- XP decisions: config rules, weekly training, season-end reward ----------------

    private static void Test_XpRewardRules_AreReadFromConfig(ProgressionService progression)
    {
        var rules = progression.GetXpRewardRules();
        Assert(rules != null, "XpRewardRules: loaded from progression.json");
        if (rules == null) return;

        AssertEqual(5, rules.match_played, "XpRewardRules: match played");
        AssertEqual(10, rules.win, "XpRewardRules: win bonus");
        AssertEqual(2, rules.loss, "XpRewardRules: loss bonus");
        AssertEqual(2, rules.training_per_week, "XpRewardRules: weekly training XP");
        AssertEqual(15, rules.MatchXp(true), "XpRewardRules: a win is worth 15 base XP");
        AssertEqual(7, rules.MatchXp(false), "XpRewardRules: a loss is worth 7 base XP");
        AssertEqual(50, rules.SeasonRewardForPlacement(1), "XpRewardRules: 1st place");
        AssertEqual(30, rules.SeasonRewardForPlacement(2), "XpRewardRules: 2nd place");
        AssertEqual(15, rules.SeasonRewardForPlacement(3), "XpRewardRules: 3rd place");
        AssertEqual(0, rules.SeasonRewardForPlacement(4), "XpRewardRules: nothing below 3rd");
    }

    private static SeasonSaveData BuildTwoTeamSeason(int currentWeek, int totalWeeks)
    {
        return new SeasonSaveData
        {
            season_id = Guid.NewGuid().ToString(),
            current_week = currentWeek,
            total_weeks = totalWeeks,
            teams = new List<TeamSaveData>
            {
                new TeamSaveData { team_id = Guid.NewGuid().ToString(), player_id = TestPlayerId, team_name = "PlayerTeam", is_player_team = true, stats = new TeamStatsSaveData() },
                new TeamSaveData { team_id = Guid.NewGuid().ToString(), player_id = Guid.NewGuid().ToString(), team_name = "AiTeam", is_player_team = false, stats = new TeamStatsSaveData() }
            }
        };
    }

    private static void Test_SimulateWeek_GrantsOneTrainingEntryPerWeek(LocalSeasonBackend backend, ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);
        var season = BuildTwoTeamSeason(currentWeek: 0, totalWeeks: 10);

        string error = null;
        backend.SimulateWeek(season, s => { }, e => error = e);
        backend.SimulateWeek(season, s => { }, e => error = e);
        Assert(error == null, $"WeeklyTraining: two weeks simulate without error ({error})");

        var history = progression.GetState(TestPlayerId, createIfMissing: false).xp_history;
        var training = history.Where(e => e.source == "training").ToList();
        AssertEqual(2, training.Count, "WeeklyTraining: exactly one training grant per simulated week");
        AssertEqual(2, history.Count(e => e.source.StartsWith("match_")), "WeeklyTraining: one match grant per week alongside it");

        float expected = 2f * new FacilitiesService().GetProgressionXpMultiplier(TestPlayerId, "training");
        Assert(training.All(e => Mathf.Abs(e.xp_gained - expected) < XpTolerance),
            $"WeeklyTraining: each grant is 2 XP x the Weight Room multiplier ({expected})");
        Assert(training.All(e => e.id.EndsWith(":training")), "WeeklyTraining: grant ids are per week and per source");
        Assert(history.Select(e => e.source).SequenceEqual(new[] { history[0].source, "training", history[2].source, "training" })
               && history[0].source.StartsWith("match_") && history[2].source.StartsWith("match_"),
            "WeeklyTraining: each week's training grant follows its match");
    }

    private static void Test_AwardTrainingXp_IsIdempotentPerWeek(LocalSeasonBackend backend, ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);
        var award = typeof(LocalSeasonBackend).GetMethod("AwardTrainingXp", BindingFlags.NonPublic | BindingFlags.Instance);
        string seasonId = Guid.NewGuid().ToString();

        award.Invoke(backend, new object[] { TestPlayerId, seasonId, 3 });
        award.Invoke(backend, new object[] { TestPlayerId, seasonId, 3 });
        AssertEqual(1, progression.GetState(TestPlayerId).xp_history.Count(e => e.source == "training"),
            "WeeklyTraining: the same week's grant cannot be applied twice");
    }

    // Deterministic standings: wins drive points (SimulateWeek recomputes points = wins x 2), so
    // seed wins as well, with ranks 100 points apart so one random week cannot reorder them.
    private static SeasonSaveData BuildSeasonOneWeekFromEnd(int playerPlacement)
    {
        var season = BuildSeasonWithPlayerAtPlacement(playerPlacement);
        season.current_week = season.total_weeks - 1;
        foreach (var team in season.teams) team.stats.wins = team.stats.points / 2;
        return season;
    }

    private static void Test_FinalWeek_AwardsSeasonRewardOnce(LocalSeasonBackend backend, ProgressionService progression)
    {
        var facilities = new FacilitiesService();
        float seasonMultiplier = facilities.GetProgressionXpMultiplier(TestPlayerId, "season_reward");

        foreach (var (placement, baseXp) in new[] { (1, 50), (2, 30), (3, 15) })
        {
            progression.ClearPlayerProgression(TestPlayerId);
            var season = BuildSeasonOneWeekFromEnd(placement);
            season.teams.First(t => t.is_player_team).player_id = TestPlayerId;

            backend.SimulateWeek(season, s => { }, e => { });
            AssertEqual(season.total_weeks, season.current_week, $"SeasonEnd {placement}: the final week was simulated");

            var rewards = progression.GetState(TestPlayerId).xp_history.Where(e => e.source == "season_reward").ToList();
            AssertEqual(1, rewards.Count, $"SeasonEnd {placement}: the season reward is awarded when the final week is simulated");
            if (rewards.Count == 1)
            {
                AssertEqual(baseXp * seasonMultiplier, rewards[0].xp_gained, $"SeasonEnd {placement}: reward is {baseXp} base XP x the season multiplier", XpTolerance);
                AssertEqual($"{season.season_id}:season_reward", rewards[0].id, $"SeasonEnd {placement}: the grant id is derived from the season");
            }

            backend.AwardSeasonRewards(season, s => { }, e => { });
            AssertEqual(1, progression.GetState(TestPlayerId).xp_history.Count(e => e.source == "season_reward"),
                $"SeasonEnd {placement}: calling the reward again cannot pay out twice");
        }

        progression.ClearPlayerProgression(TestPlayerId);
        var lowSeason = BuildSeasonOneWeekFromEnd(4);
        lowSeason.teams.First(t => t.is_player_team).player_id = TestPlayerId;
        backend.SimulateWeek(lowSeason, s => { }, e => { });
        AssertEqual(0, progression.GetState(TestPlayerId).xp_history.Count(e => e.source == "season_reward"),
            "SeasonEnd 4th: no reward below 3rd place");
    }

    private static void Test_MidSeasonWeek_DoesNotAwardSeasonReward(LocalSeasonBackend backend, ProgressionService progression)
    {
        progression.ClearPlayerProgression(TestPlayerId);
        var season = BuildSeasonWithPlayerAtPlacement(1);
        season.current_week = 4;
        foreach (var team in season.teams) team.stats.wins = team.stats.points / 2;

        backend.SimulateWeek(season, s => { }, e => { });
        AssertEqual(0, progression.GetState(TestPlayerId).xp_history.Count(e => e.source == "season_reward"),
            "SeasonEnd: no season reward before the final week");
    }

    private static void Test_LastMatchProperties_IgnoreTrainingEntry(ProgressionService progression)
    {
        var go = new GameObject("SeasonManager_TestHarness");
        try
        {
            var manager = go.AddComponent<SeasonManager>();
            ForceAwake(manager);
            typeof(SeasonManager).GetField("progressionService", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(manager, progression);
            typeof(SeasonManager).GetField("seasonData", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(manager, BuildTwoTeamSeason(1, 10));

            // A win followed by its training grant: the result screen must still read "win" and
            // show the week's total, not the 2-XP training entry that is now the last entry.
            progression.ClearPlayerProgression(TestPlayerId);
            progression.AddXp(TestPlayerId, 15, "match_win", Guid.NewGuid().ToString());
            progression.AddXp(TestPlayerId, 2, "training", Guid.NewGuid().ToString());
            var history = progression.GetState(TestPlayerId).xp_history;
            Assert(manager.WasLastMatchWin, "LastMatch: a win followed by training still reads as a win");
            AssertEqual("match_win", manager.LastMatchXpSource, "LastMatch: the source is the match, not training");
            AssertEqual(XpFormat.ToDisplay(history[0].xp_gained + history[1].xp_gained), manager.LastXpGained, "LastMatch: XP gained is the week's match + training");

            progression.ClearPlayerProgression(TestPlayerId);
            progression.AddXp(TestPlayerId, 7, "match_loss", Guid.NewGuid().ToString());
            progression.AddXp(TestPlayerId, 2, "training", Guid.NewGuid().ToString());
            Assert(!manager.WasLastMatchWin, "LastMatch: a loss followed by training reads as a loss");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }

        var entries = new List<XpHistoryEntry>
        {
            new XpHistoryEntry("p", 9f, "duplicate_card_rare"),
            new XpHistoryEntry("p", 15f, "match_win"),
            new XpHistoryEntry("p", 2f, "training"),
            new XpHistoryEntry("p", 9f, "duplicate_card_rare")
        };
        AssertEqual(1, SeasonManager.LastMatchIndex(entries), "LastMatch: finds the latest match entry among other sources");
        AssertEqual(17f, SeasonManager.LastWeekXp(entries), "LastMatch: the week total is the match + its training, excluding later duplicates", XpTolerance);
        AssertEqual(-1, SeasonManager.LastMatchIndex(new List<XpHistoryEntry>()), "LastMatch: no entries means no match");
        AssertEqual(0f, SeasonManager.LastWeekXp(new List<XpHistoryEntry>()), "LastMatch: no match means no week XP", XpTolerance);
    }

    private static void Test_XpScreen_GroupsEntriesByWeek()
    {
        XpHistoryEntry E(float xp, string source) => new XpHistoryEntry("p", xp, source);

        // Two weeks of match + training, a CCAS duplicate in week 2, then a third match.
        var entries = new List<XpHistoryEntry>
        {
            E(15, "match_win"), E(2, "training"),
            E(7, "match_loss"), E(2, "training"), E(25, "duplicate_card_rare"),
            E(15, "match_win")
        };
        Assert(XPUI.WeekNumbers(entries).SequenceEqual(new[] { 1, 1, 2, 2, 2, 3 }),
            "XpScreen: weeks come from matches, not from row position (6 entries span 3 weeks, not 6)");

        var totals = XPUI.TotalsByWeek(entries);
        AssertEqual(3, totals.Count, "XpScreen: the chart has one bar per week");
        AssertEqual(17f, totals[0].Value, "XpScreen: week 1 total is match + training", XpTolerance);
        AssertEqual(34f, totals[1].Value, "XpScreen: week 2 total includes its training and the duplicate opened that week", XpTolerance);
        AssertEqual(15f, totals[2].Value, "XpScreen: week 3 total", XpTolerance);

        Assert(XPUI.WeekNumbers(new List<XpHistoryEntry> { E(25, "duplicate_card_rare"), E(15, "match_win") }).SequenceEqual(new[] { 1, 1 }),
            "XpScreen: an entry before the first match counts as week 1");
    }

    private static void Test_SimulateWeek_RejectsPastFinalWeek(LocalSeasonBackend backend)
    {
        var season = new SeasonSaveData
        {
            season_id = Guid.NewGuid().ToString(),
            current_week = 10,
            total_weeks = 10,
            teams = new List<TeamSaveData>()
        };

        SeasonSaveData updated = null;
        string error = null;
        backend.SimulateWeek(season, s => updated = s, e => error = e);

        Assert(updated == null, "SimulateWeek_RejectsPastFinalWeek: no success callback fired");
        Assert(error != null, "SimulateWeek_RejectsPastFinalWeek: error callback fired");
    }

    // ---------------- Facilities integration ----------------

    private const string FacilitiesTestPlayerId = "__integration_test_facilities__";
    private const string FacilitiesTestPlayerId_NoFunds = "__integration_test_facilities_nofunds__";

    /// <summary>
    /// Integration test: Facilities -> Progression (XP multipliers) and Economy -> Facilities
    /// (upgrade cost gating). Exercises the real FacilitiesService/EconomyService/ProgressionService
    /// together - no mocking - via Unity's command-line batch mode:
    ///   Unity.exe -batchmode -projectPath &lt;path&gt; -executeMethod ProgressionUnitTests.RunFacilitiesIntegration -quit -logFile &lt;path&gt;
    /// </summary>
    public static void RunFacilitiesIntegration()
    {
        bool passed = RunFacilitiesSuite().Passed;
        if (Application.isBatchMode)
            EditorApplication.Exit(passed ? 0 : 1);
    }

    public static TestSuiteRunResult RunFacilitiesSuite()
    {
        _passed = 0;
        _failed = 0;
        _failures.Clear();

        Log("===== ProgressionUnitTests.RunFacilitiesIntegration: starting =====");

        var progression = GetOrCreateProgressionService();
        var facilities = new FacilitiesService();
        var economy = new EconomyService();

        // Clean slate for both test players across all three subsystems.
        ResetFacilitiesIntegrationState(progression, facilities, economy, FacilitiesTestPlayerId);
        ResetFacilitiesIntegrationState(progression, facilities, economy, FacilitiesTestPlayerId_NoFunds);

        Test_Baseline_Level1FilmRoom_AppliesDefaultMatchMultiplier(progression, facilities);
        Test_UpgradeFilmRoom_IncreasesMatchXpMultiplier(progression, facilities, economy);
        Test_UpgradeWeightRoom_AppliesTrainingMultiplier(progression, facilities, economy);
        Test_UpgradeRehabCenter_AppliesRecoveryMultiplier(progression, facilities, economy);
        Test_DuplicateCardSource_ExemptFromFacilityMultiplier(progression, facilities);
        Test_UpgradeFacility_InsufficientFunds_Rejected(facilities, economy);

        ResetFacilitiesIntegrationState(progression, facilities, economy, FacilitiesTestPlayerId);
        ResetFacilitiesIntegrationState(progression, facilities, economy, FacilitiesTestPlayerId_NoFunds);

        Log($"===== RunFacilitiesIntegration: {_passed} passed, {_failed} failed =====");
        if (_failed > 0)
        {
            Log("FAILURES:\n - " + string.Join("\n - ", _failures));
        }

        return new TestSuiteRunResult(_passed, _failed, _failures);
    }

    private static void ResetFacilitiesIntegrationState(
        ProgressionService progression, FacilitiesService facilities, EconomyService economy, string playerId)
    {
        progression.ClearPlayerProgression(playerId);
        facilities.ResetFacilityState(playerId);
        economy.ResetWallet(playerId);
    }

    private static void Test_Baseline_Level1FilmRoom_AppliesDefaultMatchMultiplier(
        ProgressionService progression, FacilitiesService facilities)
    {
        // A fresh player defaults to level-1 Film Room, which already carries a passive
        // +3% PlayerIntelligenceBoost (facilities_config.json). Baseline is 1.03x, not 1.0x -
        // this test locks that in so it's visible if the config or calc ever drifts.
        float multiplier = facilities.GetProgressionXpMultiplier(FacilitiesTestPlayerId, "match_win");
        AssertEqual(1.03f, multiplier, "Baseline: level-1 Film Room match multiplier is 1.03x");

        progression.AddXp(FacilitiesTestPlayerId, 100, "match_win", Guid.NewGuid().ToString());
        float xp = progression.GetState(FacilitiesTestPlayerId).current_xp;
        AssertEqual(103f, xp, "Baseline: 100 base XP becomes 103 at level-1 Film Room", XpTolerance);
    }

    private static void Test_UpgradeFilmRoom_IncreasesMatchXpMultiplier(
        ProgressionService progression, FacilitiesService facilities, EconomyService economy)
    {
        economy.AddCurrency(FacilitiesTestPlayerId, 50000, 0, "integration_test_seed");

        bool upgraded = facilities.TryUpgradeFacility(FacilitiesTestPlayerId, "film_room", out var newState);
        Assert(upgraded, "UpgradeFilmRoom: TryUpgradeFacility succeeds with sufficient funds");
        AssertEqual(2, newState?.level ?? -1, "UpgradeFilmRoom: facility reaches level 2");

        float multiplier = facilities.GetProgressionXpMultiplier(FacilitiesTestPlayerId, "match_win");
        AssertEqual(1.08f, multiplier, "UpgradeFilmRoom: level-2 match multiplier is 1.08x");

        progression.ClearPlayerProgression(FacilitiesTestPlayerId);
        progression.AddXp(FacilitiesTestPlayerId, 100, "match_win", Guid.NewGuid().ToString());
        float xp = progression.GetState(FacilitiesTestPlayerId).current_xp;
        AssertEqual(108f, xp, "UpgradeFilmRoom: 100 base XP becomes 108 at level-2 Film Room", XpTolerance);
    }

    private static void Test_UpgradeWeightRoom_AppliesTrainingMultiplier(
        ProgressionService progression, FacilitiesService facilities, EconomyService economy)
    {
        economy.AddCurrency(FacilitiesTestPlayerId, 50000, 0, "integration_test_seed");

        bool upgraded = facilities.TryUpgradeFacility(FacilitiesTestPlayerId, "weight_room", out var newState);
        Assert(upgraded, "UpgradeWeightRoom: TryUpgradeFacility succeeds with sufficient funds");
        AssertEqual(2, newState?.level ?? -1, "UpgradeWeightRoom: facility reaches level 2");

        float multiplier = facilities.GetProgressionXpMultiplier(FacilitiesTestPlayerId, "training_session");
        AssertEqual(1.15f, multiplier, "UpgradeWeightRoom: level-2 training multiplier is 1.15x");

        progression.ClearPlayerProgression(FacilitiesTestPlayerId);
        progression.AddXp(FacilitiesTestPlayerId, 100, "training_session", Guid.NewGuid().ToString());
        float xp = progression.GetState(FacilitiesTestPlayerId).current_xp;
        AssertEqual(115f, xp, "UpgradeWeightRoom: 100 base XP becomes 115 at level-2 Weight Room", XpTolerance);
    }

    private static void Test_UpgradeRehabCenter_AppliesRecoveryMultiplier(
        ProgressionService progression, FacilitiesService facilities, EconomyService economy)
    {
        economy.AddCurrency(FacilitiesTestPlayerId, 50000, 0, "integration_test_seed");

        bool upgraded = facilities.TryUpgradeFacility(FacilitiesTestPlayerId, "rehab_center", out var newState);
        Assert(upgraded, "UpgradeRehabCenter: TryUpgradeFacility succeeds with sufficient funds");
        AssertEqual(2, newState?.level ?? -1, "UpgradeRehabCenter: facility reaches level 2");

        float multiplier = facilities.GetProgressionXpMultiplier(FacilitiesTestPlayerId, "recovery_bonus");
        AssertEqual(1.17f, multiplier, "UpgradeRehabCenter: level-2 recovery multiplier is 1.17x");

        progression.ClearPlayerProgression(FacilitiesTestPlayerId);
        progression.AddXp(FacilitiesTestPlayerId, 100, "recovery_bonus", Guid.NewGuid().ToString());
        float xp = progression.GetState(FacilitiesTestPlayerId).current_xp;
        AssertEqual(117f, xp, "UpgradeRehabCenter: 100 base XP becomes 117 at level-2 Rehab Center", XpTolerance);
    }

    private static void Test_DuplicateCardSource_ExemptFromFacilityMultiplier(
        ProgressionService progression, FacilitiesService facilities)
    {
        // Film Room is still at level 2 from the earlier test (1.08x for match sources),
        // but duplicate_card sources must always be exempted regardless of facility level.
        float multiplier = facilities.GetProgressionXpMultiplier(FacilitiesTestPlayerId, "duplicate_card_common");
        AssertEqual(1f, multiplier, "DuplicateCardSource: multiplier stays 1.0x despite upgraded facilities");

        progression.ClearPlayerProgression(FacilitiesTestPlayerId);
        progression.AddXp(FacilitiesTestPlayerId, 100, "duplicate_card_common", Guid.NewGuid().ToString());
        float xp = progression.GetState(FacilitiesTestPlayerId).current_xp;
        AssertEqual(100f, xp, "DuplicateCardSource: 100 base XP stays 100, unaffected by Facilities", XpTolerance);
    }

    private static void Test_UpgradeFacility_InsufficientFunds_Rejected(
        FacilitiesService facilities, EconomyService economy)
    {
        // Deliberately no AddCurrency call - this player has 0 coins.
        bool upgraded = facilities.TryUpgradeFacility(FacilitiesTestPlayerId_NoFunds, "film_room", out var state);

        Assert(!upgraded, "InsufficientFunds: TryUpgradeFacility fails with 0 coins");
        AssertEqual(1, state?.level ?? -1, "InsufficientFunds: facility stays at level 1");

        float multiplier = facilities.GetProgressionXpMultiplier(FacilitiesTestPlayerId_NoFunds, "match_win");
        AssertEqual(1.03f, multiplier, "InsufficientFunds: multiplier unchanged at baseline (level-1)");
    }

    // ---------------- CCAS integration ----------------

    private const string CcasTestPlayerId = "__integration_test_ccas__";
    private const string CcasTestPlayerId_NoFunds = "__integration_test_ccas_nofunds__";
    private const string BronzePackId = "bronze_pack";

    /// <summary>
    /// Integration test: CCAS -> Progression (duplicate-card XP) and CCAS -> Economy
    /// (pack purchase cost gating). Exercises the real CCASService/EconomyService/
    /// ProgressionService together - no mocking - via Unity's command-line batch mode:
    ///   Unity.exe -batchmode -projectPath &lt;path&gt; -executeMethod ProgressionUnitTests.RunCcasIntegration -quit -logFile &lt;path&gt;
    /// </summary>
    public static void RunCcasIntegration()
    {
        bool passed = RunCcasSuite().Passed;
        if (Application.isBatchMode)
            EditorApplication.Exit(passed ? 0 : 1);
    }

    public static TestSuiteRunResult RunCcasSuite()
    {
        _passed = 0;
        _failed = 0;
        _failures.Clear();

        Log("===== ProgressionUnitTests.RunCcasIntegration: starting =====");

        var progression = GetOrCreateProgressionService();
        var ccas = GetOrCreateCCASService();
        var dropConfig = GetOrCreateDropConfigManager();
        var catalog = GetOrCreateCardCatalogLoader();
        var economy = new EconomyService();

        ResetCcasIntegrationState(progression, economy, CcasTestPlayerId);
        ResetCcasIntegrationState(progression, economy, CcasTestPlayerId_NoFunds);

        Test_OpenPack_InsufficientFunds_Rejected(ccas, economy);
        Test_OpenPack_SpendsCoinsViaEconomy_NoDuplicatesOnFreshCollection(progression, ccas, economy, dropConfig);
        Test_OpenPack_AllDuplicates_AwardsConfigDrivenXp(progression, ccas, economy, catalog, dropConfig);
        Test_OpenPack_SecondDuplicateBatch_XpAccumulatesFurther(progression, ccas, dropConfig);
        Test_DuplicateCardXp_EventIdIsIdempotent(progression);

        ResetCcasIntegrationState(progression, economy, CcasTestPlayerId);
        ResetCcasIntegrationState(progression, economy, CcasTestPlayerId_NoFunds);

        Log($"===== RunCcasIntegration: {_passed} passed, {_failed} failed =====");
        if (_failed > 0)
        {
            Log("FAILURES:\n - " + string.Join("\n - ", _failures));
        }

        return new TestSuiteRunResult(_passed, _failed, _failures);
    }

    private static void ResetCcasIntegrationState(ProgressionService progression, EconomyService economy, string playerId)
    {
        progression.ClearPlayerProgression(playerId);
        economy.ResetWallet(playerId);
        DeleteCcasFileIfExists(playerId, "card_collection.json");
        DeleteCcasFileIfExists(playerId, "pack_drop_history.json");
    }

    private static void DeleteCcasFileIfExists(string playerId, string fileName)
    {
        var path = FilePathResolver.GetCCASPath(playerId, fileName);
        if (File.Exists(path)) File.Delete(path);
        var backupPath = path + ".bak";
        if (File.Exists(backupPath)) File.Delete(backupPath);
    }

    private static void Test_OpenPack_InsufficientFunds_Rejected(CCASService ccas, EconomyService economy)
    {
        // Deliberately no AddCurrency call - this player has 0 coins.
        var result = ccas.OpenPack(CcasTestPlayerId_NoFunds, BronzePackId);

        Assert(!result.success, "InsufficientFunds: OpenPack fails with 0 coins");
        AssertEqual("insufficient_funds", result.failureReason, "InsufficientFunds: failure reason");
        AssertEqual(0, result.cards?.Count ?? 0, "InsufficientFunds: no cards rolled");

        int coins = economy.GetWallet(CcasTestPlayerId_NoFunds).coins;
        AssertEqual(0, coins, "InsufficientFunds: wallet untouched at 0 coins");

        int collectionCount = ccas.GetCollection(CcasTestPlayerId_NoFunds).Count();
        AssertEqual(0, collectionCount, "InsufficientFunds: no collection entries created");
    }

    private static void Test_OpenPack_SpendsCoinsViaEconomy_NoDuplicatesOnFreshCollection(
        ProgressionService progression, CCASService ccas, EconomyService economy, DropConfigManager dropConfig)
    {
        economy.AddCurrency(CcasTestPlayerId, 5000, 0, "integration_test_seed");
        int before = economy.GetWallet(CcasTestPlayerId).coins;
        int cost = dropConfig.config.pack_types[BronzePackId].cost;

        var result = ccas.OpenPack(CcasTestPlayerId, BronzePackId);

        Assert(result.success, "SpendsCoinsViaEconomy: OpenPack succeeds with sufficient funds");
        AssertEqual(cost, result.costPaid, "SpendsCoinsViaEconomy: costPaid matches config");

        int after = economy.GetWallet(CcasTestPlayerId).coins;
        AssertEqual(before - cost, after, "SpendsCoinsViaEconomy: wallet debited by pack cost");

        // This player's collection was empty before this call, so nothing pulled can
        // possibly already be owned - isDuplicate must be false for every card, and no
        // duplicate XP can have been awarded to Progression as a result.
        Assert(result.cardDetails.Count > 0, "SpendsCoinsViaEconomy: cards were pulled");
        Assert(result.cardDetails.All(c => !c.isDuplicate), "SpendsCoinsViaEconomy: first-ever pack has no duplicates");

        float xp = progression.GetState(CcasTestPlayerId, createIfMissing: false)?.current_xp ?? 0f;
        AssertEqual(0f, xp, "SpendsCoinsViaEconomy: no XP awarded on first pack (no duplicates possible)");
    }

    private static void Test_OpenPack_AllDuplicates_AwardsConfigDrivenXp(
        ProgressionService progression, CCASService ccas, EconomyService economy, CardCatalogLoader catalog, DropConfigManager dropConfig)
    {
        progression.ClearPlayerProgression(CcasTestPlayerId);
        economy.AddCurrency(CcasTestPlayerId, 5000, 0, "integration_test_seed");

        SeedFullOwnedCollection(CcasTestPlayerId, catalog, dropConfig, BronzePackId);
        int poolSize = DuplicateEligiblePoolSize(catalog, dropConfig, BronzePackId);
        AssertEqual(poolSize, TotalCollectionQuantity(ccas, CcasTestPlayerId), "AllDuplicates: seeded collection covers the full common/uncommon/rare pool");

        var result = ccas.OpenPack(CcasTestPlayerId, BronzePackId);
        Assert(result.success, "AllDuplicates: OpenPack succeeds");
        Assert(result.cardDetails.Count > 0, "AllDuplicates: cards were pulled");

        // Every card in the pool was pre-seeded as already owned, so every pull this
        // call makes - whichever cards they happen to be - must be flagged duplicate.
        Assert(result.cardDetails.All(c => c.isDuplicate), "AllDuplicates: every pulled card is flagged duplicate (pre-seeded universe)");

        var dxp = dropConfig.config.duplicate_xp;
        Assert(result.cardDetails.All(c => c.xpAwarded == GetExpectedDupXp(dxp, c.rarity)), "AllDuplicates: per-card xpAwarded matches config's duplicate_xp table");

        int expectedXp = result.cardDetails.Sum(c => GetExpectedDupXp(dxp, c.rarity));
        float actualXp = progression.GetState(CcasTestPlayerId, createIfMissing: false)?.current_xp ?? 0f;
        AssertEqual(expectedXp, actualXp, "AllDuplicates: ProgressionService XP total matches sum of duplicate awards");

        AssertEqual(poolSize + result.cardDetails.Count, TotalCollectionQuantity(ccas, CcasTestPlayerId), "AllDuplicates: collection quantities incremented by exactly the cards pulled");
    }

    private static void Test_OpenPack_SecondDuplicateBatch_XpAccumulatesFurther(
        ProgressionService progression, CCASService ccas, DropConfigManager dropConfig)
    {
        // Collection is still the full pre-seeded universe left over from the previous
        // test, so this second, independent OpenPack() call is again guaranteed
        // all-duplicates. Each pull gets its own eventId (ccas_pack_open:<new
        // packOpenId>:<cardId>), so unlike Test_DuplicateCardXp_EventIdIsIdempotent
        // below, this XP must be additional, not deduplicated against the first batch.
        int quantityBefore = TotalCollectionQuantity(ccas, CcasTestPlayerId);
        float xpBefore = progression.GetState(CcasTestPlayerId, createIfMissing: false)?.current_xp ?? 0f;

        var result = ccas.OpenPack(CcasTestPlayerId, BronzePackId);
        Assert(result.success, "SecondDuplicateBatch: OpenPack succeeds");
        Assert(result.cardDetails.Count > 0, "SecondDuplicateBatch: cards were pulled");
        Assert(result.cardDetails.All(c => c.isDuplicate), "SecondDuplicateBatch: still all duplicates (universe still fully owned)");

        var dxp = dropConfig.config.duplicate_xp;
        int batchXp = result.cardDetails.Sum(c => GetExpectedDupXp(dxp, c.rarity));

        float xpAfter = progression.GetState(CcasTestPlayerId, createIfMissing: false)?.current_xp ?? 0f;
        AssertEqual(xpBefore + batchXp, xpAfter, "SecondDuplicateBatch: XP accumulates on top of the first batch, not deduplicated across separate pack opens");

        AssertEqual(quantityBefore + result.cardDetails.Count, TotalCollectionQuantity(ccas, CcasTestPlayerId), "SecondDuplicateBatch: collection quantities keep accumulating across pack opens");
    }

    private static void Test_DuplicateCardXp_EventIdIsIdempotent(ProgressionService progression)
    {
        // Confirms ProgressionService.AddXp's idempotency (already covered generically by
        // Test_AddXp_DuplicateEventIdIsIdempotent) also holds for CCASService's actual
        // eventId format: "ccas_pack_open:{packOpenId}:{cardId}".
        progression.ClearPlayerProgression(CcasTestPlayerId);
        string eventId = $"ccas_pack_open:{Guid.NewGuid()}:HE1RB0001";

        progression.AddXp(CcasTestPlayerId, 25, "duplicate_card_rare", eventId);
        progression.AddXp(CcasTestPlayerId, 25, "duplicate_card_rare", eventId);

        AssertEqual(25, progression.GetState(CcasTestPlayerId).current_xp, "DuplicateCardXp_EventIdIsIdempotent");
    }

    private class CcasCollectionFileMirror
    {
        public List<CardCollectionEntry> entries = new();
    }

    /// <summary>
    /// Pre-seeds the player's persisted card collection with every catalog card whose
    /// rarity this pack type can roll, each at quantity 1. Guarantees the next
    /// OpenPack() call for this pack type is 100% duplicates regardless of which
    /// specific cards get randomly pulled - avoids relying on natural collision odds,
    /// which would otherwise make this test flaky/slow given the catalog's large
    /// per-tier card pool (checked cards_catalog.json directly rather than assume).
    /// </summary>
    private static void SeedFullOwnedCollection(string playerId, CardCatalogLoader catalog, DropConfigManager dropConfig, string packTypeId)
    {
        var tiers = RollableTiers(dropConfig, packTypeId);
        var nowIso = DateTime.UtcNow.ToString("o");

        var file = new CcasCollectionFileMirror
        {
            entries = catalog.catalog.cards
                .Where(c => tiers.Contains(c.cardTier))
                .Select(c => new CardCollectionEntry
                {
                    player_id = playerId,
                    card_id = c.uid,
                    quantity = 1,
                    first_acquired_at = nowIso
                })
                .ToList()
        };

        var path = FilePathResolver.GetCCASPath(playerId, "card_collection.json");
        File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.Indented));
    }

    private static HashSet<int> RollableTiers(DropConfigManager dropConfig, string packTypeId)
    {
        var rates = dropConfig.config.pack_types[packTypeId].drop_rates;
        var tiers = new HashSet<int>();
        if (rates.common > 0) tiers.Add(1);
        if (rates.uncommon > 0) tiers.Add(2);
        if (rates.rare > 0) tiers.Add(3);
        if (rates.epic > 0) tiers.Add(4);
        if (rates.legendary > 0) tiers.Add(5);
        return tiers;
    }

    private static int DuplicateEligiblePoolSize(CardCatalogLoader catalog, DropConfigManager dropConfig, string packTypeId)
    {
        var tiers = RollableTiers(dropConfig, packTypeId);
        return catalog.catalog.cards.Count(c => tiers.Contains(c.cardTier));
    }

    private static int TotalCollectionQuantity(CCASService ccas, string playerId) =>
        ccas.GetCollection(playerId).Sum(e => e.quantity);

    private static int GetExpectedDupXp(DuplicateXP dxp, string rarity)
    {
        return (rarity ?? "common").ToLowerInvariant() switch
        {
            "uncommon" => dxp.uncommon_duplicate_xp,
            "rare" => dxp.rare_duplicate_xp,
            "epic" => dxp.epic_duplicate_xp,
            "legendary" => dxp.legendary_duplicate_xp,
            _ => dxp.common_duplicate_xp
        };
    }

    // Tolerance for comparing XP values: float math leaves noise (100 x 1.08f is 108.0000076).
    private const float XpTolerance = 0.001f;

    // ---------------- Test infrastructure ----------------

    // In batchmode edit-mode (no Play Mode session), Unity does not reliably invoke
    // Awake() synchronously after AddComponent within a single -executeMethod call.
    // Real gameplay always runs Awake() before anything else touches these singletons,
    // so we force it here to match that guarantee rather than silently testing against
    // uninitialized singletons. Awake() on these types calls DontDestroyOnLoad, which
    // Unity forbids outside Play Mode and throws for - that exception aborts the rest
    // of Awake() (e.g. config loading), so we swallow it here and let callers finish
    // any remaining setup (see GetOrCreateProgressionService).
    private static void ForceAwake(MonoBehaviour target)
    {
        var method = target.GetType().GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance);
        try
        {
            method?.Invoke(target, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            // Expected: "DontDestroyOnLoad can only be used in play mode" - Instance
            // assignment (which happens before the DontDestroyOnLoad call in these
            // Awake() implementations) has already taken effect by this point.
        }
    }

    private static void ForcePrivateMethod(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        method?.Invoke(target, null);
    }

    private static ProgressionService GetOrCreateProgressionService()
    {
        var existing = ProgressionService.Instance;
        if (existing != null) return existing;

        var go = new GameObject("ProgressionService_TestHarness");
        var service = go.AddComponent<ProgressionService>();
        ForceAwake(service);
        // Awake() aborted before reaching LoadProgressionConfig() - finish it explicitly.
        ForcePrivateMethod(service, "LoadProgressionConfig");
        return service;
    }

    private static LocalSeasonBackend GetOrCreateLocalSeasonBackend()
    {
        var existing = LocalSeasonBackend.Instance;
        // LocalSeasonBackend.Instance's own getter already creates+returns one via
        // AddComponent, but Awake() may not have run synchronously (see ForceAwake).
        // Nothing follows DontDestroyOnLoad in its Awake(), so no further setup needed.
        ForceAwake(existing);
        return existing;
    }

    private static CCASService GetOrCreateCCASService()
    {
        var existing = CCASService.Instance;
        if (existing != null) return existing;

        var go = new GameObject("CCASService_TestHarness");
        var service = go.AddComponent<CCASService>();
        ForceAwake(service);
        return service;
    }

    private static DropConfigManager GetOrCreateDropConfigManager()
    {
        var existing = DropConfigManager.Instance;
        if (existing != null && existing.config != null) return existing;

        var mgr = existing != null ? existing : new GameObject("DropConfigManager_TestHarness").AddComponent<DropConfigManager>();
        ForceAwake(mgr);
        // Awake() aborted before reaching LoadConfig() - finish it explicitly (see ForceAwake).
        if (mgr.config == null)
            ForcePrivateMethod(mgr, "LoadConfig");
        return mgr;
    }

    private static CardCatalogLoader GetOrCreateCardCatalogLoader()
    {
        var existing = CardCatalogLoader.Instance;
        if (existing != null && existing.catalog != null) return existing;

        var loader = existing != null ? existing : new GameObject("CardCatalogLoader_TestHarness").AddComponent<CardCatalogLoader>();
        ForceAwake(loader);
        // Awake() aborted before reaching LoadCatalog() - finish it explicitly (see ForceAwake).
        if (loader.catalog == null)
            ForcePrivateMethod(loader, "LoadCatalog");
        return loader;
    }

    private static void Assert(bool condition, string testName)
    {
        if (condition)
        {
            _passed++;
            Log($"PASS: {testName}");
        }
        else
        {
            _failed++;
            _failures.Add(testName);
            Log($"FAIL: {testName}");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string testName)
    {
        bool equal = EqualityComparer<T>.Default.Equals(expected, actual);
        Assert(equal, equal ? testName : $"{testName} (expected {expected}, got {actual})");
    }

    // Float equality is unreliable bit-for-bit (a literal like 1.03f and a computed
    // 1f + 0.03f can differ at the ULP level) - compare with a small tolerance instead.
    private static void AssertEqual(float expected, float actual, string testName, float tolerance = 0.0001f)
    {
        bool equal = Mathf.Abs(expected - actual) <= tolerance;
        Assert(equal, equal ? testName : $"{testName} (expected {expected}, got {actual})");
    }

    private static void Log(string message)
    {
        Debug.Log($"[ProgressionUnitTests] {message}");
    }
}
#endif
