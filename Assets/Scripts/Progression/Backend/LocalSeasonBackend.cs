using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using SimpleJSON;

public class LocalSeasonBackend : MonoBehaviour, ISeasonBackend
{
    private static LocalSeasonBackend _instance;
    public static LocalSeasonBackend Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<LocalSeasonBackend>();
                if (_instance == null)
                {
                    var go = new GameObject("LocalSeasonBackend");
                    _instance = go.AddComponent<LocalSeasonBackend>();
                }
            }
            return _instance;
        }
    }

    private ProgressionService _progressionService;
    private string _currentSeasonPath;
    private string _currentPlayerId;

    // XP amounts (match, weekly training, season placement) are not constants here: they
    // come from progression.json via ProgressionService.GetXpRewardRules().

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        EnsureProgressionService();
    }

    private ProgressionService EnsureProgressionService()
    {
        if (_progressionService == null)
        {
            _progressionService = ProgressionService.Instance;
            if (_progressionService == null)
            {
                Debug.LogError("[LocalSeasonBackend] ProgressionService not found!");
            }
        }
        return _progressionService;
    }

    #region ISeasonBackend Implementations

    public void CreateSeason(List<string> teamNames, string playerTeamName, Action<SeasonSaveData> onSuccess, Action<string> onError = null)
    {
        try
        {
            var seasonData = new SeasonSaveData
            {
                season_id = Guid.NewGuid().ToString(),
                current_week = 0,
                total_weeks = 10,
                teams = new List<TeamSaveData>()
            };

            // Add AI teams
            foreach (var teamName in teamNames)
            {
                var team = new TeamSaveData
                {
                    team_id = Guid.NewGuid().ToString(),
                    player_id = Guid.NewGuid().ToString(),
                    team_name = teamName,
                    rating = 1000,
                    is_player_team = false,
                    rank = 0,
                    stats = new TeamStatsSaveData
                    {
                        wins = 0,
                        losses = 0,
                        points = 0,
                        total_matches = 0
                    },
                    progression = new TeamProgressionSaveData
                    {
                        total_xp = 0,
                        current_level = 0,
                        tier = "rookie",
                        xp_history = new List<Dictionary<string, object>>()
                    }
                };
                seasonData.teams.Add(team);
            }

            // Add player team
            var playerTeam = new TeamSaveData
            {
                team_id = Guid.NewGuid().ToString(),
                player_id = "local_player",
                team_name = playerTeamName,
                rating = 1000,
                is_player_team = true,
                rank = 0,
                stats = new TeamStatsSaveData
                {
                    wins = 0,
                    losses = 0,
                    points = 0,
                    total_matches = 0
                },
                progression = new TeamProgressionSaveData
                {
                    total_xp = 0,
                    current_level = 0,
                    tier = "rookie",
                    xp_history = new List<Dictionary<string, object>>()
                }
            };
            seasonData.teams.Add(playerTeam);

            // Save season data
            SaveSeasonData(seasonData);

            // Fresh progression for this season's player (history scoped to player_id)
            _currentPlayerId = playerTeam.player_id;
            EnsureProgressionService()?.ClearPlayerProgression(_currentPlayerId);
            EnsureProgressionService()?.GetState(_currentPlayerId, createIfMissing: true);
            Debug.Log($"[LocalSeasonBackend] Season created: {seasonData.season_id} with {seasonData.teams.Count} teams (player: {playerTeam.player_id})");
            onSuccess?.Invoke(seasonData);
        }
        catch (Exception ex)
        {
            var error = $"[LocalSeasonBackend] CreateSeason failed: {ex.Message}";
            Debug.LogError(error);
            onError?.Invoke(error);
        }
    }


    public void SimulateWeek(SeasonSaveData currentSeason, Action<SeasonSaveData> onSuccess, Action<string> onError = null)
    {
        try
        {
            if (currentSeason == null || currentSeason.teams == null)
                throw new ArgumentNullException("currentSeason or currentSeason.teams is null");

            if (currentSeason.current_week >= currentSeason.total_weeks)
            {
                onError?.Invoke($"Season complete: all {currentSeason.total_weeks} weeks have been simulated.");
                return;
            }

            currentSeason.current_week++;

            var random = new System.Random();
            var playerTeam = currentSeason.teams.FirstOrDefault(t => t.is_player_team);
            bool playerWonThisWeek = false;

            // Simulate matches for all teams
            foreach (var team in currentSeason.teams)
            {
                if (team == null || team.stats == null) continue;

                if (!team.is_player_team)
                {
                    if (random.NextDouble() > 0.5) team.stats.wins++;
                    else team.stats.losses++;
                    team.stats.total_matches++;
                    team.stats.points = team.stats.wins * 2;
                }
                else
                {
                    // Player team match
                    bool playerWins = random.NextDouble() > 0.5;
                    if (playerWins)
                    {
                        team.stats.wins++;
                        playerWonThisWeek = true;
                    }
                    else
                    {
                        team.stats.losses++;
                    }
                    team.stats.total_matches++;
                    team.stats.points = team.stats.wins * 2; // Simple points calculation
                }
            }

            // Award this week's XP: the match, then the weekly training beat (same tick).
            if (playerTeam != null)
            {
                if (string.IsNullOrEmpty(playerTeam.player_id))
                {
                    Debug.LogWarning("[LocalSeasonBackend] Player team has null/empty player_id");
                }
                else
                {
                    AwardMatchXp(playerTeam.player_id, currentSeason.season_id, currentSeason.current_week, playerWonThisWeek);
                    AwardTrainingXp(playerTeam.player_id, currentSeason.season_id, currentSeason.current_week);
                }
            }
            else
            {
                Debug.LogWarning("[LocalSeasonBackend] No player team found in season");
            }

            // The final week closes the season: pay out the placement reward. The guard at the
            // top of this method means the final week is only ever simulated once per season.
            if (currentSeason.current_week >= currentSeason.total_weeks)
            {
                AwardSeasonRewards(currentSeason,
                    message => Debug.Log($"[LocalSeasonBackend] Season complete: {message}"),
                    error => Debug.LogError($"[LocalSeasonBackend] Season reward failed: {error}"));
            }

            // Save updated season
            SaveSeasonData(currentSeason);

            Debug.Log($"[LocalSeasonBackend] Week {currentSeason.current_week} simulated");
            onSuccess?.Invoke(currentSeason);
        }
        catch (Exception ex)
        {
            var error = $"[LocalSeasonBackend] SimulateWeek failed: {ex.Message}";
            Debug.LogError(ex);
            onError?.Invoke(ex.Message);
        }
    }

  
    public void GetPlayerProgression(string playerId, Action<PlayerProgressionSaveData> onSuccess, Action<string> onError = null)
    {
        try
        {
            if (string.IsNullOrEmpty(playerId))
            {
                throw new ArgumentException("playerId is null or empty");
            }

            var state = _progressionService?.GetState(playerId, createIfMissing: false);
            if (state == null)
            {
                throw new Exception($"Progression state not found for player: {playerId}");
            }

            // Convert to legacy format
            var legacyData = ConvertToLegacyFormat(state);
            onSuccess?.Invoke(legacyData);
        }
        catch (Exception ex)
        {
            var error = $"[LocalSeasonBackend] GetPlayerProgression failed: {ex.Message}";
            Debug.LogError(error);
            onError?.Invoke(error);
        }
    }


    public void GetLocalProgression(string playerId, Action<PlayerProgressionState> onSuccess, Action<string> onError = null)
    {
        try
        {
            if (string.IsNullOrEmpty(playerId))
            {
                throw new ArgumentException("playerId is null or empty");
            }

            var state = _progressionService?.GetState(playerId, createIfMissing: false);
            if (state == null)
            {
                throw new Exception($"Progression state not found for player: {playerId}");
            }

            onSuccess?.Invoke(state);
        }
        catch (Exception ex)
        {
            var error = $"[LocalSeasonBackend] GetLocalProgression failed: {ex.Message}";
            Debug.LogError(error);
            onError?.Invoke(error);
        }
    }

    public void AddPlayerXp(string playerId, int xpAmount, string source, Action<PlayerProgressionState> onSuccess, Action<string> onError = null)
    {
        try
        {
            _progressionService?.AddXp(
                playerId,
                xpAmount,
                source,
                Guid.NewGuid().ToString());

            SaveProgressionDataToJson(playerId);

            var state = _progressionService?.GetState(playerId, createIfMissing: false);
            onSuccess?.Invoke(state);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LocalSeasonBackend] AddPlayerXp failed: {ex}");
            onError?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Awards season-end XP based on final standings: 1st, 2nd and 3rd place are rewarded
    /// (amounts from progression.json), nothing below 3rd. Called when the final week is
    /// simulated. The grant id is derived from the season, so calling this twice for the same
    /// season cannot pay out twice.
    /// </summary>
    public void AwardSeasonRewards(SeasonSaveData seasonData, Action<string> onSuccess, Action<string> onError = null)
    {
        try
        {
            if (seasonData == null || seasonData.teams == null || seasonData.teams.Count == 0)
            {
                throw new ArgumentException("Invalid season data");
            }

            var progression = EnsureProgressionService();
            var rules = progression?.GetXpRewardRules();
            if (rules == null)
            {
                throw new InvalidOperationException("XP reward rules are unavailable (progression.json), so no season reward can be awarded");
            }

            // Sort teams by points (ranking)
            var rankedTeams = seasonData.teams
                .Where(t => t != null && t.is_player_team)
                .OrderByDescending(t => t.stats.points)
                .ToList();

            if (rankedTeams.Count == 0)
            {
                throw new Exception("No player team found in season data");
            }

            var playerTeam = rankedTeams.First();
            int placement = 1;
            int xpReward = 0;

            // Determine placement and reward
            // This is simplified - in a real scenario, you'd rank all teams
            var allTeamsByPoints = seasonData.teams
                .Where(t => t != null && t.stats != null)
                .OrderByDescending(t => t.stats.points)
                .ToList();

            placement = allTeamsByPoints.FindIndex(t => t.is_player_team) + 1;

            xpReward = rules.SeasonRewardForPlacement(placement);
            if (xpReward <= 0)
            {
                // No reward for 4th place or lower
                Debug.Log($"[LocalSeasonBackend] Player team finished {placement}th - no season reward");
                onSuccess?.Invoke("No reward for this placement");
                return;
            }

            // Award the XP (one grant per season: the id repeats if this is called again)
            progression.AddXp(
                playerTeam.player_id,
                xpReward,
                "season_reward",
                $"{seasonData.season_id}:season_reward");
            SaveProgressionDataToJson(playerTeam.player_id);
            Debug.Log($"[LocalSeasonBackend] Season reward awarded: {xpReward} XP for {placement} place finish");
            onSuccess?.Invoke($"Awarded {xpReward} XP for {placement} place finish");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LocalSeasonBackend] AwardSeasonRewards failed: {ex}");
            onError?.Invoke(ex.Message);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Single grant per match: the played amount plus the win or loss bonus, all from
    /// progression.json (xp_reward_rules).
    /// </summary>
    private void AwardMatchXp(string playerId, string seasonId, int week, bool playerWon)
    {
        var progression = EnsureProgressionService();
        var rules = progression?.GetXpRewardRules();
        if (rules == null) return;

        int totalXp = rules.MatchXp(playerWon);
        string source = playerWon ? "match_win" : "match_loss";
        string eventId = $"{seasonId}:week:{week}:match";

        progression.AddXp(playerId, totalXp, source, eventId);
    }

    /// <summary>
    /// One small training grant per simulated week, alongside the match. The "training"
    /// source is what makes Facilities apply the Weight Room multiplier. Recovery is
    /// deliberately not an XP source.
    /// </summary>
    private void AwardTrainingXp(string playerId, string seasonId, int week)
    {
        var progression = EnsureProgressionService();
        var rules = progression?.GetXpRewardRules();
        if (rules == null || rules.training_per_week <= 0) return;

        progression.AddXp(playerId, rules.training_per_week, "training", $"{seasonId}:week:{week}:training");
    }

    private PlayerProgressionSaveData ConvertToLegacyFormat(PlayerProgressionState state)
    {
        var legacy = new PlayerProgressionSaveData
        {
            player_id = state.player_id,
            // The legacy DTO is what UI screens read, so it carries the display integer.
            current_xp = XpFormat.ToDisplay(state.current_xp),
            current_tier = state.current_tier,
            tier_progression = _progressionService?.GetAllTiers() ?? new Dictionary<string, TierData>(),
            xp_history = new List<XpHistoryEntry>()
        };

        foreach (var entry in state.xp_history)
        {
            if (!string.IsNullOrEmpty(entry.player_id) &&
                !string.Equals(entry.player_id, state.player_id, StringComparison.Ordinal))
            {
                continue;
            }

            legacy.xp_history.Add(new XpHistoryEntry
            {
                player_id = state.player_id,
                timestamp = entry.timestamp,
                xp_gained = entry.xp_gained,
                source = entry.source
            });
        }

        return legacy;
    }

    private void SaveSeasonData(SeasonSaveData seasonData)
    {
        try
        {
            var seasonDir = Path.Combine(FilePathResolver.GetRoot(), "seasons");
            Directory.CreateDirectory(seasonDir);

            var seasonPath = Path.Combine(seasonDir, $"{seasonData.season_id}.json");
            var json = new JSONObject();

            json["season_id"] = seasonData.season_id;
            json["current_week"] = seasonData.current_week;
            json["total_weeks"] = seasonData.total_weeks;

            var teamsArray = new JSONArray();
            foreach (var team in seasonData.teams)
            {
                var teamJson = new JSONObject
                {
                    ["team_id"] = team.team_id,
                    ["player_id"] = team.player_id,
                    ["team_name"] = team.team_name,
                    ["rating"] = team.rating,
                    ["is_player_team"] = team.is_player_team,
                    ["wins"] = team.stats.wins,
                    ["losses"] = team.stats.losses,
                    ["points"] = team.stats.points
                };
                teamsArray.Add(teamJson);
            }
            json["teams"] = teamsArray;

            File.WriteAllText(seasonPath, json.ToString(2));
            _currentSeasonPath = seasonPath;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LocalSeasonBackend] Failed to save season: {ex}");
        }
    }
    private SeasonSaveData LoadSeasonData(string seasonId)
    {
        try
        {
            var seasonDir = Path.Combine(FilePathResolver.GetRoot(), "seasons");
            var seasonPath = Path.Combine(seasonDir, $"{seasonId}.json");

            if (!File.Exists(seasonPath))
            {
                return null;
            }

            var json = JSONNode.Parse(File.ReadAllText(seasonPath));
            var seasonData = new SeasonSaveData
            {
                season_id = json["season_id"].Value,
                current_week = json["current_week"].AsInt,
                total_weeks = json["total_weeks"].AsInt,
                teams = new List<TeamSaveData>()
            };

            foreach (KeyValuePair<string, JSONNode> kvp in json["teams"])
            {
                var teamNode = kvp.Value;
                var team = new TeamSaveData
                {
                    team_id = teamNode["team_id"].Value,
                    player_id = teamNode["player_id"].Value,
                    team_name = teamNode["team_name"].Value,
                    rating = teamNode["rating"].AsInt,
                    is_player_team = teamNode["is_player_team"].AsBool,
                    stats = new TeamStatsSaveData
                    {
                        wins = teamNode["wins"].AsInt,
                        losses = teamNode["losses"].AsInt,
                        points = teamNode["points"].AsInt,
                        total_matches = teamNode["wins"].AsInt + teamNode["losses"].AsInt
                    },
                    progression = new TeamProgressionSaveData()
                };
                seasonData.teams.Add(team);
            }

            return seasonData;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LocalSeasonBackend] Failed to load season data: {ex}");
            return null;
        }
    }
    private void SaveProgressionDataToJson(string playerId = null)
    {
        try
        {
            var dir = Path.Combine(Application.streamingAssetsPath, "Progression");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var allStates = _progressionService?.GetAllStates()
                            ?? new Dictionary<string, PlayerProgressionState>();

            // Export only the current human player when id is known
            IEnumerable<PlayerProgressionState> statesToExport = allStates.Values;
            if (!string.IsNullOrEmpty(playerId) && allStates.TryGetValue(playerId, out var playerState))
            {
                statesToExport = new[] { playerState };
            }

            var statesList = statesToExport.Where(s => s != null).ToList();

            // xp_history.jsonl — current session snapshot for the player(s) being exported
            var historyPath = Path.Combine(dir, "xp_history.jsonl");
            using (var writer = new StreamWriter(historyPath, append: false))
            {
                foreach (var state in statesList)
                {
                    if (state?.xp_history == null) continue;

                    foreach (var entry in state.xp_history)
                    {
                        if (!string.IsNullOrEmpty(entry.player_id) &&
                            !string.Equals(entry.player_id, state.player_id, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var e = new JSONObject
                        {
                            ["id"] = entry.id,
                            ["player_id"] = state.player_id,
                            ["timestamp"] = entry.timestamp,
                            ["xp_gained"] = XpFormat.ToStorage(entry.xp_gained),
                            ["source"] = entry.source
                        };

                        writer.WriteLine(e.ToString());
                    }
                }
            }

            // progression_state_schema.json — snapshot for exported player(s)
            var progArray = new JSONArray();
            foreach (var state in statesList)
            {
                var s = new JSONObject
                {
                    ["player_id"] = state.player_id,
                    ["current_xp"] = XpFormat.ToStorage(state.current_xp),
                    ["current_tier"] = state.current_tier
                };
                progArray.Add(s);
            }

            File.WriteAllText(
                Path.Combine(dir, "progression_state_schema.json"),
                progArray.ToString(2)
            );

            Debug.Log("[LocalSeasonBackend] Progression inspection data exported to StreamingAssets for current player.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LocalSeasonBackend] Failed to save progression data: {ex}");
        }
    }
    #endregion
}
