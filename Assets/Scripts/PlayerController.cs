using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

// Controls: A/D or arrows = move, Space = jump, Left click = sword slash, Right click = overhead attack,
// Shift = dash, E = teleport. Gamepad: left stick, South = jump, West = slash, North = overhead, East = dash, Right shoulder = teleport.
//
// Animations are designed to merge into each other:
//  - you keep running while you swing (slower, see the move multipliers) instead of freezing
//  - every change of animation is a short cross-dissolve, never a snap
//  - from the "cancel frame" of an attack / dash / teleport you can flow into running, jumping or the next action,
//    and presses made a little early are remembered (input buffer)
//  - speed changes smoothly (acceleration), so dash -> run and attack -> run hand over without a jolt
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(BoxCollider2D))]
public class PlayerController : MonoBehaviour
{
    private enum State { Normal, Slash, Overhead, Dash, Teleport, Damage, Dead }
    private enum Buffered { None, Slash, Overhead, Dash, Teleport }

    // One animated action: which frames of which clip, how long it lasts, and the frames that matter.
    [System.Serializable]
    public class ActionClip
    {
        public string clip;
        [Tooltip("First frame (0-based) shown. Skipping the first frames removes wind-up so the action reacts instantly.")]
        public int startFrame;
        public int endFrame = 15;
        [Tooltip("Seconds the whole action lasts. The frames are stretched to fit.")]
        public float duration = 0.5f;
        [Tooltip("The frame where the blow lands / the teleport happens.")]
        public int keyFrame;
        [Tooltip("From this frame you can cancel into running or the next action.")]
        public int cancelFrame = 12;
        [Tooltip("Seconds of cross-dissolve from whatever was showing before.")]
        public float blendIn = 0.06f;

        public ActionClip() { }

        public ActionClip(string clip, int startFrame, int endFrame, float duration, int keyFrame, int cancelFrame, float blendIn)
        {
            this.clip = clip;
            this.startFrame = startFrame;
            this.endFrame = endFrame;
            this.duration = duration;
            this.keyFrame = keyFrame;
            this.cancelFrame = cancelFrame;
            this.blendIn = blendIn;
        }

        public float FrameTime => duration / Mathf.Max(1, endFrame - startFrame + 1);
        public float TimeOfFrame(int frame) => Mathf.Max(0, frame - startFrame) * FrameTime;
    }

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 6f;
    [Tooltip("How quickly you reach full speed (units/second per second). Higher = snappier.")]
    [SerializeField] private float acceleration = 60f;
    [Tooltip("How quickly you slow to a stop when you let go.")]
    [SerializeField] private float deceleration = 80f;

    [Header("Jump")]
    [SerializeField] private float jumpForce = 15f;

    [Header("Ground Check")]
    [SerializeField] private float groundCheckDistance = 0.1f;
    [SerializeField] private LayerMask groundLayer = ~0;

    [Header("Controller")]
    [SerializeField] private float stickDeadzone = 0.2f;

    [Header("Blending")]
    [Tooltip("Fraction of run speed kept while swinging (1 = full speed).")]
    [SerializeField, Range(0f, 1f)] private float slashMoveMultiplier = 0.75f;
    [SerializeField, Range(0f, 1f)] private float overheadMoveMultiplier = 0.5f;
    [Tooltip("An attack / dash / teleport / jump pressed up to this long before it can start is remembered and fired as soon as possible.")]
    [SerializeField] private float inputBufferTime = 0.35f;
    [Tooltip("Cross-dissolve when switching between idle, run and the air poses.")]
    [SerializeField] private float locomotionBlend = 0.1f;
    [SerializeField] private float airBlend = 0.07f;
    [Tooltip("Landing animation length, and the fall speed needed to trigger it.")]
    [SerializeField] private float landDuration = 0.22f;
    [SerializeField] private float landMinSpeed = 4f;
    [Tooltip("Moving cuts the landing animation short after this many seconds.")]
    [SerializeField] private float landCancelAfter = 0.06f;

    [Header("Sword Slash (left click)")]
    [SerializeField] private ActionClip slash = new ActionClip("sword_slash", 2, 15, 0.42f, 8, 12, 0.05f);
    [SerializeField] private float attackRange = 1.2f;
    [SerializeField] private float attackHeight = 0.75f; // roughly chest height above the player's feet

    [Header("Overhead Attack (right click)")]
    [SerializeField] private ActionClip overhead = new ActionClip("overhead_attack", 4, 15, 0.56f, 8, 12, 0.05f);
    [SerializeField] private float overheadRange = 1.6f;

    [Header("Heavy Air Attack (any attack while airborne)")]
    [Tooltip("Played instead of the slash / overhead clips when the attack starts in the air. Ignored if the clip doesn't exist.")]
    [SerializeField] private ActionClip airSlash = new ActionClip("heavy_air", 0, 16, 0.48f, 8, 13, 0.05f);

    [Header("Heavy Weapon Impact Effects")]
    [SerializeField] private bool impactEffects = true;
    [Tooltip("Screen shake on the key frame of every swing, even one that hits nothing (strength, seconds).")]
    [SerializeField] private float swingShake = 0.06f;
    [SerializeField] private float swingShakeTime = 0.12f;
    [Tooltip("Extra screen shake when the blow connects with the boss (strength, seconds).")]
    [SerializeField] private float hitShake = 0.18f;
    [SerializeField] private float hitShakeTime = 0.22f;
    [Tooltip("Real-time seconds the game freezes when a blow connects with the boss.")]
    [SerializeField] private float hitStopTime = 0.07f;
    [Tooltip("The overhead attack's shake, hit-stop and sparks are this much stronger.")]
    [SerializeField] private float overheadImpactBoost = 1.5f;

