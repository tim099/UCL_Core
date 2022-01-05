using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI
{
    [AddComponentMenu("UCL/UI/Vertical Layout Group", 151)]
    public class UCL_VerticalLayoutGroup : UCL_HorizontalOrVerticalLayoutGroup
    {
        protected UCL_VerticalLayoutGroup()
        { }

        public override void CalculateLayoutInputHorizontal()
        {
            base.CalculateLayoutInputHorizontal();
            CalcAlongAxis(0, true);
        }

        public override void CalculateLayoutInputVertical()
        {
            CalcAlongAxis(1, true);
        }

        public override void SetLayoutHorizontal()
        {
            SetChildrenAlongAxis(0, true);
        }

        public override void SetLayoutVertical()
        {
            SetChildrenAlongAxis(1, true);
        }
    }
}

