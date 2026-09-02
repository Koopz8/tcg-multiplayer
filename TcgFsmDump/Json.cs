using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TcgFsmDump
{
    /// <summary>
    /// Minimal streaming JSON writer. Hand-rolled on purpose: the game ships
    /// Newtonsoft, but binding to it would tie this tool to whichever version
    /// the current build happens to carry.
    /// </summary>
    internal sealed class JsonWriter : IDisposable
    {
        private readonly TextWriter _w;
        private readonly Stack<bool> _hasItems = new Stack<bool>();
        private int _depth;

        public JsonWriter(TextWriter w) { _w = w; }

        private void Indent()
        {
            _w.Write('\n');
            for (int i = 0; i < _depth; i++) _w.Write("  ");
        }

        private void Separate()
        {
            if (_hasItems.Count > 0 && _hasItems.Peek()) _w.Write(',');
            if (_hasItems.Count > 0)
            {
                _hasItems.Pop();
                _hasItems.Push(true);
            }
            if (_depth > 0) Indent();
        }

        public void StartObject() { Separate(); _w.Write('{'); _depth++; _hasItems.Push(false); }
        public void StartObject(string name) { Separate(); WriteName(name); _w.Write('{'); _depth++; _hasItems.Push(false); }
        public void EndObject() { bool any = _hasItems.Pop(); _depth--; if (any) Indent(); _w.Write('}'); }

        public void StartArray(string name) { Separate(); WriteName(name); _w.Write('['); _depth++; _hasItems.Push(false); }
        public void EndArray() { bool any = _hasItems.Pop(); _depth--; if (any) Indent(); _w.Write(']'); }

        private void WriteName(string name) { WriteString(name); _w.Write(": "); }

        public void Prop(string name, string value)
        {
            Separate(); WriteName(name);
            if (value == null) _w.Write("null"); else WriteString(value);
        }

        public void Prop(string name, bool value) { Separate(); WriteName(name); _w.Write(value ? "true" : "false"); }
        public void Prop(string name, int value) { Separate(); WriteName(name); _w.Write(value.ToString(CultureInfo.InvariantCulture)); }
        public void Prop(string name, uint value) { Separate(); WriteName(name); _w.Write(value.ToString(CultureInfo.InvariantCulture)); }

        public void Prop(string name, float value)
        {
            Separate(); WriteName(name);
            if (float.IsNaN(value) || float.IsInfinity(value)) _w.Write("null");
            else _w.Write(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        public void PropNull(string name) { Separate(); WriteName(name); _w.Write("null"); }

        public void Value(string value)
        {
            Separate();
            if (value == null) _w.Write("null"); else WriteString(value);
        }

        private void WriteString(string s)
        {
            _w.Write('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _w.Write("\\\""); break;
                    case '\\': _w.Write("\\\\"); break;
                    case '\n': _w.Write("\\n"); break;
                    case '\r': _w.Write("\\r"); break;
                    case '\t': _w.Write("\\t"); break;
                    case '\b': _w.Write("\\b"); break;
                    case '\f': _w.Write("\\f"); break;
                    default:
                        if (c < ' ' || c > '~') _w.Write("\\u" + ((int)c).ToString("x4"));
                        else _w.Write(c);
                        break;
                }
            }
            _w.Write('"');
        }

        public void Dispose() { _w.Flush(); }
    }
}
