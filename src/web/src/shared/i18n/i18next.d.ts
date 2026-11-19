import type it from './locales/it.json';

// Chiavi di traduzione tipizzate: `t('nav.activitis')` non compila. L'italiano è il riferimento
// (un test verifica che l'inglese abbia le stesse chiavi).
declare module 'i18next' {
  interface CustomTypeOptions {
    defaultNS: 'translation';
    resources: { translation: typeof it };
  }
}
