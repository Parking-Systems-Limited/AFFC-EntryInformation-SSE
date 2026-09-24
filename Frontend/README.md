# AFFC 入閘資訊面板（React Frontend）

亞果機場空運中心（AFFC）入場車輛資訊顯示用 React 前端，透過 SSE（Server-Sent Events）即時更新。

React frontend for AFFC entrance vehicle info, updated live over SSE.

完整技術文件（架構、後端端點、LPRS、重連／watchdog）：見倉庫根目錄 [Doc/TECH.md](../Doc/TECH.md)。

## 環境需求 Prerequisites

- Node.js（建議 v18+）
- npm
- 可連線的 SSE 後端，預設 `http://localhost:5100`（見 `.env`）

## 安裝與啟動 Run

```bash
cd Frontend
npm install
npm run dev
```

開發伺服器：`http://localhost:5173`

建置預覽：

```bash
npm run build
npm run preview
```

## 裝置代碼（clientKey）查詢參數

**裝置代碼（例如 EN21）必須由 URL 查詢參數 `clientKey` 傳入前端。**

- 啟動後請在網址加上 `?clientKey=EN21`
- 範例：`http://localhost:5173/?clientKey=EN21`
- 若未提供參數，前端預設使用 `EN21`
- 前端會連線到：`{VITE_SSE_BASE_URL}/sse/{clientKey}`  
  例：`http://localhost:5100/sse/EN21`

切換其他裝置時改 URL 參數即可，例如：

```text
http://localhost:5173/?clientKey=EN22
```

## SSE 連線摘要

| 項目 | 說明 |
|------|------|
| 連線 | `GET /sse/{clientKey}`（EventSource） |
| 事件 `vehicle` / `info` | 更新車輛資訊（含 photo） |
| 事件 `clear` | 清空面板資料 |
| 事件 `error` | 顯示錯誤訊息 |
| 事件 `ping` | Keep-alive（後端約 20 秒）；前端 90 秒 watchdog + 指數重連 |

車輛 JSON 以英文鍵為主（`lpn`, `entryTime`, …）；詳見 `src/types.ts` 與 [Doc/TECH.md](../Doc/TECH.md)。

## 後端觸發（ingest）

後端透過 `POST /api/vehicle` 查 LPRS 並推送到對應 `clientKey` 的 SSE 頻道。範例見 [Doc/TECH.md](../Doc/TECH.md) Postman 一節。

## 環境變數

檔案：`.env`

```env
VITE_SSE_BASE_URL=http://localhost:5100
```

開發時 `vite.config.ts` 亦代理 `/sse`、`/api` 到 `localhost:5100`，可減少 CORS 問題。此 localhost URL 非密鑰，可提交。

## 畫面結構

- 品牌：AFFC／亞果機場空運中心標誌
- 中間標題：`裝置 {clientKey}`
- 連線狀態：連線中／已連線／重連中／離線
- 車牌列與審批／繳費狀態晶片
- 資訊欄：入場時間、身份、車種、倉庫目的地、繳費時間、預約等

## 注意

- 本目錄僅含 Frontend；SSE 與 LPRS 實作在 Backend。
- 請先啟動 Backend（埠 5100），再開 Frontend。
