namespace OpenSkiTime.Domain;

public static class TextRules
{
    // Text that every export (result XML, timing report XML, PDF) can carry. Tab, line feed and carriage return are
    // allowed; other C0 control characters (a pasted Word line break is U+000B), unpaired surrogates and the
    // noncharacters U+FFFE/U+FFFF are not.
    public static bool IsPortable(string? text)
    {
        if (text is null) { return true; }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if ((c < ' ' && c is not ('\t' or '\n' or '\r')) || char.IsSurrogate(c) || c is '￾' or '￿') { return false; }
        }
        return true;
    }
}
