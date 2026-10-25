using UnityEngine;

namespace TideShot.Core
{
    /// <summary>
    /// 通用纯色发光块批量渲染器。所有「纯色发光块」——子弹、光条、冲击波环——都走它。
    /// 为什么需要：满屏弹幕会有 500+ 个可见元素，每个都做成 GameObject 会直接拖垮帧率。
    /// 这里把它们塞进一个 Mesh，一次 draw call 画完。
    ///
    /// 图元有两种：矩形（Add / AddLine）和圆（AddCircle）。
    /// 圆不是装饰：弹幕游戏的子弹语言是「圆」。用矩形画子弹，
    /// 玩家会把它读成方块，而碰撞判定却是圆的（`sqrMagnitude <= reach²`）——
    /// 视觉形状与判定形状不一致，玩家凭视觉预判擦弹就会失误，
    /// 且始终说不出自己为什么失误。这是公平性问题，不是美观问题。
    /// </summary>
    public sealed class BatchMesh : MonoBehaviour
    {
        // 2048 → 4096：圆图元每个占 10 顶点，敌弹「核心 + 光晕」两个圆就是 20 顶点，
        // 2048 只够画 102 颗，密集波次会漏画——子弹凭空消失比方块更难接受。
        // 4096 × 10 = 40960 顶点，仍在 Unity 16 位索引上限 65535 之内。
        private const int Capacity = 4096;

        // 每个 Item 固定占用的顶点/索引数，取「矩形」与「圆」的较大者，
        // 这样 Commit 一次写满整个数组，零分配、零 GC。
        // 矩形：4 顶点 / 6 索引。8 段圆（三角形扇）：10 顶点 / 24 索引。
        private const int VertsPerItem = 10;
        private const int TrisPerItem = 24;
        private const int DefaultCircleSegments = 8;

        private struct Item
        {
            public Vector2 Pos;
            public Vector2 Half;
            public float Cos;
            public float Sin;
            public Color Color;

            /// <summary>0 = 矩形；≥3 = 用三角形扇近似圆，值为边数。</summary>
            public int Segments;
        }

        private Item[] _items = new Item[Capacity];
        private int _count;

        /// <summary>上一帧 Commit 时的图元数。用来在 _count 变小时把废弃槽位压成退化，
        /// 否则旧顶点会被当成有效图元画出来（详见 Commit 里的注释）。</summary>
        private int _lastCount;

        private Mesh _mesh;
        private Vector3[] _verts;
        private Color32[] _colors;
        private int[] _tris;

        public int Count => _count;

        public void Initialize(Material material, int sortingOrder)
        {
            _verts = new Vector3[Capacity * VertsPerItem];
            _colors = new Color32[Capacity * VertsPerItem];
            _tris = new int[Capacity * TrisPerItem];

            for (int i = 0; i < Capacity; i++)
            {
                int v = i * VertsPerItem;
                int t = i * TrisPerItem;

                // 矩形索引：两个三角形
                _tris[t] = v;
                _tris[t + 1] = v + 1;
                _tris[t + 2] = v + 2;
                _tris[t + 3] = v;
                _tris[t + 4] = v + 2;
                _tris[t + 5] = v + 3;

                // 其余索引全部指向同一个顶点：三点共点的三角形面积为零，
                // GPU 光栅化时不产生任何像素。这样圆比矩形多用的顶点
                // 在画矩形时自动退化成「不存在」，不需要每帧重建索引缓冲。
                for (int k = 6; k < TrisPerItem; k += 3)
                {
                    _tris[t + k] = v;
                    _tris[t + k + 1] = v;
                    _tris[t + k + 2] = v;
                }
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "BatchMesh" };
                _mesh.MarkDynamic();
            }
            var mf = gameObject.GetComponent<MeshFilter>();
            if (mf == null)
            {
                mf = gameObject.AddComponent<MeshFilter>();
            }
            mf.sharedMesh = _mesh;

            var mr = gameObject.GetComponent<MeshRenderer>();
            if (mr == null)
            {
                mr = gameObject.AddComponent<MeshRenderer>();
            }
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sortingOrder = sortingOrder;
        }

        public void Clear()
        {
            _count = 0;
        }

        /// <summary>加一个中心定位、可旋转的矩形。</summary>
        public void Add(Vector2 pos, Vector2 half, float rotationDeg, Color color)
        {
            if (_count >= Capacity)
            {
                return;
            }
            float rad = rotationDeg * Mathf.Deg2Rad;
            _items[_count] = new Item
            {
                Pos = pos,
                Half = half,
                Cos = Mathf.Cos(rad),
                Sin = Mathf.Sin(rad),
                Color = color,
                Segments = 0
            };
            _count++;
        }

        /// <summary>
        /// 加一个用三角形扇近似的圆。segments 为边数，8 段在弹幕尺度下与真圆无视觉差异。
        /// 半径越小段数越不重要——子弹半径通常只有 0.2 世界单位左右。
        /// </summary>
        public void AddCircle(Vector2 pos, float radius, Color color, int segments = DefaultCircleSegments)
        {
            if (_count >= Capacity || radius <= 0f)
            {
                return;
            }
            if (segments < 3)
            {
                segments = 3;
            }
            _items[_count] = new Item
            {
                Pos = pos,
                Half = new Vector2(radius, radius),
                Cos = 1f,
                Sin = 0f,
                Color = color,
                Segments = segments
            };
            _count++;
        }

