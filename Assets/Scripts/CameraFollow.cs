using UnityEngine;

/// <summary>
/// Camera follower styled after Nine Sols:
/// - Smooth tracking with dead zones
/// - Forward lookahead based on player movement/facing
/// - Screen shake on parry, heavy attacks, and boss hits
/// - Clamped to level boundaries
/// </summary>
public class CameraFollow : MonoBehaviour
{
    private static CameraFollow instance;
    public static CameraFollow Instance => instance;

    [SerializeField] private Transform target;
    [SerializeField] private float smoothTime = 0.18f;
    // Positive Y keeps the target low on screen, so the ground sits at the bottom and the view opens up above the player.
    [SerializeField] private Vector3 offset = new Vector3(0f, 3f, -10f);

    [Header("Nine Sols Lookahead")]
    [Tooltip("How far the camera looks ahead horizontally in the direction the player is moving or facing.")]
    [SerializeField] private float lookAheadDistance = 2.0f;
    [SerializeField] private float lookAheadSmoothTime = 0.35f;

    [Header("Vertical stability")]
    [Tooltip("How far (world units) the player can rise above their normal spot before the camera starts following upward. Keeps the view steady during ordinary jumps. The camera always comes straight back down.")]
    [SerializeField] private float verticalDeadZone = 3f;

    [Header("Level limits")]
    [Tooltip("Keep the whole view inside the level: nothing is shown below the ground or beyond its ends.")]
    [SerializeField] private bool clampToLevel = true;
    [SerializeField] private float levelMinX = -14.14f;
    [SerializeField] private float levelMaxX = 73.86f;
    [Tooltip("Lowest world Y the camera may show (the bottom edge of the ground).")]
    [SerializeField] private float levelMinY = -5.34f;

    private Camera cam;
    private Vector3 velocity = Vector3.zero;
    private float focusY;
    private float currentLookAheadX;
    private float lookAheadVelocity;

    // Screen Shake state
    private float shakeIntensity;
    private float shakeDuration;
    private float shakeTimer;

    private void Awake()
    {
        instance = this;
        cam = GetComponent<Camera>();
    }

    private void Start()
    {
        if (target == null)
        {
            var p = GameObject.Find("Player");
            if (p != null) target = p.transform;
        }

        // Start already framed correctly instead of panning in from wherever the camera was placed.
        if (target != null)
        {
            Vector3 start = ClampToLevel(target.position + offset);
            transform.position = start;
            focusY = start.y;
        }
    }

    private void LateUpdate()
    {
        if (target == null)
        {
            var p = GameObject.Find("Player");
            if (p != null) target = p.transform;
            if (target == null) return;
        }

        // 1. Calculate dynamic horizontal lookahead
        float targetLookAhead = 0f;
        var pc = target.GetComponent<PlayerController>();
        if (pc != null)
        {
            targetLookAhead = pc.FacingDirection * lookAheadDistance;
        }

        currentLookAheadX = Mathf.SmoothDamp(currentLookAheadX, targetLookAhead, ref lookAheadVelocity, lookAheadSmoothTime);

        Vector3 desiredPosition = target.position + offset;
        desiredPosition.x += currentLookAheadX;

        // Vertical dead zone: rise only once the player is well above their usual spot, drop back immediately.
        if (desiredPosition.y > focusY + verticalDeadZone)
        {
            focusY = desiredPosition.y - verticalDeadZone;
        }
        else if (desiredPosition.y < focusY)
        {
            focusY = desiredPosition.y;
        }
        desiredPosition.y = focusY;

        desiredPosition = ClampToLevel(desiredPosition);
        Vector3 smoothed = Vector3.SmoothDamp(transform.position, desiredPosition, ref velocity, smoothTime);

        // 2. Apply Screen Shake
        if (shakeTimer > 0f)
        {
            shakeTimer -= Time.unscaledDeltaTime;
            float progress = shakeTimer / Mathf.Max(0.001f, shakeDuration);
            float currentMagnitude = shakeIntensity * progress;
            Vector2 randomOffset = Random.insideUnitCircle * currentMagnitude;
            smoothed.x += randomOffset.x;
            smoothed.y += randomOffset.y;
        }

        transform.position = ClampToLevel(smoothed);
    }

    /// <summary>
    /// Triggers screen shake with intensity and duration.
    /// </summary>
    public static void Shake(float intensity, float duration)
    {
        if (instance != null)
        {
            instance.shakeIntensity = intensity;
            instance.shakeDuration = duration;
            instance.shakeTimer = duration;
        }
    }

    // Called by the level generator as the ground grows ahead of the player and is trimmed behind them.
    public void SetLevelBounds(float minX, float maxX)
    {
        levelMinX = minX;
        levelMaxX = maxX;
    }

    private Vector3 ClampToLevel(Vector3 position)
    {
        if (!clampToLevel || cam == null || !cam.orthographic)
        {
            return position;
        }

        float halfHeight = cam.orthographicSize;
        float halfWidth = halfHeight * cam.aspect;

        float minX = levelMinX + halfWidth;
        float maxX = levelMaxX - halfWidth;
        // If the window is wider than the level, just centre on it.
        position.x = minX > maxX ? (levelMinX + levelMaxX) * 0.5f : Mathf.Clamp(position.x, minX, maxX);
        position.y = Mathf.Max(position.y, levelMinY + halfHeight);
        return position;
    }
}
