using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UCL.Core.UI
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_TextFollower : MonoBehaviour
    {
        /// <summary>
        /// Follow text OnEnable
        /// </summary>
        public bool m_IsFollowOnEnable = true;
        /// <summary>
        /// Follow text on Update
        /// </summary>
        public bool m_IsFollowOnUpdate = false;
        /// <summary>
        /// Target Text to follow
        /// </summary>
        public Text m_TextContent;
        /// <summary>
        /// Target string in TextContent to follow
        /// </summary>
        public string m_Target;
        /// <summary>
        /// Which fit string to follow(ect. m_Target = "A", Context = "An Apple",then ID = 0 will follow the first A in string,ID = 1 will follow the second)
        /// </summary>
        public int m_ID = 0;
        /// <summary>
        /// The canvas camera
        /// </summary>
        public Camera m_Camera = null;
        /// <summary>
        /// 
        /// </summary>
        protected string[] m_Seperators = null;
        private void OnEnable()
        {
            
            if (m_IsFollowOnEnable)
            {
                UCL.Core.ServiceLib.UCL_UpdateService.AddDelayAction(FollowPos, 0.1f);
            }
        }
        private void Update()
        {
            if (m_IsFollowOnUpdate)
            {
                FollowPos();
            }
        }
        /// <summary>
        /// Follow the text position
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        virtual public void FollowPos()
        {
            if (m_TextContent == null)
            {
                return;
            }
            if (m_Seperators == null || m_Seperators.Length < 1)
            {
                m_Seperators = new string[1];
                m_Seperators[0] = m_Target;
            }
            if (m_Camera == null)
            {
                Canvas aCanvas = transform.GetComponentInParent<Canvas>();
                if (aCanvas != null)
                {
                    m_Camera = aCanvas.worldCamera;
                }
                else
                {
                    m_Camera = Camera.main;
                    Debug.LogError("aCanvas == null");
                }
            }

            m_Seperators[0] = m_Target;
            string aStrContent = m_TextContent.text;

            TextGenerator aTextGen = new TextGenerator(aStrContent.Length);
            Vector2 aExtents = m_TextContent.gameObject.GetComponent<RectTransform>().rect.size;
            aTextGen.Populate(aStrContent, m_TextContent.GetGenerationSettings(aExtents));
            List<UICharInfo> aCharacters = new List<UICharInfo>();
            aTextGen.GetCharacters(aCharacters);

            if (m_Target.Length >= 1)
            {
                var aResult = aStrContent.Split(m_Seperators, StringSplitOptions.None);
                if (aResult.Length > 0)
                {
                    int aIndex = 0;
                    for (int i = 0; i <= m_ID && i < aResult.Length; i++)
                    {
                        aIndex += aResult[i].Length + m_Target.Length;
                    }
                    aIndex -= Mathf.CeilToInt(0.5f * m_Target.Length);

                    if (aIndex >= 0 && aIndex < aCharacters.Count)
                    {
                        var aCharInfo = aCharacters[aIndex];
                        if (m_Camera != null)
                        {
                            var aScreePos = m_Camera.WorldToScreenPoint(m_TextContent.transform.position);
                            aScreePos += new Vector3(aCharInfo.cursorPos.x + 0.5f * aCharInfo.charWidth, aCharInfo.cursorPos.y - aCharInfo.charWidth);
                            var aPos = m_Camera.ScreenToWorldPoint(aScreePos);
                            transform.position = aPos;
                        }
                        else
                        {
                            var aScreePos = m_TextContent.transform.position;
                            aScreePos += new Vector3(aCharInfo.cursorPos.x + 0.5f * aCharInfo.charWidth, aCharInfo.cursorPos.y - aCharInfo.charWidth);
                            transform.position = aScreePos;
                        }
                    }
                }
            }
        }
    }
}