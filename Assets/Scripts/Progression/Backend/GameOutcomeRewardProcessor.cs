using System;

/// <summary>Applies the player-facing Economy consequence of one simulated game result.</summary>
public static class GameOutcomeRewardProcessor
{
    public const string RewardSource = "simulated_game_outcome";

    public static bool TryApplyCoinReward(EconomyService economy, string eventId,
        string playerId, int coins, out bool applied)
    {
        applied = false;
        if (economy == null || string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(playerId))
        {
            return false;
        }

        if (coins < 0)
        {
            return false;
        }

        return economy.TryAddCurrencyOnce(playerId, coins, 0, 0,
            RewardSource, eventId, out applied);
    }
}
