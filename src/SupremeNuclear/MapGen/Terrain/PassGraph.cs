using System.Collections.Generic;

namespace SupremeNuclear
{
    /// <summary>
    /// Граф проходимости террасной карты (паттерн Moho path_clusters / M28Map
    /// land zones): связные области проходимых клеток = КЛАСТЕРЫ (плато/поля),
    /// пары соседних клеток разных кластеров = РЁБРА (рампы/перешейки).
    /// Проходимость пары клеток — перепад высоты ниже порога (сглаженные рампы
    /// проходят). AI использует достижимость: экспансия — точки своего и
    /// смежных кластеров, не «по прямой через обрыв». Чистая логика (тесты).
    /// </summary>
    public sealed class PassGraph
    {
        public readonly int W, H;
        /// <summary>Кластер клетки (−1 — непроходимая вода/обрыв).</summary>
        public readonly int[,] Cluster;
        /// <summary>Число кластеров.</summary>
        public readonly int Count;
        /// <summary>Рёбра достижимости: пары кластеров (i&lt;j).</summary>
        public readonly List<int[]> Edges = new List<int[]>();

        private PassGraph(int[,] cluster, int count, int w, int h)
        {
            Cluster = cluster;
            Count = count;
            W = w;
            H = h;
        }

        /// <summary>Кластер точки (локальные координаты высот; −1 — вода).</summary>
        public int ClusterAt(float x, float z, float step)
        {
            int i = Clamp((int)(x / step + W * 0.5f), 0, W - 1);
            int j = Clamp((int)(z / step + H * 0.5f), 0, H - 1);
            return Cluster[i, j];
        }

        /// <summary>Множество кластеров, достижимых из данного за depth переходов
        /// (BFS по рёбрам; 0 — только свой).</summary>
        public HashSet<int> Reachable(int from, int depth)
        {
            var result = new HashSet<int> { from };
            if (depth <= 0)
                return result;
            var frontier = new List<int> { from };
            var adjacency = Adjacency();
            for (int d = 0; d < depth; d++)
            {
                var next = new List<int>();
                foreach (int c in frontier)
                    foreach (int n in adjacency[c])
                        if (result.Add(n))
                            next.Add(n);
                frontier = next;
            }
            return result;
        }

        /// <summary>Списки смежности по рёбрам (индекс = кластер).</summary>
        public List<int>[] Adjacency()
        {
            // Кластеры 1-based (0 = «не посещена»): слотов Count+1 — иначе
            // последний кластер выходил за границы (краш тика AI, 2026-10-30).
            var adj = new List<int>[Count + 1];
            for (int c = 0; c <= Count; c++)
                adj[c] = new List<int>();
            foreach (int[] e in Edges)
            {
                if (!adj[e[0]].Contains(e[1])) adj[e[0]].Add(e[1]);
                if (!adj[e[1]].Contains(e[0])) adj[e[1]].Add(e[0]);
            }
            return adj;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Построить граф по высотам: клетка проходима, если не ниже
        /// уровня моря+запас; связь соседей — перепад ≤ maxStep.</summary>
        public static PassGraph Build(float[,] height, int w, int h, float step,
            float seaLevel, float maxStep)
        {
            // Проходимость клеток и связи (4-соседство): BFS-заливка кластеров.
            var cluster = new int[w, h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                    cluster[i, j] = height[i, j] < seaLevel + 1f ? -1 : 0; // 0 = не посещена

            int count = 0;
            var queue = new Queue<int>();
            var edgeSet = new HashSet<long>();
            for (int j0 = 0; j0 < h; j0++)
                for (int i0 = 0; i0 < w; i0++)
                {
                    if (cluster[i0, j0] != 0)
                        continue;
                    count++;
                    cluster[i0, j0] = count;
                    queue.Enqueue(i0);
                    queue.Enqueue(j0);
                    while (queue.Count > 0)
                    {
                        int i = queue.Dequeue();
                        int j = queue.Dequeue();
                        // 4-соседи: проходимая связь — перепад в допуске
                        TryVisit(i + 1, j);
                        TryVisit(i - 1, j);
                        TryVisit(i, j + 1);
                        TryVisit(i, j - 1);

                        void TryVisit(int ni, int nj)
                        {
                            if (ni < 0 || nj < 0 || ni >= w || nj >= h)
                                return;
                            if (height[ni, nj] < seaLevel + 1f)
                                return; // вода — не связываем и не заливаем
                            if (System.Math.Abs(height[ni, nj] - height[i, j]) > maxStep)
                                return; // обрыв — связи нет
                            int other = cluster[ni, nj];
                            if (other == 0)
                            {
                                cluster[ni, nj] = count;
                                queue.Enqueue(ni);
                                queue.Enqueue(nj);
                            }
                            else if (other != count && other > 0)
                            {
                                // соседний кластер в пределах шага — ребро
                                int a = count < other ? count : other;
                                int b = count < other ? other : count;
                                edgeSet.Add((long)a * 1000000 + b);
                            }
                        }
                    }
                }

            var graph = new PassGraph(cluster, count, w, h);
            foreach (long key in edgeSet)
                graph.Edges.Add(new[] { (int)(key / 1000000), (int)(key % 1000000) });
            return graph;
        }
    }
}
