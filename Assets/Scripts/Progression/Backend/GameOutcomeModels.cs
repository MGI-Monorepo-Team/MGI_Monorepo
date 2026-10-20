using System;

/// <summary>
/// File format produced by the standalone game-outcome simulator.
/// An empty or missing file means no external results are available yet.
/// </summary>
[Serializable]
public class GameOutcomeFile
{
    public string schema_version = "1.0";
    public GameOutcomeRecord[] outcomes = Array.Empty<GameOutcomeRecord>();
}

/// <summary>
/// One completed game. Team IDs must match the season's TeamSaveData.team_id values.
/// event_id is carried forward to Economy so replaying the same output cannot pay twice.
/// </summary>
[Serializable]
public class GameOutcomeRecord
{
    public string event_id;
    public string season_id;
    public int week;
    public string home_team_id;
    public string away_team_id;
    public string winner_team_id;
    public int home_score;
    public int away_score;
    public int coin_reward;
}
