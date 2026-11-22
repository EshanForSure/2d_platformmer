using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// The player's heads-up display, built in code (no scene setup) and drawn with the Blasphemous 2 HUD art.
// It appears by itself in any scene that has a PlayerController and shows, top-left:
//   MEDALLION - the heavy-weapon (censer) medallion, like the weapon medallion in the game
//   HEALTH    - the ornate gold frame with the red bar; a gold "chip" trails behind to show what you just lost
//   FERVOR    - fills as you land hits; every notch is one heal's worth
//   FLASKS    - red flasks that fill up as fervor builds; a full flask is one heal you can use (R / left shoulder)
// plus a red flash when you are hit, a green flash when you heal, a pulse at low health and a "YOU DIED" banner.
//
// The art is loaded from Resources/BlasphemousHUD (copied from the exported game by
// Tools > Blasphemous > Copy HUD Assets). Sprites are looked up by their number in the sheet.
public class GameHUD : MonoBehaviour
{
    // canvas units per art pixel (the canvas is 1920x1080, so 3 = the game's chunky pixel-art look)
    private const float S = 3f;
    private const string Dir = "BlasphemousHUD/";

    // Player-HUD-v2 sheet ("HudV2")
    private const int Medallion = 49;     // censer medallion with the cherub
    private const int HealthFrame = 67;   // ornate gold bar frame
    private const int HealthFill = 68;    // red bar
    private const int FervorFrame = 89;   // smaller frame
    private const int FervorFill = 71;    // pink bar
    private const int FervorBack = 72;    // dark purple bar behind it
    // player-HUD sheet ("HudV1")
    private const int FlaskFull = 5;      // red flask
    private const int FlaskEmpty = 23;    // dark flask
    private const int Banner = 62;        // wide dark banner with gold lines

