using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// Lets the player leave the level and return to the main menu (Esc on keyboard, Start on gamepad).
// Without this, a built game would have no way back to the menu or to quit.
public class ReturnToMenu : MonoBehaviour
{
    [Tooltip("Scene loaded when Esc / Start is pressed. It must be in the Build Profiles scene list.")]
    [SerializeField] private string menuSceneName = "MainMenu";

    private bool loading;

    private void Update()
    {
        if (loading)
        {
            return;
        }

        bool pressed = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
                       (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);
        if (!pressed)
        {
            return;
        }

        if (!Application.CanStreamedLevelBeLoaded(menuSceneName))
        {
            Debug.LogError($"ReturnToMenu: scene '{menuSceneName}' can't be loaded. Add it to the Build Profiles scene list.");
            return;
        }

        loading = true;
        SceneManager.LoadSceneAsync(menuSceneName);
    }
}