        /// <summary>加一根从 a 到 b 的发光条（子弹拖尾、激光都用这个）。</summary>
        public void AddLine(Vector2 a, Vector2 b, float thickness, Color color)
        {
            Vector2 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.0001f)
            {
                return;
            }
            Vector2 dir = delta / len;
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            Add((a + b) * 0.5f, new Vector2(len * 0.5f, thickness * 0.5f), angle, color);
        }

        /// <summary>提交本帧所有图元到 GPU。</summary>
        public void Commit()
        {
            if (_mesh == null)
            {
                return;
            }
            if (_count == 0)
            {
                CollapseUnused(0, _lastCount);
                _lastCount = 0;
                if (_mesh.vertexCount > 0)
                {
                    _mesh.Clear();
                }
                return;
            }

            for (int i = 0; i < _count; i++)
            {
                Item it = _items[i];
                int v = i * VertsPerItem;
                int t = i * TrisPerItem;

                if (it.Segments >= 3)
                {
                    WriteFan(it, v, t);
                }
                else
                {
                    WriteRect(it, v, t);
                }
            }

            // 把上次用过、这次没用到的槽位压成退化。
            //
            // 为什么必须做：`mesh.vertices = _verts` 上传的是**整个数组**，
            // 不是「前 _count*VertsPerItem 个」。所以当本帧图元数比上帧少时，
            // 多出来的槽位里存的还是上一帧的顶点位置和颜色，GPU 会照画——
            // 表现为子弹重影、元素残影，且 _count 波动越大越明显。
            //
            // 这个坑原版（每项固定 4 顶点）就存在，只是那时数组小、残影只有一帧
            // 且长度有限；换成圆图元后每项占 10 顶点，同样的废弃槽位会画出
            // 更长的重影。属于我放大了一个原有缺陷，必须一起补掉。
            CollapseUnused(_count, _lastCount);
            _lastCount = _count;

            _mesh.Clear();
            _mesh.vertices = _verts;
            _mesh.colors32 = _colors;
            _mesh.triangles = _tris;
            _mesh.RecalculateBounds();
        }

        /// <summary>
        /// 把 [from, to) 区间内所有 Item 槽位的顶点压成同一点。
        /// 配合 Initialize 里预置的退化索引（三点共点、面积为零），
        /// 这些槽位在 GPU 光栅化时不产生任何像素，等于「这一格不存在」。
        /// 零分配，只是把已有顶点写成同一个坐标。
        /// </summary>
        private void CollapseUnused(int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                int v = i * VertsPerItem;
                Vector3 p = _verts[v];
                for (int k = 0; k < VertsPerItem; k++)
                {
                    _verts[v + k] = p;
                }
            }
        }

        private void WriteRect(Item it, int v, int t)
        {
            float hx = it.Half.x;
            float hy = it.Half.y;
            float cx = it.Cos;
            float sy = it.Sin;

            float x0 = (-hx * cx - -hy * sy);
            float y0 = (-hx * sy + -hy * cx);
            float x1 = (hx * cx - -hy * sy);
            float y1 = (hx * sy + -hy * cx);
            float x2 = (hx * cx - hy * sy);
            float y2 = (hx * sy + hy * cx);
            float x3 = (-hx * cx - hy * sy);
            float y3 = (-hx * sy + hy * cx);

            Vector3 p0 = new Vector3(it.Pos.x + x0, it.Pos.y + y0, 0f);
            Vector3 p1 = new Vector3(it.Pos.x + x1, it.Pos.y + y1, 0f);
            Vector3 p2 = new Vector3(it.Pos.x + x2, it.Pos.y + y2, 0f);
            Vector3 p3 = new Vector3(it.Pos.x + x3, it.Pos.y + y3, 0f);

            _verts[v] = p0;
            _verts[v + 1] = p1;
            _verts[v + 2] = p2;
            _verts[v + 3] = p3;
            // 顶点 4..9 复制 p3：配上前缀里退化的索引，这些三角形面积为零。
            for (int k = 4; k < VertsPerItem; k++)
            {
                _verts[v + k] = p3;
            }

            Color32 c = it.Color;
            for (int k = 0; k < VertsPerItem; k++)
            {
                _colors[v + k] = c;
            }

            // 索引在 Initialize 里已按矩形填好，圆的分支会覆写自己那段。
            _ = t;
        }

        private void WriteFan(Item it, int v, int t)
        {
            int seg = it.Segments;
            float radius = it.Half.x;

            // 顶点 0 是圆心，1..seg 是边缘
            _verts[v] = new Vector3(it.Pos.x, it.Pos.y, 0f);
            for (int s = 0; s < seg; s++)
            {
                float a = (s / (float)seg) * Mathf.PI * 2f;
                _verts[v + 1 + s] = new Vector3(
                    it.Pos.x + Mathf.Cos(a) * radius,
                    it.Pos.y + Mathf.Sin(a) * radius,
                    0f);
            }
            // 顶点 seg+1..9 复制圆心：同样靠零面积三角形消隐
            for (int k = seg + 1; k < VertsPerItem; k++)
            {
                _verts[v + k] = _verts[v];
            }

            Color32 c = it.Color;
            for (int k = 0; k < VertsPerItem; k++)
            {
                _colors[v + k] = c;
            }

            // 三角形扇：圆心 + 相邻两个边缘点
            for (int s = 0; s < seg; s++)
            {
                int ti = t + s * 3;
                _tris[ti] = v;
                _tris[ti + 1] = v + 1 + s;
                _tris[ti + 2] = v + 1 + ((s + 1) % seg);
            }
            // 剩余索引退化为圆心
            for (int k = seg * 3; k < TrisPerItem; k += 3)
            {
                _tris[t + k] = v;
                _tris[t + k + 1] = v;
                _tris[t + k + 2] = v;
            }
        }
    }
}
