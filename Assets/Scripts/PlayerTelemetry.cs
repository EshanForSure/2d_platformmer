using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PenitentCharacter; // shared data classes: TelemetrySample, TelemetryEvent, BehaviorAnalysis, TelemetrySnapshot
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Keeps the last N seconds (default 30) of the player's movement, actions and button presses,
/// derives simple behaviour indicators, and writes them to a LIVE file refreshed every second.
///
/// Player-visible: F9 shows the exact data + analysis on screen, F10 opens the folder with the files.
/// The data survives the death/respawn scene reload, so the seconds before a death are kept.
/// Everything stays on this machine.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerController))]
public class PlayerTelemetry : MonoBehaviour
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
    public Key toggleOverlayKey = Key.F9;
    public Key openFolderKey = Key.F10;
    public bool overlayVisibleAtStart = false;
    public float hintSeconds = 10f;

    public bool OverlayVisible { get; set; }
    public string DirectoryPath { get; private set; }
    public string JsonFilePath => Path.Combine(DirectoryPath ?? "", jsonFileName);
    public string SummaryFilePath => Path.Combine(DirectoryPath ?? "", summaryFileName);
    public BehaviorAnalysis LastAnalysis { get; private set; }

    // Static so the window survives the scene reload that happens after death.
    private static readonly List<TelemetrySample> samples = new List<TelemetrySample>();
    private static readonly List<TelemetryEvent> events = new List<TelemetryEvent>();
    private static float sessionStart = -1f;
    private static string sessionId;
    private static bool overlayState;
    private static bool overlayStateSet;

    private PlayerController controller;
    private float sampleTimer, writeTimer, analysisTimer;
    private int lastDir;
    private int damageTakenInWindow;
    private string cachedSummary = "";
    private bool warnedWriteFailure;
    private float sceneStartTime;

    private GUIStyle labelStyle, hintStyle;
    private Texture2D bgTexture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        samples.Clear();
        events.Clear();
        sessionStart = -1f;
        overlayStateSet = false;
    }

    private float Now => Time.time - sessionStart;

    // ------------------------------------------------------------------
    private void Awake()
    {
        controller = GetComponent<PlayerController>();

        if (sessionStart < 0f)
        {
            sessionStart = Time.time;
            sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        }
        sceneStartTime = Time.time;

        OverlayVisible = overlayStateSet ? overlayState : overlayVisibleAtStart;

#if UNITY_EDITOR
        // In the editor the files go next to the Assets folder so they are easy to find.
        DirectoryPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "PlayerTelemetry");
#else
        DirectoryPath = Path.Combine(Application.persistentDataPath, "PlayerTelemetry");
