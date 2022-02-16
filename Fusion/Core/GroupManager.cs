using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;

namespace Fusion
{
    // --- Message Structs ---------------------------------------------------------------------------------------------------

    class GroupCreated : IMessage
    {
        VariableGroup m_Group;

        public GroupCreated( VariableGroup group )
        {
            Debug.Assert( group != null );
            m_Group = group;
        }
        public void Process()
        {
            m_Group.Node.RaiseOnGroupCreated( m_Group );
        }
    }

    class GroupDestroyed : IMessage
    {
        VariableGroup m_Group;

        public GroupDestroyed( VariableGroup group )
        {
            Debug.Assert( group != null );
            m_Group = group;
        }
        public void Process()
        {
            m_Group.Node.RaiseOnGroupDestroyed( m_Group );
        }
    }

    // --- Class ---------------------------------------------------------------------------------------------------

    public class GroupManager
    {
        const uint GroupIdsToProvidePerRequest = 100;
        const int  CheckIdPacksInterval = 5;

        uint            m_GroupIdCounter;
        double          m_LastIdPackRequestTime;
        Stopwatch       m_Time;
        bool            m_RelayGroupMessages;
        List<uint>      m_IdPacks;
        List<VariableGroup> m_PendingGroups;
        Dictionary<uint, VariableGroup> m_Groups;

        internal ConnectedNode Node { get; }
        internal bool RelayGroupMessages { get; set; }

        internal GroupManager( ConnectedNode node, bool relayGroupMessages = false )
        {
            Node = node;
            RelayGroupMessages = relayGroupMessages;
            m_Time          = new Stopwatch();
            m_IdPacks       = new List<uint>();
            m_PendingGroups = new List<VariableGroup>();
            m_Groups        = new Dictionary<uint, VariableGroup>();
        }

        internal void Clear()
        {
            m_GroupIdCounter = 0;
            m_LastIdPackRequestTime = 0;
            m_Groups.Clear();
            m_PendingGroups.Clear();
            m_IdPacks.Clear();
        }

        public void CreateGroup( uint type, params UpdatableType[] updatableTypes )
        {
            m_PendingGroups.Add( new VariableGroup( Node, null, type, 0, updatableTypes ) );
        }

        public void DestroyGroup( VariableGroup group )
        {
            if (group.Owner != null)
            {
                throw new InvalidOperationException( "Trying to destroy a remote created group is not allowed. This can lead to unordered result in p2p graph." );
            }
            bool groupWasDestroyed = false;
            lock (m_Groups)
            {
                if (m_Groups.TryGetValue( group.Id, out VariableGroup resolvedGroup ))
                {
                    Debug.Assert( group.IdIsAssigned );
                    SendGroupDestroyed( Node.BinWriter, null, false, ReliableStream.SystemChannel, group.Id );
                    m_Groups.Remove( group.Id );
                    groupWasDestroyed = true;
                }
            }
            if (!groupWasDestroyed)
            {
                bool wasRemoved = m_PendingGroups.Remove( group );
                Debug.Assert( wasRemoved );// Group must be either in pending list or in m_Groups.
            }
        }

        internal uint GetNextGroupIdRange()
        {
            uint nextGroupIdBase = m_GroupIdCounter;
            m_GroupIdCounter += GroupIdsToProvidePerRequest;
            return nextGroupIdBase;
        }

        void TryResolvePendingGroups( BinaryWriter writer )
        {
            while (m_PendingGroups.Count != 0 && m_IdPacks.Count != 0 && Node.Server != null)
            {
                // We have a pending group and an available Id. Resolve it.
                VariableGroup group = m_PendingGroups[0];
                m_PendingGroups.RemoveAt( 0 );
                uint currentId = m_IdPacks[0];
                group.AssignId( currentId );
                lock (m_Groups)
                {
                    m_Groups.Add( currentId, group );
                }
                currentId++;
                if (currentId % GroupManager.GroupIdsToProvidePerRequest == 0)
                {
                    // This is the id of the next pack. We do not know if that is valid.
                    m_IdPacks.RemoveAt( 0 );
                }
                // Raise on group created (Id was connected locally).
                Node.RaiseOnGroupCreated( group );
                SendGroupCreated( writer, null, false, ReliableStream.SystemChannel, group.Type, group.Id, group.UpdateTypes );
            }
        }

        void AutoRequestNewIdPackIfRunningOut()
        {
            if (Node.Server != null && Node.Server.ConnectStream.ConnectionState == ConnectionState.Active)
            {
                if (m_Time.Elapsed.TotalSeconds - m_LastIdPackRequestTime > CheckIdPacksInterval
                     || m_LastIdPackRequestTime == 0 /*Initially*/ )
                {
                    SendIdPacketRequest( Node.Server.EndPoint );

                    // Initially send two, so that we have a buffer immediately without awaiting the check interval for a new request.
                    if (m_LastIdPackRequestTime==0)
                    {
                        SendIdPacketRequest( Node.Server.EndPoint );
                    }

                    m_LastIdPackRequestTime = m_Time.Elapsed.TotalSeconds;
                }
            }
        }

        internal void Sync( BinaryWriter writer )
        {
            AutoRequestNewIdPackIfRunningOut();
            TryResolvePendingGroups( writer );
        }

        // --- Messages ---------------------------------------------------------------------------------------------

        internal void SendIdPacketRequest( IPEndPoint endpoint )
        {
            Node.SendPrivate( (byte)SystemPacketId.IdPackRequest, null, ReliableStream.SystemChannel, SendMethod.Reliable, endpoint );
        }

