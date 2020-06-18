
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// alter version of easing functions from https://easings.net/
/// </summary>
namespace UCL.Core.Tween {
    static class Const {
        public const int DirCount = 2;
        public const int DirMask = 0b_0000_0000_0000_0011;
    }
    public enum EaseDir {
        In = 0,
        Out,
        InOut
    }
    public enum EaseClass {
        Linear = 0,
        Spring,
        Bounce,
        Elastic,
        Sin,
        Quad,
        Cubic,
        Quart,
        Quint,
        Expo,
        Circ,
        Back
    }

    public enum EaseType {
        Linear = EaseClass.Linear << Const.DirCount | EaseDir.In,
        Spring = EaseClass.Spring << Const.DirCount | EaseDir.In,

        InBounce = EaseClass.Bounce << Const.DirCount | EaseDir.In,
        OutBounce = EaseClass.Bounce << Const.DirCount | EaseDir.Out,
        InOutBounce = EaseClass.Bounce << Const.DirCount | EaseDir.InOut,

        InElastic = EaseClass.Elastic << Const.DirCount | EaseDir.In,
        OutElastic = EaseClass.Elastic << Const.DirCount | EaseDir.Out,
        InOutElastic = EaseClass.Elastic << Const.DirCount | EaseDir.InOut,

        InSin = EaseClass.Sin << Const.DirCount | EaseDir.In,
        OutSin = EaseClass.Sin << Const.DirCount | EaseDir.Out,
        InOutSin = EaseClass.Sin << Const.DirCount | EaseDir.InOut,

        InQuad = EaseClass.Quad << Const.DirCount | EaseDir.In,
        OutQuad = EaseClass.Quad << Const.DirCount | EaseDir.Out,
        InOutQuad = EaseClass.Quad << Const.DirCount | EaseDir.InOut,

        InCubic = EaseClass.Cubic << Const.DirCount | EaseDir.In,
        OutCubic = EaseClass.Cubic << Const.DirCount | EaseDir.Out,
        InOutCubic = EaseClass.Cubic << Const.DirCount | EaseDir.InOut,

        InQuart = EaseClass.Quart << Const.DirCount | EaseDir.In,
        OutQuart = EaseClass.Quart << Const.DirCount | EaseDir.Out,
        InOutQuart = EaseClass.Quart << Const.DirCount | EaseDir.InOut,

        InQuint = EaseClass.Quint << Const.DirCount | EaseDir.In,
        OutQuint = EaseClass.Quint << Const.DirCount | EaseDir.Out,
        InOutQuint = EaseClass.Quint << Const.DirCount | EaseDir.InOut,

        InExpo = EaseClass.Expo << Const.DirCount | EaseDir.In,
        OutExpo = EaseClass.Expo << Const.DirCount | EaseDir.Out,
        InOutExpo = EaseClass.Expo << Const.DirCount | EaseDir.InOut,

        InCirc = EaseClass.Circ << Const.DirCount | EaseDir.In,
        OutCirc = EaseClass.Circ << Const.DirCount | EaseDir.Out,
        InOutCirc = EaseClass.Circ << Const.DirCount | EaseDir.InOut,

