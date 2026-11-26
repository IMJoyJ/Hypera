using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hypera.Rendering;

/// <summary>
/// Single button/non-button segment produced by <see cref="ButtonStringCreator.SplitButton"/>.
/// Mirrors original Emuera's <c>MinorShift.Emuera.UI.Game.ButtonPrimitive</c>.
/// </summary>
internal readonly record struct ButtonPrimitive(string Text, long Input, bool CanSelect);

/// <summary>
/// Faithful implementation of original Emuera.NET's <c>ButtonStringCreator</c> automatic bracket-button
/// recognition algorithm across an entire accumulated print run on a console line.
/// </summary>
internal static partial class ButtonStringCreator
{
    [GeneratedRegex(@"^\[\s*([0][xXbB])?[+-]?[0-9]+([eEpP][0-9]+)?\s*\]$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericBracketRegex();

    public static List<ButtonPrimitive> SplitButton(string printBuffer)
    {
        string text = printBuffer ?? string.Empty;
        var ret = new List<ButtonPrimitive>();

        if (text.Length > 0 && text.Contains('[') && text.Contains(']'))
        {
            List<string>? tokens = Lex(text);
            if (tokens is not null)
            {
                bool hasTextBeforeFirstBtn = false;
                bool hasTextAfterLastBtn = false;
                int btnCount = 0;
                long lastInput = 0L;

                for (int i = 0; i < tokens.Count; i++)
                {
                    string tok = tokens[i];
                    if (tok.Length == 0 || IsEmueraWhiteSpace(tok[0]))
                    {
                        continue;
                    }

                    if (TryParseButtonCore(tok, out long parsed))
                    {
                        lastInput = parsed;
                        btnCount++;
                        hasTextAfterLastBtn = false;
                        continue;
                    }

                    hasTextAfterLastBtn = true;
                    if (btnCount == 0)
                    {
                        hasTextBeforeFirstBtn = true;
                    }
                }

                // Original Emuera rule: 0 or 1 bracketed number in the entire print run
                // makes the ENTIRE run (all PRINT/PRINTFORM/SETCOLOR/SETFONT fragments together)
                // either plain text (0) or ONE single clickable button (1)!
                if (btnCount <= 1)
                {
                    ret.Add(new ButtonPrimitive(text, btnCount >= 1 ? lastInput : 0L, btnCount >= 1));
                    return ret;
                }

                bool prefixMode = !hasTextBeforeFirstBtn && hasTextAfterLastBtn;
                bool suffixMode = hasTextBeforeFirstBtn && !hasTextAfterLastBtn;
                bool mixedMode = !prefixMode && !suffixMode;

                bool canSelect = false;
                long currentInput = 0L;
                int state = 0; // bit 0 = has button token, bit 1 = has non-button text token
                var buffer = new StringBuilder(text.Length);

                void Reduce()
                {
                    if (buffer.Length != 0)
                    {
                        ret.Add(new ButtonPrimitive(buffer.ToString(), currentInput, canSelect));
                        buffer.Clear();
                        canSelect = false;
                        currentInput = 0L;
                    }
                }

                for (int j = 0; j < tokens.Count; j++)
                {
                    string tok = tokens[j];
                    if (tok.Length == 0)
                    {
                        continue;
                    }

                    if (IsEmueraWhiteSpace(tok[0]))
                    {
                        if ((state & 3) == 3 && mixedMode && tok.Length >= 2)
                        {
                            Reduce();
                            buffer.Append(tok);
                            state = 0;
                        }
                        else
                        {
                            buffer.Append(tok);
                        }
                    }
                    else if (TryParseButtonCore(tok, out long parsed))
                    {
                        if ((state & 1) == 1 || prefixMode)
                        {
                            Reduce();
                            buffer.Append(tok);
                            currentInput = parsed;
                            canSelect = true;
                            state = 1;
                        }
                        else if (suffixMode)
                        {
                            buffer.Append(tok);
                            currentInput = parsed;
                            canSelect = true;
                            Reduce();
                            state = 0;
                        }
                        else
                        {
                            buffer.Append(tok);
                            currentInput = parsed;
                            canSelect = true;
                            state = 1;
                        }
                    }
                    else
                    {
                        buffer.Append(tok);
                        state |= 2;
                    }
                }

                Reduce();
                return ret;
            }
        }

        ret.Add(new ButtonPrimitive(text, 0L, false));
        return ret;
    }

    private static List<string>? Lex(string text)
    {
        var tokens = new List<string>();
        int bracketDepth = 0;
        int startIndex = 0;
        int pos = 0;

        void Reduce()
        {
            if (pos != startIndex)
            {
                tokens.Add(text.Substring(startIndex, pos - startIndex));
                startIndex = pos;
            }
        }

        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '[')
            {
                if (bracketDepth == 1)
                {
                    return null;
                }

                Reduce();
                bracketDepth = 1;
                pos++;
            }
            else if (c == ']')
            {
                if (bracketDepth != 1)
                {
                    return null;
                }

                pos++;
                Reduce();
                bracketDepth = 0;
            }
            else if (bracketDepth == 0 && IsEmueraWhiteSpace(c))
            {
                Reduce();
                while (pos < text.Length && IsEmueraWhiteSpace(text[pos]))
                {
                    pos++;
                }

                Reduce();
            }
            else
            {
                pos++;
            }
        }

        Reduce();
        return tokens;
    }

