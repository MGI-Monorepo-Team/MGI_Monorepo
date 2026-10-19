using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// ── Data models ──────────────────────────────────────────────────────────────

/// <summary>
/// Runtime team state: which coaches are assigned to the player's team.
/// Persisted to mgi_state/{playerId}/coaches/teams.json
/// </summary>
[Serializable]
public class TeamState
{
    public string team_id;
    public string player_id;
    public string offence_coach;       // coach_id or empty
    public string defence_coach;       // coach_id or empty
    public string special_teams_coach; // coach_id or empty
}

/// <summary>
/// A single active coach contract.
/// Persisted to mgi_state/{playerId}/coaches/coach_contracts.json
/// </summary>
[Serializable]
public class CoachContract
{
    public string id;
    public string player_id;
    public string team_id;
    public string coach_id;
    public string coach_type;    // "O" | "D" | "S"
    public float salary;
    public int contract_length;
    public int games_served;       // NEW: increments on match/week completion
    public string hired_at;      // ISO 8601
}

[Serializable]
public class CoachContractList
{
    public CoachContract[] contracts = new CoachContract[0];
}

/// <summary>Payload attached to the hire_coach event.</summary>
[Serializable]
public class HireCoachPayload
{
    public string team_id;
    public string coach_id;
    public string coach_type;
    public int cost_paid_coins;
}

/// <summary>Payload attached to the fire_coach event.</summary>
[Serializable]
public class FireCoachPayload
{
    public string team_id;
    public string coach_id;
    public string coach_type;
    public int refund_amount_coins;
}

/// <summary>
/// Unity JsonUtility-friendly coach XP bonus config.
/// The requested dictionary-like layout is represented with arrays so the
/// config can be parsed reliably without manual JSON deserialization.
/// </summary>
[Serializable]
public class CoachesBonusConfig
{
    public CoachTypeBonusEntry[] xp_bonus_rules = new CoachTypeBonusEntry[0];
    public SynergyBonusRule[] synergy_bonus = new SynergyBonusRule[0];
}

[Serializable]
public class CoachTypeBonusEntry
{
    public string coach_type;
    public XpSourceBonusRule[] source_rules = new XpSourceBonusRule[0];
}

[Serializable]
public class XpSourceBonusRule
{
    public string xp_source;
    public float base_bonus;
    public float rating_multiplier;
}

[Serializable]
public class SynergyBonusRule
{
    public string[] required = new string[0];
    public string xp_source;
    public float bonus;
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Single entry point for all coach catalog and hiring operations.
/// Reads the coach catalog from StreamingAssets (read-only).
/// Writes teams.json and coach_contracts.json via FilePathResolver.
/// Delegates wallet checks to EconomyService — never touches wallet.json directly.
/// No other class may write coaches/ runtime files.
/// </summary>
public static class CoachesService
{
    // Retained for compatibility; PlayerIdProvider is the canonical local resolver.
    public const string LocalPlayerId = PlayerIdProvider.DefaultPlayerId;
    private const string CoachHiringSpendSource = "coach_hiring";
    private const string CoachHiringRefundSource  = "coach_hiring_refund";
    private const string CoachFiringRefundSource  = "coach_firing_refund";
    private const string CoachBonusConfigFileName = "coaches_bonus_config.json";
    private const float REFUND_RATE = 0.5f; // tunable: fraction of hire cost refunded, scaled by games served
    public const float BASELINE_RATING = 12f;
    public const float NORMALIZATION_FACTOR = 1f;

    private static CoachesBonusConfig cachedBonusConfig;
    private static bool bonusConfigLoaded;

