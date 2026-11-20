import { Activity, Upload, type LucideIcon } from 'lucide-react';

export interface NavItem {
  to: '/activities' | '/upload';
  /** Chiave di traduzione dell'etichetta. */
  labelKey: 'nav.activities' | 'nav.upload';
  icon: LucideIcon;
}

/** Voci della navigazione principale, condivise dalla barra in basso (telefono) e da quella laterale (desktop). */
export const navItems: readonly NavItem[] = [
  { to: '/activities', labelKey: 'nav.activities', icon: Activity },
  { to: '/upload', labelKey: 'nav.upload', icon: Upload },
];
