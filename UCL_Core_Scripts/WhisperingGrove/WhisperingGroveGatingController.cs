using UnityEngine;
using UnityEngine.UI;
using System.Collections;
//using RCG.UI; compile error
using RCG;

namespace UCL.Core.WhisperingGrove
{
    // 區塊職責：控制「低語樹林」模組的視覺門檻邏輯 (UI Gating)
    // 物理意義：根據玩家在子故事場景中的停留時間，動態調整「記憶噪訊」材質的強度，實現「聆聽越久，真相越清晰/干涉越強」的體驗。
    // 數值影響：直接修改 UI Image 材質中的 _StaticIntensity 屬性 (0.0 到 0.7 之間)。
    public class WhisperingGroveGatingController : MonoBehaviour
    {
        // 區塊職責：定義受監控的故事 ID 與子故事名稱
        private const string TARGET_STORY_ID = "WhisperingGrove";
        private const string SUB_STORY_LISTEN = "Listen";
        private const string SUB_STORY_BONFIRE = "Bonfire";

        // 區塊職責：定義時間門檻與強度映射參數
        private const float GATING_START_TIME = 3.0f;  // 開始產生噪訊的時間 (秒)
        private const float GATING_PEAK_TIME = 10.0f;  // 噪訊達到高峰的時間 (秒)
        private const float MAX_INTENSITY = 0.7f;      // 最大噪訊強度

        // 區塊職責：快取 UI 組件與材質狀態
        //private RCG_OptionEventUI m_StoryUI;           // 引用遊戲的故事 UI 系統 compile error
        private string m_CurrentSubStory;              // 當前追蹤的子故事 ID
        private float m_DwellTime = 0f;                // 玩家在當前子故事停留的累計時間 (秒)

        // 區塊職責：初始化組件並開始監聽
        // 物理意義：在物件啟用時，尋找場景中的故事 UI 組件並進行快取。
        private void Awake()
        {
            // 尋找場景中唯一的故事 UI 實例
            // m_StoryUI = Object.FindObjectOfType<RCG_OptionEventUI>(); compile error
        }

        // 區塊職責：每幀更新計時器與材質屬性
        // 物理意義：計算停留時間並根據時間曲線映射至 Shader 參數。
        private void Update()
        {
            // 如果沒找到 UI 或 UI 未顯示，則不執行邏輯
            // if (m_StoryUI == null || !m_StoryUI.gameObject.activeInHierarchy) compile error
            {
                return;
            }

            // 區塊職責：偵測故事與子故事切換並重置計時  compile error
            // 數值影響：當子故事變更時，m_DwellTime 重置為 0，確保每個場景的門檻獨立計算。
            //if (m_StoryUI.m_SubStory != m_CurrentSubStory)
            //{
            //    m_CurrentSubStory = m_StoryUI.m_SubStory; // 更新當前子故事記錄
            //    m_DwellTime = 0f;                         // 重設計時器
            //}

            // 僅在目標故事「低語樹林」下執行邏輯
            //if (m_StoryUI.m_StoryData != null && m_StoryUI.m_StoryData.ID == TARGET_STORY_ID)  compile error
            //{
            //    // 針對特定子故事進行門檻處理 (Listen 或 Bonfire)
            //    if (m_CurrentSubStory == SUB_STORY_LISTEN || m_CurrentSubStory == SUB_STORY_BONFIRE)
            //    {
            //        m_DwellTime += Time.deltaTime;     // 累加停留時間
            //    }
            //    else
            //    {
            //        m_DwellTime = 0f;                  // 其他子故事 (如 Start) 保持清淨
            //    }

            //    UpdateStaticIntensity();               // 更新 Shader 強度
            //}
        }

        // 區塊職責：計算並應用噪訊強度
        // 物理意義：將停留時間轉換為 0~1 的強度值，應用於 Shader 的 _StaticIntensity 屬性。
        private void UpdateStaticIntensity()
        {
            // 獲取當前 UI 使用的事件圖片組件
            // Image img = m_StoryUI.EventImage;  compile error
            // if (img == null || img.material == null) return;  compile error

            // 區塊職責：時間強度曲線計算
            // 邏輯說明：
            // 1. m_DwellTime <= 3s: intensity = 0 (維持原始安靜夜景)
            // 2. 3s < m_DwellTime <= 10s: 使用 InverseLerp 計算 3 到 10 秒間的進度，線性映射至 0~0.7
            // 3. m_DwellTime > 10s: 達到 0.7 峰值，並加上快頻率 Sine 震盪模擬「數位殘影」的動態感
            float intensity = 0f;
            if (m_DwellTime > GATING_START_TIME)
            {
                // 計算 0~1 的標準化進度值
                float progress = Mathf.InverseLerp(GATING_START_TIME, GATING_PEAK_TIME, m_DwellTime);
                
                // 計算基礎強度，最高不超過 MAX_INTENSITY
                intensity = progress * MAX_INTENSITY; 
                
                // 當時間超過 10s，進入高頻震盪模式 (模擬 Slow Decay 的電磁不穩定性)
                if (m_DwellTime > GATING_PEAK_TIME)
                {
                    intensity += Mathf.Sin(Time.time * 25f) * 0.03f; 
                }
            }

            // 數值影響：透過 Material.SetFloat 將強度值寫入 Shader 變數
            // 限制：此操作要求材質球必須使用支援 _StaticIntensity 屬性的 Shader
            // img.material.SetFloat("_StaticIntensity", intensity);  compile error
        }
    }
}
