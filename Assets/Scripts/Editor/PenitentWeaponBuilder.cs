#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Gives the Player its heavy weapon (the censer).
//
// The body sheets for idle / run / jump show the Penitent holding the weapon with empty hands: the censer is a separate
// sheet. Unity's automatic slicing breaks that sheet into loose fragments, so instead each frame is cut out using the
// frame data that ships next to it (<sheet>_<n>.json: rect + pivot). Body and weapon frames share the same origin (the
// feet), so a weapon sprite drawn at the player's position lines up with the body without any extra offset.
//
// The weapon is shown by SpriteAnimator's overlay layer, so it plays exactly the frame of the body animation.
// Menu: Tools > Penitent > Attach Heavy Weapon   (also runs automatically at the end of Tools > Penitent > Build Player)
public static class PenitentWeaponBuilder
{
    private const string SpriteRoot = "Assets/Export/blasphemos/Assets/#Art/Sprites/_TEMP/";
    private const string OutDir = "Assets/Penitent/Baked";
    private const string WeaponObjectName = "HeavyWeapon";
    private const int WeaponSortingOffset = 2; // above the body (+0) and the body's cross-dissolve ghost (+1)

    // Player clip -> censer sheet (no extension). Only the clips that are baked from a heavy-weapon body sheet have a censer.
    private static readonly (string clip, string sheet)[] Map =
    {
        ("idle", "TPO_idle_wieldingHeavyWeapon_censer"),
        ("walk", "TPO_runCycleCompleteWieldingHeavyWeapon_censer"),
        ("jump", "TPO_jumpVerticalSequenceWieldingHeavyWeapon_censer"),
    };

    [MenuItem("Tools/Penitent/Attach Heavy Weapon")]
    public static void AttachFromMenu()
    {
        var log = new List<string>();
        Attach(log);
        Debug.Log("Heavy weapon:\n" + string.Join("\n", log));
    }

    public static void Attach(List<string> log)
    {
        var anim = Object.FindFirstObjectByType<SpriteAnimator>();
        if (anim == null)
        {
            log.Add("Heavy weapon: no SpriteAnimator in the open scene - build the Player first.");
            return;
        }

        var sprites = new Dictionary<string, Dictionary<int, Sprite>>();
        foreach (var m in Map)
        {
            Dictionary<int, Sprite> set = SliceSheet(m.clip, m.sheet, log);
            if (set != null && set.Count > 0)
            {
                sprites[m.clip] = set;
            }
        }

        SpriteRenderer weapon = EnsureWeaponRenderer(anim);
        FillOverlayFrames(anim, weapon, sprites, log);

        EditorSceneManager.MarkSceneDirty(anim.gameObject.scene);
        EditorSceneManager.SaveScene(anim.gameObject.scene);
        AssetDatabase.SaveAssets();
    }

    // Cuts one censer sheet into whole frames using its json data. Returns frame number -> sprite.
    private static Dictionary<int, Sprite> SliceSheet(string clip, string sheet, List<string> log)
    {
        string src = SpriteRoot + sheet + ".png";
        if (!File.Exists(src))
        {
            log.Add("MISSING " + src);
            return null;
        }

        Directory.CreateDirectory(OutDir);
        string outPath = OutDir + "/censer_" + clip + ".png";
        File.Copy(src, outPath, true); // a copy, so the exported originals keep their own import settings
        AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);

