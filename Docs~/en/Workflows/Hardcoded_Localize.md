# Hardcoded Localization Workflow (UCL_CodeLocalize)

## 1. Overview
`UCL_CodeLocalize` is a high-performance, hardcoded localization utility designed to store core UI strings directly in C# code. It serves as a reliable fallback and high-speed alternative to external JSON/CSV localization files.

### Why use Hardcoded Localization?
*   **Safety**: Critical UI strings (e.g., "Save", "Cancel", "Error") are always available, even if external asset files are missing.
*   **Performance**: Uses C# `switch` expressions for O(1) or near-O(1) lookup speed with zero memory allocation at runtime.
*   **Maintainability**: Leverages `partial class` to separate translations into different files by language.

## 2. Architecture
The system consists of a core logic file and multiple language-specific partial files:
*   `UCL_CodeLocalize.cs`: Core dispatch logic based on `UCL_LocalizeManager.s_LangName`.
*   `UCL_CodeLocalize.en.cs`: English translations (Final Fallback).
*   `UCL_CodeLocalize.zh-Hant.cs`: Traditional Chinese translations.
*   ... (Other languages)

## 3. How to Use

### 3.1 Fetching a Localized String
Simply call `UCL_CodeLocalize.Get(key)` in your code:
```csharp
string windowTitle = UCL_CodeLocalize.Get("UCL_ModuleServiceEditPage");
```

### 3.2 Fallback Logic
1.  The system identifies the current language via `UCL_LocalizeManager.s_LangName`.
2.  It attempts to find the key in the corresponding language file.
3.  If not found (returns `null`), it falls back to the **English** version.
4.  If still not found, it returns the **Key** itself.

## 4. How to Add New Translations

### Step 1: Add the Key to Language Files
Open the relevant language files (e.g., `UCL_CodeLocalize.zh-Hant.cs`) and add your key-value pair to the `switch` expression:

```csharp
static public string Get_zhHant(string iKey)
{
    return iKey switch
    {
        "My_New_Key" => "我的新詞條",
        // ... existing entries
        _ => null
    };
}
```

### Step 2: Ensure English Fallback
Always add the entry to `UCL_CodeLocalize.en.cs` to ensure that users in other languages have at least an English description if their specific language is missing the translation.

```csharp
static public string Get_en(string iKey)
{
    return iKey switch
    {
        "My_New_Key" => "My New Key",
        _ => iKey // English should always return iKey as default
    };
}
```

## 5. Best Practices
> [!IMPORTANT]
> Use `UCL_CodeLocalize` for **Core UI** and **Framework strings**. For game content (items, dialogue, etc.) that requires frequent updates by non-programmers, continue using `UCL_LocalizeAsset` (External CSV/Text files).
