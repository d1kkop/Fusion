using System.Collections.Generic;

namespace Fusion
{
    interface IMessage
    {
        void Process();
    }

    public class Messagable
    {
        Queue<IMessage> m_Messages = new Queue<IMessage>();

        internal void AddMessage( IMessage msg )
        {
            lock (m_Messages)
            {
                m_Messages.Enqueue( msg );
            }
        }

        internal void ProcessMessages()
        {
            while (m_Messages.Count != 0)
            {
                IMessage msg;
                lock (m_Messages)
                {
                    msg = m_Messages.Dequeue();
                }
                msg.Process();
            }
        }
    }
}
