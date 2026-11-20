// Interfaccia pubblica della feature: il resto della cartella è interno. Da fuori si importa
// solo da qui (`@/features/activities`), mai dai file interni (regola di lint).
// Validazione dei parametri di ricerca dell'elenco (la usa il router: un valore non valido è scartato, mai un errore).
export { parseActivitiesSearch, type ActivitiesSearch } from './data/activitiesSearch';
export { ActivityDetailPage } from './detail/ActivityDetailPage';
export { ActivityListPage } from './list/ActivityListPage';
// Upload accettati e non ancora (necessariamente) in elenco: store in memoria condiviso tra Carica ed elenco.
export { pendingUploads, usePendingUploads, type PendingUpload } from './pending/pendingUploads';
export { UploadPage } from './upload/UploadPage';