    [Header("Power and Healing (shown on the HUD)")]
    [SerializeField] private float maxPower = 100f;
    [Tooltip("Power you start with.")]
    [SerializeField] private float power = 34f;
    [Tooltip("Power spent on one heal. Max Power / Heal Cost = the heals you can hold.")]
    [SerializeField] private float healCost = 33f;
    [SerializeField] private int healAmount = 2;
    [Tooltip("Power gained when a swing hits the boss.")]
    [SerializeField] private float powerPerHit = 10f;
    [SerializeField] private float powerPerOverheadHit = 18f;
    [SerializeField] private float healCooldown = 0.6f;

    [Header("Dash (Shift)")]
    [SerializeField] private ActionClip dash = new ActionClip("dash", 3, 15, 0.40f, 4, 12, 0.05f);
    [SerializeField] private float dashSpeed = 18f;
    [Tooltip("Frame at which the burst of speed is over and the dash slides to a stop.")]
    [SerializeField] private int dashMoveEndFrame = 11;
    [SerializeField] private float dashCooldown = 0.6f;
    [Tooltip("The dash only starts on the ground, and is cancelled if the ground disappears for longer than this (seconds). Prevents sliding through the air.")]
    [SerializeField] private float dashGroundGrace = 0.06f;

    [Header("Teleport (E)")]
    [Tooltip("The frame at which you actually move is this clip's Key Frame (the vortex has swallowed you by then).")]
    [SerializeField] private ActionClip teleport = new ActionClip("teleport", 0, 15, 0.62f, 8, 12, 0.08f);
    [Tooltip("How far the teleport carries you in the direction you face. It stops short of walls.")]
    [SerializeField] private float teleportDistance = 6f;
    [SerializeField] private float teleportCooldown = 1f;

    [Header("Health, damage and death")]
    [SerializeField] private int maxHealth = 5;
    [SerializeField] private ActionClip damage = new ActionClip("damage", 0, 15, 0.5f, 0, 10, 0.05f);
    [SerializeField] private float invulnerableTime = 1f;
    [SerializeField] private Vector2 knockback = new Vector2(4f, 3f);
    [SerializeField] private ActionClip death = new ActionClip("death", 0, 15, 1.3f, 0, 15, 0.1f);
    [Tooltip("Seconds after dying before the level restarts.")]
    [SerializeField] private float respawnDelay = 2.2f;
    [Tooltip("Testing only: H = take 1 damage, K = die, C = swap character. Switch off once real hazards exist.")]
    [SerializeField] private bool enableDebugKeys = true;

    [Header("Audio - Sword Swing")]
    // Clips are played in order and alternate when attacks are chained (A, B, A, B...).
    [SerializeField] private AudioClip[] swordSwingSounds;
    [SerializeField, Range(0f, 1f)] private float swordSwingVolume = 0.8f;
    // If the next attack starts later than this (seconds) after the previous one, the sequence restarts from the first clip.
    [SerializeField] private float swordComboWindow = 1f;

    [Header("Audio - Destroying Decorations")]
    [SerializeField, Range(0f, 1f)] private float foliageDestroyVolume = 0.8f;
    // Bush / flower / sunflower use the plant sounds. Names are matched case-insensitively ("Flower" also matches "Sunflower").
    [SerializeField] private AudioClip[] plantDestroySounds;
    [SerializeField] private string[] plantNameKeywords = { "Bush", "Flower" };
    // Trees keep their own tree-hit sounds (this field was previously called foliageDestroySounds).
    [FormerlySerializedAs("foliageDestroySounds")]
    [SerializeField] private AudioClip[] treeDestroySounds;
    [SerializeField] private string[] treeNameKeywords = { "Tree" };

    [Header("Extra systems (added automatically if missing)")]
    [Tooltip("Adds PlayerSfx: sounds for walk, jump, land, dash, attacks, teleport, damage, death and hits.")]
    [SerializeField] private bool autoAddSfx = true;
    [Tooltip("Adds PlayerTelemetry: records the last 30s of behaviour into a live file (F9 = show on screen, F10 = open folder).")]
    [SerializeField] private bool autoAddTelemetry = true;

    [Header("Wall Contact")]
    [Tooltip("While airborne, don't push into a wall: stops the player sticking to it when a direction key is held.")]
    [SerializeField] private float wallCheckDistance = 0.05f;

    private Rigidbody2D rb;
    private BoxCollider2D boxCollider;
    private SpriteRenderer spriteRenderer;
    private SpriteAnimator spriteAnimator;
    private AudioSource audioSource;      // one-shot effects (destroying decorations)
    private AudioSource swordSource;      // sword swings, so a new swing can cut off the previous tail
    private float[] swordStartOffsets;    // seconds of leading silence to skip in each sword clip
    private int nextSwordSoundIndex;
    private float lastSwordSoundTime = -999f;

    private float clock;                 // our own game clock (advanced in StepFrame), used for cooldowns and input buffers
    private State state = State.Normal;
    private float moveInput;
    private bool jumpRequested;
    private bool isGrounded;
    private bool wasGrounded = true;
    private bool facingRight = true;

    // This frame's button presses
    private bool pJump, pSlash, pOverhead, pDash, pTeleport, pHeal;
    private Buffered buffered = Buffered.None;
    private float bufferedUntil;
    private bool jumpBuffered;
    private float jumpBufferedUntil;

    // The action currently playing
    private float actionElapsed;
    private bool attackHitApplied;
    private float attackReach;
    private float healReadyTime;
    private ActionClip attackClip;       // the clip the current attack plays (slash, overhead or the air version)
    private float dashDirection = 1f;
    private float dashReadyTime;
    private float dashAirTime;           // how long the ground has been missing during the current dash
    private bool teleportMoved;
    private float teleportReadyTime;
    private float defaultGravity;
    private RigidbodyInterpolation2D defaultInterpolation;

