using System;
using System.Globalization;
using System.IO;

namespace Timberbot
{
    // Pure static helpers extracted from Unity-dependent classes for testability.
    // Original call sites delegate here via one-liners.
    public static class TimberbotPure
    {
        // --- from TimberbotAgent ---

        public static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length > 2000) s = s.Substring(0, 2000) + "...(truncated)";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        public static bool IsCodexBinary(string binary)
        {
            if (string.IsNullOrWhiteSpace(binary))
                return false;

            try
            {
                return string.Equals(Path.GetFileNameWithoutExtension(binary.Trim()), "codex", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string QuoteArg(string value)
        {
            if (value == null)
                value = "";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public static string ShellQuoteArg(string value)
        {
            if (value == null)
                value = "";
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }

        // --- from TimberbotPlacement ---

        public static int ParseOrientation(string orient)
        {
            if (string.IsNullOrEmpty(orient)) return 0;
            var lower = orient.Trim().ToLowerInvariant();
            switch (lower)
            {
                case "south": return 0;
                case "west": return 1;
                case "north": return 2;
                case "east": return 3;
                default: return -1;
            }
        }

        // --- from TimberbotEntityRegistry ---

        public static string CanonicalName(string name)
        {
            return name.Replace("(Clone)", "").Trim();
        }

        public static string CleanName(string name, string factionSuffix)
        {
            var clean = CanonicalName(name);
            if (factionSuffix != null && factionSuffix.Length > 0)
                clean = clean.Replace(factionSuffix, "");
            return clean.Trim();
        }

        // --- from TimberbotDebug ---

        public static bool TryGetNumeric(object value, out double numeric)
        {
            numeric = 0;
            if (value == null) return false;
            try
            {
                if (value is bool b) { numeric = b ? 1 : 0; return true; }
                if (value is IConvertible) { numeric = Convert.ToDouble(value); return true; }
            }
            catch { }
            return false;
        }

        public static bool ValuesEqual(object left, object right)
        {
            if (left == null || right == null) return left == right;
            if (TryGetNumeric(left, out var leftNum) && TryGetNumeric(right, out var rightNum))
                return Math.Abs(leftNum - rightNum) < 0.0001;
            return Equals(left, right);
        }

        public static int CompareValues(object left, object right, out bool comparable)
        {
            comparable = false;
            if (TryGetNumeric(left, out var leftNum) && TryGetNumeric(right, out var rightNum))
            {
                comparable = true;
                return leftNum.CompareTo(rightNum);
            }
            if (left is string ls && right is string rs)
            {
                comparable = true;
                return string.Compare(ls, rs, StringComparison.Ordinal);
            }
            return 0;
        }

        public static bool EvaluateAssertion(object left, string op, object right, out string detail)
        {
            detail = null;
            switch (op)
            {
                case "eq": return ValuesEqual(left, right);
                case "neq": return !ValuesEqual(left, right);
                case "null": return left == null;
                case "notnull": return left != null;
                case "gt":
                case "gte":
                case "lt":
                case "lte":
                    var cmp = CompareValues(left, right, out var comparable);
                    if (!comparable) { detail = "values not comparable"; return false; }
                    if (op == "gt") return cmp > 0;
                    if (op == "gte") return cmp >= 0;
                    if (op == "lt") return cmp < 0;
                    return cmp <= 0;
                default:
                    detail = $"unknown op '{op}'";
                    return false;
            }
        }

        // --- from TimberbotPanel ---

        public static string NormalizeValue(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        public static string NormalizeBoolString(string value, bool fallback)
        {
            var normalized = NormalizeValue(value, fallback ? "true" : "false").ToLowerInvariant();
            return normalized == "false" ? "false" : "true";
        }

        public static string NormalizeIntString(string value, int fallback, int minValue)
        {
            if (int.TryParse(NormalizeValue(value, fallback.ToString()), out var parsed) && parsed >= minValue)
                return parsed.ToString();

            return fallback.ToString();
        }

        public static string NormalizeDoubleString(string value, double fallback, double minValue)
        {
            if (double.TryParse(NormalizeValue(value, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= minValue)
                return parsed.ToString(CultureInfo.InvariantCulture);

            return fallback.ToString(CultureInfo.InvariantCulture);
        }
    }
}
