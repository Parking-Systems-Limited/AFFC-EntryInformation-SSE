# Backend — Web_dPanel_Backend

ASP.NET Core 8 Minimal API：SSE 推送 + LPRS 車牌查詢。

ASP.NET Core 8 Minimal API: SSE push + LPRS plate lookup.

## 執行 Run

```powershell
cd Web_dPanel_Backend
dotnet run --launch-profile http
```

預設 `http://localhost:5100`。健康檢查：`GET /health`。

## 主要端點 Main endpoints

| Method | Path | 用途 |
|--------|------|------|
| GET | `/sse/{clientKey}` | SSE 訂閱 |
| POST | `/api/vehicle` | 車牌 → LPRS → SSE |
| POST | `/api/plate` | 同上 |
| POST | `/api/clear/{clientKey}` | 清屏 |
| GET | `/health` | 健康檢查 |

完整說明（繁中 + English）、Postman 範例與設定注意事項見根目錄 [Doc/TECH.md](../Doc/TECH.md)。

## 設定 Config

編輯 `appsettings.json` 的 `Sse` / `LprsApi`。**`LprsApi:ApiKey` 提交時必須為 `""`**；本機用 User Secrets 或 `appsettings.*.local.json`（已 gitignore）覆寫。

Never commit a real API key.
