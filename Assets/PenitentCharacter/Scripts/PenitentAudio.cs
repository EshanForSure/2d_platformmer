using UnityEngine;

namespace PenitentCharacter
{
    /// <summary>
    /// Plays sound effects for every player action: walk, jump, land, dodge/slide,
    /// sword combo (3 swings), heavy censer swing (ground + air), parry, parry success,
    /// weapon switch and hit impacts.
    ///
    /// Works out of the box: every slot falls back to a built-in synthesized sound.
    /// To use your own audio, drag clips into a slot (several clips = random variation).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PenitentMotor), typeof(PenitentStateMachine))]
    public class PenitentAudio : MonoBehaviour
    {
        [System.Serializable]
        public class SoundSlot
        {
            [Tooltip("Optional. Leave empty to use the built-in synthesized sound. Several clips = random variation.")]
            public AudioClip[] clips;
            [Range(0f, 1f)] public float volume = 1f;
            public Vector2 pitchRange = new Vector2(0.95f, 1.05f);
        }

        [Header("Master")]
        [Range(0f, 1f)] public float masterVolume = 0.8f;
        [Min(2)] public int voices = 8;

        [Header("Movement")]
        public SoundSlot footstep = new SoundSlot { volume = 0.5f };
        public SoundSlot jump = new SoundSlot { volume = 0.6f };
        public SoundSlot land = new SoundSlot { volume = 0.8f };
        public SoundSlot dodge = new SoundSlot { volume = 0.8f };

        [Header("Sword (Mea Culpa)")]
        public SoundSlot swordSwing1 = new SoundSlot { volume = 0.7f };
        public SoundSlot swordSwing2 = new SoundSlot { volume = 0.7f, pitchRange = new Vector2(1.05f, 1.15f) };
        public SoundSlot swordSwing3 = new SoundSlot { volume = 0.85f, pitchRange = new Vector2(0.9f, 1f) };
        public SoundSlot parry = new SoundSlot { volume = 0.8f };
        public SoundSlot parrySuccess = new SoundSlot { volume = 0.95f };

        [Header("Censer (Veredicto)")]
        public SoundSlot heavySwing = new SoundSlot { volume = 0.9f };
        public SoundSlot heavyAirSwing = new SoundSlot { volume = 0.9f };

        [Header("Other")]
        public SoundSlot hit = new SoundSlot { volume = 0.9f, pitchRange = new Vector2(0.9f, 1.1f) };
        public SoundSlot weaponSwitch = new SoundSlot { volume = 0.7f };

        [Header("Tuning")]
        public float footstepInterval = 0.34f;
        [Tooltip("Heavy weapon walks slower and lower.")]
        public float heavyFootstepIntervalMultiplier = 1.15f;
        [Tooltip("Landing with less fall speed than this plays only a soft footstep.")]
        public float minLandImpactSpeed = 3f;

        private PenitentMotor motor;
        private PenitentStateMachine sm;
        private AudioSource[] pool;
        private int poolIndex;

        private AudioClip[] stepClips;
        private AudioClip jumpClip, landClip, dodgeClip, swing1Clip, swing2Clip, swing3Clip;
        private AudioClip heavyClip, heavyAirClip, parryClip, parrySuccessClip, hitClip, switchClip;

        private float stepTimer;
        private int lastStepIndex = -1;
        private float lastHitSoundTime = -1f;

        private void Awake()
        {
            motor = GetComponent<PenitentMotor>();
            sm = GetComponent<PenitentStateMachine>();

            var sfxRoot = new GameObject("PenitentSFX");
            sfxRoot.transform.SetParent(transform, false);
            pool = new AudioSource[Mathf.Max(2, voices)];
            for (int i = 0; i < pool.Length; i++)
            {
                var s = sfxRoot.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f; // 2D
                pool[i] = s;
            }

            BuildSynthesizedDefaults();
        }

        private void OnEnable()
        {
            if (motor == null) motor = GetComponent<PenitentMotor>();
            if (sm == null) sm = GetComponent<PenitentStateMachine>();

            motor.OnJumped += HandleJumped;
            motor.OnLanded += HandleLanded;
            motor.OnDashStarted += HandleDashStarted;
            sm.OnAttackStarted += HandleAttackStarted;
            sm.OnParryStarted += HandleParryStarted;
            sm.OnWeaponSwitched += HandleWeaponSwitched;
            sm.OnHitLanded += HandleHitLanded;
        }

        private void OnDisable()
        {
            if (motor != null)
            {
                motor.OnJumped -= HandleJumped;
                motor.OnLanded -= HandleLanded;
                motor.OnDashStarted -= HandleDashStarted;
            }
            if (sm != null)
            {
                sm.OnAttackStarted -= HandleAttackStarted;
                sm.OnParryStarted -= HandleParryStarted;
                sm.OnWeaponSwitched -= HandleWeaponSwitched;
                sm.OnHitLanded -= HandleHitLanded;
            }
        }

