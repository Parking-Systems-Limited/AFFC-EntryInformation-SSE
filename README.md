# AFFC SSE — Entry Information Panel

AFFC 入閘資訊面板（Server-Sent Events）：Backend 推送車輛／LPRS 資料，Frontend 即時顯示。

AFFC entrance information panel over SSE: Backend pushes vehicle/LPRS data; Frontend displays it live.

## 目錄 Structure

| 路徑 Path | 說明 |
|-----------|------|
| [Backend/](Backend/) | ASP.NET Core 8 SSE + LPRS API（`Web_dPanel_Backend`） |
| [Frontend/](Frontend/) | React + Vite 入閘面板 UI |
| [Doc/TECH.md](Doc/TECH.md) | **技術文件（繁中 + English）** — 架構、端點、SSE、LPRS、clientKey、ping/重連、Postman、設定與執行 |

## 快速開始 Quick start

詳見 [Doc/TECH.md](Doc/TECH.md) 第 9 節。摘要：

```powershell
# Backend
cd Backend\Web_dPanel_Backend
dotnet run --launch-profile http
# → http://localhost:5100

# Frontend（另開終端）
cd Frontend
npm install
npm run dev
# → http://localhost:5173/?clientKey=EN21
```

## 安全 Security

- `LprsApi:ApiKey` 在提交的 `appsettings.json` 必須為空字串 `""`。本機再以 local／secrets／環境變數填入。  
  Committed `ApiKey` must be `""`. Set the real key only locally.
- 勿提交 `.env.local`、真實金鑰、`publish/` 大型輸出、`_bak_*` 備份。  
  Do not commit secrets, large publish output, or `_bak_*` backups.

## 授權／狀態 Notes

Private GitHub repo: https://github.com/manpiulo22/AFFC-EntryInformation-SSE
