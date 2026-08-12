using System.Collections.Generic;
using System.Linq;

namespace Segmento.Editor
{
    public static class PageRange
    {
        /// <summary>
        /// Parsuje składnię "1-3,7,12-" na 1-based numery stron w zakresie [1..total].
        /// Pusty/niepoprawny wejściowo → wszystkie strony.
        /// </summary>
        public static List<int> Parse(string? spec, int total)
        {
            var result = new SortedSet<int>();
            if (string.IsNullOrWhiteSpace(spec))
            {
                for (int i = 1; i <= total; i++) result.Add(i);
                return result.ToList();
            }

            foreach (var raw in spec.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;

                int dash = part.IndexOf('-');
                if (dash < 0)
                {
                    if (int.TryParse(part, out int n)) AddIfValid(result, n, total);
                }
                else
                {
                    string a = part[..dash].Trim();
                    string b = part[(dash + 1)..].Trim();
                    int from = a.Length == 0 ? 1 : (int.TryParse(a, out int fa) ? fa : 1);
                    int to = b.Length == 0 ? total : (int.TryParse(b, out int tb) ? tb : total);
                    if (from > to) (from, to) = (to, from);
                    for (int i = from; i <= to; i++) AddIfValid(result, i, total);
                }
            }
            return result.ToList();
        }

        private static void AddIfValid(SortedSet<int> set, int n, int total)
        {
            if (n >= 1 && n <= total) set.Add(n);
        }
    }
}
