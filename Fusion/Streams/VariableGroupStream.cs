using System.Collections.Generic;

namespace Fusion
{
    class VariableGroupStream
    {
        const byte VGID  = 3;
        const byte VGACK = 4;
        const int MaxVariableGroupSize   = ushort.MaxValue;

        struct UpdatableSequence
        {
            uint m_Newest;
            uint m_Received;
            uint m_Ack;
        }

        struct GroupData
        {
            internal uint m_Newest;
            internal uint m_Received;
            internal uint m_Ack;
            internal List<UpdatableSequence> m_UpdatableSequences;
            internal VariableGroup m_Group;
        };

        Dictionary<uint, VariableGroup> m_Groups = new Dictionary<uint, VariableGroup>();

        internal void AddUpdatable( Updatable updatable )
        {
            if (!m_Groups.TryGetValue( updatable.Group.Id, out VariableGroup group ))
            {
                group = updatable.Group;
                m_Groups.Add( updatable.Group.Id, group );
            }
        }

        internal void FlushST()
        {

        }
    }
}
