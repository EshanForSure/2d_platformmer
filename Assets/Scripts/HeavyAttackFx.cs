using UnityEngine;

// Impact effects for the heavy censer swings, built in code so there is nothing to wire up in the scene:
//   HitStop - freezes the game for a few hundredths of a second when a blow connects (the classic "weight" trick)
//   Embers  - a burst of glowing sparks thrown off along the swing
//   Dust    - a puff of ground dust kicked up in front of the player
public class HeavyAttackFx : MonoBehaviour
{
    private const float StopScale = 0.02f;

    private static HeavyAttackFx instance;
    private static Material particleMaterial;

    private float stopUntil;
    private bool stopping;

    private static HeavyAttackFx Instance
    {
        get
        {
            if (instance == null)
            {
                var go = new GameObject("HeavyAttackFx");
                instance = go.AddComponent<HeavyAttackFx>();
            }
            return instance;
        }
    }

    private static Material ParticleMaterial
    {
        get
        {
            if (particleMaterial == null)
            {
                particleMaterial = new Material(Shader.Find("Sprites/Default")) { name = "HeavyAttackFxParticles" };
            }
            return particleMaterial;
        }
    }

    private static Material dustMaterial;

    // Dust is a soft round puff (embers stay crisp pixels to match the art). The texture is made once, in code.
    private static Material DustMaterial
    {
        get
        {
            if (dustMaterial == null)
            {
                const int n = 32;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
                {
                    name = "DustPuff",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n * 0.5f, n * 0.5f)) / (n * 0.5f);
                        float alpha = Mathf.Clamp01(1f - d);
                        alpha = alpha * alpha * (3f - 2f * alpha);   // smoothstep: soft edge
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                    }
                }
                tex.Apply();
                dustMaterial = new Material(Shader.Find("Sprites/Default")) { name = "HeavyAttackFxDust", mainTexture = tex };
            }
            return dustMaterial;
        }
    }

    // ---------------------------------------------------------------- hit stop

    // Slows time almost to a halt for `seconds` of real time. Overlapping calls extend the stop instead of stacking.
    public static void HitStop(float seconds)
    {
        if (seconds <= 0f)
        {
            return;
        }

        HeavyAttackFx fx = Instance;
        fx.stopUntil = Mathf.Max(fx.stopUntil, Time.unscaledTime + seconds);
        fx.stopping = true;
        Time.timeScale = StopScale;
    }

    private void Update()
    {
        if (stopping && Time.unscaledTime >= stopUntil)
        {
            stopping = false;
            Time.timeScale = 1f;
        }
    }

    // Never leave the game frozen: if this object goes away mid-stop (scene reload), time goes back to normal.
    private void OnDestroy()
    {
        if (stopping)
        {
            Time.timeScale = 1f;
        }
        if (instance == this)
        {
            instance = null;
        }
    }

    // --------------------------------------------------------------- particles

    // A burst of orange-yellow sparks flying out in `direction` (+1 right, -1 left as a vector's x).
    public static void Embers(Vector2 position, Vector2 direction, float strength = 1f)
    {
        float side = direction.x >= 0f ? 1f : -1f;
        ParticleSystem ps = CreateSystem("Embers", position, side);

        var main = ps.main;
        main.duration = 0.4f;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.65f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(3f * strength, 9f * strength);
        main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.14f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.55f, 0.1f, 1f), new Color(1f, 0.9f, 0.4f, 1f));
        main.gravityModifier = 0.7f;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Clamp(Mathf.RoundToInt(22f * strength), 6, 60)) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 38f;
        shape.radius = 0.1f;
        shape.rotation = new Vector3(0f, 90f * side, 0f);   // cones point along +Z; turn it to face along the swing

        FadeAndShrink(ps, 1f, 0f);
        ps.Play();
    }

    // A low puff of brownish dust drifting up from the ground.
    public static void Dust(Vector2 position, Vector2 direction, float strength = 1f)
    {
        float side = direction.x >= 0f ? 1f : -1f;
        ParticleSystem ps = CreateSystem("Dust", position, side);
        ps.GetComponent<ParticleSystemRenderer>().sharedMaterial = DustMaterial;

        var main = ps.main;
        main.duration = 0.4f;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.65f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 2.2f * strength);
        main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.38f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.47f, 0.38f, 0.55f), new Color(0.75f, 0.68f, 0.58f, 0.4f));
        main.gravityModifier = -0.08f;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Clamp(Mathf.RoundToInt(9f * strength), 4, 24)) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 22f;
        shape.radius = 0.25f;
        shape.rotation = new Vector3(-12f, 90f * side, 0f);  // low, slightly upward

        FadeAndShrink(ps, 1f, 1.8f);   // dust grows as it fades instead of shrinking
        ps.Play();
    }

    // Soft green-gold motes that float up around the player when they heal.
    public static void HealGlow(Vector2 position)
    {
        ParticleSystem ps = CreateSystem("HealGlow", position, 1f);
        ps.GetComponent<ParticleSystemRenderer>().sharedMaterial = DustMaterial;
        var main = ps.main;
        main.duration = 0.5f;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.4f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.28f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.3f, 1f, 0.5f, 0.9f), new Color(1f, 0.95f, 0.5f, 0.9f));
        main.gravityModifier = -0.15f;
        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)26) });
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.55f;
        shape.rotation = new Vector3(90f, 0f, 0f);
        FadeAndShrink(ps, 1f, 0.2f);
        ps.Play();
    }

    private static ParticleSystem CreateSystem(string label, Vector2 position, float side)
    {
        var go = new GameObject(label);
        go.transform.position = new Vector3(position.x, position.y, 0f);

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction = ParticleSystemStopAction.Destroy;   // cleans itself up once every particle has died

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = ParticleMaterial;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortingOrder = 20;   // in front of the body (10) and all of its weapon / flame layers
        return ps;
    }

    private static void FadeAndShrink(ParticleSystem ps, float startSize, float endSize)
    {
        var colour = ps.colorOverLifetime;
        colour.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
        colour.color = gradient;

        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, startSize, 1f, endSize));
    }
}
