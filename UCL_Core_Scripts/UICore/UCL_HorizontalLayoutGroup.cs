using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI
{
    [AddComponentMenu("UCL/UI/Horizontal Layout Group", 150)]
    public class UCL_HorizontalLayoutGroup : UCL_HorizontalOrVerticalLayoutGroup
    {
        protected UCL_HorizontalLayoutGroup() { }

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            CalcAlongAxis(0, false);
        }

        public override void CalculateLayoutInputVertical()
        {
            CalcAlongAxis(1, false);
        }

        public override void SetLayoutHorizontal()
        {
            SetChildrenAlongAxis(0, false);
        }

        public override void SetLayoutVertical()
        {
            SetChildrenAlongAxis(1, false);
        }
    }
}
