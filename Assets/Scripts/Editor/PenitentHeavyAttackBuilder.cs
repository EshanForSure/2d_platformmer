#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Gives the Player its heavy-weapon (war censer) attack animations, frame by frame.
//
// Every attack is made of up to four sheets that share the same frame numbers and the same origin (the feet):
//   body    - the Penitent swinging, hands empty
//   censer  - the weapon itself
//   fire    - the flame that trails the swing
//   miasma  - the smoke that comes off the censer
// Each sheet is cut into whole frames with the json data that ships next to it, then all four are stored in one
// SpriteAnimator clip, so frame N of the body always shows with frame N of the weapon and both effects.
//
// This edits the existing Player in place (it does not rebuild it).
// Menu: Tools > Penitent > Attach Heavy Attacks
public static class PenitentHeavyAttackBuilder
{
    private const string SpriteRoot = "Assets/Export/blasphemos/Assets/#Art/Sprites/_TEMP/";
    private const string OutDir = "Assets/Penitent/Baked/Heavy";

    // Sorting order relative to the body: smoke behind the weapon, flame in front of it.
    private const int MiasmaSortingOffset = 1;
    private const int FireSortingOffset = 3;

    private class Segment
    {
        public string body, censer, fire, miasma;   // sheet names relative to SpriteRoot, without ".png"
    }

    private class ClipDef
    {
        public string clip;
        public float frameTime;
        public Segment[] segments;
    }

    // How the PlayerController plays a clip: seconds for the whole action, the frame where the blow lands,
    // the frame you may cancel from, and (optionally) how far the hit reaches.
    private class ActionSetup
    {
        public string field, clip;
        public float duration;
        public int keyFrame, cancelFrame;
        public float blendIn = 0.05f;
        public string rangeField;
        public float range;
    }

    private static readonly ClipDef[] Defs =
    {
        // The 26-frame ground swing. Wind-up 0-10 (the flaming censer spins up), arc over the head 11-12, it crashes down in
        // front of the feet on 14, the flame shockwave rolls along the ground 15-18, recovery 19-25.
        new ClipDef
        {
            clip = "heavy_slash", frameTime = 0.027f,
            segments = new[]
            {
                new Segment
                {
                    body = "TPO_WPN01_groundedAttackRevamp_armor00",
                    censer = "TPO_WPN01_groundedAttackRevamp_censer",
                    fire = "TPO_WPN01_groundedAttackRevamp_fireVFX",
                    miasma = "MiasmaCenser/TPO_WPN01_groundedAttackRevamp_MiasmaVFX",
                },
            },
        },
        // The 17-frame jump swing: arc over the head 8-10, slam on 11, fire burst 13.
        new ClipDef
        {
            clip = "heavy_air", frameTime = 0.028f,
            segments = new[]
            {
                new Segment
                {
                    body = "TPO_WPN01_jumpAttackRevamp_armor00",
                    censer = "TPO_WPN01_jumpAttackRevamp_censer",
                    fire = "TPO_WPN01_jumpAttackRevamp_fireVFX",
                    miasma = "MiasmaCenser/TPO_WPN01_jumpAttackRevamp_MiasmaVFX",
                },
            },
        },
    };

    private static readonly ActionSetup[] Actions =
    {
        // Left click: the swing at full speed.
        new ActionSetup { field = "slash", clip = "heavy_slash", duration = 0.70f, keyFrame = 14, cancelFrame = 20, rangeField = "attackRange", range = 2.0f },
        // Right click: the same swing as a slow charged slam - the spinning wind-up lasts longer, the blow reaches further.
        new ActionSetup { field = "overhead", clip = "heavy_slash", duration = 0.92f, keyFrame = 14, cancelFrame = 21, rangeField = "overheadRange", range = 2.4f },
        // Any attack started in the air.
        new ActionSetup { field = "airSlash", clip = "heavy_air", duration = 0.48f, keyFrame = 11, cancelFrame = 14 },
    };

    [MenuItem("Tools/Penitent/Attach Heavy Attacks")]
    public static void AttachFromMenu()
    {
        var log = new List<string>();
        Attach(log);
        Debug.Log("Heavy attacks:\n" + string.Join("\n", log));
    }

