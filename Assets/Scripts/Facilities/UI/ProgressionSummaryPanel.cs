using System.IO;
using UnityEngine;
using TMPro;
using Newtonsoft.Json.Linq;

public class ProgressionSummaryPanel : MonoBehaviour
{
    [Header("IDs (local mode)")]
    public string playerId = FacilitiesService.DefaultPlayerId;

    [Header("UI")]
    public TMP_Text outputText;

    void OnEnable()
    {
        Refresh();
    }

    public void Refresh()
    {
        if (!outputText) return;

        string id = PlayerIdProvider.Get();

        float currentXp = 0f;
        string currentTier = "rookie";

        var statePath = FilePathResolver.GetProgressionPath(id, "progression_state.json");
        if (File.Exists(statePath))
        {
            var state = JObject.Parse(File.ReadAllText(statePath));
            currentXp = state.Value<float?>("current_xp") ?? 0f;
            currentTier = state.Value<string>("current_tier") ?? "rookie";
        }

        string tierDisplayName = currentTier;
        int currentTierMinXp = 0;
        string nextTierDisplayName = null;
        int nextTierMinXp = 0;

        var configPath = Path.Combine(Application.streamingAssetsPath, "Progression", "progression.json");
        if (File.Exists(configPath))
        {
            var tiers = JObject.Parse(File.ReadAllText(configPath))["tier_progression"] as JObject;
            if (tiers != null && tiers.TryGetValue(currentTier, out var tierToken))
            {
                tierDisplayName = tierToken.Value<string>("display_name") ?? currentTier;
                currentTierMinXp = tierToken.Value<int?>("min_xp") ?? 0;
            }

            if (tiers != null)
            {
                // The next tier is the one with the smallest min_xp above the current tier's.
                // (Only min_xp is configured - there is no max_xp to match against.)
                foreach (var prop in tiers.Properties())
                {
                    int min = prop.Value.Value<int?>("min_xp") ?? 0;
                    if (min > currentTierMinXp && (nextTierDisplayName == null || min < nextTierMinXp))
                    {
                        nextTierDisplayName = prop.Value.Value<string>("display_name");
                        nextTierMinXp = min;
                    }
                }
            }
        }

        var facilities = new FacilitiesService();
        float trainingMultiplier = facilities.GetProgressionXpMultiplier(id, "training");
        float recoveryMultiplier = facilities.GetProgressionXpMultiplier(id, "recovery");
        float matchMultiplier = facilities.GetProgressionXpMultiplier(id, "match");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Current Tier: {tierDisplayName}");
        sb.AppendLine($"Total XP: {XpFormat.ToDisplay(currentXp)}");
        sb.AppendLine();
        sb.AppendLine(nextTierDisplayName != null
            ? $"Next Tier: {nextTierDisplayName} ({Mathf.Max(0, XpFormat.ToDisplay(nextTierMinXp - currentXp))} XP to go)"
            : "Highest tier reached.");
        sb.AppendLine();
        sb.AppendLine("Facility XP Bonuses:");
        sb.AppendLine($"- Training (Weight Room): +{FormatBonusPercent(trainingMultiplier)}%");
        sb.AppendLine($"- Recovery (Rehab Center): +{FormatBonusPercent(recoveryMultiplier)}%");
        sb.AppendLine($"- Match (Film Room): +{FormatBonusPercent(matchMultiplier)}%");

        outputText.text = sb.ToString().TrimEnd();
    }

    static string FormatBonusPercent(float multiplier)
    {
        return Mathf.Max(0f, (multiplier - 1f) * 100f).ToString("0.#");
    }
}
