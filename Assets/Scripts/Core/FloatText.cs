using System.Collections.Generic;
using UnityEngine;

namespace TideShot.Core
{
    /// <summary>
    /// 伤害飘字队列。用 OnGUI 绘制，避免引入 TextMeshPro 与 Canvas 依赖。
    /// 这是 MVP 的视觉反馈手段（GDD 6.5「命中」行），正式版应交由世界空间 UI 实现。
    /// 边界：队列硬上限 64 条，超出部分丢弃最旧的，防止极端同屏时无限增长。
    /// @trace AUD-001
    /// </summary>
    public static class FloatText
    {
        private struct Entry
        {
            public Vector3 WorldPos;
            public string Text;
            public Color Color;
            public float Age;
            public float Life;
            public bool Big;
        }

        private const int MaxEntries = 64;
        private static readonly List<Entry> Entries = new List<Entry>(MaxEntries);

        public static void Push(Vector2 worldPos, string text, Color color, bool big = false)
        {
            if (Entries.Count >= MaxEntries)
            {
                Entries.RemoveAt(0);
            }
            Entries.Add(new Entry
            {
                WorldPos = new Vector3(worldPos.x, worldPos.y, 0f),
                Text = text,
                Color = color,
                Age = 0f,
                Life = 0.4f,
                Big = big
            });
        }

        public static void Tick(float dt)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                Entry e = Entries[i];
                e.Age += dt;
                if (e.Age >= e.Life)
                {
                    Entries.RemoveAt(i);
                    continue;
                }
                // 上浮 0.8 单位，淡出（GDD 10.3）
                e.WorldPos += new Vector3(0f, 0.8f * (dt / e.Life), 0f);
                Entries[i] = e;
            }
        }

        public static IReadOnlyList<(Vector3 pos, string text, Color color, float alpha, bool big)> Snapshot()
        {
            var result = new List<(Vector3, string, Color, float, bool)>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                float t = Mathf.Clamp01(e.Age / e.Life);
                result.Add((e.WorldPos, e.Text, e.Color, 1f - t, e.Big));
            }
            return result;
        }

        public static void Clear()
        {
            Entries.Clear();
        }
    }
}
