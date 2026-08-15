# MessageService 專案規則

## 文件紀律：現行版本 vs 修改歷程

本專案的 Markdown 文件分成兩層，動筆前後都要遵守：

- **現行文件**：repo 根目錄的 `README.md` 與 `docs/*.md`（`DEPLOYMENT-GUIDE`、`DEPLOYMENT-MODES`、
  `ENCRYPTION`、`LINE-BOT-SETUP`）。只寫「現在就是如此」的事實。
- **`docs/current/`**：進行中輪次的工作文件（計畫、回饋清單、驗收紀錄）。新一輪開工就建在這裡。
- **`docs/history/`**：已完成輪次的過程記錄與決策理由，附 `README.md` 索引。

### 讀取紀律

預設**只讀現行文件**。`docs/history/` 非必要不要讀（先看它的 `README.md` 索引決定要開哪一份），
避免浪費 token。

### 寫作紀律

現行文件不寫「為什麼變成這樣」。定稿前掃一次這些字眼——「原本」「曾經」「之前是」「後來」
「改成／改為」「經過討論」「決定採用」「為了解決」「修正了」「第 X 輪」「回饋」「審查後」
「取代」「棄用」「不再」——抓到就只留動作後的結果、用現在式陳述，把原因與過程挪進
`docs/history/`。真的需要提示背景時只能用一行連結帶過，不可就地展開。

同一主題只在一份文件寫完整版，其他地方改成連結，不要重複解釋。

### 每輪完工的四步收尾

1. `git mv docs/current/XXX.md docs/history/YYYY-MM-DD_XXX.md`（加日期前綴，確認 `git status`
   顯示的是 rename 而非新增＋刪除）。
2. 更新 `docs/history/README.md` 索引，補一列一行摘要。
3. 更新現行文件的「目前狀態」段落，只寫結論。
4. 同步這輪改動導致過時的現行文件內容，並跟程式碼／設定檔核對一次。
