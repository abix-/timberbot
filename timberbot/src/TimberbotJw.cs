using System.Text;

namespace Timberbot
{
    // Fluent zero-allocation JSON writer. Allocated once as a field, Reset() per request.
    //
    // Why not Newtonsoft.Json? Newtonsoft allocates Dictionary/List objects, boxes value
    // types, and uses reflection. For the hot path (CollectBuildings with 200+ buildings),
    // this writer is ~7x faster and produces zero GC pressure.
    //
    // COMMA HANDLING (the trickiest part):
    // JSON requires commas between items: {"a":1,"b":2} and [1,2,3]
    // AutoSep() inserts a comma before a value IF there's already a value at this depth.
    //
    //   _hasValue[depth] tracks whether we've written a value at the current nesting level.
    //   - OpenArr/OpenObj: push depth, _hasValue = false (no values yet at new level)
    //   - Key(): writes "key": and sets _hasValue = false (the value comes next, no comma before it)
    //   - Int/Str/Bool/etc: calls AutoSep() which adds comma if _hasValue is true, then sets _hasValue = true
    //   - CloseArr/CloseObj: pop depth, _hasValue = true (the container counts as a value in the parent)
    //
    // Example trace for {"a":1,"b":2}:
    //   OpenObj -> depth=1, hasValue[1]=false
    //   Key("a") -> AutoSep (no comma, hasValue=false), writes "a":, hasValue[1]=false
    //   Int(1) -> AutoSep (no comma, hasValue=false), writes 1, hasValue[1]=true
    //   Key("b") -> AutoSep (comma! hasValue=true), writes ,"b":, hasValue[1]=false
    //   Int(2) -> AutoSep (no comma, hasValue=false), writes 2, hasValue[1]=true
    //   CloseObj -> writes }, depth=0, hasValue[0]=true
    //
    // Max nesting: 16 levels (arrays/objects within arrays/objects). Enough for any API response.
    //
    // Usage:
    //   _jw.Reset().OpenArr();
    //   _jw.OpenObj().Key("id").Int(1).Key("name").Str("Path").CloseObj();
    //   _jw.OpenObj().Key("id").Int(2).Key("name").Str("Farm").CloseObj();
    //   _jw.CloseArr();
    //   return _jw.ToString();
    public class TimberbotJw
    {
        private readonly StringBuilder _sb;        // pre-allocated, reused via Clear()
        private int _depth;                        // current nesting level (0 = root)
        private readonly bool[] _hasValue = new bool[16]; // per-depth "has a value been written?"

        public TimberbotJw(int capacity = 100000) { _sb = new StringBuilder(capacity); }

        // Reset for next request. StringBuilder.Clear() doesn't deallocate -- it resets length to 0.
        public TimberbotJw Reset() { _sb.Clear(); _depth = 0; _hasValue[0] = false; return this; }

        // Structural tokens: open/close arrays and objects
        public TimberbotJw OpenArr() { AutoSep(); _sb.Append('['); _hasValue[++_depth] = false; return this; }
        public TimberbotJw CloseArr() { _sb.Append(']'); _depth--; _hasValue[_depth] = true; return this; }
        public TimberbotJw OpenObj() { AutoSep(); _sb.Append('{'); _hasValue[++_depth] = false; return this; }
        public TimberbotJw CloseObj() { _sb.Append('}'); _depth--; _hasValue[_depth] = true; return this; }

        // Begin: Reset + Open in one call. Use for multi-line builders.
        //   var jw = _jw.BeginArr();  // instead of _jw.Reset().OpenArr()
        //   ... loop ...
        //   return jw.End();          // instead of jw.CloseArr().ToString()
        public TimberbotJw BeginArr() => Reset().OpenArr();
        public TimberbotJw BeginObj() => Reset().OpenObj();

        // End: Close + ToString in one call. Auto-detects array vs object from depth state.
        public string End() { if (_sb.Length > 0 && _sb[0] == '[') CloseArr(); else CloseObj(); return ToString(); }

        // Key writes "name": and resets hasValue so the next value doesn't get a leading comma
        public TimberbotJw Key(string name) { AutoSep(); _sb.Append('"'); _sb.Append(name); _sb.Append("\":"); _hasValue[_depth] = false; return this; }

        // Value methods: each calls AutoSep() for comma handling, then writes the value
        public TimberbotJw Bool(bool v) { AutoSep(); _sb.Append(v ? "true" : "false"); _hasValue[_depth] = true; return this; }
        public TimberbotJw Int(int v) { AutoSep(); _sb.Append(v); _hasValue[_depth] = true; return this; }
        public TimberbotJw Long(long v) { AutoSep(); _sb.Append(v); _hasValue[_depth] = true; return this; }

