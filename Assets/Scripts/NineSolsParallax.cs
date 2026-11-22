using UnityEngine;

/// <summary>
/// Smooth parallax scrolling for Nine Sols background layers.
/// Attach to any background SpriteRenderer.
///
/// parallaxFactor:
///   0.0 = layer locked to world (no scroll)
///   1.0 = layer moves 1:1 with camera (feels "attached")
///   0.0–0.3 = distant layers (slowest apparent motion = furthest away)
///   0.3–0.7 = mid-distance layers
/// </summary>
public class NineSolsParallax : MonoBehaviour
{
    [Tooltip("0 = fully fixed to world. 1 = moves with camera. Use 0.05–0.3 for distant BG layers.")]
    [Range(0f, 1f)]
    public float parallaxFactor = 0.1f;

    [Tooltip("If true, also parallax vertically (useful for tall BG panels).")]
    public bool verticalParallax = false;

    [Tooltip("Clamp vertical parallax to this range to avoid showing empty space.")]
    public float verticalClamp = 2f;

    private Camera cam;
    private Vector3 startPos;
    private Vector3 camStartPos;

    private void Start()
    {
        cam = Camera.main;
        if (cam == null) { enabled = false; return; }

        startPos    = transform.position;
        camStartPos = cam.transform.position;
    }

    private void LateUpdate()
    {
        if (cam == null) return;

        Vector3 camDelta = cam.transform.position - camStartPos;

        float dx = camDelta.x * parallaxFactor;
        float dy = verticalParallax
            ? Mathf.Clamp(camDelta.y * parallaxFactor, -verticalClamp, verticalClamp)
            : 0f;

        transform.position = new Vector3(
            startPos.x + dx,
            startPos.y + dy,
            startPos.z);
    }

#if UNITY_EDITOR
    // Show gizmo so you can see the layer in Editor
    private void OnDrawGizmosSelected()
    {
        var sr = GetComponent<SpriteRenderer>();
        if (sr == null) return;
        Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.3f);
        Gizmos.DrawWireCube(transform.position, new Vector3(sr.size.x, sr.size.y, 0.1f));
    }
#endif
}
