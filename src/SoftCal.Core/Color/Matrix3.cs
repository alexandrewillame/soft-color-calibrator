namespace SoftCal.Core.Color;

/// <summary>An immutable 3×3 matrix in row-major order (M<sub>row,column</sub>).</summary>
public readonly record struct Matrix3(
    double M11, double M12, double M13,
    double M21, double M22, double M23,
    double M31, double M32, double M33)
{
    public static Matrix3 Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public Vec3 Row1 => new(M11, M12, M13);

    public Vec3 Row2 => new(M21, M22, M23);

    public Vec3 Row3 => new(M31, M32, M33);

    public Vec3 Column1 => new(M11, M21, M31);

    public Vec3 Column2 => new(M12, M22, M32);

    public Vec3 Column3 => new(M13, M23, M33);

    public double Determinant =>
        (M11 * ((M22 * M33) - (M23 * M32)))
        - (M12 * ((M21 * M33) - (M23 * M31)))
        + (M13 * ((M21 * M32) - (M22 * M31)));

    public static Matrix3 Diagonal(Vec3 d) => new(d.X, 0, 0, 0, d.Y, 0, 0, 0, d.Z);

    public static Matrix3 FromRows(Vec3 r1, Vec3 r2, Vec3 r3) =>
        new(r1.X, r1.Y, r1.Z, r2.X, r2.Y, r2.Z, r3.X, r3.Y, r3.Z);

    public static Matrix3 FromColumns(Vec3 c1, Vec3 c2, Vec3 c3) =>
        new(c1.X, c2.X, c3.X, c1.Y, c2.Y, c3.Y, c1.Z, c2.Z, c3.Z);

    public Matrix3 Transpose() => new(M11, M21, M31, M12, M22, M32, M13, M23, M33);

    /// <summary>Returns the inverse, or throws when the matrix is singular (or numerically close to it).</summary>
    public Matrix3 Inverse()
    {
        var det = Determinant;
        var scale = Math.Max(Row1.Map(Math.Abs).Max, Math.Max(Row2.Map(Math.Abs).Max, Row3.Map(Math.Abs).Max));
        if (double.IsNaN(det) || scale == 0 || Math.Abs(det) <= 1e-12 * scale * scale * scale)
        {
            throw new InvalidOperationException("The matrix is singular and cannot be inverted.");
        }

        var inv = 1.0 / det;
        return new(
            ((M22 * M33) - (M23 * M32)) * inv, ((M13 * M32) - (M12 * M33)) * inv, ((M12 * M23) - (M13 * M22)) * inv,
            ((M23 * M31) - (M21 * M33)) * inv, ((M11 * M33) - (M13 * M31)) * inv, ((M13 * M21) - (M11 * M23)) * inv,
            ((M21 * M32) - (M22 * M31)) * inv, ((M12 * M31) - (M11 * M32)) * inv, ((M11 * M22) - (M12 * M21)) * inv);
    }

    public static Matrix3 operator *(Matrix3 a, Matrix3 b) => FromRows(
        new(a.Row1.Dot(b.Column1), a.Row1.Dot(b.Column2), a.Row1.Dot(b.Column3)),
        new(a.Row2.Dot(b.Column1), a.Row2.Dot(b.Column2), a.Row2.Dot(b.Column3)),
        new(a.Row3.Dot(b.Column1), a.Row3.Dot(b.Column2), a.Row3.Dot(b.Column3)));

    public static Vec3 operator *(Matrix3 m, Vec3 v) => new(m.Row1.Dot(v), m.Row2.Dot(v), m.Row3.Dot(v));

    public static Matrix3 operator *(Matrix3 m, double s) => new(
        m.M11 * s, m.M12 * s, m.M13 * s,
        m.M21 * s, m.M22 * s, m.M23 * s,
        m.M31 * s, m.M32 * s, m.M33 * s);
}