#endif
        try { Directory.CreateDirectory(DirectoryPath); }
        catch (Exception e) { Debug.LogWarning("[PlayerTelemetry] Cannot create folder: " + e.Message); }
    }

    private void Start()
    {
        Debug.Log("[PlayerTelemetry] Live behaviour files are written to: " + DirectoryPath);
    }

    private void OnEnable()
    {
        if (controller == null) controller = GetComponent<PlayerController>();
        controller.ActionPerformed += OnAction;
        controller.InputPressed += OnInput;
    }

    private void OnDisable()
    {
        if (controller != null)
        {
            controller.ActionPerformed -= OnAction;
            controller.InputPressed -= OnInput;
        }
    }

    private void OnDestroy()
    {
        overlayState = OverlayVisible;
        overlayStateSet = true;
        if (bgTexture != null) Destroy(bgTexture);
        WriteFiles();
    }

    private void OnApplicationQuit() { WriteFiles(); }
    private void OnApplicationPause(bool paused) { if (paused) WriteFiles(); }

    // ------------------------------------------------------------------
    // EVENTS
    // ------------------------------------------------------------------
    private void OnInput(string button)
    {
        LogEvent("input", button);
    }

    private void OnAction(PlayerAction action, float value)
    {
        switch (action)
        {
            case PlayerAction.Jump: LogEvent("jump"); break;
            case PlayerAction.Land: if (value >= 3f) LogEvent("land", value.ToString("0.0")); break;
            case PlayerAction.Dash: LogEvent("dodge", "dash"); break;
            case PlayerAction.Teleport: LogEvent("dodge", "teleport"); break;
            case PlayerAction.Slash: LogEvent("attack", "Slash"); break;
            case PlayerAction.Overhead: LogEvent("attack", "Overhead"); break;
            case PlayerAction.AirAttack: LogEvent("attack", "AirAttack"); break;
            case PlayerAction.HitBoss: LogEvent("hit", "boss"); break;
            case PlayerAction.HitDecoration: LogEvent("decoration"); break;
            case PlayerAction.Damage: LogEvent("damage"); break;
            case PlayerAction.Death: LogEvent("death"); break;
        }
    }

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
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard[toggleOverlayKey].wasPressedThisFrame) OverlayVisible = !OverlayVisible;
            if (keyboard[openFolderKey].wasPressedThisFrame) OpenDataFolder();
        }

        float dt = Time.deltaTime;

        if (recordingEnabled)
        {
            // direction changes (turning around while moving)
            float m = controller.MoveInputValue;
            int dir = m > 0.1f ? 1 : (m < -0.1f ? -1 : 0);
            if (dir != 0)
            {
                if (lastDir != 0 && dir != lastDir) LogEvent("turn", dir > 0 ? "to_right" : "to_left");
                lastDir = dir;
            }

            float interval = 1f / Mathf.Max(1f, samplesPerSecond);
            sampleTimer += dt;
            if (sampleTimer > 1f) sampleTimer = interval; // after a hitch, don't burst-sample
            while (sampleTimer >= interval)
            {
                sampleTimer -= interval;
                TakeSample();
            }
            Trim();
        }

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
        Vector2 v = controller.Velocity;

        samples.Add(new TelemetrySample
        {
            t = R(Now),
            x = R(p.x),
            y = R(p.y),
            vx = R(v.x),
            vy = R(v.y),
            state = controller.StateName,
            weapon = "",
            grounded = controller.IsGrounded,
            facingRight = controller.FacingDirection > 0f,
            input = R(controller.MoveInputValue)
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
        int damageTaken = 0;

        foreach (var e in events)
        {
            switch (e.type)
            {
                case "jump": a.jumps++; break;
                case "dodge": a.dodges++; break;
                case "attack": a.attacks++; break;
                case "hit": a.hitsLanded++; break;
                case "turn": a.directionChanges++; break;
                case "input": a.buttonPresses++; break;
                case "damage": damageTaken++; break;
            }
        }
        damageTakenInWindow = damageTaken;

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
                    case "Slash": case "Overhead": case "AirAttack": atk++; break;
                    case "Dash": case "Teleport": def++; break;
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

        int actions = a.jumps + a.dodges + a.attacks;
        a.actionsPerMinute = R(actions / span * 60f);
        a.inputsPerSecond = R(a.buttonPresses / span);

        Classify(a, span, damageTaken);
        return a;
    }

    private static void Classify(BehaviorAnalysis a, float span, int damageTaken)
    {
        float attacksPerMin = a.attacks / span * 60f;
        float jumpsPerMin = a.jumps / span * 60f;
        float turnsPerSec = a.directionChanges / span;

        if (span < 8f)
        {
            a.styleLabel = "Collecting data...";
        }
        else if (a.attackingPercent >= 25f || attacksPerMin >= 25f)
        {
            a.styleLabel = (a.dodges > 0 && a.dodges >= a.attacks * 0.5f)
                ? "Aggressive but careful (attacks mixed with dodges)"
                : "Aggressive / pressure-oriented";
        }
        else if (a.dodges >= 3 && a.dodges > a.attacks)
        {
            a.styleLabel = "Defensive / evasive";
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
        if (a.attacks >= 5 && a.hitsLanded == 0) notes.Add("many swings but no hits on the boss (spacing/timing)");
        if (turnsPerSec >= 0.5f) notes.Add("frequent direction changes");
        if (damageTaken >= 3) notes.Add("took several hits (" + damageTaken + ")");
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
                Debug.LogWarning("[PlayerTelemetry] Could not write live file: " + ex.Message);
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
    // HUMAN-READABLE SUMMARY (used by the overlay and the .txt file)
    // ------------------------------------------------------------------
    public string BuildSummaryText()
    {
        var a = LastAnalysis ?? ComputeAnalysis();
        var sb = new StringBuilder();

        sb.AppendLine("PLAYER BEHAVIOR  (live, last " + a.windowSeconds.ToString("0") + "s)");
        sb.AppendLine("Style: " + a.styleLabel);
        sb.AppendLine("Actions/min: " + a.actionsPerMinute.ToString("0") + "    Button presses/s: " + a.inputsPerSecond.ToString("0.0"));
        sb.AppendLine("Attacks " + a.attacks + "  Jumps " + a.jumps + "  Dashes/Teleports " + a.dodges +
                      "  Boss hits " + a.hitsLanded + "  Damage taken " + damageTakenInWindow);
        sb.AppendLine("Idle " + a.idlePercent.ToString("0") + "%  Airborne " + a.airbornePercent.ToString("0") +
                      "%  Attacking " + a.attackingPercent.ToString("0") + "%  Dashing " + a.defensivePercent.ToString("0") + "%");
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
    // OVERLAY (only when the player turns it on)
    // ------------------------------------------------------------------
    private void OnGUI()
    {
        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            labelStyle.normal.textColor = Color.white;
            hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.8f);

            bgTexture = new Texture2D(1, 1);
            bgTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            bgTexture.Apply();
        }

        if (!OverlayVisible)
        {
            if (Time.time - sceneStartTime <= hintSeconds)
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