    private static bool IsEmueraWhiteSpace(char c) => c is ' ' or '\t' or '\u3000';

    private static bool TryParseButtonCore(string token, out long value)
    {
        value = 0L;
        if (token.Length < 3 || token[0] != '[' || token[^1] != ']')
        {
            return false;
        }

        if (!NumericBracketRegex().IsMatch(token))
        {
            return false;
        }

        ReadOnlySpan<char> inner = token.AsSpan(1, token.Length - 2).Trim();
        if (inner.IsEmpty)
        {
            return false;
        }

        try
        {
            // Hex literal (0x... / 0X...)
            if (inner.Length > 2 && inner[0] == '0' && inner[1] is 'x' or 'X')
            {
                ReadOnlySpan<char> hex = inner[2..];
                bool neg = false;
                if (!hex.IsEmpty && hex[0] is '+' or '-')
                {
                    neg = hex[0] == '-';
                    hex = hex[1..];
                }

                if (!long.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long hval))
                {
                    return false;
                }

                value = neg ? checked(-hval) : hval;
                return true;
            }

            // Binary literal (0b... / 0B...)
            if (inner.Length > 2 && inner[0] == '0' && inner[1] is 'b' or 'B')
            {
                ReadOnlySpan<char> bin = inner[2..];
                bool neg = false;
                if (!bin.IsEmpty && bin[0] is '+' or '-')
                {
                    neg = bin[0] == '-';
                    bin = bin[1..];
                }

                long bval = Convert.ToInt64(bin.ToString(), 2);
                value = neg ? checked(-bval) : bval;
                return true;
            }

            // Optional Emuera integer exponent suffix (e.g. 1p3 = 1<<3, 2e2 = 200)
            int expIdx = inner.IndexOfAny(['e', 'E', 'p', 'P']);
            if (expIdx > 0)
            {
                char expKind = inner[expIdx];
                if (!long.TryParse(inner[..expIdx], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long mantissa) ||
                    !int.TryParse(inner[(expIdx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int exp))
                {
                    return false;
                }

                if (expKind is 'p' or 'P')
                {
                    if (exp < 0 || exp >= 63)
                    {
                        return false;
                    }

                    value = checked(mantissa * (1L << exp));
                    return true;
                }

                long pow10 = 1L;
                for (int k = 0; k < exp; k++)
                {
                    pow10 = checked(pow10 * 10L);
                }

                value = checked(mantissa * pow10);
                return true;
            }

            return long.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