        InBack = EaseClass.Back << Const.DirCount | EaseDir.In,
        OutBack = EaseClass.Back << Const.DirCount | EaseDir.Out,
        InOutBack = EaseClass.Back << Const.DirCount | EaseDir.InOut,
    }
    public static class EaseCreator {
        //static Dictionary<EaseType, Ease.UCL_Ease> m_Dic = new Dictionary<EaseType, Ease.UCL_Ease>();
        static Dictionary<EaseClass, Dictionary<EaseDir, Ease.UCL_Ease>> m_EaseDic =
            new Dictionary<EaseClass, Dictionary<EaseDir, Ease.UCL_Ease>>();
        public static Ease.UCL_Ease Create(EaseClass ease) {
            switch(ease) {
                ///*
                case EaseClass.Linear: return new Ease.Linear();
                case EaseClass.Spring: return new Ease.Spring();
                case EaseClass.Bounce: return new Ease.Bounce();
                case EaseClass.Elastic: return new Ease.Elastic();
                case EaseClass.Sin: return new Ease.Sin();
                case EaseClass.Quad: return new Ease.Quad();
                case EaseClass.Cubic: return new Ease.Cubic();
                case EaseClass.Quart: return new Ease.Quart();
                case EaseClass.Quint: return new Ease.Quint();
                case EaseClass.Expo: return new Ease.Expo();
                case EaseClass.Circ: return new Ease.Circ();
                //*/
                default:
                    //Debug.LogWarning("Ease:" + ease.ToString());
                    Type type = Type.GetType("UCL.Core.Tween.Ease." + ease.ToString());
                    var e = Activator.CreateInstance(type) as Ease.UCL_Ease;
                    if(e != null) return e;
                    return new Ease.UCL_Ease();
            }
            /*
             * https://stackoverflow.com/questions/493490/converting-a-string-to-a-class-name
            Type elementType = Type.GetType("FullyQualifiedName.Of.Customer");
            Type listType = typeof(List<>).MakeGenericType(new Type[] { elementType });

            object list = Activator.CreateInstance(listType);
            */
        }
        public static EaseType GetType(EaseClass ec, EaseDir dir) {
            switch(ec) {
                case EaseClass.Linear:
                case EaseClass.Spring:
                    dir = EaseDir.In;//No dir
                    break;
            }
            return (EaseType)((int)ec << Const.DirCount | (int)dir);
        }
        public static EaseClass GetClass(EaseType ease) {
            return (EaseClass)((int)ease >> Const.DirCount);
        }
        public static EaseDir GetDir(EaseType ease) {
            return (EaseDir)((int)ease & Const.DirMask);
        }
        /// <summary>
        /// Create a new UCL_Ease, please use Get(EaseType ease) if you don't need to modified ease
        /// </summary>
        /// <param name="ease"></param>
        /// <returns></returns>
        public static Ease.UCL_Ease Create(EaseType ease) {
            return Create(GetClass(ease)).SetDir(GetDir(ease));
        }
        /// <summary>
        /// Use Common UCL_Ease if you don't need to modified Ease
        /// </summary>
        /// <param name="ease"></param>
        /// <returns></returns>
        public static Ease.UCL_Ease Get(EaseType ease) {
            return Get(GetClass(ease), GetDir(ease));
        }
        /// <summary>
        /// Use Common UCL_Ease if you don't need to modified Ease
        /// </summary>
        /// <param name="ease"></param>
        /// <returns></returns>
        public static Ease.UCL_Ease Get(EaseClass ease, EaseDir dir = EaseDir.In) {
            lock(m_EaseDic) {
                if(!m_EaseDic.ContainsKey(ease)) {
                    Dictionary<EaseDir , Ease.UCL_Ease> ease_dic = new Dictionary<EaseDir, Ease.UCL_Ease>();
                    
                    foreach(EaseDir e_dir in Enum.GetValues(typeof(EaseDir))) {
                        ease_dic.Add(e_dir, Create(ease).SetDir(e_dir));
                    }
                    m_EaseDic[ease] = ease_dic;//Create(ease);
                }
            }

            return m_EaseDic[ease][dir];
        }
    }

}
namespace UCL.Core.Tween.Ease {
    [System.Serializable]
    public class UCL_Ease {
        public EaseDir m_Dir = EaseDir.In;
        /// <summary>
        /// return the path of the script
        /// </summary>
        /// <param name="libpath"></param> Root directory of UCLib
        /// <param name="class_name"></param> 
        /// <returns></returns>
        virtual public string GetScriptPath(string libpath,string class_name) {
            string file_name = "UCL_Ease" + class_name + ".cs";
            string sc_path = libpath + "/UCL_Core_Scripts/TweenCore/UCL_EaseScripts/" + file_name;
            return sc_path;
        }

        virtual public float GetEase(float start, float end, float value) {
            return Mathf.Lerp(start, end, GetEase(value));
        }
        virtual public EaseClass GetClass() {
            return EaseClass.Linear;
        }
        virtual public EaseDir GetDir() {
            return m_Dir;
        }
        virtual public EaseType GetEaseType() {
            return EaseCreator.GetType(GetClass(), m_Dir);
        }
        /// <summary>
        /// assume start is 0 and end is 1
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        virtual public float GetEase(float value) {
            return value;//Linear
        }
        virtual public UCL_Ease SetDir(EaseDir dir) {
            m_Dir = dir;
            return this;
        }
    }
}