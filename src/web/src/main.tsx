import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from '@/app/App';
import { initI18n } from '@/shared/i18n';
import { initTheme } from '@/shared/lib/theme';
import './index.css';

async function bootstrap() {
  await initI18n();
  initTheme(); // la classe `dark` c'è già (script inline di index.html): qui si avviano preferenza e ascolto del sistema
  const container = document.getElementById('root');
  if (!container) throw new Error('Elemento #root non trovato');
  createRoot(container).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

void bootstrap();
