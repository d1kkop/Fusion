

namespace Fusion
{
    public enum UpdatableType
    {
        Byte,
        Short,
        Int,
        Float,
        Double,
        String,
        Vector,
        Quaternion,
        Matrix3x3,
        Matrix4x4,
        List,
        Dictionary,
        Count
    }

    public class Updatable
    {
     //   VariableGroup m_Group;

      //  public VariableGroup Group => m_Group;
    }

    public class UpdatableOneByte : Updatable
    {

    }

    public class UpdatableTwoBytes : Updatable
    {

    }

    public class UpdatableFourBytes : Updatable
    {

    }

    public class UpdatableEightBytes : Updatable
    {

    }

    public class UpdatableArray : Updatable
    {

    }

    public class UpdatableVector : Updatable
    {
        public float m_X, m_Y, m_Z;
    }

    public class UpdatableQuaternion : Updatable
    {
        public float m_X, m_Y, m_Z, m_W;
    }

    public class UpdatableMatrix3x3 : Updatable
    {
        public float [] m_Cells = new float[9];
    }

    public class UpdatableMatrix4x4 : Updatable
    {
        public float [] m_Cells = new float[16];
    }

    public class UpdatableShort : UpdatableTwoBytes { }

    public class UpdatableInt : UpdatableFourBytes { }
    public class UpdatableFloat : UpdatableFourBytes { }
    public class UpdatableDouble : UpdatableEightBytes { }

}