        private void BuildSynthesizedDefaults()
        {
            stepClips = new[]
            {
                PenitentSynth.Footstep(0),
                PenitentSynth.Footstep(1),
                PenitentSynth.Footstep(2)
            };
            jumpClip = PenitentSynth.Jump();
            landClip = PenitentSynth.Land();
            dodgeClip = PenitentSynth.Dodge();
            swing1Clip = PenitentSynth.Swing("pen_swing1", 11, 0.22f, 0.55f, 0.14f, 0f);
            swing2Clip = PenitentSynth.Swing("pen_swing2", 23, 0.20f, 0.65f, 0.18f, 0f);
            swing3Clip = PenitentSynth.Swing("pen_swing3", 37, 0.32f, 0.40f, 0.08f, 0.35f);
            heavyClip = PenitentSynth.HeavySwing("pen_heavy", 51, 0.60f);
            heavyAirClip = PenitentSynth.HeavySwing("pen_heavy_air", 77, 0.48f);
            parryClip = PenitentSynth.Parry();
            parrySuccessClip = PenitentSynth.ParrySuccess();
            hitClip = PenitentSynth.Hit();
            switchClip = PenitentSynth.WeaponSwitch();
        }

        private void Update()
        {
            UpdateFootsteps(Time.deltaTime);
        }

        // ------------------------------------------------------------------
        // FOOTSTEPS (walk / run)
        // ------------------------------------------------------------------
        private void UpdateFootsteps(float dt)
        {
            bool walking = sm.CurrentState == PenitentState.Run
                           && motor.IsGrounded
                           && Mathf.Abs(motor.Velocity.x) > 0.5f;

            if (!walking)
            {
                // Small lead-in so the first step lands shortly after you start moving
                stepTimer = footstepInterval * 0.3f;
                return;
            }

            stepTimer -= dt;
            if (stepTimer > 0f) return;

            bool heavy = sm.CurrentWeapon == WeaponType.Veredicto;
            stepTimer += footstepInterval * (heavy ? heavyFootstepIntervalMultiplier : 1f);

            int idx;
            do { idx = Random.Range(0, stepClips.Length); } while (idx == lastStepIndex && stepClips.Length > 1);
            lastStepIndex = idx;

            PlaySlot(footstep, stepClips[idx], heavy ? 1.25f : 1f, heavy ? 0.82f : 1f);
        }

        // ------------------------------------------------------------------
        // EVENT HANDLERS
        // ------------------------------------------------------------------
        private void HandleJumped()
        {
            PlaySlot(jump, jumpClip, 1f, sm.CurrentWeapon == WeaponType.Veredicto ? 0.88f : 1f);
        }

        private void HandleLanded(float impactSpeed)
        {
            if (impactSpeed < 0.5f) return;

            if (impactSpeed < minLandImpactSpeed)
            {
                PlaySlot(footstep, stepClips[0], 0.8f);
                return;
            }

            float k = Mathf.InverseLerp(minLandImpactSpeed, 22f, impactSpeed);
            float heavyPitch = sm.CurrentWeapon == WeaponType.Veredicto ? 0.88f : 1f;
            PlaySlot(land, landClip, Mathf.Lerp(0.55f, 1.15f, k), Mathf.Lerp(1.1f, 0.85f, k) * heavyPitch);
        }

        private void HandleDashStarted()
        {
            PlaySlot(dodge, dodgeClip);
        }

        private void HandleAttackStarted(PenitentState state, WeaponType weapon)
        {
            switch (state)
            {
                case PenitentState.Attack1: PlaySlot(swordSwing1, swing1Clip); break;
                case PenitentState.Attack2: PlaySlot(swordSwing2, swing2Clip); break;
                case PenitentState.Attack3: PlaySlot(swordSwing3, swing3Clip); break;
                case PenitentState.HeavyAttack: PlaySlot(heavySwing, heavyClip); break;
                case PenitentState.HeavyJumpAttack: PlaySlot(heavyAirSwing, heavyAirClip); break;
            }
        }

        private void HandleParryStarted(bool success)
        {
            if (success) PlaySlot(parrySuccess, parrySuccessClip);
            else PlaySlot(parry, parryClip);
        }

        private void HandleWeaponSwitched(WeaponType weapon)
        {
            PlaySlot(weaponSwitch, switchClip, 1f, weapon == WeaponType.Veredicto ? 0.85f : 1.1f);
        }

