# AFFC SSE 技術文件 / Technical Documentation

> 雙語說明（繁體中文 + English）。本專案為 AFFC 入閘資訊面板：Backend（ASP.NET Core 8）以 SSE 推送車輛資料，Frontend（React + Vite）即時顯示。  
> Bilingual notes (Traditional Chinese + English). Entrance kiosk panel: ASP.NET Core 8 Backend pushes vehicle data via SSE; React + Vite Frontend renders it live.

---

## 1. 架構 / Architecture

```text
[LPRS / Management API]  <--HTTP GET-->  [Backend :5100]  --SSE-->  [Frontend kiosk]
        ^                                      ^
        |                                      |
   x-api-key                              POST /api/vehicle
                                          POST /api/plate
                                          POST /api/clear/{clientKey}
                                          GET  /sse/{clientKey}
                                          GET  /health
```

| 元件 Component | 路徑 Path | 技術 Stack |
|---|---|---|
| Backend | `Backend/Web_dPanel_Backend/` | .NET 8 Minimal API, `MessageHub`, HttpClient → LPRS |
| Frontend | `Frontend/` | React 19, Vite 8, EventSource |

**主流程 Primary flow**

1. 外部系統（或 Postman）呼叫 `POST /api/vehicle`（或 `/api/plate`），帶 `clientKey` + 車牌 `lpn`。  
   Caller posts `clientKey` + plate `lpn` to `/api/vehicle` (or `/api/plate`).
2. Backend 向 LPRS `GET /api/LprsEvents/Vehicle/{lpn}` 查詢（Header：`x-api-key`）。  
   Backend queries LPRS with `x-api-key`.
3. 映射為面板 JSON，經 `MessageHub` 以 SSE 事件 `vehicle`（及 `info`）推給該 `clientKey` 的訂閱者。  
   Mapped panel JSON is published as SSE `vehicle` (+ `info`) to that `clientKey`.
4. Frontend 以 `EventSource` 連線 `/sse/{clientKey}`，更新畫面；`ping` 維持連線；斷線自動重連 + watchdog。  
   Frontend listens on `/sse/{clientKey}`; `ping` keep-alive; auto-reconnect + watchdog.

**舊相容 Legacy：** `POST /api/message` 仍保留（依 `clientKey` 是否 `EN*` 分流入場／出場字串 payload）。新面板請用 `/api/vehicle`。  
Legacy `/api/message` remains; prefer `/api/vehicle` for the new panel.

---


詳細機制圖（架構、Happy Path 時序、SSE 重連／Watchdog、失敗路徑）見：

| 檔案 File | 說明 |
|-----------|------|

建議用瀏覽器開啟對應 `.html`。  
Open the `.html` files in a browser for best viewing.

---

## 2. HTTP 端點 / Endpoints

預設監聽 Default listen：`http://localhost:5100`（`Sse:ListenUrl`）。

| Method | Path | 說明 Description |
|--------|------|------------------|
| `GET` | `/sse/{clientKey}` | SSE 訂閱；可選 IP 白名單（`Sse:RestrictByIp`） |
| `POST` | `/api/vehicle` | 車牌 → LPRS → SSE `vehicle`/`info` |
| `POST` | `/api/plate` | 同上別名 Same as `/api/vehicle` |
| `POST` | `/api/clear/{clientKey}` | 清除最新狀態並送 `clear` |
| `POST` | `/api/message` | 舊版 ingest（字串 payload） |
| `GET` | `/health` | 健康檢查（含 `apiKeyConfigured` 布林，**不會**回傳金鑰） |

### `POST /api/vehicle` 請求體 Body

```json
{
  "clientKey": "EN21",
  "lpn": "AB1234",
  "tid": "optional-transaction-id"
}
```

亦可使用 `plate` 或 `vehicleNo` 代替 `lpn`。  
`plate` / `vehicleNo` may replace `lpn`.

成功時回傳 `status: "sent"` 與映射後 `payload`。LPRS 失敗時可回 502，並向該 `clientKey` 推送 SSE `error`。  
On LPRS failure may return 502 and publish SSE `error` to that `clientKey`.

---

## 3. SSE 事件 / SSE Events

連線：`GET /sse/{clientKey}`，`Content-Type: text/event-stream; charset=utf-8`。

連線建立時會 **replay** 該 `clientKey` 最近一次的：`vehicle`, `info`, `alert`, `success`, `warning`, `error`（若有）。  
On connect, last events of those types are replayed when present.

| event | data | 說明 |
|-------|------|------|
| `vehicle` | JSON 面板物件 | 主要車輛資料 Primary vehicle panel |
| `info` | 同 JSON（或舊版字串） | 相容舊前端 Compatibility |
| `clear` | `CLEAR_SCREEN` | 清空畫面 |
| `error` | JSON 錯誤訊息 | LPRS／伺服器錯誤 |
| `ping` | `{"ts":"...","clientKey":"...","ip":"..."}` | Keep-alive |
| `success` / `warning` / `alert` | 字串／JSON | 舊流程／擴充 |

### 面板 JSON 欄位（英文鍵）/ Panel fields (English keys)

