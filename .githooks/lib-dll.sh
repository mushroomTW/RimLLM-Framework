# 出貨 DLL 檢查的共用設定，由 pre-commit 與 post-commit 載入。
dll="Assemblies/RimLLM Framework.dll"
project="Source/RimLLM Framework/RimLLM Framework.csproj"
# 正式專案的建置輸入；測試專案與 .editorconfig 不影響出貨 DLL 的 IL。
set -- "Source/RimLLM Framework/" Source/Directory.Build.props Source/Directory.Build.targets global.json
# pre-commit 檢查過的內容留下指紋，post-commit 比對 HEAD 相同就不重複建置。
# 存指紋而非空檔：pre-commit 通過但提交被 commit-msg 擋下時，殘留的標記不能讓下一筆 rebase 提交略過檢查。
dll_checked_marker=$(git rev-parse --git-path rimllm-dll-checked)

index_fingerprint() {
  git ls-files --format='%(objectname) %(path)' -- "$@" Assemblies/ | git hash-object --stdin
}

head_fingerprint() {
  git ls-tree -r --format='%(objectname) %(path)' HEAD -- "$@" Assemblies/ | git hash-object --stdin
}
