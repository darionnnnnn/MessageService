# 修改歷程 / 決策記錄

這個資料夾放的是點狀時間的規劃書、審查回饋、與已經併入現行文件的設計決策理由——
**不是**現行文件，讀取前請先確認真的需要（例如要知道某個設定為什麼長這樣、或要追溯
某次改版的取捨），單純想知道「現在系統長什麼樣子」請直接看 repo 根目錄的
[README.md](../../README.md) 與 `docs/` 底下的其他現行文件，不需要進來這裡，避免浪費 token。

## 目錄

| 檔案 | 內容 |
|---|---|
| `CONSOLIDATION-PLAN.md` | 2026-08-13 部署收斂輪：兩專案合併成 `MessageService.Web`、模式改名 `AllInOne`/`Edge`/`Core`/`Viewer`、schema 改用 `Database.Migrate()` 的規劃與執行紀錄 |
| `POST-CONSOLIDATION-REVIEW-PLAN.md` | 部署收斂後的外部審查回饋輪（P0 批次 ingest bug、心跳監測、加密信封 key id 等） |
| `REVIEW-FEEDBACK-2-PLAN.md` | 部署收斂第二輪審查回饋（SQLite 路徑修正、provider 推導與救場、心跳強化等） |
| `DEPLOYMENT-MODES-DECISIONS.md` | `docs/DEPLOYMENT-MODES.md` 移出的設計決策理由、雙行程端到端驗證紀錄、原始建置分期 |
| `WEB-UI-DESIGN-NOTES.md` | 檢視端 UI 歷次改版的設計決策理由、放棄的替代方案、已知限制 |

## 慣例

- 檔名盡量沿用整理前的原檔名，方便對照 git log。
- 現行文件（README、`docs/DEPLOYMENT-*.md`、`docs/ENCRYPTION.md` 等）最上方都有一句指向這裡的提示；
  這裡的檔案不需要回頭複製現行文件的內容，只保留「為什麼」與「當時怎麼決定的」。
