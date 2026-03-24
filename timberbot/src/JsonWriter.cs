using System.Text;

namespace Timberbot
{
    // zero-allocation JSON helper for StringBuilder serialization
    static class Jw
    {
        public static void Key(StringBuilder sb, string name)
        { sb.Append(",\""); sb.Append(name); sb.Append("\":"); }

        public static void KeyFirst(StringBuilder sb, string name)
        { sb.Append('"'); sb.Append(name); sb.Append("\":"); }

        public static void Bool(StringBuilder sb, bool v)
        { sb.Append(v ? "true" : "false"); }

        public static void Int(StringBuilder sb, int v)
        { sb.Append(v); }

        public static void Float(StringBuilder sb, float v, string fmt = "F2")
        { sb.Append(v.ToString(fmt)); }

        public static void Str(StringBuilder sb, string v)
        { sb.Append('"'); sb.Append(v ?? ""); sb.Append('"'); }

        public static void Open(StringBuilder sb) { sb.Append('{'); }
        public static void Close(StringBuilder sb) { sb.Append('}'); }
        public static void OpenArr(StringBuilder sb) { sb.Append('['); }
        public static void CloseArr(StringBuilder sb) { sb.Append(']'); }
        public static void Sep(StringBuilder sb) { sb.Append(','); }
    }
}