    // Landing
    private float lastVelocityY;
    private bool landedFlag;
    private float landImpactSpeed;
    private float landTimer;

    // Health
    private int health;
    private float invulnerableUntil;
    private float deathTimer;
    private bool restarting;

    private readonly RaycastHit2D[] wallHits = new RaycastHit2D[8];
    private ContactFilter2D wallFilter;

    public float FacingDirection => facingRight ? 1f : -1f;

    public int CurrentHealth
    {
        get
        {
            return health;
        }
    }
    public int MaxHealth
    {
        get
        {
            return maxHealth;
        }
    }
    public bool IsDead
    {
        get
        {
            return state == State.Dead;
        }
    }

    // ---- Read-only info + events for the sound and behaviour-recording systems ----

    /// <summary>Fired whenever the player jumps, lands, dashes, attacks, teleports, is hurt, dies or lands a hit.</summary>
    public event System.Action<PlayerAction, float> ActionPerformed;
    /// <summary>Fired on every button press (even ones that do nothing): "jump", "slash", "overhead", "dash", "teleport".</summary>
    public event System.Action<string> InputPressed;

    public bool IsGrounded => isGrounded;
    public float MoveInputValue => moveInput;
    public Vector2 Velocity => rb != null ? rb.linearVelocity : Vector2.zero;
    public bool HasCustomSwingSounds
    {
        get
        {
            if (swordSwingSounds == null) return false;
            foreach (AudioClip c in swordSwingSounds)
            {
                if (c != null) return true;
            }
            return false;
        }
    }

    /// <summary>Idle, Run, Jump, Fall, Slash, Overhead, AirAttack, Dash, Teleport, Damage or Dead.</summary>
    public string StateName
    {
        get
        {
            switch (state)
            {
                case State.Normal:
                    if (!isGrounded) return rb != null && rb.linearVelocity.y > 0.1f ? "Jump" : "Fall";
                    return Mathf.Abs(moveInput) > 0.1f ? "Run" : "Idle";
                case State.Slash:
                case State.Overhead:
                    return attackClip == airSlash ? "AirAttack" : state.ToString();
                default:
                    return state.ToString();
            }
        }
    }

    private void Raise(PlayerAction action, float value = 0f)
    {
        ActionPerformed?.Invoke(action, value);
    }

    public float Power => power;
    public float MaxPower => maxPower;
    public float HealCost => healCost;
    public int HealAmount => healAmount;
    // How many heals the current power pays for / the most it can ever hold.
    public int HealCharges => Mathf.FloorToInt(power / Mathf.Max(1f, healCost) + 0.001f);
    public int MaxHealCharges => Mathf.FloorToInt(maxPower / Mathf.Max(1f, healCost) + 0.001f);

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        boxCollider = GetComponent<BoxCollider2D>();
        spriteRenderer = GetComponent<SpriteRenderer>();
        spriteAnimator = GetComponent<SpriteAnimator>();
        defaultGravity = rb.gravityScale;
        defaultInterpolation = rb.interpolation;
        health = maxHealth;

        wallFilter = new ContactFilter2D();
        wallFilter.useTriggers = false;
        wallFilter.SetLayerMask(groundLayer);

        AudioSource[] sources = GetComponents<AudioSource>();
        audioSource = sources.Length > 0 ? sources[0] : gameObject.AddComponent<AudioSource>();
        swordSource = sources.Length > 1 ? sources[1] : gameObject.AddComponent<AudioSource>();
        ConfigureSfxSource(audioSource);
        ConfigureSfxSource(swordSource);

        CacheSwordStartOffsets();

