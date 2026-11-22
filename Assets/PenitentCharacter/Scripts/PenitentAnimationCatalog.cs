using System;
using System.Collections.Generic;
using UnityEngine;

namespace PenitentCharacter
{
    [Serializable]
    public class PenitentFrame
    {
        public string name;
        public Rect rect;
        public Vector2 pivot;
        public Vector2 offset;
        public float ppu = 32f;
    }

    [Serializable]
    public class PenitentAnimationData
    {
        public string animationName;
        public string textureName;
        public float fps = 12f;
        public bool loop = true;
        public List<Sprite> sprites = new List<Sprite>();
    }

    public class PenitentAnimationCatalog : MonoBehaviour
    {
        public static PenitentAnimationCatalog Instance { get; private set; }

        public readonly Dictionary<string, PenitentAnimationData> animations = new Dictionary<string, PenitentAnimationData>();

        [Header("Animation Speeds (FPS)")]
        public float idleFps = 12f;
        public float runFps = 16f;
        public float jumpFps = 14f;
        public float dodgeFps = 18f;
        public float attackFps = 16f;
        public float parryFps = 16f;
        public float heavyAttackFps = 16f;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            LoadAnimations();
        }

        public void LoadAnimations()
        {
            animations.Clear();

            // Mea Culpa (Sword) Base Set
            LoadAnimation("Idle", "TPO_idle_thornedArm_IC", idleFps, true);
            LoadAnimation("Run", "TPO_runCycleCompleteThornedArm_IC", runFps, true);
            LoadAnimation("Jump", "TPO_jumpVerticalSequenceFinal_IC", jumpFps, false);
            LoadAnimation("JumpHorizontal", "TPO_jumpHorizontalSequenceFinal_IC", jumpFps, false);
            LoadAnimation("Dodge", "TPO_dodge_thornedArm_IC", dodgeFps, false);
            LoadAnimation("Attack1", "TPO_WPN03_groundedCombo01_armor00_IC", attackFps, false);
            LoadAnimation("Attack2", "TPO_WPN03_groundedCombo02_armor00_IC", attackFps, false);
            LoadAnimation("Attack3", "TPO_WPN03_groundedCombo03_armor00_IC", attackFps, false);
            LoadAnimation("Parry", "TPO_WPN03_parry_armor00_IC", parryFps, false);
            LoadAnimation("ParrySuccess", "TPO_WPN03_parrySuccess_armor00_IC", parryFps, false);

            // Heavy Weapon (Veredicto War Censer) Set
            LoadAnimation("Heavy_Idle", "TPO_idle_wieldingHeavyWeapon_Armor00_IC", idleFps, true);
            LoadAnimation("Heavy_Run", "TPO_runCycleCompleteWieldingHeavyWeapon_Amor00_IC", runFps, true);
            LoadAnimation("Heavy_Jump", "TPO_jumpVerticalSequenceWieldingHeavyWeapon_armor00_IC", jumpFps, false);
            LoadAnimation("Heavy_JumpHorizontal", "TPO_jumpHorizontalSequenceWieldingHeavyWeapon_armor00_IC", jumpFps, false);
            LoadAnimation("Heavy_Attack", "TPO_WPN01_groundedAttackRevamp_armor00_IC", heavyAttackFps, false);
            LoadAnimation("Heavy_JumpAttack", "TPO_WPN01_jumpAttackRevamp_armor00_IC", heavyAttackFps, false);
            LoadAnimation("Heavy_Charged", "TPO_WPN01_chargedHammer_armor00_IC", heavyAttackFps, false);
        }

        private void LoadAnimation(string animName, string textureName, float fps, bool loop)
        {
            string path = $"PenitentCharacter/Sprites/{textureName}";
            Sprite[] loadedSprites = Resources.LoadAll<Sprite>(path);

            if (loadedSprites == null || loadedSprites.Length == 0)
            {
#if UNITY_EDITOR
                string assetPath = $"Assets/PenitentCharacter/Sprites/{textureName}.png";
                var assets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(assetPath);
                var list = new List<Sprite>();
                foreach (var obj in assets)
                {
                    if (obj is Sprite s) list.Add(s);
                }
                loadedSprites = list.ToArray();
#endif
            }

            if (loadedSprites != null && loadedSprites.Length > 0)
            {
                // Sort naturally by frame number
                Array.Sort(loadedSprites, (a, b) =>
                {
                    int numA = ExtractTrailingNumber(a.name);
                    int numB = ExtractTrailingNumber(b.name);
                    return numA.CompareTo(numB);
                });

                var animData = new PenitentAnimationData
                {
                    animationName = animName,
                    textureName = textureName,
                    fps = fps,
                    loop = loop,
                    sprites = new List<Sprite>(loadedSprites)
                };

                animations[animName] = animData;
            }
        }

        private int ExtractTrailingNumber(string name)
        {
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore >= 0 && int.TryParse(name.Substring(lastUnderscore + 1), out int n))
            {
                return n;
            }
            return 0;
        }

        public PenitentAnimationData GetAnimation(string animName)
        {
            if (animations.TryGetValue(animName, out var anim))
                return anim;
            return null;
        }
    }
}
