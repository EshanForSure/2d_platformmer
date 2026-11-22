#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PenitentCharacter.Editor
{
    [InitializeOnLoad]
    public static class PenitentSceneSetup
    {
        static PenitentSceneSetup()
        {
            EditorApplication.delayCall += EnsurePlaygroundSceneExists;
        }

        private static void EnsurePlaygroundSceneExists()
        {
            string scenePath = "Assets/PenitentCharacter/Scenes/Penitent_Playground.unity";
            if (!System.IO.File.Exists(scenePath))
            {
                CreatePlaygroundScene();
            }
        }

        [MenuItem("Penitent/Setup Penitent Playground Scene")]
        public static void CreatePlaygroundScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1. Setup Camera
            GameObject camObj = new GameObject("Main Camera");
            camObj.tag = "MainCamera";
            Camera cam = camObj.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 6.0f;
            cam.backgroundColor = new Color(0.08f, 0.08f, 0.10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            camObj.transform.position = new Vector3(0f, 2.5f, -10f);
            camObj.AddComponent<PenitentCameraFollow>();

            // 2. Setup Lighting
            GameObject lightObj = new GameObject("Directional Light");
            Light light = lightObj.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.9f, 0.85f, 0.8f);
            light.intensity = 1.0f;
            lightObj.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // 3. Ground Platform
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.layer = LayerMask.NameToLayer("Default");
            ground.transform.position = new Vector3(0f, -1f, 0f);
            ground.transform.localScale = new Vector3(60f, 2f, 1f);
            
            Object.DestroyImmediate(ground.GetComponent<BoxCollider>());
            ground.AddComponent<BoxCollider2D>();
            
            var groundMat = new Material(Shader.Find("Sprites/Default"));
            groundMat.color = new Color(0.18f, 0.14f, 0.12f);
            ground.GetComponent<Renderer>().material = groundMat;

            // Extra testing platforms
            CreatePlatform("Platform_Low", new Vector3(-6f, 2.2f, 0f), new Vector3(6f, 0.6f, 1f));
            CreatePlatform("Platform_Mid", new Vector3(4f, 4.2f, 0f), new Vector3(6f, 0.6f, 1f));
            CreatePlatform("Platform_High", new Vector3(14f, 6.2f, 0f), new Vector3(6f, 0.6f, 1f));
            CreatePlatform("Wall_Left", new Vector3(-28f, 8f, 0f), new Vector3(2f, 20f, 1f));
            CreatePlatform("Wall_Right", new Vector3(28f, 8f, 0f), new Vector3(2f, 20f, 1f));

            // Dummy Target Dummy to test Hitboxes
            CreateDummyTarget("TargetDummy_1", new Vector3(5f, 0.9f, 0f));
            CreateDummyTarget("TargetDummy_Air", new Vector3(4f, 5.0f, 0f));

            // 4. Create Penitent Character
            GameObject player = new GameObject("Penitent_One");
            player.transform.position = new Vector3(0f, 0.1f, 0f);

            var rb = player.AddComponent<Rigidbody2D>();
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;

            var capCol = player.AddComponent<CapsuleCollider2D>();
            capCol.size = new Vector2(0.9f, 2.2f);
            capCol.offset = new Vector2(0f, 1.1f);

            var catalog = player.AddComponent<PenitentAnimationCatalog>();
            catalog.LoadAnimations();

            var motor = player.AddComponent<PenitentMotor>();
            motor.groundLayer = LayerMask.GetMask("Default");
            motor.jumpHeight = 5.2f;
            motor.RecalculateJumpPhysics();

            var sm = player.AddComponent<PenitentStateMachine>();
            player.AddComponent<PenitentPlayerController>();
            player.AddComponent<PenitentDebugHUD>();

            // Visual Child
            GameObject visual = new GameObject("VisualRoot");
            visual.transform.SetParent(player.transform);
            visual.transform.localPosition = Vector3.zero;

            var sr = visual.AddComponent<SpriteRenderer>();
            sr.material = new Material(Shader.Find("Sprites/Default"));
            visual.AddComponent<PenitentAnimationPlayer>();

            // Hitbox Children
            GameObject heavyHitboxObj = new GameObject("HeavyHitbox");
            heavyHitboxObj.transform.SetParent(player.transform);
            heavyHitboxObj.transform.localPosition = Vector3.zero;
            var hCol = heavyHitboxObj.AddComponent<BoxCollider2D>();
            hCol.isTrigger = true;
            hCol.size = new Vector2(2.5f, 2.3f);
            hCol.offset = new Vector2(1.25f, 1.15f);
            var hHitbox = heavyHitboxObj.AddComponent<PenitentHitbox>();
            hHitbox.damage = 50;
            hHitbox.knockback = 9.0f;
            sm.heavyHitbox = hHitbox;

            GameObject meleeHitboxObj = new GameObject("MeleeHitbox");
            meleeHitboxObj.transform.SetParent(player.transform);
            meleeHitboxObj.transform.localPosition = Vector3.zero;
            var mCol = meleeHitboxObj.AddComponent<BoxCollider2D>();
            mCol.isTrigger = true;
            mCol.size = new Vector2(1.8f, 1.6f);
            mCol.offset = new Vector2(0.9f, 1.0f);
            var mHitbox = meleeHitboxObj.AddComponent<PenitentHitbox>();
            mHitbox.damage = 25;
            mHitbox.knockback = 4.0f;
            sm.meleeHitbox = mHitbox;

            // Assign Camera target
            camObj.GetComponent<PenitentCameraFollow>().target = player.transform;

            // Save scene and prefab
            string dirPath = "Assets/PenitentCharacter/Scenes";
            if (!System.IO.Directory.Exists(dirPath)) System.IO.Directory.CreateDirectory(dirPath);

            string sceneFilePath = "Assets/PenitentCharacter/Scenes/Penitent_Playground.unity";
            EditorSceneManager.SaveScene(scene, sceneFilePath);

            string prefabDirPath = "Assets/PenitentCharacter/Prefabs";
            if (!System.IO.Directory.Exists(prefabDirPath)) System.IO.Directory.CreateDirectory(prefabDirPath);

            string prefabPath = "Assets/PenitentCharacter/Prefabs/Penitent_One.prefab";
            PrefabUtility.SaveAsPrefabAsset(player, prefabPath);

            Debug.Log($"[Penitent] Successfully created Playground Scene at {sceneFilePath} and Prefab at {prefabPath}!");
        }

        private static void CreatePlatform(string name, Vector3 pos, Vector3 scale)
        {
            GameObject platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
            platform.name = name;
            platform.layer = LayerMask.NameToLayer("Default");
            platform.transform.position = pos;
            platform.transform.localScale = scale;

            Object.DestroyImmediate(platform.GetComponent<BoxCollider>());
            platform.AddComponent<BoxCollider2D>();

            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = new Color(0.24f, 0.20f, 0.18f);
            platform.GetComponent<Renderer>().material = mat;
        }

        private static void CreateDummyTarget(string name, Vector3 pos)
        {
            GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            dummy.name = name;
            dummy.transform.position = pos;
            dummy.transform.localScale = new Vector3(0.8f, 1.8f, 1f);

            Object.DestroyImmediate(dummy.GetComponent<BoxCollider>());
            var col = dummy.AddComponent<BoxCollider2D>();

            var rb = dummy.AddComponent<Rigidbody2D>();
            rb.mass = 2f;
            rb.freezeRotation = true;

            var mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = new Color(0.8f, 0.25f, 0.25f);
            dummy.GetComponent<Renderer>().material = mat;
        }
    }
}
#endif
