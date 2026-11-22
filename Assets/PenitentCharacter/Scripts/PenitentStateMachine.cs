using System;
using UnityEngine;

namespace PenitentCharacter
{
    public enum WeaponType
    {
        MeaCulpa, // Classic Sword
        Veredicto // Heavy War Censer
    }

    public enum PenitentState
    {
        Idle,
        Run,
        Jump,
        Fall,
        Dodge,
        Attack1,
        Attack2,
        Attack3,
        HeavyAttack,
        HeavyJumpAttack,
        Parry,
        ParrySuccess
    }

    [RequireComponent(typeof(PenitentMotor))]
    public class PenitentStateMachine : MonoBehaviour
    {
        private PenitentMotor motor;
        private PenitentAnimationPlayer animPlayer;
        private Rigidbody2D body;

        public PenitentState CurrentState { get; private set; } = PenitentState.Idle;
        public WeaponType CurrentWeapon { get; private set; } = WeaponType.Veredicto;

        // ---- Events (consumed by PenitentAudio / PenitentTelemetry) ----
        public event Action<PenitentState, PenitentState> OnStateChanged;
        public event Action<PenitentState, WeaponType> OnAttackStarted;
        /// <summary>true = successful parry, false = parry attempt.</summary>
        public event Action<bool> OnParryStarted;
        public event Action<WeaponType> OnWeaponSwitched;
        public event Action<PenitentHitbox, Collider2D> OnHitLanded;

        [Header("Hitbox References")]
        public PenitentHitbox meleeHitbox;
        public PenitentHitbox heavyHitbox;

        [Header("Combat Settings")]
        public float comboWindow = 0.55f;
        public float parryDuration = 0.40f;

        private float stateTimer = 0f;
        private bool attackQueued = false;
        private bool isAttacking = false;
        private bool isParrying = false;

        private void Awake()
        {
            motor = GetComponent<PenitentMotor>();
            body = GetComponent<Rigidbody2D>();
            animPlayer = GetComponentInChildren<PenitentAnimationPlayer>();

            // Find or create hitboxes if not assigned
            SetupHitboxes();

            if (meleeHitbox != null) meleeHitbox.HitTarget += HandleHitboxHit;
            if (heavyHitbox != null) heavyHitbox.HitTarget += HandleHitboxHit;

            if (animPlayer != null)
            {
                animPlayer.OnAnimationCompleted += OnAnimationCompleted;
                animPlayer.OnFrameChanged += OnAnimationFrameChanged;
            }
        }

        private void SetupHitboxes()
        {
            if (meleeHitbox == null)
            {
                var meleeObj = transform.Find("MeleeHitbox");
                if (meleeObj != null) meleeHitbox = meleeObj.GetComponent<PenitentHitbox>();
            }

            if (heavyHitbox == null)
            {
                var heavyObj = transform.Find("HeavyHitbox");
                if (heavyObj != null) heavyHitbox = heavyObj.GetComponent<PenitentHitbox>();
            }

            // Create dynamically if missing
            if (heavyHitbox == null)
            {
                var hObj = new GameObject("HeavyHitbox");
                hObj.transform.SetParent(transform);
                hObj.transform.localPosition = Vector3.zero;
                var col = hObj.AddComponent<BoxCollider2D>();
                col.isTrigger = true;
                col.size = new Vector2(2.4f, 2.2f);
                col.offset = new Vector2(1.2f, 1.1f);
                heavyHitbox = hObj.AddComponent<PenitentHitbox>();
                heavyHitbox.damage = 45;
                heavyHitbox.knockback = 8.5f;
            }

            if (meleeHitbox == null)
            {
                var mObj = new GameObject("MeleeHitbox");
                mObj.transform.SetParent(transform);
                mObj.transform.localPosition = Vector3.zero;
                var col = mObj.AddComponent<BoxCollider2D>();
                col.isTrigger = true;
                col.size = new Vector2(1.8f, 1.6f);
                col.offset = new Vector2(0.9f, 1.0f);
                meleeHitbox = mObj.AddComponent<PenitentHitbox>();
                meleeHitbox.damage = 25;
                meleeHitbox.knockback = 4f;
            }
        }

        private void OnDestroy()
        {
            if (meleeHitbox != null) meleeHitbox.HitTarget -= HandleHitboxHit;
            if (heavyHitbox != null) heavyHitbox.HitTarget -= HandleHitboxHit;

            if (animPlayer != null)
            {
                animPlayer.OnAnimationCompleted -= OnAnimationCompleted;
                animPlayer.OnFrameChanged -= OnAnimationFrameChanged;
            }
        }

