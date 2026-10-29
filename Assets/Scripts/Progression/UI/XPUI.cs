using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;


    public class XPUI : MonoBehaviour
    {
        public TextMeshProUGUI currentXPText;
        public Transform xpListParent;
        public GameObject xpEntryPrefab;

        [Header("XP Chart (VIEW XP CHART button)")]
        public TextMeshProUGUI chartToggleButtonLabel;
        [SerializeField] private float chartRowHeight = 160f;
        [SerializeField] private float chartBarWidth = 28f;
        [SerializeField] private Color chartBarGainColor = new Color(0.4549f, 0.7529f, 0.9882f); // #74C0FC, matches the list view's XP-gain color
        [SerializeField] private Color chartBarLossColor = new Color(1f, 0.4196f, 0.4196f); // #FF6B6B, matches the list view's loss color

        // 36pt (Test_Row.prefab's list-view size) turned out too wide for a ~44px
        // bar column - with several bars in a row, neighboring value labels
        // overlapped into an unreadable jumble. 20pt is the compromise: still well
        // clear of the earlier blur (which came from forced Bold faking a weight
        // this pixel font doesn't have, not from being small - see NoWrap fix
        // below), but narrow enough to fit "+27" within one column without
        // colliding with its neighbors.
        [SerializeField] private float chartValueFontSize = 20f;
        private const float ChartValueLabelHeight = 26f;
        private const float ChartIndexLabelHeight = 18f;

        private bool _showingChart;

        void OnEnable()
        {
            if (SeasonManager.Instance != null)
                SeasonManager.Instance.OnSeasonDataUpdated += Refresh;
            else
                StartCoroutine(WaitAndSubscribe());
        }

        void OnDisable()
        {
            if (SeasonManager.Instance != null)
                SeasonManager.Instance.OnSeasonDataUpdated -= Refresh;
        }

        private System.Collections.IEnumerator WaitAndSubscribe()
        {
            while (SeasonManager.Instance == null)
                yield return null;
            SeasonManager.Instance.OnSeasonDataUpdated += Refresh;
            Refresh();
        }

        /// <summary>
        /// Flips between the XP history list and the per-week XP bar chart, both
        /// rendered into the same scroll view content. Wired to the XP screen's
        /// "VIEW XP CHART" button OnClick.
        /// </summary>
        public void ToggleChartView()
        {
            _showingChart = !_showingChart;
            if (chartToggleButtonLabel != null)
                chartToggleButtonLabel.text = _showingChart ? "VIEW XP LIST" : "VIEW XP CHART";
            Refresh();
        }

        public void Refresh()
        {
            if (_showingChart) RefreshXpChart();
            else RefreshXPHistory();
        }

        /// <summary>
        /// Resets to the list view and refreshes it. Called on screen entry
        /// (ScreenManager.ShowXP) so every visit to the XP screen starts from a
        /// predictable default, regardless of which view was showing last time.
        /// </summary>
        public void ResetToListView()
        {
            _showingChart = false;
            if (chartToggleButtonLabel != null)
                chartToggleButtonLabel.text = "VIEW XP CHART";
            RefreshXPHistory();
        }

        public void RefreshXPHistory()
        {
            if (xpListParent == null || xpEntryPrefab == null) return;

            var state = GetProgressionState();
            UpdateHeader(state);
            ClearListParent();

            var prog = state?.xp_history;
            if (prog == null || prog.Count == 0)
            {
                ShowEmptyState();
                return;
            }

            // A week can hold several entries (its match, its training grant, any CCAS
            // duplicates), so the label comes from the week number, not the row position.
            var weeks = WeekNumbers(prog);

            // Create new entries (Iterate backwards to show NEWEST first)
            for (int i = prog.Count - 1; i >= 0; i--)
            {
                var entry = prog[i];

                var go = Instantiate(xpEntryPrefab, xpListParent);
                var texts = go.GetComponentsInChildren<TextMeshProUGUI>();

                if (texts.Length >= 3)
                {
                    texts[0].text = $"Week {weeks[i]}";

                    texts[1].text = FormatResult(entry.source);

                    string color = entry.xp_gained > 0 ? "#74C0FC" : "#FF6B6B";
                    texts[2].text = $"<color={color}>+{XpFormat.ToDisplay(entry.xp_gained)} XP</color>";
                    Debug.Log("XPUI three text fields found");
                }
                else if (texts.Length == 1)
                {
                    texts[0].text = $"{FormatResult(entry.source)}: +{XpFormat.ToDisplay(entry.xp_gained)}";
                    Debug.Log("XPUI single text field found");
                }
            }
        }

        /// <summary>
        /// Week number for each history entry. A new week starts at every match entry, and the
        /// week's other entries (its weekly training grant, any CCAS duplicates opened before the
        /// next match) belong to it. Entries before the first match count as week 1.
        /// </summary>
        public static List<int> WeekNumbers(IReadOnlyList<XpHistoryEntry> entries)
        {
            var weeks = new List<int>(entries.Count);
            int week = 0;
            foreach (var entry in entries)
            {
                if (SeasonManager.IsMatchSource(entry.source)) week++;
                weeks.Add(Mathf.Max(1, week));
            }
            return weeks;
        }

        /// <summary>
        /// XP summed per week (full precision), in week order - one bar per week for the chart.
        /// </summary>
        public static List<KeyValuePair<int, float>> TotalsByWeek(IReadOnlyList<XpHistoryEntry> entries)
        {
            var weeks = WeekNumbers(entries);
            var totals = new List<KeyValuePair<int, float>>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (totals.Count > 0 && totals[totals.Count - 1].Key == weeks[i])
                    totals[totals.Count - 1] = new KeyValuePair<int, float>(weeks[i], totals[totals.Count - 1].Value + entries[i].xp_gained);
                else
                    totals.Add(new KeyValuePair<int, float>(weeks[i], entries[i].xp_gained));
            }
            return totals;
        }

        /// <summary>
        /// Builds a per-week XP bar chart entirely at runtime (mirroring how
        /// RefreshXPHistory() instantiates list rows) into the same scroll view
        /// content used by the list, so it needs no dedicated scene UI of its own.
        /// One bar per week: its height is that week's XP (match + training), not a running
        /// total. The list view shows the per-source breakdown.
        /// </summary>
        private void RefreshXpChart()
        {
            if (xpListParent == null) return;

            var state = GetProgressionState();
            UpdateHeader(state);
            ClearListParent();

            var entries = state?.xp_history;
            if (entries == null || entries.Count == 0)
            {
                ShowEmptyState();
                return;
            }

            // Bar heights use the full-precision weekly total so proportions stay accurate;
            // only the printed labels are rounded to whole numbers.
            var weekTotals = TotalsByWeek(entries);
            float maxGain = Mathf.Max(1f, weekTotals.Max(t => Mathf.Abs(t.Value)));
            float barAreaHeight = Mathf.Max(10f, chartRowHeight - ChartValueLabelHeight - ChartIndexLabelHeight);

            var row = new GameObject("ChartRow", typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(xpListParent, false);

            var rowLayout = row.GetComponent<LayoutElement>();
            rowLayout.preferredHeight = chartRowHeight;
            rowLayout.minHeight = chartRowHeight;

            var hlg = row.GetComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.LowerCenter;
            hlg.spacing = 8;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;

            for (int i = 0; i < weekTotals.Count; i++)
            {
                int week = weekTotals[i].Key;
                float weekXp = weekTotals[i].Value;

                // Each bar gets its own fixed-size column so a value label can sit
                // above it and a week label below it, without fighting the row's
                // HorizontalLayoutGroup (which only controls x-position, not sizing -
                // see the sizeDelta comment below).
                var column = new GameObject($"BarColumn_{i}", typeof(RectTransform));
                var columnRt = column.GetComponent<RectTransform>();
                columnRt.SetParent(row.transform, false);
                columnRt.sizeDelta = new Vector2(chartBarWidth + 16f, chartRowHeight);

                float heightRatio = Mathf.Abs(weekXp) / maxGain;
                float barPixelHeight = Mathf.Max(2f, heightRatio * barAreaHeight);

                var barGO = new GameObject("Bar", typeof(RectTransform), typeof(Image));
                var barRt = barGO.GetComponent<RectTransform>();
                barRt.SetParent(columnRt, false);
                barRt.anchorMin = barRt.anchorMax = new Vector2(0.5f, 0f);
                barRt.pivot = new Vector2(0.5f, 0f);
                barRt.anchoredPosition = new Vector2(0, ChartIndexLabelHeight);
                // childControlWidth/childControlHeight are off on the row's
                // HorizontalLayoutGroup, so it positions each column using its actual
                // RectTransform size but never applies LayoutElement.preferred* to
                // it - size has to be set directly here, or every bar renders at the
                // same default size regardless of this loop's math.
                barRt.sizeDelta = new Vector2(chartBarWidth, barPixelHeight);
                barGO.GetComponent<Image>().color = weekXp >= 0 ? chartBarGainColor : chartBarLossColor;

                CreateChartLabel(columnRt, "ValueLabel", $"+{XpFormat.ToDisplay(weekXp)}",
                    new Vector2(0, ChartIndexLabelHeight + barPixelHeight + 2f), ChartValueLabelHeight, chartValueFontSize, Color.white);

                // The week number matches the list view's "Week N" labels (both come from
                // WeekNumbers) - per the reviewed spec, the chart's bars are grouped by week.
                CreateChartLabel(columnRt, "WeekLabel", $"W{week}",
                    Vector2.zero, ChartIndexLabelHeight, 10, new Color(1f, 1f, 1f, 0.7f));
            }
        }

        /// <summary>
        /// Creates a small bottom-anchored TMP label (used for the XP-gained value
        /// above a bar, the source label below it, and the empty-state message),
        /// matching the screen's existing font so it doesn't look like a foreign element.
        /// </summary>
        private TextMeshProUGUI CreateChartLabel(RectTransform parent, string name, string text, Vector2 anchoredPosition, float height, float fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = anchoredPosition;
            rt.sizeDelta = new Vector2(0f, height);

            var label = go.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = color;
            // These are single-line captions in narrow bar columns - a fresh TMP
            // component defaults to word-wrap enabled, which at larger font sizes
            // breaks "+27" etc. onto a second line that spills down into the bar
            // below it instead of just overflowing sideways (harmless, since
            // nothing here is clipped by a mask).
            label.textWrappingMode = TextWrappingModes.NoWrap;
            if (currentXPText != null) label.font = currentXPText.font;
            return label;
        }

        private void ClearListParent()
        {
            for (int i = xpListParent.childCount - 1; i >= 0; i--)
            {
                Transform child = xpListParent.GetChild(i);
                child.SetParent(null); // Important: Detach from layout first
                Destroy(child.gameObject);
            }
        }

        private void UpdateHeader(PlayerProgressionState state)
        {
            if (currentXPText == null) return;
            int xp = XpFormat.ToDisplay(state?.current_xp ?? 0f);
            string tier = state?.current_tier ?? "rookie";
            currentXPText.text = $"Total XP: {xp}, Tier: {FormatTierLabel(tier)}";
        }

        /// <summary>
        /// Reads the current player's progression state straight from
        /// ProgressionService - the same source the integration tests assert
        /// against - rather than through SeasonManager's XpHistoryEntries/PlayerXP/
        /// PlayerTier wrapper properties. SeasonManager is still consulted for the
        /// player_id itself (that's genuinely where "who is the current player"
        /// lives, via the active season's player team), just not for the XP data.
        /// </summary>
        private PlayerProgressionState GetProgressionState()
        {
            var playerId = SeasonManager.Instance?.PlayerTeam?.player_id;
            if (string.IsNullOrEmpty(playerId)) return null;
            return ProgressionService.Instance?.GetState(playerId, createIfMissing: false);
        }

        private void ShowEmptyState()
        {
            if (xpListParent == null) return;
            CreateChartLabel((RectTransform)xpListParent, "EmptyState", "No XP earned yet",
                Vector2.zero, 40f, 16, new Color(1f, 1f, 1f, 0.6f));
        }

        private static string FormatTierLabel(string tier)
        {
            if (string.IsNullOrEmpty(tier)) return "Unknown";
            var spaced = tier.Replace('_', ' ');
            return char.ToUpper(spaced[0]) + spaced.Substring(1);
        }

        // Helper function to clean up the source string
        private string FormatResult(string sourceRaw)
        {
            if (string.IsNullOrEmpty(sourceRaw)) return "Unknown";

            string lower = sourceRaw.ToLower();

            if (lower.Contains("loss")) return "Match Loss";
            if (lower.Contains("win")) return "Match Win";

            // Fallback: just Capitalize the raw string if it's something else
            return char.ToUpper(sourceRaw[0]) + sourceRaw.Substring(1);
        }
    }
