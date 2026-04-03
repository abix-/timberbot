using Xunit;
using Timberbot;

namespace Timberbot.Tests
{
    public class JsonEscapeTests
    {
        [Fact]
        public void Null_ReturnsEmpty() => Assert.Equal("", TimberbotPure.JsonEscape(null));

        [Fact]
        public void Empty_ReturnsEmpty() => Assert.Equal("", TimberbotPure.JsonEscape(""));

        [Fact]
        public void Normal_Unchanged() => Assert.Equal("hello", TimberbotPure.JsonEscape("hello"));

        [Fact]
        public void Backslash_Escaped() => Assert.Equal("a\\\\b", TimberbotPure.JsonEscape("a\\b"));

        [Fact]
        public void Quote_Escaped() => Assert.Equal("say \\\"hi\\\"", TimberbotPure.JsonEscape("say \"hi\""));

        [Fact]
        public void Newline_Escaped() => Assert.Equal("a\\nb", TimberbotPure.JsonEscape("a\nb"));

        [Fact]
        public void Tab_Escaped() => Assert.Equal("a\\tb", TimberbotPure.JsonEscape("a\tb"));

        [Fact]
        public void CarriageReturn_Escaped() => Assert.Equal("a\\rb", TimberbotPure.JsonEscape("a\rb"));

        [Fact]
        public void LongString_Truncated()
        {
            var input = new string('x', 2500);
            var result = TimberbotPure.JsonEscape(input);
            Assert.EndsWith("...(truncated)", result);
            Assert.True(result.Length < 2500);
        }

        [Fact]
        public void ExactlyAtLimit_NotTruncated()
        {
            var input = new string('x', 2000);
            var result = TimberbotPure.JsonEscape(input);
            Assert.Equal(2000, result.Length);
            Assert.DoesNotContain("truncated", result);
        }
    }

    public class IsCodexBinaryTests
    {
        [Theory]
        [InlineData("codex", true)]
        [InlineData("Codex", true)]
        [InlineData("CODEX", true)]
        [InlineData("codex.exe", true)]
        [InlineData("claude", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("  codex  ", true)]
        public void DetectsCodex(string input, bool expected) =>
            Assert.Equal(expected, TimberbotPure.IsCodexBinary(input));
    }

    public class QuoteArgTests
    {
        [Fact]
        public void Null_QuotedEmpty() => Assert.Equal("\"\"", TimberbotPure.QuoteArg(null));

        [Fact]
        public void Empty_QuotedEmpty() => Assert.Equal("\"\"", TimberbotPure.QuoteArg(""));

        [Fact]
        public void Normal_Quoted() => Assert.Equal("\"hello\"", TimberbotPure.QuoteArg("hello"));

        [Fact]
        public void Backslash_Escaped() => Assert.Equal("\"a\\\\b\"", TimberbotPure.QuoteArg("a\\b"));

        [Fact]
        public void InnerQuote_Escaped() => Assert.Equal("\"say \\\"hi\\\"\"", TimberbotPure.QuoteArg("say \"hi\""));
    }

    public class ShellQuoteArgTests
    {
        [Fact]
        public void Null_QuotedEmpty() => Assert.Equal("''", TimberbotPure.ShellQuoteArg(null));

        [Fact]
        public void Empty_QuotedEmpty() => Assert.Equal("''", TimberbotPure.ShellQuoteArg(""));

        [Fact]
        public void Normal_Quoted() => Assert.Equal("'hello'", TimberbotPure.ShellQuoteArg("hello"));

        [Fact]
        public void SingleQuote_Escaped() =>
            Assert.Equal("'it'\"'\"'s'", TimberbotPure.ShellQuoteArg("it's"));
    }

    public class ParseOrientationTests
    {
        [Theory]
        [InlineData("south", 0)]
        [InlineData("west", 1)]
        [InlineData("north", 2)]
        [InlineData("east", 3)]
        [InlineData("SOUTH", 0)]
        [InlineData("North", 2)]
        [InlineData(" north ", 2)]
        [InlineData("invalid", -1)]
        public void ParsesDirection(string input, int expected) =>
            Assert.Equal(expected, TimberbotPure.ParseOrientation(input));

        [Fact]
        public void Null_ReturnsSouth() => Assert.Equal(0, TimberbotPure.ParseOrientation(null));

        [Fact]
        public void Empty_ReturnsSouth() => Assert.Equal(0, TimberbotPure.ParseOrientation(""));
    }

