using UnityEngine;
using TMPro;
using System.Linq;
using System.Collections;

public class RewardsUI : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI titleText;
    public Transform tableParent;
    public GameObject rowPrefab;

    private SeasonManager subscribedManager;

    void OnEnable()
    {
        RefreshRewards();
        StartCoroutine(SubscribeToSeasonManager());
    }

    void OnDisable()
    {
        StopAllCoroutines();
        if (subscribedManager != null)
        {
            subscribedManager.OnSeasonDataUpdated -= RefreshRewards;
            subscribedManager = null;
        }
    }

    private IEnumerator SubscribeToSeasonManager()
    {
        while (SeasonManager.Instance == null)
            yield return null;

        subscribedManager = SeasonManager.Instance;
        subscribedManager.OnSeasonDataUpdated += RefreshRewards;

        RefreshRewards();
    }

    public void RefreshRewards()
    {
        var sm = SeasonManager.Instance;

        if (sm == null || sm.AllTiers == null || sm.AllTiers.Count == 0)
        {
            Debug.LogWarning("RewardsUI: No tier data available yet.");
            return;
        }

        if (titleText != null)
            titleText.text = $"REWARDS — {sm.PlayerXP} XP";

        for (int i = tableParent.childCount - 1; i >= 0; i--)
        {
            Transform child = tableParent.GetChild(i);
            child.SetParent(null);
            Destroy(child.gameObject);
        }

        var tiers = sm.AllTiers.Values.OrderBy(t => t.min_xp).ToList();

        foreach (var tier in tiers)
        {
            var row = Instantiate(rowPrefab, tableParent);

            var fields = row.GetComponentsInChildren<TextMeshProUGUI>();

            if (fields.Length >= 3)
            {
                fields[0].text = tier.display_name;
                fields[1].text = $"{tier.XpRangeLabel()} XP";
                fields[2].text = tier.unlock_features != null && tier.unlock_features.Count > 0
                    ? string.Join(", ", tier.unlock_features)
                    : "-";

                bool isCurrentTier = string.Equals(tier.display_name, sm.CurrentTierData?.display_name, System.StringComparison.OrdinalIgnoreCase);
                bool isUnlocked = sm.PlayerXPExact >= tier.min_xp;

                Color rowColor = isCurrentTier ? new Color(1f, 0.8f, 0f) // Gold — current tier
                    : isUnlocked ? Color.white
                    : new Color(0.5f, 0.5f, 0.5f); // Grey — locked

                foreach (var txt in fields) txt.color = rowColor;
            }
            else
            {
                Debug.LogError($"Rewards Row Prefab only has {fields.Length} text fields. Needed 3.");
            }
        }
    }
}
