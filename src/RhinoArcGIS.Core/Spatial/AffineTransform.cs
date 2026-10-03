using System;

namespace RhinoArcGIS.Core.Spatial
{
    /// <summary>
    /// A 3D affine transform stored as a 3x3 linear part (row-major) plus a translation. Pure and
    /// fully unit-tested — it is the single geometric source of truth for georeferencing.
    /// Composition order: <c>A.Compose(B)</c> applies B first, then A (i.e. A∘B).
    /// </summary>
    public sealed class AffineTransform
    {
        // Row-major 3x3: _r[0.._2] = row 0, _r[3.._5] = row 1, _r[6.._8] = row 2.
        private readonly double[] _r;
        private readonly double[] _t;

        public AffineTransform(double[] linear3x3, double[] translation)
        {
            if (linear3x3 == null || linear3x3.Length != 9) throw new ArgumentException("linear must have 9 elements", nameof(linear3x3));
            if (translation == null || translation.Length != 3) throw new ArgumentException("translation must have 3 elements", nameof(translation));
            _r = (double[])linear3x3.Clone();
            _t = (double[])translation.Clone();
        }

        public static AffineTransform Identity =>
            new AffineTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, new double[] { 0, 0, 0 });

        public static AffineTransform Translation(Xyz v) =>
            new AffineTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, new double[] { v.X, v.Y, v.Z });

        public static AffineTransform Scale(double s) => Scale(s, s, s);

        public static AffineTransform Scale(double sx, double sy, double sz) =>
            new AffineTransform(new double[] { sx, 0, 0, 0, sy, 0, 0, 0, sz }, new double[] { 0, 0, 0 });

        /// <summary>Right-handed rotation about +Z by <paramref name="degrees"/> (CCW looking down).</summary>
        public static AffineTransform RotationZ(double degrees)
        {
            double a = degrees * Math.PI / 180.0;
            double c = Math.Cos(a), s = Math.Sin(a);
            return new AffineTransform(new double[] { c, -s, 0, s, c, 0, 0, 0, 1 }, new double[] { 0, 0, 0 });
        }

        /// <summary>
        /// Build from a row-major 4x4 matrix (e.g. a RhinoCommon <c>Transform</c>'s M00..M33). The
        /// bottom row is assumed affine [0 0 0 1]; any perspective terms are ignored.
        /// </summary>
        public static AffineTransform FromRowMajor4x4(double[] m)
        {
            if (m == null || m.Length != 16) throw new ArgumentException("expected 16 elements", nameof(m));
            var r = new double[]
            {
                m[0], m[1], m[2],
                m[4], m[5], m[6],
                m[8], m[9], m[10]
            };
            var t = new double[] { m[3], m[7], m[11] };
            return new AffineTransform(r, t);
        }

        public Xyz Apply(Xyz p)
        {
            double x = _r[0] * p.X + _r[1] * p.Y + _r[2] * p.Z + _t[0];
            double y = _r[3] * p.X + _r[4] * p.Y + _r[5] * p.Z + _t[1];
            double z = _r[6] * p.X + _r[7] * p.Y + _r[8] * p.Z + _t[2];
            return new Xyz(x, y, z);
        }

        /// <summary>Returns <c>this ∘ other</c>: apply <paramref name="other"/> first, then this.</summary>
        public AffineTransform Compose(AffineTransform other)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = _r[i * 3] * other._r[j] + _r[i * 3 + 1] * other._r[3 + j] + _r[i * 3 + 2] * other._r[6 + j];

            // t = R * other.t + this.t
            var t = new double[3];
            for (int i = 0; i < 3; i++)
                t[i] = _r[i * 3] * other._t[0] + _r[i * 3 + 1] * other._t[1] + _r[i * 3 + 2] * other._t[2] + _t[i];

            return new AffineTransform(r, t);
        }

        public AffineTransform Inverse()
        {
            double[] ri = Invert3x3(_r);
            // t' = -Rinv * t
            var t = new double[3];
            for (int i = 0; i < 3; i++)
                t[i] = -(ri[i * 3] * _t[0] + ri[i * 3 + 1] * _t[1] + ri[i * 3 + 2] * _t[2]);
            return new AffineTransform(ri, t);
        }

        /// <summary>Linear scale factor of the transform (norm of the first basis column); exact for
        /// uniform scale + rotation, approximate otherwise.</summary>
        public double ScaleFactor()
        {
            return Math.Sqrt(_r[0] * _r[0] + _r[3] * _r[3] + _r[6] * _r[6]);
        }

        private static double[] Invert3x3(double[] m)
        {
            double a = m[0], b = m[1], c = m[2];
            double d = m[3], e = m[4], f = m[5];
            double g = m[6], h = m[7], i = m[8];

            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            // Relative to the matrix's own size: a micron model scales by 1e-6, so its determinant
            // is 1e-18 and perfectly invertible.
            double size = 0;
            foreach (double v in m) size = Math.Max(size, Math.Abs(v));
            if (size == 0 || Math.Abs(det) < 1e-12 * size * size * size)
                throw new InvalidOperationException("Transform is singular and cannot be inverted.");
            double inv = 1.0 / det;

            return new double[]
            {
                (e * i - f * h) * inv, (c * h - b * i) * inv, (b * f - c * e) * inv,
                (f * g - d * i) * inv, (a * i - c * g) * inv, (c * d - a * f) * inv,
                (d * h - e * g) * inv, (b * g - a * h) * inv, (a * e - b * d) * inv
            };
        }
    }
}
