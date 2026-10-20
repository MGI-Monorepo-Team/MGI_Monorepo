using System;

/// <summary>Applies the player-facing Economy consequence of one simulated game result.</summary>
public static class GameOutcomeRewardProcessor
{
    public const string RewardSource = "simulated_game_outcome";

    public static bool TryApplyCoinReward(EconomyService economy, GameOutcomeRecord outcome,
        string playerId, out bool applied)
    {
        applied = false;
        if (economy == null || outcome == null || string.IsNullOrWhiteSpace(playerId))
        {
            return false;
        }

        if (outcome.coin_reward < 0)
        {
            return false;
        }

        return economy.TryAddCurrencyOnce(playerId, outcome.coin_reward, 0, 0,
            RewardSource, outcome.event_id, out applied);
    }
}