    private static string ResolvePlayerId(string playerId)
    {
        return string.IsNullOrWhiteSpace(playerId)
            ? PlayerIdProvider.Get()
            : playerId.Trim();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all coaches from the catalog that are not currently hired by this player.
    /// </summary>
    public static List<CoachDatabaseRecord> GetAvailableCoaches(string playerId = null)
    {
        playerId = ResolvePlayerId(playerId);

        var catalog = LoadCatalog();
        var state = LoadTeamState(playerId);

        var hiredIds = new HashSet<string>(StringComparer.Ordinal);
        if (state != null)
        {
            if (!string.IsNullOrEmpty(state.offence_coach)) hiredIds.Add(state.offence_coach);
            if (!string.IsNullOrEmpty(state.defence_coach)) hiredIds.Add(state.defence_coach);
            if (!string.IsNullOrEmpty(state.special_teams_coach)) hiredIds.Add(state.special_teams_coach);
        }

        return catalog.Where(c => !hiredIds.Contains(c.coach_id)).ToList();
    }

    /// <summary>
    /// Attempts to hire a coach for the given team.
    /// Checks wallet via EconomyService, updates teams.json and coach_contracts.json,
    /// then publishes a hire_coach event.
    /// Returns false if the coach is not found, funds are insufficient, or coach type is unknown.
    /// </summary>
    public static bool TryHireCoach(string teamId, string coachId,
        out CoachDatabaseRecord hiredCoach, string playerId = null)
    {
        playerId = ResolvePlayerId(playerId);
        hiredCoach = null;

        // 1. Resolve coach from catalog
        var catalog = LoadCatalog();
        var coach = catalog.FirstOrDefault(c => c.coach_id == coachId);
        if (coach == null)
        {
            Debug.LogWarning($"[CoachesService] Coach '{coachId}' not found in catalog.");
            return false;
        }

        // 2. Validate coach type
        string coachType = coach.coach_type?.ToUpper();
        if (coachType != "O" && coachType != "D" && coachType != "S")
        {
            Debug.LogWarning($"[CoachesService] Unknown coach type '{coach.coach_type}' for coach '{coachId}'.");
            return false;
        }

        // 3. Idempotency: if this hire is already applied, no-op successfully.
        var existingState = LoadTeamState(playerId);
        var existingAssigned = GetAssignedCoachId(existingState, coachType);
        if (string.Equals(existingAssigned, coachId, StringComparison.Ordinal))
        {
            hiredCoach = coach;
            return true;
        }

        var existingContracts = GetActiveContracts(playerId);
        if (existingContracts.Any(c =>
                string.Equals(c.team_id, teamId, StringComparison.Ordinal) &&
                string.Equals(c.coach_id, coachId, StringComparison.Ordinal) &&
                string.Equals(NormalizeCoachType(c.coach_type), coachType, StringComparison.Ordinal)))
        {
            // Contract exists but state may not; treat as already-hired and avoid double-spend.
            var stateToRepair = existingState ?? new TeamState { player_id = playerId, team_id = teamId };
            stateToRepair.team_id = teamId;
            ApplyAssignedCoachId(stateToRepair, coachType, coachId);
            _ = SaveTeamState(playerId, stateToRepair);
            hiredCoach = coach;
            return true;
        }

        // 4. Check and deduct wallet via EconomyService
        int hireCost = Mathf.RoundToInt(coach.salary * 1_000_000f / 52f);
        var economy = new EconomyService();
        if (!economy.TrySpend(playerId, hireCost, 0, CoachHiringSpendSource, out _))
        {
            Debug.LogWarning($"[CoachesService] Insufficient funds to hire {coach.coach_name} (cost: {hireCost} coins).");
            return false;
        }

        // 5. Update runtime team state
        var state = existingState ?? new TeamState
        {
            team_id = teamId,
            player_id = playerId
        };
        state.team_id = teamId;

        ApplyAssignedCoachId(state, coachType, coachId);

        var teamSaved = SaveTeamState(playerId, state);
        var contractSaved = SaveCoachContract(playerId, teamId, coach);
        if (!teamSaved || !contractSaved)
        {
            // Persistence failed after spend; refund and abort without publishing event.
            economy.AddCurrency(playerId, hireCost, 0, CoachHiringRefundSource);
            Debug.LogError("[CoachesService] Hire failed while saving state/contracts; spend has been refunded.");
            return false;
        }

        // 6. Publish hire_coach event
        var payload = new HireCoachPayload
        {
            team_id = teamId,
            coach_id = coachId,
            coach_type = coachType,
            cost_paid_coins = hireCost
        };

        EventBus.Publish(new EventBus.EventEnvelope
        {
            event_id = Guid.NewGuid().ToString(),
            event_type = "hire_coach",
            player_id = playerId,
            timestamp = DateTime.UtcNow.ToString("o"),
            payloadJson = JsonUtility.ToJson(payload)
        });

        Debug.Log($"[CoachesService] Hired {coach.coach_name} ({coachType}) for team '{teamId}'. Cost: {hireCost} coins.");
        hiredCoach = coach;
        return true;
    }

    /// <summary>Returns the current runtime team state for this player, or null if none exists yet.</summary>
    public static bool ResetPlayerCoachState(string playerId = null)
    {
        playerId = ResolvePlayerId(playerId);

        foreach (var fileName in new[] { "teams.json", "coach_contracts.json" })
        {
            var path = FilePathResolver.GetCoachesPath(playerId, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var resetState = new TeamState
        {
            player_id = playerId,
            team_id = string.Empty,
            offence_coach = string.Empty,
            defence_coach = string.Empty,
            special_teams_coach = string.Empty
        };

        var resetContracts = new CoachContractList();

        bool teamSaved = SaveTeamState(playerId, resetState);
        bool contractsSaved = TryWriteAllTextAtomic(
            FilePathResolver.GetCoachesPath(playerId, "coach_contracts.json"),
            JsonUtility.ToJson(resetContracts, true));

        if (!teamSaved || !contractsSaved)
        {
            Debug.LogError($"[CoachesService] Failed to create reset state files for '{playerId}'.");
            return false;
        }

        Debug.Log($"[CoachesService] Reset coach state for '{playerId}'.");
        return true;
    }

    public static TeamState GetTeamState(string playerId = null)
    {
        return LoadTeamState(ResolvePlayerId(playerId));
    }

    /// <summary>Returns all active coach contracts for this player.</summary>
    public static List<CoachContract> GetActiveContracts(string playerId = null)
    {
        playerId = ResolvePlayerId(playerId);
        var path = FilePathResolver.GetCoachesPath(playerId, "coach_contracts.json");
        if (!File.Exists(path)) return new List<CoachContract>();

        try
        {
            var list = JsonUtility.FromJson<CoachContractList>(File.ReadAllText(path));
            return new List<CoachContract>(list.contracts ?? new CoachContract[0]);
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoachesService] Failed to read contracts: {e.Message}");
            return new List<CoachContract>();
        }
    }

    /// <summary>Look up a single coach from the catalog by id.</summary>
    public static CoachDatabaseRecord GetCoachById(string coachId)
    {
        return LoadCatalog().FirstOrDefault(c => c.coach_id == coachId);
    }
    public static float GetTeamRating(string playerId = null)
    {
        playerId = ResolvePlayerId(playerId);
        var state = LoadTeamState(playerId);
        float totalBonus = 0;

        if (state != null)
        {
            if (!string.IsNullOrEmpty(state.offence_coach))
            {
                var dbRecord = GetCoachById(state.offence_coach);
                if (dbRecord != null) totalBonus += CoachData.CreateFromDatabaseRecord(dbRecord).offenseBonus;
            }
            if (!string.IsNullOrEmpty(state.defence_coach))
            {
                var dbRecord = GetCoachById(state.defence_coach);
                if (dbRecord != null) totalBonus += CoachData.CreateFromDatabaseRecord(dbRecord).defenseBonus;
            }
            if (!string.IsNullOrEmpty(state.special_teams_coach))
            {
                var dbRecord = GetCoachById(state.special_teams_coach);
                if (dbRecord != null) totalBonus += CoachData.CreateFromDatabaseRecord(dbRecord).specialTeamsBonus;
            }
        }

        return BASELINE_RATING + (totalBonus / NORMALIZATION_FACTOR);
    }

    /// <summary>
    /// Removes the coach of the given type from the team's state and contracts,
    /// then refunds the weekly salary and publishes a fire_coach event.
    /// </summary>
    public static bool FireCoach(string coachType, string playerId = null)
    {
        playerId = ResolvePlayerId(playerId);
        coachType = NormalizeCoachType(coachType);

        if (coachType != "O" && coachType != "D" && coachType != "S")
        {
            Debug.LogWarning($"[CoachesService] Unknown coach type '{coachType}' for fire operation.");
            return false;
        }

        var state = LoadTeamState(playerId);
        if (state == null)
        {
            Debug.LogWarning("[CoachesService] No team state found; nothing to fire.");
            return false;
        }

        var firedCoachId = GetAssignedCoachId(state, coachType);
        if (string.IsNullOrEmpty(firedCoachId))
        {
            Debug.LogWarning($"[CoachesService] No {coachType} coach assigned; nothing to fire.");
            return false;
        }

        string teamId = state.team_id;

        // Read salary from the contract before removing it.
        var activeContracts = GetActiveContracts(playerId);
        var contract = activeContracts.FirstOrDefault(c =>
            string.Equals(NormalizeCoachType(c.coach_type), coachType, StringComparison.Ordinal));
        int refundAmount = 0;
        if (contract != null)
        {
            int hireCost = Mathf.RoundToInt(contract.salary * 1_000_000f / 52f);
            int gamesServed = Mathf.Clamp(contract.games_served, 0, Math.Max(contract.contract_length, 1));
            float refundFraction = 1f - ((float)gamesServed / Math.Max(contract.contract_length, 1));
            refundAmount = Mathf.RoundToInt(hireCost * REFUND_RATE * refundFraction);
            Debug.Log($"[CoachesService] Refund calc: hireCost={hireCost}, gamesServed={gamesServed}, contractLength={contract?.contract_length}, refundAmount={refundAmount}");
        }
        

        ApplyAssignedCoachId(state, coachType, string.Empty);
        if (!SaveTeamState(playerId, state))
        {
            Debug.LogError("[CoachesService] Failed to save team state after firing coach.");
            return false;
        }

        if (!RemoveCoachContract(playerId, coachType))
        {
            Debug.LogError("[CoachesService] Failed to remove coach contract; refund and event suppressed to avoid inconsistent state.");
            return false;
        }

        if (refundAmount > 0)
            new EconomyService().AddCurrency(playerId, refundAmount, 0, CoachFiringRefundSource);

        EventBus.Publish(new EventBus.EventEnvelope
        {
            event_id   = Guid.NewGuid().ToString(),
            event_type = "fire_coach",
            player_id  = playerId,
            timestamp  = DateTime.UtcNow.ToString("o"),
            payloadJson = JsonUtility.ToJson(new FireCoachPayload
            {
                team_id    = teamId,
                coach_id   = firedCoachId,
                coach_type = coachType,
                refund_amount_coins = refundAmount
            })
        });

        Debug.Log($"[CoachesService] Fired coach '{firedCoachId}' ({coachType}) from team '{teamId}'.");
        return true;
    }

    /// <summary>
    /// Returns the XP bonus multiplier (additive) for the given player and XP source.
    /// Sums bonuses from all hired coaches whose rules match the source, then adds synergy.
    /// Read-only: no file writes, no hiring changes, no XP application.
    /// </summary>
    public static float GetCoachXpBonusPercent(string playerId, string xpSource)
    {
        playerId = ResolvePlayerId(playerId);
        var normalizedXpSource = NormalizeXpSourceForCoachBonus(xpSource);
        if (string.IsNullOrEmpty(normalizedXpSource)) return 0f;

        var state = GetTeamState(playerId);
        if (state == null) return 0f;

        var config = LoadBonusConfig();
        if (config?.xp_bonus_rules == null)
        {
            Debug.LogWarning("[CoachesService] Coach XP bonus config could not be loaded.");
            return 0f;
        }

        float totalBonus = 0f;

        foreach (var typeEntry in config.xp_bonus_rules)
        {
            if (typeEntry == null) continue;

            var rule = typeEntry.source_rules?.FirstOrDefault(r =>
                r != null &&
                string.Equals(r.xp_source, normalizedXpSource, StringComparison.OrdinalIgnoreCase));
            if (rule == null) continue;

            var coachId = GetAssignedCoachId(state, NormalizeCoachType(typeEntry.coach_type));
            if (string.IsNullOrEmpty(coachId)) continue;

            var coach = GetCoachById(coachId);
            if (coach == null) continue;

            totalBonus += CalculateCoachRuleBonus(coach, rule);
        }

        // Synergy bonus: applies only when its source rule matches and all
        // required coach types are hired.
        if (config.synergy_bonus != null)
        {
            foreach (var synergy in config.synergy_bonus)
            {
                if (synergy == null || synergy.required == null || synergy.required.Length == 0) continue;
                if (!string.Equals(synergy.xp_source, normalizedXpSource, StringComparison.OrdinalIgnoreCase)) continue;
                if (synergy.required.All(t => HasAssignedCoachType(state, t)))
                    totalBonus += synergy.bonus;
            }
        }

        return totalBonus;
    }

    /// <summary>
    /// Maps progression XP sources to coaches_bonus_config xp_source keys.
    /// Returns null when coach per-source bonuses should not apply.
    /// </summary>
    private static string NormalizeXpSourceForCoachBonus(string xpSource)
    {
        if (string.IsNullOrWhiteSpace(xpSource))
            return null;

        var s = xpSource.Trim().ToLowerInvariant();

        if (s.StartsWith("duplicate_card"))
            return null;

        if (s.Contains("match") && s.Contains("win"))
            return "win";
        if (s.Contains("match") && s.Contains("loss"))
            return "loss";

        if (s.Contains("offensive") || s.Contains("offence") || s.Contains("offense_drill"))
            return "offensive_drill";
        if (s.Contains("defensive") || s.Contains("defence") || s.Contains("defense_drill"))
            return "defensive_drill";
        if (s.Contains("special_teams") || s.Contains("special_team"))
            return "special_teams_drill";

        return s;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static CoachesBonusConfig LoadBonusConfig()
    {
        if (bonusConfigLoaded)
        {
            return cachedBonusConfig;
        }

        bonusConfigLoaded = true;

        string path = Path.Combine(Application.streamingAssetsPath, "Coaches", CoachBonusConfigFileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[CoachesService] Bonus config not found at '{path}'.");
            cachedBonusConfig = null;
            return null;
        }

        try
        {
            cachedBonusConfig = JsonUtility.FromJson<CoachesBonusConfig>(File.ReadAllText(path)) ?? new CoachesBonusConfig();
            return cachedBonusConfig;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CoachesService] Failed to load bonus config: {e.Message}");
            cachedBonusConfig = null;
            return null;
        }
    }

    private static bool HasAssignedCoachType(TeamState state, string coachType)
    {
        if (state == null)
        {
            return false;
        }

        return !string.IsNullOrEmpty(GetAssignedCoachId(state, coachType));
    }

    private static float CalculateCoachRuleBonus(CoachDatabaseRecord coach, XpSourceBonusRule rule)
    {
        if (coach == null || rule == null)
        {
            return 0f;
        }

        return rule.base_bonus + (coach.overall_rating * rule.rating_multiplier);
    }

    /// <summary>
    /// Derives a static overall rating from the four stats relevant to the coach's role.
    /// This is intentionally independent of Team Rating, contracts, and XP application.
    /// </summary>
    public static float CalculateStaticOverallRating(CoachDatabaseRecord coach)
    {
        if (coach == null)
            return 0f;

        switch (NormalizeSchemaCoachType(coach.coach_type))
        {
            case "O":
                return (coach.passing_efficiency + coach.rush + coach.red_zone_conversion + coach.play_variation) / 4f;
            case "D":
                return (coach.coverage_discipline + coach.run_defence + coach.turnover + coach.pressure_control) / 4f;
            case "S":
                return (coach.kickoff_instance + coach.return_coverage + coach.field_goal_accuracy + coach.return_speed) / 4f;
            default:
                return 0f;
        }
    }

    private static List<CoachDatabaseRecord> ApplyStaticOverallRatings(IEnumerable<CoachDatabaseRecord> catalog)
    {
        var ratedCatalog = new List<CoachDatabaseRecord>();
        if (catalog == null)
            return ratedCatalog;

        foreach (var coach in catalog)
        {
            if (coach == null)
                continue;

            var coachType = NormalizeSchemaCoachType(coach.coach_type);
            if (coachType != "O" && coachType != "D" && coachType != "S")
                Debug.LogWarning($"[CoachesService] Unsupported coach type '{coach.coach_type}' for coach '{coach.coach_id}'. Overall rating set to 0.");

            coach.overall_rating = CalculateStaticOverallRating(coach);
            ratedCatalog.Add(coach);
        }

        return ratedCatalog;
    }

    private static List<CoachDatabaseRecord> LoadCatalog()
    {
        // Primary: coaches_schema_data.json (canonical catalog from main)
        string schemaPath = Path.Combine(Application.streamingAssetsPath, "Coaches", "coaches_schema_data.json");
        if (File.Exists(schemaPath))
        {
            try
            {
                string json = File.ReadAllText(schemaPath);
                var file = JsonUtility.FromJson<CoachesSchemaFile>(json);
                if (file?.coach != null && file.coach.Length > 0)
                {
                    return ApplyStaticOverallRatings(file.coach.Select(r => r.ToCoachDatabaseRecord()));
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[CoachesService] Failed to load coaches_schema_data.json: {e.Message}");
            }
        }

        // Fallback: legacy coach.json flat array
        string legacyPath = Path.Combine(Application.streamingAssetsPath, "Coaches", "Database", "coach.json");
        if (!File.Exists(legacyPath))
        {
            Debug.LogWarning("[CoachesService] No coach catalog found.");
            return new List<CoachDatabaseRecord>();
        }

        try
        {
            string json = File.ReadAllText(legacyPath);
            var wrapper = JsonUtility.FromJson<JsonWrapper>("{\"Items\":" + json + "}");
            return wrapper?.Items != null
                ? ApplyStaticOverallRatings(wrapper.Items)
                : new List<CoachDatabaseRecord>();
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoachesService] Failed to load legacy coach.json: {e.Message}");
            return new List<CoachDatabaseRecord>();
        }
    }

    // ── Schema classes for coaches_schema_data.json ──────────────────────────

    [Serializable]
    private class CoachesSchemaFile
    {
        public string schema_version;
        public CoachSchemaRecord[] coach;
    }

    [Serializable]
    private class CoachSchemaRecord
    {
        public string coach_id;
        public string coach_name;
        public string coach_type;
        public int experience;
        public int championship_won;
        public float overall_rating_calculated;
        public float salary;
        public int contract_length;
        public int bonus_percentage;
        public string current_team_assigned_when_coach_is_hired;
        public string prev_team;
        public float run_defence;
        public float pressure_control;
        public float coverage_discipline;
        public float turnover;
        public float passing_efficiency;
        public float rush;
        public float red_zone_conversion;
        public float play_variation;
        public float field_goal_accuracy;
        public float kickoff_instance;
        public float return_speed;
        public float return_coverage;

        public CoachDatabaseRecord ToCoachDatabaseRecord() => new CoachDatabaseRecord
        {
            coach_id              = coach_id,
            coach_name            = coach_name,
            coach_type            = NormalizeSchemaCoachType(coach_type),
            experience            = experience,
            championship_won      = championship_won,
            overall_rating        = overall_rating_calculated,
            salary                = salary,
            contract_length       = contract_length,
            bonus_percentage      = bonus_percentage,
            current_team          = current_team_assigned_when_coach_is_hired,
            prev_team             = prev_team,
            run_defence           = run_defence,
            pressure_control      = pressure_control,
            coverage_discipline   = coverage_discipline,
            turnover              = turnover,
            passing_efficiency    = passing_efficiency,
            rush                  = rush,
            red_zone_conversion   = red_zone_conversion,
            play_variation        = play_variation,
            field_goal_accuracy   = field_goal_accuracy,
            kickoff_instance      = kickoff_instance,
            return_speed          = return_speed,
            return_coverage       = return_coverage
        };
    }

    private static TeamState LoadTeamState(string playerId)
    {
        string path = FilePathResolver.GetCoachesPath(playerId, "teams.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonUtility.FromJson<TeamState>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoachesService] Failed to load team state: {e.Message}");
            return null;
        }
    }

    private static bool SaveTeamState(string playerId, TeamState state)
    {
        string path = FilePathResolver.GetCoachesPath(playerId, "teams.json");
        try
        {
            return TryWriteAllTextAtomic(path, JsonUtility.ToJson(state, true));
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoachesService] Failed to save team state: {e.Message}");
            return false;
        }
    }

