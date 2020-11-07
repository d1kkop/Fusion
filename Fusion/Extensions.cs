using System;
using System.Collections.Generic;
using System.IO;

namespace Fusion
{
    public static class Extensions
    {
        public static Value GetOrAdd<Key, Value>(this Dictionary<Key, Value> dictionary, Key key) where Value : new()
        {
            Value v;
            if (!dictionary.TryGetValue(key, out v))
            {
                v = new Value();
                dictionary.Add( key, v );
            }
            return v;
        }

        public static byte [] GetData(this BinaryWriter writer)
        {
            // Very hacky thing. But BinaryWriter is always made up of a memory stream in the application.
            return ((MemoryStream)writer.BaseStream).GetBuffer();
        }

        public static void ResetPosition(this BinaryWriter writer)
        {
            writer.BaseStream.Position = 0;
        }
    }
}
