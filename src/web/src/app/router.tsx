import {
  createRootRoute,
  createRoute,
  createRouter,
  redirect,
  type RouterHistory,
} from '@tanstack/react-router';
import { ActivityDetailPage, ActivityListPage, parseActivitiesSearch, UploadPage } from '@/features/activities';
import { AppShell } from './layout/AppShell';
import { NotFoundPage } from './NotFoundPage';

// Instradamento a codice, servito dalla radice (nessun basepath: serve anche al guscio nativo, DEC-015).

const rootRoute = createRootRoute({
  component: AppShell,
  notFoundComponent: NotFoundPage,
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  beforeLoad: () => {
    throw redirect({ to: '/activities', replace: true });
  },
});

const activitiesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'activities',
  // `?sport=` (run|ride|swim|other) e `?page=` (intero >= 2): un valore non valido si scarta, vale il predefinito.
  validateSearch: parseActivitiesSearch,
  component: function ActivitiesRoute() {
    const { sport, page } = activitiesRoute.useSearch();
    return <ActivityListPage sport={sport} page={page} />;
  },
});

const activityDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'activities/$activityId',
  // Filtro e pagina dell'elenco di provenienza, per il link «Torna all'elenco».
  validateSearch: parseActivitiesSearch,
  component: function ActivityDetailRoute() {
    const { activityId } = activityDetailRoute.useParams();
    const backSearch = activityDetailRoute.useSearch();
    return <ActivityDetailPage activityId={activityId} backSearch={backSearch} />;
  },
});

const uploadRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'upload',
  component: UploadPage,
});

const routeTree = rootRoute.addChildren([indexRoute, activitiesRoute, activityDetailRoute, uploadRoute]);

export function createAppRouter(history?: RouterHistory) {
  return createRouter({ routeTree, history });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