        private void HandleHitLanded(PenitentHitbox hitbox, Collider2D other)
        {
            // One impact sound per ~50ms, even if several colliders are hit in the same swing
            if (Time.time - lastHitSoundTime < 0.05f) return;
            lastHitSoundTime = Time.time;

            float heavy = hitbox != null && hitbox.damage >= 40 ? 1.1f : 0.9f;
            PlaySlot(hit, hitClip, heavy, hitbox != null && hitbox.damage >= 40 ? 0.85f : 1f);
        }

        // ------------------------------------------------------------------
        // PLAYBACK
        // ------------------------------------------------------------------
        private void PlaySlot(SoundSlot slot, AudioClip fallback, float volumeMultiplier = 1f, float pitchMultiplier = 1f)
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

    /// <summary>
    /// Tiny procedural sound generator so the character has sound effects without any audio files.
    /// </summary>
    internal static class PenitentSynth
    {
        private const int SR = 44100;
        private const float TwoPi = Mathf.PI * 2f;

        // ---------- helpers ----------
        private static float[] Buf(float seconds) => new float[Mathf.CeilToInt(seconds * SR)];

        private static float Noise(System.Random r) => (float)(r.NextDouble() * 2.0 - 1.0);

        // Fast attack, exponential decay (tau = decay time constant)
        private static float Env(float t, float attack, float tau)
        {
            return t < attack ? t / attack : Mathf.Exp(-(t - attack) / tau);
        }

        private static AudioClip Make(string name, float[] d, float peak)
        {
            float max = 0.0001f;
            for (int i = 0; i < d.Length; i++) max = Mathf.Max(max, Mathf.Abs(d[i]));
            float g = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= g;

            // short fade-out to avoid a click at the end
            int fade = Mathf.Min(d.Length, 220);
            for (int i = 0; i < fade; i++) d[d.Length - 1 - i] *= i / (float)fade;

            var clip = AudioClip.Create(name, d.Length, 1, SR, false);
            clip.SetData(d, 0);
            return clip;
        }

        private static void AddThump(float[] d, float startTime, float amp, float f0 = 120f, float f1 = 50f, float tau = 0.07f)
        {
            int start = Mathf.Clamp(Mathf.RoundToInt(startTime * SR), 0, d.Length);
            float ph = 0f;
            for (int i = start; i < d.Length; i++)
            {
                float t = (i - start) / (float)SR;
                float e = Env(t, 0.002f, tau);
                if (e < 0.002f) break;
                float f = Mathf.Lerp(f0, f1, Mathf.Clamp01(t / 0.12f));
                ph += TwoPi * f / SR;
                d[i] += Mathf.Sin(ph) * e * amp;
            }
        }

        private static void AddClank(float[] d, float startTime, float[] freqs, float[] amps, float tau)
        {
            int start = Mathf.Clamp(Mathf.RoundToInt(startTime * SR), 0, d.Length);
            for (int i = start; i < d.Length; i++)
            {
                float t = (i - start) / (float)SR;
                float e = Env(t, 0.0008f, tau);
                if (e < 0.002f) break;
                float s = 0f;
                for (int k = 0; k < freqs.Length; k++) s += Mathf.Sin(TwoPi * freqs[k] * t) * amps[k];
                d[i] += s * e * 0.3f;
            }
        }

        private static void AddNoiseBurst(float[] d, System.Random r, float startTime, float amp, float lowpass, float tau)
        {
            int start = Mathf.Clamp(Mathf.RoundToInt(startTime * SR), 0, d.Length);
            float lp = 0f;
            for (int i = start; i < d.Length; i++)
            {
                float t = (i - start) / (float)SR;
                float e = Env(t, 0.0005f, tau);
                if (e < 0.002f) break;
                lp += lowpass * (Noise(r) - lp);
                d[i] += lp * e * amp;
            }
        }

        // ---------- sounds ----------
        public static AudioClip Footstep(int seed)
        {
            var d = Buf(0.16f);
            var r = new System.Random(seed * 7919 + 13);
            float lp = 0f, ph = 0f;
            float f = 62f + seed * 9f;
            float a = 0.18f + 0.05f * seed;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)SR;
                lp += a * (Noise(r) - lp);
                ph += TwoPi * f / SR;
                d[i] = lp * Env(t, 0.002f, 0.03f) * 2.2f + Mathf.Sin(ph) * Env(t, 0.003f, 0.05f) * 0.7f;
            }
            return Make("pen_step_" + seed, d, 0.7f);
        }

