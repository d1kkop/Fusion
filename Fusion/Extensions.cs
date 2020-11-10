using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

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

        public static void SendSafe( this UdpClient client, byte [] data, int len, IPEndPoint target )
        {
            try
            {
                client.SendAsync( data, len, target );
            }
            catch (System.ObjectDisposedException)
            {
                // If sending while client gets closed from different thread, this is thrown.
                // Could also fix this by atomically sending and closing with a lock, but we would need a lock for only the closure situation.
            }
        }
    }
}
