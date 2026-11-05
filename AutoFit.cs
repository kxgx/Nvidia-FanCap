using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NvidiaFanCap
{
    /// <summary>
    /// Thermal model learned online from observed steady states:
    ///
    ///     T = A + B*P + C*F        (P = board power in W, F = fan in %, C &lt; 0)
    ///
    /// Only samples taken while the fan command and the temperature were stable
    /// are fed in (a transient says nothing about the equilibrium). The fit is a
    /// ridge-regularised least squares pull toward physically sane priors, so a
    /// handful of samples already gives a usable model and a bad sample cannot
    /// wreck it.
    ///
    /// In auto mode the fan curve is derived from this model and the power limit
    /// the user chose: F* is the fan that holds the target temperature at that
    /// power, and the curve is the line through (target, F*) with the slope
    /// 1/|C|. That slope makes the closed loop settle exactly on the target:
    /// with T = A + B*P + C*F and F = F* + (T-target)/|C|, the equilibrium is
    /// T = target for any C &lt; 0.
    /// </summary>
    internal sealed class ThermalModel
    {
        private const int Capacity = 240;

        // priors: measured on the blower 4070 Ti SUPER. They are also the hard
        // bounds below - the linear model is locally valid at best, and letting
        // the fit wander outside these ranges is what produced nonsense anchors.
        private const double PriorA = 60.0;    // C
        private const double PriorB = 0.035;   // C per W   (120W->80C, 220W->83C at fixed fan)
        private const double PriorC = -0.22;   // C per fan % (4.5 %/C, measured)
        private const double RidgeA = 0.5;
        private const double RidgeB = 2.0;
        private const double RidgeC = 8.0;
        private const double MinB = 0.02, MaxB = 0.06;
        private const double MinC = -0.40, MaxC = -0.18;

        private readonly Queue<double[]> samples = new Queue<double[]>();

        public double A { get; private set; }

        /// <summary>Learned fan percent at the target temperature (&lt;0 = not learned yet).</summary>
        public double Anchor { get; set; } = -1;
        public double B { get; private set; }
        public double C { get; private set; }

        public ThermalModel()
        {
            A = PriorA;
            B = PriorB;
            C = PriorC;
        }

        public int Count { get { return samples.Count; } }
        public bool IsReady { get { return samples.Count >= 3 && C < -0.05; } }

        /// <summary>Slope of the control curve: fan percent per degree C (always positive).</summary>
        public double Slope { get { return 1.0 / Math.Max(0.05, Math.Abs(C)); } }

        /// <summary>Fan percent per degree C, bounded to the measured range.</summary>
        public double FanPerDegree { get { return 1.0 / Math.Min(0.40, Math.Max(0.18, Math.Abs(C))); } }

        /// <summary>Last recorded steady observation, if any.</summary>
        public bool TryGetLast(out double power, out double fan, out double temp)
        {
            double[] last = null;
            foreach (double[] s in samples) last = s;
            if (last == null) { power = fan = temp = 0; return false; }
            power = last[0]; fan = last[1]; temp = last[2];
            return true;
        }

        /// <summary>Fan percent that holds <paramref name="targetTemp"/> at this power.</summary>
        public double AnchorFan(double targetTemp, double powerWatts)
        {
            double f = (targetTemp - A - B * powerWatts) / C;
            if (f < 0) return 0;
            if (f > 100) return 100;
            return f;
        }

        /// <summary>
        /// Anchor derived from the last real steady observation instead of the
        /// intercept. The linear fit's A is poorly identifiable (P and F are
        /// correlated in normal use), but the deltas are not:
        ///
        ///   T* - T0 = B*(P* - P0) + C*(F* - F0)
        ///   F*       = F0 + ((T* - T0) - B*(P* - P0)) / C
        ///
        /// so the curve stays consistent with what the card actually did.
        /// Returns -1 when there is no usable observation yet.
        /// </summary>
        public double AnchorFanFromLast(double targetTemp, double powerWatts)
        {
            double[] last = null;
            foreach (double[] s in samples) last = s;
            if (last == null) return -1;
            double f = last[1] + ((targetTemp - last[2]) - B * (powerWatts - last[0])) / C;
            if (f < 0) return 0;
            if (f > 100) return 100;
            return f;
        }

        public void Add(double power, double fan, double temp)
        {
            if (power <= 1.0 || fan < 0 || fan > 100 || temp <= 1) return;
            samples.Enqueue(new double[] { power, fan, temp });
            while (samples.Count > Capacity) samples.Dequeue();
            Solve();
        }

        /// <summary>
        /// Least squares for y = A + B*p + C*f with a ridge term that pulls each
        /// coefficient toward its prior. Solved with Cramer's rule on the 3x3
        /// normal equations (the system is tiny and does not need a library).
        /// </summary>
        private void Solve()
        {
            double n = samples.Count;
            double sp = 0, sf = 0, st = 0, spp = 0, sff = 0, spf = 0, spt = 0, sft = 0;
            foreach (double[] s in samples)
            {
                double p = s[0], f = s[1], t = s[2];
                sp += p; sf += f; st += t;
                spp += p * p; sff += f * f; spf += p * f;
                spt += p * t; sft += f * t;
            }

            double a11 = n + RidgeA, a12 = sp, a13 = sf;
            double a21 = sp, a22 = spp + RidgeB, a23 = spf;
            double a31 = sf, a32 = spf, a33 = sff + RidgeC;
            // scale the ridge with the data so a couple of samples cannot drag a
            // coefficient to its clamp: the prior stays meaningful until enough
            // independent points exist to outweigh it
            double scale = Math.Max(spp, Math.Max(sff, 1.0));
            double ridgeB = Math.Max(RidgeB, 0.10 * scale);
            double ridgeC = Math.Max(RidgeC, 0.10 * scale);
            a22 = spp + ridgeB;
            a33 = sff + ridgeC;
            double b1 = st + RidgeA * PriorA;
            double b2 = spt + ridgeB * PriorB;
            double b3 = sft + ridgeC * PriorC;

            double det = a11 * (a22 * a33 - a23 * a32)
                       - a12 * (a21 * a33 - a23 * a31)
                       + a13 * (a21 * a32 - a22 * a31);
            if (Math.Abs(det) < 1e-9) return;

            double da = b1 * (a22 * a33 - a23 * a32)
                      - a12 * (b2 * a33 - a23 * b3)
                      + a13 * (b2 * a32 - a22 * b3);
            double db = a11 * (b2 * a33 - a23 * b3)
                      - b1 * (a21 * a33 - a23 * a31)
                      + a13 * (a21 * b3 - b2 * a31);
            double dc = a11 * (a22 * b3 - b2 * a32)
                      - a12 * (a21 * b3 - b2 * a31)
                      + b1 * (a21 * a32 - a22 * a31);

            double a = da / det, b = db / det, c = dc / det;
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(c)) return;
            if (double.IsInfinity(a) || double.IsInfinity(b) || double.IsInfinity(c)) return;

            // keep the coefficients inside physically plausible ranges
            if (b < MinB) b = MinB; else if (b > MaxB) b = MaxB;
            if (c > MaxC) c = MaxC; else if (c < MinC) c = MinC;
            if (a < 0) a = 0; else if (a > 80) a = 80;

            A = a; B = b; C = c;
        }

        /// <summary>
        /// Fan curve for a fixed power limit: straight line through the anchor
        /// (targetTemp, F*), flat-zero below the knee so idle still gets 0 RPM.
        /// Clamped by Curve itself; the rate limiter in the daemon handles the
        /// transition when the model updates.
        /// </summary>
        public Curve BuildFanCurve(double targetTemp, double powerWatts)
        {
            double fStar = AnchorFanFromLast(targetTemp, powerWatts);
            if (fStar < 0) fStar = AnchorFan(targetTemp, powerWatts);
            double slope = Slope;
            // three collinear knots: flat-zero far below the knee, the anchor at
            // the target temperature, and a saturated top above the driver zone
            double lo = targetTemp - 40.0;
            double hi = targetTemp + 20.0;
            double[] xs = new double[] { lo, targetTemp, hi };
            double[] ys = new double[] { fStar + (lo - targetTemp) * slope, fStar, fStar + (hi - targetTemp) * slope };
            for (int i = 0; i < ys.Length; i++)
            {
                if (ys[i] < 0) ys[i] = 0; else if (ys[i] > 100) ys[i] = 100;
            }
            if (ys[0] > ys[1]) ys[0] = ys[1];
            if (ys[2] < ys[1]) ys[2] = ys[1];
            Curve c = Curve.Build(xs, ys);
            return c;
        }

        public string Format()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "T={0:0.0}+{1:0.000}P{2:0.000}F (n={3})",
                A, B, C, samples.Count);
        }

        public string Serialize()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.####},{1:0.####},{2:0.####},{3},{4:0.####}",
                A, B, C, samples.Count, Anchor);
        }

        public static ThermalModel TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path).Trim();
                string[] parts = text.Split(',');
                if (parts.Length < 3) return null;
                double a, b, c;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)) return null;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return null;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out c)) return null;
                if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(c)) return null;
                if (b <= 0 || c >= 0) return null;
                ThermalModel m = new ThermalModel();
                m.A = a; m.B = b; m.C = c;
                double anchor;
                if (parts.Length >= 5 && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out anchor) &&
                    anchor > 0 && anchor <= 100)
                    m.Anchor = anchor;
                return m;
            }
            catch { return null; }
        }

        public bool Save(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, Serialize());
                return true;
            }
            catch { return false; }
        }
    }
}
