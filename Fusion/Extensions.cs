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

        public static ArraySegment<byte> GetData( this BinaryWriter writer )
        {
            return new ArraySegment<byte>(
                ((MemoryStream)writer.BaseStream).GetBuffer(), 0, (int) writer.BaseStream.Position);
        }

        public static void ResetPosition( this BinaryWriter writer )
        {
            writer.BaseStream.Position = 0;
        }

        public static void SendSafe( this UdpClient client, ArraySegment<byte> data, IPEndPoint target )
        {
            try
            {
                client.SendAsync( data.Array, data.Count, target );
            } catch (System.ObjectDisposedException/*Disposed*/) { } catch (SocketException/*Close or shutdown*/) { }
        }

        public static byte [] GetCopy( this ArraySegment<byte> segment )
        {
            byte [] cpy = new byte[segment.Count];
            if (segment.Count!=0)
                segment.CopyTo( cpy );
            return cpy;
        }
    }
}
