using UnityEngine;

namespace PenitentCharacter
{
    public class PenitentDebugHUD : MonoBehaviour
    {
        private PenitentMotor motor;
        private PenitentStateMachine sm;
        private Rigidbody2D rb;

        private void Start()
        {
            motor = GetComponent<PenitentMotor>();
            sm = GetComponent<PenitentStateMachine>();
            rb = GetComponent<Rigidbody2D>();
        }

        private void OnGUI()
        {
            if (motor == null || sm == null || rb == null) return;

            GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
            boxStyle.fontSize = 14;
            boxStyle.alignment = TextAnchor.UpperLeft;
            boxStyle.normal.textColor = Color.white;

            GUILayout.BeginArea(new Rect(15, 15, 360, 240), boxStyle);
            GUILayout.Label("<b>=== PENITENT ONE CONTROLLER ===</b>");
            GUILayout.Label($"<b>Weapon:</b> <color=cyan>{sm.CurrentWeapon}</color> (Press Q to Switch)");
            GUILayout.Label($"<b>State:</b> <color=yellow>{sm.CurrentState}</color>");
            GUILayout.Label($"<b>Grounded:</b> {(motor.IsGrounded ? "<color=green>YES</color>" : "<color=red>NO</color>")}");
            GUILayout.Label($"<b>Velocity:</b> X={rb.linearVelocity.x:F2}, Y={rb.linearVelocity.y:F2}");
            GUILayout.Label($"<b>Jump Height:</b> {motor.jumpHeight} units (Snappy Air Control)");
            GUILayout.Label($"<b>Facing:</b> {(motor.IsFacingRight ? "RIGHT" : "LEFT")}");
            GUILayout.Space(6);
            GUILayout.Label("<b>Controls:</b>");
            GUILayout.Label("A / D: Run | Space: Jump (High, No Air Slide)");
            GUILayout.Label("Shift: Dodge Slide (Grounded only)");
            GUILayout.Label("J / LMB: Attack (Weapon-Specific Arc & Hitbox)");
            GUILayout.Label("K / RMB: Parry (Mea Culpa) | Q: Switch Weapon");
            GUILayout.EndArea();
        }
    }
}
