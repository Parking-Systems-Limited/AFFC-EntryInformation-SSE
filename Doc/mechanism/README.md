# AFFC Entry Information — SSE 機制圖說明

本目錄為 **架構專家** 產出的機制圖，供 Leon 開啟／附檔使用。圖檔為 **自包含 SVG** 與 **內嵌 SVG 的 HTML**（無需 Figma）。

路徑 Path：`Doc/mechanism/`（本倉庫）

---

## 圖檔一覽

| 檔案 | 內容 |
|------|------|
| `architecture.svg` / `.html` | 系統架構：誰呼叫誰、clientKey、REST vs SSE |
| `sequence-happy.svg` / `.html` | Happy Path 時序：車輛推送 + 清屏 |
| `reconnect.svg` / `.html` | Ping / Watchdog / 退避重連 / Visibility |
| `failure-paths.svg` / `.html` | LPRS 失敗、Timeout、SSE Closed（無假資料） |
| `summary.md` | 給 parent agent：程式驗證 vs 設計、落差 |

建議用瀏覽器開啟對應 `.html`（深色頁框 + 白底圖，列印／截圖友善）。

---

## 圖例（Legend）

| 符號／顏色 | 意義 |
|------------|------|
| 藍線 **REST** | 短請求／回應（Postman→Backend、Backend→LPRS） |
| 綠線 **SSE** | 長連線推播（Backend→Frontend EventSource） |
| 紫線 **Hub** | 記憶體 MessageHub Publish / Subscribe / Clear |
| **clientKey** | 面板識別（例 `EN21`）；REST body 指定目標，SSE path 訂閱同一 key |
| `event: vehicle` / `info` | 車輛面板資料 |
| `event: clear` | 清屏 |
| `event: error` | 錯誤訊息（不推假資料） |
| `event: ping` | Keep-alive（約 20s），不更新面板 |

---

## 關鍵端點（與程式一致）

### REST（觸發）
- `POST /api/vehicle` · `POST /api/plate` — body: `{ "clientKey", "lpn" }`
- `POST /api/clear/{clientKey}` — 清屏
- `POST /api/message` — legacy
- `GET /health` — 健康與 pingInterval 等資訊

### SSE（訂閱）
- `GET /sse/{clientKey}` — `text/event-stream`

### 外部
- LPRS `GET https://192.168.103.59:8012/api/LprsEvents/Vehicle/{lpn}` + header `x-api-key`

---

## 穩定性要點（一眼記住）

1. **Backend ping ≈ 20s**（`Sse:PingIntervalSeconds`）
2. **前端 onerror**：close 舊 EventSource → 指數退避 `1→2→5→10→30s` → new EventSource
3. **Watchdog 90s**：期間無任何事件（含 ping）→ 強制重連
4. **Visibility**：回前景且非 OPEN → 強制重連
5. **UI**：`connecting` / `open` / `reconnecting` / `offline`

---

## 開啟方式

```bash
# 在 box 或複製到本機後
xdg-open Doc/mechanism/architecture.html
# 或 open Doc/mechanism/architecture.html（macOS）
# 或直接用瀏覽器開啟同目錄任意 .svg / .html
```
