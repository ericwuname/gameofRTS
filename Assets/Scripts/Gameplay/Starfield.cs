using UnityEngine;
using TideShot.Core;

namespace TideShot.Gameplay
{
    /// <summary>
    /// 背景：滚动星流 + 缓慢呼吸的网格。
    /// 弹幕游戏里背景不能抢戏，但也不能是一块死黑——
    /// 有东西在动，玩家才会觉得「在前进」。
    /// </summary>
    public sealed class Starfield : MonoBehaviour
    {
        private struct Star
        {
            public Vector2 Pos;
            public float Speed;
            public float Size;
            public float Bright;
            public Color Color;
        }

        private const int StarCount = 220;
        private const float BoundX = 18.5f;
        private const float BoundY = 12f;

        private Star[] _stars = new Star[StarCount];
        private BatchMesh _mesh;
        private float _time;

        public void Initialize(Material material)
        {
            var go = new GameObject("StarMesh");
            go.transform.SetParent(transform, false);
            _mesh = go.AddComponent<BatchMesh>();
            _mesh.Initialize(material, -20);

            // 背景星只用近白灰阶，不用彩色。
            // 彩星会和敌弹/玩家弹抢色相——玩家在高速中靠色相找威胁，
            // 背景一旦有同类色，等于在威胁识别里掺噪声。
            var palette = new[]
            {
                new Color32(0xE8, 0xF1, 0xF5, 0xFF),
                new Color32(0xC8, 0xD2, 0xDA, 0xFF),
                new Color32(0xD8, 0xCE, 0xC8, 0xFF),
                new Color32(0xCE, 0xD8, 0xDC, 0xFF)
            };

            for (int i = 0; i < StarCount; i++)
            {
                _stars[i] = new Star
                {
                    Pos = new Vector2(Random.Range(-BoundX, BoundX), Random.Range(-BoundY, BoundY)),
                    Speed = Random.Range(0.8f, 5.5f),
                    Size = Random.Range(0.025f, 0.085f),
                    // 亮度上限从 0.75 压到 0.38：背景是低频层，
                    // 不该有任何一颗亮到能被误认成子弹。
                    Bright = Random.Range(0.10f, 0.38f),
                    Color = palette[Random.Range(0, palette.Length)]
                };
            }
        }

        public void Tick(float dt)
        {
            if (_mesh == null)
            {
                return;
            }
            _time += dt;
            _mesh.Clear();

            float pulse = 0.5f + 0.5f * Mathf.Sin(_time * 0.9f);

            for (int i = 0; i < StarCount; i++)
            {
                Star s = _stars[i];
                s.Pos.y -= s.Speed * dt;
                if (s.Pos.y < -BoundY)
                {
                    s.Pos.y = BoundY;
                    s.Pos.x = Random.Range(-BoundX, BoundX);
                }
                _stars[i] = s;

                Color c = s.Color;
                c.a = s.Bright * (0.55f + 0.45f * pulse);
                _mesh.Add(s.Pos, Vector2.one * s.Size, 0f, c);
            }

            // 网格：极暗的横竖线，提供空间参照。
            // alpha 从 0.055 砍到 0.028——原来那版在密集弹幕下
            // 会被读成「背景里有一排敌人在排队」。
            Color grid = new Color(0.35f, 0.75f, 0.85f, 0.028f + 0.014f * pulse);
            for (int i = -12; i <= 12; i++)
            {
                float x = i * 1.5f;
                _mesh.Add(new Vector2(x, 0f), new Vector2(0.012f, BoundY), 0f, grid);
            }
            for (int i = -12; i <= 12; i++)
            {
                float y = i * 1.45f;
                _mesh.Add(new Vector2(0f, y), new Vector2(BoundX, 0.012f), 0f, grid);
            }

            _mesh.Commit();
        }
    }
}
