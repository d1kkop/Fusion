using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Fusion
{
    public static class Extensions
    {
        public static Value GetOrAdd<Key, Value>( this Dictionary<Key, Value> dictionary, Key key ) where Value : new()
        {
            Value v;
            if (!dictionary.TryGetValue( key, out v ))
            {
                v = new Value();
                dictionary.Add( key, v );
            }
            return v;
        }

        public static byte[] GetData( this BinaryWriter writer )
        {
            // Copy the data, because the writer may be used for other purposes. Just providing a reference to the buffer might
            // result in overwritten results! TODO 
            byte [] bufferReference = ((MemoryStream)writer.BaseStream).GetBuffer();
            return bufferReference.AsSpan( 0, (int)writer.BaseStream.Position ).ToArray();
        }

        public static void ResetPosition( this BinaryWriter writer )
        {
            writer.BaseStream.Position = 0;
        }

        public static void SendSafe( this UdpClient client, byte[] data, int len, IPEndPoint target )
        {
            try
            {
                client.SendAsync( data, len, target );
            } catch (System.ObjectDisposedException)
            {
                // If sending while client gets closed from different thread, this is thrown.
                // Could also fix this by atomically sending and closing with a lock, but we would need a lock for only the closure situation.
            }
        }
    }
}