    private static readonly Color Parchment = new Color(0.93f, 0.88f, 0.76f, 1f);
    private static readonly Color Chip = new Color(1f, 0.78f, 0.35f, 0.95f);
    private static readonly Color LowTint = new Color(1f, 0.55f, 0.45f, 1f);
    private static readonly Color ReadyTint = new Color(0.65f, 0.95f, 1f, 1f);
    private static readonly Color FlaskPart = new Color(0.62f, 0.62f, 0.62f, 1f);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        if (FindFirstObjectByType<GameHUD>() == null)
        {
            new GameObject("GameHUD").AddComponent<GameHUD>();
        }
    }

    private PlayerController player;
    private Canvas canvas;
    private RectTransform root;
    private Font textFont, titleFont;
    private Dictionary<int, Sprite> v2, v1;

    private Image healthFill, healthChip, fervorFill, damageFlash, healFlash, deathShade, banner;
    private RectTransform healthTicks, fervorTicks, flaskHolder;
    private Text deathText;
    private Image[] flaskFill;
    private Vector2 healthFillSize, fervorFillSize;

    private float chipHealth = -1f;
    private float lastHealth = -1f;
    private float damageAlpha, healAlpha, deathAlpha;
    private int builtHealthSegments = -1, builtFlasks = -1;
    private float searchTimer;

    // ------------------------------------------------------------------ build

    private void Awake()
    {
        v2 = LoadSheet(Dir + "HudV2");
        v1 = LoadSheet(Dir + "HudV1");
        Font builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        textFont = Resources.Load<Font>(Dir + "CompassPro") ?? builtin;
        titleFont = Resources.Load<Font>(Dir + "Blasphemy") ?? textFont;

        var go = new GameObject("HUDCanvas");
        go.transform.SetParent(transform, false);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        BuildScreenEffects(go.transform);

        root = NewRect("Panel", go.transform);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(0f, 1f);
        root.anchoredPosition = new Vector2(44f, -34f);
        root.sizeDelta = new Vector2(700f, 190f);

        // HEALTH: frame first, then the chip and the red fill inside it (the bar slides out from behind the medallion)
        Sprite hFrame = Sp(v2, HealthFrame), hFill = Sp(v2, HealthFill);
        Vector2 hFrameSize = SizeOf(hFrame);
        healthFillSize = SizeOf(hFill);
        Vector2 hPos = new Vector2(150f, -22f);
        Vector2 hInner = hPos + new Vector2((hFrameSize.x - healthFillSize.x) * 0.5f, -(hFrameSize.y - healthFillSize.y) * 0.5f);
        Place("HealthFrame", root, hFrame, hPos, hFrameSize);
        healthChip = Place("Chip", root, hFill, hInner, healthFillSize);
        healthChip.color = Chip;
        MakeFilled(healthChip);
        healthFill = Place("HealthFill", root, hFill, hInner, healthFillSize);
        MakeFilled(healthFill);
        healthTicks = NewRect("HealthTicks", root);
        PlaceRect(healthTicks, hInner, healthFillSize);

        // FERVOR
        Sprite fFrame = Sp(v2, FervorFrame);
        Vector2 fFrameSize = SizeOf(fFrame);
        fervorFillSize = new Vector2(fFrameSize.x - 2f * S, 6f * S);
        Vector2 fPos = new Vector2(162f, -72f);
        Vector2 fInner = fPos + new Vector2((fFrameSize.x - fervorFillSize.x) * 0.5f, -(fFrameSize.y - fervorFillSize.y) * 0.5f);
        Place("FervorFrame", root, fFrame, fPos, fFrameSize);
        Place("FervorBack", root, Sp(v2, FervorBack), fInner, fervorFillSize);
        fervorFill = Place("FervorFill", root, Sp(v2, FervorFill), fInner, fervorFillSize);
        MakeFilled(fervorFill);
        fervorTicks = NewRect("FervorTicks", root);
        PlaceRect(fervorTicks, fInner, fervorFillSize);

        // FLASKS (built when the player is found, they depend on its settings)
        flaskHolder = NewRect("Flasks", root);
        flaskHolder.anchorMin = flaskHolder.anchorMax = flaskHolder.pivot = new Vector2(0f, 1f);
        flaskHolder.anchoredPosition = new Vector2(205f, -118f);

        // The medallion goes on top, so the bars appear to come out from behind it.
        Sprite medal = Sp(v2, Medallion);
        Place("Medallion", root, medal, Vector2.zero, SizeOf(medal));

        // Controls hint, bottom-left
        var hint = MakeText("Hint", go.transform, "Click: swing    Right click: charged slam    Shift: dash    E: teleport    R: heal", 20, TextAnchor.LowerLeft, Vector2.zero, new Vector2(1300f, 30f), textFont);
        hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = hint.rectTransform.pivot = new Vector2(0f, 0f);
        hint.rectTransform.anchoredPosition = new Vector2(44f, 22f);
        hint.color = new Color(Parchment.r, Parchment.g, Parchment.b, 0.55f);
    }

    // Full-screen flashes and the death screen sit behind the panel but over the game.
    private void BuildScreenEffects(Transform parent)
    {
        damageFlash = MakeFullscreen("DamageFlash", parent, new Color(0.9f, 0.05f, 0.05f, 0f));
        healFlash = MakeFullscreen("HealFlash", parent, new Color(0.3f, 1f, 0.5f, 0f));
        damageFlash.sprite = VignetteSprite();   // flashes tint the screen edges, not the whole picture
        healFlash.sprite = VignetteSprite();
        deathShade = MakeFullscreen("DeathShade", parent, new Color(0f, 0f, 0f, 0f));

        // the death banner: the game's dark banner with gold lines, scaled up 4x, with the title over it
        Sprite b = Sp(v1, Banner);
        Vector2 size = b != null ? b.rect.size * 4f : new Vector2(1476f, 180f);
        var rt = NewRect("DeathBanner", parent);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
        banner = rt.gameObject.AddComponent<Image>();
        banner.sprite = b;
        banner.color = new Color(1f, 1f, 1f, 0f);
        banner.raycastTarget = false;

        deathText = MakeText("DeathText", parent, "YOU DIED", 110, TextAnchor.MiddleCenter, Vector2.zero, new Vector2(1200f, 200f), titleFont);
        var r = deathText.rectTransform;
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = Vector2.zero;
        deathText.color = new Color(0.8f, 0.1f, 0.12f, 0f);
    }

    private void BuildPlayerWidgets()
    {
        // one notch on the health bar for every point of health
        int segments = Mathf.Max(1, player.MaxHealth);
        if (segments != builtHealthSegments)
        {
            builtHealthSegments = segments;
            Clear(healthTicks);
            for (int i = 1; i < segments; i++)
            {
                MakeTick(healthTicks, healthFillSize.x * i / segments, healthFillSize.y, new Color(0f, 0f, 0f, 0.55f));
            }
        }

        int flasks = Mathf.Max(1, player.MaxHealCharges);
        if (flasks != builtFlasks)
        {
            builtFlasks = flasks;
            Clear(flaskHolder);

            Sprite full = Sp(v1, FlaskFull), empty = Sp(v1, FlaskEmpty);
            Vector2 size = SizeOf(full);
            float step = size.x + 3f * S;
            flaskHolder.sizeDelta = new Vector2(flasks * step, size.y);
            flaskFill = new Image[flasks];
            for (int i = 0; i < flasks; i++)
            {
                Place("Empty", flaskHolder, empty, new Vector2(i * step, 0f), size);
                var fill = Place("Fill", flaskHolder, full, new Vector2(i * step, 0f), size);
                fill.type = Image.Type.Filled;
                fill.fillMethod = Image.FillMethod.Vertical;
                fill.fillOrigin = (int)Image.OriginVertical.Bottom;
                fill.fillAmount = 0f;
                flaskFill[i] = fill;
            }

            // a notch on the fervor bar where each heal's worth ends
            Clear(fervorTicks);
            for (int i = 1; i <= flasks; i++)
            {
                float x = fervorFillSize.x * Mathf.Clamp01(player.HealCost * i / Mathf.Max(1f, player.MaxPower));
                if (x >= fervorFillSize.x - 2f) continue;
                MakeTick(fervorTicks, x, fervorFillSize.y, new Color(1f, 1f, 1f, 0.55f));
            }
        }
    }

    // Clear in the middle, solid at the edges.
    private static Sprite vignetteSprite;
    private static Sprite VignetteSprite()
    {
        if (vignetteSprite != null) return vignetteSprite;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "Vignette" };
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n * 0.5f, n * 0.5f)) / (n * 0.5f);
                float a = Mathf.Clamp01((d - 0.72f) / 0.55f);
                a = a * a * (3f - 2f * a);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        tex.Apply();
        vignetteSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        return vignetteSprite;
    }

    // ----------------------------------------------------------------- update

    private void Update()
    {
        Refresh(Time.unscaledDeltaTime);
    }

    // Public so it can be driven by hand (tests).
    public void Refresh(float dt)
    {
        if (player == null)
        {
            searchTimer -= dt;
            if (searchTimer <= 0f)
            {
                searchTimer = 0.5f;
                player = FindFirstObjectByType<PlayerController>();
            }
            canvas.enabled = player != null;
            if (player == null) return;
        }

        canvas.enabled = true;
        BuildPlayerWidgets();

        float max = Mathf.Max(1, player.MaxHealth);
        float hp = player.CurrentHealth;
        if (lastHealth < 0f) { lastHealth = hp; chipHealth = hp; }

        if (hp < lastHealth) damageAlpha = 0.5f;
        if (hp > lastHealth) healAlpha = 0.4f;
        lastHealth = hp;

        // the chip waits a moment, then drains down to the real value
        if (chipHealth > hp) chipHealth = Mathf.MoveTowards(chipHealth, hp, dt * max * 0.6f);
        else chipHealth = hp;

        float frac = Mathf.Clamp01(hp / max);
        healthFill.fillAmount = frac;
        healthChip.fillAmount = Mathf.Clamp01(chipHealth / max);
        bool low = frac <= 0.34f && hp > 0f;
        float pulse = low ? 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f) : 0f;
        healthFill.color = Color.Lerp(Color.white, LowTint, pulse);

        // fervor
        float pFrac = Mathf.Clamp01(player.Power / Mathf.Max(1f, player.MaxPower));
        fervorFill.fillAmount = pFrac;
        fervorFill.color = player.HealCharges > 0 ? Color.Lerp(Color.white, ReadyTint, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4f)) : Color.white;

        // flasks: each fills as fervor builds toward one heal; a full one is ready to use
        for (int i = 0; i < flaskFill.Length; i++)
        {
            float amount = Mathf.Clamp01(player.Power / Mathf.Max(1f, player.HealCost) - i);
            flaskFill[i].fillAmount = amount;
            flaskFill[i].color = amount >= 0.999f ? Color.white : FlaskPart;
        }

        // screen flashes
        damageAlpha = Mathf.MoveTowards(damageAlpha, 0f, dt * 0.9f);
        healAlpha = Mathf.MoveTowards(healAlpha, 0f, dt * 0.8f);
        float lowGlow = low ? 0.18f + 0.14f * pulse : 0f;
        SetAlpha(damageFlash, Mathf.Max(damageAlpha, lowGlow));
        SetAlpha(healFlash, healAlpha);

        // death screen
        deathAlpha = Mathf.MoveTowards(deathAlpha, player.IsDead ? 1f : 0f, dt * (player.IsDead ? 0.8f : 4f));
        SetAlpha(deathShade, deathAlpha * 0.6f);
        SetAlpha(banner, deathAlpha);
        deathText.color = new Color(0.8f, 0.1f, 0.12f, deathAlpha);
    }

    // ---------------------------------------------------------------- helpers

    // "Player-HUD-v2-spritesheet_49" -> 49
    private static Dictionary<int, Sprite> LoadSheet(string path)
    {
        var map = new Dictionary<int, Sprite>();
        foreach (Sprite s in Resources.LoadAll<Sprite>(path))
        {
            int u = s.name.LastIndexOf('_');
            int id;
            if (u >= 0 && int.TryParse(s.name.Substring(u + 1), out id))
            {
                map[id] = s;
            }
        }
        if (map.Count == 0)
        {
            Debug.LogError("GameHUD: no sprites found in Resources/" + path + " - run Tools > Blasphemous > Copy HUD Assets.");
        }
        return map;
    }

    private static Sprite Sp(Dictionary<int, Sprite> sheet, int id)
    {
        Sprite s;
        return sheet != null && sheet.TryGetValue(id, out s) ? s : null;
    }

    private static Vector2 SizeOf(Sprite s)
    {
        return s != null ? s.rect.size * S : new Vector2(32f, 32f);
    }

    private static void SetAlpha(Image image, float a)
    {
        var c = image.color;
        c.a = a;
        image.color = c;
    }

    private static void MakeFilled(Image image)
    {
        image.type = Image.Type.Filled;
        image.fillMethod = Image.FillMethod.Horizontal;
        image.fillOrigin = (int)Image.OriginHorizontal.Left;
        image.fillAmount = 1f;
    }

    private static void Clear(RectTransform holder)
    {
        for (int i = holder.childCount - 1; i >= 0; i--)
        {
            Destroy(holder.GetChild(i).gameObject);
        }
    }

    private static void PlaceRect(RectTransform rt, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    private static void MakeTick(RectTransform holder, float x, float height, Color color)
    {
        var rt = NewRect("Tick", holder);
        PlaceRect(rt, new Vector2(x - 1f, 0f), new Vector2(2f, height));
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    // A sprite placed top-left at a position / size inside its parent.
    private static Image Place(string name, Transform parent, Sprite sprite, Vector2 pos, Vector2 size)
    {
        var rt = NewRect(name, parent);
        PlaceRect(rt, pos, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.raycastTarget = false;
        return img;
    }

    private Image MakeFullscreen(string name, Transform parent, Color color)
    {
        var rt = NewRect(name, parent);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private Text MakeText(string name, Transform parent, string text, int size, TextAnchor anchor, Vector2 pos, Vector2 box, Font font)
    {
        var rt = NewRect(name, parent);
        PlaceRect(rt, pos, box);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = font;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = Parchment;
        t.text = text;
        t.supportRichText = true;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        var shadow = rt.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
        shadow.effectDistance = new Vector2(1.5f, -1.5f);
        return t;
    }
}
