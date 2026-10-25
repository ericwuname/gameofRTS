using UnityEngine;

namespace TideShot.Core
{
    /// <summary>
    /// 程序化图形生成。整个游戏不含任何图片资源——所有形状都在运行时算出来。
    /// 为什么：这个类型的视觉是「几何 + 光」，不需要贴图；
    /// 用程序化图形反而能保证边缘锐利、颜色精确可控。
    /// </summary>
    public static class SpriteFactory
    {
        public const float PixelsPerUnit = 64f;

        public static Texture2D NewTexture(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var clear = new Color32[w * h];
            tex.SetPixels32(clear);
            return tex;
        }

        public static Sprite ToSprite(Texture2D tex)
        {
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), PixelsPerUnit);
        }

        /// <summary>朝上的三角形（玩家飞船）。带外描边与内部亮芯。</summary>
        public static Sprite Triangle(int size, Color fill, Color rim)
        {
            size = Mathf.Max(16, size);
            Texture2D tex = NewTexture(size, size);
            float cx = size * 0.5f;
            // 顶点：上中、左下、右下
            Vector2 a = new Vector2(cx, size - 2f);
            Vector2 b = new Vector2(2f, 2f);
            Vector2 c = new Vector2(size - 2f, 2f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float d = DistanceToTriangle(p, a, b, c);
                    if (d < 0f)
                    {
                        continue;
                    }
                    // 靠近边缘用 rim 色，内部用 fill
                    Color col = d < size * 0.09f ? rim : fill;
                    col.a = Mathf.Clamp01(d * 3f);
                    tex.SetPixel(x, y, col);
                }
            }
            return ToSprite(tex);
        }

        /// <summary>菱形（敌人）。</summary>
        public static Sprite Diamond(int size, Color fill, Color rim)
        {
            size = Mathf.Max(12, size);
            Texture2D tex = NewTexture(size, size);
            float cx = size * 0.5f;
            float cy = size * 0.5f;
            float r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - cx) / r;
                    float dy = Mathf.Abs(y + 0.5f - cy) / r;
                    float d = dx + dy;
                    if (d > 1f)
                    {
                        continue;
                    }
                    Color col = d > 0.78f ? rim : fill;
                    col.a = Mathf.Clamp01((1f - d) * 8f);
                    tex.SetPixel(x, y, col);
                }
            }
            return ToSprite(tex);
        }

        /// <summary>六边形（重型敌人）。</summary>
        public static Sprite Hexagon(int size, Color fill, Color rim)
        {
            size = Mathf.Max(16, size);
            Texture2D tex = NewTexture(size, size);
            float cx = size * 0.5f;
            float cy = size * 0.5f;
            float r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - cx);
                    float dy = Mathf.Abs(y + 0.5f - cy);
                    // 六边形距离场：取三个轴向投影的最大值
                    float d = Mathf.Max(dx * 0.866f + dy * 0.5f, dy);
                    if (d > r)
                    {
                        continue;
                    }
                    Color col = d > r * 0.82f ? rim : fill;
                    col.a = Mathf.Clamp01((r - d) * 0.5f);
                    tex.SetPixel(x, y, col);
                }
            }
            return ToSprite(tex);
        }

        /// <summary>实心圆（光球 / 爆心）。</summary>
        public static Sprite Circle(int size, Color fill)
        {
            size = Mathf.Max(8, size);
            Texture2D tex = NewTexture(size, size);
            float cx = size * 0.5f;
            float cy = size * 0.5f;
            float r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > r)
                    {
                        continue;
                    }
                    Color c = fill;
                    c.a = Mathf.Clamp01((r - dist) * 2f);
                    tex.SetPixel(x, y, c);
                }
            }
            return ToSprite(tex);
        }

        /// <summary>柔光圆（爆心辉光）。中心亮、向外衰减。</summary>
        public static Sprite Glow(int size, Color fill)
        {
            size = Mathf.Max(16, size);
            Texture2D tex = NewTexture(size, size);
            float cx = size * 0.5f;
            float cy = size * 0.5f;
            float r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - cx;
                    float dy = y + 0.5f - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) / r;
                    if (dist > 1f)
                    {
                        continue;
                    }
                    Color c = fill;
                    c.a = Mathf.Pow(1f - dist, 2.2f) * fill.a;
                    tex.SetPixel(x, y, c);
                }
            }
            return ToSprite(tex);
        }

        /// <summary>点到三角形三条边的内向距离，负值表示在外部。</summary>
        private static float DistanceToTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(a, b, p);
            float d2 = Cross(b, c, p);
            float d3 = Cross(c, a, p);
            bool inside = (d1 >= 0f && d2 >= 0f && d3 >= 0f);
            if (!inside)
            {
                return -1f;
            }
            return Mathf.Min(d1, Mathf.Min(d2, d3));
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 p)
        {
            Vector2 ab = b - a;
            Vector2 ap = p - a;
            float cross = ab.x * ap.y - ab.y * ap.x;
            float len = ab.magnitude;
            return len < 0.0001f ? 0f : cross / len;
        }
    }
}