        private void HandleHitboxHit(PenitentHitbox hitbox, Collider2D other)
        {
            OnHitLanded?.Invoke(hitbox, other);
        }

        private void Play(string animName, bool forceRestart = false)
        {
            if (animPlayer != null) animPlayer.PlayAnimation(animName, forceRestart);
        }

        private void Face()
        {
            if (animPlayer != null) animPlayer.SetFacing(motor.IsFacingRight);
        }

        public void SwitchWeapon()
        {
            if (isAttacking || isParrying || motor.IsDashing) return;

            CurrentWeapon = (CurrentWeapon == WeaponType.MeaCulpa) ? WeaponType.Veredicto : WeaponType.MeaCulpa;
            Debug.Log($"[Penitent] Switched active weapon to: {CurrentWeapon}");
            OnWeaponSwitched?.Invoke(CurrentWeapon);
        }

        public void UpdateStateMachine(float moveInput, bool jumpPressed, bool jumpReleased, bool dodgePressed, bool attackPressed, bool parryPressed, float dt)
        {
            stateTimer += dt;

            // GROUND CHECK FIRST: every decision below uses a fresh grounded value
            motor.RefreshGrounded();

            // Handle Parry request (Mea Culpa only)
            if (parryPressed && CurrentWeapon == WeaponType.MeaCulpa && motor.IsGrounded && !isAttacking && !isParrying && !motor.IsDashing)
            {
                TriggerParry();
                return;
            }

            // Handle Attack request
            if (attackPressed && !isParrying && !motor.IsDashing)
            {
                if (isAttacking)
                {
                    attackQueued = true;
                }
                else
                {
                    TriggerAttack();
                    return;
                }
            }

            // Dashing / Dodging (STRICTLY grounded: TickDash cancels the dash the moment the ground is lost)
            if (motor.IsDashing)
            {
                motor.TickDash(dt);

                if (motor.IsDashing)
                {
                    SetState(PenitentState.Dodge);
                    Play("Dodge");
                    Face();
                    return;
                }
                // Dash ended this frame -> fall through to normal locomotion
            }

            // Attacking State handling
            if (isAttacking)
            {
                Face();
                if (motor.IsGrounded)
                {
                    motor.StopHorizontalMovement();
                }
                else
                {
                    // Air attack: never carry horizontal momentum (no sliding in the air)
                    motor.DampHorizontal(dt);
                }
                return;
            }

            // Parrying State handling
            if (isParrying)
            {
                Face();
                motor.StopHorizontalMovement();
                if (stateTimer >= parryDuration)
                {
                    isParrying = false;
                }
                return;
            }

            // Standard Locomotion (Idle, Run, Jump, Fall)
            motor.StepMovement(moveInput, jumpPressed, jumpReleased, dodgePressed, dt);
            Face();

            // Update Hitbox Facing & Position
            UpdateHitboxFacing();

            if (motor.IsDashing)
            {
                // Dash was started this frame by StepMovement
                SetState(PenitentState.Dodge);
                Play("Dodge");
                return;
            }

            if (!motor.IsGrounded)
            {
                if (body.linearVelocity.y > 0.1f)
                {
                    SetState(PenitentState.Jump);
                    if (CurrentWeapon == WeaponType.Veredicto)
                        Play(Mathf.Abs(moveInput) > 0.1f ? "Heavy_JumpHorizontal" : "Heavy_Jump");
                    else
                        Play(Mathf.Abs(moveInput) > 0.1f ? "JumpHorizontal" : "Jump");
                }
                else
                {
                    SetState(PenitentState.Fall);
                    if (CurrentWeapon == WeaponType.Veredicto)
                        Play(Mathf.Abs(moveInput) > 0.1f ? "Heavy_JumpHorizontal" : "Heavy_Jump");
                    else
                        Play(Mathf.Abs(moveInput) > 0.1f ? "JumpHorizontal" : "Jump");
                }
            }
            else
            {
                if (Mathf.Abs(moveInput) > 0.1f)
                {
                    SetState(PenitentState.Run);
                    Play(CurrentWeapon == WeaponType.Veredicto ? "Heavy_Run" : "Run");
                }
                else
                {
                    SetState(PenitentState.Idle);
                    Play(CurrentWeapon == WeaponType.Veredicto ? "Heavy_Idle" : "Idle");
                }
            }
        }

