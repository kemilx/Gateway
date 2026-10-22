namespace Gateway;

// Whole original decoded JSON string only; no issuer rules or free-text discovery.
internal static class SecondaryPanDetector
{
    internal static bool IsMatch(string value)
    {
        if (value.Length is < 13 or > 37) return false;
        var digits = 0;
        var sum = 0;
        var doubleDigit = false;
        var needsDigit = true;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            var c = value[i];
            if (c is >= '0' and <= '9')
            {
                digits++;
                var digit = c - '0';
                if (doubleDigit) { digit *= 2; if (digit > 9) digit -= 9; }
                sum += digit;
                doubleDigit = !doubleDigit;
                needsDigit = false;
            }
            else if (c is ' ' or '-' && !needsDigit && i > 0) needsDigit = true;
            else return false;
        }
        return !needsDigit && digits is >= 13 and <= 19 && sum % 10 == 0;
    }
}
