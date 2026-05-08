---
title: 建立新的 UCL_Asset 子类工作流
description: 步骤化 SOP — 在 UCL_Core 体系下新增持久化数据类型，**一律继承 UCL_Asset<T>**，禁止裸 ScriptableObject 或自写存档。涵盖继承样板、ID/SaveFolderPath 惯例、AssetGroup attribute、JSON 序列化、Edit/Preview hook、与常见地雷。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_Assets/
namespace: UCL.Core
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create UCL Asset, 新增 Asset, UCL_Asset 子类, 持久化数据]
tags: [workflow, asset, scriptableobject, persistence]
related:
  - ucl_core:Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md | Create EditorPage Workflow | 新 Page 入口（搭配本档 — Page 是 UI、Asset 是数据）
  - ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md | Validate UCL Asset Workflow | UCL_Asset 序列化验收（agent 改完 .json 后验证）
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_SelectAssetPage.md | UCL_SelectAssetPage | 自动列出所有 UCL_Asset 子类的选择 UI（不必自写 list page）
---

# 🛠️ 建立新的 UCL_Asset 子类工作流

> [!IMPORTANT]
> **在 UCL_Core 体系下，所有持久化数据一律继承 `UCL_Asset<T>`**，禁止：
> - 裸 `ScriptableObject` + `[CreateAssetMenu]`（跟 UCL_ModuleService 模块路径机制不兼容）
> - 自写 `File.WriteAllText` / `JsonUtility.ToJson` 之类的存档机制（重复造轮 + 没模块路径解析）
> - FileSystemWatcher / EditorApplication.update polling 双向同步双 store（UCL_Asset 自身就是 source-of-truth，不需要 mirror）
>
> 设计哲学：**一个 .json 档 = 一笔 ID 的 UCL_Asset 子类实例**。base 处理 IO / 模块路径 / Editor UI / 序列化 — 子类只填栏位 + 两个 ctor。

---

## 0. TL;DR — 最小骨架

```csharp
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_<Name>Asset : UCL_Asset<UCL_<Name>Asset>
    {
        public const string DefaultID = "Default";

        public string m_SomeField = string.Empty;
        public int    m_SomeNumber = 0;

        public UCL_<Name>Asset() { ID = DefaultID; }
        public UCL_<Name>Asset(string iID) { Init(iID); }
    }
}
```

**就这样**。没有 `[CreateAssetMenu]`、没有 OnValidate、没有 FileSystemWatcher。Edit / Save / Load 全自动。

---

## 1. 为什么一律 UCL_Asset

| 诉求 | 裸 ScriptableObject | UCL_Asset |
|---|---|---|
| 跨模块存放 | ❌ 写死 `Assets/...` 路径 | ✅ `UCL_ModuleService` 模块相对路径 |
| JSON 序列化 | ⚠ 要自写 ToJson/FromJson | ✅ base 自带 SerializeToJson |
| Editor 编辑 UI | ⚠ 要自写 Custom Inspector | ✅ base OnGUI 自动跑 DrawObjectData 反射 |
| 列表 / 选择 UI | ⚠ 要自写 EditorWindow | ✅ `UCL_SelectAssetPage` 反射列举 |
| Mod 系统兼容 | ❌ 不在模块系统内 | ✅ 对应 UCL_Module 自动切换 |
| Per-file git diff | ⚠ asset 档是 binary YAML | ✅ .json 纯文字、merge 友善 |

**结论**：UCL_Core 本身就是 mod-friendly asset 框架，新增任何持久化数据都该走 UCL_Asset。除非你有**极特殊**理由（目前 UCL_Core 内也只有 `UCL_LocalizeAsset` 等少数例外，因为它们需要 Unity 序列化 hook） — 否则不要绕开。

---

## 2. 必要组成

### 2.1 继承
```csharp
public class UCL_MyAsset : UCL_Asset<UCL_MyAsset>
```
泛型 `T` 是子类自身（CRTP 样板）。

### 2.2 两个 ctor

```csharp
public UCL_MyAsset() { ID = DefaultID; }    // 无参 — 反射 / new() 用
public UCL_MyAsset(string iID) { Init(iID); }  // 带 ID — 显式建立
```

`UCL_Asset<T>` 约束 `T : new()`，所以**无参 ctor 必填**。

### 2.3 ID 默认常数

```csharp
public const string DefaultID = "Default";
```

无参 ctor 用这个当 placeholder ID（Init 时会覆写）。

### 2.4 栏位（采 m_-prefix 惯例）

```csharp
public string m_DisplayName = string.Empty;
public List<string> m_Tags = new List<string>();
public Color m_TintColor = Color.white;
```

- 用 `m_` prefix（被 `UCL_LocalizeManager` / `LocalizeFieldName` 自动去掉前缀显示）
- 默认值用 inline initializer，**不要**在 ctor 内 new（UCL_Asset 反序列化会覆写）

---

## 3. 可选 attribute

| Attribute | 用途 |
|---|---|
| `[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.X)]` | 把 Asset 分到 Data / Config / Editor / Assembly group（影响 SelectAssetPage 排序） |
| `[UCL.Core.ATTR.UCL_Sort(int)]` | group 内排序 hint |
| `[HelpURL("ucl_core:Docs~/{lang}/...")]` | Editor Inspector 显示 ? 按钮跳文件 |

