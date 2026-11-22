#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Bakes the Penitent One's indexed-colour animation atlases (from Assets/Export) into normal coloured sprite sheets,
// then rebuilds the Player object with them.  Menu: Tools > Penitent > Build Player
public static class PenitentBuilder
{
    private const string AnimRoot = "Assets/Export/blasphemos/Assets/#Art/Animations/Penitent/IndexedAnims/";
    private const string PaletteAsset = "Assets/Export/blasphemos/Assets/#Art/Sprites/Player/Palettes/default_palette.png";
    private const string OutDir = "Assets/Penitent/Baked";

    private static readonly string[] SourceDirs =
    {
        "Player Controller Armor 00 heavy IC",
        "Base Player Controller IC",
        "Player Controller Armor 00 light IC",
        "Player Controller Armor Whip IC",
    };

    // clip name used by PlayerController, a lowercase fragment of the source png name, seconds per frame
    private static readonly (string clip, string key, float frameTime)[] Map =
    {
        ("idle",            "tpo_idle_wieldingheavyweapon",                 0.08f),
        ("walk",            "runcyclecompletewieldingheavyweapon",      0.045f),
        ("jump",            "jumpverticalsequencewieldingheavyweapon",  0.05f),
        ("sword_slash",     "wpn03_groundedcombo01",                    0.03f),
        ("overhead_attack", "wpn03_upperslash_armor00",                 0.04f),
        ("dash",            "dodge_thornedarm",                         0.025f),
        ("teleport",        "meaculpateleportv2",                       0.05f),
        ("damage",          "stagger_thornedarm_",                      0.04f),
        ("death",           "deathanticipationsequence",                0.08f),
    };

    private static Color32[] palette;

    [MenuItem("Tools/Penitent/Build Player")]
    public static string Build()
    {
        var log = new List<string>();
        LoadPalette();
        Directory.CreateDirectory(OutDir);

        var clips = new List<(string name, Sprite[] sprites, float ft)>();
        foreach (var m in Map)
        {
            string png = FindSource(m.key);
            if (png == null) { log.Add("MISSING " + m.clip + " <- " + m.key); continue; }
            Sprite[] sprites = BakeSheet(png, m.clip, log);
            if (sprites == null || sprites.Length == 0) { log.Add("NO FRAMES " + m.clip); continue; }
            clips.Add((m.clip, sprites, m.frameTime));
            log.Add(m.clip + " <- " + Path.GetFileName(png) + " frames=" + sprites.Length);
        }

        BuildPlayer(clips, log);
        PenitentWeaponBuilder.Attach(log); // heavy weapon (censer) overlay
        return string.Join("\n", log);
    }

