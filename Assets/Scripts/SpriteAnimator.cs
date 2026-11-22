using System.Collections.Generic;
using UnityEngine;

// Plays sprite-frame animations on a SpriteRenderer and blends smoothly between them.
//
// Three ways to play a clip:
//   PlayLoop    - repeats forever, remembers where it was so walk -> attack -> walk resumes the stride (no foot pop).
//   PlaySegment - plays a range of frames once, stretched to last exactly N seconds (used for attacks, dash, teleport...).
//   HoldFrame   - shows one fixed frame (used for physics-driven poses such as jump / fall).
//
// Whenever the clip changes, the previous picture is cross-dissolved into the new one by a "ghost" sprite that fades out
// on top, so poses never snap.
//
// Optional layers drawn over the body, all frame-locked to it (layer[i] is shown whenever frames[i] is on screen):
//   Overlay (the heavy weapon / censer), Fx (the flame of the swing) and Miasma (the smoke trail of the censer).
//   A clip without frames for a layer hides that layer. Leave a layer's target empty to switch it off.
[DefaultExecutionOrder(50)]
public class SpriteAnimator : MonoBehaviour
{
    [System.Serializable]
    public class Clip
    {
        public string name;
        public Sprite[] frames;
        [Tooltip("Optional second layer drawn over the body (e.g. the heavy weapon). overlayFrames[i] is shown together with frames[i]. Leave empty for none.")]
        public Sprite[] overlayFrames;
        [Tooltip("Optional flame / slash effect layer. fxFrames[i] is shown together with frames[i]. Leave empty for none.")]
        public Sprite[] fxFrames;
        [Tooltip("Optional miasma / smoke effect layer. miasmaFrames[i] is shown together with frames[i]. Leave empty for none.")]
        public Sprite[] miasmaFrames;
        [Tooltip("Seconds per frame when the clip loops at normal speed.")]
        public float frameTime = 0.05f;
    }

    // An extra character. Any clip it doesn't have is taken from the main clips instead.
    [System.Serializable]
    public class Skin
    {
        public string name;
        public Clip[] clips;
    }

    private enum Mode { Loop, Segment, Hold }

    // One frame-locked layer on top of the body, with its own cross-dissolve ghost.
    private class Layer
    {
        public string ghostName;
        public SpriteRenderer target;
        public SpriteRenderer ghost;
        public System.Func<Clip, Sprite[]> frames;
    }

    [SerializeField] private SpriteRenderer target;
    [Tooltip("Optional child renderer that shows each clip's Overlay Frames in step with the body (the heavy weapon).")]
    [SerializeField] private SpriteRenderer overlayTarget;
    [Tooltip("Optional child renderer for each clip's Fx Frames (the flame of a heavy swing).")]
    [SerializeField] private SpriteRenderer fxTarget;
    [Tooltip("Optional child renderer for each clip's Miasma Frames (the smoke of the censer).")]
    [SerializeField] private SpriteRenderer miasmaTarget;
    [Tooltip("The main character. This set should be complete: it is the fallback for every other skin.")]
    [SerializeField] private Clip[] clips;
    [Tooltip("Extra characters. A skin may be partial; missing clips fall back to the main clips above.")]
    [SerializeField] private Skin[] extraSkins;
    [Tooltip("0 = the main clips, 1... = Extra Skins in order.")]
    [SerializeField] private int activeSkin;
    [Tooltip("Cross-dissolve time (seconds) used when a call doesn't specify its own.")]
    [SerializeField] private float defaultBlendTime = 0.08f;

    private SpriteRenderer ghost;
    private Layer[] layers;
    private Clip clip;
    private Mode mode;
    private int fromFrame;
    private int toFrame;
    private float frameDuration = 0.05f;
    private float time;
    private float speed = 1f;
    private int heldFrame = -1;
    private int shownFrame = -1;
    private float blendLeft;
    private float blendTotal;
    private readonly Dictionary<string, float> loopTime = new Dictionary<string, float>();

    public string CurrentName => clip != null ? clip.name : string.Empty;
    public bool IsFinished { get; private set; }
    // Index (within the clip) of the frame currently shown.
    public int CurrentFrame => shownFrame;
    public bool IsBlending => blendLeft > 0f;
    public int ActiveSkin => activeSkin;
    public int SkinCount => 1 + (extraSkins != null ? extraSkins.Length : 0);
    public string ActiveSkinName => activeSkin > 0 && extraSkins != null && activeSkin - 1 < extraSkins.Length ? extraSkins[activeSkin - 1].name : "Main";

    // Number of frames in a clip (after fallback), 0 if there is no such clip.
    public int FrameCount(string clipName)
    {
        Clip found = Find(clipName);
        return found != null && found.frames != null ? found.frames.Length : 0;
    }

