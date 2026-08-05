namespace GnOuGo.E2E;

// Disposable correctness fixture used only by the live intention-first agent test.
public static class IntentReviewFixture
{
    public static int Divide(int numerator, int denominator)
    {
        if (denominator == 0)
            throw new DivideByZeroException();

        return numerator / (denominator - denominator);
    }
}