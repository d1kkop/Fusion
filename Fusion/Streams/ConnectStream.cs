using System.IO;

namespace Fusion
{
    /* ConnectStream intenteion is to put the recipient in a 'Active' state.
     * We dont use the reliable stream for this because the reliablie stream does not know anything about a connection and as such
     * we might receive invalid acks or other data from recipients.
     * So, before any streams become active (Reliable, Unreliable, VariableGroup, etc) we first try to put
     * a recipient in an 'Active' state by handshaking. Once done we can 'easily' throw out data that is out of bounds or invalid not otherwise specified.
     */
    public class ConnectStream : UnreliableStream
    {
        internal ConnectStream( Recipient recipient ) :
            base( recipient )
        {
        }

        internal override StreamId GetStreamId()
        {
            return StreamId.CID;
        }

        internal void ReceiveDataNewConnectionWT( BinaryReader reader, BinaryWriter writer )
        {
            byte streamId = reader.ReadByte();
            if ((StreamId)streamId != StreamId.CID)
            {
                return;
            }
            uint sequence = reader.ReadUInt32();
            if (IsSequenceNewer( sequence, m_UnreliableDataRT.m_Expected ))
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    ushort messageLen = reader.ReadUInt16();
                    byte dataId       = reader.ReadByte();
                    long oldPosition  = reader.BaseStream.Position;
                    Recipient.ReceiveSystemMessageWT( reader, writer, dataId, Recipient.EndPoint, ReliableStream.SystemChannel );
                    sequence += 1;
                    // Move to next read position. This is better as the receive function may not have read all data incase of irrelevant.
                    reader.BaseStream.Position = oldPosition + messageLen;
                }
                m_UnreliableDataRT.m_Expected = sequence;
            }
        }
    }
}
