/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_SSE_BASE_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}