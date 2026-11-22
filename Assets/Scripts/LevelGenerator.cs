using System.Collections.Generic;
using UnityEngine;

// Endless level. Keeps ground, platforms and decorations generated ahead of the player and removes what is far behind.
//
// The world is split into chunks. When the player gets within "Generate Ahead" of the end of the ground, a new chunk is
// built: the ground strip is stretched, a random arrangement of platforms is placed, and decorations are scattered over
// the free ground. Chunks that are further than "Keep Behind" behind the player are destroyed and the ground is trimmed.
public class LevelGenerator : MonoBehaviour
{
    private struct PlatformInfo
    {
        public GameObject template;   // inactive copy used for spawning
        public float leftOffset;      // collider left edge relative to the template's x
        public float width;
        public float topY;            // world Y of the top surface
    }

    private struct DecorInfo
    {
        public GameObject prefab;
        public float width;
        public float centerXOffset;   // bounds centre X relative to the instance's x
        public float bottomYOffset;   // bounds bottom Y relative to the instance's y
    }

    private struct PlacedPlatform
    {
        public float left;
        public float right;
        public float topY;
    }

    private class Chunk
    {
        public float left;
        public float right;
        public readonly List<GameObject> objects = new List<GameObject>();
    }

    private struct Part
    {
        public readonly int template;   // index into Platform Templates
        public readonly float x;        // left edge, relative to the start of the pattern
        public Part(int template, float x)
        {
            this.template = template;
            this.x = x;
        }
    }

    // Platform arrangements. Template indices: 0 = A (1.3 up), 1 = B (0.7 up), 2 = C (2.1 up), 3 = D (0.7 up).
    // Every step is within the player's jump (about 2 units high) and gaps are well under the horizontal jump reach.
    private static readonly Part[][] Patterns =
    {
        // Cluster: the hand-made A-B-C-D stairs.
        new[] { new Part(0, 0f), new Part(1, 1.1f), new Part(2, 2.2f), new Part(3, 3.4f) },
        // High run: climb up, cross a row of high platforms, come back down.
        new[] { new Part(1, 0f), new Part(0, 2.8f), new Part(2, 5.8f), new Part(2, 9.2f), new Part(0, 12.8f) },
        // Stepping stones: low platforms with small gaps.
        new[] { new Part(3, 0f), new Part(1, 4f), new Part(3, 8f), new Part(1, 12f) },
    };

    [Header("Scene references")]
    [SerializeField] private Transform player;
    [SerializeField] private CameraFollow cameraFollow;
    [Tooltip("The ground strip. It is stretched to the right as the player advances and trimmed from the left behind them.")]
    [SerializeField] private GameObject ground;
    [Tooltip("Order matters: 0 = A (1.3 up), 1 = B (0.7 up, one-way), 2 = C (2.1 up), 3 = D (0.7 up, one-way). Copies are made at start, so the originals can be removed with the hand-made section.")]
    [SerializeField] private GameObject[] platformTemplates;
    [Tooltip("Hand-made content already in the scene. It is assigned to chunks at start, so it is removed together with the ground once the player is far past it.")]
    [SerializeField] private Transform[] existingContentRoots;
    [SerializeField] private string[] ignoreChildNames = { "MainGround", "Building_Blocks", "Decorations_Showcase" };

    [Header("Decoration prefabs")]
    [Tooltip("Plants, rocks and grass. Several per chunk.")]
    [SerializeField] private GameObject[] commonDecorations;
    [Tooltip("Bigger landmarks (wells, statues, lamps...). One or two per chunk.")]
    [SerializeField] private GameObject[] featureDecorations;
    [Tooltip("Small props that can sit on top of platforms.")]
    [SerializeField] private GameObject[] smallDecorations;

    [Header("Generation")]
    [SerializeField] private float chunkWidth = 22f;
    [Tooltip("Build new chunks until the ground reaches this far past the player.")]
    [SerializeField] private float generateAhead = 45f;
    [Tooltip("Chunks further than this behind the player are removed.")]
    [SerializeField] private float keepBehind = 60f;
    [SerializeField] private bool despawnBehind = true;
    [Tooltip("0 = a different level every run. Any other number repeats the same level.")]
    [SerializeField] private int seed;