| 欄位 Field | 說明 |
|------------|------|
| `photo` | 入場圖 URL（相對路徑會拼上 LPRS `BaseUrl`） |
| `lpn` | 車牌 |
| `entryTime` | 入場時間 |
| `identity` | 身份 |
| `vehicleType` | 車種 |
| `warehouseDestination` | 倉庫目的地 |
| `approvalStatus` | 審批狀態 |
| `paymentStatus` | 繳費狀態 |
| `paymentTime` | 繳費時間 |
| `hasBooking` | 是否有預約 |
| `bookingId` | 預約 ID |
| `clientKey`, `tid`, `ts` | 除錯／追蹤 |
| `floor`, `warehouseName`, … | 額外脈絡 Extra context |

Frontend 亦相容部分中文別名鍵（見 `Frontend/src/types.ts`）。  
Frontend also accepts some Chinese alias keys (see `types.ts`).

---

## 4. LPRS 流程 / LPRS Flow

1. 讀設定 `LprsApi`（`BaseUrl`, `VehiclePathTemplate`, `ApiKey`, `SiteKey`, …）。
2. `HttpClient` 名稱 `"LprsApi"`：BaseAddress = `BaseUrl`；Timeout 預設 15s；可選忽略 SSL（`IgnoreSslErrors`）。
3. Header：`ApiKeyHeaderName`（預設 `x-api-key`）= `ApiKey`；若 `ApiKey` 空白則回退使用 `SiteKey`。  
   Header value = `ApiKey`, or fall back to `SiteKey` when `ApiKey` is empty.
4. `GET` `{VehiclePathTemplate}`，將 `{lpn}` / `{LPN}` 替換為 URL-encoded 車牌。
5. 解析 envelope（`resCode` / `resMsg` / `data`），`VehiclePanelMapper` 映射為面板 JSON。
6. `hub.Publish(clientKey, "vehicle", json)` 與 `"info"`。
7. 照片：優先 `entryImage`／車身圖路徑；否則可用 `EntryImagePathTemplate` 組絕對 URL。

**安全注意 Security：** 真實 `ApiKey` **不可**提交到 Git。提交用的 `appsettings.json` 應維持 `"ApiKey": ""`，本機用 User Secrets、環境變數或 `appsettings.*.local.json`（已列入 `.gitignore`）覆寫。  
Never commit a real `ApiKey`. Keep committed `"ApiKey": ""`; override locally via secrets / env / `*.local.json`.

---

## 5. clientKey（裝置／面板識別）

- 每個入閘面板一個 `clientKey`（例如 `EN21`, `EN22`）。  
  One `clientKey` per entrance panel (e.g. `EN21`).
- Frontend：URL 查詢參數 `?clientKey=EN21`；省略時預設 `EN21`。  
  Frontend reads `?clientKey=`; default `EN21`.
- SSE URL：`{VITE_SSE_BASE_URL}/sse/{clientKey}`。
- Backend 以 `clientKey` 分頻道；多個瀏覽器可同時訂閱同一 key。  
  Backend fans out by `clientKey`; multiple browsers may share one key.

範例 Examples：

```text
http://localhost:5173/?clientKey=EN21
http://localhost:5100/sse/EN21
```

---

## 6. Ping / 重連 / Watchdog

### Backend ping

- 設定：`Sse:PingIntervalSeconds`（目前 **20** 秒；程式下限 clamp ≥ 5）。  
  Config `PingIntervalSeconds` (**20** s; code clamps to ≥ 5).
- 每個 SSE 訂閱背景 loop 定期寫入 `event: ping`。  
  Per-subscription background loop writes `ping` events.

### Frontend reconnect

- 自管重連（關閉瀏覽器 EventSource 自動重試）：backoff `1s → 2s → 5s → 10s → 30s`。  
  App-controlled reconnect with that backoff.
- 收到 `vehicle` / `info` / `clear` / `error` / `ping` 會重置 backoff。  
  Those events reset backoff.
- `visibilitychange` 回到可見且連線非 OPEN 時立即重連。  
  On tab visible again, force reconnect if not OPEN.

### Frontend watchdog

- 若可見狀態下 **90 秒** 未收到任何 SSE 事件 → 強制重連。  
  If no SSE event for **90s** while visible → force reconnect.
- 檢查週期 5 秒。Tick every 5s.

因此後端 20s ping 可讓 watchdog 在正常網路下保持連線。  
Server 20s pings keep the watchdog happy under normal conditions.

---

## 7. 設定 / Configuration

### Backend `appsettings.json`（示意；金鑰請留空）

```json
{
  "Sse": {
    "ListenUrl": "http://localhost:5100",
    "PingIntervalSeconds": 20,
    "RestrictByIp": false,
    "KioskIps": [ "10.33.3.231", "10.33.3.236" ]
  },
  "LprsApi": {
    "BaseUrl": "https://192.168.103.59:8012",
    "VehiclePathTemplate": "/api/LprsEvents/Vehicle/{lpn}",
    "SiteKey": "HK1",
    "ApiKey": "",
    "ApiKeyHeaderName": "x-api-key",
    "TimeoutSeconds": 15,
    "IgnoreSslErrors": true,
    "EntryImagePathTemplate": "/api/ManagementSystem/Summaries/Vehicle/EntryImage/{lpn}"
  }
}
```