        internal void ReceiveIdPacketRequestWT( BinaryReader reader, BinaryWriter writer, IPEndPoint endpoint, byte channel )
        {
            SendIdPacketProvide( writer, endpoint, channel );
        }

        internal void SendIdPacketProvide( BinaryWriter writer, IPEndPoint endpoint, byte channel )
        {
            uint idBase = GetNextGroupIdRange();
            writer.ResetPosition();
            writer.Write( idBase );
            Node.SendPrivate( (byte)SystemPacketId.IdPackProvide, writer.GetData(), channel, SendMethod.Reliable, endpoint );
        }

        internal void ReceiveIdPacketProvideWT( BinaryReader reader, BinaryWriter writer, IPEndPoint endpoint, byte channel )
        {
            uint idBase = reader.ReadUInt32();
            m_IdPacks.Add( idBase );
        }

        internal void SendGroupCreated( BinaryWriter writer, IPEndPoint endpoint, bool endpointIsExcept, byte channel, uint type, uint id, params UpdatableType[] updateTypes )
        {
            if (updateTypes.Length>255)
            {
                throw new InvalidOperationException( "Max updatables per variable group is 255" );
            }
            writer.ResetPosition();
            writer.Write( type );
            writer.Write( id );
            writer.Write( (byte)updateTypes.Length );
            foreach (var t in updateTypes)
            {
                writer.Write( (byte)t );
            }
            if (!endpointIsExcept)
            {
                // Send only to endpoint.
                Node.SendPrivate( (byte)SystemPacketId.CreateGroup, writer.GetData(), channel, SendMethod.Reliable, endpoint );
            }
            else
            {
                // To all but endpoint.
                Node.SendPrivate( (byte)SystemPacketId.CreateGroup, writer.GetData(), channel, SendMethod.Reliable, null, endpoint );
            }
        }

        internal void ReceiveGroupCreatedWT( BinaryReader reader, BinaryWriter writer, IPEndPoint endpoint, byte channel )
        {
            uint type = reader.ReadUInt32();
            uint id   = reader.ReadUInt32();
            byte num  = reader.ReadByte();
#if DEBUG
            lock (m_Groups)
            {
                Debug.Assert( !m_Groups.ContainsKey( id ) );
            }
#endif
            UpdatableType [] updatableTypes = new UpdatableType[num];
            for (int i = 0;i < num;i++)
            {
                UpdatableType updateType = (UpdatableType) reader.ReadByte();
                updatableTypes[i] = updateType;
            }
            VariableGroup vg = new VariableGroup( Node, endpoint, type, id, updatableTypes );
            vg.AssignId( id );
            lock (m_Groups) // Accessed from mainthread too, to add local groups.
            {
                m_Groups.Add( id, vg );
            }
            Node.AddMessage( new GroupCreated( vg ) );
            if (m_RelayGroupMessages)
            {
                SendGroupCreated( writer, endpoint, true, channel, type, id, updatableTypes );
            }
        }

        internal void SendGroupDestroyed( BinaryWriter writer, IPEndPoint endpoint, bool endpointIsExcept, byte channel, uint id )
        {
            writer.ResetPosition();
            writer.Write( id );
            if (!endpointIsExcept)
            {
                // Send only to endpoint.
                Node.SendPrivate( (byte)SystemPacketId.CreateGroup, writer.GetData(), channel, SendMethod.Reliable, endpoint );
            }
            else
            {
                // To all but endpoint.
                Node.SendPrivate( (byte)SystemPacketId.CreateGroup, writer.GetData(), channel, SendMethod.Reliable, null, endpoint );
            }
        }
        internal void ReceiveGroupDestroyedWT( BinaryReader reader, BinaryWriter writer, IPEndPoint endpoint, byte channel )
        {
            uint id = reader.ReadUInt32();
            VariableGroup vg;
            lock (m_Groups)
            {
                vg = m_Groups[id];
                Debug.Assert( vg != null );
                m_Groups.Remove( vg.Id );
            }
            Node.AddMessage( new GroupDestroyed( vg ) );
            if (m_RelayGroupMessages)
            {
                SendGroupDestroyed( writer, endpoint, true, channel, id );
            }
        }

        internal void SendDestroyAllGroups( BinaryWriter writer, IPEndPoint endpoint, bool endpointIsExcept, byte channel )
        {
            if (!endpointIsExcept)
            {
                // Send only to endpoint.
                Node.SendPrivate( (byte)SystemPacketId.DestroyAllGroups, null, channel, SendMethod.Reliable, endpoint );
            }
            else
            {
                // To all but endpoint.
                Node.SendPrivate( (byte)SystemPacketId.DestroyAllGroups, null, channel, SendMethod.Reliable, null, endpoint );
            }
        }

        internal void ReceiveDestroyAllGroupsWT( BinaryReader reader, BinaryWriter writer, IPEndPoint endpoint, byte channel )
        {
            lock (m_Groups)
            {
                IEnumerable<KeyValuePair<uint, VariableGroup>> groupsToDestroy = m_Groups.Where( ( kvp ) => kvp.Value.Owner == endpoint );
                foreach (var kvp in groupsToDestroy)
                {
                    VariableGroup vg = kvp.Value;
                    Debug.Assert( vg.IdIsAssigned );
                    m_Groups.Remove( vg.Id );
                    Node.AddMessage( new GroupDestroyed( vg ) );
                }
            }
            if (m_RelayGroupMessages)
            {
                SendDestroyAllGroups( writer, endpoint, true, channel );
            }
        }
    }
}
