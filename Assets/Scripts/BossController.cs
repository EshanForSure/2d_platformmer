using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(BoxCollider2D))]
[RequireComponent(typeof(SpriteRenderer))]
public class BossController : MonoBehaviour
{
    public enum State
    {
        Idle,
        Chase,
        Attack,
        Dead
    }

    [Header("Boss Identity")]
    [SerializeField] private string bossName = "Boss";
    [SerializeField] private string bossTitle = "Menacing Foe";

    [Header("Stats")]
    [SerializeField] private int maxHealth = 15;
    [SerializeField] private int currentHealth = 15;

    [Header("Movement & AI")]
    [SerializeField] private float moveSpeed = 3.6f;
    [SerializeField] private float chaseRange = 25f;
    [SerializeField] private float attackRange = 2.1f;
    [SerializeField] private float stopDistance = 1.2f;
    [SerializeField] private float jumpForce = 8.5f;
    [SerializeField] private float jumpCooldown = 1.6f;
    [SerializeField] private float groundCheckDistance = 0.15f;
    [SerializeField] private LayerMask groundLayer = ~0;

    [Header("Attack Settings")]
    [SerializeField] private int attackDamage = 1;
    [SerializeField] private float attackReach = 2.4f;
    [SerializeField] private float attackHeight = 0.9f;
    [SerializeField] private float attackCooldown = 1.3f;
    [SerializeField] private float attackFrameTime = 0.085f;
    [SerializeField] private int strikeKeyFrame = 4;
    [SerializeField] private Vector2 lungeSpeed = new Vector2(2.0f, 0f);
    [SerializeField] private bool isRangedAttacker = false;
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private float projectileSpeed = 7f;

    [Header("Audio")]
    [SerializeField] private AudioClip swordSwingSound;
    [SerializeField] private AudioClip swordHitSound;
    [SerializeField] private AudioClip hurtSound;
    [SerializeField] private AudioClip defeatSound;
    [SerializeField, Range(0f, 1f)] private float soundVolume = 0.85f;

    [Header("Animations")]
    [SerializeField] private Sprite[] idleFrames;
    [SerializeField] private Sprite[] walkFrames;
    [SerializeField] private Sprite[] attackFrames;
    [SerializeField] private Sprite[] deathFrames;
    [SerializeField] private float idleFrameTime = 0.12f;
    [SerializeField] private float walkFrameTime = 0.09f;

    [Header("Visual Effects")]
    [SerializeField] private Color damageFlashColor = new Color(1f, 0.35f, 0.35f, 1f);
    [SerializeField] private float flashDuration = 0.14f;
    [SerializeField] private Light2D bossLight;

    [Header("Custom UI Bar (Optional)")]
    [SerializeField] private Texture2D customBarUnder;
    [SerializeField] private Texture2D customBarProgress;
    [SerializeField] private Texture2D customBarOver;

    private Rigidbody2D rb;
    private BoxCollider2D boxCollider;
    private SpriteRenderer spriteRenderer;
    private AudioSource audioSource;
    private Transform playerTransform;
    private PlayerController playerController;
    private Collider2D playerCollider;

    private State currentState = State.Idle;
    private bool facingRight = false; // default sprite faces south-east / right
    private float nextAttackTime;
    private float nextJumpTime;
    private float stuckTimer;
    private bool isInvulnerable;

    // Animation state
    private float animTimer;
    private int currentFrameIndex;
    private bool attackHitApplied;

    // UI state
    private bool bossActive;
    private float displayHealth;
    private float defeatTimer;
    private bool defeated;

    // Static registry of active bosses to neatly stack or prioritize UI
    private static readonly List<BossController> activeBosses = new List<BossController>();

    // GUI styles & default textures
    private static Texture2D defaultBarBgTex;
    private static Texture2D defaultBarFillTex;
    private static Texture2D defaultBarLagTex;
    private static Texture2D defaultBorderTex;
    private static GUIStyle titleStyle;
    private static GUIStyle subtitleStyle;
    private static GUIStyle hpTextStyle;
    private static GUIStyle victoryStyle;

    public int MaxHealth => maxHealth;
    public int CurrentHealth => currentHealth;
    public State CurrentState => currentState;
    public bool IsDead => currentState == State.Dead;
    public string BossName => bossName;
    public string BossTitle => bossTitle;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        boxCollider = GetComponent<BoxCollider2D>();
        spriteRenderer = GetComponent<SpriteRenderer>();

        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0.4f;