| 鍵 Key | 說明 |
|--------|------|
| `Sse:ListenUrl` | 綁定 URL |
| `Sse:PingIntervalSeconds` | SSE ping 間隔（秒） |
| `Sse:RestrictByIp` | `true` 時僅允許 `KioskIps` |
| `LprsApi:ApiKey` | **提交時必須為 `""`**；本機再填真實值 |
| `LprsApi:SiteKey` | `ApiKey` 空白時作為 `x-api-key` 回退 |

本機覆寫建議（勿提交）：

- `appsettings.Development.local.json` / `appsettings.*.local.json`
- 環境變數：`LprsApi__ApiKey=...`
- .NET User Secrets

檔案日誌：`LogFile` 寫入 `C:\AppLog\Dpanel_log\yyyy-MM\`（本機路徑，非 repo）。  
File logs go under `C:\AppLog\Dpanel_log\...` (local, not in repo).

### Frontend `.env`

```env
VITE_SSE_BASE_URL=http://localhost:5100
```

此為本機 URL，非密鑰，可保留。未設定時 Vite 可走 proxy `/sse`、`/api` → `5100`。  
Localhost URL is not a secret. Without it, Vite proxies `/sse` and `/api` to `:5100`.

---

## 8. Postman / 呼叫範例 Examples

### Health

```http
GET http://localhost:5100/health
```

### Vehicle lookup → SSE

```http
POST http://localhost:5100/api/vehicle
Content-Type: application/json

{
  "clientKey": "EN21",
  "lpn": "AB1234",
  "tid": "demo-001"
}
```

PowerShell：

```powershell
Invoke-RestMethod -Method Post -Uri "http://localhost:5100/api/vehicle" `
  -ContentType "application/json" `
  -Body (@{ clientKey = "EN21"; lpn = "AB1234"; tid = "demo-001" } | ConvertTo-Json)
```

### Clear panel

```http
POST http://localhost:5100/api/clear/EN21
```

### SSE（瀏覽器或支援 SSE 的客戶端）

```text
GET http://localhost:5100/sse/EN21
Accept: text/event-stream
```

Postman：可開 raw 請求觀察 `event:` / `data:` 串流；建議同時開 Frontend `?clientKey=EN21` 驗證 UI。  
Use a streaming-capable client; verify UI with Frontend `?clientKey=EN21`.

---

## 9. 如何執行 / How to run

### 先決條件 Prerequisites

- .NET SDK 8+
- Node.js 18+（建議）與 npm
- 可連線的 LPRS（或先測 `/health` + mock／內網）

### Backend

```powershell
cd "Backend\Web_dPanel_Backend"
# 本機設定 ApiKey（勿提交）後：
dotnet run --launch-profile http
```

預設約 `http://localhost:5100`。確認：`GET /health`。

### Frontend

```powershell
cd Frontend
npm install
npm run dev
```

開啟 `http://localhost:5173/?clientKey=EN21`。

建置：

```powershell
npm run build
npm run preview
```

### 建議啟動順序 Suggested order

1. 啟動 Backend（5100）  
2. 啟動 Frontend（5173）  
3. 開面板 URL → 確認連線狀態為「已連線」  
4. Postman `POST /api/vehicle` → 畫面應更新  

---

## 10. 目錄與 Git 注意 / Layout & Git notes

```text
SSE_Backend/
  README.md
  .gitignore
  Doc/TECH.md
  Backend/          # .sln + Web_dPanel_Backend
  Frontend/         # React app
```

- **不要** `git push` 直到 GitHub／Cursor 連線就緒（本準備步驟不含 push）。  
  Do **not** push until GitHub/Cursor connect is ready.
- `.gitignore` 已排除：`bin/`, `obj/`, `node_modules/`, `dist/`, `.vs/`, `publish/`, `*.user`, `appsettings.*.local.json`, `.env.local`, secrets、`_bak_*` / `*._bak_*`、logs 等。  
- 備份檔 `Program.cs._bak_*`、`appsettings.json._bak_*` 等應被忽略，勿提交。  
  Backup `*_bak_*` files are ignored — do not commit them.

---

## 11. 相關檔案 / Key source files

| 檔案 | 用途 |
|------|------|
| `Backend/Web_dPanel_Backend/Program.cs` | 端點、LPRS、SSE、MessageHub |
| `Backend/Web_dPanel_Backend/appsettings.json` | Sse / LprsApi 設定 |
| `Frontend/src/App.tsx` | EventSource、重連、watchdog |
| `Frontend/src/types.ts` | Vehicle payload 正規化 |
| `Frontend/.env` | `VITE_SSE_BASE_URL` |
| `Frontend/vite.config.ts` | `/sse`、`/api` proxy |

---

*文件版本 Document version：與 2026-09-24 SSE reconnect / LPRS 面板實作對齊。*
