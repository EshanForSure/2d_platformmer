using UnityEngine;

// Parallax for one background layer. Attach to each layer root under "Background".
// A layer root can hold any number of child SpriteRenderers (e.g. a row of tiles); their combined bounds are used.
// Runs after CameraFollow's LateUpdate so the background never lags a frame behind the camera.
[DefaultExecutionOrder(100)]
public class ParallaxLayer : MonoBehaviour
{
    [SerializeField] private Camera targetCamera;

    [Header("Parallax")]
    [Tooltip("How much of the camera's horizontal movement this layer follows. 0 = fixed in the world (scrolls with gameplay), 1 = locked to the camera (looks static). Far layers use higher values, so they scroll slower on screen.")]
    [SerializeField, Range(0f, 1f)] private float followX = 0.5f;
    [Tooltip("Same as Follow X, for vertical camera movement.")]
    [SerializeField, Range(0f, 1f)] private float followY = 0.5f;

    [Header("Placement")]
    [Tooltip("Camera position at which this layer sits exactly at Rest Position (roughly the camera when the Player stands in the middle of the level).")]
    [SerializeField] private Vector2 focusCameraPosition = new Vector2(3.5f, -1.7f);
    [SerializeField] private Vector2 restPosition;

    [Header("Safety")]
    [Tooltip("Never let the layer's edge enter the camera view, even if the camera leaves the designed level area.")]
    [SerializeField] private bool clampToCameraView = true;
    [Tooltip("Also keep the layer covering the view vertically. Turn this OFF for layers that must stay anchored to the ground (e.g. trees), so nothing can ever lift them.")]
    [SerializeField] private bool clampVertically = true;
    [Tooltip("Endless scrolling: shifts the layer by whole mirrored tile pairs, which looks identical, so it never runs out however far the player travels.")]
    [SerializeField] private bool wrapHorizontally = true;

    private SpriteRenderer[] renderers;
    private float wrapPeriod;

    private void Awake()
    {
        renderers = GetComponentsInChildren<SpriteRenderer>();
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }
    }

    private void LateUpdate()
    {
        if (targetCamera == null)
        {
            return;
        }

        Vector2 target = ComputeTargetPosition(targetCamera.transform.position);
        transform.position = new Vector3(target.x, target.y, transform.position.z);
    }

    // Pure calculation (no side effects), so it can also be used to test coverage in the editor.
    public Vector2 ComputeTargetPosition(Vector3 cameraPosition)
    {
        Vector2 position = restPosition + new Vector2(
            followX * (cameraPosition.x - focusCameraPosition.x),
            followY * (cameraPosition.y - focusCameraPosition.y));

        if (wrapHorizontally)
        {
            float period = GetWrapPeriod();
            if (period > 0f)
            {
                float offsetX = position.x - cameraPosition.x;
                offsetX = Mathf.Repeat(offsetX + period * 0.5f, period) - period * 0.5f;
                position.x = cameraPosition.x + offsetX;
            }
        }

        if (clampToCameraView && targetCamera != null && targetCamera.orthographic && TryGetCombinedBounds(out Bounds bounds))
        {
            // Offset between this object's pivot and the centre of everything it draws.
            Vector2 centerOffset = (Vector2)(bounds.center - transform.position);
            Vector2 extents = bounds.extents;
            float halfHeight = targetCamera.orthographicSize;
            float halfWidth = halfHeight * targetCamera.aspect;
            Vector2 centre = position + centerOffset;

            if (extents.x > halfWidth)
            {
                centre.x = Mathf.Clamp(centre.x, cameraPosition.x + halfWidth - extents.x, cameraPosition.x - halfWidth + extents.x);
            }
            if (clampVertically && extents.y > halfHeight)
            {
                centre.y = Mathf.Clamp(centre.y, cameraPosition.y + halfHeight - extents.y, cameraPosition.y - halfHeight + extents.y);
            }

            position = centre - centerOffset;
        }

        return position;
    }

    // Two tiles: the row alternates normal / mirrored tiles, so shifting by a pair gives an identical picture.
    private float GetWrapPeriod()
    {
        if (wrapPeriod > 0f)
        {
            return wrapPeriod;
        }

        if (renderers == null || renderers.Length == 0)
        {
            renderers = GetComponentsInChildren<SpriteRenderer>();
        }

        if (renderers.Length >= 3 && renderers[0] != null)
        {
            wrapPeriod = renderers[0].bounds.size.x * 2f;
        }
        return wrapPeriod;
    }

    private bool TryGetCombinedBounds(out Bounds combined)
    {
        if (renderers == null || renderers.Length == 0)
        {
            renderers = GetComponentsInChildren<SpriteRenderer>();
        }

        combined = default;
        bool found = false;
        foreach (SpriteRenderer sr in renderers)
        {
            if (sr == null)
            {
                continue;
            }

            if (!found)
            {
                combined = sr.bounds;
                found = true;
            }
            else
            {
                combined.Encapsulate(sr.bounds);
            }
        }

        return found;
    }
}