        private void UpdateHitboxFacing()
        {
            float dir = motor.IsFacingRight ? 1f : -1f;
            if (heavyHitbox != null)
            {
                heavyHitbox.transform.localScale = new Vector3(dir, 1f, 1f);
            }
            if (meleeHitbox != null)
            {
                meleeHitbox.transform.localScale = new Vector3(dir, 1f, 1f);
            }
        }

        private void TriggerAttack()
        {
            isAttacking = true;
            attackQueued = false;
            stateTimer = 0f;
            UpdateHitboxFacing();

            if (CurrentWeapon == WeaponType.Veredicto)
            {
                if (!motor.IsGrounded)
                {
                    SetState(PenitentState.HeavyJumpAttack);
                    Play("Heavy_JumpAttack", true);
                }
                else
                {
                    SetState(PenitentState.HeavyAttack);
                    Play("Heavy_Attack", true);
                }
            }
            else
            {
                // Mea Culpa 3-hit combo
                if (CurrentState == PenitentState.Attack1)
                {
                    SetState(PenitentState.Attack2);
                    Play("Attack2", true);
                }
                else if (CurrentState == PenitentState.Attack2)
                {
                    SetState(PenitentState.Attack3);
                    Play("Attack3", true);
                }
                else
                {
                    SetState(PenitentState.Attack1);
                    Play("Attack1", true);
                }
            }

            OnAttackStarted?.Invoke(CurrentState, CurrentWeapon);
        }

        private void TriggerParry()
        {
            isParrying = true;
            stateTimer = 0f;
            SetState(PenitentState.Parry);
            Play("Parry", true);
            OnParryStarted?.Invoke(false);
        }

        public void TriggerParrySuccess()
        {
            isParrying = true;
            stateTimer = 0f;
            SetState(PenitentState.ParrySuccess);
            Play("ParrySuccess", true);
            OnParryStarted?.Invoke(true);
        }

        private void OnAnimationFrameChanged(string animName, int frameIndex)
        {
            // Synchronize active hitbox frames with animation swings
            if (animName == "Heavy_Attack")
            {
                // Active impact window: frames 8 to 15 (censer arc swing)
                if (frameIndex >= 8 && frameIndex <= 15)
                {
                    heavyHitbox.Activate(50, 9.0f);
                }
                else
                {
                    heavyHitbox.Deactivate();
                }
            }
            else if (animName == "Heavy_JumpAttack")
            {
                // Active impact window: frames 5 to 11
                if (frameIndex >= 5 && frameIndex <= 11)
                {
                    heavyHitbox.Activate(45, 8.0f);
                }
                else
                {
                    heavyHitbox.Deactivate();
                }
            }
            else if (animName == "Attack1")
            {
                if (frameIndex >= 4 && frameIndex <= 9) meleeHitbox.Activate(25, 4.0f);
                else meleeHitbox.Deactivate();
            }
            else if (animName == "Attack2")
            {
                if (frameIndex >= 3 && frameIndex <= 7) meleeHitbox.Activate(30, 4.5f);
                else meleeHitbox.Deactivate();
            }
            else if (animName == "Attack3")
            {
                if (frameIndex >= 4 && frameIndex <= 9) meleeHitbox.Activate(45, 7.0f);
                else meleeHitbox.Deactivate();
            }
        }

        private void OnAnimationCompleted(string animName)
        {
            heavyHitbox?.Deactivate();
            meleeHitbox?.Deactivate();

            if (animName == "Heavy_Attack" || animName == "Heavy_JumpAttack")
            {
                isAttacking = false;
                attackQueued = false;
                SetState(motor.IsGrounded ? PenitentState.Idle : PenitentState.Fall);
            }
            else if (animName == "Attack1" || animName == "Attack2" || animName == "Attack3")
            {
                if (attackQueued && animName != "Attack3")
                {
                    TriggerAttack();
                }
                else
                {
                    isAttacking = false;
                    attackQueued = false;
                    SetState(motor.IsGrounded ? PenitentState.Idle : PenitentState.Fall);
                }
            }
            else if (animName == "Parry" || animName == "ParrySuccess")
            {
                isParrying = false;
                SetState(motor.IsGrounded ? PenitentState.Idle : PenitentState.Fall);
            }
            else if (animName == "Dodge")
            {
                SetState(motor.IsGrounded ? PenitentState.Idle : PenitentState.Fall);
            }
        }

        private void SetState(PenitentState newState)
        {
            if (newState == CurrentState) return;

            PenitentState previous = CurrentState;
            CurrentState = newState;
            OnStateChanged?.Invoke(previous, newState);
        }
    }
}
