using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System;

public class AppManager : MonoBehaviour
{
    [Header("Main Screen Components")]

    // Game objects for the screens
    public GameObject mainMenu;
    public GameObject coachHiringScreen;
    public GameObject coachDetailsScreen;
    public GameObject performanceScreen;
    public GameObject historyScreen;
    public GameObject compareScreen;
    public static bool OpenPerformanceOnLoad = false;

    [Header("Navigation Buttons for Screen 1")]

    // Buttons used in Screen 1: FMG Coach Main Menu
    public Button viewOffenseCoachButton;
    public Button fireOffenseCoachButton;
    public Button viewDefenseCoachButton;
    public Button fireDefenseCoachButton;
    public Button viewSpecialCoachButton;
    public Button hiringCoachMarketButton;
    public Button performanceButton;
    public Button historyButton;
    
    [SerializeField] private TMPro.TextMeshProUGUI teamRatingText;
    private IDisposable hireSubscription;
    private IDisposable fireSubscription;

    [Header("Navigation Buttons for Screen 2")]
    public Button backToMainMenuButton;
    public Button refreshButton;

    public Button hireCoach1Button;
    public Button compareCoach1Button;
    public Button viewCoach1Button;
    public Button hireCoach2Button;
    public Button compareCoach2Button;
    public Button viewCoach2Button;

    [Header("Navigation Buttons for Screen 3")]
    // Buttons used in Screen 3: View Coach Details Page
    public Button compareButton;
    public Button backToMarketButton;
    public Button hireButton;

    [Header("Navigation Buttons for Screen 4")]
    public Button backButton;
    public Button detailedStatsButton;
    public Button compareBackButton;

    [Header("Navigation Button for History Screen")]
    public Button historyBackButton;

    private void Start()
    {
        if (viewOffenseCoachButton != null)
            viewOffenseCoachButton.onClick.AddListener(() =>
            {
                var state = CoachesService.GetTeamState();
                if (string.IsNullOrEmpty(state?.offence_coach)) return;
                var coach = CoachesService.GetCoachById(state.offence_coach);
                if (coach == null)
                {
                    Debug.LogWarning($"[AppManager] Offence coach '{state.offence_coach}' not found in catalog.");
                    return;
                }
                var populator = coachDetailsScreen?.GetComponentInChildren<CoachProfilePopulator>(true);
                populator?.PopulateFromRecord(coach);
                ShowScreen(coachDetailsScreen);
            });
        if (fireOffenseCoachButton != null)
            fireOffenseCoachButton.onClick.AddListener(() => Debug.Log("Fire offence coach button was clicked"));
        if (viewDefenseCoachButton != null)
            viewDefenseCoachButton.onClick.AddListener(() =>
            {
                var state = CoachesService.GetTeamState();
                if (string.IsNullOrEmpty(state?.defence_coach)) return;
                var coach = CoachesService.GetCoachById(state.defence_coach);
                if (coach == null)
                {
                    Debug.LogWarning($"[AppManager] Defence coach '{state.defence_coach}' not found in catalog.");
                    return;
                }
                var populator = coachDetailsScreen?.GetComponentInChildren<CoachProfilePopulator>(true);
                populator?.PopulateFromRecord(coach);
                ShowScreen(coachDetailsScreen);
            });
        if (fireDefenseCoachButton != null)
            fireDefenseCoachButton.onClick.AddListener(() => Debug.Log("Fire defence coach button was clicked"));
        if (viewSpecialCoachButton != null)
            viewSpecialCoachButton.onClick.AddListener(() =>
            {
                var state = CoachesService.GetTeamState();
                if (string.IsNullOrEmpty(state?.special_teams_coach)) return;
                var coach = CoachesService.GetCoachById(state.special_teams_coach);
                if (coach == null)
                {
                    Debug.LogWarning($"[AppManager] Special teams coach '{state.special_teams_coach}' not found in catalog.");
                }
                var populator = coachDetailsScreen?.GetComponentInChildren<CoachProfilePopulator>(true);
                populator?.PopulateFromRecord(coach);
                ShowScreen(coachDetailsScreen);
            });
        if (hiringCoachMarketButton != null)
            hiringCoachMarketButton.onClick.AddListener(() => ShowScreen(coachHiringScreen));
        if (performanceButton != null)
            performanceButton.onClick.AddListener(() => ShowScreen(performanceScreen));
        if (historyButton != null)
            historyButton.onClick.AddListener(() => ShowScreen(historyScreen));

        if (backToMainMenuButton != null)
            backToMainMenuButton.onClick.AddListener(() => ShowScreen(mainMenu));
        if (refreshButton != null)
            refreshButton.onClick.AddListener(() => coachHiringScreen?.GetComponent<CoachHiringMarket>()?.RefreshCoaches());

        if (hireCoach1Button != null)
            hireCoach1Button.onClick.AddListener(() => Debug.Log("Hire Coach 1 button was clicked"));
        if (compareCoach1Button != null)
            compareCoach1Button.onClick.AddListener(() =>
                OpenCompareScreen(coachHiringScreen?.GetComponent<CoachHiringMarket>()?.GetCoach(1)));
        if (viewCoach1Button != null)
            viewCoach1Button.onClick.AddListener(() =>
            {
                var market = coachHiringScreen?.GetComponent<CoachHiringMarket>();
                var populator = coachDetailsScreen?.GetComponentInChildren<CoachProfilePopulator>(true);
                if (market != null && populator != null)
                    populator.PopulateFromRecord(market.GetCoach(1));
                ShowScreen(coachDetailsScreen);
            });

        if (hireCoach2Button != null)
            hireCoach2Button.onClick.AddListener(() => Debug.Log("Hire Coach 2 button was clicked"));
        if (compareCoach2Button != null)
            compareCoach2Button.onClick.AddListener(() =>
                OpenCompareScreen(coachHiringScreen?.GetComponent<CoachHiringMarket>()?.GetCoach(2)));
        if (viewCoach2Button != null)
            viewCoach2Button.onClick.AddListener(() =>
            {
                var market = coachHiringScreen?.GetComponent<CoachHiringMarket>();
                var populator = coachDetailsScreen?.GetComponentInChildren<CoachProfilePopulator>(true);
                if (market != null && populator != null)
                    populator.PopulateFromRecord(market.GetCoach(2));
                ShowScreen(coachDetailsScreen);
            });

        if (compareButton != null)
            compareButton.onClick.AddListener(() =>
                OpenCompareScreen(coachDetailsScreen?.GetComponentInChildren<CoachProfilePopulator>(true)?.CurrentRecord));
        if (backToMarketButton != null)
            backToMarketButton.onClick.AddListener(() => ShowScreen(coachHiringScreen));
        if (compareBackButton != null)
            compareBackButton.onClick.AddListener(() => ShowScreen(coachHiringScreen));
        if (hireButton != null)
            hireButton.onClick.AddListener(() => Debug.Log("Hire button was clicked"));

        if (backButton != null)
            backButton.onClick.AddListener(() => ShowScreen(mainMenu));
        if (historyBackButton != null)
            historyBackButton.onClick.AddListener(() => ShowScreen(mainMenu));
        if (detailedStatsButton != null)
            detailedStatsButton.onClick.AddListener(() => Debug.Log("Detailed Stats button was clicked"));

        if (OpenPerformanceOnLoad)
        {
            OpenPerformanceOnLoad = false;
            ShowScreen(performanceScreen);
        }
        else
        {
            ShowScreen(mainMenu);
        }
        UpdateTeamRating();
    }

