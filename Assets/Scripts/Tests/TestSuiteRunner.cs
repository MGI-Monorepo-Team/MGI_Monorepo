#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Single batch-mode entry point for the repository's custom Unity test suites.
/// Set MGI_TESTS to a comma-separated list of suite names to retain individual selection.
/// </summary>
public static class TestSuiteRunner
{
    private const string AllSuites = "all";
    private const string DefaultReportPath = "Logs/test-report.md";

    private sealed class SuiteDefinition
    {
        public string Name;
        public string Subsystem;
        public string Description;
        public Func<TestSuiteRunResult> Run;
    }

    private sealed class SuiteResult
    {
        public SuiteDefinition Definition;
        public TestSuiteRunResult TestResult;
        public string Error;
    }

    public static void RunAll()
    {
        Run(AllSuites);
    }

    public static void RunSelected()
    {
        Run(Environment.GetEnvironmentVariable("MGI_TESTS") ?? AllSuites);
    }

    public static void Run(string selection)
    {
        var definitions = BuildSuites();
        var selectedNames = ParseSelection(selection);
        var selectedSuites = definitions
            .Where(suite => selectedNames.Contains(suite.Name) || selectedNames.Contains(AllSuites))
            .ToList();
        var unknownNames = selectedNames
            .Where(name => name != AllSuites && definitions.All(suite => suite.Name != name))
            .ToList();
        var results = new List<SuiteResult>();

        Debug.Log($"===== TestSuiteRunner: starting ({string.Join(", ", selectedNames)}) =====");

        foreach (var suite in selectedSuites)
        {
            var result = new SuiteResult { Definition = suite };
            try
            {
                result.TestResult = suite.Run();
            }
            catch (Exception exception)
            {
                result.Error = exception.ToString();
                result.TestResult = new TestSuiteRunResult(0, 1, new[] { "Unhandled suite exception" });
                Debug.LogException(exception);
            }

            results.Add(result);
            Debug.Log($"===== TestSuiteRunner: {suite.Name} {(result.TestResult.Passed ? "PASS" : "FAIL")} " +
                $"({result.TestResult.PassedCount} passed, {result.TestResult.FailedCount} failed) =====");
        }

        bool passed = unknownNames.Count == 0 && results.Count > 0 && results.All(result => result.TestResult.Passed);
        string reportPath = ResolveReportPath();
        WriteReport(reportPath, selection, selectedNames, unknownNames, results, passed);
        Debug.Log($"===== TestSuiteRunner: {(passed ? "PASS" : "FAIL")}; report: {reportPath} =====");

        if (Application.isBatchMode)
            EditorApplication.Exit(passed ? 0 : 1);
    }

    private static List<SuiteDefinition> BuildSuites()
    {
        return new List<SuiteDefinition>
        {
            new SuiteDefinition
            {
                Name = "progression",
                Subsystem = "Progression and LocalSeasonBackend",
                Description = "XP state, tiers, idempotency, history, season rewards, and week simulation.",
                Run = ProgressionUnitTests.RunSuite
            },
            new SuiteDefinition
            {
                Name = "progression-facilities",
                Subsystem = "Facilities -> Progression and Economy",
                Description = "Facility XP multipliers, upgrades, and insufficient-funds gating.",
                Run = ProgressionUnitTests.RunFacilitiesSuite
            },
            new SuiteDefinition
            {
                Name = "progression-ccas",
                Subsystem = "CCAS -> Progression and Economy",
                Description = "Pack purchase cost gating, duplicate-card XP, and event idempotency.",
                Run = ProgressionUnitTests.RunCcasSuite
            },
            new SuiteDefinition
            {
                Name = "ccas",
                Subsystem = "CCAS service",
                Description = "Pack validation, persistence, duplicate XP, refunds, reset, seed, and telemetry cleanup.",
                Run = CCASUnitTests.RunSuite
            },
            new SuiteDefinition
            {
                Name = "ccas-progression",
                Subsystem = "CCAS -> Progression",
                Description = "Duplicate-card XP persistence, history, events, player isolation, and funds gating.",
                Run = CCASProgressionIntegrationTests.RunSuite
            },
            new SuiteDefinition
            {
                Name = "ccas-economy",
                Subsystem = "CCAS -> Economy",
                Description = "Pack cost deduction, transaction ledger, collection/history persistence, events, and funds gating.",
                Run = CCASEconomyIntegrationTests.RunSuite
            },
            new SuiteDefinition
            {
                Name = "ccas-boundary",
                Subsystem = "CCAS -> Progression; Facilities and Coaches boundary",
                Description = "Duplicate-card XP remains owned by CCAS despite facility upgrades and active coaches.",
                Run = CCASFacilitiesCoachesBoundaryIntegrationTests.RunSuite
            },
            new SuiteDefinition
            {
                Name = "ccas-scene-smoke",
                Subsystem = "CCAS scene UI",
                Description = "CCAS scene opens, critical services and inspector references exist, and core panel navigation works.",
                Run = CCASSceneSmokeTests.RunSuite
            },
            new SuiteDefinition
            {
                Name = "canonical-player-id",
                Subsystem = "Shared identity -> CCAS, Economy, Facilities, Coaches, Progression",
                Description = "A non-default active player profile stays consistent across the five game services.",
                Run = CanonicalPlayerIdIntegrationTests.RunSuite
            },
            new SuiteDefinition
            {
                Name = "game-outcomes",
                Subsystem = "Game outcome simulator -> Progression",
                Description = "Validates complete simulator result weeks before LocalSeasonBackend applies them.",
                Run = GameOutcomeReaderTests.RunSuite
            },
            new SuiteDefinition
            {
                Name = "game-outcome-rewards",
                Subsystem = "Game outcome simulator -> Economy",
                Description = "Applies simulator coin rewards once per stable result event.",
                Run = GameOutcomeRewardTests.RunSuite
            }
        };
    }

