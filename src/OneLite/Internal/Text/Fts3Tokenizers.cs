namespace RecursiveAI.OneLite.Internal.Text;

/// <summary>
/// The <c>porter</c> tokenizer of fts3 and fts4 (sqlite.org/fts3.html, "Tokenizers"). Its token characters are the ASCII letters, digits and
/// underscore and every non-ASCII byte; tokens are folded to lower case (ASCII only) and, when they are three to twenty letters, stemmed.
/// A token with any other character is kept as it is.
/// </summary>
internal sealed class Fts3PorterTokenizer : FtsTokenizer
{
    private static bool IsTokenByte(byte b) => b >= 0x80 || b == (byte)'_' || (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');

    internal override void Tokenize(ReadOnlySpan<byte> text, TokenizeReason reason, TokenSink sink)
    {
        int i = 0;
        var buf = new List<byte>(32);
        while (i < text.Length)
        {
            if (!IsTokenByte(text[i]))
            {
                i++;
                continue;
            }

            int start = i;
            buf.Clear();
            bool letters = true;
            while (i < text.Length && IsTokenByte(text[i]))
            {
                byte b = text[i];
                if (b >= (byte)'A' && b <= (byte)'Z')
                {
                    b = (byte)(b + 32);
                }

                if (b < (byte)'a' || b > (byte)'z')
                {
                    letters = false;
                }

                buf.Add(b);
                i++;
            }

            byte[] tok = [.. buf];
            if (letters && tok.Length >= 3 && tok.Length <= 20)
            {
                tok = Porter.Stem(tok);
            }

            sink.Token(tok, start, i);
        }
    }
}
