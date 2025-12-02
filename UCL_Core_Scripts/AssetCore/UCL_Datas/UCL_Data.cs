
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/24 2024 13:53
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
namespace UCL.Core
{
    public interface UCLI_Data
    {
        /// <summary>
        /// Unique key of this data
        /// </summary>
        string Key { get; }
        /// <summary>
        /// Is the Empty(Null)
        /// </summary>
        bool IsEmpty { get; }
        /// <summary>
        /// Name if this Data
        /// </summary>
        string Name => string.Empty;
    }

    [UCL.Core.ATTR.UCL_IgnoreInTypeListable]
    public class UCL_Data : UCL.Core.JsonLib.UnityJsonSerializable
    {
        /// <summary>
        /// Unique key of this data
        /// </summary>
        virtual public string Key => string.Empty;
        /// <summary>
        /// Is the Empty(Null)
        /// </summary>
        virtual public bool IsEmpty => true;
        /// <summary>
        /// Name if this Data
        /// </summary>
        virtual public string Name => string.Empty;


        /// <summary>
        /// Release Object load from UCL_Data
        /// </summary>
        /// <param name=""></param>
        virtual public void Release()
        {

        }
        virtual public UniTask<UnityEngine.Object> LoadAsync(CancellationToken iToken)
        {
            return default;
        }
        virtual public Sprite GetSprite()
        {
            return null;
        }
        virtual public UniTask<Sprite> LoadSpriteAsync(CancellationToken iToken, UCLI_LoadTextureConfig config)
        {
            return default;
        }
        virtual public async UniTask<Texture2D> LoadTextureAsync(CancellationToken iToken, UCLI_LoadTextureConfig config)
        {
            var sprite = await LoadSpriteAsync(iToken, config);
            if(sprite == null)
            {
                return null;
            }
            return sprite.texture;
        }
        virtual public async UniTask<T> LoadComponentAsync<T>(CancellationToken iToken) where T : UnityEngine.Component
        {

            UnityEngine.Object aObject = await LoadAsync(iToken);
            if (aObject == null)
            {
                Debug.LogError($"UCL_Data.LoadComponentAsync, aObject == null,Key:{Key}");
                return null;
            }
            GameObject aGameObject = aObject as GameObject;

            if (aGameObject == null)
            {
                Debug.LogError($"UCL_Data.LoadAsync aGameObject == null,Key:{Key}");
                return null;
            }
            T aComponent = aGameObject.GetComponent<T>();
            if (aComponent == null)
            {
                Debug.LogError($"UCL_Data.LoadAsync,aGameObject:{aGameObject.name},Component({typeof(T).FullName}) == null,m_AddressableKey:{Key}");
                return null;
            }
            return aComponent;
        }
    }
}