        var frames = new SortedDictionary<int, SpriteMetaData>();
        var re = new Regex("^" + Regex.Escape(sheet) + "_(\\d+)\\.json$");
        foreach (string jf in Directory.GetFiles(SpriteRoot, sheet + "_*.json"))
        {
            Match mm = re.Match(Path.GetFileName(jf));
            if (!mm.Success) continue;

            string json = File.ReadAllText(jf);
            Match r = Regex.Match(json, "\"m_Rect\":\\s*\\{\\s*\"m_Height\":\\s*([\\d.]+),\\s*\"m_Width\":\\s*([\\d.]+),\\s*\"m_X\":\\s*([\\d.]+),\\s*\"m_Y\":\\s*([\\d.]+)");
            Match pv = Regex.Match(json, "\"m_Pivot\":\\s*\\{\\s*\"m_X\":\\s*(-?[\\d.eE+-]+),\\s*\"m_Y\":\\s*(-?[\\d.eE+-]+)");
            if (!r.Success || !pv.Success) continue;

            int idx = int.Parse(mm.Groups[1].Value);
            frames[idx] = new SpriteMetaData
            {
                name = "censer_" + clip + "_" + idx.ToString("000"),
                rect = new Rect(F(r.Groups[3].Value), F(r.Groups[4].Value), F(r.Groups[2].Value), F(r.Groups[1].Value)),
                alignment = (int)SpriteAlignment.Custom,
                pivot = new Vector2(F(pv.Groups[1].Value), F(pv.Groups[2].Value)),
            };
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

        var result = new Dictionary<int, Sprite>();
        foreach (Sprite sp in AssetDatabase.LoadAllAssetsAtPath(outPath).OfType<Sprite>())
        {
            Match mm = Regex.Match(sp.name, "_(\\d+)$");
            if (mm.Success) result[int.Parse(mm.Groups[1].Value)] = sp;
        }

        log.Add("censer " + clip + " <- " + sheet + " frames=" + result.Count);
        return result;
    }

    private static float F(string v) { return float.Parse(v, CultureInfo.InvariantCulture); }

    // A child of the Player at the same position, drawn just above the body.
    private static SpriteRenderer EnsureWeaponRenderer(SpriteAnimator anim)
    {
        Transform t = anim.transform.Find(WeaponObjectName);
        if (t == null)
        {
            var go = new GameObject(WeaponObjectName);
            Undo.RegisterCreatedObjectUndo(go, "Create Heavy Weapon");
            go.transform.SetParent(anim.transform, false);
            t = go.transform;
        }

        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;

        SpriteRenderer sr = t.GetComponent<SpriteRenderer>();
        if (sr == null)
        {
            sr = t.gameObject.AddComponent<SpriteRenderer>();
        }

        SpriteRenderer body = anim.GetComponent<SpriteRenderer>();
        sr.sharedMaterial = body.sharedMaterial;
        sr.sortingLayerID = body.sortingLayerID;
        sr.sortingOrder = body.sortingOrder + WeaponSortingOffset;
        return sr;
    }

    // overlayFrames[k] = the censer frame with the same frame number as the body frame frames[k]
    // (so the trimmed walk loop, which starts at frame 8, picks the right weapon frames).
    private static void FillOverlayFrames(SpriteAnimator anim, SpriteRenderer weapon, Dictionary<string, Dictionary<int, Sprite>> sprites, List<string> log)
    {
        var so = new SerializedObject(anim);
        so.FindProperty("overlayTarget").objectReferenceValue = weapon;

        SerializedProperty clips = so.FindProperty("clips");
        for (int i = 0; i < clips.arraySize; i++)
        {
            SerializedProperty e = clips.GetArrayElementAtIndex(i);
            string clipName = e.FindPropertyRelative("name").stringValue;
            SerializedProperty frames = e.FindPropertyRelative("frames");
            SerializedProperty overlay = e.FindPropertyRelative("overlayFrames");

            Dictionary<int, Sprite> set;
            if (!sprites.TryGetValue(clipName, out set))
            {
                overlay.arraySize = 0; // no heavy weapon in this clip
                continue;
            }

            overlay.arraySize = frames.arraySize;
            int matched = 0;
            for (int k = 0; k < frames.arraySize; k++)
            {
                var body = frames.GetArrayElementAtIndex(k).objectReferenceValue as Sprite;
                Sprite w = null;
                if (body != null)
                {
                    Match mm = Regex.Match(body.name, "_(\\d+)$");
                    if (mm.Success) set.TryGetValue(int.Parse(mm.Groups[1].Value), out w);
                }

                overlay.GetArrayElementAtIndex(k).objectReferenceValue = w;
                if (w != null) matched++;
            }

            log.Add(clipName + ": weapon on " + matched + "/" + frames.arraySize + " frames");
        }

        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
#endif
