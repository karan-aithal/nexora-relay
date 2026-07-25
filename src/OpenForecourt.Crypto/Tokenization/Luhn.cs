namespace OpenForecourt.Crypto.Tokenization;

/// <summary>The Luhn (mod-10) check used on PANs and PAN-shaped surrogate tokens.</summary>
public static class Luhn
{
    /// <summary>True when the digit string satisfies the Luhn checksum.</summary>
    public static bool IsValid(string digits)
    {
        ArgumentException.ThrowIfNullOrEmpty(digits);
        return Checksum(digits) == 0;
    }

    /// <summary>The Luhn sum modulo 10; zero means valid.</summary>
    public static int Checksum(ReadOnlySpan<char> digits)
    {
        int sum = 0;
        bool doubleIt = false; // rightmost digit (the check digit) is never doubled
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10;
    }
}