        public static AudioClip Jump()
        {
            var d = Buf(0.26f);
            var r = new System.Random(5);
            float ph = 0f, lp = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)SR;
                float f = Mathf.Lerp(150f, 340f, t / 0.26f);
                ph += TwoPi * f / SR;
                float n = Noise(r);
                lp += 0.12f * (n - lp);
                float hiss = n - lp;
                float e = Env(t, 0.012f, 0.07f);
                d[i] = (Mathf.Sin(ph) * 0.22f + hiss * 0.25f) * e;
            }
            AddThump(d, 0f, 0.25f, 140f, 90f, 0.04f); // push-off
            return Make("pen_jump", d, 0.7f);
        }

        public static AudioClip Land()
        {
            var d = Buf(0.28f);
            var r = new System.Random(9);
            AddThump(d, 0f, 1f, 105f, 42f, 0.075f);
            AddNoiseBurst(d, r, 0f, 1.4f, 0.2f, 0.05f);
            return Make("pen_land", d, 0.85f);
        }

        public static AudioClip Dodge()
        {
            // cloth / armor slide: brightening whoosh + low scrape
            const float T = 0.42f;
            var d = Buf(T);
            var r = new System.Random(21);
            float lp = 0f, rumble = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float x = i / (float)d.Length;
                float a = Mathf.Lerp(0.04f, 0.32f, x);
                float n = Noise(r);
                lp += a * (n - lp);
                rumble += 0.015f * (Noise(r) - rumble);
                float bell = Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Pow(x, 0.7f)), 2f);
                d[i] = (lp * 3f + rumble * 6f) * bell;
            }
            return Make("pen_dodge", d, 0.7f);
        }

        private static float[] SwingBuf(int seed, float dur, float aStart, float aEnd)
        {
            var d = Buf(dur);
            var r = new System.Random(seed);
            float fast = 0f, slow = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float x = i / (float)d.Length;
                float a = Mathf.Lerp(aStart, aEnd, x);
                float n = Noise(r);
                fast += a * (n - fast);
                slow += 0.02f * (n - slow);
                float band = fast - slow;
                float env = Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Pow(x, 0.65f)), 2f);
                d[i] = band * env;
            }
            return d;
        }

        public static AudioClip Swing(string name, int seed, float dur, float aStart, float aEnd, float endThump)
        {
            var d = SwingBuf(seed, dur, aStart, aEnd);
            if (endThump > 0f) AddThump(d, dur * 0.7f, endThump * 0.35f, 130f, 60f, 0.06f);
            return Make(name, d, 0.7f);
        }

        public static AudioClip HeavySwing(string name, int seed, float dur)
        {
            var d = SwingBuf(seed, dur, 0.22f, 0.05f);
            var r = new System.Random(seed + 3);

            // chain rattle
            for (int k = 0; k < 6; k++)
            {
                float t0 = 0.04f + (float)r.NextDouble() * (dur * 0.7f);
                float f = 2600f + (float)r.NextDouble() * 1800f;
                AddClank(d, t0, new[] { f, f * 1.51f }, new[] { 0.35f, 0.2f }, 0.012f);
            }

            // heavy impact at the end of the arc
            AddThump(d, dur * 0.72f, 0.9f, 95f, 40f, 0.09f);
            return Make(name, d, 0.8f);
        }

        public static AudioClip Parry()
        {
            var d = Buf(0.6f);
            var r = new System.Random(31);
            AddClank(d, 0f, new[] { 1250f, 1870f, 2790f, 4010f }, new[] { 1f, 0.7f, 0.5f, 0.35f }, 0.16f);
            AddNoiseBurst(d, r, 0f, 0.5f, 0.6f, 0.01f);
            return Make("pen_parry", d, 0.75f);
        }

        public static AudioClip ParrySuccess()
        {
            var d = Buf(0.95f);
            var r = new System.Random(32);
            AddClank(d, 0f, new[] { 1760f, 2640f, 3950f, 5300f }, new[] { 1f, 0.8f, 0.55f, 0.4f }, 0.28f);
            AddClank(d, 0f, new[] { 110f, 220f }, new[] { 1f, 0.5f }, 0.1f);
            AddNoiseBurst(d, r, 0f, 0.7f, 0.7f, 0.012f);
            return Make("pen_parry_success", d, 0.85f);
        }

        public static AudioClip Hit()
        {
            var d = Buf(0.28f);
            var r = new System.Random(41);
            AddThump(d, 0f, 1f, 135f, 55f, 0.06f);
            AddNoiseBurst(d, r, 0f, 1.6f, 0.45f, 0.03f);
            return Make("pen_hit", d, 0.85f);
        }

        public static AudioClip WeaponSwitch()
        {
            var d = Buf(0.28f);
            AddClank(d, 0f, new[] { 2300f, 3400f, 4700f }, new[] { 1f, 0.6f, 0.4f }, 0.03f);
            AddClank(d, 0.08f, new[] { 1800f, 2700f }, new[] { 1f, 0.6f }, 0.05f);
            return Make("pen_switch", d, 0.6f);
        }
    }
}