    private static bool SaveCoachContract(string playerId, string teamId, CoachDatabaseRecord coach)
    {
        string path = FilePathResolver.GetCoachesPath(playerId, "coach_contracts.json");

        CoachContractList list;
        if (File.Exists(path))
        {
            try { list = JsonUtility.FromJson<CoachContractList>(File.ReadAllText(path)); }
            catch { list = new CoachContractList(); }
        }
        else
        {
            list = new CoachContractList();
        }

        // One active contract per coach type — replace if same type already exists
        var contracts = new List<CoachContract>(list.contracts ?? new CoachContract[0]);
        contracts.RemoveAll(c => string.Equals(c.coach_type, coach.coach_type, StringComparison.OrdinalIgnoreCase));

        contracts.Add(new CoachContract
        {
            id = Guid.NewGuid().ToString(),
            player_id = playerId,
            team_id = teamId,
            coach_id = coach.coach_id,
            coach_type = coach.coach_type,
            salary = coach.salary,
            contract_length = coach.contract_length,
            hired_at = DateTime.UtcNow.ToString("o")
        });

        list.contracts = contracts.ToArray();

        try
        {
            return TryWriteAllTextAtomic(path, JsonUtility.ToJson(list, true));
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoachesService] Failed to save contracts: {e.Message}");
            return false;
        }
    }

