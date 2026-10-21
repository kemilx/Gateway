namespace Gateway;

// Explicit conservative formats, not detectors or universal address/telephone validation.
internal static class SensitiveValueMasker
{
    private const string Redacted = "[REDACTED]";
    private static bool Digit(char c) => c is >= '0' and <= '9';
    private static bool Alphanumeric(char c) => Digit(c) || c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    internal static string Email(string value)
    {
        if (value.Length is < 1 or > 254) return Redacted;
        var at = value.IndexOf('@');
        if (at is < 1 or > 64 || at != value.LastIndexOf('@') || at == value.Length - 1) return Redacted;
        var local = value.AsSpan(0, at);
        if (local[0] == '.' || local[^1] == '.') return Redacted;
        for (var i = 0; i < local.Length; i++)
        {
            var c = local[i];
            if (c == '.')
            {
                if (i > 0 && local[i - 1] == '.') return Redacted;
            }
            else if (!Alphanumeric(c) && !"!#$%&'*+-/=?^_`{|}~".Contains(c)) return Redacted;
        }
        var domain = value.AsSpan(at + 1);
        var labelStart = 0;
        for (var i = 0; i <= domain.Length; i++)
        {
            if (i < domain.Length && domain[i] != '.') continue;
            var label = domain[labelStart..i];
            if (label.Length is < 1 or > 63 || !Alphanumeric(label[0]) || !Alphanumeric(label[^1])) return Redacted;
            foreach (var c in label)
                if (!Alphanumeric(c) && c != '-') return Redacted;
            labelStart = i + 1;
        }
        return "***@" + value[(at + 1)..];
    }

    internal static string Phone(string value)
    {
        if (value.Length is < 1 or > 64 || (!Digit(value[0]) && value[0] != '+') || !Digit(value[^1])) return Redacted;
        var digits = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (Digit(c)) digits++;
            else if (c is not (' ' or '-') && !(i == 0 && c == '+')) return Redacted;
        }
        if (digits is < 5 or > 15) return Redacted;
        var remaining = digits - 4;
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length && remaining > 0; i++)
            if (Digit(chars[i])) { chars[i] = '*'; remaining--; }
        return new string(chars);
    }
}