    public static string Attach(List<string> log)
    {
        var anim = Object.FindFirstObjectByType<SpriteAnimator>();
        if (anim == null)
        {
            log.Add("No SpriteAnimator in the open scene - open SampleScene first.");
            return string.Join("\n", log);
        }

        Directory.CreateDirectory(OutDir);

        SpriteRenderer miasma = EnsureLayerRenderer(anim, "HeavyMiasma", MiasmaSortingOffset);
        SpriteRenderer fire = EnsureLayerRenderer(anim, "HeavyFx", FireSortingOffset);

        var so = new SerializedObject(anim);
        so.FindProperty("fxTarget").objectReferenceValue = fire;
        so.FindProperty("miasmaTarget").objectReferenceValue = miasma;

        SerializedProperty clips = so.FindProperty("clips");
        var built = new Dictionary<string, int>();

        // An earlier version built a separate overhead clip; it is gone now (the right click reuses the slash clip).
        RemoveClip(clips, "heavy_overhead");
        foreach (string stale in AssetDatabase.FindAssets("heavy_overhead", new[] { OutDir }))
        {
            AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(stale));
        }

        foreach (ClipDef def in Defs)
        {
            var body = new List<Sprite>();
            var censer = new List<Sprite>();
            var fx = new List<Sprite>();
            var smoke = new List<Sprite>();

            for (int s = 0; s < def.segments.Length; s++)
            {
                Segment seg = def.segments[s];
                string tag = def.clip + "_" + s;
                Sprite[] b = Slice(seg.body, tag + "_body", log);
                if (b == null || b.Length == 0)
                {
                    log.Add("SKIPPED " + def.clip + ": no body frames for " + seg.body);
                    body.Clear();
                    break;
                }

                // Every layer is looked up by frame number so a missing frame leaves a gap instead of shifting the rest.
                Sprite[] c = Slice(seg.censer, tag + "_censer", log);
                Sprite[] f = Slice(seg.fire, tag + "_fire", log);
                Sprite[] m = Slice(seg.miasma, tag + "_miasma", log);
                for (int i = 0; i < b.Length; i++)
                {
                    body.Add(b[i]);
                    censer.Add(Pick(c, i));
                    fx.Add(Pick(f, i));
                    smoke.Add(Pick(m, i));
                }
            }

            if (body.Count == 0)
            {
                continue;
            }

            SerializedProperty element = FindOrAddClip(clips, def.clip);
            element.FindPropertyRelative("name").stringValue = def.clip;
            element.FindPropertyRelative("frameTime").floatValue = def.frameTime;
            SetSprites(element.FindPropertyRelative("frames"), body);
            SetSprites(element.FindPropertyRelative("overlayFrames"), censer);
            SetSprites(element.FindPropertyRelative("fxFrames"), fx);
            SetSprites(element.FindPropertyRelative("miasmaFrames"), smoke);
            built[def.clip] = body.Count;

            log.Add(def.clip + ": " + body.Count + " frames (weapon " + censer.Count(x => x != null) + ", flame " + fx.Count(x => x != null) + ", miasma " + smoke.Count(x => x != null) + ")");
        }

        so.ApplyModifiedPropertiesWithoutUndo();

        WireController(anim, built, log);

