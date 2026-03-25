using System.Text;

namespace Timberbot
{
    // fluent zero-allocation JSON writer. allocate once as a field, Reset() per request.
    // auto-handles commas between array items and object keys. nesting-aware up to 16 levels.
    //
    // usage:
    //   _jw.Reset().OpenArr();
    //   _jw.OpenObj().Key("id").Int(1).Key("name").Str("Path").CloseObj();
    //   _jw.OpenObj().Key("id").Int(2).Key("name").Str("Farm").CloseObj();
    //   _jw.CloseArr();
    //   return _jw.ToString();
    class JwWriter
    {
        private readonly StringBuilder _sb;
        private int _depth;
        private readonly bool[] _hasValue = new bool[16];

        public JwWriter(int capacity = 100000) { _sb = new StringBuilder(capacity); }

        public JwWriter Reset() { _sb.Clear(); _depth = 0; _hasValue[0] = false; return this; }

        public JwWriter OpenArr() { AutoSep(); _sb.Append('['); _hasValue[++_depth] = false; return this; }
        public JwWriter CloseArr() { _sb.Append(']'); _depth--; _hasValue[_depth] = true; return this; }
        public JwWriter OpenObj() { AutoSep(); _sb.Append('{'); _hasValue[++_depth] = false; return this; }
        public JwWriter CloseObj() { _sb.Append('}'); _depth--; _hasValue[_depth] = true; return this; }

        public JwWriter Key(string name) { AutoSep(); _sb.Append('"'); _sb.Append(name); _sb.Append("\":"); return this; }
        public JwWriter Bool(bool v) { _sb.Append(v ? "true" : "false"); _hasValue[_depth] = true; return this; }
        public JwWriter Int(int v) { _sb.Append(v); _hasValue[_depth] = true; return this; }
        public JwWriter Long(long v) { _sb.Append(v); _hasValue[_depth] = true; return this; }
        public JwWriter Float(float v, string fmt = "F2") { _sb.Append(v.ToString(fmt)); _hasValue[_depth] = true; return this; }
        public JwWriter Str(string v) { _sb.Append('"'); _sb.Append(v ?? ""); _sb.Append('"'); _hasValue[_depth] = true; return this; }
        public JwWriter Null() { _sb.Append("null"); _hasValue[_depth] = true; return this; }
        public JwWriter Raw(string json) { AutoSep(); _sb.Append(json); _hasValue[_depth] = true; return this; }

        public override string ToString() => _sb.ToString();

        private void AutoSep()
        {
            if (_hasValue[_depth]) _sb.Append(',');
        }
    }
}