    private static HashSet<string> ParseSelection(string selection)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in (selection ?? AllSuites).Split(','))
        {
            string normalized = name.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalized))
                names.Add(normalized);
        }

        if (names.Count == 0)
            names.Add(AllSuites);
        return names;
    }

    private static string ResolveReportPath()
    {
        string configuredPath = Environment.GetEnvironmentVariable("MGI_TEST_REPORT");
        string relativePath = string.IsNullOrWhiteSpace(configuredPath) ? DefaultReportPath : configuredPath;
        return Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(Directory.GetParent(Application.dataPath).FullName, relativePath);
    }

    private static void WriteReport(
        string reportPath,
        string selection,
        HashSet<string> selectedNames,
        List<string> unknownNames,
        List<SuiteResult> results,
        bool passed)
    {
        string directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var report = new StringBuilder();
        report.AppendLine("# MGI test diagnostic report");
        report.AppendLine();
        report.AppendLine($"- Generated (UTC): {DateTime.UtcNow:O}");
        report.AppendLine($"- Unity version: {Application.unityVersion}");
        report.AppendLine($"- Selection: `{selection}`");
        report.AppendLine($"- Overall result: **{(passed ? "PASS" : "FAIL")}**");
        report.AppendLine();

        report.AppendLine("## Full-run results");
        report.AppendLine();
        report.AppendLine("| Suite | Subsystem | Result | Scope |");
        report.AppendLine("| --- | --- | --- | --- |");
        var failureDetails = new List<string>();
        foreach (SuiteDefinition suite in BuildSuites())
        {
            SuiteResult result = results.FirstOrDefault(item => item.Definition.Name == suite.Name);
            string resultText = result == null ? "not selected" : result.TestResult.Passed ? "PASS" : "FAIL";
            string counts = result == null ? "" : $" ({result.TestResult.PassedCount} passed, {result.TestResult.FailedCount} failed)";
            report.AppendLine($"| `{suite.Name}` | {suite.Subsystem} | **{resultText}**{counts} | {suite.Description} |");

            if (result != null && result.TestResult.FailureMessages.Count > 0)
            {
                var details = new StringBuilder();
                details.AppendLine($"### `{suite.Name}` failure details");
                foreach (string failure in result.TestResult.FailureMessages)
                    details.AppendLine($"- {failure}");
                failureDetails.Add(details.ToString());
            }

            if (result != null && !string.IsNullOrEmpty(result.Error))
            {
                failureDetails.Add(
                    $"### `{suite.Name}` unhandled exception\n```text\n{result.Error}\n```");
            }
        }

        if (failureDetails.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("## Failure details");
            report.AppendLine();
            foreach (string details in failureDetails)
                report.AppendLine(details);
        }

        if (unknownNames.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"Unknown suite names: `{string.Join("`, `", unknownNames)}`");
        }

        report.AppendLine();
        report.AppendLine("## Test inventory and subsystem connections");
        report.AppendLine();
        report.AppendLine("| Test surface | Connection | Execution type |");
        report.AppendLine("| --- | --- | --- |");
        report.AppendLine("| `ProgressionUnitTests` | ProgressionService, LocalSeasonBackend, FacilitiesService | Automated Unity batch suite |");
        report.AppendLine("| `ProgressionUnitTests.RunFacilitiesIntegration` | FacilitiesService -> ProgressionService and EconomyService | Automated Unity batch suite |");
        report.AppendLine("| `ProgressionUnitTests.RunCcasIntegration` | CCASService -> ProgressionService and EconomyService | Automated Unity batch suite |");
        report.AppendLine("| `CCASUnitTests` | CCASService, EconomyService, ProgressionService, FacilitiesService, CoachesService, EventBus, catalog/config loaders | Automated Unity batch suite |");
        report.AppendLine("| `CCASProgressionIntegrationTests` | CCASService -> ProgressionService | Automated Unity integration suite |");
        report.AppendLine("| `CCASEconomyIntegrationTests` | CCASService -> EconomyService | Automated Unity integration suite |");
        report.AppendLine("| `CCASFacilitiesCoachesBoundaryIntegrationTests` | CCAS duplicate XP boundary; FacilitiesService and CoachesService must not modify it | Automated Unity integration suite |");
        report.AppendLine("| `CCASSceneSmokeTests` | CCAS scene asset, services, inspector wiring, and core panel navigation | Automated Unity editor smoke suite |");
        report.AppendLine("| `CanonicalPlayerIdIntegrationTests` | PlayerIdProvider -> CCAS, Economy, Facilities, Coaches, Progression | Automated Unity integration suite |");
        report.AppendLine("| `GameOutcomeReaderTests` | Game outcome simulator output -> LocalSeasonBackend | Automated Unity file-contract suite |");
        report.AppendLine("| `GameOutcomeRewardTests` | Game outcome simulator output -> EconomyService | Automated Unity idempotency suite |");
        report.AppendLine("| `EconomyServiceSmokeTestRunner` | EconomyService wallet, currency, spending, transactions, and events | Manual Play Mode/context-menu diagnostic; does not fail the process |");
        report.AppendLine("| `EconomyServiceJsonOperationsTester` | Economy JSON files | Manual inspector utility |");
        report.AppendLine("| `SystemTester` | SaveLoadLogic, RuntimeValidator, StatusDeltaChecker, CoachManager | Manual coroutine/scene diagnostic |");
        report.AppendLine("| `QuickTestPanel` | Coaches save/load, validation, delta checks, and singleton presence | Manual UI diagnostic |");
        report.AppendLine("| `CoachPreloadTester` | CoachManager preload/reload/fire behavior | Manual keyboard/UI diagnostic |");
        report.AppendLine("| `test_PackOpeningController`, `test_BoosterMarketAuto`, `test_AcquisitionHubController` | Older CCAS UI prototypes | Historical/commented-out, not executable |");

        report.AppendLine();
        report.AppendLine("## Engine-independent assessment");
        report.AppendLine();
        report.AppendLine("The current automated suites cannot be moved wholesale to a normal .NET unit-test project without changing their coverage: they instantiate Unity `GameObject`/`MonoBehaviour` services, invoke Unity lifecycle methods, load Unity-backed configuration, use `Application.persistentDataPath`, publish through Unity-connected services, and exercise file-backed state.");
        report.AppendLine();
        report.AppendLine("The practical extraction candidates are pure progression tier calculation, XP/idempotency rules, economy balance/transaction rules, duplicate-XP lookup, pack/drop calculations, and configuration parsing. Those rules are currently embedded in the Unity services/configuration boundary, so this run keeps them as Unity tests rather than creating a second test implementation that could drift. A future refactor can move those calculations behind plain C# interfaces and add a normal .NET test project without weakening the integration coverage above.");

        if (results.Any(result => !string.IsNullOrEmpty(result.Error)))
        {
            report.AppendLine();
            report.AppendLine("## Runner exceptions");
            foreach (SuiteResult result in results.Where(item => !string.IsNullOrEmpty(item.Error)))
            {
                report.AppendLine();
                report.AppendLine($"### `{result.Definition.Name}`");
                report.AppendLine("```text");
                report.AppendLine(result.Error);
                report.AppendLine("```");
            }
        }

        report.AppendLine();
        report.AppendLine("## Reproduction");
        report.AppendLine();
        report.AppendLine("Full run:");
        report.AppendLine("```text");
        report.AppendLine("Unity -batchmode -projectPath <path-to-MGI_Monorepo> -executeMethod TestSuiteRunner.RunAll -quit -logFile <path-to-log>");
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("Individual selection:");
        report.AppendLine("```text");
        report.AppendLine("MGI_TESTS=ccas-economy Unity -batchmode -projectPath <path-to-MGI_Monorepo> -executeMethod TestSuiteRunner.RunSelected -quit -logFile <path-to-log>");
        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("Available selections: `progression`, `progression-facilities`, `progression-ccas`, `ccas`, `ccas-progression`, `ccas-economy`, `ccas-boundary`, `ccas-scene-smoke`, `canonical-player-id`, `game-outcomes`, `game-outcome-rewards`, or `all`.");

        File.WriteAllText(reportPath, report.ToString());
    }
}
#endif
