# RimTalk 編譯期參考組件

`RimTalk.dll` 取自 Steam 工作坊 RimTalk（`cj.rimtalk`，工作坊 ID 3551203752）1.6 版，Mod 版本 1.2.12。

**僅供編譯期參考，不隨 Mod 出貨**（csproj 中 `Private=false`）。執行期一律使用玩家實際安裝的 RimTalk；
`Compat/RimTalk/` 下的程式碼只在偵測到 RimTalk 啟用時才會被 JIT，RimTalk 缺席時不會觸發型別載入。

RimTalk 更新且 `IAIClient` / `Payload` / `JsonStreamParser` 簽名變動時，換掉這顆 DLL 並修正轉接器。
