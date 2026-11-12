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
        Test_LoadingSave_ReconcilesStaleTier(progression);

        // CreateSeason runs first, matching real gameplay order: a season must be
        // created (which warms up LocalSeasonBackend's ProgressionService reference)
        // before any match simulation or season-end rewards can occur.
        Test_CreateSeason_InitializesPlayerProgression(backend, progression);
        Test_CreateSeason_KeepsCareerXp(backend, progression);
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
        Test_XpScreen_WeekLabelsRestartEachSeason();

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

        // Walk up through every boundary the config declares: one XP short of a tier's min
        // is still the tier below, and exactly its min is the tier. No threshold is written
        // here, so changing progression.json needs no test edit.
        var tiers = SortedTiers(progression);
        AssertEqual("rookie", tiers[0].Key, "TierBoundaries: the lowest tier is rookie");
        AssertEqual(0, tiers[0].Value.min_xp, "TierBoundaries: rookie starts at 0 XP");

        int xp = 0;
        for (int i = 1; i < tiers.Count; i++)
        {
            string below = tiers[i - 1].Key;
            string tier = tiers[i].Key;
            int min = tiers[i].Value.min_xp;

            if (min - 1 > xp)
            {
                progression.AddXp(TestPlayerId, min - 1 - xp, "unit_test", Guid.NewGuid().ToString());
                xp = min - 1;
            }
            AssertEqual(below, progression.GetState(TestPlayerId).current_tier, $"TierBoundaries: {xp} XP is {below}");

            progression.AddXp(TestPlayerId, min - xp, "unit_test", Guid.NewGuid().ToString());
            xp = min;
            AssertEqual(tier, progression.GetState(TestPlayerId).current_tier, $"TierBoundaries: {xp} XP is {tier}");
        }
    }

    // The loaded tiers, lowest first.
    private static List<KeyValuePair<string, TierData>> SortedTiers(ProgressionService progression) =>
        progression.GetAllTiers().OrderBy(kvp => kvp.Value.min_xp).ToList();

    private static int TierMinXp(ProgressionService progression, string tierKey) =>
        progression.GetAllTiers()[tierKey].min_xp;

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
        var path = Path.Combine(Application.streamingAssetsPath, "Progression", "progression.json");
        var configTiers = (Newtonsoft.Json.Linq.JObject)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path))["tier_progression"];
        AssertEqual(configTiers.Count, tiers.Count, "GetAllTiers_MatchesConfig: tier count matches progression.json");

        foreach (var property in configTiers.Properties())
        {
            if (!tiers.TryGetValue(property.Name, out var tier))
            {
                Assert(false, $"GetAllTiers_MatchesConfig: '{property.Name}' tier missing");
                continue;
            }
            AssertEqual(property.Value.Value<int>("min_xp"), tier.min_xp, $"GetAllTiers_MatchesConfig: {property.Name} min_xp");
            Assert(property.Value["max_xp"] == null, $"GetAllTiers_MatchesConfig: {property.Name} configures only min_xp (the top is derived)");
        }

        // Derived ranges: each tier ends one XP before the next begins, so there is no gap and no
        // overlap, and the top tier is open-ended (it used to stop at a made-up 999).
        var sorted = SortedTiers(progression);
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            AssertEqual(sorted[i + 1].Value.min_xp - 1, sorted[i].Value.max_xp, $"GetAllTiers_MatchesConfig: {sorted[i].Key} ends one XP before {sorted[i + 1].Key} starts");
            Assert(!sorted[i].Value.IsOpenEnded(), $"GetAllTiers_MatchesConfig: {sorted[i].Key} is bounded");
        }
        var top = sorted[sorted.Count - 1].Value;
        Assert(top.IsOpenEnded(), "GetAllTiers_MatchesConfig: the top tier has no upper bound");
        AssertEqual($"{top.min_xp}+", top.XpRangeLabel(), "GetAllTiers_MatchesConfig: the top tier's range label is open-ended");
        AssertEqual($"{sorted[0].Value.min_xp} - {sorted[1].Value.min_xp - 1}", sorted[0].Value.XpRangeLabel(), "GetAllTiers_MatchesConfig: a bounded tier's range label shows both ends");
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
        // Tier ranges are whole numbers, so fractional XP just below a tier's min (e.g. 99.5)
        // used to match no tier at all and fall back to rookie; so did any XP above the old top
        // range. Checked against every boundary the config declares.
        var tiers = SortedTiers(progression);
        for (int i = 0; i < tiers.Count; i++)
        {
            string tier = tiers[i].Key;
            int min = tiers[i].Value.min_xp;

            if (i > 0)
                AssertEqual(tiers[i - 1].Key, TierFor(progression, min - 0.5f), $"TierLookup: {min - 0.5f} XP is {tiers[i - 1].Key}, not rookie");
            AssertEqual(tier, TierFor(progression, min), $"TierLookup: {min} XP is {tier}");
            if (i + 1 < tiers.Count)
                AssertEqual(tier, TierFor(progression, tiers[i + 1].Value.min_xp - 0.1f), $"TierLookup: {tiers[i + 1].Value.min_xp - 0.1f} XP is still {tier}");
        }

        var last = tiers[tiers.Count - 1];
        AssertEqual(last.Key, TierFor(progression, last.Value.min_xp * 10f + 1f), "TierLookup: XP far above the top tier's start stays in the top tier");
    }

    // A save's stored tier can disagree with its own XP (thresholds changed since it was written,
    // or it was edited by hand). Loading it must correct the tier, and write the correction back
    // because Facilities reads progression_state.json directly for its upgrade gate.
    private static void Test_LoadingSave_ReconcilesStaleTier(ProgressionService progression)
    {
        var tiers = SortedTiers(progression);
        var top = tiers[tiers.Count - 1];

        // Stored tier too low for the XP.
        WriteStoredProgression(TestPlayerId, top.Value.min_xp, "rookie");
        var state = progression.GetState(TestPlayerId, createIfMissing: false);
        AssertEqual(top.Key, state?.current_tier, "StaleTier: a tier that is too low for the XP is corrected on load");
        AssertEqual((float)top.Value.min_xp, state?.current_xp ?? -1f, "StaleTier: the XP itself is left alone", XpTolerance);
        AssertEqual(top.Key, ReadStoredTier(TestPlayerId), "StaleTier: the correction is written back to progression_state.json");

        var gatesPath = Path.Combine(Application.streamingAssetsPath, "Facilities", "facilities_config.json");
        int expectedGate = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(gatesPath))["tier_upgrade_gates"].Value<int>(top.Key);
        AssertEqual(expectedGate, new FacilitiesService().GetMaxFacilityLevelForPlayerTier(TestPlayerId),
            "StaleTier: Facilities' upgrade gate sees the corrected tier");

        // Stored tier too high for the XP (thresholds were raised): it drops, consistently.
        WriteStoredProgression(TestPlayerId, 0, top.Key);
        state = progression.GetState(TestPlayerId, createIfMissing: false);
        AssertEqual("rookie", state?.current_tier, "StaleTier: a tier that is too high for the XP is corrected on load");
        AssertEqual("rookie", ReadStoredTier(TestPlayerId), "StaleTier: the downward correction is written back too");

        // A save that is already consistent is left as it is.
        WriteStoredProgression(TestPlayerId, tiers[1].Value.min_xp, tiers[1].Key);
        AssertEqual(tiers[1].Key, progression.GetState(TestPlayerId, createIfMissing: false)?.current_tier, "StaleTier: a consistent save keeps its tier");

        progression.ClearPlayerProgression(TestPlayerId);
    }

    // Writes progression_state.json as an older build would have, and drops the cached state so
    // the next GetState reads it from disk.
    private static void WriteStoredProgression(string playerId, float xp, string tier)
    {
        var progression = ProgressionService.Instance;
        progression.ClearPlayerProgression(playerId);

        string path = FilePathResolver.GetProgressionPath(playerId, "progression_state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, new Newtonsoft.Json.Linq.JObject
        {
            ["player_id"] = playerId,
            ["current_xp"] = xp,
            ["current_tier"] = tier
        }.ToString());
    }

    private static string ReadStoredTier(string playerId)
    {
        string path = FilePathResolver.GetProgressionPath(playerId, "progression_state.json");
        return Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)).Value<string>("current_tier");
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
        // Run as a throwaway profile (CreateSeason uses the active profile, PlayerIdProvider) so the
        // test neither reads nor clears a developer's real "local_player" career XP.
        WithActivePlayer(CareerTestPlayerId, () =>
        {
            progression.ClearPlayerProgression(CareerTestPlayerId);

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
                    AssertEqual(CareerTestPlayerId, playerTeam.player_id, "CreateSeason_InitializesPlayerProgression: player team uses the active profile id");
                    var state = progression.GetState(playerTeam.player_id, createIfMissing: false);
                    Assert(state != null, "CreateSeason_InitializesPlayerProgression: progression state created for player");
                    if (state != null)
                    {
                        AssertEqual(0, state.current_xp, "CreateSeason_InitializesPlayerProgression: a player with no XP starts at 0 XP");
                    }
                }
            }

            progression.ClearPlayerProgression(CareerTestPlayerId);
        });
    }

    private const string CareerTestPlayerId = "__unit_test_progression_career__";

    // Runs body with PlayerIdProvider pointing at playerId, restoring the previous PlayerPrefs
    // value afterwards (same pattern as CanonicalPlayerIdIntegrationTests).
    private static void WithActivePlayer(string playerId, Action body)
    {
        bool hadPrevious = PlayerPrefs.HasKey(PlayerIdProvider.PlayerPrefsKey);
        string previous = PlayerPrefs.GetString(PlayerIdProvider.PlayerPrefsKey);
        try
        {
            PlayerIdProvider.Set(playerId);
            body();
        }
        finally
        {
            if (hadPrevious)
                PlayerIdProvider.Set(previous);
            else
            {
                PlayerPrefs.DeleteKey(PlayerIdProvider.PlayerPrefsKey);
                PlayerPrefs.Save();
            }
        }
    }

    // XP is career XP: creating a new season (every launch calls it) must keep the player's
    // XP, tier and history, and the new season's grants must stack on top without colliding
    // with the earlier season's idempotency keys.
    private static void Test_CreateSeason_KeepsCareerXp(LocalSeasonBackend backend, ProgressionService progression)
    {
        WithActivePlayer(CareerTestPlayerId, () =>
        {
            progression.ClearPlayerProgression(CareerTestPlayerId);
            // Enough XP to be exactly at the pro threshold (read from the config), in two grants.
            int careerXp = TierMinXp(progression, "pro");
            progression.AddXp(CareerTestPlayerId, careerXp - 20, "unit_test", Guid.NewGuid().ToString());
            progression.AddXp(CareerTestPlayerId, 20, "unit_test", Guid.NewGuid().ToString());
            AssertEqual("pro", progression.GetState(CareerTestPlayerId).current_tier, $"CareerXp: setup - {careerXp} XP is pro");

            string error = null;
            SeasonSaveData season2 = null;
            backend.CreateSeason(new List<string> { "Jets", "Hawks" }, "MyTeam", s => season2 = s, e => error = e);
            Assert(error == null && season2 != null, $"CareerXp: a new season is created ({error})");
            if (season2 == null) return;

            var state = progression.GetState(CareerTestPlayerId, createIfMissing: false);
            AssertEqual((float)careerXp, state.current_xp, "CareerXp: XP survives a new season", XpTolerance);
            AssertEqual("pro", state.current_tier, "CareerXp: tier survives a new season");
            AssertEqual(2, state.xp_history.Count, "CareerXp: XP history survives a new season");

            // The new season's first week stacks on the carried total.
            backend.SimulateWeek(season2, s => { }, e => error = e);
            state = progression.GetState(CareerTestPlayerId, createIfMissing: false);
            Assert(state.current_xp > careerXp, $"CareerXp: the new season's XP adds to the carried total (got {state.current_xp})");
            AssertEqual(4, state.xp_history.Count, "CareerXp: the new season's match + training are appended to the history");

            // A third season: week 1 again must not collide with season 2's week-1 event ids.
            SeasonSaveData season3 = null;
            float beforeSeason3 = state.current_xp;
            backend.CreateSeason(new List<string> { "Jets", "Hawks" }, "MyTeam", s => season3 = s, e => error = e);
            AssertEqual(beforeSeason3, progression.GetState(CareerTestPlayerId, createIfMissing: false).current_xp,
                "CareerXp: creating another season changes nothing", XpTolerance);
            backend.SimulateWeek(season3, s => { }, e => error = e);
            state = progression.GetState(CareerTestPlayerId, createIfMissing: false);
            AssertEqual(6, state.xp_history.Count, "CareerXp: season 3 week 1 is awarded too (event ids are per season)");
            Assert(state.current_xp > beforeSeason3, "CareerXp: season 3 week 1 XP is added");

            progression.ClearPlayerProgression(CareerTestPlayerId);
        });
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

    // XP is career XP, so the history spans seasons. "Week N" must restart at 1 in each season
    // (the hub shows "Week N of 10"), and a season that only got one week in must not be merged
    // with the next season's week 1 just because both are labelled "W1".
    private static void Test_XpScreen_WeekLabelsRestartEachSeason()
    {
        XpHistoryEntry S(string season, int week, string kind, float xp) =>
            new XpHistoryEntry("p", xp, kind == "match" ? "match_win" : "training") { id = $"{season}:week:{week}:{kind}" };

        var entries = new List<XpHistoryEntry>
        {
            S("seasonA", 1, "match", 15), S("seasonA", 1, "training", 2),
            S("seasonA", 2, "match", 15), S("seasonA", 2, "training", 2),
            S("seasonB", 1, "match", 15), S("seasonB", 1, "training", 2),
            S("seasonC", 1, "match", 15), S("seasonC", 1, "training", 2)
        };
        Assert(XPUI.WeekNumbers(entries).SequenceEqual(new[] { 1, 1, 2, 2, 1, 1, 1, 1 }),
            "XpScreen seasons: the week label restarts at 1 for each new season");

        var totals = XPUI.TotalsByWeek(entries);
        Assert(totals.Select(t => t.Key).SequenceEqual(new[] { 1, 2, 1, 1 }),
            "XpScreen seasons: one bar per played week, labelled with the season's own week number");
        Assert(totals.All(t => Mathf.Abs(t.Value - 17f) < XpTolerance),
            "XpScreen seasons: adjacent seasons' W1 bars are not merged (each is match + training)");

        AssertEqual("seasonA", XPUI.SeasonIdOf(entries[0]), "XpScreen seasons: season id is read from a match grant id");
        AssertEqual("seasonB", XPUI.SeasonIdOf(new XpHistoryEntry("p", 30f, "season_reward") { id = "seasonB:season_reward" }),
            "XpScreen seasons: season id is read from a season-reward grant id");
        Assert(XPUI.SeasonIdOf(new XpHistoryEntry("p", 9f, "duplicate_card_rare")) == null,
            "XpScreen seasons: entries without a season id have none");
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

        Test_TierGate_RookieCannotUpgradePastLevel2_ProUnlocksLevel3(progression, facilities, economy);

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

    // Tier gate (facilities_config.json "tier_upgrade_gates": rookie 2, pro 3, all_star 4,
    // legend 5). FacilitiesService reads the tier straight from progression_state.json, so
    // this drives the tier through the real ProgressionService (AddXp) rather than writing
    // the file by hand - that is the Progression -> Facilities link the gate depends on.
    private static void Test_TierGate_RookieCannotUpgradePastLevel2_ProUnlocksLevel3(
        ProgressionService progression, FacilitiesService facilities, EconomyService economy)
    {
        const string facility = "film_room";
        string gatePlayer = FacilitiesTestPlayerId;
        economy.AddCurrency(gatePlayer, 10_000_000, 0, "integration_test_seed");

        AssertEqual("rookie", progression.GetState(gatePlayer).current_tier, "TierGate: fresh player starts as rookie");
        AssertEqual(2, facilities.GetMaxFacilityLevelForPlayerTier(gatePlayer), "TierGate: rookie max facility level is 2");

        // Rookie: level 1 -> 2 is allowed.
        bool toLevel2 = facilities.TryUpgradeFacility(gatePlayer, facility, out var level2State);
        Assert(toLevel2, "TierGate: rookie can upgrade Film Room to level 2");
        AssertEqual(2, level2State?.level ?? -1, "TierGate: Film Room is level 2");

        // Rookie: level 2 -> 3 is refused, nothing is spent, nothing changes.
        int coinsBefore = economy.GetWallet(gatePlayer, createIfMissing: false).coins;
        bool canUpgrade = facilities.CanUpgradeFacility(gatePlayer, facility, out string blockReason);
        Assert(!canUpgrade, "TierGate: CanUpgradeFacility reports a rookie blocked at level 3");
        Assert(!string.IsNullOrEmpty(blockReason) && blockReason.Contains("Rookie"),
            $"TierGate: block reason names the current tier (got '{blockReason}')");

        bool toLevel3Rookie = facilities.TryUpgradeFacility(gatePlayer, facility, out var blockedState);
        Assert(!toLevel3Rookie, "TierGate: rookie cannot upgrade Film Room past level 2");
        AssertEqual(2, blockedState?.level ?? -1, "TierGate: refused upgrade leaves Film Room at level 2");
        AssertEqual(coinsBefore, economy.GetWallet(gatePlayer, createIfMissing: false).coins,
            "TierGate: refused upgrade spends no coins (gate is checked before payment)");

        // The gate is per tier, not per room: Weight Room hits the same ceiling.
        bool weightTo2 = facilities.TryUpgradeFacility(gatePlayer, "weight_room", out _);
        bool weightTo3 = facilities.TryUpgradeFacility(gatePlayer, "weight_room", out var weightState);
        Assert(weightTo2, "TierGate: rookie can upgrade Weight Room to level 2");
        Assert(!weightTo3, "TierGate: rookie cannot upgrade Weight Room past level 2 either");
        AssertEqual(2, weightState?.level ?? -1, "TierGate: Weight Room stays at level 2");

        // One XP short of pro is still rookie - the gate must not open early. (The pro threshold
        // comes from progression.json, so a threshold change needs no edit here.)
        int proMin = TierMinXp(progression, "pro");
        progression.AddXp(gatePlayer, proMin - 1, "unit_test", Guid.NewGuid().ToString());
        AssertEqual("rookie", progression.GetState(gatePlayer).current_tier, $"TierGate: {proMin - 1} XP is still rookie");
        Assert(!facilities.TryUpgradeFacility(gatePlayer, facility, out _), $"TierGate: {proMin - 1} XP does not unlock level 3");

        // Reaching the pro threshold crosses into pro - level 3 opens.
        progression.AddXp(gatePlayer, 1, "unit_test", Guid.NewGuid().ToString());
        AssertEqual("pro", progression.GetState(gatePlayer).current_tier, $"TierGate: {proMin} XP is pro");
        AssertEqual(3, facilities.GetMaxFacilityLevelForPlayerTier(gatePlayer), "TierGate: pro max facility level is 3");
        Assert(facilities.CanUpgradeFacility(gatePlayer, facility, out _), "TierGate: CanUpgradeFacility allows level 3 once pro");

        bool toLevel3Pro = facilities.TryUpgradeFacility(gatePlayer, facility, out var level3State);
        Assert(toLevel3Pro, "TierGate: pro can upgrade Film Room to level 3");
        AssertEqual(3, level3State?.level ?? -1, "TierGate: Film Room is level 3");

        // Pro is gated too: level 4 stays closed until all_star.
        bool toLevel4Pro = facilities.TryUpgradeFacility(gatePlayer, facility, out var level4State);
        Assert(!toLevel4Pro, "TierGate: pro cannot upgrade Film Room past level 3");
        AssertEqual(3, level4State?.level ?? -1, "TierGate: Film Room stays at level 3 as pro");
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

    // ---------------- Coaches integration ----------------

    private const string CoachesTestPlayerId = "__integration_test_coaches__";
    private const string CoachesTestPlayerId_NoFunds = "__integration_test_coaches_nofunds__";
    private const string CoachesTestTeamId = "__integration_test_coaches_team__";

    // Numbers pinned from coaches_bonus_config.json (as of PRs #91/#93): an Offensive or
    // Defensive coach adds 0.03 + 0.0004 x overall rating on a win, and having both adds a
    // 0.02 synergy bonus on a win only. The other tests derive expectations from the config
    // file; this pin exists so an intentional rebalance shows up as a visible test change.
    private const float PinnedCoachBaseBonus = 0.03f;
    private const float PinnedCoachRatingMultiplier = 0.0004f;
    private const float PinnedSynergyBonus = 0.02f;

    /// <summary>
    /// Integration test: Coaches -> Progression (per-coach and synergy XP bonuses, chained
    /// on top of Facilities' multiplier) and Coaches -> Economy (hiring cost gating).
    /// Exercises the real CoachesService/FacilitiesService/EconomyService/ProgressionService
    /// together - no mocking - via Unity's command-line batch mode:
    ///   Unity.exe -batchmode -projectPath &lt;path&gt; -executeMethod ProgressionUnitTests.RunCoachesIntegration -quit -logFile &lt;path&gt;
    ///
    /// Regression history: CoachesService.NormalizeXpSourceForCoachBonus() once mapped
    /// "match_win"/"match_loss" to "offensive_drill"/"defensive_drill" while the config keys
    /// its rules "win"/"loss", so hired O/D coaches silently added zero XP on matches
    /// (fixed in PR #81). PR #93 then scoped the synergy bonus to "win" (it used to apply to
    /// every non-duplicate source, including losses and training).
    /// </summary>
    public static void RunCoachesIntegration()
    {
        bool passed = RunCoachesSuite().Passed;
        if (Application.isBatchMode)
            EditorApplication.Exit(passed ? 0 : 1);
    }

    public static TestSuiteRunResult RunCoachesSuite()
    {
        _passed = 0;
        _failed = 0;
        _failures.Clear();

        Log("===== ProgressionUnitTests.RunCoachesIntegration: starting =====");

        var progression = GetOrCreateProgressionService();
        var facilities = new FacilitiesService();
        var economy = new EconomyService();

        ResetCoachesIntegrationState(progression, facilities, economy, CoachesTestPlayerId);
        ResetCoachesIntegrationState(progression, facilities, economy, CoachesTestPlayerId_NoFunds);

        // The tests share state on purpose, in order: coaches hired by one are the
        // starting point of the next (see the comment at the top of each).
        Test_NoCoachesHired_NoBonusApplied(progression, facilities);
        Test_HireOffensiveCoach_AppliesConfigDrivenBonusToMatchWin(progression, facilities, economy);
        Test_HireBothOffenseAndDefense_SynergyBonusStacks(progression, facilities, economy);
        Test_Synergy_AppliesToWinsOnly_NotLossesOrTraining(progression, facilities);
        Test_DuplicateCardSource_ExemptFromCoachBonus(progression);
        Test_FireCoach_RemovesItsBonus(progression, facilities);
        Test_FacilityThenCoachBonus_IsNotRoundedBetweenSteps(progression, facilities);
        Test_HireCoach_InsufficientFunds_Rejected();

        ResetCoachesIntegrationState(progression, facilities, economy, CoachesTestPlayerId);
        ResetCoachesIntegrationState(progression, facilities, economy, CoachesTestPlayerId_NoFunds);

        Log($"===== RunCoachesIntegration: {_passed} passed, {_failed} failed =====");
        if (_failed > 0)
        {
            Log("FAILURES:\n - " + string.Join("\n - ", _failures));
        }

        return new TestSuiteRunResult(_passed, _failed, _failures);
    }

    private static void ResetCoachesIntegrationState(
        ProgressionService progression, FacilitiesService facilities, EconomyService economy, string playerId)
    {
        progression.ClearPlayerProgression(playerId);
        facilities.ResetFacilityState(playerId);
        economy.ResetWallet(playerId);
        CoachesService.ResetPlayerCoachState(playerId);
    }

    private static void Test_NoCoachesHired_NoBonusApplied(ProgressionService progression, FacilitiesService facilities)
    {
        float bonus = CoachesService.GetCoachXpBonusPercent(CoachesTestPlayerId, "match_win");
        AssertEqual(0f, bonus, "NoCoachesHired: bonus percent is 0 with nobody hired");

        // A fresh player's level-1 Film Room still applies its own baseline multiplier
        // (see the Facilities suite) - this confirms Coaches contributes nothing on top
        // of that when nobody's hired, not that XP is untouched by every system.
        float facilityMultiplier = facilities.GetProgressionXpMultiplier(CoachesTestPlayerId, "match_win");
        float expectedXp = ExpectedBonusXp(100, facilityMultiplier, 0f);

        progression.AddXp(CoachesTestPlayerId, 100, "match_win", Guid.NewGuid().ToString());
        float actualXp = progression.GetState(CoachesTestPlayerId).current_xp;
        AssertEqual(expectedXp, actualXp, "NoCoachesHired: XP reflects only the Facilities multiplier", XpTolerance);
    }

    private static void Test_HireOffensiveCoach_AppliesConfigDrivenBonusToMatchWin(
        ProgressionService progression, FacilitiesService facilities, EconomyService economy)
    {
        progression.ClearPlayerProgression(CoachesTestPlayerId);
        CoachesService.ResetPlayerCoachState(CoachesTestPlayerId);
        economy.AddCurrency(CoachesTestPlayerId, 10_000_000, 0, "integration_test_seed");

        var coach = HireCoachOfType(CoachesTestPlayerId, "O");
        Assert(coach != null, "HireOffensiveCoach: an Offensive coach was hired");
        if (coach == null) return;

        var config = LoadCoachBonusConfigForTest();
        float expectedBonus = GetExpectedTypeBonus(config, "O", "win", coach);
        Assert(expectedBonus > 0f, "HireOffensiveCoach: config declares a non-zero base_bonus for O/win (sanity check on the fix)");

        float actualBonus = CoachesService.GetCoachXpBonusPercent(CoachesTestPlayerId, "match_win");
        AssertEqual(expectedBonus, actualBonus, "HireOffensiveCoach: GetCoachXpBonusPercent matches config-derived value");
        AssertEqual(PinnedCoachBaseBonus + coach.overall_rating * PinnedCoachRatingMultiplier, actualBonus,
            "HireOffensiveCoach: bonus is 0.03 + 0.0004 x overall rating (pinned config numbers)");

        float facilityMultiplier = facilities.GetProgressionXpMultiplier(CoachesTestPlayerId, "match_win");
        float expectedXp = ExpectedBonusXp(100, facilityMultiplier, expectedBonus);

        progression.AddXp(CoachesTestPlayerId, 100, "match_win", Guid.NewGuid().ToString());
        float actualXp = progression.GetState(CoachesTestPlayerId).current_xp;
        AssertEqual(expectedXp, actualXp, "HireOffensiveCoach: AddXp chains Facilities then Coaches correctly", XpTolerance);
    }

    private static void Test_HireBothOffenseAndDefense_SynergyBonusStacks(
        ProgressionService progression, FacilitiesService facilities, EconomyService economy)
    {
        progression.ClearPlayerProgression(CoachesTestPlayerId);
        CoachesService.ResetPlayerCoachState(CoachesTestPlayerId);
        economy.AddCurrency(CoachesTestPlayerId, 10_000_000, 0, "integration_test_seed");

        var offCoach = HireCoachOfType(CoachesTestPlayerId, "O");
        var defCoach = HireCoachOfType(CoachesTestPlayerId, "D");
        Assert(offCoach != null && defCoach != null, "SynergyBonus: both an Offensive and a Defensive coach were hired");
        if (offCoach == null || defCoach == null) return;

        var config = LoadCoachBonusConfigForTest();
        float offBonus = GetExpectedTypeBonus(config, "O", "win", offCoach);
        float defBonus = GetExpectedTypeBonus(config, "D", "win", defCoach);
        float synergyBonus = config?.synergy_bonus?
            .FirstOrDefault(s => s != null && s.required != null && s.required.Contains("O") && s.required.Contains("D"))
            ?.bonus ?? 0f;
        Assert(synergyBonus > 0f, "SynergyBonus: config declares a non-zero O+D synergy bonus (sanity check)");
        AssertEqual(PinnedSynergyBonus, synergyBonus, "SynergyBonus: config synergy bonus is the pinned 0.02");

        float expectedTotal = offBonus + defBonus + synergyBonus;
        float actualTotal = CoachesService.GetCoachXpBonusPercent(CoachesTestPlayerId, "match_win");
        AssertEqual(expectedTotal, actualTotal, "SynergyBonus: total bonus is both per-type bonuses plus synergy");

        float facilityMultiplier = facilities.GetProgressionXpMultiplier(CoachesTestPlayerId, "match_win");
        float expectedXp = ExpectedBonusXp(100, facilityMultiplier, expectedTotal);

        progression.AddXp(CoachesTestPlayerId, 100, "match_win", Guid.NewGuid().ToString());
        float actualXp = progression.GetState(CoachesTestPlayerId).current_xp;
        AssertEqual(expectedXp, actualXp, "SynergyBonus: AddXp reflects the stacked per-type + synergy bonus", XpTolerance);
    }

    private static void Test_Synergy_AppliesToWinsOnly_NotLossesOrTraining(
        ProgressionService progression, FacilitiesService facilities)
    {
        // O+D are still hired from the previous test. Before PR #93 the 0.02 synergy bonus
        // applied to every non-duplicate source; the config now scopes it to "win", and the
        // per-type rules only exist for "win" too, so losses and training get no coach bonus.
        foreach (string source in new[] { "match_loss", "training" })
        {
            float bonus = CoachesService.GetCoachXpBonusPercent(CoachesTestPlayerId, source);
            AssertEqual(0f, bonus, $"SynergyWinOnly: coach bonus on '{source}' is 0 with O+D hired");

            progression.ClearPlayerProgression(CoachesTestPlayerId);
            float facilityMultiplier = facilities.GetProgressionXpMultiplier(CoachesTestPlayerId, source);
            float expectedXp = ExpectedBonusXp(100, facilityMultiplier, 0f);

            progression.AddXp(CoachesTestPlayerId, 100, source, Guid.NewGuid().ToString());
            float actualXp = progression.GetState(CoachesTestPlayerId).current_xp;
            AssertEqual(expectedXp, actualXp, $"SynergyWinOnly: '{source}' XP reflects only Facilities, no coach bonus", XpTolerance);
        }
    }

    private static void Test_DuplicateCardSource_ExemptFromCoachBonus(ProgressionService progression)
    {
        // O+D coaches are still hired - duplicate-card XP must be exempt from coach
        // bonuses regardless, same exemption already proven for Facilities multipliers.
        float bonus = CoachesService.GetCoachXpBonusPercent(CoachesTestPlayerId, "duplicate_card_common");
        AssertEqual(0f, bonus, "DuplicateCardSource: coach bonus percent is 0 despite hired coaches");

        progression.ClearPlayerProgression(CoachesTestPlayerId);
        progression.AddXp(CoachesTestPlayerId, 100, "duplicate_card_common", Guid.NewGuid().ToString());
        float xp = progression.GetState(CoachesTestPlayerId).current_xp;
        AssertEqual(100f, xp, "DuplicateCardSource: 100 base XP stays 100, unaffected by hired coaches");
    }

    private static void Test_FireCoach_RemovesItsBonus(ProgressionService progression, FacilitiesService facilities)
    {
        // O+D are still hired (synergy active). Firing the Offensive coach should drop both
        // its own per-type bonus AND the synergy bonus (which requires both types), leaving
        // only D's.
        bool fired = CoachesService.FireCoach("O", CoachesTestPlayerId);
        Assert(fired, "FireCoach: firing the Offensive coach succeeds");

        var config = LoadCoachBonusConfigForTest();
        var defCoachId = CoachesService.GetTeamState(CoachesTestPlayerId)?.defence_coach;
        var defCoach = string.IsNullOrEmpty(defCoachId) ? null : CoachesService.GetCoachById(defCoachId);
        Assert(defCoach != null, "FireCoach: Defensive coach is still on the roster");
        if (defCoach == null) return;

        float expectedRemaining = GetExpectedTypeBonus(config, "D", "win", defCoach);
        float actualRemaining = CoachesService.GetCoachXpBonusPercent(CoachesTestPlayerId, "match_win");
        AssertEqual(expectedRemaining, actualRemaining, "FireCoach: only the remaining Defensive coach's bonus applies (no synergy, no Offensive bonus)");

        progression.ClearPlayerProgression(CoachesTestPlayerId);
        float facilityMultiplier = facilities.GetProgressionXpMultiplier(CoachesTestPlayerId, "match_win");
        float expectedXp = ExpectedBonusXp(100, facilityMultiplier, expectedRemaining);

        progression.AddXp(CoachesTestPlayerId, 100, "match_win", Guid.NewGuid().ToString());
        float actualXp = progression.GetState(CoachesTestPlayerId).current_xp;
        AssertEqual(expectedXp, actualXp, "FireCoach: AddXp reflects the reduced bonus after firing", XpTolerance);
    }

    private static void Test_FacilityThenCoachBonus_IsNotRoundedBetweenSteps(ProgressionService progression, FacilitiesService facilities)
    {
        // A Defensive coach is still hired from the previous test. A small base amount makes
        // rounding visible: 7 x 1.03 (Facilities) x (1 + coach bonus) is about 7.44, but
        // rounding after the Facilities step and again after the Coaches step used to
        // collapse it to 7.
        progression.ClearPlayerProgression(CoachesTestPlayerId);
        float coachBonus = CoachesService.GetCoachXpBonusPercent(CoachesTestPlayerId, "match_win");
        float facilityMultiplier = facilities.GetProgressionXpMultiplier(CoachesTestPlayerId, "match_win");
        float expectedXp = ExpectedBonusXp(7, facilityMultiplier, coachBonus);

        Assert(coachBonus > 0f, "NoIntermediateRounding: a coach bonus is active for this check");
        Assert(Mathf.Abs(expectedXp - Mathf.Round(expectedXp)) > 0.05f,
            $"NoIntermediateRounding: test data actually produces a fractional result ({expectedXp}), so rounding would be visible");

        progression.AddXp(CoachesTestPlayerId, 7, "match_win", Guid.NewGuid().ToString());
        float actualXp = progression.GetState(CoachesTestPlayerId).current_xp;
        AssertEqual(expectedXp, actualXp, "NoIntermediateRounding: XP keeps its fractional part through both bonus steps", XpTolerance);
    }

    private static void Test_HireCoach_InsufficientFunds_Rejected()
    {
        // Deliberately no AddCurrency call - this player has 0 coins.
        var candidate = CoachesService.GetAvailableCoaches(CoachesTestPlayerId_NoFunds)
            .FirstOrDefault(c => c != null && c.coach_type == "O");
        Assert(candidate != null, "HireInsufficientFunds: an Offensive coach exists in the catalog");
        if (candidate == null) return;

        bool hired = CoachesService.TryHireCoach(CoachesTestTeamId, candidate.coach_id, out _, CoachesTestPlayerId_NoFunds);
        Assert(!hired, "HireInsufficientFunds: TryHireCoach fails with 0 coins");
        Assert(string.IsNullOrEmpty(CoachesService.GetTeamState(CoachesTestPlayerId_NoFunds)?.offence_coach),
            "HireInsufficientFunds: no Offensive coach is assigned");
        AssertEqual(0f, CoachesService.GetCoachXpBonusPercent(CoachesTestPlayerId_NoFunds, "match_win"),
            "HireInsufficientFunds: no coach bonus is granted");
    }

    // Mirrors ProgressionService's bonus chain: Facilities multiplier, then the Coaches
    // bonus on top, with no rounding at either step (the minimum-1 floor still applies).
    private static float ExpectedBonusXp(float baseXp, float facilityMultiplier, float coachBonusPercent) =>
        Mathf.Max(1f, Mathf.Max(1f, baseXp * facilityMultiplier) * (1f + coachBonusPercent));

    private static CoachDatabaseRecord HireCoachOfType(string playerId, string coachType)
    {
        var candidate = CoachesService.GetAvailableCoaches(playerId).FirstOrDefault(c => c != null && c.coach_type == coachType);
        if (candidate == null) return null;

        bool hired = CoachesService.TryHireCoach(CoachesTestTeamId, candidate.coach_id, out var hiredCoach, playerId);
        return hired ? hiredCoach : null;
    }

    // Reads coaches_bonus_config.json directly (the same file CoachesService itself reads)
    // rather than calling its private LoadBonusConfig(), matching how the Facilities/CCAS
    // suites compute expected values from real config rather than hardcoded literals.
    private static CoachesBonusConfig LoadCoachBonusConfigForTest()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "Coaches", "coaches_bonus_config.json");
        return JsonUtility.FromJson<CoachesBonusConfig>(File.ReadAllText(path));
    }

    private static float GetExpectedTypeBonus(CoachesBonusConfig config, string coachType, string normalizedSource, CoachDatabaseRecord coach)
    {
        var typeEntry = config?.xp_bonus_rules?.FirstOrDefault(t => t != null && string.Equals(t.coach_type, coachType, StringComparison.OrdinalIgnoreCase));
        var rule = typeEntry?.source_rules?.FirstOrDefault(r => r != null && string.Equals(r.xp_source, normalizedSource, StringComparison.OrdinalIgnoreCase));
        if (rule == null || coach == null) return 0f;
        return rule.base_bonus + coach.overall_rating * rule.rating_multiplier;
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