    private bool initialized;
    private System.Random rng;
    private SpriteRenderer groundSprite;
    private BoxCollider2D groundCollider;
    private float groundLeft;
    private float groundRight;
    private float groundTop;
    private Transform container;
    private Transform templateRoot;
    private Transform leftWall;
    private PlatformInfo[] platformInfos;
    private readonly List<DecorInfo> commonInfos = new List<DecorInfo>();
    private readonly List<DecorInfo> featureInfos = new List<DecorInfo>();
    private readonly List<DecorInfo> smallInfos = new List<DecorInfo>();
    private readonly List<Chunk> chunks = new List<Chunk>();
    private readonly List<PlacedPlatform> placedPlatforms = new List<PlacedPlatform>();
    private int lastPattern = -1;

    public float GroundLeft => groundLeft;
    public float GroundRight => groundRight;
    public float GroundTop => groundTop;
    public int ChunkCount => chunks.Count;

    private void Start()
    {
        Initialize();
    }

    private void Update()
    {
        if (player != null)
        {
            Tick(player.position.x);
        }
    }

    public void Initialize()
    {
        if (initialized)
        {
            return;
        }
        initialized = true;

        rng = seed == 0 ? new System.Random() : new System.Random(seed);
        Physics2D.SyncTransforms();

        groundSprite = ground.GetComponent<SpriteRenderer>();
        groundCollider = ground.GetComponent<BoxCollider2D>();
        Bounds groundBounds = groundCollider.bounds;
        groundLeft = groundBounds.min.x;
        groundRight = groundBounds.max.x;
        groundTop = groundBounds.max.y;

        container = new GameObject("Generated").transform;
        container.SetParent(transform, false);

        templateRoot = new GameObject("Templates").transform;
        templateRoot.SetParent(transform, false);
        templateRoot.gameObject.SetActive(false);

        BuildPlatformInfos();
        ProbeDecorations(commonDecorations, commonInfos);
        ProbeDecorations(featureDecorations, featureInfos);
        ProbeDecorations(smallDecorations, smallInfos);
        BuildInitialChunks();
        CreateLeftWall();
        UpdateBounds();
    }

    // Builds whatever is missing ahead of the player and removes what is far behind.
    public void Tick(float playerX)
    {
        if (!initialized)
        {
            Initialize();
        }

        int guard = 0;
        while (playerX + generateAhead > groundRight && guard++ < 8)
        {
            GenerateChunk();
        }

        if (despawnBehind)
        {
            DespawnBehind(playerX);
        }
    }

    // ---------------------------------------------------------------- setup

    private void BuildPlatformInfos()
    {
        int count = platformTemplates != null ? platformTemplates.Length : 0;
        platformInfos = new PlatformInfo[count];
        for (int i = 0; i < count; i++)
        {
            GameObject template = platformTemplates[i];
            if (template == null)
            {
                continue;
            }

            Collider2D[] colliders = template.GetComponentsInChildren<Collider2D>();
            if (colliders.Length == 0)
            {
                continue;
            }

            Bounds b = colliders[0].bounds;
            foreach (Collider2D c in colliders)
            {
                b.Encapsulate(c.bounds);
            }

            GameObject copy = Instantiate(template, templateRoot);
            copy.name = template.name;

            platformInfos[i] = new PlatformInfo
            {
                template = copy,
                leftOffset = b.min.x - template.transform.position.x,
                width = b.size.x,
                topY = b.max.y,
            };
        }
    }

