using System;
using System.Collections.Generic;

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
    }
}
