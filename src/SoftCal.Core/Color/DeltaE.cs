namespace SoftCal.Core.Color;

/// <summary>Color-difference formulas.</summary>
public static class DeltaE
{
    private static readonly double Pow25To7 = Math.Pow(25, 7);

    /// <summary>CIE 1976 ΔE*ab (Euclidean distance in L*a*b*).</summary>
    public static double Cie76(Lab first, Lab second)
    {
        var dl = first.L - second.L;
        var da = first.A - second.A;
        var db = first.B - second.B;
        return Math.Sqrt((dl * dl) + (da * da) + (db * db));
    }

    /// <summary>
    /// CIEDE2000 color difference, implemented as in Sharma, Wu and Dalal (2005),
    /// "The CIEDE2000 color-difference formula: implementation notes, supplementary test data, and mathematical observations".
    /// </summary>
    public static double Ciede2000(Lab first, Lab second, double kL = 1, double kC = 1, double kH = 1)
    {
        var c1 = first.Chroma;
        var c2 = second.Chroma;
        var cBar = (c1 + c2) / 2;
        var cBar7 = Math.Pow(cBar, 7);
        var g = 0.5 * (1 - Math.Sqrt(cBar7 / (cBar7 + Pow25To7)));

        var a1 = (1 + g) * first.A;
        var a2 = (1 + g) * second.A;
        var c1p = Math.Sqrt((a1 * a1) + (first.B * first.B));
        var c2p = Math.Sqrt((a2 * a2) + (second.B * second.B));
        var h1p = HueDegrees(a1, first.B);
        var h2p = HueDegrees(a2, second.B);

        var dLp = second.L - first.L;
        var dCp = c2p - c1p;

        double dhp;
        if (c1p * c2p == 0)
        {
            dhp = 0;
        }
        else
        {
            dhp = h2p - h1p;
            if (dhp > 180)
            {
                dhp -= 360;
            }
            else if (dhp < -180)
            {
                dhp += 360;
            }
        }

        var dHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(ToRadians(dhp / 2));

        var lBarP = (first.L + second.L) / 2;
        var cBarP = (c1p + c2p) / 2;

        double hBarP;
        if (c1p * c2p == 0)
        {
            hBarP = h1p + h2p;
        }
        else if (Math.Abs(h1p - h2p) <= 180)
        {
            hBarP = (h1p + h2p) / 2;
        }
        else if (h1p + h2p < 360)
        {
            hBarP = (h1p + h2p + 360) / 2;
        }
        else
        {
            hBarP = (h1p + h2p - 360) / 2;
        }

        var t = 1
            - (0.17 * Math.Cos(ToRadians(hBarP - 30)))
            + (0.24 * Math.Cos(ToRadians(2 * hBarP)))
            + (0.32 * Math.Cos(ToRadians((3 * hBarP) + 6)))
            - (0.20 * Math.Cos(ToRadians((4 * hBarP) - 63)));

        var dTheta = 30 * Math.Exp(-Math.Pow((hBarP - 275) / 25, 2));
        var cBarP7 = Math.Pow(cBarP, 7);
        var rC = 2 * Math.Sqrt(cBarP7 / (cBarP7 + Pow25To7));
        var lMinus50Sq = (lBarP - 50) * (lBarP - 50);
        var sL = 1 + (0.015 * lMinus50Sq / Math.Sqrt(20 + lMinus50Sq));
        var sC = 1 + (0.045 * cBarP);
        var sH = 1 + (0.015 * cBarP * t);
        var rT = -Math.Sin(ToRadians(2 * dTheta)) * rC;

        var l = dLp / (kL * sL);
        var c = dCp / (kC * sC);
        var h = dHp / (kH * sH);
        return Math.Sqrt((l * l) + (c * c) + (h * h) + (rT * c * h));
    }

    /// <summary>Δu′v′ between two chromaticities.</summary>
    public static double UvPrime(Chromaticity first, Chromaticity second) =>
        first.ToUvPrime().DistanceTo(second.ToUvPrime());

    private static double HueDegrees(double a, double b)
    {
        if (a == 0 && b == 0)
        {
            return 0;
        }

        var h = Math.Atan2(b, a) * 180 / Math.PI;
        return h < 0 ? h + 360 : h;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