    // Switches to another character. The current picture cross-dissolves into the new one.
    public void SetSkin(int index)
    {
        index = Mathf.Clamp(index, 0, SkinCount - 1);
        if (index == activeSkin)
        {
            return;
        }

        RememberLoopTime();
        activeSkin = index;
        loopTime.Clear();
        clip = null;
        mode = Mode.Loop;
        PlayLoop("idle", 1f, 0.12f);
    }

    public void CycleSkin()
    {
        SetSkin((activeSkin + 1) % SkinCount);
    }

    private void Awake()
    {
        if (target == null)
        {
            target = GetComponent<SpriteRenderer>();
        }
        CreateGhost();
        CreateLayers();
    }

    private void Start()
    {
        if (clip == null)
        {
            PlayLoop("idle", 1f, 0f);
        }
    }

    private void Update()
    {
        Tick(Time.deltaTime);
    }

    // Advances the animation. Public so it can also be driven manually (e.g. in tests).
    public void Tick(float dt)
    {
        if (clip != null && clip.frames != null && clip.frames.Length > 0)
        {
            switch (mode)
            {
                case Mode.Loop:
                    {
                        time += dt * speed;
                        int count = toFrame - fromFrame + 1;
                        int step = (int)(time / frameDuration);
                        Show(fromFrame + step % count);
                        break;
                    }
                case Mode.Segment:
                    {
                        time += dt;
                        int count = toFrame - fromFrame + 1;
                        float position = time / frameDuration;
                        Show(fromFrame + Mathf.Min((int)position, count - 1));
                        IsFinished = position >= count;
                        break;
                    }
            }
        }

        UpdateGhost(dt);
        SyncLayerFlip();
    }

    // Repeats a clip. Does nothing but update the speed if it is already looping. blendTime < 0 uses the default.
    public void PlayLoop(string clipName, float playbackSpeed = 1f, float blendTime = -1f)
    {
        Clip found = Find(clipName);
        if (found == null || found.frames == null || found.frames.Length == 0)
        {
            return;
        }

        if (mode == Mode.Loop && clip == found)
        {
            speed = Mathf.Max(0.05f, playbackSpeed);
            return;
        }

        RememberLoopTime();
        BeginBlend(blendTime);

        clip = found;
        mode = Mode.Loop;
        fromFrame = 0;
        toFrame = found.frames.Length - 1;
        frameDuration = Mathf.Max(0.005f, found.frameTime);
        speed = Mathf.Max(0.05f, playbackSpeed);
        IsFinished = false;
        loopTime.TryGetValue(found.name, out time);   // resume where this loop left off
        shownFrame = -1;
        Show(fromFrame + (int)(time / frameDuration) % (toFrame - fromFrame + 1));
    }

    // Plays frames fromFrame..toFrame once, stretched to last totalDuration seconds. Always restarts.
    public void PlaySegment(string clipName, int from, int to, float totalDuration, float blendTime = -1f)
    {
        Clip found = Find(clipName);
        if (found == null || found.frames == null || found.frames.Length == 0)
        {
            return;
        }

        RememberLoopTime();
        BeginBlend(blendTime);

        clip = found;
        mode = Mode.Segment;
        fromFrame = Mathf.Clamp(from, 0, found.frames.Length - 1);
        toFrame = Mathf.Clamp(to, fromFrame, found.frames.Length - 1);
        frameDuration = Mathf.Max(0.005f, totalDuration) / (toFrame - fromFrame + 1);
        time = 0f;
        IsFinished = false;
        shownFrame = -1;
        Show(fromFrame);
    }

    // Shows one fixed frame. Calling it again with the same frame does nothing; a different frame is blended in.
    public void HoldFrame(string clipName, int frame, float blendTime = -1f)
    {
        Clip found = Find(clipName);
        if (found == null || found.frames == null || found.frames.Length == 0)
        {
            return;
        }

        frame = Mathf.Clamp(frame, 0, found.frames.Length - 1);
        if (mode == Mode.Hold && clip == found && heldFrame == frame)
        {
            return;
        }

        RememberLoopTime();
        BeginBlend(blendTime);

        clip = found;
        mode = Mode.Hold;
        heldFrame = frame;
        IsFinished = false;
        shownFrame = -1;
        Show(frame);
    }

    private void RememberLoopTime()
    {
        if (mode == Mode.Loop && clip != null)
        {
            loopTime[clip.name] = time;
        }
    }

    private void Show(int frame)
    {
        if (target == null || frame == shownFrame || clip == null || frame < 0 || frame >= clip.frames.Length)
        {
            return;
        }

        shownFrame = frame;
        target.sprite = clip.frames[frame];
        ShowLayers(frame);
    }

    // Each layer shows the sprite with the same index as the body frame; a clip without frames for a layer hides it.
    private void ShowLayers(int frame)
    {
        if (layers == null)
        {
            return;
        }

        foreach (Layer layer in layers)
        {
            if (layer.target == null)
            {
                continue;
            }

            Sprite sprite = null;
            Sprite[] set = clip != null ? layer.frames(clip) : null;
            if (set != null && frame >= 0 && frame < set.Length)
            {
                sprite = set[frame];
            }

            layer.target.sprite = sprite;
            layer.target.enabled = sprite != null;
        }

        SyncLayerFlip();
    }

