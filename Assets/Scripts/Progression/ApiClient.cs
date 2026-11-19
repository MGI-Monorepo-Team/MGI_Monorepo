using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System;
using System.Collections.Generic;
using SimpleJSON;

[Serializable]
public class PlayerProgressionSaveData
{
    public string player_id;
    public int current_xp;
    public string current_tier;
    public Dictionary<string, TierData> tier_progression;
    public List<XpHistoryEntry> xp_history;
}

[Serializable]
public class TierData
{
    public int min_xp;

    // Derived, not configured: ProgressionService sets it to the next tier's min_xp - 1 (so the
    // ranges are always contiguous) and to OpenEnded for the top tier. Only min_xp lives in
    // progression.json. These are methods, not properties, so serializers leave them out.
    public const int OpenEnded = -1;
    public int max_xp;

    public string display_name;
    public List<string> unlock_features;

    public bool IsOpenEnded() => max_xp < 0;

    /// <summary>"0 - 49" for a bounded tier, "150+" for the open-ended top tier.</summary>
    public string XpRangeLabel() => IsOpenEnded() ? $"{min_xp}+" : $"{min_xp} - {max_xp}";
}

[Serializable]
public class TeamProgressionSaveData
{
    public int total_xp;
    public int current_level;
    public string tier;
    public List<Dictionary<string, object>> xp_history;
}



public class TeamSaveData
{
    public string team_id;
    public string player_id;
    public string team_name;
    public int rating;
    public bool is_player_team;
    public int rank;
    public TeamStatsSaveData stats;
    public TeamProgressionSaveData progression;
}

[Serializable]
public class TeamStatsSaveData
{
    public int wins;
    public int losses;
    public int points;
    public int total_matches;
}

[Serializable]
public class SeasonSaveData
{
    public string season_id;
    public int current_week;
    public int total_weeks;
    public List<TeamSaveData> teams;
    public List<PlayerProgressionSaveData> player_progression;
}

public class ApiClient : MonoBehaviour
{
    public static ApiClient Instance { get; private set; }
    private readonly string baseUrl = "http://127.0.0.1:8000"; // Adjust if hosted elsewhere

    public PlayerProgressionSaveData PlayerProgressionSaveData { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // --- CREATE SEASON ---
    public IEnumerator PostCreateSeason(Action<SeasonSaveData> callback)
    {
        string url = $"{baseUrl}/seasons";

        var requestJson = new JSONObject();
        var teamsArray = new JSONArray();
        string[] teamNames = { "Jets", "Hawks", "Sharks", "Bears", "Lions", "Giants", "Eagles", "YOU" };

        foreach (string name in teamNames)
        {
            var teamJson = new JSONObject();
            teamJson["team_name"] = name;
            teamJson["rating"] = 1000;
            teamsArray.Add(teamJson);
        }

        requestJson["teams"] = teamsArray;
        requestJson["player_team_name"] = "YOU";

        string payload = requestJson.ToString();
        Debug.Log("Payload: " + payload);

        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(payload);
            www.uploadHandler = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"CreateSeason failed: {www.error}\n{www.downloadHandler.text}");
            }
            else
            {
                var json = JSONNode.Parse(www.downloadHandler.text);
                SeasonSaveData data = ParseSeasonSaveData(json);
                callback?.Invoke(data);
            }
        }
    }

    // --- SIMULATE WEEK ---
    public IEnumerator PostSimulateWeek(SeasonSaveData currentData, Action<SeasonSaveData> callback)
    {
        if (string.IsNullOrEmpty(currentData.season_id))
        {
            Debug.LogError("SimulateWeek failed: season_id is null or empty.");
            yield break;
        }

        string url = $"{baseUrl}/seasons/{currentData.season_id}/simulate_week";

        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(new byte[0]);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"SimulateWeek failed: {www.error}\n{www.downloadHandler.text}");
            }
            else
            {
                var json = JSONNode.Parse(www.downloadHandler.text);
                SeasonSaveData data = ParseSeasonSaveData(json);
                callback?.Invoke(data);
            }
        }
    }

    public IEnumerator GetPlayerProgression(string playerId, Action onComplete = null)
    {
        if (string.IsNullOrEmpty(playerId))
        {
            Debug.LogError("❌ GetPlayerProgression failed: player_id is null or empty.");
            yield break;
        }

        string url = $"{baseUrl}/progression/{playerId}";
        Debug.Log($"Fetching progression for player: {playerId}");

        using (UnityWebRequest www = UnityWebRequest.Get(url))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"❌ GetPlayerProgression failed: {www.error}\n{www.downloadHandler.text}");
            }
            else
            {
                var json = JSONNode.Parse(www.downloadHandler.text);
                PlayerProgressionSaveData = ParsePlayerProgression(json);
                Debug.Log($"✅ Player progression updated: {PlayerProgressionSaveData.current_xp} XP, Tier: {PlayerProgressionSaveData.current_tier}");
            }
        }

        onComplete?.Invoke();
    }

    // --- JSON PARSERS ---
    private SeasonSaveData ParseSeasonSaveData(JSONNode json)
    {
        SeasonSaveData data = new SeasonSaveData
        {
            season_id = json["season_id"],
            current_week = json["week"].AsInt,
            total_weeks = json["total_weeks"].AsInt,
            teams = new List<TeamSaveData>()
        };

        foreach (JSONNode t in json["teams"].AsArray)
        {
            TeamSaveData team = new TeamSaveData
            {
                team_id = t["team_id"],
                player_id = t["player_id"],
                team_name = t["team_name"],
                rating = t["rating"].AsInt,
                is_player_team = t["is_player_team"].AsBool,
                rank = 0,
                stats = new TeamStatsSaveData
                {
                    wins = t["wins"].AsInt,
                    losses = t["losses"].AsInt,
                    points = 0,
                    total_matches = t["wins"].AsInt + t["losses"].AsInt
                },
                progression = new TeamProgressionSaveData
                {
                    total_xp = 0,
                    current_level = 0,
                    tier = "",
                    xp_history = new List<Dictionary<string, object>>()
                }
            };

            data.teams.Add(team);
        }

        return data;
    }

    private PlayerProgressionSaveData ParsePlayerProgression(JSONNode p)
    {
        var prog = new PlayerProgressionSaveData
        {
            player_id = p["player_id"],
            current_xp = p["current_xp"].AsInt,
            current_tier = p["current_tier"],
            tier_progression = new Dictionary<string, TierData>(),
            xp_history = new List<XpHistoryEntry>()
        };

        foreach (KeyValuePair<string, JSONNode> kv in p["tier_progression"].AsObject)
        {
            var tierNode = kv.Value;
            prog.tier_progression[kv.Key] = new TierData
            {
                min_xp = tierNode["min_xp"].AsInt,
                max_xp = tierNode["max_xp"].AsInt,
                display_name = tierNode["display_name"],
                unlock_features = new List<string>()
            };

            foreach (JSONNode feat in tierNode["unlock_features"].AsArray)
                prog.tier_progression[kv.Key].unlock_features.Add(feat);
        }

        foreach (JSONNode entry in p["xp_history"].AsArray)
        {
            prog.xp_history.Add(new XpHistoryEntry
            {
                id = entry["id"],
                player_id = entry["player_id"],
                timestamp = entry["timestamp"],
                xp_gained = entry["xp_gained"].AsInt,
                source = entry["source"]
                
            });
        }

        return prog;
    }
}