        EditorSceneManager.MarkSceneDirty(anim.gameObject.scene);
        EditorSceneManager.SaveScene(anim.gameObject.scene);
        AssetDatabase.SaveAssets();
        return string.Join("\n", log);
    }

    // Points the controller's attack actions at the new clips, with timings that fit their frame counts.
    private static void WireController(SpriteAnimator anim, Dictionary<string, int> built, List<string> log)
    {
        var pc = anim.GetComponent<PlayerController>();
        if (pc == null)
        {
            log.Add("No PlayerController next to the SpriteAnimator.");
            return;
        }

        var pso = new SerializedObject(pc);
        foreach (ActionSetup a in Actions)
        {
            if (!built.TryGetValue(a.clip, out int frames))
            {
                log.Add("No clip '" + a.clip + "' - " + a.field + " left unchanged.");
                continue;
            }

            SerializedProperty p = pso.FindProperty(a.field);
            if (p == null)
            {
                log.Add("PlayerController has no field '" + a.field + "'.");
                continue;
            }

            p.FindPropertyRelative("clip").stringValue = a.clip;
            p.FindPropertyRelative("startFrame").intValue = 0;
            p.FindPropertyRelative("endFrame").intValue = frames - 1;
            p.FindPropertyRelative("duration").floatValue = a.duration;
            p.FindPropertyRelative("keyFrame").intValue = Mathf.Clamp(a.keyFrame, 0, frames - 1);
            p.FindPropertyRelative("cancelFrame").intValue = Mathf.Clamp(a.cancelFrame, 0, frames - 1);
            p.FindPropertyRelative("blendIn").floatValue = a.blendIn;

            // The censer's arc is much bigger than the rosary blade's: the hit reach follows the picture.
            if (!string.IsNullOrEmpty(a.rangeField))
            {
                pso.FindProperty(a.rangeField).floatValue = a.range;
            }
        }

        pso.ApplyModifiedPropertiesWithoutUndo();
        log.Add("PlayerController: slash / overhead / airSlash now play the heavy clips.");
    }

    // ------------------------------------------------------------------ helpers

    private static Sprite Pick(Sprite[] set, int index)
    {
        return set != null && index >= 0 && index < set.Length ? set[index] : null;
    }

    private static void SetSprites(SerializedProperty array, List<Sprite> sprites)
    {
        // A layer with no sprite at all is left empty, so the animator hides it for this clip.
        bool any = sprites.Any(s => s != null);
        array.arraySize = any ? sprites.Count : 0;
        if (!any)
        {
            return;
        }

        for (int i = 0; i < sprites.Count; i++)
        {
            array.GetArrayElementAtIndex(i).objectReferenceValue = sprites[i];
        }
    }

    private static void RemoveClip(SerializedProperty clips, string name)
    {
        for (int i = clips.arraySize - 1; i >= 0; i--)
        {
            if (clips.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue == name)
            {
                clips.DeleteArrayElementAtIndex(i);
            }
        }
    }

    private static SerializedProperty FindOrAddClip(SerializedProperty clips, string name)
    {
        for (int i = 0; i < clips.arraySize; i++)
        {
            SerializedProperty e = clips.GetArrayElementAtIndex(i);
            if (e.FindPropertyRelative("name").stringValue == name)
            {
                return e;
            }
        }

        clips.arraySize++;
        return clips.GetArrayElementAtIndex(clips.arraySize - 1);
    }

    // A child of the Player at the same position, drawn above the body.
    private static SpriteRenderer EnsureLayerRenderer(SpriteAnimator anim, string objectName, int sortingOffset)
    {
        Transform t = anim.transform.Find(objectName);
        if (t == null)
        {
            var go = new GameObject(objectName);
            Undo.RegisterCreatedObjectUndo(go, "Create " + objectName);
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
        sr.sortingOrder = body.sortingOrder + sortingOffset;
        sr.enabled = false;
        return sr;
    }

    // Cuts one sheet into whole frames using its json data. Returns the sprites ordered by frame number (no gaps allowed
    // in the numbering, because layers are matched to the body by position).
    private static Sprite[] Slice(string sheet, string tag, List<string> log)
    {
        if (string.IsNullOrEmpty(sheet))
        {
            return null;
        }

        string src = SpriteRoot + sheet + ".png";
        if (!File.Exists(src))
        {
            log.Add("MISSING " + src);
            return null;
        }

        string dir = Path.GetDirectoryName(src).Replace('\\', '/');
        string baseName = Path.GetFileNameWithoutExtension(src);

        string outPath = OutDir + "/" + tag + ".png";
        File.Copy(src, outPath, true);   // a copy, so the exported originals keep their own import settings
        AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);

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
            frames[idx] = new SpriteMetaData
            {
                name = tag + "_" + idx.ToString("000"),
                rect = new Rect(F(r.Groups[3].Value), F(r.Groups[4].Value), F(r.Groups[2].Value), F(r.Groups[1].Value)),
                alignment = (int)SpriteAlignment.Custom,
                pivot = new Vector2(F(pv.Groups[1].Value), F(pv.Groups[2].Value)),
            };
        }

        if (frames.Count == 0)
        {
            log.Add("NO FRAMES in " + baseName);
            return null;
        }

        var imp = (TextureImporter)AssetImporter.GetAtPath(outPath);
        imp.textureType = TextureImporterType.Sprite;
        imp.spriteImportMode = SpriteImportMode.Multiple;
        imp.spritePixelsPerUnit = 32f;
        imp.filterMode = FilterMode.Point;
        imp.mipmapEnabled = false;
        imp.alphaIsTransparency = true;
        imp.textureCompression = TextureImporterCompression.Uncompressed;
        imp.maxTextureSize = 4096;
        imp.spritesheet = frames.Values.ToArray();
        imp.SaveAndReimport();

        var byIndex = new SortedDictionary<int, Sprite>();
        foreach (Sprite sp in AssetDatabase.LoadAllAssetsAtPath(outPath).OfType<Sprite>())
        {
            Match mm = Regex.Match(sp.name, "_(\\d+)$");
            if (mm.Success) byIndex[int.Parse(mm.Groups[1].Value)] = sp;
        }

        int max = byIndex.Keys.Max();
        var result = new Sprite[max + 1];
        foreach (var kv in byIndex) result[kv.Key] = kv.Value;
        return result;
    }

    private static float F(string v) { return float.Parse(v, CultureInfo.InvariantCulture); }
}
#endif
