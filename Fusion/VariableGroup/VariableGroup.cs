using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;

namespace Fusion
{
    public class VariableGroup
    {
        const int MaxUpdatables = 256;

        bool m_IdAssigned;
        List<Updatable> m_Updatables;

        public IPEndPoint Owner { get; private set; }
        public uint Type { get; }
        public uint Id { get; private set; }
        public UpdatableType [] UpdateTypes { get; private set; }
        internal ConnectedNode Node { get; private set; }
        internal bool IdIsAssigned => m_IdAssigned;

        internal VariableGroup( ConnectedNode node, IPEndPoint owner, uint typeId, uint id, params UpdatableType[] types )
        {
            Node  = node;
            Owner = owner;
            Type  = typeId;
            Id    = id;
            UpdateTypes = types;
            m_Updatables = new List<Updatable>();
            foreach (var type in types)
            {
                switch (type)
                {
                    case UpdatableType.Byte:
                    m_Updatables.Add( new UpdatableOneByte() );
                    break;
                    case UpdatableType.Short:
                    m_Updatables.Add( new UpdatableShort() );
                    break;
                    case UpdatableType.Int:
                    m_Updatables.Add( new UpdatableInt() );
                    break;
                    case UpdatableType.Float:
                    m_Updatables.Add( new UpdatableFloat() );
                    break;
                    case UpdatableType.Double:
                    m_Updatables.Add( new UpdatableDouble() );
                    break;
                    case UpdatableType.Vector:
                    m_Updatables.Add( new UpdatableVector() );
                    break;
                    case UpdatableType.Quaternion:
                    m_Updatables.Add( new UpdatableQuaternion() );
                    break;
                    case UpdatableType.Matrix3x3:
                    m_Updatables.Add( new UpdatableMatrix3x3() );
                    break;
                    case UpdatableType.Matrix4x4:
                    m_Updatables.Add( new UpdatableMatrix4x4() );
                    break;
                    default:
                    throw new InvalidOperationException( "Invalid updatable size." );
                }
            }
        }

        internal void AssignId( uint id )
        {
            Debug.Assert( !m_IdAssigned );
            Id = id;
            m_IdAssigned = true;
        }
    }
}
