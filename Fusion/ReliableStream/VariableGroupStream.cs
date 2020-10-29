﻿using System.Collections.Generic;

namespace Fusion
{
    class VariableGroupStream
    {
        const byte VGID  = 100;
        const byte VGACK = 110;
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

    }
}
