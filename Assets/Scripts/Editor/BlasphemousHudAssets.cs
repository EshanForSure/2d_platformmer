#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Copies the Blasphemous 2 HUD art and fonts that GameHUD needs from the exported game folder into
// Assets/Resources/BlasphemousHUD, because GameHUD builds itself in code and loads them with Resources.Load.
// The copies keep the sprite slicing of the originals; only the filtering is set to crisp pixels.
// Menu: Tools > Blasphemous > Copy HUD Assets
public static class BlasphemousHudAssets
{
    private const string Art = "Assets/Export/blasphemos/Assets/#Art/";
    private const string OutDir = "Assets/Resources/BlasphemousHUD/";

    private static readonly (string src, string dst, bool sprites)[] Files =
    {
        (Art + "Sprites/UI/Hud/Player-HUD-v2-spritesheet.png", "HudV2.png", true),   // medallions, health / fervour bars
        (Art + "Sprites/UI/Hud/player-HUD-spritesheet.png",    "HudV1.png", true),   // flasks, banners
        (Art + "Fonts/ttf/Blasphemy.ttf",                       "Blasphemy.ttf", false),
        (Art + "Fonts/ttf/CompassPro.ttf",                      "CompassPro.ttf", false),
    };

    [MenuItem("Tools/Blasphemous/Copy HUD Assets")]
    public static void CopyFromMenu()
    {
        var log = new List<string>();
        Copy(log);
        Debug.Log("Blasphemous HUD assets:\n" + string.Join("\n", log));
    }

    public static void Copy(List<string> log)
    {
        Directory.CreateDirectory(OutDir);
        foreach (var f in Files)
        {
            if (!File.Exists(f.src))
            {
                log.Add("MISSING " + f.src);
                continue;
            }

            string dst = OutDir + f.dst;
            if (File.Exists(dst))
            {
                AssetDatabase.DeleteAsset(dst);
            }

            if (!AssetDatabase.CopyAsset(f.src, dst))
            {
                log.Add("FAILED to copy " + f.src);
                continue;
            }

            if (f.sprites)
            {
                var imp = (TextureImporter)AssetImporter.GetAtPath(dst);
                imp.filterMode = FilterMode.Point;
                imp.mipmapEnabled = false;
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.alphaIsTransparency = true;
                imp.SaveAndReimport();
            }

            log.Add("copied " + f.dst);
        }
        AssetDatabase.Refresh();
    }
}
#endif