    public class CanonicalNameTests
    {
        [Fact]
        public void RemovesCloneSuffix() => Assert.Equal("Path", TimberbotPure.CanonicalName("Path(Clone)"));

        [Fact]
        public void NoSuffix_Unchanged() => Assert.Equal("Path", TimberbotPure.CanonicalName("Path"));

        [Fact]
        public void Trims_Whitespace() => Assert.Equal("Path", TimberbotPure.CanonicalName("  Path  "));

        [Fact]
        public void Empty_ReturnsEmpty() => Assert.Equal("", TimberbotPure.CanonicalName(""));
    }

    public class CleanNameTests
    {
        [Fact]
        public void RemovesFactionSuffix() =>
            Assert.Equal("Lumberjack", TimberbotPure.CleanName("Lumberjack.Folktails", ".Folktails"));

        [Fact]
        public void RemovesCloneAndFaction() =>
            Assert.Equal("Lumberjack", TimberbotPure.CleanName("Lumberjack.Folktails(Clone)", ".Folktails"));

        [Fact]
        public void NullSuffix_JustCleansClone() =>
            Assert.Equal("Path", TimberbotPure.CleanName("Path(Clone)", null));

        [Fact]
        public void EmptySuffix_JustCleansClone() =>
            Assert.Equal("Path", TimberbotPure.CleanName("Path(Clone)", ""));
    }

    public class ValuesEqualTests
    {
        [Fact]
        public void BothNull_Equal() => Assert.True(TimberbotPure.ValuesEqual(null, null));

        [Fact]
        public void OneNull_NotEqual() => Assert.False(TimberbotPure.ValuesEqual(null, 1));

        [Fact]
        public void SameInt_Equal() => Assert.True(TimberbotPure.ValuesEqual(1, 1));

        [Fact]
        public void DifferentInt_NotEqual() => Assert.False(TimberbotPure.ValuesEqual(1, 2));

        [Fact]
        public void IntFloat_CloseEnough() => Assert.True(TimberbotPure.ValuesEqual(1, 1.00005));

        [Fact]
        public void IntFloat_TooFar() => Assert.False(TimberbotPure.ValuesEqual(1, 1.001));

        [Fact]
        public void SameString_Equal() => Assert.True(TimberbotPure.ValuesEqual("a", "a"));

        [Fact]
        public void DifferentString_NotEqual() => Assert.False(TimberbotPure.ValuesEqual("a", "b"));
    }

    public class TryGetNumericTests
    {
        [Fact]
        public void Int_Converts()
        {
            Assert.True(TimberbotPure.TryGetNumeric(42, out var n));
            Assert.Equal(42.0, n);
        }

        [Fact]
        public void BoolTrue_IsOne()
        {
            Assert.True(TimberbotPure.TryGetNumeric(true, out var n));
            Assert.Equal(1.0, n);
        }

        [Fact]
        public void BoolFalse_IsZero()
        {
            Assert.True(TimberbotPure.TryGetNumeric(false, out var n));
            Assert.Equal(0.0, n);
        }

        [Fact]
        public void Null_ReturnsFalse() => Assert.False(TimberbotPure.TryGetNumeric(null, out _));

        [Fact]
        public void String_Number_Converts()
        {
            // string implements IConvertible
            Assert.True(TimberbotPure.TryGetNumeric("3.14", out var n));
            Assert.Equal(3.14, n, 2);
        }
    }

    public class CompareValuesTests
    {
        [Fact]
        public void Ints_Comparable()
        {
            var result = TimberbotPure.CompareValues(5, 3, out var comparable);
            Assert.True(comparable);
            Assert.True(result > 0);
        }

        [Fact]
        public void Strings_Comparable()
        {
            var result = TimberbotPure.CompareValues("a", "b", out var comparable);
            Assert.True(comparable);
            Assert.True(result < 0);
        }

        [Fact]
        public void Incomparable_ReturnsFalse()
        {
            TimberbotPure.CompareValues(new object(), new object(), out var comparable);
            Assert.False(comparable);
        }
    }

    public class EvaluateAssertionTests
    {
        [Fact]
        public void Eq_True() => Assert.True(TimberbotPure.EvaluateAssertion(1, "eq", 1, out _));

        [Fact]
        public void Eq_False() => Assert.False(TimberbotPure.EvaluateAssertion(1, "eq", 2, out _));

        [Fact]
        public void Neq_True() => Assert.True(TimberbotPure.EvaluateAssertion(1, "neq", 2, out _));

