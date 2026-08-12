#if UNITY_EDITOR
// ===========================================================
// 區塊職責：project-scoped EditorPrefs 封裝 —— UCL_Core 內所有 EditorPrefs 一律經此層。
// 物理意義：EditorPrefs 是 per-machine 全域儲存，裸 key 會讓 A 專案的開關直接生效在 B 專案
//          （Tim 2026-08-12 拍板全面收攏；UCL_RepoPath.ProjectFingerprint 的註解早寫著
//          「任何 per-project 的 EditorPrefs key 都該加它」，本類別把那句話從提醒變成 API ——
//          寫下來只讓下一個人知道，變成工具預設才不需要被記得）。
// 數值影響：實際落盤 key = "<key>@<ProjectFingerprint>"。首次讀取時若專案 key 不存在而
//          舊全域 key 存在 → 把舊值抄進專案 key 並 Log（自癒可以自動做，但不能安靜地發生）。
//          **不刪舊全域 key** —— 其他還沒更新 UCL_Core 的專案仍在讀它，刪掉會靜默改變它們的行為。
// 設計取捨：遷移發生在「每個專案各自第一次讀」——舊全域值會被各專案各抄一份（等於把
//          「現在大家共用的狀態」凍結成各自的初始值），之後各自獨立。這保留現狀不突變。
// ===========================================================
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// project-scoped EditorPrefs：key 自動加綴 <see cref="UCL_RepoPath.ProjectFingerprint"/>，
    /// 並在首次讀取時把舊全域 key 的值遷移過來（不刪舊 key）。
    /// UCL_Core 內請一律用本類別，不要裸用 EditorPrefs —— 裸 key 必跨專案汙染。
    /// </summary>
    public static class UCL_ProjectEditorPrefs
    {
        /// <summary>實際落盤 key。需要跟外部工具對 key 時用（例如清 registry）。</summary>
        public static string ScopedKey(string iKey) => $"{iKey}@{UCL_RepoPath.ProjectFingerprint}";

        public static bool GetBool(string iKey, bool iDefault = false)
        {
            MigrateIfNeeded(iKey, () => EditorPrefs.SetBool(ScopedKey(iKey), EditorPrefs.GetBool(iKey)));
            return EditorPrefs.GetBool(ScopedKey(iKey), iDefault);
        }
        public static void SetBool(string iKey, bool iValue) => EditorPrefs.SetBool(ScopedKey(iKey), iValue);

        public static int GetInt(string iKey, int iDefault = 0)
        {
            MigrateIfNeeded(iKey, () => EditorPrefs.SetInt(ScopedKey(iKey), EditorPrefs.GetInt(iKey)));
            return EditorPrefs.GetInt(ScopedKey(iKey), iDefault);
        }
        public static void SetInt(string iKey, int iValue) => EditorPrefs.SetInt(ScopedKey(iKey), iValue);

        public static float GetFloat(string iKey, float iDefault = 0f)
        {
            MigrateIfNeeded(iKey, () => EditorPrefs.SetFloat(ScopedKey(iKey), EditorPrefs.GetFloat(iKey)));
            return EditorPrefs.GetFloat(ScopedKey(iKey), iDefault);
        }
        public static void SetFloat(string iKey, float iValue) => EditorPrefs.SetFloat(ScopedKey(iKey), iValue);

        public static string GetString(string iKey, string iDefault = "")
        {
            MigrateIfNeeded(iKey, () => EditorPrefs.SetString(ScopedKey(iKey), EditorPrefs.GetString(iKey)));
            return EditorPrefs.GetString(ScopedKey(iKey), iDefault);
        }
        public static void SetString(string iKey, string iValue) => EditorPrefs.SetString(ScopedKey(iKey), iValue);

        /// <summary>只看專案 key（遷移前的舊全域 key 不算存在 —— 判準跟讀取行為一致，先讀後判不會變）。</summary>
        public static bool HasKey(string iKey) => EditorPrefs.HasKey(ScopedKey(iKey));

        /// <summary>只刪專案 key。舊全域 key 一律不碰（別的專案還在讀）。</summary>
        public static void DeleteKey(string iKey) => EditorPrefs.DeleteKey(ScopedKey(iKey));

        // 區塊職責：舊全域 key → 專案 key 的一次性遷移。
        // 物理意義：EditorPrefs 沒有「查型別」API，所以複製動作由各型別 getter 以 closure 傳入；
        //          本函式只判斷「該不該遷」並負責把這件事說出口。
        static void MigrateIfNeeded(string iKey, System.Action iCopy)
        {
            string aScoped = ScopedKey(iKey);
            if (EditorPrefs.HasKey(aScoped) || !EditorPrefs.HasKey(iKey)) return;
            iCopy();
            Debug.Log($"[UCL_ProjectEditorPrefs] 舊全域 key「{iKey}」首次遷移為本專案 key（舊 key 保留給其他專案）。");
        }
    }
}
#endif
