using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;

namespace Fusion
{
    public class RPC : Attribute
    {

    }

    class RPCMessage : IMessage
    {
        object [] m_Arguments;
        MethodInfo m_Method;

        internal RPCMessage( MethodInfo method, object[] arguments )
        {
            m_Method    = method;
            m_Arguments = arguments;
        }

        public void Process()
        {
            m_Method.Invoke( null, m_Arguments );
        }
    }

    public partial class ConnectedNode
    {
        internal struct RPCData
        {
            internal byte m_Id;
            internal MethodInfo m_MethodInfo;
        }

        static Dictionary<string, RPCData> m_RPC;
        static Dictionary<Type, Action<BinaryWriter, object>> m_TypeSerializers;
        static Dictionary<Type, Func<BinaryReader, object>> m_TypeDeserializers;

        static void InitializeRPCStatic()
        {
            InitializeMapping();
            InitializeSerializers();
            InitializeDeserializers();
        }

        static void InitializeMapping()
        {
            m_RPC = new Dictionary<string, RPCData>();

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var methods = assemblies.SelectMany( a => a.GetTypes() ).SelectMany( t => t.GetMethods() )
                .Where( m => m.GetCustomAttributes( typeof( RPC ), false ).Length > 0 )
                      .ToArray();

            if (methods.Length > byte.MaxValue)
                throw new InvalidOperationException( $"Max RPC functions is {byte.MaxValue}" );

            byte id = 0;
            foreach (var m in methods)
            {
                RPCData data = new RPCData();
                data.m_Id = id;
                data.m_MethodInfo = m;
                m_RPC.Add( m.Name, data );
                id++;
            }
        }

        static void InitializeSerializers()
        {
            m_TypeSerializers = new Dictionary<Type, Action<BinaryWriter, object>>();
            m_TypeSerializers.Add( typeof( byte ), ( bw, o ) => bw.Write( (byte)o ) );
            m_TypeSerializers.Add( typeof( sbyte ), ( bw, o ) => bw.Write( (sbyte)o ) );
            m_TypeSerializers.Add( typeof( char ), ( bw, o ) => bw.Write( (char)o ) );
            m_TypeSerializers.Add( typeof( short ), ( bw, o ) => bw.Write( (short)o ) );
            m_TypeSerializers.Add( typeof( ushort ), ( bw, o ) => bw.Write( (ushort)o ) );
            m_TypeSerializers.Add( typeof( int ), ( bw, o ) => bw.Write( (int)o ) );
            m_TypeSerializers.Add( typeof( uint ), ( bw, o ) => bw.Write( (uint)o ) );
            m_TypeSerializers.Add( typeof( float ), ( bw, o ) => bw.Write( (float)o ) );
            m_TypeSerializers.Add( typeof( double ), ( bw, o ) => bw.Write( (double)o ) );
            m_TypeSerializers.Add( typeof( decimal ), ( bw, o ) => bw.Write( (decimal)o ) );
            m_TypeSerializers.Add( typeof( string ), ( bw, o ) => bw.Write( (string)o ) );

            Action<BinaryWriter, object> serializeListStr = (BinaryWriter bw, object o) =>
            {
                List<string> list = o as List<string>;
                byte num = (byte) list.Count;
                if ( num > byte.MaxValue )
                    throw new InvalidOperationException("Too many entries in list added.");
                bw.Write(num);
                for ( int i = 0; i < num; i++ )
                    bw.Write(list[i]);
            };

            m_TypeSerializers.Add( typeof( List<string> ), serializeListStr );

            Action<BinaryWriter, object> serializeDicStrStr = (BinaryWriter bw, object o) =>
            {
                Dictionary<string, string> d = o as Dictionary<string, string>;
                byte num = (byte) d.Count;
                if ( num > byte.MaxValue )
                    throw new InvalidOperationException("Too many entries in dictionary added.");
                bw.Write(num);
                foreach( var kvp in d )
                {
                    bw.Write( kvp.Key );
                    bw.Write( kvp.Value );
                }
            };

            m_TypeSerializers.Add( typeof( Dictionary<string, string> ), serializeDicStrStr );
        }