        currentHealth = maxHealth;
        displayHealth = maxHealth;
    }

    private void OnEnable()
    {
        if (!activeBosses.Contains(this))
        {
            activeBosses.Add(this);
        }
    }

    private void OnDisable()
    {
        activeBosses.Remove(this);
    }

    private void Start()
    {
        FindPlayer();
    }

    private void FindPlayer()
    {
        if (playerTransform != null) return;

        playerController = Object.FindAnyObjectByType<PlayerController>();
        if (playerController != null)
        {
            playerTransform = playerController.transform;
            playerCollider = playerController.GetComponent<Collider2D>();

            if (playerCollider != null && boxCollider != null)
            {
                // Prevent physics snagging so player and boss fight smoothly
                Physics2D.IgnoreCollision(playerCollider, boxCollider, true);
            }
        }
    }

    private void Update()
    {
        if (playerTransform == null)
        {
            FindPlayer();
        }

        // Smooth trailing health for UI
        if (displayHealth > currentHealth)
        {
            displayHealth = Mathf.MoveTowards(displayHealth, currentHealth, Time.deltaTime * 6f);
        }
        else
        {
            displayHealth = currentHealth;
        }

        if (currentState == State.Dead)
        {
            defeatTimer += Time.deltaTime;
            return;
        }

        if (playerTransform == null) return;

        float distanceToPlayer = Vector2.Distance(transform.position, playerTransform.position);
        float dx = playerTransform.position.x - transform.position.x;
        float dy = playerTransform.position.y - transform.position.y;

        // Activate boss when player is in chase range
        if (!bossActive && distanceToPlayer <= chaseRange)
        {
            bossActive = true;
        }

        switch (currentState)
        {
            case State.Idle:
                UpdateIdle(distanceToPlayer);
                break;

            case State.Chase:
                UpdateChase(distanceToPlayer, dx, dy);
                break;

            case State.Attack:
                UpdateAttack();
                break;
        }

        UpdateAnimation();
    }

    private void UpdateIdle(float distanceToPlayer)
    {
        if (distanceToPlayer <= chaseRange)
        {
            currentState = State.Chase;
        }
    }

    private void UpdateChase(float distanceToPlayer, float dx, float dy)
    {
        if (playerController != null && playerController.IsDead)
        {
            currentState = State.Idle;
            rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
            return;
        }

        // Update facing direction towards player
        if (Mathf.Abs(dx) > 0.1f)
        {
            SetFacing(dx > 0f);
        }

        float absDx = Mathf.Abs(dx);

        // Check if within attack range
        if (absDx <= attackRange && Mathf.Abs(dy) <= 2.2f)
        {
            if (Time.time >= nextAttackTime)
            {
                StartAttack();
                return;
            }
            else
            {
                // Wait for attack cooldown, slow down or hold ground
                rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
                stuckTimer = 0f;
                return;
            }
        }

        // Move towards player
        float moveDir = Mathf.Sign(dx);
        rb.linearVelocity = new Vector2(moveDir * moveSpeed, rb.linearVelocity.y);

        // Check if boss is blocked by an obstacle or platform
        bool grounded = IsGrounded();
        if (grounded)
        {
            if (Mathf.Abs(rb.linearVelocity.x) < 0.4f && absDx > stopDistance)
            {
                stuckTimer += Time.deltaTime;
            }
            else
            {
                stuckTimer = 0f;
            }

            bool playerHigher = dy > 1.1f && absDx < 6f;
            bool obstacleAhead = HasObstacleAhead(moveDir);
            bool isStuck = stuckTimer > 0.18f;

            if ((playerHigher || obstacleAhead || isStuck) && Time.time >= nextJumpTime)
            {
                rb.linearVelocity = new Vector2(moveDir * moveSpeed, jumpForce);
                nextJumpTime = Time.time + jumpCooldown;
                stuckTimer = 0f;
            }
        }
    }

    private void StartAttack()
    {
        currentState = State.Attack;
        animTimer = 0f;
        currentFrameIndex = 0;
        attackHitApplied = false;

        // Forward strike lunge (only if melee)
        if (!isRangedAttacker)
        {
            float dir = facingRight ? 1f : -1f;
            rb.linearVelocity = new Vector2(dir * lungeSpeed.x, rb.linearVelocity.y);
        }
        else
        {
            rb.linearVelocity = new Vector2(0f, rb.linearVelocity.y);
        }

        if (swordSwingSound != null)
        {
            PlayOneShot(swordSwingSound);
        }
    }

    private void UpdateAttack()
    {
        animTimer += Time.deltaTime;
        int frame = Mathf.FloorToInt(animTimer / attackFrameTime);

        if (attackFrames != null && attackFrames.Length > 0)
        {
            if (frame < attackFrames.Length)
            {
                currentFrameIndex = frame;

                // Deal strike damage or fire projectile at key frame
                if (!attackHitApplied && currentFrameIndex >= strikeKeyFrame)
                {
                    if (isRangedAttacker)
                    {
                        PerformRangedAttack();
                    }
                    else
                    {
                        PerformMeleeStrike();
                    }
                    attackHitApplied = true;
                }
            }
            else
            {
                // Attack finished
                currentState = State.Chase;
                nextAttackTime = Time.time + attackCooldown;
                currentFrameIndex = 0;
                animTimer = 0f;
            }
        }
        else
        {
            currentState = State.Chase;
        }
    }

    private void PerformMeleeStrike()
    {
        Vector2 dir = facingRight ? Vector2.right : Vector2.left;
        Vector2 origin = (Vector2)transform.position + Vector2.up * attackHeight + dir * (attackReach * 0.5f);

        // Hit player
        if (playerTransform != null && playerController != null && !playerController.IsDead)
        {
            float dist = Vector2.Distance(origin, (Vector2)playerTransform.position + Vector2.up * 0.75f);
            if (dist <= attackReach)
            {
                playerController.TakeDamage(attackDamage, dir.x);
                if (swordHitSound != null)
                {
                    PlayOneShot(swordHitSound);
                }
            }
        }

        // Environmental destruction: cut down decorations in path of strike!
        GameObject[] decorations = GameObject.FindGameObjectsWithTag("Decoration");
        foreach (var deco in decorations)
        {
            if (deco != null && Vector2.Distance(origin, deco.transform.position) <= attackReach)
            {
                Destroy(deco);
            }
        }
    }

    private void PerformRangedAttack()
    {
        Vector2 dir = facingRight ? Vector2.right : Vector2.left;
        Vector2 spawnPos = (Vector2)transform.position + Vector2.up * attackHeight + dir * 0.8f;

        if (projectilePrefab != null)
        {
            GameObject projObj = Instantiate(projectilePrefab, spawnPos, Quaternion.identity);
            MagicProjectile proj = projObj.GetComponent<MagicProjectile>();
            if (proj != null)
            {
                proj.Launch(dir, attackDamage);
            }
        }
        else
        {
            // Fallback raycast blast
            PerformMeleeStrike();
        }
    }

    private void UpdateAnimation()
    {
        if (spriteRenderer == null) return;

        if (currentState == State.Attack)
        {
            if (attackFrames != null && attackFrames.Length > 0 && currentFrameIndex < attackFrames.Length)
            {
                spriteRenderer.sprite = attackFrames[currentFrameIndex];
            }
        }
        else if (currentState == State.Chase && walkFrames != null && walkFrames.Length > 0)
        {
            // Dedicated walk cycle
            animTimer += Time.deltaTime;
            int frame = Mathf.FloorToInt(animTimer / walkFrameTime) % walkFrames.Length;
            spriteRenderer.sprite = walkFrames[frame];
        }
        else
        {
            // Idle animation
            if (idleFrames != null && idleFrames.Length > 0)
            {
                animTimer += Time.deltaTime;
                float rate = (currentState == State.Chase) ? (idleFrameTime * 0.85f) : idleFrameTime;
                int frame = Mathf.FloorToInt(animTimer / rate) % idleFrames.Length;
                spriteRenderer.sprite = idleFrames[frame];
            }
        }
    }

    private void SetFacing(bool right)
    {
        facingRight = right;
        if (spriteRenderer != null)
        {
            // Default sprites face right; flipX = true makes them face left
            spriteRenderer.flipX = !facingRight;
        }
    }

    private bool IsGrounded()
    {
        if (boxCollider == null) return true;
        Bounds bounds = boxCollider.bounds;
        Vector2 origin = new Vector2(bounds.center.x, bounds.min.y);
        Vector2 size = new Vector2(bounds.size.x * 0.9f, 0.06f);

        RaycastHit2D hit = Physics2D.BoxCast(origin, size, 0f, Vector2.down, groundCheckDistance, groundLayer);
        return hit.collider != null;
    }

    private bool HasObstacleAhead(float direction)
    {
        if (boxCollider == null) return false;
        Bounds bounds = boxCollider.bounds;
        Vector2 origin = new Vector2(bounds.center.x + direction * (bounds.extents.x + 0.1f), bounds.center.y);
        RaycastHit2D hit = Physics2D.Raycast(origin, new Vector2(direction, 0f), 0.9f, groundLayer);
        if (hit.collider != null) return true;

        Vector2 originHead = new Vector2(bounds.center.x + direction * (bounds.extents.x + 0.1f), bounds.max.y - 0.2f);
        RaycastHit2D hitHead = Physics2D.Raycast(originHead, new Vector2(direction, 0f), 0.9f, groundLayer);
        return hitHead.collider != null;
    }

    public void TakeDamage(int amount, float knockbackDir = 0f)
    {
        if (currentState == State.Dead || isInvulnerable || amount <= 0) return;

        currentHealth = Mathf.Max(0, currentHealth - amount);
        bossActive = true;

        if (hurtSound != null)
        {
            PlayOneShot(hurtSound);
        }

        StartCoroutine(FlashDamageRoutine());

        // Knockback away from attack
        float kDir = Mathf.Abs(knockbackDir) > 0.01f ? Mathf.Sign(knockbackDir) : (facingRight ? -1f : 1f);
        rb.linearVelocity = new Vector2(kDir * 2.2f, 2.0f);

        if (currentHealth <= 0)
        {
            Die();
        }
    }

    private IEnumerator FlashDamageRoutine()
    {
        isInvulnerable = true;
        Color originalColor = spriteRenderer != null ? spriteRenderer.color : Color.white;
        if (spriteRenderer != null)
        {
            spriteRenderer.color = damageFlashColor;
        }

        yield return new WaitForSeconds(flashDuration);

        if (spriteRenderer != null && currentState != State.Dead)
        {
            spriteRenderer.color = originalColor;
        }
        isInvulnerable = false;
    }

    private void Die()
    {
        currentState = State.Dead;
        defeated = true;
        defeatTimer = 0f;

        if (boxCollider != null) boxCollider.enabled = false;
        rb.linearVelocity = Vector2.zero;
        rb.gravityScale = 0f;

        if (defeatSound != null)
        {
            PlayOneShot(defeatSound);
        }

        StartCoroutine(DeathRoutine());
    }

    private IEnumerator DeathRoutine()
    {
        // If death frames are provided, play them
        if (deathFrames != null && deathFrames.Length > 0 && spriteRenderer != null)
        {
            float perFrame = 0.12f;
            for (int i = 0; i < deathFrames.Length; i++)
            {
                spriteRenderer.sprite = deathFrames[i];
                yield return new WaitForSeconds(perFrame);
            }
        }

        // Dissolve / fade out
        float duration = 2.4f;
        float elapsed = 0f;
        Color startCol = spriteRenderer != null ? spriteRenderer.color : Color.white;
        float startLight = bossLight != null ? bossLight.intensity : 1f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            if (spriteRenderer != null)
            {
                Color c = Color.Lerp(startCol, new Color(startCol.r * 0.7f, 0f, 0f, 0f), t);
                spriteRenderer.color = c;
            }

            if (bossLight != null)
            {
                bossLight.intensity = Mathf.Lerp(startLight, 0f, t);
            }

            yield return null;
        }

        if (spriteRenderer != null) spriteRenderer.enabled = false;
    }

    private void PlayOneShot(AudioClip clip)
    {
        if (clip != null && audioSource != null)
        {
            audioSource.PlayOneShot(clip, soundVolume);
        }
    }

    // ------------------------------------------------------------------ UI
    private static void InitGUIStyles()
    {
        if (defaultBarBgTex == null)
        {
            defaultBarBgTex = MakeColorTexture(new Color(0.08f, 0.08f, 0.12f, 0.9f));
            defaultBarFillTex = MakeColorTexture(new Color(0.88f, 0.15f, 0.15f, 1f));
            defaultBarLagTex = MakeColorTexture(new Color(0.95f, 0.72f, 0.2f, 1f));
            defaultBorderTex = MakeColorTexture(new Color(0.8f, 0.65f, 0.35f, 0.95f));

            titleStyle = new GUIStyle();
            titleStyle.fontSize = 20;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.alignment = TextAnchor.MiddleCenter;
            titleStyle.normal.textColor = new Color(1f, 0.88f, 0.65f);

            subtitleStyle = new GUIStyle();
            subtitleStyle.fontSize = 12;
            subtitleStyle.fontStyle = FontStyle.Italic;
            subtitleStyle.alignment = TextAnchor.MiddleCenter;
            subtitleStyle.normal.textColor = new Color(0.85f, 0.5f, 0.4f);

            hpTextStyle = new GUIStyle();
            hpTextStyle.fontSize = 13;
            hpTextStyle.fontStyle = FontStyle.Bold;
            hpTextStyle.alignment = TextAnchor.MiddleCenter;
            hpTextStyle.normal.textColor = Color.white;

            victoryStyle = new GUIStyle();
            victoryStyle.fontSize = 32;
            victoryStyle.fontStyle = FontStyle.Bold;
            victoryStyle.alignment = TextAnchor.MiddleCenter;
            victoryStyle.normal.textColor = new Color(1f, 0.85f, 0.3f);
        }
    }

    private static Texture2D MakeColorTexture(Color col)
    {
        Texture2D tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, col);
        tex.Apply();
        return tex;
    }

    private void OnGUI()
    {
        if (!bossActive && currentState != State.Dead) return;

        InitGUIStyles();

        // Calculate vertical stacking index among active bosses
        int index = 0;
        foreach (var b in activeBosses)
        {
            if (b == this) break;
            if (b != null && (b.bossActive || b.currentState == State.Dead)) index++;
        }

        float screenW = Screen.width;
        float barW = Mathf.Clamp(screenW * 0.45f, 320f, 600f);
        float barH = 20f;
        float barX = (screenW - barW) * 0.5f;
        float barY = 32f + (index * 68f);

        // If custom textures are provided (Minotaur custom healthbar)
        if (customBarOver != null && customBarProgress != null && customBarUnder != null)
        {
            float customW = 340f;
            float customH = 48f;
            float customX = (screenW - customW) * 0.5f;

            GUI.Label(new Rect(customX, barY - 26f, customW, 22f), bossName, titleStyle);
            GUI.Label(new Rect(customX, barY - 10f, customW, 16f), bossTitle, subtitleStyle);

            // Under
            GUI.DrawTexture(new Rect(customX, barY, customW, customH), customBarUnder);

            // Progress (clipped)
            float frac = Mathf.Clamp01((float)currentHealth / maxHealth);
            GUI.BeginGroup(new Rect(customX + 16f, barY, (customW - 32f) * frac, customH));
            GUI.DrawTexture(new Rect(0, 0, customW - 32f, customH), customBarProgress);
            GUI.EndGroup();

            // Over frame
            GUI.DrawTexture(new Rect(customX, barY, customW, customH), customBarOver);

            // HP text
            GUI.Label(new Rect(customX, barY + 12f, customW, 20f), $"{currentHealth} / {maxHealth}", hpTextStyle);
        }
        else
        {
            // Default ornate fantasy health bar
            GUI.Label(new Rect(barX, barY - 30f, barW, 24f), bossName, titleStyle);
            GUI.Label(new Rect(barX, barY - 12f, barW, 18f), bossTitle, subtitleStyle);

            // Border & Background
            GUI.DrawTexture(new Rect(barX - 3f, barY - 3f, barW + 6f, barH + 6f), defaultBorderTex);
            GUI.DrawTexture(new Rect(barX, barY, barW, barH), defaultBarBgTex);

            // Trailing yellow lag bar
            float lagFraction = Mathf.Clamp01(displayHealth / maxHealth);
            GUI.DrawTexture(new Rect(barX, barY, barW * lagFraction, barH), defaultBarLagTex);

            // Red health bar
            float healthFraction = Mathf.Clamp01((float)currentHealth / maxHealth);
            GUI.DrawTexture(new Rect(barX, barY, barW * healthFraction, barH), defaultBarFillTex);

            // HP Text
            GUI.Label(new Rect(barX, barY - 1f, barW, barH), $"{currentHealth} / {maxHealth}", hpTextStyle);
        }

        // Victory banner
        if (defeated && defeatTimer < 6f)
        {
            float alpha = Mathf.Clamp01((6f - defeatTimer) / 1.5f);
            Color prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, 60f), $"{bossName.ToUpper()} VANQUISHED", victoryStyle);
            GUI.color = prev;
        }
    }

    private void OnDrawGizmosSelected()
    {
        Vector2 dir = facingRight ? Vector2.right : Vector2.left;
        Vector2 origin = (Vector2)transform.position + Vector2.up * attackHeight + dir * (attackReach * 0.5f);
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(origin, attackReach);
    }
}
