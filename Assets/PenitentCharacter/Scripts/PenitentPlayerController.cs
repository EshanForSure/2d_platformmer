using UnityEngine;

namespace PenitentCharacter
{
    [RequireComponent(typeof(PenitentMotor), typeof(PenitentStateMachine))]
    public class PenitentPlayerController : MonoBehaviour
    {
        private PenitentStateMachine stateMachine;
        private PenitentTelemetry telemetry;

        [Header("Controls")]
        public KeyCode leftKey = KeyCode.A;
        public KeyCode rightKey = KeyCode.D;
        public KeyCode jumpKey = KeyCode.Space;
        public KeyCode dodgeKey = KeyCode.LeftShift;
        public KeyCode attackKey = KeyCode.J;
        public KeyCode parryKey = KeyCode.K;
        public KeyCode switchWeaponKey = KeyCode.Q;

        [Header("Auto-setup")]
        [Tooltip("Adds PenitentAudio (action sound effects) automatically if missing.")]
        public bool autoAddAudio = true;
        [Tooltip("Adds PenitentTelemetry (last-30s behaviour recorder + live file) automatically if missing.")]
        public bool autoAddTelemetry = true;

        private void Awake()
        {
            stateMachine = GetComponent<PenitentStateMachine>();

            if (autoAddAudio && GetComponent<PenitentAudio>() == null)
            {
                gameObject.AddComponent<PenitentAudio>();
            }

            telemetry = GetComponent<PenitentTelemetry>();
            if (telemetry == null && autoAddTelemetry)
            {
                telemetry = gameObject.AddComponent<PenitentTelemetry>();
            }
        }

        private void Update()
        {
            float moveInput = 0f;
            if (Input.GetKey(rightKey) || Input.GetKey(KeyCode.RightArrow)) moveInput += 1f;
            if (Input.GetKey(leftKey) || Input.GetKey(KeyCode.LeftArrow)) moveInput -= 1f;

            bool jumpPressed = Input.GetKeyDown(jumpKey);
            bool jumpReleased = Input.GetKeyUp(jumpKey);
            bool dodgePressed = Input.GetKeyDown(dodgeKey);
            bool attackPressed = Input.GetKeyDown(attackKey) || Input.GetMouseButtonDown(0);
            bool parryPressed = Input.GetKeyDown(parryKey) || Input.GetMouseButtonDown(1);

            if (telemetry != null)
            {
                telemetry.RecordInput(moveInput, jumpPressed, dodgePressed, attackPressed, parryPressed);
            }

            if (Input.GetKeyDown(switchWeaponKey))
            {
                stateMachine.SwitchWeapon();
            }

            stateMachine.UpdateStateMachine(moveInput, jumpPressed, jumpReleased, dodgePressed, attackPressed, parryPressed, Time.deltaTime);
        }
    }
}