        static void InitializeDeserializers()
        {
            m_TypeDeserializers = new Dictionary<Type, Func<BinaryReader, object>>();
            m_TypeDeserializers.Add( typeof( byte ), ( bw ) => bw.ReadByte() );
            m_TypeDeserializers.Add( typeof( sbyte ), ( bw ) => bw.ReadSByte() );
            m_TypeDeserializers.Add( typeof( char ), ( bw ) => bw.ReadChar() );
            m_TypeDeserializers.Add( typeof( short ), ( bw ) => bw.ReadInt16() );
            m_TypeDeserializers.Add( typeof( ushort ), ( bw ) => bw.ReadUInt16() );
            m_TypeDeserializers.Add( typeof( int ), ( bw ) => bw.ReadInt32() );
            m_TypeDeserializers.Add( typeof( uint ), ( bw ) => bw.ReadUInt32() );
            m_TypeDeserializers.Add( typeof( float ), ( bw ) => bw.ReadSingle() );
            m_TypeDeserializers.Add( typeof( double ), ( bw ) => bw.ReadDouble() );
            m_TypeDeserializers.Add( typeof( decimal ), ( bw ) => bw.ReadDecimal() );
            m_TypeDeserializers.Add( typeof( string ), ( bw ) => bw.ReadString() );

            Func<BinaryReader, object> deserializeListStr = (BinaryReader bw) =>
            {
                List<string> list = new List<string>();
                byte num = bw.ReadByte();
                for ( int i = 0; i < num; i++ )
                {
                    string str = bw.ReadString();
                    list.Add( str );
                }
                return list;
            };

            m_TypeDeserializers.Add( typeof( List<string> ), deserializeListStr );

            Func<BinaryReader, object> serializeDicStrStr = (BinaryReader bw) =>
            {
                Dictionary<string, string> d = new Dictionary<string, string>();
                byte num = bw.ReadByte();
                for ( int i = 0; i < num; i++ )
                {
                    string key   = bw.ReadString();
                    string value = bw.ReadString();
                    d.Add( key, value );
                }
                return d;
            };

            m_TypeDeserializers.Add( typeof( Dictionary<string, string> ), serializeDicStrStr );
        }

        public void DoReliableRPC( string methodName, byte channel, IPEndPoint onlyThisRecipient, bool callLocally, params object[] arguments )
        {
            SendRPC( methodName, SendMethod.Reliable, channel, onlyThisRecipient, callLocally, arguments );
        }

        public void DoUnreliableRPC( string methodName, IPEndPoint onlyThisRecipient, bool callLocally, params object[] arguments )
        {
            SendRPC( methodName, SendMethod.Unreliable, 0, onlyThisRecipient, callLocally, arguments );
        }

        void SendRPC( string methodName, SendMethod sendMethod, byte channel, IPEndPoint onlyThisRecipient, bool callLocally, params object[] arguments )
        {
            RPCData data   = m_RPC[methodName];
            var signatures = data.m_MethodInfo.GetParameters();

            if (arguments.Length != signatures.Length-2)
                throw new InvalidOperationException( "Argument count does not match number of arguments of method: " + methodName );

            if (arguments.Length > byte.MaxValue)
                throw new InvalidOperationException( "Max number of argumetns is " + byte.MaxValue );

            if (callLocally)
            {
                var localArguments = arguments.Append( null ).Append( channel ).ToArray();
                data.m_MethodInfo.Invoke( null, localArguments );
            }

            BinWriter.ResetPosition();
            BinWriter.Write( (byte)data.m_Id );
            BinWriter.Write( (byte)arguments.Length );
            for( int i = 0; i < arguments.Length; i++ )
            {
                if (signatures[i].ParameterType != arguments[i].GetType())
                {
                    throw new InvalidOperationException( $"Argument type {i} is invalid." );
                }
                m_TypeSerializers[signatures[i].ParameterType].Invoke( BinWriter, arguments[i] );
            }

            switch (sendMethod)
            {
                case SendMethod.Reliable:
                SendPrivate( (byte)SystemPacketId.RPC, BinWriter.GetData(), channel, SendMethod.Reliable, onlyThisRecipient, null );
                break;

                case SendMethod.Unreliable:
                SendUnreliablePrivate( (byte)SystemPacketId.RPC, true, BinWriter.GetData(), onlyThisRecipient, null );
                break;

                default:
                Debug.Assert( false );
                break;
            }
        }

        internal void ReceiveRPCWT( bool isReliableMsg, BinaryReader reader, byte channel, ConnectedRecipient recipient )
        {
            byte methodId    = reader.ReadByte();
            byte numAguments = reader.ReadByte();
            RPCData data     = m_RPC.Where( kvp => kvp.Value.m_Id == methodId ).Single().Value;

            var arguments = data.m_MethodInfo.GetParameters();
            if (arguments.Length-2 != numAguments)
                throw new InvalidOperationException( "Num argments does not match incoming amount." );

            object [] rpcArguments = new object[arguments.Length];
            for (int i = 0;i < numAguments;i++)
            {
                rpcArguments[i] = m_TypeDeserializers[arguments[i].ParameterType].Invoke( reader );
            }

            rpcArguments[numAguments]   = recipient;
            rpcArguments[numAguments+1] = channel;

            // In case the RPC is received in reliable fashion, we must insert it into the other reliable ordered messages.
            // Else, if sent unreliable, just add it as a generic RPC Message.
            var rpcMessage = new RPCMessage( data.m_MethodInfo, rpcArguments );
            if (isReliableMsg)
            {
                recipient.ReliableStreams[channel].AddRecvMessageWT( rpcMessage );
            }
            else
            {
                AddMessage( new RPCMessage( data.m_MethodInfo, rpcArguments ) );
            }
        }
    }
}
