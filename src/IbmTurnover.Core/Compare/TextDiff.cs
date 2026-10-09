namespace IbmTurnover.Core.Compare;

public enum LineOp { Same, Added, Removed }

public sealed record DiffLine(LineOp Op, string? Left, string? Right, int? LeftNo, int? RightNo);

/// <summary>Line-level LCS diff for the side-by-side view. Inputs are DDL-sized, so O(n*m) is fine; capped for safety.</summary>
public static class TextDiff
{
    /// <summary>Cells in the LCS table (middle part only). 4M ints = 16 MB worst case per request.</summary>
    private const long MaxCells = 4_000_000;

    public static IReadOnlyList<DiffLine> Compute(string? left, string? right)
    {
        var a = Split(left);
        var b = Split(right);
        var result = new List<DiffLine>(a.Length + b.Length);

        // Common head and tail cost nothing and are usually most of a changed procedure.
        var head = 0;
        while (head < a.Length && head < b.Length && Same(a[head], b[head])) head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && Same(a[a.Length - 1 - tail], b[b.Length - 1 - tail])) tail++;
        for (var i = 0; i < head; i++) result.Add(new DiffLine(LineOp.Same, a[i], b[i], i + 1, i + 1));

        int n = a.Length - head - tail, m = b.Length - head - tail;
        if ((long)(n + 1) * (m + 1) > MaxCells)
        {
            // Too big to align line by line within a request: show the middle as removed-then-added.
            for (var i = 0; i < n; i++) result.Add(new DiffLine(LineOp.Removed, a[head + i], null, head + i + 1, null));
            for (var j = 0; j < m; j++) result.Add(new DiffLine(LineOp.Added, null, b[head + j], null, head + j + 1));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = Same(a[head + i], b[head + j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (Same(a[head + x], b[head + y])) { result.Add(new DiffLine(LineOp.Same, a[head + x], b[head + y], head + x + 1, head + y + 1)); x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) { result.Add(new DiffLine(LineOp.Removed, a[head + x], null, head + x + 1, null)); x++; }
                else { result.Add(new DiffLine(LineOp.Added, null, b[head + y], null, head + y + 1)); y++; }
            }
            for (; x < n; x++) result.Add(new DiffLine(LineOp.Removed, a[head + x], null, head + x + 1, null));
            for (; y < m; y++) result.Add(new DiffLine(LineOp.Added, null, b[head + y], null, head + y + 1));
        }
        for (var k = tail; k > 0; k--)
            result.Add(new DiffLine(LineOp.Same, a[a.Length - k], b[b.Length - k], a.Length - k + 1, b.Length - k + 1));
        return result;
    }

    private static string[] Split(string? s) =>
        string.IsNullOrEmpty(s) ? [] : s.Replace("\r\n", "\n").Split('\n');

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.Ordinal);
}