    // The player flips by flipX on the body renderer; every layer has to turn with it (they share the same pivot).
    private void SyncLayerFlip()
    {
        if (layers == null || target == null)
        {
            return;
        }

        foreach (Layer layer in layers)
        {
            if (layer.target != null)
            {
                layer.target.flipX = target.flipX;
            }
        }
    }

    // The active skin's clip if it has one, otherwise the main clip of that name.
    private Clip Find(string clipName)
    {
        if (activeSkin > 0 && extraSkins != null && activeSkin - 1 < extraSkins.Length)
        {
            Clip fromSkin = FindIn(extraSkins[activeSkin - 1].clips, clipName);
            if (fromSkin != null)
            {
                return fromSkin;
            }
        }

        return FindIn(clips, clipName);
    }

    private static Clip FindIn(Clip[] set, string clipName)
    {
        if (set == null)
        {
            return null;
        }

        foreach (Clip c in set)
        {
            if (c != null && c.name == clipName && c.frames != null && c.frames.Length > 0)
            {
                return c;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- blending

    private void CreateGhost()
    {
        if (target == null)
        {
            return;
        }

        ghost = MakeGhost("BlendGhost", target);
    }

    // Same idea for the weapon / flame / smoke layers, so a layer that disappears (or changes pose) fades instead of snapping.
    private void CreateLayers()
    {
        layers = new[]
        {
            new Layer { ghostName = "BlendGhostOverlay", target = overlayTarget, frames = c => c.overlayFrames },
            new Layer { ghostName = "BlendGhostFx", target = fxTarget, frames = c => c.fxFrames },
            new Layer { ghostName = "BlendGhostMiasma", target = miasmaTarget, frames = c => c.miasmaFrames },
        };

        foreach (Layer layer in layers)
        {
            if (layer.target != null)
            {
                layer.ghost = MakeGhost(layer.ghostName, layer.target);
            }
        }
    }

    private SpriteRenderer MakeGhost(string ghostName, SpriteRenderer source)
    {
        Transform existing = transform.Find(ghostName);
        GameObject go = existing != null ? existing.gameObject : new GameObject(ghostName);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        SpriteRenderer sr = go.GetComponent<SpriteRenderer>();
        if (sr == null)
        {
            sr = go.AddComponent<SpriteRenderer>();
        }
        sr.sharedMaterial = source.sharedMaterial;
        sr.sortingLayerID = source.sortingLayerID;
        sr.sortingOrder = source.sortingOrder + 1;   // fades out on top of the new picture
        sr.enabled = false;
        return sr;
    }

    // Freezes whatever is on screen into the ghosts, which then fade away while the new clip plays underneath.
    private void BeginBlend(float blendTime)
    {
        if (blendTime < 0f)
        {
            blendTime = defaultBlendTime;
        }

        if (ghost == null || target == null || target.sprite == null || blendTime <= 0.001f)
        {
            HideGhosts();
            blendLeft = 0f;
            return;
        }

        ghost.sprite = target.sprite;
        ghost.flipX = target.flipX;
        ghost.color = Color.white;
        ghost.enabled = true;

        if (layers != null)
        {
            foreach (Layer layer in layers)
            {
                if (layer.ghost == null)
                {
                    continue;
                }

                bool had = layer.target != null && layer.target.enabled && layer.target.sprite != null;
                layer.ghost.sprite = had ? layer.target.sprite : null;
                layer.ghost.flipX = layer.target != null && layer.target.flipX;
                layer.ghost.color = Color.white;
                layer.ghost.enabled = had;
            }
        }

        blendTotal = blendTime;
        blendLeft = blendTime;
    }

    private void HideGhosts()
    {
        if (ghost != null)
        {
            ghost.enabled = false;
        }

        if (layers != null)
        {
            foreach (Layer layer in layers)
            {
                if (layer.ghost != null)
                {
                    layer.ghost.enabled = false;
                }
            }
        }
    }

    private void UpdateGhost(float dt)
    {
        if (ghost == null || blendLeft <= 0f)
        {
            return;
        }

        blendLeft -= dt;
        if (blendLeft <= 0f)
        {
            blendLeft = 0f;
            HideGhosts();
            return;
        }

        float a = Mathf.Clamp01(blendLeft / blendTotal);
        a = a * a * (3f - 2f * a); // smoothstep: eases in and out
        Color faded = new Color(1f, 1f, 1f, a);
        ghost.color = faded;

        if (layers != null)
        {
            foreach (Layer layer in layers)
            {
                if (layer.ghost != null)
                {
                    layer.ghost.color = faded;
                }
            }
        }
    }
}
