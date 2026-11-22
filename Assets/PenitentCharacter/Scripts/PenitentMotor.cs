using System;
using UnityEngine;

namespace PenitentCharacter
{
    [RequireComponent(typeof(Rigidbody2D), typeof(Collider2D))]
    public class PenitentMotor : MonoBehaviour
    {
        private Rigidbody2D rb;
        private Collider2D col;

        [Header("Authentic Blasphemous Movement Parameters")]
        public float walkSpeed = 6.0f;
        public float acceleration = 45f;
        public float deceleration = 50f;
        public float airAcceleration = 35f;
        [Tooltip("How fast horizontal speed is killed in the air when there is no input (prevents air sliding).")]
        public float airStopDeceleration = 120f;

        [Header("Jump Parameters (Enhanced Height)")]
        public float jumpHeight = 5.2f; // Increased jump height
        public float jumpDuration = 0.48f;
        public float cancelJumpFactor = 0.45f;
        public float coyoteTime = 0.08f;
        public float jumpBufferTime = 0.12f;

        [Header("Dash / Dodge Parameters")]
        public float dashSpeed = 12.0f;
        public float dashDuration = 0.32f;
        public float dashCooldown = 0.50f;

        [Header("Ground Detection")]
        public LayerMask groundLayer;
        [Tooltip("Height of the ground-check box under the feet.")]
        public float groundCheckDistance = 0.15f;
        [Tooltip("After a jump starts, the character cannot count as grounded for this long (stops the flag lingering for a frame).")]
        public float jumpGroundLock = 0.1f;

        public bool IsGrounded { get; private set; }
        public bool IsFacingRight { get; private set; } = true;
        public bool IsDashing { get; private set; } = false;
        public Vector2 Velocity => rb != null ? rb.linearVelocity : Vector2.zero;

        /// <summary>Jump actually executed.</summary>
        public event Action OnJumped;
        /// <summary>Touched the ground after being airborne. Argument = fall speed just before impact.</summary>
        public event Action<float> OnLanded;
        public event Action OnDashStarted;
        public event Action OnDashEnded;

        private float gravity;
        private float initialJumpVelocity;
        private float coyoteTimer;
        private float jumpBufferTimer;
        private float dashTimer;
        private float dashCooldownTimer;
        private int dashDirection;
        private float jumpLockUntil;
        private float peakFallSpeed;

        private readonly Collider2D[] groundHits = new Collider2D[8];
        private ContactFilter2D groundFilter;

        private void Awake()
        {
            rb = GetComponent<Rigidbody2D>();
            col = GetComponent<Collider2D>();

            RecalculateJumpPhysics();

            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.freezeRotation = true;
        }

        public void RecalculateJumpPhysics()
        {
            // Authentic Kinematic2D jump derivation:
            // h = 0.5 * g * t^2 => g = 2 * h / t^2
            // v0 = g * t = 2 * h / t
            gravity = (2f * jumpHeight) / (jumpDuration * jumpDuration);
            initialJumpVelocity = (2f * jumpHeight) / jumpDuration;

            if (rb != null)
            {
                rb.gravityScale = gravity / Physics2D.gravity.magnitude;
            }
        }

        private void Update()
        {
            RefreshGrounded();

            if (IsGrounded)
            {
                coyoteTimer = coyoteTime;
            }
            else
            {
                coyoteTimer -= Time.deltaTime;
            }

            if (jumpBufferTimer > 0f) jumpBufferTimer -= Time.deltaTime;
            if (dashCooldownTimer > 0f) dashCooldownTimer -= Time.deltaTime;
        }

        // ---------------------------------------------------------------
        // GROUND CHECK
        // ---------------------------------------------------------------

        /// <summary>
        /// Re-evaluates IsGrounded right now. Called by Update and by the state machine
        /// before any decision, so movement logic never works with a stale value.
        /// </summary>
        public void RefreshGrounded()
        {
            bool wasGrounded = IsGrounded;
            IsGrounded = Time.time >= jumpLockUntil && OverlapGround();

            if (!IsGrounded)
            {
                peakFallSpeed = Mathf.Max(peakFallSpeed, -rb.linearVelocity.y);
            }
            else if (!wasGrounded)
            {
                float impact = peakFallSpeed;
                peakFallSpeed = 0f;
                OnLanded?.Invoke(impact);
            }
        }