    private static void LoadPalette()
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(File.ReadAllBytes(PaletteAsset));
        int w = tex.width;
        palette = new Color32[256];
        Color32[] px = tex.GetPixels32();
        for (int i = 0; i < 256; i++)
        {
            palette[i] = i < w ? px[i] : new Color32(0, 0, 0, 0); // row 0
        }
        Object.DestroyImmediate(tex);
    }

    private static string FindSource(string key)
    {
        foreach (string dir in SourceDirs)
        {
            string full = AnimRoot + dir;
            if (!Directory.Exists(full)) continue;
            foreach (string f in Directory.GetFiles(full, "*_IC.png"))
            {
                if (Path.GetFileName(f).ToLowerInvariant().Contains(key)) return f.Replace('\\', '/');
            }
        }
        return null;
    }

    private static Sprite[] BakeSheet(string icPath, string clip, List<string> log)
    {
        string dir = Path.GetDirectoryName(icPath).Replace('\\', '/');
        string baseName = Path.GetFileNameWithoutExtension(icPath);
        baseName = baseName.Substring(0, baseName.Length - 3); // strip "_IC"

        var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        src.LoadImage(File.ReadAllBytes(icPath));
        Color32[] s = src.GetPixels32();
        var outPx = new Color32[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            Color32 c = s[i];
            if (c.a == 0) { outPx[i] = new Color32(0, 0, 0, 0); continue; }
            Color32 p = palette[c.r];
            outPx[i] = new Color32(p.r, p.g, p.b, (byte)(p.a * c.a / 255));
        }
        var baked = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        baked.SetPixels32(outPx);
        baked.Apply();

        string outPath = OutDir + "/" + clip + ".png";
        File.WriteAllBytes(outPath, baked.EncodeToPNG());
        Object.DestroyImmediate(src);
        Object.DestroyImmediate(baked);
        AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);

        // frame data
        var frames = new SortedDictionary<int, SpriteMetaData>();
        var re = new Regex("^" + Regex.Escape(baseName) + "_(\\d+)\\.json$");
        foreach (string jf in Directory.GetFiles(dir, baseName + "_*.json"))
        {
            Match mm = re.Match(Path.GetFileName(jf));
            if (!mm.Success) continue;
            string json = File.ReadAllText(jf);
            Match r = Regex.Match(json, "\"m_Rect\":\\s*\\{\\s*\"m_Height\":\\s*([\\d.]+),\\s*\"m_Width\":\\s*([\\d.]+),\\s*\"m_X\":\\s*([\\d.]+),\\s*\"m_Y\":\\s*([\\d.]+)");
            Match pv = Regex.Match(json, "\"m_Pivot\":\\s*\\{\\s*\"m_X\":\\s*(-?[\\d.eE+-]+),\\s*\"m_Y\":\\s*(-?[\\d.eE+-]+)");
            if (!r.Success || !pv.Success) continue;
            int idx = int.Parse(mm.Groups[1].Value);
            var meta = new SpriteMetaData
            {
                name = clip + "_" + idx.ToString("000"),
                rect = new Rect(F(r.Groups[3].Value), F(r.Groups[4].Value), F(r.Groups[2].Value), F(r.Groups[1].Value)),
                alignment = (int)SpriteAlignment.Custom,
                pivot = new Vector2(F(pv.Groups[1].Value), F(pv.Groups[2].Value)),
            };
            frames[idx] = meta;
        }

        var imp = (TextureImporter)AssetImporter.GetAtPath(outPath);
        imp.textureType = TextureImporterType.Sprite;
        imp.spriteImportMode = SpriteImportMode.Multiple;
        imp.spritePixelsPerUnit = 32f;
        imp.filterMode = FilterMode.Point;
        imp.mipmapEnabled = false;
        imp.alphaIsTransparency = true;
        imp.textureCompression = TextureImporterCompression.Uncompressed;
        imp.spritesheet = frames.Values.ToArray();
        imp.SaveAndReimport();

        return AssetDatabase.LoadAllAssetsAtPath(outPath).OfType<Sprite>().OrderBy(sp => sp.name).ToArray();
    }

    private static float F(string v) { return float.Parse(v, CultureInfo.InvariantCulture); }

    private static void BuildPlayer(List<(string name, Sprite[] sprites, float ft)> clips, List<string> log)
    {
        // remove any old / missing Player
        foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (go.transform.parent == null && go.name.StartsWith("Player")) Object.DestroyImmediate(go);
        }

        var player = new GameObject("Player");
        player.transform.position = new Vector3(-6f, -3.32f, 0f);
        player.layer = 0;

        var rb = player.AddComponent<Rigidbody2D>();
        rb.gravityScale = 3f;
        rb.freezeRotation = true;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        var box = player.AddComponent<BoxCollider2D>();
        box.size = new Vector2(0.7f, 2.15f);
        box.offset = new Vector2(0f, 1.075f);

        string matPath = "Assets/Penitent/NoFriction.physicsMaterial2D";
        var mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(matPath);
        if (mat == null)
        {
            mat = new PhysicsMaterial2D("NoFriction") { friction = 0f, bounciness = 0f };
            AssetDatabase.CreateAsset(mat, matPath);
        }
        box.sharedMaterial = mat;

        var sr = player.AddComponent<SpriteRenderer>();
        sr.sortingOrder = 10;
        if (clips.Count > 0) sr.sprite = clips[0].sprites[0];

        var anim = player.AddComponent<SpriteAnimator>();
        player.AddComponent<PlayerController>();

        // animator clips
        var aso = new SerializedObject(anim);
        aso.FindProperty("target").objectReferenceValue = sr;
        SerializedProperty arr = aso.FindProperty("clips");
        arr.arraySize = clips.Count;
        for (int i = 0; i < clips.Count; i++)
        {
            SerializedProperty e = arr.GetArrayElementAtIndex(i);
            e.FindPropertyRelative("name").stringValue = clips[i].name;
            e.FindPropertyRelative("frameTime").floatValue = clips[i].ft;
            SerializedProperty fr = e.FindPropertyRelative("frames");
            fr.arraySize = clips[i].sprites.Length;
            for (int k = 0; k < clips[i].sprites.Length; k++) fr.GetArrayElementAtIndex(k).objectReferenceValue = clips[i].sprites[k];
        }
        aso.ApplyModifiedPropertiesWithoutUndo();

        // controller: ground layer + action timings that fit the Penitent's frame counts
        var pso = new SerializedObject(player.GetComponent<PlayerController>());
        int ground = LayerMask.GetMask("Ground");
        if (ground != 0) pso.FindProperty("groundLayer").intValue = ground;
        SetAction(pso, "slash", clips, "sword_slash", 0.42f, 0.45f, 0.72f);
        SetAction(pso, "overhead", clips, "overhead_attack", 0.56f, 0.5f, 0.75f);
        SetAction(pso, "dash", clips, "dash", 0.40f, 0.2f, 0.7f);
        SetAction(pso, "teleport", clips, "teleport", 0.62f, 0.55f, 0.8f);
        SetAction(pso, "damage", clips, "damage", 0.5f, 0.0f, 0.6f);
        SetAction(pso, "death", clips, "death", 1.3f, 0.0f, 1.0f);
        pso.ApplyModifiedPropertiesWithoutUndo();

        Undo.RegisterCreatedObjectUndo(player, "Create Penitent Player");
        EditorSceneManager.MarkSceneDirty(player.scene);
        EditorSceneManager.SaveScene(player.scene);
        AssetDatabase.SaveAssets();
        log.Add("Player built. ground mask=" + ground);
    }

    private static void SetAction(SerializedObject pso, string field, List<(string name, Sprite[] sprites, float ft)> clips, string clipName, float duration, float keyFrac, float cancelFrac)
    {
        var c = clips.FirstOrDefault(x => x.name == clipName);
        if (c.sprites == null) return;
        int n = c.sprites.Length;
        SerializedProperty p = pso.FindProperty(field);
        p.FindPropertyRelative("clip").stringValue = clipName;
        p.FindPropertyRelative("startFrame").intValue = 0;
        p.FindPropertyRelative("endFrame").intValue = n - 1;
        p.FindPropertyRelative("duration").floatValue = duration;
        p.FindPropertyRelative("keyFrame").intValue = Mathf.Clamp(Mathf.RoundToInt((n - 1) * keyFrac), 0, n - 1);
        p.FindPropertyRelative("cancelFrame").intValue = Mathf.Clamp(Mathf.RoundToInt((n - 1) * cancelFrac), 0, n - 1);
    }
}
#endif