范例见 `UCL_ConfigAsset.cs`：
```csharp
[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
[UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditConfigType.UCL_ConfigAsset)]
public class UCL_ConfigAsset : UCL_Asset<UCL_ConfigAsset>
```

---

## 4. 档案落地

base 自动处理：
- `SaveFolderPath` → `<module>/UCL_Assets/<TypeName>/`
- `AssetPath` → `<SaveFolderPath>/<ID>.json`
- 一个 ID = 一个 .json 档（纯文字、git diff 友善）

范例（`UCL_ConfigAsset` ID=`CurLangKey`）：
```
<module>/UCL_Assets/UCL_ConfigAsset/CurLangKey.json
  → { "m_Value": "MyProj_CurLang" }
```

---

## 5. 编辑与选择 UI

### 5.1 不要自写
- `UCL_CommonEditPage` 自动接手任何 UCL_Asset 的编辑 — 直接呼 `UCL_CommonEditPage.Create(asset)` 即可
- `UCL_SelectAssetPage` 反射列举所有 UCL_Asset 子类，自动 grouped + searchable
- 子类只覆写 `OnGUI` / `Preview` 才需要客制显示，否则 base 用 `UCL_GUILayout.DrawObjectData` 反射出 UI

### 5.2 用 base 入口
```csharp
// 从外部开编辑页
UCL_CommonEditPage.Create(myAsset);

// 从外部开选择页（让使用者选一个 ID）
// 走 UCL_SelectAssetPage 惯例 — 详见 UCL_SelectAssetPage.md
```

---

## 6. 常见地雷

| # | 地雷 | 症状 | 解法 |
|---|---|---|---|
| 1 | 继承 `ScriptableObject` 而非 `UCL_Asset<T>` | 进不了 SelectAssetPage、跟 UCL_ModuleService 不通 | 改继承 `UCL_Asset<T>` |
| 2 | 缺无参 ctor | `UCL_Asset<T>` `where T : new()` 编不过 | 加 `public UCL_MyAsset() { ID = DefaultID; }` |
| 3 | ctor 内 `new List<>()` 把栏位塞满 | 反序列化读不进来（被 ctor 覆写） | 移到 inline field initializer |
| 4 | 栏位名没 m_ prefix | 显示名没对齐 UCL 惯例（虽然能用） | 改用 `m_FieldName` |
| 5 | 自写 FileSystemWatcher / OnValidate write-back 双向同步 | 重复造轮 + race condition | 删掉，UCL_Asset 自己就是 source-of-truth |
| 6 | 用 `[CreateAssetMenu]` 想在 Project window 右键建 | 创出来的 .asset 不是 UCL_Asset 认的 | 不用，走 `CreateData(iID)` 或 SelectAssetPage 流程 |
| 7 | 开个 EditorWindow 自写 list 页 | 重复造轮 | 直接用 `UCL_SelectAssetPage` |
| 8 | 在 .json 之外又开 identities.json / 之类 single-file roster | 双 store 同步问题 | 确认 single source-of-truth — 要嘛全 UCL_Asset、要嘛全 single-file |

---

## 7. 何时触发本 workflow

- 使用者要求「新增一个 X 资料」「做个 X 设定档」「持久化某状态」
- agent 看到自己想 `[CreateAssetMenu]` / `ScriptableObject` / 自写 .json 序列化 — **暂停**，先读本档
- code review 看到他人裸 ScriptableObject — 提案改 UCL_Asset

---

## 8. 范例参考

| Asset | 看点 |
|---|---|
| `UCL_ConfigAsset` | 最小骨架（单 m_Value 字串），DefaultID 惯例 |
| `UCL_BundleAsset` | 加 `IDisposable` + 客制 Field UI (`UCLI_FieldOnGUI`) |
| `UCL_CSVAsset` | 处理大资料 + 自订 OnGUI |
| `UCL_ChatTavernIdentityAsset` | 角色卡（rich persona） — m_-prefix 栏位、List<string> 集合 |

---

## 9. 相关文件

- [Create_EditorPage_Workflow.md](Create_EditorPage_Workflow.md) — 对应 UI 层（Page）的建立规范
- [Validate_UCL_Asset_Workflow.md](Validate_UCL_Asset_Workflow.md) — 改 .json 后跑 ValidateAssetFormat 验收
- [UCL_SelectAssetPage.md](../UCL_EditorPage/UCL_SelectAssetPage.md) — 列表 / 选择 UI（自动接 UCL_Asset 反射）
- [UCL_CommonEditPage.md](../UCL_EditorPage/UCL_CommonEditPage.md) — 编辑 UI 入口
- [Cmd_MigrateAssetToTemplate.md](../API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md) — Asset 客制内容回流 Templates~ 当默认范本（指定 type + id 即可）
- [UCL_CoreBootstrap.md](../UCL_ModuleService/UCL_CoreBootstrap.md) — Templates~ ↔ 专案 .BuiltinModules 双向同步机制
