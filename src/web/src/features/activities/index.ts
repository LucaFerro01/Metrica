// Interfaccia pubblica della feature: il resto della cartella è interno. Da fuori si importa
// solo da qui (`@/features/activities`), mai dai file interni (regola di lint).
export { ActivityDetailPage } from './detail/ActivityDetailPage';
export { ActivityListPage } from './list/ActivityListPage';
export { UploadPage } from './upload/UploadPage';
