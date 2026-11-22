using PenitentCharacter; // shared procedural sound generator (PenitentSynth), used only as a fallback for empty slots
using UnityEngine;

/// <summary>
/// Sound effects for the player: walking, jump, landing, dash, sword slash, overhead attack,
/// air attack, teleport, taking damage, death and hitting the boss / decorations.
///
/// Drag your own clips into the slots. Any slot left empty falls back to a built-in synthesized sound.
/// Several clips in one slot play at random.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerController))]
public class PlayerSfx : MonoBehaviour
{
    [System.Serializable]
    public class SoundSlot
    {
        [Tooltip("Optional. Leave empty to use the built-in synthesized sound.")]
        public AudioClip[] clips;
        [Range(0f, 1f)] public float volume = 1f;
        public Vector2 pitchRange = new Vector2(0.95f, 1.05f);
    }

    [Header("Master")]
    [Range(0f, 1f)] public float masterVolume = 0.8f;
    [Min(2)] public int voices = 8;

    [Header("Movement")]
    [Tooltip("A looping footsteps clip played while running on the ground (e.g. hero_run_footsteps_stone). Empty = synthesized steps.")]
    public AudioClip runLoop;
    [Range(0f, 1f)] public float runLoopVolume = 0.5f;
    public SoundSlot footstep = new SoundSlot { volume = 0.5f };
    public SoundSlot jump = new SoundSlot { volume = 0.6f };
    public SoundSlot land = new SoundSlot { volume = 0.8f };
    public SoundSlot landHard = new SoundSlot { volume = 0.9f };
    public SoundSlot dash = new SoundSlot { volume = 0.8f };
    public SoundSlot teleport = new SoundSlot { volume = 0.8f };

    [Header("Attacks (ignored if the PlayerController already has its own Sword Swing Sounds)")]
    public SoundSlot slash = new SoundSlot { volume = 0.7f };
    public SoundSlot overhead = new SoundSlot { volume = 0.9f };
    public SoundSlot airAttack = new SoundSlot { volume = 0.9f };

    [Header("Impacts")]
    public SoundSlot hitBoss = new SoundSlot { volume = 0.9f, pitchRange = new Vector2(0.9f, 1.1f) };
    public SoundSlot hitDecoration = new SoundSlot { volume = 0.6f, pitchRange = new Vector2(0.95f, 1.1f) };
    public SoundSlot damage = new SoundSlot { volume = 1f };
    public SoundSlot death = new SoundSlot { volume = 1f };

    [Header("Tuning")]
    public float footstepInterval = 0.32f;
    [Tooltip("Landing with less fall speed than this plays nothing.")]
    public float minLandImpactSpeed = 1.5f;
    [Tooltip("Fall speed from which the hard landing sound is used.")]
    public float hardLandSpeed = 17f;

    private PlayerController controller;
    private AudioSource[] pool;
    private AudioSource loopSource;
    private int poolIndex;

    private AudioClip[] stepClips;
    private AudioClip jumpClip, landClip, dashClip, teleportClip, shimmerClip;
    private AudioClip slashClipA, slashClipB, overheadClip, airClip, hitClip;

    private float stepTimer;
    private int lastStepIndex = -1;
    private bool nextSlashIsB;

    private void Awake()
    {
        controller = GetComponent<PlayerController>();

        var root = new GameObject("PlayerSFX");
        root.transform.SetParent(transform, false);
        pool = new AudioSource[Mathf.Max(2, voices)];
        for (int i = 0; i < pool.Length; i++)
        {
            var s = root.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            pool[i] = s;
        }

        loopSource = root.AddComponent<AudioSource>();
        loopSource.playOnAwake = false;
        loopSource.loop = true;
        loopSource.spatialBlend = 0f;

        BuildFallbacks();
    }

    // Synthesized sounds are only generated for slots that have no real clip assigned.
    private void BuildFallbacks()
    {
        if (runLoop == null) stepClips = new[] { PenitentSynth.Footstep(0), PenitentSynth.Footstep(1), PenitentSynth.Footstep(2) };
        if (!Has(jump)) jumpClip = PenitentSynth.Jump();
        if (!Has(land) || !Has(landHard)) landClip = PenitentSynth.Land();
        if (!Has(dash)) dashClip = PenitentSynth.Dodge();
        if (!Has(teleport)) { teleportClip = PenitentSynth.Dodge(); shimmerClip = PenitentSynth.ParrySuccess(); }
        if (!Has(slash))
        {
            slashClipA = PenitentSynth.Swing("pl_slash_a", 11, 0.22f, 0.55f, 0.14f, 0f);
            slashClipB = PenitentSynth.Swing("pl_slash_b", 23, 0.20f, 0.65f, 0.18f, 0f);
        }
        if (!Has(overhead)) overheadClip = PenitentSynth.HeavySwing("pl_overhead", 51, 0.60f);
        if (!Has(airAttack)) airClip = PenitentSynth.HeavySwing("pl_air", 77, 0.48f);
        if (!Has(hitBoss) || !Has(damage) || !Has(death) || !Has(hitDecoration)) hitClip = PenitentSynth.Hit();
    }

