using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SimpleJSON;


public class ProgressionService : MonoBehaviour
{
    public static ProgressionService Instance { get; private set; }

    // In-memory cache of progression states to avoid repeated file I/O
    private Dictionary<string, PlayerProgressionState> _progressionCache = 
        new Dictionary<string, PlayerProgressionState>();

    // Read-only configuration loaded from progression.json
    private ProgressionConfig _progressionConfig;

    // Read-only facilities lookup (no upgrades or state writes from Progression)
    private FacilitiesService _facilitiesReader;

    // Events
    public event Action<string, float> OnXpUpdated; // (playerId, newTotalXp - full precision, not display-rounded)
    public event Action<string, string> OnTierChanged; // (playerId, newTier)

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        LoadProgressionConfig();
    }


    private void LoadProgressionConfig()
    {
        var configPath = Path.Combine(Application.streamingAssetsPath, "Progression", "progression.json");
        
        if (!File.Exists(configPath))
        {
            Debug.LogError($"[ProgressionService] Config not found: {configPath}");
            return;
        }

        try
        {
            var json = JSONNode.Parse(File.ReadAllText(configPath));
            _progressionConfig = new ProgressionConfig(json);
            Debug.Log("[ProgressionService] Progression config loaded successfully");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProgressionService] Failed to parse progression config: {ex}");
        }
    }

    public PlayerProgressionState GetState(string playerId, bool createIfMissing = true)
    {
        if (string.IsNullOrEmpty(playerId))
        {
            Debug.LogError("[ProgressionService] GetState called with null/empty playerId");
            return null;
        }

        // Check cache first
        if (_progressionCache.TryGetValue(playerId, out var cachedState))
        {
            return cachedState;
        }

        // Try to load from disk
        var state = LoadProgressionStateFromDisk(playerId);
        if (state != null)
        {
            _progressionCache[playerId] = state;
            return state;
        }

        // Create new if allowed and doesn't exist
        if (createIfMissing)
        {
            state = new PlayerProgressionState(playerId);
            _progressionCache[playerId] = state;
            SaveProgressionStateToDisk(state);
            return state;
        }

        return null;
    }

   
    /// <summary>
    /// Records an XP grant. Base amount is adjusted by facility multipliers, then coach bonuses.
    /// eventId prevents applying the same grant twice (e.g. CCAS pack opens).
    /// </summary>
    public void AddXp(string playerId, int xp, string source, string eventId)
    {
        if (string.IsNullOrEmpty(playerId))
        {
            Debug.LogError("[ProgressionService] AddXp called with null/empty playerId");
            return;
        }

        if (xp <= 0)
        {
            Debug.LogWarning($"[ProgressionService] AddXp called with non-positive amount: {xp}");
            return;
        }

        var state = GetState(playerId, createIfMissing: true);
        if (state == null)
        {
            Debug.LogError($"[ProgressionService] Could not get or create state for {playerId}");
            return;
        }

        // Idempotent grant: same eventId must not apply XP twice (e.g. CCAS pack re-open).
        bool isDuplicateEvent = state.xp_history.Exists(e => e.id == eventId);
        if (isDuplicateEvent)
        {
            Debug.Log($"[ProgressionService] Duplicate XP ignored: {eventId}");
            return;
        }

        float finalXp = ApplyXpBonuses(playerId, xp, source);

        var historyEntry = new XpHistoryEntry(playerId, finalXp, source)
        {
            id = eventId
        };

        state.xp_history.Add(historyEntry);

        float oldXp = state.current_xp;
        state.current_xp += finalXp;

        // Recalculate tier
        string oldTier = state.current_tier;
        state.current_tier = CalculateTierForXp(state.current_xp);

        // Persist changes
        SaveProgressionStateToDisk(state);

        // Publish event
        PublishXpUpdatedEvent(playerId, oldXp, state.current_xp, oldTier, state.current_tier);

        if (finalXp != xp)
        {
            Debug.Log(
                $"[ProgressionService] Added {finalXp} XP to {playerId} (source: {source}, base: {xp}). " +
                $"Total: {state.current_xp}, Tier: {state.current_tier}");
        }
        else
        {
            Debug.Log($"[ProgressionService] Added {finalXp} XP to {playerId} (source: {source}). Total: {state.current_xp}, Tier: {state.current_tier}");
        }
    }

    /// <summary>
    /// Read-only facilities lookup. Uses FacilitiesService getters only.
    /// </summary>
    private FacilitiesService GetFacilitiesReader()
    {
        return _facilitiesReader ??= new FacilitiesService();
    }

    // Bonuses are deliberately not rounded here: rounding after the Facilities
    // step and again after the Coaches step compounded into visible drift. The
    // full value is kept and only the UI turns it into an integer (XpFormat).
    private float ApplyXpBonuses(string playerId, int baseXp, string source)
    {
        float xp = ApplyFacilityXpMultiplier(playerId, baseXp, source);
        return ApplyCoachXpBonus(playerId, xp, source);
    }

    private float ApplyFacilityXpMultiplier(string playerId, float baseXp, string source)
    {
        float multiplier = GetFacilitiesReader().GetProgressionXpMultiplier(playerId, source);
        if (multiplier <= 0f)
        {
            multiplier = 1f;
        }

        if (Mathf.Approximately(multiplier, 1f))
        {
            return baseXp;
        }

        return Mathf.Max(1f, baseXp * multiplier);
    }

    /// <summary>
    /// Read-only coach bonus from CoachesService (active assignments + coaches_bonus_config.json).
    /// Applied on top of facility-adjusted XP.
    /// </summary>
    private static float ApplyCoachXpBonus(string playerId, float xpAfterFacilities, string source)
    {
        float coachBonusPercent = CoachesService.GetCoachXpBonusPercent(playerId, source);
        if (coachBonusPercent <= 0f || Mathf.Approximately(coachBonusPercent, 0f))
        {
            return xpAfterFacilities;
        }

        return Mathf.Max(1f, xpAfterFacilities * (1f + coachBonusPercent));
    }


    public TierData GetCurrentTier(string playerId)
    {
        var state = GetState(playerId, createIfMissing: false);
        if (state == null || _progressionConfig == null)
        {
            return null;
        }

        return _progressionConfig.GetTierData(state.current_tier);
    }

    public TierData GetTierData(string tierName)
    {
        if (_progressionConfig == null)
        {
            return null;
        }

        return _progressionConfig.GetTierData(tierName);
    }

    public Dictionary<string, TierData> GetAllTiers()
    {
        return _progressionConfig?.GetAllTiers() ?? new Dictionary<string, TierData>();
    }

    /// <summary>
    /// Base XP amounts from progression.json (match, weekly training, season placement).
    /// Null if the config is missing or incomplete - callers must not award XP then.
    /// </summary>
    public XpRewardRules GetXpRewardRules()
    {
        return _progressionConfig?.RewardRules;
    }

   
    private string CalculateTierForXp(float totalXp)
    {
        if (_progressionConfig == null)
        {
            return "rookie";
        }

        return _progressionConfig.GetTierForXp(totalXp);
    }

    private PlayerProgressionState LoadProgressionStateFromDisk(string playerId)
    {
        try
        {
            var statePath = FilePathResolver.GetProgressionPath(playerId, "progression_state.json");
            var historyPath = FilePathResolver.GetProgressionPath(playerId, "xp_history.jsonl");

            bool stateExists = File.Exists(statePath);
            bool historyExists = File.Exists(historyPath);
            if (!stateExists && !historyExists)
            {
                // Nothing on disk for this player - let GetState's createIfMissing decide what happens next.
                return null;
            }

            var state = new PlayerProgressionState(playerId);

            // Load current state (XP and tier) if it exists
            // If progression_state.json doesn't exist, state starts with defaults (0 XP, rookie tier)
            if (stateExists)
            {
                var json = JSONNode.Parse(File.ReadAllText(statePath));
                state.current_xp = json["current_xp"].AsFloat;
                state.current_tier = json["current_tier"].Value;
            }

            // Load XP history for this player only (jsonl may contain legacy mixed entries)
            state.xp_history = LoadXpHistoryForPlayer(historyPath, playerId);

            return state;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProgressionService] Failed to load progression state for {playerId}: {ex}");
            return null;
        }
    }

 
    private void SaveProgressionStateToDisk(PlayerProgressionState state)
    {
        try
        {
            var statePath = FilePathResolver.GetProgressionPath(state.player_id, "progression_state.json");
            var historyPath = FilePathResolver.GetProgressionPath(state.player_id, "xp_history.jsonl");
            
            // Save current state (XP and tier) - overwrite
            var json = new JSONObject();
            json["player_id"] = state.player_id;
            json["current_xp"] = XpFormat.ToStorage(state.current_xp);
            json["current_tier"] = state.current_tier;
            File.WriteAllText(statePath, json.ToString(2));

            // Append NEW XP history entries to JSONL (one entry per line)
            // Load existing history count to know which entries to append
            int existingEntryCount = 0;
            if (File.Exists(historyPath))
            {
                var existingLines = File.ReadAllLines(historyPath);
                existingEntryCount = existingLines.Length;
            }

            // Append only new entries
            if (state.xp_history.Count > existingEntryCount)
            {
                using (var writer = File.AppendText(historyPath))
                {
                    for (int i = existingEntryCount; i < state.xp_history.Count; i++)
                    {
                        var entry = state.xp_history[i];
                        var entryJson = new JSONObject();
                        entryJson["id"] = entry.id;
                        entryJson["player_id"] = entry.player_id;
                        entryJson["timestamp"] = entry.timestamp;
                        entryJson["xp_gained"] = XpFormat.ToStorage(entry.xp_gained);
                        entryJson["source"] = entry.source;
                        writer.WriteLine(entryJson.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProgressionService] Failed to save progression state for {state.player_id}: {ex}");
        }
    }


    private void PublishXpUpdatedEvent(string playerId, float oldXp, float newXp, string oldTier, string newTier)
    {
        var payload = new JSONObject();
        payload["player_id"] = playerId;
        payload["old_xp"] = XpFormat.ToStorage(oldXp);
        payload["new_xp"] = XpFormat.ToStorage(newXp);
        payload["xp_gained"] = XpFormat.ToStorage(newXp - oldXp);
        payload["old_tier"] = oldTier;
        payload["new_tier"] = newTier;

        var evt = new EventBus.EventEnvelope
        {
            event_id = Guid.NewGuid().ToString(),
            event_type = "xp_updated",
            player_id = playerId,
            timestamp = DateTime.UtcNow.ToString("o"),
            payloadJson = payload.ToString()
        };

        EventBus.Publish(evt);

        // Fire local event
        OnXpUpdated?.Invoke(playerId, newXp);
        
        if (oldTier != newTier)
        {
            OnTierChanged?.Invoke(playerId, newTier);
        }
    }

    public void ResetAllPlayerXp()
    {
        if (_progressionCache == null || _progressionCache.Count == 0)
        {
            Debug.Log("[ProgressionService] No player progression states to reset.");
            return;
        }

        foreach (var kvp in _progressionCache)
        {
            var state = kvp.Value;
            if (state != null)
            {
                state.current_xp = 0;
                state.current_tier = "rookie"; // reset to starting tier
                state.xp_history.Clear();

                // Save the cleared state to disk
                SaveProgressionStateToDisk(state);
            }
        }

        Debug.Log("[ProgressionService] All player XP has been reset for this session.");
    }

    /// <summary>
    /// Clears progression state and XP history for one player (new season/session).
    /// </summary>
    public void ClearPlayerProgression(string playerId)
    {
        if (string.IsNullOrEmpty(playerId))
        {
            Debug.LogError("[ProgressionService] ClearPlayerProgression called with null/empty playerId");
            return;
        }

        _progressionCache.Remove(playerId);

        try
        {
            var progressionDir = Path.GetDirectoryName(
                FilePathResolver.GetProgressionPath(playerId, "progression_state.json"));
            if (!Directory.Exists(progressionDir)) return;

            var stateFile = Path.Combine(progressionDir, "progression_state.json");
            var historyFile = Path.Combine(progressionDir, "xp_history.jsonl");
            if (File.Exists(stateFile)) File.Delete(stateFile);
            if (File.Exists(historyFile)) File.Delete(historyFile);

            Debug.Log($"[ProgressionService] Cleared progression data for {playerId}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProgressionService] Failed to clear progression for {playerId}: {ex}");
        }
    }

    /// <summary>
    /// Clears all player progression data from both cache and disk.
    /// Called at app/session start before a new season is created.
    /// </summary>
    public void ClearAllPlayerProgression()
    {
        try
        {
            // Clear the cache
            _progressionCache.Clear();

            // Delete all progression files from disk (persistentDataPath, not StreamingAssets)
            var progressionRootDir = Path.Combine(Application.persistentDataPath, "mgi_state");
            if (Directory.Exists(progressionRootDir))
            {
                // Find all player directories
                var playerDirs = Directory.GetDirectories(progressionRootDir);
                foreach (var playerDir in playerDirs)
                {
                    var progressionDir = Path.Combine(playerDir, "progression");
                    if (Directory.Exists(progressionDir))
                    {
                        var stateFile = Path.Combine(progressionDir, "progression_state.json");
                        var historyFile = Path.Combine(progressionDir, "xp_history.jsonl");
                        if (File.Exists(stateFile))
                        {
                            File.Delete(stateFile);
                            Debug.Log($"[ProgressionService] Deleted progression state: {stateFile}");
                        }
                        if (File.Exists(historyFile))
                        {
                            File.Delete(historyFile);
                        }
                    }
                }
            }

            Debug.Log("[ProgressionService] All player progression data cleared for new session.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ProgressionService] Failed to clear player progression data: {ex}");
        }
    }

   

    private class ProgressionConfig
    {
        private Dictionary<string, TierData> _tiers = new Dictionary<string, TierData>();

        // Null when xp_reward_rules is missing or incomplete; callers must treat that as
        // "no XP can be awarded" rather than guessing amounts.
        public XpRewardRules RewardRules { get; private set; }

        public ProgressionConfig(JSONNode configJson)
        {
            RewardRules = ParseRewardRules(configJson["xp_reward_rules"]);

            // Parse tier_progression section
            var tiersNode = configJson["tier_progression"];
            foreach (KeyValuePair<string, JSONNode> kvp in tiersNode)
            {
                var tierName = kvp.Key;
                var tierNode = kvp.Value;
                var tierData = new TierData
                {
                    min_xp = tierNode["min_xp"].AsInt,
                    max_xp = tierNode["max_xp"].AsInt,
                    display_name = tierNode["display_name"].Value,
                    unlock_features = new List<string>()
                };

                foreach (var feature in tierNode["unlock_features"].AsArray)
                {
                    tierData.unlock_features.Add(feature.Value);
                }

                _tiers[tierName] = tierData;
            }
        }

        private static XpRewardRules ParseRewardRules(JSONNode node)
        {
            if (node == null || node.IsNull || !node.IsObject)
            {
                Debug.LogError("[ProgressionService] progression.json has no xp_reward_rules; no match, training or season XP can be awarded.");
                return null;
            }

            var seasonRewards = node["season_rewards"];
            var rules = new XpRewardRules();

            // Evaluate every key (no short-circuit) so one run reports all missing values.
            bool ok = true;
            ok &= TryRead(node, "match_played", "xp_reward_rules.match_played", out rules.match_played);
            ok &= TryRead(node, "win", "xp_reward_rules.win", out rules.win);
            ok &= TryRead(node, "loss", "xp_reward_rules.loss", out rules.loss);
            ok &= TryRead(node, "training_per_week", "xp_reward_rules.training_per_week", out rules.training_per_week);
            ok &= TryRead(seasonRewards, "1st", "xp_reward_rules.season_rewards.1st", out rules.season_1st);
            ok &= TryRead(seasonRewards, "2nd", "xp_reward_rules.season_rewards.2nd", out rules.season_2nd);
            ok &= TryRead(seasonRewards, "3rd", "xp_reward_rules.season_rewards.3rd", out rules.season_3rd);

            return ok ? rules : null;
        }

        private static bool TryRead(JSONNode parent, string key, string path, out int value)
        {
            if (parent == null || !parent.HasKey(key) || !parent[key].IsNumber)
            {
                Debug.LogError($"[ProgressionService] progression.json is missing a numeric value for {path}.");
                value = 0;
                return false;
            }

            value = parent[key].AsInt;
            return true;
        }

        public string GetTierForXp(float totalXp)
        {
            // Highest tier whose minimum has been reached. The config's max_xp values
            // are whole numbers (49, 99, ...), so matching "min <= xp <= max" would
            // leave fractional XP such as 99.5 in a gap that no tier covers.
            string currentTier = "rookie";
            int bestMin = int.MinValue;
            foreach (var kvp in _tiers)
            {
                if (totalXp >= kvp.Value.min_xp && kvp.Value.min_xp > bestMin)
                {
                    bestMin = kvp.Value.min_xp;
                    currentTier = kvp.Key;
                }
            }
            return currentTier;
        }

        public TierData GetTierData(string tierName)
        {
            _tiers.TryGetValue(tierName, out var tierData);
            return tierData;
        }

        public Dictionary<string, TierData> GetAllTiers()
        {
            return _tiers;
        }
       
    }

    public Dictionary<string, PlayerProgressionState> GetAllStates()
    {
        // Return a shallow copy to avoid external modification
        return new Dictionary<string, PlayerProgressionState>(_progressionCache);
    }

    private static List<XpHistoryEntry> LoadXpHistoryForPlayer(string historyPath, string playerId)
    {
        var history = new List<XpHistoryEntry>();
        if (!File.Exists(historyPath)) return history;

        var lines = File.ReadAllLines(historyPath);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var json = JSONNode.Parse(line);
            var entryPlayerId = json["player_id"].Value;
            if (!string.IsNullOrEmpty(entryPlayerId) &&
                !string.Equals(entryPlayerId, playerId, StringComparison.Ordinal))
            {
                continue;
            }

            history.Add(new XpHistoryEntry(playerId, 0, "")
            {
                id = json["id"].Value,
                player_id = string.IsNullOrEmpty(entryPlayerId) ? playerId : entryPlayerId,
                timestamp = json["timestamp"].Value,
                xp_gained = json["xp_gained"].AsFloat,
                source = json["source"].Value
            });
        }

        return history;
    }
}
