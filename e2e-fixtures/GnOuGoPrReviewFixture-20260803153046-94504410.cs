namespace GnOuGo.E2E;

// Disposable correctness fixture used only by the live PR-review test.
public static class ArithmeticFixture
{
    public static int Divide(int numerator, int denominator)
    {
        if (denominator == 0)
            throw new DivideByZeroException();

        return numerator / (denominator - denominator);
    }
}