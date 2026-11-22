using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PenitentCharacter
{
    // ------------------------------------------------------------------
    // Data written to the live file
    // ------------------------------------------------------------------
    [Serializable]
    public class TelemetrySample
    {
        public float t;          // seconds since session start
        public float x, y;       // world position
        public float vx, vy;     // velocity
        public string state;     // Idle / Run / Jump / Fall / Dodge / Attack1...
        public string weapon;
        public bool grounded;
        public bool facingRight;
        public float input;      // horizontal input (-1..1)
    }

    [Serializable]
    public class TelemetryEvent
    {
        public float t;
        public string type;      // jump, land, dodge, attack, parry, weapon_switch, hit, turn, input
        public string detail;
    }

    [Serializable]
    public class BehaviorAnalysis
    {
        public float windowSeconds;
        public string styleLabel;
        public string notes;

        public int attacks, jumps, dodges, parries, weaponSwitches, hitsLanded;
        public int directionChanges, buttonPresses;

        public float actionsPerMinute;
        public float inputsPerSecond;
        public float idlePercent, airbornePercent, attackingPercent, defensivePercent;
        public float distanceTraveled, avgSpeed, longestIdleSeconds;
    }

    [Serializable]
    public class TelemetrySnapshot
    {
        public string note = "Heuristic indicators computed from a short rolling window of movement and input data. Not a psychological assessment.";
        public string sessionId;
        public string generatedAtUtc;
        public float windowSeconds;
        public float samplesPerSecond;
        public BehaviorAnalysis analysis;
        public List<TelemetrySample> samples;
        public List<TelemetryEvent> events;
    }

    /// <summary>
    /// Keeps the last N seconds (default 30) of player movement, actions and inputs,
    /// derives simple behaviour indicators from them, and writes everything to a LIVE file
    /// that is refreshed every second.
    ///
    /// Player visibility: nothing is hidden. Press F9 in game to show the exact data and analysis on screen,
    /// F10 to open the folder containing the live files. The data stays on this machine.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PenitentMotor), typeof(PenitentStateMachine))]
    public class PenitentTelemetry : MonoBehaviour
    {
        [Header("Recording")]
        public bool recordingEnabled = true;
        [Range(5f, 60f)] public float windowSeconds = 30f;
        [Range(2f, 30f)] public float samplesPerSecond = 10f;

        [Header("Live file (updated while the game runs)")]
        public bool writeLiveFile = true;
        [Range(0.25f, 10f)] public float fileWriteInterval = 1f;
        public string jsonFileName = "player_behavior_live.json";
        public string summaryFileName = "player_behavior_summary.txt";

        [Header("Player-visible overlay")]
        public KeyCode toggleOverlayKey = KeyCode.F9;
        public KeyCode openFolderKey = KeyCode.F10;
        public bool overlayVisibleAtStart = false;
        public float hintSeconds = 10f;

        public bool OverlayVisible { get; set; }
        public string DirectoryPath { get; private set; }
        public string JsonFilePath => Path.Combine(DirectoryPath ?? "", jsonFileName);
        public string SummaryFilePath => Path.Combine(DirectoryPath ?? "", summaryFileName);
        public BehaviorAnalysis LastAnalysis { get; private set; }

        private static readonly string[] StateNames = Enum.GetNames(typeof(PenitentState));

        private PenitentMotor motor;
        private PenitentStateMachine sm;

        private readonly List<TelemetrySample> samples = new List<TelemetrySample>();
        private readonly List<TelemetryEvent> events = new List<TelemetryEvent>();

        private string sessionId;
        private float sessionStart;
        private float sampleTimer;
        private float writeTimer;
        private float analysisTimer;
        private float lastMove;
        private int lastDir;
        private string cachedSummary = "";
        private bool warnedWriteFailure;

        // overlay GUI
        private GUIStyle labelStyle, hintStyle;
        private Texture2D bgTexture;

        private float Now => Time.time - sessionStart;

        // ------------------------------------------------------------------
        private void Awake()
        {
            motor = GetComponent<PenitentMotor>();
            sm = GetComponent<PenitentStateMachine>();

            sessionStart = Time.time;
            sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            OverlayVisible = overlayVisibleAtStart;

            DirectoryPath = Path.Combine(Application.persistentDataPath, "PlayerTelemetry");
            try { Directory.CreateDirectory(DirectoryPath); }
            catch (Exception e) { Debug.LogWarning("[PenitentTelemetry] Cannot create folder: " + e.Message); }
        }

        private void Start()
        {
            Debug.Log("[PenitentTelemetry] Live behaviour data: " + JsonFilePath);
        }

        private void OnEnable()
        {
            if (motor == null) motor = GetComponent<PenitentMotor>();
            if (sm == null) sm = GetComponent<PenitentStateMachine>();

            motor.OnJumped += HandleJumped;
            motor.OnLanded += HandleLanded;
            motor.OnDashStarted += HandleDash;
            sm.OnAttackStarted += HandleAttack;
            sm.OnParryStarted += HandleParry;
            sm.OnWeaponSwitched += HandleWeaponSwitch;
            sm.OnHitLanded += HandleHit;
        }

        private void OnDisable()
        {
            if (motor != null)
            {
                motor.OnJumped -= HandleJumped;
                motor.OnLanded -= HandleLanded;
                motor.OnDashStarted -= HandleDash;
            }
            if (sm != null)
            {
                sm.OnAttackStarted -= HandleAttack;
                sm.OnParryStarted -= HandleParry;
                sm.OnWeaponSwitched -= HandleWeaponSwitch;
                sm.OnHitLanded -= HandleHit;
            }
        }

        private void OnDestroy()
        {
            if (bgTexture != null) Destroy(bgTexture);
        }

        private void OnApplicationQuit() { WriteFiles(); }
        private void OnApplicationPause(bool paused) { if (paused) WriteFiles(); }

        // ------------------------------------------------------------------
        // INPUT (called by PenitentPlayerController every frame)
        // ------------------------------------------------------------------
        public void RecordInput(float moveInput, bool jumpPressed, bool dodgePressed, bool attackPressed, bool parryPressed)
        {
            if (!recordingEnabled) return;

            lastMove = moveInput;

            int dir = moveInput > 0.1f ? 1 : (moveInput < -0.1f ? -1 : 0);
            if (dir != 0)
            {
                if (lastDir != 0 && dir != lastDir) LogEvent("turn", dir > 0 ? "to_right" : "to_left");
                lastDir = dir;
            }

            if (jumpPressed) LogEvent("input", "jump");
            if (dodgePressed) LogEvent("input", "dodge");
            if (attackPressed) LogEvent("input", "attack");
            if (parryPressed) LogEvent("input", "parry");
        }

        public void SetRecording(bool enabled) { recordingEnabled = enabled; }

        // ------------------------------------------------------------------
        // GAME EVENTS
        // ------------------------------------------------------------------
        private void HandleJumped() { LogEvent("jump"); }
        private void HandleLanded(float impact) { if (impact >= 3f) LogEvent("land", impact.ToString("0.0")); }
        private void HandleDash() { LogEvent("dodge"); }
        private void HandleAttack(PenitentState state, WeaponType weapon) { LogEvent("attack", state + "/" + weapon); }
        private void HandleParry(bool success) { LogEvent("parry", success ? "success" : "attempt"); }
        private void HandleWeaponSwitch(WeaponType weapon) { LogEvent("weapon_switch", weapon.ToString()); }
        private void HandleHit(PenitentHitbox hb, Collider2D other) { LogEvent("hit", other != null ? other.name : ""); }

        private void LogEvent(string type, string detail = "")
        {
            if (!recordingEnabled) return;
            events.Add(new TelemetryEvent { t = R(Now), type = type, detail = detail });
        }

        // ------------------------------------------------------------------
        // UPDATE LOOP
        // ------------------------------------------------------------------
        private void Update()
        {
            if (Input.GetKeyDown(toggleOverlayKey)) OverlayVisible = !OverlayVisible;
            if (Input.GetKeyDown(openFolderKey)) OpenDataFolder();

            float dt = Time.deltaTime;

            if (recordingEnabled)
            {
                float interval = 1f / Mathf.Max(1f, samplesPerSecond);
                sampleTimer += dt;
                if (sampleTimer > 1f) sampleTimer = interval; // after a long hitch, don't burst-sample
                while (sampleTimer >= interval)
                {
                    sampleTimer -= interval;
                    TakeSample();
                }
                Trim();
            }

            // Refresh derived analysis ~2x per second (only when someone looks at it or it is written)
            analysisTimer += dt;
            if (analysisTimer >= 0.5f)
            {
                analysisTimer = 0f;
                if (OverlayVisible || writeLiveFile)
                {
                    LastAnalysis = ComputeAnalysis();
                    cachedSummary = BuildSummaryText();
                }
            }

            if (writeLiveFile)
            {
                writeTimer += dt;
                if (writeTimer >= fileWriteInterval)
                {
                    writeTimer = 0f;
                    WriteFiles();
                }
            }
        }

        private void TakeSample()
        {
            Vector2 p = transform.position;
            Vector2 v = motor.Velocity;
            int stateIndex = Mathf.Clamp((int)sm.CurrentState, 0, StateNames.Length - 1);

            samples.Add(new TelemetrySample
            {
                t = R(Now),
                x = R(p.x),
                y = R(p.y),
                vx = R(v.x),
                vy = R(v.y),
                state = StateNames[stateIndex],
                weapon = sm.CurrentWeapon.ToString(),
                grounded = motor.IsGrounded,
                facingRight = motor.IsFacingRight,
                input = R(lastMove)
            });
        }

        private void Trim()
        {
            float cutoff = Now - windowSeconds;

            int s = 0;
            while (s < samples.Count && samples[s].t < cutoff) s++;
            if (s > 0) samples.RemoveRange(0, s);

            int e = 0;
            while (e < events.Count && events[e].t < cutoff) e++;
            if (e > 0) events.RemoveRange(0, e);
        }

        // ------------------------------------------------------------------
        // ANALYSIS
        // ------------------------------------------------------------------
        public BehaviorAnalysis ComputeAnalysis()
        {
            float span = Mathf.Clamp(Now, 1f, windowSeconds);
            var a = new BehaviorAnalysis { windowSeconds = Mathf.Round(span * 10f) / 10f };

            foreach (var e in events)
            {
                switch (e.type)
                {
                    case "jump": a.jumps++; break;
                    case "dodge": a.dodges++; break;
                    case "attack": a.attacks++; break;
                    case "parry": a.parries++; break;
                    case "weapon_switch": a.weaponSwitches++; break;
                    case "hit": a.hitsLanded++; break;
                    case "turn": a.directionChanges++; break;
                    case "input": a.buttonPresses++; break;
                }
            }

            int n = samples.Count;
            if (n > 0)
            {
                float sampleDt = 1f / Mathf.Max(1f, samplesPerSecond);
                int idle = 0, air = 0, atk = 0, def = 0;
                float dist = 0f, streak = 0f, longest = 0f;

                for (int i = 0; i < n; i++)
                {
                    var s = samples[i];

                    if (s.state == "Idle") { idle++; streak += sampleDt; if (streak > longest) longest = streak; }
                    else streak = 0f;

                    if (!s.grounded) air++;

                    switch (s.state)
                    {
                        case "Attack1": case "Attack2": case "Attack3": case "HeavyAttack": case "HeavyJumpAttack":
                            atk++; break;
                        case "Dodge": case "Parry": case "ParrySuccess":
                            def++; break;
                    }

                    if (i > 0)
                    {
                        float dx = s.x - samples[i - 1].x;
                        float dy = s.y - samples[i - 1].y;
                        dist += Mathf.Sqrt(dx * dx + dy * dy);
                    }
                }

                a.idlePercent = 100f * idle / n;
                a.airbornePercent = 100f * air / n;
                a.attackingPercent = 100f * atk / n;
                a.defensivePercent = 100f * def / n;
                a.distanceTraveled = R(dist);
                a.longestIdleSeconds = R(longest);
                a.avgSpeed = R(dist / span);
            }

            int actions = a.jumps + a.dodges + a.attacks + a.parries + a.weaponSwitches;
            a.actionsPerMinute = R(actions / span * 60f);
            a.inputsPerSecond = R(a.buttonPresses / span);

            Classify(a, span);
            return a;
        }

        private static void Classify(BehaviorAnalysis a, float span)
        {
            float attacksPerMin = a.attacks / span * 60f;
            float jumpsPerMin = a.jumps / span * 60f;
            float turnsPerSec = a.directionChanges / span;
            int defensiveActs = a.dodges + a.parries;

            if (span < 8f)
            {
                a.styleLabel = "Collecting data...";
            }
            else if (a.attackingPercent >= 25f || attacksPerMin >= 25f)
            {
                a.styleLabel = (defensiveActs > 0 && defensiveActs >= a.attacks * 0.5f)
                    ? "Aggressive but careful (attacks mixed with dodges/parries)"
                    : "Aggressive / pressure-oriented";
            }
            else if (defensiveActs >= 3 && defensiveActs > a.attacks)
            {
                a.styleLabel = "Defensive / reactive";
            }
            else if (a.idlePercent >= 60f)
            {
                a.styleLabel = "Passive / observing";
            }
            else if (turnsPerSec >= 0.5f)
            {
                a.styleLabel = "Erratic / indecisive movement";
            }
            else if (jumpsPerMin >= 20f)
            {
                a.styleLabel = "Evasive / jump-heavy";
            }
            else
            {
                a.styleLabel = "Balanced / exploring";
            }

            var notes = new List<string>();
            if (a.inputsPerSecond >= 4f) notes.Add("button mashing");
            if (a.longestIdleSeconds >= 8f) notes.Add("long pauses (hesitation or reading the situation)");
            if (a.attacks >= 5 && a.hitsLanded == 0) notes.Add("many swings but no hits connected (spacing/timing)");
            if (turnsPerSec >= 0.5f) notes.Add("frequent direction changes");
            if (a.parries >= 3) notes.Add("uses parry actively");
            a.notes = notes.Count > 0 ? string.Join("; ", notes) : "-";
        }

        // ------------------------------------------------------------------
        // FILE OUTPUT
        // ------------------------------------------------------------------
        private void WriteFiles()
        {
            if (!writeLiveFile || string.IsNullOrEmpty(DirectoryPath)) return;

            try
            {
                LastAnalysis = ComputeAnalysis();
                cachedSummary = BuildSummaryText();

                var snapshot = new TelemetrySnapshot
                {
                    sessionId = sessionId,
                    generatedAtUtc = DateTime.UtcNow.ToString("o"),
                    windowSeconds = windowSeconds,
                    samplesPerSecond = samplesPerSecond,
                    analysis = LastAnalysis,
                    samples = samples,
                    events = events
                };

                AtomicWrite(JsonFilePath, JsonUtility.ToJson(snapshot));
                AtomicWrite(SummaryFilePath, cachedSummary + "\nFile: " + JsonFilePath + "\n");
            }
            catch (Exception ex)
            {
                if (!warnedWriteFailure)
                {
                    warnedWriteFailure = true;
                    Debug.LogWarning("[PenitentTelemetry] Could not write live file: " + ex.Message);
                }
            }
        }

        // Write to a temp file then swap, so anyone reading the live file never sees a half-written one
        private static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                File.WriteAllText(path, content, Encoding.UTF8);
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        [ContextMenu("Open Data Folder")]
        public void OpenDataFolder()
        {
            if (string.IsNullOrEmpty(DirectoryPath)) return;
            WriteFiles();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(JsonFilePath);
#else
            Application.OpenURL("file://" + DirectoryPath.Replace('\\', '/'));
#endif
        }

        // ------------------------------------------------------------------
        // HUMAN-READABLE SUMMARY (same text is used for the overlay and the .txt file)
        // ------------------------------------------------------------------
        public string BuildSummaryText()
        {
            var a = LastAnalysis ?? ComputeAnalysis();
            var sb = new StringBuilder();

            sb.AppendLine("PLAYER BEHAVIOR  (live, last " + a.windowSeconds.ToString("0") + "s)");
            sb.AppendLine("Style: " + a.styleLabel);
            sb.AppendLine("Actions/min: " + a.actionsPerMinute.ToString("0") + "    Button presses/s: " + a.inputsPerSecond.ToString("0.0"));
            sb.AppendLine("Attacks " + a.attacks + "  Jumps " + a.jumps + "  Dodges " + a.dodges + "  Parries " + a.parries +
                          "  Switches " + a.weaponSwitches + "  Hits " + a.hitsLanded);
            sb.AppendLine("Idle " + a.idlePercent.ToString("0") + "%  Airborne " + a.airbornePercent.ToString("0") +
                          "%  Attacking " + a.attackingPercent.ToString("0") + "%  Defensive " + a.defensivePercent.ToString("0") + "%");
            sb.AppendLine("Distance " + a.distanceTraveled.ToString("0.0") + "  Avg speed " + a.avgSpeed.ToString("0.0") +
                          "  Turns " + a.directionChanges + "  Longest idle " + a.longestIdleSeconds.ToString("0.0") + "s");
            sb.AppendLine("Notes: " + a.notes);
            sb.AppendLine();
            sb.AppendLine("Recent events:");

            int shown = 0;
            for (int i = events.Count - 1; i >= 0 && shown < 8; i--)
            {
                var e = events[i];
                if (e.type == "input" || e.type == "turn") continue;
                sb.AppendLine("  " + e.t.ToString("0.0") + "s  " + e.type + (string.IsNullOrEmpty(e.detail) ? "" : "  (" + e.detail + ")"));
                shown++;
            }
            if (shown == 0) sb.AppendLine("  -");

            sb.AppendLine();
            sb.AppendLine("Indicators are heuristics from a short window, not a psychological assessment.");
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // OVERLAY (visible only when the player turns it on)
        // ------------------------------------------------------------------
        private void OnGUI()
        {
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = false, wordWrap = true };
                labelStyle.normal.textColor = Color.white;
                hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);

                bgTexture = new Texture2D(1, 1);
                bgTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
                bgTexture.Apply();
            }

            if (!OverlayVisible)
            {
                if (Time.time - sessionStart <= hintSeconds)
                {
                    GUI.Label(new Rect(10, Screen.height - 28, 700, 22),
                        "[" + toggleOverlayKey + "] show player behavior data   [" + openFolderKey + "] open data folder", hintStyle);
                }
                return;
            }

            const float width = 440f;
            var rect = new Rect(Screen.width - width - 10f, 10f, width, 300f);
            GUI.DrawTexture(rect, bgTexture);
            GUI.Label(new Rect(rect.x + 8, rect.y + 6, rect.width - 16, rect.height - 12), cachedSummary, labelStyle);
        }

        private static float R(float v) { return Mathf.Round(v * 100f) / 100f; }
    }
}
