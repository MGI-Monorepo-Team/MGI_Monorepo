#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CCAS.Backend;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Verifies the intended integration boundary for CCAS duplicate-card XP:
/// CCAS owns the configured duplicate amount; Facilities and Coaches must not
/// alter that reward even when their upgrades/assignments are active.
///
/// Run with:
/// Unity -batchmode -projectPath <path> -executeMethod CCASFacilitiesCoachesBoundaryIntegrationTests.RunAll -quit -logFile <path>
/// </summary>
public static class CCASFacilitiesCoachesBoundaryIntegrationTests
{
    private const string TestPlayerId = "__integration_test_ccas_boundary__";
    private const string TestTeamId = "ccas_boundary_test_team";
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
        Log("===== CCASFacilitiesCoachesBoundaryIntegrationTests: starting =====");

        var ccas = GetOrCreateCCASService();
        var progression = GetOrCreateProgressionService();
        var dropConfig = GetOrCreateDropConfigManager();
        var catalog = GetOrCreateCardCatalogLoader();
        var facilities = new FacilitiesService();

        Assert(dropConfig.config != null, "Setup: CCAS pack configuration loaded");
        Assert(catalog.catalog?.cards?.Length > 0, "Setup: CCAS card catalog loaded");

        Test_DuplicateCardXp_IsNotModifiedByFacilitiesOrCoaches(
            ccas, progression, dropConfig, catalog, facilities);

        ResetWorkflowState(ccas, progression, facilities);
        Log($"===== CCASFacilitiesCoachesBoundaryIntegrationTests: {_passed} passed, {_failed} failed =====");
        if (_failed > 0)
            Log("FAILURES:\n - " + string.Join("\n - ", Failures));

