using System.Collections.Generic;

/// <summary>
/// Season placement, in one place so the hub's displayed rank and the season-end XP reward can
/// never disagree about where a team finished.
/// </summary>
public static class SeasonStandings
{
    /// <summary>
    /// 1-based placement of <paramref name="team"/>: one more than the number of teams that
    /// finished strictly ahead (more points, then more wins). Teams level on both share the
    /// better placement ("1, 2, 2, 4"), so a tie never pushes anyone down and the outcome does
    /// not depend on the order teams happen to be stored in (the player's team is stored last,
    /// which used to make it lose every tie). Returns 0 if the team is not in the list.
    /// </summary>
    public static int PlacementOf(IReadOnlyList<TeamSaveData> teams, TeamSaveData team)
    {
        if (teams == null || team == null) return 0;

        bool found = false;
        int teamsAhead = 0;
        foreach (var other in teams)
        {
            if (other == null) continue;
            if (ReferenceEquals(other, team)) { found = true; continue; }
            if (IsAhead(other, team)) teamsAhead++;
        }

        return found ? teamsAhead + 1 : 0;
    }

    private static bool IsAhead(TeamSaveData a, TeamSaveData b)
    {
        int aPoints = a.stats?.points ?? 0;
        int bPoints = b.stats?.points ?? 0;
        if (aPoints != bPoints) return aPoints > bPoints;

        return (a.stats?.wins ?? 0) > (b.stats?.wins ?? 0);
    }
}
