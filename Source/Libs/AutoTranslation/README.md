# AutoTranslation 編譯期參考組件

`AutoTranslation.dll` 取自 Steam 工作坊 Auto Translation（`seohyeon.autotranslation`，工作坊 ID 3278005460）1.6 版。

**僅供編譯期參考，不隨 Mod 出貨**（csproj 中 `Private=false`）。執行期一律使用玩家實際安裝的 Auto Translation；
`Compat/AutoTranslation/` 下的程式碼只在偵測到 Auto Translation 啟用時才會被 JIT，Auto Translation 缺席時不會觸發型別載入。
**Auto Translation 型別只能出現在方法簽章與方法本體**，不得當任何類別的基底、介面或欄位型別——那是 `Assembly.GetTypes()`
與 `GetFields()` 會解析的層級，Auto Translation 缺席時整顆框架 DLL 會被 RimWorld 拒載（`FrameworkAssembly_HasNoAutoTranslationTypesInBaseInterfacesOrFields` 測試鎖住）。

Auto Translation 更新且相關類別簽章變動時，換掉這顆 DLL 並修正轉接器。
