using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// A bar fill whose end leans like a backslash ("\") instead of ending in a straight vertical cut.
// Fill = 0..1 is how much of the bar is filled; the lean (Slant, in canvas units) runs over the bar's height, with the
// top of the edge further left than the bottom. At 0 nothing is drawn and at 1 the bar is completely full (no gap), so the
// lean only shows while the bar is partly filled. The sprite is stretched across the whole bar, exactly like a normal
// stretched Image, so use the sprite cut down to its visible rows.
[RequireComponent(typeof(CanvasRenderer))]
public class SlantedFill : MaskableGraphic
{
    [SerializeField] private Sprite sprite;
    [SerializeField, Range(0f, 1f)] private float fill = 1f;
    [SerializeField] private float slant = 9f;

    public Sprite Sprite
    {
        get { return sprite; }
        set { if (sprite != value) { sprite = value; SetAllDirty(); } }
    }

    public float Fill
    {
        get { return fill; }
        set
        {
            value = Mathf.Clamp01(value);
            if (!Mathf.Approximately(fill, value)) { fill = value; SetVerticesDirty(); }
        }
    }

    public float Slant
    {
        get { return slant; }
        set { if (!Mathf.Approximately(slant, value)) { slant = value; SetVerticesDirty(); } }
    }

    public override Texture mainTexture
    {
        get { return sprite != null ? sprite.texture : s_WhiteTexture; }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (fill <= 0f) return;

        Rect r = GetPixelAdjustedRect();
        float w = r.width, h = r.height;
        if (w <= 0f || h <= 0f) return;

        // The edge runs from (xt, top) down to (xb, bottom). It starts fully left of the bar (fill 0) and ends fully past
        // it (fill 1), so the shape is simply the bar cut by that slanted line.
        float xb = fill * (w + slant);
        float xt = xb - slant;

        // local space: origin bottom-left, y up; clockwise like Unity's own UI quads
        var poly = new List<Vector2>
        {
            new Vector2(0f, h), new Vector2(xt, h), new Vector2(xb, 0f), new Vector2(0f, 0f),
        };
        poly = Clip(poly, true, 0f);
        poly = Clip(poly, false, w);
        if (poly.Count < 3) return;

        Vector4 uv = sprite != null ? UnityEngine.Sprites.DataUtility.GetOuterUV(sprite) : new Vector4(0f, 0f, 1f, 1f);
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 p = poly[i];
            var texCoord = new Vector2(Mathf.Lerp(uv.x, uv.z, p.x / w), Mathf.Lerp(uv.y, uv.w, p.y / h));
            vh.AddVert(new Vector3(r.xMin + p.x, r.yMin + p.y, 0f), color, texCoord);
        }
        for (int i = 1; i < poly.Count - 1; i++)
        {
            vh.AddTriangle(0, i, i + 1);
        }
    }

    // Cuts a polygon against a vertical line, keeping the side with x >= line (keepGreater) or x <= line.
    private static List<Vector2> Clip(List<Vector2> input, bool keepGreater, float x)
    {
        var output = new List<Vector2>();
        for (int i = 0; i < input.Count; i++)
        {
            Vector2 a = input[i], b = input[(i + 1) % input.Count];
            bool aIn = keepGreater ? a.x >= x : a.x <= x;
            bool bIn = keepGreater ? b.x >= x : b.x <= x;
            if (aIn) output.Add(a);
            if (aIn != bIn)
            {
                float t = (x - a.x) / (b.x - a.x);
                output.Add(new Vector2(x, a.y + t * (b.y - a.y)));
            }
        }
        return output;
    }
}