        if (autoAddSfx && GetComponent<PlayerSfx>() == null)
        {
            gameObject.AddComponent<PlayerSfx>();
        }
        if (autoAddTelemetry && GetComponent<PlayerTelemetry>() == null)
        {
            gameObject.AddComponent<PlayerTelemetry>();
        }
    }

    private static void ConfigureSfxSource(AudioSource source)
    {
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;
    }

    // Some sword clips begin with silence. Find the first audible sample so the sound lines up with the button press.
    private void CacheSwordStartOffsets()
    {
        int count = swordSwingSounds != null ? swordSwingSounds.Length : 0;
        swordStartOffsets = new float[count];

        for (int i = 0; i < count; i++)
        {
            AudioClip clip = swordSwingSounds[i];
            if (clip == null || clip.loadType == AudioClipLoadType.Streaming)
            {
                continue;
            }

            float[] data = new float[clip.samples * clip.channels];
            if (!clip.GetData(data, 0))
            {
                continue;
            }

            for (int s = 0; s < data.Length; s++)
            {
                if (Mathf.Abs(data[s]) > 0.01f)
                {
                    float seconds = (s / clip.channels) / (float)clip.frequency;
                    swordStartOffsets[i] = Mathf.Max(0f, seconds - 0.02f);
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ update

    private void Update()
    {
        float move = ReadInput();

        if (InputPressed != null)
        {
            if (pJump) InputPressed("jump");
            if (pSlash) InputPressed("slash");
            if (pOverhead) InputPressed("overhead");
            if (pDash) InputPressed("dash");
            if (pTeleport) InputPressed("teleport");
        }

        StepFrame(Time.deltaTime, move);
    }

    // One frame of game logic. Kept separate from Update so it can be driven with a fake clock and scripted input.
    private void StepFrame(float dt, float move)
    {
        clock += dt;

        if (landedFlag)
        {
            landedFlag = false;
            Raise(PlayerAction.Land, landImpactSpeed);
            if (state == State.Normal && landImpactSpeed > landMinSpeed)
            {
                StartLanding();
            }
        }

        switch (state)
        {
            case State.Dead:
                moveInput = 0f;
                deathTimer += dt;
                if (!restarting && deathTimer >= respawnDelay)
                {
                    Restart();
                }
                break;

            case State.Damage:
                moveInput = 0f;
                actionElapsed += dt;
                ProcessAction(move, damage);
                break;

            case State.Dash:
                moveInput = 0f;
                actionElapsed += dt;
                ProcessAction(move, dash);
                break;

            case State.Teleport:
                moveInput = 0f;
                actionElapsed += dt;
                if (!teleportMoved && actionElapsed >= teleport.TimeOfFrame(teleport.keyFrame))
                {
                    teleportMoved = true;
                    DoTeleportMove();
                }
                ProcessAction(move, teleport);
                break;

            case State.Slash:
            case State.Overhead:
                UpdateAttack(dt, move);
                break;

            default:
                UpdateNormal(move);
                break;
        }

        UpdateFacing();
        UpdateAnimation(dt);
    }

    // Reads the keyboard, mouse and gamepad. Returns the horizontal move input and sets this frame's button presses.
    private float ReadInput()
    {
        float move = 0f;
        pJump = pSlash = pOverhead = pDash = pTeleport = pHeal = false;

        // Keyboard: A/D and Left/Right Arrow move, Space jumps, Shift dashes, E teleports.
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
            {
                move -= 1f;
            }
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
            {
                move += 1f;
            }
            pJump |= keyboard.spaceKey.wasPressedThisFrame;
            pDash |= keyboard.leftShiftKey.wasPressedThisFrame || keyboard.rightShiftKey.wasPressedThisFrame;
            pTeleport |= keyboard.eKey.wasPressedThisFrame;
            pHeal |= keyboard.rKey.wasPressedThisFrame;

            if (enableDebugKeys)
            {
                if (keyboard.hKey.wasPressedThisFrame)
                {
                    TakeDamage(1);
                }
                if (keyboard.kKey.wasPressedThisFrame)
                {
                    Die();
                }
                if (keyboard.cKey.wasPressedThisFrame && spriteAnimator != null)
                {
                    spriteAnimator.CycleSkin(); // swap between the characters
                }
            }
        }

        // Mouse: left click = sword slash, right click = overhead attack.
        var mouse = Mouse.current;
        if (mouse != null)
        {
            pSlash |= mouse.leftButton.wasPressedThisFrame;
            pOverhead |= mouse.rightButton.wasPressedThisFrame;
        }

        // Controller: left stick moves, South jumps, West = slash, North = overhead, East = dash, Right shoulder = teleport.
        var gamepad = Gamepad.current;
        if (gamepad != null)
        {
            float stickX = gamepad.leftStick.x.value;
            if (Mathf.Abs(stickX) > stickDeadzone)
            {
                move = Mathf.Clamp(move + stickX, -1f, 1f);
            }
            pJump |= gamepad.buttonSouth.wasPressedThisFrame;
            pSlash |= gamepad.buttonWest.wasPressedThisFrame;
            pOverhead |= gamepad.buttonNorth.wasPressedThisFrame;
            pDash |= gamepad.buttonEast.wasPressedThisFrame;
            pTeleport |= gamepad.rightShoulder.wasPressedThisFrame;
            pHeal |= gamepad.leftShoulder.wasPressedThisFrame;
        }

        return move;
    }

    private void UpdateNormal(float move)
    {
        moveInput = Mathf.Clamp(move, -1f, 1f);

        if (pHeal)
        {
            TryHeal();
        }

        // A press made a moment ago (during the previous action) counts as pressed now.
        bool slashNow = pSlash, overheadNow = pOverhead, dashNow = pDash, teleportNow = pTeleport;
        if (buffered != Buffered.None)
        {
            if (clock <= bufferedUntil)
            {
                slashNow |= buffered == Buffered.Slash;
                overheadNow |= buffered == Buffered.Overhead;
                dashNow |= buffered == Buffered.Dash;
                teleportNow |= buffered == Buffered.Teleport;
            }
            buffered = Buffered.None;
        }

        // Jump is buffered too, so pressing it just before landing still jumps.
        if (pJump)
        {
            jumpBuffered = true;
            jumpBufferedUntil = clock + inputBufferTime;
        }
        if (jumpBuffered && clock > jumpBufferedUntil)
        {
            jumpBuffered = false;
        }

        if (teleportNow && clock >= teleportReadyTime)
        {
            StartTeleport();
        }
        else if (dashNow && clock >= dashReadyTime && isGrounded)
        {
            // GROUND CHECK: the dash (slide) can only start while standing on the ground
            StartDash(move);
        }
        else if (overheadNow)
        {
            StartAttack(State.Overhead, move);
        }
        else if (slashNow)
        {
            StartAttack(State.Slash, move);
        }
        else if (jumpBuffered && isGrounded)
        {
            jumpRequested = true;
            jumpBuffered = false;
            landTimer = 0f;
        }
    }

    // ----------------------------------------------------------- action timeline

    // Shared by every timed action: remembers early presses and lets the action flow into the next one.
    private void ProcessAction(float move, ActionClip clip)
    {
        if (pSlash) Buffer(Buffered.Slash);
        if (pOverhead) Buffer(Buffered.Overhead);
        if (pDash) Buffer(Buffered.Dash);
        if (pTeleport) Buffer(Buffered.Teleport);
        if (pJump)
        {
            jumpBuffered = true;
            jumpBufferedUntil = clock + inputBufferTime;
        }

        if (actionElapsed >= clip.TimeOfFrame(clip.cancelFrame))
        {
            if (TryStartBuffered(move))
            {
                return;
            }

            // Past the cancel frame, holding a direction (or a pending jump) ends the action so the recovery flows into running.
            if (Mathf.Abs(move) > 0.1f || (jumpBuffered && isGrounded))
            {
                ExitAction();
                return;
            }
        }

        if (actionElapsed >= clip.duration)
        {
            ExitAction();
        }
    }

    private void Buffer(Buffered action)
    {
        buffered = action;
        bufferedUntil = clock + inputBufferTime;
    }

    private bool TryStartBuffered(float move)
    {
        if (buffered == Buffered.None || clock > bufferedUntil)
        {
            buffered = Buffered.None;
            return false;
        }

        Buffered pending = buffered;
        switch (pending)
        {
            case Buffered.Slash:
                buffered = Buffered.None;
                ExitAction();
                StartAttack(State.Slash, move);
                return true;
            case Buffered.Overhead:
                buffered = Buffered.None;
                ExitAction();
                StartAttack(State.Overhead, move);
                return true;
            case Buffered.Dash:
                if (!isGrounded)
                {
                    buffered = Buffered.None; // GROUND CHECK: a dash buffered in the air is dropped
                    return false;
                }
                if (clock < dashReadyTime)
                {
                    return false;
                }
                buffered = Buffered.None;
                ExitAction();
                StartDash(move);
                return true;
            case Buffered.Teleport:
                if (clock < teleportReadyTime)
                {
                    return false;
                }
                buffered = Buffered.None;
                ExitAction();
                StartTeleport();
                return true;
        }

        return false;
    }

    // Leaves the current action, putting gravity / interpolation back if it had changed them.
    private void ExitAction()
    {
        if (state == State.Dash)
        {
            rb.gravityScale = defaultGravity;
        }
        else if (state == State.Teleport)
        {
            rb.gravityScale = defaultGravity;
            rb.interpolation = defaultInterpolation;
            rb.linearVelocity = Vector2.zero;
        }

        state = State.Normal;
    }

    // ----------------------------------------------------------------- attacks

    private void StartAttack(State kind, float move)
    {
        bool isOverhead = kind == State.Overhead;
        bool inAir = !isGrounded && spriteAnimator != null && spriteAnimator.FrameCount(airSlash.clip) > 0;
        ActionClip clip = inAir ? airSlash : (isOverhead ? overhead : slash);
        attackClip = clip;

        // Running one way and swinging: turn to face the way you're running, so the swing goes where you're headed.
        if (Mathf.Abs(move) > 0.1f)
        {
            SetFacing(move > 0f);
        }

        state = kind;
        actionElapsed = 0f;
        attackHitApplied = false;
        attackReach = isOverhead ? overheadRange : attackRange;
        jumpRequested = false;
        landTimer = 0f;

        if (spriteAnimator != null)
        {
            spriteAnimator.PlaySegment(clip.clip, clip.startFrame, clip.endFrame, clip.duration, clip.blendIn);
        }
        PlaySwordSwingSound();
        Raise(inAir ? PlayerAction.AirAttack : (isOverhead ? PlayerAction.Overhead : PlayerAction.Slash));
    }

    private void UpdateAttack(float dt, float move)
    {
        ActionClip clip = attackClip != null ? attackClip : (state == State.Overhead ? overhead : slash);
        actionElapsed += dt;

        if (!attackHitApplied && actionElapsed >= clip.TimeOfFrame(clip.keyFrame))
        {
            PerformAttackHit(attackReach);
            attackHitApplied = true;
        }

        // You keep running through the swing, just slower.
        moveInput = Mathf.Clamp(move, -1f, 1f) * (state == State.Overhead ? overheadMoveMultiplier : slashMoveMultiplier);

        ProcessAction(move, clip);
    }

    // ------------------------------------------------------------------- dash

    private void StartDash(float move)
    {
        dashDirection = Mathf.Abs(move) > 0.1f ? Mathf.Sign(move) : (facingRight ? 1f : -1f);
        SetFacing(dashDirection > 0f);

        state = State.Dash;
        actionElapsed = 0f;
        dashReadyTime = clock + dashCooldown;
        dashAirTime = 0f;
        jumpRequested = false;
        landTimer = 0f;
        rb.gravityScale = 0f;

        if (spriteAnimator != null)
        {
            spriteAnimator.PlaySegment(dash.clip, dash.startFrame, dash.endFrame, dash.duration, dash.blendIn);
        }
        Raise(PlayerAction.Dash);
    }

    // Fast at the start, easing off through the burst frames, then sliding to a stop in the recovery.
    private float DashVelocity()
    {
        float burstEnd = Mathf.Max(0.02f, dash.TimeOfFrame(dashMoveEndFrame));
        float profile;
        if (actionElapsed < burstEnd)
        {
            float u = actionElapsed / burstEnd;
            profile = 1f - 0.75f * u * u;
        }
        else
        {
            float u = Mathf.Clamp01((actionElapsed - burstEnd) / Mathf.Max(0.02f, dash.duration - burstEnd));
            profile = 0.25f * (1f - u);
        }

        return dashDirection * dashSpeed * profile;
    }

    // --------------------------------------------------------------- teleport

    private void StartTeleport()
    {
        state = State.Teleport;
        actionElapsed = 0f;
        teleportMoved = false;
        teleportReadyTime = clock + teleportCooldown;
        invulnerableUntil = Mathf.Max(invulnerableUntil, clock + teleport.duration);
        jumpRequested = false;
        landTimer = 0f;

        rb.gravityScale = 0f;
        rb.linearVelocity = Vector2.zero;
        rb.interpolation = RigidbodyInterpolation2D.None; // so the jump in position isn't smoothed into a slide

        if (spriteAnimator != null)
        {
            spriteAnimator.PlaySegment(teleport.clip, teleport.startFrame, teleport.endFrame, teleport.duration, teleport.blendIn);
        }
        Raise(PlayerAction.Teleport);
    }

    // Moves the player forward, stopping just short of any wall in the way.
    private void DoTeleportMove()
    {
        float direction = facingRight ? 1f : -1f;
        float distance = teleportDistance;
        if (TryGetWallDistance(direction, teleportDistance, out float wallDistance))
        {
            distance = Mathf.Max(0f, wallDistance - 0.02f);
        }

        Vector2 target = rb.position + new Vector2(direction * distance, 0f);
        rb.position = target;
        transform.position = new Vector3(target.x, target.y, transform.position.z);
        Physics2D.SyncTransforms();
    }

    // ------------------------------------------------------- damage and death

    // Call this from hazards / enemies. knockbackDirection: -1 = push left, 1 = push right, 0 = push away from the way we face.
    public void TakeDamage(int amount, float knockbackDirection = 0f)
    {
        if (state == State.Dead || clock < invulnerableUntil || amount <= 0)
        {
            return;
        }

        ExitAction();
        health = Mathf.Max(0, health - amount);
        if (health <= 0)
        {
            Die();
            return;
        }

        state = State.Damage;
        actionElapsed = 0f;
        invulnerableUntil = clock + invulnerableTime;
        jumpRequested = false;
        landTimer = 0f;
        buffered = Buffered.None;

        float direction = Mathf.Abs(knockbackDirection) > 0.01f ? Mathf.Sign(knockbackDirection) : (facingRight ? -1f : 1f);
        rb.linearVelocity = new Vector2(direction * knockback.x, knockback.y);

        if (spriteAnimator != null)
        {
            spriteAnimator.PlaySegment(damage.clip, damage.startFrame, damage.endFrame, damage.duration, damage.blendIn);
        }
        Raise(PlayerAction.Damage, amount);
    }

    public void Die()
    {
        if (state == State.Dead)
        {
            return;
        }

        ExitAction();
        health = 0;
        state = State.Dead;
        deathTimer = 0f;
        jumpRequested = false;
        landTimer = 0f;
        buffered = Buffered.None;

        if (spriteAnimator != null)
        {
            spriteAnimator.PlaySegment(death.clip, death.startFrame, death.endFrame, death.duration, death.blendIn);
        }
        Raise(PlayerAction.Death);
    }

    private void Restart()
    {
        restarting = true;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    // ------------------------------------------------------------ fixed update

    private void FixedUpdate()
    {
        // Ground check via a small box cast just below the collider.
        bool nowGrounded = CheckGrounded();
        if (nowGrounded && !wasGrounded)
        {
            landedFlag = true;
            landImpactSpeed = -lastVelocityY; // speed just before touching down
        }
        wasGrounded = nowGrounded;
        isGrounded = nowGrounded;

        switch (state)
        {
            case State.Dash:
                {
                    // GROUND CHECK every physics step: if the ground disappears (ledge, gap) the dash is cancelled,
                    // gravity comes back and the sideways speed is clamped - the player never slides through the air.
                    dashAirTime = isGrounded ? 0f : dashAirTime + Time.fixedDeltaTime;
                    if (dashAirTime > dashGroundGrace)
                    {
                        dashAirTime = 0f;
                        ExitAction();
                        rb.linearVelocity = new Vector2(Mathf.Clamp(rb.linearVelocity.x, -moveSpeed, moveSpeed), rb.linearVelocity.y);
                        break;
                    }

                    float velocityX = DashVelocity();
                    if (IsPressingAgainstWall(dashDirection))
                    {
                        velocityX = 0f; // stop at a wall instead of grinding against it
                    }
                    rb.linearVelocity = new Vector2(velocityX, 0f);
                    break;
                }

            case State.Teleport:
                rb.linearVelocity = Vector2.zero;
                break;

            case State.Damage:
                break; // let the knockback play out

            case State.Dead:
                rb.linearVelocity = new Vector2(Mathf.MoveTowards(rb.linearVelocity.x, 0f, deceleration * Time.fixedDeltaTime), rb.linearVelocity.y);
                break;

            default:
                {
                    // Ease towards the wanted speed instead of snapping to it. This is also what lets a dash or knockback hand over
                    // smoothly into running.
                    float target = moveInput * moveSpeed;
                    float velocityX = rb.linearVelocity.x;
                    float rate = Mathf.Abs(target) > 0.01f ? acceleration : deceleration;
                    velocityX = Mathf.MoveTowards(velocityX, target, rate * Time.fixedDeltaTime);

                    // No sliding in the air: with no direction held, any sideways drift (from a run, dash or knockback) stops at once.
                    if (!isGrounded && Mathf.Abs(moveInput) <= 0.01f)
                    {
                        velocityX = 0f;
                    }

                    // In the air, don't drive into a wall: the wall's friction would otherwise hold the player in place while the key is held.
                    if (!isGrounded && Mathf.Abs(moveInput) > 0.01f && IsPressingAgainstWall(Mathf.Sign(moveInput)))
                    {
                        velocityX = 0f;
                    }
                    rb.linearVelocity = new Vector2(velocityX, rb.linearVelocity.y);

                    if (jumpRequested && state == State.Normal)
                    {
                        rb.linearVelocity = new Vector2(rb.linearVelocity.x, jumpForce);
                        Raise(PlayerAction.Jump);
                    }
                    break;
                }
        }

        jumpRequested = false;
        lastVelocityY = rb.linearVelocity.y;
    }

    // ------------------------------------------------------- facing / animation

    private void UpdateFacing()
    {
        if (state != State.Normal)
        {
            return; // never flip mid-swing, mid-dash, etc.
        }

        if (moveInput > 0.01f && !facingRight)
        {
            SetFacing(true);
        }
        else if (moveInput < -0.01f && facingRight)
        {
            SetFacing(false);
        }
    }

    private void SetFacing(bool right)
    {
        facingRight = right;
        spriteRenderer.flipX = !right; // the art faces right
    }

    private void StartLanding()
    {
        // The landing uses the last four frames of a full 16-frame jump clip; a character with only a jump pose just skips it.
        if (spriteAnimator != null && spriteAnimator.FrameCount("jump") < 16)
        {
            return;
        }

        landTimer = landDuration;
        if (spriteAnimator != null)
        {
            // Last four frames of the jump clip: the knight absorbs the landing.
            spriteAnimator.PlaySegment("jump", 12, 15, landDuration, airBlend);
        }
    }

    // Normal state only: the action states start their own clip when they begin.
    private void UpdateAnimation(float dt)
    {
        if (spriteAnimator == null || state != State.Normal)
        {
            return;
        }

        if (!isGrounded)
        {
            // The pose follows the real jump: rising frames, apex, then falling frames.
            landTimer = 0f;
            spriteAnimator.HoldFrame("jump", AirFrame(rb.linearVelocity.y), airBlend);
            return;
        }

        if (landTimer > 0f)
        {
            landTimer -= dt;
            bool moving = Mathf.Abs(moveInput) > 0.1f && landTimer < landDuration - landCancelAfter;
            if (landTimer > 0f && !moving)
            {
                return; // let the landing finish
            }
            landTimer = 0f;
        }

        if (Mathf.Abs(moveInput) > 0.01f)
        {
            spriteAnimator.PlayLoop("walk", Mathf.Max(0.6f, Mathf.Abs(moveInput)), locomotionBlend);
        }
        else
        {
            spriteAnimator.PlayLoop("idle", 1f, locomotionBlend);
        }
    }

    // Frames of the jump clip: 3-7 rising, 8 apex, 9-11 falling (cape streaming up).
    private int AirFrame(float verticalSpeed)
    {
        if (verticalSpeed > 1.5f)
        {
            float progress = 1f - Mathf.Clamp01(verticalSpeed / Mathf.Max(0.1f, jumpForce));
            return 3 + Mathf.RoundToInt(progress * 4f);
        }

        if (verticalSpeed > -1.5f)
        {
            return 8;
        }

        float fall = Mathf.Clamp01((-verticalSpeed - 1.5f) / 10f);
        return 9 + Mathf.RoundToInt(fall * 2f);
    }

    // ------------------------------------------------------------------ sounds

    // Plays the next sword clip. Chained attacks alternate between clips; after a pause the sequence restarts.
    private void PlaySwordSwingSound()
    {
        if (swordSource == null || swordSwingSounds == null || swordSwingSounds.Length == 0)
        {
            return;
        }

        if (clock - lastSwordSoundTime > swordComboWindow)
        {
            nextSwordSoundIndex = 0;
        }

        int index = nextSwordSoundIndex % swordSwingSounds.Length;
        nextSwordSoundIndex = (index + 1) % swordSwingSounds.Length;
        lastSwordSoundTime = clock;

        AudioClip clip = swordSwingSounds[index];
        if (clip == null)
        {
            return;
        }

        swordSource.Stop();
        swordSource.clip = clip;
        swordSource.volume = swordSwingVolume;
        swordSource.time = (swordStartOffsets != null && index < swordStartOffsets.Length)
            ? Mathf.Min(swordStartOffsets[index], clip.length * 0.9f)
            : 0f;
        swordSource.Play();
    }

    // Destroys any GameObject tagged "Decoration" within the swing's reach in front of the player.
    // Plays at most one sound per swing, however many decorations are hit.
    private void PerformAttackHit(float reach)
    {
        Vector2 direction = facingRight ? Vector2.right : Vector2.left;
        Vector2 origin = (Vector2)transform.position + Vector2.up * attackHeight + direction * (reach * 0.5f);
        bool plantDestroyed = false;
        bool treeDestroyed = false;
        bool hitBoss = false;
        bool hitDecoration = false;

        // Damage Boss / Enemies in reach
        Collider2D[] enemyHits = Physics2D.OverlapCircleAll(origin, reach);
        foreach (Collider2D hit in enemyHits)
        {
            if (hit == null || hit.gameObject == gameObject) continue;
            BossController boss = hit.GetComponentInParent<BossController>();
            if (boss != null)
            {
                int dmg = (state == State.Overhead) ? 2 : 1;
                boss.TakeDamage(dmg, direction.x);
                hitBoss = true;
                AddPower(state == State.Overhead ? powerPerOverheadHit : powerPerHit);
            }
        }

        GameObject[] decorations = GameObject.FindGameObjectsWithTag("Decoration");
        foreach (GameObject deco in decorations)
        {
            if (deco == null)
            {
                continue;
            }

            if (Vector2.Distance(origin, deco.transform.position) <= reach)
            {
                if (MatchesAnyKeyword(deco.name, plantNameKeywords))
                {
                    plantDestroyed = true;
                }
                else if (MatchesAnyKeyword(deco.name, treeNameKeywords))
                {
                    treeDestroyed = true;
                }

                Destroy(deco);
                hitDecoration = true;
            }
        }

        PlayImpactEffects(origin, direction, reach, hitBoss, hitDecoration);

        if (hitBoss)
        {
            Raise(PlayerAction.HitBoss, state == State.Overhead ? 2f : 1f);
        }
        else if (hitDecoration)
        {
            Raise(PlayerAction.HitDecoration);
        }

        // One sound per swing. Plants take priority if a bush and a tree are hit together.
        if (plantDestroyed)
        {
            PlayRandomOneShot(plantDestroySounds);
        }
        else if (treeDestroyed)
        {
            PlayRandomOneShot(treeDestroySounds);
        }
    }

    // Heavy-weapon impact feel. Every swing shakes the screen a little and throws sparks / dust where the flame lands;
    // connecting with the boss adds a hit-stop, a bigger shake and a second burst of sparks. The overhead hits harder.
    private void PlayImpactEffects(Vector2 origin, Vector2 direction, float reach, bool hitBoss, bool hitDecoration)
    {
        if (!impactEffects)
        {
            return;
        }

        float boost = state == State.Overhead ? overheadImpactBoost : 1f;
        // On the ground the censer crashes into the floor just in front of the feet; in the air it meets the target at chest height.
        Vector2 contact = isGrounded
            ? (Vector2)transform.position + Vector2.up * 0.3f + direction * (reach * 0.65f)
            : (Vector2)transform.position + Vector2.up * attackHeight + direction * (reach * 0.85f);

        CameraFollow.Shake(swingShake * boost, swingShakeTime);
        HeavyAttackFx.Embers(contact, direction, boost);

        if (isGrounded)
        {
            HeavyAttackFx.Dust((Vector2)transform.position + direction * (reach * 0.5f) + Vector2.up * 0.05f, direction, boost);
        }

        if (hitBoss)
        {
            CameraFollow.Shake(hitShake * boost, hitShakeTime);
            HeavyAttackFx.Embers(contact, -direction, 1.3f * boost);   // sparks fly back off the boss
            HeavyAttackFx.HitStop(hitStopTime * boost);
        }
        else if (hitDecoration)
        {
            HeavyAttackFx.Embers(contact, direction, 0.6f);
        }
    }

    public void AddPower(float amount)
    {
        power = Mathf.Clamp(power + amount, 0f, maxPower);
    }

    // Spends one heal's worth of power to restore health. Only on solid ground, only when hurt, never when dead.
    public bool TryHeal()
    {
        if (state == State.Dead || !isGrounded || clock < healReadyTime || health >= maxHealth || power + 0.001f < healCost)
        {
            return false;
        }

        power -= healCost;
        health = Mathf.Min(maxHealth, health + healAmount);
        healReadyTime = clock + healCooldown;
        HeavyAttackFx.HealGlow(transform.position + Vector3.up * 1.0f);
        return true;
    }

    private static bool MatchesAnyKeyword(string objectName, string[] keywords)
    {
        if (keywords == null)
        {
            return false;
        }

        foreach (string keyword in keywords)
        {
            if (!string.IsNullOrEmpty(keyword) &&
                objectName.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // Plays on the Player's own AudioSource (not on the decoration, which is being destroyed).
    private void PlayRandomOneShot(AudioClip[] clips)
    {
        if (audioSource == null || clips == null || clips.Length == 0)
        {
            return;
        }

        // Pick one random clip, skipping any empty slots.
        int validCount = 0;
        foreach (AudioClip clip in clips)
        {
            if (clip != null)
            {
                validCount++;
            }
        }
        if (validCount == 0)
        {
            return;
        }

        int pick = UnityEngine.Random.Range(0, validCount);
        foreach (AudioClip clip in clips)
        {
            if (clip == null)
            {
                continue;
            }
            if (pick == 0)
            {
                audioSource.PlayOneShot(clip, foliageDestroyVolume);
                return;
            }
            pick--;
        }
    }

    // ----------------------------------------------------------------- physics

    // True if a solid, non-one-way collider is right beside the player in the given direction (-1 = left, 1 = right).
    private bool IsPressingAgainstWall(float direction)
    {
        return TryGetWallDistance(direction, wallCheckDistance, out _);
    }

    // Distance to the nearest solid wall within maxDistance in the given direction. Floors, ceilings and one-way platforms don't count.
    private bool TryGetWallDistance(float direction, float maxDistance, out float distance)
    {
        distance = maxDistance;
        bool found = false;

        int count = boxCollider.Cast(new Vector2(direction, 0f), wallFilter, wallHits, maxDistance);
        for (int i = 0; i < count; i++)
        {
            if (Mathf.Abs(wallHits[i].normal.x) < 0.5f)
            {
                continue; // floor or ceiling contact, not a wall
            }

            Collider2D other = wallHits[i].collider;
            if (other != null && other.usedByEffector)
            {
                PlatformEffector2D effector = other.GetComponent<PlatformEffector2D>();
                if (effector != null && effector.useOneWay)
                {
                    continue; // one-way platforms don't block sideways movement
                }
            }

            if (!found || wallHits[i].distance < distance)
            {
                distance = wallHits[i].distance;
            }
            found = true;
        }

        return found;
    }

    private bool CheckGrounded()
    {
        Bounds bounds = boxCollider.bounds;
        Vector2 origin = new Vector2(bounds.center.x, bounds.min.y);
        Vector2 size = new Vector2(bounds.size.x * 0.9f, 0.05f);

        RaycastHit2D hit = Physics2D.BoxCast(
            origin,
            size,
            0f,
            Vector2.down,
            groundCheckDistance,
            groundLayer);

        return hit.collider != null;
    }

    private void OnDrawGizmosSelected()
    {
        // Visualize the attack reaches in the editor for tuning (red = slash, orange = overhead).
        Vector2 direction = facingRight ? Vector2.right : Vector2.left;
        Vector2 basePoint = (Vector2)transform.position + Vector2.up * attackHeight;
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(basePoint + direction * (attackRange * 0.5f), attackRange);
        Gizmos.color = new Color(1f, 0.55f, 0f);
        Gizmos.DrawWireSphere(basePoint + direction * (overheadRange * 0.5f), overheadRange);
    }
}
