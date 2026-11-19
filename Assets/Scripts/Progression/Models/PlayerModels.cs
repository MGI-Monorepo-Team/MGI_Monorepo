using System;
using System.Collections.Generic;
using UnityEngine;


[System.Serializable]
public class Player
{
    public string player_id;
    public string display_name;
    public string created_at;
    public Player(string id, string name)
    {
        player_id = id;
        display_name = name;
        created_at = DateTime.UtcNow.ToString("o");
    }
}

[System.Serializable]
public class XpHistoryEntry
{
    public string id;
    public string player_id;
    public string timestamp;
    public float xp_gained;
    public string source;

    public XpHistoryEntry() {}


    public XpHistoryEntry(string playerId, float xpGained, string source)
    {
        id = Guid.NewGuid().ToString();
        player_id = playerId;
        timestamp = DateTime.UtcNow.ToString("o");
        xp_gained = xpGained;
        this.source = source;
    }


}

/// <summary>
/// Base XP amounts, read from progression.json's xp_reward_rules (the single source
/// of truth - there are no XP constants in code).
/// </summary>
public class XpRewardRules
{
    public int match_played;
    public int win;
    public int loss;
    public int training_per_week;
    public int season_1st;
    public int season_2nd;
    public int season_3rd;

    public int MatchXp(bool won) => match_played + (won ? win : loss);

    /// <summary>Season-end XP for a final placement; 0 below 3rd.</summary>
    public int SeasonRewardForPlacement(int placement)
    {
        switch (placement)
        {
            case 1: return season_1st;
            case 2: return season_2nd;
            case 3: return season_3rd;
            default: return 0;
        }
    }
}

[System.Serializable]
public class PlayerProgressionState
{
    public string player_id;
    public float current_xp;
    public string current_tier;
    public List<XpHistoryEntry> xp_history;

    public PlayerProgressionState(string playerId)
    {
        player_id = playerId;
        current_xp = 0;
        current_tier = "rookie";
        xp_history = new List<XpHistoryEntry>();
    }
}