        // Zero-allocation float: writes digits directly to StringBuilder instead of
        // calling v.ToString("F2") which allocates a string on the heap every time.
        // Handles F1 (1 decimal) and F2 (2 decimals, default).
        public TimberbotJw Float(float v, string fmt = "F2")
        {
            AutoSep();
            if (v < 0) { _sb.Append('-'); v = -v; }
            int whole = (int)v;
            _sb.Append(whole);
            _sb.Append('.');
            float frac = v - whole;
            if (fmt == "F1")
            {
                // one decimal place: 3.7
                _sb.Append((int)(frac * 10 + 0.5f));
            }
            else // F2 default
            {
                // two decimal places: 3.14 (pad with leading zero if needed: 3.05)
                int d = (int)(frac * 100 + 0.5f);
                if (d < 10) _sb.Append('0');
                _sb.Append(d);
            }
            _hasValue[_depth] = true;
            return this;
        }
        public TimberbotJw Str(string v) { AutoSep(); _sb.Append('"'); _sb.Append(v ?? ""); _sb.Append('"'); _hasValue[_depth] = true; return this; }
        public TimberbotJw Null() { AutoSep(); _sb.Append("null"); _hasValue[_depth] = true; return this; }
        // Raw: inject pre-built JSON (e.g. from a nested JW call). No quoting.
        public TimberbotJw Raw(string json) { AutoSep(); _sb.Append(json); _hasValue[_depth] = true; return this; }

        // --- Key+Value shortcuts: one call instead of two ---
        // Before: jw.Key("id").Int(c.Id).Key("name").Str(c.Name).Key("alive").Bool(true)
        // After:  jw.Prop("id", c.Id).Prop("name", c.Name).Prop("alive", true)
        public TimberbotJw Prop(string name, int v) => Key(name).Int(v);
        public TimberbotJw Prop(string name, long v) => Key(name).Long(v);
        public TimberbotJw Prop(string name, bool v) => Key(name).Bool(v);
        public TimberbotJw Prop(string name, string v) => Key(name).Str(v);
        public TimberbotJw Prop(string name, float v, string fmt = "F2") => Key(name).Float(v, fmt);
        // Fallback for complex types (List, Dict, object). Serializes via Newtonsoft.
        // Only used for POST responses (not hot path). Keeps all returns going through JwWriter.
        public TimberbotJw Prop(string name, object v) => RawProp(name, Newtonsoft.Json.JsonConvert.SerializeObject(v));

        // --- Key+Structure shortcuts ---
        // Before: jw.Key("population").OpenObj()...CloseObj()
        // After:  jw.Obj("population")...CloseObj()
        public TimberbotJw Obj(string name) => Key(name).OpenObj();
        public TimberbotJw Arr(string name) => Key(name).OpenArr();

        // --- Key+Raw shortcut for embedding pre-serialized JSON ---
        // Before: jw.Key("districts").Raw(districtsJson)
        // After:  jw.RawProp("districts", districtsJson)
        public TimberbotJw RawProp(string name, string json) => Key(name).Raw(json);

        // --- One-call response builders ---
        // Pass all properties as tuples. Handles Reset/OpenObj/CloseObj/ToString internally.
        //
        //   _jw.Result(("id", 5), ("name", "Path"), ("placed", true))
        //   -> {"id":5,"name":"Path","placed":true}
        //
        //   _jw.Error("not found")
        //   -> {"error":"not found"}
        //
        //   _jw.Error("not found", ("id", buildingId))
        //   -> {"error":"not found","id":42}
        public string Result(params (string key, object val)[] props)
        {
            Reset().OpenObj();
            foreach (var (key, val) in props)
                WriteProp(key, val);
            CloseObj();
            return ToString();
        }

        public string Error(string msg, params (string key, object val)[] extra)
        {
            Reset().OpenObj().Prop("error", msg);
            foreach (var (key, val) in extra)
                WriteProp(key, val);
            CloseObj();
            return ToString();
        }

        private void WriteProp(string key, object val)
        {
            switch (val)
            {
                case int i: Prop(key, i); break;
                case long l: Prop(key, l); break;
                case bool b: Prop(key, b); break;
                case float f: Prop(key, f); break;
                case string s: Prop(key, s); break;
                default: Prop(key, val); break; // Newtonsoft fallback for complex types
            }
        }

        public override string ToString() => _sb.ToString();

        // Returns the content inside the outermost {} or [] without the braces.
        // jw.Reset().OpenObj().Prop("a", 1).Prop("b", 2).CloseObj().ToInnerString()
        //   -> "a":1,"b":2
        public string ToInnerString()
        {
            var s = _sb.ToString();
            return s.Length > 2 ? s.Substring(1, s.Length - 2) : "";
        }

        // Insert a comma separator if there's already a value at this nesting depth.
        // This is the core trick: we never need to "look ahead" -- just track whether
        // the current depth has seen a value yet.
        private void AutoSep()
        {
            if (_hasValue[_depth]) _sb.Append(',');
        }
    }
}
