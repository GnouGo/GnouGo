using System.Numerics;

namespace GnOuGo.Flow.Core.Runtime;

public sealed partial class LLMUsageBudgetScope
{
    private static readonly BigInteger MaximumDecimalCostMantissa = (BigInteger.One << 96) - 1;

    // Decimal +/- can round between removing a reservation and adding its receipt.
    // Accumulate exact scaled units, then round only the nonnegative aggregate upward
    // to the nearest representable decimal. Immutable individual charges stay intact.
    private static BigInteger ExactCostUnits(decimal value)
    {
        var bits = decimal.GetBits(value);
        var mantissa = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        var scale = (bits[3] >> 16) & 0xff;
        var units = mantissa * BigInteger.Pow(10, 28 - scale);
        return bits[3] < 0 ? -units : units;
    }

    internal static decimal SumCostConservatively(params decimal[] amounts)
    {
        var units = amounts.Aggregate(BigInteger.Zero, (sum, amount) => sum + ExactCostUnits(amount));
        if (units.Sign < 0) throw new OverflowException("Cumulative LLM cost cannot be negative.");
        var scale = 28;
        while (units > MaximumDecimalCostMantissa && scale > 0)
        {
            units = BigInteger.DivRem(units, 10, out var remainder);
            if (!remainder.IsZero) units++;
            scale--;
        }
        if (units > MaximumDecimalCostMantissa) throw new OverflowException("Cumulative LLM cost exceeds decimal capacity.");
        while (scale > 0 && units % 10 == 0) { units /= 10; scale--; }
        return new decimal(unchecked((int)(uint)(units & uint.MaxValue)),
            unchecked((int)(uint)((units >> 32) & uint.MaxValue)), unchecked((int)(uint)(units >> 64)), false, (byte)scale);
    }

    internal static bool CostSumExceeds(decimal limit, params decimal[] amounts)
        => amounts.Aggregate(BigInteger.Zero, (sum, amount) => sum + ExactCostUnits(amount)) > ExactCostUnits(limit);
}
