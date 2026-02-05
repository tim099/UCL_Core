
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
using UnityEngine;

namespace UCL.Core
{
    public interface UCLI_ValueSource : UCLI_TypeListable
    {
        object GetValue(UCLI_Scope scope);
    }
    public abstract class UCL_ValueSource : JsonLib.UnityJsonSerializable, UCLI_ValueSource, UCLI_ShortName
    {
        abstract public object GetValue(UCLI_Scope scope);


        virtual public string GetShortName() => this.ToString();
    }

    public class UCL_VariableVS : UCL_ValueSource
    {
        public string m_Variable;
        public override string ToString() => m_Variable;
        public override object GetValue(UCLI_Scope scope)
        {
            return scope.GetVariable(m_Variable);
        }
    }
}