        private bool OverlapGround()
        {
            Bounds b = col.bounds;
            float h = Mathf.Max(0.05f, groundCheckDistance);

            // Box sits just under the feet (slightly overlapping the sole so resting contact is detected).
            Vector2 size = new Vector2(b.size.x * 0.85f, h);
            Vector2 center = new Vector2(b.center.x, b.min.y + 0.03f - h * 0.5f);

            int mask = groundLayer.value != 0 ? groundLayer.value : Physics2D.DefaultRaycastLayers;
            groundFilter.useTriggers = false;
            groundFilter.SetLayerMask(mask);

            int count = Physics2D.OverlapBox(center, size, 0f, groundFilter, groundHits);
            for (int i = 0; i < count; i++)
            {
                Collider2D hit = groundHits[i];
                if (hit == null || hit.transform.root == transform.root) continue;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------
        // MOVEMENT
        // ---------------------------------------------------------------

        public void StepMovement(float moveInput, bool jumpPressed, bool jumpReleased, bool dashRequested, float dt)
        {
            if (jumpPressed)
            {
                jumpBufferTimer = jumpBufferTime;
            }

            if (IsDashing)
            {
                TickDash(dt);
                if (IsDashing) return;
            }

            // Start Dash (STRICTLY grounded only)
            if (dashRequested && dashCooldownTimer <= 0f && IsGrounded)
            {
                StartDash();
                return;
            }

            // Horizontal Movement & Air Control
            float currentSpeedX = rb.linearVelocity.x;
            float newSpeedX;

            if (IsGrounded)
            {
                float targetSpeed = moveInput * walkSpeed;
                float accelRate = Mathf.Abs(targetSpeed) > 0.01f ? acceleration : deceleration;
                newSpeedX = Mathf.MoveTowards(currentSpeedX, targetSpeed, accelRate * dt);
            }
            else
            {
                if (Mathf.Abs(moveInput) > 0.05f)
                {
                    float targetSpeed = moveInput * walkSpeed;
                    newSpeedX = Mathf.MoveTowards(currentSpeedX, targetSpeed, airAcceleration * dt);
                }
                else
                {
                    // No input in the air = stop drifting almost instantly (no air sliding)
                    newSpeedX = Mathf.MoveTowards(currentSpeedX, 0f, airStopDeceleration * dt);
                }
            }

            // Update Facing
            if (Mathf.Abs(moveInput) > 0.05f)
            {
                IsFacingRight = moveInput > 0f;
            }

            // Jump Execution
            float newSpeedY = rb.linearVelocity.y;
            if (jumpBufferTimer > 0f && coyoteTimer > 0f)
            {
                newSpeedY = initialJumpVelocity;
                jumpBufferTimer = 0f;
                coyoteTimer = 0f;

                // Leave the ground immediately and block re-grounding for a moment
                jumpLockUntil = Time.time + jumpGroundLock;
                IsGrounded = false;
                peakFallSpeed = 0f;
                OnJumped?.Invoke();
            }
            else if (jumpReleased && newSpeedY > 0f)
            {
                // Early release reduces jump velocity for variable jump heights
                newSpeedY *= cancelJumpFactor;
            }

            rb.linearVelocity = new Vector2(newSpeedX, newSpeedY);
        }

        // ---------------------------------------------------------------
        // DASH / DODGE (ground slide)
        // ---------------------------------------------------------------

        private void StartDash()
        {
            IsDashing = true;
            dashTimer = dashDuration;
            dashDirection = IsFacingRight ? 1 : -1;
            rb.linearVelocity = new Vector2(dashDirection * dashSpeed, rb.linearVelocity.y);
            OnDashStarted?.Invoke();
        }

        /// <summary>
        /// Advances an active dash. Re-checks the ground EVERY frame: the moment the feet leave
        /// the ground (ledge, platform edge) the dash is cancelled and horizontal speed is clamped,
        /// so the player can never slide through the air.
        /// </summary>
        public void TickDash(float dt)
        {
            if (!IsDashing) return;

            if (!IsGrounded)
            {
                EndDash();
                return;
            }

            dashTimer -= dt;
            rb.linearVelocity = new Vector2(dashDirection * dashSpeed, rb.linearVelocity.y);

            if (dashTimer <= 0f)
            {
                EndDash();
            }
        }

        private void EndDash()
        {
            IsDashing = false;
            dashCooldownTimer = dashCooldown;

            Vector2 v = rb.linearVelocity;
            v.x = Mathf.Clamp(v.x, -walkSpeed, walkSpeed);
            rb.linearVelocity = v;

            OnDashEnded?.Invoke();
        }

        // ---------------------------------------------------------------
        // HELPERS USED BY THE STATE MACHINE
        // ---------------------------------------------------------------

        /// <summary>Instantly stops horizontal movement (grounded attacks / parry).</summary>
        public void StopHorizontalMovement()
        {
            if (!IsDashing)
            {
                rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
            }
        }

        /// <summary>Bleeds off horizontal speed (used for air attacks so they never slide).</summary>
        public void DampHorizontal(float dt)
        {
            if (IsDashing) return;
            float rate = IsGrounded ? deceleration : airStopDeceleration;
            rb.linearVelocity = new Vector2(Mathf.MoveTowards(rb.linearVelocity.x, 0f, rate * dt), rb.linearVelocity.y);
        }
    }
}
