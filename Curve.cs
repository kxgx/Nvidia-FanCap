using System;
using System.Globalization;
using System.Text;

namespace NvidiaFanCap
{
    /// <summary>
    /// Shape-preserving cubic Hermite interpolation (Fritsch-Carlson, "PCHIP").
    /// A plain cubic spline overshoots between samples, which is exactly what a
    /// fan/power curve must never do: a rising temperature has to raise the fan,
    /// not dip it because of a ringing spline (and the same for the power limit).
    /// PCHIP keeps every segment monotone, so the curve stays between the two
    /// sample values and never invents a value outside the sampled range.
    /// Values outside the sampled range are clamped to the end points - never
    /// extrapolated.
    /// </summary>
    internal sealed class Curve
    {
        private readonly double[] xs;
        private readonly double[] ys;
        private readonly double[] tangents;   // slope at each knot
        private readonly double minY, maxY;

        private Curve(double[] xs, double[] ys, double[] tangents)
        {
            this.xs = xs;
            this.ys = ys;
            this.tangents = tangents;
            minY = maxY = ys[0];
            for (int i = 1; i < ys.Length; i++)
            {
                if (ys[i] < minY) minY = ys[i];
                if (ys[i] > maxY) maxY = ys[i];
            }
        }

        public int Count { get { return xs.Length; } }
        public double MinX { get { return xs[0]; } }
        public double MaxX { get { return xs[xs.Length - 1]; } }

        /// <summary>
        /// Build from parallel arrays. Points may be unsorted (they are sorted by
        /// x); a repeated x keeps the last y. Returns null when no sane curve can
        /// be built (too few points, mismatched lengths, NaN/Infinity).
        /// </summary>
        public static Curve Build(double[] x, double[] y)
        {
            if (x == null || y == null || x.Length != y.Length || x.Length < 2) return null;
            int n = x.Length;
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(x[i]) || double.IsNaN(y[i]) ||
                    double.IsInfinity(x[i]) || double.IsInfinity(y[i])) return null;
            }

            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, delegate (int a, int b)
            {
                int c = x[a].CompareTo(x[b]);
                return c != 0 ? c : a.CompareTo(b);   // stable: duplicates keep file order
            });

            double[] px = new double[n];
            double[] py = new double[n];
            int m = 0;
            for (int k = 0; k < n; k++)
            {
                int i = order[k];
                if (m > 0 && px[m - 1] == x[i]) { py[m - 1] = y[i]; continue; }  // duplicate x: last wins
                px[m] = x[i];
                py[m] = y[i];
                m++;
            }
            if (m < 2) return null;

            // the working arrays must be exactly m long: trailing zeros would be
            // read as an extra knot at x = 0
            double[] sx = new double[m];
            double[] sy = new double[m];
            Array.Copy(px, sx, m);
            Array.Copy(py, sy, m);

            double[] h = new double[m - 1];
            double[] delta = new double[m - 1];
            for (int i = 0; i < m - 1; i++)
            {
                h[i] = sx[i + 1] - sx[i];
                delta[i] = (sy[i + 1] - sy[i]) / h[i];
            }

            double[] tangents = new double[m];
            if (m == 2)
            {
                tangents[0] = tangents[1] = delta[0];       // only one slope: straight line
            }
            else
            {
                // interior: zero at local extrema, weighted harmonic mean otherwise
                for (int i = 1; i < m - 1; i++)
                {
                    if (delta[i - 1] * delta[i] <= 0) tangents[i] = 0;
                    else
                    {
                        double w1 = 2 * h[i] + h[i - 1];
                        double w2 = h[i] + 2 * h[i - 1];
                        tangents[i] = (w1 + w2) / (w1 / delta[i - 1] + w2 / delta[i]);
                    }
                }
                // endpoints: one-sided estimate, limited so the first/last segment
                // cannot overshoot its neighbours either
                tangents[0] = EndpointTangent(h[0], h[1], delta[0], delta[1]);
                tangents[m - 1] = EndpointTangent(h[m - 2], h[m - 3], delta[m - 2], delta[m - 3]);
            }
            return new Curve(sx, sy, tangents);
        }

        /// <summary>
        /// Like Build, but silently falls back to the compiled-in defaults when the
        /// user-supplied arrays are unusable (never let a bad ini key kill the loop).
        /// </summary>
        public static Curve BuildOrDefault(double[] x, double[] y, double[] fallbackX, double[] fallbackY)
        {
            Curve c = Build(x, y);
            return c != null ? c : Build(fallbackX, fallbackY);
        }

        private static double EndpointTangent(double h0, double h1, double d0, double d1)
        {
            if (d0 == 0) return 0;
            double t = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
            if (t * d0 <= 0) return 0;                                   // wrong direction
            if (d0 * d1 <= 0 && Math.Abs(t) > Math.Abs(3 * d0)) return 3 * d0;  // shape limit
            return t;
        }

        /// <summary>Interpolate at x; clamps to the end points outside [MinX, MaxX].</summary>
        public double Evaluate(double x)
        {
            int n = xs.Length;
            if (double.IsNaN(x) || x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];

            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (xs[mid] <= x) lo = mid; else hi = mid;
            }

            double h = xs[lo + 1] - xs[lo];
            double s = (x - xs[lo]) / h;
            double s2 = s * s, s3 = s2 * s;
            double v = (2 * s3 - 3 * s2 + 1) * ys[lo]
                     + (s3 - 2 * s2 + s) * h * tangents[lo]
                     + (-2 * s3 + 3 * s2) * ys[lo + 1]
                     + (s3 - s2) * h * tangents[lo + 1];
            if (v < minY) return minY;
            if (v > maxY) return maxY;
            return v;
        }

        /// <summary>
        /// Parse a "35,45,55" style list (comma, semicolon or whitespace separated).
        /// Returns null when any token is not a finite number or fewer than two
        /// values were given.
        /// </summary>
        public static double[] ParseList(string text)
        {
            if (text == null) return null;
            string[] parts = text.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            double[] values = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                double v;
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
                if (double.IsNaN(v) || double.IsInfinity(v)) return null;
                values[i] = v;
            }
            return values;
        }

        /// <summary>Serialize back to the "35,45,55" ini format.</summary>
        public static string Format(double[] values)
        {
            if (values == null || values.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