    // Maps catalog values like "ST" → "S" so all coach types are O | D | S.
    private static string NormalizeSchemaCoachType(string raw)
    {
        var upper = raw?.ToUpperInvariant();
        return upper == "ST" ? "S" : upper;
    }

    private static string NormalizeCoachType(string coachType)
    {
        return coachType?.ToUpperInvariant();
    }

    private static string GetAssignedCoachId(TeamState state, string coachType)
    {
        if (state == null) return null;
        return coachType switch
        {
            "O" => state.offence_coach,
            "D" => state.defence_coach,
            "S" => state.special_teams_coach,
            _ => null
        };
    }

    private static void ApplyAssignedCoachId(TeamState state, string coachType, string coachId)
    {
        switch (coachType)
        {
            case "O": state.offence_coach = coachId; break;
            case "D": state.defence_coach = coachId; break;
            case "S": state.special_teams_coach = coachId; break;
        }
    }

    private static bool RemoveCoachContract(string playerId, string coachType)
    {
        string path = FilePathResolver.GetCoachesPath(playerId, "coach_contracts.json");
        if (!File.Exists(path)) return true;
        try
        {
            var list = JsonUtility.FromJson<CoachContractList>(File.ReadAllText(path));
            if (list?.contracts == null) return true;
            var contracts = new List<CoachContract>(list.contracts);
            contracts.RemoveAll(c => string.Equals(NormalizeCoachType(c.coach_type), coachType, StringComparison.Ordinal));
            list.contracts = contracts.ToArray();
            return TryWriteAllTextAtomic(path, JsonUtility.ToJson(list, true));
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoachesService] Failed to remove coach contract: {e.Message}");
            return false;
        }
    }

    private static bool TryWriteAllTextAtomic(string destinationPath, string contents)
    {
        try
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = destinationPath + ".tmp";
            File.WriteAllText(tempPath, contents);

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(tempPath, destinationPath);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[CoachesService] Atomic write failed for '{destinationPath}': {e.Message}");
            return false;
        }
    }
}