        [Fact]
        public void Null_True() => Assert.True(TimberbotPure.EvaluateAssertion(null, "null", null, out _));

        [Fact]
        public void Null_False() => Assert.False(TimberbotPure.EvaluateAssertion(1, "null", null, out _));

        [Fact]
        public void Notnull_True() => Assert.True(TimberbotPure.EvaluateAssertion(1, "notnull", null, out _));

        [Fact]
        public void Gt_True() => Assert.True(TimberbotPure.EvaluateAssertion(5, "gt", 3, out _));

        [Fact]
        public void Gt_False() => Assert.False(TimberbotPure.EvaluateAssertion(3, "gt", 5, out _));

        [Fact]
        public void Gte_Equal() => Assert.True(TimberbotPure.EvaluateAssertion(3, "gte", 3, out _));

        [Fact]
        public void Lt_True() => Assert.True(TimberbotPure.EvaluateAssertion(3, "lt", 5, out _));

        [Fact]
        public void Lte_Equal() => Assert.True(TimberbotPure.EvaluateAssertion(3, "lte", 3, out _));

        [Fact]
        public void UnknownOp_FalseWithDetail()
        {
            var result = TimberbotPure.EvaluateAssertion(1, "xyz", 2, out var detail);
            Assert.False(result);
            Assert.Contains("unknown op", detail);
        }

        [Fact]
        public void Gt_Incomparable_FalseWithDetail()
        {
            var result = TimberbotPure.EvaluateAssertion(new object(), "gt", new object(), out var detail);
            Assert.False(result);
            Assert.Equal("values not comparable", detail);
        }
    }

    public class NormalizeValueTests
    {
        [Fact]
        public void Normal_Trimmed() => Assert.Equal("hello", TimberbotPure.NormalizeValue("  hello  ", "def"));

        [Fact]
        public void Null_ReturnsFallback() => Assert.Equal("def", TimberbotPure.NormalizeValue(null, "def"));

        [Fact]
        public void Empty_ReturnsFallback() => Assert.Equal("def", TimberbotPure.NormalizeValue("", "def"));

        [Fact]
        public void Whitespace_ReturnsFallback() => Assert.Equal("def", TimberbotPure.NormalizeValue("   ", "def"));
    }

    public class NormalizeBoolStringTests
    {
        [Fact]
        public void True_ReturnsTrue() => Assert.Equal("true", TimberbotPure.NormalizeBoolString("true", false));

        [Fact]
        public void False_ReturnsFalse() => Assert.Equal("false", TimberbotPure.NormalizeBoolString("false", true));

        [Fact]
        public void Null_ReturnsFallback() => Assert.Equal("true", TimberbotPure.NormalizeBoolString(null, true));

        [Fact]
        public void Garbage_ReturnsTrue() =>
            Assert.Equal("true", TimberbotPure.NormalizeBoolString("garbage", false));
    }

    public class NormalizeIntStringTests
    {
        [Fact]
        public void Valid_ReturnsValue() => Assert.Equal("42", TimberbotPure.NormalizeIntString("42", 10, 0));

        [Fact]
        public void BelowMin_ReturnsFallback() => Assert.Equal("10", TimberbotPure.NormalizeIntString("-5", 10, 0));

        [Fact]
        public void AtMin_ReturnsValue() => Assert.Equal("0", TimberbotPure.NormalizeIntString("0", 10, 0));

        [Fact]
        public void Null_ReturnsFallback() => Assert.Equal("10", TimberbotPure.NormalizeIntString(null, 10, 0));

        [Fact]
        public void NotANumber_ReturnsFallback() => Assert.Equal("10", TimberbotPure.NormalizeIntString("abc", 10, 0));
    }

    public class NormalizeDoubleStringTests
    {
        [Fact]
        public void Valid_ReturnsValue() => Assert.Equal("1.5", TimberbotPure.NormalizeDoubleString("1.5", 0.5, 0.0));

        [Fact]
        public void BelowMin_ReturnsFallback() =>
            Assert.Equal("0.5", TimberbotPure.NormalizeDoubleString("-1.0", 0.5, 0.0));

        [Fact]
        public void Null_ReturnsFallback() =>
            Assert.Equal("0.5", TimberbotPure.NormalizeDoubleString(null, 0.5, 0.0));

        [Fact]
        public void NotANumber_ReturnsFallback() =>
            Assert.Equal("0.5", TimberbotPure.NormalizeDoubleString("abc", 0.5, 0.0));
    }
}
