using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Fusion.RPC
{
    public class RPC : Attribute
    {

    }

    public partial class ConnectedNode
    {
        Dictionary<string, MemberInfo> m_RPCs;

        public ConnectedNode()
        {
            m_RPCs = new Dictionary<string, MemberInfo>();
            var assembly = Assembly.GetExecutingAssembly();
            var methods  = assembly.GetTypes()
                      .SelectMany(t => t.GetMethods())
                      .Where(m => m.GetCustomAttributes(typeof(RPC), false).Length > 0)
                      .ToArray();
            foreach( var m in methods )
            {
                m_RPCs.Add( m.Name, m );
            }
        }
    }
}