        private void OpenCompareScreen(CoachDatabaseRecord candidate)
    {
        if (candidate == null) return;

        var state = CoachesService.GetTeamState();
        string slottedId = candidate.coach_type switch
        {
            "O" => state?.offence_coach,
            "D" => state?.defence_coach,
            "S" => state?.special_teams_coach,
            _ => null
        };
        var slotted = !string.IsNullOrEmpty(slottedId) ? CoachesService.GetCoachById(slottedId) : null;

        var populator = compareScreen?.GetComponentInChildren<CoachComparePopulator>(true);
        populator?.Populate(candidate, slotted);
        ShowScreen(compareScreen);
    }

    private void UpdateTeamRating()
    {
        if (teamRatingText != null)
        {
            float rating = CoachesService.GetTeamRating();
            teamRatingText.text = $"Team Rating: +{Mathf.RoundToInt(rating)}%";
        }
    }

        private void ShowScreen(GameObject targetScreen)
    {
        // Deactivate all
        mainMenu.SetActive(false);
        coachHiringScreen.SetActive(false);
        coachDetailsScreen.SetActive(false);
        performanceScreen.SetActive(false);
        if (historyScreen != null)
            historyScreen.SetActive(false);
        if (compareScreen != null)
            compareScreen.SetActive(false);

        // Activate the one you want
        if (targetScreen != null)
            targetScreen.SetActive(true);

        if (targetScreen == mainMenu)
            UpdateTeamRating();
    }
}

//     // Start is called before the first frame update
//     void Start()
//     {
//         // Assigning button clicks to different menus
//         hireCoachButton.onClick.AddListener(ShowCoachHiringScreen);
//         viewOffenseCoachButton.onClick.AddListener(ShowOffenseCoachDetails);
//         viewDefenseCoachButton.onClick.AddListener(ShowDefenseCoachDetails);
//         hiringCoachMarketButton.onClick.AddListener(ShowHiringMarket);
//         performanceButton.onClick.AddListener(ShowPerformanceScreen);
//         historyButton.onClick.AddListener(ShowHistoryScreen);

//         ShowMainMenu();
//     }

//     public void ShowMainMenu()
//     {
//         mainMenu.SetActive(true);
//         coachHiringScreen.SetActive(false);
//     }

//     public void ShowCoachHiringScreen()
//     {
//         mainMenu.SetActive(false);
//         coachHiringScreen.SetActive(true);
//     }

//     public void ShowOffenseCoachDetails()
//     {
//         mainMenu.SetActive(false);
//         CoachProfilePopulator coachProfilePopulator = FindObjectOfType<CoachProfilePopulator>();

//         // Load coach
//         if (coachProfilePopulator != null)
//         {
//             // coachProfilePopulator.coach.LoadCoachProfile("offense");
//         }
//         coachDetailsScreen.SetActive(true);
//     }

//     public void ShowDefenseCoachDetails()
//     {
//         mainMenu.SetActive(false);
//         CoachProfilePopulator coachProfilePopulator = FindObjectOfType<CoachProfilePopulator>();

//         // Load coach
//         if (coachProfilePopulator != null)
//         {
//             // coachProfilePopulator.coach.LoadCoachProfile("defense");
//         }
//         coachDetailsScreen.SetActive(true);
//     }

//     public void ShowHiringMarket()
//     {
//         mainMenu.SetActive(false);
//         coachHiringScreen.SetActive(true);
//     }

//     public void ShowPerformanceScreen()
//     {
//         mainMenu.SetActive(false);
//         coachDetailsScreen.SetActive(true);

//         // Populate the coach details UI
//     }

//     public void ShowHistoryScreen()
//     {
//         mainMenu.SetActive(false);
//         coachDetailsScreen.SetActive(true);

//         // Populate the coach details UI
//     }
// }