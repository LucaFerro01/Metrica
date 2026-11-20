/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL dell'API vista dal browser (predefinita `/api`), vedi `.env.example`. */
  readonly VITE_API_BASE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