        return new TestSuiteRunResult(_passed, _failed, Failures);
    }

    private static void Test_DuplicateCardXp_IsNotModifiedByFacilitiesOrCoaches(
        CCASService ccas,
        ProgressionService progression,
        DropConfigManager dropConfig,
        CardCatalogLoader catalog,
        FacilitiesService facilities)
    {
        ResetWorkflowState(ccas, progression, facilities);
        var economy = new EconomyService();
        economy.AddCurrency(TestPlayerId, 2_000_000, 0, 0, "integration_test_seed");

        bool upgraded = facilities.TryUpgradeFacility(TestPlayerId, "film_room", out var filmRoom);
        Assert(upgraded, "Setup: Film Room upgrade succeeds");
        AssertEqual(2, filmRoom?.level ?? -1, "Setup: Film Room reaches level 2");
        AssertEqual(1f, facilities.GetProgressionXpMultiplier(TestPlayerId, "duplicate_card_common"),
            "Boundary: upgraded Facilities leave duplicate-card multiplier at 1.0x");

        var availableCoaches = CoachesService.GetAvailableCoaches(TestPlayerId);
        var offenceCoach = availableCoaches.FirstOrDefault(coach => coach?.coach_type == "O");
        var defenceCoach = availableCoaches.FirstOrDefault(coach => coach?.coach_type == "D");
        Assert(offenceCoach != null && defenceCoach != null, "Setup: offense and defense coaches are available");

        if (offenceCoach != null)
            Assert(CoachesService.TryHireCoach(TestTeamId, offenceCoach.coach_id, out _, TestPlayerId),
                "Setup: offense coach hire succeeds");
        if (defenceCoach != null)
            Assert(CoachesService.TryHireCoach(TestTeamId, defenceCoach.coach_id, out _, TestPlayerId),
                "Setup: defense coach hire succeeds");

        float eligibleCoachBonus = CoachesService.GetCoachXpBonusPercent(TestPlayerId, "win");
        float duplicateCoachBonus = CoachesService.GetCoachXpBonusPercent(TestPlayerId, "duplicate_card_common");
        float lossCoachBonus = CoachesService.GetCoachXpBonusPercent(TestPlayerId, "match_loss");
        float seasonRewardCoachBonus = CoachesService.GetCoachXpBonusPercent(TestPlayerId, "season_reward");
        float trainingCoachBonus = CoachesService.GetCoachXpBonusPercent(TestPlayerId, "training");
        float recoveryCoachBonus = CoachesService.GetCoachXpBonusPercent(TestPlayerId, "recovery");
        Assert(eligibleCoachBonus > 0f, "Setup: active coaches apply a positive bonus to an eligible win source");
        AssertEqual(0f, duplicateCoachBonus,
            "Boundary: active coaches leave duplicate-card bonus at 0%");
        AssertEqual(0f, lossCoachBonus,
            "Boundary: O+D synergy does not apply to match-loss XP");
        AssertEqual(0f, seasonRewardCoachBonus,
            "Boundary: O+D synergy does not apply to season-reward XP");
        AssertEqual(0f, trainingCoachBonus,
            "Boundary: O+D synergy does not apply to training XP");
        AssertEqual(0f, recoveryCoachBonus,
            "Boundary: O+D synergy does not apply to recovery XP");

        var fullCollection = catalog.catalog.cards
            .Where(card => card != null && !string.IsNullOrWhiteSpace(card.uid))
            .Select(card => new CardCollectionEntry { card_id = card.uid, quantity = 1 })
            .ToList();
        Assert(ccas.SeedCollectionForTesting(TestPlayerId, fullCollection),
            "Setup: CCAS seeds every catalog card as owned");

        int xpUpdatedEvents = 0;
        using (EventBus.Subscribe("xp_updated", evt =>
        {
            if (evt.player_id == TestPlayerId)
                xpUpdatedEvents++;
        }))
        {
            var result = ccas.OpenPack(TestPlayerId, "bronze_pack");
            int duplicateCount = result.cardDetails.Count(detail => detail.isDuplicate);
            int expectedCcasXp = result.cardDetails.Sum(detail => detail.xpAwarded);
            var progressionState = progression.GetState(TestPlayerId, createIfMissing: false);

            Assert(result.success, "DuplicatePack: CCAS pack opening succeeds");
            AssertEqual(dropConfig.config.pack_types["bronze_pack"].guaranteed_cards, duplicateCount,
                "DuplicatePack: every pull is a duplicate after full collection seed");
            Assert(expectedCcasXp > 0, "DuplicatePack: CCAS calculates configured duplicate XP");
            AssertEqual(expectedCcasXp, progressionState?.current_xp ?? 0,
                "Boundary: Progression stores exactly CCAS duplicate XP with upgrades active");
            AssertEqual(expectedCcasXp, progressionState?.xp_history?.Sum(entry => entry.xp_gained) ?? 0,
                "Boundary: persisted XP history matches CCAS duplicate XP");
            AssertEqual(duplicateCount, xpUpdatedEvents,
                "DuplicatePack: Progression publishes one XP update per duplicate");
        }
    }

    private static void ResetWorkflowState(CCASService ccas, ProgressionService progression, FacilitiesService facilities)
    {
        ccas.ResetPlayerState(TestPlayerId);
        progression.ClearPlayerProgression(TestPlayerId);
        facilities.ResetFacilityState(TestPlayerId);
        CoachesService.ResetPlayerCoachState(TestPlayerId);
        new EconomyService().ResetWallet(TestPlayerId);
    }

    private static CCASService GetOrCreateCCASService()
    {
        if (CCASService.Instance != null) return CCASService.Instance;
        var service = new GameObject("CCASBoundaryIntegration_CCASSvc").AddComponent<CCASService>();
        ForceAwake(service);
        return service;
    }

    private static ProgressionService GetOrCreateProgressionService()
    {
        if (ProgressionService.Instance != null) return ProgressionService.Instance;
        var service = new GameObject("CCASBoundaryIntegration_ProgressionSvc").AddComponent<ProgressionService>();
        ForceAwake(service);
        ForcePrivateMethod(service, "LoadProgressionConfig");
        return service;
    }

    private static DropConfigManager GetOrCreateDropConfigManager()
    {
        if (DropConfigManager.Instance != null) return DropConfigManager.Instance;
        var manager = new GameObject("CCASBoundaryIntegration_DropConfig").AddComponent<DropConfigManager>();
        ForceAwake(manager);
        ForcePrivateMethod(manager, "LoadConfig");
        return manager;
    }

    private static CardCatalogLoader GetOrCreateCardCatalogLoader()
    {
        if (CardCatalogLoader.Instance != null) return CardCatalogLoader.Instance;
        var loader = new GameObject("CCASBoundaryIntegration_CardCatalog").AddComponent<CardCatalogLoader>();
        ForceAwake(loader);
        ForcePrivateMethod(loader, "LoadCatalog");
        return loader;
    }

    private static void ForceAwake(MonoBehaviour target)
    {
        var method = target.GetType().GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance);
        try
        {
            method?.Invoke(target, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            // DontDestroyOnLoad is invalid in edit-mode batch tests; singleton assignment already happened.
        }
    }

    private static void ForcePrivateMethod(object target, string methodName)
    {
        target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(target, null);
    }

    private static void Assert(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            Log($"PASS: {name}");
            return;
        }

        _failed++;
        Failures.Add(name);
        Log($"FAIL: {name}");
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
    {
        bool equal = EqualityComparer<T>.Default.Equals(expected, actual);
        Assert(equal, equal ? name : $"{name} (expected {expected}, got {actual})");
    }

    private static void Log(string message)
    {
        Debug.Log($"[CCASFacilitiesCoachesBoundaryIntegrationTests] {message}");
    }
}
#endif
