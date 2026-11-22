using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Controller for the main menu scene: Play, Options (volume + fullscreen) and Quit.
// Works with mouse, keyboard (arrows / Enter / Esc) and gamepad (stick or d-pad / South / East).
public class MainMenu : MonoBehaviour
{
    private const string VolumeKey = "MasterVolume";

    [Header("Scenes")]
    [Tooltip("Scene loaded by Play. It must be in File > Build Profiles > Scene List.")]
    [SerializeField] private string levelSceneName = "SampleScene";

    [Header("Panels")]
    [SerializeField] private GameObject mainPanel;
    [SerializeField] private GameObject optionsPanel;
    [SerializeField] private GameObject loadingLabel;

    [Header("Buttons")]
    [SerializeField] private Button playButton;
    [SerializeField] private Button optionsButton;
    [SerializeField] private Button quitButton;
    [SerializeField] private Button backButton;

    [Header("Options")]
    [SerializeField] private Slider volumeSlider;
    [SerializeField] private Text volumeValueText;
    [SerializeField] private Toggle fullscreenToggle;

    private bool loading;

    // Apply the saved master volume as soon as the game starts, in any scene.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ApplySavedVolume()
    {
        AudioListener.volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumeKey, 1f));
    }

    private void Awake()
    {
        Time.timeScale = 1f;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        if (playButton != null) playButton.onClick.AddListener(Play);
        if (optionsButton != null) optionsButton.onClick.AddListener(OpenOptions);
        if (quitButton != null) quitButton.onClick.AddListener(Quit);
        if (backButton != null) backButton.onClick.AddListener(CloseOptions);

        if (volumeSlider != null)
        {
            volumeSlider.minValue = 0f;
            volumeSlider.maxValue = 1f;
            volumeSlider.SetValueWithoutNotify(AudioListener.volume);
            volumeSlider.onValueChanged.AddListener(SetVolume);
            UpdateVolumeText(volumeSlider.value);
        }

        if (fullscreenToggle != null)
        {
            fullscreenToggle.SetIsOnWithoutNotify(Screen.fullScreen);
            fullscreenToggle.onValueChanged.AddListener(SetFullscreen);
        }

        if (loadingLabel != null) loadingLabel.SetActive(false);
        ShowPanel(false);
    }

    private void Update()
    {
        bool optionsOpen = optionsPanel != null && optionsPanel.activeSelf;

        // Esc / gamepad East closes the options panel.
        if (optionsOpen)
        {
            bool back = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
                        (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame);
            if (back)
            {
                CloseOptions();
                return;
            }
        }

        // If the mouse click cleared the selection, the first keyboard/gamepad navigation input re-selects a control.
        EventSystem es = EventSystem.current;
        if (es != null && es.currentSelectedGameObject == null && !loading && NavigationInputPressed())
        {
            GameObject target = optionsOpen
                ? (volumeSlider != null ? volumeSlider.gameObject : null)
                : (playButton != null ? playButton.gameObject : null);
            es.SetSelectedGameObject(target);
        }
    }

    private static bool NavigationInputPressed()
    {
        var kb = Keyboard.current;
        if (kb != null && (kb.upArrowKey.wasPressedThisFrame || kb.downArrowKey.wasPressedThisFrame ||
                           kb.leftArrowKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame ||
                           kb.wKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame ||
                           kb.enterKey.wasPressedThisFrame))
        {
            return true;
        }

        var pad = Gamepad.current;
        return pad != null && (pad.dpad.up.wasPressedThisFrame || pad.dpad.down.wasPressedThisFrame ||
                               pad.dpad.left.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame ||
                               pad.buttonSouth.wasPressedThisFrame || pad.leftStick.ReadValue().sqrMagnitude > 0.25f);
    }

    public void Play()
    {
        if (loading)
        {
            return;
        }

        if (!Application.CanStreamedLevelBeLoaded(levelSceneName))
        {
            Debug.LogError($"MainMenu: scene '{levelSceneName}' can't be loaded. Add it to the Build Profiles scene list.");
            return;
        }

        loading = true;
        if (loadingLabel != null) loadingLabel.SetActive(true);
        if (mainPanel != null) mainPanel.SetActive(false);
        SceneManager.LoadSceneAsync(levelSceneName);
    }

    public void OpenOptions()
    {
        ShowPanel(true);
    }

    public void CloseOptions()
    {
        ShowPanel(false);
    }

    public void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void ShowPanel(bool options)
    {
        if (mainPanel != null) mainPanel.SetActive(!options);
        if (optionsPanel != null) optionsPanel.SetActive(options);

        EventSystem es = EventSystem.current;
        if (es != null)
        {
            GameObject first = options
                ? (volumeSlider != null ? volumeSlider.gameObject : null)
                : (playButton != null ? playButton.gameObject : null);
            es.SetSelectedGameObject(first);
        }
    }

    private void SetVolume(float value)
    {
        AudioListener.volume = value;
        PlayerPrefs.SetFloat(VolumeKey, value);
        UpdateVolumeText(value);
    }

    private void UpdateVolumeText(float value)
    {
        if (volumeValueText != null)
        {
            volumeValueText.text = Mathf.RoundToInt(value * 100f) + "%";
        }
    }

    private void SetFullscreen(bool on)
    {
        Screen.fullScreen = on;
    }

    private void OnDisable()
    {
        PlayerPrefs.Save();
    }
}
