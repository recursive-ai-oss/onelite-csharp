using RecursiveAI.OneLite.Storage.Record;

namespace RecursiveAI.OneLite.Vm;

internal static partial class Scalar
{
    private static void SetMathResult(ref Mem dst, double r)
    {
        // A NaN result is stored as NULL (datatype3: NaN is NULL); infinities stay.
        if (double.IsNaN(r))
        {
            Values.SetNull(ref dst);
        }
        else
        {
            Values.SetReal(ref dst, r);
        }
    }

    /// <summary>The math functions (lang_mathfunc): a NULL or non-numeric argument gives NULL, a result outside the function's domain gives NULL.</summary>
    private static void MathFunc(FuncId id, ReadOnlySpan<Mem> args, ref Mem dst)
    {
        if (id == FuncId.Pi)
        {
            Values.SetReal(ref dst, Math.PI);
            return;
        }

        StorageClass k0 = NumericArg(args[0], out long i0, out double d0);
        if (k0 == StorageClass.Null)
        {
            Values.SetNull(ref dst);
            return;
        }

        double x = k0 == StorageClass.Integral ? i0 : d0;
        if (id is FuncId.Ceil or FuncId.Floor or FuncId.Trunc)
        {
            // lang_mathfunc: an integer argument comes back as the same integer.
            if (k0 == StorageClass.Integral)
            {
                Values.SetInt(ref dst, i0);
                return;
            }

            Values.SetReal(ref dst, id == FuncId.Ceil ? Math.Ceiling(x) : id == FuncId.Floor ? Math.Floor(x) : Math.Truncate(x));
            return;
        }

        if (id is FuncId.Atan2 or FuncId.Mod or FuncId.Pow || (id == FuncId.Log && args.Length == 2))
        {
            double y;
            if (id == FuncId.Log)
            {
                // Observed: log(B, X) reads X with the lenient text-to-real rule ('3x' is 3, a blob reads as its text), while B and the
                // arguments of atan2, mod and pow must be numbers outright.
                if (args[1].Kind == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                y = Values.ToReal(args[1]);
            }
            else
            {
                StorageClass k1 = NumericArg(args[1], out long i1, out double d1);
                if (k1 == StorageClass.Null)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                y = k1 == StorageClass.Integral ? i1 : d1;
            }

            switch (id)
            {
                case FuncId.Atan2:
                    SetMathResult(ref dst, Math.Atan2(x, y));
                    return;
                case FuncId.Mod:
                    SetMathResult(ref dst, CFmod(x, y));
                    return;
                case FuncId.Pow:
                    SetMathResult(ref dst, Math.Pow(x, y));
                    return;
                default:
                    // log(B, X): the logarithm of X to base B; observed: B must exceed 1 and X must be positive.
                    if (x <= 1 || y <= 0)
                    {
                        Values.SetNull(ref dst);
                        return;
                    }

                    SetMathResult(ref dst, Math.Log(y) / Math.Log(x));
                    return;
            }
        }

        double r;
        switch (id)
        {
            case FuncId.Acos: r = Math.Acos(x); break;
            case FuncId.Acosh: r = Math.Acosh(x); break;
            case FuncId.Asin: r = Math.Asin(x); break;
            case FuncId.Asinh: r = Math.Asinh(x); break;
            case FuncId.Atan: r = Math.Atan(x); break;
            case FuncId.Atanh: r = Math.Atanh(x); break;
            case FuncId.Cos: r = Math.Cos(x); break;
            case FuncId.Cosh: r = Math.Cosh(x); break;
            case FuncId.Degrees: r = x * (180.0 / Math.PI); break;
            case FuncId.Exp: r = Math.Exp(x); break;
            case FuncId.Radians: r = x * (Math.PI / 180.0); break;
            case FuncId.Sin: r = Math.Sin(x); break;
            case FuncId.Sinh: r = Math.Sinh(x); break;
            case FuncId.Sqrt: r = Math.Sqrt(x); break;
            case FuncId.Tan: r = Math.Tan(x); break;
            case FuncId.Tanh: r = Math.Tanh(x); break;
            case FuncId.Log:
            case FuncId.Log10:
                if (x <= 0)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                r = Math.Log10(x);
                break;
            case FuncId.Ln:
                if (x <= 0)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                r = Math.Log(x);
                break;
            case FuncId.Log2:
                if (x <= 0)
                {
                    Values.SetNull(ref dst);
                    return;
                }

                r = Math.Log2(x);
                break;
            default:
                throw new InvalidOperationException("not a math function");
        }

        SetMathResult(ref dst, r);
    }

    /// <summary>C's fmod: the remainder with the sign of the dividend; NaN for a zero divisor or an infinite dividend.</summary>
    private static double CFmod(double x, double y) => x % y;
}