    // Spawns each prefab once, off screen, to measure its real size, then discards it.
    private void ProbeDecorations(GameObject[] prefabs, List<DecorInfo> into)
    {
        if (prefabs == null)
        {
            return;
        }

        foreach (GameObject prefab in prefabs)
        {
            if (prefab == null)
            {
                continue;
            }

            GameObject probe = Instantiate(prefab, new Vector3(0f, -1000f, 0f), Quaternion.identity);
            SpriteRenderer[] renderers = probe.GetComponentsInChildren<SpriteRenderer>();
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                foreach (SpriteRenderer r in renderers)
                {
                    b.Encapsulate(r.bounds);
                }

                into.Add(new DecorInfo
                {
                    prefab = prefab,
                    width = b.size.x,
                    centerXOffset = b.center.x - probe.transform.position.x,
                    bottomYOffset = b.min.y - probe.transform.position.y,
                });
            }
            DestroyObject(probe);
        }
    }

    // Splits the existing ground into chunks and hands the hand-made content to them, so it is cleaned up like the rest.
    private void BuildInitialChunks()
    {
        int count = Mathf.Max(1, Mathf.CeilToInt((groundRight - groundLeft) / chunkWidth - 0.001f));
        for (int i = 0; i < count; i++)
        {
            float left = groundLeft + i * chunkWidth;
            chunks.Add(new Chunk { left = left, right = Mathf.Min(left + chunkWidth, groundRight) });
        }

        if (existingContentRoots == null)
        {
            return;
        }

        foreach (Transform root in existingContentRoots)
        {
            if (root == null)
            {
                continue;
            }

            foreach (Transform child in root)
            {
                if (IsIgnored(child.name))
                {
                    continue;
                }

                int index = Mathf.Clamp(Mathf.FloorToInt((ContentCenterX(child) - groundLeft) / chunkWidth), 0, chunks.Count - 1);
                chunks[index].objects.Add(child.gameObject);
            }
        }
    }

    private bool IsIgnored(string objectName)
    {
        if (ignoreChildNames == null)
        {
            return false;
        }

        foreach (string ignored in ignoreChildNames)
        {
            if (!string.IsNullOrEmpty(ignored) && objectName == ignored)
            {
                return true;
            }
        }
        return false;
    }

    private static float ContentCenterX(Transform content)
    {
        SpriteRenderer[] renderers = content.GetComponentsInChildren<SpriteRenderer>();
        if (renderers.Length == 0)
        {
            return content.position.x;
        }

        Bounds b = renderers[0].bounds;
        foreach (SpriteRenderer r in renderers)
        {
            b.Encapsulate(r.bounds);
        }
        return b.center.x;
    }

    // Invisible wall at the left end of the ground, so the player can't walk off into the void.
    private void CreateLeftWall()
    {
        GameObject wall = new GameObject("LevelLeftWall");
        wall.transform.SetParent(transform, false);
        BoxCollider2D box = wall.AddComponent<BoxCollider2D>();
        box.size = new Vector2(1f, 60f);
        leftWall = wall.transform;
    }

    // ----------------------------------------------------------- generation

    private void GenerateChunk()
    {
        Chunk chunk = new Chunk { left = groundRight, right = groundRight + chunkWidth };

        SetGround(groundLeft, chunk.right);
        PlacePlatforms(chunk);
        PlaceDecorations(chunk);

        chunks.Add(chunk);
        groundRight = chunk.right;
        UpdateBounds();
    }

    private void PlacePlatforms(Chunk chunk)
    {
        placedPlatforms.Clear();
        if (platformInfos == null || platformInfos.Length < 4)
        {
            return;
        }

        float spanLeft = chunk.left + 2.5f;
        float spanRight = chunk.right - 2.5f;
        float cursor = spanLeft;
        int patternCount = rng.NextDouble() < 0.5 ? 2 : 1;

        for (int n = 0; n < patternCount; n++)
        {
            int pattern = rng.Next(Patterns.Length);
            if (pattern == lastPattern)
            {
                pattern = (pattern + 1) % Patterns.Length;
            }

            float width = PatternWidth(Patterns[pattern]);
            float latestStart = spanRight - width;
            if (latestStart < cursor)
            {
                continue; // doesn't fit in what's left of this chunk
            }

            float start = Range(cursor, latestStart);
            foreach (Part part in Patterns[pattern])
            {
                SpawnPlatform(chunk, part, start + part.x);
            }

            lastPattern = pattern;
            cursor = start + width + 2.5f;
        }
    }

    private float PatternWidth(Part[] pattern)
    {
        float width = 0f;
        foreach (Part part in pattern)
        {
            width = Mathf.Max(width, part.x + platformInfos[part.template].width);
        }
        return width;
    }

    private void SpawnPlatform(Chunk chunk, Part part, float left)
    {
        PlatformInfo info = platformInfos[part.template];
        if (info.template == null)
        {
            return;
        }

        GameObject instance = Instantiate(info.template, container);
        instance.SetActive(true);
        instance.name = "Platform_" + "ABCD"[part.template] + "_gen";
        Vector3 position = info.template.transform.position;
        position.x = left - info.leftOffset;
        instance.transform.position = position;

        chunk.objects.Add(instance);
        placedPlatforms.Add(new PlacedPlatform { left = left, right = left + info.width, topY = info.topY });
    }

    private void PlaceDecorations(Chunk chunk)
    {
        // Free ground = the chunk minus the stretches under platforms.
        List<Vector2> free = new List<Vector2> { new Vector2(chunk.left + 0.8f, chunk.right - 0.8f) };
        foreach (PlacedPlatform platform in placedPlatforms)
        {
            SubtractRange(free, platform.left - 0.4f, platform.right + 0.4f);
        }

        List<Vector2> taken = new List<Vector2>();
        PlaceOnGround(chunk, featureInfos, rng.Next(1, 3), free, taken);
        PlaceOnGround(chunk, commonInfos, rng.Next(6, 10), free, taken);

        // Small props on top of some platforms.
        if (smallInfos.Count > 0)
        {
            foreach (PlacedPlatform platform in placedPlatforms)
            {
                if (rng.NextDouble() < 0.4)
                {
                    DecorInfo info = smallInfos[rng.Next(smallInfos.Count)];
                    SpawnDecoration(chunk, info, (platform.left + platform.right) * 0.5f, platform.topY);
                }
            }
        }
    }

    private void PlaceOnGround(Chunk chunk, List<DecorInfo> source, int count, List<Vector2> free, List<Vector2> taken)
    {
        if (source.Count == 0)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                DecorInfo info = source[rng.Next(source.Count)];

                // Pick a free stretch, weighted by its length.
                float total = 0f;
                foreach (Vector2 range in free)
                {
                    total += range.y - range.x;
                }
                if (total <= 0f)
                {
                    return;
                }

                float pick = Range(0f, total);
                Vector2 chosen = free[free.Count - 1];
                foreach (Vector2 range in free)
                {
                    pick -= range.y - range.x;
                    if (pick <= 0f)
                    {
                        chosen = range;
                        break;
                    }
                }

                if (chosen.y - chosen.x < info.width)
                {
                    continue;
                }

                float centerX = Range(chosen.x + info.width * 0.5f, chosen.y - info.width * 0.5f);
                float min = centerX - info.width * 0.5f - 0.25f;
                float max = centerX + info.width * 0.5f + 0.25f;
                bool overlaps = false;
                foreach (Vector2 other in taken)
                {
                    if (min < other.y && max > other.x)
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (overlaps)
                {
                    continue;
                }

                SpawnDecoration(chunk, info, centerX, groundTop);
                taken.Add(new Vector2(min, max));
                break;
            }
        }
    }

    private void SpawnDecoration(Chunk chunk, DecorInfo info, float centerX, float surfaceY)
    {
        GameObject instance = Instantiate(info.prefab, container);
        instance.name = info.prefab.name.Replace("PF Village Props - ", "").Replace("TX Village Props - ", "").Replace(' ', '_');
        instance.tag = "Decoration"; // so a sword swing can cut it down
        // Sunk 0.03 into the surface so it never hovers.
        instance.transform.position = new Vector3(centerX - info.centerXOffset, surfaceY - 0.03f - info.bottomYOffset, 0f);
        chunk.objects.Add(instance);
    }

    private static void SubtractRange(List<Vector2> ranges, float from, float to)
    {
        List<Vector2> result = new List<Vector2>();
        foreach (Vector2 range in ranges)
        {
            if (to <= range.x || from >= range.y)
            {
                result.Add(range);
                continue;
            }
            if (from > range.x)
            {
                result.Add(new Vector2(range.x, from));
            }
            if (to < range.y)
            {
                result.Add(new Vector2(to, range.y));
            }
        }
        ranges.Clear();
        ranges.AddRange(result);
    }

    // -------------------------------------------------------------- cleanup

    private void DespawnBehind(float playerX)
    {
        bool changed = false;
        while (chunks.Count > 2 && chunks[0].right < playerX - keepBehind)
        {
            foreach (GameObject obj in chunks[0].objects)
            {
                DestroyObject(obj);
            }
            groundLeft = chunks[0].right;
            chunks.RemoveAt(0);
            changed = true;
        }

        if (changed)
        {
            SetGround(groundLeft, groundRight);
            UpdateBounds();
        }
    }

    // ---------------------------------------------------------------- helpers

    private void SetGround(float left, float right)
    {
        float width = right - left;
        groundSprite.size = new Vector2(width, groundSprite.size.y);
        groundCollider.size = new Vector2(width, groundCollider.size.y);
        Vector3 position = ground.transform.position;
        position.x = (left + right) * 0.5f;
        ground.transform.position = position;
    }

    private void UpdateBounds()
    {
        if (cameraFollow != null)
        {
            cameraFollow.SetLevelBounds(groundLeft, groundRight);
        }
        if (leftWall != null)
        {
            leftWall.position = new Vector3(groundLeft - 0.5f, groundTop + 10f, 0f);
        }
    }

    private float Range(float min, float max)
    {
        return min + (float)rng.NextDouble() * (max - min);
    }

    private static void DestroyObject(GameObject obj)
    {
        if (obj == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(obj);
        }
        else
        {
            DestroyImmediate(obj);
        }
    }
}