    private static bool Has(SoundSlot slot)
    {
        if (slot == null || slot.clips == null) return false;
        foreach (AudioClip c in slot.clips)
        {
            if (c != null) return true;
        }
        return false;
    }

    private void OnEnable()
    {
        if (controller == null) controller = GetComponent<PlayerController>();
        controller.ActionPerformed += OnAction;
    }

    private void OnDisable()
    {
        if (controller != null) controller.ActionPerformed -= OnAction;
        if (loopSource != null) loopSource.Stop();
    }

    private void Update()
    {
        bool running = controller.StateName == "Run" && controller.IsGrounded && Mathf.Abs(controller.Velocity.x) > 0.5f;

        // Real looping footsteps clip
        if (runLoop != null)
        {
            if (running)
            {
                if (loopSource.clip != runLoop) loopSource.clip = runLoop;
                loopSource.volume = runLoopVolume * masterVolume;
                if (!loopSource.isPlaying) loopSource.Play();
            }
            else if (loopSource.isPlaying)
            {
                loopSource.Stop();
            }
            return;
        }

        // Fallback: synthesized steps on a timer
        if (!running)
        {
            stepTimer = footstepInterval * 0.3f;
            return;
        }

        stepTimer -= Time.deltaTime;
        if (stepTimer > 0f) return;
        stepTimer += footstepInterval;

        int idx;
        do { idx = Random.Range(0, stepClips.Length); } while (idx == lastStepIndex && stepClips.Length > 1);
        lastStepIndex = idx;
        Play(footstep, stepClips[idx]);
    }

    private void OnAction(PlayerAction action, float value)
    {
        switch (action)
        {
            case PlayerAction.Jump:
                Play(jump, jumpClip);
                break;

            case PlayerAction.Land:
                if (value < minLandImpactSpeed) break;
                if (value >= hardLandSpeed && Has(landHard))
                {
                    Play(landHard, landClip);
                }
                else
                {
                    float k = Mathf.InverseLerp(minLandImpactSpeed, hardLandSpeed, value);
                    Play(land, landClip, Mathf.Lerp(0.5f, 1.1f, k), Has(land) ? 1f : Mathf.Lerp(1.1f, 0.85f, k));
                }
                break;

            case PlayerAction.Dash:
                Play(dash, dashClip);
                break;

            case PlayerAction.Teleport:
                if (Has(teleport))
                {
                    Play(teleport, null);
                }
                else
                {
                    Play(teleport, teleportClip, 1f, 0.6f);
                    Play(null, shimmerClip, 0.35f, 1.4f);
                }
                break;

            case PlayerAction.Slash:
                if (controller.HasCustomSwingSounds) break;
                Play(slash, nextSlashIsB ? slashClipB : slashClipA);
                nextSlashIsB = !nextSlashIsB;
                break;

            case PlayerAction.Overhead:
                if (controller.HasCustomSwingSounds) break;
                Play(overhead, overheadClip);
                break;

            case PlayerAction.AirAttack:
                if (controller.HasCustomSwingSounds) break;
                Play(airAttack, airClip);
                break;

            case PlayerAction.HitBoss:
                Play(hitBoss, hitClip, value >= 2f ? 1.15f : 0.95f, Has(hitBoss) ? 1f : (value >= 2f ? 0.85f : 1f));
                break;

            case PlayerAction.HitDecoration:
                Play(hitDecoration, hitClip, 0.5f, Has(hitDecoration) ? 1f : 1.3f);
                break;

            case PlayerAction.Damage:
                Play(damage, hitClip, 1f, Has(damage) ? 1f : 0.75f);
                break;

            case PlayerAction.Death:
                Play(death, landClip, 1f, Has(death) ? 1f : 0.6f);
                if (!Has(death)) Play(null, hitClip, 0.9f, 0.5f);
                break;
        }
    }

    private void Play(SoundSlot slot, AudioClip fallback, float volumeMultiplier = 1f, float pitchMultiplier = 1f)
    {
        AudioClip clip = fallback;
        if (slot != null && slot.clips != null && slot.clips.Length > 0)
        {
            AudioClip custom = slot.clips[Random.Range(0, slot.clips.Length)];
            if (custom != null) clip = custom;
        }
        if (clip == null) return;

        float vol = (slot != null ? slot.volume : 1f) * volumeMultiplier * masterVolume;
        Vector2 pr = slot != null ? slot.pitchRange : Vector2.one;
        float pitch = Random.Range(Mathf.Min(pr.x, pr.y), Mathf.Max(pr.x, pr.y)) * pitchMultiplier;

        AudioSource src = pool[poolIndex];
        poolIndex = (poolIndex + 1) % pool.Length;
        src.Stop();
        src.clip = clip;
        src.volume = Mathf.Clamp01(vol);
        src.pitch = Mathf.Max(0.1f, pitch);
        src.Play();
    }
}
